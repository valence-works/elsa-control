#!/usr/bin/env python3
"""Compile and inspect the managed lifecycle telemetry sink boundary."""

import json
import re
import shutil
import subprocess
import unittest
from datetime import datetime, timedelta, timezone
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
MAIN = ROOT / "infra/managed-telemetry/main.bicep"
QUERY = ROOT / "infra/managed-telemetry/recovery-required-entered.kql"
ENVIRONMENT_PLACEHOLDER = "{{environment}}"
EVENT_NAME = "managed_lifecycle.recovery_required.entered"


def load_rule_query(environment: str) -> str:
    if not QUERY.is_file():
        raise AssertionError("RecoveryRequired alert query source is missing")
    return QUERY.read_text().replace(ENVIRONMENT_PLACEHOLDER, environment)


def _unquote(value: str):
    if (value.startswith("'") and value.endswith("'")) or (value.startswith('"') and value.endswith('"')):
        return value[1:-1]
    return value


def _resolve(expr: str, row: dict, now: datetime):
    expr = expr.strip()
    if (expr.startswith("'") and expr.endswith("'")) or (expr.startswith('"') and expr.endswith('"')):
        return expr[1:-1]
    ago = re.fullmatch(r"ago\((\d+)m\)", expr)
    if ago:
        return now - timedelta(minutes=int(ago.group(1)))
    if expr == "ingestion_time()":
        return row["ingestion_time"]
    tostring = re.fullmatch(r"tostring\((.+)\)", expr)
    if tostring:
        value = _resolve(tostring.group(1), row, now)
        return "" if value is None else str(value)
    nonempty = re.fullmatch(r"isnotempty\((.+)\)", expr)
    if nonempty:
        value = _resolve(nonempty.group(1), row, now)
        return value not in (None, "")
    if "." in expr:
        current = row
        for part in expr.split("."):
            if not isinstance(current, dict) or part not in current:
                return None
            current = current[part]
        return current
    if expr in row:
        return row[expr]
    return _unquote(expr)


def _compare(expr: str, row: dict, now: datetime) -> bool:
    for operator in ("==", ">"):
        if operator not in expr:
            continue
        left, right = expr.split(operator, 1)
        left_value = _resolve(left, row, now)
        right_value = _resolve(right, row, now)
        if operator == "==":
            return left_value == right_value
        return left_value > right_value
    return bool(_resolve(expr, row, now))


def run_rule_query(rows, environment="staging", now=None):
    """Execute the shared KQL source against an in-memory AppDependencies fixture."""
    now = now or datetime.now(timezone.utc)
    stages = []
    for raw in load_rule_query(environment).splitlines():
        line = raw.split("//", 1)[0].strip()
        if not line:
            continue
        stages.append(line)
    if not stages or stages[0] != "AppDependencies":
        raise AssertionError("RecoveryRequired query must start from AppDependencies")

    current = [dict(row) for row in rows]
    for stage in stages[1:]:
        if not stage.startswith("|"):
            raise AssertionError(f"Unsupported KQL stage: {stage}")
        body = stage[1:].strip()
        if body.startswith("where "):
            current = [row for row in current if _compare(body[len("where "):], row, now)]
            continue
        if body.startswith("extend "):
            name, expr = body[len("extend "):].split("=", 1)
            for row in current:
                row[name.strip()] = _resolve(expr, row, now)
            continue
        summarized = re.fullmatch(
            r"summarize (\w+) = min\((.+)\) by (\w+)",
            body,
        )
        if summarized:
            alias, expr, key = summarized.groups()
            grouped = {}
            for row in current:
                grouped.setdefault(row[key], []).append(row)
            current = [
                {key: identity, alias: min(_resolve(expr, row, now) for row in group)}
                for identity, group in grouped.items()
            ]
            continue
        raise AssertionError(f"Unsupported KQL stage: {stage}")
    return current


