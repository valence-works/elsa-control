"""Control Catalog SQL SKU parameters for azd and the deploy helper.

Staging/test stay on Standard S0 with a 250 GiB cap. Production keeps the
existing GP_S_Gen5_1 serverless objective. Max size is a string so the 250 GiB
byte count (268435456000) never passes through an ARM 32-bit int parameter.
"""

from __future__ import annotations

from typing import Mapping


STAGING_ENVIRONMENT_NAMES = frozenset({"test", "valence-control-staging"})
STAGING_MAX_SIZE_BYTES = "268435456000"

STAGING_SKU: dict[str, object] = {
    "sqlDatabaseSkuName": "S0",
    "sqlDatabaseSkuTier": "Standard",
    "sqlDatabaseSkuFamily": "",
    "sqlDatabaseSkuCapacity": 10,
    "sqlDatabaseMaxSizeBytes": STAGING_MAX_SIZE_BYTES,
}

PRODUCTION_SKU: dict[str, object] = {
    "sqlDatabaseSkuName": "GP_S_Gen5",
    "sqlDatabaseSkuTier": "GeneralPurpose",
    "sqlDatabaseSkuFamily": "Gen5",
    "sqlDatabaseSkuCapacity": 1,
    "sqlDatabaseMaxSizeBytes": "0",
}


def sku_parameters(environment_name: str) -> dict[str, object]:
    """Return the explicit Catalog SQL SKU payload for one Azure environment name."""

    if environment_name in STAGING_ENVIRONMENT_NAMES:
        return dict(STAGING_SKU)
    return dict(PRODUCTION_SKU)


def is_staging_sql_environment(environment_name: str) -> bool:
    return environment_name in STAGING_ENVIRONMENT_NAMES


def as_json_object(parameters: Mapping[str, object]) -> dict[str, object]:
    return dict(parameters)
