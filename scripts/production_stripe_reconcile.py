#!/usr/bin/env python3
"""Preserve and audit the production Stripe settings without exposing secrets.

The deployment workflow captures the complete existing ``Billing__Stripe__*``
payload before a production app or infrastructure replacement and reapplies the
same payload before restarting the API. The audit is read-only and prints only
named checks. Provider responses and setting values never reach stdout/stderr.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Mapping, Sequence

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
                raise ReconciliationError(f"{name} is required")
            return value

        price_id = required("STRIPE_PRODUCTION_PRICE_ID")
        if not PRICE_ID_PATTERN.fullmatch(price_id):
            raise ReconciliationError("STRIPE_PRODUCTION_PRICE_ID has an invalid format")

        webhook_url = parse_https_url(
            required("CONTROL_PRODUCTION_WEBHOOK_URL"),
            name="CONTROL_PRODUCTION_WEBHOOK_URL",
        )
        portal_return_url = parse_https_url(
            required("AZURE_EXPECTED_PRODUCTION_PORTAL_RETURN_URL"),
            name="AZURE_EXPECTED_PRODUCTION_PORTAL_RETURN_URL",
        )
        checkout_success_url = parse_https_url(
            required("AZURE_EXPECTED_PRODUCTION_CHECKOUT_SUCCESS_URL"),
            name="AZURE_EXPECTED_PRODUCTION_CHECKOUT_SUCCESS_URL",
            allow_query=True,
        )
        checkout_cancel_url = parse_https_url(
            required("AZURE_EXPECTED_PRODUCTION_CHECKOUT_CANCEL_URL"),
            name="AZURE_EXPECTED_PRODUCTION_CHECKOUT_CANCEL_URL",
        )
        cloud_portal_return_url = parse_https_url(
            required("AZURE_EXPECTED_PRODUCTION_CLOUD_PORTAL_RETURN_URL"),
            name="AZURE_EXPECTED_PRODUCTION_CLOUD_PORTAL_RETURN_URL",
        )
        event_text = env.get("STRIPE_PRODUCTION_WEBHOOK_EVENTS", "").strip()
        events = frozenset(item.strip() for item in event_text.split(",") if item.strip()) if event_text else DEFAULT_WEBHOOK_EVENTS
        if events != DEFAULT_WEBHOOK_EVENTS:
            raise ReconciliationError("STRIPE_PRODUCTION_WEBHOOK_EVENTS must use the approved production event set")
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
        endpoints = stripe_list_all(self._stripe, "/webhook_endpoints?limit=100")
        matches = [
            endpoint
            for endpoint in endpoints
            if endpoint.get("url") == self._config.webhook_url and endpoint.get("livemode") is True
        ]
        if len(matches) != 1:
            raise ReconciliationError("Production Stripe live webhook endpoint is missing or ambiguous")
        endpoint = matches[0]
        if endpoint.get("status") != "enabled":
            raise ReconciliationError("Production Stripe webhook endpoint is not enabled in live mode")
        events = endpoint.get("enabled_events")
        if (
            not isinstance(events, list)
            or any(not isinstance(event, str) for event in events)
            or len(events) != len(self._config.webhook_events)
            or frozenset(events) != self._config.webhook_events
        ):
            raise ReconciliationError("Production Stripe webhook events do not match")
        return ("production-billing-settings", "production-stripe-webhook")

    def _check_settings(self, settings: Mapping[str, str]) -> None:
        missing = PRODUCTION_AUDIT_SETTINGS.difference(settings)
        if missing:
            raise ReconciliationError("Production Stripe settings are incomplete")
        if settings["Billing__Stripe__Enabled"].lower() != "true":
            raise ReconciliationError("Production Stripe billing is not enabled")
        if settings["Billing__Stripe__ExpectedMode"].strip().lower() not in {"live", "production"}:
            raise ReconciliationError("Production Stripe expected mode is not live")
        require_live_secret(settings["Billing__Stripe__SecretKey"], name="Production Stripe secret setting")
        if (
            self._expected_secret_key is not None
            and settings["Billing__Stripe__SecretKey"] != self._expected_secret_key
        ):
            raise ReconciliationError("Production Stripe audit key does not match the deployed billing setting")
        if not WEBHOOK_SECRET_PATTERN.fullmatch(settings["Billing__Stripe__WebhookSigningSecret"]):
            raise ReconciliationError("Production Stripe webhook signing secret has an unexpected shape")
        if settings["Billing__Stripe__DefaultPriceId"] != self._config.price_id:
            raise ReconciliationError("Production Stripe price does not match")
        expected = {
            "Billing__Stripe__CheckoutSuccessUrl": self._config.checkout_success_url,
            "Billing__Stripe__CheckoutCancelUrl": self._config.checkout_cancel_url,
            "Billing__Stripe__PortalReturnUrl": self._config.portal_return_url,
            "Billing__Stripe__CloudPortalReturnUrl": self._config.cloud_portal_return_url,
        }
        for name, value in expected.items():
            if settings[name] != value:
                raise ReconciliationError(f"Production Stripe setting {name} does not match")


def _required_azure() -> AzureWebApp:
    resource_group = os.environ.get("AZURE_RESOURCE_GROUP", "").strip()
    webapp_name = os.environ.get("AZURE_WEBAPP_NAME", "").strip()
    if not resource_group or not webapp_name:
        raise ReconciliationError("AZURE_RESOURCE_GROUP and AZURE_WEBAPP_NAME are required")
    return AzureWebApp(resource_group, webapp_name)


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
        require_live_secret(
            stripe_key,
            name="Production Stripe captured setting" if captured_secret_key else "STRIPE_PRODUCTION_SECRET_KEY",
        )
        checks = ProductionStripeReconciler(
            StripeApi(stripe_key, validator=lambda value: require_live_secret(value, name="STRIPE_PRODUCTION_SECRET_KEY")),
            azure,
            config,
            expected_secret_key=captured_secret_key,
        ).audit()
        for check in checks:
            print(f"PASS {check}")
        return 0
    except (KeyError, ReconciliationError, OSError, ValueError):
        print("FAIL production-stripe-reconciliation", file=sys.stderr)
        return 1
    except Exception:
        print("FAIL production-stripe-reconciliation", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
