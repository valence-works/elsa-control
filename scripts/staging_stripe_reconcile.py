#!/usr/bin/env python3
"""Audit the isolated Stripe test and Azure Control staging configuration.

The command is intentionally read-only by default.  It accepts provider
credentials only through the process environment and emits check names and
safe outcomes, never provider identifiers, URLs returned by Stripe, or secret
values.

Required environment for an audit:

* ``STRIPE_SECRET_KEY`` (must be a ``sk_test_`` key)
* ``STRIPE_WEBHOOK_SECRET`` (must be a ``whsec_`` value)
* ``STRIPE_HOSTED_PRICE_ID``
* ``CONTROL_STAGING_WEBHOOK_URL``
* ``AZURE_RESOURCE_GROUP`` and ``AZURE_WEBAPP_NAME``
* ``CLOUD_PORTAL_RETURN_URL`` (the expected HTTPS Cloud dashboard origin)

``CONTROL_STAGING_WEBHOOK_EVENTS`` is a comma-separated list and defaults to
the events consumed by the Hosted billing projection.  Price amount/currency/
recurrence can be overridden with ``STRIPE_HOSTED_PRICE_*`` variables.  A
portal configuration id may be pinned with ``STRIPE_PORTAL_CONFIGURATION_ID``;
without one, exactly one active test configuration must exist.

``--bootstrap-webhook-secret PATH`` is the one deliberate mutating operation.
It creates the exact test webhook endpoint only when one is not already
present, then writes the one-time returned signing secret to a new file with
mode ``0600``.  The secret is never printed.  The normal audit does not create
or update Stripe objects. ``--bootstrap-portal`` idempotently creates the test
Customer Portal policy only when no active configuration exists.
"""

from __future__ import annotations

import argparse
import base64
import hashlib
import json
import os
import re
import stat
import subprocess
import sys
import tempfile
import urllib.error
import urllib.parse
import urllib.request
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Callable, Mapping, Sequence


DEFAULT_WEBHOOK_EVENTS = frozenset(
    {
        "checkout.session.completed",
        "customer.subscription.created",
        "customer.subscription.updated",
        "customer.subscription.deleted",
        "customer.subscription.paused",
        "customer.subscription.resumed",
    }
)
TEST_SECRET_PREFIX = "sk_test_"
WEBHOOK_SECRET_PREFIX = "whsec_"
STRIPE_API_BASE = "https://api.stripe.com/v1"
HTTPS_SCHEME = "https"
PRICE_ID_PATTERN = re.compile(r"^price_[A-Za-z0-9]+$")
CONTROL_STAGING_HOST_SUFFIX = ".azurewebsites.net"
CLOUD_STAGING_HOST_SUFFIX = ".azurestaticapps.net"


class ReconciliationError(RuntimeError):
    """A safe, non-provider-specific reconciliation failure."""


def require_test_secret(value: str, *, name: str, prefix: str) -> None:
    if not value or not value.startswith(prefix) or len(value) == len(prefix):
        raise ReconciliationError(f"{name} is not a test-mode credential")


def require_test_object(value: Mapping[str, Any], *, kind: str) -> None:
    # Stripe returns livemode on the relevant resources.  Treat an absent or
    # malformed value as unsafe rather than assuming the endpoint was test.
    if value.get("livemode") is not False:
        raise ReconciliationError(f"Stripe returned a non-test {kind}")


def parse_https_url(value: str, *, name: str, allow_query: bool = False) -> str:
    decoded = urllib.parse.unquote(value)
    if any(character.isspace() or ord(character) < 0x20 or character == "\\" for character in decoded):
        raise ReconciliationError(f"{name} contains unsafe URL characters")
    parsed = urllib.parse.urlsplit(value)
    try:
        parsed.port
    except ValueError as error:
        raise ReconciliationError(f"{name} must be an HTTPS URL") from error
    if (
        parsed.scheme != HTTPS_SCHEME
        or not parsed.netloc
        or not parsed.hostname
        or parsed.username
        or parsed.password
    ):
        raise ReconciliationError(f"{name} must be an HTTPS URL")
    if (parsed.query and not allow_query) or parsed.fragment:
        raise ReconciliationError(f"{name} must not contain a query or fragment")
    return value


def _json_object(value: Any, *, kind: str) -> Mapping[str, Any]:
    if not isinstance(value, Mapping):
        raise ReconciliationError(f"Stripe returned an invalid {kind}")
    return value


Transport = Callable[[str, str, Mapping[str, Any] | None, str | None], Mapping[str, Any]]


