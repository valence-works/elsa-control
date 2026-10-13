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

## Amendment 1 (2026-10-13): Signed allowlist for Valence-verified activity packages on Shared and Data-isolated

Status: Accepted (pending merge). Accepted by the CEO on 2026-10-13 under Sipke's decision 1 on #795.

## Context
- **ADR-0009 today:**
  - Customer packages are executable code.
  - Arbitrary customer packages are prohibited on Shared and Data-isolated "unless a separately reviewed sandbox proves an adequate boundary".
  - Valence-approved packages are limited to Dedicated and Private.
  - Built-in packages may appear on any profile.
  - Private custom code uses an immutable image workflow.
- **#110 (threat model):** hostile-package attack paths include reading process state, secrets and files; network probing and exfiltration; and resource exhaustion.
- **#355:** the Private profile and extension-policy matrix. `packages.arbitrary` is Private-only.
- **#794 (Training labs):** learner packages load at runtime on a **dedicated lab shard** only, with per-tenant collectible ALCs, an IL pre-scan, an audit trail and a kill switch.
- **#795 (Elsa Integrations):** decision 1 (Sipke, 13 Oct 21:37 CEST). Valence-verified, signed packages may run on Shared. Learner and community packages stay dedicated-only (lab shard, Dedicated, BYO).
- **An AssemblyLoadContext is not a security boundary** (.NET Core and later: no CAS, no AppDomain sandbox). Per-tenant ALCs give functional isolation only (types, activities, descriptors). Any package on Shared therefore runs with full process trust. This amendment is a **supply-chain and review control, not isolation**.

### Decision
1. **Shared allowlist.** A package may load on Shared only if it is on the **Valence-signed Shared allowlist**. The allowlist entry is `{packageId, version, SHA-512 hash, tier, publisher, reviewer of record, review date}`. The allowlist itself is signed by Valence CI and versioned.
2. **Signatures.** The package needs an author signature **and a Valence repository countersignature**. The hash is pinned in the allowlist. Floating versions are not allowed.
3. **Load-time enforcement.** The host verifies the countersignature, the hash against the allowlist, and allowlist freshness before loading. It **fails closed** on any mismatch, on expiry, or on an unreachable or stale allowlist past its grace TTL.
4. **Activities only.** No DI-host integration, hosted services, HTTP endpoints, EF migrations or `HostIntegrated` load mode on Shared. Loading is collectible per tenant (#794 loader).
5. **Egress only to declared hosts,** through the platform `HttpClient` handler. Hosts are declared in the manifest and reviewed. Other outbound access is a review failure.
6. **Credentials as connection handles only:** scoped, short-lived handles (#795 §2). No raw secrets, refresh tokens or client secrets reach package code. No managed-identity access is exposed to packages.
7. **Revocable** through the kill switch per version, package and publisher (a signed revocation list, polled every ≤60 s). Revocation removes the package from the allowlist and unloads it, or recycles the replica. Affected workflows fail closed. A **security revocation** takes effect immediately and fails closed. A **non-security deprecation** gets 14 days' notice before removal from the allowlist.
8. **Community and learner packages never load on Shared.** They load only on the lab shard, Dedicated or BYO, under #355 entitlements.
9. **Paying Shared customers cannot upload packages.** On Shared they only select allowlisted packages.
10. **Security-review gate (binding).** "Verified" requires a **security review of untrusted code**, which is separate from shared Code Review (correctness). Until the pipeline has a named security-review step, only **Valence-built packages from the Integrations lead or connector builder, built and signed in Valence CI**, may enter the Shared allowlist. The step needs:
    - a written checklist;
    - a scan gate (IL pre-scan, dependency/CVE, license, manifest and egress diff);
    - a **reviewer of record**.

    Third-party Verified-on-Shared opens only once that step is live and evidenced. Every new version needs a fresh review; nothing is inherited.

    **Reviewer of record:** the Elsa Control Architect, who must not be the package's author. The security sign-off is a separate recorded step from the Code Review `**Verdict: APPROVE**` and doesn't replace it. Opening the allowlist to third-party packages additionally requires Sipke to name an external or human security reviewer first.
11. This amendment changes ADR-0009's profile matrix as follows:
    - Valence-built and Verified packages are permitted on Shared **and Data-isolated** **only** under 1–10.
    - Arbitrary customer packages remain prohibited on Shared and Data-isolated.
    - The immutable-image rule for Private custom code is unchanged.

### Alternatives considered
- **No packages on Shared ever.** Rejected: it blocks first-party connectors (#795) on the entry tier and forces Dedicated for basic integrations.
- **Any signed package on Shared** (author signature only). Rejected: a signature proves identity, not safety. Compromised or malicious publishers would get process trust on a multi-tenant shard.
- **Per-tenant process or sandbox isolation on Shared** (worker process per tenant, ACA dynamic sessions, gVisor). Rejected for now: high cost and latency per execution, IPC redesign of activity execution, and a conflict with the Shared price point (€19/month). It stays the path if third-party volume or a security incident demands it, and it would satisfy ADR-0009's "reviewed sandbox" clause.

### Consequences
- Shared gains a narrow, reviewed extension surface. Residual risk: a reviewed package or the signing pipeline could be compromised. This is mitigated by hash pinning, re-review per version, revocation, and least-privilege Shared workloads (no secrets in the environment beyond what the engine needs).
- **Prerequisites:**
  - a security-review capacity (reviewer of record);
  - signing keys in an HSM-backed Valence CI;
  - an allowlist service and revocation channel;
  - the #794 per-tenant collectible loader;
  - per-tenant load and unload in elsa-foundation (the critical path).
- The isolation threat model (`docs/product/isolation-threat-model.md`) must add the Shared-allowlist claim and non-claims and the promotion gates.

### Proofs and acceptance criteria
1. **Unsigned, author-only, or wrong-hash package.** Shared refuses the load and logs an audit event. Proof: automated negative tests.
2. **Allowlisted package.** It loads for one tenant only, and other tenants can't resolve its types. Proof: an isolation test (as #794 AC1).
3. **Revocation.** The package is unloaded or the replica recycled within ≤60 s, and workflows fail closed. Proof: a staging QA live run.
4. **Egress.** A call to an undeclared host through the platform handler is blocked. Review rejects packages that open sockets directly. Proof: a test plus the checklist record.
5. **Credentials.** Package code obtains only a scoped handle. Refresh tokens and secrets are never present in package memory paths exposed by the SDK. Proof: an SDK contract test.
6. **Gate.** The allowlist build rejects a third-party package while the security-review step isn't live, and accepts a Valence-CI-signed package from the lead or builder. Proof: a CI policy test.
7. **Uploads.** A Shared customer can't upload a package. Proof: an API negative test.
8. **Stale allowlist.** A stale allowlist past its TTL fails closed. Proof: a test.

**Merge gate:** Code Review APPROVE on the exact head plus green CI. Accepted by the CEO; merges under the standard Code Review rule.

### Resolved
- Reviewer of record: Elsa Control Architect (not the author).
- Data-isolated follows Shared.
- Merges under the standard Code Review rule.
- Still to set during implementation: allowlist grace TTL.
