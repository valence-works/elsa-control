"""Shared names and predicates for Control API Key Vault references.

Scripts that touch App Service settings use this module so they can recognize
a reference without printing or comparing secret values.
"""

from __future__ import annotations

import os
import re
from typing import Mapping

KEY_VAULT_REFERENCE_PREFIX = "@Microsoft.KeyVault("
# Versionless SecretUri only. A trailing secret version, VaultName= form, or
# arbitrary contents inside the parentheses is not a supported reference.
KEY_VAULT_REFERENCE = re.compile(
    r"^@Microsoft\.KeyVault\(SecretUri=https://[a-zA-Z0-9-]{3,24}"
    r"\.vault\.azure\.net/secrets/[A-Za-z0-9-]{1,127}\)$"
)

# Full inventory, including production. Keep the names in sync with
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

# Opt-in secret settings: present only when their feature is configured, so a
# missing one is not a failure. When present each must be a versionless
# reference that resolves. UnresolvedKeyVaultReferenceGuard does not list them;
# the feature's own startup validator refuses an unresolved reference while the
# feature is enabled, so RateLimiting__BffCredentials__Enabled=false stays a
# complete off switch.
OPTIONAL_SECRET_APP_SETTINGS: dict[str, str] = {
    "RateLimiting__BffCredentials__Sources__0__Secret": "RateLimiting-BffCredentials-cloud-Secret",
    "RateLimiting__BffCredentials__Sources__0__PreviousSecret": "RateLimiting-BffCredentials-cloud-PreviousSecret",
    "RateLimiting__BffCredentials__Sources__1__Secret": "RateLimiting-BffCredentials-hub-Secret",
    "RateLimiting__BffCredentials__Sources__1__PreviousSecret": "RateLimiting-BffCredentials-hub-PreviousSecret",
}

REQUIRED_SECRET_APP_SETTING_NAMES: tuple[str, ...] = tuple(SECRET_APP_SETTINGS)
OPTIONAL_SECRET_APP_SETTING_NAMES: tuple[str, ...] = tuple(OPTIONAL_SECRET_APP_SETTINGS)

# Owner-approved staging cutover uses the root Stripe webhook signing secret.
# Bind the exception to the actual ARM scope, never to caller flags or absence.
STAGING_SECRET_RESOURCE_GROUP_ID = (
    "/subscriptions/8e23037a-420f-4ad0-9594-9d194de29e84/"
    "resourceGroups/rg-valence-control-staging"
)
STAGING_EXCLUDED_SECRET_APP_SETTING_NAMES = (
    "Billing__Stripe__WebhookSources__0__SigningSecret",
    "Billing__Stripe__WebhookSources__1__SigningSecret",
)
# Every secret setting name, required or optional. Scripts that must never
# capture, compare or write a secret setting use this.
SECRET_APP_SETTING_NAMES: tuple[str, ...] = REQUIRED_SECRET_APP_SETTING_NAMES + OPTIONAL_SECRET_APP_SETTING_NAMES


def secret_app_setting_requirements(site_resource_id: str) -> tuple[tuple[str, str], ...]:
    """Select six staging requirements; every other scope retains all eight.

    Excluded staging settings must stay absent, even if a reference resolves.
    The full secret maps remain unchanged for guards and reconciliation.
    """
    group_id = site_resource_id.lower().split("/providers/", 1)[0]
    staging = group_id == STAGING_SECRET_RESOURCE_GROUP_ID.lower()
    return tuple(
        ("absent" if staging and name in STAGING_EXCLUDED_SECRET_APP_SETTING_NAMES else "required", name)
        for name in REQUIRED_SECRET_APP_SETTING_NAMES
    ) + tuple(("optional", name) for name in OPTIONAL_SECRET_APP_SETTING_NAMES)


def is_key_vault_reference(value: str | None) -> bool:
    """Return True when value is a versionless SecretUri Key Vault reference."""

    if not isinstance(value, str):
        return False
    return KEY_VAULT_REFERENCE.fullmatch(value.strip()) is not None


