# ADR-0009: Treat Customer Packages as Executable Code and Gate Them by Isolation

## Status

Accepted

## Date

2026-08-29

## Context

Elsa/Nuplane package installation can execute customer-supplied .NET code with the workload process permissions. Such code can read process state, secrets and files; make network requests; consume resources; probe reachable services; and exfiltrate accessible data.

Package metadata, approval and compatibility checks improve governance but do not create a runtime sandbox.

## Decision

- Define extension policies as built-in, Valence-approved and arbitrary customer packages.
- Derive the class server-side from producer/catalog ownership and governed approval,
  validation, listing and immutable-manifest facts. Customer intent and workspace approval
  cannot upgrade an arbitrary package to Valence-approved.
- Bind the resolution-time catalog decision into the immutable resolved plan with a safe
  digest. This digest is an admission decision seal, not a signature or a runtime sandbox.
- Arbitrary customer packages are prohibited on Shared and Data-isolated compute unless a separately reviewed sandbox proves an adequate boundary.
- Approved packages require provenance, vulnerability and compatibility policy but do not automatically become safe for every isolation profile.
- Private/custom-code deployment shall use a reproducible immutable artifact/image workflow unless a future security review accepts another model.
- The workflow shall capture exact inputs and digests, validate provenance/security, deploy a revision, validate health and support rollback.
- Secret, network and telemetry access follows least privilege at the workload boundary.

## Alternatives Considered

### Allow arbitrary NuGet packages because tenants are logically separated

Rejected: logical tenant discrimination does not isolate executable code in one process or compute boundary.

### Rely only on package approval

Rejected: approval does not prevent compromise, unsafe behavior or excessive resource use.

### Permanently mutate running containers

Rejected as the managed default because reproducibility, rollback and provenance are weak.

## Consequences

- Any future Shared profile has a constrained extension surface.
- Private/custom-code launch requires a threat model, provenance/build pipeline and isolation tests.
- Entitlements can grant access only to policies that the selected provider/profile actually enforces.
- Managed launch admission is Dedicated-only. Provider-neutral plans retain the documented
  Shared, Data-isolated and Private vocabulary, but those profiles remain unavailable to the
  managed launcher until their separate evidence gates are implemented. Built-in packages may
  be represented across known profiles, while Valence-approved packages are limited to Dedicated
  and Private; arbitrary customer packages remain prohibited by this policy.

## Evidence and profile gates

The [isolation threat model](../product/isolation-threat-model.md) records the
assets, trust boundaries, explicit profile claims/non-claims, hostile-package
attack paths, Dedicated launch claim matrix and executable promotion gates. It
is the required security evidence register; this ADR does not imply that any
profile or custom-code path has already passed those gates.

## Amendment 1 (2026-10-13): Signed allowlist for in-house Valence-built activity packages on Shared

Status: Accepted (pending merge). Accepted by the CEO on 2026-10-13 under Sipke's decision 1 on #795. Scope is the **Shared** profile only; Data-isolated is unchanged and stays as the original decision above states.

### Context
- **ADR-0009 today:**
  - Customer packages are executable code.
  - Arbitrary customer packages are prohibited on Shared and Data-isolated "unless a separately reviewed sandbox proves an adequate boundary".
  - Valence-approved packages are limited to Dedicated and Private.
  - Built-in packages may appear on any profile.
  - Private custom code uses an immutable image workflow.
- **#110 (threat model):** hostile-package attack paths include reading process state, secrets and files; network probing and exfiltration; and resource exhaustion.
- **#355:** the Private profile and extension-policy matrix. `packages.arbitrary` is Private-only.
- **#794 (Training labs):** learner packages load at runtime on a **dedicated lab shard** only, with per-tenant collectible ALCs, an IL pre-scan, an audit trail and a kill switch.
- **#795 (Elsa Integrations):** decision 1 (Sipke, 13 Oct 21:37 CEST). Valence-verified, signed packages may run on Shared. Learner and community packages stay dedicated-only (lab shard, Dedicated, BYO). Integrations target Elsa 4 only, with no Elsa 3 adapter. Valence registers the OAuth apps; customers may bring their own.
- **An AssemblyLoadContext is not a security boundary** (.NET Core and later: no CAS, no AppDomain sandbox). Per-tenant ALCs give functional isolation only (types, activities, descriptors). Any package on Shared therefore runs with full process trust. This amendment is a **supply-chain and review control plus platform-enforced runtime controls, not isolation**.

### Supersession
This amendment supersedes, **for the Shared profile only and only for packages meeting every clause below**:
- the limit of Valence-approved packages to Dedicated and Private (original decision, approved-packages bullet); and
- the requirement in the original decision that approval "does not automatically become safe for every isolation profile", to the extent that an allowlisted package becomes loadable on Shared once the preconditions in clause 12 have passed.

It does not supersede the prohibition of arbitrary customer packages on Shared or Data-isolated, the Data-isolated rules, the immutable-image rule for Private custom code, or the Dedicated-only managed launch admission. **Managed launch admission stays Dedicated-only until the clause 12 preconditions have passed.**

