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
| `Fingerprint` | SHA-256 (lowercase hex) over the RFC 8785 (JCS) canonical UTF-8 serialization of `{kind, kindSchemaVersion, target}`. |
| `State` | `Reserved` → `Active` → `Retiring` → `Retired`. |
| `SupersedesAssignmentId` | Set when this assignment replaces another one (a move). |
| `CreatedAt`, `ActivatedAt`, `RetiredAt` | Lifecycle timestamps. |

Kind targets:

| Kind | Target | Provider record below it |
|---|---|---|
| `dedicated-valence` | `{ scopeRef: "valence-workload" }`. It resolves to the host `AzureProviderTargetScope` (ADR-0016). | `AzureProviderResourceAssignment` (unchanged) |
| `byo-subscription` | `{ bindId, subscriptionId, tenantId, location }`. The bind is the Active `azure-bound` bind (#434). The subscription and tenant are read from the bind at assignment time and frozen into the target and the fingerprint. | `AzureProviderResourceAssignment` with the bind's subscription (#436 AC1) |
| `shared-shard` | `{ shardId, region, tenantId, shardClass, packageSetDigest }` | A provider-owned shard (stamp) record (#352, ADR-0010) plus a tenant entry |

- **`shardClass`** is `standard` or `lab`. The lab shard is a class of `shared-shard`
  **for the code only**. It's not a fourth kind: it uses the same shard lifecycle,
  capacity accounting and provider code. Only the admission rule and the package policy
  differ (invariant I5). See §1a for what the lab class is *not*.
- **`packageSetDigest`** (#798) identifies the tenant's Verified package set on a
  `standard` shard. Its definition:
  - **Inputs:** the **full dependency closure** of the tenant's selected Verified
    packages, as a list of `{packageId, version, contentHash}`. `packageId` is
    lowercased, `version` is normalized SemVer, and `contentHash` is the SHA-512 package
    hash taken from the **signed Shared allowlist entry** (ADR-0009 Amendment 1, clauses 1–2),
    never from the package bytes alone.
  - **Canonical form:** the list is sorted by `packageId`, then `version` (ordinal),
    serialized with RFC 8785 (JCS) as UTF-8, and hashed with SHA-256 (lowercase hex).
  - **Empty set:** the empty set is the reserved value `base`. That's every tenant on an
    Elsa 3 shard, because Integrations are Elsa 4 only.
  - **Load-time check:** the engine **recomputes** the digest at tenant load from the
    allowlist entries it verified. A mismatch with the assignment **fails closed** (ADR-0009 Amendment 1, clause 4): no
    load, an audit event and safe reason code `placement.package-set.mismatch`.
  - **Revocation:** a revoked closure member invalidates the digest within the
    revocation list's **120 s** hard TTL (Amendment 1, clause 9). Loads under that digest
    then fail closed until the tenant is moved to a new digest.
  - **Changes:** the digest changes **only through a move** (I1), never by an in-place
    edit of the assignment. A move always gets a **new `tenantId`**, even when it targets
    the same shard (I1). So a same-shard package-set change never reuses a
    `(shardId, tenantId)` that a `Retiring` assignment still holds.
  - **What `tenantId` is:** a shard-internal address only, allocated by the shard and
    **never reused** on that shard, even after `Retired`. Nothing durable is keyed on it
    outside the shard. Quota usage, telemetry, audit history, the lab retention clock
    (#794) and entitlement usage are keyed by **`InstanceId`**. Tenant-scoped tokens carry
    `InstanceId` and are re-issued on the flip. The move's import step **re-keys** the
    tenant-scoped state that lives inside the shard (workflows, bookmarks, packages,
    per-tenant run counters) to the new `tenantId`. The Retiring assignment's cleanup
    **destroys** the old copy, and only then does the old `tenantId` stop being occupied.
  - **On a `lab` shard:** the field holds the reserved value `per-tenant`. Each lab tenant
    gets its own shell, and learner packages swap inside it under #794's drain rules, so
    no shared package-set group exists to identify.
- **What the customer sees (ADR-0007):** a customer API exposes the kind as a label
  only (`Dedicated`, `Your Azure subscription`, `Shared`, and `Training lab` for the lab
  class). It never exposes a shard id, tenant id, subscription id, bind id or
  fingerprint (#352 AC2).
- **Uniqueness:**
  - an instance has **at most one `Active` assignment**;
  - `(shardId, tenantId)` is **unique** across all non-`Retired` `shared-shard`
    assignments, `Retiring` included. Moves allocate a fresh `tenantId` (above), so this
    holds for same-shard moves too. A `Retiring` tenant never loads (I2: cleanup only).

  Both are enforced by database constraints.
- **BYO stores no customer secrets.** Neither the placement record nor the bind stores
  customer credentials, keys or secrets. Control acts in the customer subscription only
  through the **Azure Lighthouse delegation** (#434), under Valence's own managed
  identity. If the bind stops being `Active` (delegation removed, bind suspended or
  revoked):
  - every new provider operation on that assignment is refused with
    `placement.byo.bind-not-active`, with no fallback to the Valence subscription and no
    retarget;
  - observation projects the instance as `Unknown` with that reason;
  - Delete records Control-side cleanup and marks the customer-subscription resources
    `retained-customer-owned` in the cleanup receipt (#774–#777), because Valence can no
    longer reach them. The assignment goes `Retired` without claiming the resources
    were removed;
  - if the same bind becomes `Active` again, operations resume on the same assignment.
    A different bind always means a move.

### 1a. The lab class is not the Shared isolation profile

`shardClass = lab` shares code with `shared-shard`, but it is **not** the `Shared`
isolation profile:

- **No Shared claims.** It makes **no ADR-0009 Shared claims** and no `SH-*` or
  `SH-AL-*` claims, and the signed Shared allowlist doesn't govern it.
- **Never shown as Shared.** Its label is `Training lab`, and `IsAvailableForManagedLaunch`
  opening to Shared never opens it.
- **No Shared allowance.** It **consumes and grants no Shared-tenant allowance** and no
  `MaxInstances`. It uses its own lab entitlement.
- **Nothing sensitive on it.** It hosts **no paying tenants, no customer data, no
  managed identity and no secrets** (#794). Placement enforces this as invariant **I9**;
  it isn't left to operator discipline.
- **Accepted residual risk:** learners placed on the same lab shard **share a process**
  (an AssemblyLoadContext is not a boundary). A learner package can observe or disrupt
  other learners on the same replica. Mitigations: there is nothing sensitive to reach,
  egress is restricted, quotas apply, and the shard is recycled on a schedule (#794).
- **Governing record:** this ADR governs the lab class: lab-only orgs (§2), I9 and
  this section. ADR-0009 Amendment 2 (#812) points here and doesn't add a separate
  lab precondition (Elsa Control Lead, 15 Oct 2026). Lab launch still requires #794's
  acceptance criteria, including egress, quotas, recycle and scale-to-zero.

**Decided by the Elsa Control Lead on 15 Oct 2026:** the lab shard is a non-Shared class
of `shared-shard`, not a separate kind. This is consistent with #794's decision
record ([#794 comment 6146018113](https://github.com/valence-works/elsa-control/issues/794#issuecomment-6146018113),
13 Oct): Sipke approved the dedicated lab shard, and the CEO set the lab package scope to
activities only. The shared-process risk is accepted
only on the condition that I9 holds.

### 2. Kind selection happens on the server

Control derives the kind when it resolves the plan, from facts the customer can't
forge. The rows are **evaluated in order and the first match wins**, so exactly one
outcome applies. This holds because an org has exactly one active commercial provider,
and a lab instance is a separate instance:

| # | Inputs | Kind | Allowance consumed |
|---|---|---|---|
| 1 | The instance was created by the Training-labs flow (server-set origin `training-lab`, only through its purpose-built endpoint) in a **lab org** that holds a lab entitlement (see below), **and** its `IsolationProfile` is not `Data-isolated` or `Private` **and** it uses the governed image (no customer-built image). Otherwise the instance falls through to row 2 and is refused. | `shared-shard`, class `lab` | Lab entitlement only |
| 2 | `IsolationProfile` is `Data-isolated` or `Private`, or the plan uses a customer-built image (#629) | **Refused**, as today, with `isolation.profile.unavailable`. Until #629 lands, customer-built images are refused for every kind. When #629 lands, this row will exclude `byo-subscription`, which is where those images go (#729 decision). An isolated Valence-hosted tier needs its own ADR. | — |
| 3 | `IsolationProfile = Shared` | `shared-shard`, class `standard`. Requires a Shared plan or trial entitlement, else refused with `placement.entitlement.missing`. Binds are ignored. | Shared-tenant allowance |
| 4 | `IsolationProfile = Dedicated`, and the org's active commercial provider is `azure-bound` | `byo-subscription`, using that provider's bind. If the bind isn't `Active`, the request is **refused** with `placement.byo.bind-not-active`, never downgraded to `dedicated-valence`. | `MaxInstances` |
| 5 | `IsolationProfile = Dedicated`, and the active commercial provider is `stripe` or `internal` | `dedicated-valence`. Any bind record is ignored; a bind alone never selects BYO. | `MaxInstances` |
| 6 | Anything else | **Refused** with `placement.kind.unresolved` | — |

**Who holds a lab entitlement.** Only a **lab org** does. A lab org is created only by
the Training-labs flow, holds only a lab entitlement, and **never has a commercial
provider of any kind**: attaching **any** provider (`stripe`, `azure-bound` or
`internal`) to it is refused with `placement.lab.not-eligible`. A paying Cloud org never holds a lab entitlement. A
learner from a paying customer gets a separate lab org, linked only through their
Training identity, never through the customer's org, data or secrets. So a lab instance
always belongs to a lab org, and a Dedicated or Shared request never sees a lab
entitlement; it resolves by rows 2–6. A lab instance exists only when the labs flow
creates one (row 1).

Customer intent can't name a subscription, shard or tenant. Today `IsAvailableForManagedLaunch`
stays Dedicated-only. It opens to Shared (class `standard` only) when the #633 build
and the `SH-*` gates pass.

### 3. Invariants

- **I1. Assignments are fingerprinted and never silently retargeted** (ADR-0016,
  #436 AC3). `Kind`, `Target` and `Fingerprint` are immutable once an assignment is
  `Reserved`. A different target means a **new** assignment with
  `SupersedesAssignmentId`, created by an explicit move operation. Examples are a
  shard rebalance, Shared → Dedicated, Dedicated → BYO, or a package-set change that
  overflows the shard. The old assignment goes `Retiring` → `Retired` only after the
  move has been confirmed and cleanup is complete. A move flips states atomically: the
  new assignment goes `Reserved` → `Active` and the old one goes `Active` → `Retiring`
  in one transaction, so there's never more than one `Active` assignment. On `shared-shard`,
  every move, same-shard ones included, allocates a **fresh, never-reused `tenantId`**
  (§1, `packageSetDigest` and "What `tenantId` is"). The existing provider-scope
  *rebind* (template or tool fingerprint rotation with placement unchanged) remains
  the only in-place change, and it stays below the placement record.
- **I2. Each operation resolves its target from the placement record.** Every provider
  operation resolves its target, when it claims the lease, from exactly one assignment:
  - the instance's `Active` assignment, for normal operations;
  - during a move, the **new `Reserved`** assignment, for the move's own provisioning,
    import and verification steps only. Every other operation keeps using the old
    `Active` assignment until the flip;
  - the `Retiring` assignment, for cleanup only.

  It records the assignment fingerprint on the operation. That covers create, observe, reconcile, health probe,
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
- **I5. Shard classes never mix.** A `lab` shard admits only `training-lab`-origin
  instances (§2 row 1). A `standard` shard never hosts one, never loads learner or
  community packages, and loads only built-in packages plus, after `SH-AL-*`, the
  signed Shared allowlist (ADR-0009 Amendment 1).
- **I6. Shard capacity is enforced under concurrency.** Placement on a shard is refused
  once it would exceed the shard's ceiling, which is set from the measured load test
  (about 70% of peak). On a `standard` shard, it's also refused once the shard
  exceeds its cap on package-set groups (#798). The cap applies to `standard` shards
  only. On a `lab` shard every tenant is `per-tenant`, so the cap doesn't apply, and only
  the ceiling limits it. That cap is **provisionally 10 groups per shard** (decided
  by the Elsa Control Lead on 15 Oct 2026). It is provisional pending the AC1
  measurement in elsa-foundation#2757, and the AC1 result will revise it. Refused placement goes to
  the next eligible shard **in the same region** (I8), or fails with a safe reason code
  (`placement.shard.capacity`). Occupancy drops only after cleanup has been
  confirmed (#352).
- **I7. Release lines must match.** A tenant is placed only on a shard whose single
  Elsa release line satisfies the instance's resolved plan (#340: same line; patch may
  differ). A shard upgrade moves all of its tenants together. It isn't a placement
  change.
- **I8. Data residency: the target region equals the intent's region.** The resolved
  target's region must equal `ElsaPlacementIntent.RegionCode` for every kind:
  - for `dedicated-valence`, the host scope `Location`;
  - for `byo-subscription`, the deployment `location` frozen into the target;
  - for `shared-shard`, the shard's `region`.

  Overflow, rebalancing and moves stay **in-region**. If no in-region target exists, or
  a BYO bind can't deploy to that region, placement **fails** with
  `placement.region.unavailable`. It never falls back to another region.
- **I9. Nothing sensitive is placed on a lab shard.** An instance is **lab-eligible**
  only if all of these hold:
  - it has the `training-lab` origin;
  - it belongs to a lab org (§2), which has no provider of any kind;
  - its `IsolationProfile` is not `Data-isolated` or `Private`, and it uses the governed
    image (no customer-built image);
  - in its resolved plan (`ResolvedElsaApplicationPlan`):
    - no `Configuration.Entries` item has `Secret = true` or a `SecretReference`;
    - every `Configuration.Entries` item matches an entry on the lab configuration
      allowlist, which is deny by default. Each allowlist entry pins the **key**, the
      allowed **`JsonType`** (fixed per key), and an **allowed set of values**: an enum, a
      bounded number range or a boolean. **Free-form strings are denied by default.** A
      string key is allowlisted only with a closed pattern that can't express a URL,
      host, connection string or token. Any URL, host or connection-string value is a
      **customer data source** unless the lab shard issued it. Only shard-issued values,
      which the labs flow writes and the plan can't set, are admitted. A value that fails
      its type or set is treated like a non-allowlisted key;
    - no `ProviderCapabilities` item requires a managed identity, key vault or secret
      store;
    - every `Capacity` storage item is lab storage that the lab shard provisions itself;
    - every `Packages` item is a built-in package or an activities-only learner package
      (#794).
  
  A **customer data source** is any database, storage account, queue, connection
  string, URL, host or endpoint credential that the plan supplies rather than the lab
  shard issuing or provisioning it. The allowlist and storage rules above exclude all of them.

  A lab shard's host has no managed identity and no secret store binding.

  **When it's checked.** Eligibility is checked at first placement, and **re-checked
  on every** plan resolve, plan update and reconcile, and on any provider change on the
  instance's org. A failed check at placement or on an update is **refused**. A failed
  check on a running lab instance **quiesces** it: no new executions, scaled to zero, and
  never moved to another class. Either outcome uses `placement.lab.not-eligible` and is
  audited. A quiesced instance resumes only after a clean re-check, and otherwise it is
  wiped under #794's retention rule.

  **Residual risk.** I9 governs what the *plan* brings. It can't stop a learner typing
  sensitive data or a credential into a workflow input or a learner package. Another
  learner on the same replica could read it. The labs terms and the lab UI say a lab is
  not for real data or credentials. The scheduled recycle (for example nightly) and the
  retention wipe (30 days after last activity is the current input, #794) bound how
  long such data persists. This risk is part of the residual risk accepted in §1a.

  This is the condition the shared-process risk in §1a was accepted on.

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
  This is not the Shared profile (§1a). The lab class is governed by this ADR (§1a,
  I9), not by an ADR-0009 lab amendment.

### 6. F5: commercial gate and entitlement

- **Every kind goes through the same `ElsaInstanceCommercialGate`**, inside the same
  serializable create (#502), with the same entitlement snapshot.
- **What differs per kind:**
  - the provider: `stripe` or `internal` for Valence kinds, `azure-bound` for BYO (#435);
  - the allowance consumed:
    - `dedicated-valence` and `byo-subscription` consume `MaxInstances`;
    - `shared-shard` `standard` consumes a separate Shared-tenant allowance, plus quota
      profiles for runs and storage (#349). It never consumes `MaxInstances`;
    - `shared-shard` `lab` consumes only the lab entitlement. It never consumes or
      grants the Shared-tenant allowance or `MaxInstances` (§1a).
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
  - On a `standard` shard, the package-set group beyond the configured cap overflows to
    the next shard.
  - On a `lab` shard, more lab tenants than the cap are admitted up to the ceiling.
  - **Same-shard package-set change:** a tenant on shard S moves to a new digest on S.
    The new assignment gets a new `tenantId`, and the uniqueness constraint holds while
    the old one is `Retiring`. The old tenant never loads after the flip. Cleanup removes
    it, and only then does occupancy drop.
  - **State carries across the move:** quota usage, run counters, the audit history and
    the lab retention clock are unchanged by a same-shard move, because they're keyed by
    `InstanceId`. Tenant-scoped state is reachable under the new `tenantId`, and the old
    copy is destroyed by cleanup. A `tenantId` is never allocated twice on one shard,
    even after `Retired`.
  - Occupancy is unchanged until cleanup is confirmed.
- **I7.** Placement of a plan on release line L onto a shard on line M ≠ L is refused.
- **I8.**
  - `RegionCode = westeurope` with the only `westeurope` shard full and a free
    `northeurope` shard: placement fails with `placement.region.unavailable`, and no
    tenant is created in `northeurope`.
  - The same check holds for a BYO request whose region the bind can't deploy to, and
    for a `dedicated-valence` request whose host `Location` differs from `RegionCode`.
  - A move or rebalance to another region is rejected.
- **I9.**
  - **Admitted:** a `training-lab` instance in a lab org, with only allowlisted
    non-secret configuration and lab-provisioned storage, is placed on a `lab` shard.
  - Attaching `stripe`, `azure-bound` or `internal` to a lab org is refused with
    `placement.lab.not-eligible`.
  - A lab plan with `Secret = true`, a `SecretReference`, a non-allowlisted
    configuration key, a managed-identity, key vault or secret store capability, or
    plan-supplied storage is refused the same way.
  - An allowlisted key with the wrong `JsonType`, or a value outside its allowed set, is
    refused the same way.
  - **Negative:** an allowlisted key with `Secret = false` carrying an endpoint URL, a
    host name, a connection string or a credential-shaped token is refused with
    `placement.lab.not-eligible`. The same URL issued by the lab shard is admitted.
  - A running lab instance whose plan update or reconcile fails the check is quiesced
    (scaled to zero, with no new executions) and audited, and it's never moved off the
    lab class.
  - Lab shard provisioning fails if a managed identity or secret store is attached.
- **Kind selection (§2).** A table test over every combination of isolation profile,
  commercial provider, bind state and lab entitlement resolves to exactly one row,
  including:
  - Dedicated + `azure-bound` + bind not `Active` → refused;
  - Dedicated + `stripe` + an Active bind → `dedicated-valence`;
  - a lab org requesting Dedicated → refused with `placement.kind.unresolved` (row 6),
    because it has no commercial provider;
  - a lab org requesting Shared → refused with `placement.entitlement.missing` (row 3);
  - a paying org calling the labs endpoint → refused (`placement.lab.not-eligible`),
    because it can't hold a lab entitlement;
  - a lab org through the labs flow → `shared-shard`, class `lab`;
  - a labs-flow instance with `Data-isolated`/`Private` or a customer-built image →
    refused by row 2 (`isolation.profile.unavailable`), never row 1.
- **Uniqueness and canonical form.**
  - A second `Active` assignment for one instance, or a duplicate non-`Retired`
    `(shardId, tenantId)`, is rejected by the database.
  - The fingerprint golden test: JCS bytes and their SHA-256 for fixed targets of each
    kind.
- **packageSetDigest.**
  - A golden digest is computed for a fixed closure, and reordering the inputs doesn't
    change it.
  - An engine-recomputed mismatch fails closed with `placement.package-set.mismatch`.
  - Revoking a member blocks loads under that digest within 120 s.
- **BYO bind not Active.**
  - Removing the delegation refuses create, update and recover with
    `placement.byo.bind-not-active`, and never touches the Valence subscription.
  - Delete produces a receipt with `retained-customer-owned` resources.
