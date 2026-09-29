#!/usr/bin/env python3
"""Offline contract checks for the Azure API deployment workflow."""

from __future__ import annotations

import json
import os
import subprocess
import tempfile
import unittest
from pathlib import Path
from textwrap import dedent


ROOT = Path(__file__).resolve().parents[2]
WORKFLOW = ROOT / ".github" / "workflows" / "azure-api-deploy.yml"


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
        self.assertIn(
            "Restore previous API deployment after deployment, configuration, or health failure",
            self.source,
        )

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
        self.assertIn("STAGING_SMOKE_OWNER_ORGANIZATION_ID: ${{ vars.STAGING_SMOKE_OWNER_ORGANIZATION_ID }}", self.source)
        self.assertIn(
            "STAGING_RECOVERY_REQUIRED_ALERT_RECIPIENT: ${{ vars.STAGING_RECOVERY_REQUIRED_ALERT_RECIPIENT }}",
            self.source,
        )
        self.assertIn(
            "PRODUCTION_RECOVERY_REQUIRED_ALERT_RECIPIENT: ${{ vars.PRODUCTION_RECOVERY_REQUIRED_ALERT_RECIPIENT }}",
            self.source,
        )
        self.assertIn("Staging never uses the production", self.source)
        self.assertNotEqual(
            "STAGING_RECOVERY_REQUIRED_ALERT_RECIPIENT",
            "PRODUCTION_RECOVERY_REQUIRED_ALERT_RECIPIENT",
        )
        self.assertIn(
            "EXTERNAL_ENGINE_PAIRING_ALLOWED_ORG_IDS: ${{ steps.deployment-config.outputs.pairing_allowlist }}",
            self.source,
        )
        self.assertIn("scripts/apply-external-engine-pairing-settings.sh", self.source)
        self.assertIn("scripts/apply-staging-billing-lifecycle-lever-settings.sh", self.source)
        self.assertIn(
            "STAGING_BILLING_LIFECYCLE_LEVER_ALLOWED_ORG_IDS: ${{ steps.deployment-config.outputs.staging_billing_lever_allowlist }}",
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
            ) -> subprocess.CompletedProcess[str]:
                call_log.unlink(missing_ok=True)
                environment = os.environ.copy()
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

        with tempfile.NamedTemporaryFile() as output, tempfile.NamedTemporaryFile() as github_env:
            environment = os.environ.copy()
            environment.update(base)
            environment.pop("STAGING_BILLING_LIFECYCLE_LEVER_ENABLED", None)
            environment.pop("STAGING_BILLING_LIFECYCLE_LEVER_ALLOWED_ORG_IDS", None)
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


if __name__ == "__main__":
    unittest.main()
