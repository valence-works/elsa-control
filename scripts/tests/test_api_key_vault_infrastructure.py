#!/usr/bin/env python3
"""Compile and inspect the Control API Key Vault template (#831)."""

from __future__ import annotations

import json
import os
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
LIB = ROOT / "scripts" / "lib"
if str(LIB) not in sys.path:
    sys.path.insert(0, str(LIB))
MAIN = ROOT / "infra/api-key-vault/api-key-vault.module.bicep"
README = ROOT / "infra/api-key-vault/README.md"
CHECK_SCRIPT = ROOT / "scripts/check-key-vault-references.sh"
WEBAPP_ID = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-test/providers/Microsoft.Web/sites/api-test"
SECRETS_USER = "4633458b-17de-408a-b874-0445c86b69e6"


class ApiKeyVaultInfrastructureTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        az = shutil.which("az")
        if az is None:
            raise RuntimeError("Azure CLI with Bicep is required for API Key Vault contract tests")
        result = subprocess.run(
            [az, "bicep", "build", "--file", str(MAIN), "--stdout"],
            capture_output=True,
            text=True,
            check=False,
            timeout=120,
        )
        if result.returncode:
            raise AssertionError(result.stderr)
        cls.template = json.loads(result.stdout)
        cls.by_type: dict[str, list[dict]] = {}
        for resource in cls.template["resources"]:
            cls.by_type.setdefault(resource["type"], []).append(resource)
        cls.source = MAIN.read_text()

    def test_vault_uses_rbac_soft_delete_purge_protection_and_a_firewall(self) -> None:
        vaults = self.by_type["Microsoft.KeyVault/vaults"]
        self.assertEqual(1, len(vaults))
        props = vaults[0]["properties"]
        self.assertTrue(props["enableRbacAuthorization"])
        self.assertTrue(props["enableSoftDelete"])
        self.assertTrue(props["enablePurgeProtection"])
        self.assertEqual(90, props["softDeleteRetentionInDays"])
        self.assertEqual("Enabled", props["publicNetworkAccess"])
        acls = props["networkAcls"]
        self.assertEqual("Deny", acls["defaultAction"])
        self.assertEqual("None", acls["bypass"])
        self.assertIn("virtualNetworkRules", acls)
        self.assertNotIn("ignoreMissingVnetServiceEndpoint", self.source)
        self.assertNotIn("ignoreMissingVnetServiceEndpoint", json.dumps(self.template))
        self.assertIn("ipRules", acls)
        self.assertNotIn("Microsoft.KeyVault/vaults/secrets", json.dumps(self.template))

    def test_role_assignment_is_exactly_secrets_user_for_the_api_identity(self) -> None:
        assignments = self.by_type["Microsoft.Authorization/roleAssignments"]
        self.assertEqual(1, len(assignments))
        self.assertEqual(SECRETS_USER, self.template["variables"]["keyVaultSecretsUserRoleId"])
        assignment = assignments[0]
        props = assignment["properties"]
        self.assertEqual("ServicePrincipal", props["principalType"])
        self.assertIn("variables('keyVaultSecretsUserRoleId')", props["roleDefinitionId"])
        self.assertIn("Microsoft.KeyVault/vaults", assignment["scope"])
        self.assertIn("parameters('apiIdentityPrincipalId')", props["principalId"])
        self.assertNotIn("deployIdentityPrincipalId", self.source)
        self.assertNotIn("b86a8fe4-44ce-4948-aee5-eccb2c155cd7", json.dumps(props))
        self.assertEqual(2, len(self.template["resources"]))
        self.assertEqual(
            {
                "Microsoft.KeyVault/vaults",
                "Microsoft.Authorization/roleAssignments",
            },
            set(self.by_type),
        )

    def test_readme_records_operator_write_path_and_no_ci_secret_write(self) -> None:
        readme = README.read_text()
        self.assertIn("operator writes", readme.lower())
        self.assertIn("never writes a secret value", readme.lower())
        self.assertIn("Authentication-ApiKey", readme)
        self.assertIn("Key Vault Secrets User", readme)
        self.assertIn("secrets/getSecret", readme)
        self.assertIn("no Key Vault data role", readme)
        self.assertIn("configreferences", readme)

    def test_is_key_vault_reference_accepts_only_versionless_secret_uri(self) -> None:
        from key_vault_app_settings import is_key_vault_reference

        valid = (
            "@Microsoft.KeyVault(SecretUri=https://ecapikvtest.vault.azure.net/secrets/Billing-Stripe-SecretKey)"
        )
        self.assertTrue(is_key_vault_reference(valid))
        self.assertTrue(is_key_vault_reference(f"  {valid}  "))
        self.assertFalse(is_key_vault_reference("@Microsoft.KeyVault(foo)"))
        self.assertFalse(is_key_vault_reference("@Microsoft.KeyVault(SecretUri=foo)"))
        self.assertFalse(
            is_key_vault_reference(
                "@Microsoft.KeyVault(SecretUri=https://kv.example.vault.azure.net/secrets/X/0123456789abcdef0123456789abcdef)"
            )
        )
        self.assertFalse(is_key_vault_reference("@Microsoft.KeyVault(VaultName=kv;SecretName=X)"))
        self.assertFalse(is_key_vault_reference("sk_live_not_a_reference"))
        self.assertFalse(is_key_vault_reference(None))

    def test_guard_keys_match_the_python_secret_setting_map(self) -> None:
        from key_vault_app_settings import SECRET_APP_SETTINGS

        guard = (ROOT / "src/Hosting/ElsaControl.Api/Hosting/UnresolvedKeyVaultReferenceGuard.cs").read_text()
        expected = {name.replace("__", ":") for name in SECRET_APP_SETTINGS}
        found = set()
        for line in guard.splitlines():
            stripped = line.strip().strip(',').strip('"')
            if stripped.startswith("Authentication:") or stripped.startswith("Billing:") or stripped.startswith("ManagedElsa:"):
                found.add(stripped)
        self.assertEqual(expected, found)


    def test_optional_bff_credential_settings_are_separate_from_the_guard_keys(self) -> None:
        from key_vault_app_settings import (
            OPTIONAL_SECRET_APP_SETTINGS,
            REQUIRED_SECRET_APP_SETTING_NAMES,
            SECRET_APP_SETTING_NAMES,
            SECRET_APP_SETTINGS,
        )

        self.assertEqual(
            {
                "RateLimiting__BffCredentials__Sources__0__Secret": "RateLimiting-BffCredentials-cloud-Secret",
                "RateLimiting__BffCredentials__Sources__0__PreviousSecret": "RateLimiting-BffCredentials-cloud-PreviousSecret",
                "RateLimiting__BffCredentials__Sources__1__Secret": "RateLimiting-BffCredentials-hub-Secret",
                "RateLimiting__BffCredentials__Sources__1__PreviousSecret": "RateLimiting-BffCredentials-hub-PreviousSecret",
            },
            OPTIONAL_SECRET_APP_SETTINGS,
        )
        self.assertEqual(tuple(SECRET_APP_SETTINGS), REQUIRED_SECRET_APP_SETTING_NAMES)
        self.assertEqual(set(SECRET_APP_SETTINGS) | set(OPTIONAL_SECRET_APP_SETTINGS), set(SECRET_APP_SETTING_NAMES))
        self.assertFalse(set(SECRET_APP_SETTINGS) & set(OPTIONAL_SECRET_APP_SETTINGS))
        for secret in OPTIONAL_SECRET_APP_SETTINGS.values():
            self.assertIn(f"'{secret}'", self.source)
        guard = (ROOT / "src/Hosting/ElsaControl.Api/Hosting/UnresolvedKeyVaultReferenceGuard.cs").read_text()
        self.assertNotIn("RateLimiting:", guard)
        # The feature validator refuses an unresolved reference while the section is enabled.
        validator = (ROOT / "src/Hosting/ElsaControl.Api/Authentication/ControlRateLimitBffCredential.cs").read_text()
        self.assertIn("UnresolvedKeyVaultReferenceGuard.ReferencePrefix", validator)

    def run_check(self, properties: dict, plain_names: tuple[str, ...] = ()) -> subprocess.CompletedProcess[str]:
        with tempfile.TemporaryDirectory() as temporary:
            fake_az = Path(temporary) / "az"
            fake_az.write_text(
                "#!/usr/bin/env bash\n"
                "set -euo pipefail\n"
                "case \"$1 $2\" in\n"
                "  'webapp show') printf '%s\\n' \"$FAKE_WEBAPP_ID\" ;;\n"
                "  'rest --method') printf '%s\\n' \"$FAKE_REFERENCES\" ;;\n"
                "  'webapp config') printf '%s\\n' \"$FAKE_SETTING_NAMES\" ;;\n"
                "  'resource show') printf 'false\\n' ;;\n"
                "  *) exit 41 ;;\n"
                "esac\n"
            )
            fake_az.chmod(0o755)
            environment = os.environ.copy()
            environment["PATH"] = f"{temporary}{os.pathsep}{environment['PATH']}"
            environment["FAKE_WEBAPP_ID"] = WEBAPP_ID
            environment["FAKE_REFERENCES"] = json.dumps({"properties": properties})
            # The site's setting names: every reference plus any plain-value setting.
            environment["FAKE_SETTING_NAMES"] = "\n".join([*properties, *plain_names])
            return subprocess.run(
                [str(CHECK_SCRIPT), "--resource-group", "rg-test", "--name", "api-test"],
                cwd=ROOT,
                env=environment,
                capture_output=True,
                text=True,
                check=False,
                timeout=60,
            )

    def test_check_script_reports_optional_settings_without_requiring_them(self) -> None:
        from key_vault_app_settings import OPTIONAL_SECRET_APP_SETTINGS, SECRET_APP_SETTINGS

        resolved = {name: {"status": "Resolved"} for name in SECRET_APP_SETTINGS}
        off = self.run_check(resolved)
        self.assertEqual(0, off.returncode, off.stdout + off.stderr)
        for name in OPTIONAL_SECRET_APP_SETTINGS:
            self.assertIn(f"{name}\tNotSet", off.stdout)
        self.assertIn("basicPublishingCredentialsPolicies/scm\tallow=false", off.stdout)

        cloud_secret = "RateLimiting__BffCredentials__Sources__0__Secret"
        on = self.run_check(resolved | {cloud_secret: {"status": "Resolved"}})
        self.assertEqual(0, on.returncode, on.stdout + on.stderr)
        self.assertIn(f"{cloud_secret}\tResolved", on.stdout)

        broken = self.run_check(resolved | {cloud_secret: {"status": "SecretNotFound"}})
        self.assertEqual(2, broken.returncode, broken.stdout + broken.stderr)
        self.assertIn(f"{cloud_secret}\tSecretNotFound", broken.stdout)

        # A secret setting stored as a plain value is not a reference and fails.
        plain = self.run_check(resolved, plain_names=(cloud_secret,))
        self.assertEqual(2, plain.returncode, plain.stdout + plain.stderr)
        self.assertIn(f"{cloud_secret}\tNotAReference", plain.stdout)

        required_missing = dict(resolved)
        required_missing.pop("Authentication__ApiKey")
        missing = self.run_check(required_missing)
        self.assertEqual(2, missing.returncode, missing.stdout + missing.stderr)
        self.assertIn("Authentication__ApiKey\tMissing", missing.stdout)


if __name__ == "__main__":
    unittest.main()