class StripeApi:
    """Small dependency-free Stripe API client with no response logging."""

    def __init__(self, secret_key: str, transport: Transport | None = None) -> None:
        require_test_secret(secret_key, name="STRIPE_SECRET_KEY", prefix=TEST_SECRET_PREFIX)
        self._secret_key = secret_key
        self._transport = transport or self._request

    def post(
        self,
        path: str,
        form: Mapping[str, Any],
        *,
        idempotency_key: str | None = None,
    ) -> Mapping[str, Any]:
        return _json_object(self._transport("POST", path, form, idempotency_key), kind="response")

    def delete(self, path: str) -> Mapping[str, Any]:
        return _json_object(self._transport("DELETE", path, None, None), kind="response")

    def get(self, path: str) -> Mapping[str, Any]:
        return _json_object(self._transport("GET", path, None, None), kind="response")

    def _request(
        self,
        method: str,
        path: str,
        form: Mapping[str, Any] | None,
        idempotency_key: str | None,
    ) -> Mapping[str, Any]:
        if not path.startswith("/") or path.startswith("//"):
            raise ReconciliationError("Stripe request path is invalid")
        url = STRIPE_API_BASE + path
        headers = {
            "Authorization": "Basic " + base64.b64encode(f"{self._secret_key}:".encode()).decode(),
            "Accept": "application/json",
            "User-Agent": "elsa-control-staging-reconcile/1",
        }
        if idempotency_key:
            headers["Idempotency-Key"] = idempotency_key
        body = None
        if form is not None:
            body = urllib.parse.urlencode(list(_form_items(form))).encode()
            headers["Content-Type"] = "application/x-www-form-urlencoded"
        request = urllib.request.Request(url, data=body, headers=headers, method=method)
        try:
            with urllib.request.urlopen(request, timeout=30) as response:
                payload = json.loads(response.read().decode("utf-8"))
        except (urllib.error.HTTPError, urllib.error.URLError, TimeoutError, json.JSONDecodeError) as error:
            # Do not surface Stripe's error body, request id, object id, or URL.
            raise ReconciliationError("Stripe API request failed") from error
        return _json_object(payload, kind="response")


def stripe_list_all(stripe: StripeApi, path: str) -> list[Mapping[str, Any]]:
    items: list[Mapping[str, Any]] = []
    next_path = path
    for _ in range(100):
        response = stripe.get(next_path)
        data = response.get("data")
        if not isinstance(data, list) or any(not isinstance(item, Mapping) for item in data):
            raise ReconciliationError("Stripe returned invalid paginated data")
        items.extend(data)
        has_more = response.get("has_more")
        if not isinstance(has_more, bool):
            raise ReconciliationError("Stripe pagination state is invalid")
        if not has_more:
            return items
        if not data or not isinstance(data[-1].get("id"), str) or not data[-1]["id"]:
            raise ReconciliationError("Stripe pagination cursor is invalid")
        parsed = urllib.parse.urlsplit(next_path)
        query = urllib.parse.parse_qsl(parsed.query, keep_blank_values=True)
        query = [(name, value) for name, value in query if name != "starting_after"]
        query.append(("starting_after", data[-1]["id"]))
        next_path = urllib.parse.urlunsplit(("", "", parsed.path, urllib.parse.urlencode(query), ""))
    raise ReconciliationError("Stripe pagination exceeded its safety limit")


def idempotency_key(kind: str, value: str) -> str:
    digest = hashlib.sha256(value.encode("utf-8")).hexdigest()[:32]
    return f"elsa-control-staging-{kind}-{digest}"


def delete_test_webhook(stripe: StripeApi, endpoint_id: str) -> None:
    deleted = stripe.delete(f"/webhook_endpoints/{urllib.parse.quote(endpoint_id, safe='')}")
    if deleted.get("id") != endpoint_id or deleted.get("deleted") is not True:
        raise ReconciliationError("Stripe webhook cleanup did not delete the endpoint")


def _form_items(form: Mapping[str, Any]):
    for key, value in form.items():
        if isinstance(value, (list, tuple, frozenset, set)):
            for item in value:
                yield (key, str(item))
        else:
            yield (key, str(value))


