#!/usr/bin/env python3
"""Offline contracts for parameterized Control Catalog SQL SKU."""

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
sys.path.insert(0, str(ROOT / "scripts" / "lib"))

from control_sql_sku import (  # noqa: E402
    PRODUCTION_SKU,
    STAGING_ENVIRONMENT_NAMES,
    STAGING_MAX_SIZE_BYTES,
    STAGING_SKU,
    sku_parameters,
)

MAIN_BICEP = ROOT / "infra" / "main.bicep"
MODULE = ROOT / "infra" / "control-sql" / "control-sql.module.bicep"
AZD_PARAMETERS = ROOT / "infra" / "main.parameters.json"
STAGING_PARAMETERS = ROOT / "infra" / "main.parameters.staging.json"
PRODUCTION_PARAMETERS = ROOT / "infra" / "main.parameters.production.json"
DEPLOY_SCRIPT = ROOT / "scripts" / "deploy-azure-elsa-control.sh"
REGENERATE = ROOT / "dev" / "regenerate-infra.sh"
PATCH = ROOT / "dev" / "patch-control-sql-sku.py"


def nested_module_parameters(template: dict) -> dict:
    """Return the parameter values a compiled wrapper passes into the SQL module."""

    for resource in template.get("resources", []):
        parameters = (resource.get("properties") or {}).get("parameters")
        if parameters:
            return parameters
    raise AssertionError("Compiled wrapper did not emit nested module parameters.")


