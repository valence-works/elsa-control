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
WORKFLOW = ROOT / ".github" / "workflows" / "staging-compat-fixture.yml"
SCRIPT = ROOT / "scripts" / "staging-compat-fixture.sh"
CI = ROOT / ".github" / "workflows" / "ci.yml"
DEPLOY = ROOT / ".github" / "workflows" / "azure-api-deploy.yml"


class StagingCompatExclusiveTests(unittest.TestCase):
    def test_freeze_requires_the_latest_on_marker(self) -> None:
        self.assertFalse(freeze_is_on([]))
        self.assertTrue(
            freeze_is_on([{"body": "staging-freeze: on\nExclusive window."}])
        )
        self.assertFalse(
            freeze_is_on(
                [
                    {"body": "staging-freeze: on"},
                    {"body": "staging-freeze: off"},
                ]
            )
        )
        self.assertTrue(
            freeze_is_on(
                [
                    {"body": "Please freeze staging"},
                    {"body": "staging-freeze: ON"},
                ]
            )
        )

    def test_open_issue_or_open_marker_is_an_open_qa_window(self) -> None:
        self.assertTrue(qa_window_is_open("open", []))
        self.assertFalse(qa_window_is_open("closed", []))
        self.assertTrue(
            qa_window_is_open("closed", [{"body": "qa-window: open"}])
        )
        self.assertFalse(
            qa_window_is_open(
                "closed",
                [{"body": "qa-window: open"}, {"body": "qa-window: closed"}],
            )
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
                        "environment": "production",
                    },
                    {
                        "id": "13",
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
                        "environment": "",
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
        self.assertEqual(1, self.source.count("timeout-minutes: 20"))
        self.assertIn("if: ${{ always() }}\n", self.source)
        self.assertIn("scripts/staging-compat-fixture.sh restore", self.source)
        self.assertIn("scripts/staging-compat-fixture.sh preflight", self.source)
        self.assertIn("scripts/staging-compat-fixture.sh arm", self.source)
        self.assertNotIn("secrets.", self.source)
        self.assertNotIn("production", self.source)

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
            "  echo '[{\"body\":\"staging-freeze: on\"}]'\n"
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
            "  exit 0\n"
            "fi\n"
            "exit 41\n"
        )
        fake.chmod(fake.stat().st_mode | stat.S_IEXEC)
        return fake

    def write_fake_curl(self, temporary: Path) -> Path:
        fake = temporary / "curl"
        fake.write_text(
            "#!/usr/bin/env bash\n"
            "set -euo pipefail\n"
            "output=\"\"\n"
            "while [ \"$#\" -gt 0 ]; do\n"
            "  case \"$1\" in\n"
            "    --output) output=\"$2\"; shift 2 ;;\n"
            "    --write-out) shift 2 ;;\n"
            "    --silent|--show-error) shift ;;\n"
            "    --max-time) shift 2 ;;\n"
            "    *) shift ;;\n"
            "  esac\n"
            "done\n"
            "printf '%s\\n' '{\"status\":\"ok\",\"buildNumber\":\"232\",\"imageId\":\"e5e9b84fd9a2f0b90931cf503f786eb89e2b0f02\"}' > \"$output\"\n"
            "printf '200'\n"
        )
        fake.chmod(fake.stat().st_mode | stat.S_IEXEC)
        return fake

    def run_script(self, env: dict[str, str], *args: str) -> subprocess.CompletedProcess[str]:
        return subprocess.run(
            [str(SCRIPT), *args],
            cwd=ROOT,
            env=env,
            capture_output=True,
            text=True,
            check=False,
        )

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

    def test_preflight_records_names_digest_and_revision_only(self) -> None:
        with tempfile.TemporaryDirectory() as raw:
            temporary = Path(raw)
            digest = "sha256:" + ("a" * 64)
            image = f"example.azurecr.io/elsa-control/api@{digest}"
            self.write_fake_az(temporary, ["Application__BuildNumber", "WEBSITES_PORT"], image)
            self.write_fake_gh(temporary)
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
            self.assertIn(f"revision={image}", output)
            self.assertIn("Application__BuildNumber", summary)
            self.assertIn("WEBSITES_PORT", summary)
            self.assertNotIn("232", summary)
            az_log = (temporary / "az.log").read_text()
            self.assertNotIn("query '[].value'", az_log)
            self.assertNotIn("appsettings set", az_log)

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
            env = self.environment(temporary)
            env["AZ_CALL_LOG"] = str(temporary / "az.log")
            env["GH_CALL_LOG"] = str(temporary / "gh.log")
            result = self.run_script(env, "preflight")
            self.assertNotEqual(0, result.returncode)
            self.assertIn("already present", result.stderr + result.stdout)

    def test_restore_removes_the_setting_and_checks_the_baseline(self) -> None:
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
            env = self.environment(temporary)
            env["AZ_CALL_LOG"] = str(temporary / "az.log")
            env["AZ_SETTING_PRESENT"] = str(present)
            env["EXPECTED_DIGEST"] = digest
            env["EXPECTED_SETTING_NAMES_PATH"] = str(names)
            env["HEALTH_RETRY_SECONDS"] = "0"
            env["GITHUB_STEP_SUMMARY"] = str(temporary / "summary.md")
            result = self.run_script(env, "restore")
            self.assertEqual(0, result.returncode, result.stderr + result.stdout)
            self.assertFalse(present.exists())
            az_log = (temporary / "az.log").read_text()
            self.assertIn("appsettings delete", az_log)
            self.assertIn("CloudCompatibility__StagingFixture", az_log)
            self.assertNotIn("=missing-capability", az_log)


if __name__ == "__main__":
    unittest.main()