@dataclass(frozen=True)
class ReconciliationConfig:
    hosted_price_id: str
    hosted_price_amount_cents: int
    hosted_price_currency: str
    hosted_price_interval: str
    hosted_price_interval_count: int
    control_webhook_url: str
    control_webhook_events: frozenset[str]
    portal_configuration_id: str | None
    webhook_secret: str
    cloud_portal_return_url: str
    azure_resource_group: str
    azure_webapp_name: str
    expected_checkout_success_url: str | None = None
    expected_checkout_cancel_url: str | None = None
    expected_portal_return_url: str | None = None

    @classmethod
    def from_environment(
        cls,
        environment: Mapping[str, str] | None = None,
        *,
        require_webhook_secret: bool = True,
    ) -> "ReconciliationConfig":
        env = os.environ if environment is None else environment

        def required(name: str) -> str:
            value = env.get(name, "").strip()
            if not value:
                raise ReconciliationError(f"{name} is required")
            return value

        price_id = required("STRIPE_HOSTED_PRICE_ID")
        if not PRICE_ID_PATTERN.fullmatch(price_id):
            raise ReconciliationError("STRIPE_HOSTED_PRICE_ID has an invalid format")
        webhook_secret = env.get("STRIPE_WEBHOOK_SECRET", "").strip()
        if require_webhook_secret:
            if not webhook_secret:
                raise ReconciliationError("STRIPE_WEBHOOK_SECRET is required")
            require_test_secret(webhook_secret, name="STRIPE_WEBHOOK_SECRET", prefix=WEBHOOK_SECRET_PREFIX)
        cloud_return = parse_https_url(required("CLOUD_PORTAL_RETURN_URL"), name="CLOUD_PORTAL_RETURN_URL")
        webhook_url = parse_https_url(required("CONTROL_STAGING_WEBHOOK_URL"), name="CONTROL_STAGING_WEBHOOK_URL")
        cloud_parts = urllib.parse.urlsplit(cloud_return)
        webhook_parts = urllib.parse.urlsplit(webhook_url)
        if cloud_parts.path != "/dashboard" or not cloud_parts.hostname.endswith(CLOUD_STAGING_HOST_SUFFIX):
            raise ReconciliationError("CLOUD_PORTAL_RETURN_URL is not the approved staging dashboard")
        if (
            webhook_parts.path != "/api/billing/webhooks/stripe"
            or not webhook_parts.hostname.endswith(CONTROL_STAGING_HOST_SUFFIX)
        ):
            raise ReconciliationError("CONTROL_STAGING_WEBHOOK_URL has an unexpected path")

        event_text = env.get("CONTROL_STAGING_WEBHOOK_EVENTS", "").strip()
        events = frozenset(item.strip() for item in event_text.split(",") if item.strip()) if event_text else DEFAULT_WEBHOOK_EVENTS
        if not events:
            raise ReconciliationError("CONTROL_STAGING_WEBHOOK_EVENTS cannot be empty")

        def integer(name: str, default: int) -> int:
            text = env.get(name, str(default)).strip()
            try:
                result = int(text)
            except ValueError as error:
                raise ReconciliationError(f"{name} must be an integer") from error
            if result < 0:
                raise ReconciliationError(f"{name} must not be negative")
            return result

        def optional_https(name: str, *, allow_query: bool = False) -> str | None:
            value = env.get(name, "").strip()
            return parse_https_url(value, name=name, allow_query=allow_query) if value else None

        return cls(
            hosted_price_id=price_id,
            hosted_price_amount_cents=integer("STRIPE_HOSTED_PRICE_AMOUNT_CENTS", 9900),
            hosted_price_currency=env.get("STRIPE_HOSTED_PRICE_CURRENCY", "eur").strip().lower(),
            hosted_price_interval=env.get("STRIPE_HOSTED_PRICE_INTERVAL", "month").strip().lower(),
            hosted_price_interval_count=integer("STRIPE_HOSTED_PRICE_INTERVAL_COUNT", 1),
            control_webhook_url=webhook_url,
            control_webhook_events=events,
            portal_configuration_id=env.get("STRIPE_PORTAL_CONFIGURATION_ID", "").strip() or None,
            webhook_secret=webhook_secret,
            cloud_portal_return_url=cloud_return,
            azure_resource_group=required("AZURE_RESOURCE_GROUP"),
            azure_webapp_name=required("AZURE_WEBAPP_NAME"),
            expected_checkout_success_url=optional_https(
                "AZURE_EXPECTED_CHECKOUT_SUCCESS_URL",
                allow_query=True,
            ),
            expected_checkout_cancel_url=optional_https("AZURE_EXPECTED_CHECKOUT_CANCEL_URL"),
            expected_portal_return_url=optional_https("AZURE_EXPECTED_PORTAL_RETURN_URL"),
        )


@dataclass(frozen=True)
class AzureAppSetting:
    value: str
    slot_setting: bool


