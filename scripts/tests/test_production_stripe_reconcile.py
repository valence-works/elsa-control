from __future__ import annotations

import copy
import os
import stat
import tempfile
import unittest
from pathlib import Path

from scripts.production_stripe_reconcile import (
    ProductionAuditConfig,
    ProductionStripeReconciler,
    ReconciliationError,
    read_capture,
    write_capture,
)
from scripts.staging_stripe_reconcile import AzureAppSetting, DEFAULT_WEBHOOK_EVENTS


WEBHOOK_URL = "https://control.example.com/api/billing/webhooks/stripe"
CLOUD_ORIGIN = "https://cloud.example.com"
PORTAL_RETURN_URL = "https://control.example.com/billing"
EXPECTED_SETTINGS = {
    "Billing__Stripe__Enabled": "true",
    "Billing__Stripe__SecretKey": "rk_live_private",
    "Billing__Stripe__WebhookSigningSecret": "whsec_private",
    "Billing__Stripe__DefaultPriceId": "price_live",
    "Billing__Stripe__CheckoutSuccessUrl": f"{CLOUD_ORIGIN}/checkout/return?session_id={{CHECKOUT_SESSION_ID}}",
    "Billing__Stripe__CheckoutCancelUrl": f"{CLOUD_ORIGIN}/dashboard/billing",
    "Billing__Stripe__PortalReturnUrl": PORTAL_RETURN_URL,
    "Billing__Stripe__CloudPortalReturnUrl": f"{CLOUD_ORIGIN}/dashboard",
    "Billing__Stripe__ExpectedMode": "live",
    "Billing__Stripe__UnknownFutureSetting": "preserve-me",
}


def config() -> ProductionAuditConfig:
    return ProductionAuditConfig(
        price_id="price_live",
        webhook_url=WEBHOOK_URL,
        cloud_portal_return_url=f"{CLOUD_ORIGIN}/dashboard",
        checkout_success_url=EXPECTED_SETTINGS["Billing__Stripe__CheckoutSuccessUrl"],
        checkout_cancel_url=EXPECTED_SETTINGS["Billing__Stripe__CheckoutCancelUrl"],
        portal_return_url=PORTAL_RETURN_URL,
        resource_group="production-rg",
        webapp_name="production-api",
    )


def records() -> dict[str, AzureAppSetting]:
    return {
        name: AzureAppSetting(value, name.endswith("ExpectedMode"))
        for name, value in EXPECTED_SETTINGS.items()
    }


class FakeAzure:
    def __init__(self, initial: dict[str, AzureAppSetting] | None = None) -> None:
        self.settings = copy.deepcopy(initial or records())
        self.applied: list[dict[str, str]] = []

    def app_setting_records(self) -> dict[str, AzureAppSetting]:
        return copy.deepcopy(self.settings)

    def app_settings(self) -> dict[str, str]:
        return {name: setting.value for name, setting in self.settings.items()}

    def apply_billing_setting_records(self, values: dict[str, AzureAppSetting]) -> None:
        self.applied.append({name: setting.value for name, setting in values.items()})
        self.settings.update(copy.deepcopy(values))


class FakePublishingAzure(FakeAzure):
    def __init__(self, initial: dict[str, AzureAppSetting] | None = None) -> None:
        super().__init__(initial)
        self.events: list[str] = []

    def replace_for(self, publishing_mode: str) -> None:
        self.events.append(f"replace:{publishing_mode}")
        if publishing_mode == "infra":
            self.settings = {"Unrelated__Setting": AzureAppSetting("keep", False)}
        elif publishing_mode in {"app", "promote"}:
            self.settings["Deployment__Marker"] = AzureAppSetting(publishing_mode, False)
        else:
            raise AssertionError(f"unexpected publishing mode: {publishing_mode}")

    def set_expected_mode(self, mode: str) -> None:
        self.events.append(f"set-mode:{mode}")
        self.settings["Billing__Stripe__ExpectedMode"] = AzureAppSetting(mode, False)

    def apply_billing_setting_records(self, values: dict[str, AzureAppSetting]) -> None:
        self.events.append("reapply")
        super().apply_billing_setting_records(values)


class FakeStripe:
    def __init__(self, endpoint: dict[str, object] | None = None) -> None:
        self.endpoint = endpoint or {
            "id": "we_live_authoritative",
            "livemode": True,
            "status": "enabled",
            "url": WEBHOOK_URL,
            "enabled_events": sorted(DEFAULT_WEBHOOK_EVENTS),
        }

    def get(self, path: str) -> dict[str, object]:
        assert path == "/webhook_endpoints?limit=100"
        return {"data": [self.endpoint], "has_more": False}