def dependency(identity, ingested_at, environment="staging", name=EVENT_NAME):
    return {
        "Name": name,
        "Properties": {
            "environment": environment,
            "dedupe_identity": identity,
        },
        "ingestion_time": ingested_at,
    }


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
        self.assertEqual("PT1H", rule["windowSize"])
        condition = rule["criteria"]["allOf"][0]
        self.assertGreaterEqual(condition["threshold"], 1)
        self.assertEqual("identity", condition["dimensions"][0]["name"])
        self.assertEqual("Include", condition["dimensions"][0]["operator"])
        self.assertEqual(["*"], condition["dimensions"][0]["values"])
        self.assertIn(
            "variables('recoveryRequiredActionGroupName')",
            json.dumps(rule["actions"]))
        self.assertIn("One email per RecoveryRequired entry", rule["description"])
        self.assertIn(
            "more than an hour after its first ingestion can email again",
            rule["description"])
        self.assertNotIn("one email per actual RecoveryRequired entry", self.source)

    def test_recovery_required_query_is_app_dependencies_on_the_emitted_event(self):
        query = self.template["variables"]["recoveryRequiredAlertQuery"]
        serialized = json.dumps(self.template)
        source_query = QUERY.read_text()
        loaded_name = re.search(r"variables\('([^']+)'\)", query)
        self.assertIsNotNone(loaded_name)
        loaded_query = self.template["variables"][loaded_name.group(1)]
        self.assertEqual(source_query, loaded_query)
        self.assertIn("loadTextContent('recovery-required-entered.kql')", self.source)
        self.assertIn(ENVIRONMENT_PLACEHOLDER, source_query)
        self.assertIn("firstIngested = min(ingestion_time()) by identity", source_query)
        self.assertIn("firstIngested > ago(5m)", source_query)
        self.assertIn("AppDependencies", loaded_query)
        self.assertIn("Name == 'managed_lifecycle.recovery_required.entered'", loaded_query)
        self.assertIn("Properties.environment", loaded_query)
        self.assertIn("Properties.dedupe_identity", loaded_query)
        self.assertIn("ingestion_time()", loaded_query)
        self.assertIn("firstIngested", loaded_query)
        self.assertNotIn("customEvents", loaded_query)
        self.assertNotIn("union", loaded_query)
        self.assertNotIn("TimeGenerated", loaded_query)
        self.assertNotIn(" or name == ", loaded_query)
        self.assertIn("[replace(", query)
        self.assertIn("{{environment}}", query)
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


class RecoveryRequiredAlertQueryTests(unittest.TestCase):
    def test_bicep_and_fixture_load_the_same_query_file(self):
        source = MAIN.read_text()
        query = QUERY.read_text()
        self.assertIn("loadTextContent('recovery-required-entered.kql')", source)
        self.assertIn(ENVIRONMENT_PLACEHOLDER, query)
        self.assertIn("firstIngested = min(ingestion_time()) by identity", query)
        self.assertIn("firstIngested > ago(5m)", query)
        self.assertIn("One email per RecoveryRequired entry", source)
        self.assertIn(
            "more than an hour after its first ingestion can email again",
            source,
        )
        self.assertNotIn("one email per actual RecoveryRequired entry", source)

    def test_rule_query_datatable_fixture_covers_first_ingestion_gating(self):
        now = datetime(2026, 10, 3, 12, 0, tzinfo=timezone.utc)
        first_slot = now - timedelta(minutes=7)
        second_slot = now - timedelta(minutes=2)
        resend_20m = now - timedelta(minutes=20)
        recovered = now - timedelta(minutes=40)

        consecutive_first = run_rule_query(
            [dependency("entry-a", second_slot)],
            now=now,
        )
        self.assertEqual(["entry-a"], [row["identity"] for row in consecutive_first])

        consecutive_resend = run_rule_query(
            [dependency("entry-a", first_slot), dependency("entry-a", second_slot)],
            now=now,
        )
        self.assertEqual([], consecutive_resend)

        twenty_minute_resend = run_rule_query(
            [dependency("entry-b", resend_20m), dependency("entry-b", second_slot)],
            now=now,
        )
        self.assertEqual([], twenty_minute_resend)

        two_identities = run_rule_query(
            [dependency("entry-c", second_slot), dependency("entry-d", second_slot)],
            now=now,
        )
        self.assertCountEqual(["entry-c", "entry-d"], [row["identity"] for row in two_identities])

        after_recover = run_rule_query(
            [dependency("entry-e-attempt-1", recovered), dependency("entry-e-attempt-2", second_slot)],
            now=now,
        )
        self.assertEqual(["entry-e-attempt-2"], [row["identity"] for row in after_recover])


if __name__ == "__main__":
    unittest.main()
