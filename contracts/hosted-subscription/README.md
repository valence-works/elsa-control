# Hosted subscription contracts

These JSON files lock the Hub-facing `HostedSubscriptionManagementResponse` and
`OrganizationBillingStatusResponse` shapes. `elsa-hub` `cloud_test.ts` should use
the same files. An accidental field, enum, or copy change fails CI.

Assumptions:

- Cloud billing portal is configured, so a linked owner gets `open-portal`.
- `source` is `hub` when a Hosted subscription row exists.
- `maxInstances` is the current commercial engine cap (`null` when no entitlement exists). Trial and the default paid allowance are 1.
- `activeDedicatedCount` is the number of non-deleted Dedicated engines; a drop in quantity never auto-deletes them.
- Organization id is the stable example `aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa`.
- No prices, secrets, or Stripe customer/subscription references.

Regenerate locally with
`ELSA_CONTROL_REGENERATE_HOSTED_SUBSCRIPTION_CONTRACTS=1` on the
`HostedSubscriptionContractTests` filter. Regeneration is refused in CI.
