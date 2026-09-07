#!/usr/bin/env python3
"""Offline tests for the Catalog rehearsal harness. Fake az/sqlcmd/curl boundaries only; no network, SQL, or Azure."""

from __future__ import annotations

import json
import os
import shutil
import subprocess
import tempfile
import unittest
from datetime import datetime, timedelta, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
HARNESS = ROOT / "scripts" / "catalog-rehearsal"
REGISTRY = "acr.azurecr.io"
CANDIDATE_SOURCE = "a" * 40
PREVIOUS_SOURCE = "b" * 40
CANDIDATE_IMAGE = f"{REGISTRY}/elsa-control/api@sha256:{'1' * 64}"
PREVIOUS_IMAGE = f"{REGISTRY}/elsa-control/api@sha256:{'2' * 64}"
PROBE_IMAGE = f"{REGISTRY}/elsa-control/azure-lifecycle-proof@sha256:{'3' * 64}"
MI_CLIENT = "c5055d7d-d66d-468d-8984-077214496243"
SUBSCRIPTION = "8e23037a-420f-4ad0-9594-9d194de29e84"
COUNTS = ("Accounts", "Organizations", "Workspaces", "ElsaInstances", "DeploymentRuns", "AzureProviderOperations")
MIGRATIONS = [f"2026090100000{i:d}_M{i}" if i < 10 else f"202609010000{i:d}_M{i}" for i in range(1, 54)]


def python(script: str, *arguments: str, env: dict[str, str] | None = None, stdin: str | None = None) -> subprocess.CompletedProcess[str]:
    return subprocess.run(["python3", str(HARNESS / script), *arguments], capture_output=True, text=True, env={**os.environ, **(env or {})}, input=stdin, check=False, timeout=120)


def passed_result(phase: str, group: str, source: str, build: str, baseline_ids: list[str], preview_baseline: int) -> dict[str, object]:
    counts = {name: 1 for name in COUNTS}
    return {
        "schema": "elsa-control.catalog-rehearsal/v1", "phase": phase, "result": "passed", "code": "ok", "healthChecks": 2,
        "migrationIds": MIGRATIONS, "baselineMigrationIds": baseline_ids, "previewColumnCount": 2, "baselinePreviewColumnCount": preview_baseline,
        "duplicateTargetGroupCount": 0, "duplicateOperationGroupCount": 0, "billingProviderEventNullStateCount": 0, "billingProviderEventsPresent": True,
        "foreignKeyIntegrityViolationCount": 0, "checkConstraintIntegrityViolationCount": 0, "orphanProviderAssignmentCount": 0, "orphanOperationTransitionCount": 0,
        "providerAssignmentSchemaPresent": True, "recoveryObservationColumnsValid": True, "recoveryObservationForeignKeysValid": True,
        "recoveryObservationNaturalKeyIndexValid": True, "recoveryObservationAppendOnlyTriggerValid": True, "recoveryRequestColumnsValid": True,
        "attemptedStepColumnValid": True, "commonCountsEqual": True, "permissionChecks": True, "principalChecks": True, "integrityChecks": True,
        "indexChecks": True, "baselineCounts": counts, "postCounts": counts, "bakedImageId": source, "buildNumber": build, "rehearsalGroupName": group,
    }


