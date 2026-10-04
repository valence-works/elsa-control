"""Re-apply Control Catalog SQL SKU parameters after Aspire regeneration.

Aspire still emits a hardcoded GP_S_Gen5 Catalog. This patch is idempotent: an
already parameterized module and main template are left unchanged. A missing
control-sql module (test fixtures that only regenerate main.bicep) is a no-op
for that file.
"""

from __future__ import annotations

import json
import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
MODULE = ROOT / "infra" / "control-sql" / "control-sql.module.bicep"
MAIN = ROOT / "infra" / "main.bicep"
PARAMETERS = ROOT / "infra" / "main.parameters.json"
STAGING_PARAMETERS = ROOT / "infra" / "main.parameters.staging.json"
PRODUCTION_PARAMETERS = ROOT / "infra" / "main.parameters.production.json"

sys.path.insert(0, str(ROOT / "scripts" / "lib"))
from control_sql_sku import production_parameters_document, staging_parameters_document

SKU_PARAMS = """
@description('Azure SQL Catalog service objective (SKU name). Production default is S0.')
param sqlDatabaseSkuName string = 'S0'

@description('Azure SQL Catalog edition (SKU tier). Production default is Standard.')
param sqlDatabaseSkuTier string = 'Standard'

@description('Azure SQL Catalog SKU family. Leave empty for DTU objectives such as S0.')
param sqlDatabaseSkuFamily string = ''

@description('Azure SQL Catalog SKU capacity (vCores or DTUs).')
param sqlDatabaseSkuCapacity int = 10

@description('Azure SQL Catalog max size in bytes. Empty or 0 omits the property. Use a string so 250 GiB (268435456000) is not an ARM 32-bit int.')
param sqlDatabaseMaxSizeBytes string = '268435456000'
"""

GENERATED_CATALOG_PROPERTIES = """  properties: {
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
"""

PARAMETERIZED_CATALOG = """  properties: catalogProperties
  sku: catalogSku
"""

GENERATED_MODULE_PARAMS = """  params: {
    location: location
  }
"""

PARAMETERIZED_MODULE_PARAMS = """  params: {
    location: location
    sqlDatabaseSkuName: resolvedSqlDatabaseSkuName
    sqlDatabaseSkuTier: resolvedSqlDatabaseSkuTier
    sqlDatabaseSkuFamily: resolvedSqlDatabaseSkuFamily
    sqlDatabaseSkuCapacity: resolvedSqlDatabaseSkuCapacity
    sqlDatabaseMaxSizeBytes: resolvedSqlDatabaseMaxSizeBytes
  }
"""

MAIN_SKU_BLOCK_MARKER = "var resolvedSqlDatabaseSkuName"


def replace_once(content: str, already: str, anchor: str, replacement: str, what: str) -> str:
    if already in content:
        return content
    if content.count(anchor) != 1:
        raise SystemExit(f"Cannot find exactly one {what} to patch.")
    return content.replace(anchor, replacement, 1)


def patch_module(content: str) -> str:
    if "param sqlDatabaseSkuName string" not in content:
        location = "@description('The location for the resource(s) to be deployed.')\nparam location string = resourceGroup().location\n"
        if content.count(location) != 1:
            raise SystemExit("Cannot find the generated control-sql location parameter.")
        vars_block = (
            SKU_PARAMS
            + """
var serverlessSku = startsWith(sqlDatabaseSkuName, 'GP_S_')
var catalogMaxSizeBytes = json(empty(sqlDatabaseMaxSizeBytes) ? '0' : sqlDatabaseMaxSizeBytes)
var catalogSku = empty(sqlDatabaseSkuFamily) ? {
  name: sqlDatabaseSkuName
  tier: sqlDatabaseSkuTier
  capacity: sqlDatabaseSkuCapacity
} : {
  name: sqlDatabaseSkuName
  tier: sqlDatabaseSkuTier
  family: sqlDatabaseSkuFamily
  capacity: sqlDatabaseSkuCapacity
}
var catalogProperties = union(
  {
    zoneRedundant: false
    requestedBackupStorageRedundancy: 'Zone'
    useFreeLimit: false
  },
  catalogMaxSizeBytes > 0 ? {
    maxSizeBytes: catalogMaxSizeBytes
  } : {},
  serverlessSku ? {
    autoPauseDelay: 60
    minCapacity: json('0.5')
  } : {}
)
"""
        )
        content = content.replace(location, location + vars_block, 1)
    content = replace_once(
        content,
        "properties: catalogProperties",
        GENERATED_CATALOG_PROPERTIES,
        PARAMETERIZED_CATALOG,
        "generated Catalog SKU/properties block",
    )
    return content


