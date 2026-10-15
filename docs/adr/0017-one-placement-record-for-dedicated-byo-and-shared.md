# ADR-0017: One Placement Record for Dedicated, BYO Subscription and Shared Shard Instances

## Status

Proposed (#739). Becomes Accepted when Elsa Control Code Review gives
**Verdict: APPROVE** on the exact head and required CI is green, per
`.github/reviewers.md`. This ADR is design only. It doesn't implement the record
(#436, #352 and the #633 leaves do), and it creates no Azure resource.

## Date

2026-10-15

## Context

Two P1 items after the first public Hosted launch (#508) both mean "place this
instance somewhere other than the default per-instance Valence resources":

- **#436, BYO overlay:** create and operate the instance in the customer's own Azure
  subscription, which Valence operates through Azure Lighthouse (#430, #433, #434 and
  #435 are done).
- **#633, Shared shards:** put the customer on a tenant inside a multi-tenant engine
  shard that Valence runs.

If each one gets its own code path in the provider runner, we'll have two placement
models that drift apart. Roadmap #729 names this foundation **F1** and requires one
model. This ADR defines it, and says how F2 (image and trust), F4 (telemetry) and
F5 (commercial gate) apply to every kind.

### What exists today (cited)

- **Customer intent is already provider-neutral.** `ElsaPlacementIntent` carries
  `TargetMode`, `RegionCode`, `IsolationProfile`, `CapacityProfile`, `NetworkOutcome`
  and `DomainOutcome`, and no provider identifiers
  (`src/Deployment/ElsaControl.Deployment.Abstractions/Instances/ElsaInstanceModels.cs:247-251`).
- **The instance keeps only an opaque, Control-owned assignment reference.**
  `ElsaPlacementAssignmentReference(AssignmentId)`
  (`src/Deployment/ElsaControl.Deployment.Abstractions/Instances/ElsaInstanceReferences.cs:226-235`).
- **The only provider assignment today is Azure, and it's fingerprinted.**
  `AzureProviderResourceAssignment` stores `ProviderScopeFingerprint`,
  `SubscriptionId`, resource group and ownership key per instance
  (`src/Deployment/ElsaControl.Deployment.Azure/AzureProviderResourceAssignment.cs:23-41`).
  Changing the scope fingerprint is a governed *rebind*, refused when placement
  changed, when more than one live assignment exists or when an operation is in
  flight, and audited (`AzureProviderAssignmentRebindRecord`,
  `AzureProviderAssignmentRebindDiagnostics`, same file).
- **The target authority is host-global.** `AzureProviderTargetScope`
  (subscription, resource group, registry, location) comes from configuration section
  `Deployment:AzureProvider:Runner:TargetScope`. Its SHA-256 fingerprint is persisted
  with each operation so a restarted worker can't move the target
  (`src/Deployment/ElsaControl.Deployment.Azure/AzureProviderRunnerOptions.cs:9-47`).
  That's the ADR-0016 default, and it's what #436 must stop treating as the only target.
- **Managed launch admits Dedicated only.** `ResolvedExtensionPolicy.IsAvailableForManagedLaunch`
  returns true for `Dedicated` only, and the plan resolver refuses other profiles for
  `TargetMode = managed`
  (`src/RuntimeBuilder/ElsaControl.RuntimeBuilder.Abstractions/Plans/ResolvedExtensionPolicy.cs:18-19`;
  `src/RuntimeBuilder/ElsaControl.RuntimeBuilder.Core/Plans/ElsaInstancePlanResolver.cs:811-817`).
- **The commercial gate counts every live `TargetMode = managed` instance against
  `MaxInstances`**
  (`src/PackageCatalog/ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore/AccountWorkspaceStore.cs:259-262`;
  `ElsaInstanceLifecycleStore.cs:748`). A Shared tenant counted this way would consume
  a Dedicated engine allowance.

### Inputs decided since #739 was filed (4 Oct)

- Shared tier: the 14-day trial runs on Shared, with no Dedicated trial. Shared starts
  on one starter shard and moves to mid-size shards as it fills. The shard cap is set
  at about 70% of the peak capacity measured in a load test. Horizontal scale-out
  comes after the Elsa 3.9.0 bump and is a prerequisite for shards (delivery plan,
  Sipke 13 Oct). Commercial terms are deliberately left out of this ADR.
- ADR-0009 Amendment 1 (PR #796, merged 13 Oct): in-house, Valence-built, signed and
  allowlisted activity packages may load on **Shared only**, behind the `SH-*` and
  `SH-AL-*` gates. Arbitrary customer packages stay prohibited on Shared and
  Data-isolated.
- #798 ruling: on Elsa 4 shards, tenants with an identical Verified package set
  (closure hash) share one **shell group**. The proposed cap is 10 package-set groups
  per shard, with overflow placed on the next shard.
- #794 / delivery plan §5b: a separate **lab shard** for Training extension labs runs
  learner packages. It never hosts paying tenants and holds no customer data,
  managed identity or secrets.
- IaC plan P2: the `IManagedEnginePlatform` interface (plan, submit, observe, classify,
  cleanup) is keyed by placement kind and folded into this ADR and #436.

## Decision

### 1. One placement record, with kinds

Control owns one `ElsaPlacementAssignment` per live placement of an instance. The
instance keeps `ElsaPlacementAssignmentReference` exactly as today.

| Field | Meaning |
|---|---|
| `AssignmentId` | Opaque id. This is the existing `ElsaPlacementAssignmentReference`. |
| `InstanceId`, `OrganizationId`, `WorkspaceId` | Ownership. |
| `Kind` | `dedicated-valence` \| `byo-subscription` \| `shared-shard`. A catalog value, not a C# enum in the public contract, so a later kind needs no schema change (ADR-0007). |
| `KindSchemaVersion` | The version of the kind's target shape. |
| `Target` | The kind-specific target (below). Control-internal; never in a customer API. |
| `Fingerprint` | SHA-256 over the canonical `{kind, kindSchemaVersion, target}`. |
| `State` | `Reserved` → `Active` → `Retiring` → `Retired`. |
| `SupersedesAssignmentId` | Set when this assignment replaces another one (a move). |
| `CreatedAt`, `ActivatedAt`, `RetiredAt` | Lifecycle timestamps. |

Kind targets:

| Kind | Target | Provider record below it |
|---|---|---|
| `dedicated-valence` | `{ scopeRef: "valence-workload" }`. It resolves to the host `AzureProviderTargetScope` (ADR-0016). | `AzureProviderResourceAssignment` (unchanged) |
| `byo-subscription` | `{ bindId }`, the Active `azure-bound` bind (#434). The subscription and tenant are read from the bind at assignment time and frozen into the fingerprint. | `AzureProviderResourceAssignment` with the bind's subscription (#436 AC1) |
| `shared-shard` | `{ shardId, tenantId, shardClass, packageSetDigest }` | A provider-owned shard (stamp) record (#352, ADR-0010) plus a tenant entry |

- **`shardClass`** is `standard` or `lab`. The lab shard is a class of `shared-shard`,
  not a fourth kind. It uses the same shard lifecycle, capacity accounting and
  provider code; only the admission rule and the package policy differ (invariant I5).
- **`packageSetDigest`** is the closure hash of the tenant's Verified packages (#798).
  It's `base` when the tenant has none, which is every tenant on an Elsa 3 shard,
  because Integrations are Elsa 4 only.
- **What the customer sees (ADR-0007):** a customer API exposes the kind as a label
  only (`Dedicated`, `Your Azure subscription`, `Shared`). It never exposes a shard id,
  tenant id, subscription id, bind id or fingerprint (#352 AC2).

### 2. Kind selection happens on the server

Control derives the kind when it resolves the plan, from facts the customer can't
forge:

| Inputs | Kind |
|---|---|
| `IsolationProfile = Dedicated`, no Active `azure-bound` bind for the org | `dedicated-valence` |
| `IsolationProfile = Dedicated`, the org's active commercial provider is `azure-bound` with an Active bind | `byo-subscription` |
| `IsolationProfile = Shared` (Shared plan or trial entitlement) | `shared-shard`, class `standard` |
| A Training-lab entitlement | `shared-shard`, class `lab` |
| `Data-isolated`, `Private`, customer-built image (#629) | No kind yet. Refused, as today. #629 images go to `byo-subscription` only (#729 decision); an isolated Valence-hosted tier needs its own ADR. |

Customer intent can't name a subscription, shard or tenant. Today `IsAvailableForManagedLaunch`
stays Dedicated-only; it opens to Shared only when the #633 build and the
`SH-*` gates pass.

### 3. Invariants

- **I1. Assignments are fingerprinted and never silently retargeted** (ADR-0016,
  #436 AC3). `Kind`, `Target` and `Fingerprint` are immutable once an assignment is
  `Reserved`. A different target means a **new** assignment with
  `SupersedesAssignmentId`, created by an explicit move operation. Examples are a
  shard rebalance, Shared → Dedicated, Dedicated → BYO, or a package-set change that
  overflows the shard. The old assignment goes `Retiring` → `Retired` only after the
  move has been confirmed and cleanup is complete. The existing provider-scope
  *rebind* (template or tool fingerprint rotation with placement unchanged) remains
  the only in-place change, and it stays below the placement record.
- **I2. Each operation resolves its target from the placement record.** Every provider
  operation resolves its target from the instance's `Active` (or, for cleanup,
  `Retiring`) assignment when it claims the lease, and records the assignment
  fingerprint on the operation. That covers create, observe, reconcile, health probe,
  recover, retry and cleanup, including the #747/#752 recovery and delete calls. If
  the operation's fingerprint and the assignment's don't match, the operation is
  refused. There's no global configuration swap. The host `TargetScope` remains the
  authority for `dedicated-valence` only (#436 AC1/AC2, #436 non-goal).
- **I3. Existing instances are `dedicated-valence`.** A one-time backfill creates a
  `dedicated-valence` placement assignment for each live
  `AzureProviderResourceAssignment`. It doesn't rewrite `ProviderScopeFingerprint`,
  `SubscriptionId` or any operation fingerprint.
- **I4. Customers can't steer placement.** No customer or BFF input selects a kind,
  subscription, shard or tenant. A request carrying one is rejected.
- **I5. Shard classes never mix.** A `lab` shard admits only lab-entitled tenants. A
  `standard` shard never loads learner or community packages, and loads only built-in
  packages plus, after `SH-AL-*`, the signed Shared allowlist (ADR-0009 Amendment 1).
- **I6. Shard capacity is enforced under concurrency.** Placement on a shard is refused
  once it would exceed the shard's ceiling, which is set from the measured load test
  (about 70% of peak). It's also refused once the shard exceeds its cap on
  package-set groups (#798). Refused placement goes to the next eligible shard, or
  fails with a safe reason code. Occupancy drops only after cleanup has been
  confirmed (#352).
- **I7. Release lines must match.** A tenant is placed only on a shard whose single
  Elsa release line satisfies the instance's resolved plan (#340: same line; patch may
  differ). A shard upgrade moves all of its tenants together. It isn't a placement
  change.

### 4. Provider interface keyed by kind

`IManagedEnginePlatform` (IaC plan P2) wraps the existing ports
(`IElsaInstanceProviderSubmissionPort`, reconciliation, health probe, cleanup) and is
selected by `Kind`:

- `dedicated-valence` and `byo-subscription` share **one** Azure implementation, which
  receives the target scope resolved per operation (I2). BYO is a different scope, not
  a different code path.
- `shared-shard` has its own implementation. It provisions a **tenant inside an
  existing shard**: no Azure resources per customer, tenant create, quiesce, export and
  delete. Shards themselves are platform resources that operators provision, one stack
  per shard (IaC §6.2).

### 5. F2: image catalog and trust

- `dedicated-valence`: the governed Valence Runtime image (`VerifiedManifest`,
  ADR-0015), unchanged.
- `byo-subscription`: the same governed image for now (cross-tenant AcrPull is still
  concierge, #430). Later, customer-built images (#629) are allowed on this kind only,
  and are always `SelfReported`.
- `shared-shard` `standard`: only the governed Valence image. Packages are built-in
  only, plus the signed in-house Shared allowlist on Elsa 4 shards once `SH-AL-*` pass
  (ADR-0009 Amendment 1). No customer uploads and no custom images (#633 Q8).
- `shared-shard` `lab`: the governed image plus learner activity packages, loaded per
  tenant as collectible (#794). Evidence: `SelfReported` for anything learner-supplied.

### 6. F5: commercial gate and entitlement

- **Every kind goes through the same `ElsaInstanceCommercialGate`**, inside the same
  serializable create (#502), with the same entitlement snapshot.
- **What differs per kind:**
  - the provider: `stripe` or `internal` for Valence kinds, `azure-bound` for BYO (#435);
  - the allowance consumed:
    - `dedicated-valence` and `byo-subscription` consume `MaxInstances`;
    - `shared-shard` consumes a separate Shared-tenant allowance, plus quota profiles
      for runs and storage (#349). It never consumes `MaxInstances`.
- **Required code change (#502 rework):** the counts at
  `AccountWorkspaceStore.cs:262` and `ElsaInstanceLifecycleStore.cs:748` must filter
  by placement kind, not by `TargetMode = managed` alone.
- Commercial terms (prices and quota sizes) are set outside this ADR.

### 7. F4: telemetry and health

- `dedicated-valence` and `byo-subscription`: per instance, as today. BYO telemetry is
  collected in the customer's subscription.
- `shared-shard`: health and uptime are measured per shard and projected to each
  tenant (#633 Q5, #632), plus per-tenant quota usage. A shard incident shows on every
  tenant placed on it. Uptime evidence is labelled as shard-level.

### 8. Cloud surface

The kind label is a field in the existing customer instance views (#625/#631), served
by the existing purpose-built BFF endpoints. There's no new page. Cloud never proxies
the Console.

### 9. Not decided here

- **#633's tenancy model** (database per tenant vs a shared database with a tenant
  column). The data layout is a property of the **shard** record, not of the placement
  assignment, so both models fit without a schema change here. #633's decision record
  must conform to this ADR or state where it amends it.
- Data-isolated and Private placement, and any isolated Valence-hosted custom-image
  tier. Each needs its own ADR.
- Commercial terms.

## Alternatives considered

- **Two separate code paths in the provider runner, one for BYO and one for Shared.**
  Rejected: the two placement models drift apart, and every lifecycle fix (#747, #750,
  #752) would have to be made twice. BYO is a scope; Shared is a different
  implementation behind the same interface.
- **Separate BYO and Shared aggregates.** Rejected: the commercial gate, Cloud views,
  telemetry and moves (Shared → Dedicated) would have to join across aggregates, and
  `ElsaInstance` (ADR-0013) would stop being the single customer-facing identity.
- **A global subscription configuration switch** (#436 non-goal). Rejected: it breaks
  the ADR-0016 Valence default for everyone else and can't express two targets at once.
- **Putting the subscription or shard id in `ElsaPlacementIntent`.** Rejected: it leaks
  provider identifiers into customer intent (ADR-0007) and lets the customer steer
  placement (I4).
- **Mutating an assignment's target in place on a move.** Rejected: it breaks
  fingerprint pinning and lost-response recovery (ADR-0016, #436 AC3). A move creates a
  new assignment.
- **A fourth kind `lab-shard`.** Rejected: it would duplicate the shard lifecycle and
  capacity code. A shard class with a hard admission invariant (I5) gives the same
  separation with one implementation.
- **Encoding the package-set group as its own kind or a separate record.** Rejected: a
  group is the engine's shell layout inside a shard (#798). Placement needs only the
  digest to apply the group cap.

## Consequences

- #436 implements `byo-subscription` and I1–I3 for the Azure kinds. Its AC1–AC3 map to
  I2, I2 and I1 respectively.
- #352 becomes the shard (stamp) record and capacity accounting for `shared-shard`, with
  I6 as its concurrency test.
- #502's limit logic must count per kind before the Shared trial ships.
- IaC P2 (`IManagedEnginePlatform`) is keyed by `Kind`.
- Moves (Shared → Dedicated, rebalancing) are an explicit operation with
  quiesce/export/import/flip semantics. That's a separate, L-sized build leaf.

## Test sketches for the invariants

- **I1.**
  - Store test: updating `Kind`, `Target` or `Fingerprint` on a `Reserved`/`Active`
    assignment throws, and the database rejects it.
  - Move test: a move creates a new row with `SupersedesAssignmentId`; the old row
    stays `Retiring` until cleanup is confirmed.
  - Rebind test: the existing rebind diagnostics still refuse a rebind when placement
    changed.
- **I2.**
  - Two orgs: A has no bind (Valence workload subscription), B has an Active bind
    (subscription S-B).
  - Run concurrent create, observe, recover and delete for both, and assert that every
    provider call carries A's or B's own subscription.
  - Assert the host `TargetScope` configuration is never read for B, and an operation
    whose recorded fingerprint differs from its assignment's is refused with a safe
    reason code.
  - Covers the #747/#752 cleanup paths.
- **I3.** Run the backfill on a copy of a staging database. Every live
  `AzureProviderResourceAssignment` gets exactly one `dedicated-valence` assignment;
  `ProviderScopeFingerprint` and all operation fingerprints are byte-identical before
  and after.
- **I4.** API negative tests: a create or update carrying a subscription, shard, tenant
  or kind field is rejected; customer API and BFF responses contain no shard, tenant,
  subscription, bind or fingerprint values (a #352 AC2 sweep).
- **I5.**
  - A non-lab tenant can't be placed on a `lab` shard.
  - A `standard` shard refuses a learner package load with an audit event (with
    SH-AL-01).
- **I6.**
  - N parallel placements against a shard with ceiling C never yield more than C active
    tenants (a database constraint test, #352 AC1).
  - The 11th package-set group overflows to the next shard.
  - Occupancy is unchanged until cleanup is confirmed.
- **I7.** Placement of a plan on release line L onto a shard on line M ≠ L is refused.
