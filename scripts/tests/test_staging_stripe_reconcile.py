#!/usr/bin/env python3
"""Focused contract tests for the staging Stripe reconciliation helper."""

from __future__ import annotations

import copy
import os
import stat
import tempfile
import unittest
from pathlib import Path
from typing import Any, Mapping
from unittest import mock

from scripts.staging_stripe_reconcile import (
    DEFAULT_WEBHOOK_EVENTS,
    AzureAppSetting,
    AzureWebApp,
    ReconciliationConfig,
    ReconciliationError,
    StripeApi,
    StagingStripeReconciler,
    bootstrap_webhook_secret,
    bootstrap_portal,
    idempotency_key,
    require_test_secret,
    stripe_list_all,
)


PRICE_ID = "price_hostedtest"
WEBHOOK_URL = "https://control-staging.azurewebsites.net/api/billing/webhooks/stripe"
CLOUD_RETURN_URL = "https://cloud-staging.azurestaticapps.net/dashboard"
WEBHOOK_SECRET = "whsec_staging_test"


def config(**overrides: Any) -> ReconciliationConfig:
    values = {
        "hosted_price_id": PRICE_ID,
        "hosted_price_amount_cents": 9900,
        "hosted_price_currency": "eur",
        "hosted_price_interval": "month",
        "hosted_price_interval_count": 1,
        "control_webhook_url": WEBHOOK_URL,
        "control_webhook_events": DEFAULT_WEBHOOK_EVENTS,
        "portal_configuration_id": None,
        "webhook_secret": WEBHOOK_SECRET,
        "cloud_portal_return_url": CLOUD_RETURN_URL,
        "azure_resource_group": "control-staging-rg",
        "azure_webapp_name": "control-staging-api",
    }
    values.update(overrides)
    return ReconciliationConfig(**values)


def stripe_objects() -> dict[str, Mapping[str, Any]]:
    return {
        "price": {
            "id": PRICE_ID,
            "livemode": False,
            "active": True,
            "type": "recurring",
            "unit_amount": 9900,
            "currency": "eur",
            "recurring": {"interval": "month", "interval_count": 1},
        },
        "webhooks": {
            "has_more": False,
            "data": [
                {
                    "id": "we_test_control",
                    "livemode": False,
                    "url": WEBHOOK_URL,
                    "status": "enabled",
                    "enabled_events": sorted(DEFAULT_WEBHOOK_EVENTS),
                }
            ]
        },
        "portals": {
            "has_more": False,
            "data": [
                {
                    "id": "bpc_test_hosted",
                    "livemode": False,
                    "active": True,
                    "features": {
                        "invoice_history": {"enabled": True},
                        "payment_method_update": {"enabled": True},
                        "subscription_cancel": {"enabled": True, "mode": "at_period_end"},
                    },
                }
            ]
        },
    }


def azure_settings() -> list[dict[str, str]]:
    return [
        {"name": "Billing__Stripe__Enabled", "value": "true"},
        {"name": "Billing__Stripe__SecretKey", "value": "sk_test_staging"},
        {"name": "Billing__Stripe__WebhookSigningSecret", "value": WEBHOOK_SECRET},
        {"name": "Billing__Stripe__DefaultPriceId", "value": PRICE_ID},
        {"name": "Billing__Stripe__CloudPortalReturnUrl", "value": CLOUD_RETURN_URL},
        {"name": "Billing__Stripe__CheckoutSuccessUrl", "value": "https://cloud-staging.azurestaticapps.net/checkout/return?session_id={CHECKOUT_SESSION_ID}"},
        {"name": "Billing__Stripe__CheckoutCancelUrl", "value": "https://cloud-staging.azurestaticapps.net/dashboard/billing"},
        {"name": "Billing__Stripe__PortalReturnUrl", "value": "https://control-staging.azurewebsites.net/billing"},
    ]


