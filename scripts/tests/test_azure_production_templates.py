#!/usr/bin/env python3
"""Focused offline contracts for the production Azure template authority."""

from __future__ import annotations

import json
import re
import shutil
import subprocess
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
PRODUCTION = ROOT / "infra" / "azure-production"
MAIN = PRODUCTION / "main.bicep"
APP = PRODUCTION / "modules" / "container-app.bicep"
HANDOFF_STRING_PARAMETERS = (
    "managedHandoffInstanceId",
    "managedHandoffAudience",
    "managedHandoffControlBaseUrl",
    "managedHandoffControlContinuationUrl",
    "managedHandoffRuntimeMaximumLifetime",
)


class AzureProductionTemplateTests(unittest.TestCase):
    def test_runner_contract_is_checked_in_without_proof_only_artifacts(self) -> None:
        expected = {
            "main.bicep",
            "acr-pull-role.bicep",
            "sql-bootstrap.sql",
            "modules/identity.bicep",
            "modules/key-vault.bicep",
            "modules/observability.bicep",
            "modules/sql.bicep",
            "modules/container-apps-environment.bicep",
            "modules/container-app.bicep",
        }
        actual = {str(path.relative_to(PRODUCTION)) for path in PRODUCTION.rglob("*") if path.is_file()}
        self.assertTrue(expected <= actual)

    def test_production_templates_have_no_disposable_authority_markers(self) -> None:
        source = "\n".join(path.read_text() for path in PRODUCTION.rglob("*" ) if path.is_file())
        for marker in ("proof", "expiry", "proof-admin", "feedz.io", "Elsa 3.8", "3.8.0-preview"):
            self.assertNotIn(marker.lower(), source.lower(), marker)

    def test_release_identity_is_data_driven(self) -> None:
        main = MAIN.read_text()
        app = APP.read_text()
        for parameter in ("elsaVersion", "releaseLine", "sqlWorkflowPackageVersion", "sqlQuartzPackageVersion"):
            self.assertRegex(main, rf"(?m)^param\s+{parameter}\s+string\s*$")
        self.assertIn("param releaseVersion string = ''", main)
        self.assertIn("param releaseFeedServiceIndex string = 'https://api.nuget.org/v3/index.json'", main)
        self.assertIn("releaseLine: releaseLine", main)
        self.assertIn("releaseVersion: effectiveReleaseVersion", main)
        self.assertIn("releaseFeedServiceIndex: releaseFeedServiceIndex", main)
        self.assertIn("'workload-name': workloadName", main)
        self.assertIn("'release-line': releaseLine", main)
        self.assertIn("'release-version': effectiveReleaseVersion", main)
        self.assertIn("name: 'ELSA_RELEASE_LINE'", app)
        self.assertIn("name: 'ELSA_RELEASE_VERSION'", app)
        self.assertIn("value: releaseFeedServiceIndex", app)
        self.assertNotRegex(app, r"param\s+(elsaVersion|releaseLine|releaseVersion)\s+string\s*=")

    def test_workload_capacity_is_required_plan_data_without_template_literals(self) -> None:
        main = MAIN.read_text()
        app = APP.read_text()
        capacity = {
            "workloadMinReplicas": ("int", "minReplicas"),
            "workloadMaxReplicas": ("int", "maxReplicas"),
            "workloadCpu": ("string", "cpu"),
            "workloadMemory": ("string", "memory"),
        }
        for parameter, (kind, module_parameter) in capacity.items():
            self.assertRegex(main, rf"(?m)^param\s+{parameter}\s+{kind}\s*$")
            self.assertRegex(main, rf"(?m)^\s+{module_parameter}: {parameter}$")
            self.assertRegex(app, rf"(?m)^param\s+{module_parameter}\s+{kind}\s*$")
        self.assertRegex(main, r"@minValue\(0\)\s*@maxValue\(300\)\s*param workloadMinReplicas int")
        self.assertRegex(main, r"@minValue\(1\)\s*@maxValue\(300\)\s*param workloadMaxReplicas int")
        self.assertIn("capacity=${workloadMinReplicas}/${workloadMaxReplicas}/${workloadCpu}/${workloadMemory}", main)
        self.assertRegex(app, r"(?m)^\s+minReplicas: minReplicas$")
        self.assertRegex(app, r"(?m)^\s+maxReplicas: maxReplicas$")
        self.assertNotRegex(app, r"(?:min|max)Replicas:\s*\d")
        self.assertIn("resources: consumptionResources['${cpu}/${memory}']", app)
        self.assertNotRegex(app, r"resources:\s*\{\s*cpu:")

    def test_example_parameters_supply_the_required_capacity(self) -> None:
        parameters = json.loads((PRODUCTION / "main.parameters.example.json").read_text())["parameters"]
        self.assertEqual(
            {"workloadMinReplicas": 1, "workloadMaxReplicas": 1, "workloadCpu": "0.5", "workloadMemory": "1Gi"},
            {name: parameters[name]["value"] for name in ("workloadMinReplicas", "workloadMaxReplicas", "workloadCpu", "workloadMemory")},
        )

    def test_managed_handoff_is_required_typed_data_and_derives_its_callback(self) -> None:
        main = MAIN.read_text()
        app = APP.read_text()
        self.assertRegex(main, r"(?m)^param\s+managedHandoffEnabled\s+bool\s*$")
        self.assertRegex(app, r"(?m)^param\s+managedHandoffEnabled\s+bool\s*$")
        for parameter in HANDOFF_STRING_PARAMETERS:
            self.assertRegex(main, rf"(?m)^param\s+{parameter}\s+string\s*=\s*''$")
            self.assertRegex(main, rf"(?m)^\s+{parameter}: {parameter}$")
        self.assertRegex(main, r"(?m)^param\s+managedHandoffRuntimePermissions\s+array\s*=\s*\[\]$")
        # The callback is never a caller input: it is the external app origin on the environment's default domain.
        self.assertNotRegex(main, r"(?m)^param\s+managedHandoffCallbackUri\b")
        self.assertIn(
            "toLower('https://${workloadAppName}.${containerEnvironment.outputs.defaultDomain}/managed-elsa/handoff/callback')",
            main,
        )
        self.assertIn("name: workloadAppName", main)
        self.assertIn("managedHandoffCallbackUri: managedHandoffCallbackUri", main)
        self.assertIn("output managedHandoffCallbackUri string = deployWorkload ? managedHandoffCallbackUri : ''", main)
        self.assertIn("|handoff=${managedHandoffInput}|", main)

    def test_managed_handoff_environment_is_complete_or_explicitly_disabled(self) -> None:
        app = APP.read_text()
        for name, value in (
            ("ManagedElsa__Handoff__ControlBaseUrl", "managedHandoffControlBaseUrl"),
            ("ManagedElsa__Handoff__ControlContinuationUrl", "managedHandoffControlContinuationUrl"),
            ("ManagedElsa__Handoff__InstanceId", "managedHandoffInstanceId"),
            ("ManagedElsa__Handoff__Audience", "managedHandoffAudience"),
            ("ManagedElsa__Handoff__CallbackUri", "managedHandoffCallbackUri"),
            ("ManagedElsa__Handoff__UpstreamAuthenticationScheme", "'Jwt-or-ApiKey'"),
            ("ManagedElsa__Handoff__SuccessPath", "'/'"),
            ("ManagedElsa__Handoff__StateLifetime", "'00:05:00'"),
            ("ManagedElsa__Handoff__RuntimeMaximumLifetime", "managedHandoffRuntimeMaximumLifetime"),
            ("ASPNETCORE_FORWARDEDHEADERS_ENABLED", "'true'"),
        ):
            self.assertRegex(app, rf"name: '{name}'\s+value: {re.escape(value)}\n", name)
        self.assertIn("name: 'ManagedElsa__Handoff__RuntimePermissions__${index}'", app)
        self.assertRegex(app, r"disabled: \[\s*\{\s*name: 'ManagedElsa__Handoff__Enabled'\s+value: 'false'")
        self.assertRegex(app, r"enabled: concat\(\[\s*\{\s*name: 'ManagedElsa__Handoff__Enabled'\s+value: 'true'")
        # No 'invalid' entry: an enabled handoff with a missing input or more than one replica fails the deployment.
        self.assertIn(
            "var managedHandoffMode = !managedHandoffEnabled ? 'disabled' : managedHandoffInputsComplete && maxReplicas == 1 ? 'enabled' : 'invalid'",
            app,
        )
        self.assertIn("}[managedHandoffMode]", app)
        self.assertNotRegex(app, r"(?m)^\s+invalid:")
        for parameter in (*HANDOFF_STRING_PARAMETERS, "managedHandoffCallbackUri", "managedHandoffRuntimePermissions"):
            self.assertIn(f"!empty({parameter})", app)
        self.assertIn("concat(nuplaneFeedEnvironment, featureEnvironment, managedHandoffEnvironment)", app)

    def test_example_parameters_enable_a_complete_handoff(self) -> None:
        parameters = json.loads((PRODUCTION / "main.parameters.example.json").read_text())["parameters"]
        self.assertIs(True, parameters["managedHandoffEnabled"]["value"])
        instance_id = parameters["managedHandoffInstanceId"]["value"]
        self.assertEqual(f"urn:elsa:instance:{instance_id}", parameters["managedHandoffAudience"]["value"])
        control = parameters["managedHandoffControlBaseUrl"]["value"]
        self.assertEqual(f"{control}/admin/runtimes", parameters["managedHandoffControlContinuationUrl"]["value"])
        self.assertEqual("08:00:00", parameters["managedHandoffRuntimeMaximumLifetime"]["value"])
        self.assertEqual(["*"], parameters["managedHandoffRuntimePermissions"]["value"])
        self.assertNotIn("managedHandoffCallbackUri", parameters)

    def test_runtime_admin_identity_is_required_and_secret_safe(self) -> None:
        main = MAIN.read_text()
        app = APP.read_text()
        self.assertRegex(main, r"(?m)^param\s+adminUsername\s+string\s*$")
        self.assertRegex(app, r"(?m)^param\s+adminUsername\s+string\s*$")
        self.assertNotIn("proof-admin", main.lower())
        self.assertNotIn("proof-admin", app.lower())
        for source in (main, app):
            output_lines = [line.lower() for line in source.splitlines() if line.lstrip().startswith("output ")]
            for line in output_lines:
                self.assertFalse(any(token in line for token in ("secret", "password", "connectionstring")))

    def test_vault_bootstrap_uses_a_managed_service_principal(self) -> None:
        vault = (PRODUCTION / "modules/key-vault.bicep").read_text()
        self.assertNotIn("principalType: 'User'", vault)
        self.assertEqual(2, vault.count("principalType: 'ServicePrincipal'"))

    def test_runner_files_preserve_immutable_image_and_sql_bootstrap_contract(self) -> None:
        main = MAIN.read_text()
        acr = (PRODUCTION / "acr-pull-role.bicep").read_text()
        bootstrap = (PRODUCTION / "sql-bootstrap.sql").read_text()
        self.assertIn("param imageDigest string", main)
        self.assertIn("@sha256:${toLower(imageDigest)}", main)
        self.assertNotRegex(main, r"param\s+imageTag\b")
        for parameter in ("registryName", "workloadIdentityId", "workloadPrincipalId"):
            self.assertIn(f"param {parameter} ", acr)
        self.assertIn("TYPE = E", bootstrap)
        self.assertIn("__WORKLOAD_IDENTITY_CLIENT_ID__", bootstrap)
        self.assertNotIn("__WORKLOAD_IDENTITY_OBJECT_ID__", bootstrap)

    def test_templates_compile_with_bicep_when_available(self) -> None:
        az = shutil.which("az")
        if az is None:
            self.skipTest("Azure CLI is not installed")
        for template in (MAIN, PRODUCTION / "acr-pull-role.bicep"):
            result = subprocess.run(
                [az, "bicep", "build", "--file", str(template), "--stdout"],
                check=False,
                capture_output=True,
                text=True,
            )
            self.assertEqual(0, result.returncode, result.stderr)
            if template == MAIN:
                compiled = json.loads(result.stdout)["parameters"]
                self.assertEqual({"type": "bool"}, {key: value for key, value in compiled["managedHandoffEnabled"].items() if key != "metadata"})
                self.assertEqual("array", compiled["managedHandoffRuntimePermissions"]["type"])
                for parameter in HANDOFF_STRING_PARAMETERS:
                    self.assertEqual(("string", ""), (compiled[parameter]["type"], compiled[parameter]["defaultValue"]))


if __name__ == "__main__":
    unittest.main()
