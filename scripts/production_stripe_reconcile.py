#!/usr/bin/env python3
"""Preserve and audit the production Stripe settings without exposing secrets.

The deployment workflow captures the complete existing ``Billing__Stripe__*``
payload before a production app or infrastructure replacement and reapplies the
same payload before restarting the API. The audit is read-only. Success prints
the same named ``PASS`` checks as before. Each ``--audit`` failure prints a
secret-free ``FAIL <check>`` reason that names the missing env/GitHub variable,
the mismatched Azure app setting name, the Stripe API error type or code with
HTTP status and resource group, the live webhook endpoint count, or the missing
and extra event names. Provider response bodies, setting values, secrets, URL
query strings, and customer ids never reach stdout/stderr.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import sys
import urllib.error
import urllib.parse
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Iterable, Mapping, Sequence

if __package__:
    from .staging_stripe_reconcile import (
        AzureAppSetting,
        AzureWebApp,
        DEFAULT_WEBHOOK_EVENTS,
        ReconciliationError,
        StripeApi,
        parse_https_url,
        require_live_secret,
        stripe_list_all,
    )
else:
    from staging_stripe_reconcile import (
        AzureAppSetting,
        AzureWebApp,
        DEFAULT_WEBHOOK_EVENTS,
        ReconciliationError,
        StripeApi,
        parse_https_url,
        require_live_secret,
        stripe_list_all,
    )


PRODUCTION_PREFIX = "Billing__Stripe__"
PRODUCTION_PRESERVED_SETTINGS = frozenset(
    {
        "Billing__Stripe__Enabled",
        "Billing__Stripe__SecretKey",
        "Billing__Stripe__WebhookSigningSecret",
        "Billing__Stripe__DefaultPriceId",
        "Billing__Stripe__CheckoutSuccessUrl",
        "Billing__Stripe__CheckoutCancelUrl",
        "Billing__Stripe__PortalReturnUrl",
        "Billing__Stripe__CloudPortalReturnUrl",
    }
)
PRODUCTION_AUDIT_SETTINGS = frozenset(
    {
        *PRODUCTION_PRESERVED_SETTINGS,
        "Billing__Stripe__ExpectedMode",
    }
)
PRICE_ID_PATTERN = re.compile(r"^price_[A-Za-z0-9]+$")
WEBHOOK_SECRET_PATTERN = re.compile(r"^whsec_[A-Za-z0-9]+$")
AUDIT_FAIL_CHECK = "production-stripe-reconciliation"
WEBHOOK_LIST_PATH = "/webhook_endpoints?limit=100"
_SAFE_TOKEN = re.compile(r"^[a-z][a-z0-9_]{0,63}$")
_SAFE_EVENT = re.compile(r"^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)+$")
_RESOURCE_SEGMENTS = frozenset(
    {
        "billing_portal",
        "checkout",
        "configurations",
        "customers",
        "invoices",
        "payment_intents",
        "prices",
        "products",
        "sessions",
        "subscriptions",
        "webhook_endpoints",
    }
)


def redact_secrets(text: str) -> str:
    """Keep ``sk_live``/``rk_live`` type prefixes; drop values, queries, and customer ids."""

    redacted = re.sub(r"\?[^\s]+", "", text)
    redacted = re.sub(r"\b((?:sk|rk)_(?:live|test))_[A-Za-z0-9]+", r"\1", redacted)
    redacted = re.sub(r"\bwhsec_[A-Za-z0-9]+", "whsec", redacted)
    redacted = re.sub(r"\bcus_[A-Za-z0-9]+", "cus", redacted)
    return redacted


def stripe_request_label(path: str) -> str:
    parsed = urllib.parse.urlsplit(path)
    parts = [urllib.parse.unquote(part) for part in parsed.path.split("/") if part]
    resource_parts: list[str] = []
    has_object = False
    for part in parts:
        if part in _RESOURCE_SEGMENTS:
            resource_parts.append(part)
        else:
            has_object = True
    resource = ".".join(resource_parts) or "request"
    return f"{resource}.{'retrieve' if has_object else 'list'}"


def format_stripe_api_failure(path: str, error: BaseException) -> str:
    return f"stripe-api {stripe_request_label(path)}: {_stripe_error_label(error)}"


def _stripe_error_label(error: BaseException) -> str:
    status: int | None = None
    error_type: str | None = None
    error_code: str | None = None
    current: BaseException | None = error
    seen: set[int] = set()
    while current is not None and id(current) not in seen:
        seen.add(id(current))
        if isinstance(current, urllib.error.HTTPError):
            try:
                status = int(current.code)
            except (TypeError, ValueError):
                status = None
            payload = _stripe_error_payload(current)
            error_type = _safe_token(payload.get("type"))
            error_code = _safe_token(payload.get("code"))
            break
        current = current.__cause__ or current.__context__
    label = error_type or error_code or "request_failed"
    if status is not None and 100 <= status <= 599:
        return f"{label} ({status})"
    return label


def _stripe_error_payload(error: urllib.error.HTTPError) -> Mapping[str, Any]:
    try:
        raw = error.read()
    except Exception:
        return {}
    if not raw:
        return {}
    try:
        parsed = json.loads(raw.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError, AttributeError):
        return {}
    if not isinstance(parsed, Mapping):
        return {}
    payload = parsed.get("error", parsed)
    return payload if isinstance(payload, Mapping) else {}


def _safe_token(value: Any) -> str | None:
    return value if isinstance(value, str) and _SAFE_TOKEN.fullmatch(value) else None


def _safe_event_names(names: Iterable[str]) -> list[str]:
    return [name if _SAFE_EVENT.fullmatch(name) else "invalid_event" for name in names]


def _env_reason(message: str) -> str:
    return message if message.startswith("env: ") else f"env: {message}"


def _print_failure(reason: str | None = None) -> None:
    text = redact_secrets(reason).strip() if reason else ""
    print(f"FAIL {text or AUDIT_FAIL_CHECK}", file=sys.stderr)


@dataclass(frozen=True)
class ProductionAuditConfig:
    price_id: str
    webhook_url: str
    cloud_portal_return_url: str
    checkout_success_url: str
    checkout_cancel_url: str
    portal_return_url: str
    resource_group: str
    webapp_name: str
    webhook_events: frozenset[str] = DEFAULT_WEBHOOK_EVENTS

    @classmethod
    def from_environment(cls, environment: Mapping[str, str] | None = None) -> "ProductionAuditConfig":
        env = os.environ if environment is None else environment

        def required(name: str) -> str:
            value = env.get(name, "").strip()
            if not value:
                raise ReconciliationError(f"env: {name} is missing")
            return value

        def required_https(name: str, *, allow_query: bool = False) -> str:
            try:
                return parse_https_url(required(name), name=name, allow_query=allow_query)
            except ReconciliationError as error:
                raise ReconciliationError(_env_reason(str(error))) from error

        price_id = required("STRIPE_PRODUCTION_PRICE_ID")
        if not PRICE_ID_PATTERN.fullmatch(price_id):
            raise ReconciliationError("env: STRIPE_PRODUCTION_PRICE_ID has an invalid format")

        webhook_url = required_https("CONTROL_PRODUCTION_WEBHOOK_URL")
        portal_return_url = required_https("AZURE_EXPECTED_PRODUCTION_PORTAL_RETURN_URL")
        checkout_success_url = required_https(
            "AZURE_EXPECTED_PRODUCTION_CHECKOUT_SUCCESS_URL",
            allow_query=True,
        )
        checkout_cancel_url = required_https("AZURE_EXPECTED_PRODUCTION_CHECKOUT_CANCEL_URL")
        cloud_portal_return_url = required_https("AZURE_EXPECTED_PRODUCTION_CLOUD_PORTAL_RETURN_URL")
        event_text = env.get("STRIPE_PRODUCTION_WEBHOOK_EVENTS", "").strip()
        events = frozenset(item.strip() for item in event_text.split(",") if item.strip()) if event_text else DEFAULT_WEBHOOK_EVENTS
        if events != DEFAULT_WEBHOOK_EVENTS:
            raise ReconciliationError("env: STRIPE_PRODUCTION_WEBHOOK_EVENTS must use the approved production event set")
        return cls(
            price_id=price_id,
            webhook_url=webhook_url,
            cloud_portal_return_url=cloud_portal_return_url,
            checkout_success_url=checkout_success_url,
            checkout_cancel_url=checkout_cancel_url,
            portal_return_url=portal_return_url,
            resource_group=required("AZURE_RESOURCE_GROUP"),
            webapp_name=required("AZURE_WEBAPP_NAME"),
            webhook_events=events,
        )


def _setting_payload(settings: Mapping[str, AzureAppSetting]) -> list[dict[str, object]]:
    return [
        {"name": name, "value": setting.value, "slotSetting": setting.slot_setting}
        for name, setting in sorted(settings.items())
    ]


def validate_preserved_production_mode(settings: Mapping[str, AzureAppSetting]) -> None:
    """Reject an enabled capture that cannot safely start as production."""

    if any(
        not isinstance(setting, AzureAppSetting)
        or not isinstance(setting.value, str)
        or not isinstance(setting.slot_setting, bool)
        for setting in settings.values()
    ):
        raise ReconciliationError("Production Stripe settings are invalid")

    if settings["Billing__Stripe__Enabled"].value.strip().lower() != "true":
        return
    require_live_secret(
        settings["Billing__Stripe__SecretKey"].value,
        name="Production Stripe secret setting",
    )
    expected_mode = settings.get("Billing__Stripe__ExpectedMode")
    if expected_mode is not None and expected_mode.value.strip().lower() not in {"live", "production"}:
        raise ReconciliationError("Production Stripe capture has an unexpected expected mode")


def write_capture(settings: Mapping[str, AzureAppSetting], destination: Path) -> None:
    if str(destination) == "-" or destination.exists():
        raise ReconciliationError("Production Stripe capture destination must be a new file")
    if not destination.parent.is_dir():
        raise ReconciliationError("Production Stripe capture destination directory does not exist")
    if not PRODUCTION_PRESERVED_SETTINGS.issubset(settings):
        raise ReconciliationError("Production Stripe settings are incomplete; refusing an unprotected deployment")
    validate_preserved_production_mode(settings)
    descriptor = os.open(destination, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    try:
        with os.fdopen(descriptor, "w", encoding="utf-8") as output:
            json.dump(_setting_payload(settings), output)
            output.write("\n")
    except Exception:
        destination.unlink(missing_ok=True)
        raise


def read_capture(source: Path) -> dict[str, AzureAppSetting]:
    try:
        if source.stat().st_mode & 0o077:
            raise ReconciliationError("Production Stripe capture permissions are too broad")
    except OSError as error:
        raise ReconciliationError("Production Stripe capture is unreadable") from error
    try:
        values = json.loads(source.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        raise ReconciliationError("Production Stripe capture is unreadable") from error
    if not isinstance(values, list):
        raise ReconciliationError("Production Stripe capture is invalid")
    settings: dict[str, AzureAppSetting] = {}
    for item in values:
        if not isinstance(item, Mapping) or not isinstance(item.get("name"), str):
            raise ReconciliationError("Production Stripe capture is invalid")
        name = item["name"]
        if not name.startswith(PRODUCTION_PREFIX) or name in settings:
            raise ReconciliationError("Production Stripe capture is invalid")
        value = item.get("value")
        slot_setting = item.get("slotSetting", False)
        if not isinstance(value, str) or not isinstance(slot_setting, bool):
            raise ReconciliationError("Production Stripe capture is invalid")
        settings[name] = AzureAppSetting(value, slot_setting)
    if not PRODUCTION_PRESERVED_SETTINGS.issubset(settings):
        raise ReconciliationError("Production Stripe capture is incomplete")
    validate_preserved_production_mode(settings)
    return settings


class ProductionStripeReconciler:
    def __init__(
        self,
        stripe: Any,
        azure: Any,
        config: ProductionAuditConfig,
        expected_secret_key: str | None = None,
    ) -> None:
        self._stripe = stripe
        self._azure = azure
        self._config = config
        self._expected_secret_key = expected_secret_key

    def capture(self) -> dict[str, AzureAppSetting]:
        records = self._azure.app_setting_records()
        settings = {name: setting for name, setting in records.items() if name.startswith(PRODUCTION_PREFIX)}
        if not PRODUCTION_PRESERVED_SETTINGS.issubset(settings):
            raise ReconciliationError("Production Stripe settings are incomplete; refusing an unprotected deployment")
        validate_preserved_production_mode(settings)
        return settings

    def reapply(self, settings: Mapping[str, AzureAppSetting]) -> tuple[str, ...]:
        names = set(settings)
        if not PRODUCTION_PRESERVED_SETTINGS.issubset(names):
            raise ReconciliationError("Production Stripe settings are incomplete; refusing a partial replacement")
        validate_preserved_production_mode(settings)
        self._azure.apply_billing_setting_records(settings)
        current = self._azure.app_setting_records()
        if any(
            name not in current
            or current[name].value != setting.value
            or current[name].slot_setting != setting.slot_setting
            for name, setting in settings.items()
        ):
            raise ReconciliationError("Production Stripe settings did not round-trip")
        return ("production-billing-settings",)

    def audit(self) -> tuple[str, ...]:
        settings = self._azure.app_settings()
        self._check_settings(settings)
        try:
            endpoints = stripe_list_all(self._stripe, WEBHOOK_LIST_PATH)
        except (ReconciliationError, urllib.error.HTTPError, urllib.error.URLError) as error:
            raise ReconciliationError(format_stripe_api_failure(WEBHOOK_LIST_PATH, error)) from error
        matches = [
            endpoint
            for endpoint in endpoints
            if endpoint.get("url") == self._config.webhook_url and endpoint.get("livemode") is True
        ]
        if len(matches) != 1:
            raise ReconciliationError(f"webhook-endpoint: expected 1 live endpoint, found {len(matches)}")
        endpoint = matches[0]
        if endpoint.get("status") != "enabled":
            raise ReconciliationError("webhook-endpoint: live endpoint is not enabled")
        events = endpoint.get("enabled_events")
        if not isinstance(events, list) or any(not isinstance(event, str) for event in events):
            raise ReconciliationError("webhook-events: enabled_events is invalid")
        actual = frozenset(events)
        if len(events) != len(actual) or actual != self._config.webhook_events:
            missing = _safe_event_names(sorted(self._config.webhook_events - actual))
            extra = _safe_event_names(sorted(actual - self._config.webhook_events))
            parts: list[str] = []
            if missing:
                parts.append("missing " + ", ".join(missing))
            if extra:
                parts.append("extra " + ", ".join(extra))
            if len(events) != len(actual) and not parts:
                parts.append("duplicate event names")
            raise ReconciliationError("webhook-events: " + "; ".join(parts))
        return ("production-billing-settings", "production-stripe-webhook")

    def _check_settings(self, settings: Mapping[str, str]) -> None:
        missing = sorted(PRODUCTION_AUDIT_SETTINGS.difference(settings))
        if missing:
            raise ReconciliationError(f"azure-setting: missing {', '.join(missing)}")
        if settings["Billing__Stripe__Enabled"].lower() != "true":
            raise ReconciliationError("azure-setting: Billing__Stripe__Enabled is not true")
        if settings["Billing__Stripe__ExpectedMode"].strip().lower() not in {"live", "production"}:
            raise ReconciliationError("azure-setting: Billing__Stripe__ExpectedMode is not live")
        try:
            require_live_secret(settings["Billing__Stripe__SecretKey"], name="Production Stripe secret setting")
        except ReconciliationError:
            raise ReconciliationError("azure-setting: Billing__Stripe__SecretKey is not a live-mode credential") from None
        if (
            self._expected_secret_key is not None
            and settings["Billing__Stripe__SecretKey"] != self._expected_secret_key
        ):
            raise ReconciliationError("azure-setting: Billing__Stripe__SecretKey does not match the audit key")
        if not WEBHOOK_SECRET_PATTERN.fullmatch(settings["Billing__Stripe__WebhookSigningSecret"]):
            raise ReconciliationError("azure-setting: Billing__Stripe__WebhookSigningSecret has an unexpected shape")
        if settings["Billing__Stripe__DefaultPriceId"] != self._config.price_id:
            raise ReconciliationError("azure-setting: Billing__Stripe__DefaultPriceId does not match")
        expected = {
            "Billing__Stripe__CheckoutSuccessUrl": self._config.checkout_success_url,
            "Billing__Stripe__CheckoutCancelUrl": self._config.checkout_cancel_url,
            "Billing__Stripe__PortalReturnUrl": self._config.portal_return_url,
            "Billing__Stripe__CloudPortalReturnUrl": self._config.cloud_portal_return_url,
        }
        for name, value in expected.items():
            if settings[name] != value:
                raise ReconciliationError(f"azure-setting: {name} does not match")


def _required_azure() -> AzureWebApp:
    missing = [
        name
        for name in ("AZURE_RESOURCE_GROUP", "AZURE_WEBAPP_NAME")
        if not os.environ.get(name, "").strip()
    ]
    if missing:
        verb = "is" if len(missing) == 1 else "are"
        raise ReconciliationError(f"env: {', '.join(missing)} {verb} missing")
    return AzureWebApp(os.environ["AZURE_RESOURCE_GROUP"].strip(), os.environ["AZURE_WEBAPP_NAME"].strip())


def _parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    operation = parser.add_mutually_exclusive_group(required=True)
    operation.add_argument("--capture", type=Path, metavar="PATH")
    operation.add_argument("--reapply", type=Path, metavar="PATH")
    operation.add_argument("--audit", action="store_true")
    parser.add_argument(
        "--audit-capture",
        type=Path,
        metavar="PATH",
        help="use the private captured production key for --audit",
    )
    return parser


def main(argv: Sequence[str] | None = None) -> int:
    parser = _parser()
    args = parser.parse_args(argv)
    if args.audit_capture and not args.audit:
        parser.error("--audit-capture requires --audit")
    try:
        azure = _required_azure()
        if args.capture:
            settings = ProductionStripeReconciler(None, azure, object()).capture()
            write_capture(settings, args.capture)
            print("PASS production-billing-capture")
            return 0
        if args.reapply:
            settings = read_capture(args.reapply)
            ProductionStripeReconciler(None, azure, object()).reapply(settings)
            print("PASS production-billing-reapply")
            return 0
        config = ProductionAuditConfig.from_environment()
        captured_secret_key = None
        if args.audit_capture:
            captured = read_capture(args.audit_capture)
            captured_secret_key = captured["Billing__Stripe__SecretKey"].value
        stripe_key = captured_secret_key or os.environ.get("STRIPE_PRODUCTION_SECRET_KEY", "")
        if not captured_secret_key and not os.environ.get("STRIPE_PRODUCTION_SECRET_KEY", "").strip():
            raise ReconciliationError("env: STRIPE_PRODUCTION_SECRET_KEY is missing")
        try:
            require_live_secret(
                stripe_key,
                name="Production Stripe captured setting" if captured_secret_key else "STRIPE_PRODUCTION_SECRET_KEY",
            )
        except ReconciliationError:
            if captured_secret_key:
                raise ReconciliationError("azure-setting: Billing__Stripe__SecretKey is not a live-mode credential") from None
            raise ReconciliationError("env: STRIPE_PRODUCTION_SECRET_KEY is not a live-mode credential") from None
        checks = ProductionStripeReconciler(
            StripeApi(stripe_key, validator=lambda value: require_live_secret(value, name="STRIPE_PRODUCTION_SECRET_KEY")),
            azure,
            config,
            expected_secret_key=captured_secret_key,
        ).audit()
        for check in checks:
            print(f"PASS {check}")
        return 0
    except ReconciliationError as error:
        _print_failure(str(error) if args.audit else None)
        return 1
    except (KeyError, OSError, ValueError):
        _print_failure()
        return 1
    except Exception:
        _print_failure()
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