class FakeStripe:
    def __init__(self, objects: Mapping[str, Mapping[str, Any]] | None = None) -> None:
        self.objects = copy.deepcopy(objects or stripe_objects())
        self.posts: list[tuple[str, Mapping[str, Any]]] = []
        self.post_keys: list[str | None] = []
        self.deletes: list[str] = []

    def get(self, path: str) -> Mapping[str, Any]:
        if path.startswith("/prices/"):
            return self.objects["price"]
        if path.startswith("/webhook_endpoints"):
            return self.objects["webhooks"]
        if path.startswith("/billing_portal/configurations"):
            return self.objects["portals"]
        raise AssertionError(path)

    def post(
        self,
        path: str,
        form: Mapping[str, Any],
        *,
        idempotency_key: str | None = None,
    ) -> Mapping[str, Any]:
        self.posts.append((path, form))
        self.post_keys.append(idempotency_key)
        if path.startswith("/billing_portal/configurations/"):
            return {
                "id": path.rsplit("/", 1)[-1],
                "livemode": False,
                "active": False,
            }
        if path == "/billing_portal/configurations":
            return {
                "id": "bpc_test_created",
                "livemode": False,
                "active": True,
                "features": {
                    "invoice_history": {"enabled": True},
                    "payment_method_update": {"enabled": True},
                    "subscription_cancel": {"enabled": True, "mode": "at_period_end"},
                },
            }
        return {
            "id": "we_test_created",
            "livemode": False,
            "url": WEBHOOK_URL,
            "status": "enabled",
            "enabled_events": sorted(DEFAULT_WEBHOOK_EVENTS),
            "secret": "whsec_issued_once",
        }

    def delete(self, path: str) -> Mapping[str, Any]:
        self.deletes.append(path)
        return {"id": path.rsplit("/", 1)[-1], "deleted": True}


class FakeAzure:
    def __init__(self, values: list[dict[str, str]] | None = None) -> None:
        initial = azure_settings() if values is None else values
        self.values = {item["name"]: item["value"] for item in initial}
        self.applied: dict[str, str] | None = None
        self.applications: list[dict[str, str]] = []
        self.deleted: list[str] = []

    def app_settings(self) -> dict[str, str]:
        return dict(self.values)

    def app_setting_records(self) -> dict[str, AzureAppSetting]:
        return {name: AzureAppSetting(value, False) for name, value in self.values.items()}

    def apply_billing_settings(self, settings: Mapping[str, str]) -> None:
        self.applied = dict(settings)
        self.applications.append(dict(settings))
        self.values.update(settings)

    def delete_billing_settings(self, names: list[str]) -> None:
        self.deleted.extend(names)
        for name in names:
            self.values.pop(name, None)


class AmbiguousApplyAzure(FakeAzure):
    def __init__(self, values: list[dict[str, str]] | None = None) -> None:
        super().__init__(values)
        self._fail_next_apply = True

    def apply_billing_settings(self, settings: Mapping[str, str]) -> None:
        super().apply_billing_settings(settings)
        if self._fail_next_apply:
            self._fail_next_apply = False
            raise ReconciliationError("Azure billing settings update failed")