class AzureWebApp:
    """Read and reconcile app settings without exposing secret values."""

    def __init__(self, resource_group: str, webapp_name: str, runner: Callable[..., subprocess.CompletedProcess[str]] | None = None) -> None:
        self._resource_group = resource_group
        self._webapp_name = webapp_name
        self._runner = runner or subprocess.run

    def app_setting_records(self) -> dict[str, AzureAppSetting]:
        command = [
            "az",
            "webapp",
            "config",
            "appsettings",
            "list",
            "--resource-group",
            self._resource_group,
            "--name",
            self._webapp_name,
            "--output",
            "json",
            "--only-show-errors",
        ]
        try:
            result = self._runner(command, capture_output=True, text=True, check=False, timeout=60)
        except (OSError, subprocess.SubprocessError, TimeoutError) as error:
            raise ReconciliationError("Azure CLI request failed") from error
        if result.returncode != 0:
            raise ReconciliationError("Azure CLI request failed")
        try:
            values = json.loads(result.stdout)
        except json.JSONDecodeError as error:
            raise ReconciliationError("Azure returned invalid app settings") from error
        if not isinstance(values, list):
            raise ReconciliationError("Azure returned invalid app settings")
        settings: dict[str, AzureAppSetting] = {}
        for item in values:
            if not isinstance(item, Mapping) or not isinstance(item.get("name"), str):
                raise ReconciliationError("Azure returned invalid app settings")
            if item["name"] in settings:
                raise ReconciliationError("Azure returned duplicate app settings")
            value = item.get("value")
            if value is None:
                value = ""
            if not isinstance(value, str):
                raise ReconciliationError("Azure returned invalid app settings")
            slot_setting = item.get("slotSetting", False)
            if not isinstance(slot_setting, bool):
                raise ReconciliationError("Azure returned invalid app settings")
            settings[item["name"]] = AzureAppSetting(value, slot_setting)
        return settings

    def app_settings(self) -> dict[str, str]:
        return {name: setting.value for name, setting in self.app_setting_records().items()}

    def apply_billing_settings(self, settings: Mapping[str, str]) -> None:
        """Apply one complete billing payload through a private temporary file.

        Secret values never appear in the command line, standard output, or
        standard error. Azure's ``@file`` form also avoids partially applying
        individual settings when a later setting is invalid.
        """

        if not settings or any(not name or not isinstance(value, str) for name, value in settings.items()):
            raise ReconciliationError("Azure billing settings payload is invalid")
        payload = [{"name": name, "value": value, "slotSetting": False} for name, value in settings.items()]
        path: Path | None = None
        try:
            descriptor, raw_path = tempfile.mkstemp(prefix="elsa-control-staging-billing-", suffix=".json")
            path = Path(raw_path)
            os.fchmod(descriptor, 0o600)
            with os.fdopen(descriptor, "w", encoding="utf-8") as output:
                json.dump(payload, output)
            command = [
                "az",
                "webapp",
                "config",
                "appsettings",
                "set",
                "--resource-group",
                self._resource_group,
                "--name",
                self._webapp_name,
                "--settings",
                f"@{path}",
                "--output",
                "none",
                "--only-show-errors",
            ]
            result = self._runner(command, capture_output=True, text=True, check=False, timeout=60)
            if result.returncode != 0:
                raise ReconciliationError("Azure billing settings update failed")
        except (OSError, subprocess.SubprocessError, TimeoutError) as error:
            raise ReconciliationError("Azure billing settings update failed") from error
        finally:
            if path is not None:
                path.unlink(missing_ok=True)

    def delete_billing_settings(self, names: Sequence[str]) -> None:
        if not names or any(not name for name in names):
            return
        command = [
            "az",
            "webapp",
            "config",
            "appsettings",
            "delete",
            "--resource-group",
            self._resource_group,
            "--name",
            self._webapp_name,
            "--setting-names",
            *names,
            "--output",
            "none",
            "--only-show-errors",
        ]
        try:
            result = self._runner(command, capture_output=True, text=True, check=False, timeout=60)
        except (OSError, subprocess.SubprocessError, TimeoutError) as error:
            raise ReconciliationError("Azure billing settings rollback failed") from error
        if result.returncode != 0:
            raise ReconciliationError("Azure billing settings rollback failed")


