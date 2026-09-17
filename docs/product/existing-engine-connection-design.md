# Connect an existing Elsa engine

**Status:** Recommended design for [issue #473](https://github.com/valence-works/elsa-control/issues/473)

**Scope:** Connect a customer-operated Elsa engine to Elsa Cloud. This is a product/security design, not an authorization to provision resources or expose existing Control APIs through the Cloud BFF.

## Decision

Treat a self-deployed engine as an **external connection**, with its own trust and responsibility boundary. A Cloud user entering an engine URL, signing into Elsa Cloud, or binding an Azure subscription does not prove that the user controls that engine. A true connection must be claimed by a runtime-side connector that initiates an authenticated enrollment with Control. Until that protocol exists, the product may offer a clearly labeled external link, but it must not label address-only registration as verified or give it deployment/control authority.

The first connected-engine release should provide an attested, read-mostly dashboard card: fresh heartbeat, observed version/distribution, supported capabilities, and an explicit link to the customer's own Studio. The customer remains responsible for hosting, networking, updates, backups, scale, availability, and deletion. Artifact deployment, runtime controls, upgrades, and resource deletion require separate capabilities and permission gates.

Do not fold this connection into managed-instance provisioning. Keep these paths distinct:

| Path | Who owns the engine's infrastructure? | What Control may claim |
|---|---|---|
| Valence-hosted managed engine | Valence | Managed lifecycle only for the exact admitted release and provider contract. |
| Customer-subscription managed engine | Customer owns the subscription; Valence operates through the separately bound and entitled Azure authority | Managed only after Lighthouse bind, `azure-bound` entitlement, and customer-subscription targeting prerequisites pass (#434, #435, #436). |
| Connected self-hosted engine | Customer or their hosting provider | A registered connection and only the capabilities proven by its connector. This is not a Valence-provisioned or Valence-operated workload. |

Social/email identity identifies the human and workspace. It does not establish Azure subscription ownership or ownership of an arbitrary endpoint. The Azure bind flow and external-engine enrollment are separate consents.

## Actors, trust, and responsibility

| Actor | Authority and responsibility |
|---|---|
| Workspace member | `Read` may list connections. A member needs `ManageSetup` to start pairing, repair, or disconnect. Organization membership alone does not grant workspace access. |
| Elsa Cloud BFF | Presents a narrow customer-scoped surface. It must not hold an Admin API key or proxy the full Control Console API. Every route is authorized for the selected workspace and explicitly admitted to the BFF. |
| Elsa Control | Issues a short-lived, single-use, workspace-bound enrollment challenge; validates redemption and connector identity; stores safe connection metadata, trust state, capability evidence, and audit events; revokes the connection on disconnect. |
| Runtime connector on the customer's side | Initiates outbound TLS to Control, redeems the enrollment challenge, proves possession of its enrolled identity, reports heartbeat and observed capabilities, and receives only commands that are later authorized. It does not confer Azure rights. |
| Customer/operator of the engine | Chooses and operates the host, grants only the connector's documented permissions, controls customer data, and handles infrastructure incidents. The displayed connection does not transfer ownership to Valence. |

Recommended pairing is initiated in the chosen workspace, then redeemed by a connector running beside the engine. The challenge should be high entropy, short lived, single use, audience-limited, and stored only as a hash. Redemption creates a distinct engine identity with a rotatable/revocable credential. Prefer proof-of-possession (for example, a connector key registered at redemption) over a reusable shared secret. The exact token/key protocol needs a security review before implementation; do not expose a broad API key or persist a connector private key in Control.

The runtime-originated channel is the trust boundary. Control should not make server-side requests to a user-entered engine URL to decide whether it is authentic. This also supports private engines that cannot accept inbound traffic. A browser link to the customer's Studio is user initiated and remains governed by that engine's own authentication.

This connection is distinct from the customer infrastructure reconciler under [#103](https://github.com/valence-works/elsa-control/issues/103) and its [#316 runtime-command trust assessment](https://github.com/valence-works/elsa-control/issues/316). An external-engine connector is scoped to one engine identity and its explicitly granted engine capabilities; it does not reconcile Azure, Kubernetes, Docker, or host infrastructure. Reuse one-time enrollment, identity rotation, revocation, outbound transport, and command/audit primitives from that work where their trust semantics fit, but keep identities, scopes, permissions, and lifecycle contracts separate. This engine-connection design does not replace #316's gap assessment of the broader agent against the [proposed ADR-0011 evidence gates](../adr/0011-customer-deployment-agent.md).

## What the current code proves (and does not prove)

Control already has a generic deployment-engine registration flow in `WorkspaceDeploymentEndpoints`: a workspace setup manager can register an engine URL against an application environment, update it, and manually verify it. The flow immediately calls `EngineHealthService`; the API routes require `ManageSetup` ([registration and verification routes](../../src/Hosting/ElsaControl.Api/Workspace/WorkspaceDeploymentEndpoints.cs#L370), [credential-reference routes](../../src/Hosting/ElsaControl.Api/Workspace/WorkspaceDeploymentEndpoints.cs#L203)). The Console has a “Connect an engine” form and can accept a new API key, a saved credential reference, or deferred credentials ([ConnectEnginePage.tsx](../../src/Hosting/ElsaControl.Console/src/features/deployments/ConnectEnginePage.tsx#L104)). This is an advanced Console registration flow, not a Cloud BFF contract or proof of remote ownership.

There are three security gaps to close before reusing it for a Cloud connection:

1. `HttpEngineHealthProbe` sends an unauthenticated `GET` to the stored `BaseUrl`. It treats every response except 401/403 as `CredentialVerificationStatus.Verified`, so even 404 and 5xx responses can be labeled credential-verified; it never resolves or uses the registered credential reference ([EngineHealthService.cs](../../src/Deployment/ElsaControl.Deployment.Core/Workspace/EngineHealthService.cs#L12), [probe implementation](../../src/Deployment/ElsaControl.Deployment.Core/Workspace/EngineHealthService.cs#L97)). This is reachability evidence, not credential or ownership proof.
2. The supplied URL is probed during create/update/verify and again by the background verifier, which selects due engines on a 15-minute verification interval ([EngineVerificationHostedService.cs](../../src/Hosting/ElsaControl.Api/Workspace/EngineVerificationHostedService.cs#L6)). A caller-controlled target therefore creates an SSRF surface, including redirects and DNS rebinding unless guarded at connect time. Do not expose this probe through Cloud or call it on an untrusted candidate.
3. The Console builds a registration request with `engine.reload-configuration` and a “Reload Configuration” control in browser code, rather than receiving those facts from the engine ([DeploymentSetupPanel.tsx](../../src/Hosting/ElsaControl.Console/src/features/deployments/DeploymentSetupPanel.tsx#L139)). A browser must never be authoritative for runtime capabilities or controls.

Workspace credential references already distinguish a protected local secret from an external reference and avoid returning the secret value ([WorkspaceDeploymentModels.cs](../../src/Deployment/ElsaControl.Deployment.Core/Workspace/WorkspaceDeploymentModels.cs#L59), [secret protection on create](../../src/Hosting/ElsaControl.Api/Workspace/WorkspaceDeploymentEndpoints.cs#L203)). Reuse that discipline for connector identity, but do not treat a saved secret reference as evidence that the user controls the target host.

Cloud BFF tokens are accepted only on endpoints marked with explicit allowlist metadata; all other endpoints fail closed ([CloudBffAuthorization.cs](../../src/Hosting/ElsaControl.Api/Authentication/CloudBffAuthorization.cs#L139)). The new connection API must be a narrow customer route with exact workspace authorization and BFF allowlisting, not direct exposure of the generic deployments group ([issue #459](https://github.com/valence-works/elsa-control/issues/459)).

## V1 journey

1. **Choose the path.** In the Cloud dashboard, show separate actions: “Create a managed engine” and “Connect an existing engine.” Explain that the second path does not create, secure, or operate Azure resources. Azure subscription binding is not requested here.
2. **Check prerequisites.** Show supported connector/runtime versions, outbound HTTPS requirement, the exact permissions the connector needs, and the three provenance choices below. Do not ask the user to paste an admin key or assume a public endpoint is reachable.
3. **Pair.** A `ManageSetup` member names the connection and requests a one-time pairing challenge. The engine-side connector redeems it over outbound TLS and returns its key proof and runtime-originated metadata. The browser never submits capabilities.
4. **Verify.** Display “Waiting for engine,” then “Connected” only after valid challenge redemption and a fresh authenticated heartbeat. Show `last seen`, connector version, observed engine version, and provenance confidence. Do not report `Healthy` solely because a URL returned HTTP 200. A self-reported version is not release attestation.
5. **Open Studio.** Provide an explicit external link using an HTTPS address supplied or confirmed in the pairing flow. Show the destination host before the first visit, preserve a configured path when the customer's Studio is hosted under one, and open with opener isolation. The user authenticates at the customer's own Studio. Do not silently reuse the managed-instance handoff or imply Cloud SSO.
6. **Repair.** Offer “Retry connection” to request a fresh heartbeat and “Re-pair connector” to revoke/reissue enrollment. Explain whether the issue is stale heartbeat, rejected/revoked identity, unsupported connector, or missing capability. Preserve the last verified facts and timestamp.
7. **Disconnect.** Explain that this revokes the Control connection and stops future Control commands; it does not stop, delete, or change customer resources. Confirm, revoke immediately, and retain a safe audit/tombstone record. Reconnecting requires a new challenge.

Show a stale heartbeat as “Not recently seen” or “Connection unavailable,” not a fabricated provisioning state. Keep engine lifecycle, health, and connector reachability as separate fields. Retry operations must be idempotent and must not create a second active connection after a lost response.

## Distribution and capability policy

Connection type and runtime distribution are separate dimensions. The connection record must say `External / customer operated`; its distribution should be one of:

| Distribution | Evidence | Permitted description |
|---|---|---|
| Valence Runtime | Exact image/component digests matched to a verified, signed producer release manifest and the governed catalog | “Valence Runtime” only for the matched release. External hosting still is not Valence-managed hosting. |
| Elsa OSS, supported release | Exact version plus a catalog entry explicitly marked Supported for the relevant runtime/capability | “Supported Elsa OSS release” only within that published support boundary. |
| Other or unverified image | Missing/unknown manifest, unsupported version, or only self-reported metadata | “External image — support and capabilities unverified.” No managed, supported, or security claim. |

The catalog owns release lifecycle, immutable image digest, topology, signature/provenance evidence, and compatibility. Tags, HTTP version headers, and user choices do not substitute for that evidence ([commercial image release audit](commercial-image-release-audit.md#machine-readable-ownership-contract), [resolved plan contract](resolved-elsa-application-plan.md#contract-shape)). “Choose your own image” belongs to a separate admitted deployment flow in #406; it is not a property inferred by connecting an already-running engine.

V1 enables dashboard status and the external Studio link only. The connector may report an allowlisted capability set, but the UI should expose no mutation action until the runtime protocol, API authorization, command audit, and compatibility checks are implemented. Later scopes may add artifact deployment (`ExecuteDeployment`) and runtime controls (`ExecuteControls`) independently. Never infer `stop`, `restart`, `upgrade`, `scale`, or `delete` from an `EngineApi` capability. Disconnect never destroys an engine.

## API and domain shape

Keep this as an `ExternalEngineConnection` (or equivalently explicit external-connection aggregate), scoped to `OrganizationId` and `WorkspaceId`, rather than an `ElsaInstance`. The managed `ElsaInstance` owns desired state and a durable provider lifecycle; the generic `WorkspaceWorkflowEngine` is currently a deployability/registration projection, not the authority for managed lifecycle ([instance aggregate](elsa-instance-aggregate.md#decision-summary)). A later adapter may expose an attested connection to existing deployment features, but it must not erase the connection's provenance or trust state.

Safe fields include stable ID, workspace, display name, `Pending | Connected | Degraded | Revoked`, last authenticated heartbeat, connector protocol/version, observed distribution/version with evidence level, allowlisted capabilities, and audit timestamps. Keep secrets, private keys, raw runtime payloads, workflow definitions, and credentials out of list/read DTOs, logs, commands, and audit events.

Illustrative routes (final route naming is an implementation decision):

- Customer BFF: list/read connections; create a pairing attempt; read pairing progress; re-pair; disconnect. `Read` for list/read and `ManageSetup` for every mutation.
- Runtime: redeem a one-time pairing challenge; rotate or revoke its own identity; submit authenticated heartbeat and attested capability/provenance metadata. Runtime identity is scoped to one connection/workspace and cannot select another workspace or engine.
- Later mutation routes remain separately permissioned and capability checked. A user-owned connection does not authorize Azure management; subscription targeting stays in #434–#436.

Before a future direct-probe mode is considered, isolate it behind a reviewed egress service with HTTPS/origin validation, no userinfo/query/fragment, redirect denial or strict same-origin revalidation, connect-time IP checks against loopback/private/link-local/metadata ranges, DNS rebinding protection, timeouts/response limits, audit, and tests. These controls are defense in depth; V1 should use runtime-originated communication and not server-fetch candidate URLs.

## Child issue proposal and gates

1. **[#479: harden the existing direct engine probe](https://github.com/valence-works/elsa-control/issues/479)** — stop treating an unauthenticated response as credential verification, block unsafe egress targets and redirects, and keep scheduled verification under the same policy. Gate: SSRF, DNS/redirect, response-classification, and no-credential tests across create, update, manual verify, and background verification.
2. **[#480: implement runtime-originated engine enrollment](https://github.com/valence-works/elsa-control/issues/480)** — one-time scoped challenge, proof-of-possession, replay/expiry/revocation, tenant/workspace isolation, and threat model. Gate: protocol review and tests for replay, cross-workspace redemption, stolen/expired challenge, rotation, and revoke.
3. **[#481: add external connection persistence and narrow customer/runtime APIs](https://github.com/valence-works/elsa-control/issues/481)** — no browser-set capability authority; safe audit projection; exact `AllowCloudBff` routes; `Read`/`ManageSetup` authorization. Gate: API security tests, idempotent pairing, secret/log redaction, and proof that BFF tokens cannot call generic deployment or admin routes.
4. **[#482: emit authenticated heartbeat and release/capability evidence](https://github.com/valence-works/elsa-control/issues/482)** — signed/allowlisted facts from the runtime/connector, with unsupported versions represented explicitly. Gate: forged/self-reported version cannot claim a governed release or elevate capabilities.
5. **[#483: add the Cloud connect/status/repair/disconnect UX](https://github.com/valence-works/elsa-control/issues/483)** — accessible pending/failure/stale states, no provisioning language, and explicit customer-ownership copy. Gate: browser tests against pending, success, stale, revoked, unsupported, and retry responses; verify no secret in browser persistence.
6. **[#484: gate deployment and runtime controls](https://github.com/valence-works/elsa-control/issues/484)** — only after the connection trust work, align each action with existing deployment permissions and runtime command audit. Gate: separate tests for `ExecuteDeployment` and `ExecuteControls`, deny unadvertised operations, and compatibility checks; no lifecycle delete/upgrade claim without its own contract.
7. **[#485: run the live rehearsal and publish the support runbook](https://github.com/valence-works/elsa-control/issues/485)** — public and private-network engine cases, connector rotation/revocation, lost connectivity, and disconnect behavior. Gate: evidence shows no Azure resource is created or changed by connect/disconnect and Studio remains customer-authenticated.

Keep #406 responsible for package/instance tiers and allowed deployment images. Keep #430/#434–#436 responsible for customer-tenant identity, Azure Lighthouse binding, `azure-bound` entitlement, and subscription targeting. Keep #459 responsible for the Cloud dashboard/BFF happy path and its security boundary. #473 should close when this split and its trust contract are agreed; each implementation slice should be tracked separately and ordered by the gates above.

## Source references

- [Workspace deployment engine registration and verification](../../src/Hosting/ElsaControl.Api/Workspace/WorkspaceDeploymentEndpoints.cs#L370)
- [Health verification and HTTP probe](../../src/Deployment/ElsaControl.Deployment.Core/Workspace/EngineHealthService.cs#L12)
- [Periodic verifier](../../src/Hosting/ElsaControl.Api/Workspace/EngineVerificationHostedService.cs#L17)
- [Runtime commands and per-engine authorization](../../src/Hosting/ElsaControl.Api/Workspace/RuntimeCommandEndpoints.cs#L14)
- [Cloud BFF allowlist](../../src/Hosting/ElsaControl.Api/Authentication/CloudBffAuthorization.cs#L139)
- [Exact Cloud BFF endpoint allowlist tests](../../tests/Hosting/ElsaControl.Api.Tests/CloudBffAuthorizationTests.cs#L54)
- [Customer workload subscription boundary, ADR-0016](../adr/0016-customer-workload-subscription-boundary.md)
- [BYO Azure feasibility evidence, #430](../spikes/430-byo-azure-bind-feasibility.md)
- [Cloud product architecture, #459](https://github.com/valence-works/elsa-control/issues/459)
- [Capability packaging, #406](https://github.com/valence-works/elsa-control/issues/406)
