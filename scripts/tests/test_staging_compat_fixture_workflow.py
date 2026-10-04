#!/usr/bin/env python3
"""Offline contracts for the staging compatibility fixture workflow."""

from __future__ import annotations

import importlib.util
import json
import os
import stat
import subprocess
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
EXCLUSIVE_SPEC = importlib.util.spec_from_file_location(
    "staging_compat_exclusive",
    ROOT / "scripts" / "lib" / "staging_compat_exclusive.py",
)
exclusive = importlib.util.module_from_spec(EXCLUSIVE_SPEC)
assert EXCLUSIVE_SPEC.loader is not None
EXCLUSIVE_SPEC.loader.exec_module(exclusive)
conflicting_runs = exclusive.conflicting_runs
freeze_is_on = exclusive.freeze_is_on
qa_window_is_open = exclusive.qa_window_is_open
compute_hold_seconds = exclusive.compute_hold_seconds
FIXTURE_CAP_SECONDS = exclusive.FIXTURE_CAP_SECONDS
RESTORE_BUDGET_SECONDS = exclusive.RESTORE_BUDGET_SECONDS
MAX_HOLD_SECONDS = exclusive.MAX_HOLD_SECONDS
JOB_BACKSTOP_SECONDS = exclusive.JOB_BACKSTOP_SECONDS
WORKFLOW = ROOT / ".github" / "workflows" / "staging-compat-fixture.yml"
SCRIPT = ROOT / "scripts" / "staging-compat-fixture.sh"
CI = ROOT / ".github" / "workflows" / "ci.yml"
DEPLOY = ROOT / ".github" / "workflows" / "azure-api-deploy.yml"

BASELINE_CAPABILITIES = [
    "cloud.bootstrap.v1",
    "hosted.instances.list.v1",
    "hosted.instances.create.v1",
    "hosted.instances.status.v1",
    "hosted.instances.provisioning-progress.v1",
    "hosted.instances.overview.v1",
    "hosted.studio.handoff.issue.v1",
    "hosted.instances.quota-problem.v1",
    "hosted.instances.confirmed-delete.v1",
    "hosted.subscription.manage.v1",
    "hosted.deployments.audit.v1",
]


def trusted(body: str) -> dict[str, object]:
    return {
        "body": body,
        "author_association": "MEMBER",
        "user": {"login": "sfmskywalker"},
    }


def outsider(body: str) -> dict[str, object]:
    return {
        "body": body,
        "author_association": "NONE",
        "user": {"login": "random-commenter"},
    }


class StagingCompatExclusiveTests(unittest.TestCase):
    def test_freeze_requires_the_latest_trusted_on_marker(self) -> None:
        self.assertFalse(freeze_is_on([]))
        self.assertTrue(freeze_is_on([trusted("staging-freeze: on\nExclusive window.")]))
        self.assertFalse(
            freeze_is_on([trusted("staging-freeze: on"), trusted("staging-freeze: off")])
        )
        self.assertTrue(
            freeze_is_on([trusted("Please freeze staging"), trusted("staging-freeze: ON")])
        )

    def test_freeze_ignores_markers_from_untrusted_commenters(self) -> None:
        self.assertFalse(freeze_is_on([outsider("staging-freeze: on")]))
        self.assertTrue(
            freeze_is_on([outsider("staging-freeze: off"), trusted("staging-freeze: on")])
        )
        self.assertTrue(
            freeze_is_on([trusted("staging-freeze: on"), outsider("staging-freeze: off")])
        )

    def test_open_issue_or_trusted_open_marker_is_an_open_qa_window(self) -> None:
        self.assertTrue(qa_window_is_open("open", []))
        self.assertFalse(qa_window_is_open("closed", []))
        self.assertTrue(qa_window_is_open("closed", [trusted("qa-window: open")]))
        self.assertFalse(
            qa_window_is_open("closed", [trusted("qa-window: open"), trusted("qa-window: closed")])
        )

    def test_qa_window_ignores_markers_from_untrusted_commenters(self) -> None:
        self.assertFalse(qa_window_is_open("closed", [outsider("qa-window: open")]))
        self.assertTrue(
            qa_window_is_open("closed", [outsider("qa-window: closed"), trusted("qa-window: open")])
        )

    def test_hold_budget_reserves_restore_inside_the_fixture_cap(self) -> None:
        self.assertLessEqual(FIXTURE_CAP_SECONDS + RESTORE_BUDGET_SECONDS, JOB_BACKSTOP_SECONDS)
        self.assertEqual(20 * 60, FIXTURE_CAP_SECONDS)
        self.assertEqual(4 * 60, RESTORE_BUDGET_SECONDS)
        self.assertEqual(30 * 60, JOB_BACKSTOP_SECONDS)
        self.assertEqual(MAX_HOLD_SECONDS, compute_hold_seconds(0))
        self.assertEqual(660, compute_hold_seconds(300))
        self.assertEqual(0, compute_hold_seconds(1000))
        self.assertEqual(
            0,
            compute_hold_seconds(FIXTURE_CAP_SECONDS - RESTORE_BUDGET_SECONDS + 1),
        )

    def test_conflicting_runs_ignore_this_run_and_non_staging_deploys(self) -> None:
        self.assertEqual(
            [],
            conflicting_runs(
                [
                    {
                        "id": "11",
                        "name": "Staging Control compatibility fixture",
                        "path": ".github/workflows/staging-compat-fixture.yml",
                        "status": "in_progress",
                        "environment": "test",
                    },
                    {
                        "id": "12",
                        "name": "Azure Control API Deploy",
                        "path": ".github/workflows/azure-api-deploy.yml",
                        "status": "in_progress",
                    },
                    {
                        "id": "13",
                        "name": "CI",
                        "path": ".github/workflows/ci.yml",
                        "status": "in_progress",
                    },
                    {
                        "id": "14",
                        "name": "CI",
                        "path": ".github/workflows/ci.yml",
                        "status": "completed",
                        "environment": "test",
                    },
                ],
                "11",
            ),
        )
        self.assertEqual(
            ["22", "33"],
            conflicting_runs(
                [
                    {
                        "id": "22",
                        "name": "Azure Control API Deploy",
                        "path": ".github/workflows/azure-api-deploy.yml",
                        "status": "queued",
                        "environment": "test",
                    },
                    {
                        "id": "33",
                        "name": "Managed Prove",
                        "path": ".github/workflows/prove.yml",
                        "status": "in_progress",
                    },
                ],
                "11",
            ),
        )
        self.assertEqual(
            ["44", "55"],
            conflicting_runs(
                [
                    {
                        "id": "44",
                        "name": "Staging Control compatibility fixture",
                        "path": ".github/workflows/staging-compat-fixture.yml",
                        "status": "queued",
                    },
                    {
                        "id": "55",
                        "name": "Azure Control API Deploy",
                        "path": ".github/workflows/azure-api-deploy.yml",
                        "status": "in_progress",
                        "environment": "test",
                    },
                    {
                        "id": "66",
                        "name": "CI",
                        "path": ".github/workflows/ci.yml",
                        "status": "in_progress",
                    },
                ],
                "11",
            ),
        )


class StagingCompatWorkflowTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.source = WORKFLOW.read_text()
        cls.script = SCRIPT.read_text()
        cls.ci = CI.read_text()
        cls.deploy = DEPLOY.read_text()

    def test_manual_dispatch_only_in_the_test_environment_with_mode_input(self) -> None:
        self.assertIn("name: Staging Control compatibility fixture\n", self.source)
        self.assertIn("workflow_dispatch:", self.source)
        self.assertNotIn("pull_request:", self.source)
        self.assertNotIn("push:", self.source)
        self.assertNotIn("schedule:", self.source)
        self.assertNotIn("workflow_call:", self.source)
        self.assertIn("environment: test\n", self.source)
        self.assertIn("TARGET_ENVIRONMENT: test\n", self.source)
        self.assertIn("- missing-capability\n", self.source)
        self.assertIn("- older-contract\n", self.source)
        self.assertIn("FIXTURE_MODE: ${{ inputs.mode }}\n", self.source)
        self.assertIn("DISPATCH_MODE: ${{ inputs.mode }}\n", self.source)
        self.assertNotIn("${{ github.event.inputs.mode }}", self.source)
        self.assertIn("timeout-minutes: 30\n", self.source)
        self.assertIn("timeout-minutes: 12\n", self.source)
        self.assertIn("timeout-minutes: 4\n", self.source)
        self.assertNotIn("timeout-minutes: 20\n", self.source)
        self.assertIn("if: ${{ always() && steps.preflight.outcome == 'success' }}\n", self.source)
        self.assertNotIn("if: ${{ always() }}\n", self.source)
        self.assertIn('HEALTH_BUDGET_SECONDS: "300"', self.source)
        self.assertIn('HEALTH_BUDGET_SECONDS: "150"', self.source)
        self.assertIn("HEALTH_CURL_MAX_TIME", self.source)
        self.assertIn("fail() {\n  echo \"::error::$1\" >&2\n", self.script)
        self.assertIn("exit 1\n}", self.script)
        self.assertIn("kill -s TERM \"$$\"", self.script)
        self.assertIn("read_setting_presence", self.script)
        self.assertNotIn('if [ "$(read_setting_presence)"', self.script)
        self.assertIn("scripts/staging-compat-fixture.sh restore", self.source)
        self.assertIn("scripts/staging-compat-fixture.sh preflight", self.source)
        self.assertIn("scripts/staging-compat-fixture.sh arm", self.source)
        self.assertIn("az webapp restart", self.script)
        self.assertIn("appsettings delete", self.script)
        self.assertNotIn("production", self.source)
        self.assertIn("deployments: read\n", self.source)
        self.assertIn("secrets.CLOUD_COMPATIBILITY_TOKEN", self.source)
        self.assertEqual(1, self.source.count("secrets."))

    def test_shares_the_deploy_staging_concurrency_group(self) -> None:
        self.assertIn(
            "group: azure-api-deploy-${{ github.event_name == 'workflow_dispatch' && inputs.target_environment || 'production' }}",
            self.deploy,
        )
        self.assertIn("group: azure-api-deploy-test\n", self.source)
        self.assertIn("cancel-in-progress: false\n", self.source)

    def test_script_never_prints_setting_values(self) -> None:
        self.assertIn("query '[].name'", self.script)
        self.assertIn("--output none", self.script)
        self.assertIn("Never prints setting values", self.script)
        self.assertNotIn("query '[].value'", self.script)
        self.assertNotIn(".value", self.script)
        self.assertIn("CloudCompatibility__StagingFixture", self.script)
        self.assertIn('SETTING_NAME}=${FIXTURE_MODE}', self.script)
        self.assertNotIn("actions/runs?environment=", self.script)
        self.assertIn("deployments?environment=test", self.script)
        self.assertIn("compatibilityFixture", self.script)
        self.assertIn("inherit_errexit", self.script)
        self.assertIn("prove_authenticated_compatibility", self.script)
        self.assertIn("prove_bff_smoke_compatible", self.script)
        self.assertNotIn("container app", self.script.lower())
        self.assertNotIn("container-app", self.script.lower())
        self.assertNotIn("new revision", self.script.lower())
        self.assertIn("Manual removal", (ROOT / "docs" / "deployment" / "azure-app-service.md").read_text())

    def test_ci_runs_the_offline_workflow_contract(self) -> None:
        self.assertIn(
            "python3 scripts/tests/test_staging_compat_fixture_workflow.py",
            self.ci,
        )
        self.assertIn("scripts/staging-compat-fixture.sh", self.ci)