class StagingStripeReconciler:
    def __init__(self, stripe: StripeApi, azure: AzureWebApp, config: ReconciliationConfig) -> None:
        self._stripe = stripe
        self._azure = azure
        self._config = config

    def run(self, *, apply_azure_settings: bool = False, stripe_secret_key: str) -> tuple[str, ...]:
        require_test_secret(stripe_secret_key, name="STRIPE_SECRET_KEY", prefix=TEST_SECRET_PREFIX)
        passed = list(self.audit_stripe())

        # Provider resources are validated before the only Azure mutation. A
        # missing or live-mode Stripe object therefore cannot write settings.
        desired_settings = {
            "Billing__Stripe__Enabled": "true",
            "Billing__Stripe__SecretKey": stripe_secret_key,
            "Billing__Stripe__WebhookSigningSecret": self._config.webhook_secret,
            "Billing__Stripe__DefaultPriceId": self._config.hosted_price_id,
            "Billing__Stripe__CloudPortalReturnUrl": self._config.cloud_portal_return_url,
        }
        if apply_azure_settings:
            previous_records = self._azure.app_setting_records()
            previous = {name: setting.value for name, setting in previous_records.items()}
            previous_secret = previous.get("Billing__Stripe__SecretKey", "")
            if previous_secret and not previous_secret.startswith(TEST_SECRET_PREFIX):
                raise ReconciliationError("Azure staging is not configured with a test Stripe account")
            if any(
                previous_records[name].slot_setting
                for name in desired_settings
                if name in previous_records
            ):
                raise ReconciliationError("Azure staging billing settings must not be slot-sticky")
            previous_billing = {name: previous.get(name) for name in desired_settings}
            try:
                self._azure.apply_billing_settings(desired_settings)
                self.check_azure_settings(expected_stripe_secret_key=stripe_secret_key)
            except Exception:
                self._restore_azure_settings(previous_billing)
                raise
        else:
            self.check_azure_settings(expected_stripe_secret_key=stripe_secret_key)
        passed.append("azure-billing-settings")
        return tuple(passed)

    def audit_stripe(self) -> tuple[str, ...]:
        checks = (
            ("stripe-price", self.check_price),
            ("control-webhook", self.check_webhook),
            ("customer-portal", self.check_portal),
        )
        passed: list[str] = []
        for name, check in checks:
            check()
            passed.append(name)
        return tuple(passed)

    def _restore_azure_settings(self, previous: Mapping[str, str | None]) -> None:
        values = {name: value for name, value in previous.items() if value is not None}
        missing = [name for name, value in previous.items() if value is None]
        try:
            if values:
                self._azure.apply_billing_settings(values)
            if missing:
                self._azure.delete_billing_settings(missing)
            restored = self._azure.app_settings()
            if any(restored.get(name) != value for name, value in values.items()):
                raise ReconciliationError("Azure billing settings rollback failed")
            if any(name in restored for name in missing):
                raise ReconciliationError("Azure billing settings rollback failed")
        except Exception as error:
            raise ReconciliationError("Azure billing settings verification failed and rollback failed") from error

    def check_price(self) -> None:
        price = _json_object(self._stripe.get(f"/prices/{self._config.hosted_price_id}"), kind="price")
        require_test_object(price, kind="price")
        if price.get("id") != self._config.hosted_price_id:
            raise ReconciliationError("Stripe price identity did not match the requested test price")
        if price.get("active") is not True or price.get("type") != "recurring":
            raise ReconciliationError("Hosted test price is not active and recurring")
        if price.get("unit_amount") != self._config.hosted_price_amount_cents:
            raise ReconciliationError("Hosted test price amount does not match")
        if price.get("currency") != self._config.hosted_price_currency:
            raise ReconciliationError("Hosted test price currency does not match")
        recurring = price.get("recurring")
        if not isinstance(recurring, Mapping) or recurring.get("interval") != self._config.hosted_price_interval:
            raise ReconciliationError("Hosted test price interval does not match")
        if recurring.get("interval_count") != self._config.hosted_price_interval_count:
            raise ReconciliationError("Hosted test price interval count does not match")

    def check_webhook(self) -> None:
        data = stripe_list_all(self._stripe, "/webhook_endpoints?limit=100")
        matches = [
            item
            for item in data
            if isinstance(item, Mapping) and item.get("url") == self._config.control_webhook_url
        ]
        if len(matches) != 1:
            raise ReconciliationError("Control staging webhook endpoint is missing or ambiguous")
        endpoint = _json_object(matches[0], kind="webhook endpoint")
        require_test_object(endpoint, kind="webhook endpoint")
        if endpoint.get("status") != "enabled":
            raise ReconciliationError("Control staging webhook endpoint is not enabled")
        events = endpoint.get("enabled_events")
        if not isinstance(events, list) or frozenset(events) != self._config.control_webhook_events:
            raise ReconciliationError("Control staging webhook events do not match")

    def check_portal(self) -> None:
        data = stripe_list_all(self._stripe, "/billing_portal/configurations?limit=100")
        if self._config.portal_configuration_id:
            matches = [
                item
                for item in data
                if isinstance(item, Mapping) and item.get("id") == self._config.portal_configuration_id
            ]
        else:
            matches = [item for item in data if isinstance(item, Mapping) and item.get("active") is True]
        if len(matches) != 1:
            raise ReconciliationError("Active test Customer Portal configuration is missing or ambiguous")
        configuration = _json_object(matches[0], kind="portal configuration")
        require_test_object(configuration, kind="portal configuration")
        if configuration.get("active") is not True:
            raise ReconciliationError("Customer Portal configuration is not active")
        validate_portal_policy(configuration)

    def check_azure_settings(self, *, expected_stripe_secret_key: str | None = None) -> None:
        settings = self._azure.app_settings()
        expected = {
            "Billing__Stripe__Enabled": "true",
            "Billing__Stripe__DefaultPriceId": self._config.hosted_price_id,
            "Billing__Stripe__WebhookSigningSecret": self._config.webhook_secret,
            "Billing__Stripe__CloudPortalReturnUrl": self._config.cloud_portal_return_url,
        }
        for name, value in expected.items():
            if settings.get(name) != value:
                raise ReconciliationError(f"Azure billing setting {name} does not match")
        azure_secret = settings.get("Billing__Stripe__SecretKey", "")
        require_test_secret(azure_secret, name="Azure Stripe secret setting", prefix=TEST_SECRET_PREFIX)
        if expected_stripe_secret_key is not None and azure_secret != expected_stripe_secret_key:
            raise ReconciliationError("Azure Stripe secret setting does not match the audited test account")
        for name in (
            "Billing__Stripe__CheckoutSuccessUrl",
            "Billing__Stripe__CheckoutCancelUrl",
        ):
            value = settings.get(name, "")
            # Stripe Checkout success/cancel URLs may carry an allowed query
            # placeholder; credentials and fragments remain forbidden.
            parse_https_url(value, name=name, allow_query=True)
        cloud = urllib.parse.urlsplit(self._config.cloud_portal_return_url)
        cloud_origin = urllib.parse.urlunsplit((cloud.scheme, cloud.netloc, "", "", ""))
        expected_checkout_success_url = (
            self._config.expected_checkout_success_url
            or f"{cloud_origin}/checkout/return?session_id={{CHECKOUT_SESSION_ID}}"
        )
        expected_checkout_cancel_url = (
            self._config.expected_checkout_cancel_url
            or f"{cloud_origin}/dashboard/billing"
        )
        legacy_portal_return = settings.get("Billing__Stripe__PortalReturnUrl", "")
        if legacy_portal_return:
            parse_https_url(
                legacy_portal_return,
                name="Billing__Stripe__PortalReturnUrl",
                allow_query=True,
            )
        if settings["Billing__Stripe__CheckoutSuccessUrl"] != expected_checkout_success_url:
            raise ReconciliationError("Azure checkout success URL does not match")
        if settings["Billing__Stripe__CheckoutCancelUrl"] != expected_checkout_cancel_url:
            raise ReconciliationError("Azure checkout cancel URL does not match")
        if self._config.expected_portal_return_url and legacy_portal_return != self._config.expected_portal_return_url:
            raise ReconciliationError("Azure portal return URL does not match")


