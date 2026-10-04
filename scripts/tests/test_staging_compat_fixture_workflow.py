#!/usr/bin/env python3
"""Offline contracts for the staging compatibility fixture workflow."""

from __future__ import annotations

import base64
import importlib.util
import json
import os
import re
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
RESTORE_HEALTH_ATTEMPTS = exclusive.RESTORE_HEALTH_ATTEMPTS
RESTORE_HEALTH_BUDGET_SECONDS = exclusive.RESTORE_HEALTH_BUDGET_SECONDS
RESTORE_STEP_TIMEOUT_SECONDS = exclusive.RESTORE_STEP_TIMEOUT_SECONDS
IN_HOLD_BUDGET_SECONDS = exclusive.IN_HOLD_BUDGET_SECONDS
IN_HOLD_K_PROBES_TIMEOUT_SECONDS = exclusive.IN_HOLD_K_PROBES_TIMEOUT_SECONDS
IN_HOLD_SCREENS_TIMEOUT_SECONDS = exclusive.IN_HOLD_SCREENS_TIMEOUT_SECONDS
POST_RESTORE_SCREENS_TIMEOUT_SECONDS = exclusive.POST_RESTORE_SCREENS_TIMEOUT_SECONDS
TELEMETRY_STEP_TIMEOUT_SECONDS = exclusive.TELEMETRY_STEP_TIMEOUT_SECONDS
MAX_HOLD_SECONDS = exclusive.MAX_HOLD_SECONDS
JOB_BACKSTOP_SECONDS = exclusive.JOB_BACKSTOP_SECONDS
WORKFLOW = ROOT / ".github" / "workflows" / "staging-compat-fixture.yml"
SCRIPT = ROOT / "scripts" / "staging-compat-fixture.sh"
SCREENS = ROOT / "scripts" / "staging-compat-fixture-screens.mjs"
CI = ROOT / ".github" / "workflows" / "ci.yml"
DEPLOY = ROOT / ".github" / "workflows" / "azure-api-deploy.yml"

STAGING_SUPABASE_REF = "abcdefghij0123456789"
PRODUCTION_SUPABASE_REF = "jhrcnclyydzngnyvhdht"
STAGING_SUPABASE_ORIGIN = f"https://{STAGING_SUPABASE_REF}.supabase.co"
STAGING_BFF_URL = f"{STAGING_SUPABASE_ORIGIN}/functions/v1/control-bff"
STAGING_ISSUER = f"{STAGING_SUPABASE_ORIGIN}/auth/v1"
DEPLOY_STAGING_DEPLOYMENT_ID = "6839060690"
DEPLOY_STAGING_RUN_ID = "37191070750"
HEALTH_COMMIT = "e5e9b84fd9a2f0b90931cf503f786eb89e2b0f02"
ALLOWED_AZ_COMMANDS = {
    ("webapp", "config", "appsettings", "list"),
    ("webapp", "config", "appsettings", "set"),
    ("webapp", "config", "appsettings", "delete"),
    ("webapp", "config", "show"),
    ("webapp", "sitecontainers", "show"),
    ("webapp", "restart"),
    ("webapp", "show"),
    ("acr", "manifest", "show-metadata"),
}
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


