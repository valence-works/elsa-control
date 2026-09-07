#!/usr/bin/env python3
"""Compile and inspect the Control API deploy-identity template (#308)."""

import json
import shutil
import subprocess
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
MAIN = ROOT / "infra/control-deploy-identity/main.bicep"
PRODUCTION = ROOT / "infra/control-deploy-identity/main.parameters.production.json"
ROLE_IDS = {
    "reader": "acdd72a7-3385-48ef-bd42-f606fba81ae7",
    "websiteContributor": "de139f84-1756-47ae-9be6-808fbbe84772",
    "acrPush": "8311e382-0749-4cb8-b61a-304f252e45ec",
}


class ControlDeployIdentityTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        az = shutil.which("az")
        if az is None:
            raise RuntimeError("Azure CLI with Bicep is required for deploy-identity contract tests")
        result = subprocess.run([az, "bicep", "build", "--file", str(MAIN), "--stdout"], capture_output=True, text=True, check=False, timeout=120)
        if result.returncode:
            raise AssertionError(result.stderr)
        cls.template = json.loads(result.stdout)
        cls.resources = cls.template["resources"]
        cls.by_type = {}
        for resource in cls.resources:
            cls.by_type.setdefault(resource["type"], []).append(resource)
        cls.production = json.loads(PRODUCTION.read_text())["parameters"]
        cls.variables = cls.template.get("variables", {})

    @classmethod
    def resolve(cls, expression):
        """Substitute compiled variables('name') references with their literal values."""
        for name, value in cls.variables.items():
            expression = expression.replace(f"variables('{name}')", repr(value)).replace(f"[{value!r}]", value)
        return expression

    def test_only_identity_credential_and_three_role_assignments_are_created(self):
        self.assertEqual(5, len(self.resources))
        self.assertEqual(1, len(self.by_type["Microsoft.ManagedIdentity/userAssignedIdentities"]))
        self.assertEqual(1, len(self.by_type["Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials"]))
        self.assertEqual(3, len(self.by_type["Microsoft.Authorization/roleAssignments"]))

    def test_federated_credential_trusts_github_oidc_with_the_token_exchange_audience(self):
        credential = self.by_type["Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials"][0]["properties"]
        self.assertEqual("https://token.actions.githubusercontent.com", self.resolve(credential["issuer"]))
        self.assertEqual(["api://AzureADTokenExchange"], credential["audiences"])
        self.assertEqual("[parameters('githubSubject')]", credential["subject"])

    def test_roles_are_exact_and_each_is_bound_to_its_scope(self):
        assignments = self.by_type["Microsoft.Authorization/roleAssignments"]
        bound = {}
        for assignment in assignments:
            role_id = next(role for role in ROLE_IDS.values() if role in self.resolve(assignment["properties"]["roleDefinitionId"]))
            bound[role_id] = assignment.get("scope", "<resourceGroup>")
            self.assertEqual("ServicePrincipal", assignment["properties"]["principalType"])
            self.assertIn("Microsoft.ManagedIdentity/userAssignedIdentities", assignment["properties"]["principalId"])
            self.assertIn("parameters('identityName')", assignment["properties"]["principalId"])
        self.assertEqual("<resourceGroup>", bound[ROLE_IDS["reader"]])
        self.assertIn("Microsoft.Web/sites", bound[ROLE_IDS["websiteContributor"]])
        self.assertIn("parameters('apiSiteName')", bound[ROLE_IDS["websiteContributor"]])
        self.assertIn("Microsoft.ContainerRegistry/registries", bound[ROLE_IDS["acrPush"]])
        self.assertIn("parameters('registryName')", bound[ROLE_IDS["acrPush"]])

    def test_no_default_identity_name_location_or_subject(self):
        parameters = self.template["parameters"]
        for name in ("identityName", "location", "githubSubject", "apiSiteName", "registryName"):
            self.assertNotIn("defaultValue", parameters[name], name)
        for name in ("readerRoleAssignmentName", "websiteContributorRoleAssignmentName", "acrPushRoleAssignmentName"):
            self.assertIn("guid(", parameters[name]["defaultValue"])

    def test_production_parameters_adopt_the_live_identity_exactly(self):
        values = {name: value["value"] for name, value in self.production.items()}
        self.assertEqual("valence_control_contributor_mi-m5uymkuaf222o", values["identityName"])
        self.assertEqual("belgiumcentral", values["location"])
        self.assertRegex(values["githubSubject"], r"^repo:valence-works(@[0-9]+)?/elsa-control(@[0-9]+)?:environment:production$")
        self.assertEqual("api-m5uymkuaf222o", values["apiSiteName"])
        self.assertEqual("valencecontrolacrm5uymkuaf222o", values["registryName"])
        # The live assignment names: a different value would create a duplicate assignment instead of adopting.
        self.assertEqual("73cdddfd-3707-5f90-a858-75455aac2d90", values["readerRoleAssignmentName"])
        self.assertEqual("a51368d6-377e-5237-8236-97e5231baf0b", values["websiteContributorRoleAssignmentName"])
        self.assertEqual("130c7d80-0cb5-4e7b-bcbd-3c6ce913a3e7", values["acrPushRoleAssignmentName"])
        self.assertEqual(set(values), set(self.template["parameters"]) - {"tags"})

    def test_readme_states_the_identity_must_never_be_deleted(self):
        readme = (MAIN.parent / "README.md").read_text()
        self.assertIn("Never delete this identity", readme)
        self.assertIn("AZURE_CLIENT_ID", readme)
        self.assertIn("NoChange", readme)


if __name__ == "__main__":
    unittest.main()