def bootstrap_webhook_secret(stripe: StripeApi, config: ReconciliationConfig, destination: Path) -> None:
    if str(destination) == "-" or destination.exists():
        raise ReconciliationError("Webhook secret destination must be a new file")
    if not destination.parent.is_dir():
        raise ReconciliationError("Webhook secret destination directory does not exist")
    flags = os.O_WRONLY | os.O_CREAT | os.O_EXCL
    cleanup_endpoint_id: str | None = None
    owns_destination = False
    reconcile_key = idempotency_key("webhook", config.control_webhook_url)
    request = {
        "url": config.control_webhook_url,
        "enabled_events[]": sorted(config.control_webhook_events),
        "metadata[elsa_reconcile_key]": reconcile_key,
    }
    try:
        descriptor = os.open(destination, flags, 0o600)
        owns_destination = True
        os.close(descriptor)
        data = stripe_list_all(stripe, "/webhook_endpoints?limit=100")
        matches = [item for item in data if item.get("url") == config.control_webhook_url]
        if len(matches) > 1:
            raise ReconciliationError("Control staging webhook endpoint is ambiguous")
        existing_endpoint_id: str | None = None
        if matches:
            metadata = matches[0].get("metadata")
            if not isinstance(metadata, Mapping) or metadata.get("elsa_reconcile_key") != reconcile_key:
                raise ReconciliationError("Control staging webhook endpoint already exists; secret cannot be reissued")
            raw_existing_id = matches[0].get("id")
            if not isinstance(raw_existing_id, str) or not raw_existing_id:
                raise ReconciliationError("Existing Control staging webhook endpoint is invalid")
            existing_endpoint_id = raw_existing_id
        endpoint = stripe.post("/webhook_endpoints", request, idempotency_key=reconcile_key)
        raw_endpoint_id = endpoint.get("id")
        endpoint_id = raw_endpoint_id if isinstance(raw_endpoint_id, str) and raw_endpoint_id else None
        if not endpoint_id:
            raise ReconciliationError("Stripe created an invalid webhook endpoint")
        if existing_endpoint_id and endpoint_id != existing_endpoint_id:
            cleanup_endpoint_id = endpoint_id
            raise ReconciliationError("Stripe webhook idempotency window expired; secret cannot be recovered safely")
        if not existing_endpoint_id:
            cleanup_endpoint_id = endpoint_id
        require_test_object(endpoint, kind="webhook endpoint")
        if endpoint.get("url") != config.control_webhook_url or endpoint.get("status") != "enabled":
            raise ReconciliationError("Stripe created an unexpected webhook endpoint")
        events = endpoint.get("enabled_events")
        secret = endpoint.get("secret")
        if not isinstance(events, list) or frozenset(events) != config.control_webhook_events:
            raise ReconciliationError("Stripe created an unexpected webhook event set")
        if not isinstance(secret, str) or not secret.startswith(WEBHOOK_SECRET_PREFIX):
            raise ReconciliationError("Stripe did not return a test webhook secret")
        destination.write_text(f"{secret}\n", encoding="utf-8")
        os.chmod(destination, stat.S_IRUSR | stat.S_IWUSR)
    except (OSError, ValueError) as error:
        if owns_destination:
            destination.unlink(missing_ok=True)
        if cleanup_endpoint_id:
            try:
                delete_test_webhook(stripe, cleanup_endpoint_id)
            except Exception as cleanup_error:
                raise ReconciliationError("Webhook bootstrap write and cleanup failed") from cleanup_error
        raise ReconciliationError("Could not write the webhook secret file") from error
    except Exception:
        if owns_destination:
            destination.unlink(missing_ok=True)
        if cleanup_endpoint_id:
            try:
                delete_test_webhook(stripe, cleanup_endpoint_id)
            except Exception as cleanup_error:
                raise ReconciliationError("Webhook bootstrap validation and cleanup failed") from cleanup_error
        raise
    if stat.S_IMODE(destination.stat().st_mode) != 0o600:
        raise ReconciliationError("Webhook secret file permissions are unsafe")


