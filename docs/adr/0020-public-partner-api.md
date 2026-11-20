# ADR-0020: A Public Partner API for Programmatic Engine Provisioning

## Status

Proposed. Design only. It implements nothing and creates no Azure or Stripe resource.
It builds on [ADR-0017](0017-one-placement-record-for-dedicated-byo-and-shared.md) and
[ADR-0019](0019-shared-hosted-engine-tenancy-model.md), and it follows the surface rule
of [ADR-0018](0018-elsa-hub-is-the-customer-front-door.md).

## Date

2026-11-20

## Context

Platforms that sell workflow features to their own customers (for example a WordPress
plugin vendor) want to create, update and delete Elsa engines from their own servers, and
to onboard as partners without talking to us. Today every lifecycle call goes through a
human session on the Hub BFF. There is no machine identity, no partner concept, no
webhook and no public contract.

### What exists today (cited)

- **The lifecycle is already one service.** `ElsaInstanceLifecycleService` behind
  `/api/workspaces/{workspaceId}/instances` handles create, read, list, operations and
  delete, with `Idempotency-Key`, strong `If-Match`, a two-step delete
  confirmation and the commercial gate
  (`src/Hosting/ElsaControl.Api/Workspace/ManagedElsaInstanceEndpoints.cs`).
- **Operations are typed.** `ElsaInstanceOperationAction` has Create, Reconcile,
  UpdateIntent, Start, Stop, Restart, ApproveMinorUpgrade, MajorMigration, Delete, Retry
  and Recover
  (`src/Deployment/ElsaControl.Deployment.Abstractions/Instances/ElsaInstanceStateMachine.cs`).
- **Admission is commercial and typed.** `instance_limit_reached` (422, Dedicated
  `MaxInstances`), `placement.shared-allowance.exceeded` (422, Shared `MaxSharedTenants`)
  and `rate-limit.exceeded` (429) already exist
  (`ElsaInstanceCommercialGate.cs`, `ManagedElsaInstanceLifecycleRateLimiting.cs`).
- **Studio sign-in is a one-time handoff.** `POST /api/managed-elsa/handoff/issue` and
  `/redeem` mint a short-lived ticket for the engine audience, and the Studio grant set is
  fixed by `contracts/managed-elsa-studio-grants-v2.json`. Both routes are on the Hub and
  Cloud BFF allowlists only.
- **Hosted billing is organization-level with a quantity.** A Hosted subscription row,
  Stripe webhook projection and the Retained/Suspended/Canceled rules are in #960, #964
  and #966, with public golden files in `contracts/hosted-subscription/`.
- **Authentication has no machine scheme.** Program.cs registers JWT bearer for Control,
  Cloud and Hub identities, and a customer cookie. All are human sessions.

## Decision

1. **A partner is an organization with `PartnerStatus`.** It is not a new tenant model.
   The organization is the billing and quota boundary (ADR-0006, ADR-0017 F5). Statuses are
   `Pending`, `Active`, `Suspended` and `Closed`. `Active` has full access to the routes
   its key scopes allow. `Suspended` is read-only and may call only these verbs:
   `GET /account`, `GET /releases`, `GET /engines`, `GET /engines/{id}`,
   `GET /engines/{id}/operations/{opId}` and `GET /webhook-endpoints` (webhook deliveries
   for existing endpoints continue). Every other verb, including delete, Studio sessions,
   API tokens and webhook changes, returns 403 `partner.not-active`. `Pending` and
   `Closed` get nothing: every machine route returns 403 `partner.not-active`, and closing
   a partner revokes all its keys.
2. **The partner API is a separate, thin public surface at `/partner/v1`.** It is a facade
   over the same application services as the workspace routes. It does not reuse
   `/api/workspaces/...` routes, does not go through the Hub or Cloud BFF, and adds no
   second lifecycle path. It has its own authentication scheme, authorization policy,
   rate-limit policies and OpenAPI document.
3. **Credentials are Control-native API keys in the first slice, and the Elsa Hub is the only place a human creates and manages them.** Control is the source of truth for partner identities, keys and scopes. The Hub stores no key secret. See Identity and Key management through the Hub.
4. **One resource kind, `engine`, with a `placement` field.** `placement` is
   `dedicated` in v1. `shared` is reserved in the schema and refused with
   `placement.unavailable` until ADR-0019's L6 flag (#945) opens it.
5. **Billing is one Stripe customer per partner organization, one subscription, one item
   per engine type, quantity = the engine allowance.** The first slice is prepaid
   allowance only. Auto-scaling quantity is deferred.