class ProductionStripeReconcileTests(unittest.TestCase):
    def test_capture_round_trips_every_billing_setting_with_private_permissions(self) -> None:
        azure = FakeAzure()
        reconciler = ProductionStripeReconciler(FakeStripe(), azure, config())

        with tempfile.TemporaryDirectory() as temporary:
            destination = Path(temporary) / "capture.json"
            captured = reconciler.capture()
            write_capture(captured, destination)

            self.assertEqual(0o600, stat.S_IMODE(destination.stat().st_mode))
            restored = read_capture(destination)
            self.assertEqual(set(EXPECTED_SETTINGS), set(restored))
            self.assertEqual("rk_live_private", restored["Billing__Stripe__SecretKey"].value)

    def test_capture_preserves_legacy_settings_before_expected_mode_exists(self) -> None:
        legacy = records()
        legacy.pop("Billing__Stripe__ExpectedMode")
        azure = FakeAzure(legacy)

        captured = ProductionStripeReconciler(FakeStripe(), azure, config()).capture()
        self.assertNotIn("Billing__Stripe__ExpectedMode", captured)

        azure.settings = {}
        ProductionStripeReconciler(FakeStripe(), azure, config()).reapply(captured)

        self.assertEqual({name: setting.value for name, setting in legacy.items()}, azure.app_settings())

    def test_publishing_modes_preserve_billing_around_fake_provider_operations(self) -> None:
        for publishing_mode in ("app", "infra", "promote"):
            with self.subTest(publishing_mode=publishing_mode), tempfile.TemporaryDirectory() as temporary:
                azure = FakePublishingAzure()
                reconciler = ProductionStripeReconciler(FakeStripe(), azure, config())
                destination = Path(temporary) / "capture.json"

                captured = reconciler.capture()
                write_capture(captured, destination)
                azure.replace_for(publishing_mode)
                azure.set_expected_mode("live")
                reconciler.reapply(read_capture(destination))
                destination.unlink()

                self.assertEqual(EXPECTED_SETTINGS, {
                    name: setting.value
                    for name, setting in azure.app_setting_records().items()
                    if name.startswith("Billing__Stripe__")
                })
                self.assertEqual([f"replace:{publishing_mode}", "set-mode:live", "reapply"], azure.events)
                self.assertTrue(azure.app_setting_records()["Billing__Stripe__ExpectedMode"].slot_setting)
                self.assertFalse(destination.exists())

        build_provider = FakePublishingAzure()
        build_provider.events.append("build")
        self.assertEqual([], build_provider.applied)
        self.assertEqual(EXPECTED_SETTINGS, build_provider.app_settings())
        self.assertEqual(["build"], build_provider.events)

    def test_capture_cleanup_runs_when_fake_provider_replacement_fails(self) -> None:
        azure = FakePublishingAzure()
        captured = ProductionStripeReconciler(FakeStripe(), azure, config()).capture()

        with tempfile.TemporaryDirectory() as temporary:
            destination = Path(temporary) / "capture.json"
            write_capture(captured, destination)
            try:
                azure.replace_for("infra")
                raise ReconciliationError("fake provider replacement failed")
            except ReconciliationError:
                pass
            finally:
                destination.unlink(missing_ok=True)

            self.assertFalse(destination.exists())
            self.assertEqual([], azure.applied)

    def test_failed_post_replacement_path_restores_before_runtime_restart(self) -> None:
        azure = FakePublishingAzure()
        reconciler = ProductionStripeReconciler(FakeStripe(), azure, config())
        captured = reconciler.capture()

        with tempfile.TemporaryDirectory() as temporary:
            destination = Path(temporary) / "capture.json"
            write_capture(captured, destination)
            try:
                azure.replace_for("infra")
                azure.set_expected_mode("live")
                raise ReconciliationError("fake health gate failed")
            except ReconciliationError:
                reconciler.reapply(read_capture(destination))
            finally:
                destination.unlink(missing_ok=True)

            self.assertEqual(EXPECTED_SETTINGS, {
                name: setting.value
                for name, setting in azure.app_setting_records().items()
                if name.startswith("Billing__Stripe__")
            })
            self.assertEqual(["replace:infra", "set-mode:live", "reapply"], azure.events)

    def test_partial_payload_is_rejected_before_any_replacement(self) -> None:
        azure = FakeAzure()
        captured = ProductionStripeReconciler(FakeStripe(), azure, config()).capture()
        captured.pop("Billing__Stripe__WebhookSigningSecret")
        previous = azure.app_settings()

        with self.assertRaisesRegex(ReconciliationError, "incomplete"):
            ProductionStripeReconciler(FakeStripe(), azure, config()).reapply(captured)

        self.assertEqual(previous, azure.app_settings())
        self.assertEqual([], azure.applied)

    def test_stale_expected_mode_is_rejected_before_reapply(self) -> None:
        stale = records()
        stale["Billing__Stripe__ExpectedMode"] = AzureAppSetting("test", True)
        azure = FakeAzure(stale)
        previous = azure.app_settings()

        with self.assertRaisesRegex(ReconciliationError, "expected mode"):
            ProductionStripeReconciler(FakeStripe(), azure, config()).reapply(stale)

        self.assertEqual(previous, azure.app_settings())
        self.assertEqual([], azure.applied)

    def test_audit_accepts_restricted_live_key_and_exact_event_set(self) -> None:
        checks = ProductionStripeReconciler(FakeStripe(), FakeAzure(), config()).audit()

        self.assertEqual(("production-billing-settings", "production-stripe-webhook"), checks)

    def test_audit_rejects_a_different_live_account_than_the_captured_setting(self) -> None:
        with self.assertRaisesRegex(ReconciliationError, "does not match") as raised:
            ProductionStripeReconciler(
                FakeStripe(),
                FakeAzure(),
                config(),
                expected_secret_key="sk_live_other_account",
            ).audit()

        self.assertNotIn("sk_live_other_account", str(raised.exception))

    def test_audit_rejects_wrong_event_set_without_exposing_secret(self) -> None:
        endpoint = {
            "id": "we_live_authoritative",
            "livemode": True,
            "status": "enabled",
            "url": WEBHOOK_URL,
            "enabled_events": ["customer.subscription.updated"],
        }

        with self.assertRaisesRegex(ReconciliationError, "events") as raised:
            ProductionStripeReconciler(FakeStripe(endpoint), FakeAzure(), config()).audit()

        self.assertNotIn("rk_live_private", str(raised.exception))

    def test_audit_requires_a_unique_live_endpoint_for_the_approved_url(self) -> None:
        endpoint = {
            "id": "we_test_same_url",
            "livemode": False,
            "status": "enabled",
            "url": WEBHOOK_URL,
            "enabled_events": sorted(DEFAULT_WEBHOOK_EVENTS),
        }

        with self.assertRaisesRegex(ReconciliationError, "missing or ambiguous"):
            ProductionStripeReconciler(FakeStripe(endpoint), FakeAzure(), config()).audit()

    def test_capture_rejects_missing_required_configuration(self) -> None:
        incomplete = records()
        incomplete.pop("Billing__Stripe__CheckoutCancelUrl")

        with self.assertRaisesRegex(ReconciliationError, "incomplete"):
            ProductionStripeReconciler(FakeStripe(), FakeAzure(incomplete), config()).capture()

    def test_audit_configuration_requires_explicit_production_return_urls(self) -> None:
        values = {
            "STRIPE_PRODUCTION_PRICE_ID": "price_live",
            "CONTROL_PRODUCTION_WEBHOOK_URL": WEBHOOK_URL,
            "AZURE_EXPECTED_PRODUCTION_PORTAL_RETURN_URL": PORTAL_RETURN_URL,
            "AZURE_EXPECTED_PRODUCTION_CHECKOUT_SUCCESS_URL": EXPECTED_SETTINGS["Billing__Stripe__CheckoutSuccessUrl"],
            "AZURE_EXPECTED_PRODUCTION_CHECKOUT_CANCEL_URL": EXPECTED_SETTINGS["Billing__Stripe__CheckoutCancelUrl"],
            "AZURE_EXPECTED_PRODUCTION_CLOUD_PORTAL_RETURN_URL": EXPECTED_SETTINGS["Billing__Stripe__CloudPortalReturnUrl"],
            "AZURE_RESOURCE_GROUP": "production-rg",
            "AZURE_WEBAPP_NAME": "production-api",
        }

        parsed = ProductionAuditConfig.from_environment(values)
        self.assertEqual(EXPECTED_SETTINGS["Billing__Stripe__CheckoutSuccessUrl"], parsed.checkout_success_url)
        values.pop("AZURE_EXPECTED_PRODUCTION_CHECKOUT_CANCEL_URL")
        with self.assertRaisesRegex(ReconciliationError, "CHECKOUT_CANCEL_URL"):
            ProductionAuditConfig.from_environment(values)

    def test_capture_file_with_broad_permissions_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            destination = Path(temporary) / "capture.json"
            write_capture(records(), destination)
            os.chmod(destination, 0o644)

            with self.assertRaisesRegex(ReconciliationError, "permissions"):
                read_capture(destination)


if __name__ == "__main__":
    unittest.main()