def validate_portal_policy(configuration: Mapping[str, Any]) -> None:
    features = configuration.get("features")
    if not isinstance(features, Mapping):
        raise ReconciliationError("Customer Portal features are invalid")
    cancellation = features.get("subscription_cancel")
    if (
        not isinstance(cancellation, Mapping)
        or cancellation.get("enabled") is not True
        or cancellation.get("mode") != "at_period_end"
    ):
        raise ReconciliationError("Customer Portal cancellation is not configured for period end")
    for feature_name in ("invoice_history", "payment_method_update"):
        feature = features.get(feature_name)
        if not isinstance(feature, Mapping) or feature.get("enabled") is not True:
            raise ReconciliationError("Customer Portal management features are not enabled")


def bootstrap_portal(stripe: StripeApi, config: ReconciliationConfig) -> None:
    data = stripe_list_all(stripe, "/billing_portal/configurations?limit=100")
    if config.portal_configuration_id:
        matches = [item for item in data if item.get("id") == config.portal_configuration_id]
        if len(matches) != 1:
            raise ReconciliationError("Pinned test Customer Portal configuration is missing or ambiguous")
        require_test_object(matches[0], kind="portal configuration")
        if matches[0].get("active") is not True:
            raise ReconciliationError("Pinned test Customer Portal configuration is not active")
        validate_portal_policy(matches[0])
        return
    active = [item for item in data if isinstance(item, Mapping) and item.get("active") is True]
    if len(active) > 1:
        raise ReconciliationError("Active test Customer Portal configuration is ambiguous")
    if active:
        require_test_object(active[0], kind="portal configuration")
        validate_portal_policy(active[0])
        return

    origin = urllib.parse.urlunsplit((*urllib.parse.urlsplit(config.cloud_portal_return_url)[:2], "", "", ""))
    created = stripe.post(
        "/billing_portal/configurations",
        {
            "business_profile[headline]": "Manage your Elsa Cloud Hosted subscription",
            "business_profile[privacy_policy_url]": f"{origin}/privacy",
            "business_profile[terms_of_service_url]": f"{origin}/terms",
            "default_return_url": config.cloud_portal_return_url,
            "features[invoice_history][enabled]": "true",
            "features[payment_method_update][enabled]": "true",
            "features[subscription_cancel][enabled]": "true",
            "features[subscription_cancel][mode]": "at_period_end",
        },
        idempotency_key=idempotency_key("portal", config.cloud_portal_return_url),
    )
    created_id = created.get("id") if isinstance(created.get("id"), str) else None
    try:
        require_test_object(created, kind="portal configuration")
        if not created_id or created.get("active") is not True:
            raise ReconciliationError("Stripe created an unexpected Customer Portal configuration")
        validate_portal_policy(created)
    except ReconciliationError:
        if created_id:
            try:
                deactivated = stripe.post(
                    f"/billing_portal/configurations/{urllib.parse.quote(created_id, safe='')}",
                    {"active": "false"},
                    idempotency_key=idempotency_key("portal-cleanup", created_id),
                )
                require_test_object(deactivated, kind="portal configuration")
                if deactivated.get("id") != created_id or deactivated.get("active") is not False:
                    raise ReconciliationError("Customer Portal cleanup did not deactivate the configuration")
            except Exception as error:
                raise ReconciliationError(
                    "Customer Portal bootstrap validation and cleanup failed"
                ) from error
        raise


