from __future__ import annotations

import copy
import io
import json
import os
import stat
import tempfile
import unittest
import urllib.error
from contextlib import redirect_stderr, redirect_stdout
from pathlib import Path
from unittest import mock

from scripts.production_stripe_reconcile import (
    ProductionAuditConfig,
    ProductionStripeReconciler,
    ReconciliationError,
    format_stripe_api_failure,
    main,
    read_capture,
    redact_secrets,
    stripe_request_label,
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

        with self.assertRaisesRegex(ReconciliationError, r"webhook-endpoint: expected 1 live endpoint, found 0"):
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


LIVE_KEY = "rk_live_private"
OTHER_LIVE_KEY = "sk_live_other_account"
WEBHOOK_SECRET = "whsec_private"
CUSTOMER_ID = "cus_leakedCustomer"
QUERY_FRAGMENT = "session_id={CHECKOUT_SESSION_ID}"
STRIPE_ERROR_MESSAGE = f"Invalid API Key provided: {LIVE_KEY} for {CUSTOMER_ID}?{QUERY_FRAGMENT}"


def forbidden_secret_fragments() -> tuple[str, ...]:
    return (
        LIVE_KEY,
        OTHER_LIVE_KEY,
        WEBHOOK_SECRET,
        CUSTOMER_ID,
        QUERY_FRAGMENT,
        "rk_live_",
        "sk_live_",
        "whsec_",
        "cus_",
        "?",
    )


def assert_secret_free(test: unittest.TestCase, text: str) -> None:
    for fragment in forbidden_secret_fragments():
        test.assertNotIn(fragment, text)
    test.assertNotRegex(text, r"(?:sk|rk)_live_[A-Za-z0-9]")
    test.assertNotRegex(text, r"whsec_[A-Za-z0-9]")
    test.assertNotRegex(text, r"cus_[A-Za-z0-9]")
    test.assertNotRegex(text, r"\?[^\s]+")


def stripe_http_error(
    status: int,
    error_type: str | None = None,
    *,
    code: str | None = None,
    message: str = STRIPE_ERROR_MESSAGE,
    url: str = f"https://api.stripe.com/v1/prices/{EXPECTED_SETTINGS['Billing__Stripe__DefaultPriceId']}?customer={CUSTOMER_ID}",
) -> urllib.error.HTTPError:
    payload: dict[str, object] = {"message": message}
    if error_type is not None:
        payload["type"] = error_type
    if code is not None:
        payload["code"] = code
    return urllib.error.HTTPError(
        url,
        status,
        "error",
        hdrs=None,
        fp=io.BytesIO(json.dumps({"error": payload}).encode()),
    )


def audit_environment() -> dict[str, str]:
    return {
        "STRIPE_PRODUCTION_PRICE_ID": "price_live",
        "STRIPE_PRODUCTION_SECRET_KEY": LIVE_KEY,
        "CONTROL_PRODUCTION_WEBHOOK_URL": WEBHOOK_URL,
        "AZURE_EXPECTED_PRODUCTION_PORTAL_RETURN_URL": PORTAL_RETURN_URL,
        "AZURE_EXPECTED_PRODUCTION_CHECKOUT_SUCCESS_URL": EXPECTED_SETTINGS["Billing__Stripe__CheckoutSuccessUrl"],
        "AZURE_EXPECTED_PRODUCTION_CHECKOUT_CANCEL_URL": EXPECTED_SETTINGS["Billing__Stripe__CheckoutCancelUrl"],
        "AZURE_EXPECTED_PRODUCTION_CLOUD_PORTAL_RETURN_URL": EXPECTED_SETTINGS["Billing__Stripe__CloudPortalReturnUrl"],
        "AZURE_RESOURCE_GROUP": "production-rg",
        "AZURE_WEBAPP_NAME": "production-api",
    }


AUDIT_ENV_KEYS = frozenset(audit_environment()) | {"STRIPE_PRODUCTION_WEBHOOK_EVENTS"}


class FailingStripe:
    def __init__(self, error: BaseException, path: str | None = None) -> None:
        self.error = error
        self.path = path

    def get(self, path: str) -> dict[str, object]:
        if self.path is not None:
            assert path == self.path
        raise self.error


class ProductionStripeAuditReasonTests(unittest.TestCase):
    def _reason(self, run) -> str:
        with self.assertRaises(ReconciliationError) as raised:
            run()
        reason = str(raised.exception)
        assert_secret_free(self, reason)
        return reason

    def test_redact_secrets_keeps_key_type_only(self) -> None:
        redacted = redact_secrets(
            f"used {LIVE_KEY} {WEBHOOK_SECRET} {CUSTOMER_ID} https://example.com/?{QUERY_FRAGMENT}"
        )
        self.assertIn("rk_live", redacted)
        assert_secret_free(self, redacted)

    def test_stripe_request_label_names_resource_groups_without_ids_or_queries(self) -> None:
        self.assertEqual("prices.retrieve", stripe_request_label(f"/prices/price_live?customer={CUSTOMER_ID}"))
        self.assertEqual("checkout.sessions.list", stripe_request_label("/checkout/sessions"))
        self.assertEqual("checkout.sessions.retrieve", stripe_request_label("/checkout/sessions/cs_live_secret"))
        self.assertEqual("webhook_endpoints.list", stripe_request_label("/webhook_endpoints?limit=100"))
        self.assertEqual("customers.retrieve", stripe_request_label(f"/customers/{CUSTOMER_ID}"))
        for path in (
            f"/prices/price_live?customer={CUSTOMER_ID}",
            f"/customers/{CUSTOMER_ID}",
            "/checkout/sessions?customer=cus_leakedCustomer",
        ):
            assert_secret_free(self, stripe_request_label(path))

    def test_stripe_api_reason_names_prices_retrieve_authentication_error(self) -> None:
        error = ReconciliationError("Stripe API request failed")
        error.__cause__ = stripe_http_error(401, "authentication_error")
        reason = format_stripe_api_failure(f"/prices/price_live?customer={CUSTOMER_ID}", error)
        self.assertEqual("stripe-api prices.retrieve: authentication_error (401)", reason)
        assert_secret_free(self, reason)

    def test_stripe_api_reason_names_checkout_sessions_permission_error(self) -> None:
        error = ReconciliationError("Stripe API request failed")
        error.__cause__ = stripe_http_error(403, "permission_error", code="more_permissions_required")
        reason = format_stripe_api_failure("/checkout/sessions", error)
        self.assertEqual("stripe-api checkout.sessions.list: permission_error (403)", reason)
        assert_secret_free(self, reason)

    def test_stripe_api_reason_falls_back_to_status_when_body_is_unsafe(self) -> None:
        error = ReconciliationError("Stripe API request failed")
        error.__cause__ = stripe_http_error(401, "Invalid API Key provided: rk_live_private")
        reason = format_stripe_api_failure("/webhook_endpoints?limit=100", error)
        self.assertEqual("stripe-api webhook_endpoints.list: request_failed (401)", reason)
        assert_secret_free(self, reason)

    def test_missing_env_variable_names_the_github_variable(self) -> None:
        values = audit_environment()
        values.pop("STRIPE_PRODUCTION_PRICE_ID")
        reason = self._reason(lambda: ProductionAuditConfig.from_environment(values))
        self.assertEqual("env: STRIPE_PRODUCTION_PRICE_ID is missing", reason)

    def test_invalid_checkout_success_url_names_the_variable_without_query(self) -> None:
        values = audit_environment()
        values["AZURE_EXPECTED_PRODUCTION_CHECKOUT_SUCCESS_URL"] = "http://cloud.example.com/checkout/return?session_id=cus_leakedCustomer"
        reason = self._reason(lambda: ProductionAuditConfig.from_environment(values))
        self.assertIn("env: AZURE_EXPECTED_PRODUCTION_CHECKOUT_SUCCESS_URL", reason)
        self.assertIn("HTTPS", reason)

    def test_missing_azure_setting_names_the_setting(self) -> None:
        incomplete = records()
        incomplete.pop("Billing__Stripe__ExpectedMode")
        reason = self._reason(lambda: ProductionStripeReconciler(FakeStripe(), FakeAzure(incomplete), config()).audit())
        self.assertEqual("azure-setting: missing Billing__Stripe__ExpectedMode", reason)

    def test_mismatched_azure_url_names_the_setting_without_query(self) -> None:
        wrong = records()
        wrong["Billing__Stripe__CheckoutSuccessUrl"] = AzureAppSetting(
            f"{CLOUD_ORIGIN}/other/return?session_id={{CHECKOUT_SESSION_ID}}&customer={CUSTOMER_ID}",
            False,
        )
        reason = self._reason(lambda: ProductionStripeReconciler(FakeStripe(), FakeAzure(wrong), config()).audit())
        self.assertEqual("azure-setting: Billing__Stripe__CheckoutSuccessUrl does not match", reason)

    def test_secret_key_mismatch_names_the_setting_without_key_values(self) -> None:
        reason = self._reason(
            lambda: ProductionStripeReconciler(
                FakeStripe(),
                FakeAzure(),
                config(),
                expected_secret_key=OTHER_LIVE_KEY,
            ).audit()
        )
        self.assertEqual("azure-setting: Billing__Stripe__SecretKey does not match the audit key", reason)

    def test_non_live_secret_names_the_setting_without_the_value(self) -> None:
        wrong = records()
        wrong["Billing__Stripe__SecretKey"] = AzureAppSetting("sk_test_not_for_production", False)
        reason = self._reason(lambda: ProductionStripeReconciler(FakeStripe(), FakeAzure(wrong), config()).audit())
        self.assertEqual("azure-setting: Billing__Stripe__SecretKey is not a live-mode credential", reason)

    def test_unexpected_webhook_secret_shape_names_the_setting(self) -> None:
        wrong = records()
        wrong["Billing__Stripe__WebhookSigningSecret"] = AzureAppSetting("not-a-webhook-secret", False)
        reason = self._reason(lambda: ProductionStripeReconciler(FakeStripe(), FakeAzure(wrong), config()).audit())
        self.assertEqual("azure-setting: Billing__Stripe__WebhookSigningSecret has an unexpected shape", reason)

    def test_billing_disabled_and_mode_name_their_settings(self) -> None:
        disabled = records()
        disabled["Billing__Stripe__Enabled"] = AzureAppSetting("false", False)
        reason = self._reason(lambda: ProductionStripeReconciler(FakeStripe(), FakeAzure(disabled), config()).audit())
        self.assertEqual("azure-setting: Billing__Stripe__Enabled is not true", reason)

        mode = records()
        mode["Billing__Stripe__ExpectedMode"] = AzureAppSetting("test", True)
        reason = self._reason(lambda: ProductionStripeReconciler(FakeStripe(), FakeAzure(mode), config()).audit())
        self.assertEqual("azure-setting: Billing__Stripe__ExpectedMode is not live", reason)

    def test_price_mismatch_names_the_setting(self) -> None:
        wrong = records()
        wrong["Billing__Stripe__DefaultPriceId"] = AzureAppSetting("price_other", False)
        reason = self._reason(lambda: ProductionStripeReconciler(FakeStripe(), FakeAzure(wrong), config()).audit())
        self.assertEqual("azure-setting: Billing__Stripe__DefaultPriceId does not match", reason)

    def test_webhook_endpoint_count_is_named(self) -> None:
        extra = {
            "id": "we_live_duplicate",
            "livemode": True,
            "status": "enabled",
            "url": WEBHOOK_URL,
            "enabled_events": sorted(DEFAULT_WEBHOOK_EVENTS),
        }

        class TwoEndpoints(FakeStripe):
            def get(self, path: str) -> dict[str, object]:
                assert path == "/webhook_endpoints?limit=100"
                return {"data": [self.endpoint, extra], "has_more": False}

        reason = self._reason(lambda: ProductionStripeReconciler(TwoEndpoints(), FakeAzure(), config()).audit())
        self.assertEqual("webhook-endpoint: expected 1 live endpoint, found 2", reason)

    def test_disabled_webhook_endpoint_is_named(self) -> None:
        endpoint = {
            "id": "we_live_authoritative",
            "livemode": True,
            "status": "disabled",
            "url": WEBHOOK_URL,
            "enabled_events": sorted(DEFAULT_WEBHOOK_EVENTS),
        }
        reason = self._reason(lambda: ProductionStripeReconciler(FakeStripe(endpoint), FakeAzure(), config()).audit())
        self.assertEqual("webhook-endpoint: live endpoint is not enabled", reason)

    def test_webhook_events_name_missing_and_extra(self) -> None:
        endpoint = {
            "id": "we_live_authoritative",
            "livemode": True,
            "status": "enabled",
            "url": WEBHOOK_URL,
            "enabled_events": ["customer.subscription.updated", "invoice.paid"],
        }
        reason = self._reason(lambda: ProductionStripeReconciler(FakeStripe(endpoint), FakeAzure(), config()).audit())
        self.assertTrue(reason.startswith("webhook-events: missing "))
        self.assertIn("checkout.session.completed", reason)
        self.assertIn("customer.subscription.created", reason)
        self.assertIn("extra invoice.paid", reason)
        self.assertNotIn(WEBHOOK_URL, reason)

    def test_audit_stripe_api_failure_uses_named_reason(self) -> None:
        error = ReconciliationError("Stripe API request failed")
        error.__cause__ = stripe_http_error(401, "authentication_error")
        reason = self._reason(
            lambda: ProductionStripeReconciler(FailingStripe(error), FakeAzure(), config()).audit()
        )
        self.assertEqual("stripe-api webhook_endpoints.list: authentication_error (401)", reason)

    def test_success_still_returns_the_same_checks(self) -> None:
        checks = ProductionStripeReconciler(FakeStripe(), FakeAzure(), config()).audit()
        self.assertEqual(("production-billing-settings", "production-stripe-webhook"), checks)

    def _run_main(self, argv: list[str], *, stripe: object | None = None, azure: object | None = None, env: dict[str, str] | None = None) -> tuple[int, str, str]:
        stdout = io.StringIO()
        stderr = io.StringIO()

        class SuccessStripe:
            def __init__(self, *args: object, **kwargs: object) -> None:
                self._inner = FakeStripe()

            def get(self, path: str) -> dict[str, object]:
                return self._inner.get(path)

        merged = {key: value for key, value in os.environ.items() if key not in AUDIT_ENV_KEYS}
        merged.update(env if env is not None else audit_environment())
        with (
            mock.patch("scripts.production_stripe_reconcile.StripeApi", stripe if stripe is not None else SuccessStripe),
            mock.patch("scripts.production_stripe_reconcile.AzureWebApp", azure if azure is not None else (lambda *args, **kwargs: FakeAzure())),
            mock.patch.dict(os.environ, merged, clear=True),
            redirect_stdout(stdout),
            redirect_stderr(stderr),
        ):
            code = main(argv)
        combined = stdout.getvalue() + stderr.getvalue()
        assert_secret_free(self, combined)
        return code, stdout.getvalue(), stderr.getvalue()

    def test_main_audit_success_is_unchanged(self) -> None:
        code, stdout, stderr = self._run_main(["--audit"])
        self.assertEqual(0, code)
        self.assertEqual("PASS production-billing-settings\nPASS production-stripe-webhook\n", stdout)
        self.assertEqual("", stderr)

    def test_main_audit_prints_named_stripe_api_reason_and_keeps_exit_code(self) -> None:
        class UnauthorizedStripe:
            def __init__(self, *args: object, **kwargs: object) -> None:
                pass

            def get(self, path: str) -> dict[str, object]:
                raise ReconciliationError("Stripe API request failed") from stripe_http_error(401, "authentication_error")

        code, stdout, stderr = self._run_main(["--audit"], stripe=UnauthorizedStripe)
        self.assertEqual(1, code)
        self.assertEqual("", stdout)
        self.assertEqual("FAIL stripe-api webhook_endpoints.list: authentication_error (401)\n", stderr)

    def test_main_audit_prints_missing_env_variable(self) -> None:
        env = audit_environment()
        env.pop("CONTROL_PRODUCTION_WEBHOOK_URL")
        code, stdout, stderr = self._run_main(["--audit"], env=env)
        self.assertEqual(1, code)
        self.assertEqual("", stdout)
        self.assertEqual("FAIL env: CONTROL_PRODUCTION_WEBHOOK_URL is missing\n", stderr)

    def test_main_audit_prints_azure_setting_mismatch(self) -> None:
        wrong = records()
        wrong["Billing__Stripe__PortalReturnUrl"] = AzureAppSetting("https://control.example.com/other", False)

        class WrongAzure:
            def __init__(self, *args: object, **kwargs: object) -> None:
                self._inner = FakeAzure(wrong)

            def app_settings(self) -> dict[str, str]:
                return self._inner.app_settings()

        code, stdout, stderr = self._run_main(["--audit"], azure=WrongAzure)
        self.assertEqual(1, code)
        self.assertEqual("FAIL azure-setting: Billing__Stripe__PortalReturnUrl does not match\n", stderr)

    def test_main_capture_failure_keeps_generic_line_and_exit_code(self) -> None:
        incomplete = records()
        incomplete.pop("Billing__Stripe__WebhookSigningSecret")

        class IncompleteAzure:
            def __init__(self, *args: object, **kwargs: object) -> None:
                self._inner = FakeAzure(incomplete)

            def app_setting_records(self) -> dict[str, AzureAppSetting]:
                return self._inner.app_setting_records()

        with tempfile.TemporaryDirectory() as temporary:
            destination = Path(temporary) / "capture.json"
            code, stdout, stderr = self._run_main(["--capture", str(destination)], azure=IncompleteAzure)
        self.assertEqual(1, code)
        self.assertEqual("FAIL production-stripe-reconciliation\n", stderr)


if __name__ == "__main__":
    unittest.main()
