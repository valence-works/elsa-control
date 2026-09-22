#!/usr/bin/env python3
"""Offline contracts for the GitHub/Azure environment bootstrap helper."""

from __future__ import annotations

import os
import subprocess
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
BOOTSTRAP_SCRIPT = ROOT / "scripts" / "bootstrap-github-azure.sh"


class BootstrapGitHubAzureTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.source = BOOTSTRAP_SCRIPT.read_text()

    def test_default_resource_group_matches_subscription_template(self) -> None:
        self.assertIn('EXPECTED_RESOURCE_GROUP="rg-$AZURE_ENVIRONMENT"', self.source)
        self.assertIn('RESOURCE_GROUP="${RESOURCE_GROUP:-$EXPECTED_RESOURCE_GROUP}"', self.source)
        self.assertNotIn("rg-elsa-control-$AZURE_ENVIRONMENT", self.source)

    def test_rejects_non_supabase_cloud_issuer_before_external_commands(self) -> None:
        environment = os.environ.copy()
        environment["CLOUD_ACCOUNT_ISSUER"] = "https://example.invalid/auth/v1"
        result = subprocess.run(
            [str(BOOTSTRAP_SCRIPT), "--environment", "test"],
            cwd=ROOT,
            env=environment,
            capture_output=True,
            text=True,
            check=False,
        )

        self.assertNotEqual(0, result.returncode)
        self.assertIn("exact Supabase Auth issuer", result.stderr)

    def test_rejects_resource_group_that_cannot_be_owned_by_template(self) -> None:
        result = subprocess.run(
            [
                str(BOOTSTRAP_SCRIPT),
                "--environment",
                "test",
                "--resource-group",
                "rg-elsa-control-test",
            ],
            cwd=ROOT,
            env=os.environ.copy(),
            capture_output=True,
            text=True,
            check=False,
        )

        self.assertNotEqual(0, result.returncode)
        self.assertIn("Resource group must be rg-test", result.stderr)

    @staticmethod
    def write_fake_commands(directory: Path, missing_outputs: bool = False) -> tuple[Path, Path]:
        az_log = directory / "az-calls"
        gh_log = directory / "gh-calls"
        fake_az = directory / "az"
        fake_az.write_text(
            "#!/usr/bin/env python3\n"
            "import os, sys\n"
            "args = sys.argv[1:]\n"
            "with open(os.environ['AZ_CALL_LOG'], 'a', encoding='utf-8') as log:\n"
            "    log.write(' '.join(args) + '\\n')\n"
            "if args[:2] == ['account', 'set']:\n"
            "    raise SystemExit(0)\n"
            "if args[:2] == ['account', 'show']:\n"
            "    print('00000000-0000-0000-0000-000000000004')\n"
            "    raise SystemExit(0)\n"
            "if args[:3] == ['deployment', 'sub', 'show']:\n"
            f"    print({'null'!r} if {missing_outputs!r} else "
            "'{\"azurE_CONTAINER_REGISTRY_ENDPOINT\":{\"value\":\"test.azurecr.io\"}}')\n"
            "    raise SystemExit(0)\n"
            "if args[:2] == ['webapp', 'list']:\n"
            "    print('1' if 'length(@)' in args else 'test-webapp')\n"
            "    raise SystemExit(0)\n"
            "if args[:2] == ['acr', 'show']:\n"
            "    print('/subscriptions/test/resourceGroups/rg-test/providers/Microsoft.ContainerRegistry/registries/test')\n"
            "    raise SystemExit(0)\n"
            "if args[:3] == ['ad', 'app', 'list']:\n"
            "    print('00000000-0000-0000-0000-000000000005')\n"
            "    raise SystemExit(0)\n"
            "if args[:3] == ['ad', 'app', 'show']:\n"
            "    print('00000000-0000-0000-0000-000000000006')\n"
            "    raise SystemExit(0)\n"
            "if args[:3] == ['ad', 'sp', 'create']:\n"
            "    raise SystemExit(0)\n"
            "if args[:3] == ['ad', 'sp', 'show']:\n"
            "    print('00000000-0000-0000-0000-000000000007')\n"
            "    raise SystemExit(0)\n"
            "if args[:4] == ['ad', 'app', 'federated-credential', 'list']:\n"
            "    if not os.environ.get('MISSING_FEDERATED_CREDENTIAL'):\n"
            "        print('existing-credential')\n"
            "    raise SystemExit(0)\n"
            "if args[:4] == ['ad', 'app', 'federated-credential', 'create']:\n"
            "    parameters = next(value[1:] for value in args if value.startswith('@'))\n"
            "    with open(parameters, encoding='utf-8') as source, open(os.environ['AZ_CALL_LOG'], 'a', encoding='utf-8') as log:\n"
            "        log.write(source.read() + '\\n')\n"
            "    raise SystemExit(0)\n"
            "if args[:3] == ['role', 'assignment', 'list']:\n"
            "    raise SystemExit(0)\n"
            "if args[:3] == ['role', 'assignment', 'create']:\n"
            "    raise SystemExit(42)\n"
            "raise SystemExit(41)\n"
        )
        fake_az.chmod(0o755)

        fake_gh = directory / "gh"
        fake_gh.write_text(
            "#!/usr/bin/env python3\n"
            "import os, sys\n"
            "args = sys.argv[1:]\n"
            "with open(os.environ['GH_CALL_LOG'], 'a', encoding='utf-8') as log:\n"
            "    log.write(' '.join(args) + '\\n')\n"
            "if args[:2] == ['auth', 'status']:\n"
            "    raise SystemExit(0)\n"
            "if args[:2] == ['repo', 'view']:\n"
            "    print('owner/repo')\n"
            "    raise SystemExit(0)\n"
            "if args[:2] == ['variable', 'get']:\n"
            "    variable = args[2]\n"
            "    value = (os.environ.get('APPROVED_CLOUD_ACCOUNT_ISSUER', '')\n"
            "             if variable == 'EXPECTED_CLOUD_ACCOUNT_ISSUER'\n"
            "             else os.environ.get('EXISTING_CLOUD_ACCOUNT_ISSUER', ''))\n"
            "    if value:\n"
            "        print(value)\n"
            "        raise SystemExit(0)\n"
            "    raise SystemExit(1)\n"
            "if args and args[0] == 'api' and args[1].endswith('/actions/oidc/customization/sub'):\n"
            "    print(os.environ.get('OIDC_SUBJECT_CONFIG', '{\"use_default\":true}'))\n"
            "    raise SystemExit(0)\n"
            "if args and args[0] == 'api':\n"
            "    raise SystemExit(0)\n"
            "if args[:2] in (['variable', 'set'], ['variable', 'delete'], ['secret', 'set']):\n"
            "    raise SystemExit(0)\n"
            "raise SystemExit(41)\n"
        )
        fake_gh.chmod(0o755)
        return az_log, gh_log

    def test_missing_deployment_outputs_cause_no_external_mutations(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            az_log, gh_log = self.write_fake_commands(directory, missing_outputs=True)
            environment = os.environ.copy()
            environment.update(
                {
                    "PATH": f"{directory}{os.pathsep}{environment['PATH']}",
                    "AZ_CALL_LOG": str(az_log),
                    "GH_CALL_LOG": str(gh_log),
                    "AZURE_SUBSCRIPTION_ID": "00000000-0000-0000-0000-000000000003",
                }
            )

            result = subprocess.run(
                [str(BOOTSTRAP_SCRIPT), "--environment", "test"],
                cwd=ROOT,
                env=environment,
                capture_output=True,
                text=True,
                check=False,
            )

            self.assertNotEqual(0, result.returncode)
            self.assertIn("Could not read subscription deployment outputs", result.stderr)
            self.assertNotIn("ad app create", az_log.read_text())
            self.assertNotIn("role assignment create", az_log.read_text())
            self.assertNotIn("api --method PUT", gh_log.read_text())
            self.assertNotIn("variable set", gh_log.read_text())
            self.assertNotIn("secret set", gh_log.read_text())

    def test_explicit_disable_removes_active_issuer_but_keeps_approval(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            az_log, gh_log = self.write_fake_commands(directory)
            environment = os.environ.copy()
            environment.update(
                {
                    "PATH": f"{directory}{os.pathsep}{environment['PATH']}",
                    "AZ_CALL_LOG": str(az_log),
                    "GH_CALL_LOG": str(gh_log),
                    "AZURE_SUBSCRIPTION_ID": "00000000-0000-0000-0000-000000000003",
                    "EXISTING_CLOUD_ACCOUNT_ISSUER": "https://abcdefghijklmnopqrst.supabase.co/auth/v1",
                }
            )
            environment.pop("CLOUD_ACCOUNT_ISSUER", None)

            result = subprocess.run(
                [
                    str(BOOTSTRAP_SCRIPT),
                    "--environment",
                    "test",
                    "--skip-role-assignments",
                    "--disable-cloud-account-issuer",
                ],
                cwd=ROOT,
                env=environment,
                capture_output=True,
                text=True,
                check=False,
            )

            self.assertEqual(0, result.returncode, result.stderr)
            calls = gh_log.read_text()
            self.assertIn("variable delete CLOUD_ACCOUNT_ISSUER --env test", calls)
            self.assertNotIn("variable delete EXPECTED_CLOUD_ACCOUNT_ISSUER", calls)

    def test_cloud_issuer_must_match_preapproved_environment_value(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            az_log, gh_log = self.write_fake_commands(directory, missing_outputs=True)
            environment = os.environ.copy()
            environment.update(
                {
                    "PATH": f"{directory}{os.pathsep}{environment['PATH']}",
                    "AZ_CALL_LOG": str(az_log),
                    "GH_CALL_LOG": str(gh_log),
                    "AZURE_SUBSCRIPTION_ID": "00000000-0000-0000-0000-000000000003",
                    "CLOUD_ACCOUNT_ISSUER": "https://abcdefghijklmnopqrst.supabase.co/auth/v1",
                    "APPROVED_CLOUD_ACCOUNT_ISSUER": "https://jhrcnclyydzngnyvhdht.supabase.co/auth/v1",
                }
            )

            result = subprocess.run(
                [str(BOOTSTRAP_SCRIPT), "--environment", "test"],
                cwd=ROOT,
                env=environment,
                capture_output=True,
                text=True,
                check=False,
            )

            self.assertNotEqual(0, result.returncode)
            self.assertIn("independently approved environment issuer", result.stderr)
            self.assertFalse(az_log.exists(), "Azure must not be queried before issuer approval")
            self.assertNotIn("variable set", gh_log.read_text())

    def test_role_assignment_failure_stops_before_github_configuration(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            az_log, gh_log = self.write_fake_commands(directory)
            environment = os.environ.copy()
            environment.update(
                {
                    "PATH": f"{directory}{os.pathsep}{environment['PATH']}",
                    "AZ_CALL_LOG": str(az_log),
                    "GH_CALL_LOG": str(gh_log),
                    "AZURE_SUBSCRIPTION_ID": "00000000-0000-0000-0000-000000000003",
                }
            )

            result = subprocess.run(
                [str(BOOTSTRAP_SCRIPT), "--environment", "test"],
                cwd=ROOT,
                env=environment,
                capture_output=True,
                text=True,
                check=False,
            )

            self.assertNotEqual(0, result.returncode)
            self.assertIn("Could not create Azure role 'Contributor'", result.stderr)
            azure_calls = az_log.read_text()
            self.assertIn("role assignment create", azure_calls)
            self.assertIn("role assignment list", azure_calls)
            self.assertIn("--scope /subscriptions/", azure_calls)
            self.assertNotIn("role assignment list --assignee 00000000-0000-0000-0000-000000000007 --role Contributor --all", azure_calls)
            self.assertNotIn("api --method PUT", gh_log.read_text())
            self.assertNotIn("variable set", gh_log.read_text())
            self.assertNotIn("secret set", gh_log.read_text())

    def test_uses_repository_immutable_oidc_subject_prefix(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            az_log, gh_log = self.write_fake_commands(directory)
            environment = os.environ.copy()
            environment.update(
                {
                    "PATH": f"{directory}{os.pathsep}{environment['PATH']}",
                    "AZ_CALL_LOG": str(az_log),
                    "GH_CALL_LOG": str(gh_log),
                    "AZURE_SUBSCRIPTION_ID": "00000000-0000-0000-0000-000000000003",
                    "MISSING_FEDERATED_CREDENTIAL": "1",
                    "OIDC_SUBJECT_CONFIG": (
                        '{"use_default":true,"use_immutable_subject":true,'
                        '"sub_claim_prefix":"repo:owner@123/repo@456"}'
                    ),
                }
            )

            result = subprocess.run(
                [
                    str(BOOTSTRAP_SCRIPT),
                    "--environment",
                    "test",
                    "--skip-role-assignments",
                ],
                cwd=ROOT,
                env=environment,
                capture_output=True,
                text=True,
                check=False,
            )

            self.assertEqual(0, result.returncode, result.stderr)
            self.assertIn(
                '"subject": "repo:owner@123/repo@456:environment:test"',
                az_log.read_text(),
            )

    def test_immutable_oidc_mode_without_prefix_fails_before_azure_mutation(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            az_log, gh_log = self.write_fake_commands(directory)
            environment = os.environ.copy()
            environment.update(
                {
                    "PATH": f"{directory}{os.pathsep}{environment['PATH']}",
                    "AZ_CALL_LOG": str(az_log),
                    "GH_CALL_LOG": str(gh_log),
                    "AZURE_SUBSCRIPTION_ID": "00000000-0000-0000-0000-000000000003",
                    "OIDC_SUBJECT_CONFIG": '{"use_default":true,"use_immutable_subject":true}',
                }
            )

            result = subprocess.run(
                [str(BOOTSTRAP_SCRIPT), "--environment", "test"],
                cwd=ROOT,
                env=environment,
                capture_output=True,
                text=True,
                check=False,
            )

            self.assertNotEqual(0, result.returncode)
            self.assertIn("immutable OIDC subjects", result.stderr)
            self.assertNotIn("deployment sub show", az_log.read_text())


if __name__ == "__main__":
    unittest.main()
