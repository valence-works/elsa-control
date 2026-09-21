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

    def test_uses_current_subscription_and_api_modules_with_immutable_image(self) -> None:
        self.assertIn("az deployment sub create", self.source)
        self.assertIn("--base-only", self.source)
        self.assertIn('if [[ "$BASE_ONLY" == true ]]', self.source)
        self.assertIn("--template-file infra/main.bicep", self.source)
        self.assertIn("az deployment group create", self.source)
        self.assertIn("--template-file infra/api/api-website.module.bicep", self.source)
        self.assertIn("IMAGE=\"$IMAGE_REPOSITORY@$IMAGE_DIGEST\"", self.source)
        self.assertIn("AZURE_CONTAINER_REGISTRY_ENDPOINT", self.source)
        self.assertIn("CONTROL_SQL_SQLSERVERFQDN", self.source)
        self.assertNotIn("SQL_ADMINISTRATOR_PASSWORD", self.source)
        self.assertNotIn("containerRegistryLoginServer", self.source)

    def test_rejects_non_supabase_cloud_issuer_before_azure_mutation(self) -> None:
        environment = self.environment()
        environment["CLOUD_ACCOUNT_ISSUER"] = "https://example.invalid/auth/v1"
        environment["EXPECTED_CLOUD_ACCOUNT_ISSUER"] = environment["CLOUD_ACCOUNT_ISSUER"]
        result = subprocess.run(
            [str(DEPLOY_SCRIPT), "--environment", "test"],
            cwd=ROOT,
            env=environment,
            capture_output=True,
            text=True,
            check=False,
        )
        self.assertNotEqual(0, result.returncode)
        self.assertIn("exact Supabase Auth issuer", result.stderr)

    def test_what_if_uses_subscription_scope_without_building_an_image(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            temporary_path = Path(temporary)
            call_log = temporary_path / "az-calls"
            fake_az = temporary_path / "az"
            fake_az.write_text(
                "#!/usr/bin/env bash\n"
                "set -euo pipefail\n"
                "printf '%s\\n' \"$*\" >> \"${AZ_CALL_LOG:?}\"\n"
                "case \"$*\" in\n"
                "  'account set --subscription '*) exit 0 ;;\n"
                "  'deployment sub what-if '*) exit 0 ;;\n"
                "  *) exit 41 ;;\n"
                "esac\n"
            )
            fake_az.chmod(0o755)
            environment = self.environment()
            environment["PATH"] = f"{temporary_path}{os.pathsep}{environment['PATH']}"
            environment["AZ_CALL_LOG"] = str(call_log)
            environment["CLOUD_ACCOUNT_ISSUER"] = "https://abcdefghijklmnopqrst.supabase.co/auth/v1"
            environment["EXPECTED_CLOUD_ACCOUNT_ISSUER"] = environment["CLOUD_ACCOUNT_ISSUER"]

            result = subprocess.run(
                [str(DEPLOY_SCRIPT), "--environment", "test", "--what-if"],
                cwd=ROOT,
                env=environment,
                capture_output=True,
                text=True,
                check=False,
            )

            self.assertEqual(0, result.returncode, result.stderr)
            calls = call_log.read_text()
            self.assertIn("deployment sub what-if", calls)
            self.assertIn("--template-file infra/main.bicep", calls)
            self.assertNotIn("deployment group", calls)

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

            result = subprocess.run(
                [str(DEPLOY_SCRIPT), "--environment", "test"],
                cwd=ROOT,
                env=environment,
                capture_output=True,
                text=True,
                check=False,
            )

            self.assertNotEqual(0, result.returncode)
            self.assertIn("Missing deployment output: AZURE_CONTAINER_REGISTRY_ENDPOINT", result.stderr)
            self.assertFalse(docker_log.exists(), "docker must not run when base outputs are incomplete")

    def test_base_only_does_not_require_api_secrets_or_docker(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            temporary_path = Path(temporary)
            call_log = temporary_path / "az-calls"
            fake_az = temporary_path / "az"
            fake_az.write_text(
                "#!/usr/bin/env bash\n"
                "set -euo pipefail\n"
                "printf '%s\\n' \"$*\" >> \"${AZ_CALL_LOG:?}\"\n"
                "case \"$*\" in\n"
                "  'account set --subscription '*) exit 0 ;;\n"
                "  'deployment sub create '*) exit 0 ;;\n"
                "  *) exit 41 ;;\n"
                "esac\n"
            )
            fake_az.chmod(0o755)
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

            result = subprocess.run(
                [str(DEPLOY_SCRIPT), "--environment", "test", "--base-only"],
                cwd=ROOT,
                env=environment,
                capture_output=True,
                text=True,
                check=False,
            )

            self.assertEqual(0, result.returncode, result.stderr)
            self.assertIn("deployment sub create", call_log.read_text())
            self.assertIn("Base deployment completed", result.stdout)


if __name__ == "__main__":
    unittest.main()