class HarnessCase(unittest.TestCase):
    def setUp(self) -> None:
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.temp = Path(temporary.name)
        self.ids_file = self.temp / "expected-migrations.txt"
        self.ids_file.write_text("\n".join(MIGRATIONS) + "\n")

    def common_env(self, phase: str = "candidate", **overrides: str) -> dict[str, str]:
        phase = overrides.get("REHEARSAL_PHASE", phase)
        env = {
            "REHEARSAL_PHASE": phase,
            "REHEARSAL_GROUP_NAME": f"catalog-rehearsal-{phase}-96",
            "AZURE_SUBSCRIPTION_ID": SUBSCRIPTION, "AZURE_LOCATION": "westeurope",
            "RESOURCE_GROUP": "rg-proof", "AUTHORITY_RESOURCE_GROUP": "rg-control",
            "CATALOG_SERVER": "controlsql.database.windows.net", "CATALOG_DATABASE": "Catalog-Rehearsal-20260906",
            "API_IDENTITY_NAME": "api_identity", "CATALOG_MI_CLIENT_ID": MI_CLIENT, "ACR_PULL_IDENTITY_NAME": "acr_pull",
            "PROBE_IMAGE": PROBE_IMAGE,
            "CANDIDATE_IMAGE": CANDIDATE_IMAGE, "CANDIDATE_SOURCE_ID": CANDIDATE_SOURCE, "CANDIDATE_BUILD_NUMBER": "96",
            "PREVIOUS_IMAGE": PREVIOUS_IMAGE, "PREVIOUS_SOURCE_ID": PREVIOUS_SOURCE, "PREVIOUS_BUILD_NUMBER": "89",
            "EXPECTED_BASELINE_MIGRATIONS": "45" if phase == "candidate" else "53", "EXPECTED_MIGRATIONS": "53",
            "EXPECTED_BASELINE_PREVIEW_COLUMNS": "0" if phase == "candidate" else "2", "EXPECTED_PREVIEW_COLUMNS": "2",
            "EXPECTED_MIGRATION_IDS_FILE": str(self.ids_file),
            "REHEARSAL_EXPIRY_UTC": (datetime.now(timezone.utc) + timedelta(days=1)).strftime("%Y-%m-%d"),
        }
        env.update(overrides)
        return env


class RendererTests(HarnessCase):
    def render(self, **overrides: str) -> tuple[int, dict[str, object] | None]:
        output = self.temp / "spec.json"
        completed = python("render-container-group.py", "--output", str(output), "--probe-script", str(HARNESS / "probe.py"), env=self.common_env(**overrides))
        return completed.returncode, (json.loads(output.read_text()) if output.exists() else None)

    def test_candidate_spec_is_private_pinned_and_worker_disabled(self) -> None:
        code, spec = self.render()
        self.assertEqual(0, code)
        assert spec is not None
        properties = spec["properties"]
        self.assertNotIn("ipAddress", properties)
        self.assertEqual("Never", properties["restartPolicy"])
        containers = {c["name"]: c["properties"] for c in properties["containers"]}
        self.assertEqual(CANDIDATE_IMAGE, containers["api"]["image"])
        self.assertEqual(PROBE_IMAGE, containers["probe"]["image"])
        api_env = {e["name"]: e for e in containers["api"]["environmentVariables"]}
        self.assertIn("secureValue", api_env["ConnectionStrings__Catalog"])
        self.assertIn("Authentication=Active Directory Managed Identity", api_env["ConnectionStrings__Catalog"]["secureValue"])
        self.assertIn(f"User Id={MI_CLIENT}", api_env["ConnectionStrings__Catalog"]["secureValue"])
        self.assertNotIn("Password", api_env["ConnectionStrings__Catalog"]["secureValue"])
        self.assertTrue(all(api_env[k]["value"] == "false" for k in api_env if k.endswith("Enabled") and not k.startswith("Authentication")))
        self.assertEqual("96", api_env["Application__BuildNumber"]["value"])
        probe_env = {e["name"]: e["value"] for e in containers["probe"]["environmentVariables"]}
        self.assertEqual(CANDIDATE_SOURCE, probe_env["EXPECTED_IMAGE_ID"])
        self.assertEqual(("45", "53", "0", "2"), tuple(probe_env[k] for k in ("EXPECTED_BASELINE_MIGRATIONS", "EXPECTED_MIGRATIONS", "EXPECTED_BASELINE_PREVIEW_COLUMNS", "EXPECTED_PREVIEW_COLUMNS")))
        self.assertEqual(MIGRATIONS, probe_env["EXPECTED_MIGRATION_IDS"].split())
        self.assertTrue(probe_env["REHEARSAL_SCRIPT"].startswith("#!/usr/bin/env python3"))
        self.assertEqual({"server": REGISTRY, "identity": spec["properties"]["imageRegistryCredentials"][0]["identity"]}, properties["imageRegistryCredentials"][0])
        self.assertIn("/acr_pull", properties["imageRegistryCredentials"][0]["identity"])
        self.assertEqual(2, len(spec["identity"]["userAssignedIdentities"]))
        self.assertIn("/rehearsal/api-exited", containers["api"]["command"][2])
        self.assertIn(">/dev/null 2>&1", containers["api"]["command"][2])

    def test_previous_phase_binds_previous_image_and_migrated_baseline(self) -> None:
        code, spec = self.render(REHEARSAL_PHASE="previous", REHEARSAL_GROUP_NAME="catalog-rehearsal-previous-96")
        self.assertEqual(0, code)
        assert spec is not None
        containers = {c["name"]: c["properties"] for c in spec["properties"]["containers"]}
        self.assertEqual(PREVIOUS_IMAGE, containers["api"]["image"])
        probe_env = {e["name"]: e["value"] for e in containers["probe"]["environmentVariables"]}
        self.assertEqual((PREVIOUS_SOURCE, "89", "53", "2"), tuple(probe_env[k] for k in ("EXPECTED_IMAGE_ID", "EXPECTED_BUILD_NUMBER", "EXPECTED_BASELINE_MIGRATIONS", "EXPECTED_BASELINE_PREVIEW_COLUMNS")))

    def test_renderer_rejects_unsafe_inputs(self) -> None:
        cases = {
            "tag image": {"CANDIDATE_IMAGE": f"{REGISTRY}/elsa-control/api:latest"},
            "foreign probe registry": {"PROBE_IMAGE": f"other.azurecr.io/proof@sha256:{'3' * 64}"},
            "bad source": {"CANDIDATE_SOURCE_ID": "not-a-sha"},
            "bad build": {"CANDIDATE_BUILD_NUMBER": "0"},
            "wrong group prefix": {"REHEARSAL_GROUP_NAME": "catalog-rehearsal-previous-96"},
            "expired": {"REHEARSAL_EXPIRY_UTC": "2020-01-01"},
            "missing subscription": {"AZURE_SUBSCRIPTION_ID": "REQUIRED"},
            "ids count mismatch": {"EXPECTED_MIGRATIONS": "52"},
            "bad phase": {"REHEARSAL_PHASE": "production"},
        }
        for name, overrides in cases.items():
            with self.subTest(case=name):
                code, spec = self.render(**overrides)
                self.assertEqual(2, code)
                self.assertIsNone(spec)


