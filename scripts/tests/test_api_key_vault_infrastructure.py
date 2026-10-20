#!/usr/bin/env python3
"""Compile and inspect the Control API Key Vault template (#831)."""

from __future__ import annotations

import json
import shutil
import subprocess
import sys
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
LIB = ROOT / "scripts" / "lib"
if str(LIB) not in sys.path:
    sys.path.insert(0, str(LIB))
MAIN = ROOT / "infra/api-key-vault/api-key-vault.module.bicep"
README = ROOT / "infra/api-key-vault/README.md"
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
        self.assertNotIn("can use the GitHub deploy identity", readme)
        self.assertIn("Key Vault Contributor", readme)
        self.assertIn("subnets/joinViaServiceEndpoint/action", readme)
        self.assertIn("only management-plane read on the", readme)

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


if __name__ == "__main__":
    unittest.main()