6. **Webhooks are signed and at-least-once, from a transactional outbox.** `GET` stays the
   source of truth.
7. **Isolation is derived from the credential only.** The partner organization is never
   read from a path, query or body.

### Identity and self-serve onboarding

- **Sign-up.** A person signs in on the Hub (Supabase identity, ADR-0018), opens the
  Partner page in the signed-in section, and creates a partner organization. The Hub's
  backend (the Supabase edge functions on the cloud-bff path) calls purpose-built Control
  endpoints on the Hub BFF allowlist. These are human-session endpoints, separate from the
  machine API.
- **Terms.** Acceptance is an immutable record: terms version, timestamp, accepting
  account id, and the organization. The terms text is a legal document owned by Sipke and
  counsel and is not in this repository. `PartnerStatus` moves to `Active` only when the
  current terms version is accepted and a Hosted subscription is active. A new terms
  version sets a grace flag, not an instant suspension.
- **Keys.** Format `ecp_<env>_<keyId>_<secret>`, with at least 256 bits of random secret
  shown once. Control stores `keyId`, the SHA-256 of the secret (high entropy, so no
  salt is needed), scopes, creation time, optional expiry, last-used time and created-by.
  A lookup is by `keyId` and a constant-time compare. Keys are sent as
  `Authorization: Bearer`.
- **Rotation and revocation.** At most two active keys per partner, so rotation is
  create-new, deploy, revoke-old. Revocation takes effect immediately: the key
  store invalidates the cache entry on revoke, in every instance, so there is no accept
  window after revoke. A cache entry for a valid key may live at most 30 seconds, and
  only so that the invalidation is a safety net, not the mechanism. A test revokes a key
  and proves the next request on every instance gets 401. Revoking a key never touches
  engines, and it invalidates the engine tokens that key minted (see API token).
- **Scopes.** `engines:read`, `engines:write`, `engines:delete`, `releases:apply`,
  `studio:sessions`, `webhooks:manage`. Delete and apply are separate scopes so a
  read-and-create key cannot destroy or change an engine. A key can never manage keys,
  terms, billing or permissions. Those need a human session on the Hub.
- **Principal.** The handler produces a service principal bound to the partner
  organization with a bounded `PartnerProvisioner` role evaluated by the existing
  `WorkspacePermissionService`. It cannot be granted Owner.
- **Failed authentication.** Unknown or revoked keys return the same 401 body.
  Failed attempts are throttled per source IP with the existing low-permit fallback
  bucket. Key secrets never appear in logs, problem bodies or audit text, and the audit
  records `keyId` only.

### Key management through the Hub

Partners create, list, rotate and revoke keys in the Hub's signed-in Partner page, the
same way the Hub fronts engine management today (ADR-0018). The Hub is the presentation
and identity layer. Control owns everything else.

- **Control is the source of truth.** Partner identities, keys, scopes, expiry and
  last-used time live in Control only. The Hub keeps no copy of any of them, not even a
  key id, and reads the list from Control on each page load.
- **The Hub never stores a secret.** `POST /api/organizations/{organizationId}/partner/keys`
  returns the full key once in the response. The Hub edge function passes that body to the
  browser and keeps nothing: no database row, no cache, no log line, no analytics event,
  no error report with the body. The browser shows the secret once with a copy button and
  drops it when the dialog closes. Control keeps only the SHA-256 hash, and neither side can
  show a secret again. A lost key is revoked and replaced.
- **Endpoints (human session, Hub BFF allowlist only):** create, list (no secrets, with
  key id, name, scopes, created, expires, last used, state), rotate (creates the new key
  and returns it once, leaving the old one active until the partner revokes it), and
  revoke. Plus `GET .../partner/usage` for the usage view below. Each has its own
  authorization and tests. None is on any machine surface, and none is reachable with an
  API key, so a stolen key can't mint more keys.
- **Authorization.** Control authorizes the Hub session's linked account (#851) as an
  Owner or Admin of the partner organization. A signed-in caller who is not a member of the
  partner organization, including the owner of another organization, gets 404 on every
  partner key route. A member without the Owner or Admin role gets 403. The Hub checks
  nothing that Control doesn't also check. Responses carry `Cache-Control: no-store`. The create and rotate routes use
  the existing identity-and-IP rate limit, and the secret never appears in a URL, a query
  string or a header the Hub forwards or logs.
- **Hub edge function rules.** The create and rotate functions redact response bodies in
  logs and error reports, return `no-store`, never write the body to storage or to a queue,
  and use a short timeout with no retry (a retry would mint a second key). The Hub's
  contract tests include a check that the secret does not appear in any log fixture.