class ParserAndGateTests(HarnessCase):
    def write(self, name: str, payload: object, raw: str | None = None) -> Path:
        path = self.temp / name
        path.write_text(raw if raw is not None else json.dumps(payload) + "\n")
        return path

    def test_parser_accepts_only_one_safe_line_and_rejects_extra_fields(self) -> None:
        payload = passed_result("candidate", "catalog-rehearsal-candidate-96", CANDIDATE_SOURCE, "96", MIGRATIONS[:45], 0)
        ok = python("parse-result.py", str(self.write("log", payload, raw="noise line\n" + json.dumps(payload) + "\n")), "candidate")
        self.assertEqual(2, ok.returncode)  # more than one non-empty line is rejected
        ok = python("parse-result.py", str(self.write("log", payload)), "candidate")
        self.assertEqual(0, ok.returncode, ok.stderr)
        self.assertEqual(payload, json.loads(ok.stdout))
        for name, mutate in {
            "extra field": lambda p: p.update(server="controlsql"),
            "raw migration id": lambda p: p.update(migrationIds=["DROP TABLE x"]),
            "wrong phase": lambda p: p.update(phase="previous"),
            "foreign group": lambda p: p.update(rehearsalGroupName="catalog-rehearsal-previous-96"),
            "health above two": lambda p: p.update(healthChecks=3),
            "partial counts on pass": lambda p: p.update(postCounts={"Accounts": 1}),
        }.items():
            with self.subTest(case=name):
                broken = json.loads(json.dumps(payload))
                mutate(broken)
                self.assertEqual(2, python("parse-result.py", str(self.write("log", broken)), "candidate").returncode)

    def test_parser_failure_outcome_and_failed_modes(self) -> None:
        path = self.temp / "failure.json"
        failure = python("parse-result.py", "--failure", str(path), "candidate", "container-create-failed", "catalog-rehearsal-candidate-96")
        self.assertEqual(0, failure.returncode)
        value = json.loads(path.read_text())
        self.assertEqual(("failed", "container-create-failed", "catalog-rehearsal-candidate-96"), (value["result"], value["code"], value["rehearsalGroupName"]))
        self.assertEqual(0, python("parse-result.py", "--is-failed", str(path)).returncode)
        self.assertEqual(1, python("parse-result.py", "--outcome", str(path), "Succeeded", "0", "0").returncode)
        passed = self.write("passed.json", passed_result("candidate", "catalog-rehearsal-candidate-96", CANDIDATE_SOURCE, "96", MIGRATIONS[:45], 0))
        self.assertEqual(0, python("parse-result.py", "--outcome", str(passed), "Succeeded", "0", "0").returncode)
        for state, api, probe in (("Failed", "0", "0"), ("Succeeded", "1", "0"), ("Succeeded", "0", "1"), ("Stopped", "0", "0")):
            self.assertEqual(1, python("parse-result.py", "--outcome", str(passed), state, api, probe).returncode)
        self.assertEqual(1, python("parse-result.py", "--is-failed", str(passed)).returncode)
        unsafe = python("parse-result.py", "--failure", str(path), "candidate", "bad code with spaces", "not a group")
        self.assertEqual(("unknown", "unknown"), (json.loads(unsafe.stdout)["code"], json.loads(unsafe.stdout)["rehearsalGroupName"]))

    def test_compare_gate_passes_only_the_full_two_phase_contract(self) -> None:
        candidate = self.write("candidate.json", passed_result("candidate", "catalog-rehearsal-candidate-96", CANDIDATE_SOURCE, "96", MIGRATIONS[:45], 0))
        previous = self.write("previous.json", passed_result("previous", "catalog-rehearsal-previous-96", PREVIOUS_SOURCE, "89", MIGRATIONS, 2))
        env = self.common_env()
        ok = python("compare-results.py", str(candidate), str(previous), env=env)
        self.assertEqual((0, "CATALOG_REHEARSAL_PASSED"), (ok.returncode, ok.stdout.strip()))
        cases = {
            "previous not passed": ("previous.json", lambda p: p.update(code="api-health-timeout"), "RESULT_NOT_PASSED"),
            "candidate wrong image": ("candidate.json", lambda p: p.update(bakedImageId=PREVIOUS_SOURCE), "RESULT_IMAGE_BINDING"),
            "reused group": ("previous.json", lambda p: p.update(rehearsalGroupName="catalog-rehearsal-candidate-96"), "RESULT_GROUP_BINDING"),
            "candidate baseline drift": ("candidate.json", lambda p: p.update(baselineMigrationIds=MIGRATIONS[:44]), "CANDIDATE_MIGRATIONS_MISMATCH"),
            "previous baseline not migrated": ("previous.json", lambda p: p.update(baselineMigrationIds=MIGRATIONS[:45]), "PREVIOUS_MIGRATIONS_MISMATCH"),
            "counts differ": ("previous.json", lambda p: p.update(postCounts={**p["postCounts"], "Accounts": 2}), "COMMON_COUNTS_DIFFER"),
            "contract flag false": ("candidate.json", lambda p: p.update(recoveryObservationAppendOnlyTriggerValid=False), "RESULT_CONTRACT_INCOMPLETE"),
            "null billing state": ("candidate.json", lambda p: p.update(billingProviderEventNullStateCount=1), "BILLING_EVENT_STATE_NULL"),
        }
        for name, (target, mutate, expected) in cases.items():
            with self.subTest(case=name):
                originals = {"candidate.json": candidate.read_text(), "previous.json": previous.read_text()}
                broken = json.loads(originals[target])
                mutate(broken)
                (self.temp / target).write_text(json.dumps(broken))
                completed = python("compare-results.py", str(candidate), str(previous), env=env)
                self.assertEqual((1, expected), (completed.returncode, completed.stdout.strip()))
                (self.temp / target).write_text(originals[target])
        self.assertEqual("INPUT_INVALID", python("compare-results.py", str(candidate), str(previous), env={**env, "PREVIOUS_SOURCE_ID": CANDIDATE_SOURCE}).stdout.strip())


