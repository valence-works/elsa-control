"""Shared names and predicates for Control API Key Vault references.

Scripts that touch App Service settings use this module so they can recognize
a reference without printing or comparing secret values.
"""

from __future__ import annotations

import re

KEY_VAULT_REFERENCE_PREFIX = "@Microsoft.KeyVault("
# Versionless SecretUri only. A trailing secret version, VaultName= form, or
# arbitrary contents inside the parentheses is not a supported reference.
KEY_VAULT_REFERENCE = re.compile(
    r"^@Microsoft\.KeyVault\(SecretUri=https://[a-zA-Z0-9-]{3,24}"
    r"\.vault\.azure\.net/secrets/[A-Za-z0-9-]{1,127}\)$"
)

# App setting name -> vault secret name. Keep in sync with
# infra/api-key-vault/api-key-vault.module.bicep and
# UnresolvedKeyVaultReferenceGuard.
SECRET_APP_SETTINGS: dict[str, str] = {
    "Authentication__ApiKey": "Authentication-ApiKey",
    "Authentication__ControlIdentity__ClientSecret": "Authentication-ControlIdentity-ClientSecret",
    "Authentication__BuilderClientApiKey": "Authentication-BuilderClientApiKey",
    "Billing__Stripe__SecretKey": "Billing-Stripe-SecretKey",
    "Billing__Stripe__WebhookSigningSecret": "Billing-Stripe-WebhookSigningSecret",
    "Billing__Stripe__WebhookSources__0__SigningSecret": "Billing-Stripe-WebhookSources-control-SigningSecret",
    "Billing__Stripe__WebhookSources__1__SigningSecret": "Billing-Stripe-WebhookSources-hub-SigningSecret",
    "ManagedElsa__Handoff__ActivePrivateKeyPem": "ManagedElsa-Handoff-ActivePrivateKeyPem",
}

SECRET_APP_SETTING_NAMES: tuple[str, ...] = tuple(SECRET_APP_SETTINGS)


def is_key_vault_reference(value: str | None) -> bool:
    """Return True when value is a versionless SecretUri Key Vault reference."""

    if not isinstance(value, str):
        return False
    return KEY_VAULT_REFERENCE.fullmatch(value.strip()) is not None