# Set by the operator right after the manual reference switch. Until then the
# deploy scripts keep the plain values (pre-cutover mode).
SECRETS_WRITTEN_VARIABLE = "API_KEY_VAULT_SECRETS_WRITTEN"

PLAIN = "plain"
REFERENCE = "reference"
MIXED = "mixed"

PRE_CUTOVER_MODE = "pre-cutover"
KEY_VAULT_MODE = "key-vault"


class KeyVaultModeError(ValueError):
    """A mode refusal. The message carries setting names only, never a value."""


def key_vault_secrets_written(environment: Mapping[str, str] | None = None) -> bool:
    """Return True when API_KEY_VAULT_SECRETS_WRITTEN is ``true`` (any case)."""

    env = os.environ if environment is None else environment
    return env.get(SECRETS_WRITTEN_VARIABLE, "").lower() == "true"


def looks_like_key_vault_reference(value: str | None) -> bool:
    """Return True for any value that starts like a reference, malformed or not."""

    return isinstance(value, str) and value.strip().startswith(KEY_VAULT_REFERENCE_PREFIX)


def _split_secret_settings(
    values: Mapping[str, str], names: tuple[str, ...] = SECRET_APP_SETTING_NAMES
) -> tuple[list[str], list[str], list[str]]:
    present = [name for name in names if name in values]
    references = [name for name in present if looks_like_key_vault_reference(values[name])]
    plain = [name for name in present if name not in references]
    malformed = [name for name in references if not is_key_vault_reference(values[name])]
    return references, plain, malformed


def classify_secret_settings(values: Mapping[str, str]) -> str:
    """Classify the listed secret settings present in ``values``.

    ``values`` maps setting names to the site's values. Names that are not
    listed are ignored, and so are the optional settings: an opt-in feature's
    reference must not turn an otherwise plain site ``mixed``. A value that
    starts with ``@Microsoft.KeyVault(`` but fails the strict pattern counts as
    a reference here.
    """

    references, plain, _ = _split_secret_settings(values, REQUIRED_SECRET_APP_SETTING_NAMES)
    if not references:
        return PLAIN
    return MIXED if plain else REFERENCE


def require_secret_settings_are_all_references(values: Mapping[str, str]) -> None:
    """Refuse, with names only, unless every listed setting present is a valid reference."""

    references, plain, malformed = _split_secret_settings(values)
    if plain:
        valid = [name for name in references if name not in malformed]
        malformed_names = f"; malformed: {', '.join(malformed)}" if malformed else ""
        raise KeyVaultModeError(
            f"{SECRETS_WRITTEN_VARIABLE}=true but listed secret settings are not all Key Vault references "
            f"(references: {', '.join(valid) or 'none'}; plain: {', '.join(plain)}{malformed_names})"
        )
    if malformed:
        raise KeyVaultModeError(f"malformed Key Vault reference: {', '.join(malformed)}")


def select_secret_settings_mode(values: Mapping[str, str], *, secrets_written: bool) -> str:
    """Choose pre-cutover or Key Vault mode, or refuse with a names-only reason.

    With ``secrets_written`` the Key Vault mode is returned without looking at
    the values: the strict reference checks then fail on any plain or
    malformed value exactly as before. Without it, only an all-plain site may
    run in pre-cutover mode.
    """

    if secrets_written:
        return KEY_VAULT_MODE
    classification = classify_secret_settings(values)
    if classification == PLAIN:
        return PRE_CUTOVER_MODE
    references, plain, malformed = _split_secret_settings(values, REQUIRED_SECRET_APP_SETTING_NAMES)
    if classification == REFERENCE:
        reason = (
            f"Key Vault references are present on {', '.join(references)} "
            f"(classification: {REFERENCE}); set {SECRETS_WRITTEN_VARIABLE}=true"
        )
    else:
        reason = (
            f"listed secret settings are {MIXED} "
            f"(references: {', '.join(references)}; plain: {', '.join(plain)}); "
            f"switch every listed setting to a Key Vault reference, then set {SECRETS_WRITTEN_VARIABLE}=true"
        )
    if malformed:
        reason += f"; malformed Key Vault reference: {', '.join(malformed)}"
    raise KeyVaultModeError(reason)
