# Elsa Commercial Platform Isolation Threat Model

Date: 2026-08-29

Issue: [#110](https://github.com/valence-works/elsa-control/issues/110)

Status: launch decision supported; executable proof still required

## Executive determination

The Dedicated-first launch recommendation remains valid. It is the narrowest
profile that can support a managed Elsa workload without making an unproven
cross-customer executable-code claim:

- one Elsa runtime application and one Elsa database are provisioned for each
  instance;
- the launch image and extension surface are producer-built or Valence-approved;
- arbitrary customer NuGet packages are rejected at planning/admission time;
- image, artifact, secret, identity, network, telemetry, backup and support
  boundaries are verified before the profile is advertised.

Dedicated means a dedicated workload/database allocation in the managed product
contract. It does **not** mean a dedicated physical Azure host, a complete
customer-controlled network, immunity from a cloud-provider compromise, or an
in-process sandbox for .NET assemblies. A package that is loaded into the Elsa
process has that process's permissions; package approval and vulnerability
scanning are governance controls, not a runtime isolation boundary.

This document is the security gate for the initial profile. It does not authorize
Shared, Data-isolated or arbitrary-package launch (ADR-0009 Amendment 1 permits
in-house allowlisted packages on Shared only after the `SH-*` and `SH-AL-*` gates pass). Those profiles remain closed
until the profile-specific tests below produce signed evidence. A failed gate is
an escalation to the program lead, not a marketing exception.

## Evidence boundary and assumptions

The assessment combines the accepted product decisions and current repository
evidence with the following authoritative platform references:

- [ADR-0009: extension code isolation policy](../adr/0009-extension-code-isolation-policy.md)
- [current-state assessment](current-state-assessment.md)
- [commercial image release and topology authority audit](commercial-image-release-audit.md)
- [repository responsibilities and trust boundaries](repository-responsibilities.md)
- [ADR-0012: durable artifact storage and provenance](../adr/0012-artifact-storage-and-provenance.md)
- [runtime transport trust policy](../runtime-transport-trust-policy.md)
- [Azure Container Apps security overview](https://learn.microsoft.com/en-us/azure/container-apps/security)
- [Azure Container Apps environments and boundaries](https://learn.microsoft.com/en-us/azure/container-apps/environment)
- [Azure Container Apps revisions](https://learn.microsoft.com/en-us/azure/container-apps/revisions)
- [Azure Container Apps networking](https://learn.microsoft.com/en-us/azure/container-apps/networking)
- [Azure Container Apps secrets](https://learn.microsoft.com/en-us/azure/container-apps/manage-secrets)
- [NuGet restore and security auditing](https://learn.microsoft.com/en-us/nuget/consume-packages/package-restore)
- [.NET restore security auditing](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-restore)

The Azure documentation describes platform capabilities, not an Elsa Control
guarantee. Every claim below remains conditional on the exact provider template,
permissions, image, configuration and runtime behavior passing the executable
tests. The #108 proof established the signed-image, Azure SQL, identity/secret,
health-gated revision and cleanup foundation. The production Azure workload
provider and the multi-instance isolation evidence below do not yet exist
([#108 conclusion](../spikes/108-azure-workload-provider-preflight.md)).

## Assets and actors

### Assets to protect

| Asset | Confidentiality / integrity / availability concern |
|---|---|
| Workflow definitions, variables, execution state and customer data | Cross-instance disclosure, tampering, deletion and unbounded resource use |
| Package and image inputs | Malicious code, dependency confusion, mutable tags and compromised build output |
| Secret values and identity tokens | Workload or support paths reading or exfiltrating credentials |
| Control-plane records | Organization/workspace authorization, desired state, deployment history, provider metadata and audit evidence |
| Runtime network and data services | Lateral movement, unauthorized egress/ingress and abuse of reachable dependencies |
| Telemetry, diagnostics and support evidence | Tenant mixing, secret/payload disclosure and unauthorized operator access |
| Backups and artifacts | Offline disclosure, restore-time cross-tenant access, retention/deletion failure |
| Provider subscription and capacity | Resource exhaustion, unexpected spend and destructive control-plane actions |

### Actors and trust assumptions

| Actor | Security posture assumed for this model |
|---|---|
| Customer user | Authenticated but may be malicious within their organization; authorization is checked server-side |
| Customer package author | Hostile by default when package is arbitrary; can intentionally inspect process, files, environment, network and resources |
| Customer organization administrator | Trusted to administer that organization, not other organizations or the platform |
| Compromised dependency or package feed | May supply a package that passes compatibility checks but performs hostile runtime behavior |
| External attacker | May attack public endpoints, credentials, artifacts, provider APIs or exposed dependencies |
| Valence operator/support user | Privileged but not implicitly trusted; elevation is scoped, time-bound where possible and audited |
| Elsa Control service identity | Platform-managed identity with only the provider/data operations it needs |
| Cloud provider | Infrastructure trust boundary; Azure platform compromise is outside the product guarantee |

## Trust boundaries

| ID | Boundary | Required rule |
|---|---|---|
| TB-01 | Browser/client to Elsa Control | Derive organization/workspace/instance from trusted identity; never trust caller-supplied ownership IDs |
| TB-02 | Elsa Control to provider/IaC | Provider commands are scoped to an authorized instance and use safe metadata; no raw secrets or workflow payloads in history |
| TB-03 | Runtime process to package/extension | Treat loaded package code as same-privilege code; no `AssemblyLoadContext`, Nuplane policy or approval label is considered a sandbox |
| TB-04 | Runtime to database | Use an instance-scoped identity/database; reject cross-instance identifiers and direct shared credentials |
| TB-05 | Runtime to secret provider | Resolve only declared references with least-privilege identity; do not expose broad platform credentials to workload code |
| TB-06 | Runtime to network | Ingress and egress are explicit provider policy; deny lateral access by default and authenticate every control/data dependency |
| TB-07 | Runtime to telemetry/support | Correlation and tenant labels are mandatory; payloads, tokens and secret-like values are redacted before storage or display |
| TB-08 | Artifact/image/package supply chain | Admit only immutable digests with verified provenance, signature, SBOM and vulnerability policy evidence |
| TB-09 | Backup/restore | Backups are encrypted, access-scoped and restored into a new instance without mixing organizations or secret values |

## Profile contracts

These are product contracts, not descriptions of what Azure supplies automatically.
“Launch” in the table means the contract may be advertised only after the
corresponding tests pass in the production provider path.

| Profile | Compute and data boundary | Secret/network/telemetry boundary | Extension policy | Explicit non-guarantees and gate |
|---|---|---|---|---|
| Shared | Deferred pooled runtime and pooled data design; no customer may infer process isolation | Must prove organization/instance data, secret, network and telemetry isolation plus noisy-neighbor controls | Built-in, plus in-house Valence-built allowlisted packages under ADR-0009 Amendment 1 once `SH-AL-*` pass; arbitrary packages prohibited | Allowlisting is a supply-chain and review control, not process isolation; no shared-compute or arbitrary-code claim before `SH-*` tests and cost/abuse evidence pass |
| Data-isolated | Deferred pooled compute with a dedicated database/data boundary per instance | Must prove pooled compute cannot cross data, secret, network or telemetry boundaries and that one tenant cannot starve another | Built-in only unless sandbox evidence exists; arbitrary packages prohibited | A separate database does not prove process or noisy-neighbor isolation; all `DI-*` gates required |
| Dedicated | First paid profile: one managed Elsa runtime application and one database per instance; physical host remains provider-managed | Instance-scoped workload identity, secret references, explicit ingress/egress, tenant-scoped telemetry, tested backup/restore | Producer-built and Valence-approved extensions only at launch; arbitrary packages prohibited | Not a physical-host, private-network or arbitrary-code sandbox; all `D-*` launch gates required |
| Private | Customer-specific isolated compute, persistence, secret and advanced network boundary; exact placement must be provider/customer-agent evidence | Dedicated identity and secret store, customer-controlled or explicitly isolated network, tenant-scoped operations and backups | Arbitrary packages permitted only through reproducible immutable image/artifact and provenance pipeline | No promise until `P-*` attack tests, rollback and operational review pass; a stronger claim requires explicit ADR |

The Dedicated baseline is intentionally narrower than “all custom code works.”
It provides an operable managed runtime while the Private workstream proves the
additional boundary required for hostile customer code.

## Package policy

Package classification is an admission policy and provenance record. It never
changes the fact that a loaded assembly is executable code.

| Class | Meaning | Shared / Data-isolated | Dedicated launch | Private |
|---|---|---|---|---|
| Built-in | Included in the producer-owned immutable image and its release manifest | Allowed after image gate | Allowed after image gate | Allowed after image gate |
| Valence-approved | Reviewed source/package identity, compatibility, vulnerability and provenance evidence; included through a controlled immutable build | Shared: in-house Valence-CI-built packages on the signed Shared allowlist only, after `SH-*` and `SH-AL-*` pass (ADR-0009 Amendment 1). Data-isolated: allowed only if the profile's non-code boundary is independently proven | Allowed at launch, subject to image and package gates | Allowed, subject to private image gate |
| Arbitrary customer | Customer-selected package or dependency whose code is not part of the approved producer build | **Rejected** | **Rejected at launch** | Allowed only after `P-*` gates and immutable build workflow |

Required behavior:

1. Resolve versions and transitive dependencies deterministically; do not use
   mutable tags or floating package versions for a deployment.
2. Verify package/image identity, digest, provenance, signature, SBOM and
   vulnerability policy before admission. A clean vulnerability report is not
   evidence of safe runtime behavior.
3. Keep raw package payloads and secret values out of control-plane history,
   diagnostics and audit views; retain safe references and digests.
4. Keep Nuplane runtime/image-side for the initial proof. Direct Control-to-
   Nuplane orchestration is deferred until the Private/custom-package boundary
   has an explicit contract.
5. Fail closed when package class, release manifest, digest, compatibility,
   signature or isolation entitlement is missing or stale.

## Attack paths and mitigations

### Secret exfiltration

**Path.** A package reads environment variables, mounted files, process memory,
metadata endpoints or reachable services, then sends values to an external host.
An ordinary application vulnerability can produce the same path without a
malicious package.

**Required controls.** Do not load arbitrary packages outside Private. Use a
workload identity scoped to the instance's own database and declared secret
references. Keep control-plane/provider credentials out of the workload. Do not
place raw secret values in artifacts, commands, logs or telemetry. Restrict
egress and use TLS/authentication for allowed dependencies. Rotate credentials
without editing desired-state records.

**Proof.** `D-03` proves that the arbitrary canary is rejected before it can run
in a launch workload. A separate approved/built-in diagnostic component and
`P-03` run the instrumented environment/file/process, metadata and controlled
egress attempts in the appropriate test boundary. The test proves that an
allowed workload can reach only its declared secret and that the canary cannot
obtain another instance's secret, control-plane token or provider credential. A
blocked outbound sink and redacted diagnostics are assertions, not merely
observations.

### Cross-tenant data access

**Path.** A user changes an organization/workspace/instance identifier, a package
uses a shared connection or runtime API, or support tooling and backups expose
another customer's rows, blobs, logs or workflow state.

**Required controls.** Resolve authorization from trusted identity at every
control-plane and provider boundary. Use database-per-instance for Dedicated and
Private; never rely on a tenant discriminator alone for the Dedicated claim.
Use separate storage prefixes/keys and restore targets. Add tenant and instance
dimensions to telemetry queries and support tools, with deny-by-default access.

**Proof.** `D-01`, `D-02`, `D-04`, `D-05` and `P-01` seed two organizations and
instances, then attempt access through API, provider, runtime, database, backup,
artifact, telemetry and support paths. Every negative attempt must return a safe
denial or an empty result with no existence leak.

### Resource exhaustion and noisy neighbor

**Path.** A workflow or package creates unbounded CPU work, threads, memory,
child processes, disk, database activity, requests or egress, starving other
instances or causing an unexpected bill.

**Required controls.** Define per-instance CPU/memory/replica/storage/database
quotas, request/queue timeouts, concurrency limits, payload limits and provider
rate limits. Alert on saturation, error rate and spend. Stop or restart unhealthy
revisions without allowing a retry loop to amplify load. Shared/Data-isolated
must demonstrate neighbor protection; Dedicated must demonstrate its own
instance cannot exceed the entitled budget.

**Proof.** `SH-03`, `DI-03`, `D-06` and `P-04` run bounded CPU, memory, database,
network and request floods from one workload while a control workload remains
within its availability/latency envelope. The run records resource ceilings,
throttling, termination behavior, billing estimate and recovery. An Azure
Container Apps environment boundary or dedicated workload profile is not by
itself proof of an Elsa tenant budget.

### Provenance compromise

**Path.** A mutable tag, dependency-confusion package, compromised feed/builder,
tampered artifact, unsigned image or misleading version label introduces code
that the catalog or operator did not approve.

**Required controls.** The producer release manifest must bind release line,
topology, component versions, source revision, image index digest, SBOM,
provenance, signature identity and vulnerability result. Resolve by digest only;
verify the expected signer and subject; lock transitive package inputs; retain
attestation references; reject on mismatch. The producer manifest contract and
known-good verification now exist; Elsa Control catalog/provider admission enforcement
remains the launch blocker for final release admission.

**Proof.** `D-07` and `P-02` try a mutable tag, changed digest, unsigned image,
wrong signer, stale scan, dependency substitution and modified artifact. All are
rejected before deployment, and the denial contains no secret or payload. A
known-good digest must still pass health and rollback tests after admission.

## Dedicated launch claim-to-control matrix

The following is the minimum evidence register for public Dedicated claims. All
rows must be `Pass` in the provider's production-like environment; `Planned`,
`Partial` or `Not applicable` is not a launch approval.

| Claim ID | Launch claim | Concrete control | Required executable evidence | Current disposition |
|---|---|---|---|---|
| D-01 | Runtime ownership is per instance | Provider creates one workload app/revision set per instance and scopes commands by instance ID | Two instances provisioned; inspect resource IDs, command authorization and deletion behavior | Not proven; provider work pending |
| D-02 | Persistent Elsa state is per instance | Dedicated database and instance-scoped database identity; no shared launch database | Cross-instance query/credential attempts fail; migration, backup and restore-to-new-instance pass | Single-instance SQL durability passed in #147; cross-instance and restore evidence pending |
| D-03 | Arbitrary package code cannot affect the launch fleet | Admission rejects arbitrary package class; only built-in/approved immutable build is deployable | Negative package-policy test plus canary package is rejected before runtime | Catalog-to-resolver, lifecycle reservation and Azure provider-boundary rejection are automated in #221; live canary/provider evidence remains pending |
| D-04 | Secrets are not exposed through control-plane records | Secret references only, managed identity/Key Vault resolution, redaction | Seed canary secret; inspect DB/history/logs/telemetry and attempt wrong-instance read | Existing guidance; provider execution pending |
| D-05 | Organization/workspace/instance data is authorization-scoped | Server-derived identity, centralized authorization and provider/runtime scope checks | Two-organization API, runtime, artifact, backup, telemetry and support negative matrix | Workspace tests exist; managed-instance path pending |
| D-06 | One instance cannot consume unbounded shared capacity | CPU/memory/replica/storage/timeouts and database quotas plus alerting | Bounded exhaustion test with cost and recovery evidence | Proof limits configured; exhaustion, alerting and recovery evidence pending |
| D-07 | The deployed release is the approved release | Signed release manifest, immutable image/package digests, provenance/SBOM/scan verification | Tamper/mutable-tag/wrong-signer rejection and successful known-good verification | Control admission and safe evidence projection are automated by #158/#202/#221; exact production-provider evidence remains pending |
| D-08 | Network exposure is deliberate | HTTPS ingress, authenticated control path, deny-by-default private dependencies and controlled egress | Ingress/egress matrix, TLS/auth check, no metadata/lateral access | Public HTTPS/auth passed; private-dependency and egress matrix pending |
| D-09 | Health failure does not silently cut traffic | Readiness/liveness probes, revision readiness gate, traffic protection and rollback | Deploy known-bad revision; prove old good revision remains serving; restore and verify | Passed in disposable #147 proof; provider integration proof pending |
| D-10 | Operations are recoverable and auditable | Mandatory backup, restore-to-new-instance, retention, safe diagnostics, scoped support elevation | Backup/restore exercise meets 24-hour RPO and 4-hour RTO targets where claimed; inspect audit trail | Product target; implementation/evidence pending |
| D-11 | Telemetry does not become a cross-tenant leak | Organization/instance dimensions, redaction, access-filtered logs/traces and support views | Query as customer A/operator B; verify only permitted records and no payload/secret leakage | Existing redaction guidance; managed telemetry pending |
| D-12 | Control-plane interruption does not corrupt workload state | Durable desired state/provider commands and idempotent reconciliation | Interrupt/retry/apply same revision; prove no duplicate/destructive apply and safe recovery | Runbook reapply/recovery passed; durable provider lifecycle pending |
| D-13 | Deprovisioning honors retention and ownership | Explicit delete confirmation, backup/export policy, artifact/blob ownership and purge audit | Delete one instance; verify other instance remains; inspect retention and provider resources | Product target; implementation/evidence pending |

The matrix intentionally includes operational and supply-chain claims. A
Dedicated runtime/database pair without D-04, D-07, D-09 and D-10 would be an
incomplete managed-service boundary.

## Executable test matrix

Run these tests against the provider/IaC path and against the highest useful
external seam. Unit tests can support a row but cannot close it alone.

| Test ID | Profile(s) | Setup and assertion | Evidence retained |
|---|---|---|---|
| SH-01 | Shared | Two organizations cannot read or mutate one another's API, artifacts, commands, logs or audit records | Test run, safe denial codes and query audit |
| SH-02 | Shared | Package policy rejects arbitrary packages and no assembly outside the signed Shared allowlist is loaded in pooled compute | Admission record and runtime package inventory |
| SH-03 | Shared | CPU/memory/request/database flood from A cannot breach B's availability/latency/quota envelope | Resource graphs, limits, alerts and recovery |
| SH-04 | Shared | Secret, metadata, private network and telemetry reads from A are denied for B | Identity policy, denied requests and redacted logs |
| SH-AL-01 | Shared | Unsigned, author-only, wrong-hash, wrong-dependency-hash or missing-provenance package is refused with audit and tenant-visible error | Negative test run |
| SH-AL-02 | Shared | Direct socket from package code to an undeclared host and any IMDS/MSI request are blocked at the network layer | Packet/denial evidence |
| SH-AL-03 | Shared | No secret, refresh token or client secret is present in engine memory or environment; packages see only connection handles; no cross-tenant handle or memory exposure via SDK | Memory/env inspection and SDK contract test |
| SH-AL-04 | Shared | Revoked package: new loads fail closed immediately; loads and in-flight runs fail closed once the revocation list exceeds its 120 s hard TTL; security revocation applies within 60 s; unconfirmed unload recycles the replica | Staging live run |
| SH-AL-05 | Shared | Allowlist governance rejects non-in-house entries, missing reviewer-of-record records and reviewer = author or CI triggerer | CI policy test |
| DI-01 | Data-isolated | A cannot access B's dedicated database, storage, backup or restore target using API, runtime or provider identity | Database/identity ACLs and negative matrix |
| DI-02 | Data-isolated | Pooled compute process and network cannot cross customer boundaries | Network traces, endpoint ACLs and process/resource inspection |
| DI-03 | Data-isolated | A's exhaustion is throttled/terminated while B remains within objective | Load test and quota/recovery report |
| D-01 | Dedicated | One runtime app/revision set and one database are created per instance, with no shared launch credentials | Provider plan/resource inventory and DB ACL evidence |
| D-02 | Dedicated | Cross-instance API/runtime/database/artifact/backup/telemetry access fails closed | Automated two-instance negative test |
| D-03 | Dedicated | Arbitrary package is rejected before runtime; an approved diagnostic canary receives only declared secrets and no other-instance/control-plane/provider credentials | Admission result, canary result, secret ACLs and redaction scan |
| D-04 | Dedicated | Declared egress works; metadata, lateral/private targets and undeclared hosts do not | Network policy and packet/HTTP evidence |
| D-05 | Dedicated | Known-bad revision fails readiness and does not receive traffic; known-good revision rolls back | Revision events, endpoint checks and provider state |
| D-06 | Dedicated | Resource ceilings, retry bounds, cancellation and recovery hold under hostile workload | Resource/cost/latency graphs and post-test health |
| D-07 | Dedicated | Unsigned, wrong-signer, changed-digest, mutable-tag or stale-scan inputs are rejected | Admission decision and signed verification record |
| D-08 | Dedicated | Backup restores to a new isolated instance and does not copy secret values or another tenant's data | Restore manifest, isolation test and RPO/RTO timestamps |
| D-09 | Dedicated | Support elevation is scoped, expires/revokes, is customer-visible as allowed and leaves an audit trail | Role/elevation events and access query |
| D-10 | Dedicated | Control-plane interruption and repeated apply remain idempotent; workload remains safe | Reconciliation timeline and final state hash |
| P-01 | Private | Arbitrary package can read only the explicitly granted instance resources and cannot cross the private boundary | Hostile package test and identity/network evidence |
| P-02 | Private | Reproducible build from locked inputs yields expected digest; tamper/rollback/revocation fail closed | Build attestation, SBOM, signature and admission log |
| P-03 | Private | Secret, metadata, filesystem, process, network and telemetry canary attempts remain inside the declared boundary | Full canary report with safe output |
| P-04 | Private | Resource exhaustion is bounded and cannot affect control plane or other customer workloads | Isolation/load report, quota events and recovery |
| P-05 | Private | Immutable revision rollback and backup restore work after hostile-package failure | Revision/restore audit and recovery times |

## Operational controls and release gates

### Admission and release

- A resolved plan contains exact release line, topology, feature set, package
  class, package lock/digest, image digest, provider profile and entitlement.
- The provider refuses a plan with a mutable tag, missing release manifest,
  unverified signature/provenance/SBOM, incompatible package, stale scan or
  forbidden package class.
- CI builds and scans the exact artifact/image that will be deployed. Promotion
  records the same digest; a later registry lookup cannot silently change it.
- Emergency release overrides require a named approver, reason, expiry and audit
  event. They cannot bypass the arbitrary-package or isolation-profile gate.

### Runtime and provider hardening

- Run without privileged containers or host mounts; use non-root/read-only
  filesystem settings where the Elsa runtime permits it.
- Set CPU, memory, replica, disk, request, concurrency, timeout, queue and
  database limits from an entitlement-backed profile, not an unbounded customer
  value.
- Use readiness/liveness probes and revision traffic controls. Do not route
  traffic to a revision before startup and health checks pass.
- Keep provider identities separate from workload identities. Scope each to the
  smallest resource set and remove access during deprovisioning.

### Data, secret and network handling

- Store only opaque provider references, digests, hashes and safe diagnostics in
  control-plane records. Raw workflow/package payloads and secrets stay out of
  commands and history.
- Use one database and one database identity per Dedicated/Private instance;
  define the shared-profile data boundary separately before implementation.
- Use Key Vault or equivalent provider-backed secret storage and rotate values
  behind stable references. Never inject a control-plane admin/API key into a
  customer workload.
- Define ingress and egress allowlists, TLS/authentication, private endpoints
  and DNS behavior in the provider contract. Test cloud metadata and lateral
  service reachability explicitly.

### Telemetry, backup and support

- Apply organization/instance labels at emission and query time; enforce access
  filters in customer and operator views, not only in UI code.
- Redact bearer tokens, connection strings, keys, package payloads, workflow
  content and provider error bodies. Run automated leak scans over logs,
  telemetry, artifacts, backups and audit records.
- Back up relational state, immutable desired state and artifact references; bind
  secret values again from their owning provider. Restore to a new instance
  first, then cut over only after health and isolation checks pass.
- Support access is least privilege, explicitly approved, time-bound where
  feasible, and append-only audited. Support tooling must select an instance by
  authorization context rather than an untrusted ID.

### Incident and revocation

- A provenance or isolation failure stops new deployments for the affected
  release/profile and marks existing revisions for operator review.
- Revoke a digest/signing identity or package source without deleting customer
  records. Quarantine suspect revisions, preserve safe evidence, and roll back
  only to a verified known-good revision.
- For suspected secret exposure, rotate the provider value, revoke workload
  identity access, disable affected egress/endpoint and record the incident
  without copying the secret into tickets or logs.
- A profile is demoted or removed from the catalog if any mandatory test fails;
  entitlements cannot re-enable it until fresh evidence is approved.

## Promotion gates for deferred profiles

### Shared

Shared can be considered only after `SH-01` through `SH-04` pass with multiple
organizations and hostile workload load. If arbitrary packages are desired,
the platform must first adopt a separately reviewed sandbox boundary with a
clear escape-analysis result. In-process .NET loading, Nuplane package policy,
container labels and package approval do not satisfy that requirement.

### Data-isolated

Data-isolated additionally requires `DI-01` through `DI-03`, including evidence
that pooled compute, network and capacity behavior cannot let one instance
affect another. A database-per-instance design closes only part of the threat
model; it does not prove executable-code or noisy-neighbor isolation.

### Private

Private additionally requires `P-01` through `P-05`, an approved reproducible
build/image pipeline, a customer-package support policy, resource-abuse controls,
secret and egress boundaries, and an operational rollback/restore exercise.
The exact phrase “strongly isolated” must be replaced by resource-level claims in
the provider contract and customer terms before launch.

## Follow-up work

This threat model deliberately produces gates rather than speculative sandbox
code. The following work must close before the associated promise is made:

- **Completed foundation — [`elsa-production-image#27`](https://github.com/valence-works/elsa-production-image/issues/27):**
  the producer now publishes the signed release manifest binding immutable image,
  topology, component, SBOM, provenance, signature and scan evidence identified by
  #105. Elsa Control admission enforcement remains follow-up implementation work.
- **Completed foundation — [`elsa-production-image#26`](https://github.com/valence-works/elsa-production-image/issues/26)
  and #108:** SQL Server persistence and the actual Azure identity, database,
  revision, bounded-cost and cleanup behavior passed. Preserve those contracts
  in the provider implementation.
- **Completed #106/#114:** the provider-neutral resolved plan and Elsa Instance
  lifecycle persist exact release/topology/profile identity and fail closed on unsafe plans.
- **#125/#126:** continue the Azure provider lifecycle and first Dedicated
  endpoint/workflow smoke evidence; admission tests alone do not satisfy the live gate.
- **#132:** close Dedicated hardening gates, including resource placement,
  capacity/noisy-neighbor and support/backup evidence.
- **#133:** implement Private immutable custom-code delivery only after the
  `P-*` matrix and threat review are approved.
- **Future security review:** define a sandbox only if product demand requires
  arbitrary packages on Shared/Data-isolated; do not infer it from current
  runtime assembly loading.

## Acceptance record

This issue is complete when the report is reviewed, every Dedicated launch claim
has a named control and executable test, every deferred profile has explicit
promotion gates, and no arbitrary-package path is available where the boundary
is insufficient. The present record supports Dedicated-first but does not mark
the public launch security gates as passed.