### Decision
1. **Shared allowlist.** A package may load on Shared only if it is on the **Valence-signed Shared allowlist**. The allowlist entry is `{packageId, version, SHA-512 hash, tier, publisher, reviewer of record, review date, provenance attestation digest}`. The allowlist is signed by Valence CI and versioned. Hosts are **Elsa 4 hosts only**.
2. **Signatures and hashes.** The package needs an author signature **and a Valence repository countersignature**. The pinned SHA-512 is computed over the **final countersigned artifact**. Every dependency assembly in the package closure is also pinned by SHA-512 in the allowlist entry. Floating versions are not allowed.
3. **Key management.**
   - **Trust root:** a Valence offline root key certifies three separate keys: countersignature, allowlist signing and revocation-list signing. Hosts receive the root public key (and current intermediate fingerprints) in the signed production image and pin it; they do not fetch trust roots at runtime.
   - **Custody:** all signing keys are non-exportable keys in an HSM-backed KMS (Azure Key Vault Managed HSM or equivalent). Only Valence CI identities hold sign permission; no human can export a key.
   - **Rotation:** keys rotate at least yearly and on any personnel change with sign rights; new keys are certified by the root and announced in a signed key manifest.
   - **Compromise:** the key is revoked in the key manifest, everything it signed is treated as untrusted (fail closed), and the affected packages and lists are re-signed under a new key and re-reviewed before reload.
