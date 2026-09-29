#!/usr/bin/env python3
"""Compile and inspect the managed lifecycle telemetry sink boundary."""

import json
import shutil
import subprocess
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
MAIN = ROOT / "infra/managed-telemetry/main.bicep"


class ManagedTelemetryInfrastructureTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        if not MAIN.is_file():
            raise AssertionError("Managed telemetry infrastructure is missing")
        az = shutil.which("az")
        if az is None:
            raise RuntimeError("Azure CLI with Bicep is required for telemetry contract tests")
        result = subprocess.run(
            [az, "bicep", "build", "--file", str(MAIN), "--stdout"],
            capture_output=True, text=True, check=False, timeout=60,
        )
        if result.returncode:
            raise AssertionError(result.stderr)
        cls.template = json.loads(result.stdout)
        cls.resources = {resource["type"]: resource for resource in cls.template["resources"]}
        cls.source = MAIN.read_text()

    def test_only_sink_publisher_role_alert_rule_and_action_group_are_created(self):
        self.assertEqual(5, len(self.template["resources"]))
        self.assertEqual({
            "Microsoft.OperationalInsights/workspaces",
            "Microsoft.Insights/components",
            "Microsoft.Authorization/roleAssignments",
            "Microsoft.Insights/actionGroups",
            "Microsoft.Insights/scheduledQueryRules",
        }, set(self.resources))

    def test_local_authentication_is_disabled_on_both_ingestion_surfaces(self):
        workspace = self.resources["Microsoft.OperationalInsights/workspaces"]["properties"]
        component = self.resources["Microsoft.Insights/components"]["properties"]
        self.assertTrue(workspace["features"]["disableLocalAuth"])
        self.assertTrue(component["DisableLocalAuth"])
        self.assertFalse(component["DisableIpMasking"])
        self.assertEqual("[resourceId('Microsoft.OperationalInsights/workspaces', parameters('workspaceName'))]",
                         component["WorkspaceResourceId"])

    def test_retention_and_ingestion_cost_controls_are_bounded(self):
        workspace = self.resources["Microsoft.OperationalInsights/workspaces"]["properties"]
        self.assertEqual("PerGB2018", workspace["sku"]["name"])
        self.assertNotIn("capacityReservationLevel", workspace["sku"])
        self.assertEqual(30, workspace["retentionInDays"])
        self.assertEqual("[parameters('dailyQuotaGb')]", workspace["workspaceCapping"]["dailyQuotaGb"])
        quota = self.template["parameters"]["dailyQuotaGb"]
        self.assertEqual(1, quota["defaultValue"])
        self.assertEqual(1, quota["minValue"])
        self.assertEqual(5, quota["maxValue"])

    def test_publisher_is_bound_to_existing_identity_at_exact_component_scope(self):
        role = self.resources["Microsoft.Authorization/roleAssignments"]
        self.assertEqual("[resourceId('Microsoft.Insights/components', parameters('applicationInsightsName'))]", role["scope"])
        props = role["properties"]
        self.assertEqual("ServicePrincipal", props["principalType"])
        self.assertIn("MonitoringMetricsPublisherRoleId", props["roleDefinitionId"])
        self.assertEqual("3913510d-42f4-4e42-8a64-420c390055eb",
                         self.template["variables"]["MonitoringMetricsPublisherRoleId"])
        self.assertIn("Microsoft.ManagedIdentity/userAssignedIdentities", props["principalId"])
        self.assertNotIn("principalId", self.template["parameters"])

    def test_no_app_mutation_key_output_or_anonymous_dashboard_is_introduced(self):
        serialized = json.dumps(self.template)
        self.assertNotIn("Microsoft.Web/sites", serialized)
        self.assertNotIn("Microsoft.Resources/deploymentScripts", serialized)
        self.assertNotIn("listKeys", serialized)
        outputs = json.dumps(self.template.get("outputs", {})).lower()
        self.assertNotIn("connectionstring", outputs)
        self.assertNotIn("instrumentationkey", outputs)
        self.assertNotIn("unsecured", serialized.lower())

    def test_recovery_required_alert_uses_parameterized_email_and_no_paging(self):
        parameter = self.template["parameters"]["recoveryRequiredAlertEmail"]
        self.assertNotIn("defaultValue", parameter)
        self.assertEqual("string", parameter["type"])
        serialized = json.dumps(self.template)
        self.assertNotRegex(serialized, r"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")
        self.assertNotIn("smsReceivers", serialized)
        self.assertNotIn("voiceReceivers", serialized)
        self.assertNotIn("webhookReceivers", serialized)
        self.assertNotIn("armRoleReceivers", serialized)
        self.assertNotIn("itsmReceivers", serialized)
        self.assertNotIn("azureFunctionReceivers", serialized)
        self.assertNotIn("logicAppReceivers", serialized)
        self.assertNotIn("automationRunbookReceivers", serialized)
        self.assertNotIn("azureAppPushReceivers", serialized)
        action_group = self.resources["Microsoft.Insights/actionGroups"]["properties"]
        self.assertTrue(action_group["enabled"])
        self.assertEqual(1, len(action_group["emailReceivers"]))
        self.assertEqual("[parameters('recoveryRequiredAlertEmail')]",
                         action_group["emailReceivers"][0]["emailAddress"])
        rule = self.resources["Microsoft.Insights/scheduledQueryRules"]["properties"]
        self.assertTrue(rule["enabled"])
        self.assertEqual(1, rule["severity"])
        self.assertFalse(rule["autoMitigate"])
        self.assertEqual("PT5M", rule["evaluationFrequency"])
        self.assertEqual("PT15M", rule["windowSize"])
        self.assertGreaterEqual(rule["criteria"]["allOf"][0]["threshold"], 1)
        self.assertIn(
            "variables('recoveryRequiredActionGroupName')",
            json.dumps(rule["actions"]))

    def test_recovery_required_query_is_app_dependencies_on_the_emitted_event(self):
        query = self.template["variables"]["recoveryRequiredAlertQuery"]
        serialized = json.dumps(self.template)
        self.assertIn("AppDependencies", query)
        self.assertIn("Name == ''managed_lifecycle.recovery_required.entered''", query)
        self.assertIn("Properties.environment", query)
        self.assertIn("Properties.dedupe_identity", query)
        self.assertIn("ingestion_time()", query)
        self.assertNotIn("customEvents", query)
        self.assertNotIn("union", query)
        self.assertNotIn("TimeGenerated", query)
        self.assertNotIn(" or name == ", query)
        self.assertIn("parameters('environment')", serialized)
        environment = self.template["parameters"]["environment"]
        self.assertNotIn("defaultValue", environment)
        self.assertEqual(["staging", "production"], environment["allowedValues"])
        self.assertEqual(
            "managed_lifecycle.recovery_required.entered",
            self.template["variables"]["recoveryRequiredEventName"])

    def test_staging_and_production_alert_resources_are_environment_scoped(self):
        serialized = json.dumps(self.template)
        variables = self.template["variables"]
        self.assertIn("ag-recovery-required-", variables["recoveryRequiredActionGroupName"])
        self.assertIn("qr-recovery-required-entered-", variables["recoveryRequiredAlertRuleName"])
        self.assertIn("parameters('environment')", variables["recoveryRequiredActionGroupName"])
        self.assertIn("parameters('environment')", variables["recoveryRequiredAlertRuleName"])
        self.assertEqual(
            "[variables('recoveryRequiredActionGroupName')]",
            self.resources["Microsoft.Insights/actionGroups"]["name"])
        self.assertEqual(
            "[variables('recoveryRequiredAlertRuleName')]",
            self.resources["Microsoft.Insights/scheduledQueryRules"]["name"])
        self.assertIn("STAGING_RECOVERY_REQUIRED_ALERT_RECIPIENT", self.source)
        self.assertIn("PRODUCTION_RECOVERY_REQUIRED_ALERT_RECIPIENT", self.source)
        self.assertNotIn("sipke", self.source.lower())
        self.assertNotIn("@valence", self.source.lower())


if __name__ == "__main__":
    unittest.main()
