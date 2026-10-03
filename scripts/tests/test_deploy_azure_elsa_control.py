#!/usr/bin/env python3
"""Offline contracts for the current Aspire-generated Azure deployment helper."""

from __future__ import annotations

import os
import subprocess
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
DEPLOY_SCRIPT = ROOT / "scripts" / "deploy-azure-elsa-control.sh"


class DeployAzureElsaControlTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.source = DEPLOY_SCRIPT.read_text()

    @staticmethod
    def environment() -> dict[str, str]:
        environment = os.environ.copy()
        environment.pop("TARGET_ENVIRONMENT", None)
        environment.pop("AZURE_ENV_NAME", None)
        environment.update(
            {
                "ADMIN_API_KEY": "test-only-admin-key",
                "BUILDER_CLIENT_API_KEY": "test-only-builder-key",
                "CONTROL_ENTRA_TENANT_ID": "00000000-0000-0000-0000-000000000001",
                "CONTROL_ENTRA_CLIENT_ID": "00000000-0000-0000-0000-000000000002",
                "CONTROL_ENTRA_CLIENT_SECRET": "test-only-client-secret",
                "AZURE_SUBSCRIPTION_ID": "00000000-0000-0000-0000-000000000003",
            }
        )
        return environment

    @staticmethod
    def write_fake_az(temporary_path: Path) -> Path:
        fake_az = temporary_path / "az"
        fake_az.write_text(
            "#!/usr/bin/env bash\n"
            "set -euo pipefail\n"
            "printf '%s\\n' \"$*\" >> \"${AZ_CALL_LOG:?}\"\n"
            "dump_parameters() {\n"
            "  local arg params=''\n"
            "  for arg in \"$@\"; do\n"
            "    case \"$arg\" in\n"
            "      @*) params=\"${arg#@}\" ;;\n"
            "    esac\n"
            "  done\n"
            "  if [ -n \"$params\" ]; then\n"
            "    printf 'BASE_PARAMETERS ' >> \"${AZ_CALL_LOG}\"\n"
            "    cat \"$params\" >> \"${AZ_CALL_LOG}\"\n"
            "    printf '\\n' >> \"${AZ_CALL_LOG}\"\n"
            "  fi\n"
            "}\n"
            "case \"$*\" in\n"
            "  'account set --subscription '*) exit 0 ;;\n"
            "  'deployment sub what-if '*) dump_parameters \"$@\" ; exit 0 ;;\n"
            "  'deployment sub create '*) dump_parameters \"$@\" ; exit 0 ;;\n"
            "  *) exit 41 ;;\n"
            "esac\n"
        )
        fake_az.chmod(0o755)
        return fake_az

    def run_deploy(self, environment: dict[str, str], *args: str) -> subprocess.CompletedProcess[str]:
        return subprocess.run(
            [str(DEPLOY_SCRIPT), *args],
            cwd=ROOT,
            env=environment,
            capture_output=True,
            text=True,
            check=False,
        )

    def test_uses_current_subscription_and_api_modules_with_immutable_image(self) -> None:
        self.assertIn("az deployment sub create", self.source)
        self.assertIn("--base-only", self.source)
        self.assertIn('if [[ "$BASE_ONLY" == true ]]', self.source)
        self.assertIn("--template-file infra/main.bicep", self.source)
        self.assertIn("az deployment group create", self.source)
        self.assertIn("--template-file infra/api/api-website.module.bicep", self.source)
        self.assertIn("pairingallowedorganizationids_value", self.source)
        self.assertIn("EXTERNAL_ENGINE_PAIRING_ALLOWED_ORG_IDS", self.source)
        self.assertIn("stagingbillingleverenabled_value", self.source)
        self.assertIn("stagingbillingleverallowedorganizationids_value", self.source)
        self.assertIn("STAGING_BILLING_LIFECYCLE_LEVER_ENABLED", self.source)
        self.assertIn("STAGING_BILLING_LIFECYCLE_LEVER_ALLOWED_ORG_IDS", self.source)
        self.assertIn("STAGING_RECOVERY_LIFECYCLE_LEVER_ENABLED", self.source)
        self.assertIn("STAGING_RECOVERY_LIFECYCLE_LEVER_ALLOWED_INSTANCE_IDS", self.source)
        self.assertIn("STAGING_SMOKE_OWNER_INSTANCE_ID", self.source)
        self.assertIn("scripts/lib/staging-lever-target.sh", self.source)
        self.assertEqual(3, self.source.count("is_staging_billing_lever_target"))
        self.assertIn("EXTERNAL_ENGINE_PAIRING_ALLOWED_ORG_IDS\" ]] && ! is_staging_billing_lever_target", self.source)
        self.assertNotIn("is_staging_pairing_allowlist_target", self.source)
        self.assertIn("from control_sql_sku import sku_parameters", self.source)
        self.assertIn("IMAGE=\"$IMAGE_REPOSITORY@$IMAGE_DIGEST\"", self.source)
        self.assertIn("AZURE_CONTAINER_REGISTRY_ENDPOINT", self.source)
        self.assertIn("CONTROL_SQL_SQLSERVERFQDN", self.source)
        self.assertNotIn("SQL_ADMINISTRATOR_PASSWORD", self.source)
        self.assertNotIn("containerRegistryLoginServer", self.source)

    def test_refuses_a_pairing_allowlist_unless_the_target_is_staging(self) -> None:
        environment = self.environment()
        environment["EXTERNAL_ENGINE_PAIRING_ALLOWED_ORG_IDS"] = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"
        result = self.run_deploy(environment, "--environment", "prod")
        self.assertNotEqual(0, result.returncode)
        self.assertIn("only permitted for the test (staging) environment", result.stderr)
        self.assertNotIn("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", result.stdout + result.stderr)

        production = self.run_deploy(environment, "--environment", "production")
        self.assertNotEqual(0, production.returncode)
        self.assertIn("only permitted for the test (staging) environment", production.stderr)

    def test_refuses_a_pairing_allowlist_for_bare_environment_test(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            temporary_path = Path(temporary)
            call_log = temporary_path / "az-calls"
            self.write_fake_az(temporary_path)
            environment = self.environment()
            environment["PATH"] = f"{temporary_path}{os.pathsep}{environment['PATH']}"
            environment["AZ_CALL_LOG"] = str(call_log)
            environment["EXTERNAL_ENGINE_PAIRING_ALLOWED_ORG_IDS"] = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"
            result = self.run_deploy(environment, "--environment", "test", "--what-if")
            self.assertNotEqual(0, result.returncode)
            self.assertIn("only permitted for the test (staging) environment", result.stderr)
            self.assertNotIn("deployment sub", call_log.read_text() if call_log.exists() else "")
            self.assertNotIn("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", result.stdout + result.stderr)

    def test_allows_a_pairing_allowlist_for_the_real_staging_azure_env_name(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            temporary_path = Path(temporary)
            call_log = temporary_path / "az-calls"
            self.write_fake_az(temporary_path)
            environment = self.environment()
            environment["PATH"] = f"{temporary_path}{os.pathsep}{environment['PATH']}"
            environment["AZ_CALL_LOG"] = str(call_log)
            environment["EXTERNAL_ENGINE_PAIRING_ALLOWED_ORG_IDS"] = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"
            result = self.run_deploy(
                environment, "--environment", "valence-control-staging", "--what-if"
            )
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
            self.assertIn("deployment sub what-if", call_log.read_text())
            self.assertNotIn("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", result.stdout + result.stderr)

            environment["TARGET_ENVIRONMENT"] = "test"
            workflow_staging = self.run_deploy(
                environment, "--environment", "valence-control-staging", "--what-if"
            )
            self.assertEqual(0, workflow_staging.returncode, workflow_staging.stdout + workflow_staging.stderr)

            environment["TARGET_ENVIRONMENT"] = "production"
            refused = self.run_deploy(environment, "--environment", "valence-control-staging")
            self.assertNotEqual(0, refused.returncode)
            self.assertIn("Staging lever target names disagree", refused.stderr)
            self.assertNotIn("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", refused.stdout + refused.stderr)

    def test_empty_pairing_allowlist_is_accepted_for_every_target(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            temporary_path = Path(temporary)
            call_log = temporary_path / "az-calls"
            self.write_fake_az(temporary_path)

            for name in ("prod", "production", "test", "valence-control-staging"):
                with self.subTest(environment=name):
                    environment = self.environment()
                    environment["PATH"] = f"{temporary_path}{os.pathsep}{environment['PATH']}"
                    environment["AZ_CALL_LOG"] = str(call_log)
                    environment["EXTERNAL_ENGINE_PAIRING_ALLOWED_ORG_IDS"] = ""
                    call_log.write_text("")
                    result = self.run_deploy(environment, "--environment", name, "--what-if")
                    self.assertEqual(0, result.returncode, result.stderr)
                    self.assertIn("deployment sub what-if", call_log.read_text())

    def test_refuses_the_staging_billing_lifecycle_lever_unless_the_target_is_staging(self) -> None:
        environment = self.environment()
        environment["STAGING_BILLING_LIFECYCLE_LEVER_ENABLED"] = "true"
        environment["STAGING_BILLING_LIFECYCLE_LEVER_ALLOWED_ORG_IDS"] = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"
        result = self.run_deploy(environment, "--environment", "prod")
        self.assertNotEqual(0, result.returncode)
        self.assertIn("only permitted for the test (staging) environment", result.stderr)
        self.assertNotIn("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", result.stdout + result.stderr)

    def test_allows_the_real_staging_azure_env_name_for_the_lever(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            temporary_path = Path(temporary)
            call_log = temporary_path / "az-calls"
            self.write_fake_az(temporary_path)

            environment = self.environment()
            environment["PATH"] = f"{temporary_path}{os.pathsep}{environment['PATH']}"
            environment["AZ_CALL_LOG"] = str(call_log)
            environment["STAGING_BILLING_LIFECYCLE_LEVER_ENABLED"] = "true"
            environment["STAGING_BILLING_LIFECYCLE_LEVER_ALLOWED_ORG_IDS"] = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"
            result = self.run_deploy(
                environment, "--environment", "valence-control-staging", "--what-if"
            )
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
            self.assertIn("deployment sub what-if", call_log.read_text())
            self.assertNotIn("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", result.stdout + result.stderr)

            environment["TARGET_ENVIRONMENT"] = "production"
            refused = self.run_deploy(environment, "--environment", "valence-control-staging")
            self.assertNotEqual(0, refused.returncode)
            self.assertIn("Staging lever target names disagree", refused.stderr)
            self.assertNotIn("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", refused.stdout + refused.stderr)

            environment["TARGET_ENVIRONMENT"] = "test"
            allowed = self.run_deploy(
                environment, "--environment", "valence-control-staging", "--what-if"
            )
            self.assertEqual(0, allowed.returncode, allowed.stdout + allowed.stderr)

    def test_refuses_the_staging_recovery_lifecycle_lever_unless_the_target_is_staging(self) -> None:
        environment = self.environment()
        environment["STAGING_RECOVERY_LIFECYCLE_LEVER_ENABLED"] = "true"
        environment["STAGING_RECOVERY_LIFECYCLE_LEVER_ALLOWED_INSTANCE_IDS"] = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"
        result = self.run_deploy(environment, "--environment", "prod")
        self.assertNotEqual(0, result.returncode)
        self.assertIn("only permitted for the test (staging) environment", result.stderr)
        self.assertNotIn("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", result.stdout + result.stderr)

        billing_only = self.environment()
        billing_only["STAGING_BILLING_LIFECYCLE_LEVER_ENABLED"] = "true"
        billing_only["STAGING_BILLING_LIFECYCLE_LEVER_ALLOWED_ORG_IDS"] = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"
        billing_only.pop("STAGING_RECOVERY_LIFECYCLE_LEVER_ENABLED", None)
        billing_only.pop("STAGING_RECOVERY_LIFECYCLE_LEVER_ALLOWED_INSTANCE_IDS", None)
        with tempfile.TemporaryDirectory() as temporary:
            temporary_path = Path(temporary)
            call_log = temporary_path / "az-calls"
            self.write_fake_az(temporary_path)
            billing_only["PATH"] = f"{temporary_path}{os.pathsep}{billing_only['PATH']}"
            billing_only["AZ_CALL_LOG"] = str(call_log)
            allowed = self.run_deploy(billing_only, "--environment", "valence-control-staging", "--what-if")
            self.assertEqual(0, allowed.returncode, allowed.stdout + allowed.stderr)
            self.assertNotIn("stagingrecoverylever", call_log.read_text())

    def test_allows_the_real_staging_azure_env_name_for_the_recovery_lever(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            temporary_path = Path(temporary)
            call_log = temporary_path / "az-calls"
            self.write_fake_az(temporary_path)

            environment = self.environment()
            environment["PATH"] = f"{temporary_path}{os.pathsep}{environment['PATH']}"
            environment["AZ_CALL_LOG"] = str(call_log)
            environment["STAGING_RECOVERY_LIFECYCLE_LEVER_ENABLED"] = "true"
            environment["STAGING_RECOVERY_LIFECYCLE_LEVER_ALLOWED_INSTANCE_IDS"] = (
                "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"
            )
            result = self.run_deploy(
                environment, "--environment", "valence-control-staging", "--what-if"
            )
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
            self.assertIn("deployment sub what-if", call_log.read_text())
            self.assertNotIn("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", result.stdout + result.stderr)

            environment["TARGET_ENVIRONMENT"] = "production"
            refused = self.run_deploy(environment, "--environment", "valence-control-staging")
            self.assertNotEqual(0, refused.returncode)
            self.assertIn("Staging lever target names disagree", refused.stderr)
            self.assertNotIn("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", refused.stdout + refused.stderr)

    def test_refuses_when_explicit_environment_disagrees_with_azure_env_name(self) -> None:
        environment = self.environment()
        environment["AZURE_ENV_NAME"] = "valence-control-staging"
        environment["STAGING_RECOVERY_LIFECYCLE_LEVER_ENABLED"] = "true"
        environment["STAGING_RECOVERY_LIFECYCLE_LEVER_ALLOWED_INSTANCE_IDS"] = (
            "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"
        )
        result = self.run_deploy(environment, "--environment", "valence-control-production")
        self.assertNotEqual(0, result.returncode)
        self.assertIn("Staging lever target names disagree", result.stderr)
        self.assertNotIn("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", result.stdout + result.stderr)

        mismatched = self.environment()
        mismatched["TARGET_ENVIRONMENT"] = "test"
        mismatched["AZURE_ENV_NAME"] = "valence-control-staging"
        mismatched["STAGING_RECOVERY_LIFECYCLE_LEVER_ENABLED"] = "true"
        mismatched["STAGING_RECOVERY_LIFECYCLE_LEVER_ALLOWED_INSTANCE_IDS"] = (
            "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"
        )
        refused = self.run_deploy(mismatched, "--environment", "valence-control-production")
        self.assertNotEqual(0, refused.returncode)
        self.assertIn("Staging lever target names disagree", refused.stderr)
        self.assertNotIn("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", refused.stdout + refused.stderr)

    def test_refuses_when_target_is_a_staging_azure_name_and_azure_env_is_not(self) -> None:
        for azure_name in ("elsa-control", "dev"):
            with self.subTest(environment=azure_name):
                with tempfile.TemporaryDirectory() as temporary:
                    temporary_path = Path(temporary)
                    call_log = temporary_path / "az-calls"
                    self.write_fake_az(temporary_path)
                    environment = self.environment()
                    environment["PATH"] = f"{temporary_path}{os.pathsep}{environment['PATH']}"
                    environment["AZ_CALL_LOG"] = str(call_log)
                    environment["TARGET_ENVIRONMENT"] = "valence-control-staging"
                    environment["STAGING_RECOVERY_LIFECYCLE_LEVER_ENABLED"] = "true"
                    environment["STAGING_RECOVERY_LIFECYCLE_LEVER_ALLOWED_INSTANCE_IDS"] = (
                        "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"
                    )
                    result = self.run_deploy(environment, "--environment", azure_name, "--what-if")
                    self.assertNotEqual(0, result.returncode)
                    self.assertIn(
                        "are only permitted for the test (staging) environment",
                        result.stderr,
                    )
                    self.assertNotIn("deployment sub", call_log.read_text() if call_log.exists() else "")
                    self.assertNotIn("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", result.stdout + result.stderr)

    def test_allows_the_workflow_shape_for_the_recovery_lever(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            temporary_path = Path(temporary)
            call_log = temporary_path / "az-calls"
            self.write_fake_az(temporary_path)
            environment = self.environment()
            environment["PATH"] = f"{temporary_path}{os.pathsep}{environment['PATH']}"
            environment["AZ_CALL_LOG"] = str(call_log)
            environment["TARGET_ENVIRONMENT"] = "test"
            environment["AZURE_ENV_NAME"] = "valence-control-staging"
            environment["STAGING_RECOVERY_LIFECYCLE_LEVER_ENABLED"] = "true"
            environment["STAGING_RECOVERY_LIFECYCLE_LEVER_ALLOWED_INSTANCE_IDS"] = (
                "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"
            )
            self.assertEqual("valence-control-staging", environment["AZURE_ENV_NAME"])
            result = self.run_deploy(
                environment, "--environment", "valence-control-staging", "--what-if"
            )
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
            self.assertIn("deployment sub what-if", call_log.read_text())
            self.assertIn("stagingrecoveryleverenabled_value", self.source)
            self.assertNotIn("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", result.stdout + result.stderr)

    def test_refuses_the_recovery_lever_for_bare_environment_test(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            temporary_path = Path(temporary)
            call_log = temporary_path / "az-calls"
            self.write_fake_az(temporary_path)
            environment = self.environment()
            environment["PATH"] = f"{temporary_path}{os.pathsep}{environment['PATH']}"
            environment["AZ_CALL_LOG"] = str(call_log)
            environment["STAGING_RECOVERY_LIFECYCLE_LEVER_ENABLED"] = "true"
            environment["STAGING_RECOVERY_LIFECYCLE_LEVER_ALLOWED_INSTANCE_IDS"] = (
                "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"
            )
            self.assertNotIn("AZURE_ENV_NAME", environment)
            result = self.run_deploy(environment, "--environment", "test", "--what-if")
            self.assertNotEqual(0, result.returncode)
            self.assertIn(
                "are only permitted for the test (staging) environment",
                result.stderr,
            )
            self.assertNotIn("deployment sub", call_log.read_text() if call_log.exists() else "")
            self.assertNotIn("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", result.stdout + result.stderr)

    def test_production_target_with_elsa_control_deploys_without_the_lever(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            temporary_path = Path(temporary)
            call_log = temporary_path / "az-calls"
            self.write_fake_az(temporary_path)
            environment = self.environment()
            environment["PATH"] = f"{temporary_path}{os.pathsep}{environment['PATH']}"
            environment["AZ_CALL_LOG"] = str(call_log)
            environment["TARGET_ENVIRONMENT"] = "production"
            result = self.run_deploy(environment, "--environment", "elsa-control", "--what-if")
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
            calls = call_log.read_text()
            self.assertIn("deployment sub what-if", calls)
            self.assertNotIn("stagingrecoverylever", calls)
            self.assertIn('"sqlDatabaseSkuName": {"value": "GP_S_Gen5"}', calls)
            self.assertIn('"sqlDatabaseSkuTier": {"value": "GeneralPurpose"}', calls)
            self.assertIn('"sqlDatabaseSkuFamily": {"value": "Gen5"}', calls)
            self.assertIn('"sqlDatabaseMaxSizeBytes": {"value": "0"}', calls)
            self.assertNotIn('"S0"', calls)

    def test_refuses_when_target_environment_is_test_and_environment_name_is_test(self) -> None:
        environment = self.environment()
        environment["TARGET_ENVIRONMENT"] = "test"
        environment["STAGING_RECOVERY_LIFECYCLE_LEVER_ENABLED"] = "true"
        environment["STAGING_RECOVERY_LIFECYCLE_LEVER_ALLOWED_INSTANCE_IDS"] = (
            "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"
        )
        result = self.run_deploy(environment, "--environment", "test")
        self.assertNotEqual(0, result.returncode)
        self.assertIn("Staging lever target names disagree", result.stderr)
        self.assertNotIn("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", result.stdout + result.stderr)

    def test_refuses_when_target_environment_is_test_with_wrong_case(self) -> None:
        environment = self.environment()
        environment["TARGET_ENVIRONMENT"] = "Test"
        environment["AZURE_ENV_NAME"] = "valence-control-staging"
        environment["STAGING_RECOVERY_LIFECYCLE_LEVER_ENABLED"] = "true"
        environment["STAGING_RECOVERY_LIFECYCLE_LEVER_ALLOWED_INSTANCE_IDS"] = (
            "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"
        )
        result = self.run_deploy(environment, "--environment", "valence-control-staging")
        self.assertNotEqual(0, result.returncode)
        self.assertIn("Staging lever target names disagree", result.stderr)
        self.assertNotIn("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", result.stdout + result.stderr)

    def test_refuses_when_environment_name_is_test_and_azure_env_name_is_staging(self) -> None:
        environment = self.environment()
        environment["AZURE_ENV_NAME"] = "valence-control-staging"
        environment["STAGING_RECOVERY_LIFECYCLE_LEVER_ENABLED"] = "true"
        environment["STAGING_RECOVERY_LIFECYCLE_LEVER_ALLOWED_INSTANCE_IDS"] = (
            "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"
        )
        result = self.run_deploy(environment, "--environment", "test")
        self.assertNotEqual(0, result.returncode)
        self.assertIn("Staging lever target names disagree", result.stderr)
        self.assertNotIn("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", result.stdout + result.stderr)

    def test_treats_a_false_or_empty_lever_flag_as_unset_on_production(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            temporary_path = Path(temporary)
            call_log = temporary_path / "az-calls"
            self.write_fake_az(temporary_path)

            for enabled in ("", "false", "FALSE"):
                with self.subTest(enabled=enabled):
                    environment = self.environment()
                    environment["PATH"] = f"{temporary_path}{os.pathsep}{environment['PATH']}"
                    environment["AZ_CALL_LOG"] = str(call_log)
                    environment["STAGING_BILLING_LIFECYCLE_LEVER_ENABLED"] = enabled
                    environment["STAGING_BILLING_LIFECYCLE_LEVER_ALLOWED_ORG_IDS"] = ""
                    call_log.write_text("")
                    result = self.run_deploy(environment, "--environment", "prod", "--what-if")
                    self.assertEqual(0, result.returncode, result.stderr)
                    self.assertIn("deployment sub what-if", call_log.read_text())
                    self.assertNotIn("stagingbillinglever", call_log.read_text())

    def test_rejects_non_supabase_cloud_issuer_before_azure_mutation(self) -> None:
        environment = self.environment()
        environment["CLOUD_ACCOUNT_ISSUER"] = "https://example.invalid/auth/v1"
        environment["EXPECTED_CLOUD_ACCOUNT_ISSUER"] = environment["CLOUD_ACCOUNT_ISSUER"]
        result = self.run_deploy(environment, "--environment", "test")
        self.assertNotEqual(0, result.returncode)
        self.assertIn("exact Supabase Auth issuer", result.stderr)

    def test_what_if_uses_subscription_scope_without_building_an_image(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            temporary_path = Path(temporary)
            call_log = temporary_path / "az-calls"
            self.write_fake_az(temporary_path)
            environment = self.environment()
            environment["PATH"] = f"{temporary_path}{os.pathsep}{environment['PATH']}"
            environment["AZ_CALL_LOG"] = str(call_log)
            environment["CLOUD_ACCOUNT_ISSUER"] = "https://abcdefghijklmnopqrst.supabase.co/auth/v1"
            environment["EXPECTED_CLOUD_ACCOUNT_ISSUER"] = environment["CLOUD_ACCOUNT_ISSUER"]

            result = self.run_deploy(environment, "--environment", "test", "--what-if")

            self.assertEqual(0, result.returncode, result.stderr)
            calls = call_log.read_text()
            self.assertIn("deployment sub what-if", calls)
            self.assertIn("--template-file infra/main.bicep", calls)
            self.assertNotIn("deployment group", calls)
            self.assertIn('"sqlDatabaseSkuName": {"value": "S0"}', calls)
            self.assertIn('"sqlDatabaseSkuTier": {"value": "Standard"}', calls)
            self.assertIn('"sqlDatabaseMaxSizeBytes": {"value": "268435456000"}', calls)
            self.assertNotIn("GP_S_Gen5", calls)

    def test_missing_base_output_fails_before_image_build(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            temporary_path = Path(temporary)
            az_log = temporary_path / "az-calls"
            docker_log = temporary_path / "docker-calls"
            fake_az = temporary_path / "az"
            fake_az.write_text(
                "#!/usr/bin/env bash\n"
                "set -euo pipefail\n"
                "printf '%s\\n' \"$*\" >> \"${AZ_CALL_LOG:?}\"\n"
                "case \"$*\" in\n"
                "  'account set --subscription '*) exit 0 ;;\n"
                "  'deployment sub create '*) exit 0 ;;\n"
                "  'deployment sub show '*) printf '{}\\n'; exit 0 ;;\n"
                "  *) exit 41 ;;\n"
                "esac\n"
            )
            fake_az.chmod(0o755)
            fake_docker = temporary_path / "docker"
            fake_docker.write_text(
                "#!/usr/bin/env bash\n"
                "printf '%s\\n' \"$*\" >> \"${DOCKER_CALL_LOG:?}\"\n"
            )
            fake_docker.chmod(0o755)

            environment = self.environment()
            environment["PATH"] = f"{temporary_path}{os.pathsep}{environment['PATH']}"
            environment["AZ_CALL_LOG"] = str(az_log)
            environment["DOCKER_CALL_LOG"] = str(docker_log)

            result = self.run_deploy(environment, "--environment", "test")

            self.assertNotEqual(0, result.returncode)
            self.assertIn("Missing deployment output: AZURE_CONTAINER_REGISTRY_ENDPOINT", result.stderr)
            self.assertFalse(docker_log.exists(), "docker must not run when base outputs are incomplete")

    def test_base_only_does_not_require_api_secrets_or_docker(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            temporary_path = Path(temporary)
            call_log = temporary_path / "az-calls"
            self.write_fake_az(temporary_path)
            environment = os.environ.copy()
            for name in (
                "ADMIN_API_KEY",
                "BUILDER_CLIENT_API_KEY",
                "CONTROL_ENTRA_TENANT_ID",
                "CONTROL_ENTRA_CLIENT_ID",
                "CONTROL_ENTRA_CLIENT_SECRET",
                "CLOUD_ACCOUNT_ISSUER",
                "EXPECTED_CLOUD_ACCOUNT_ISSUER",
            ):
                environment.pop(name, None)
            environment.update(
                {
                    "PATH": f"{temporary_path}{os.pathsep}{environment['PATH']}",
                    "AZ_CALL_LOG": str(call_log),
                    "AZURE_SUBSCRIPTION_ID": "00000000-0000-0000-0000-000000000003",
                }
            )

            result = self.run_deploy(environment, "--environment", "test", "--base-only")

            self.assertEqual(0, result.returncode, result.stderr)
            self.assertIn("deployment sub create", call_log.read_text())
            self.assertIn("Base deployment completed", result.stdout)


if __name__ == "__main__":
    unittest.main()
