#!/usr/bin/env python3
"""Focused contract tests for the staging Stripe reconciliation helper."""

from __future__ import annotations

import copy
import io
import os
import stat
import tempfile
import unittest
from contextlib import redirect_stderr, redirect_stdout
from pathlib import Path
from typing import Any, Mapping, Sequence
from unittest import mock

from scripts.staging_stripe_reconcile import (
    DEFAULT_WEBHOOK_EVENTS,
    KEY_VAULT_MODE,
    PRE_CUTOVER_MODE,
    SECRET_APP_SETTING_NAMES,
    AzureAppSetting,
    AzureWebApp,
    ReconciliationConfig,
    ReconciliationError,
    StripeApi,
    StagingStripeReconciler,
    bootstrap_webhook_secret,
    bootstrap_portal,
    idempotency_key,
    main,
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


KV_STRIPE_SECRET = "@Microsoft.KeyVault(SecretUri=https://ecapikvtest.vault.azure.net/secrets/Billing-Stripe-SecretKey)"
KV_STRIPE_WEBHOOK = "@Microsoft.KeyVault(SecretUri=https://ecapikvtest.vault.azure.net/secrets/Billing-Stripe-WebhookSigningSecret)"
KV_STRIPE_CONTROL = "@Microsoft.KeyVault(SecretUri=https://ecapikvtest.vault.azure.net/secrets/Billing-Stripe-WebhookSources-control-SigningSecret)"
KV_STRIPE_HUB = "@Microsoft.KeyVault(SecretUri=https://ecapikvtest.vault.azure.net/secrets/Billing-Stripe-WebhookSources-hub-SigningSecret)"


def azure_settings() -> list[dict[str, str]]:
    return [
        {"name": "Billing__Lifecycle__Enabled", "value": "true"},
        {"name": "Billing__Lifecycle__PollInterval", "value": "00:00:15"},
        {"name": "Billing__Stripe__Enabled", "value": "true"},
        {"name": "Billing__Stripe__SecretKey", "value": KV_STRIPE_SECRET},
        {"name": "Billing__Stripe__WebhookSigningSecret", "value": KV_STRIPE_WEBHOOK},
        {"name": "Billing__Stripe__WebhookSources__0__SigningSecret", "value": KV_STRIPE_CONTROL},
        {"name": "Billing__Stripe__WebhookSources__1__SigningSecret", "value": KV_STRIPE_HUB},
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
    def __init__(self, values: list[dict[str, str]] | None = None, *, resource_id: str = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/control-staging-rg/providers/Microsoft.Web/sites/control-staging-api") -> None:
        initial = azure_settings() if values is None else values
        self.values = {item["name"]: item["value"] for item in initial}
        self.applied: dict[str, str] | None = None
        self.applications: list[dict[str, str]] = []
        self.deleted: list[str] = []
        self._resource_id = resource_id

    def resource_id(self) -> str:
        return self._resource_id

    def list_names(self) -> list[str]:
        return list(self.values)

    def app_settings(self, names: list[str] | None = None) -> dict[str, str]:
        values = dict(self.values)
        if names is None:
            return values
        return {name: values[name] for name in names if name in values}

    def app_setting_records(self, names: list[str] | None = None) -> dict[str, AzureAppSetting]:
        records = {name: AzureAppSetting(value, False) for name, value in self.values.items()}
        if names is None:
            return records
        return {name: records[name] for name in names if name in records}

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
                "Billing__Lifecycle__Enabled",
                "Billing__Lifecycle__PollInterval",
                "Billing__Stripe__Enabled",
                "Billing__Stripe__DefaultPriceId",
                "Billing__Stripe__CloudPortalReturnUrl",
            }]
        )

        result = StagingStripeReconciler(FakeStripe(), azure, config()).run(
            apply_azure_settings=True,
            stripe_secret_key="sk_test_staging",
        )

        self.assertEqual("true", azure.applied["Billing__Lifecycle__Enabled"])
        self.assertEqual("00:00:15", azure.applied["Billing__Lifecycle__PollInterval"])
        self.assertEqual("true", azure.applied["Billing__Stripe__Enabled"])
        self.assertNotIn("Billing__Stripe__SecretKey", azure.applied)
        self.assertNotIn("Billing__Stripe__WebhookSigningSecret", azure.applied)
        self.assertEqual(PRICE_ID, azure.applied["Billing__Stripe__DefaultPriceId"])
        self.assertEqual(4, len(result))

    def test_audit_accepts_key_vault_references_instead_of_comparing_secret_values(self) -> None:
        result = StagingStripeReconciler(FakeStripe(), FakeAzure(), config()).run(
            stripe_secret_key="sk_test_different",
        )
        self.assertIn("azure-billing-settings", result)

    def test_approved_six_secret_inventory_passes_audit_and_apply(self) -> None:
        from key_vault_app_settings import STAGING_EXCLUDED_SECRET_APP_SETTING_NAMES, STAGING_SECRET_RESOURCE_GROUP_ID

        values = [item for item in azure_settings() if item["name"] not in STAGING_EXCLUDED_SECRET_APP_SETTING_NAMES]
        target = STAGING_SECRET_RESOURCE_GROUP_ID + "/providers/Microsoft.Web/sites/api-tud53zotij43k"
        for apply in (False, True):
            with self.subTest(apply=apply):
                azure = FakeAzure(values, resource_id=target)
                result = StagingStripeReconciler(FakeStripe(), azure, config()).run(
                    stripe_secret_key="sk_test_staging", apply_azure_settings=apply,
                )
                self.assertIn("azure-billing-settings", result)
                self.assertFalse(set(STAGING_EXCLUDED_SECRET_APP_SETTING_NAMES) & azure.values.keys())
                self.assertEqual(set(), written_secret_names(azure))

    def test_approved_inventory_refuses_excluded_secret_before_any_write(self) -> None:
        from key_vault_app_settings import STAGING_EXCLUDED_SECRET_APP_SETTING_NAMES, STAGING_SECRET_RESOURCE_GROUP_ID

        values = [item for item in azure_settings() if item["name"] not in STAGING_EXCLUDED_SECRET_APP_SETTING_NAMES]
        target = STAGING_SECRET_RESOURCE_GROUP_ID + "/providers/Microsoft.Web/sites/api-tud53zotij43k"
        for mode in (KEY_VAULT_MODE, PRE_CUTOVER_MODE):
            for name in STAGING_EXCLUDED_SECRET_APP_SETTING_NAMES:
                for value in (KV_STRIPE_CONTROL, "whsec_plainExcludedFixture"):
                    with self.subTest(mode=mode, name=name, value_kind="reference" if value.startswith("@") else "plain"):
                        azure = FakeAzure(values + [{"name": name, "value": value}], resource_id=target)
                        reconciler = StagingStripeReconciler(FakeStripe(), azure, config(), mode=mode)
                        with self.assertRaisesRegex(ReconciliationError, name + " must be absent"):
                            reconciler.run(stripe_secret_key="sk_test_staging", apply_azure_settings=True)
                        self.assertEqual([], azure.applications)
                        with self.assertRaisesRegex(ReconciliationError, name + " must be absent"):
                            reconciler.check_azure_settings()

    def test_approved_six_secret_inventory_passes_pre_cutover_audit_and_apply(self) -> None:
        from key_vault_app_settings import STAGING_EXCLUDED_SECRET_APP_SETTING_NAMES, STAGING_SECRET_RESOURCE_GROUP_ID

        values = [item for item in plain_azure_settings() if item["name"] not in STAGING_EXCLUDED_SECRET_APP_SETTING_NAMES]
        target = STAGING_SECRET_RESOURCE_GROUP_ID + "/providers/Microsoft.Web/sites/api-tud53zotij43k"
        for apply in (False, True):
            with self.subTest(apply=apply):
                azure = FakeAzure(values, resource_id=target)
                reconciler = StagingStripeReconciler(FakeStripe(), azure, config(webhook_secret=PLAIN_STAGING_WEBHOOK), mode=PRE_CUTOVER_MODE)
                result = reconciler.run(stripe_secret_key=PLAIN_STAGING_KEY, apply_azure_settings=apply)
                self.assertIn("azure-billing-settings", result)
                self.assertFalse(set(STAGING_EXCLUDED_SECRET_APP_SETTING_NAMES) & azure.values.keys())
                self.assertEqual(set(), written_secret_names(azure))

    def test_six_secret_inventory_remains_rejected_outside_approved_scope(self) -> None:
        from key_vault_app_settings import STAGING_EXCLUDED_SECRET_APP_SETTING_NAMES, STAGING_SECRET_RESOURCE_GROUP_ID

        values = [item for item in azure_settings() if item["name"] not in STAGING_EXCLUDED_SECRET_APP_SETTING_NAMES]
        for group in (
            STAGING_SECRET_RESOURCE_GROUP_ID.replace("staging", "prod"),
            STAGING_SECRET_RESOURCE_GROUP_ID.replace("8e23037a-420f-4ad0-9594-9d194de29e84", "00000000-0000-0000-0000-000000000000"),
        ):
            with self.subTest(group=group):
                azure = FakeAzure(values, resource_id=group + "/providers/Microsoft.Web/sites/api-fixture")
                with self.assertRaisesRegex(ReconciliationError, "must be a Key Vault reference"):
                    StagingStripeReconciler(FakeStripe(), azure, config()).run(stripe_secret_key="sk_test_staging")

    def test_audit_requires_staging_billing_cleanup_worker(self) -> None:
        azure = FakeAzure([item for item in azure_settings() if item["name"] != "Billing__Lifecycle__Enabled"])

        with self.assertRaisesRegex(ReconciliationError, "Billing__Lifecycle__Enabled does not match"):
            StagingStripeReconciler(FakeStripe(), azure, config()).run(
                stripe_secret_key="sk_test_staging",
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
            def app_setting_records(self, names: list[str] | None = None) -> dict[str, AzureAppSetting]:
                records = super().app_setting_records(names)
                if "Billing__Stripe__Enabled" in records:
                    records["Billing__Stripe__Enabled"] = AzureAppSetting(
                        records["Billing__Stripe__Enabled"].value,
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
            if item["name"] != "Billing__Stripe__CloudPortalReturnUrl"
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
            ["Billing__Stripe__CloudPortalReturnUrl"],
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

    def test_environment_config_accepts_the_stripe_checkout_session_query_placeholder(self) -> None:
        values = {
            "STRIPE_HOSTED_PRICE_ID": PRICE_ID,
            "STRIPE_WEBHOOK_SECRET": WEBHOOK_SECRET,
            "CONTROL_STAGING_WEBHOOK_URL": WEBHOOK_URL,
            "CLOUD_PORTAL_RETURN_URL": CLOUD_RETURN_URL,
            "AZURE_RESOURCE_GROUP": "staging-rg",
            "AZURE_WEBAPP_NAME": "staging-app",
            "AZURE_EXPECTED_CHECKOUT_SUCCESS_URL": (
                "https://cloud-staging.azurestaticapps.net/checkout/return"
                "?session_id={CHECKOUT_SESSION_ID}"
            ),
            "AZURE_EXPECTED_CHECKOUT_CANCEL_URL": (
                "https://cloud-staging.azurestaticapps.net/dashboard/billing"
            ),
        }

        parsed = ReconciliationConfig.from_environment(values)

        self.assertEqual(
            values["AZURE_EXPECTED_CHECKOUT_SUCCESS_URL"],
            parsed.expected_checkout_success_url,
        )

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

        with self.assertRaisesRegex(ReconciliationError, "must be a Key Vault reference") as raised:
            StagingStripeReconciler(FakeStripe(), FakeAzure(values), config()).check_azure_settings()
        self.assertNotIn("sk_live_must_fail", str(raised.exception))

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
    def test_inventory_scope_uses_verified_site_id_and_caches_metadata_read(self) -> None:
        target = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/resource-group/providers/Microsoft.Web/sites/webapp"
        runner = mock.Mock(return_value=mock.Mock(returncode=0, stdout=target + "\n"))
        azure = AzureWebApp("resource-group", "webapp", runner=runner)
        self.assertEqual(target, azure.resource_id())
        self.assertEqual(target, azure.resource_id())
        runner.assert_called_once()
        command = runner.call_args.args[0]
        self.assertEqual(["az", "webapp", "show"], command[:3])
        self.assertEqual("id", command[command.index("--query") + 1])

    def test_inventory_scope_refuses_mismatched_or_malformed_site_id(self) -> None:
        targets = (
            "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/other-group/providers/Microsoft.Web/sites/webapp",
            "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/resource-group/providers/Microsoft.Web/sites/other-app",
            "invalid-site-id",
        )
        for target in targets:
            with self.subTest(target=target):
                runner = mock.Mock(return_value=mock.Mock(returncode=0, stdout=target))
                with self.assertRaisesRegex(ReconciliationError, "does not match the requested target"):
                    AzureWebApp("resource-group", "webapp", runner=runner).resource_id()

    def test_azure_cli_failure_is_sanitized(self) -> None:
        def runner(*args: Any, **kwargs: Any):
            class Result:
                returncode = 1
                stdout = ""
                stderr = "provider-id-and-secret-must-not-escape"

            return Result()

        with self.assertRaisesRegex(ReconciliationError, "Azure CLI request failed"):
            AzureWebApp("resource-group", "webapp", runner=runner).app_settings(
                ["Billing__Stripe__SecretKey"]
            )

    def test_app_setting_records_preserve_slot_metadata(self) -> None:
        def runner(command: list[str], **kwargs: Any):
            query = command[command.index("--query") + 1] if "--query" in command else ""

            class Result:
                returncode = 0
                stdout = (
                    "Billing__Stripe__SecretKey\n"
                    if query == "[].name"
                    else '[{"name":"Billing__Stripe__SecretKey","value":"sk_test_safe","slotSetting":true}]'
                )
                stderr = ""

            return Result()

        records = AzureWebApp("resource-group", "webapp", runner=runner).app_setting_records(
            ["Billing__Stripe__SecretKey"]
        )

        self.assertEqual("sk_test_safe", records["Billing__Stripe__SecretKey"].value)
        self.assertTrue(records["Billing__Stripe__SecretKey"].slot_setting)

    def test_app_setting_records_require_exact_names(self) -> None:
        with self.assertRaisesRegex(ReconciliationError, "exact setting names"):
            AzureWebApp("resource-group", "webapp").app_setting_records()

    def test_list_names_requests_names_only(self) -> None:
        observed: list[str] = []

        def runner(command: list[str], **kwargs: Any):
            class Result:
                returncode = 0
                stdout = "Billing__Stripe__Enabled\nBilling__Stripe__SecretKey\n"
                stderr = ""

            observed.extend(command)
            return Result()

        names = AzureWebApp("resource-group", "webapp", runner=runner).list_names()

        self.assertEqual(["Billing__Stripe__Enabled", "Billing__Stripe__SecretKey"], names)
        self.assertIn("[].name", observed)
        self.assertNotIn("[].value", " ".join(observed))

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


PLAIN_STAGING_KEY = "sk_test_plainStagingFixture"
PLAIN_STAGING_WEBHOOK = "whsec_plainStagingFixture"
PLAIN_STAGING_SECRETS = {
    "Billing__Stripe__SecretKey": PLAIN_STAGING_KEY,
    "Billing__Stripe__WebhookSigningSecret": PLAIN_STAGING_WEBHOOK,
    "Billing__Stripe__WebhookSources__0__SigningSecret": "whsec_plainSourceZeroFixture",
    "Billing__Stripe__WebhookSources__1__SigningSecret": "whsec_plainSourceOneFixture",
    "Authentication__ApiKey": "plain-staging-admin-key-fixture",
}
OTHER_PLAIN_STAGING_KEY = "sk_test_otherStagingFixture"
LIVE_PLAIN_STAGING_KEY = "sk_live_plainStagingFixture"
OTHER_PLAIN_STAGING_WEBHOOK = "whsec_otherStagingFixture"
STAGING_FIXTURE_SECRETS = (
    *PLAIN_STAGING_SECRETS.values(),
    OTHER_PLAIN_STAGING_KEY,
    LIVE_PLAIN_STAGING_KEY,
    OTHER_PLAIN_STAGING_WEBHOOK,
    KV_STRIPE_SECRET,
    KV_STRIPE_WEBHOOK,
)


def plain_azure_settings(**overrides: str) -> list[dict[str, str]]:
    """The staging site before the manual reference switch."""

    values = {item["name"]: item["value"] for item in azure_settings()}
    values.update(PLAIN_STAGING_SECRETS)
    values.update(overrides)
    return [{"name": name, "value": value} for name, value in values.items()]


def mixed_azure_settings() -> list[dict[str, str]]:
    return plain_azure_settings(Billing__Stripe__SecretKey=KV_STRIPE_SECRET)


def staging_environment(**overrides: str) -> dict[str, str]:
    values = {
        "STRIPE_SECRET_KEY": PLAIN_STAGING_KEY,
        "STRIPE_WEBHOOK_SECRET": PLAIN_STAGING_WEBHOOK,
        "STRIPE_HOSTED_PRICE_ID": PRICE_ID,
        "CONTROL_STAGING_WEBHOOK_URL": WEBHOOK_URL,
        "CLOUD_PORTAL_RETURN_URL": CLOUD_RETURN_URL,
        "AZURE_RESOURCE_GROUP": "control-staging-rg",
        "AZURE_WEBAPP_NAME": "control-staging-api",
    }
    values.update(overrides)
    return values


def written_secret_names(azure: FakeAzure) -> set[str]:
    return {name for payload in azure.applications for name in payload if name in SECRET_APP_SETTING_NAMES}


class StagingKeyVaultModeTests(unittest.TestCase):
    """Pre-cutover and Key Vault modes for the staging reconcile (#865)."""

    def assert_secret_free(self, text: str) -> None:
        for value in STAGING_FIXTURE_SECRETS:
            self.assertNotIn(value, text)
        self.assertNotIn("SecretUri", text)

    def _run_main(
        self,
        azure: FakeAzure,
        *,
        secrets_written: str | None,
        argv: Sequence[str] = ("--apply-azure-settings",),
        **env: str,
    ) -> tuple[int, str, str]:
        environment = {
            name: value
            for name, value in os.environ.items()
            if name != "API_KEY_VAULT_SECRETS_WRITTEN" and not name.startswith(("STRIPE_", "AZURE_", "CONTROL_STAGING_"))
        }
        environment.update(staging_environment(**env))
        if secrets_written is not None:
            environment["API_KEY_VAULT_SECRETS_WRITTEN"] = secrets_written
        stdout = io.StringIO()
        stderr = io.StringIO()
        with (
            mock.patch("scripts.staging_stripe_reconcile.StripeApi", lambda *args, **kwargs: FakeStripe()),
            mock.patch("scripts.staging_stripe_reconcile.AzureWebApp", lambda *args, **kwargs: azure),
            mock.patch.dict(os.environ, environment, clear=True),
            redirect_stdout(stdout),
            redirect_stderr(stderr),
        ):
            code = main(list(argv))
        self.assert_secret_free(stdout.getvalue() + stderr.getvalue())
        return code, stdout.getvalue(), stderr.getvalue()

    def _refusal(self, run) -> str:
        stdout = io.StringIO()
        stderr = io.StringIO()
        with redirect_stdout(stdout), redirect_stderr(stderr):
            with self.assertRaises(ReconciliationError) as raised:
                run()
        reason = str(raised.exception)
        self.assert_secret_free(reason + stdout.getvalue() + stderr.getvalue())
        return reason

    def test_pre_cutover_apply_passes_the_pre_863_checks_and_writes_no_secret_setting(self) -> None:
        azure = FakeAzure([
            item for item in plain_azure_settings()
            if item["name"] not in {"Billing__Lifecycle__Enabled", "Billing__Stripe__DefaultPriceId"}
        ])

        code, stdout, stderr = self._run_main(azure, secrets_written=None)

        self.assertEqual(0, code, stderr)
        self.assertEqual(
            "INFO key-vault-mode: pre-cutover (classification: plain)\n"
            "PASS stripe-price\nPASS control-webhook\nPASS customer-portal\nPASS azure-billing-settings\n",
            stdout,
        )
        self.assertEqual("", stderr)
        self.assertEqual(1, len(azure.applications))
        self.assertEqual(set(), written_secret_names(azure))
        self.assertEqual(PRICE_ID, azure.applied["Billing__Stripe__DefaultPriceId"])
        for name, value in PLAIN_STAGING_SECRETS.items():
            self.assertEqual(value, azure.values[name])

    def test_pre_cutover_read_only_audit_checks_the_plain_values(self) -> None:
        result = StagingStripeReconciler(
            FakeStripe(),
            FakeAzure(plain_azure_settings()),
            config(webhook_secret=PLAIN_STAGING_WEBHOOK),
            mode=PRE_CUTOVER_MODE,
        ).run(stripe_secret_key=PLAIN_STAGING_KEY)

        self.assertIn("azure-billing-settings", result)

    def test_pre_cutover_rejects_a_site_key_that_is_not_the_audited_test_key(self) -> None:
        azure = FakeAzure([
            item for item in plain_azure_settings()
            if item["name"] != "Billing__Stripe__DefaultPriceId"
        ])

        reason = self._refusal(
            lambda: StagingStripeReconciler(
                FakeStripe(),
                azure,
                config(webhook_secret=PLAIN_STAGING_WEBHOOK),
                mode=PRE_CUTOVER_MODE,
            ).run(apply_azure_settings=True, stripe_secret_key=OTHER_PLAIN_STAGING_KEY)
        )

        self.assertEqual("Azure Stripe secret setting does not match the audited test account", reason)
        self.assertEqual(set(), written_secret_names(azure))
        self.assertNotIn("Billing__Stripe__DefaultPriceId", azure.values)
        self.assertEqual(PLAIN_STAGING_KEY, azure.values["Billing__Stripe__SecretKey"])

    def test_pre_cutover_rejects_a_live_site_key_before_any_write(self) -> None:
        azure = FakeAzure(plain_azure_settings(Billing__Stripe__SecretKey=LIVE_PLAIN_STAGING_KEY))

        reason = self._refusal(
            lambda: StagingStripeReconciler(
                FakeStripe(),
                azure,
                config(webhook_secret=PLAIN_STAGING_WEBHOOK),
                mode=PRE_CUTOVER_MODE,
            ).run(apply_azure_settings=True, stripe_secret_key=PLAIN_STAGING_KEY)
        )

        self.assertEqual("Azure staging is not configured with a test Stripe account", reason)
        self.assertEqual([], azure.applications)

    def test_pre_cutover_requires_the_site_webhook_secret_to_match(self) -> None:
        azure = FakeAzure(plain_azure_settings(Billing__Stripe__WebhookSigningSecret=OTHER_PLAIN_STAGING_WEBHOOK))

        reason = self._refusal(
            lambda: StagingStripeReconciler(
                FakeStripe(),
                azure,
                config(webhook_secret=PLAIN_STAGING_WEBHOOK),
                mode=PRE_CUTOVER_MODE,
            ).run(apply_azure_settings=True, stripe_secret_key=PLAIN_STAGING_KEY)
        )

        self.assertEqual("Azure billing setting Billing__Stripe__WebhookSigningSecret does not match", reason)
        self.assertEqual(set(), written_secret_names(azure))
        self.assertEqual(OTHER_PLAIN_STAGING_WEBHOOK, azure.values["Billing__Stripe__WebhookSigningSecret"])

    def test_references_without_the_variable_fail_before_any_write_and_name_it(self) -> None:
        azure = FakeAzure()

        code, stdout, stderr = self._run_main(azure, secrets_written=None)

        self.assertEqual(1, code)
        self.assertEqual("", stdout)
        self.assertIn("FAIL key-vault-mode: Key Vault references are present on", stderr)
        self.assertIn("classification: reference", stderr)
        self.assertIn("API_KEY_VAULT_SECRETS_WRITTEN=true", stderr)
        self.assertEqual([], azure.applications)

    def test_a_value_other_than_true_is_not_the_variable(self) -> None:
        azure = FakeAzure()

        code, _, stderr = self._run_main(azure, secrets_written="yes")

        self.assertEqual(1, code)
        self.assertIn("API_KEY_VAULT_SECRETS_WRITTEN=true", stderr)
        self.assertEqual([], azure.applications)

    def test_mixed_state_fails_without_the_variable_naming_references_and_plain_settings(self) -> None:
        azure = FakeAzure(mixed_azure_settings())

        code, stdout, stderr = self._run_main(azure, secrets_written=None)

        self.assertEqual(1, code)
        self.assertEqual("", stdout)
        self.assertIn("mixed", stderr)
        self.assertIn("references: Billing__Stripe__SecretKey;", stderr)
        self.assertIn(
            "plain: Authentication__ApiKey, Billing__Stripe__WebhookSigningSecret, "
            "Billing__Stripe__WebhookSources__0__SigningSecret, Billing__Stripe__WebhookSources__1__SigningSecret",
            stderr,
        )
        self.assertIn("API_KEY_VAULT_SECRETS_WRITTEN=true", stderr)
        self.assertEqual([], azure.applications)

    def test_mixed_state_with_the_variable_fails_as_863_does(self) -> None:
        azure = FakeAzure(mixed_azure_settings())

        code, stdout, stderr = self._run_main(azure, secrets_written="true")
        self.assertEqual(1, code)
        self.assertEqual("FAIL staging-stripe-reconciliation\n", stderr)
        self.assertEqual([], azure.applications)

        reason = self._refusal(
            lambda: StagingStripeReconciler(FakeStripe(), azure, config()).run(
                apply_azure_settings=True,
                stripe_secret_key=PLAIN_STAGING_KEY,
            )
        )
        self.assertEqual("Azure billing setting Billing__Stripe__WebhookSigningSecret must be a Key Vault reference", reason)
        self.assertEqual([], azure.applications)

    def test_plain_values_with_the_variable_fail_as_863_does(self) -> None:
        azure = FakeAzure(plain_azure_settings())

        code, stdout, stderr = self._run_main(azure, secrets_written="true")
        self.assertEqual(1, code)
        self.assertEqual("", stdout)
        self.assertEqual("FAIL staging-stripe-reconciliation\n", stderr)
        self.assertEqual([], azure.applications)

        reason = self._refusal(
            lambda: StagingStripeReconciler(FakeStripe(), azure, config()).run(
                apply_azure_settings=True,
                stripe_secret_key=PLAIN_STAGING_KEY,
            )
        )
        self.assertEqual("Azure billing setting Billing__Stripe__SecretKey must be a Key Vault reference", reason)
        reason = self._refusal(
            lambda: StagingStripeReconciler(FakeStripe(), azure, config()).check_azure_settings()
        )
        self.assertEqual("Azure billing setting Billing__Stripe__SecretKey must be a Key Vault reference", reason)

    def test_references_with_the_variable_in_any_case_take_the_863_path(self) -> None:
        azure = FakeAzure([
            item for item in azure_settings()
            if item["name"] != "Billing__Stripe__DefaultPriceId"
        ])

        code, stdout, stderr = self._run_main(azure, secrets_written="TRUE")

        self.assertEqual(0, code, stderr)
        self.assertNotIn("pre-cutover", stdout)
        self.assertEqual(
            "PASS stripe-price\nPASS control-webhook\nPASS customer-portal\nPASS azure-billing-settings\n",
            stdout,
        )
        self.assertEqual(set(), written_secret_names(azure))

    def test_key_vault_mode_refuses_a_plain_non_stripe_secret_setting(self) -> None:
        # The Stripe names are references, so the #863 checks pass; a plain
        # Authentication__ApiKey must still fail the staging apply (#865 AC3).
        plain_api_key = "plain-staging-api-key-fixture"
        azure = FakeAzure([*azure_settings(), {"name": "Authentication__ApiKey", "value": plain_api_key}])

        code, stdout, stderr = self._run_main(azure, secrets_written="true")

        self.assertEqual(1, code)
        self.assertEqual("", stdout)
        self.assertIn("Authentication__ApiKey", stderr)
        self.assertIn("API_KEY_VAULT_SECRETS_WRITTEN=true", stderr)
        self.assertIn("Billing__Stripe__SecretKey", stderr)
        self.assertNotIn(plain_api_key, stderr)
        self.assertEqual([], azure.applications)

    def test_key_vault_mode_read_only_check_refuses_a_plain_non_stripe_secret_setting(self) -> None:
        plain_api_key = "plain-staging-api-key-fixture"
        azure = FakeAzure([*azure_settings(), {"name": "Authentication__ApiKey", "value": plain_api_key}])

        code, stdout, stderr = self._run_main(azure, secrets_written="true", argv=())

        self.assertEqual(1, code)
        self.assertEqual("", stdout)
        self.assertIn("Authentication__ApiKey", stderr)
        self.assertNotIn(plain_api_key, stderr)

    def test_key_vault_mode_refuses_a_malformed_non_stripe_reference(self) -> None:
        versioned = "@Microsoft.KeyVault(SecretUri=https://ecapikvtest.vault.azure.net/secrets/Authentication-ApiKey/0123456789abcdef)"
        azure = FakeAzure([*azure_settings(), {"name": "Authentication__ApiKey", "value": versioned}])

        code, stdout, stderr = self._run_main(azure, secrets_written="true")

        self.assertEqual(1, code)
        self.assertEqual("", stdout)
        self.assertIn("malformed Key Vault reference: Authentication__ApiKey", stderr)
        self.assertEqual([], azure.applications)

    def test_key_vault_mode_names_a_malformed_reference_apart_from_plain_settings(self) -> None:
        versioned = "@Microsoft.KeyVault(SecretUri=https://ecapikvtest.vault.azure.net/secrets/Authentication-ApiKey/0123456789abcdef)"
        azure = FakeAzure(
            [
                *azure_settings(),
                {"name": "Authentication__ApiKey", "value": versioned},
                {"name": "Authentication__BuilderClientApiKey", "value": "plain-builder-key-fixture"},
            ]
        )

        code, _, stderr = self._run_main(azure, secrets_written="true")

        self.assertEqual(1, code)
        self.assertIn("plain: Authentication__BuilderClientApiKey; malformed: Authentication__ApiKey)", stderr)
        self.assertNotIn("references: Authentication__ApiKey", stderr)
        self.assertNotIn("plain-builder-key-fixture", stderr)
        self.assertEqual([], azure.applications)

    def test_key_vault_mode_passes_when_every_listed_setting_is_a_reference(self) -> None:
        api_key_reference = "@Microsoft.KeyVault(SecretUri=https://ecapikvtest.vault.azure.net/secrets/Authentication-ApiKey)"
        azure = FakeAzure([*azure_settings(), {"name": "Authentication__ApiKey", "value": api_key_reference}])

        code, stdout, stderr = self._run_main(azure, secrets_written="true")

        self.assertEqual(0, code, stderr)
        self.assertEqual(set(), written_secret_names(azure))


if __name__ == "__main__":
    unittest.main()