- **Partner usage view (cheap version).** `GET .../partner/usage` returns the allowance and
  current use per engine type (from the commercial gate), engine counts by public state,
  and per-key last-used time. Request volume per key needs metering and is deferred. The
  Hub renders it as read-only numbers and never computes them.
- **Audit.** Control records key created, rotated and revoked with the acting account id and
  the key id, and never the secret.

### Isolation: end customers map to engines

- A partner's end customer is represented by an opaque `externalReference` (up to 128
  characters, unique per partner) on the engine. Control stores no end-customer personal
  data. The partner is the controller of that data and Valence is the processor, which
  the terms and a data processing addendum must say. This is a legal question for Sipke,
  not an engineering one.
- First slice: all of a partner's engines live in one partner workspace created with the
  organization. Workspace-per-end-customer is deferred, because it adds a workspace
  lifecycle and no isolation the engine boundary doesn't already give: every Dedicated
  engine has its own resources and identity, and ADR-0019 gives every Shared tenant its
  own shell and database.
- Every query filters by the organization from the credential. A cross-partner id returns
  404, never 403, so ids can't be probed. There is no endpoint that lists partners or
  accepts an organization id. Webhook endpoints, keys and events are partner-scoped the
  same way. So are `Idempotency-Key` values and pagination tokens: an idempotency record is
  keyed by the partner and the key, so the same key from two partners never collides, and a
  pagination token is bound to the partner that received it, so a token presented by another
  partner returns 404 and never a page.
- ADR-0019's tenantId stays opaque and is never an input to the partner API.

### The `/partner/v1` API

| Route | Maps to | Notes |
| --- | --- | --- |
| `GET /account` | organization, allowance, status | Allowance and usage per engine type; no prices. |
| `GET /releases` | governed catalog (`onboarding-options`) | Same Supported selector as onboarding-options: NuGet semver descending, `recommended` on the newest, `ManagedElsa:ReleasePicker:MaxChoices` cap, no Preview. |
| `POST /engines` | Create | `Idempotency-Key` required. Body: `externalReference`, `name`, `placement`, `releaseId`. Returns 202 with the engine and its operation. |
| `GET /engines`, `GET /engines/{id}` | list, read | Filter by `externalReference`. Page size capped. Response carries `version` and an `ETag`. |
| `GET /engines/{id}/operations/{opId}` | operation read | Public state enum below. |
| `POST /engines/{id}/release-applications` | `ApproveMinorUpgrade` | `If-Match` and `Idempotency-Key` required. The target must come from `GET /releases`. |
| `DELETE /engines/{id}` | Delete | `If-Match` and `Idempotency-Key` required, scope `engines:delete`. See below. |
| `POST /engines/{id}/studio-sessions` | handoff issue | Returns a one-time Studio URL. |
| `POST /engines/{id}/api-tokens` | handoff issue, API audience | Returns a short-lived engine bearer token. |
| `/webhook-endpoints` | webhooks | CRUD, secret rotation, replay. |

- **Public engine states** are `provisioning`, `ready`, `updating`, `stopped`, `failed`,
  `deleting`, `deleted` and `suspended`. They are mapped from the existing lifecycle and
  billing states. Provider identifiers, subscription ids, resource groups and assignment
  ids are never exposed (ADR-0017 keeps the assignment opaque).
- **Delete.** The human two-step confirmation exists to protect a click. An API key is a
  deliberate act, so v1 replaces it with the separate scope plus `If-Match` and a required
  `X-Confirm-Delete: <engine id>` header. Delete follows the existing semantics and
  retention rules (ADR-0014). The partner API adds no faster or harsher path.
- **Apply release** is minor upgrades inside the governed catalog only. `MajorMigration`,
  Recover and Retry stay operator-only. A partner sees a failed operation and a
  `support` contact, not a recovery verb.
- **Errors** are RFC 9457 problem bodies with the existing `code` values. A client switches
  on `code` only: `instance_limit_reached`, `placement.shared-allowance.exceeded`,
  `rate-limit.exceeded`, `instance.operation-active`, `instance.slug-conflict`,
  `instance.version-conflict`, `placement.unavailable`. New codes are additive.
- **Versioning.** The version is in the path. Within `v1`, only additive changes are
  allowed: new optional fields, new enum values clients must tolerate, new routes. A
  breaking change is `v2`, served beside `v1` for at least 12 months with `Deprecation`
  and `Sunset` headers.

### Studio and API endpoint handoff