class ControlSqlSkuTests(unittest.TestCase):
    def test_helper_library_maps_staging_and_production_names(self) -> None:
        self.assertEqual({"test", "valence-control-staging"}, STAGING_ENVIRONMENT_NAMES)
        self.assertEqual("268435456000", STAGING_MAX_SIZE_BYTES)
        self.assertEqual(STAGING_SKU, sku_parameters("valence-control-staging"))
        self.assertEqual(STAGING_SKU, sku_parameters("test"))
        self.assertEqual(PRODUCTION_SKU, sku_parameters("elsa-control"))
        self.assertEqual(PRODUCTION_SKU, sku_parameters("valence-control-prod"))
        self.assertEqual(PRODUCTION_SKU, sku_parameters("production"))
        self.assertEqual("S0", STAGING_SKU["sqlDatabaseSkuName"])
        self.assertEqual("Standard", STAGING_SKU["sqlDatabaseSkuTier"])
        self.assertEqual(10, STAGING_SKU["sqlDatabaseSkuCapacity"])
        self.assertEqual("GP_S_Gen5", PRODUCTION_SKU["sqlDatabaseSkuName"])
        self.assertEqual("GeneralPurpose", PRODUCTION_SKU["sqlDatabaseSkuTier"])
        self.assertEqual("Gen5", PRODUCTION_SKU["sqlDatabaseSkuFamily"])
        self.assertEqual(1, PRODUCTION_SKU["sqlDatabaseSkuCapacity"])
        self.assertEqual("0", PRODUCTION_SKU["sqlDatabaseMaxSizeBytes"])

    def test_parameter_files_pin_staging_s0_and_production_gp(self) -> None:
        staging = json.loads(STAGING_PARAMETERS.read_text())["parameters"]
        production = json.loads(PRODUCTION_PARAMETERS.read_text())["parameters"]
        azd = json.loads(AZD_PARAMETERS.read_text())["parameters"]

        self.assertEqual("valence-control-staging", staging["environmentName"]["value"])
        self.assertEqual("S0", staging["sqlDatabaseSkuName"]["value"])
        self.assertEqual("Standard", staging["sqlDatabaseSkuTier"]["value"])
        self.assertEqual("", staging["sqlDatabaseSkuFamily"]["value"])
        self.assertEqual(10, staging["sqlDatabaseSkuCapacity"]["value"])
        self.assertEqual(STAGING_MAX_SIZE_BYTES, staging["sqlDatabaseMaxSizeBytes"]["value"])

        self.assertEqual("GP_S_Gen5", production["sqlDatabaseSkuName"]["value"])
        self.assertEqual("GeneralPurpose", production["sqlDatabaseSkuTier"]["value"])
        self.assertEqual("Gen5", production["sqlDatabaseSkuFamily"]["value"])
        self.assertEqual(1, production["sqlDatabaseSkuCapacity"]["value"])
        self.assertEqual("0", production["sqlDatabaseMaxSizeBytes"]["value"])
        self.assertNotIn("environmentName", production)

        for name in (
            "sqlDatabaseSkuName",
            "sqlDatabaseSkuTier",
            "sqlDatabaseSkuFamily",
            "sqlDatabaseSkuCapacity",
            "sqlDatabaseMaxSizeBytes",
        ):
            self.assertIn(name, azd)
        self.assertEqual("", azd["sqlDatabaseSkuName"]["value"])
        self.assertEqual(0, azd["sqlDatabaseSkuCapacity"]["value"])

    def test_templates_parameterize_sku_and_keep_production_defaults(self) -> None:
        main = MAIN_BICEP.read_text()
        module = MODULE.read_text()
        deploy = DEPLOY_SCRIPT.read_text()
        regenerate = REGENERATE.read_text()

        self.assertIn("param sqlDatabaseSkuName string = ''", main)
        self.assertIn("param sqlDatabaseSkuTier string = ''", main)
        self.assertIn("param sqlDatabaseMaxSizeBytes string = ''", main)
        self.assertIn("'valence-control-staging'", main)
        self.assertIn("'test'", main)
        self.assertIn("useStagingControlSqlSku", main)
        self.assertIn("sqlDatabaseSkuName: resolvedSqlDatabaseSkuName", main)
        self.assertNotIn("name: 'GP_S_Gen5'", main)

        self.assertIn("param sqlDatabaseSkuName string = 'GP_S_Gen5'", module)
        self.assertIn("param sqlDatabaseSkuTier string = 'GeneralPurpose'", module)
        self.assertIn("param sqlDatabaseSkuFamily string = 'Gen5'", module)
        self.assertIn("param sqlDatabaseSkuCapacity int = 1", module)
        self.assertIn("sku: catalogSku", module)
        self.assertIn("properties: catalogProperties", module)
        self.assertIn("startsWith(sqlDatabaseSkuName, 'GP_S_')", module)
        self.assertNotIn("name: 'GP_S_Gen5'", module)
        self.assertNotIn("autoPauseDelay: 60", module.split("serverlessSku", 1)[0])

        self.assertIn("from control_sql_sku import sku_parameters", deploy)
        self.assertIn("patch-control-sql-sku.py", regenerate)

    def test_regenerate_patch_is_idempotent_and_rewrites_generated_gp(self) -> None:
        generated_module = """@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource Catalog 'Microsoft.Sql/servers/databases@2023-08-01' = {
  name: 'Catalog'
  location: location
  properties: {
    autoPauseDelay: 60
    zoneRedundant: false
    minCapacity: json('0.5')
    requestedBackupStorageRedundancy: 'Zone'
    useFreeLimit: false
  }
  sku: {
    name: 'GP_S_Gen5'
    tier: 'GeneralPurpose'
    family: 'Gen5'
    capacity: 1
  }
  parent: control_sql
}
"""
        generated_main = """param environmentName string
param location string
@description('Id of the user or app to assign application roles')
param principalId string = ''

var tags = {
  'azd-env-name': environmentName
}

module control_sql 'control-sql/control-sql.module.bicep' = {
  name: 'control-sql'
  scope: rg
  params: {
    location: location
  }
}
"""
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            module = root / "infra" / "control-sql" / "control-sql.module.bicep"
            main = root / "infra" / "main.bicep"
            parameters = root / "infra" / "main.parameters.json"
            module.parent.mkdir(parents=True)
            module.write_text(generated_module)
            main.write_text(generated_main)
            parameters.write_text(
                json.dumps(
                    {
                        "$schema": "https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#",
                        "contentVersion": "1.0.0.0",
                        "parameters": {"environmentName": {"value": "dev"}},
                    }
                )
            )
            script = root / "dev" / "patch-control-sql-sku.py"
            script.parent.mkdir()
            script.write_text(PATCH.read_text())
            result = subprocess.run(
                [sys.executable, str(script)],
                cwd=root,
                capture_output=True,
                text=True,
                check=False,
            )
            self.assertEqual(0, result.returncode, result.stderr)
            patched_module = module.read_text()
            patched_main = main.read_text()
            patched_parameters = json.loads(parameters.read_text())["parameters"]
            self.assertIn("param sqlDatabaseSkuName string = 'GP_S_Gen5'", patched_module)
            self.assertIn("sku: catalogSku", patched_module)
            self.assertNotIn("name: 'GP_S_Gen5'", patched_module)
            self.assertIn("resolvedSqlDatabaseSkuName", patched_main)
            self.assertIn("valence-control-staging", patched_main)
            self.assertEqual("", patched_parameters["sqlDatabaseSkuName"]["value"])

            again = subprocess.run(
                [sys.executable, str(script)],
                cwd=root,
                capture_output=True,
                text=True,
                check=False,
            )
            self.assertEqual(0, again.returncode, again.stderr)
            self.assertEqual(patched_module, module.read_text())
            self.assertEqual(patched_main, main.read_text())

    def test_templates_compile_with_bicep_when_available(self) -> None:
        az = shutil.which("az")
        if az is None:
            self.skipTest("Azure CLI is not installed")
        for template in (MAIN_BICEP, MODULE):
            result = subprocess.run(
                [az, "bicep", "build", "--file", str(template), "--stdout"],
                cwd=ROOT,
                capture_output=True,
                text=True,
                check=False,
            )
            self.assertEqual(0, result.returncode, result.stderr)

    def test_staging_wrapper_build_emits_s0_and_250_gib_without_serverless(self) -> None:
        az = shutil.which("az")
        if az is None:
            self.skipTest("Azure CLI is not installed")
        with tempfile.TemporaryDirectory() as temporary:
            wrapper = Path(temporary) / "staging-sql.bicep"
            compiled = Path(temporary) / "staging-sql.json"
            wrapper.write_text(
                "targetScope = 'resourceGroup'\n"
                f"module control_sql '{os.path.relpath(MODULE, wrapper.parent)}' = {{\n"
                "  name: 'control-sql'\n"
                "  params: {\n"
                "    location: 'westeurope'\n"
                "    sqlDatabaseSkuName: 'S0'\n"
                "    sqlDatabaseSkuTier: 'Standard'\n"
                "    sqlDatabaseSkuFamily: ''\n"
                "    sqlDatabaseSkuCapacity: 10\n"
                f"    sqlDatabaseMaxSizeBytes: '{STAGING_MAX_SIZE_BYTES}'\n"
                "  }\n"
                "}\n"
            )
            result = subprocess.run(
                [az, "bicep", "build", "--file", str(wrapper), "--outfile", str(compiled)],
                cwd=ROOT,
                capture_output=True,
                text=True,
                check=False,
            )
            self.assertEqual(0, result.returncode, result.stderr)
            params = nested_module_parameters(json.loads(compiled.read_text()))
            self.assertEqual("S0", params["sqlDatabaseSkuName"]["value"])
            self.assertEqual("Standard", params["sqlDatabaseSkuTier"]["value"])
            self.assertEqual("", params["sqlDatabaseSkuFamily"]["value"])
            self.assertEqual(10, params["sqlDatabaseSkuCapacity"]["value"])
            self.assertEqual(STAGING_MAX_SIZE_BYTES, params["sqlDatabaseMaxSizeBytes"]["value"])

    def test_production_wrapper_build_keeps_gp_s_gen5_without_max_size(self) -> None:
        az = shutil.which("az")
        if az is None:
            self.skipTest("Azure CLI is not installed")
        with tempfile.TemporaryDirectory() as temporary:
            wrapper = Path(temporary) / "production-sql.bicep"
            compiled = Path(temporary) / "production-sql.json"
            wrapper.write_text(
                "targetScope = 'resourceGroup'\n"
                f"module control_sql '{os.path.relpath(MODULE, wrapper.parent)}' = {{\n"
                "  name: 'control-sql'\n"
                "  params: {\n"
                "    location: 'westeurope'\n"
                "    sqlDatabaseSkuName: 'GP_S_Gen5'\n"
                "    sqlDatabaseSkuTier: 'GeneralPurpose'\n"
                "    sqlDatabaseSkuFamily: 'Gen5'\n"
                "    sqlDatabaseSkuCapacity: 1\n"
                "    sqlDatabaseMaxSizeBytes: '0'\n"
                "  }\n"
                "}\n"
            )
            result = subprocess.run(
                [az, "bicep", "build", "--file", str(wrapper), "--outfile", str(compiled)],
                cwd=ROOT,
                capture_output=True,
                text=True,
                check=False,
            )
            self.assertEqual(0, result.returncode, result.stderr)
            params = nested_module_parameters(json.loads(compiled.read_text()))
            self.assertEqual("GP_S_Gen5", params["sqlDatabaseSkuName"]["value"])
            self.assertEqual("GeneralPurpose", params["sqlDatabaseSkuTier"]["value"])
            self.assertEqual("Gen5", params["sqlDatabaseSkuFamily"]["value"])
            self.assertEqual(1, params["sqlDatabaseSkuCapacity"]["value"])
            self.assertEqual("0", params["sqlDatabaseMaxSizeBytes"]["value"])


if __name__ == "__main__":
    unittest.main()
