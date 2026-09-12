#!/usr/bin/env python3
"""Focused offline contracts for the production Azure template authority."""

from __future__ import annotations

import json
import shutil
import subprocess
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
PRODUCTION = ROOT / "infra" / "azure-production"
MAIN = PRODUCTION / "main.bicep"
APP = PRODUCTION / "modules" / "container-app.bicep"


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


if __name__ == "__main__":
    unittest.main()