- An engine response carries `studioUrl` and `apiBaseUrl`, both Control-derived. For a
  Dedicated engine they are the engine's own hostname. For a Shared tenant (later) they
  are the tenant host of ADR-0019. The partner never builds a URL.
- **Studio session.** The partner server calls `studio-sessions` with an optional
  `externalUserReference`. Control issues the existing handoff ticket (one time, a
  60-second lifetime, engine audience) and returns the redirect URL. The grant set is the
  fixed Studio grants contract. The partner API cannot request custom grants in v1.
- **API token.** The partner server calls `api-tokens` and gets a bearer token for the
  engine audience that lives at most 15 minutes, with the `partner-runtime` permission
  set. Control never gives out a long-lived engine secret, so there is nothing to rotate or
  leak at rest on the partner's side. A token is bound to one engine (later one Shared
  tenant) and any other engine refuses it. It stops working when that engine is deleted or
  suspended, when the partner is suspended or closed, and when the key that minted it is
  revoked. The first leaf spike must confirm that the engine
  accepts this token on its API. If it doesn't, the engine side needs a change in the
  shared engine image first, and the leaf says so.
- Diagnostics permissions are in neither the Studio nor the API token set for any Shared
  tenant, whatever the partner asks for.

### Webhooks

- **Endpoints.** HTTPS only, at most 5 per partner, port 443 only. A delivery refuses
  redirects and refuses any address that resolves to a private, loopback, link-local or
  metadata range, checked at delivery time after DNS, not only at registration. The worker
  connects to the address that passed the check, not to a second lookup of the name, and
  sends the original host name as SNI and Host. Limits: 5 seconds to connect, 10 seconds
  in total, a request body of at most 256 KB, and a response read of at most 64 KB, after
  which the rest is discarded. Any non-2xx, timeout or oversized response counts as a
  failed attempt. Leaf PA8 carries each of these as an acceptance criterion with a test.
- **Envelope.** `{ id, type, apiVersion, createdAt, engineId, externalReference, version,
  data }`. Payloads carry ids and states only: no keys, tokens or personal data.
- **Events.** `engine.ready`, `engine.failed`, `engine.updated` (release applied),
  `engine.suspended`, `engine.resumed`, `engine.deleting`, `engine.deleted`. Each is
  written to an outbox in the same transaction as the state change, so no event is lost
  and none describes a rolled-back change.
- **Signature.** The header `Elsa-Signature: t=<unix>,v1=<hex>` where `v1` is
  `HMAC-SHA256(secret, t + "." + rawBody)`. One secret per endpoint, shown once, with up
  to two valid at once during rotation. The quick-start tells receivers to reject a
  timestamp older than 5 minutes and to compare in constant time.
- **Retries.** At-least-once. A 2xx response is success. Otherwise retry with
  exponential backoff and jitter at roughly 1 minute, 5 minutes, 30 minutes, 2, 6 and 12
  hours, then give up after 24 hours. An endpoint that fails for 3 days is disabled and
  the partner is told. `POST /webhook-endpoints/{id}/events/{eventId}/replay` re-sends one
  event. Ordering is not guaranteed, so receivers use `version` and `GET`.

### Billing per partner

- The partner organization has one Stripe customer and one Hosted subscription, with one
  item per engine type (`dedicated` now, `shared` later). The item quantity is the
  allowance, and `MaxInstances` and `MaxSharedTenants` follow it through the S1 and S3
  rules already merged in #964 and #966. Partner billing adds no new entitlement rule.
- **First slice is a prepaid allowance.** The partner sets the quantity in the Hub partner
  page through the Stripe portal. The API refuses engines beyond it with
  `instance_limit_reached`, so there are no surprise charges and no new billing code
  path.
- **Deferred: auto-scaling quantity.** Raising the quantity when the API creates an
  engine is a real partner need, but it changes what a partner can be charged without a
  click. It needs Sipke's decision on consent, proration and a spend ceiling before any
  code.
- Prices, partner discounts and Stripe price ids are private configuration, selected per
  partner by an operator through the existing admin entitlement endpoints. They are not in
  this repository, the OpenAPI document or the quick-start.
- The hosted-subscription golden files gain a `kind` field. The existing
  Retained/Suspended/Canceled behaviour is unchanged. A `Suspended` partner is
  read-only, as listed under Decision 1: reads work, every write is refused with
  `partner.not-active`, and `engine.suspended` is emitted. Engine-level commercial codes for
  Create and Apply release stay as they are for an `Active` partner at its allowance.

### Quotas and rate limits