def _parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--bootstrap-webhook-secret",
        type=Path,
        metavar="PATH",
        help="create the missing test endpoint and write its one-time secret to PATH with mode 0600",
    )
    parser.add_argument(
        "--apply-azure-settings",
        action="store_true",
        help="apply the audited test billing values to the staging Web App before verifying them",
    )
    parser.add_argument(
        "--bootstrap-portal",
        action="store_true",
        help="create the period-end test portal policy only when none is active",
    )
    parser.add_argument(
        "--audit-stripe-only",
        action="store_true",
        help="verify the test price, webhook, and portal policy without reading or changing Azure",
    )
    return parser


def main(argv: Sequence[str] | None = None) -> int:
    parser = _parser()
    args = parser.parse_args(argv)
    if args.audit_stripe_only and (
        args.apply_azure_settings or args.bootstrap_webhook_secret or args.bootstrap_portal
    ):
        parser.error("--audit-stripe-only cannot be combined with a mutating option")
    try:
        config = ReconciliationConfig.from_environment(
            require_webhook_secret=not bool(args.bootstrap_webhook_secret)
        )
        stripe_secret_key = os.environ["STRIPE_SECRET_KEY"]
        stripe = StripeApi(stripe_secret_key)
        if args.audit_stripe_only:
            checks = StagingStripeReconciler(
                stripe,
                AzureWebApp(config.azure_resource_group, config.azure_webapp_name),
                config,
            ).audit_stripe()
            for check in checks:
                print(f"PASS {check}")
            return 0
        if args.bootstrap_webhook_secret:
            bootstrap_webhook_secret(stripe, config, args.bootstrap_webhook_secret)
            print("PASS webhook-secret-bootstrap")
        if args.bootstrap_portal:
            bootstrap_portal(stripe, config)
            print("PASS customer-portal-bootstrap")
        if args.bootstrap_webhook_secret or args.bootstrap_portal:
            return 0
        checks = StagingStripeReconciler(
            stripe,
            AzureWebApp(config.azure_resource_group, config.azure_webapp_name),
            config,
        ).run(
            apply_azure_settings=args.apply_azure_settings,
            stripe_secret_key=stripe_secret_key,
        )
        for check in checks:
            print(f"PASS {check}")
        return 0
    except (KeyError, ReconciliationError):
        # Keep CI output useful without leaking a provider response or secret.
        print("FAIL staging-stripe-reconciliation", file=sys.stderr)
        return 1
    except Exception:
        # A malformed provider response must not produce a traceback containing
        # request details or values copied from a secret-bearing setting.
        print("FAIL staging-stripe-reconciliation", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