4. **Load-time enforcement and fail closed.** Before loading, the host verifies the countersignature chain to the pinned root, the package and dependency hashes, the provenance attestation, and the freshness of both the allowlist and the revocation list. **Fail closed at load time** means: no load, an audit event, and a tenant-visible error (`PackageNotAllowed` with the reason). It applies on any mismatch, on expiry, or on an unreachable or stale allowlist or revocation list past its TTL. **Allowlist freshness TTL: 15 minutes maximum. Revocation-list hard TTL: 120 s maximum (2× the poll interval)**; there is no grace beyond it.
5. **Activities only.** No DI-host integration, hosted services, HTTP endpoints, EF migrations or `HostIntegrated` load mode on Shared. Loading is collectible per tenant (#794 loader). No runtime dependency resolution: the package closure ships complete, and loading from public feeds is forbidden.
6. **Network-layer egress, default deny.** Shared workloads run behind network-layer egress enforcement (default deny plus a per-workload allowlist derived from the reviewed manifest hosts), enforced outside the engine process (egress proxy / network policy / firewall). The platform `HttpClient` handler is a convenience, not the control. Direct sockets to undeclared hosts are blocked by the network layer.
7. **Credentials outside the engine.** Credential exchange runs in a **broker outside the engine process** (sidecar or service) that injects auth at the egress proxy. Package code gets only scoped, short-lived connection handles (#795 §2); no raw secrets, refresh tokens or client secrets enter the engine process. This applies to Valence-registered and BYO OAuth apps alike: BYO OAuth apps still get connection handles only. The engine environment carries no secrets beyond what the engine itself needs.
8. **Managed-identity blocking.** The workload is blocked from the IMDS/MSI token endpoint (169.254.169.254 and the platform identity endpoint) at the network layer. No managed-identity access is exposed to packages.
9. **Revocation.** Through the kill switch per version, package, publisher or signing key, via a signed revocation list delivered by **push plus a poll every ≤60 s**. The revocation list has the same signature and unreachable-fails-closed rules as the allowlist (clause 4), with its own **hard TTL of 120 s**.
   - **New loads:** when a package is revoked, every new load of it fails closed immediately (no grace).
   - **Stale revocation list:** once the revocation list is older than its 120 s hard TTL (for example, the channel is unreachable), both new loads and in-flight runs of allowlisted packages fail closed. The 15-minute TTL applies to allowlist freshness only, never to revocation.
   - A **security revocation** takes effect immediately and fails closed: maximum window 60 s from publication. In-flight executions using the package are **faulted** (`PackageRevisionRevoked`) and new loads refused. If the collectible ALC unload isn't confirmed within **30 s**, the replica is **recycled** (mandatory).
   - A **non-security deprecation** gets 14 days' notice before removal from the allowlist; running instances drain normally.
10. **Community and learner packages never load on Shared.** They load only on the lab shard, Dedicated or BYO, under #355 entitlements. **Paying Shared customers cannot upload packages**; on Shared they only select allowlisted packages.
11. **In-house only (permanent) and allowlist governance.**
    - Only **Valence-built packages from the Integrations Lead or connector builder, built and signed in Valence CI**, may enter the Shared allowlist. This is permanent, with no sunset. Any third-party path requires a **separate ADR amendment signed off by Sipke and the CEO**. (Free catalog listing for third-party connectors is a listing decision, not Shared-allowlist admission.)
    - "Verified" requires a **security review of untrusted code**, separate from shared Code Review (correctness), with a written checklist; a scan gate (IL pre-scan, dependency/CVE, license, manifest and egress diff); and a **reviewer of record**. Every new version needs a fresh review; nothing is inherited.
    - **Reviewer of record:** the Elsa Control Architect, who is never the package author. The security sign-off is a recorded step separate from the Code Review `**Verdict: APPROVE**` and doesn't replace it. No human co-sign is required (Sipke, 2026-10-13).
    - **Change control:** allowlist changes happen only through a reviewed PR to a dedicated allowlist source. Two-person rule: the reviewer of record is not the package author and is a different person from the CI triggerer. Because all agents post to GitHub under one shared account, the rule is enforced on the **harness (agent) identity** recorded in the signed sign-off record (author, reviewer and CI-triggerer agent ids), not on the GitHub login; CI rejects entries where any two match. Every add, remove or revoke is written to an append-only audit log. CI rejects any entry without a reviewer-of-record record.
12. **Preconditions before any allowlisted load on Shared.** None of the following is a follow-up:
    - the isolation threat model (`docs/product/isolation-threat-model.md`) states the Shared-allowlist claims and non-claims, including cross-tenant memory and connection-handle exposure, and the Shared promotion gates pass;
    - **transitive dependencies:** every dependency assembly pinned and hashed; no runtime resolution from public feeds;
    - **dependency confusion:** pinned private-feed source mapping, no public-feed fallback in the build or at load;
    - **build-pipeline and insider compromise:** SLSA-style provenance attestations verified at load, reproducible builds, protected CI runners;
    - **IL pre-scan limits** documented (reflection, dynamic codegen, native interop) with the review checklist covering them;
    - **resource exhaustion / noisy neighbour** (#110's third path): per-tenant CPU, memory and execution-time limits enforced on Shared;
    - clauses 3, 6, 7 and 8 implemented and tested (AC below).
13. **Profile matrix change:** in-house Valence-built, Verified packages are permitted on **Shared only**, only under 1–12. Arbitrary customer packages remain prohibited on Shared and Data-isolated. Data-isolated, Private and Dedicated rules are unchanged.

### Alternatives considered
- **No packages on Shared ever.** Rejected: it blocks first-party connectors (#795) on the entry tier and forces Dedicated for basic integrations.
- **Any signed package on Shared** (author signature only). Rejected: a signature proves identity, not safety. Compromised or malicious publishers would get process trust on a multi-tenant shard.
- **Extend the allowlist to Data-isolated now.** Rejected: Data-isolated carries its own isolation promise; it stays as the original decision states.
- **Open the allowlist to third-party packages once a review step exists.** Rejected: in-house only is permanent; a third-party path needs its own amendment.
- **Per-tenant process or sandbox isolation on Shared** (worker process per tenant, ACA dynamic sessions, gVisor). Rejected for now: high cost and latency per execution, IPC redesign of activity execution, and a conflict with the Shared tier's price point. It stays the path if a security incident demands it, and it would satisfy ADR-0009's "reviewed sandbox" clause.

### Consequences
- Shared gains a narrow, reviewed extension surface. Residual risk: a reviewed package or the signing pipeline could be compromised. Mitigated by hash pinning (package and dependencies), provenance, re-review per version, revocation, network-layer egress, credentials outside the engine, IMDS blocking and per-tenant resource limits.
- **Prerequisites:** a security-review capacity (reviewer of record); HSM-backed KMS keys; an allowlist service and revocation channel (push plus poll); an egress proxy and credential broker; the #794 per-tenant collectible loader; per-tenant load and unload in elsa-foundation (the critical path).
- Managed launch admission stays Dedicated-only until clause 12 passes.

### Proofs and acceptance criteria
1. **Unsigned, author-only, wrong-hash or wrong-dependency-hash package, or missing provenance.** Shared refuses the load, logs an audit event and shows a tenant-visible error. Proof: automated negative tests.
2. **Allowlisted package.** It loads for one tenant only, and other tenants can't resolve its types. Proof: an isolation test (as #794 AC1).
3. **Security revocation.** Pushed revocation faults in-flight executions and unloads within ≤60 s; an unconfirmed unload triggers a replica recycle within 30 s. Proof: a staging QA live run.
4. **Egress.** A direct socket from package code to an undeclared host is blocked at the network layer. Proof: a runtime test.
5. **Credentials.** No secret, refresh token or client secret is present in the engine process or environment; package code obtains only a scoped handle (Valence and BYO OAuth apps). Proof: a memory/env inspection test plus an SDK contract test.
6. **IMDS.** A request from the workload to the IMDS/MSI endpoint is blocked. Proof: a runtime test.
7. **Gate and governance.** The allowlist build rejects any non-in-house package, any entry without a reviewer-of-record record, and any change where reviewer equals author or CI triggerer. Proof: a CI policy test.
8. **Uploads.** A Shared customer can't upload a package. Proof: an API negative test.
9. **Stale lists.** An allowlist past its 15-minute freshness TTL fails closed for new loads; a revocation list past its 120 s hard TTL fails closed for new loads and in-flight runs. Proof: a test for each.
10. **Key compromise.** Revoking a signing key makes everything it signed fail closed. Proof: a test.
11. **Resource limits.** A package exceeding per-tenant CPU, memory or time limits is stopped without affecting other tenants. Proof: a load test.

**Merge gate:** Code Review APPROVE on the exact head plus green CI. Accepted by the CEO; merges under the standard Code Review rule.

### Resolved
- Scope: Shared only (CEO, 13 Oct). Data-isolated unchanged.
- In-house only, permanent; third-party needs a separate amendment signed off by Sipke and the CEO.
- Reviewer of record: Elsa Control Architect (never the author); no human co-sign (Sipke, 2026-10-13).
- Allowlist freshness TTL: 15 minutes maximum. Revocation-list hard TTL: 120 s; past it, loads and in-flight runs fail closed.
- Merges under the standard Code Review rule.

## Amendment 2 (2026-10-15): Customer, learner and community packages on Valence Dedicated, isolated per customer

Status: **Proposed.** No decision is open. The CEO's calls of 15 Oct 2026 and the Elsa Control Lead's lab call of 15 Oct 2026 are applied (see Resolved). Customer-written packages are allowed (Sipke, 15 Oct 2026, recorded in valence-works/strategy PR #12 (`decisions/2026-10-15-customer-packages-on-dedicated.md`)). The amendment isn't in force until it merges under the standard Code Review rule. Until then, #221's Dedicated admission behaviour is unchanged: arbitrary packages fail closed. Scope: the **Valence Dedicated** profile only, on the Elsa 4 host (clause 2). Shared stays under Amendment 1. Data-isolated and Private are unchanged. The lab shard is governed by ADR-0017 (#809), not by this amendment (clause 10).

### Context
- **Original decision (this ADR):** arbitrary customer packages are prohibited at managed launch. Dedicated admits built-in and Valence-approved packages only, and #221 enforces that at plan resolution and again before provider submission.
- **Sipke, 13 Oct 2026 (#795 decision 1, recorded in Amendment 1's context):** learner and community packages stay off Shared and run only on dedicated engines (lab shard, Dedicated, BYO).
- **CEO, 13 Oct 2026, delivery plan §5b:** no runtime packages for paying Shared customers; they're for Dedicated and BYO only.
- **#629 decision (CEO, 13 Oct 2026, #729 carry-over):** customer-built *images* run only in BYO subscriptions. A future isolated Valence-hosted tier stays open and needs its own isolation ADR before any build.
- **#355:** the Private profile and extension-policy matrix, which proposed `packages.arbitrary` for Private only.
- **ADR-0016:** each Dedicated instance already gets its own resource group, Container Apps environment, Azure SQL database, user-assigned identity and Key Vault. Engine secrets reach the container as Container Apps Key Vault references, which the platform resolves outside the engine process.
- **An AssemblyLoadContext is not a security boundary** (TB-03). A package loaded into an Elsa engine has that engine's full process trust: memory, environment, files, database connection and network. Nuplane loading, an IL pre-scan and package approval are governance controls. None of them is a sandbox.

### Decision
1. **What's admitted.** On Valence Dedicated, a customer may run **learner, community and customer-written packages** (arbitrary code, not Valence-reviewed) in that customer's own engines (Sipke, 15 Oct 2026, recorded in valence-works/strategy PR #12 (`decisions/2026-10-15-customer-packages-on-dedicated.md`)). Together these form the "customer package" class. Admission needs:
   - the `packages.customer` entitlement, a capability rather than a plan name;
   - an explicit customer opt-in per instance, recorded with the actor and an acknowledgement that the code runs with the engine's full trust;
   - every clause 12 precondition to have passed.

   Classification stays server-derived (as in #221). Caller-supplied classification can never upgrade a package, and a customer package never becomes "Valence-approved".
2. **The boundary is the per-customer engine.** "Isolated per customer" means all of the following. Each is a provider-template invariant with a test:
   - **Elsa host:** customer packages load only into the **Elsa 4 engine host** (elsa-workflows/elsa-foundation) running in Valence's governed image. Amendment 1 and the Integrations packages are Elsa 4 only, and so is this amendment. No other host loads customer packages under this amendment. That includes Elsa 3.8 Combined engines, which today's Dedicated proof deploys (ADR-0010), and any customer-supplied host. **Elsa 3.8 Dedicated customers don't get customer packages.** This is intended and matches Sipke's decision that Integrations target Elsa 4 only, with no Elsa 3 adapter (confirmed by the Elsa Control Lead, 15 Oct 2026). Revisiting it needs a new decision. An Elsa 3.8 Dedicated instance never gets `packages.customer` admission (`D-CP-06`).
   - **Own engine and process:** each customer package runs only inside an Elsa engine that belongs to one customer (organization). There's no engine, replica, process or Container Apps environment shared with another customer, and no co-hosting of two organizations' tenants in one engine.
   - **Own identity:** one workload identity per instance (ADR-0016). Its role assignments cover only that instance's own resources. It has no role on Control, the provider subscription scope, the registry beyond pulling the governed image, or any other customer's resources. **It can't delete, shorten the retention of, or overwrite the instance's backups or point-in-time restore (PITR) data.** Backup and PITR settings are managed only by the provider identity, never by the engine identity, so a package acting as the engine can destroy live data but not the recovery points (D-08).
   - **No shared credentials:** nothing in the engine's environment, files, database or Key Vault is valid outside that instance. That includes no Control API key, no fleet-wide signing key, no shared telemetry ingestion secret, no shared feed credential and no shared database login. Every engine credential is unique to the instance and revocable on its own.
   - **Network-layer egress default deny stays:** egress is enforced outside the engine process by a **shared egress proxy that applies a per-tenant (per-instance) policy**, chosen for cost (CEO, 15 Oct). Each instance's allowlist is declared by the customer and recorded by Control. The proxy authenticates each instance's traffic so that one instance's policy can never be used by another. Engines have no direct route out that bypasses the proxy. **Per-instance enforcement** (a dedicated network boundary and firewall per instance) is a later premium option, not a launch requirement. Package code can't open sockets to undeclared hosts. Lateral and private targets are not routable, including other instances, Control's private endpoints, Valence's provider and IaC endpoints, and other customers' SQL and Key Vault.
   - **Prohibited destinations are denied whatever the customer declares.** The proxy applies a Valence-held deny list that takes precedence over every per-tenant allowlist. It covers:
     - Control's management, internal and private endpoints;
     - the provider plane: Valence's provider and IaC endpoints (deployment pipelines, state stores and registry push), plus the instance metadata and identity token endpoints;
     - other customers' endpoints: their instances, SQL, Key Vault and package stores (clause 3).

     **The Azure management endpoint (Azure Resource Manager) is not on the deny list.** It's one public host shared by every Azure customer, so denying it would also block customer workflows that manage the customer's own Azure. Valence's subscription is protected by credentials instead: the engine has no managed identity and no provider role, the metadata and identity endpoints stay denied, and the broker strips injected credentials on cross-origin redirects. There's no TLS inspection to filter by subscription (Elsa Control Lead, 15 Oct 2026).

     A declaration that names a prohibited destination is refused when the plan is resolved (`egress.destination.prohibited`), and the proxy denies it anyway if one gets through. Control's public endpoints stay reachable under the "no route into Control" bullet below.
   - **Credential broker stays:** outbound connection credentials (OAuth and API connections) come from a broker outside the engine process, as in Amendment 1 clause 7. Package code gets scoped, short-lived connection handles, and no refresh tokens or client secrets enter the engine. **Plain HTTP:** the broker injects credentials only into HTTPS (TLS-verified) requests. A plain-HTTP destination is refused **before** any credential or auth header is attached. That covers a redirect from HTTPS down to HTTP. **Redirects:** on a redirect that changes the origin (scheme, host or port), the broker and proxy strip every injected credential and auth header (`Authorization`, `Proxy-Authorization`, `Cookie` and injected API-key headers) before following it. A redirect target that isn't on the instance's allowlist, or is a prohibited destination, is rejected. Covered by `D-CP-04`.
   - **Managed-identity blocking stays:** the instance metadata endpoint and the platform identity token endpoint are blocked from the workload at the network layer. The engine gets its own secrets through platform-resolved Key Vault references, so it needs no runtime token endpoint. Clause 12 makes "the engine still starts and runs with the endpoint blocked" a precondition.
   - **No route into Control or other customers** (wording accepted by the CEO, 15 Oct): a customer package can't reach Control's management plane, internal or private endpoints, the provider subscription or another customer's resources. Control's **public** endpoints that the engine legitimately uses (for example handoff redemption) stay internet-reachable, as for any client. They accept only credentials bound to this one instance, so a package can do no more there than its own instance already could. Anything an engine reports to Control is treated as untrusted input from that instance.
3. **Reproducible package set, per-org store and provenance.** Customer packages are pinned by package id, version and SHA-512 over the full dependency closure in the instance's resolved plan (the immutable lock). The resolved plan plus the governed image digest defines the revision. Floating versions, runtime resolution from public feeds and public-feed fallback are not allowed. For Dedicated customer packages, this lock **replaces the "immutable image" requirement** in the original decision. The governed Valence image is never rebuilt or mutated per customer. Rollback restores a previous lock.
   - **Per-org package store.** Package bytes come only from a content-addressed package store (feed) that belongs to one organization. Each instance reads its org's store with an instance-unique, read-only credential (`D-CP-01`). There are no cross-org reads: one org's instances, users and API calls can't list, resolve or pull another org's packages, even when they know the digest. Two orgs that use the same community package each hold their own copy, or at least their own authorization record, and every read is authorized per org.
   - **How packages get in.** A customer uploads a package, or imports one with its full dependency closure from a named source feed, into the org store. An import happens once, at import time, never at load. The engine never reaches a public feed.
   - **Provenance on each lock entry:** the uploader (actor), the source (upload or the source feed URL), the SHA-512 digest and the import time. Control records this, not the engine.
   - **Signature check.** Where a package carries an author or repository signature, it's verified on import, and an invalid signature is rejected. An unsigned package is allowed but recorded as unsigned. A signature proves identity, not safety.
   - **Malware scan.** Every package and dependency is scanned on import, and a detection blocks it from any lock. The scan is a governance step, **not a boundary**. The per-customer engine (clause 2) stays the boundary.
   - Proven by `D-CP-07`.
4. **Load mode.** **Activities only** (CEO, 15 Oct): no `HostIntegrated` load mode, hosted services, HTTP endpoints or EF migrations from customer packages. Full host integration needs its own later amendment. Unloading is not a security control on Dedicated. The engine is the boundary, so a package change becomes a new revision or engine restart.
5. **Resource limits.** Per-instance CPU, memory, replica, request and execution-time limits come from the entitlement-backed profile (#349). A customer package can exhaust only its own instance. It can't take capacity from other customers or from Control (D-06).
6. **Kill switch, fleet-wide digest denylist and revocation.**
   - **Per instance:** operators can disable a customer package or an instance's whole customer-package capability. The effect is fail closed on the next revision, with a forced revision restart for a security incident.
   - **Fleet-wide digest denylist.** Valence runs a signed denylist of package digests (SHA-512, any package or dependency in a closure) that applies to every Dedicated engine in every org. It follows Amendment 1's revocation pattern: signed under the Amendment 1 clause 3 key rules with its own key purpose, delivered by **push plus a poll every ≤60 s**, with a **hard TTL of 120 s** (2× the poll) and no grace. Past the TTL, the engine host refuses new customer-package loads and faults in-flight runs of customer packages, as Amendment 1 clause 9 does for allowlisted packages. Built-in and Valence-approved packages keep running.
   - **Enforcement outside the engine.** A package running in the engine has the engine's full trust and could tamper with an in-engine check, so the in-engine check is defence in depth only. Control refuses to resolve a plan or lock that contains a denylisted digest (`PackageDigestDenylisted`), and the org stores (clause 3) refuse to serve it.
   - **Bounded forced restart.** A **security** denylist entry takes effect within **60 s of publication**. Engines that loaded the digest have in-flight executions using it faulted (`PackageRevisionRevoked`), and Control force-restarts them to a revision whose lock excludes it. Any replica still running the denylisted digest **120 s after publication** is stopped (scaled to zero) until that revision is healthy. The customer is told which package was denylisted and why.
   - **Who adds an entry:** a recorded security reviewer, through a reviewed, append-only change, as for Amendment 1's revocation list. The denylist matches digests, so it hits the same community package in every org at once.
   - The audit records the actor, reason, digests and affected instances. Revocation and denylisting never delete customer data.
   - Proven by `D-CP-08`.
7. **Support posture.** Valence supports the platform, not the behaviour of customer packages. Support elevation into an engine running customer packages follows D-09 and treats the engine as possibly hostile: no operator credential is typed or pasted into it.
8. **Shared is unchanged.** Shared stays limited to the in-house Valence-built, signed-allowlist packages of Amendment 1. Customer, learner and community packages never load on Shared, and Shared customers can't upload packages (Amendment 1 clause 10).
9. **Customer images are unchanged (confirmed by the CEO, 15 Oct).** Customer packages loaded into Valence's governed image are a separate class from customer-built **images**. Customer-built images stay BYO-only under the #629 decision. This amendment is the isolation ADR for packages on Dedicated only. It doesn't authorise customer images on Valence Hosted.
10. **Lab shard is out of scope; ADR-0017 governs it.** The lab shard (#794) co-hosts several learners' packages in one engine, with per-tenant collectible ALCs, so it isn't isolation between learners and this amendment's per-customer boundary doesn't apply to it. Amendment 1 clause 10 already admits learner packages on the lab shard. Lab placement is governed by **ADR-0017 (#809)**: labs run only in lab-only orgs that never hold a commercial provider, invariant **I9** keeps paying tenants, customer data and secrets off lab shards, and the lab class is not the Shared profile and makes no Shared claims. ADR-0017 is **Proposed**, and lab launch waits until it's accepted. This amendment adds no lab precondition (Elsa Control Lead, 15 Oct 2026).
11. **Prices:** none here. Packaging and pricing of `packages.customer` is a commercial decision outside this ADR.
12. **Preconditions before any customer package is admitted on Dedicated.** None of these is a follow-up:
    - **network:** D-04 and #351 implemented for Dedicated: default-deny egress through the shared egress proxy with per-tenant policy (no bypass route), private endpoints for SQL and Key Vault, and no lateral route. Proven by `D-CP-02`.
    - **identity:** the identity-endpoint block is live, and the engine runs with it in place (`D-CP-03`).
    - **credentials:** the credential broker is live for Dedicated (`D-CP-04`).
    - **no shared credentials:** an inventory proves every engine credential is instance-unique, with no Control or fleet secret in the engine (`D-CP-01`).
    - **separation:** two organizations' instances show no cross-access over API, runtime, database, Key Vault, network or telemetry while running a hostile canary package (`D-CP-05`).
    - **admission:** #221's admission is extended with `packages.customer`, opt-in and lock checks, and still fails closed without them (`D-CP-06`).
    - **package store:** per-org stores with provenance, signature checks and malware scan are live, and cross-org reads fail (`D-CP-07`).
    - **denylist:** the signed fleet-wide digest denylist, its 120 s hard TTL and the bounded forced restart are live (`D-CP-08`).
    - **backups:** the engine identity can't delete or change backups or PITR data (`D-CP-05`).
    - **resource exhaustion:** D-06 evidence covers a hostile package.
    - **threat model** (`docs/product/isolation-threat-model.md`): the Dedicated contract, package policy and D-03 claim are updated as listed below, and the `D-CP-*` rows pass.
    - **customer terms and the product page** say plainly that customer packages run with full engine trust, that the boundary is the customer's own engine, and that Valence doesn't sandbox package code. **The opt-in terms need a legal review before GA.** That review is a GA precondition and doesn't block merging this amendment as Proposed (CEO, 15 Oct).

### Threat-model updates (land with acceptance)
- **Profile contracts, Dedicated:** extension policy becomes: "Producer-built and Valence-approved; customer packages (`packages.customer`, opt-in) after the `D-CP-*` gates; boundary is the per-customer engine". The non-guarantees gain "no in-engine isolation between packages, or between a customer's packages and that engine's data and secrets".
- **Package policy, arbitrary customer, Dedicated column:** "Rejected at launch" becomes "Allowed only with entitlement, opt-in, pinned lock and passed `D-CP-*`".
- **D-03** is re-worded from "cannot affect the launch fleet" to "is confined to its own customer's engine: no other-customer, Control or provider reach".
- **Hostile-package attack paths (secret exfiltration, cross-tenant access, resource exhaustion):** add the Dedicated customer-package case. Self-scoped exposure of the instance's own secrets and data is **accepted residual risk**. Cross-customer and control-plane exposure is mitigated by clause 2.
- **New tests `D-CP-01` to `D-CP-08`:** added to the executable test matrix, marked proposed until acceptance.
- **New actor nuance:** community and learner package authors are third parties to the customer who installs them. The customer's opt-in is the trust decision, and Valence does not vet their behaviour.

### Residual risk (stated plainly)
- **Inside its engine, a customer package has everything that engine has.** That means the instance's database, its Elsa identity signing key, its admin credential, its workflow data and its handoff sessions. A malicious community or learner package can read or destroy that customer's own data and act as that engine. This amendment doesn't reduce that risk. It contains it to the customer who opted in.
- The boundary is the engine and its network, identity and credential isolation, enforced by the Azure platform and our provider templates. A container escape or a cloud-provider compromise is outside the product guarantee, as it already is for Dedicated today.
- Control's public endpoints stay reachable from the internet, including from package code. Safety there relies on instance-bound credentials and on treating engine input as untrusted.
- **The shared egress proxy and credential broker are shared components.** They hold every Dedicated customer's egress policy and inject every customer's connection credentials (Amendment 1 clause 7). A bug or compromise there could expose one customer's credentials or traffic to another, or apply the wrong policy. Mitigations: each tenant's credentials are encrypted under a per-tenant key and fetched per request for that tenant only; the proxy authenticates each instance's traffic; the proxy and broker run outside every engine, with their own identity and audit log; and per-instance enforcement (clause 2) is the later premium option that removes the shared policy point. This is accepted for cost (CEO, 15 Oct).
- The malware scan and signature checks (clause 3) catch known-bad and tampered packages only. A novel malicious package passes them, and the per-customer engine is what contains it.
- **The engine can reach Azure management with customer-supplied credentials, by design.** A customer package can use any Azure credential the customer configures to act on that customer's Azure. It can't reach Valence's subscription, because the engine holds no Valence or provider credential and no managed identity (clause 2).
- Between a denylist publication and the forced restart (clause 6), a denylisted package can keep running in an engine for up to the stated bound.

### Alternatives considered
- **Keep the prohibition (status quo).** Rejected: it contradicts Sipke's 13 Oct decision, and it pushes every custom-code need to BYO even when the customer accepts the risk on their own engine.
- **Treat a per-tenant ALC or the IL pre-scan as the boundary, and co-host customers in one engine.** Rejected: an AssemblyLoadContext isn't a security boundary (TB-03), and the pre-scan is bypassable by reflection, dynamic code and native interop.
- **Require a Valence-built customer image per customer (the original "immutable image" route, #358).** Rejected for packages: it puts build compute and registry authority on Valence, which the #629 decision rejected. A pinned package lock in the resolved plan gives reproducibility and rollback without a per-customer image.
- **A per-package sandbox inside the engine** (worker process per package, ACA dynamic sessions, gVisor). Rejected for now: it means an IPC redesign of activity execution plus extra cost and latency for every run. The per-customer engine already confines the risk to the customer who chose it. It stays the path if in-engine isolation between a customer's own packages is ever required.
- **Valence review of every customer package (allowlist like Shared).** Rejected: there's no review capacity for arbitrary third-party code, and a review would imply a safety claim we can't make. Review stays Shared-only (Amendment 1).

### Consequences
- Dedicated gains a custom-code path without an in-process sandbox claim. The security work moves to network, identity and credential isolation per instance, most of which Dedicated needed anyway (D-04, D-08, #351).
- #221's admission code gains a third admitted class on Dedicated, gated by entitlement, opt-in and lock. All of its existing fail-closed tests stay.
- The Private profile (#355) is narrowed. What remains for Private is a stronger boundary than Dedicated (customer-controlled network, dedicated hosts or the isolated tier), and that is the future isolated-tier ADR.

### Proofs and acceptance criteria
1. **`D-CP-01` no shared credentials:** an inventory of the engine's environment, mounted files, Key Vault and database logins shows only instance-unique credentials, with no Control, fleet, feed or telemetry secret. Proof: an automated inventory diff across two instances.
2. **`D-CP-02` egress:** a hostile canary package can reach its own instance's declared hosts only, through the shared proxy. Direct sockets that bypass the proxy, hosts declared only by another instance, undeclared hosts, other instances, Control's private endpoints and other customers' SQL and Key Vault all fail at the network layer. **Negative cases with a declaration:** the tenant declares each prohibited destination (a Control management or private endpoint, a Valence provider or IaC endpoint, the metadata and identity endpoints, and another customer's instance, SQL, Key Vault and store). Plan resolution refuses each one with `egress.destination.prohibited`, and with the plan check bypassed, the proxy still denies it. Proof: packet and denial evidence.
3. **`D-CP-03` identity:** requests from package code to the instance metadata and identity token endpoints are blocked, and the engine passes health with the block in place. Proof: a runtime test.
4. **`D-CP-04` credential broker:** no refresh token or client secret is present in engine memory or environment, and package code gets handles only. A redirect to a different origin doesn't receive the original credentials or auth headers, and a redirect to an undeclared or prohibited host is rejected. A brokered call to a plain-HTTP destination, or one redirected from HTTPS to HTTP, is refused before any credential is attached. Proof: an inspection plus SDK contract tests, including the redirect cases.
5. **`D-CP-05` cross-customer and backups:** organization A's hostile package can't read or change organization B's API data, runtime, database, secrets, telemetry or backups. Acting as its own engine identity, it also can't delete, shorten the retention of, or overwrite its own instance's backups or PITR data. Proof: an automated two-organization negative matrix plus a role-assignment check.
6. **`D-CP-06` admission:** a customer package without the entitlement, opt-in or a pinned closure is rejected before any provider mutation. A caller-supplied classification can't upgrade it, and Shared and Elsa 3.8 Dedicated instances still reject it. Proof: API, lifecycle and provider tests extending #221's suite.
7. **`D-CP-07` package store:** organization A's instances, users and API calls can't list, resolve or pull organization B's packages, even with the exact id, version and digest. Every lock entry carries the uploader, source and digest. A package with an invalid signature or a malware detection never reaches a lock. Proof: an automated cross-org isolation test plus import tests.
8. **`D-CP-08` digest denylist:** a security denylist entry for a digest loaded in two orgs' engines faults in-flight executions using it and restarts both engines to a revision without it within 60 s of publication; no replica runs it 120 s after publication. Control refuses a plan containing it, and the store refuses to serve it. A denylist older than 120 s makes the host refuse new customer-package loads and fault in-flight customer-package runs. Proof: a staging live run plus a stale-list test.

**Merge gate:** Code Review APPROVE on the exact head plus green CI.

### Resolved (CEO, 15 Oct 2026)
- **Load mode:** activities only. Full host integration needs its own later amendment (clause 4).
- **#629 alignment:** customer packages in Valence's governed image are separate from customer-built images. Customer-built images stay BYO-only (clause 9).
- **Egress:** a shared egress proxy with per-tenant policy, chosen for cost. Per-instance enforcement is a later premium option (clause 2).
- **Opt-in terms:** legal review before GA. It doesn't block merging this amendment as Proposed (clause 12).
- **"No route into Control":** covers the management and private plane only. Public endpoints rely on credentials bound to one instance (clause 2).

### Resolved (Elsa Control Lead, 15 Oct 2026)
- **Lab shard:** not gated by this amendment. Labs are governed by ADR-0017 (#809, Proposed): lab-only orgs, invariant I9 and a non-Shared lab class (clause 10). Lab launch waits until ADR-0017 is accepted.

### Resolved (Sipke, 15 Oct 2026)
- **Customer-written packages:** allowed on Valence Dedicated, isolated per customer, activities only. Recorded in valence-works/strategy PR #12 (`decisions/2026-10-15-customer-packages-on-dedicated.md`).
