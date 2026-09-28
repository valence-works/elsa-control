# Spike 127 — Managed Elsa identity handoff

**Status:** Contract implemented; the real local and Azure/public-TLS handoff journeys
pass against the published immutable Combined image. Workflow execution is proven by
the separate isolated managed-runtime browser harness under #249.

## Decision

Use a two-party, short-lived signed handoff code. Elsa Control remains the only
customer identity and organization authority. The managed Elsa runtime does not
create a second customer account or accept a Control session cookie.

1. The browser is already authenticated to Elsa Control through the existing
   `IWorkspaceIdentityReader` seam (OIDC cookie or validated Control JWT). When a
   customer selects Open, it navigates to the selected runtime's fixed
   `/managed-elsa/handoff/start` endpoint. The runtime creates an unpredictable,
   short-lived `state` and PKCE verifier, retaining both in protected browser-bound
   correlation state (for example an HttpOnly/Secure/SameSite cookie or a server-side
   record). The verifier never enters the Control browser flow.
2. The runtime redirects to a fixed, configured Elsa Control continuation with only
   the instance ID, `state`, and S256 `code_challenge` in the URL. Control resolves
   the current organization and Elsa Instance authorization from its own store.
   Browser-provided account IDs, roles, organization IDs, instance IDs, callback
   URIs, and audiences are never trusted as authority.
3. Control signs a one-minute RS256 JWT for the authoritative managed instance
   audience and exact callback URI, including the runtime-supplied PKCE challenge.
   It then form-POSTs only `code` and `state` to that exact callback. Control never
   creates, receives, or posts the PKCE verifier.
4. The runtime validates and atomically consumes the browser-bound `state`
   correlation before redeeming the code server-to-server with its retained
   verifier, expected audience, and callback URI. Control validates the signature,
   claims and verifier, atomically consumes the `jti`, and checks current
   organization/instance authorization again.
5. The runtime creates its own short-lived HttpOnly/SameSite session. It never uses
   the handoff code as a long-lived bearer token. Runtime logout revokes that local
   session.

The prototype endpoints are:

```text
POST /api/managed-elsa/handoff/issue
POST /api/managed-elsa/handoff/redeem
```

The issue request carries a high-entropy PKCE `code_challenge` and the redeem
request carries its matching `code_verifier`. The challenge is S256 only. This
prevents a stolen front-channel JWT from being redeemed without the verifier.
The browser's callback `state` remains a separate CSRF/callback-correlation value:
it is generated and checked by the console/runtime and is not an authorization
claim or a substitute for PKCE.

They remain disabled until production signing and issuer configuration is present.
When enabled, the application registers the instance-aware production authorizer.
It resolves the persisted instance scope and current identity binding, requires
workspace access plus the `instances.open` permission, and rechecks access, health,
audience, callback and binding version during redemption. The explicit deny-all
authorizer remains available as a safe fallback for hosts that do not register the
production adapter.

## Token contract

The issuer is the configured HTTPS Control issuer. The audience is the
instance-specific value `urn:elsa:instance:{lowercase-canonical(instance-id)}` and
is not a shared `elsa` audience. For the current Guid representation,
`lowercase-canonical` is invariant `D` format with hyphens and no braces. The token
is accepted only with `RS256` and a known `kid`.
Its claims are:

| Claim | Meaning |
|---|---|
| `iss` | Elsa Control handoff issuer. |
| `aud` | Exact managed Elsa Instance audience. |
| `sub` | Control account identifier, not an Elsa runtime credential. |
| `control_iss` / `control_sub` | Stable source identity used for the second authorization check. |
| `org_id` | Authorized customer organization. |
| `instance_id` | Authorized Elsa Instance. |
| `redirect_uri` | Exact HTTPS (or local development) callback binding. |
| `code_challenge` | S256 PKCE challenge held by the initiating browser/runtime. |
| `scope` | Only `runtime:session` in this prototype. |
| `session_exp` | Authenticated Control-session upper bound for the runtime's separate local session, capped by Control's configured runtime-session maximum. This is distinct from code `exp`. |
| `jti` | Unique one-time redemption identifier. |
| `iat`, `nbf`, `exp` | One-minute lifetime; maximum configured lifetime is five minutes. |