class ProbeTests(HarnessCase):
    """Runs probe.py against fake sqlcmd/curl. The fake sqlcmd answers by matching fixed fragments of the query."""

    def make_fakes(self, migrated: bool, health_json: str = '{"status":"ok","buildNumber":"96","imageId":"%s"}' % CANDIDATE_SOURCE, sqlcmd_fail_after: int | None = None, answer_overrides: dict[str, str] | None = None) -> None:
        state = self.temp / "state"
        state.mkdir(exist_ok=True)
        (state / "migrated").write_text("1" if migrated else "0")
        answers = {
            "FROM dbo.__EFMigrationsHistory": "MIGRATIONS",
            "COLUMN_NAME = 'PreviewManifestDigest'": "PREVIEW",
            "HAVING COUNT(*) > 1": "0",
            "TABLE_NAME = 'BillingProviderEvents'": "1",
            "FROM dbo.BillingProviderEvents WHERE State IS NULL": "0",
            "sys.foreign_keys fk": "0",
            "sys.check_constraints": "0",
            "COLUMN_NAME = 'ProviderAssignmentId'": "1",
            "TABLE_NAME = 'AzureProviderResourceAssignments'": "1",
            "a.Id = o.ProviderAssignmentId": "0",
            "o.Id = t.OperationId": "0",
            "TABLE_NAME = 'AzureProviderRecoveryObservations' AND COLUMN_NAME IN": "OBS_COLUMNS",
            "FROM sys.foreign_keys WHERE name IN": "OBS_FKS",
            "IX_AzureProviderRecoveryObservations_WorkspaceId_NaturalKey": "OBS_INDEX",
            "TR_AzureProviderRecoveryObservations_AppendOnly": "OBS_TRIGGER",
            "TABLE_NAME = 'ElsaInstanceRecoveryRequests' AND COLUMN_NAME IN": "REQ_COLUMNS",
            "COLUMN_NAME = 'AttemptedStep'": "ATTEMPTED",
            "IX_AzureProviderOperations_WorkspaceId_TargetKey'": "1",
            "IX_AzureProviderOperations_WorkspaceId_TargetKey_OperationIdentity'": "1",
            "HAS_PERMS_BY_NAME": "2",
            "sys.database_principals": "2",
        }
        answers.update(answer_overrides or {})
        (state / "answers.json").write_text(json.dumps(answers))
        (state / "sqlcmd-fail-after").write_text(str(sqlcmd_fail_after if sqlcmd_fail_after is not None else -1))
        (self.temp / "sqlcmd").write_text(f"""#!/usr/bin/env python3
import json, sys, pathlib
state = pathlib.Path({str(state)!r})
calls = state / "calls"; n = int(calls.read_text()) + 1 if calls.exists() else 1; calls.write_text(str(n))
fail_after = int((state / "sqlcmd-fail-after").read_text())
if fail_after >= 0 and n > fail_after: sys.exit(1)
args = sys.argv[1:]
if "--authentication-method" not in args or "ActiveDirectoryManagedIdentity" not in args or "-U" not in args: sys.exit(3)
query = args[args.index("-Q") + 1]
migrated = (state / "migrated").read_text() == "1"
ids = {json.dumps(MIGRATIONS)}
answers = json.loads((state / "answers.json").read_text())
for fragment, answer in answers.items():
    if fragment in query:
        if answer.isdigit(): print(answer); sys.exit(0)
        if answer == "MIGRATIONS": print("\\n".join(ids if migrated else ids[:45])); sys.exit(0)
        if answer == "PREVIEW": print("2" if migrated else "0"); sys.exit(0)
        if answer == "OBS_COLUMNS": print("32" if migrated else "0"); sys.exit(0)
        if answer == "OBS_FKS": print("4" if migrated else "0"); sys.exit(0)
        if answer in ("OBS_INDEX", "ATTEMPTED"): print("1" if migrated else "0"); sys.exit(0)
        if answer == "OBS_TRIGGER": print("2" if migrated else "0"); sys.exit(0)
        if answer == "REQ_COLUMNS": print("5" if migrated else "0"); sys.exit(0)
        print(answer); sys.exit(0)
if "SELECT COUNT(*) FROM dbo." in query: print("1"); sys.exit(0)
sys.exit(4)
""")
        (self.temp / "sqlcmd").chmod(0o755)
        (self.temp / "curl").write_text(f"""#!/usr/bin/env bash
state={str(state)!r}
if [ -e "$state/api-started" ]; then printf '%s\\n200' {health_json!r}; else printf '\\n000'; exit 7; fi
""")
        (self.temp / "curl").chmod(0o755)

    def run_probe(self, phase: str = "candidate", start_api: bool = True, api_exits: bool = False, **overrides: str) -> tuple[subprocess.CompletedProcess[str], Path]:
        barrier = self.temp / "rehearsal"
        shutil.rmtree(barrier, ignore_errors=True)
        barrier.mkdir()
        state = self.temp / "state"
        for stale in ("api-started", "calls"):
            (state / stale).unlink(missing_ok=True)
        env = self.common_env(phase, **overrides)
        env.update({
            "REHEARSAL_BARRIER_PATH": str(barrier), "SQLCMD_PATH": str(self.temp / "sqlcmd"), "CURL_PATH": str(self.temp / "curl"),
            "EXPECTED_IMAGE_ID": CANDIDATE_SOURCE if phase == "candidate" else PREVIOUS_SOURCE, "EXPECTED_BUILD_NUMBER": "96" if phase == "candidate" else "89",
            "EXPECTED_MIGRATION_IDS": " ".join(MIGRATIONS), "CATALOG_MI_PRINCIPAL_NAME": "api_identity",
            "API_START_TIMEOUT_SECONDS": "30", "HEALTH_SAMPLE_SECONDS": "1",
        })
        # Simulate the API container: when the barrier appears, "start" (curl answers) and mark the clone migrated.
        watcher = subprocess.Popen(["bash", "-c", f"""
for i in $(seq 1 400); do
  if [ -e {str(barrier / 'start-api')!r} ]; then
    if [ {str(api_exits).lower()!r} = true ]; then printf 134 > {str(barrier / 'api-exited')!r}; exit 0; fi
    if [ {str(start_api).lower()!r} = true ]; then printf 1 > {str(state / 'migrated')!r}; touch {str(state / 'api-started')!r}; fi
    exit 0
  fi
  sleep 0.05
done"""])
        try:
            completed = subprocess.run(["python3", str(HARNESS / "probe.py")], capture_output=True, text=True, env={**os.environ, **env}, check=False, timeout=120)
        finally:
            watcher.kill()
        return completed, barrier

    def result(self, completed: subprocess.CompletedProcess[str]) -> dict[str, object]:
        lines = [line for line in completed.stdout.splitlines() if line.strip()]
        self.assertEqual(1, len(lines), completed.stdout)
        self.assertEqual("", completed.stderr)
        return json.loads(lines[0])

    def test_candidate_pass_records_migration_identity_health_and_contract(self) -> None:
        self.make_fakes(migrated=False)
        completed, barrier = self.run_probe()
        value = self.result(completed)
        self.assertEqual((0, "passed", "ok"), (completed.returncode, value["result"], value["code"]))
        self.assertEqual((45, 53, 0, 2, 2), (len(value["baselineMigrationIds"]), len(value["migrationIds"]), value["baselinePreviewColumnCount"], value["previewColumnCount"], value["healthChecks"]))
        self.assertEqual((CANDIDATE_SOURCE, "96", "catalog-rehearsal-candidate-96"), (value["bakedImageId"], value["buildNumber"], value["rehearsalGroupName"]))
        self.assertTrue(all(value[k] for k in ("integrityChecks", "indexChecks", "permissionChecks", "principalChecks", "commonCountsEqual", "recoveryObservationAppendOnlyTriggerValid", "recoveryRequestColumnsValid", "attemptedStepColumnValid", "providerAssignmentSchemaPresent")))
        self.assertTrue((barrier / "start-api").exists() and (barrier / "stop-api").exists())
        (self.temp / "out").write_text(completed.stdout)
        self.assertEqual(0, python("parse-result.py", str(self.temp / "out"), "candidate").returncode)
        self.assertNotIn("controlsql", completed.stdout)

    def test_baseline_mismatch_never_starts_the_api(self) -> None:
        self.make_fakes(migrated=True)  # clone already at 53 while the candidate phase expects 45
        completed, barrier = self.run_probe()
        value = self.result(completed)
        self.assertEqual((1, "baseline-migrations-unexpected"), (completed.returncode, value["code"]))
        self.assertFalse((barrier / "start-api").exists())
        self.assertTrue((barrier / "stop-api").exists())

    def test_api_crash_and_health_timeout_fail_closed_with_post_audit(self) -> None:
        self.make_fakes(migrated=False)
        crashed, _ = self.run_probe(api_exits=True)
        self.assertEqual("api-exited-before-health", self.result(crashed)["code"])
        self.make_fakes(migrated=False)
        timed_out, _ = self.run_probe(start_api=False, API_START_TIMEOUT_SECONDS="30")
        value = self.result(timed_out)
        self.assertEqual("api-health-timeout", value["code"])
        self.assertEqual(45, len(value["migrationIds"]))  # post audit still recorded the unchanged clone

    def test_wrong_image_identity_or_build_is_not_health(self) -> None:
        for health in ('{"status":"ok","buildNumber":"97","imageId":"%s"}' % CANDIDATE_SOURCE, '{"status":"ok","buildNumber":"96","imageId":"%s"}' % PREVIOUS_SOURCE):
            with self.subTest(health=health):
                self.make_fakes(migrated=False, health_json=health)
                completed, _ = self.run_probe(API_START_TIMEOUT_SECONDS="30")
                self.assertEqual("api-health-timeout", self.result(completed)["code"])

    def test_failed_audits_produce_stable_codes_and_never_pass(self) -> None:
        cases = {
            "duplicate active operations": ({"HAVING COUNT(*) > 1": "1"}, "baseline-duplicate-active-operations", False),
            "insufficient permissions": ({"HAS_PERMS_BY_NAME": "1"}, "baseline-authority-invalid", False),
            "foreign principal": ({"sys.database_principals": "1"}, "baseline-authority-invalid", False),
            "foreign key violation": ({"sys.foreign_keys fk": "3"}, "post-authority-or-integrity-invalid", True),
            "orphan transition": ({"o.Id = t.OperationId": "1"}, "post-authority-or-integrity-invalid", True),
            # The filtered indexes arrive with migration 48 of 53, so they gate only the post-migration audit.
            "missing filtered index": ({"IX_AzureProviderOperations_WorkspaceId_TargetKey'": "0"}, "post-authority-or-integrity-invalid", True),
            "null billing state": ({"FROM dbo.BillingProviderEvents WHERE State IS NULL": "2"}, "billing-event-state-null", True),
        }
        for name, (overrides, expected, api_started) in cases.items():
            with self.subTest(case=name):
                self.make_fakes(migrated=False, answer_overrides=overrides)
                completed, barrier = self.run_probe()
                value = self.result(completed)
                self.assertEqual((1, "failed", expected), (completed.returncode, value["result"], value["code"]))
                self.assertEqual(api_started, (barrier / "start-api").exists())

    def test_missing_append_only_trigger_fails_the_migration_contract(self) -> None:
        self.make_fakes(migrated=False, answer_overrides={"TR_AzureProviderRecoveryObservations_AppendOnly": "0"})
        completed, _ = self.run_probe()
        value = self.result(completed)
        self.assertEqual("post-migration-contract-invalid", value["code"])
        self.assertFalse(value["recoveryObservationAppendOnlyTriggerValid"])
        self.assertTrue(value["recoveryObservationNaturalKeyIndexValid"])

    def test_sql_boundary_failure_is_a_stable_code_without_error_text(self) -> None:
        self.make_fakes(migrated=False, sqlcmd_fail_after=0)
        completed, _ = self.run_probe()
        value = self.result(completed)
        self.assertEqual((1, "baseline-migration-query-failed"), (completed.returncode, value["code"]))


