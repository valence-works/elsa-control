#!/usr/bin/env python3
"""Compile and inspect the versioned Azure Lighthouse offer boundary."""

import json
import shutil
import subprocess
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
TEMPLATE = ROOT / "infra/azure-lighthouse/v1/main.bicep"
README = TEMPLATE.parent / "README.md"
RUNBOOK = ROOT / "docs/deployment/azure-lighthouse-concierge-runbook.md"

CONTRIBUTOR = "b24988ac-6180-42a0-ab88-20f7382dd24c"
OWNER = "8e3af657-a8ff-443c-a75c-2fe8c4bcb635"
USER_ACCESS_ADMINISTRATOR = "18d7d88d-d35e-4fb5-a5c3-7773c20a72d9"
RBAC_ADMINISTRATOR = "f58310d9-a9f6-439a-9e8d-f62e7b41a168"
KEY_VAULT_SECRETS_USER = "4633458b-17de-408a-b874-0445c86b69e6"
KEY_VAULT_SECRETS_OFFICER = "b86a8fe4-44ce-4948-aee5-eccb2c155cd7"
ACR_PULL = "7f951dda-4ed3-4680-a7ca-43fe172d538d"
REGISTRATION_DEFINITION_NAME = "9f8cf4c0-1f7a-4c7b-9c7b-e5f26a2d8bd9"
REGISTRATION_ASSIGNMENT_NAME = "50f0f9d1-8c11-47a3-8af5-9a87fa5c7af9"


class AzureLighthouseOfferTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        az = shutil.which("az")
        if az is None:
            raise RuntimeError("Azure CLI with Bicep is required for Lighthouse offer contract tests")
        result = subprocess.run(
            [az, "bicep", "build", "--file", str(TEMPLATE), "--stdout"],
            capture_output=True,
            text=True,
            check=False,
        )
        if result.returncode:
            raise AssertionError(result.stderr)
        cls.template = json.loads(result.stdout)
        cls.source = TEMPLATE.read_text()
        cls.readme = README.read_text()
        cls.runbook = RUNBOOK.read_text()

    def test_compiles_as_subscription_scoped_lighthouse_only_template(self):
        self.assertTrue(self.template["$schema"].endswith("subscriptionDeploymentTemplate.json#"))
        self.assertCountEqual(
            [resource["type"] for resource in self.template["resources"]],
            ["Microsoft.ManagedServices/registrationDefinitions", "Microsoft.ManagedServices/registrationAssignments"],
        )
        for resource in self.template["resources"]:
            self.assertEqual("2022-10-01", resource["apiVersion"])
        serialized = json.dumps(self.template)
        self.assertNotIn("Microsoft.Authorization/roleAssignments", serialized)
        self.assertNotIn("Microsoft.Web/sites", serialized)
        self.assertNotIn("Microsoft.App/containerApps", serialized)
        self.assertNotIn("Microsoft.KeyVault/vaults", serialized)
        self.assertNotIn("Microsoft.Sql/servers", serialized)
        self.assertIn(REGISTRATION_DEFINITION_NAME, serialized)
        self.assertIn(REGISTRATION_ASSIGNMENT_NAME, serialized)

    def test_ids_are_parameters_or_reviewed_role_constants(self):
        parameters = self.template["parameters"]
        self.assertEqual("string", parameters["managingTenantId"]["type"])
        self.assertEqual("array", parameters["managingPrincipalObjectIds"]["type"])
        self.assertEqual(1, parameters["managingPrincipalObjectIds"]["minLength"])
        self.assertNotIn("defaultValue", parameters["managingTenantId"])
        self.assertNotIn("defaultValue", parameters["managingPrincipalObjectIds"])
        for role_id in (CONTRIBUTOR, USER_ACCESS_ADMINISTRATOR, KEY_VAULT_SECRETS_USER):
            self.assertIn(role_id, self.source)

    def test_lighthouse_authorization_shape_is_supported_and_narrow(self):
        self.assertIn("targetScope = 'subscription'", self.source)
        self.assertIn(f"var registrationDefinitionName = '{REGISTRATION_DEFINITION_NAME}'", self.source)
        self.assertIn(f"var registrationAssignmentName = '{REGISTRATION_ASSIGNMENT_NAME}'", self.source)
        self.assertNotIn("guid(subscription().id, managingTenantId", self.source)
        self.assertIn("delegatedRoleDefinitionIds: delegatedManagedIdentityRoleDefinitionIds", self.source)
        self.assertIn("var authorizations = concat(contributorAuthorizations, userAccessAdministratorAuthorizations)", self.source)
        self.assertIn(KEY_VAULT_SECRETS_USER, self.source)
        # These are documented preflight/direct-grant alternatives, never v1
        # Lighthouse authorizations. The compiled template must not emit them.
        compiled = json.dumps(self.template)
        self.assertNotIn(OWNER, compiled)
        self.assertNotIn(RBAC_ADMINISTRATOR, compiled)
        self.assertNotIn(KEY_VAULT_SECRETS_OFFICER, compiled)
        self.assertIn("registrationDefinitionId", compiled)

    def test_docs_keep_guided_honesty_and_data_plane_boundary(self):
        combined = f"{self.readme}\n{self.runbook}".lower()
        for phrase in (
            "guided",
            "design-partner",
            "marketplace",
            "preview",
            "dedicated",
            "ga",
            "slo",
            "azure bill",
            "elsa fee",
            "rbac administrator",
            "user access administrator",
            "owner",
            "data-plane",
            "az keyvault secret list",
            "secrets officer",
            "active",
            "commercial `ready`",
        ):
            self.assertIn(phrase, combined)
        self.assertIn(ACR_PULL, combined)
        self.assertIn(CONTRIBUTOR, combined)
        self.assertIn(USER_ACCESS_ADMINISTRATOR, combined)
        self.assertIn(KEY_VAULT_SECRETS_OFFICER, combined)


if __name__ == "__main__":
    unittest.main()
