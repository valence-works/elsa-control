#!/usr/bin/env python3
"""Offline contract checks for the Azure API deployment workflow."""

from __future__ import annotations

import json
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from textwrap import dedent


ROOT = Path(__file__).resolve().parents[2]
WORKFLOW = ROOT / ".github" / "workflows" / "azure-api-deploy.yml"
PRODUCTION_BILLING_FIXTURE = {
    "STRIPE_PRODUCTION_PRICE_ID": "price_livefixture",
    "CONTROL_PRODUCTION_WEBHOOK_URL": "https://control.example.test/api/billing/webhooks/stripe",
    "AZURE_EXPECTED_PRODUCTION_PORTAL_RETURN_URL": "https://control.example.test/billing",
    "AZURE_EXPECTED_PRODUCTION_CHECKOUT_SUCCESS_URL": "https://cloud.example.test/checkout/return?session_id={CHECKOUT_SESSION_ID}",
    "AZURE_EXPECTED_PRODUCTION_CHECKOUT_CANCEL_URL": "https://cloud.example.test/dashboard/billing",
    "AZURE_EXPECTED_PRODUCTION_CLOUD_PORTAL_RETURN_URL": "https://cloud.example.test/dashboard",
}


class AzureApiDeployWorkflowTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.source = WORKFLOW.read_text()

    def test_capture_supports_classic_and_sitecontainer_runtime_images(self) -> None:
        self.assertIn("image_reference_pattern=", self.source)
        self.assertIn("(@sha256:[[:xdigit:]]{64})?", self.source)
        self.assertIn(
            'if [[ "$linux_fx_version" =~ ^DOCKER\\|$image_reference_pattern$ ]]; then',
            self.source,
        )
        self.assertIn('elif [ "$linux_fx_version" = "SITECONTAINERS" ]', self.source)
        self.assertIn("az webapp sitecontainers show", self.source)
        self.assertEqual(3, self.source.count("--query image"))
        self.assertNotIn("--query properties.image", self.source)
        self.assertIn(
            "Could not capture the current main sitecontainer image; refusing an unprotected deployment.",
            self.source,
        )
        self.assertIn(
            'if [[ ! "$sitecontainer_image" =~ ^$image_reference_pattern$ ]]; then',
            self.source,
        )
        self.assertNotIn("--registry-password", self.source)
        self.assertNotIn("--registry-username", self.source)

    def test_run_blocks_have_yaml_indented_content(self) -> None:
        """Catch workflow validation failures caused by unindented heredoc lines."""

        lines = self.source.splitlines()
        run_block_count = 0
        for line_number, line in enumerate(lines):
            if not line.startswith("        run: |"):
                continue

            run_block_count += 1
            key_indent = len(line) - len(line.lstrip(" "))
            next_step = next(
                (
                    index
                    for index in range(line_number + 1, len(lines))
                    if lines[index].startswith("      - name:")
                ),
                len(lines),
            )
            content = lines[line_number + 1 : next_step]
            self.assertTrue(
                any(candidate.strip() for candidate in content),
                f"run block on line {line_number + 1} has no content",
            )
            for offset, candidate in enumerate(content, line_number + 2):
                if candidate.strip():
                    self.assertGreaterEqual(
                        len(candidate) - len(candidate.lstrip(" ")),
                        key_indent + 2,
                        f"workflow content on line {offset} is outside its YAML block",
                    )

        self.assertGreater(run_block_count, 0)

    def test_deploy_and_rollback_use_the_captured_runtime_mode(self) -> None:
        self.assertIn("env.DEPLOY_MODE != 'build'", self.source)
        self.assertIn("env.DEPLOY_MODE == 'promote'", self.source)
        self.assertIn("actions/download-artifact@v4", self.source)
        self.assertIn("actions/upload-artifact@v4", self.source)
        self.assertIn("Verify candidate image in ACR", self.source)
        self.assertIn('"::error::The validated candidate image is not present in the configured registry; refusing promotion."', self.source)
        self.assertIn('command -v jq >/dev/null 2>&1', self.source)
        self.assertIn('candidate_tag="candidate-${GITHUB_RUN_ID}-${GITHUB_RUN_ATTEMPT}-${GITHUB_SHA}"', self.source)
        self.assertIn('--name "elsa-control/api:$candidate_tag"', self.source)
        self.assertIn("candidate_descriptor_directory=\"$RUNNER_TEMP/elsa-control-api-candidate\"", self.source)
        self.assertIn('"$RUNNER_TEMP/elsa-control-api-candidate/candidate.json"', self.source)
        self.assertIn("capture_succeeded=false", self.source)
        self.assertIn("capture_succeeded=true", self.source)
        self.assertIn("steps.current-deployment.outputs.capture_succeeded == 'true'", self.source)
        main_guard_start = self.source.index("      - name: Require main ref for Azure mutation")
        main_guard_end = self.source.index("\n      - name:", main_guard_start + 1)
        restore_start = self.source.index("      - name: Restore")
        login_start = self.source.index("      - name: Log in Azure CLI")
        self.assertLess(main_guard_start, restore_start)
        self.assertLess(main_guard_start, login_start)
        main_guard = self.source[main_guard_start:main_guard_end]
        self.assertIn('GITHUB_REF:-', main_guard)
        self.assertNotIn("DEPLOY_MODE != 'build'", main_guard)
        self.assertIn("The promoted runtime did not match the validated immutable image", self.source)
        self.assertIn("--query linuxFxVersion", self.source)
        self.assertEqual(3, self.source.count("--query image"))
        self.assertIn(
            'current_deployment_mode="${{ steps.current-deployment.outputs.deployment_mode }}"',
            self.source,
        )
        self.assertIn('if [ "$current_deployment_mode" = "sitecontainers" ]', self.source)
        self.assertIn('elif [ "$current_deployment_mode" = "classic" ]', self.source)
        self.assertIn("PREVIOUS_SITECONTAINER_IMAGE", self.source)
        self.assertIn('--image "$PREVIOUS_SITECONTAINER_IMAGE"', self.source)
        self.assertIn("steps.deploy-api.outcome == 'failure'", self.source)
        self.assertIn("steps.health-gate.outcome == 'failure'", self.source)
        self.assertNotIn("steps.managed-telemetry.outcome == 'failure'", self.source)
        self.assertNotIn("steps.managed-telemetry-what-if.outcome == 'failure'", self.source)
        self.assertIn("steps.production-billing-capture.outcome == 'success'", self.source)
        self.assertIn(
            "Restore previous API deployment after deployment, configuration, or health failure",
            self.source,
        )

    def test_production_billing_capture_restore_and_cleanup_surround_replacement(self) -> None:
        capture_start = self.source.index("      - name: Capture production Stripe billing settings before replacement")
        deploy_start = self.source.index("      - name: Deploy API app")
        rollback_start = self.source.index("      - name: Restore previous API deployment")
        cleanup_start = self.source.index("      - name: Clean up captured production Stripe billing settings")
        self.assertLess(capture_start, deploy_start)
        self.assertLess(deploy_start, rollback_start)
        self.assertLess(rollback_start, cleanup_start)
        rollback_end = self.source.index("\n      - name:", rollback_start + 1)
        rollback = self.source[rollback_start:rollback_end]
        self.assertIn('python3 scripts/production_stripe_reconcile.py --reapply "$PRODUCTION_BILLING_CAPTURE_PATH"', rollback)
        self.assertIn("--audit-capture", self.source)
        self.assertIn('rm -f -- "$PRODUCTION_BILLING_CAPTURE_PATH"', self.source)

    def test_deploy_blocks_preserve_billing_with_fake_provider_for_each_mutating_mode(self) -> None:
        """Execute the workflow shell blocks with safe fake providers."""

        def step_script(step_name: str) -> str:
            start = self.source.index(f"      - name: {step_name}")
            run_start = self.source.index("        run: |\n", start) + len("        run: |\n")
            end = self.source.find("\n      - name:", run_start)
            if end == -1:
                end = len(self.source)
            return dedent(self.source[run_start:end])

        deploy_script = step_script("Deploy API app").replace(
            "${{ steps.current-deployment.outputs.deployment_mode }}", "classic"
        )
        capture_script = step_script("Capture production Stripe billing settings before replacement")
        audit_script = step_script("Audit production Stripe billing")
        health_script = step_script("Verify deployed API health")
        rollback_script = step_script("Restore previous API deployment after deployment, configuration, or health failure")
        cleanup_script = step_script("Clean up captured production Stripe billing settings")

        with tempfile.TemporaryDirectory() as temporary:
            temporary_path = Path(temporary)
            bin_path = temporary_path / "bin"
            bin_path.mkdir()
            events_path = temporary_path / "events.log"
            state_path = temporary_path / "state.json"
            capture_path = temporary_path / "production-billing.json"
            state_path.write_text(json.dumps({"runtime": "DOCKER|old-image"}))

            def write_executable(name: str, contents: str) -> None:
                path = bin_path / name
                path.write_text(contents)
                path.chmod(0o700)

            write_executable(
                "az",
                f'''#!/usr/bin/env python3
import json
import os
import sys
from pathlib import Path

events = Path({str(events_path)!r})
state_path = Path({str(state_path)!r})
args = sys.argv[1:]
state = json.loads(state_path.read_text())

def record(value):
    with events.open("a", encoding="utf-8") as handle:
        handle.write(value + "\\n")

if args[:3] == ["webapp", "config", "appsettings"] and "list" in args:
    query = args[args.index("--query") + 1] if "--query" in args else ""
    record("appsettings-list")
    if "Billing__Stripe__ExpectedMode" in query:
        current = os.environ.get("CURRENT_EXPECTED_MODE", "")
        if current:
            print(json.dumps({{
                "name": "Billing__Stripe__ExpectedMode",
                "value": current,
                "slotSetting": False,
            }}))
        else:
            print("null")
    else:
        print("0" if "length(@" in query else "")
elif args[:3] == ["webapp", "config", "appsettings"] and "set" in args:
    record("appsettings-set")
    if any("Billing__Stripe__ExpectedMode=" in argument for argument in args):
        record("billing-expected-mode-set")
    if os.environ.get("FAIL_BEFORE_REAPPLY") == "1":
        raise SystemExit(41)
elif args[:3] == ["webapp", "config", "appsettings"] and "delete" in args:
    record("appsettings-delete")
elif args[:3] == ["webapp", "config", "set"]:
    runtime = args[args.index("--linux-fx-version") + 1]
    state["runtime"] = runtime
    state_path.write_text(json.dumps(state))
    record("old-runtime-restored")
elif args[:3] == ["webapp", "config", "container"] and "set" in args:
    image = args[args.index("--container-image-name") + 1]
    state["runtime"] = "DOCKER|" + image
    state_path.write_text(json.dumps(state))
    record("runtime-replaced")
elif args[:3] == ["webapp", "sitecontainers", "update"]:
    image = args[args.index("--image") + 1]
    state["runtime"] = image
    state_path.write_text(json.dumps(state))
    record("runtime-replaced")
elif args[:3] == ["webapp", "config", "show"]:
    record("runtime-read")
    print(state["runtime"])
elif args[:3] == ["webapp", "sitecontainers", "show"]:
    record("runtime-read")
    print(state["runtime"])
elif args[:2] == ["webapp", "restart"]:
    record("restart")
elif args[:2] == ["webapp", "show"]:
    record("health-host-read")
    print("production-api.azurewebsites.net")
else:
    record("az:" + (args[0] if args else "empty"))
''',
            )
            write_executable(
                "docker",
                f'''#!/usr/bin/env bash
set -euo pipefail
printf 'docker:%s\\n' "$1" >> {str(events_path)!r}
''',
            )
            write_executable(
                "infra-replacement",
                f'''#!/usr/bin/env bash
set -euo pipefail
printf 'infra-replaced\\n' >> {str(events_path)!r}
if [ "${{FAIL_INFRA:-0}}" = 1 ]; then
  exit 43
fi
''',
            )
            real_python = sys.executable
            write_executable(
                "python3",
                f'''#!{real_python}
import os
import sys
from pathlib import Path

args = sys.argv[1:]
if args and args[0].endswith("scripts/production_stripe_reconcile.py"):
    operation = "audit" if "--audit" in args else "capture" if "--capture" in args else "reapply"
    with Path({str(events_path)!r}).open("a", encoding="utf-8") as handle:
        handle.write(operation + "\\n")
    if operation == "capture":
        destination = Path(args[args.index("--capture") + 1])
        destination.write_text("fake-private-capture")
    if operation == "reapply" and os.environ.get("FAIL_REAPPLY") == "1":
        raise SystemExit(47)
    if operation == "audit" and os.environ.get("FAIL_AUDIT") == "1":
        raise SystemExit(53)
    raise SystemExit(0)
os.execv({real_python!r}, [{real_python!r}, *args])
''',
            )
            write_executable(
                "curl",
                '''#!/usr/bin/env bash
set -euo pipefail
output=""
while [ "$#" -gt 0 ]; do
  if [ "$1" = "--output" ]; then output="$2"; shift 2; continue; fi
  shift
done
if [ "${ROLLBACK_HEALTH:-0}" = 1 ]; then
  printf '{"status":"ok","buildNumber":"88"}' > "$output"
  printf '200'
else
  printf '{"status":"unhealthy"}' > "$output"
  printf '500'
fi
''',
            )
            write_executable("sleep", "#!/usr/bin/env bash\nexit 0\n")

            base_environment = os.environ.copy()
            base_environment.update(
                {
                    "PATH": f"{bin_path}:{base_environment['PATH']}",
                    "TARGET_ENVIRONMENT": "production",
                    "AZURE_ENV_NAME": "production",
                    "AZURE_RESOURCE_GROUP": "rg-production",
                    "AZURE_WEBAPP_NAME": "production-api",
                    "AZURE_LOCATION": "westeurope",
                    "AZURE_SUBSCRIPTION_ID": "subscription",
                    "AZURE_CONTAINER_REGISTRY_ENDPOINT": "registry.azurecr.io",
                    "GITHUB_SHA": "a" * 40,
                    "GITHUB_RUN_NUMBER": "101",
                    "VALIDATED_CANDIDATE_IMAGE": "registry.azurecr.io/elsa-control/api@sha256:" + "b" * 64,
                    "VALIDATED_CANDIDATE_BUILD_NUMBER": "99",
                    "PRODUCTION_BILLING_CAPTURE_PATH": str(capture_path),
                    "BILLING_EXPECTED_MODE": "live",
                    "EXTERNAL_ENGINE_PAIRING_ALLOWED_ORG_IDS": "",
                    "STAGING_BILLING_LIFECYCLE_LEVER_ENABLED": "",
                    "STAGING_BILLING_LIFECYCLE_LEVER_ALLOWED_ORG_IDS": "",
                    "STAGING_RECOVERY_LIFECYCLE_LEVER_ENABLED": "",
                    "STAGING_RECOVERY_LIFECYCLE_LEVER_ALLOWED_INSTANCE_IDS": "",
                    "STAGING_SMOKE_OWNER_INSTANCE_ID": "",
                }
            )

            def run_shell(script: str, *, mode: str, **extra: str) -> subprocess.CompletedProcess[str]:
                environment = base_environment | {"DEPLOY_MODE": mode, **extra}
                return subprocess.run(
                    ["bash", "-c", script],
                    cwd=ROOT,
                    env=environment,
                    capture_output=True,
                    text=True,
                    check=False,
                )

            def events() -> list[str]:
                return events_path.read_text().splitlines() if events_path.exists() else []

            rollback_environment = {
                "PREVIOUS_DEPLOYMENT_MODE": "classic",
                "PREVIOUS_LINUX_FX_VERSION": "DOCKER|old-image",
                "PREVIOUS_SITECONTAINER_IMAGE": "",
                "PREVIOUS_BUILD_NUMBER": "88",
                "PREVIOUS_BUILD_NUMBER_PRESENT": "true",
                "PREVIOUS_HEALTH_BUILD_NUMBER": "88",
                "PREVIOUS_HEALTH_IMAGE_ID": "",
                "ROLLBACK_HEALTH": "1",
            }

            def run_rollback(mode: str) -> subprocess.CompletedProcess[str]:
                return run_shell(rollback_script, mode=mode, **rollback_environment)

            for mode in ("app", "infra", "promote"):
                events_path.write_text("")
                state_path.write_text(json.dumps({"runtime": "DOCKER|old-image"}))
                capture = run_shell(capture_script, mode=mode)
                self.assertEqual(0, capture.returncode, capture.stderr)
                self.assertTrue(capture_path.exists())

                mode_script = deploy_script.replace(
                    "scripts/deploy-azure-elsa-control.sh", str(bin_path / "infra-replacement")
                )
                result = run_shell(mode_script, mode=mode)
                self.assertEqual(0, result.returncode, result.stdout + result.stderr)
                observed = events()
                replacement = "infra-replaced" if mode == "infra" else "runtime-replaced"
                self.assertLess(observed.index("capture"), observed.index(replacement))
                self.assertLess(observed.index(replacement), observed.index("reapply"))
                self.assertIn("restart", observed, observed)
                self.assertLess(observed.index("reapply"), observed.index("restart"))
                self.assertIn("billing-expected-mode-set", observed)
                first_expected_mode = observed.index("billing-expected-mode-set")
                if mode in ("app", "promote"):
                    # First billing-enabled start must see ExpectedMode before
                    # the image switch. The later combined settings write still
                    # includes ExpectedMode after the switch. Rollback restores
                    # or deletes the captured prior setting after a failed switch.
                    self.assertLess(first_expected_mode, observed.index(replacement))
                    self.assertLess(
                        observed.index(replacement),
                        observed.index("billing-expected-mode-set", first_expected_mode + 1),
                    )
                else:
                    self.assertLess(observed.index(replacement), first_expected_mode)
                    self.assertEqual(1, observed.count("billing-expected-mode-set"))
                if mode == "app":
                    self.assertIn("docker:build", observed)
                else:
                    self.assertNotIn("docker:build", observed)

            events_path.write_text("")
            state_path.write_text(json.dumps({"runtime": "DOCKER|old-image"}))
            already_set = run_shell(deploy_script, mode="app", CURRENT_EXPECTED_MODE="live")
            self.assertEqual(0, already_set.returncode, already_set.stdout + already_set.stderr)
            already_set_events = events()
            replacement = already_set_events.index("runtime-replaced")
            self.assertLess(
                replacement,
                already_set_events.index("billing-expected-mode-set"),
            )
            self.assertEqual(1, already_set_events.count("billing-expected-mode-set"))

            events_path.write_text("")
            state_path.write_text(json.dumps({"runtime": "DOCKER|old-image"}))
            self.assertEqual(0, run_shell(capture_script, mode="infra").returncode)
            failed_replacement = run_shell(
                deploy_script.replace(
                    "scripts/deploy-azure-elsa-control.sh", str(bin_path / "infra-replacement")
                ),
                mode="infra",
                FAIL_INFRA="1",
            )
            self.assertNotEqual(0, failed_replacement.returncode)
            self.assertNotIn("reapply", events())
            self.assertTrue(capture_path.exists())
            rollback = run_rollback("infra")
            self.assertEqual(0, rollback.returncode, rollback.stdout + rollback.stderr)
            observed = events()
            self.assertLess(observed.index("reapply"), observed.index("old-runtime-restored"))
            self.assertLess(observed.index("old-runtime-restored"), observed.index("restart"))
            self.assertEqual(0, run_shell(cleanup_script, mode="infra").returncode)
            self.assertFalse(capture_path.exists())

            events_path.write_text("")
            state_path.write_text(json.dumps({"runtime": "DOCKER|old-image"}))
            self.assertEqual(0, run_shell(capture_script, mode="app").returncode)
            failed_reapply = run_shell(deploy_script, mode="app", FAIL_REAPPLY="1")
            self.assertNotEqual(0, failed_reapply.returncode)
            self.assertIn("reapply", events())
            self.assertNotIn("restart", events())
            self.assertEqual(0, run_shell(cleanup_script, mode="app").returncode)
            self.assertFalse(capture_path.exists())

            events_path.write_text("")
            capture_path.write_text("fake-private-capture")
            failed_audit = run_shell(audit_script, mode="app", FAIL_AUDIT="1")
            self.assertNotEqual(0, failed_audit.returncode)
            self.assertIn("audit", events())
            rollback = run_rollback("app")
            self.assertEqual(0, rollback.returncode, rollback.stdout + rollback.stderr)
            observed = events()
            self.assertLess(observed.index("audit"), observed.index("reapply"))
            self.assertLess(observed.index("reapply"), observed.index("old-runtime-restored"))
            self.assertEqual(0, run_shell(cleanup_script, mode="app").returncode)
            self.assertFalse(capture_path.exists())

            events_path.write_text("")
            capture_path.write_text("fake-private-capture")
            failed_health = run_shell(health_script, mode="app")
            self.assertNotEqual(0, failed_health.returncode)
            self.assertIn("health-host-read", events())
            rollback = run_rollback("app")
            self.assertEqual(0, rollback.returncode, rollback.stdout + rollback.stderr)
            observed = events()
            self.assertLess(observed.index("reapply"), observed.index("old-runtime-restored"))
            self.assertLess(observed.index("old-runtime-restored"), observed.index("restart"))
            self.assertEqual(0, run_shell(cleanup_script, mode="app").returncode)
            self.assertFalse(capture_path.exists())

    def test_build_mode_skips_production_capture_reapply_and_audit_steps(self) -> None:
        for step_name in (
            "Capture production Stripe billing settings before replacement",
            "Deploy API app",
            "Audit production Stripe billing",
        ):
            start = self.source.index(f"      - name: {step_name}")
            end = self.source.find("\n      - name:", start + 1)
            step = self.source[start : len(self.source) if end == -1 else end]
            self.assertIn("env.DEPLOY_MODE != 'build'", step)

    def test_staged_candidate_contract_is_immutable_and_separate_from_app_mutation(self) -> None:
        self.assertIn("candidate_run_id:", self.source)
        self.assertIn("candidate_digest:", self.source)
        self.assertIn("scripts/validate-azure-api-candidate.sh", self.source)
        self.assertIn("docker push \"$image\"", self.source)
        self.assertIn("az acr manifest show-metadata", self.source)
        self.assertIn("VALIDATED_CANDIDATE_IMAGE: ${{ steps.candidate-authority.outputs.candidate_image }}", self.source)
        self.assertIn("@sha256:", self.source)
        self.assertIn("The candidate image is not the validated immutable repository reference", self.source)
        self.assertIn("ELSA_CONTROL_IMAGE_ID", self.source)

        build_start = self.source.index("      - name: Build and publish API candidate")
        build_end = self.source.index("      - name: Upload API candidate descriptor", build_start)
        build_script = self.source[build_start:build_end]
        self.assertNotIn("az webapp", build_script)
        self.assertNotIn("restart", build_script)
        self.assertNotIn('image="$AZURE_CONTAINER_REGISTRY_ENDPOINT/elsa-control/api:$GITHUB_SHA"', build_script)
        self.assertIn('--build-arg ELSA_CONTROL_IMAGE_ID="$GITHUB_SHA"', build_script)

        promote_start = self.source.index('elif [ "$DEPLOY_MODE" = "promote" ]; then')
        promote_end = self.source.index('else\n            echo "::error::The selected deployment mode cannot mutate', promote_start)
        promote_script = self.source[promote_start:promote_end]
        self.assertNotIn("docker build", promote_script)
        self.assertNotIn("docker push", promote_script)
        self.assertIn('az webapp', promote_script)

        job_env_start = self.source.index("    env:\n", self.source.index("    permissions:"))
        job_env_end = self.source.index("    steps:\n", job_env_start)
        job_env = self.source[job_env_start:job_env_end]
        self.assertNotIn("ADMIN_API_KEY:", job_env)
        self.assertNotIn("BUILDER_CLIENT_API_KEY:", job_env)
        self.assertNotIn("SQL_ADMINISTRATOR_PASSWORD:", job_env)

        self.assertIn("env.DEPLOY_MODE == 'infra' && secrets.ADMIN_API_KEY", self.source)
        self.assertIn("env.DEPLOY_MODE == 'infra' && secrets.BUILDER_CLIENT_API_KEY", self.source)
        self.assertIn("env.DEPLOY_MODE == 'infra' && secrets.CONTROL_ENTRA_CLIENT_SECRET", self.source)
        self.assertIn("CONTROL_ENTRA_CLIENT_ID", self.source)
        self.assertIn("CONTROL_ENTRA_TENANT_ID", self.source)
        self.assertIn("AZURE_PROVISIONER_IDENTITY_ID: ${{ vars.AZURE_PROVISIONER_IDENTITY_ID }}", self.source)
        self.assertIn("AZURE_API_EGRESS_SUBNET_ID: ${{ vars.AZURE_API_EGRESS_SUBNET_ID }}", self.source)
        self.assertIn("EXTERNAL_ENGINE_PAIRING_ALLOWED_ORG_IDS: ${{ vars.EXTERNAL_ENGINE_PAIRING_ALLOWED_ORG_IDS }}", self.source)
        self.assertIn("STAGING_BILLING_LIFECYCLE_LEVER_ENABLED: ${{ vars.STAGING_BILLING_LIFECYCLE_LEVER_ENABLED }}", self.source)
        self.assertIn("STAGING_BILLING_LIFECYCLE_LEVER_ALLOWED_ORG_IDS: ${{ vars.STAGING_BILLING_LIFECYCLE_LEVER_ALLOWED_ORG_IDS }}", self.source)
        self.assertIn("STAGING_RECOVERY_LIFECYCLE_LEVER_ENABLED: ${{ vars.STAGING_RECOVERY_LIFECYCLE_LEVER_ENABLED }}", self.source)
        self.assertIn("STAGING_RECOVERY_LIFECYCLE_LEVER_ALLOWED_INSTANCE_IDS: ${{ vars.STAGING_RECOVERY_LIFECYCLE_LEVER_ALLOWED_INSTANCE_IDS }}", self.source)
        self.assertIn("STAGING_SMOKE_OWNER_ORGANIZATION_ID: ${{ vars.STAGING_SMOKE_OWNER_ORGANIZATION_ID }}", self.source)
        self.assertIn("STAGING_SMOKE_OWNER_INSTANCE_ID: ${{ vars.STAGING_SMOKE_OWNER_INSTANCE_ID }}", self.source)
        self.assertIn(
            "STAGING_RECOVERY_REQUIRED_ALERT_RECIPIENT: ${{ vars.STAGING_RECOVERY_REQUIRED_ALERT_RECIPIENT }}",
            self.source,
        )
        self.assertIn(
            "PRODUCTION_RECOVERY_REQUIRED_ALERT_RECIPIENT: ${{ vars.PRODUCTION_RECOVERY_REQUIRED_ALERT_RECIPIENT }}",
            self.source,
        )
        self.assertIn("scripts/deploy-managed-telemetry.sh", self.source)
        self.assertIn("scripts/deploy-managed-telemetry.sh --what-if", self.source)
        self.assertIn("Preview managed telemetry sink", self.source)
        self.assertIn("Deploy managed telemetry sink", self.source)
        self.assertIn("- telemetry", self.source)
        self.assertIn("app|infra|telemetry|build|promote", self.source)
        self.assertIn("env.DEPLOY_MODE != 'telemetry'", self.source)
        self.assertIn("env.DEPLOY_MODE == 'telemetry' && env.TELEMETRY_ACTION == 'what-if'", self.source)
        self.assertIn("env.DEPLOY_MODE == 'telemetry' && env.TELEMETRY_ACTION == 'create'", self.source)
        self.assertNotIn("env.DEPLOY_MODE == 'infra') && (env.TARGET_ENVIRONMENT == 'test'", self.source)
        self.assertIn("telemetry_action:", self.source)
        self.assertIn("confirm_environment:", self.source)
        self.assertIn('elif [ "$DEPLOY_MODE" = "app" ]; then', self.source)
        self.assertIn("scripts/deploy-azure-elsa-control.sh", self.source)
        deploy_start = self.source.index("      - name: Deploy API app")
        deploy_end = self.source.index("\n      - name:", deploy_start)
        deploy_step = self.source[deploy_start:deploy_end]
        self.assertIn('elif [ "$DEPLOY_MODE" = "app" ]; then', deploy_step)
        self.assertIn("docker build", deploy_step)
        self.assertIn("az webapp sitecontainers update", deploy_step)
        self.assertNotIn("telemetry", deploy_step.split('elif [ "$DEPLOY_MODE" = "app" ]; then', 1)[1][:400])
        self.assertIn("ManagedLifecycleTelemetry__AzureMonitor__Environment=staging", self.source)
        self.assertIn("ManagedLifecycleTelemetry__AzureMonitor__Environment=production", self.source)
        self.assertIn("MANAGED_LIFECYCLE_AZURE_MONITOR_ENABLED", self.source)
        self.assertIn("required+=(PRODUCTION_RECOVERY_REQUIRED_ALERT_RECIPIENT)", self.source)
        self.assertIn(
            """              required+=(
                STAGING_RECOVERY_REQUIRED_ALERT_RECIPIENT
                PRODUCTION_RECOVERY_REQUIRED_ALERT_RECIPIENT
              )""",
            self.source,
        )
        self.assertIn("Staging RecoveryRequired alerts must not use the production mailbox.", self.source)
        self.assertIn(
            "EXTERNAL_ENGINE_PAIRING_ALLOWED_ORG_IDS: ${{ steps.deployment-config.outputs.pairing_allowlist }}",
            self.source,
        )
        self.assertIn("scripts/apply-external-engine-pairing-settings.sh", self.source)
        self.assertIn("scripts/apply-staging-billing-lifecycle-lever-settings.sh", self.source)
        self.assertIn("scripts/apply-staging-recovery-lifecycle-lever-settings.sh", self.source)
        self.assertIn(
            "STAGING_BILLING_LIFECYCLE_LEVER_ALLOWED_ORG_IDS: ${{ steps.deployment-config.outputs.staging_billing_lever_allowlist }}",
            self.source,
        )
        self.assertIn(
            "STAGING_RECOVERY_LIFECYCLE_LEVER_ALLOWED_INSTANCE_IDS: ${{ steps.deployment-config.outputs.staging_recovery_lever_allowlist }}",
            self.source,
        )

    def test_cloud_account_issuer_accepts_exact_supabase_projects_only(self) -> None:
        check_start = self.source.index(
            "        run: |\n",
            self.source.index("      - name: Check deployment configuration"),
        )
        check_end = self.source.index("\n      - name:", check_start)
        check_script = dedent(self.source[check_start + len("        run: |\n") : check_end])
        check_script = check_script.replace("${{ github.event_name }}", "workflow_dispatch")

        with tempfile.NamedTemporaryFile() as github_env:
            base_environment = os.environ.copy()
            base_environment.update(
                {
                    "DEPLOY_MODE": "app",
                    "AZURE_CLIENT_ID": "00000000-0000-0000-0000-000000000001",
                    "AZURE_TENANT_ID": "00000000-0000-0000-0000-000000000002",
                    "AZURE_SUBSCRIPTION_ID": "00000000-0000-0000-0000-000000000003",
                    "AZURE_CONTAINER_REGISTRY_ENDPOINT": "test.azurecr.io",
                    "AZURE_ENV_NAME": "test",
                    "AZURE_LOCATION": "westeurope",
                    "AZURE_RESOURCE_GROUP": "rg-test",
                    "AZURE_WEBAPP_NAME": "test-api",
                    "TARGET_ENVIRONMENT": "production",
                    "GITHUB_ENV": github_env.name,
                }
            )
            base_environment.update(PRODUCTION_BILLING_FIXTURE)

            for issuer in (
                "https://jhrcnclyydzngnyvhdht.supabase.co/auth/v1",
                "https://abcdefghijklmnopqrst.supabase.co/auth/v1",
            ):
                with self.subTest(issuer=issuer), tempfile.NamedTemporaryFile() as output:
                    environment = base_environment | {
                        "CLOUD_ACCOUNT_ISSUER": issuer,
                        "EXPECTED_CLOUD_ACCOUNT_ISSUER": issuer,
                        "GITHUB_OUTPUT": output.name,
                    }
                    result = subprocess.run(
                        ["bash", "-c", check_script],
                        env=environment,
                        capture_output=True,
                        text=True,
                        check=False,
                    )
                    self.assertEqual(0, result.returncode, result.stderr)

            with tempfile.NamedTemporaryFile() as output:
                rejected = subprocess.run(
                    ["bash", "-c", check_script],
                    env=base_environment
                    | {
                        "CLOUD_ACCOUNT_ISSUER": "https://abcdefghijklmnopqrst.supabase.co/auth/v1/extra",
                        "GITHUB_OUTPUT": output.name,
                    },
                    capture_output=True,
                    text=True,
                    check=False,
                )
            self.assertNotEqual(0, rejected.returncode)
            self.assertIn("exact Supabase Auth issuer", rejected.stdout + rejected.stderr)

            with tempfile.NamedTemporaryFile() as output:
                mismatched = subprocess.run(
                    ["bash", "-c", check_script],
                    env=base_environment
                    | {
                        "CLOUD_ACCOUNT_ISSUER": "https://abcdefghijklmnopqrst.supabase.co/auth/v1",
                        "EXPECTED_CLOUD_ACCOUNT_ISSUER": "https://jhrcnclyydzngnyvhdht.supabase.co/auth/v1",
                        "GITHUB_OUTPUT": output.name,
                    },
                    capture_output=True,
                    text=True,
                    check=False,
                )
            self.assertNotEqual(0, mismatched.returncode)
            self.assertIn("approved environment issuer", mismatched.stdout + mismatched.stderr)

            with tempfile.NamedTemporaryFile() as output:
                disabled = subprocess.run(
                    ["bash", "-c", check_script],
                    env=base_environment
                    | {
                        "CLOUD_ACCOUNT_ISSUER": "",
                        "EXPECTED_CLOUD_ACCOUNT_ISSUER": "https://jhrcnclyydzngnyvhdht.supabase.co/auth/v1",
                        "GITHUB_OUTPUT": output.name,
                    },
                    capture_output=True,
                    text=True,
                    check=False,
                )
            self.assertEqual(0, disabled.returncode, disabled.stderr)

    def _deployment_config_script(self) -> str:
        check_start = self.source.index(
            "        run: |\n",
            self.source.index("      - name: Check deployment configuration"),
        )
        check_end = self.source.index("\n      - name:", check_start)
        check_script = dedent(self.source[check_start + len("        run: |\n") : check_end])
        return check_script.replace("${{ github.event_name }}", "workflow_dispatch")

    def test_pairing_allowlist_is_staging_only_and_excludes_the_smoke_owner_org(self) -> None:
        check_script = self._deployment_config_script()
        rehearsal = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"
        smoke = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"
        base = {
            "DEPLOY_MODE": "app",
            "AZURE_CLIENT_ID": "00000000-0000-0000-0000-000000000001",
            "AZURE_TENANT_ID": "00000000-0000-0000-0000-000000000002",
            "AZURE_SUBSCRIPTION_ID": "00000000-0000-0000-0000-000000000003",
            "AZURE_CONTAINER_REGISTRY_ENDPOINT": "test.azurecr.io",
            "AZURE_ENV_NAME": "test",
            "AZURE_LOCATION": "westeurope",
            "AZURE_RESOURCE_GROUP": "rg-test",
            "AZURE_WEBAPP_NAME": "test-api",
        }

        def run_check(**extra: str) -> tuple[subprocess.CompletedProcess[str], str, str]:
            with tempfile.NamedTemporaryFile() as output, tempfile.NamedTemporaryFile() as github_env:
                environment = os.environ.copy()
                environment.update(base)
                environment.update(extra)
                if environment.get("TARGET_ENVIRONMENT") == "production":
                    environment.update(PRODUCTION_BILLING_FIXTURE)
                environment["GITHUB_OUTPUT"] = output.name
                environment["GITHUB_ENV"] = github_env.name
                result = subprocess.run(
                    ["bash", "-c", check_script],
                    env=environment,
                    capture_output=True,
                    text=True,
                    check=False,
                )
                return result, Path(output.name).read_text(), Path(github_env.name).read_text()

        staging_stripe = {
            "ELSA_CLOUD_STAGING_ORIGIN": "https://staging.example.test",
            "STRIPE_HOSTED_PRICE_ID": "price_test",
            "STRIPE_TEST_SECRET_KEY": "sk_test_fixture",
            "STRIPE_TEST_WEBHOOK_SIGNING_SECRET": "whsec_fixture",
        }
        second = "cccccccc-cccc-cccc-cccc-cccccccccccc"

        production_set, _, _ = run_check(
            TARGET_ENVIRONMENT="production",
            EXTERNAL_ENGINE_PAIRING_ALLOWED_ORG_IDS=rehearsal,
        )
        self.assertNotEqual(0, production_set.returncode)
        self.assertIn("must be unset", production_set.stdout + production_set.stderr)
        self.assertNotIn(rehearsal, production_set.stdout + production_set.stderr)

        production_empty, _, _ = run_check(TARGET_ENVIRONMENT="production", EXTERNAL_ENGINE_PAIRING_ALLOWED_ORG_IDS="")
        self.assertEqual(0, production_empty.returncode, production_empty.stderr)

        staging_includes_smoke, _, _ = run_check(
            TARGET_ENVIRONMENT="test",
            EXTERNAL_ENGINE_PAIRING_ALLOWED_ORG_IDS=f"{rehearsal},{smoke}",
            STAGING_SMOKE_OWNER_ORGANIZATION_ID=smoke,
            **staging_stripe,
        )
        self.assertNotEqual(0, staging_includes_smoke.returncode)
        combined = staging_includes_smoke.stdout + staging_includes_smoke.stderr
        self.assertIn("must not include the staging Hosted smoke owner organization", combined)
        self.assertNotIn(rehearsal, combined)
        self.assertNotIn(smoke, combined)

        staging_missing_smoke_id, _, _ = run_check(
            TARGET_ENVIRONMENT="test",
            EXTERNAL_ENGINE_PAIRING_ALLOWED_ORG_IDS=rehearsal,
            STAGING_SMOKE_OWNER_ORGANIZATION_ID="",
            **staging_stripe,
        )
        self.assertNotEqual(0, staging_missing_smoke_id.returncode)
        self.assertIn("STAGING_SMOKE_OWNER_ORGANIZATION_ID", staging_missing_smoke_id.stdout + staging_missing_smoke_id.stderr)

        for raw in (f"{rehearsal},", f"{rehearsal},,{second}", f"{rehearsal},   ,{second}", "   ", " , "):
            with self.subTest(raw=raw):
                rejected, _, _ = run_check(
                    TARGET_ENVIRONMENT="test",
                    EXTERNAL_ENGINE_PAIRING_ALLOWED_ORG_IDS=raw,
                    STAGING_SMOKE_OWNER_ORGANIZATION_ID=smoke,
                    **staging_stripe,
                )
                self.assertNotEqual(0, rejected.returncode)
                self.assertIn("empty organization id", rejected.stdout + rejected.stderr)
                self.assertNotIn(rehearsal, rejected.stdout + rejected.stderr)
                self.assertNotIn(second, rejected.stdout + rejected.stderr)

        staging_ok, output, github_env = run_check(
            TARGET_ENVIRONMENT="test",
            EXTERNAL_ENGINE_PAIRING_ALLOWED_ORG_IDS=f" {rehearsal} , {second} ",
            STAGING_SMOKE_OWNER_ORGANIZATION_ID=smoke,
            **staging_stripe,
        )
        self.assertEqual(0, staging_ok.returncode, staging_ok.stdout + staging_ok.stderr)
        self.assertIn("organization id(s).", staging_ok.stdout)
        self.assertNotIn(rehearsal, staging_ok.stdout + staging_ok.stderr)
        self.assertNotIn(second, staging_ok.stdout + staging_ok.stderr)
        self.assertIn(f"pairing_allowlist={rehearsal},{second}", output)
        self.assertIn("pairing_allowlist_count=2", output)
        self.assertIn(f"EXTERNAL_ENGINE_PAIRING_ALLOWED_ORG_IDS={rehearsal},{second}", github_env)

        self.assertIn('pairing_allowlist_prefix=', self.source)
        helper = (ROOT / "scripts" / "apply-external-engine-pairing-settings.sh").read_text()
        self.assertIn("Pairing allowlist app setting count: before=", helper)
        self.assertNotIn("echo \"$EXTERNAL_ENGINE_PAIRING_ALLOWED_ORG_IDS\"", self.source)
        self.assertNotIn("echo '${EXTERNAL_ENGINE_PAIRING_ALLOWED_ORG_IDS}'", self.source)
        self.assertNotIn("echo \"$EXTERNAL_ENGINE_PAIRING_ALLOWED_ORG_IDS\"", helper)
        self.assertNotIn("echo '${EXTERNAL_ENGINE_PAIRING_ALLOWED_ORG_IDS}'", helper)

    def test_staging_billing_lifecycle_lever_is_staging_only_and_excludes_the_smoke_owner_org(self) -> None:
        check_script = self._deployment_config_script()
        rehearsal = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"
        smoke = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"
        base = {
            "DEPLOY_MODE": "app",
            "AZURE_CLIENT_ID": "00000000-0000-0000-0000-000000000001",
            "AZURE_TENANT_ID": "00000000-0000-0000-0000-000000000002",
            "AZURE_SUBSCRIPTION_ID": "00000000-0000-0000-0000-000000000003",
            "AZURE_CONTAINER_REGISTRY_ENDPOINT": "test.azurecr.io",
            "AZURE_ENV_NAME": "test",
            "AZURE_LOCATION": "westeurope",
            "AZURE_RESOURCE_GROUP": "rg-test",
            "AZURE_WEBAPP_NAME": "test-api",
        }

        def run_check(**extra: str) -> tuple[subprocess.CompletedProcess[str], str, str]:
            with tempfile.NamedTemporaryFile() as output, tempfile.NamedTemporaryFile() as github_env:
                environment = os.environ.copy()
                environment.update(base)
                environment.update(extra)
                if environment.get("TARGET_ENVIRONMENT") == "production":
                    environment.update(PRODUCTION_BILLING_FIXTURE)
                environment["GITHUB_OUTPUT"] = output.name
                environment["GITHUB_ENV"] = github_env.name
                result = subprocess.run(
                    ["bash", "-c", check_script],
                    env=environment,
                    capture_output=True,
                    text=True,
                    check=False,
                )
                return result, Path(output.name).read_text(), Path(github_env.name).read_text()

        staging_stripe = {
            "ELSA_CLOUD_STAGING_ORIGIN": "https://staging.example.test",
            "STRIPE_HOSTED_PRICE_ID": "price_test",
            "STRIPE_TEST_SECRET_KEY": "sk_test_fixture",
            "STRIPE_TEST_WEBHOOK_SIGNING_SECRET": "whsec_fixture",
        }

        production_enabled, _, _ = run_check(
            TARGET_ENVIRONMENT="production",
            AZURE_ENV_NAME="valence-control-staging",
            STAGING_BILLING_LIFECYCLE_LEVER_ENABLED="true",
        )
        self.assertNotEqual(0, production_enabled.returncode)
        self.assertIn("must be unset", production_enabled.stdout + production_enabled.stderr)

        production_allowlist, _, _ = run_check(
            TARGET_ENVIRONMENT="production",
            STAGING_BILLING_LIFECYCLE_LEVER_ALLOWED_ORG_IDS=rehearsal,
        )
        self.assertNotEqual(0, production_allowlist.returncode)
        self.assertIn("must be unset", production_allowlist.stdout + production_allowlist.stderr)
        self.assertNotIn(rehearsal, production_allowlist.stdout + production_allowlist.stderr)

        production_false, _, _ = run_check(
            TARGET_ENVIRONMENT="production",
            STAGING_BILLING_LIFECYCLE_LEVER_ENABLED="false",
        )
        self.assertNotEqual(0, production_false.returncode)
        self.assertIn("must be unset", production_false.stdout + production_false.stderr)

        production_empty, production_output, production_env = run_check(
            TARGET_ENVIRONMENT="production",
            STAGING_BILLING_LIFECYCLE_LEVER_ENABLED="",
            STAGING_BILLING_LIFECYCLE_LEVER_ALLOWED_ORG_IDS="",
        )
        self.assertEqual(0, production_empty.returncode, production_empty.stderr)
        self.assertRegex(production_output, r"(?m)^staging_billing_lever_enabled=$")
        self.assertRegex(production_output, r"(?m)^staging_billing_lever_allowlist=$")
        self.assertRegex(production_output, r"(?m)^staging_billing_lever_allowlist_count=0$")
        self.assertNotIn("staging_billing_lever_enabled=false", production_output)
        self.assertNotIn("staging_billing_lever_enabled=true", production_output)
        self.assertRegex(production_env, r"(?m)^STAGING_BILLING_LIFECYCLE_LEVER_ENABLED=$")
        self.assertRegex(production_env, r"(?m)^STAGING_BILLING_LIFECYCLE_LEVER_ALLOWED_ORG_IDS=$")
        self.assertNotIn("STAGING_BILLING_LIFECYCLE_LEVER_ENABLED=false", production_env)
        self.assertNotIn("STAGING_BILLING_LIFECYCLE_LEVER_ENABLED=true", production_env)

        staging_includes_smoke, _, _ = run_check(
            TARGET_ENVIRONMENT="test",
            STAGING_BILLING_LIFECYCLE_LEVER_ENABLED="true",
            STAGING_BILLING_LIFECYCLE_LEVER_ALLOWED_ORG_IDS=f"{rehearsal},{smoke}",
            STAGING_SMOKE_OWNER_ORGANIZATION_ID=smoke,
            **staging_stripe,
        )
        self.assertNotEqual(0, staging_includes_smoke.returncode)
        combined = staging_includes_smoke.stdout + staging_includes_smoke.stderr
        self.assertIn("must not include the staging Hosted smoke owner organization", combined)
        self.assertNotIn(rehearsal, combined)
        self.assertNotIn(smoke, combined)

        staging_ok, output, github_env = run_check(
            TARGET_ENVIRONMENT="test",
            AZURE_ENV_NAME="valence-control-staging",
            AZURE_RESOURCE_GROUP="rg-valence-control-staging",
            STAGING_BILLING_LIFECYCLE_LEVER_ENABLED="true",
            STAGING_BILLING_LIFECYCLE_LEVER_ALLOWED_ORG_IDS=f" {rehearsal} ",
            STAGING_SMOKE_OWNER_ORGANIZATION_ID=smoke,
            **staging_stripe,
        )
        self.assertEqual(0, staging_ok.returncode, staging_ok.stdout + staging_ok.stderr)
        self.assertNotIn(rehearsal, staging_ok.stdout + staging_ok.stderr)
        self.assertIn("staging_billing_lever_enabled=true", output)
        self.assertIn(f"staging_billing_lever_allowlist={rehearsal}", output)
        self.assertIn("staging_billing_lever_allowlist_count=1", output)
        self.assertIn("STAGING_BILLING_LIFECYCLE_LEVER_ENABLED=true", github_env)
        self.assertIn(f"STAGING_BILLING_LIFECYCLE_LEVER_ALLOWED_ORG_IDS={rehearsal}", github_env)
        helper = (ROOT / "scripts" / "apply-staging-billing-lifecycle-lever-settings.sh").read_text()
        self.assertIn("Staging billing lifecycle lever app setting count: before=", helper)
        self.assertNotIn("echo \"$STAGING_BILLING_LIFECYCLE_LEVER_ALLOWED_ORG_IDS\"", self.source)
        self.assertNotIn("echo \"$STAGING_BILLING_LIFECYCLE_LEVER_ALLOWED_ORG_IDS\"", helper)

    def test_staging_recovery_lifecycle_lever_is_staging_only_and_excludes_the_smoke_owner_instance(self) -> None:
        check_script = self._deployment_config_script()
        rehearsal = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"
        smoke = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"
        billing_org = "cccccccc-cccc-cccc-cccc-cccccccccccc"
        base = {
            "DEPLOY_MODE": "app",
            "AZURE_CLIENT_ID": "00000000-0000-0000-0000-000000000001",
            "AZURE_TENANT_ID": "00000000-0000-0000-0000-000000000002",
            "AZURE_SUBSCRIPTION_ID": "00000000-0000-0000-0000-000000000003",
            "AZURE_CONTAINER_REGISTRY_ENDPOINT": "test.azurecr.io",
            "AZURE_ENV_NAME": "test",
            "AZURE_LOCATION": "westeurope",
            "AZURE_RESOURCE_GROUP": "rg-test",
            "AZURE_WEBAPP_NAME": "test-api",
        }

        def run_check(**extra: str) -> tuple[subprocess.CompletedProcess[str], str, str]:
            with tempfile.NamedTemporaryFile() as output, tempfile.NamedTemporaryFile() as github_env:
                environment = os.environ.copy()
                environment.update(base)
                environment.update(extra)
                if environment.get("TARGET_ENVIRONMENT") == "production":
                    environment.update(PRODUCTION_BILLING_FIXTURE)
                environment["GITHUB_OUTPUT"] = output.name
                environment["GITHUB_ENV"] = github_env.name
                result = subprocess.run(
                    ["bash", "-c", check_script],
                    env=environment,
                    capture_output=True,
                    text=True,
                    check=False,
                )
                return result, Path(output.name).read_text(), Path(github_env.name).read_text()

        staging_stripe = {
            "ELSA_CLOUD_STAGING_ORIGIN": "https://staging.example.test",
            "STRIPE_HOSTED_PRICE_ID": "price_test",
            "STRIPE_TEST_SECRET_KEY": "sk_test_fixture",
            "STRIPE_TEST_WEBHOOK_SIGNING_SECRET": "whsec_fixture",
        }

        production_enabled, _, _ = run_check(
            TARGET_ENVIRONMENT="production",
            AZURE_ENV_NAME="valence-control-staging",
            STAGING_RECOVERY_LIFECYCLE_LEVER_ENABLED="true",
        )
        self.assertNotEqual(0, production_enabled.returncode)
        self.assertIn("must be unset", production_enabled.stdout + production_enabled.stderr)

        production_allowlist, _, _ = run_check(
            TARGET_ENVIRONMENT="production",
            STAGING_RECOVERY_LIFECYCLE_LEVER_ALLOWED_INSTANCE_IDS=rehearsal,
        )
        self.assertNotEqual(0, production_allowlist.returncode)
        self.assertIn("must be unset", production_allowlist.stdout + production_allowlist.stderr)
        self.assertNotIn(rehearsal, production_allowlist.stdout + production_allowlist.stderr)

        production_empty, production_output, production_env = run_check(
            TARGET_ENVIRONMENT="production",
            STAGING_RECOVERY_LIFECYCLE_LEVER_ENABLED="",
            STAGING_RECOVERY_LIFECYCLE_LEVER_ALLOWED_INSTANCE_IDS="",
        )
        self.assertEqual(0, production_empty.returncode, production_empty.stderr)
        self.assertRegex(production_output, r"(?m)^staging_recovery_lever_enabled=$")
        self.assertRegex(production_output, r"(?m)^staging_recovery_lever_allowlist=$")
        self.assertRegex(production_output, r"(?m)^staging_recovery_lever_allowlist_count=0$")
        self.assertNotIn("staging_recovery_lever_enabled=false", production_output)
        self.assertNotIn("staging_recovery_lever_enabled=true", production_output)

        billing_only, billing_output, _ = run_check(
            TARGET_ENVIRONMENT="test",
            STAGING_BILLING_LIFECYCLE_LEVER_ENABLED="true",
            STAGING_BILLING_LIFECYCLE_LEVER_ALLOWED_ORG_IDS=billing_org,
            STAGING_SMOKE_OWNER_ORGANIZATION_ID=smoke,
            **staging_stripe,
        )
        self.assertEqual(0, billing_only.returncode, billing_only.stdout + billing_only.stderr)
        self.assertIn("staging_billing_lever_enabled=true", billing_output)
        self.assertRegex(billing_output, r"(?m)^staging_recovery_lever_enabled=$")
        self.assertRegex(billing_output, r"(?m)^staging_recovery_lever_allowlist=$")
        self.assertRegex(billing_output, r"(?m)^staging_recovery_lever_allowlist_count=0$")

        staging_includes_smoke, _, _ = run_check(
            TARGET_ENVIRONMENT="test",
            STAGING_RECOVERY_LIFECYCLE_LEVER_ENABLED="true",
            STAGING_RECOVERY_LIFECYCLE_LEVER_ALLOWED_INSTANCE_IDS=f"{rehearsal},{smoke}",
            STAGING_SMOKE_OWNER_INSTANCE_ID=smoke,
            **staging_stripe,
        )
        self.assertNotEqual(0, staging_includes_smoke.returncode)
        combined = staging_includes_smoke.stdout + staging_includes_smoke.stderr
        self.assertIn("must not include the staging Hosted smoke owner instance", combined)
        self.assertNotIn(rehearsal, combined)
        self.assertNotIn(smoke, combined)

        staging_ok, output, github_env = run_check(
            TARGET_ENVIRONMENT="test",
            AZURE_ENV_NAME="valence-control-staging",
            AZURE_RESOURCE_GROUP="rg-valence-control-staging",
            STAGING_RECOVERY_LIFECYCLE_LEVER_ENABLED="true",
            STAGING_RECOVERY_LIFECYCLE_LEVER_ALLOWED_INSTANCE_IDS=f" {rehearsal} ",
            STAGING_SMOKE_OWNER_INSTANCE_ID=smoke,
            **staging_stripe,
        )
        self.assertEqual(0, staging_ok.returncode, staging_ok.stdout + staging_ok.stderr)
        self.assertNotIn(rehearsal, staging_ok.stdout + staging_ok.stderr)
        self.assertIn("staging_recovery_lever_enabled=true", output)
        self.assertIn(f"staging_recovery_lever_allowlist={rehearsal}", output)
        self.assertIn("staging_recovery_lever_allowlist_count=1", output)
        self.assertRegex(output, r"(?m)^staging_billing_lever_enabled=$")
        self.assertIn("STAGING_RECOVERY_LIFECYCLE_LEVER_ENABLED=true", github_env)
        self.assertIn(f"STAGING_RECOVERY_LIFECYCLE_LEVER_ALLOWED_INSTANCE_IDS={rehearsal}", github_env)
        helper = (ROOT / "scripts" / "apply-staging-recovery-lifecycle-lever-settings.sh").read_text()
        self.assertIn("Staging recovery lifecycle lever app setting count: before=", helper)
        self.assertIn("scripts/lib/staging-lever-target.sh", helper)
        self.assertNotIn("echo \"$STAGING_RECOVERY_LIFECYCLE_LEVER_ALLOWED_INSTANCE_IDS\"", self.source)
        self.assertNotIn("echo \"$STAGING_RECOVERY_LIFECYCLE_LEVER_ALLOWED_INSTANCE_IDS\"", helper)

    def test_staging_infra_requires_a_provisioner_identity(self) -> None:
        check_start = self.source.index("        run: |\n", self.source.index("      - name: Check deployment configuration"))
        check_end = self.source.index("\n      - name:", check_start)
        check_script = dedent(self.source[check_start + len("        run: |\n") : check_end])
        check_script = check_script.replace("${{ github.event_name }}", "workflow_dispatch")
        environment = os.environ.copy() | {
            "TARGET_ENVIRONMENT": "test", "DEPLOY_MODE": "infra",
            "AZURE_CLIENT_ID": "00000000-0000-0000-0000-000000000001",
            "AZURE_TENANT_ID": "00000000-0000-0000-0000-000000000002",
            "AZURE_SUBSCRIPTION_ID": "00000000-0000-0000-0000-000000000003",
            "AZURE_CONTAINER_REGISTRY_ENDPOINT": "test.azurecr.io",
            "AZURE_ENV_NAME": "test", "AZURE_LOCATION": "westeurope",
            "AZURE_RESOURCE_GROUP": "rg-test", "AZURE_WEBAPP_NAME": "test-api",
            "ADMIN_API_KEY": "test-only", "BUILDER_CLIENT_API_KEY": "test-only",
            "CONTROL_ENTRA_CLIENT_ID": "00000000-0000-0000-0000-000000000004",
            "CONTROL_ENTRA_CLIENT_SECRET": "test-only",
            "CONTROL_ENTRA_TENANT_ID": "00000000-0000-0000-0000-000000000005",
            "ELSA_CLOUD_STAGING_ORIGIN": "https://staging.example.test",
            "STRIPE_HOSTED_PRICE_ID": "price_test", "STRIPE_TEST_SECRET_KEY": "sk_test_fixture",
            "STRIPE_TEST_WEBHOOK_SIGNING_SECRET": "whsec_fixture",
        }
        with tempfile.NamedTemporaryFile() as output, tempfile.NamedTemporaryFile() as github_env:
            environment["GITHUB_OUTPUT"] = output.name
            environment["GITHUB_ENV"] = github_env.name
            missing = subprocess.run(["bash", "-c", check_script], env=environment,
                                     capture_output=True, text=True, check=False)
            self.assertNotEqual(0, missing.returncode)
            self.assertIn("AZURE_PROVISIONER_IDENTITY_ID", missing.stdout + missing.stderr)
            environment["AZURE_PROVISIONER_IDENTITY_ID"] = "staging-identity"
            configured = subprocess.run(["bash", "-c", check_script], env=environment,
                                        capture_output=True, text=True, check=False)
            self.assertEqual(0, configured.returncode, configured.stdout + configured.stderr)
            self.assertNotIn("STAGING_RECOVERY_REQUIRED_ALERT_RECIPIENT", configured.stdout + configured.stderr)

    def test_telemetry_mode_is_separately_guarded_from_app_and_full_infra(self) -> None:
        check_start = self.source.index("        run: |\n", self.source.index("      - name: Check deployment configuration"))
        check_end = self.source.index("\n      - name:", check_start)
        check_script = dedent(self.source[check_start + len("        run: |\n") : check_end])
        check_script = check_script.replace("${{ github.event_name }}", "workflow_dispatch")
        environment = os.environ.copy() | {
            "TARGET_ENVIRONMENT": "test",
            "DEPLOY_MODE": "telemetry",
            "AZURE_CLIENT_ID": "00000000-0000-0000-0000-000000000001",
            "AZURE_TENANT_ID": "00000000-0000-0000-0000-000000000002",
            "AZURE_SUBSCRIPTION_ID": "00000000-0000-0000-0000-000000000003",
            "AZURE_LOCATION": "westeurope",
            "AZURE_RESOURCE_GROUP": "rg-test",
            "MANAGED_TELEMETRY_WORKSPACE_NAME": "law-staging",
            "MANAGED_TELEMETRY_APPLICATION_INSIGHTS_NAME": "appi-staging",
            "MANAGED_TELEMETRY_API_IDENTITY_NAME": "id-api-staging",
            "MANAGED_TELEMETRY_API_IDENTITY_RESOURCE_GROUP": "rg-test",
            "STAGING_RECOVERY_REQUIRED_ALERT_RECIPIENT": "staging-ops@example.test",
            "PRODUCTION_RECOVERY_REQUIRED_ALERT_RECIPIENT": "prod-ops@example.test",
            "TELEMETRY_ACTION": "what-if",
        }
        with tempfile.NamedTemporaryFile() as output, tempfile.NamedTemporaryFile() as github_env:
            environment["GITHUB_OUTPUT"] = output.name
            environment["GITHUB_ENV"] = github_env.name
            configured = subprocess.run(
                ["bash", "-c", check_script],
                env=environment,
                capture_output=True,
                text=True,
                check=False,
            )
            self.assertEqual(0, configured.returncode, configured.stdout + configured.stderr)
            self.assertIn("deploy_configured=true", Path(output.name).read_text())

            environment["STAGING_RECOVERY_REQUIRED_ALERT_RECIPIENT"] = "prod-ops@example.test"
            reused = subprocess.run(
                ["bash", "-c", check_script],
                env=environment,
                capture_output=True,
                text=True,
                check=False,
            )
            self.assertNotEqual(0, reused.returncode)
            self.assertIn("must not use the production mailbox", reused.stdout + reused.stderr)

            environment["STAGING_RECOVERY_REQUIRED_ALERT_RECIPIENT"] = "staging-ops@example.test"
            environment["TELEMETRY_ACTION"] = "create"
            create_without_confirm = subprocess.run(
                ["bash", "-c", check_script],
                env=environment,
                capture_output=True,
                text=True,
                check=False,
            )
            self.assertNotEqual(0, create_without_confirm.returncode)
            self.assertIn(
                "confirm_environment to equal the target environment name",
                create_without_confirm.stdout + create_without_confirm.stderr,
            )
            environment["CONFIRM_ENVIRONMENT"] = "test"
            create_confirmed = subprocess.run(
                ["bash", "-c", check_script],
                env=environment,
                capture_output=True,
                text=True,
                check=False,
            )
            self.assertEqual(0, create_confirmed.returncode, create_confirmed.stdout + create_confirmed.stderr)

            environment["TELEMETRY_ACTION"] = "what-if"
            environment["CONFIRM_ENVIRONMENT"] = ""
            environment["TARGET_ENVIRONMENT"] = "production"
            production_unconfirmed = subprocess.run(
                ["bash", "-c", check_script],
                env=environment,
                capture_output=True,
                text=True,
                check=False,
            )
            self.assertNotEqual(0, production_unconfirmed.returncode)
            self.assertIn(
                "refuses the default production target",
                production_unconfirmed.stdout + production_unconfirmed.stderr,
            )
            environment["CONFIRM_ENVIRONMENT"] = "production"
            production_confirmed = subprocess.run(
                ["bash", "-c", check_script],
                env=environment,
                capture_output=True,
                text=True,
                check=False,
            )
            self.assertEqual(
                0,
                production_confirmed.returncode,
                production_confirmed.stdout + production_confirmed.stderr,
            )

            environment["TARGET_ENVIRONMENT"] = "development"
            environment["CONFIRM_ENVIRONMENT"] = ""
            rejected = subprocess.run(
                ["bash", "-c", check_script],
                env=environment,
                capture_output=True,
                text=True,
                check=False,
            )
            self.assertNotEqual(0, rejected.returncode)
            self.assertIn("Telemetry-only deploy is limited to test or production", rejected.stdout + rejected.stderr)

        self.assertIn("env.DEPLOY_MODE != 'build' && env.DEPLOY_MODE != 'telemetry'", self.source)
        self.assertNotIn(
            "scripts/deploy-azure-elsa-control.sh",
            self.source[self.source.index("      - name: Preview managed telemetry sink"):],
        )
        preview = self.source[
            self.source.index("      - name: Preview managed telemetry sink"):
            self.source.index("      - name: Deploy managed telemetry sink")
        ]
        self.assertIn("scripts/deploy-managed-telemetry.sh --what-if", preview)
        self.assertIn("env.TELEMETRY_ACTION == 'what-if'", preview)
        self.assertNotIn("env.DEPLOY_MODE == 'infra'", preview)
        self.assertNotIn("scripts/deploy-azure-elsa-control.sh", preview)
        deploy = self.source[
            self.source.index("      - name: Deploy managed telemetry sink"):
            self.source.index("      - name: Reconcile staging Stripe billing")
        ]
        self.assertIn("env.TELEMETRY_ACTION == 'create'", deploy)
        self.assertNotIn("env.DEPLOY_MODE == 'infra'", deploy)

    def test_app_mode_step_path_matches_last_green_app_run(self) -> None:
        """App mode must keep the executed path of run 37161715192."""

        steps = []
        for raw in self.source.split("\n      - name: ")[1:]:
            lines = raw.splitlines()
            name = lines[0].strip()
            if_line = ""
            for line in lines[1:]:
                stripped = line.strip()
                if stripped.startswith(("run:", "uses:", "with:", "env:", "working-directory:")):
                    break
                if stripped.startswith("if:"):
                    if_line = stripped
                    break
            steps.append((name, if_line))

        names = [name for name, _ in steps]
        self.assertEqual(
            names[:12],
            [
                "Checkout",
                "Setup .NET SDK",
                "Setup Node.js",
                "Check deployment configuration",
                "Require main ref for Azure mutation",
                "Audit staging Stripe resources before deployment",
                "Restore",
                "Build AppHost",
                "Test API",
                "Build and test console",
                "Log in Azure CLI",
                "Check immutable promotion inputs",
            ],
        )
        self.assertIn("Capture current API deployment", names)
        self.assertIn("Deploy API app", names)
        self.assertIn("Preview managed telemetry sink", names)
        self.assertIn("Deploy managed telemetry sink", names)
        self.assertIn("Reconcile staging Stripe billing", names)
        self.assertIn("Verify deployed API health", names)
        self.assertLess(names.index("Deploy API app"), names.index("Preview managed telemetry sink"))
        self.assertLess(names.index("Preview managed telemetry sink"), names.index("Deploy managed telemetry sink"))
        self.assertLess(names.index("Deploy managed telemetry sink"), names.index("Reconcile staging Stripe billing"))

        by_name = dict(steps)
        self.assertIn("env.DEPLOY_MODE != 'telemetry'", by_name["Setup .NET SDK"])
        self.assertIn("env.DEPLOY_MODE != 'telemetry'", by_name["Deploy API app"])
        self.assertIn("env.DEPLOY_MODE == 'telemetry'", by_name["Preview managed telemetry sink"])
        self.assertIn("env.TELEMETRY_ACTION == 'what-if'", by_name["Preview managed telemetry sink"])
        self.assertNotIn("infra", by_name["Preview managed telemetry sink"])
        self.assertIn("env.DEPLOY_MODE == 'telemetry'", by_name["Deploy managed telemetry sink"])
        self.assertIn("env.TELEMETRY_ACTION == 'create'", by_name["Deploy managed telemetry sink"])
        self.assertNotIn("infra", by_name["Deploy managed telemetry sink"])
        rollback_if = by_name["Restore previous API deployment after deployment, configuration, or health failure"]
        self.assertNotIn("managed-telemetry", rollback_if)
        self.assertIn("steps.deploy-api.outcome == 'failure'", rollback_if)
        self.assertIn("steps.health-gate.outcome == 'failure'", rollback_if)
        # Deliberate pin update from run 37161715192: app-mode step names and
        # if-conditions stay the same. The only app-path mutation-order change
        # is inside Deploy API app — ExpectedMode is written before the image
        # switch unless Azure already has the target value. Prior
        # existence/value/slot metadata is captured first and restored or
        # deleted on rollback. An unreadable prior state aborts before
        # mutation. Telemetry still never appears in the rollback condition.
        self.assertNotIn("TELEMETRY_FAILURE_ROLLS_BACK_API", self.source)
        self.assertIn("never rolls back the API in", self.source)
        self.assertIn("production, test, or development", self.source)
        self.assertIn('if [ "$current_expected_mode" = "$BILLING_EXPECTED_MODE" ]; then', self.source)
        self.assertNotIn("later, lower-priority change", self.source)
        self.assertIn("Could not read Billing__Stripe__ExpectedMode; refusing to mutate the Web App.", self.source)
        self.assertIn("billing_expected_mode_present=", self.source)
        self.assertIn("PREVIOUS_BILLING_EXPECTED_MODE_PRESENT", self.source)
        self.assertIn("could not restore the previous Billing__Stripe__ExpectedMode.", self.source)
        self.assertIn("could not remove the newly introduced Billing__Stripe__ExpectedMode.", self.source)
        self.assertIn("PRODUCTION_STRIPE_OUTCOME", self.source)
        self.assertIn("production Stripe audit", self.source)
        expected_mode_list = self.source[
            self.source.index('if ! billing_expected_mode_record="$(az webapp config appsettings list') :
            self.source.index("echo \"::error::Could not read Billing__Stripe__ExpectedMode")
        ]
        self.assertNotIn("|| true", expected_mode_list)
        deploy_start = self.source.index("      - name: Deploy API app")
        deploy_end = self.source.index("\n      - name:", deploy_start + 1)
        deploy_step = self.source[deploy_start:deploy_end]
        app_branch = deploy_step[
            deploy_step.index('elif [ "$DEPLOY_MODE" = "app" ]; then') :
            deploy_step.index('elif [ "$DEPLOY_MODE" = "promote" ]; then')
        ]
        self.assertLess(
            app_branch.index("write_billing_expected_mode"),
            app_branch.index("az webapp sitecontainers update"),
        )
        self.assertLess(
            app_branch.index("write_billing_expected_mode"),
            app_branch.index("az webapp config container set"),
        )
        promote_branch = deploy_step[
            deploy_step.index('elif [ "$DEPLOY_MODE" = "promote" ]; then') :
            deploy_step.index('echo "::error::The selected deployment mode cannot mutate the Web App."')
        ]
        self.assertLess(
            promote_branch.index("write_billing_expected_mode"),
            promote_branch.index("az webapp sitecontainers update"),
        )
        self.assertLess(
            promote_branch.index("write_billing_expected_mode"),
            promote_branch.index("az webapp config container set"),
        )
        infra_branch = deploy_step[
            deploy_step.index('if [ "$DEPLOY_MODE" = "infra" ]; then') :
            deploy_step.index('elif [ "$DEPLOY_MODE" = "app" ]; then')
        ]
        self.assertNotIn("write_billing_expected_mode", infra_branch)
        self.assertIn("Development is not a billing target.", self.source)
        self.assertIn("Enabling development billing is unsupported.", self.source)

    def test_development_target_leaves_billing_expected_mode_unset(self) -> None:
        check_start = self.source.index("        run: |\n", self.source.index("      - name: Check deployment configuration"))
        check_end = self.source.index("\n      - name:", check_start)
        check_script = dedent(self.source[check_start + len("        run: |\n") : check_end])
        check_script = check_script.replace("${{ github.event_name }}", "workflow_dispatch")
        environment = os.environ.copy() | {
            "TARGET_ENVIRONMENT": "development",
            "DEPLOY_MODE": "app",
            "AZURE_CLIENT_ID": "00000000-0000-0000-0000-000000000001",
            "AZURE_TENANT_ID": "00000000-0000-0000-0000-000000000002",
            "AZURE_SUBSCRIPTION_ID": "00000000-0000-0000-0000-000000000003",
            "AZURE_CONTAINER_REGISTRY_ENDPOINT": "dev.azurecr.io",
            "AZURE_ENV_NAME": "development",
            "AZURE_LOCATION": "westeurope",
            "AZURE_RESOURCE_GROUP": "rg-development",
            "AZURE_WEBAPP_NAME": "dev-api",
        }
        with tempfile.NamedTemporaryFile() as output, tempfile.NamedTemporaryFile() as github_env:
            environment["GITHUB_OUTPUT"] = output.name
            environment["GITHUB_ENV"] = github_env.name
            result = subprocess.run(
                ["bash", "-c", check_script],
                env=environment,
                capture_output=True,
                text=True,
                check=False,
            )
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
            self.assertIn("billing_expected_mode=\n", Path(output.name).read_text())

    def test_rollback_message_names_the_failed_trigger(self) -> None:
        start = self.source.index(
            "      - name: Restore previous API deployment after deployment, configuration, or health failure"
        )
        run_start = self.source.index("        run: |\n", start) + len("        run: |\n")
        end = self.source.find("\n      - name:", run_start)
        rollback_script = dedent(self.source[run_start:end])

        cases = (
            ({"HEALTH_GATE_OUTCOME": "failure"}, "API health gate failed"),
            ({"PRODUCTION_STRIPE_OUTCOME": "failure"}, "production Stripe audit failed"),
            ({"STAGING_STRIPE_OUTCOME": "failure"}, "staging Stripe configuration failed"),
            ({"DEPLOY_API_OUTCOME": "failure"}, "API deployment failed"),
        )

        with tempfile.TemporaryDirectory() as temporary:
            temporary_path = Path(temporary)
            bin_path = temporary_path / "bin"
            bin_path.mkdir()
            (bin_path / "az").write_text(
                """#!/usr/bin/env bash
set -euo pipefail
case "$*" in
  *"webapp config set"*) exit 0 ;;
  *"webapp config appsettings"*) exit 0 ;;
  *"webapp config show"*) printf '%s\\n' "DOCKER|old-image" ;;
  *"webapp restart"*) exit 0 ;;
  *"webapp show"*) printf '%s\\n' "api.azurewebsites.net" ;;
  *) exit 1 ;;
esac
"""
            )
            (bin_path / "az").chmod(0o700)
            (bin_path / "curl").write_text(
                '''#!/usr/bin/env bash
set -euo pipefail
output=""
while [ "$#" -gt 0 ]; do
  if [ "$1" = "--output" ]; then output="$2"; shift 2; continue; fi
  shift
done
printf '{"status":"ok","buildNumber":"88"}' > "$output"
printf '200'
'''
            )
            (bin_path / "curl").chmod(0o700)
            (bin_path / "sleep").write_text("#!/usr/bin/env bash\nexit 0\n")
            (bin_path / "sleep").chmod(0o700)
            (bin_path / "python3").write_text("#!/usr/bin/env bash\nexit 0\n")
            (bin_path / "python3").chmod(0o700)

            base = os.environ.copy()
            base.update(
                {
                    "PATH": f"{bin_path}:{base['PATH']}",
                    "TARGET_ENVIRONMENT": "test",
                    "DEPLOY_MODE": "app",
                    "AZURE_RESOURCE_GROUP": "rg-test",
                    "AZURE_WEBAPP_NAME": "test-api",
                    "PREVIOUS_DEPLOYMENT_MODE": "classic",
                    "PREVIOUS_LINUX_FX_VERSION": "DOCKER|old-image",
                    "PREVIOUS_SITECONTAINER_IMAGE": "",
                    "PREVIOUS_BUILD_NUMBER": "88",
                    "PREVIOUS_BUILD_NUMBER_PRESENT": "true",
                    "PREVIOUS_HEALTH_BUILD_NUMBER": "88",
                    "PREVIOUS_HEALTH_IMAGE_ID": "",
                    "PRODUCTION_BILLING_CAPTURE_PATH": str(temporary_path / "missing-capture.json"),
                }
            )
            for outcomes, expected in cases:
                with self.subTest(expected=expected):
                    result = subprocess.run(
                        ["bash", "-c", rollback_script],
                        cwd=ROOT,
                        env=base | outcomes,
                        capture_output=True,
                        text=True,
                        check=False,
                    )
                    self.assertEqual(0, result.returncode, result.stdout + result.stderr)
                    combined = result.stdout + result.stderr
                    self.assertIn(expected, combined)
                    self.assertIn("restored the previous deployment image and build metadata", combined)
                    self.assertNotIn("API health gate failed", combined.replace(expected, ""))

    def test_expected_mode_rollback_restores_prior_state_after_image_switch_failure(self) -> None:
        """Stateful fake Azure CLI: capture prior ExpectedMode and restore it."""

        def step_script(step_name: str) -> str:
            start = self.source.index(f"      - name: {step_name}")
            run_start = self.source.index("        run: |\n", start) + len("        run: |\n")
            end = self.source.find("\n      - name:", run_start)
            if end == -1:
                end = len(self.source)
            return dedent(self.source[run_start:end])

        deploy_script = step_script("Deploy API app").replace(
            "${{ steps.current-deployment.outputs.deployment_mode }}", "classic"
        )
        rollback_script = step_script(
            "Restore previous API deployment after deployment, configuration, or health failure"
        )

        mutation_cases = []
        for mode in ("app", "promote"):
            for environment, target in (("test", "test"), ("production", "live")):
                different = "live" if target == "test" else "test"
                mutation_cases.extend(
                    (
                        (mode, environment, target, None, False, "absent"),
                        (mode, environment, target, different, True, "different"),
                        (mode, environment, target, target, False, "already-target"),
                    )
                )
        unreadable_cases = (
            ("app", "test", "test", "live", True),
            ("promote", "production", "live", "test", False),
        )

        with tempfile.TemporaryDirectory() as temporary:
            temporary_path = Path(temporary)
            bin_path = temporary_path / "bin"
            bin_path.mkdir()
            events_path = temporary_path / "events.log"
            state_path = temporary_path / "state.json"
            capture_path = temporary_path / "production-billing.json"
            github_output_path = temporary_path / "github-output"

            def write_executable(name: str, contents: str) -> None:
                path = bin_path / name
                path.write_text(contents)
                path.chmod(0o700)

            write_executable(
                "az",
                f'''#!/usr/bin/env python3
import json
import os
import sys
from pathlib import Path

events = Path({str(events_path)!r})
state_path = Path({str(state_path)!r})
args = sys.argv[1:]
state = json.loads(state_path.read_text())

def record(value):
    with events.open("a", encoding="utf-8") as handle:
        handle.write(value + "\\n")

def persist():
    state_path.write_text(json.dumps(state))

def setting_record():
    return state.setdefault("settings", {{}}).get("Billing__Stripe__ExpectedMode")

if args[:3] == ["webapp", "config", "appsettings"] and "list" in args:
    query = args[args.index("--query") + 1] if "--query" in args else ""
    record("appsettings-list")
    if "Billing__Stripe__ExpectedMode" in query:
        if os.environ.get("FAIL_SETTINGS_LIST") == "1":
            record("appsettings-list-failed")
            raise SystemExit(17)
        current = setting_record()
        print(json.dumps(current) if current else "null")
    else:
        print("0" if "length(@" in query else "")
elif args[:3] == ["webapp", "config", "appsettings"] and "set" in args:
    record("appsettings-set")
    collecting = None
    for argument in args:
        if argument in ("--settings", "--slot-settings"):
            collecting = argument
            continue
        if collecting and argument.startswith("-"):
            collecting = None
            continue
        if collecting and "=" in argument:
            name, value = argument.split("=", 1)
            if name == "Billing__Stripe__ExpectedMode":
                record("billing-expected-mode-set")
                if collecting == "--slot-settings":
                    record("billing-expected-mode-slot-set")
                state.setdefault("settings", {{}})[name] = {{
                    "name": name,
                    "value": value,
                    "slotSetting": collecting == "--slot-settings",
                }}
                persist()
elif args[:3] == ["webapp", "config", "appsettings"] and "delete" in args:
    record("appsettings-delete")
    if "Billing__Stripe__ExpectedMode" in args:
        record("billing-expected-mode-deleted")
        state.setdefault("settings", {{}}).pop("Billing__Stripe__ExpectedMode", None)
        persist()
elif args[:3] == ["webapp", "config", "set"]:
    runtime = args[args.index("--linux-fx-version") + 1]
    state["runtime"] = runtime
    persist()
    record("old-runtime-restored")
elif args[:3] == ["webapp", "config", "container"] and "set" in args:
    image = args[args.index("--container-image-name") + 1]
    state["runtime"] = "DOCKER|" + image
    persist()
    record("runtime-replaced")
    if os.environ.get("FAIL_IMAGE_SWITCH") == "1":
        record("image-switch-failed")
        raise SystemExit(41)
elif args[:3] == ["webapp", "sitecontainers", "update"]:
    image = args[args.index("--image") + 1]
    state["runtime"] = image
    persist()
    record("runtime-replaced")
    if os.environ.get("FAIL_IMAGE_SWITCH") == "1":
        record("image-switch-failed")
        raise SystemExit(41)
elif args[:3] == ["webapp", "config", "show"]:
    record("runtime-read")
    print(state["runtime"])
elif args[:3] == ["webapp", "sitecontainers", "show"]:
    record("runtime-read")
    print(state["runtime"])
elif args[:2] == ["webapp", "restart"]:
    record("restart")
elif args[:2] == ["webapp", "show"]:
    record("health-host-read")
    print("synthetic-api.azurewebsites.net")
else:
    record("az:" + (args[0] if args else "empty"))
''',
            )
            write_executable(
                "docker",
                f'''#!/usr/bin/env bash
set -euo pipefail
printf 'docker:%s\\n' "$1" >> {str(events_path)!r}
''',
            )
            real_python = sys.executable
            write_executable(
                "python3",
                f'''#!{real_python}
import os
import sys
from pathlib import Path

args = sys.argv[1:]
if args and args[0].endswith("scripts/production_stripe_reconcile.py"):
    operation = "audit" if "--audit" in args else "capture" if "--capture" in args else "reapply"
    with Path({str(events_path)!r}).open("a", encoding="utf-8") as handle:
        handle.write(operation + "\\n")
    if operation == "capture":
        destination = Path(args[args.index("--capture") + 1])
        destination.write_text("fake-private-capture")
    raise SystemExit(0)
os.execv({real_python!r}, [{real_python!r}, *args])
''',
            )
            write_executable(
                "curl",
                '''#!/usr/bin/env bash
set -euo pipefail
output=""
while [ "$#" -gt 0 ]; do
  if [ "$1" = "--output" ]; then output="$2"; shift 2; continue; fi
  shift
done
printf '{"status":"ok","buildNumber":"88"}' > "$output"
printf '200'
''',
            )
            write_executable("sleep", "#!/usr/bin/env bash\nexit 0\n")

            def settings() -> dict:
                return json.loads(state_path.read_text()).get("settings", {})

            def expected_mode() -> dict | None:
                return settings().get("Billing__Stripe__ExpectedMode")

            def parse_outputs() -> dict[str, str]:
                parsed: dict[str, str] = {}
                if github_output_path.exists():
                    for line in github_output_path.read_text().splitlines():
                        if "=" in line:
                            key, value = line.split("=", 1)
                            parsed[key] = value
                return parsed

            def run_shell(script: str, environment: dict[str, str]) -> subprocess.CompletedProcess[str]:
                return subprocess.run(
                    ["bash", "-c", script],
                    cwd=ROOT,
                    env=environment,
                    capture_output=True,
                    text=True,
                    check=False,
                )

            def base_environment(*, mode: str, target_environment: str, billing_mode: str) -> dict[str, str]:
                environment = os.environ.copy()
                environment.update(
                    {
                        "PATH": f"{bin_path}:{environment['PATH']}",
                        "TARGET_ENVIRONMENT": target_environment,
                        "DEPLOY_MODE": mode,
                        "AZURE_ENV_NAME": target_environment,
                        "AZURE_RESOURCE_GROUP": "rg-synthetic",
                        "AZURE_WEBAPP_NAME": "synthetic-api",
                        "AZURE_LOCATION": "westeurope",
                        "AZURE_SUBSCRIPTION_ID": "subscription",
                        "AZURE_CONTAINER_REGISTRY_ENDPOINT": "registry.azurecr.io",
                        "GITHUB_SHA": "a" * 40,
                        "GITHUB_RUN_NUMBER": "101",
                        "VALIDATED_CANDIDATE_IMAGE": "registry.azurecr.io/elsa-control/api@sha256:" + "b" * 64,
                        "VALIDATED_CANDIDATE_BUILD_NUMBER": "99",
                        "PRODUCTION_BILLING_CAPTURE_PATH": str(capture_path),
                        "BILLING_EXPECTED_MODE": billing_mode,
                        "EXTERNAL_ENGINE_PAIRING_ALLOWED_ORG_IDS": "",
                        "STAGING_BILLING_LIFECYCLE_LEVER_ENABLED": "",
                        "STAGING_BILLING_LIFECYCLE_LEVER_ALLOWED_ORG_IDS": "",
                        "STAGING_RECOVERY_LIFECYCLE_LEVER_ENABLED": "",
                        "STAGING_RECOVERY_LIFECYCLE_LEVER_ALLOWED_INSTANCE_IDS": "",
                        "STAGING_SMOKE_OWNER_INSTANCE_ID": "",
                    }
                )
                return environment

            def seed_state(prior: str | None, slot_setting: bool) -> None:
                seeded: dict = {"runtime": "DOCKER|old-image", "settings": {}}
                if prior is not None:
                    seeded["settings"]["Billing__Stripe__ExpectedMode"] = {
                        "name": "Billing__Stripe__ExpectedMode",
                        "value": prior,
                        "slotSetting": slot_setting,
                    }
                state_path.write_text(json.dumps(seeded))
                events_path.write_text("")
                github_output_path.write_text("")
                capture_path.write_text("fake-private-capture")

            def rollback_environment(deploy_env: dict[str, str]) -> dict[str, str]:
                outputs = parse_outputs()
                return deploy_env | {
                    "PREVIOUS_DEPLOYMENT_MODE": "classic",
                    "PREVIOUS_LINUX_FX_VERSION": "DOCKER|old-image",
                    "PREVIOUS_SITECONTAINER_IMAGE": "",
                    "PREVIOUS_BUILD_NUMBER": "88",
                    "PREVIOUS_BUILD_NUMBER_PRESENT": "true",
                    "PREVIOUS_HEALTH_BUILD_NUMBER": "88",
                    "PREVIOUS_HEALTH_IMAGE_ID": "",
                    "ROLLBACK_HEALTH": "1",
                    "DEPLOY_API_OUTCOME": "failure",
                    "PREVIOUS_BILLING_EXPECTED_MODE": outputs.get("billing_expected_mode", ""),
                    "PREVIOUS_BILLING_EXPECTED_MODE_PRESENT": outputs.get(
                        "billing_expected_mode_present", ""
                    ),
                    "PREVIOUS_BILLING_EXPECTED_MODE_SLOT_SETTING": outputs.get(
                        "billing_expected_mode_slot_setting", ""
                    ),
                    "GITHUB_OUTPUT": str(github_output_path),
                }

            for mode, environment, target, prior, slot_setting, kind in mutation_cases:
                with self.subTest(mode=mode, environment=environment, prior=kind):
                    seed_state(prior, slot_setting)
                    deploy_env = base_environment(
                        mode=mode, target_environment=environment, billing_mode=target
                    )
                    deploy_env["GITHUB_OUTPUT"] = str(github_output_path)
                    deploy_env["FAIL_IMAGE_SWITCH"] = "1"
                    deploy = run_shell(deploy_script, deploy_env)
                    self.assertEqual(41, deploy.returncode, deploy.stdout + deploy.stderr)
                    observed = events_path.read_text().splitlines()
                    self.assertIn("image-switch-failed", observed)
                    self.assertIn("runtime-replaced", observed)
                    if kind == "already-target":
                        self.assertNotIn("billing-expected-mode-set", observed)
                        self.assertEqual(target, expected_mode()["value"])
                    else:
                        self.assertIn("billing-expected-mode-set", observed)
                        self.assertEqual(target, expected_mode()["value"])
                    outputs = parse_outputs()
                    if prior is None:
                        self.assertEqual("false", outputs.get("billing_expected_mode_present"))
                    else:
                        self.assertEqual("true", outputs.get("billing_expected_mode_present"))
                        self.assertEqual(prior, outputs.get("billing_expected_mode"))
                        self.assertEqual(
                            "true" if slot_setting else "false",
                            outputs.get("billing_expected_mode_slot_setting"),
                        )

                    rollback = run_shell(rollback_script, rollback_environment(deploy_env))
                    self.assertEqual(0, rollback.returncode, rollback.stdout + rollback.stderr)
                    self.assertEqual(
                        "DOCKER|old-image",
                        json.loads(state_path.read_text())["runtime"],
                    )
                    restored = expected_mode()
                    if prior is None:
                        self.assertIsNone(restored)
                        self.assertIn("billing-expected-mode-deleted", events_path.read_text().splitlines())
                    else:
                        self.assertIsNotNone(restored)
                        self.assertEqual(prior, restored["value"])
                        self.assertEqual(slot_setting, restored["slotSetting"])
                        if slot_setting:
                            self.assertIn(
                                "billing-expected-mode-slot-set",
                                events_path.read_text().splitlines(),
                            )
                    if environment == "production":
                        self.assertIn("reapply", events_path.read_text().splitlines())
                    else:
                        self.assertNotIn("reapply", events_path.read_text().splitlines())

            for mode, environment, target, prior, slot_setting in unreadable_cases:
                with self.subTest(mode=mode, environment=environment, prior="unreadable"):
                    seed_state(prior, slot_setting)
                    deploy_env = base_environment(
                        mode=mode, target_environment=environment, billing_mode=target
                    )
                    deploy_env["GITHUB_OUTPUT"] = str(github_output_path)
                    deploy_env["FAIL_SETTINGS_LIST"] = "1"
                    deploy_env["FAIL_IMAGE_SWITCH"] = "1"
                    before = expected_mode()
                    deploy = run_shell(deploy_script, deploy_env)
                    self.assertNotEqual(0, deploy.returncode, deploy.stdout + deploy.stderr)
                    combined = deploy.stdout + deploy.stderr
                    self.assertIn(
                        "Could not read Billing__Stripe__ExpectedMode; refusing to mutate the Web App.",
                        combined,
                    )
                    observed = events_path.read_text().splitlines()
                    self.assertIn("appsettings-list-failed", observed)
                    self.assertNotIn("billing-expected-mode-set", observed)
                    self.assertNotIn("runtime-replaced", observed)
                    self.assertNotIn("image-switch-failed", observed)
                    self.assertEqual(before, expected_mode())
                    self.assertNotIn("billing_expected_mode_present", parse_outputs())

                    rollback = run_shell(rollback_script, rollback_environment(deploy_env))
                    self.assertEqual(0, rollback.returncode, rollback.stdout + rollback.stderr)
                    self.assertEqual(before, expected_mode())
                    self.assertNotIn(
                        "billing-expected-mode-deleted",
                        events_path.read_text().splitlines(),
                    )

    def test_managed_telemetry_deploy_passes_the_environment_recipient(self) -> None:
        script = ROOT / "scripts" / "deploy-managed-telemetry.sh"
        self.assertTrue(script.is_file())
        source = script.read_text()
        self.assertIn('recipient_var=STAGING_RECOVERY_REQUIRED_ALERT_RECIPIENT', source)
        self.assertIn('recipient_var=PRODUCTION_RECOVERY_REQUIRED_ALERT_RECIPIENT', source)
        self.assertIn('environment="$environment"', source)
        self.assertIn('recoveryRequiredAlertEmail="$recipient"', source)
        self.assertIn("assignMonitoringMetricsPublisher=false", source)
        self.assertIn("az deployment group what-if", source)
        self.assertIn("az deployment group create", source)
        self.assertIn("infra/managed-telemetry/main.bicep", source)
        self.assertNotIn("deployment sub", source)
        self.assertNotIn("echo \"$recipient\"", source)
        self.assertNotIn("echo \"$STAGING_RECOVERY_REQUIRED_ALERT_RECIPIENT\"", source)
        self.assertLess(source.index("az deployment group what-if"), source.index("az deployment group create"))
        self.assertIn("Do not grant subscription-scope rights or Authorization write to the deploy identity.", source)
        self.assertIn("An RG-level or subscription-level grant does not satisfy this preflight.", source)
        self.assertIn("Not treating this as a missing role assignment.", source)
        self.assertIn("az resource show --ids", source)
        self.assertIn(
            "Staging RecoveryRequired alerts require PRODUCTION_RECOVERY_REQUIRED_ALERT_RECIPIENT so the mailbox cannot silently reuse production.",
            source,
        )

        principal_id = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"
        role_id = "3913510d-42f4-4e42-8a64-420c390055eb"
        insights_scope = (
            "/subscriptions/00000000-0000-0000-0000-000000000003/resourceGroups/rg-test"
            "/providers/Microsoft.Insights/components/appi-test"
        )
        matching_assignment = json.dumps(
            [
                {
                    "principalId": principal_id,
                    "roleDefinitionId": (
                        "/subscriptions/00000000-0000-0000-0000-000000000003"
                        f"/providers/Microsoft.Authorization/roleDefinitions/{role_id}"
                    ),
                    "scope": insights_scope,
                }
            ]
        )

        with tempfile.TemporaryDirectory() as temp_dir:
            temp_path = Path(temp_dir)
            call_log = temp_path / "az-calls"
            assignments_file = temp_path / "assignments.json"
            assignments_file.write_text(matching_assignment)
            fake_az = temp_path / "az"
            fake_az.write_text(
                """#!/usr/bin/env bash
set -euo pipefail
printf '%s\\n' "$*" >> "${AZ_CALL_LOG:?}"
case "$*" in
  'account set --subscription '*) exit 0 ;;
  'identity show '*)
    if [ "${AZ_IDENTITY_FAIL:-}" = "1" ]; then
      echo "identity-read-failed" >&2
      exit 3
    fi
    printf '%s\\n' "${AZ_PRINCIPAL_ID:?}"
    exit 0
    ;;
  'resource show '*)
    if [ "${AZ_COMPONENT_ABSENT:-}" = "1" ]; then
      echo "ResourceNotFound" >&2
      exit 3
    fi
    if [ "${AZ_COMPONENT_FAIL:-}" = "1" ]; then
      echo "component-read-failed" >&2
      exit 4
    fi
    exit 0
    ;;
  'role assignment list '*)
    if [ "${AZ_ROLE_LIST_FAIL:-}" = "1" ]; then
      echo "role-list-failed" >&2
      exit 3
    fi
    cat "${AZ_ASSIGNMENTS_FILE:?}"
    exit 0
    ;;
  'deployment group create '*|'deployment group what-if '*) exit 0 ;;
  *) exit 41 ;;
esac
"""
            )
            fake_az.chmod(0o755)

            def run_script(*args: str, **extra: str) -> subprocess.CompletedProcess[str]:
                call_log.write_text("")
                environment = os.environ.copy()
                environment.update(
                    {
                        "PATH": f"{temp_path}{os.pathsep}{environment['PATH']}",
                        "AZ_CALL_LOG": str(call_log),
                        "AZ_ASSIGNMENTS_FILE": str(assignments_file),
                        "AZ_PRINCIPAL_ID": principal_id,
                        "AZURE_SUBSCRIPTION_ID": "00000000-0000-0000-0000-000000000003",
                        "AZURE_RESOURCE_GROUP": "rg-test",
                        "AZURE_LOCATION": "westeurope",
                        "MANAGED_TELEMETRY_WORKSPACE_NAME": "law-test",
                        "MANAGED_TELEMETRY_APPLICATION_INSIGHTS_NAME": "appi-test",
                        "MANAGED_TELEMETRY_API_IDENTITY_NAME": "id-api",
                        "MANAGED_TELEMETRY_API_IDENTITY_RESOURCE_GROUP": "rg-test",
                    }
                )
                environment.update(extra)
                return subprocess.run(
                    [str(script), *args],
                    env=environment,
                    capture_output=True,
                    text=True,
                    check=False,
                    timeout=10,
                )

            missing = run_script(TARGET_ENVIRONMENT="test")
            self.assertNotEqual(0, missing.returncode)
            self.assertIn("STAGING_RECOVERY_REQUIRED_ALERT_RECIPIENT", missing.stdout + missing.stderr)
            self.assertEqual("", call_log.read_text())

            staging_without_production = run_script(
                TARGET_ENVIRONMENT="test",
                STAGING_RECOVERY_REQUIRED_ALERT_RECIPIENT="staging-ops@example.test",
            )
            self.assertNotEqual(0, staging_without_production.returncode)
            self.assertIn(
                "PRODUCTION_RECOVERY_REQUIRED_ALERT_RECIPIENT",
                staging_without_production.stdout + staging_without_production.stderr,
            )
            self.assertEqual("", call_log.read_text())

            staging = run_script(
                TARGET_ENVIRONMENT="test",
                STAGING_RECOVERY_REQUIRED_ALERT_RECIPIENT="staging-ops@example.test",
                PRODUCTION_RECOVERY_REQUIRED_ALERT_RECIPIENT="prod-ops@example.test",
            )
            self.assertEqual(0, staging.returncode, staging.stdout + staging.stderr)
            staging_calls = call_log.read_text()
            self.assertIn("identity show", staging_calls)
            self.assertIn("role assignment list", staging_calls)
            self.assertIn(f"--scope {insights_scope}", staging_calls)
            self.assertIn("deployment group what-if", staging_calls)
            self.assertIn("deployment group create", staging_calls)
            self.assertLess(
                staging_calls.index("deployment group what-if"),
                staging_calls.index("deployment group create"),
            )
            self.assertIn("environment=staging", staging_calls)
            self.assertIn("recoveryRequiredAlertEmail=staging-ops@example.test", staging_calls)
            self.assertIn("assignMonitoringMetricsPublisher=false", staging_calls)
            self.assertNotIn("deployment sub", staging_calls)
            self.assertNotIn("role assignment create", staging_calls)
            self.assertNotIn("staging-ops@example.test", staging.stdout + staging.stderr)
            self.assertIn("skipping role assignment create", staging.stdout + staging.stderr)
            self.assertIn("Managed telemetry preflight: present.", staging.stdout + staging.stderr)

            preview = run_script(
                "--what-if",
                TARGET_ENVIRONMENT="test",
                STAGING_RECOVERY_REQUIRED_ALERT_RECIPIENT="staging-ops@example.test",
                PRODUCTION_RECOVERY_REQUIRED_ALERT_RECIPIENT="prod-ops@example.test",
            )
            self.assertEqual(0, preview.returncode, preview.stdout + preview.stderr)
            preview_calls = call_log.read_text()
            self.assertIn("deployment group what-if", preview_calls)
            self.assertNotIn("deployment group create", preview_calls)

            reused = run_script(
                TARGET_ENVIRONMENT="test",
                STAGING_RECOVERY_REQUIRED_ALERT_RECIPIENT="ops@example.test",
                PRODUCTION_RECOVERY_REQUIRED_ALERT_RECIPIENT="ops@example.test",
            )
            self.assertNotEqual(0, reused.returncode)
            self.assertIn("must not use PRODUCTION_RECOVERY_REQUIRED_ALERT_RECIPIENT", reused.stdout + reused.stderr)

            reused_case = run_script(
                TARGET_ENVIRONMENT="test",
                STAGING_RECOVERY_REQUIRED_ALERT_RECIPIENT="  Ops@Example.TEST  ",
                PRODUCTION_RECOVERY_REQUIRED_ALERT_RECIPIENT="ops@example.test",
            )
            self.assertNotEqual(0, reused_case.returncode)
            self.assertIn("must not use PRODUCTION_RECOVERY_REQUIRED_ALERT_RECIPIENT", reused_case.stdout + reused_case.stderr)

            production = run_script(
                TARGET_ENVIRONMENT="production",
                PRODUCTION_RECOVERY_REQUIRED_ALERT_RECIPIENT="prod-ops@example.test",
            )
            self.assertEqual(0, production.returncode, production.stdout + production.stderr)
            production_calls = call_log.read_text()
            self.assertIn("environment=production", production_calls)
            self.assertIn("recoveryRequiredAlertEmail=prod-ops@example.test", production_calls)
            self.assertIn("deployment group what-if", production_calls)
            self.assertIn("deployment group create", production_calls)
            self.assertNotIn("prod-ops@example.test", production.stdout + production.stderr)

            assignments_file.write_text("[]")
            missing_assignment = run_script(
                TARGET_ENVIRONMENT="test",
                STAGING_RECOVERY_REQUIRED_ALERT_RECIPIENT="staging-ops@example.test",
                PRODUCTION_RECOVERY_REQUIRED_ALERT_RECIPIENT="prod-ops@example.test",
            )
            self.assertEqual(0, missing_assignment.returncode, missing_assignment.stdout + missing_assignment.stderr)
            missing_output = missing_assignment.stdout + missing_assignment.stderr
            missing_calls = call_log.read_text()
            self.assertIn("Managed telemetry preflight: missing.", missing_output)
            self.assertIn(f"principal={principal_id}", missing_output)
            self.assertIn("role=Monitoring Metrics Publisher", missing_output)
            self.assertIn(role_id, missing_output)
            self.assertIn(f"scope={insights_scope}", missing_output)
            self.assertIn("Do not grant subscription-scope rights", missing_output)
            self.assertIn("deployment group what-if", missing_calls)
            self.assertIn("deployment group create", missing_calls)
            self.assertNotIn("role assignment create", missing_calls)

            missing_preview = run_script(
                "--what-if",
                TARGET_ENVIRONMENT="test",
                STAGING_RECOVERY_REQUIRED_ALERT_RECIPIENT="staging-ops@example.test",
                PRODUCTION_RECOVERY_REQUIRED_ALERT_RECIPIENT="prod-ops@example.test",
            )
            self.assertEqual(0, missing_preview.returncode, missing_preview.stdout + missing_preview.stderr)
            self.assertIn("Managed telemetry preflight: missing.", missing_preview.stdout + missing_preview.stderr)
            self.assertIn("deployment group what-if", call_log.read_text())
            self.assertNotIn("deployment group create", call_log.read_text())

            absent_preview = run_script(
                "--what-if",
                TARGET_ENVIRONMENT="test",
                STAGING_RECOVERY_REQUIRED_ALERT_RECIPIENT="staging-ops@example.test",
                PRODUCTION_RECOVERY_REQUIRED_ALERT_RECIPIENT="prod-ops@example.test",
                AZ_COMPONENT_ABSENT="1",
            )
            self.assertEqual(0, absent_preview.returncode, absent_preview.stdout + absent_preview.stderr)
            self.assertIn("Managed telemetry preflight: component-absent.", absent_preview.stdout + absent_preview.stderr)
            self.assertIn("deployment group what-if", call_log.read_text())
            self.assertNotIn("deployment group create", call_log.read_text())
            self.assertNotIn("role assignment list", call_log.read_text())

            absent = run_script(
                TARGET_ENVIRONMENT="test",
                STAGING_RECOVERY_REQUIRED_ALERT_RECIPIENT="staging-ops@example.test",
                PRODUCTION_RECOVERY_REQUIRED_ALERT_RECIPIENT="prod-ops@example.test",
                AZ_COMPONENT_ABSENT="1",
            )
            self.assertEqual(0, absent.returncode, absent.stdout + absent.stderr)
            absent_output = absent.stdout + absent.stderr
            absent_calls = call_log.read_text()
            self.assertIn("Managed telemetry preflight: component-absent.", absent_output)
            self.assertIn(f"principal={principal_id}", absent_output)
            self.assertIn("role=Monitoring Metrics Publisher", absent_output)
            self.assertIn(f"scope={insights_scope}", absent_output)
            self.assertIn("deployment group what-if", absent_calls)
            self.assertIn("deployment group create", absent_calls)
            self.assertNotIn("role assignment list", absent_calls)

            assignments_file.write_text(matching_assignment)
            identity_missing = run_script(
                "--what-if",
                TARGET_ENVIRONMENT="production",
                PRODUCTION_RECOVERY_REQUIRED_ALERT_RECIPIENT="prod-ops@example.test",
                AZ_IDENTITY_FAIL="1",
            )
            self.assertNotEqual(0, identity_missing.returncode)
            identity_output = identity_missing.stdout + identity_missing.stderr
            self.assertIn("Could not read the Control API identity principal (az exit 3)", identity_output)
            self.assertIn("Not treating this as a missing role assignment.", identity_output)
            self.assertNotIn("Monitoring Metrics Publisher is not assigned", identity_output)
            self.assertIn("deployment group what-if", call_log.read_text())
            self.assertNotIn("deployment group create", call_log.read_text())

            role_list_fail = run_script(
                "--what-if",
                TARGET_ENVIRONMENT="production",
                PRODUCTION_RECOVERY_REQUIRED_ALERT_RECIPIENT="prod-ops@example.test",
                AZ_ROLE_LIST_FAIL="1",
            )
            self.assertNotEqual(0, role_list_fail.returncode)
            role_list_output = role_list_fail.stdout + role_list_fail.stderr
            self.assertIn("Could not list role assignments on the Insights component (az exit 3)", role_list_output)
            self.assertIn("Not treating this as a missing role assignment.", role_list_output)
            self.assertNotIn("Monitoring Metrics Publisher is not assigned", role_list_output)
            self.assertIn("deployment group what-if", call_log.read_text())
            self.assertNotIn("deployment group create", call_log.read_text())

            rg_scope = "/subscriptions/00000000-0000-0000-0000-000000000003/resourceGroups/rg-test"
            assignments_file.write_text(
                json.dumps(
                    [
                        {
                            "principalId": principal_id,
                            "roleDefinitionId": (
                                "/subscriptions/00000000-0000-0000-0000-000000000003"
                                f"/providers/Microsoft.Authorization/roleDefinitions/{role_id}"
                            ),
                            "scope": rg_scope,
                        }
                    ]
                )
            )
            wrong_scope = run_script(
                "--what-if",
                TARGET_ENVIRONMENT="production",
                PRODUCTION_RECOVERY_REQUIRED_ALERT_RECIPIENT="prod-ops@example.test",
            )
            self.assertEqual(0, wrong_scope.returncode, wrong_scope.stdout + wrong_scope.stderr)
            self.assertIn("Managed telemetry preflight: missing.", wrong_scope.stdout + wrong_scope.stderr)
            self.assertIn(f"scope={insights_scope}", wrong_scope.stdout + wrong_scope.stderr)

            assignments_file.write_text(
                json.dumps(
                    [
                        {
                            "principalId": principal_id,
                            "roleDefinitionId": (
                                "/subscriptions/00000000-0000-0000-0000-000000000003"
                                "/providers/Microsoft.Authorization/roleDefinitions/"
                                "b24988ac-6180-42a0-ab88-20f7382dd24c"
                            ),
                            "scope": insights_scope,
                        }
                    ]
                )
            )
            wrong_role = run_script(
                "--what-if",
                TARGET_ENVIRONMENT="production",
                PRODUCTION_RECOVERY_REQUIRED_ALERT_RECIPIENT="prod-ops@example.test",
            )
            self.assertEqual(0, wrong_role.returncode, wrong_role.stdout + wrong_role.stderr)
            self.assertIn("Managed telemetry preflight: missing.", wrong_role.stdout + wrong_role.stderr)

            assignments_file.write_text(
                json.dumps(
                    [
                        {
                            "principalId": "ffffffff-eeee-dddd-cccc-bbbbbbbbbbbb",
                            "roleDefinitionId": (
                                "/subscriptions/00000000-0000-0000-0000-000000000003"
                                f"/providers/Microsoft.Authorization/roleDefinitions/{role_id}"
                            ),
                            "scope": insights_scope,
                        }
                    ]
                )
            )
            wrong_principal = run_script(
                "--what-if",
                TARGET_ENVIRONMENT="production",
                PRODUCTION_RECOVERY_REQUIRED_ALERT_RECIPIENT="prod-ops@example.test",
            )
            self.assertEqual(0, wrong_principal.returncode, wrong_principal.stdout + wrong_principal.stderr)
            self.assertIn("Managed telemetry preflight: missing.", wrong_principal.stdout + wrong_principal.stderr)

    def test_promotion_reads_back_exact_runtime_before_settings_or_restart(self) -> None:
        deploy_start = self.source.index(
            "        run: |\n",
            self.source.index("      - name: Deploy API app"),
        )
        deploy_end = self.source.index("\n      - name:", deploy_start)
        deploy_script = dedent(self.source[deploy_start + len("        run: |\n") : deploy_end])
        deploy_script = deploy_script.replace(
            '${{ steps.current-deployment.outputs.deployment_mode }}',
            '"$CURRENT_DEPLOYMENT_MODE"',
        )
        candidate_image = "acr.azurecr.io/elsa-control/api@sha256:" + "c" * 64

        with tempfile.TemporaryDirectory() as temp_dir:
            temp_path = Path(temp_dir)
            fake_az = temp_path / "az"
            fake_az.write_text(
                """#!/usr/bin/env bash
set -euo pipefail
printf '%s\\n' "$*" >> "${AZ_CALL_LOG:?}"
case "$*" in
  *"webapp sitecontainers update"*) exit 0 ;;
  *"webapp config container set"*) exit 0 ;;
  *"webapp sitecontainers show"*) printf '%s\\n' "${RUNTIME_READBACK:?}" ;;
  *"webapp config show"*) printf '%s\\n' "${RUNTIME_READBACK:?}" ;;
  *"webapp config appsettings set"*) exit 0 ;;
  *"webapp config appsettings delete"*) exit 0 ;;
  *"webapp config appsettings list"*)
    case "$*" in
      *"Billing__Stripe__ExpectedMode"*) printf 'null\\n' ;;
      *"].name"*|*" ].name"*) ;;
      *"].value"*|*" ].value"*) ;;
      *) printf '%s\\n' "0" ;;
    esac
    ;;
  *"webapp restart"*) exit 0 ;;
  *) exit 1 ;;
esac
"""
            )
            fake_az.chmod(0o755)
            call_log = temp_path / "az-calls"

            def run_promotion(
                current_deployment_mode: str,
                runtime_readback: str,
                candidate_build_number: str = "96",
                cloud_account_issuer: str = "",
                target_environment: str | None = "production",
            ) -> subprocess.CompletedProcess[str]:
                call_log.unlink(missing_ok=True)
                environment = os.environ.copy()
                environment.pop("TARGET_ENVIRONMENT", None)
                environment.pop("MANAGED_LIFECYCLE_AZURE_MONITOR_ENABLED", None)
                environment.update(
                    {
                        "PATH": f"{temp_path}:{environment['PATH']}",
                        "AZ_CALL_LOG": str(call_log),
                        "AZURE_RESOURCE_GROUP": "test-rg",
                        "AZURE_WEBAPP_NAME": "test-api",
                        "AZURE_CONTAINER_REGISTRY_ENDPOINT": "acr.azurecr.io",
                        "DEPLOY_MODE": "promote",
                        "GITHUB_RUN_NUMBER": "1786839398",
                        "VALIDATED_CANDIDATE_IMAGE": candidate_image,
                        "VALIDATED_CANDIDATE_BUILD_NUMBER": candidate_build_number,
                        "CURRENT_DEPLOYMENT_MODE": current_deployment_mode,
                        "RUNTIME_READBACK": runtime_readback,
                        "CLOUD_ACCOUNT_ISSUER": cloud_account_issuer,
                    }
                )
                if target_environment is not None:
                    environment["TARGET_ENVIRONMENT"] = target_environment
                return subprocess.run(
                    ["bash", "-c", deploy_script],
                    env=environment,
                    capture_output=True,
                    text=True,
                    check=False,
                    timeout=10,
                )

            classic = run_promotion("classic", f"DOCKER|{candidate_image}")
            self.assertEqual(0, classic.returncode, classic.stderr)
            classic_calls = call_log.read_text()
            self.assertIn("webapp config container set", classic_calls)
            self.assertIn("webapp config show", classic_calls)
            self.assertIn("webapp config appsettings set", classic_calls)
            self.assertIn("ManagedLifecycleTelemetry__AzureMonitor__Environment=production", classic_calls)

            unset_environment = run_promotion(
                "classic", f"DOCKER|{candidate_image}", target_environment=None
            )
            self.assertEqual(0, unset_environment.returncode, unset_environment.stderr)
            unset_calls = call_log.read_text()
            self.assertIn("webapp config appsettings set", unset_calls)
            self.assertNotIn("ManagedLifecycleTelemetry__AzureMonitor__Environment=", unset_calls)
            self.assertIn("webapp restart", classic_calls)
            # The promoted app keeps the candidate's build number, not the promotion run number.
            self.assertIn("Application__BuildNumber=96", classic_calls)
            self.assertNotIn("Application__BuildNumber=1786839398", classic_calls)
            self.assertIn("Authentication__CloudAccount__Enabled=false", classic_calls)

            with_cloud_account = run_promotion(
                "classic", f"DOCKER|{candidate_image}",
                cloud_account_issuer="https://jhrcnclyydzngnyvhdht.supabase.co/auth/v1",
            )
            self.assertEqual(0, with_cloud_account.returncode, with_cloud_account.stderr)
            cloud_calls = call_log.read_text()
            self.assertIn("Authentication__CloudAccount__Enabled=true", cloud_calls)
            self.assertIn("Authentication__CloudAccount__Issuer=https://jhrcnclyydzngnyvhdht.supabase.co/auth/v1", cloud_calls)
            self.assertIn("Authentication__CloudAccount__Audience=authenticated", cloud_calls)

            for invalid_build_number in ("", "abc", "0", "0123", "1" * 21):
                with self.subTest(candidate_build_number=invalid_build_number):
                    rejected = run_promotion("classic", f"DOCKER|{candidate_image}", candidate_build_number=invalid_build_number)
                    self.assertNotEqual(0, rejected.returncode)
                    self.assertIn("candidate build number is unavailable", rejected.stdout + rejected.stderr)
                    # Refused before any Web App mutation, not merely before the settings write.
                    self.assertEqual("", call_log.read_text() if call_log.exists() else "")

            sitecontainers = run_promotion("sitecontainers", candidate_image)
            self.assertEqual(0, sitecontainers.returncode, sitecontainers.stderr)
            sitecontainer_calls = call_log.read_text()
            self.assertIn("webapp sitecontainers update", sitecontainer_calls)
            self.assertIn("webapp sitecontainers show", sitecontainer_calls)
            self.assertIn("webapp config appsettings set", sitecontainer_calls)
            self.assertIn("webapp restart", sitecontainer_calls)

            mismatch = run_promotion(
                "classic",
                "DOCKER|acr.azurecr.io/elsa-control/api@sha256:" + "d" * 64,
            )
            self.assertNotEqual(0, mismatch.returncode)
            self.assertIn(
                "did not match the validated immutable image",
                mismatch.stdout + mismatch.stderr,
            )
            mismatch_calls = call_log.read_text()
            self.assertIn("webapp config container set", mismatch_calls)
            self.assertIn("webapp config show", mismatch_calls)
            self.assertNotIn("webapp config appsettings set", mismatch_calls)
            self.assertNotIn("webapp restart", mismatch_calls)

            # Recreate with ExpectedMode so the early billing write is pinned
            # before the image switch; the later combined settings write stays
            # after a successful readback.
            call_log.unlink(missing_ok=True)
            billed_environment = os.environ.copy()
            billed_environment.pop("TARGET_ENVIRONMENT", None)
            billed_environment.pop("MANAGED_LIFECYCLE_AZURE_MONITOR_ENABLED", None)
            billed_environment.update(
                {
                    "PATH": f"{temp_path}:{billed_environment['PATH']}",
                    "AZ_CALL_LOG": str(call_log),
                    "AZURE_RESOURCE_GROUP": "test-rg",
                    "AZURE_WEBAPP_NAME": "test-api",
                    "AZURE_CONTAINER_REGISTRY_ENDPOINT": "acr.azurecr.io",
                    "DEPLOY_MODE": "promote",
                    "GITHUB_RUN_NUMBER": "1786839398",
                    "VALIDATED_CANDIDATE_IMAGE": candidate_image,
                    "VALIDATED_CANDIDATE_BUILD_NUMBER": "96",
                    "CURRENT_DEPLOYMENT_MODE": "classic",
                    "RUNTIME_READBACK": f"DOCKER|{candidate_image}",
                    "CLOUD_ACCOUNT_ISSUER": "",
                    "TARGET_ENVIRONMENT": "production",
                    "BILLING_EXPECTED_MODE": "live",
                }
            )
            billed = subprocess.run(
                ["bash", "-c", deploy_script],
                env=billed_environment,
                capture_output=True,
                text=True,
                check=False,
                timeout=10,
            )
            self.assertEqual(0, billed.returncode, billed.stderr)
            billed_calls = call_log.read_text().splitlines()
            expected_mode_indexes = [
                index
                for index, line in enumerate(billed_calls)
                if "Billing__Stripe__ExpectedMode=live" in line
            ]
            container_indexes = [
                index
                for index, line in enumerate(billed_calls)
                if "webapp config container set" in line
            ]
            self.assertEqual(2, len(expected_mode_indexes))
            self.assertEqual(1, len(container_indexes))
            self.assertLess(expected_mode_indexes[0], container_indexes[0])
            self.assertLess(container_indexes[0], expected_mode_indexes[1])

    def test_health_identity_separates_candidate_source_from_promotion_run(self) -> None:
        self.assertIn('VALIDATED_CANDIDATE_SOURCE_SHA: ${{ steps.candidate-authority.outputs.candidate_source_sha }}', self.source)
        self.assertIn('expected_image_id="$VALIDATED_CANDIDATE_SOURCE_SHA"', self.source)
        self.assertIn('VALIDATED_CANDIDATE_BUILD_NUMBER: ${{ steps.candidate-authority.outputs.candidate_build_number }}', self.source)
        self.assertIn('expected_build_number="$VALIDATED_CANDIDATE_BUILD_NUMBER"', self.source)
        self.assertIn('--arg expected_build_number "$expected_build_number"', self.source)
        self.assertNotIn('--arg expected_build_number "$GITHUB_RUN_NUMBER"', self.source)
        self.assertNotIn('Application__BuildNumber="$GITHUB_RUN_NUMBER"', self.source)
        self.assertIn('--arg expected_image_id "$expected_image_id"', self.source)
        self.assertIn('The Web App has a runtime image identity override; refusing promotion', self.source)

    def test_test_environment_reconciles_stripe_without_exposing_secrets_job_wide(self) -> None:
        job_env_start = self.source.index("    env:\n", self.source.index("    permissions:"))
        job_env_end = self.source.index("    steps:\n", job_env_start)
        job_env = self.source[job_env_start:job_env_end]
        self.assertNotIn("STRIPE_TEST_SECRET_KEY:", job_env)
        self.assertNotIn("STRIPE_TEST_WEBHOOK_SIGNING_SECRET:", job_env)
        self.assertNotIn("STRIPE_HOSTED_PRICE_ID:", job_env)

        config_start = self.source.index("      - name: Check deployment configuration")
        config_end = self.source.index("\n      - name:", config_start + 1)
        config_step = self.source[config_start:config_end]
        self.assertIn("secrets.STRIPE_TEST_SECRET_KEY", config_step)
        self.assertIn("secrets.STRIPE_TEST_WEBHOOK_SIGNING_SECRET", config_step)
        self.assertIn('"$TARGET_ENVIRONMENT" = "test"', config_step)
        self.assertIn("STRIPE_HOSTED_PRICE_ID", config_step)
        self.assertIn("ELSA_CLOUD_STAGING_ORIGIN", config_step)

        audit_start = self.source.index("      - name: Audit staging Stripe resources before deployment")
        restore_start = self.source.index("      - name: Restore")
        self.assertLess(audit_start, restore_start)
        audit_end = self.source.index("\n      - name:", audit_start + 1)
        audit_step = self.source[audit_start:audit_end]
        self.assertIn("--audit-stripe-only", audit_step)
        self.assertIn("env.TARGET_ENVIRONMENT == 'test'", audit_step)

        reconcile_start = self.source.index("      - name: Reconcile staging Stripe billing")
        reconcile_end = self.source.index("\n      - name:", reconcile_start + 1)
        reconcile_step = self.source[reconcile_start:reconcile_end]
        self.assertIn("env.TARGET_ENVIRONMENT == 'test'", reconcile_step)
        self.assertIn("env.DEPLOY_MODE != 'build'", reconcile_step)
        self.assertIn("scripts/staging_stripe_reconcile.py --apply-azure-settings", reconcile_step)
        self.assertIn("/api/billing/webhooks/stripe", reconcile_step)
        self.assertIn("${{ vars.ELSA_CLOUD_STAGING_ORIGIN }}/dashboard", reconcile_step)
        self.assertIn("/checkout/return?session_id={CHECKOUT_SESSION_ID}", reconcile_step)
        self.assertIn("/dashboard/billing", reconcile_step)
        self.assertNotIn("sk_test_", reconcile_step)
        self.assertNotIn("whsec_", reconcile_step)

        rollback_start = self.source.index("      - name: Restore previous API deployment")
        rollback_line = self.source[rollback_start:self.source.index("\n", rollback_start)] + self.source[self.source.index("\n", rollback_start):self.source.index("\n        env:", rollback_start)]
        self.assertIn("steps.staging-stripe.outcome == 'failure'", rollback_line)

        fresh_recovery_start = self.source.index("      - name: Report recovery path for a new test environment")
        fresh_recovery = self.source[fresh_recovery_start:]
        self.assertIn("env.DEPLOY_MODE == 'infra'", fresh_recovery)
        self.assertIn("capture_succeeded != 'true'", fresh_recovery)
        self.assertIn("retained for diagnosis", fresh_recovery)
        self.assertIn("idempotent infra deployment", fresh_recovery)

    def test_health_gates_require_exact_http_200(self) -> None:
        self.assertGreaterEqual(
            self.source.count('if [ "$http_status" = "200" ]'), 2
        )
        self.assertIn('.buildNumber == $expected_build_number', self.source)
        self.assertIn('.imageId == $expected_image_id', self.source)
        self.assertIn('expected_previous_image_id="${PREVIOUS_HEALTH_IMAGE_ID:-}"', self.source)
        self.assertIn("PREVIOUS_HEALTH_IMAGE_ID", self.source)
        self.assertIn("PREVIOUS_HEALTH_BUILD_NUMBER", self.source)
        self.assertIn("previous_health_image_id=\"$(jq -r '.imageId // empty'", self.source)
        self.assertIn('restored_runtime_image=', self.source)
        self.assertIn(
            "expected_previous_health_query='.status == \"ok\" and .buildNumber == $expected_build_number and .imageId == $expected_image_id'",
            self.source,
        )
        self.assertIn('legacy-image compatibility path', self.source)
        self.assertIn(
            'Azure is not configured with the captured previous main sitecontainer image',
            self.source,
        )
        self.assertIn('if [ "$stable_health_probes" -ge 2 ]; then', self.source)
        self.assertIn('if [ "$stable_rollback_health_probes" -ge 2 ]; then', self.source)

    def test_infra_deploy_uses_the_checked_in_api_dockerfile(self) -> None:
        deploy_script = (ROOT / "scripts" / "deploy-azure-elsa-control.sh").read_text()
        self.assertIn("--build-arg ELSA_CONTROL_IMAGE_ID=", deploy_script)
        self.assertIn("--file src/Hosting/ElsaControl.Api/Dockerfile", deploy_script)
        self.assertNotIn("--file src/ElsaControl.Api/Dockerfile", deploy_script)

    def test_capture_rejects_credential_bearing_and_scheme_based_images(self) -> None:
        capture_start = self.source.index(
            "        run: |\n",
            self.source.index("      - name: Capture current API deployment"),
        )
        capture_end = self.source.index("\n      - name:", capture_start)
        capture_script = dedent(self.source[capture_start + len("        run: |\n") : capture_end])

        with tempfile.TemporaryDirectory() as temp_dir:
            temp_path = Path(temp_dir)
            fake_az = temp_path / "az"
            fake_az.write_text(
                """#!/usr/bin/env bash
set -euo pipefail
case "$*" in
  *"webapp show"*)
    if [ "${WEBAPP_MISSING:-false}" = true ]; then
      printf '%s\\n' "(ResourceNotFound) Web App was not found." >&2
      exit 1
    fi
    printf '%s\\n' "test-api"
    ;;
  *"webapp config show"*) printf '%s\\n' "${LINUX_FX_VERSION}" ;;
  *"webapp sitecontainers show"*)
    if [ "${FAIL_SITECONTAINER_LOOKUP:-false}" = true ]; then exit 1; fi
    printf '%s\\n' "${SITECONTAINER_IMAGE}"
    ;;
  *"webapp config appsettings list"*)
    case "$*" in
      *"ELSA_CONTROL_IMAGE_ID"*) printf '%s\\n' "${IMAGE_ID_OVERRIDE_COUNT:-0}" ;;
      *) printf '%s\\n' "${APPLICATION_BUILD_NUMBER}" ;;
    esac
    ;;
  *"acr manifest show-metadata"*) printf '%s\\n' "${PREVIOUS_DIGEST:-sha256:$(printf 'd%.0s' {1..64})}" ;;
  *) exit 1 ;;
esac
"""
            )
            fake_az.chmod(0o755)
            fake_curl = temp_path / "curl"
            fake_curl.write_text(
                '''#!/usr/bin/env bash
set -euo pipefail
output_file=""
while [ "$#" -gt 0 ]; do
  if [ "$1" = "--output" ]; then
    output_file="$2"
    shift 2
  else
    shift
  fi
done
if [ -n "${HEALTH_RESPONSE:-}" ]; then
  printf '%s' "$HEALTH_RESPONSE" > "$output_file"
else
  printf '%s' '{"status":"ok","buildNumber":"1786839398","imageId":"abcdef0123456789"}' > "$output_file"
fi
printf '%s' "${HEALTH_STATUS:-200}"
'''
            )
            fake_curl.chmod(0o755)

            def run_capture(
                linux_fx_version: str,
                sitecontainer_image: str,
                fail_sitecontainer_lookup: bool = False,
                webapp_missing: bool = False,
                deploy_mode: str = "app",
                health_response: str = '{"status":"ok","buildNumber":"1786839398","imageId":"abcdef0123456789"}',
                health_status: str = "200",
                image_id_override_count: str = "0",
            ) -> subprocess.CompletedProcess[str]:
                output_file = temp_path / "github-output"
                output_file.unlink(missing_ok=True)
                environment = os.environ.copy()
                environment.update(
                    {
                        "PATH": f"{temp_path}:{environment['PATH']}",
                        "GITHUB_OUTPUT": str(output_file),
                        "LINUX_FX_VERSION": linux_fx_version,
                        "SITECONTAINER_IMAGE": sitecontainer_image,
                        "APPLICATION_BUILD_NUMBER": "1786839398",
                        "FAIL_SITECONTAINER_LOOKUP": str(fail_sitecontainer_lookup).lower(),
                        "WEBAPP_MISSING": str(webapp_missing).lower(),
                        "DEPLOY_MODE": deploy_mode,
                        "AZURE_RESOURCE_GROUP": "test-rg",
                        "AZURE_WEBAPP_NAME": "test-api",
                        "AZURE_CONTAINER_REGISTRY_ENDPOINT": "acr.azurecr.io",
                        "HEALTH_RESPONSE": health_response,
                        "HEALTH_STATUS": health_status,
                        "IMAGE_ID_OVERRIDE_COUNT": image_id_override_count,
                        "PREVIOUS_DIGEST": "sha256:" + "d" * 64,
                    }
                )
                return subprocess.run(
                    ["bash", "-c", capture_script],
                    env=environment,
                    capture_output=True,
                    text=True,
                    check=False,
                )

            unsafe_images = (
                "https://user:pass@acr.azurecr.io/elsa-control/api:latest",
                "acr.azurecr.io/elsa-control/api?secret=1",
                "acr.azurecr.io/elsa-control/api#fragment",
            )
            for image in unsafe_images:
                for runtime, captured_image in (
                    (f"DOCKER|{image}", ""),
                    ("SITECONTAINERS", image),
                ):
                    result = run_capture(runtime, captured_image)
                    self.assertNotEqual(result.returncode, 0, image)
                    if runtime == "SITECONTAINERS":
                        self.assertIn(
                            "unexpected or unsafe format",
                            result.stdout + result.stderr,
                        )

            lookup_failure = run_capture(
                "SITECONTAINERS",
                "acr.azurecr.io/elsa-control/api:latest",
                fail_sitecontainer_lookup=True,
            )
            self.assertNotEqual(lookup_failure.returncode, 0)

            valid_health_capture = run_capture(
                "DOCKER|acr.azurecr.io/elsa-control/api:latest",
                "",
            )
            self.assertEqual(
                valid_health_capture.returncode,
                0,
                valid_health_capture.stderr,
            )
            valid_output = (temp_path / "github-output").read_text()
            self.assertIn("previous_health_build_number=1786839398", valid_output)
            self.assertIn("previous_health_image_id=abcdef0123456789", valid_output)

            unsafe_health_capture = run_capture(
                "DOCKER|acr.azurecr.io/elsa-control/api:latest",
                "",
                health_response='{"status":"ok","buildNumber":"1786839398","imageId":"https://user:pass@example.test/image"}',
            )
            self.assertNotEqual(unsafe_health_capture.returncode, 0)
            self.assertIn(
                "unexpected or unsafe previous_health_image_id",
                unsafe_health_capture.stdout + unsafe_health_capture.stderr,
            )

            unhealthy_current = run_capture(
                "DOCKER|acr.azurecr.io/elsa-control/api:latest",
                "",
                health_response=(
                    '{"status":"degraded","buildNumber":"1786839398",'
                    '"imageId":"https://user:pass@example.test/image"}'
                ),
                health_status="503",
            )
            self.assertEqual(
                unhealthy_current.returncode,
                0,
                unhealthy_current.stderr,
            )
            unhealthy_output = (temp_path / "github-output").read_text()
            self.assertIn("capture_succeeded=true", unhealthy_output)
            self.assertNotIn("previous_health_image_id=", unhealthy_output)

            fresh_infra = run_capture(
                "SITECONTAINERS",
                "",
                webapp_missing=True,
                deploy_mode="infra",
            )
            self.assertEqual(fresh_infra.returncode, 0, fresh_infra.stderr)
            self.assertIn(
                "capture_succeeded=false",
                (temp_path / "github-output").read_text(),
            )

            fresh_app = run_capture(
                "SITECONTAINERS",
                "",
                webapp_missing=True,
                deploy_mode="app",
            )
            self.assertNotEqual(fresh_app.returncode, 0)

            promoted_capture = run_capture(
                "DOCKER|acr.azurecr.io/elsa-control/api:previous",
                "",
                deploy_mode="promote",
            )
            self.assertEqual(0, promoted_capture.returncode, promoted_capture.stderr)
            promoted_output = (temp_path / "github-output").read_text()
            self.assertIn(
                "linux_fx_version=DOCKER|acr.azurecr.io/elsa-control/api@sha256:" + "d" * 64,
                promoted_output,
            )

            override_capture = run_capture(
                "DOCKER|acr.azurecr.io/elsa-control/api:previous",
                "",
                deploy_mode="promote",
                image_id_override_count="1",
            )
            self.assertNotEqual(0, override_capture.returncode)
            self.assertIn("runtime image identity override", override_capture.stdout + override_capture.stderr)

    def test_rollback_identity_query_rejects_a_different_image(self) -> None:
        exact_query = (
            '.status == "ok" and .buildNumber == $expected_build_number '
            'and .imageId == $expected_image_id'
        )
        legacy_query = (
            '.status == "ok" and ((.buildNumber // $expected_build_number) '
            '== $expected_build_number)'
        )

        def evaluate(
            query: str,
            payload: dict[str, str],
            image_id: str = "",
        ) -> subprocess.CompletedProcess[str]:
            return subprocess.run(
                [
                    "jq",
                    "-e",
                    "--arg",
                    "expected_build_number",
                    "1786839398",
                    "--arg",
                    "expected_image_id",
                    image_id,
                    query,
                ],
                input=json.dumps(payload),
                capture_output=True,
                text=True,
                check=False,
            )

        self.assertEqual(
            evaluate(
                exact_query,
                {
                    "status": "ok",
                    "buildNumber": "1786839398",
                    "imageId": "abcdef0123456789",
                },
                "abcdef0123456789",
            ).returncode,
            0,
        )
        self.assertNotEqual(
            evaluate(
                exact_query,
                {"status": "ok", "buildNumber": "1786839398", "imageId": "failed-image"},
                "abcdef0123456789",
            ).returncode,
            0,
        )
        self.assertEqual(
            evaluate(
                legacy_query,
                {"status": "ok", "buildNumber": "1786839398"},
            ).returncode,
            0,
        )

    def test_pairing_settings_helper_prunes_stale_entries_and_refuses_a_live_smoke_org(self) -> None:
        script = ROOT / "scripts" / "apply-external-engine-pairing-settings.sh"
        self.assertTrue(script.is_file())
        rehearsal = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"
        leftover = "cccccccc-cccc-cccc-cccc-cccccccccccc"
        smoke = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"
        prefix = "ElsaControl__ExternalEngines__PairingAllowedOrganizationIds__"

        with tempfile.TemporaryDirectory() as temp_dir:
            temp_path = Path(temp_dir)
            store = temp_path / "settings.json"
            call_log = temp_path / "az-calls"
            fake_az = temp_path / "az"
            fake_az.write_text(
                """#!/usr/bin/env python3
import json, os, sys
open(os.environ["AZ_CALL_LOG"], "a", encoding="utf-8").write(" ".join(sys.argv[1:]) + "\\n")
store_path = os.environ["SETTINGS_STORE"]
store = json.loads(store_path.read_text() if False else open(store_path, encoding="utf-8").read())
joined = " ".join(sys.argv[1:])
prefix = "ElsaControl__ExternalEngines__PairingAllowedOrganizationIds__"
items = {name: value for name, value in store.items() if name.startswith(prefix)}
if "appsettings list" in joined:
    if "].name" in joined:
        print("\\n".join(items))
    elif "].value" in joined:
        print("\\n".join(items.values()))
    else:
        print(len(items))
elif "appsettings delete" in joined:
    args = sys.argv[1:]
    names = []
    for item in args[args.index("--setting-names") + 1:]:
        if item.startswith("--"):
            break
        names.append(item)
    for name in names:
        store.pop(name, None)
    open(store_path, "w", encoding="utf-8").write(json.dumps(store))
elif "appsettings set" in joined:
    args = sys.argv[1:]
    values = []
    for item in args[args.index("--settings") + 1:]:
        if item.startswith("--"):
            break
        values.append(item)
    for item in values:
        name, value = item.split("=", 1)
        store[name] = value
    open(store_path, "w", encoding="utf-8").write(json.dumps(store))
else:
    sys.exit(1)
"""
            )
            fake_az.chmod(0o755)

            def run_helper(allowlist: str, initial: dict[str, str]) -> tuple[subprocess.CompletedProcess[str], dict[str, str]]:
                store.write_text(json.dumps(initial))
                call_log.write_text("")
                environment = os.environ.copy()
                environment.update(
                    {
                        "PATH": f"{temp_path}:{environment['PATH']}",
                        "AZ_CALL_LOG": str(call_log),
                        "SETTINGS_STORE": str(store),
                        "AZURE_RESOURCE_GROUP": "test-rg",
                        "AZURE_WEBAPP_NAME": "test-api",
                        "TARGET_ENVIRONMENT": "test",
                        "EXTERNAL_ENGINE_PAIRING_ALLOWED_ORG_IDS": allowlist,
                        "STAGING_SMOKE_OWNER_ORGANIZATION_ID": smoke,
                    }
                )
                result = subprocess.run(
                    [str(script)],
                    env=environment,
                    capture_output=True,
                    text=True,
                    check=False,
                    timeout=10,
                )
                return result, json.loads(store.read_text())

            shortened, shortened_store = run_helper(
                rehearsal,
                {f"{prefix}0": rehearsal, f"{prefix}1": leftover},
            )
            self.assertEqual(0, shortened.returncode, shortened.stdout + shortened.stderr)
            self.assertEqual({f"{prefix}0": rehearsal}, shortened_store)
            self.assertIn("Deleted 1 stale pairing allowlist app setting(s).", shortened.stdout)
            self.assertIn("before=", shortened.stdout)
            self.assertNotIn(rehearsal, shortened.stdout + shortened.stderr)
            self.assertNotIn(leftover, shortened.stdout + shortened.stderr)
            self.assertIn("appsettings delete", call_log.read_text())

            cleared, cleared_store = run_helper(
                "",
                {f"{prefix}0": leftover, f"{prefix}1": rehearsal},
            )
            self.assertEqual(0, cleared.returncode, cleared.stdout + cleared.stderr)
            self.assertEqual({}, cleared_store)
            self.assertIn("Deleted 2 stale pairing allowlist app setting(s).", cleared.stdout)
            self.assertNotIn(rehearsal, cleared.stdout + cleared.stderr)
            self.assertNotIn(leftover, cleared.stdout + cleared.stderr)

            stale_handset, stale_store = run_helper(
                rehearsal,
                {f"{prefix}0": leftover},
            )
            self.assertEqual(0, stale_handset.returncode, stale_handset.stdout + stale_handset.stderr)
            self.assertEqual({f"{prefix}0": rehearsal}, stale_store)
            self.assertNotIn(leftover, stale_store.values())

            live_smoke, live_store = run_helper(smoke, {})
            self.assertNotEqual(0, live_smoke.returncode)
            self.assertIn("Live pairing allowlist includes the staging Hosted smoke owner organization", live_smoke.stdout + live_smoke.stderr)
            self.assertNotIn(smoke, live_smoke.stdout + live_smoke.stderr)
            self.assertEqual({f"{prefix}0": smoke}, live_store)

    def test_production_config_output_does_not_apply_lever_settings(self) -> None:
        check_script = self._deployment_config_script()
        rehearsal = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"
        smoke = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"
        helper = ROOT / "scripts" / "apply-staging-billing-lifecycle-lever-settings.sh"
        deploy = ROOT / "scripts" / "deploy-azure-elsa-control.sh"
        enabled_name = "Billing__StagingLifecycleLever__Enabled"
        prefix = "Billing__StagingLifecycleLever__AllowedOrganizationIds__"
        base = {
            "DEPLOY_MODE": "app",
            "AZURE_CLIENT_ID": "00000000-0000-0000-0000-000000000001",
            "AZURE_TENANT_ID": "00000000-0000-0000-0000-000000000002",
            "AZURE_SUBSCRIPTION_ID": "00000000-0000-0000-0000-000000000003",
            "AZURE_CONTAINER_REGISTRY_ENDPOINT": "test.azurecr.io",
            "AZURE_ENV_NAME": "prod",
            "AZURE_LOCATION": "westeurope",
            "AZURE_RESOURCE_GROUP": "rg-prod",
            "AZURE_WEBAPP_NAME": "prod-api",
            "TARGET_ENVIRONMENT": "production",
        }
        base.update(PRODUCTION_BILLING_FIXTURE)

        with tempfile.NamedTemporaryFile() as output, tempfile.NamedTemporaryFile() as github_env:
            environment = os.environ.copy()
            environment.update(base)
            environment.pop("STAGING_BILLING_LIFECYCLE_LEVER_ENABLED", None)
            environment.pop("STAGING_BILLING_LIFECYCLE_LEVER_ALLOWED_ORG_IDS", None)
            environment.pop("STAGING_RECOVERY_LIFECYCLE_LEVER_ENABLED", None)
            environment.pop("STAGING_RECOVERY_LIFECYCLE_LEVER_ALLOWED_INSTANCE_IDS", None)
            environment.pop("STAGING_SMOKE_OWNER_INSTANCE_ID", None)
            environment["GITHUB_OUTPUT"] = output.name
            environment["GITHUB_ENV"] = github_env.name
            config = subprocess.run(
                ["bash", "-c", check_script],
                env=environment,
                capture_output=True,
                text=True,
                check=False,
            )
            self.assertEqual(0, config.returncode, config.stdout + config.stderr)
            emitted = self._parse_kv(Path(output.name).read_text())

        self.assertEqual("", emitted.get("staging_billing_lever_enabled", "missing"))
        self.assertEqual("", emitted.get("staging_billing_lever_allowlist", "missing"))
        self.assertEqual("0", emitted.get("staging_billing_lever_allowlist_count", "missing"))
        self.assertEqual("", emitted.get("staging_recovery_lever_enabled", "missing"))
        self.assertEqual("", emitted.get("staging_recovery_lever_allowlist", "missing"))
        self.assertEqual("0", emitted.get("staging_recovery_lever_allowlist_count", "missing"))

        with tempfile.TemporaryDirectory() as temp_dir:
            temp_path = Path(temp_dir)
            store, call_log, fake_az = self._write_lever_fake_az(temp_path)
            store.write_text(json.dumps({}))
            helper_env = os.environ.copy()
            helper_env.update(
                {
                    "PATH": f"{temp_path}{os.pathsep}{helper_env['PATH']}",
                    "AZ_CALL_LOG": str(call_log),
                    "SETTINGS_STORE": str(store),
                    "AZURE_RESOURCE_GROUP": "prod-rg",
                    "AZURE_WEBAPP_NAME": "prod-api",
                    "TARGET_ENVIRONMENT": "production",
                    "STAGING_BILLING_LIFECYCLE_LEVER_ENABLED": emitted["staging_billing_lever_enabled"],
                    "STAGING_BILLING_LIFECYCLE_LEVER_ALLOWED_ORG_IDS": emitted["staging_billing_lever_allowlist"],
                    "STAGING_SMOKE_OWNER_ORGANIZATION_ID": smoke,
                }
            )
            helper_result = subprocess.run(
                [str(helper)],
                env=helper_env,
                capture_output=True,
                text=True,
                check=False,
                timeout=10,
            )
            self.assertEqual(0, helper_result.returncode, helper_result.stdout + helper_result.stderr)
            self.assertEqual({}, json.loads(store.read_text()))
            self.assertNotIn("appsettings set", call_log.read_text())
            self.assertNotIn(enabled_name, helper_result.stdout + helper_result.stderr)

            deploy_log = temp_path / "deploy-az-calls"
            (temp_path / "az").write_text(
                "#!/usr/bin/env bash\n"
                "set -euo pipefail\n"
                "printf '%s\\n' \"$*\" >> \"${AZ_CALL_LOG:?}\"\n"
                "case \"$*\" in\n"
                "  'account set --subscription '*) exit 0 ;;\n"
                "  'deployment sub what-if '*) exit 0 ;;\n"
                "  *) exit 41 ;;\n"
                "esac\n"
            )
            (temp_path / "az").chmod(0o755)
            deploy_env = os.environ.copy()
            for name in (
                "STAGING_BILLING_LIFECYCLE_LEVER_ENABLED",
                "STAGING_BILLING_LIFECYCLE_LEVER_ALLOWED_ORG_IDS",
                "TARGET_ENVIRONMENT",
            ):
                deploy_env.pop(name, None)
            deploy_env.update(
                {
                    "PATH": f"{temp_path}{os.pathsep}{deploy_env['PATH']}",
                    "AZ_CALL_LOG": str(deploy_log),
                    "ADMIN_API_KEY": "test-only-admin-key",
                    "BUILDER_CLIENT_API_KEY": "test-only-builder-key",
                    "CONTROL_ENTRA_TENANT_ID": "00000000-0000-0000-0000-000000000001",
                    "CONTROL_ENTRA_CLIENT_ID": "00000000-0000-0000-0000-000000000002",
                    "CONTROL_ENTRA_CLIENT_SECRET": "test-only-client-secret",
                    "AZURE_SUBSCRIPTION_ID": "00000000-0000-0000-0000-000000000003",
                    "TARGET_ENVIRONMENT": "production",
                    "STAGING_BILLING_LIFECYCLE_LEVER_ENABLED": emitted["staging_billing_lever_enabled"],
                    "STAGING_BILLING_LIFECYCLE_LEVER_ALLOWED_ORG_IDS": emitted["staging_billing_lever_allowlist"],
                }
            )
            deploy_result = subprocess.run(
                [str(deploy), "--environment", "prod", "--what-if"],
                cwd=ROOT,
                env=deploy_env,
                capture_output=True,
                text=True,
                check=False,
            )
            self.assertEqual(0, deploy_result.returncode, deploy_result.stdout + deploy_result.stderr)
            self.assertIn("deployment sub what-if", deploy_log.read_text())
            self.assertNotIn("stagingbillinglever", deploy_log.read_text())
            self.assertNotIn(rehearsal, helper_result.stdout + helper_result.stderr + deploy_result.stdout + deploy_result.stderr)

        with tempfile.NamedTemporaryFile() as output, tempfile.NamedTemporaryFile() as github_env:
            environment = os.environ.copy()
            environment.update(base)
            environment.update(
                {
                    "TARGET_ENVIRONMENT": "test",
                    "AZURE_ENV_NAME": "test",
                    "AZURE_RESOURCE_GROUP": "rg-test",
                    "AZURE_WEBAPP_NAME": "test-api",
                    "ELSA_CLOUD_STAGING_ORIGIN": "https://staging.example.test",
                    "STRIPE_HOSTED_PRICE_ID": "price_test",
                    "STRIPE_TEST_SECRET_KEY": "sk_test_fixture",
                    "STRIPE_TEST_WEBHOOK_SIGNING_SECRET": "whsec_fixture",
                }
            )
            environment.pop("STAGING_BILLING_LIFECYCLE_LEVER_ENABLED", None)
            environment.pop("STAGING_BILLING_LIFECYCLE_LEVER_ALLOWED_ORG_IDS", None)
            environment["GITHUB_OUTPUT"] = output.name
            environment["GITHUB_ENV"] = github_env.name
            staging_config = subprocess.run(
                ["bash", "-c", check_script],
                env=environment,
                capture_output=True,
                text=True,
                check=False,
            )
            self.assertEqual(0, staging_config.returncode, staging_config.stdout + staging_config.stderr)
            staging_emitted = self._parse_kv(Path(output.name).read_text())

        self.assertEqual("", staging_emitted.get("staging_billing_lever_enabled", "missing"))
        self.assertEqual("", staging_emitted.get("staging_billing_lever_allowlist", "missing"))

        with tempfile.TemporaryDirectory() as temp_dir:
            temp_path = Path(temp_dir)
            store, call_log, _ = self._write_lever_fake_az(temp_path)
            store.write_text(json.dumps({enabled_name: "true", f"{prefix}0": rehearsal}))
            helper_env = os.environ.copy()
            helper_env.update(
                {
                    "PATH": f"{temp_path}{os.pathsep}{helper_env['PATH']}",
                    "AZ_CALL_LOG": str(call_log),
                    "SETTINGS_STORE": str(store),
                    "AZURE_RESOURCE_GROUP": "rg-test",
                    "AZURE_WEBAPP_NAME": "test-api",
                    "TARGET_ENVIRONMENT": "test",
                    "STAGING_BILLING_LIFECYCLE_LEVER_ENABLED": staging_emitted["staging_billing_lever_enabled"],
                    "STAGING_BILLING_LIFECYCLE_LEVER_ALLOWED_ORG_IDS": staging_emitted["staging_billing_lever_allowlist"],
                    "STAGING_SMOKE_OWNER_ORGANIZATION_ID": smoke,
                }
            )
            stale_result = subprocess.run(
                [str(helper)],
                env=helper_env,
                capture_output=True,
                text=True,
                check=False,
                timeout=10,
            )
            self.assertEqual(0, stale_result.returncode, stale_result.stdout + stale_result.stderr)
            self.assertEqual({}, json.loads(store.read_text()))
            self.assertIn("appsettings delete", call_log.read_text())
            self.assertNotIn("appsettings set", call_log.read_text())
            self.assertNotIn(rehearsal, stale_result.stdout + stale_result.stderr)

        for raw_enabled in ("false", "FALSE"):
            with self.subTest(raw_enabled=raw_enabled), tempfile.TemporaryDirectory() as temp_dir:
                temp_path = Path(temp_dir)
                store, call_log, _ = self._write_lever_fake_az(temp_path)
                store.write_text(json.dumps({f"{prefix}0": rehearsal, enabled_name: "true"}))
                helper_env = os.environ.copy()
                helper_env.update(
                    {
                        "PATH": f"{temp_path}{os.pathsep}{helper_env['PATH']}",
                        "AZ_CALL_LOG": str(call_log),
                        "SETTINGS_STORE": str(store),
                        "AZURE_RESOURCE_GROUP": "prod-rg",
                        "AZURE_WEBAPP_NAME": "prod-api",
                        "TARGET_ENVIRONMENT": "production",
                        "STAGING_BILLING_LIFECYCLE_LEVER_ENABLED": raw_enabled,
                        "STAGING_BILLING_LIFECYCLE_LEVER_ALLOWED_ORG_IDS": "",
                    }
                )
                result = subprocess.run(
                    [str(helper)],
                    env=helper_env,
                    capture_output=True,
                    text=True,
                    check=False,
                    timeout=10,
                )
                self.assertEqual(0, result.returncode, result.stdout + result.stderr)
                self.assertEqual({}, json.loads(store.read_text()))
                self.assertNotIn("appsettings set", call_log.read_text())

    def test_lever_helper_applies_on_test_and_refuses_when_production_is_enabled(self) -> None:
        rehearsal = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"
        smoke = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"
        helper = ROOT / "scripts" / "apply-staging-billing-lifecycle-lever-settings.sh"
        enabled_name = "Billing__StagingLifecycleLever__Enabled"
        prefix = "Billing__StagingLifecycleLever__AllowedOrganizationIds__"

        with tempfile.TemporaryDirectory() as temp_dir:
            temp_path = Path(temp_dir)
            store, call_log, _ = self._write_lever_fake_az(temp_path)

            def run_helper(target: str, enabled: str, allowlist: str, initial: dict[str, str]) -> subprocess.CompletedProcess[str]:
                store.write_text(json.dumps(initial))
                call_log.write_text("")
                environment = os.environ.copy()
                environment.update(
                    {
                        "PATH": f"{temp_path}{os.pathsep}{environment['PATH']}",
                        "AZ_CALL_LOG": str(call_log),
                        "SETTINGS_STORE": str(store),
                        "AZURE_RESOURCE_GROUP": "test-rg",
                        "AZURE_WEBAPP_NAME": "test-api",
                        "TARGET_ENVIRONMENT": target,
                        "STAGING_BILLING_LIFECYCLE_LEVER_ENABLED": enabled,
                        "STAGING_BILLING_LIFECYCLE_LEVER_ALLOWED_ORG_IDS": allowlist,
                        "STAGING_SMOKE_OWNER_ORGANIZATION_ID": smoke,
                    }
                )
                return subprocess.run(
                    [str(helper)],
                    env=environment,
                    capture_output=True,
                    text=True,
                    check=False,
                    timeout=10,
                )

            applied = run_helper("test", "true", rehearsal, {})
            self.assertEqual(0, applied.returncode, applied.stdout + applied.stderr)
            self.assertEqual(
                {enabled_name: "true", f"{prefix}0": rehearsal},
                json.loads(store.read_text()),
            )
            self.assertIn("appsettings set", call_log.read_text())
            self.assertNotIn(rehearsal, applied.stdout + applied.stderr)

            stale_cleared = run_helper("test", "", "", {enabled_name: "true", f"{prefix}0": rehearsal})
            self.assertEqual(0, stale_cleared.returncode, stale_cleared.stdout + stale_cleared.stderr)
            self.assertEqual({}, json.loads(store.read_text()))
            self.assertIn("appsettings delete", call_log.read_text())
            self.assertNotIn("appsettings set", call_log.read_text())

            refused = run_helper("production", "true", rehearsal, {})
            self.assertNotEqual(0, refused.returncode)
            self.assertIn("must be unset", refused.stdout + refused.stderr)
            self.assertEqual({}, json.loads(store.read_text()))
            self.assertEqual("", call_log.read_text())
            self.assertNotIn(rehearsal, refused.stdout + refused.stderr)

            staging_env = os.environ.copy()
            staging_env.update(
                {
                    "PATH": f"{temp_path}{os.pathsep}{staging_env['PATH']}",
                    "AZ_CALL_LOG": str(call_log),
                    "SETTINGS_STORE": str(store),
                    "AZURE_RESOURCE_GROUP": "rg-valence-control-staging",
                    "AZURE_WEBAPP_NAME": "test-api",
                    "TARGET_ENVIRONMENT": "test",
                    "AZURE_ENV_NAME": "valence-control-staging",
                    "STAGING_BILLING_LIFECYCLE_LEVER_ENABLED": "true",
                    "STAGING_BILLING_LIFECYCLE_LEVER_ALLOWED_ORG_IDS": rehearsal,
                    "STAGING_SMOKE_OWNER_ORGANIZATION_ID": smoke,
                }
            )
            store.write_text(json.dumps({}))
            call_log.write_text("")
            real_staging = subprocess.run(
                [str(helper)],
                env=staging_env,
                capture_output=True,
                text=True,
                check=False,
                timeout=10,
            )
            self.assertEqual(0, real_staging.returncode, real_staging.stdout + real_staging.stderr)
            self.assertEqual(
                {enabled_name: "true", f"{prefix}0": rehearsal},
                json.loads(store.read_text()),
            )
            self.assertNotIn(rehearsal, real_staging.stdout + real_staging.stderr)

            staging_env["TARGET_ENVIRONMENT"] = "production"
            store.write_text(json.dumps({}))
            call_log.write_text("")
            production_with_staging_name = subprocess.run(
                [str(helper)],
                env=staging_env,
                capture_output=True,
                text=True,
                check=False,
                timeout=10,
            )
            self.assertNotEqual(0, production_with_staging_name.returncode)
            self.assertIn("must be unset", production_with_staging_name.stdout + production_with_staging_name.stderr)
            self.assertEqual({}, json.loads(store.read_text()))
            self.assertEqual("", call_log.read_text())

    def test_recovery_lever_helper_applies_on_test_and_refuses_when_production_is_enabled(self) -> None:
        rehearsal = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"
        smoke = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"
        helper = ROOT / "scripts" / "apply-staging-recovery-lifecycle-lever-settings.sh"
        enabled_name = "Staging__RecoveryLifecycleLever__Enabled"
        prefix = "Staging__RecoveryLifecycleLever__AllowedInstanceIds__"
        smoke_name = "Staging__RecoveryLifecycleLever__SmokeOwnerInstanceId"

        with tempfile.TemporaryDirectory() as temp_dir:
            temp_path = Path(temp_dir)
            store, call_log, _ = self._write_recovery_lever_fake_az(temp_path)

            def run_helper(
                target: str,
                enabled: str,
                allowlist: str,
                initial: dict[str, str],
                smoke_owner: str = smoke,
            ) -> subprocess.CompletedProcess[str]:
                store.write_text(json.dumps(initial))
                call_log.write_text("")
                environment = os.environ.copy()
                environment.update(
                    {
                        "PATH": f"{temp_path}{os.pathsep}{environment['PATH']}",
                        "AZ_CALL_LOG": str(call_log),
                        "SETTINGS_STORE": str(store),
                        "AZURE_RESOURCE_GROUP": "test-rg",
                        "AZURE_WEBAPP_NAME": "test-api",
                        "TARGET_ENVIRONMENT": target,
                        "STAGING_RECOVERY_LIFECYCLE_LEVER_ENABLED": enabled,
                        "STAGING_RECOVERY_LIFECYCLE_LEVER_ALLOWED_INSTANCE_IDS": allowlist,
                        "STAGING_SMOKE_OWNER_INSTANCE_ID": smoke_owner,
                    }
                )
                return subprocess.run(
                    [str(helper)],
                    env=environment,
                    capture_output=True,
                    text=True,
                    check=False,
                    timeout=10,
                )

            applied = run_helper("test", "true", rehearsal, {})
            self.assertEqual(0, applied.returncode, applied.stdout + applied.stderr)
            self.assertEqual(
                {enabled_name: "true", smoke_name: smoke, f"{prefix}0": rehearsal},
                json.loads(store.read_text()),
            )
            self.assertIn("appsettings set", call_log.read_text())
            self.assertNotIn(rehearsal, applied.stdout + applied.stderr)

            stale_cleared = run_helper(
                "test",
                "",
                "",
                {enabled_name: "true", smoke_name: smoke, f"{prefix}0": rehearsal},
                smoke_owner="",
            )
            self.assertEqual(0, stale_cleared.returncode, stale_cleared.stdout + stale_cleared.stderr)
            self.assertEqual({}, json.loads(store.read_text()))
            self.assertIn("appsettings delete", call_log.read_text())
            self.assertNotIn("appsettings set", call_log.read_text())

            refused = run_helper("production", "true", rehearsal, {})
            self.assertNotEqual(0, refused.returncode)
            self.assertIn("must be unset", refused.stdout + refused.stderr)
            self.assertEqual({}, json.loads(store.read_text()))
            self.assertEqual("", call_log.read_text())
            self.assertNotIn(rehearsal, refused.stdout + refused.stderr)

    @staticmethod
    def _parse_kv(text: str) -> dict[str, str]:
        values: dict[str, str] = {}
        for line in text.splitlines():
            if "=" not in line:
                continue
            key, value = line.split("=", 1)
            values[key] = value
        return values

    @staticmethod
    def _write_lever_fake_az(temp_path: Path) -> tuple[Path, Path, Path]:
        store = temp_path / "settings.json"
        call_log = temp_path / "az-calls"
        fake_az = temp_path / "az"
        fake_az.write_text(
            """#!/usr/bin/env python3
import json, os, sys
open(os.environ["AZ_CALL_LOG"], "a", encoding="utf-8").write(" ".join(sys.argv[1:]) + "\\n")
store_path = os.environ["SETTINGS_STORE"]
store = json.loads(open(store_path, encoding="utf-8").read())
joined = " ".join(sys.argv[1:])
prefix = "Billing__StagingLifecycleLever__AllowedOrganizationIds__"
enabled = "Billing__StagingLifecycleLever__Enabled"
items = {name: value for name, value in store.items() if name.startswith(prefix) or name == enabled}
if "appsettings list" in joined:
    if "].name" in joined:
        print("\\n".join(items))
    elif "].value" in joined:
        print("\\n".join(items.values()))
    else:
        print(len(items))
elif "appsettings delete" in joined:
    args = sys.argv[1:]
    names = []
    for item in args[args.index("--setting-names") + 1:]:
        if item.startswith("--"):
            break
        names.append(item)
    for name in names:
        store.pop(name, None)
    open(store_path, "w", encoding="utf-8").write(json.dumps(store))
elif "appsettings set" in joined:
    args = sys.argv[1:]
    values = []
    for item in args[args.index("--settings") + 1:]:
        if item.startswith("--"):
            break
        values.append(item)
    for item in values:
        name, value = item.split("=", 1)
        store[name] = value
    open(store_path, "w", encoding="utf-8").write(json.dumps(store))
else:
    sys.exit(1)
"""
        )
        fake_az.chmod(0o755)
        return store, call_log, fake_az

    @staticmethod
    def _write_recovery_lever_fake_az(temp_path: Path) -> tuple[Path, Path, Path]:
        store = temp_path / "settings.json"
        call_log = temp_path / "az-calls"
        fake_az = temp_path / "az"
        fake_az.write_text(
            """#!/usr/bin/env python3
import json, os, sys
open(os.environ["AZ_CALL_LOG"], "a", encoding="utf-8").write(" ".join(sys.argv[1:]) + "\\n")
store_path = os.environ["SETTINGS_STORE"]
store = json.loads(open(store_path, encoding="utf-8").read())
joined = " ".join(sys.argv[1:])
prefix = "Staging__RecoveryLifecycleLever__AllowedInstanceIds__"
enabled = "Staging__RecoveryLifecycleLever__Enabled"
smoke = "Staging__RecoveryLifecycleLever__SmokeOwnerInstanceId"
items = {name: value for name, value in store.items() if name.startswith(prefix) or name in (enabled, smoke)}
allowlist = {name: value for name, value in store.items() if name.startswith(prefix)}
if "appsettings list" in joined:
    if "starts_with(name, '%s')" % prefix in joined and "].value" in joined:
        print("\\n".join(allowlist.values()))
    elif "].name" in joined:
        print("\\n".join(items))
    elif "].value" in joined:
        print("\\n".join(allowlist.values()))
    else:
        print(len(items))
elif "appsettings delete" in joined:
    args = sys.argv[1:]
    names = []
    for item in args[args.index("--setting-names") + 1:]:
        if item.startswith("--"):
            break
        names.append(item)
    for name in names:
        store.pop(name, None)
    open(store_path, "w", encoding="utf-8").write(json.dumps(store))
elif "appsettings set" in joined:
    args = sys.argv[1:]
    values = []
    for item in args[args.index("--settings") + 1:]:
        if item.startswith("--"):
            break
        values.append(item)
    for item in values:
        name, value = item.split("=", 1)
        store[name] = value
    open(store_path, "w", encoding="utf-8").write(json.dumps(store))
else:
    sys.exit(1)
"""
        )
        fake_az.chmod(0o755)
        return store, call_log, fake_az


if __name__ == "__main__":
    unittest.main()
