# Hosted subscription management

Authenticated Elsa Cloud customers manage Hosted billing through Control, not
the Control Console. The Cloud BFF calls these customer routes and keeps Stripe
secrets and raw payment data on the server.

## Control contract

| Route | Purpose |
| --- | --- |
| `GET /api/organizations/{organizationId}/billing/hosted-subscription` | Truthful Hosted billing state, portal availability, and copy hooks. |
| `POST /api/organizations/{organizationId}/billing/hosted-portal` | Creates a Stripe Customer Portal session scoped to the caller's billing customer. |

Both routes accept Cloud BFF and Cloud account tokens. Workspace membership
still applies: another account receives `organization.not-found`. Portal
creation additionally requires `ManageBilling` (Owner, Administrator, or
BillingAdmin).

The Control Console checkout/portal routes remain operator/org-admin surfaces.
Do not add Hosted subscription-management UI there.

## Copy hooks

The Hosted status response and engine delete-confirmation response keep these
actions distinct:

- Engine deletion leaves the Hosted subscription active.
- Subscription cancellation ends access and billing at period end and does not
  immediately delete the managed engine.
- Missing Stripe customer linkage is an explicit `no-billing-linkage` state,
  not a silent redirect to public pricing.

## Return URL

`Billing:Stripe:CloudPortalReturnUrl` must be an HTTPS Elsa Cloud dashboard URL
with no userinfo, query, or fragment. Production uses
`https://elsacloud.app/dashboard`. An optional request `returnUrl` is accepted
as either an absolute URL on that configured origin or an absolute path such as
`/dashboard/billing`. In both forms it must stay under the configured dashboard
path and cannot contain a query or fragment.

## Stripe sandbox

Live rehearsal uses the existing Lovable-connected Stripe sandbox. Enable
Customer Portal there with subscription cancel-at-period-end. Use Checkout,
Customer Portal sessions, tokens, or test clocks only. Do not create a separate
Stripe account and do not send full card numbers or raw PANs.