def mint_jwt(
    *,
    iss: str = STAGING_ISSUER,
    aud: str = "authenticated",
    role: str = "authenticated",
) -> str:
    def encode(payload: dict[str, object]) -> str:
        raw = json.dumps(payload, separators=(",", ":")).encode()
        return base64.urlsafe_b64encode(raw).decode().rstrip("=")

    return f"{encode({'alg': 'none', 'typ': 'JWT'})}.{encode({'iss': iss, 'aud': aud, 'role': role, 'sub': 'user'})}.sig"


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
        self.assertEqual(60 * 60, JOB_BACKSTOP_SECONDS)
        self.assertEqual(MAX_HOLD_SECONDS, compute_hold_seconds(0))
        self.assertEqual(660, compute_hold_seconds(300))
        self.assertEqual(0, compute_hold_seconds(1000))
        self.assertEqual(
            0,
            compute_hold_seconds(FIXTURE_CAP_SECONDS - RESTORE_BUDGET_SECONDS + 1),
        )
        self.assertGreaterEqual(compute_hold_seconds(0), IN_HOLD_BUDGET_SECONDS)
        self.assertGreaterEqual(compute_hold_seconds(180), IN_HOLD_BUDGET_SECONDS)
        self.assertGreaterEqual(RESTORE_HEALTH_ATTEMPTS, 36)
        self.assertGreaterEqual(RESTORE_HEALTH_BUDGET_SECONDS, 10 * 60)
        self.assertGreaterEqual(RESTORE_STEP_TIMEOUT_SECONDS, RESTORE_HEALTH_BUDGET_SECONDS)
        self.assertGreaterEqual(JOB_BACKSTOP_SECONDS, FIXTURE_CAP_SECONDS + RESTORE_STEP_TIMEOUT_SECONDS)

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
        cls.screens = SCREENS.read_text()
        cls.ci = CI.read_text()
        cls.deploy = DEPLOY.read_text()

    def step_order(self) -> list[str]:
        return re.findall(r"^\s+- name: (.+)$", self.source, re.M)

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
        self.assertIn("timeout-minutes: 60\n", self.source)
        self.assertIn("timeout-minutes: 12\n", self.source)
        self.assertIn("timeout-minutes: 16\n", self.source)
        self.assertNotIn("timeout-minutes: 30\n", self.source)
        self.assertNotIn("timeout-minutes: 20\n", self.source)
        self.assertIn("if: ${{ always() && steps.preflight.outcome == 'success' }}\n", self.source)
        self.assertNotIn("if: ${{ always() }}\n", self.source)
        self.assertIn('HEALTH_BUDGET_SECONDS: "300"', self.source)
        self.assertIn('HEALTH_BUDGET_SECONDS: "600"', self.source)
        self.assertIn('HEALTH_ATTEMPTS: "36"', self.source)
        self.assertIn('HEALTH_RETRY_SECONDS: "15"', self.source)
        self.assertIn("HEALTH_CURL_MAX_TIME", self.source)
        self.assertIn("fail() {\n  echo \"::error::$1\" >&2\n", self.script)
        self.assertIn("exit 1\n}", self.script)
        self.assertIn("kill -s TERM \"$$\"", self.script)
        self.assertIn("read_setting_presence", self.script)
        self.assertNotIn('if [ "$(read_setting_presence)"', self.script)
        self.assertIn("scripts/staging-compat-fixture.sh restore", self.source)
        self.assertIn("scripts/staging-compat-fixture.sh preflight", self.source)
        self.assertIn("scripts/staging-compat-fixture.sh arm", self.source)
        self.assertIn("scripts/staging-compat-fixture.sh probes", self.source)
        self.assertIn("scripts/staging-compat-fixture.sh telemetry", self.source)
        self.assertIn("scripts/staging-compat-fixture-screens.mjs", self.source)
        self.assertIn("az webapp restart", self.script)
        self.assertIn("appsettings delete", self.script)
        self.assertNotIn("production", self.source)
        self.assertIn("deployments: read\n", self.source)
        self.assertNotIn("CLOUD_COMPATIBILITY_TOKEN", self.source)
        self.assertNotIn("CLOUD_COMPATIBILITY_TOKEN", self.script)
        self.assertIn("secrets.STAGING_E2E_COMPAT_EMAIL", self.source)
        self.assertIn("secrets.STAGING_E2E_COMPAT_PASSWORD", self.source)
        self.assertIn("secrets.VITE_SUPABASE_PUBLISHABLE_KEY", self.source)
        self.assertIn("vars.STAGING_SUPABASE_PROJECT_REF", self.source)
        self.assertIn("vars.EXPECTED_STAGING_SUPABASE_ORIGIN", self.source)
        self.assertIn("vars.CLOUD_BFF_SMOKE_URL", self.source)
        self.assertNotIn("scripts/staging-compat-fixture.sh mint", self.source)
        self.assertGreaterEqual(self.source.count("GH_TOKEN: ${{ github.token }}"), 2)
        self.assertEqual(16, self.source.count("secrets."))
        job_env = self.source.split("steps:", 1)[0]
        self.assertNotIn("secrets.STAGING_E2E_COMPAT_EMAIL", job_env)
        self.assertNotIn("secrets.STAGING_E2E_COMPAT_PASSWORD", job_env)
        self.assertNotIn("secrets.VITE_SUPABASE_PUBLISHABLE_KEY", job_env)

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
        self.assertNotIn("query '[].value'", self.script)
        self.assertIn("CloudCompatibility__StagingFixture", self.script)
        self.assertIn('SETTING_NAME}=${FIXTURE_MODE}', self.script)
        self.assertNotIn("actions/runs?environment=", self.script)
        self.assertIn("deployments?environment=test", self.script)
        self.assertIn("compatibilityFixture", self.script)
        self.assertIn("inherit_errexit", self.script)
        self.assertIn("prove_authenticated_compatibility", self.script)
        self.assertIn("prove_bff_smoke_compatible", self.script)
        self.assertIn('{"action":"compatibility"}', self.script)
        self.assertIn(".data.state == \"compatible\"", self.script)
        self.assertIn("control_update_in_progress", self.script)
        self.assertIn("Elsa Cloud is being updated. Managed engine actions are temporarily paused.", self.script)
        self.assertIn("no-store", self.script)
        self.assertIn("listOrganizations", self.script)
        self.assertIn("updateInstance", self.script)
        self.assertIn("createInstanceDeleteConfirmation", self.script)
        self.assertIn("jq -S -c '.'", self.script)
        self.assertIn("latest_deploy_staging_deployment_id", self.script)
        self.assertNotIn("webapp deployment list", self.script)
        self.assertIn("webapp sitecontainers show", self.script)
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

    def test_in_hold_checks_run_before_restore_and_cannot_skip_it(self) -> None:
        names = self.step_order()
        self.assertLess(names.index("Record K action probes while armed"), names.index("Restore the baseline"))
        self.assertLess(names.index("Capture armed customer screens"), names.index("Restore the baseline"))
        self.assertLess(names.index("Hold the remaining exclusive proof window"), names.index("Restore the baseline"))
        restore_block = self.source.split("name: Restore the baseline", 1)[1].split("- name:", 1)[0]
        self.assertIn("if: ${{ always() && steps.preflight.outcome == 'success' }}", restore_block)
        self.assertNotIn("steps.k_probes", restore_block)
        self.assertNotIn("steps.armed_screens", restore_block)
        self.assertIn("timeout-minutes: 16", restore_block)
        self.assertIn('HEALTH_ATTEMPTS: "36"', restore_block)
        self.assertIn('HEALTH_BUDGET_SECONDS: "600"', restore_block)

    def test_in_hold_failures_do_not_use_always_and_fit_the_hold(self) -> None:
        k_block = self.source.split("name: Record K action probes while armed", 1)[1].split("- name:", 1)[0]
        screens_block = self.source.split("name: Capture armed customer screens", 1)[1].split("- name:", 1)[0]
        hold_block = self.source.split("name: Hold the remaining exclusive proof window", 1)[1].split("- name:", 1)[0]
        self.assertNotIn("always()", k_block)
        self.assertNotIn("always()", screens_block)
        self.assertIn("if: ${{ success() }}", hold_block)
        self.assertIn("timeout-minutes: 2", k_block)
        self.assertIn("timeout-minutes: 5", screens_block)
        self.assertEqual(IN_HOLD_K_PROBES_TIMEOUT_SECONDS, 2 * 60)
        self.assertEqual(IN_HOLD_SCREENS_TIMEOUT_SECONDS, 5 * 60)
        self.assertLessEqual(IN_HOLD_BUDGET_SECONDS, compute_hold_seconds(0))
        self.assertGreaterEqual(16 * 60, MAX_HOLD_SECONDS)

    def test_cancel_keeps_restore_on_always_preflight_success(self) -> None:
        restore_block = self.source.split("name: Restore the baseline", 1)[1].split("- name:", 1)[0]
        hold_block = self.source.split("name: Hold the remaining exclusive proof window", 1)[1].split("- name:", 1)[0]
        k_block = self.source.split("name: Record K action probes while armed", 1)[1].split("- name:", 1)[0]
        self.assertIn("always() && steps.preflight.outcome == 'success'", restore_block)
        self.assertIn("success()", hold_block)
        self.assertNotIn("cancelled()", restore_block)
        self.assertNotIn("always()", k_block)

    def test_post_restore_evidence_runs_only_after_successful_restore(self) -> None:
        restored = self.source.split("name: Capture restored customer screens", 1)[1].split("- name:", 1)[0]
        telemetry = self.source.split("name: Report armed-window telemetry", 1)[1].split("- name:", 1)[0]
        skipped = self.source.split("name: Report skipped post-restore evidence", 1)[1]
        self.assertIn("steps.restore.outcome == 'success'", restored)
        self.assertIn("steps.restore.outcome == 'success'", telemetry)
        self.assertIn("steps.restore.outcome != 'success'", skipped)
        self.assertIn("timeout-minutes: 5", restored)
        self.assertIn("timeout-minutes: 3", telemetry)
        self.assertEqual(POST_RESTORE_SCREENS_TIMEOUT_SECONDS, 5 * 60)
        self.assertEqual(TELEMETRY_STEP_TIMEOUT_SECONDS, 3 * 60)
        names = self.step_order()
        self.assertGreater(names.index("Capture restored customer screens"), names.index("Restore the baseline"))
        self.assertGreater(names.index("Report armed-window telemetry"), names.index("Restore the baseline"))

    def test_restore_timeouts_exceed_the_health_budget(self) -> None:
        restore_block = self.source.split("name: Restore the baseline", 1)[1].split("- name:", 1)[0]
        self.assertIn("timeout-minutes: 60\n", self.source)
        self.assertGreaterEqual(JOB_BACKSTOP_SECONDS, RESTORE_STEP_TIMEOUT_SECONDS + FIXTURE_CAP_SECONDS)
        self.assertGreaterEqual(RESTORE_STEP_TIMEOUT_SECONDS, RESTORE_HEALTH_BUDGET_SECONDS)
        self.assertGreaterEqual(int(re.search(r'HEALTH_ATTEMPTS: "(\d+)"', restore_block).group(1)), 36)
        self.assertGreaterEqual(int(re.search(r'HEALTH_BUDGET_SECONDS: "(\d+)"', restore_block).group(1)), 600)
        self.assertIn('HEALTH_ATTEMPTS: "36"', restore_block)

    def test_playwright_capture_masks_identity_and_uploads_pngs_only(self) -> None:
        self.assertIn("mask:", self.screens)
        self.assertIn("STAGING_E2E_COMPAT_EMAIL", self.screens)
        self.assertIn("Service update in progress.", self.screens)
        self.assertIn("Managed engine actions are temporarily paused", self.screens)
        self.assertIn("calm-sand-03964eb03.2.azurestaticapps.net", self.screens)
        self.assertNotIn("storageState", self.screens)
        self.assertNotIn("recordHar", self.screens)
        self.assertNotIn(".har", self.screens)
        self.assertNotIn("recordVideo", self.screens)
        self.assertNotRegex(self.screens, r"trace:\s")
        self.assertNotIn("video:", self.source)
        self.assertNotIn("trace:", self.source)
        self.assertNotIn(".har", self.source)
        self.assertIn("staging-compat-screens-armed", self.source)
        self.assertIn("staging-compat-screens-restored", self.source)
        self.assertIn("*.png", self.source)
        self.assertNotIn("*.zip", self.source)
        self.assertIn("Never writes Playwright traces, HAR captures, videos, or browser storage files.", self.screens)

    def test_telemetry_step_does_not_query_or_grant_roles(self) -> None:
        telemetry = self.source.split("name: Report armed-window telemetry", 1)[1]
        self.assertIn("scripts/staging-compat-fixture.sh telemetry", telemetry)
        self.assertNotIn("az monitor", self.script)
        self.assertNotIn("role assignment", self.script)
        self.assertNotIn("az role", self.script)
        self.assertIn("Inconclusive", self.script)
        self.assertIn("do not reach a queryable App Insights", self.script)


