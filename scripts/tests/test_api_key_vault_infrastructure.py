#!/usr/bin/env python3
"""Compile and inspect the Control API Key Vault template (#831)."""

from __future__ import annotations

import json
import shutil
import subprocess
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
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
        self.assertIn("ignoreMissingVnetServiceEndpoint: false", self.source)
        self.assertIn("ipRules", acls)
        self.assertNotIn("Microsoft.KeyVault/vaults/secrets", json.dumps(self.template))

    def test_role_assignments_are_exactly_secrets_user_for_api_and_deploy(self) -> None:
        assignments = self.by_type["Microsoft.Authorization/roleAssignments"]
        self.assertEqual(2, len(assignments))
        self.assertEqual(SECRETS_USER, self.template["variables"]["keyVaultSecretsUserRoleId"])
        principals = set()
        for assignment in assignments:
            props = assignment["properties"]
            self.assertEqual("ServicePrincipal", props["principalType"])
            self.assertIn("variables('keyVaultSecretsUserRoleId')", props["roleDefinitionId"])
            self.assertIn("Microsoft.KeyVault/vaults", assignment["scope"])
            principals.add(props["principalId"])
            self.assertNotIn("b86a8fe4-44ce-4948-aee5-eccb2c155cd7", json.dumps(props))
        serialized = json.dumps(list(principals))
        self.assertIn("parameters('apiIdentityPrincipalId')", serialized)
        self.assertIn("parameters('deployIdentityPrincipalId')", serialized)
        self.assertEqual(3, len(self.template["resources"]))
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


if __name__ == "__main__":
    unittest.main()