class StagingStripeReconciliationTests(unittest.TestCase):
    def test_full_audit_passes_and_returns_only_safe_check_names(self) -> None:
        result = StagingStripeReconciler(FakeStripe(), FakeAzure(), config()).run(
            stripe_secret_key="sk_test_staging",
        )

        self.assertEqual(
            ("stripe-price", "control-webhook", "customer-portal", "azure-billing-settings"),
            result,
        )
        self.assertNotIn("price_hosted_test", " ".join(result))

    def test_apply_reconciles_the_complete_secret_bearing_azure_subset_before_audit(self) -> None:
        azure = FakeAzure(
            [item for item in azure_settings() if item["name"] not in {
                "Billing__Stripe__Enabled",
                "Billing__Stripe__SecretKey",
                "Billing__Stripe__WebhookSigningSecret",
                "Billing__Stripe__DefaultPriceId",
                "Billing__Stripe__CloudPortalReturnUrl",
            }]
        )

        result = StagingStripeReconciler(FakeStripe(), azure, config()).run(
            apply_azure_settings=True,
            stripe_secret_key="sk_test_staging",
        )

        self.assertEqual("true", azure.applied["Billing__Stripe__Enabled"])
        self.assertEqual(WEBHOOK_SECRET, azure.applied["Billing__Stripe__WebhookSigningSecret"])
        self.assertEqual(PRICE_ID, azure.applied["Billing__Stripe__DefaultPriceId"])
        self.assertEqual(4, len(result))

    def test_audit_rejects_a_different_test_account_key_in_azure(self) -> None:
        with self.assertRaisesRegex(ReconciliationError, "audited test account"):
            StagingStripeReconciler(FakeStripe(), FakeAzure(), config()).run(
                stripe_secret_key="sk_test_different",
            )

    def test_apply_rejects_live_key_before_azure_mutation(self) -> None:
        azure = FakeAzure()
        with self.assertRaisesRegex(ReconciliationError, "test-mode"):
            StagingStripeReconciler(FakeStripe(), azure, config()).run(
                apply_azure_settings=True,
                stripe_secret_key="sk_live_forbidden",
            )
        self.assertIsNone(azure.applied)

    def test_apply_rejects_a_preexisting_live_azure_key_before_mutation(self) -> None:
        values = azure_settings()
        for item in values:
            if item["name"] == "Billing__Stripe__SecretKey":
                item["value"] = "sk_live_forbidden"
        azure = FakeAzure(values)

        with self.assertRaisesRegex(ReconciliationError, "not configured with a test Stripe account"):
            StagingStripeReconciler(FakeStripe(), azure, config()).run(
                apply_azure_settings=True,
                stripe_secret_key="sk_test_staging",
            )

        self.assertEqual([], azure.applications)

    def test_apply_rejects_slot_sticky_billing_settings_before_mutation(self) -> None:
        class StickyAzure(FakeAzure):
            def app_setting_records(self) -> dict[str, AzureAppSetting]:
                records = super().app_setting_records()
                records["Billing__Stripe__SecretKey"] = AzureAppSetting(
                    records["Billing__Stripe__SecretKey"].value,
                    True,
                )
                return records

        azure = StickyAzure()
        with self.assertRaisesRegex(ReconciliationError, "must not be slot-sticky"):
            StagingStripeReconciler(FakeStripe(), azure, config()).run(
                apply_azure_settings=True,
                stripe_secret_key="sk_test_staging",
            )

        self.assertEqual([], azure.applications)

    def test_failed_post_write_verification_restores_prior_azure_settings(self) -> None:
        values = [
            item for item in azure_settings()
            if item["name"] not in {"Billing__Stripe__WebhookSigningSecret", "Billing__Stripe__CloudPortalReturnUrl"}
        ]
        for item in values:
            if item["name"] == "Billing__Stripe__CheckoutCancelUrl":
                item["value"] = "https://cloud-staging.azurestaticapps.net/wrong"
        azure = FakeAzure(values)
        previous = azure.app_settings()

        with self.assertRaisesRegex(ReconciliationError, "checkout cancel URL"):
            StagingStripeReconciler(FakeStripe(), azure, config()).run(
                apply_azure_settings=True,
                stripe_secret_key="sk_test_staging",
            )

        self.assertEqual(previous, azure.app_settings())
        self.assertEqual(2, len(azure.applications))
        self.assertCountEqual(
            ["Billing__Stripe__WebhookSigningSecret", "Billing__Stripe__CloudPortalReturnUrl"],
            azure.deleted,
        )

    def test_ambiguous_apply_failure_restores_prior_azure_settings(self) -> None:
        azure = AmbiguousApplyAzure()
        previous = azure.app_settings()

        with self.assertRaisesRegex(ReconciliationError, "settings update failed"):
            StagingStripeReconciler(FakeStripe(), azure, config()).run(
                apply_azure_settings=True,
                stripe_secret_key="sk_test_staging",
            )

        self.assertEqual(previous, azure.app_settings())
        self.assertEqual(2, len(azure.applications))

    def test_environment_config_defaults_to_the_exact_control_event_set(self) -> None:
        values = {
            "STRIPE_HOSTED_PRICE_ID": PRICE_ID,
            "STRIPE_WEBHOOK_SECRET": WEBHOOK_SECRET,
            "CONTROL_STAGING_WEBHOOK_URL": WEBHOOK_URL,
            "CLOUD_PORTAL_RETURN_URL": CLOUD_RETURN_URL,
            "AZURE_RESOURCE_GROUP": "staging-rg",
            "AZURE_WEBAPP_NAME": "staging-app",
        }

        parsed = ReconciliationConfig.from_environment(values)

        self.assertEqual(DEFAULT_WEBHOOK_EVENTS, parsed.control_webhook_events)
        self.assertEqual(9900, parsed.hosted_price_amount_cents)
        self.assertEqual("eur", parsed.hosted_price_currency)

    def test_environment_config_rejects_production_or_unapproved_hosts(self) -> None:
        values = {
            "STRIPE_HOSTED_PRICE_ID": PRICE_ID,
            "STRIPE_WEBHOOK_SECRET": WEBHOOK_SECRET,
            "CONTROL_STAGING_WEBHOOK_URL": WEBHOOK_URL,
            "CLOUD_PORTAL_RETURN_URL": "https://elsacloud.app/dashboard",
            "AZURE_RESOURCE_GROUP": "staging-rg",
            "AZURE_WEBAPP_NAME": "staging-app",
        }
        with self.assertRaisesRegex(ReconciliationError, "approved staging dashboard"):
            ReconciliationConfig.from_environment(values)

        values["CLOUD_PORTAL_RETURN_URL"] = CLOUD_RETURN_URL
        values["CONTROL_STAGING_WEBHOOK_URL"] = "https://control.example.test/api/billing/webhooks/stripe"
        with self.assertRaisesRegex(ReconciliationError, "unexpected path"):
            ReconciliationConfig.from_environment(values)

    def test_environment_config_requires_the_control_webhook_route(self) -> None:
        values = {
            "STRIPE_HOSTED_PRICE_ID": PRICE_ID,
            "STRIPE_WEBHOOK_SECRET": WEBHOOK_SECRET,
            "CONTROL_STAGING_WEBHOOK_URL": "https://control-staging.azurewebsites.net/wrong-route",
            "CLOUD_PORTAL_RETURN_URL": CLOUD_RETURN_URL,
            "AZURE_RESOURCE_GROUP": "staging-rg",
            "AZURE_WEBAPP_NAME": "staging-app",
        }

        with self.assertRaisesRegex(ReconciliationError, "unexpected path"):
            ReconciliationConfig.from_environment(values)

    def test_bootstrap_config_does_not_require_an_existing_webhook_secret(self) -> None:
        values = {
            "STRIPE_HOSTED_PRICE_ID": PRICE_ID,
            "CONTROL_STAGING_WEBHOOK_URL": WEBHOOK_URL,
            "CLOUD_PORTAL_RETURN_URL": CLOUD_RETURN_URL,
            "AZURE_RESOURCE_GROUP": "staging-rg",
            "AZURE_WEBAPP_NAME": "staging-app",
        }

        parsed = ReconciliationConfig.from_environment(values, require_webhook_secret=False)

        self.assertEqual("", parsed.webhook_secret)

    def test_live_stripe_secret_is_rejected_before_any_request(self) -> None:
        with self.assertRaisesRegex(ReconciliationError, "test-mode"):
            StripeApi("sk_live_should_never_be_used")

    def test_live_price_is_rejected_closed(self) -> None:
        objects = stripe_objects()
        objects["price"] = {**objects["price"], "livemode": True}

        with self.assertRaisesRegex(ReconciliationError, "non-test price"):
            StagingStripeReconciler(FakeStripe(objects), FakeAzure(), config()).check_price()

    def test_webhook_requires_one_exact_endpoint_and_event_set(self) -> None:
        objects = stripe_objects()
        objects["webhooks"] = {
            "has_more": False,
            "data": [
                {
                    "id": "we_test_control",
                    "livemode": False,
                    "url": WEBHOOK_URL,
                    "status": "enabled",
                    "enabled_events": ["customer.subscription.updated"],
                }
            ]
        }

        with self.assertRaisesRegex(ReconciliationError, "events"):
            StagingStripeReconciler(FakeStripe(objects), FakeAzure(), config()).check_webhook()

    def test_stripe_lists_are_followed_across_pages(self) -> None:
        calls: list[str] = []

        class PaginatedStripe:
            def get(self, path: str) -> Mapping[str, Any]:
                calls.append(path)
                if len(calls) == 1:
                    return {"data": [{"id": "first"}], "has_more": True}
                return {"data": [{"id": "second"}], "has_more": False}

        items = stripe_list_all(PaginatedStripe(), "/webhook_endpoints?limit=100")

        self.assertEqual(["first", "second"], [item["id"] for item in items])
        self.assertIn("starting_after=first", calls[1])

    def test_stripe_lists_reject_missing_or_non_boolean_pagination_state(self) -> None:
        for response in ({"data": []}, {"data": [], "has_more": "false"}):
            class MalformedStripe:
                def get(self, path: str) -> Mapping[str, Any]:
                    return response

            with self.assertRaisesRegex(ReconciliationError, "pagination state"):
                stripe_list_all(MalformedStripe(), "/webhook_endpoints?limit=100")

    def test_portal_requires_period_end_cancellation(self) -> None:
        objects = stripe_objects()
        portal = copy.deepcopy(objects["portals"]["data"][0])
        portal["features"]["subscription_cancel"]["mode"] = "immediately"
        objects["portals"] = {"data": [portal], "has_more": False}

        with self.assertRaisesRegex(ReconciliationError, "period end"):
            StagingStripeReconciler(FakeStripe(objects), FakeAzure(), config()).check_portal()

    def test_portal_requires_invoice_history_and_payment_method_management(self) -> None:
        for feature_name in ("invoice_history", "payment_method_update"):
            objects = stripe_objects()
            objects["portals"]["data"][0]["features"][feature_name]["enabled"] = False
            with self.assertRaisesRegex(ReconciliationError, "management features"):
                StagingStripeReconciler(FakeStripe(objects), FakeAzure(), config()).check_portal()

    def test_azure_settings_reject_live_secret_and_require_exact_linkage(self) -> None:
        values = azure_settings()
        for item in values:
            if item["name"] == "Billing__Stripe__SecretKey":
                item["value"] = "sk_live_must_fail"

        with self.assertRaisesRegex(ReconciliationError, "test-mode"):
            StagingStripeReconciler(FakeStripe(), FakeAzure(values), config()).check_azure_settings()

    def test_azure_settings_reject_price_or_webhook_secret_drift(self) -> None:
        values = azure_settings()
        for item in values:
            if item["name"] == "Billing__Stripe__DefaultPriceId":
                item["value"] = "price_other_test"

        with self.assertRaisesRegex(ReconciliationError, "DefaultPriceId"):
            StagingStripeReconciler(FakeStripe(), FakeAzure(values), config()).check_azure_settings()

    def test_azure_checkout_urls_allow_a_query_but_reject_credentials_or_fragments(self) -> None:
        values = azure_settings()
        for item in values:
            if item["name"] == "Billing__Stripe__CheckoutSuccessUrl":
                item["value"] = "https://cloud-staging.azurestaticapps.net/checkout/return?session_id={CHECKOUT_SESSION_ID}"
        StagingStripeReconciler(FakeStripe(), FakeAzure(values), config()).check_azure_settings()

        for unsafe in (
            "https://user:password@cloud-staging.azurestaticapps.net/checkout/return",
            "https://cloud-staging.azurestaticapps.net/checkout/return#fragment",
        ):
            unsafe_values = copy.deepcopy(values)
            for item in unsafe_values:
                if item["name"] == "Billing__Stripe__CheckoutSuccessUrl":
                    item["value"] = unsafe
            with self.assertRaises(ReconciliationError):
                StagingStripeReconciler(FakeStripe(), FakeAzure(unsafe_values), config()).check_azure_settings()

    def test_azure_checkout_urls_must_match_the_staging_cloud_routes(self) -> None:
        for name, value in (
            ("Billing__Stripe__CheckoutSuccessUrl", "https://other.azurestaticapps.net/checkout/return?session_id={CHECKOUT_SESSION_ID}"),
            ("Billing__Stripe__CheckoutCancelUrl", "https://cloud-staging.azurestaticapps.net/checkout/cancel"),
        ):
            values = azure_settings()
            for item in values:
                if item["name"] == name:
                    item["value"] = value
            with self.assertRaisesRegex(ReconciliationError, "does not match"):
                StagingStripeReconciler(FakeStripe(), FakeAzure(values), config()).check_azure_settings()

    def test_url_validation_rejects_whitespace_backslashes_and_encoded_controls(self) -> None:
        for unsafe in (
            "https://cloud-staging.azurestaticapps.net/dashboard billing",
            "https://cloud-staging.azurestaticapps.net\\@evil.example/dashboard",
            "https://cloud-staging.azurestaticapps.net/dashboard%0d%0aInjected",
        ):
            values = azure_settings()
            for item in values:
                if item["name"] == "Billing__Stripe__CheckoutCancelUrl":
                    item["value"] = unsafe
            with self.assertRaises(ReconciliationError):
                StagingStripeReconciler(FakeStripe(), FakeAzure(values), config()).check_azure_settings()

    def test_cloud_portal_does_not_require_the_legacy_control_console_return_url(self) -> None:
        values = [
            item for item in azure_settings()
            if item["name"] != "Billing__Stripe__PortalReturnUrl"
        ]

        StagingStripeReconciler(FakeStripe(), FakeAzure(values), config()).check_azure_settings()

    def test_bootstrap_writes_one_time_secret_with_mode_0600_and_never_prints_it(self) -> None:
        stripe = FakeStripe({**stripe_objects(), "webhooks": {"data": [], "has_more": False}})
        with tempfile.TemporaryDirectory() as directory:
            destination = Path(directory) / "webhook.secret"
            bootstrap_webhook_secret(stripe, config(), destination)

            self.assertEqual("whsec_issued_once\n", destination.read_text())
            self.assertEqual(0o600, stat.S_IMODE(destination.stat().st_mode))
            self.assertEqual("/webhook_endpoints", stripe.posts[0][0])
            self.assertTrue(stripe.post_keys[0].startswith("elsa-control-staging-webhook-"))
            self.assertNotIn("whsec_issued_once", " ".join(path for path, _ in stripe.posts))

    def test_bootstrap_refuses_to_reissue_an_existing_endpoint(self) -> None:
        stripe = FakeStripe()
        with tempfile.TemporaryDirectory() as directory:
            destination = Path(directory) / "webhook.secret"
            with self.assertRaisesRegex(ReconciliationError, "already exists"):
                bootstrap_webhook_secret(stripe, config(), destination)
            self.assertFalse(destination.exists())
            self.assertEqual([], stripe.posts)

    def test_bootstrap_does_not_remove_a_destination_created_by_another_process(self) -> None:
        stripe = FakeStripe({**stripe_objects(), "webhooks": {"data": [], "has_more": False}})
        with tempfile.TemporaryDirectory() as directory:
            destination = Path(directory) / "webhook.secret"

            def raced_open(path: Any, flags: int, mode: int) -> int:
                destination.write_text("owned-by-another-process\n", encoding="utf-8")
                raise FileExistsError(str(path))

            with mock.patch("scripts.staging_stripe_reconcile.os.open", side_effect=raced_open):
                with self.assertRaisesRegex(ReconciliationError, "Could not write"):
                    bootstrap_webhook_secret(stripe, config(), destination)

            self.assertEqual("owned-by-another-process\n", destination.read_text())

    def test_webhook_bootstrap_cleans_up_an_id_from_a_malformed_created_response(self) -> None:
        objects = stripe_objects()
        objects["webhooks"] = {"data": [], "has_more": False}

        class MalformedWebhookStripe(FakeStripe):
            def post(
                self,
                path: str,
                form: Mapping[str, Any],
                *,
                idempotency_key: str | None = None,
            ) -> Mapping[str, Any]:
                self.posts.append((path, form))
                self.post_keys.append(idempotency_key)
                return {
                    "id": "we_test_malformed",
                    "url": WEBHOOK_URL,
                    "status": "enabled",
                    "enabled_events": sorted(DEFAULT_WEBHOOK_EVENTS),
                    "secret": "whsec_malformed",
                }

        stripe = MalformedWebhookStripe(objects)
        with tempfile.TemporaryDirectory() as directory:
            destination = Path(directory) / "webhook.secret"
            with self.assertRaisesRegex(ReconciliationError, "non-test webhook endpoint"):
                bootstrap_webhook_secret(stripe, config(), destination)

            self.assertEqual(["/webhook_endpoints/we_test_malformed"], stripe.deletes)
            self.assertFalse(destination.exists())

    def test_bootstrap_recovers_the_same_endpoint_with_the_same_idempotency_key(self) -> None:
        objects = stripe_objects()

        class RecoveringStripe(FakeStripe):
            def post(
                self,
                path: str,
                form: Mapping[str, Any],
                *,
                idempotency_key: str | None = None,
            ) -> Mapping[str, Any]:
                self.posts.append((path, form))
                self.post_keys.append(idempotency_key)
                return {
                    "id": "we_test_control",
                    "livemode": False,
                    "url": WEBHOOK_URL,
                    "status": "enabled",
                    "enabled_events": sorted(DEFAULT_WEBHOOK_EVENTS),
                    "secret": "whsec_recovered_once",
                }

        stripe = RecoveringStripe(objects)
        stripe.objects["webhooks"]["data"][0]["metadata"] = {
            "elsa_reconcile_key": idempotency_key("webhook", WEBHOOK_URL)
        }
        with tempfile.TemporaryDirectory() as directory:
            destination = Path(directory) / "webhook.secret"
            bootstrap_webhook_secret(stripe, config(), destination)

            self.assertEqual("whsec_recovered_once\n", destination.read_text())
            self.assertEqual([], stripe.deletes)

    def test_portal_bootstrap_is_idempotent_when_the_period_end_policy_exists(self) -> None:
        stripe = FakeStripe()

        bootstrap_portal(stripe, config())

        self.assertEqual([], stripe.posts)

    def test_portal_bootstrap_honors_a_pinned_configuration_id(self) -> None:
        stripe = FakeStripe()
        with self.assertRaisesRegex(ReconciliationError, "Pinned test Customer Portal"):
            bootstrap_portal(
                stripe,
                config(portal_configuration_id="bpc_different"),
            )

        self.assertEqual([], stripe.posts)

    def test_portal_bootstrap_creates_the_period_end_policy_when_missing(self) -> None:
        objects = stripe_objects()
        objects["portals"] = {"data": [], "has_more": False}
        stripe = FakeStripe(objects)

        bootstrap_portal(stripe, config())

        self.assertEqual("/billing_portal/configurations", stripe.posts[0][0])
        form = stripe.posts[0][1]
        self.assertEqual("at_period_end", form["features[subscription_cancel][mode]"])
        self.assertEqual(CLOUD_RETURN_URL, form["default_return_url"])
        self.assertTrue(stripe.post_keys[0].startswith("elsa-control-staging-portal-"))

    def test_stripe_api_forwards_the_idempotency_key_to_transport(self) -> None:
        observed: list[str | None] = []

        def transport(method: str, path: str, form: Mapping[str, Any] | None, key: str | None):
            observed.append(key)
            return {"livemode": False}

        StripeApi("sk_test_staging", transport=transport).post(
            "/test",
            {"value": "safe"},
            idempotency_key="stable-key",
        )

        self.assertEqual(["stable-key"], observed)

    def test_portal_bootstrap_refuses_a_live_or_ambiguous_configuration(self) -> None:
        objects = stripe_objects()
        portal = objects["portals"]["data"][0]
        objects["portals"] = {"data": [portal, copy.deepcopy(portal)], "has_more": False}
        with self.assertRaisesRegex(ReconciliationError, "ambiguous"):
            bootstrap_portal(FakeStripe(objects), config())

    def test_portal_bootstrap_deactivates_an_invalid_created_configuration(self) -> None:
        objects = stripe_objects()
        objects["portals"] = {"data": [], "has_more": False}

        class InvalidPortalStripe(FakeStripe):
            def post(
                self,
                path: str,
                form: Mapping[str, Any],
                *,
                idempotency_key: str | None = None,
            ) -> Mapping[str, Any]:
                if path.startswith("/billing_portal/configurations/"):
                    return super().post(path, form, idempotency_key=idempotency_key)
                self.posts.append((path, form))
                self.post_keys.append(idempotency_key)
                return {
                    "id": "bpc_test_invalid",
                    "active": True,
                    "features": {
                        "invoice_history": {"enabled": False},
                        "payment_method_update": {"enabled": True},
                        "subscription_cancel": {"enabled": True, "mode": "at_period_end"},
                    },
                }

        stripe = InvalidPortalStripe(objects)
        with self.assertRaisesRegex(ReconciliationError, "non-test portal configuration"):
            bootstrap_portal(stripe, config())

        self.assertEqual(
            "/billing_portal/configurations/bpc_test_invalid",
            stripe.posts[-1][0],
        )
        self.assertEqual("false", stripe.posts[-1][1]["active"])
        self.assertTrue(stripe.post_keys[-1].startswith("elsa-control-staging-portal-cleanup-"))

    def test_secret_validation_does_not_accept_empty_or_live_values(self) -> None:
        for value in ("", "sk_live_x", "whsec_live_is_not_a_key_prefix"):
            with self.assertRaises(ReconciliationError):
                require_test_secret(value, name="test", prefix="sk_test_")


