"""Control Catalog SQL SKU parameters for azd and the deploy helper.

Staging/test and production Catalog databases use Standard S0 with a 250 GiB
cap (the S0 included size). Pass GP_S_* only when an environment still needs
serverless. Max size is a string so the 250 GiB byte count (268435456000)
never passes through an ARM 32-bit int parameter.
"""

from __future__ import annotations

from typing import Mapping

PARAMETER_SCHEMA = "https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#"
STAGING_ENVIRONMENT_NAME = "valence-control-staging"
STAGING_LOCATION = "westeurope"


STAGING_ENVIRONMENT_NAMES = frozenset({"test", "valence-control-staging"})
STAGING_MAX_SIZE_BYTES = "268435456000"
STANDARD_S0_MAX_SIZE_BYTES = STAGING_MAX_SIZE_BYTES

STANDARD_S0_SKU: dict[str, object] = {
    "sqlDatabaseSkuName": "S0",
    "sqlDatabaseSkuTier": "Standard",
    "sqlDatabaseSkuFamily": "",
    "sqlDatabaseSkuCapacity": 10,
    "sqlDatabaseMaxSizeBytes": STANDARD_S0_MAX_SIZE_BYTES,
}

STAGING_SKU: dict[str, object] = dict(STANDARD_S0_SKU)
PRODUCTION_SKU: dict[str, object] = dict(STANDARD_S0_SKU)


def sku_parameters(environment_name: str) -> dict[str, object]:
    """Return the explicit Catalog SQL SKU payload for one Azure environment name."""

    if environment_name in STAGING_ENVIRONMENT_NAMES:
        return dict(STAGING_SKU)
    return dict(PRODUCTION_SKU)


def is_staging_sql_environment(environment_name: str) -> bool:
    return environment_name in STAGING_ENVIRONMENT_NAMES


def as_json_object(parameters: Mapping[str, object]) -> dict[str, object]:
    return dict(parameters)


def _parameter_values(values: Mapping[str, object]) -> dict[str, dict[str, object]]:
    return {name: {"value": value} for name, value in values.items()}


def staging_parameters_document() -> dict[str, object]:
    """ARM parameter file for valence-control-staging Catalog S0 / 250 GiB."""

    return {
        "$schema": PARAMETER_SCHEMA,
        "contentVersion": "1.0.0.0",
        "parameters": {
            "environmentName": {"value": STAGING_ENVIRONMENT_NAME},
            "location": {"value": STAGING_LOCATION},
            "principalId": {"value": ""},
            **_parameter_values(STAGING_SKU),
        },
    }


def production_parameters_document() -> dict[str, object]:
    """ARM parameter overlay that pins production to Standard S0 / 250 GiB."""

    return {
        "$schema": PARAMETER_SCHEMA,
        "contentVersion": "1.0.0.0",
        "parameters": _parameter_values(PRODUCTION_SKU),
    }
