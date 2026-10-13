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