class StagingCompatScriptTests(unittest.TestCase):
    def environment(self, temporary: Path, **updates: str) -> dict[str, str]:
        env = os.environ.copy()
        env.update(
            {
                "PATH": f"{temporary}:{env.get('PATH', '')}",
                "TARGET_ENVIRONMENT": "test",
                "FIXTURE_MODE": "missing-capability",
                "AZURE_RESOURCE_GROUP": "rg-staging",
                "AZURE_WEBAPP_NAME": "elsa-control-staging",
                "AZURE_CONTAINER_REGISTRY_ENDPOINT": "example.azurecr.io",
                "GITHUB_REPOSITORY": "valence-works/elsa-control",
                "GITHUB_RUN_ID": "99",
                "GH_TOKEN": "unused",
                "CLOUD_COMPATIBILITY_TOKEN": "compat-token",
                "CLOUD_BFF_SMOKE_URL": "https://bff.example.test/smoke",
                "SETTING_READ_ATTEMPTS": "3",
                "SETTING_READ_RETRY_SECONDS": "0",
                "RESTORE_ABSENT_RECHECK_SECONDS": "0",
            }
        )
        env.update(updates)
        return env

    def write_fake_az(self, temporary: Path, setting_names: list[str], image: str) -> Path:
        names_file = temporary / "setting-names.txt"
        names_file.write_text("".join(f"{name}\n" for name in setting_names))
        fake = temporary / "az"
        fake.write_text(
            "#!/usr/bin/env bash\n"
            "set -euo pipefail\n"
            "printf '%s\\n' \"$*\" >> \"${AZ_CALL_LOG:?}\"\n"
            "if [[ \"$*\" == *\"webapp config appsettings list\"* && \"$*\" == *\"[].name\"* ]]; then\n"
            f"  cat {json.dumps(str(names_file))}\n"
            "  exit 0\n"
            "fi\n"
            "if [[ \"$*\" == *\"webapp config appsettings list\"* && \"$*\" == *\"CloudCompatibility__StagingFixture\"* ]]; then\n"
            "  if [ \"${AZ_FAIL_SETTING_LIST:-}\" = 1 ]; then echo failed-settings >&2; exit 2; fi\n"
            "  if [ -f \"${AZ_SETTING_PRESENT:-}\" ]; then echo 1; else echo 0; fi\n"
            "  exit 0\n"
            "fi\n"
            "if [[ \"$*\" == *\"webapp config show\"* ]]; then\n"
            f"  printf 'DOCKER|%s\\n' {json.dumps(image)}\n"
            "  exit 0\n"
            "fi\n"
            "if [[ \"$*\" == *\"webapp show\"* ]]; then\n"
            "  echo staging.example.test\n"
            "  exit 0\n"
            "fi\n"
            "if [[ \"$*\" == *\"webapp deployment list\"* ]]; then\n"
            "  echo \"${AZ_DEPLOYMENT_ID:-dep-1}\"\n"
            "  exit 0\n"
            "fi\n"
            "if [[ \"$*\" == *\"webapp restart\"* ]]; then\n"
            "  echo restarted >> \"${AZ_CALL_LOG}\"\n"
            "  exit 0\n"
            "fi\n"
            "if [[ \"$*\" == *\"webapp config appsettings set\"* ]]; then\n"
            "  echo present > \"${AZ_SETTING_PRESENT}\"\n"
            "  exit 0\n"
            "fi\n"
            "if [[ \"$*\" == *\"webapp config appsettings delete\"* ]]; then\n"
            "  rm -f \"${AZ_SETTING_PRESENT}\"\n"
            "  exit 0\n"
            "fi\n"
            "exit 41\n"
        )
        fake.chmod(fake.stat().st_mode | stat.S_IEXEC)
        return fake

    def write_fake_gh(self, temporary: Path) -> Path:
        fake = temporary / "gh"
        fake.write_text(
            "#!/usr/bin/env bash\n"
            "set -euo pipefail\n"
            "printf '%s\\n' \"$*\" >> \"${GH_CALL_LOG:?}\"\n"
            "if [[ \"$*\" == *'/issues/508/comments'* ]]; then\n"
            "  echo '[{\"body\":\"staging-freeze: on\",\"author_association\":\"MEMBER\",\"user\":{\"login\":\"sfmskywalker\"}}]'\n"
            "  exit 0\n"
            "fi\n"
            "if [[ \"$*\" == *'/issues/'*'/comments'* ]]; then\n"
            "  echo '[]'\n"
            "  exit 0\n"
            "fi\n"
            "if [[ \"$*\" == *'/issues/'* ]]; then\n"
            "  echo closed\n"
            "  exit 0\n"
            "fi\n"
            "if [[ \"$*\" == *'/actions/runs?'* ]]; then\n"
            "  if [ \"${GH_FAIL_RUNS:-}\" = 1 ]; then echo failed-runs >&2; exit 2; fi\n"
            "  if [ \"${GH_MALFORMED_RUNS:-}\" = 1 ]; then echo 'not-json'; exit 0; fi\n"
            "  if [ -n \"${GH_RUNS_JSON:-}\" ] && [ -f \"${GH_RUNS_JSON}\" ]; then\n"
            "    cat \"${GH_RUNS_JSON}\"\n"
            "  fi\n"
            "  exit 0\n"
            "fi\n"
            "if [[ \"$*\" == *'/deployments/'*'/statuses'* ]]; then\n"
            "  if [ \"${GH_FAIL_STATUSES:-}\" = 1 ]; then echo failed-status >&2; exit 2; fi\n"
            "  if [ -n \"${GH_DEPLOYMENT_STATUSES:-}\" ] && [ -f \"${GH_DEPLOYMENT_STATUSES}\" ]; then\n"
            "    cat \"${GH_DEPLOYMENT_STATUSES}\"\n"
            "  else\n"
            "    echo '[]'\n"
            "  fi\n"
            "  exit 0\n"
            "fi\n"
            "if [[ \"$*\" == *'/deployments?'* ]]; then\n"
            "  if [ \"${GH_FAIL_DEPLOYMENTS:-}\" = 1 ]; then echo failed-deployments >&2; exit 2; fi\n"
            "  if [ \"${GH_MALFORMED_DEPLOYMENTS:-}\" = 1 ]; then echo '{'; exit 0; fi\n"
            "  if [ -n \"${GH_DEPLOYMENT_IDS:-}\" ] && [ -f \"${GH_DEPLOYMENT_IDS}\" ]; then\n"
            "    cat \"${GH_DEPLOYMENT_IDS}\"\n"
            "  fi\n"
            "  exit 0\n"
            "fi\n"
            "exit 41\n"
        )
        fake.chmod(fake.stat().st_mode | stat.S_IEXEC)
        return fake

    def write_fake_curl(self, temporary: Path) -> Path:
        missing = [item for item in BASELINE_CAPABILITIES if item != "hosted.instances.provisioning-progress.v1"]
        compat = json.dumps({"contractVersion": 1, "capabilities": missing})
        baseline = json.dumps({"contractVersion": 1, "capabilities": BASELINE_CAPABILITIES})
        fake = temporary / "curl"
        fake.write_text(
            "#!/usr/bin/env bash\n"
            "set -euo pipefail\n"
            "printf '%s\\n' \"$*\" >> \"${CURL_CALL_LOG:-/dev/null}\"\n"
            "output=\"\"\n"
            "url=\"\"\n"
            "while [ \"$#\" -gt 0 ]; do\n"
            "  case \"$1\" in\n"
            "    --output) output=\"$2\"; shift 2 ;;\n"
            "    --write-out) shift 2 ;;\n"
            "    --silent|--show-error) shift ;;\n"
            "    --max-time) shift 2 ;;\n"
            "    --header) shift 2 ;;\n"
            "    *) url=\"$1\"; shift ;;\n"
            "  esac\n"
            "done\n"
            "if [[ \"$url\" == *'/api/cloud/compatibility'* ]]; then\n"
            f"  body={json.dumps(compat)}\n"
            "  if [ \"${COMPAT_MODE:-}\" = baseline ]; then\n"
            f"    body={json.dumps(baseline)}\n"
            "  fi\n"
            "  if [ \"${COMPAT_FAIL:-}\" = 1 ]; then printf '401'; exit 0; fi\n"
            "  printf '%s\\n' \"$body\" > \"$output\"\n"
            "  printf '200'\n"
            "  exit 0\n"
            "fi\n"
            "if [ -n \"${CLOUD_BFF_SMOKE_URL:-}\" ] && [ \"$url\" = \"${CLOUD_BFF_SMOKE_URL}\" ]; then\n"
            "  printf '%s\\n' '{\"compatible\":true}' > \"$output\"\n"
            "  printf '200'\n"
            "  exit 0\n"
            "fi\n"
            "body='{\"status\":\"ok\",\"buildNumber\":\"232\",\"imageId\":\"e5e9b84fd9a2f0b90931cf503f786eb89e2b0f02\",\"compatibilityFixture\":null}'\n"
            "if [ -n \"${HEALTH_RESPONSES:-}\" ] && [ -f \"${HEALTH_RESPONSES}\" ]; then\n"
            "  if IFS= read -r line < \"${HEALTH_RESPONSES}\"; then\n"
            "    body=\"$line\"\n"
            "    tail -n +2 \"${HEALTH_RESPONSES}\" > \"${HEALTH_RESPONSES}.next\"\n"
            "    mv \"${HEALTH_RESPONSES}.next\" \"${HEALTH_RESPONSES}\"\n"
            "  fi\n"
            "fi\n"
            "printf '%s\\n' \"$body\" > \"$output\"\n"
            "printf '200'\n"
        )
        fake.chmod(fake.stat().st_mode | stat.S_IEXEC)
        return fake

    def write_health_responses(self, temporary: Path, *bodies: str) -> Path:
        path = temporary / "health-responses.jsonl"
        path.write_text("".join(f"{body}\n" for body in bodies))
        return path

    def run_script(self, env: dict[str, str], *args: str) -> subprocess.CompletedProcess[str]:
        return subprocess.run(
            [str(SCRIPT), *args],
            cwd=ROOT,
            env=env,
            capture_output=True,
            text=True,
            check=False,
        )

    def assert_preflight_stops_at(self, env: dict[str, str], needle: str) -> None:
        result = self.run_script(env, "preflight")
        self.assertNotEqual(0, result.returncode)
        combined = result.stderr + result.stdout
        self.assertIn(needle, combined)
        self.assertNotIn("No conflicting Deploy staging or Prove runs.", combined)
        self.assertNotIn("Preflight captured", combined)
        self.assertNotIn("The fixture is absent", combined)
        az_log = Path(env["AZ_CALL_LOG"])
        if az_log.exists():
            az_text = az_log.read_text()
            self.assertNotIn("appsettings set", az_text)
            self.assertNotIn("webapp config show", az_text)
            self.assertNotIn("webapp show", az_text)
        curl_log = Path(env.get("CURL_CALL_LOG", ""))
        if curl_log and curl_log.exists():
            self.assertNotIn("/health", curl_log.read_text())

    def test_refuses_non_test_environment_before_touching_azure(self) -> None:
        with tempfile.TemporaryDirectory() as raw:
            temporary = Path(raw)
            env = self.environment(temporary, TARGET_ENVIRONMENT="production")
            env["AZ_CALL_LOG"] = str(temporary / "az.log")
            result = self.run_script(env, "preflight")
            self.assertNotEqual(0, result.returncode)
            self.assertIn("only in the test environment", result.stderr + result.stdout)
            self.assertFalse((temporary / "az.log").exists())

    def test_refuses_unknown_mode_without_echoing_it(self) -> None:
        with tempfile.TemporaryDirectory() as raw:
            temporary = Path(raw)
            env = self.environment(temporary, FIXTURE_MODE="mystery")
            env["AZ_CALL_LOG"] = str(temporary / "az.log")
            result = self.run_script(env, "preflight")
            self.assertNotEqual(0, result.returncode)
            combined = result.stderr + result.stdout
            self.assertIn("mode is not recognized", combined)
            self.assertNotIn("mystery", combined)
            self.assertFalse((temporary / "az.log").exists())

    def test_preflight_records_names_digest_build_commit_and_deployment_id(self) -> None:
        with tempfile.TemporaryDirectory() as raw:
            temporary = Path(raw)
            digest = "sha256:" + ("a" * 64)
            image = f"example.azurecr.io/elsa-control/api@{digest}"
            self.write_fake_az(temporary, ["Application__BuildNumber", "WEBSITES_PORT"], image)
            self.write_fake_gh(temporary)
            self.write_fake_curl(temporary)
            env = self.environment(temporary)
            env["AZ_CALL_LOG"] = str(temporary / "az.log")
            env["GH_CALL_LOG"] = str(temporary / "gh.log")
            env["GITHUB_OUTPUT"] = str(temporary / "github.output")
            env["GITHUB_STEP_SUMMARY"] = str(temporary / "summary.md")
            result = self.run_script(env, "preflight")
            self.assertEqual(0, result.returncode, result.stderr + result.stdout)
            output = (temporary / "github.output").read_text()
            summary = (temporary / "summary.md").read_text()
            self.assertIn(f"digest={digest}", output)
            self.assertIn(f"image_reference={image}", output)
            self.assertIn("build_number=232", output)
            self.assertIn("commit=e5e9b84fd9a2f0b90931cf503f786eb89e2b0f02", output)
            self.assertIn("deployment_id=dep-1", output)
            self.assertIn("Application__BuildNumber", summary)
            self.assertIn("WEBSITES_PORT", summary)
            az_log = (temporary / "az.log").read_text()
            self.assertNotIn("query '[].value'", az_log)
            self.assertNotIn("appsettings set", az_log)
            gh_log = (temporary / "gh.log").read_text()
            self.assertNotIn("actions/runs?environment=", gh_log)
            self.assertIn("deployments?environment=test", gh_log)

    def test_preflight_fails_if_the_fixture_is_already_present(self) -> None:
        with tempfile.TemporaryDirectory() as raw:
            temporary = Path(raw)
            digest = "sha256:" + ("b" * 64)
            image = f"example.azurecr.io/elsa-control/api@{digest}"
            self.write_fake_az(
                temporary,
                ["Application__BuildNumber", "CloudCompatibility__StagingFixture"],
                image,
            )
            self.write_fake_gh(temporary)
            self.write_fake_curl(temporary)
            env = self.environment(temporary)
            env["AZ_CALL_LOG"] = str(temporary / "az.log")
            env["GH_CALL_LOG"] = str(temporary / "gh.log")
            result = self.run_script(env, "preflight")
            self.assertNotEqual(0, result.returncode)
            self.assertIn("already present", result.stderr + result.stdout)

    def test_preflight_ignores_active_ci_when_no_test_deployment_maps_it(self) -> None:
        with tempfile.TemporaryDirectory() as raw:
            temporary = Path(raw)
            digest = "sha256:" + ("d" * 64)
            image = f"example.azurecr.io/elsa-control/api@{digest}"
            self.write_fake_az(temporary, ["Application__BuildNumber"], image)
            self.write_fake_gh(temporary)
            self.write_fake_curl(temporary)
            runs = temporary / "runs.json"
            runs.write_text(
                json.dumps(
                    {
                        "id": 77,
                        "name": "CI",
                        "path": ".github/workflows/ci.yml",
                        "status": "in_progress",
                    }
                )
                + "\n"
                + json.dumps(
                    {
                        "id": 78,
                        "name": "Azure Control API Deploy",
                        "path": ".github/workflows/azure-api-deploy.yml",
                        "status": "in_progress",
                    }
                )
                + "\n"
            )
            env = self.environment(temporary)
            env["AZ_CALL_LOG"] = str(temporary / "az.log")
            env["GH_CALL_LOG"] = str(temporary / "gh.log")
            env["GH_RUNS_JSON"] = str(runs)
            result = self.run_script(env, "preflight")
            self.assertEqual(0, result.returncode, result.stderr + result.stdout)
            gh_log = (temporary / "gh.log").read_text()
            self.assertNotIn("actions/runs?environment=", gh_log)

    def exclusive_failure_env(self, temporary: Path, image: str, **updates: str) -> dict[str, str]:
        self.write_fake_az(temporary, ["Application__BuildNumber"], image)
        self.write_fake_gh(temporary)
        self.write_fake_curl(temporary)
        env = self.environment(temporary)
        env["AZ_CALL_LOG"] = str(temporary / "az.log")
        env["GH_CALL_LOG"] = str(temporary / "gh.log")
        env["CURL_CALL_LOG"] = str(temporary / "curl.log")
        env.update(updates)
        return env

    def test_preflight_fails_when_deployments_list_fails(self) -> None:
        with tempfile.TemporaryDirectory() as raw:
            temporary = Path(raw)
            digest = "sha256:" + ("2" * 64)
            image = f"example.azurecr.io/elsa-control/api@{digest}"
            env = self.exclusive_failure_env(temporary, image, GH_FAIL_DEPLOYMENTS="1")
            self.assert_preflight_stops_at(env, "GitHub API call failed")

    def test_preflight_fails_when_deployment_status_fails(self) -> None:
        with tempfile.TemporaryDirectory() as raw:
            temporary = Path(raw)
            digest = "sha256:" + ("3" * 64)
            image = f"example.azurecr.io/elsa-control/api@{digest}"
            (temporary / "deployment-ids.txt").write_text("501\n")
            env = self.exclusive_failure_env(
                temporary,
                image,
                GH_DEPLOYMENT_IDS=str(temporary / "deployment-ids.txt"),
                GH_FAIL_STATUSES="1",
            )
            self.assert_preflight_stops_at(env, "GitHub API call failed")

    def test_preflight_fails_when_runs_list_fails(self) -> None:
        with tempfile.TemporaryDirectory() as raw:
            temporary = Path(raw)
            digest = "sha256:" + ("4" * 64)
            image = f"example.azurecr.io/elsa-control/api@{digest}"
            env = self.exclusive_failure_env(temporary, image, GH_FAIL_RUNS="1")
            self.assert_preflight_stops_at(env, "GitHub API call failed")

    def test_preflight_fails_when_run_payload_is_malformed(self) -> None:
        with tempfile.TemporaryDirectory() as raw:
            temporary = Path(raw)
            digest = "sha256:" + ("5" * 64)
            image = f"example.azurecr.io/elsa-control/api@{digest}"
            env = self.exclusive_failure_env(temporary, image, GH_MALFORMED_RUNS="1")
            self.assert_preflight_stops_at(env, "not valid JSON")

    def test_preflight_fails_when_a_test_deployment_is_unmapped(self) -> None:
        with tempfile.TemporaryDirectory() as raw:
            temporary = Path(raw)
            digest = "sha256:" + ("6" * 64)
            image = f"example.azurecr.io/elsa-control/api@{digest}"
            (temporary / "deployment-ids.txt").write_text("501\n")
            (temporary / "deployment-statuses.json").write_text(
                json.dumps(
                    [
                        {
                            "state": "in_progress",
                            "log_url": "https://example.test/no-run-id",
                        }
                    ]
                )
            )
            env = self.exclusive_failure_env(
                temporary,
                image,
                GH_DEPLOYMENT_IDS=str(temporary / "deployment-ids.txt"),
                GH_DEPLOYMENT_STATUSES=str(temporary / "deployment-statuses.json"),
            )
            self.assert_preflight_stops_at(env, "could not be mapped to a workflow run")

    def test_preflight_fails_when_a_test_deployment_maps_to_another_run(self) -> None:
        with tempfile.TemporaryDirectory() as raw:
            temporary = Path(raw)
            digest = "sha256:" + ("e" * 64)
            image = f"example.azurecr.io/elsa-control/api@{digest}"
            self.write_fake_az(temporary, ["Application__BuildNumber"], image)
            self.write_fake_gh(temporary)
            (temporary / "deployment-ids.txt").write_text("501\n")
            (temporary / "deployment-statuses.json").write_text(
                json.dumps(
                    [
                        {
                            "state": "in_progress",
                            "log_url": "https://github.com/valence-works/elsa-control/actions/runs/88",
                        }
                    ]
                )
            )
            env = self.environment(temporary)
            env["AZ_CALL_LOG"] = str(temporary / "az.log")
            env["GH_CALL_LOG"] = str(temporary / "gh.log")
            env["GH_DEPLOYMENT_IDS"] = str(temporary / "deployment-ids.txt")
            env["GH_DEPLOYMENT_STATUSES"] = str(temporary / "deployment-statuses.json")
            result = self.run_script(env, "preflight")
            self.assertNotEqual(0, result.returncode)
            combined = result.stderr + result.stdout
            self.assertIn("not exclusive", combined)
            self.assertNotIn("actions/runs?environment=", (temporary / "gh.log").read_text())

    def armed_health(self) -> tuple[str, str, str, str]:
        old = '{"status":"ok","buildNumber":"232","imageId":"e5e9b84fd9a2f0b90931cf503f786eb89e2b0f02","compatibilityFixture":null}'
        armed = '{"status":"ok","buildNumber":"232","imageId":"e5e9b84fd9a2f0b90931cf503f786eb89e2b0f02","compatibilityFixture":"missing-capability"}'
        return old, old, armed, armed

    def test_arm_requires_authenticated_compatibility_not_health_alone(self) -> None:
        with tempfile.TemporaryDirectory() as raw:
            temporary = Path(raw)
            digest = "sha256:" + ("f" * 64)
            image = f"example.azurecr.io/elsa-control/api@{digest}"
            present = temporary / "present"
            self.write_fake_az(temporary, ["Application__BuildNumber"], image)
            self.write_fake_curl(temporary)
            health = self.write_health_responses(temporary, *self.armed_health())
            env = self.environment(temporary)
            env["AZ_CALL_LOG"] = str(temporary / "az.log")
            env["AZ_SETTING_PRESENT"] = str(present)
            env["EXPECTED_DIGEST"] = digest
            env["EXPECTED_BUILD_NUMBER"] = "232"
            env["EXPECTED_COMMIT"] = "e5e9b84fd9a2f0b90931cf503f786eb89e2b0f02"
            env["HEALTH_RETRY_SECONDS"] = "0"
            env["HEALTH_RESPONSES"] = str(health)
            env["GITHUB_OUTPUT"] = str(temporary / "github.output")
            result = self.run_script(env, "arm")
            self.assertEqual(0, result.returncode, result.stderr + result.stdout)
            self.assertTrue(present.exists())
            az_log = (temporary / "az.log").read_text()
            self.assertIn("webapp restart", az_log)
            self.assertIn("armed_at=", (temporary / "github.output").read_text())
            combined = result.stderr + result.stdout
            self.assertIn("Authenticated /api/cloud/compatibility matched", combined)
            self.assertNotIn("missing-capability", combined)

    def test_arm_rejects_health_witness_without_authenticated_proof(self) -> None:
        with tempfile.TemporaryDirectory() as raw:
            temporary = Path(raw)
            digest = "sha256:" + ("7" * 64)
            image = f"example.azurecr.io/elsa-control/api@{digest}"
            present = temporary / "present"
            self.write_fake_az(temporary, ["Application__BuildNumber"], image)
            self.write_fake_curl(temporary)
            health = self.write_health_responses(temporary, *self.armed_health())
            env = self.environment(temporary)
            env["AZ_CALL_LOG"] = str(temporary / "az.log")
            env["AZ_SETTING_PRESENT"] = str(present)
            env["EXPECTED_DIGEST"] = digest
            env["HEALTH_RETRY_SECONDS"] = "0"
            env["HEALTH_RESPONSES"] = str(health)
            env["COMPAT_FAIL"] = "1"
            result = self.run_script(env, "arm")
            self.assertNotEqual(0, result.returncode)
            self.assertIn("authenticated compatibility proof did not return HTTP 200", result.stderr + result.stdout)

    def test_arm_rejects_two_healthy_responses_on_the_same_digest_without_the_fixture(self) -> None:
        with tempfile.TemporaryDirectory() as raw:
            temporary = Path(raw)
            digest = "sha256:" + ("1" * 64)
            image = f"example.azurecr.io/elsa-control/api@{digest}"
            present = temporary / "present"
            self.write_fake_az(temporary, ["Application__BuildNumber"], image)
            self.write_fake_curl(temporary)
            env = self.environment(temporary)
            env["AZ_CALL_LOG"] = str(temporary / "az.log")
            env["AZ_SETTING_PRESENT"] = str(present)
            env["EXPECTED_DIGEST"] = digest
            env["HEALTH_RETRY_SECONDS"] = "0"
            result = self.run_script(env, "arm")
            self.assertNotEqual(0, result.returncode)
            combined = result.stderr + result.stdout
            self.assertIn("did not recycle", combined)

    def test_restore_fails_when_the_settings_read_errors(self) -> None:
        with tempfile.TemporaryDirectory() as raw:
            temporary = Path(raw)
            digest = "sha256:" + ("8" * 64)
            image = f"example.azurecr.io/elsa-control/api@{digest}"
            present = temporary / "present"
            present.write_text("present\n")
            self.write_fake_az(temporary, ["Application__BuildNumber"], image)
            self.write_fake_curl(temporary)
            env = self.environment(temporary)
            env["AZ_CALL_LOG"] = str(temporary / "az.log")
            env["CURL_CALL_LOG"] = str(temporary / "curl.log")
            env["AZ_SETTING_PRESENT"] = str(present)
            env["AZ_FAIL_SETTING_LIST"] = "1"
            env["EXPECTED_DIGEST"] = digest
            result = self.run_script(env, "restore")
            self.assertNotEqual(0, result.returncode)
            combined = result.stderr + result.stdout
            self.assertIn("Could not read whether the compatibility fixture setting is present.", combined)
            self.assertNotIn("never written", combined)
            self.assertNotIn("Restored the baseline", combined)
            az_log = (temporary / "az.log").read_text()
            self.assertNotIn("appsettings delete", az_log)
            self.assertNotIn("webapp restart", az_log)
            curl_log = temporary / "curl.log"
            if curl_log.exists():
                self.assertNotIn("/health", curl_log.read_text())

    def test_restore_is_a_noop_when_the_fixture_was_never_written(self) -> None:
        with tempfile.TemporaryDirectory() as raw:
            temporary = Path(raw)
            digest = "sha256:" + ("9" * 64)
            image = f"example.azurecr.io/elsa-control/api@{digest}"
            self.write_fake_az(temporary, ["Application__BuildNumber", "WEBSITES_PORT"], image)
            self.write_fake_curl(temporary)
            env = self.environment(temporary)
            env["AZ_CALL_LOG"] = str(temporary / "az.log")
            env["EXPECTED_DIGEST"] = digest
            env["EXPECTED_BUILD_NUMBER"] = "232"
            env["EXPECTED_COMMIT"] = "e5e9b84fd9a2f0b90931cf503f786eb89e2b0f02"
            env["HEALTH_RETRY_SECONDS"] = "0"
            result = self.run_script(env, "restore")
            self.assertEqual(0, result.returncode, result.stderr + result.stdout)
            combined = result.stderr + result.stdout
            self.assertIn("never written", combined)
            self.assertIn("will not restart", combined)
            az_log = (temporary / "az.log").read_text()
            self.assertNotIn("webapp restart", az_log)
            self.assertNotIn("appsettings delete", az_log)
            self.assertNotIn("appsettings set", az_log)

    def test_restore_deletes_the_setting_and_requires_baseline_and_bff_smoke(self) -> None:
        with tempfile.TemporaryDirectory() as raw:
            temporary = Path(raw)
            digest = "sha256:" + ("c" * 64)
            image = f"example.azurecr.io/elsa-control/api@{digest}"
            names = temporary / "names.txt"
            names.write_text("Application__BuildNumber\nWEBSITES_PORT\n")
            present = temporary / "present"
            present.write_text("present\n")
            self.write_fake_az(temporary, ["Application__BuildNumber", "WEBSITES_PORT"], image)
            self.write_fake_curl(temporary)
            armed = '{"status":"ok","buildNumber":"232","imageId":"e5e9b84fd9a2f0b90931cf503f786eb89e2b0f02","compatibilityFixture":"missing-capability"}'
            restored = '{"status":"ok","buildNumber":"232","imageId":"e5e9b84fd9a2f0b90931cf503f786eb89e2b0f02","compatibilityFixture":null}'
            health = self.write_health_responses(temporary, armed, armed, restored, restored, restored)
            env = self.environment(temporary, COMPAT_MODE="baseline")
            env["AZ_CALL_LOG"] = str(temporary / "az.log")
            env["AZ_SETTING_PRESENT"] = str(present)
            env["EXPECTED_DIGEST"] = digest
            env["EXPECTED_BUILD_NUMBER"] = "232"
            env["EXPECTED_COMMIT"] = "e5e9b84fd9a2f0b90931cf503f786eb89e2b0f02"
            env["EXPECTED_DEPLOYMENT_ID"] = "dep-1"
            env["EXPECTED_SETTING_NAMES_PATH"] = str(names)
            env["HEALTH_RETRY_SECONDS"] = "0"
            env["HEALTH_RESPONSES"] = str(health)
            env["GITHUB_STEP_SUMMARY"] = str(temporary / "summary.md")
            result = self.run_script(env, "restore")
            self.assertEqual(0, result.returncode, result.stderr + result.stdout)
            self.assertFalse(present.exists())
            az_log = (temporary / "az.log").read_text()
            self.assertIn("appsettings delete", az_log)
            self.assertIn("webapp restart", az_log)
            self.assertIn("CloudCompatibility__StagingFixture", az_log)
            self.assertNotIn("=missing-capability", az_log)
            combined = result.stderr + result.stdout
            self.assertIn("BFF smoke reported compatible", combined)
            self.assertNotIn("missing-capability", combined)


if __name__ == "__main__":
    unittest.main()