class RunPhaseTests(HarnessCase):
    def fake_az(self, group_state: str = "Succeeded", api_exit: str = "0", probe_exit: str = "0", probe_log: str | None = None, create_fails: bool = False) -> Path:
        log = probe_log if probe_log is not None else json.dumps(passed_result("candidate", "catalog-rehearsal-candidate-96", CANDIDATE_SOURCE, "96", MIGRATIONS[:45], 0))
        (self.temp / "probe.log").write_text(log + "\n")
        az = self.temp / "az"
        az.write_text(f"""#!/usr/bin/env bash
printf '%s\\n' "$*" >> {str(self.temp / 'az-calls')!r}
case "$*" in
  *"container create"*) {'exit 1' if create_fails else 'exit 0'} ;;
  *"instanceView.state"*) printf '%s\\n' {group_state!r} ;;
  *"name=='api'].instanceView.currentState.state"*) printf 'Terminated\\n' ;;
  *"name=='probe'].instanceView.currentState.state"*) printf 'Terminated\\n' ;;
  *"name=='api'].instanceView.currentState.exitCode"*) printf '%s\\n' {api_exit!r} ;;
  *"name=='probe'].instanceView.currentState.exitCode"*) printf '%s\\n' {probe_exit!r} ;;
  *"container logs"*) cat {str(self.temp / 'probe.log')!r} ;;
  *) exit 1 ;;
esac
""")
        az.chmod(0o755)
        return az

    def run_phase(self, az: Path, **overrides: str) -> tuple[subprocess.CompletedProcess[str], dict[str, object]]:
        result_path = self.temp / "candidate-result.json"
        env = {**os.environ, **self.common_env(**overrides), "AZ_BIN": str(az), "RESULT_PATH": str(result_path), "AZURE_CLI_TIMEOUT_SECONDS": "10", "REHEARSAL_TMPDIR": str(self.temp / "private-tmp")}
        completed = subprocess.run(["bash", str(HARNESS / "run-phase.sh")], capture_output=True, text=True, env=env, check=False, timeout=120)
        return completed, json.loads(result_path.read_text())

    def test_passing_run_writes_the_safe_result_only(self) -> None:
        completed, value = self.run_phase(self.fake_az())
        self.assertEqual(0, completed.returncode, completed.stdout)
        self.assertEqual(("passed", "ok"), (value["result"], value["code"]))
        calls = (self.temp / "az-calls").read_text()
        self.assertIn("container create", calls)
        self.assertNotIn("container delete", calls)
        self.assertEqual(1, len([l for l in completed.stdout.splitlines() if l.strip()]))
        self.assertEqual(0o700, (self.temp / "private-tmp").stat().st_mode & 0o777)
        self.assertEqual([], list((self.temp / "private-tmp").iterdir()))  # temporary spec removed on exit

    def test_failed_container_outcome_rejects_a_passing_probe_line(self) -> None:
        for state, api, probe in (("Failed", "0", "0"), ("Succeeded", "1", "0"), ("Succeeded", "0", "1")):
            with self.subTest(state=state, api=api, probe=probe):
                completed, value = self.run_phase(self.fake_az(group_state=state, api_exit=api, probe_exit=probe))
                self.assertEqual(7, completed.returncode)
                self.assertEqual("container-outcome-invalid", value["code"])

    def test_unsafe_probe_output_and_create_failure_are_stable_codes(self) -> None:
        completed, value = self.run_phase(self.fake_az(probe_log="Server=tcp:controlsql;Password=secret"))
        self.assertEqual((6, "result-invalid"), (completed.returncode, value["code"]))
        self.assertNotIn("Password", completed.stdout)
        completed, value = self.run_phase(self.fake_az(create_fails=True))
        self.assertEqual((3, "container-create-failed"), (completed.returncode, value["code"]))
        completed, value = self.run_phase(self.fake_az(), CANDIDATE_IMAGE=f"{REGISTRY}/elsa-control/api:latest")
        self.assertEqual((2, "input-invalid"), (completed.returncode, value["code"]))


class MigrationListTests(HarnessCase):
    def test_lists_sorted_sqlserver_migration_ids_from_a_checkout(self) -> None:
        output = self.temp / "ids.txt"
        completed = python("list-migration-ids.py", str(ROOT), str(output))
        self.assertEqual(0, completed.returncode, completed.stderr)
        ids = output.read_text().split()
        self.assertEqual(int(completed.stdout.strip()), len(ids))
        self.assertEqual(sorted(ids), ids)
        self.assertTrue(all(len(i) > 15 and i[14] == "_" for i in ids))
        self.assertGreaterEqual(len(ids), 53)


if __name__ == "__main__":
    unittest.main()