class StagingCompatScriptTests(unittest.TestCase):
    def environment(self, temporary: Path, **updates: str) -> dict[str, str]:
        token = updates.pop("MINTED_ACCESS_TOKEN", mint_jwt())
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
                "STAGING_E2E_COMPAT_EMAIL": "compat@example.test",
                "STAGING_E2E_COMPAT_PASSWORD": "unused-password",
                "VITE_SUPABASE_PUBLISHABLE_KEY": "sb_publishable_test",
                "STAGING_SUPABASE_PROJECT_REF": STAGING_SUPABASE_REF,
                "EXPECTED_STAGING_SUPABASE_ORIGIN": STAGING_SUPABASE_ORIGIN,
                "CLOUD_BFF_SMOKE_URL": STAGING_BFF_URL,
                "MINTED_ACCESS_TOKEN": token,
                "SETTING_READ_ATTEMPTS": "3",
                "SETTING_READ_RETRY_SECONDS": "0",
                "RESTORE_ABSENT_RECHECK_SECONDS": "0",
            }
        )
        env.update(updates)
        return env

    def tagged_image(self, digest: str) -> str:
        return f"example.azurecr.io/elsa-control/api:{HEALTH_COMMIT}"

    def write_fake_az(self, temporary: Path, setting_names: list[str], image: str, digest: str | None = None) -> Path:
        names_file = temporary / "setting-names.txt"
        names_file.write_text("".join(f"{name}\n" for name in setting_names))
        if digest is None:
            digest = image.split("@", 1)[1] if "@sha256:" in image else "sha256:" + ("a" * 64)
        fake = temporary / "az"
        fake.write_text(
            "#!/usr/bin/env bash\n"
            "set -euo pipefail\n"
            "printf '%s\\n' \"$*\" >> \"${AZ_CALL_LOG:?}\"\n"
            "if [[ \"$*\" == *\"webapp deployment\"* ]]; then\n"
            "  echo 'az webapp deployment is not a real command used by this host' >&2\n"
            "  exit 41\n"
            "fi\n"
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
            "  payload='{\"linuxFxVersion\":\"SITECONTAINERS\",\"kind\":\"app\"}'\n"
            "  python3 -c 'import json,sys; args=sys.argv[1:]; q=\"\";\n"
            "for i,a in enumerate(args):\n"
            "    if a==\"--query\" and i+1<len(args): q=args[i+1].strip().strip(chr(39))\n"
            "data=json.loads(sys.argv[-1]); print(data[q] if q else json.dumps(data))' \"$@\" \"$payload\"\n"
            "  exit 0\n"
            "fi\n"
            "if [[ \"$*\" == *\"webapp sitecontainers show\"* ]]; then\n"
            f"  payload={json.dumps(json.dumps({'name': 'main', 'image': image}))}\n"
            "  python3 -c 'import json,sys; args=sys.argv[1:]; q=\"\";\n"
            "for i,a in enumerate(args):\n"
            "    if a==\"--query\" and i+1<len(args): q=args[i+1].strip().strip(chr(39))\n"
            "data=json.loads(sys.argv[-1]); print(data[q] if q else json.dumps(data))' \"$@\" \"$payload\"\n"
            "  exit 0\n"
            "fi\n"
            "if [[ \"$*\" == *\"acr manifest show-metadata\"* ]]; then\n"
            f"  payload={json.dumps(json.dumps({'digest': digest, 'name': f'elsa-control/api:{HEALTH_COMMIT}'}))}\n"
            "  python3 -c 'import json,sys; args=sys.argv[1:]; q=\"\";\n"
            "for i,a in enumerate(args):\n"
            "    if a==\"--query\" and i+1<len(args): q=args[i+1].strip().strip(chr(39))\n"
            "data=json.loads(sys.argv[-1]); print(data[q] if q else json.dumps(data))' \"$@\" \"$payload\"\n"
            "  exit 0\n"
            "fi\n"
            "if [[ \"$*\" == *\"webapp show\"* ]]; then\n"
            "  payload='{\"defaultHostName\":\"staging.example.test\",\"state\":\"Running\"}'\n"
            "  python3 -c 'import json,sys; args=sys.argv[1:]; q=\"\";\n"
            "for i,a in enumerate(args):\n"
            "    if a==\"--query\" and i+1<len(args): q=args[i+1].strip().strip(chr(39))\n"
            "data=json.loads(sys.argv[-1]); print(data[q] if q else json.dumps(data))' \"$@\" \"$payload\"\n"
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
            "if [[ \"$*\" == *'/actions/workflows/azure-api-deploy.yml/runs'* ]]; then\n"
            "  if [ -n \"${GH_DEPLOY_STAGING_RUNS:-}\" ] && [ -f \"${GH_DEPLOY_STAGING_RUNS}\" ]; then\n"
            "    cat \"${GH_DEPLOY_STAGING_RUNS}\"\n"
            "  fi\n"
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
            "if [[ \"$*\" == *'/actions/runs/'* ]]; then\n"
            f"  if [[ \"$*\" == *'/actions/runs/{DEPLOY_STAGING_RUN_ID}'* ]] || [[ \"$*\" == *'/actions/runs/777'* ]]; then\n"
            "    payload='{\"path\":\".github/workflows/azure-api-deploy.yml\",\"created_at\":\"2026-10-03T00:00:00Z\"}'\n"
            "  elif [[ \"$*\" == *'/actions/runs/99'* ]]; then\n"
            "    payload='{\"path\":\".github/workflows/staging-compat-fixture.yml\",\"created_at\":\"2026-10-04T09:00:00Z\"}'\n"
            "  elif [ -n \"${GH_RUN_PATH:-}\" ]; then\n"
            "    payload=$(jq -cn --arg path \"$GH_RUN_PATH\" '{path:$path,created_at:\"2026-10-03T00:00:00Z\"}')\n"
            "  else\n"
            "    payload='{\"path\":\".github/workflows/ci.yml\",\"created_at\":\"2026-10-03T00:00:00Z\"}'\n"
            "  fi\n"
            "  jq_expr=\"\"\n"
            "  for arg in \"$@\"; do\n"
            "    if [ \"${prev:-}\" = --jq ]; then jq_expr=\"$arg\"; fi\n"
            "    prev=\"$arg\"\n"
            "  done\n"
            "  if [ -n \"$jq_expr\" ]; then jq -r \"$jq_expr\" <<<\"$payload\"; else printf '%s\\n' \"$payload\"; fi\n"
            "  exit 0\n"
            "fi\n"
            "if [[ \"$*\" == *'/deployments/'* && \"$*\" != *statuses* && \"$*\" != *'/deployments?'* ]]; then\n"
            "  payload='{\"created_at\":\"${GH_DEPLOYMENT_CREATED_AT:-2026-10-03T00:00:00Z}\"}'\n"
            "  if [[ \"$*\" == *'/deployments/888'* ]]; then payload='{\"created_at\":\"2026-10-04T01:00:00Z\"}'; fi\n"
            "  jq_expr=\"\"\n"
            "  for arg in \"$@\"; do\n"
            "    if [ \"${prev:-}\" = --jq ]; then jq_expr=\"$arg\"; fi\n"
            "    prev=\"$arg\"\n"
            "  done\n"
            "  if [ -n \"$jq_expr\" ]; then jq -r \"$jq_expr\" <<<\"$payload\"; else printf '%s\\n' \"$payload\"; fi\n"
            "  exit 0\n"
            "fi\n"
            "if [[ \"$*\" == *'/deployments/'*'/statuses'* ]]; then\n"
            "  if [ \"${GH_FAIL_STATUSES:-}\" = 1 ]; then echo failed-status >&2; exit 2; fi\n"
            "  if [ -n \"${GH_DEPLOYMENT_STATUSES:-}\" ] && [ -f \"${GH_DEPLOYMENT_STATUSES}\" ]; then\n"
            "    cat \"${GH_DEPLOYMENT_STATUSES}\"\n"
            "    exit 0\n"
            "  fi\n"
            f"  if [[ \"$*\" == *'/deployments/{DEPLOY_STAGING_DEPLOYMENT_ID}/'* ]]; then\n"
            f"    echo '[{{ \"state\":\"success\",\"log_url\":\"https://github.com/valence-works/elsa-control/actions/runs/{DEPLOY_STAGING_RUN_ID}\" }}]'\n"
            "    exit 0\n"
            "  fi\n"
            "  if [[ \"$*\" == *'/deployments/99/'* ]]; then\n"
            "    echo '[{\"state\":\"in_progress\",\"log_url\":\"https://github.com/valence-works/elsa-control/actions/runs/99\"}]'\n"
            "    exit 0\n"
            "  fi\n"
            "  if [[ \"$*\" == *'/deployments/888/'* ]]; then\n"
            "    echo '[{\"state\":\"in_progress\",\"log_url\":\"https://github.com/valence-works/elsa-control/actions/runs/777\"}]'\n"
            "    exit 0\n"
            "  fi\n"
            "  echo '[]'\n"
            "  exit 0\n"
            "fi\n"
            "if [[ \"$*\" == *'/deployments?'* ]]; then\n"
            "  if [ \"${GH_FAIL_DEPLOYMENTS:-}\" = 1 ]; then echo failed-deployments >&2; exit 2; fi\n"
            "  if [ \"${GH_MALFORMED_DEPLOYMENTS:-}\" = 1 ]; then echo '{'; exit 0; fi\n"
            "  if [ -n \"${GH_DEPLOYMENT_JSON:-}\" ] && [ -f \"${GH_DEPLOYMENT_JSON}\" ]; then\n"
            "    payload=$(cat \"${GH_DEPLOYMENT_JSON}\")\n"
            "  elif [ -n \"${GH_DEPLOYMENT_IDS:-}\" ] && [ -f \"${GH_DEPLOYMENT_IDS}\" ]; then\n"
            "    payload=$(python3 -c 'import json,sys; rows=[]\n"
            "for raw in open(sys.argv[1]):\n"
            "    item=raw.strip()\n"
            "    if not item: continue\n"
            "    created=\"2026-10-04T01:00:00Z\" if item==\"888\" else \"2026-10-03T00:00:00Z\"\n"
            "    rows.append({\"id\":int(item),\"created_at\":created,\"environment\":\"test\"})\n"
            "rows.sort(key=lambda row: row[\"created_at\"], reverse=True)\n"
            "print(json.dumps(rows))' \"${GH_DEPLOYMENT_IDS}\")\n"
            "  else\n"
            f"    payload='[{{\"id\":99,\"created_at\":\"2026-10-04T09:00:00Z\",\"environment\":\"test\"}},{{\"id\":{DEPLOY_STAGING_DEPLOYMENT_ID},\"created_at\":\"2026-10-03T00:00:00Z\",\"environment\":\"test\"}},{{\"id\":9001,\"created_at\":\"2026-10-04T08:00:00Z\",\"environment\":\"production\"}}]'\n"
            "  fi\n"
            "  if [[ \"$*\" == *'environment=test'* ]]; then\n"
            "    payload=$(printf '%s' \"$payload\" | jq '[.[] | select(.environment==\"test\")]')\n"
            "  fi\n"
            "  jq_expr=\"\"\n"
            "  prev=\"\"\n"
            "  for arg in \"$@\"; do\n"
            "    if [ \"$prev\" = --jq ]; then jq_expr=\"$arg\"; fi\n"
            "    prev=\"$arg\"\n"
            "  done\n"
            "  if [ -n \"$jq_expr\" ]; then jq -r \"$jq_expr\" <<<\"$payload\"; else printf '%s\\n' \"$payload\"; fi\n"
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
        bff_ok = json.dumps(
            {
                "data": {
                    "state": "compatible",
                    "contractVersion": 1,
                    "capabilities": BASELINE_CAPABILITIES[:8],
                }
            }
        )
        bff_bootstrap = json.dumps(
            {
                "data": {
                    "organizationId": "11111111-1111-4111-8111-111111111111",
                    "workspaceId": "22222222-2222-4222-8222-222222222222",
                }
            }
        )
        bff_armed = json.dumps(
            {
                "code": "control_update_in_progress",
                "error": "Elsa Cloud is being updated. Managed engine actions are temporarily paused.",
            }
        )
        health = json.dumps(
            {
                "status": "ok",
                "buildNumber": "232",
                "imageId": HEALTH_COMMIT,
                "compatibilityFixture": None,
            }
        )
        fake = temporary / "curl"
        fake.write_text(
            "#!/usr/bin/env bash\n"
            "set -euo pipefail\n"
            "printf '%s\\n' \"$*\" >> \"${CURL_CALL_LOG:-/dev/null}\"\n"
            "output=\"\"\n"
            "header_file=\"\"\n"
            "payload=\"\"\n"
            "url=\"\"\n"
            "method=GET\n"
            "while [ \"$#\" -gt 0 ]; do\n"
            "  case \"$1\" in\n"
            "    --output) output=\"$2\"; shift 2 ;;\n"
            "    --dump-header) header_file=\"$2\"; shift 2 ;;\n"
            "    --write-out) shift 2 ;;\n"
            "    --silent|--show-error) shift ;;\n"
            "    --request|-X) method=\"$2\"; shift 2 ;;\n"
            "    --max-time) shift 2 ;;\n"
            "    --header) shift 2 ;;\n"
            "    --data|--data-raw) payload=\"$2\"; shift 2 ;;\n"
            "    *) url=\"$1\"; shift ;;\n"
            "  esac\n"
            "done\n"
            "write_headers() {\n"
            "  local status=\"$1\"\n"
            "  if [ -n \"$header_file\" ] && [ \"$header_file\" != /dev/null ]; then\n"
            "    if [ \"${BFF_OMIT_NO_STORE:-}\" = 1 ]; then\n"
            "      printf 'HTTP/1.1 %s\\r\\nCache-Control: max-age=0\\r\\n\\r\\n' \"$status\" > \"$header_file\"\n"
            "    else\n"
            "      printf 'HTTP/1.1 %s\\r\\nCache-Control: no-store\\r\\n\\r\\n' \"$status\" > \"$header_file\"\n"
            "    fi\n"
            "  fi\n"
            "}\n"
            "if [[ \"$url\" == *'/auth/v1/token'* ]]; then\n"
            "  if [ \"$method\" != POST ]; then printf '405'; exit 0; fi\n"
            "  if [ \"${TOKEN_FAIL:-}\" = 1 ]; then printf '401'; exit 0; fi\n"
            "  printf '%s\\n' \"{\\\"access_token\\\":\\\"${MINTED_ACCESS_TOKEN:?}\\\"}\" > \"$output\"\n"
            "  printf '200'\n"
            "  exit 0\n"
            "fi\n"
            "if [[ \"$url\" == *'/api/cloud/compatibility'* ]]; then\n"
            f"  body={json.dumps(baseline)}\n"
            "  if [ \"${COMPAT_MODE:-}\" = armed ]; then\n"
            f"    body={json.dumps(compat)}\n"
            "  fi\n"
            "  if [ \"${COMPAT_FAIL:-}\" = 1 ]; then printf '401'; exit 0; fi\n"
            "  printf '%s\\n' \"$body\" > \"$output\"\n"
            "  printf '200'\n"
            "  exit 0\n"
            "fi\n"
            "if [ -n \"${CLOUD_BFF_SMOKE_URL:-}\" ] && [ \"$url\" = \"${CLOUD_BFF_SMOKE_URL}\" ]; then\n"
            "  if [ \"$method\" != POST ]; then write_headers 405; printf '405'; exit 0; fi\n"
            "  if [[ \"$payload\" == *'\"bootstrap\"'* ]]; then\n"
            f"    printf '%s\\n' {json.dumps(bff_bootstrap)} > \"$output\"\n"
            "    write_headers 200\n"
            "    printf '200'\n"
            "    exit 0\n"
            "  fi\n"
            "  if [ \"${BFF_ARMED:-}\" = 1 ]; then\n"
            f"    printf '%s\\n' {json.dumps(bff_armed)} > \"$output\"\n"
            "    write_headers 503\n"
            "    printf '503'\n"
            "    exit 0\n"
            "  fi\n"
            f"  printf '%s\\n' {json.dumps(bff_ok)} > \"$output\"\n"
            "  write_headers 200\n"
            "  printf '200'\n"
            "  exit 0\n"
            "fi\n"
            f"body={json.dumps(health)}\n"
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
            image = self.tagged_image(digest)
            self.write_fake_az(temporary, ["Application__BuildNumber", "WEBSITES_PORT"], image, digest)
            self.write_fake_gh(temporary)
            self.write_fake_curl(temporary)
            env = self.environment(temporary)
            env["AZ_CALL_LOG"] = str(temporary / "az.log")
            env["GH_CALL_LOG"] = str(temporary / "gh.log")
            env["CURL_CALL_LOG"] = str(temporary / "curl.log")
            env["GITHUB_OUTPUT"] = str(temporary / "github.output")
            env["GITHUB_STEP_SUMMARY"] = str(temporary / "summary.md")
            result = self.run_script(env, "preflight")
            self.assertEqual(0, result.returncode, result.stderr + result.stdout)
            output = (temporary / "github.output").read_text()
            summary = (temporary / "summary.md").read_text()
            self.assertIn(f"digest={digest}", output)
            self.assertIn(f"image_reference={image}", output)
            self.assertIn("build_number=232", output)
            self.assertIn(f"commit={HEALTH_COMMIT}", output)
            self.assertIn(f"deployment_id={DEPLOY_STAGING_DEPLOYMENT_ID}", output)
            self.assertIn("preflight_started_at=", output)
            self.assertNotIn("CLOUD_COMPATIBILITY_TOKEN", output)
            self.assertNotIn(env["MINTED_ACCESS_TOKEN"], output)
            self.assertIn("Application__BuildNumber", summary)
            self.assertIn("WEBSITES_PORT", summary)
            az_log = (temporary / "az.log").read_text()
            self.assertIn("webapp sitecontainers show", az_log)
            self.assertIn("acr manifest show-metadata", az_log)
            self.assertNotIn("webapp deployment", az_log)
            self.assertNotIn("query '[].value'", az_log)
            self.assertNotIn("appsettings set", az_log)
            gh_log = (temporary / "gh.log").read_text()
            self.assertNotIn("actions/runs?environment=", gh_log)
            self.assertIn("deployments?environment=test", gh_log)
            self.assertIn(f"actions/runs/{DEPLOY_STAGING_RUN_ID}", gh_log)
            curl_log = (temporary / "curl.log").read_text()
            self.assertIn("/auth/v1/token?grant_type=password", curl_log)
            self.assertIn("::add-mask::", result.stdout + result.stderr)

    def test_preflight_fails_if_the_fixture_is_already_present(self) -> None:
        with tempfile.TemporaryDirectory() as raw:
            temporary = Path(raw)
            digest = "sha256:" + ("b" * 64)
            image = self.tagged_image(digest)
            self.write_fake_az(
                temporary,
                ["Application__BuildNumber", "CloudCompatibility__StagingFixture"],
                image,
                digest,
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
            image = self.tagged_image(digest)
            self.write_fake_az(temporary, ["Application__BuildNumber"], image, digest)
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
        digest = image.split("@", 1)[1] if "@sha256:" in image else "sha256:" + ("e" * 64)
        self.write_fake_az(temporary, ["Application__BuildNumber"], image, digest)
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
            image = self.tagged_image(digest)
            env = self.exclusive_failure_env(temporary, image, GH_FAIL_DEPLOYMENTS="1")
            self.assert_preflight_stops_at(env, "GitHub API call failed")

    def test_preflight_fails_when_deployment_status_fails(self) -> None:
        with tempfile.TemporaryDirectory() as raw:
            temporary = Path(raw)
            digest = "sha256:" + ("3" * 64)
            image = self.tagged_image(digest)
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
            image = self.tagged_image(digest)
            env = self.exclusive_failure_env(temporary, image, GH_FAIL_RUNS="1")
            self.assert_preflight_stops_at(env, "GitHub API call failed")

    def test_preflight_fails_when_run_payload_is_malformed(self) -> None:
        with tempfile.TemporaryDirectory() as raw:
            temporary = Path(raw)
            digest = "sha256:" + ("5" * 64)
            image = self.tagged_image(digest)
            env = self.exclusive_failure_env(temporary, image, GH_MALFORMED_RUNS="1")
            self.assert_preflight_stops_at(env, "not valid JSON")

    def test_preflight_fails_when_a_test_deployment_is_unmapped(self) -> None:
        with tempfile.TemporaryDirectory() as raw:
            temporary = Path(raw)
            digest = "sha256:" + ("6" * 64)
            image = self.tagged_image(digest)
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
            image = self.tagged_image(digest)
            self.write_fake_az(temporary, ["Application__BuildNumber"], image, digest)
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
            image = self.tagged_image(digest)
            present = temporary / "present"
            self.write_fake_az(temporary, ["Application__BuildNumber"], image, digest)
            self.write_fake_curl(temporary)
            health = self.write_health_responses(temporary, *self.armed_health())
            env = self.environment(temporary)
            env["AZ_CALL_LOG"] = str(temporary / "az.log")
            env["AZ_SETTING_PRESENT"] = str(present)
            env["EXPECTED_DIGEST"] = digest
            env["EXPECTED_BUILD_NUMBER"] = "232"
            env["EXPECTED_COMMIT"] = HEALTH_COMMIT
            env["COMPAT_MODE"] = "armed"
            env["BFF_ARMED"] = "1"
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
            self.assertIn("control_update_in_progress", combined)
            self.assertIn("no-store", combined)
            self.assertIn("armed_at_iso=", (temporary / "github.output").read_text())
            self.assertIn("compat_context_path=", (temporary / "github.output").read_text())
            self.assertNotIn("11111111-1111-4111-8111-111111111111", (temporary / "github.output").read_text())
            self.assertNotIn("missing-capability", combined)

    def test_arm_rejects_health_witness_without_authenticated_proof(self) -> None:
        with tempfile.TemporaryDirectory() as raw:
            temporary = Path(raw)
            digest = "sha256:" + ("7" * 64)
            image = self.tagged_image(digest)
            present = temporary / "present"
            self.write_fake_az(temporary, ["Application__BuildNumber"], image, digest)
            self.write_fake_curl(temporary)
            health = self.write_health_responses(temporary, *self.armed_health())
            env = self.environment(temporary)
            env["AZ_CALL_LOG"] = str(temporary / "az.log")
            env["AZ_SETTING_PRESENT"] = str(present)
            env["EXPECTED_DIGEST"] = digest
            env["HEALTH_RETRY_SECONDS"] = "0"
            env["HEALTH_RESPONSES"] = str(health)
            env["COMPAT_MODE"] = "armed"
            env["COMPAT_FAIL"] = "1"
            result = self.run_script(env, "arm")
            self.assertNotEqual(0, result.returncode)
            self.assertIn("authenticated compatibility proof did not return HTTP 200", result.stderr + result.stdout)

    def test_arm_rejects_two_healthy_responses_on_the_same_digest_without_the_fixture(self) -> None:
        with tempfile.TemporaryDirectory() as raw:
            temporary = Path(raw)
            digest = "sha256:" + ("1" * 64)
            image = self.tagged_image(digest)
            present = temporary / "present"
            self.write_fake_az(temporary, ["Application__BuildNumber"], image, digest)
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
            image = self.tagged_image(digest)
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
            image = self.tagged_image(digest)
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
            image = self.tagged_image(digest)
            names = temporary / "names.txt"
            names.write_text("Application__BuildNumber\nWEBSITES_PORT\n")
            present = temporary / "present"
            present.write_text("present\n")
            self.write_fake_az(temporary, ["Application__BuildNumber", "WEBSITES_PORT"], image, digest)
            self.write_fake_gh(temporary)
            self.write_fake_curl(temporary)
            armed = '{"status":"ok","buildNumber":"232","imageId":"e5e9b84fd9a2f0b90931cf503f786eb89e2b0f02","compatibilityFixture":"missing-capability"}'
            restored = '{"status":"ok","buildNumber":"232","imageId":"e5e9b84fd9a2f0b90931cf503f786eb89e2b0f02","compatibilityFixture":null}'
            health = self.write_health_responses(temporary, armed, armed, restored, restored, restored)
            env = self.environment(temporary)
            env["AZ_CALL_LOG"] = str(temporary / "az.log")
            env["GH_CALL_LOG"] = str(temporary / "gh.log")
            env["CURL_CALL_LOG"] = str(temporary / "curl.log")
            env["AZ_SETTING_PRESENT"] = str(present)
            env["EXPECTED_DIGEST"] = digest
            env["EXPECTED_BUILD_NUMBER"] = "232"
            env["EXPECTED_COMMIT"] = HEALTH_COMMIT
            env["EXPECTED_DEPLOYMENT_ID"] = DEPLOY_STAGING_DEPLOYMENT_ID
            env["EXPECTED_IMAGE_REFERENCE"] = image
            env["EXPECTED_PREFLIGHT_STARTED_AT"] = "2026-10-04T00:00:00Z"
            env["EXPECTED_SETTING_NAMES_PATH"] = str(names)
            env["HEALTH_RETRY_SECONDS"] = "0"
            env["HEALTH_RESPONSES"] = str(health)
            env["GITHUB_STEP_SUMMARY"] = str(temporary / "summary.md")
            env["GITHUB_OUTPUT"] = str(temporary / "github.output")
            result = self.run_script(env, "restore")
            self.assertEqual(0, result.returncode, result.stderr + result.stdout)
            self.assertFalse(present.exists())
            self.assertIn("deleted_at_iso=", (temporary / "github.output").read_text())
            self.assertIn("Confirmed deletion:", (temporary / "summary.md").read_text())
            az_log = (temporary / "az.log").read_text()
            self.assertIn("appsettings delete", az_log)
            self.assertIn("webapp restart", az_log)
            self.assertIn("CloudCompatibility__StagingFixture", az_log)
            self.assertNotIn("=missing-capability", az_log)
            combined = result.stderr + result.stdout
            self.assertIn("BFF smoke reported compatible", combined)
            self.assertNotIn("missing-capability", combined)
            curl_log = (temporary / "curl.log").read_text()
            self.assertIn("--request POST", curl_log)
            self.assertIn("action", curl_log)
            self.assertIn("/auth/v1/token?grant_type=password", curl_log)
            self.assertNotIn(env["MINTED_ACCESS_TOKEN"], (temporary / "summary.md").read_text())

    def test_script_uses_only_allowlisted_az_commands(self) -> None:
        found: set[tuple[str, ...]] = set()
        for match in re.finditer(
            r"\b(?:az_tsv|az)\s+([A-Za-z][\w-]+(?:\s+[A-Za-z][\w-]+){0,8})",
            SCRIPT.read_text(),
        ):
            command: list[str] = []
            for token in match.group(1).split():
                if token.startswith("-") or token.startswith("$") or token.startswith("\""):
                    break
                command.append(token)
            if command:
                found.add(tuple(command))
        self.assertTrue(found)
        unknown = found - ALLOWED_AZ_COMMANDS
        self.assertEqual(set(), unknown)
        self.assertNotIn("webapp deployment list", SCRIPT.read_text())
        self.assertNotIn("webapp log deployment", SCRIPT.read_text())

    def test_preflight_names_each_missing_input_before_arming(self) -> None:
        required = {
            "STAGING_E2E_COMPAT_EMAIL": "secret STAGING_E2E_COMPAT_EMAIL",
            "STAGING_E2E_COMPAT_PASSWORD": "secret STAGING_E2E_COMPAT_PASSWORD",
            "VITE_SUPABASE_PUBLISHABLE_KEY": "secret VITE_SUPABASE_PUBLISHABLE_KEY",
            "STAGING_SUPABASE_PROJECT_REF": "variable STAGING_SUPABASE_PROJECT_REF",
            "EXPECTED_STAGING_SUPABASE_ORIGIN": "variable EXPECTED_STAGING_SUPABASE_ORIGIN",
            "CLOUD_BFF_SMOKE_URL": "variable CLOUD_BFF_SMOKE_URL",
        }
        for key, needle in required.items():
            with self.subTest(key=key), tempfile.TemporaryDirectory() as raw:
                temporary = Path(raw)
                digest = "sha256:" + ("0" * 64)
                image = self.tagged_image(digest)
                env = self.exclusive_failure_env(temporary, image)
                env[key] = ""
                result = self.run_script(env, "preflight")
                combined = result.stderr + result.stdout
                self.assertNotEqual(0, result.returncode)
                self.assertIn("Compatibility fixture inputs are missing", combined)
                self.assertIn(needle, combined)
                self.assertNotIn("Preflight captured", combined)
                self.assertNotIn("appsettings set", Path(env["AZ_CALL_LOG"]).read_text() if Path(env["AZ_CALL_LOG"]).exists() else "")

    def test_refuses_the_production_supabase_ref(self) -> None:
        with tempfile.TemporaryDirectory() as raw:
            temporary = Path(raw)
            digest = "sha256:" + ("a" * 64)
            image = self.tagged_image(digest)
            env = self.exclusive_failure_env(temporary, image)
            env["STAGING_SUPABASE_PROJECT_REF"] = PRODUCTION_SUPABASE_REF
            env["EXPECTED_STAGING_SUPABASE_ORIGIN"] = f"https://{PRODUCTION_SUPABASE_REF}.supabase.co"
            env["CLOUD_BFF_SMOKE_URL"] = f"https://{PRODUCTION_SUPABASE_REF}.supabase.co/functions/v1/control-bff"
            self.assert_preflight_stops_at(env, "production Supabase project ref")

    def test_preflight_uses_deploy_staging_deployment_not_the_fixture_job(self) -> None:
        with tempfile.TemporaryDirectory() as raw:
            temporary = Path(raw)
            digest = "sha256:" + ("b" * 64)
            image = self.tagged_image(digest)
            self.write_fake_az(temporary, ["Application__BuildNumber"], image, digest)
            self.write_fake_gh(temporary)
            self.write_fake_curl(temporary)
            (temporary / "deployment-ids.txt").write_text(f"99\n{DEPLOY_STAGING_DEPLOYMENT_ID}\n")
            env = self.environment(temporary)
            env["AZ_CALL_LOG"] = str(temporary / "az.log")
            env["GH_CALL_LOG"] = str(temporary / "gh.log")
            env["GH_DEPLOYMENT_IDS"] = str(temporary / "deployment-ids.txt")
            env["GITHUB_OUTPUT"] = str(temporary / "github.output")
            result = self.run_script(env, "preflight")
            self.assertEqual(0, result.returncode, result.stderr + result.stdout)
            self.assertIn(f"deployment_id={DEPLOY_STAGING_DEPLOYMENT_ID}", (temporary / "github.output").read_text())
            self.assertNotIn("deployment_id=99", (temporary / "github.output").read_text())

    def test_restore_fails_if_a_deploy_staging_run_started(self) -> None:
        with tempfile.TemporaryDirectory() as raw:
            temporary = Path(raw)
            digest = "sha256:" + ("c" * 64)
            image = self.tagged_image(digest)
            names = temporary / "names.txt"
            names.write_text("Application__BuildNumber\n")
            present = temporary / "present"
            present.write_text("present\n")
            self.write_fake_az(temporary, ["Application__BuildNumber"], image, digest)
            self.write_fake_gh(temporary)
            self.write_fake_curl(temporary)
            restored = '{"status":"ok","buildNumber":"232","imageId":"%s","compatibilityFixture":null}' % HEALTH_COMMIT
            health = self.write_health_responses(temporary, restored, restored, restored, restored)
            (temporary / "deployment-ids.txt").write_text(
                f"{DEPLOY_STAGING_DEPLOYMENT_ID}\n888\n"
            )
            env = self.environment(temporary)
            env["AZ_CALL_LOG"] = str(temporary / "az.log")
            env["GH_CALL_LOG"] = str(temporary / "gh.log")
            env["AZ_SETTING_PRESENT"] = str(present)
            env["EXPECTED_DIGEST"] = digest
            env["EXPECTED_BUILD_NUMBER"] = "232"
            env["EXPECTED_COMMIT"] = HEALTH_COMMIT
            env["EXPECTED_DEPLOYMENT_ID"] = "888"
            env["EXPECTED_IMAGE_REFERENCE"] = image
            env["EXPECTED_PREFLIGHT_STARTED_AT"] = "2026-10-04T00:00:00Z"
            env["EXPECTED_SETTING_NAMES_PATH"] = str(names)
            env["HEALTH_RETRY_SECONDS"] = "0"
            env["HEALTH_RESPONSES"] = str(health)
            env["GH_DEPLOYMENT_IDS"] = str(temporary / "deployment-ids.txt")
            result = self.run_script(env, "restore")
            self.assertNotEqual(0, result.returncode)
            self.assertIn("Deploy staging run started between preflight and postflight", result.stderr + result.stdout)

    def test_restore_ignores_non_test_azure_api_deploy_runs(self) -> None:
        with tempfile.TemporaryDirectory() as raw:
            temporary = Path(raw)
            digest = "sha256:" + ("c" * 64)
            image = self.tagged_image(digest)
            names = temporary / "names.txt"
            names.write_text("Application__BuildNumber\nWEBSITES_PORT\n")
            present = temporary / "present"
            present.write_text("present\n")
            self.write_fake_az(temporary, ["Application__BuildNumber", "WEBSITES_PORT"], image, digest)
            self.write_fake_gh(temporary)
            self.write_fake_curl(temporary)
            restored = '{"status":"ok","buildNumber":"232","imageId":"%s","compatibilityFixture":null}' % HEALTH_COMMIT
            health = self.write_health_responses(temporary, restored, restored, restored, restored)
            (temporary / "deployments.json").write_text(
                json.dumps(
                    [
                        {
                            "id": 9001,
                            "created_at": "2026-10-04T08:00:00Z",
                            "environment": "production",
                        },
                        {
                            "id": int(DEPLOY_STAGING_DEPLOYMENT_ID),
                            "created_at": "2026-10-03T00:00:00Z",
                            "environment": "test",
                        },
                    ]
                )
            )
            env = self.environment(temporary)
            env["AZ_CALL_LOG"] = str(temporary / "az.log")
            env["GH_CALL_LOG"] = str(temporary / "gh.log")
            env["AZ_SETTING_PRESENT"] = str(present)
            env["EXPECTED_DIGEST"] = digest
            env["EXPECTED_BUILD_NUMBER"] = "232"
            env["EXPECTED_COMMIT"] = HEALTH_COMMIT
            env["EXPECTED_DEPLOYMENT_ID"] = DEPLOY_STAGING_DEPLOYMENT_ID
            env["EXPECTED_IMAGE_REFERENCE"] = image
            env["EXPECTED_PREFLIGHT_STARTED_AT"] = "2026-10-04T00:00:00Z"
            env["EXPECTED_SETTING_NAMES_PATH"] = str(names)
            env["HEALTH_RETRY_SECONDS"] = "0"
            env["HEALTH_RESPONSES"] = str(health)
            env["GH_DEPLOYMENT_JSON"] = str(temporary / "deployments.json")
            env["GITHUB_STEP_SUMMARY"] = str(temporary / "summary.md")
            result = self.run_script(env, "restore")
            self.assertEqual(0, result.returncode, result.stderr + result.stdout)
            self.assertIn("BFF smoke reported compatible", result.stderr + result.stdout)
            self.assertNotIn("/deployments/9001", (temporary / "gh.log").read_text())

    def test_mint_masks_the_token_and_never_writes_outputs(self) -> None:
        with tempfile.TemporaryDirectory() as raw:
            temporary = Path(raw)
            token = mint_jwt()
            self.write_fake_curl(temporary)
            env = self.environment(temporary, MINTED_ACCESS_TOKEN=token)
            env["GITHUB_OUTPUT"] = str(temporary / "github.output")
            env["CURL_CALL_LOG"] = str(temporary / "curl.log")
            result = self.run_script(env, "mint")
            self.assertEqual(0, result.returncode, result.stderr + result.stdout)
            combined = result.stderr + result.stdout
            self.assertIn("::add-mask::", combined)
            self.assertIn(token, combined)
            self.assertFalse((temporary / "github.output").exists() and token in (temporary / "github.output").read_text())
            self.assertIn(f"{STAGING_SUPABASE_ORIGIN}/auth/v1/token?grant_type=password", (temporary / "curl.log").read_text())
            self.assertNotIn("CLOUD_COMPATIBILITY_TOKEN", SCRIPT.read_text())

    def test_mint_rejects_a_token_without_authenticated_role(self) -> None:
        with tempfile.TemporaryDirectory() as raw:
            temporary = Path(raw)
            self.write_fake_curl(temporary)
            env = self.environment(temporary, MINTED_ACCESS_TOKEN=mint_jwt(role="anon"))
            result = self.run_script(env, "mint")
            self.assertNotEqual(0, result.returncode)
            self.assertIn("role must be authenticated", result.stderr + result.stdout)

    def test_restore_deletes_before_a_mint_failure(self) -> None:
        with tempfile.TemporaryDirectory() as raw:
            temporary = Path(raw)
            digest = "sha256:" + ("c" * 64)
            image = self.tagged_image(digest)
            names = temporary / "names.txt"
            names.write_text("Application__BuildNumber\n")
            present = temporary / "present"
            present.write_text("present\n")
            self.write_fake_az(temporary, ["Application__BuildNumber"], image, digest)
            self.write_fake_gh(temporary)
            self.write_fake_curl(temporary)
            restored = '{"status":"ok","buildNumber":"232","imageId":"%s","compatibilityFixture":null}' % HEALTH_COMMIT
            health = self.write_health_responses(temporary, restored, restored, restored, restored)
            env = self.environment(temporary)
            env["AZ_CALL_LOG"] = str(temporary / "az.log")
            env["GH_CALL_LOG"] = str(temporary / "gh.log")
            env["AZ_SETTING_PRESENT"] = str(present)
            env["EXPECTED_DIGEST"] = digest
            env["EXPECTED_BUILD_NUMBER"] = "232"
            env["EXPECTED_COMMIT"] = HEALTH_COMMIT
            env["EXPECTED_DEPLOYMENT_ID"] = DEPLOY_STAGING_DEPLOYMENT_ID
            env["EXPECTED_IMAGE_REFERENCE"] = image
            env["EXPECTED_PREFLIGHT_STARTED_AT"] = "2026-10-04T00:00:00Z"
            env["EXPECTED_SETTING_NAMES_PATH"] = str(names)
            env["HEALTH_RETRY_SECONDS"] = "0"
            env["HEALTH_RESPONSES"] = str(health)
            env["TOKEN_FAIL"] = "1"
            result = self.run_script(env, "restore")
            self.assertNotEqual(0, result.returncode)
            self.assertIn("password grant did not return HTTP 200", result.stderr + result.stdout)
            self.assertFalse(present.exists())
            az_log = (temporary / "az.log").read_text()
            self.assertIn("appsettings delete", az_log)
            self.assertIn("webapp restart", az_log)

    def test_preflight_fails_when_health_omits_compatibility_fixture(self) -> None:
        with tempfile.TemporaryDirectory() as raw:
            temporary = Path(raw)
            digest = "sha256:" + ("a" * 64)
            image = self.tagged_image(digest)
            self.write_fake_az(temporary, ["Application__BuildNumber"], image, digest)
            self.write_fake_gh(temporary)
            self.write_fake_curl(temporary)
            missing = '{"status":"ok","buildNumber":"232","imageId":"%s"}' % HEALTH_COMMIT
            health = self.write_health_responses(temporary, missing)
            env = self.environment(temporary)
            env["AZ_CALL_LOG"] = str(temporary / "az.log")
            env["GH_CALL_LOG"] = str(temporary / "gh.log")
            env["HEALTH_RESPONSES"] = str(health)
            result = self.run_script(env, "preflight")
            self.assertNotEqual(0, result.returncode)
            self.assertIn("compatibilityFixture must be JSON null", result.stderr + result.stdout)

    def test_restore_fails_closed_when_a_preflight_output_is_missing(self) -> None:
        with tempfile.TemporaryDirectory() as raw:
            temporary = Path(raw)
            digest = "sha256:" + ("c" * 64)
            image = self.tagged_image(digest)
            names = temporary / "names.txt"
            names.write_text("Application__BuildNumber\n")
            present = temporary / "present"
            present.write_text("present\n")
            self.write_fake_az(temporary, ["Application__BuildNumber"], image, digest)
            self.write_fake_gh(temporary)
            self.write_fake_curl(temporary)
            restored = '{"status":"ok","buildNumber":"232","imageId":"%s","compatibilityFixture":null}' % HEALTH_COMMIT
            health = self.write_health_responses(temporary, restored, restored, restored, restored)
            env = self.environment(temporary)
            env["AZ_CALL_LOG"] = str(temporary / "az.log")
            env["AZ_SETTING_PRESENT"] = str(present)
            env["EXPECTED_DIGEST"] = digest
            env["EXPECTED_COMMIT"] = HEALTH_COMMIT
            env["EXPECTED_DEPLOYMENT_ID"] = DEPLOY_STAGING_DEPLOYMENT_ID
            env["EXPECTED_IMAGE_REFERENCE"] = image
            env["EXPECTED_PREFLIGHT_STARTED_AT"] = "2026-10-04T00:00:00Z"
            env["EXPECTED_SETTING_NAMES_PATH"] = str(names)
            env["HEALTH_RETRY_SECONDS"] = "0"
            env["HEALTH_RESPONSES"] = str(health)
            result = self.run_script(env, "restore")
            self.assertNotEqual(0, result.returncode)
            self.assertIn("missing the preflight /health build number", result.stderr + result.stdout)
            self.assertFalse(present.exists())
            self.assertIn("appsettings delete", (temporary / "az.log").read_text())

    def test_restore_stops_deploy_walk_at_the_first_older_deployment(self) -> None:
        with tempfile.TemporaryDirectory() as raw:
            temporary = Path(raw)
            digest = "sha256:" + ("c" * 64)
            image = self.tagged_image(digest)
            names = temporary / "names.txt"
            names.write_text("Application__BuildNumber\n")
            present = temporary / "present"
            present.write_text("present\n")
            self.write_fake_az(temporary, ["Application__BuildNumber"], image, digest)
            self.write_fake_gh(temporary)
            self.write_fake_curl(temporary)
            restored = '{"status":"ok","buildNumber":"232","imageId":"%s","compatibilityFixture":null}' % HEALTH_COMMIT
            health = self.write_health_responses(temporary, restored, restored, restored, restored)
            rows = [
                {
                    "id": int(DEPLOY_STAGING_DEPLOYMENT_ID),
                    "created_at": "2026-10-03T12:00:00Z",
                    "environment": "test",
                }
            ]
            rows.extend(
                {
                    "id": 2000 + index,
                    "created_at": f"2026-10-02T{index:02d}:00:00Z",
                    "environment": "test",
                }
                for index in range(47)
            )
            (temporary / "deployments.json").write_text(json.dumps(rows))
            env = self.environment(temporary)
            env["AZ_CALL_LOG"] = str(temporary / "az.log")
            env["GH_CALL_LOG"] = str(temporary / "gh.log")
            env["AZ_SETTING_PRESENT"] = str(present)
            env["EXPECTED_DIGEST"] = digest
            env["EXPECTED_BUILD_NUMBER"] = "232"
            env["EXPECTED_COMMIT"] = HEALTH_COMMIT
            env["EXPECTED_DEPLOYMENT_ID"] = DEPLOY_STAGING_DEPLOYMENT_ID
            env["EXPECTED_IMAGE_REFERENCE"] = image
            env["EXPECTED_PREFLIGHT_STARTED_AT"] = "2026-10-04T00:00:00Z"
            env["EXPECTED_SETTING_NAMES_PATH"] = str(names)
            env["HEALTH_RETRY_SECONDS"] = "0"
            env["HEALTH_RESPONSES"] = str(health)
            env["GH_DEPLOYMENT_JSON"] = str(temporary / "deployments.json")
            env["GITHUB_STEP_SUMMARY"] = str(temporary / "summary.md")
            result = self.run_script(env, "restore")
            self.assertEqual(0, result.returncode, result.stderr + result.stdout)
            created_at_reads = (temporary / "gh.log").read_text().count("/deployments/") - (
                temporary / "gh.log"
            ).read_text().count("/deployments?")
            self.assertLess(created_at_reads, 8)

    def write_context(self, temporary: Path) -> Path:
        path = temporary / "compat-context.json"
        path.write_text(
            json.dumps(
                {
                    "organizationId": "11111111-1111-4111-8111-111111111111",
                    "workspaceId": "22222222-2222-4222-8222-222222222222",
                }
            )
        )
        return path

    def test_in_hold_probes_record_k_and_require_the_full_envelope(self) -> None:
        with tempfile.TemporaryDirectory() as raw:
            temporary = Path(raw)
            self.write_fake_curl(temporary)
            env = self.environment(temporary)
            env["CURL_CALL_LOG"] = str(temporary / "curl.log")
            env["BFF_ARMED"] = "1"
            env["EXPECTED_COMPAT_CONTEXT_PATH"] = str(self.write_context(temporary))
            env["GITHUB_STEP_SUMMARY"] = str(temporary / "summary.md")
            env["GITHUB_OUTPUT"] = str(temporary / "github.output")
            result = self.run_script(env, "probes")
            self.assertEqual(0, result.returncode, result.stderr + result.stdout)
            summary = (temporary / "summary.md").read_text()
            self.assertIn("K=3", summary)
            self.assertIn("listOrganizations: HTTP 503", summary)
            self.assertIn("updateInstance: HTTP 503", summary)
            self.assertIn("createInstanceDeleteConfirmation: HTTP 503", summary)
            self.assertIn("k_probe_count=3", (temporary / "github.output").read_text())
            self.assertNotIn(env["MINTED_ACCESS_TOKEN"], summary)
            self.assertNotIn("11111111-1111-4111-8111-111111111111", summary)
            curl_log = (temporary / "curl.log").read_text()
            self.assertIn("listOrganizations", curl_log)
            self.assertIn("updateInstance", curl_log)
            self.assertIn("createInstanceDeleteConfirmation", curl_log)

    def test_in_hold_probes_fail_without_no_store(self) -> None:
        with tempfile.TemporaryDirectory() as raw:
            temporary = Path(raw)
            self.write_fake_curl(temporary)
            env = self.environment(temporary)
            env["BFF_ARMED"] = "1"
            env["BFF_OMIT_NO_STORE"] = "1"
            env["EXPECTED_COMPAT_CONTEXT_PATH"] = str(self.write_context(temporary))
            result = self.run_script(env, "probes")
            self.assertNotEqual(0, result.returncode)
            self.assertIn("no-store", result.stderr + result.stdout)

    def test_telemetry_report_is_inconclusive_and_never_queries_azure(self) -> None:
        with tempfile.TemporaryDirectory() as raw:
            temporary = Path(raw)
            env = self.environment(temporary)
            env["AZ_CALL_LOG"] = str(temporary / "az.log")
            env["ARMED_AT_ISO"] = "2026-10-04T11:23:00Z"
            env["DELETED_AT_ISO"] = "2026-10-04T11:37:00Z"
            env["EXPECTED_COMPATIBILITY_REQUESTS"] = "7"
            env["GITHUB_STEP_SUMMARY"] = str(temporary / "summary.md")
            result = self.run_script(env, "telemetry")
            self.assertEqual(0, result.returncode, result.stderr + result.stdout)
            summary = (temporary / "summary.md").read_text()
            self.assertIn("Inconclusive", summary)
            self.assertIn("2026-10-04T11:23:00Z", summary)
            self.assertIn("2026-10-04T11:37:00Z", summary)
            self.assertIn("Expected GET /api/cloud/compatibility count from this run: 7", summary)
            self.assertIn("requests", summary)
            self.assertFalse((temporary / "az.log").exists())
            self.assertNotIn(env["MINTED_ACCESS_TOKEN"], summary)


if __name__ == "__main__":
    unittest.main()
