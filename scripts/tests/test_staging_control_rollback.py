#!/usr/bin/env python3
"""Offline contract and stub-provider checks for the Control rollback proof."""

from __future__ import annotations

import hashlib
import json
import os
import subprocess
import tempfile
import unittest
from contextlib import contextmanager
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
WORKFLOW = ROOT / ".github" / "workflows" / "staging-control-rollback.yml"
ROLLBACK = ROOT / "scripts" / "staging-control-rollback.sh"
HELPERS = ROOT / "scripts" / "lib" / "azure-api-deploy-rollback.sh"


class StagingControlRollbackTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.workflow_text = WORKFLOW.read_text()
        cls.rollback_text = ROLLBACK.read_text()

    def run_bash(self, script: str, environment: dict[str, str]) -> subprocess.CompletedProcess[str]:
        env = os.environ.copy()
        env.update(environment)
        return subprocess.run(
            ["bash", "-c", script],
            cwd=ROOT,
            env=env,
            capture_output=True,
            text=True,
            check=False,
        )

    def test_workflow_has_only_two_digest_inputs_and_reserves_restore(self) -> None:
        self.assertEqual(self.workflow_text.count("description: \"The exact"), 2)
        job = self.workflow_text.split("  rollback:\n", 1)[1].split("    steps:\n", 1)[0]
        self.assertIn("if: ${{ !cancelled() }}", job)
        self.assertNotIn("always()", job, "the job must receive cancellation to interrupt its hold")
        self.assertIn("if: ${{ always() }}", self.workflow_text)
        self.assertIn("always() && steps.preflight.outcome == 'success'", self.workflow_text)
        self.assertIn("&& !cancelled()", self.workflow_text)
        self.assertIn("concurrency:\n  group: azure-api-deploy-test", self.workflow_text)
        self.assertNotIn("STAGING_CLOUD_REQUIRED_CAPABILITIES_JSON", self.workflow_text)
        self.assertNotIn("EXPECTED_N_COMMIT: ${{ github.sha }}", self.workflow_text)
        self.assertGreaterEqual(self.workflow_text.count("EXPECTED_N_COMMIT: ${{ steps.preflight.outputs.expected_n_commit }}"), 2)
        self.assertIn("rollback_check() {", self.rollback_text)
        self.assertIn("mint_cloud_token", self.rollback_text[self.rollback_text.index("rollback_check() {"):])
        self.assertIn("rollback_postflight() {", self.rollback_text)

    def test_runtime_update_uses_slot_setting_and_only_owned_values(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            log = Path(temporary) / "az.log"
            fake_az = Path(temporary) / "az"
            fake_az.write_text(
                "#!/usr/bin/env bash\n"
                "printf '%s\\n' \"$*\" >> \"$AZ_LOG\"\n"
            )
            fake_az.chmod(0o700)
            digest = "a" * 64
            result = self.run_bash(
                f'source "{HELPERS}"; '
                f'azure_update_sitecontainer_runtime '
                f'"registry.azurecr.io/elsa-control/api@sha256:{digest}" true 233 true',
                {
                    "PATH": f"{temporary}:{os.environ['PATH']}",
                    "AZ_LOG": str(log),
                    "AZURE_RESOURCE_GROUP": "rg-test",
                    "AZURE_WEBAPP_NAME": "api-test",
                },
            )
            self.assertEqual(0, result.returncode, result.stderr)
            calls = log.read_text().splitlines()
            self.assertTrue(any("sitecontainers update" in call for call in calls))
            self.assertTrue(any("--slot-settings Application__BuildNumber=233" in call for call in calls))
            self.assertFalse(any("--settings Application__BuildNumber" in call for call in calls))

    @contextmanager
    def health_fixture(self):
        with tempfile.TemporaryDirectory() as temporary:
            fake_az = Path(temporary) / "az"
            fake_curl = Path(temporary) / "curl"
            fake_sleep = Path(temporary) / "sleep"
            fake_az.write_text('#!/usr/bin/env bash\nprintf "api.test\\n"\n')
            fake_curl.write_text(
                "#!/usr/bin/env bash\n"
                "while [ \"$#\" -gt 0 ]; do\n"
                "  if [ \"$1\" = --output ]; then output=$2; shift 2; else shift; fi\n"
                "done\n"
                "printf '%s' \"$HEALTH_RESPONSE\" > \"$output\"\n"
                "printf 200\n"
            )
            fake_sleep.write_text("#!/usr/bin/env bash\nexit 0\n")
            for path in (fake_az, fake_curl, fake_sleep):
                path.chmod(0o700)
            yield {
                "PATH": f"{temporary}:{os.environ['PATH']}",
                "AZURE_RESOURCE_GROUP": "rg-test",
                "AZURE_WEBAPP_NAME": "api-test",
                "AZURE_HEALTH_ATTEMPTS": "2",
                "AZURE_HEALTH_RETRY_SECONDS": "0",
                "HEALTH_RESPONSE": '{"status":"ok"}',
            }

    def test_shared_health_keeps_legacy_empty_identity_fallback(self) -> None:
        with self.health_fixture() as environment:
            for build, image, should_pass in [("", "", True), ("233", "", True), ("233", "source", False)]:
                with self.subTest(build=build, image=image):
                    result = self.run_bash(
                        f'source "{HELPERS}"; azure_wait_for_stable_api_health "{build}" "{image}" legacy',
                        environment,
                    )
                    self.assertEqual(should_pass, result.returncode == 0, result.stdout + result.stderr)

    def test_legacy_rollback_rejects_candidate_when_previous_build_is_unknown(self) -> None:
        workflow = (ROOT / ".github/workflows/azure-api-deploy.yml").read_text()
        start = workflow.index('          expected_previous_build_number=')
        end = workflow.index('            "Rollback"', start) + len('            "Rollback"')
        script = f'source "{HELPERS}";\n' + workflow[start:end]
        with self.health_fixture() as environment:
            for build, should_pass in [(None, True), ("unknown", True), ("234", False)]:
                with self.subTest(observed_build=build):
                    health = {"status": "ok"}
                    if build is not None:
                        health["buildNumber"] = build
                    result = self.run_bash(script, {
                        **environment,
                        "PREVIOUS_HEALTH_BUILD_NUMBER": "",
                        "PREVIOUS_BUILD_NUMBER": "unknown",
                        "PREVIOUS_HEALTH_IMAGE_ID": "",
                        "HEALTH_RESPONSE": json.dumps(health),
                    })
                    self.assertEqual(should_pass, result.returncode == 0, result.stdout + result.stderr)

    def test_health_failure_cleans_nested_temp_file_under_set_u(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            response = Path(temporary) / "health.json"
            for name, contents in {
                "az": '#!/usr/bin/env bash\nprintf "api.test\\n"\n',
                "curl": (
                    "#!/usr/bin/env bash\n"
                    "while [ \"$#\" -gt 0 ]; do\n"
                    "  if [ \"$1\" = --output ]; then output=$2; shift 2; else shift; fi\n"
                    "done\n"
                    "printf '{\"status\":\"degraded\"}' > \"$output\"\n"
                    "printf 503\n"
                ),
                "sleep": "#!/usr/bin/env bash\nexit 0\n",
                "mktemp": f"#!/usr/bin/env bash\nprintf '%s\\n' '{response}'\n",
            }.items():
                path = Path(temporary) / name
                path.write_text(contents)
                path.chmod(0o700)
            result = self.run_bash(
                f'source "{HELPERS}"; '
                'azure_wait_for_stable_api_health "" "" first || true; '
                'azure_wait_for_stable_api_health "" "" second || true',
                {
                    "PATH": f"{temporary}:{os.environ['PATH']}",
                    "AZURE_RESOURCE_GROUP": "rg-test",
                    "AZURE_WEBAPP_NAME": "api-test",
                    "AZURE_HEALTH_ATTEMPTS": "1",
                    "AZURE_HEALTH_RETRY_SECONDS": "0",
                },
            )
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
            self.assertFalse(response.exists())

    def test_hold_reserves_restore_budget_inside_fixed_cap(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            sleep_log = Path(temporary) / "sleep.log"
            fake_sleep = Path(temporary) / "sleep"
            fake_date = Path(temporary) / "date"
            fake_sleep.write_text('#!/usr/bin/env bash\nprintf "%s\\n" "$1" >> "$SLEEP_LOG"\n')
            fake_date.write_text('#!/usr/bin/env bash\nprintf "100\\n"\n')
            fake_sleep.chmod(0o700)
            fake_date.chmod(0o700)
            result = self.run_bash(
                f'source "{ROLLBACK}"; rollback_hold',
                {
                    "PATH": f"{temporary}:{os.environ['PATH']}",
                    "WRITE_EPOCH": "100",
                    "ROLLBACK_HOLD_SECONDS": "9999",
                    "SLEEP_LOG": str(sleep_log),
                    "TARGET_ENVIRONMENT": "test",
                },
            )
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
            # 1200 seconds cap minus the 300 second restore reserve.
            self.assertEqual("900", sleep_log.read_text().strip())

    def test_sorted_line_hash_uses_real_newline_separator(self) -> None:
        result = self.run_bash(
            f'source "{ROLLBACK}"; printf "b\\na\\n" | rollback_sorted_lines_hash',
            {},
        )
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertEqual(hashlib.sha256(b"a\nb").hexdigest(), result.stdout.strip())

    def test_restore_after_cancellation_without_a_switch_is_a_noop(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            events = Path(temporary) / "events.log"
            fake_az = Path(temporary) / "az"
            fake_az.write_text(f'#!/usr/bin/env bash\nprintf az >> "{events}"\n')
            fake_az.chmod(0o700)
            result = self.run_bash(
                f'source "{ROLLBACK}"; PREFLIGHT_SUCCESS=true; unset WRITE_EPOCH; rollback_restore',
                {
                    "PATH": f"{temporary}:{os.environ['PATH']}",
                    "TARGET_ENVIRONMENT": "test",
                },
            )
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
            self.assertFalse(events.exists())

    def test_restore_recovers_private_epoch_when_switch_output_is_missing(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            events = Path(temporary) / "events.log"
            marker = Path(temporary) / "elsa-control-rollback-77.write-epoch"
            marker.write_text("100\n")
            (Path(temporary) / "date").write_text("#!/usr/bin/env bash\nprintf '100\\n'\n")
            (Path(temporary) / "date").chmod(0o700)
            digest = "sha256:" + "a" * 64
            script = f'''
                source "{ROLLBACK}"
                azure_require_immutable_digest() {{ :; }}
                azure_update_sitecontainer_runtime() {{ printf update >> "{events}"; }}
                azure_restart_and_verify_api_runtime() {{ printf restart >> "{events}"; }}
                azure_wait_for_stable_api_health() {{ printf health >> "{events}"; }}
                serving_image_reference() {{ printf '%s\\n' "registry.azurecr.io/elsa-control/api@{digest}"; }}
                image_digest() {{ printf '%s\\n' "{digest}"; }}
                rollback_assert_build_setting() {{ :; }}
                PREFLIGHT_SUCCESS=true
                ORIGINAL_IMAGE_REFERENCE="registry.azurecr.io/elsa-control/api@{digest}"
                EXPECTED_DIGEST="{digest}"
                AZURE_CONTAINER_REGISTRY_ENDPOINT="registry.azurecr.io"
                EXPECTED_BUILD_NUMBER=233
                EXPECTED_IMAGE_ID="{'c' * 40}"
                BUILD_PRESENT=false
                BUILD_VALUE=""
                BUILD_SLOT_SETTING=false
                unset WRITE_EPOCH
                rollback_restore
            '''
            result = self.run_bash(
                script,
                {
                    "PATH": f"{temporary}:{os.environ['PATH']}",
                    "RUNNER_TEMP": temporary,
                    "GITHUB_RUN_ID": "77",
                },
            )
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
            self.assertEqual("updaterestarthealth", events.read_text())

    def test_pair_marker_requires_trusted_latest_state(self) -> None:
        marker = (
            "control-rollback-pair: approved N=sha256:"
            + "a" * 64
            + " N1=sha256:"
            + "b" * 64
            + " N1_COMMIT="
            + "c" * 40
        )
        with tempfile.TemporaryDirectory() as temporary:
            comments = Path(temporary) / "comments.json"
            comments.write_text(
                json.dumps(
                    [
                        {"body": marker, "author_association": "NONE", "user": {"login": "other"}},
                        {"body": marker, "author_association": "OWNER", "user": {"login": "owner"}},
                    ]
                )
            )
            script = (
                f'source "{ROLLBACK}"; fetch_issue_comments() {{ cat "{comments}"; }}; '
                'EXPECTED_DIGEST="sha256:' + "a" * 64 + '"; '
                'PREVIOUS_DIGEST="sha256:' + "b" * 64 + '"; '
                'N_MINUS_1_COMMIT="' + "c" * 40 + '"; rollback_require_pair_approval'
            )
            accepted = self.run_bash(script, {"TARGET_ENVIRONMENT": "test"})
            self.assertEqual(0, accepted.returncode, accepted.stdout + accepted.stderr)

            comments.write_text(
                json.dumps(
                    [
                        {"body": marker, "author_association": "OWNER", "user": {"login": "owner"}},
                        {"body": "control-rollback-pair: revoked", "author_association": "MEMBER", "user": {"login": "member"}},
                    ]
                )
            )
            revoked = self.run_bash(script, {"TARGET_ENVIRONMENT": "test"})
            self.assertNotEqual(0, revoked.returncode)

    def test_schema_gate_exercises_equal_missing_extra_and_read_failures(self) -> None:
        migration_id = "20261006000000_RollbackProof"
        with tempfile.TemporaryDirectory() as temporary:
            repo = Path(temporary)
            subprocess.run(["git", "init", "-q"], cwd=repo, check=True)
            subprocess.run(["git", "config", "user.email", "test@example.invalid"], cwd=repo, check=True)
            subprocess.run(["git", "config", "user.name", "Rollback test"], cwd=repo, check=True)
            migration_root = repo / "Migrations"
            migration_root.mkdir()
            (migration_root / "20261006000000_RollbackProof.Designer.cs").write_text(
                f'[Migration("{migration_id}")]\npublic sealed class Migration {{ }}\n'
            )
            subprocess.run(["git", "add", "."], cwd=repo, check=True)
            subprocess.run(["git", "commit", "-qm", "migration fixture"], cwd=repo, check=True)
            commit = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=repo, text=True).strip()

            def run_gate(applied: list[str], root: str = "Migrations") -> subprocess.CompletedProcess[str]:
                output = repo / "output"
                script = (
                    f'source "{ROLLBACK}"; '
                    f'ROLLBACK_MIGRATION_ROOT="{root}"; '
                    f'N_MINUS_1_COMMIT="{commit}"; rollback_schema_gate'
                )
                env = os.environ.copy()
                env.update(
                    {
                        "STAGING_APPLIED_MIGRATIONS_JSON": json.dumps(applied),
                        "GITHUB_OUTPUT": str(output),
                    }
                )
                return subprocess.run(
                    ["bash", "-c", script],
                    cwd=repo,
                    env=env,
                    capture_output=True,
                    text=True,
                    check=False,
                )

            equal = run_gate([migration_id])
            self.assertEqual(0, equal.returncode, equal.stdout + equal.stderr)
            self.assertIn("schema_gate=pass", (repo / "output").read_text())
            self.assertNotEqual(0, run_gate([]).returncode)
            self.assertNotEqual(0, run_gate([migration_id, "20261006000001_Extra"]).returncode)
            malformed = migration_root / "20261006000001_Malformed.Designer.cs"
            malformed.write_text("public sealed class Malformed { }\n")
            subprocess.run(["git", "add", "."], cwd=repo, check=True)
            subprocess.run(["git", "commit", "-qm", "malformed migration"], cwd=repo, check=True)
            malformed_commit = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=repo, text=True).strip()
            malformed_result = subprocess.run(
                ["bash", "-c", f'source "{ROLLBACK}"; ROLLBACK_MIGRATION_ROOT=Migrations; N_MINUS_1_COMMIT={malformed_commit}; rollback_schema_gate'],
                cwd=repo,
                capture_output=True,
                text=True,
                check=False,
                env={**os.environ, "STAGING_APPLIED_MIGRATIONS_JSON": "[]"},
            )
            self.assertNotEqual(0, malformed_result.returncode)
            unreadable = subprocess.run(
                ["bash", "-c", f'source "{ROLLBACK}"; ROLLBACK_MIGRATION_ROOT=Migrations; N_MINUS_1_COMMIT=deadbeef; STAGING_APPLIED_MIGRATIONS_JSON=[]; rollback_schema_gate'],
                cwd=repo,
                capture_output=True,
                text=True,
                check=False,
                env={**os.environ, "STAGING_APPLIED_MIGRATIONS_JSON": "[]"},
            )
            self.assertNotEqual(0, unreadable.returncode)

    def test_preflight_rejects_invalid_scope_and_inputs_before_external_access(self) -> None:
        scope = {
            "AZURE_SUBSCRIPTION_ID": "8e23037a-420f-4ad0-9594-9d194de29e84",
            "AZURE_RESOURCE_GROUP": "rg-valence-control-staging",
            "AZURE_WEBAPP_NAME": "api-tud53zotij43k",
            "AZURE_CONTAINER_REGISTRY_ENDPOINT": "elsacontrolacrtud53zotij43k.azurecr.io",
        }
        environment = {
            **scope,
            **{f"EXPECTED_STAGING_{key}": value for key, value in scope.items()},
            "TARGET_ENVIRONMENT": "test",
            "PREVIOUS_DIGEST": "sha256:" + "b" * 64,
            "EXPECTED_DIGEST": "sha256:" + "a" * 64,
            "INPUT_PREVIOUS_DIGEST": "",
            "INPUT_EXPECTED_DIGEST": "",
        }
        cases = [
            (f"environment-{value or 'missing'}", {"TARGET_ENVIRONMENT": value}, "only in the test environment")
            for value in ("production", "staging", "")
        ]
        for key in scope:
            expected_key = f"EXPECTED_STAGING_{key}"
            cases.extend([
                (f"{key}-mismatch", {key: "not-staging"}, "explicit staging allowlist value"),
                (f"{key}-missing", {key: ""}, "is required"),
                (f"{key}-no-allowlist", {expected_key: ""}, "no explicit staging allowlist value"),
                (f"{key}-production", {key: "prod-target", expected_key: "prod-target"}, "production identifier"),
            ])
        for key in ("PREVIOUS_DIGEST", "EXPECTED_DIGEST"):
            for index, value in enumerate(("", "233", "sha256:" + "g" * 64,
                                           "sha256:" + "a" * 63, "registry/api@sha256:" + "a" * 64)):
                cases.append((f"{key}-invalid-{index}", {key: value}, "must be a sha256 digest"))
        cases.append(("identical-digests", {"PREVIOUS_DIGEST": environment["EXPECTED_DIGEST"]},
                      "digests must be different"))

        with tempfile.TemporaryDirectory() as temporary:
            call_log = Path(temporary) / "external-call"
            environment["GUARD_CALL_LOG"] = str(call_log)
            script = f'''
                source "{ROLLBACK}"
                unexpected_access() {{ printf '%s\\n' external > "$GUARD_CALL_LOG"; return 97; }}
                az() {{ unexpected_access; }}
                gh() {{ unexpected_access; }}
                curl() {{ unexpected_access; }}
                check_exclusive() {{ unexpected_access; }}
                rollback_preflight
            '''
            # The valid control must reach the boundary, so an earlier unrelated
            # failure cannot make every negative case pass.
            accepted = self.run_bash(script, environment)
            self.assertEqual(97, accepted.returncode, accepted.stdout + accepted.stderr)
            self.assertTrue(call_log.exists())
            call_log.unlink()

            for label, overrides, message in cases:
                with self.subTest(case=label):
                    call_log.unlink(missing_ok=True)
                    rejected = self.run_bash(script, {**environment, **overrides})
                    self.assertNotEqual(0, rejected.returncode)
                    self.assertIn(message, rejected.stdout + rejected.stderr)
                    self.assertFalse(call_log.exists(), "invalid preflight reached external access")

    def test_scope_and_tag_baseline_refuse_before_any_provider_write(self) -> None:
        hostile_scope = self.run_bash(
            f'source "{ROLLBACK}"; rollback_require_scope',
            {
                "TARGET_ENVIRONMENT": "test",
                "AZURE_SUBSCRIPTION_ID": "production-subscription",
                "AZURE_RESOURCE_GROUP": "rg-valence-control-staging",
                "AZURE_WEBAPP_NAME": "api-tud53zotij43k",
                "AZURE_CONTAINER_REGISTRY_ENDPOINT": "elsacontrolacrtud53zotij43k.azurecr.io",
                "EXPECTED_STAGING_AZURE_SUBSCRIPTION_ID": "8e23037a-420f-4ad0-9594-9d194de29e84",
                "EXPECTED_STAGING_AZURE_RESOURCE_GROUP": "rg-valence-control-staging",
                "EXPECTED_STAGING_AZURE_WEBAPP_NAME": "api-tud53zotij43k",
                "EXPECTED_STAGING_AZURE_CONTAINER_REGISTRY_ENDPOINT": "elsacontrolacrtud53zotij43k.azurecr.io",
            },
        )
        self.assertNotEqual(0, hostile_scope.returncode)
        tag_baseline = self.run_bash(
            f'source "{ROLLBACK}"; '
            'rollback_require_scope(){ :; }; rollback_require_inputs(){ :; }; check_exclusive(){ :; }; '
            'require_smoke_inputs(){ :; }; mint_cloud_token(){ :; }; '
            'serving_image_reference(){ printf "%s\\n" "elsacontrolacrtud53zotij43k.azurecr.io/elsa-control/api:e190"; }; '
            'PREVIOUS_DIGEST="sha256:' + "b" * 64 + '"; EXPECTED_DIGEST="sha256:' + "a" * 64 + '"; '
            'rollback_preflight',
            {"TARGET_ENVIRONMENT": "test"},
        )
        self.assertNotEqual(0, tag_baseline.returncode)
        self.assertIn("tag-backed", tag_baseline.stdout + tag_baseline.stderr)

    def test_provenance_uses_immediate_predecessor_and_real_job_steps(self) -> None:
        current = "e" * 40
        predecessor = "c" * 40
        older = "b" * 40
        fixture = "f" * 40
        expected_digest = "sha256:" + "a" * 64
        previous_digest = "sha256:" + "b" * 64
        deployment_rows = "\n".join(
            [
                f"4\t{fixture}\t2026-10-06T12:00:00Z",
                f"3\t{current}\t2026-10-06T11:00:00Z",
                f"2\t{predecessor}\t2026-10-06T10:00:00Z",
                f"1\t{older}\t2026-10-06T09:00:00Z",
            ]
        )
        run_json = {
            "n": json.dumps({"conclusion": "success", "head_sha": current, "run_number": 234}),
            "predecessor": json.dumps({"conclusion": "success", "head_sha": predecessor, "run_number": 233}),
            "older": json.dumps({"conclusion": "success", "head_sha": older, "run_number": 232}),
        }
        job_json = json.dumps(
            {
                "name": "Validate and Deploy Control API",
                "conclusion": "success",
                "steps": [
                    {"name": "Deploy API app", "conclusion": "success"},
                    {"name": "Verify deployed API health", "conclusion": "success"},
                ],
            }
        )
        shell = f'''
            source "{ROLLBACK}"
            gh_get() {{
              local endpoint="" arg
              for arg in "$@"; do
                case "$arg" in repos/example/control/*) endpoint="$arg";; esac
              done
              case "$endpoint" in
                "repos/example/control/deployments?environment=test&per_page=100") printf '%s\\n' "$DEPLOYMENT_ROWS" ;;
                */deployments/4/statuses*) printf '%s\\n' '[{{"log_url":"https://github.com/example/control/actions/runs/40/job/1"}}]' ;;
                */deployments/3/statuses*) printf '%s\\n' '[{{"log_url":"https://github.com/example/control/actions/runs/30/job/1"}}]' ;;
                */deployments/2/statuses*) printf '%s\\n' "$PREDECESSOR_STATUS" ;;
                */deployments/1/statuses*) printf '%s\\n' '[{{"log_url":"https://github.com/example/control/actions/runs/10/job/1"}}]' ;;
                */actions/runs/30/jobs*) printf '%s\\n' "$JOB_JSON" ;;
                */actions/runs/20/jobs*) printf '%s\\n' "$JOB_JSON" ;;
                */actions/runs/10/jobs*) printf '%s\\n' "$JOB_JSON" ;;
                */actions/runs/30) printf '%s\\n' "$RUN_N_JSON" ;;
                */actions/runs/20) printf '%s\\n' "$RUN_PREDECESSOR_JSON" ;;
                */actions/runs/10) printf '%s\\n' "$RUN_OLDER_JSON" ;;
                *) return 1 ;;
              esac
            }}
            workflow_run_path() {{
              case "$1" in 40) printf '%s\\n' '.github/workflows/staging-compat-fixture.yml' ;; *) printf '%s\\n' '.github/workflows/azure-api-deploy.yml' ;; esac
            }}
            rollback_deployment_digest() {{
              case "$2" in
                {current}) printf '%s\\n' "$EXPECTED_DIGEST" ;;
                {predecessor}) printf '%s\\n' "$PREDECESSOR_DIGEST" ;;
                {older}) printf '%s\\n' "$PREVIOUS_DIGEST" ;;
              esac
            }}
            rollback_write_output() {{ :; }}
            rollback_resolve_n1_provenance
        '''
        environment = {
            "GITHUB_REPOSITORY": "example/control",
            "AZURE_CONTAINER_REGISTRY_ENDPOINT": "registry.azurecr.io",
            "EXPECTED_N_COMMIT": current,
            "EXPECTED_DIGEST": expected_digest,
            "PREVIOUS_DIGEST": previous_digest,
            "DEPLOYMENT_ROWS": deployment_rows,
            "RUN_N_JSON": run_json["n"],
            "RUN_PREDECESSOR_JSON": run_json["predecessor"],
            "RUN_OLDER_JSON": run_json["older"],
            "JOB_JSON": job_json,
            "PREDECESSOR_DIGEST": "sha256:" + "d" * 64,
            "PREDECESSOR_STATUS": json.dumps([{"log_url": "https://github.com/example/control/actions/runs/20/job/1"}]),
        }
        mismatch = self.run_bash(shell, environment)
        self.assertNotEqual(0, mismatch.returncode)
        self.assertIn("immediately preceding successful Deploy-staging app digest", mismatch.stdout + mismatch.stderr)

        environment["PREDECESSOR_DIGEST"] = previous_digest
        accepted = self.run_bash(shell, environment)
        self.assertEqual(0, accepted.returncode, accepted.stdout + accepted.stderr)

        for status in ("[]", "null", "[{}]", "not-json"):
            with self.subTest(status=status):
                refused = self.run_bash(shell, {**environment, "PREDECESSOR_STATUS": status})
                self.assertNotEqual(0, refused.returncode)

    def test_capability_parser_supports_prior_version_and_new_literal_capability(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            git = Path(temporary) / "git"
            git.write_text('#!/usr/bin/env bash\nprintf "%s" "$TEST_SOURCE"\n')
            git.chmod(0o700)
            for version in ("const int CurrentContractVersion = 1;", "new CloudCompatibilityResponse(1, capabilities);"):
                with self.subTest(version=version):
                    source = "\n".join(
                        [
                            version,
                            "const string ProvisioningProgressCapability = \"hosted.instances.provisioning-progress.v1\";",
                            "private static readonly string[] Capabilities = [",
                            "    \"cloud.bootstrap.v1\",",
                            "    \"hosted.instances.reconciliation-cleanup.v1\",",
                            "    ProvisioningProgressCapability",
                            "];",
                        ]
                    )
                    result = self.run_bash(
                        f'source "{ROLLBACK}"; rollback_capabilities_at_commit previous',
                        {
                            "PATH": f"{temporary}:{os.environ['PATH']}",
                            "TEST_SOURCE": source,
                        },
                    )
                    self.assertEqual(0, result.returncode, result.stdout + result.stderr)
                    self.assertEqual(
                        {
                            "contractVersion": 1,
                            "capabilities": [
                                "cloud.bootstrap.v1",
                                "hosted.instances.reconciliation-cleanup.v1",
                                "hosted.instances.provisioning-progress.v1",
                            ],
                        },
                        json.loads(result.stdout),
                    )

    def test_bff_prediction_distinguishes_compatible_and_incompatible_contracts(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            candidate = Path(temporary) / "candidate.json"
            for version, capabilities, expected in (
                (1, ["bootstrap", "billing"], "compatible"),
                (1, ["billing", "bootstrap", "extra"], "compatible"),
                (1, ["bootstrap"], "gated"),
                (2, ["bootstrap", "billing"], "gated"),
            ):
                with self.subTest(version=version, capabilities=capabilities):
                    candidate.write_text(json.dumps({"contractVersion": version, "capabilities": capabilities}))
                    result = self.run_bash(
                        f'source "{ROLLBACK}"; rollback_predict_bff_outcome "{candidate}"; '
                        'printf "%s" "$ROLLBACK_BFF_OUTCOME"',
                        {"CLOUD_REQUIRED_CAPABILITIES_JSON": '["bootstrap","billing"]'},
                    )
                    self.assertEqual(0, result.returncode, result.stderr)
                    self.assertEqual(expected, result.stdout)
                    self.assertEqual("", result.stderr)

    def test_bff_prediction_refuses_unreadable_or_invalid_contracts(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            candidate = Path(temporary) / "candidate.json"
            valid = '{"contractVersion":1,"capabilities":["bootstrap"]}'
            for source, required in (
                (None, '["bootstrap"]'),
                ("not-json", '["bootstrap"]'),
                (valid, "not-json"),
                (valid, "{}"),
                (valid, "[]"),
                (valid, '[""]'),
                ('{"contractVersion":1,"capabilities":null}', '["bootstrap"]'),
                ('{"contractVersion":1,"capabilities":[7]}', '["bootstrap"]'),
                ('{"contractVersion":"1","capabilities":["bootstrap"]}', '["bootstrap"]'),
                ('{"contractVersion":1.5,"capabilities":["bootstrap"]}', '["bootstrap"]'),
                (valid + "\n" + valid, '["bootstrap"]'),
            ):
                with self.subTest(source=source, required=required):
                    if source is None:
                        candidate.unlink(missing_ok=True)
                    else:
                        candidate.write_text(source)
                    result = self.run_bash(
                        f'source "{ROLLBACK}"; rollback_predict_bff_outcome "{candidate}"; '
                        'printf "%s" "$ROLLBACK_BFF_OUTCOME"',
                        {"CLOUD_REQUIRED_CAPABILITIES_JSON": required},
                    )
                    self.assertNotEqual(0, result.returncode)
                    self.assertNotIn("gated", result.stdout)

    def test_instance_proof_uses_real_instance_id_field_and_no_mutating_bootstrap(self) -> None:
        self.assertIn(".items[].instanceId", self.rollback_text)
        self.assertNotIn(".items[].id", self.rollback_text)
        self.assertNotIn("capture_linked_org_context", self.rollback_text)
        self.assertIn("/api/me/organizations", self.rollback_text)
        self.assertIn("/api/workspaces/${workspace}/instances?page=", self.rollback_text)

    def test_data_read_rejects_invalid_ids_and_incomplete_pagination(self) -> None:
        identifier = "12345678-1234-4234-8234-123456789abc"
        context = {"account": {"id": identifier}, "organizations": [{"id": identifier}], "workspaces": [{"id": identifier}]}
        page = {"items": [{"instanceId": identifier}], "totalCount": 1, "page": 1, "pageSize": 100, "hasMore": False}
        with tempfile.TemporaryDirectory() as temporary:
            curl = Path(temporary) / "curl"
            curl.write_text('''#!/usr/bin/env bash
                while [ "$#" -gt 0 ]; do
                  case "$1" in --output) output=$2; shift 2;; *) url=$1; shift;; esac
                done
                case "$url" in */instances*) printf '%s' "$TEST_PAGE" > "$output";; *) printf '%s' "$TEST_CONTEXT" > "$output";; esac
                printf 200
            ''')
            curl.chmod(0o700)
            script = f'source "{ROLLBACK}"; default_host_name() {{ printf offline.test; }}; rollback_capture_non_admin_data "{temporary}/hash"'
            environment = {"PATH": f"{temporary}:{os.environ['PATH']}", "STAGING_CLOUD_ACCESS_TOKEN": "offline-test-token"}
            for title, candidate_context, candidate_page, expected in (
                ("valid", context, page, 0),
                ("missing organization id", {**context, "organizations": [{}]}, page, 1),
                ("missing hasMore", context, {key: value for key, value in page.items() if key != "hasMore"}, 1),
                ("wrong page", context, {**page, "page": 2}, 1),
                ("negative total", context, {**page, "totalCount": -1}, 1),
                ("fractional total", context, {**page, "totalCount": 1.5}, 1),
            ):
                with self.subTest(title=title):
                    result = self.run_bash(script, {**environment, "TEST_CONTEXT": json.dumps(candidate_context), "TEST_PAGE": json.dumps(candidate_page)})
                    self.assertEqual(expected, result.returncode, result.stdout + result.stderr)

    def test_changed_data_paths_require_exact_trusted_path_hash_signoff(self) -> None:
        changed = "src/Deployment/Example.cs"
        digest = hashlib.sha256(changed.encode()).hexdigest()
        marker = f"control-rollback-data: approved N=N-digest N1=N1-digest paths_sha256={digest}"
        script = f'''
            source "{ROLLBACK}"
            git() {{ printf '%s\\n' 'src/Deployment/Example.cs'; }}
            fetch_issue_comments() {{ printf '%s' "$TEST_COMMENTS"; }}
            EXPECTED_DIGEST=N-digest; PREVIOUS_DIGEST=N1-digest
            EXPECTED_N_COMMIT=current; N_MINUS_1_COMMIT=previous; ROLLBACK_PAIR_APPROVED=true
            rollback_persisted_data_check
        '''
        for body, expected in ((marker, 0), (marker.replace(digest, "0" * 64), 1), ("control-rollback-data: revoked", 1)):
            with self.subTest(marker=body):
                comments = [{"author_association": "OWNER", "body": body}]
                result = self.run_bash(script, {"TEST_COMMENTS": json.dumps(comments)})
                self.assertEqual(expected, result.returncode, result.stdout + result.stderr)

    def test_fail_closed_guards_are_present(self) -> None:
        self.assertIn("tag-backed; pin N by digest", self.rollback_text)
        self.assertIn("rollback_verify_source_tag_digest", self.rollback_text)
        self.assertIn("migration tree could not be read", self.rollback_text)
        self.assertNotIn("git diff --name-only \"$N_MINUS_1_COMMIT\" \"$EXPECTED_N_COMMIT\" -- \\\n    src/PackageCatalog src/Deployment src/Domain src/Hosting/ElsaControl.Api/Cloud \\\n    docs/cloud* specs/*/contracts 2>/dev/null || true", self.rollback_text)
        self.assertIn("control-rollback-pair: approved", self.rollback_text)


if __name__ == "__main__":
    unittest.main()