- **Quota** is the allowance above, per engine type, shown by `GET /account`. Shared adds
  the per-tenant and per-database caps of #953 (L12). Those are engine-side and don't
  change the partner contract.
- **Rate limits** partition by `keyId`, not by IP, with separate read and write policies
  and a low-permit unauthenticated bucket per IP. They return the existing
  `rate-limit.exceeded` (429) with `Retry-After`. Defaults are configuration, not
  contract.
- A partner at its allowance gets the 422 code, never a 429. The two are different
  problems and are never mixed (see #948).

### Public documentation

- `contracts/partner-api/openapi.v1.json` is generated from the code and committed. A
  test fails when the generated document and the committed file differ, as with
  `contracts/hosted-subscription/`. The Hub vendors a pinned copy and checks for drift,
  with no new workflow file.
- `docs/partner/quick-start.md` walks a partner from sign-up to a running engine: create a
  key, create an engine, poll or receive a webhook, open a Studio session, apply a release,
  delete. It uses placeholder values only.
- The docs say what a partner can't do in v1 (see Deferred) so nobody builds against a
  feature that isn't there.

## What ships first

**Wave 1, "a partner can build against it on Dedicated":** an operator-created partner
organization with an allowance set through the existing admin entitlement endpoints, API
keys, the `/partner/v1` facade (account, releases, create, read, list, delete, apply
release), Studio sessions and API tokens, per-partner rate limits, the OpenAPI document
and the quick-start. A partner polls operations. The first partner can use this on
staging as soon as it is deployed.

**Wave 2, "self-serve and public":** partner sign-up and terms on the Hub, the partner
checkout and portal, the Hub key console (create, list, rotate, revoke) and usage view, the
Hub webhook console, webhooks, and the staging proof of the whole journey. Key management
in the Hub is needed before the first partner goes beyond staging; until then an operator
issues keys through the admin endpoints. Public availability needs wave 2 and the legal terms.

## Deferred

- Shared tenants through the partner API, until #945 (L6) opens and #633's proofs pass.
- OAuth `client_credentials` with short-lived tokens and `private_key_jwt`, as a second
  credential type once keys are in use.
- Auto-scaling quantity (Sipke's decision), usage metering and usage-based billing.
- A test-mode sandbox environment. The first partner uses staging by arrangement.
- Workspace-per-end-customer, resellers and sub-partners, custom domains, white-label
  Studio, backup and restore through the API, major migration and recovery verbs, and
  client SDKs.

## Alternatives Considered

### One Microsoft Entra app registration per partner

Rejected. It isn't self-serve, it needs directory permissions we don't want in a public
flow, and it ties partner identity to our tenant.

### Reuse the Hub BFF and a Hub-issued service session

Rejected. The BFF is for browsers with a human session and for a purpose-built allowlist
(ADR-0018). Routing partner servers through it makes the BFF a generic proxy.

### Expose the existing `/api/workspaces/{workspaceId}/...` routes to API keys

Rejected. They are the human workspace surface, with permission management and operator
fields. A separate facade stays small, versioned and testable, and the workspace routes
can change without breaking partners.

### Hand the partner a long-lived engine credential

Rejected. A long-lived engine secret is one more thing to rotate and one more way to leak.
A short-lived token minted per call has neither problem.

### Raise the Stripe quantity automatically on create, in the first slice

Deferred, not rejected. It's the likely end state, but it moves money without a click.

## Consequences

- Control gains its first machine-to-machine surface. Key storage, the 401 path and the
  isolation tests are the security-critical leaves and need Code Review and a threat-model
  pass before they merge.
- The partner API adds a second public contract to keep stable. The generated OpenAPI
  file and its drift test are what keep it honest.
- Webhooks add an outbox and a delivery worker to Control. That is new operational
  surface and needs the same retention and monitoring as other background work.
- Partners create engines at their own pace, so the allowance, the rate limit and the
  alerting on failed provisioning matter before the first public partner, not after.

## Acceptance for the leaves

Each leaf carries these cross-cutting criteria in addition to its own:

1. A test with two partners proves that every route returns 404 for the other partner's
   engine id, operation id, webhook endpoint id and event id, and that an idempotency key
   or a pagination token from one partner is not honoured for the other.
2. No test, log or problem body contains a key secret, a webhook secret or an engine token.
3. Every route has a documented `code` for each failure and an entry in the OpenAPI
   document, and the drift test is green.
4. No price, partner name or Stripe identifier appears in a file in this repository.
5. Required CI stays on the existing workflows. A leaf adds no new workflow file.
