#!/usr/bin/env python3
"""Compile and inspect the exact subscription bootstrap resource boundary."""

import json
import re
import shutil
import subprocess
import unittest
import unicodedata
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
MAIN = ROOT / "infra/azure-customer-subscription/main.bicep"
REGISTRY_AUTHORITY = ROOT / "infra/azure-customer-subscription/registry-authority.bicep"


class CustomerSubscriptionBootstrapTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        az = shutil.which("az")
        if az is None:
            raise RuntimeError("Azure CLI with Bicep is required for bootstrap contract tests")
        result = subprocess.run(
            [az, "bicep", "build", "--file", str(MAIN), "--stdout"],
            capture_output=True, text=True, check=False,
        )
        if result.returncode:
            raise AssertionError(result.stderr)
        cls.template = json.loads(result.stdout)
        result = subprocess.run(
            [az, "bicep", "build", "--file", str(REGISTRY_AUTHORITY), "--stdout"],
            capture_output=True, text=True, check=False,
        )
        if result.returncode:
            raise AssertionError(result.stderr)
        cls.registry_authority_template = json.loads(result.stdout)

    def test_bootstrap_contains_only_anchor_identity_and_budget(self):
        resources = self.template["resources"]
        self.assertCountEqual(
            [resource["type"] for resource in resources],
            ["Microsoft.Resources/resourceGroups", "Microsoft.Resources/deployments", "Microsoft.Consumption/budgets"],
        )
        module = next(resource for resource in resources if resource["type"] == "Microsoft.Resources/deployments")
        nested = module["properties"]["template"]["resources"]
        self.assertEqual(["Microsoft.ManagedIdentity/userAssignedIdentities"], [resource["type"] for resource in nested])
        self.assertNotIn("subscriptionId", module)
        self.assertNotIn("tenantId", module)
        self.assertEqual("Incremental", module["properties"]["mode"])

    def test_no_implicit_target_subscription_or_real_identifiers(self):
        self.assertTrue(self.template["$schema"].endswith("subscriptionDeploymentTemplate.json#"))
        serialized = json.dumps(self.template)
        self.assertNotRegex(serialized, r"(?i)[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}")
        self.assertNotRegex(serialized, r"(?i)[\w.+-]+@[\w.-]+\.[a-z]{2,}")
        self.assertNotIn("Microsoft.Authorization/roleAssignments", serialized)
        self.assertNotIn("Microsoft.Web/sites", serialized)
        self.assertEqual("production", self.template["parameters"]["environment"]["defaultValue"])
        self.assertEqual(["production", "staging"],
                         self.template["parameters"]["environment"]["allowedValues"])

    def test_budget_is_alert_only_and_recipient_is_not_an_output(self):
        parameters = self.template["parameters"]
        self.assertEqual("securestring", parameters["budgetContactEmail"]["type"].lower())
        self.assertNotIn("defaultValue", parameters["budgetContactEmail"])
        self.assertNotIn("defaultValue", parameters["budgetStartDate"])
        self.assertNotIn("defaultValue", parameters["budgetEndDate"])
        self.assertEqual(100, parameters["monthlyBudgetAmount"]["defaultValue"])
        budget = next(resource for resource in self.template["resources"] if resource["type"] == "Microsoft.Consumption/budgets")
        self.assertEqual("Monthly", budget["properties"]["timeGrain"])
        notifications = budget["properties"]["notifications"]
        self.assertEqual({"actual50", "actual80", "actual100", "forecast100"}, set(notifications))
        for name, notification in notifications.items():
            self.assertTrue(notification["enabled"])
            self.assertEqual("GreaterThanOrEqualTo", notification["operator"])
            self.assertEqual("Forecasted" if name.startswith("forecast") else "Actual", notification["thresholdType"])
            self.assertEqual(["[parameters('budgetContactEmail')]"], notification["contactEmails"])
            self.assertNotIn("contactGroups", notification)
        self.assertEqual(
            {"actual50": 50, "actual80": 80, "actual100": 100, "forecast100": 100},
            {name: notification["threshold"] for name, notification in notifications.items()},
        )
        self.assertNotIn("budgetContactEmail", json.dumps(self.template.get("outputs", {})))

    def test_registry_authority_has_only_the_reviewed_metadata_and_delegation_resources(self):
        resources = self.registry_authority_template["resources"]
        self.assertCountEqual(
            [resource["type"] for resource in resources],
            ["Microsoft.Authorization/roleDefinitions", "Microsoft.Resources/deployments"],
        )
        role = next(resource for resource in resources if resource["type"] == "Microsoft.Authorization/roleDefinitions")
        properties = role["properties"]
        self.assertEqual("CustomRole", properties["type"])
        self.assertEqual("[variables('metadataRoleName')]", properties["roleName"])
        self.assertEqual(
            "Elsa Control Registry Deployment Metadata Operator",
            self.registry_authority_template["variables"]["metadataRoleName"],
        )
        self.assertCountEqual(
            properties["permissions"][0]["actions"],
            [
                "Microsoft.Resources/deployments/read",
                "Microsoft.Resources/deployments/write",
                "Microsoft.Resources/deployments/delete",
                "Microsoft.Resources/deployments/cancel/action",
                "Microsoft.Resources/deployments/validate/action",
                "Microsoft.Resources/deployments/whatIf/action",
                "Microsoft.Resources/deployments/exportTemplate/action",
                "Microsoft.Resources/deployments/operations/read",
                "Microsoft.Resources/deployments/operationstatuses/read",
                "Microsoft.Resources/subscriptions/resourceGroups/read",
                "Microsoft.ContainerRegistry/registries/read",
                "Microsoft.Authorization/roleAssignments/read",
                "Microsoft.Authorization/roleDefinitions/read",
            ],
        )
        permissions = properties["permissions"][0]
        self.assertEqual([], permissions["notActions"])
        self.assertEqual([], permissions["dataActions"])
        self.assertEqual([], permissions["notDataActions"])
        self.assertEqual(
            ["[subscriptionResourceId('Microsoft.Resources/resourceGroups', parameters('registryResourceGroupName'))]"],
            properties["assignableScopes"],
        )

        module = next(resource for resource in resources if resource["type"] == "Microsoft.Resources/deployments")
        nested = module["properties"]["template"]["resources"]
        self.assertCountEqual(
            [resource["type"] for resource in nested],
            ["Microsoft.Authorization/roleAssignments", "Microsoft.Authorization/roleAssignments"],
        )
        metadata, administrator = nested
        self.assertNotIn("scope", metadata)
        self.assertEqual("[resourceId('Microsoft.ContainerRegistry/registries', parameters('registryName'))]", administrator["scope"])
        self.assertEqual("2.0", administrator["properties"]["conditionVersion"])
        self.assertEqual("[parameters('registryRoleAdministrationCondition')]", administrator["properties"]["condition"])

    def test_registry_authority_condition_is_exact_and_never_attached_to_workload_pull(self):
        source = REGISTRY_AUTHORITY.read_text()
        self.assertIn("targetScope = 'subscription'", source)
        self.assertIn("ActionMatches{'Microsoft.Authorization/roleAssignments/write'}", source)
        self.assertIn("@Request[Microsoft.Authorization/roleAssignments:RoleDefinitionId] ForAnyOfAnyValues:GuidEquals", source)
        self.assertIn("ActionMatches{'Microsoft.Authorization/roleAssignments/delete'}", source)
        self.assertIn("@Resource[Microsoft.Authorization/roleAssignments:RoleDefinitionId] ForAnyOfAnyValues:GuidEquals", source)
        assignment_source = (REGISTRY_AUTHORITY.parent / "registry-authority-assignments.bicep").read_text()
        self.assertIn("conditionVersion: '2.0'", assignment_source)
        workload_source = (ROOT / "infra/azure-production/acr-pull-role.bicep").read_text().lower()
        self.assertNotIn("condition", workload_source)
        self.assertNotIn("registryroleadministrationcondition", workload_source)

        runtime_source = (ROOT / "src/Deployment/ElsaControl.Deployment.Azure/AzureProviderRegistryAuthority.cs").read_text()
        match = re.search(r"RegistryRoleAdministrationCondition\s*=\s*\"(?P<condition>[^\"]*)\";", runtime_source)
        self.assertIsNotNone(match)
        runtime_condition = match.group("condition")
        compiled_condition = self.registry_authority_template["variables"]["registryRoleAdministrationCondition"]
        self.assertEqual(normalize_condition(runtime_condition), normalize_condition(compiled_condition))


def normalize_condition(value):
    """Match the runtime's safe comparison: formatting outside literals may differ."""
    if not value or not value.strip():
        return ""
    normalized = []
    in_literal = False
    for character in value:
        if unicodedata.category(character) == "Cc" and character not in "\r\n":
            return ""
        if character == "'":
            in_literal = not in_literal
            normalized.append(character)
        elif in_literal and character in "\r\n":
            return ""
        elif in_literal or not character.isspace():
            normalized.append(character if in_literal else character.lower())
    return "" if in_literal else "".join(normalized)


if __name__ == "__main__":
    unittest.main()