def patch_main(content: str) -> str:
    if MAIN_SKU_BLOCK_MARKER not in content:
        anchor = "@description('Id of the user or app to assign application roles')\nparam principalId string = ''\n\nvar tags = {\n  'azd-env-name': environmentName\n}\n"
        replacement = """@description('Id of the user or app to assign application roles')
param principalId string = ''

@description('Azure SQL Catalog service objective (SKU name). Empty selects Standard S0 for every environment. Pass GP_S_Gen5 to request serverless.')
param sqlDatabaseSkuName string = ''

@description('Azure SQL Catalog edition (SKU tier). Empty selects GeneralPurpose for GP_S_* names and Standard otherwise.')
param sqlDatabaseSkuTier string = ''

@description('Azure SQL Catalog SKU family. Empty selects Gen5 for GP_S_* objectives and omits the family for DTU objectives.')
param sqlDatabaseSkuFamily string = ''

@description('Azure SQL Catalog SKU capacity (vCores or DTUs). 0 selects the SKU default (10 for S0, 1 for GP_S_Gen5).')
param sqlDatabaseSkuCapacity int = 0

@description('Azure SQL Catalog max size in bytes. Empty selects 250 GiB for S0 and omits the property for other SKUs. 0 omits the property. String avoids ARM 32-bit int overflow.')
param sqlDatabaseMaxSizeBytes string = ''

var resolvedSqlDatabaseSkuName = !empty(sqlDatabaseSkuName) ? sqlDatabaseSkuName : 'S0'
var resolvedSqlDatabaseSkuTier = !empty(sqlDatabaseSkuTier) ? sqlDatabaseSkuTier : (startsWith(resolvedSqlDatabaseSkuName, 'GP_S_') ? 'GeneralPurpose' : 'Standard')
var resolvedSqlDatabaseSkuFamily = !empty(sqlDatabaseSkuFamily) ? sqlDatabaseSkuFamily : (startsWith(resolvedSqlDatabaseSkuName, 'GP_S_') ? 'Gen5' : '')
var resolvedSqlDatabaseSkuCapacity = sqlDatabaseSkuCapacity > 0 ? sqlDatabaseSkuCapacity : (startsWith(resolvedSqlDatabaseSkuName, 'GP_S_') ? 1 : 10)
var resolvedSqlDatabaseMaxSizeBytes = !empty(sqlDatabaseMaxSizeBytes) ? sqlDatabaseMaxSizeBytes : (resolvedSqlDatabaseSkuName == 'S0' ? '268435456000' : '0')

var tags = {
  'azd-env-name': environmentName
}
"""
        content = replace_once(
            content,
            MAIN_SKU_BLOCK_MARKER,
            anchor,
            replacement,
            "main.bicep principalId/tags block for SQL SKU parameters",
        )
    content = replace_once(
        content,
        "sqlDatabaseSkuName: resolvedSqlDatabaseSkuName",
        GENERATED_MODULE_PARAMS,
        PARAMETERIZED_MODULE_PARAMS,
        "control-sql module params block",
    )
    return content


def patch_parameters(document: dict) -> dict:
    parameters = document.setdefault("parameters", {})
    parameters.setdefault("sqlDatabaseSkuName", {"value": ""})
    parameters.setdefault("sqlDatabaseSkuTier", {"value": ""})
    parameters.setdefault("sqlDatabaseSkuFamily", {"value": ""})
    parameters.setdefault("sqlDatabaseSkuCapacity", {"value": 0})
    parameters.setdefault("sqlDatabaseMaxSizeBytes", {"value": ""})
    return document


def write_json(path: Path, document: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(document, indent=2) + "\n")


def write_environment_parameter_files() -> None:
    """Recreate the hand-authored SKU overlays after `rm -rf infra`.

    Aspire generate does not emit these files. They are not in the regenerate
    preserve list because restore runs on EXIT after this patch; recreating
    them here is the durable path.
    """

    if not (ROOT / "infra").exists():
        return
    write_json(STAGING_PARAMETERS, staging_parameters_document())
    write_json(PRODUCTION_PARAMETERS, production_parameters_document())


def main() -> None:
    if MODULE.exists():
        MODULE.write_text(patch_module(MODULE.read_text()))
    if MAIN.exists():
        MAIN.write_text(patch_main(MAIN.read_text()))
    if PARAMETERS.exists():
        document = json.loads(PARAMETERS.read_text())
        write_json(PARAMETERS, patch_parameters(document))
    write_environment_parameter_files()


if __name__ == "__main__":
    main()