No workflow definitions, package payloads, credentials or provider tokens belong in
this token. The only runtime API permissions it carries are the exact grants described
under [Runtime grants](#runtime-grants).

## Authorization boundary

`IManagedElsaHandoffAuthorizer` is the integration port. Its first method grants
the exact target, audience, redirect URI and scopes for the currently authenticated
`TrustedWorkspaceIdentity`. Its second method rechecks that grant during
redemption. This second check is required because an organization or instance
membership can be revoked after issue and before callback.

The production `ManagedElsaInstanceHandoffAuthorizer` resolves `instance_id` and
the canonical callback URI from persisted Elsa Instance state, verifies the
organization/workspace boundary, and requires the `instances.open` permission.
An instance is openable only while its desired/observed lifecycle and health are
Running/Ready/Healthy and its identity binding remains valid. Existing workspace
access is an input to that decision, never a permanent instance identity.

### Runtime grants

A caller who may open the instance also receives runtime API permissions, derived
from their Control roles when the code is issued and rechecked when it is redeemed.
The workspace role sets the ceiling; an organization role never raises grants above
Structured Logs read.

| Workspace role | Organization role | Grants on an image with `managed-elsa-studio-grants-v1` | Grants on an older image |
|---|---|---|---|
| Owner | any | The Studio grant set in `contracts/managed-elsa-studio-grants-v1.json` (Structured Logs, dashboard, workflow definition read/write/publish and the designer's descriptor reads) | `read:diagnostics:structured-logs` |
| Reader or SourceAdmin | Owner or Administrator | `read:diagnostics:structured-logs` | `read:diagnostics:structured-logs` |
| Reader or SourceAdmin | Member | none | none |

Every row still requires `instances.open`, which only a workspace Owner can grant. An
organization administrator who needs the designer makes themselves workspace Owner,
which is an explicit, audited step. Whether the image supports the Studio grant set
comes from the provider-confirmed current deployment, never from the release that is
merely selected next. A code issued with the Studio grant set is refused at redemption
if the caller has since been demoted or the engine is back on an older image, because
the issued grants no longer fall within what the current roles and image allow.

## Threats and controls

| Threat | Control |
|---|---|
| Stolen front-channel code | S256 PKCE verifier, one-minute expiry, exact audience and callback binding, TLS redemption, and atomic `jti` consumption. A code is not a runtime session. |
| Code replay/race | `IManagedElsaHandoffReplayStore.TryConsumeAsync` is the first state-changing redemption operation; `ConcurrentDictionary.TryAdd` gives the local prototype an atomic single-use check. Production must use a shared durable conditional insert with expiry. |
| Wrong runtime receives a code | `aud` is instance-specific and is validated against the target's expected audience. |
| Open redirect | Redirect URIs must be absolute HTTPS URIs (localhost HTTP is test-only), with no fragment or user-info. The target authorizer must return the canonical URI; the request is not an allowlist. |
| Callback CSRF or mix-up | Runtime `/start` creates unpredictable `state` plus the PKCE verifier in protected, browser-bound correlation state. The runtime validates and consumes that state at the callback before redeeming; Control only posts `code` and `state`. PKCE protects the code exchange even if a code is observed. |
| Revoked organization/instance membership | Current authorization is checked both at issue and redemption. A failed redemption consumes the code and returns a generic authorization failure. |
| Cross-organization confusion | `org_id` and `instance_id` are issued only from an authorization result and are checked as a pair. |
| Privilege escalation | The only prototype scope is `runtime:session`; requested scopes must be a subset of authorizer-granted scopes. Runtime API/admin permissions are not represented. |
| Signing-key compromise or rotation | JWT header carries `kid`; the verifier accepts active plus explicitly retained previous public keys. Production key material belongs in Key Vault/HSM-backed configuration, with overlap during rotation and audit of key ID. |
| Long-lived runtime session | The runtime owns a separate session cookie with bounded lifetime and explicit logout revocation. The handoff JWT is never stored as the cookie. |
| Logout race | Control logout prevents new issuance; runtime logout immediately revokes the local session. A code already issued may still redeem until expiry unless the redemption authorizer observes the revoked Control session/membership. |
| Sensitive audit leakage | Audit events record action, `jti`, account/org/instance IDs, audience and time, never the JWT or credentials. |
| Trusted-header bypass | The prototype reuses the existing identity reader, whose production trusted-header mode is separately proxy-gated; this handoff adds no new header authority. |

## Failure UX

The Control API should return stable, non-sensitive outcomes:

* `401` when there is no authenticated Control identity or the code is invalid or
  expired;
* `403` when current organization/instance authorization is absent or revoked;
* `409` when a valid code has already been redeemed;
* `503` when the handoff feature or production key configuration is unavailable.

The console should preserve the Control session, show “This managed instance is
no longer available” for `403`, and show “This link has expired; open Elsa again”
for a `401` from the runtime continuation. Control restarts the runtime-owned
`/managed-elsa/handoff/start` flow once for that expired callback; the runtime does
not independently retry. Control must never blindly retry the issue request. It
must not display the token or disclose whether another organization or instance
exists.

## Key rotation and session policy

The prototype key ring supports one active `kid` plus retained validation keys.
Production rotation is: publish the new public key, begin signing with the new
`kid`, retain the old public key for at least the maximum token lifetime plus clock
skew, then retire it. Key changes and failed validation spikes are audit events.

The runtime session lifetime is independent from the code lifetime. Control derives
`session_exp` only from the authentication handler that produced the current cookie
or bearer identity and caps it with `ManagedElsa:Handoff:RuntimeSessionMaximumLifetime`
(eight hours by default and as the hard configuration ceiling, aligned with the
Control customer-cookie lifetime). Issuance fails
closed for identities such as trusted headers that provide no authenticated expiry,
and when the source session cannot remain valid through the handoff-code lifetime.
The runtime must apply the returned bound together with its own shorter policy.
Control does not implement the runtime session store; that behavior belongs to #144.
Logout clears/revokes the runtime session. It does not use an OIDC back-channel
logout as a substitute for local session revocation; federation logout remains a
separate enterprise concern.

## Evidence and remaining work

`tests/Hosting/ElsaControl.Api.Tests/ManagedElsaHandoffTests.cs` exercises the
existing ASP.NET Core identity seam and the local protocol implementation. The
tests cover valid issue/redeem, missing and wrong PKCE verifier, concurrent replay,
wrong audience, expiry, revoked membership, cross-organization authorization,
redirect mismatch, strict token type/key ID and audit outcomes.

The repository now includes durable replay/audit persistence, the Elsa Instance
identity binding and production authorizer, and the Combined-runtime callback and
local-session hook. #185 records accepted local and Azure browser journeys, immutable
release/image identity, failure paths and safe evidence without retaining codes,
verifiers, cookies, tokens or workflow payloads. #249 extends that proof through
workflow creation, publication and successful execution while preserving the same
evidence boundary. Distributed signing-key operations remain a production deployment
concern.