class AzureWebAppTests(unittest.TestCase):
    def test_azure_cli_failure_is_sanitized(self) -> None:
        def runner(*args: Any, **kwargs: Any):
            class Result:
                returncode = 1
                stdout = ""
                stderr = "provider-id-and-secret-must-not-escape"

            return Result()

        with self.assertRaisesRegex(ReconciliationError, "Azure CLI request failed"):
            AzureWebApp("resource-group", "webapp", runner=runner).app_settings()

    def test_app_setting_records_preserve_slot_metadata(self) -> None:
        def runner(*args: Any, **kwargs: Any):
            class Result:
                returncode = 0
                stdout = '[{"name":"Billing__Stripe__SecretKey","value":"sk_test_safe","slotSetting":true}]'
                stderr = ""

            return Result()

        records = AzureWebApp("resource-group", "webapp", runner=runner).app_setting_records()

        self.assertEqual("sk_test_safe", records["Billing__Stripe__SecretKey"].value)
        self.assertTrue(records["Billing__Stripe__SecretKey"].slot_setting)

    def test_apply_uses_a_private_payload_file_and_removes_it(self) -> None:
        observed: dict[str, Any] = {}

        def runner(command: list[str], **kwargs: Any):
            class Result:
                returncode = 0
                stdout = ""
                stderr = ""

            payload_argument = command[command.index("--settings") + 1]
            self.assertTrue(payload_argument.startswith("@"))
            payload_path = Path(payload_argument[1:])
            observed["path"] = payload_path
            observed["mode"] = stat.S_IMODE(payload_path.stat().st_mode)
            observed["payload"] = payload_path.read_text()
            observed["command"] = command
            return Result()

        AzureWebApp("resource-group", "webapp", runner=runner).apply_billing_settings(
            {"Billing__Stripe__SecretKey": "sk_test_private"}
        )

        self.assertEqual(0o600, observed["mode"])
        self.assertIn("sk_test_private", observed["payload"])
        self.assertNotIn("sk_test_private", " ".join(observed["command"]))
        self.assertFalse(observed["path"].exists())

    def test_delete_removes_only_named_settings_without_secret_values(self) -> None:
        observed: list[str] = []

        def runner(command: list[str], **kwargs: Any):
            class Result:
                returncode = 0
                stdout = ""
                stderr = ""

            observed.extend(command)
            return Result()

        AzureWebApp("resource-group", "webapp", runner=runner).delete_billing_settings(
            ["Billing__Stripe__SecretKey"]
        )

        self.assertIn("delete", observed)
        self.assertIn("Billing__Stripe__SecretKey", observed)
        self.assertNotIn("sk_test_private", " ".join(observed))


if __name__ == "__main__":
    unittest.main()
