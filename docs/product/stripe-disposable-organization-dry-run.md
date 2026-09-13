# Stripe disposable-organization dry run

For the [#384](https://github.com/valence-works/elsa-control/issues/384) Stripe trial-to-paid dry run, a `control_admin` creates a fresh named organization from the Console's **Administration → Organizations** entry. Leave **Owner account ID** blank to assign the current operator, or provide an existing account ID, then retain the returned organization and default workspace IDs for the dry-run evidence.

This path is the product-supported mint. Do not insert rows with SQL, reuse the existing `14cc1107…` organization, or grant the #312 `internal` entitlement. The new organization starts without an entitlement and must follow the normal Stripe checkout and projection path for the #384 proof.
