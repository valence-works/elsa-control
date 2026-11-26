# ADR-0022: Pulumi Provisioning Profiles for the Shared and Dedicated Tiers

## Status

Proposed. Awaiting review by Elsa Control Lead, then approval by Sipke. Design only:
this ADR files no issue, marks nothing Ready, and creates no Azure resource.

## Date

2026-11-26

## Context

[ADR-0021](0021-pulumi-provisioning-module.md) made Pulumi (Automation API, state in
our Azure Blob backend, no Pulumi Cloud) the engine realization path, behind
`IProvisioningService`. Epic #1010 is proving it on staging (#1014 AC4, with #1032,
#1033, #1034 and #1035 in flight).

Sipke's intent (26 Nov 2026): use Pulumi as much as possible, behind options that
Control curates. Customers never see Pulumi. Control decides which options exist,
validates them and maps them to stack inputs; Pulumi applies them. Pulumi also
provisions the **Shared** tier (customers placed on one of our managed multi-tenant
engines), alongside **Dedicated** (own engine) and **BYO subscription** (own engine in
the customer's Azure).

What exists today:

- `ElsaPlacementIntent` already carries provider-neutral `TargetMode`, `RegionCode`,
  `IsolationProfile`, `CapacityProfile`, `NetworkOutcome`, `DomainOutcome` (ADR-0017).
- `AzureEngineProgramPolicy` fail-closes the `elsa-engine` program today: admitted
  locations, `isolation = Dedicated`, `topology = combined`, digest-only image.
- `ElsaEngineStack` builds one Dedicated engine inline (resource group, identity,
  Key Vault, Log Analytics, Container Apps environment, SQL server and database,
  Container App). It has no component boundaries yet.
- ADR-0019 (#633) fixes the Shared model: one CShells shell and one database per
  tenant on a shared SQL server, host-based routing, `RejectUnresolvedTenants` on.
  The Shared flag stays **off in production**.
- #1033 pins every instance to one backend (`bicep` or `pulumi`) for its whole life,
  with no fallback.

## Decision

The smallest design that serves the first public launch: one versioned profile
schema owned by Control, four reusable Pulumi components, a Shared fleet of shard
stacks, and preview, drift and policy run locally through the Automation API.

### 1. Provisioning profiles: a versioned, allowlisted schema owned by Control

- A **profile** is a Control document `{schemaVersion, options}` stored with the
  instance, next to the placement record (ADR-0017). Every applied profile is kept as
  an immutable, numbered **profile revision**.
- Control validates the profile against the schema version, the instance's placement
  kind and the organisation's entitlements, then **maps** it to stack config through
  one allowlist per schema version. The mapping is the only writer of stack config.
- **Raw Pulumi config never comes from customers.** Unknown options, unknown values
  and unknown schema versions are rejected (`profile.option.unknown`,
  `profile.value.rejected`), never ignored or passed through.
- The provisioning module still knows nothing about customers (ADR-0021 §3): it
  receives the mapped engine spec, not the profile.

First option set (schema v1):

| Option | Validation | Unlocked by (tier names only) | Stack input |
|---|---|---|---|
| `region` | Member of the admitted EU region set (the `AdmittedLocations` list); BYO also requires the region to be allowed in the customer's subscription | All tiers; Shared offers only regions that have a shard | `location`; on Shared, shard selection only |
| `engineSize` | Enum `small`, `medium`, `large` | Dedicated, BYO subscription. Shared has no size option | Container App CPU and memory from a Control table per size |
| `replicas` | Integer, 1 up to a per-size maximum | Dedicated, BYO subscription; more than 1 needs the scale entitlement and the horizontal scale-out readiness gate | `minReplicas`, `maxReplicas` |
| `dbTier` | Enum of tiers within our rules. v1 admits **Standard S0** only | Dedicated, BYO subscription; Shared uses the shard's pool | `databaseSku`; anything else is rejected by schema and by policy (§6) |
| `privateNetworking` | Boolean; BYO also needs a customer-supplied network reference that passes a preflight | Dedicated with the networking entitlement, BYO subscription | `networkMode`, `subnetRef` into the networking component |
| `customDomain` | FQDN, not under our own domain; ownership proved by DNS TXT before apply | Dedicated, BYO subscription. Shared: deferred | `customDomain`, managed certificate binding |
| `byoSubscription` | A Lighthouse-delegated subscription reference (#436) that passes a preflight; immutable after first Create | BYO subscription only | Target credential reference and `subscriptionId` (never shown to customers) |

Size, replica limits and entitlement flags live in Control configuration. Commercial
terms are not in this ADR.

### 2. Reusable ComponentResources

Split `ElsaEngineStack` into four `ComponentResource`s under
`src/Provisioning/ElsaControl.Provisioning.Providers.Azure/Pulumi/Components/`:

| Component | Owns | Used by |
|---|---|---|
| `EngineComponent` | Identity, Container Apps environment, Log Analytics, Container App, ingress | Dedicated, BYO, Shared shard |
| `DatabaseComponent` | SQL server, database(s) or pool, contained user, retention | Dedicated, BYO (one database); Shared shard (server and pool; tenant databases are not stack resources, see §3) |
| `NetworkingComponent` | Public ingress by default; VNet integration and private endpoints when `privateNetworking` is on | All |
| `KeyVaultComponent` | Vault, RBAC roles, seeded secrets | All |

Two programs stay as in ADR-0021: `elsa-platform` and `elsa-engine`. The Shared
shard is a **third program, `elsa-shard`**, composed from the same components.
Isolation and SKU rules carried from #1014 (fixed, not profile options):
- `EngineComponent` creates a **per-engine** Container Apps environment and Log Analytics workspace inside the engine's resource group for Dedicated and BYO, so destroy and the delete receipt cover everything. A shared Container Apps environment exists only inside an `elsa-shard` stack (the Shared tier).
- `DatabaseComponent` pins engine databases to **Standard S0**, enforced by a test and by policy (§6).
- Every run (preview, up, refresh, destroy) goes through the provisioning module's **SQL-backed queue and per-stack lock with a fencing token** (ADR-0021, #1014). The Blob state lock is a second guard only, and a conflict there ends the run as `provider.stack.locked`.

Component refactoring must produce **no diff** on an existing Pulumi engine stack
(Pulumi `aliases` where URNs move); that is a test gate.

### 3. The Shared fleet on Pulumi

- **A stack per shard**: project `elsa-shard`, stack `shard-{shardId}`, with `shardId`
  being the immutable ADR-0017 shard record ID. One Elsa version per shard (ADR-0019).
- **Two layers, never mixed:**
  - *Shard provisioning* (Pulumi): engine, SQL server or pool, vault, networking.
  - *Tenant onboarding* (Control, not infrastructure): placement assignment, tenant
    database create, shell registration, host name. Tenant rows and tenant databases
    are never Pulumi resources, so onboarding a tenant never runs `up` on the shard
    and never takes the shard's stack lock.
- **Placement.** Control places a tenant on the least-loaded **open** shard in the
  requested region. A shard is open while its load is below the **cap: about 70% of
  the load-tested peak** (ADR-0017 inputs, #939). The cap is a per-shard-class value
  in Control config, not in code.
- **Fill.** Shared starts on a starter shard and grows on **mid-size** shards. When no
  open shard remains in a region (or a forecast says one will fill soon), an operator
  approves a new shard; Control creates the shard record and enqueues `Provision` on
  a new `shard-{id}` stack. No auto-creation of new spend in v1.
- **Relation to #633.** This ADR changes only *how a shard is realized*. ADR-0019's
  tenancy model, its gates (PG-1 to PG-6) and the public-copy gate stand. The Shared
  flag stays off in production. The first `elsa-shard` stack is staging only.
- **Tenant address.** Every tenant gets `<tenant>.<our domain>`, the same form on
  Shared and Dedicated, which is also the ADR-0019 routing key.

### 4. Preview before apply

- Any change to a live instance's profile runs a Pulumi `preview` with the mapped
  config, through the module's SQL-backed queue and fenced per-stack lock, against our Blob state.
- The Hub shows a **sanitized summary** only: capability-level changes ("engine size
  medium to large", "private networking on"), flags for **replacing** or
  **data-destructive** steps, the expected downtime class, and a cost estimate. No
  resource IDs, URNs, subscription IDs or raw Pulumi text, consistent with the #1032
  sanitizer.
- **Cost estimate.** Computed by Control from our own rate table, keyed by tier and
  option value, kept outside public repositories. Not from Pulumi Cloud and not from a
  live price API. Shown as an estimate.
- A preview is bound to its profile revision and the stack's state version. Apply
  refuses if either moved (`profile.preview.stale`).

**Change lifecycle:** draft, then preview, then approve (the customer; for an
operator-initiated change, a second operator), then apply. **Undo** re-applies the
previous profile revision through the same lifecycle. Undo cannot reverse a
data-destructive change; preview flags those and they need an explicit confirmation.

**Runtime versions** are not a profile option. Customers choose when to take a new
runtime version; the default is stable (#988), and Apply release stays a separate
Hub control (ADR-0021).

### 5. Drift detection and refresh

- A scheduled job runs `refresh` in **preview mode** (`refresh --preview-only`
  semantics; no state write) for every Pulumi stack: platform, engines, shards.
- Any diff raises an operator alert with a sanitized resource-type summary and sets
  the instance's drift flag.
- **Our subscriptions:** an operator decides to accept (`refresh`, then reconcile the
  profile) or revert (`up` with the current revision). No silent auto-revert in v1.
- **BYO subscriptions:** the customer's out-of-band changes are **reported to them**
  in the Hub and to operators. They are never reverted silently. A revert needs the
  customer's approval through the change lifecycle (§4). Changes that break a
  guardrail (§6) block further applies until resolved.

### 6. Policy as code

- **Pulumi CrossGuard policy packs**, run locally through the Automation API on every
  `preview` and `up` (mandatory enforcement). No Pulumi Cloud.
- **Plus Control-side validation** before anything is enqueued (§1). Two layers: the
  schema stops bad requests; the policy pack stops bad programs.
- v1 guardrails:
  - Engine DBs stay Standard S0.
  - Allowed regions only (the admitted set).
  - diagnostics modules disabled on shared engines.
- The policy pack is versioned with the image and pinned like the Pulumi CLI.

### 7. Migrating existing Bicep-owned engines

#1033 pins an instance to `bicep` or `pulumi` for life with no fallback. So:

- **Default: recreate on upgrade.** A Bicep engine moves to Pulumi only through a new
  Pulumi instance plus a data move (the re-home operation, below), then destroy of the
  Bicep one.
- **`pulumi import`** is allowed only as an **explicit, one-way owner change**, run by
  an operator as a separate migration operation (#1017): import, prove `preview`
  shows no changes, flip the owner to `pulumi`, audit. It is never a profile edit and
  never automatic. Mid-flight or Failed Bicep engines stay on Bicep (ADR-0021).
- **Re-home** (Shared to Dedicated to BYO) is a separate, explicit operation, **only
  between Pulumi-owned placements**: data move, new stack, cutover of the tenant
  address, then destroy of the old placement. Bicep to Pulumi is not covered by this
  promise.

### 8. Phased plan and proposed issues

Every phase starts **after #1010's staging proof (#1014 AC4)**, so #1032, #1033,
#1034 and #1035 are not disrupted. These issues are **proposed, not filed**, and none
is Ready until Sipke approves this ADR.

| Phase | Proposed issue | Scope | Depends on |
|---|---|---|---|
| P1 | Profile schema v1 and allowlist mapper | Versioned schema, revisions stored with the instance, strict mapping to stack config, rejection codes | #1014 AC4 |
| P1 | Split `ElsaEngineStack` into components | Engine, Database, Networking, KeyVault components; no-diff test on an existing stack | #1014 AC4 |
| P1 | Local CrossGuard policy pack | The three v1 guardrails, enforced on preview and up via Automation API | Components |
| P2 | Profile preview and change lifecycle | Draft, preview, approve, apply, undo; sanitized summary in Hub | Schema v1, #1016 |
| P2 | Cost estimate from private rate table | Control-side estimate per tier and option; table outside public repos | Preview |
| P2 | Scheduled drift refresh | Preview-only refresh, operator alerts, BYO reporting | #1016 |
| P3 | `elsa-shard` program and shard stacks | Shard provisioning from components, staging only | Components, ADR-0019 gates |
| P3 | Shared placement and fill | Least-loaded open shard, cap at about 70% of tested peak, operator-approved new shard | `elsa-shard` |
| P4 | Re-home operation | Pulumi-to-Pulumi data move, new stack, cutover, destroy old | Shared placement, ADR-0014 |
| P4 | Bicep import as owner change | Operator-only `pulumi import` with no-diff proof and audit | #1017 |
| P4 | BYO options | `byoSubscription`, BYO networking preflight | #436 |

## Patterns considered

Patterns from the market, weighed for the first public launch:

| Pattern | Verdict | Where |
|---|---|---|
| A tenant address per customer (`<tenant>.<our domain>`), same on Shared and Dedicated | **Adopt** | §3; custom domain is an option on top |
| A tier ladder: isolated tenant on Shared, then Dedicated, then BYO subscription under licence | **Adopt**, mapped to Shared, Dedicated, BYO subscription | §1 table |
| Choose where it runs, and change it later | **Adopt** as an explicit Pulumi-to-Pulumi re-home operation, not a profile edit | §7, P4 |
| Change lifecycle: draft, preview, approve, apply, one-step undo | **Adopt**; undo is re-applying the previous revision and cannot reverse data-destructive changes | §4 |
| Customers choose when to take new runtime versions | **Adopt**; already our stable-by-default release model | §4 |
| Hourly restore points in a second EU region, as a higher-tier option | **Defer** | Deferred |
| A trial that locks at the end, keeps data for a grace period, then deletes | **Defer to Sipke**; fits existing lifecycle: lock is a stop, delete is the existing proven delete (ADR-0019 L10). The 15-day retention is a proposal only | Open questions |

## Alternatives rejected

- **Customers pass Pulumi config or stack inputs.** Rejected: breaks the curated-option
  contract and the sanitizer, and makes guardrails bypassable.
- **One stack per Shared tenant.** Rejected: tenants are rows and databases, not
  infrastructure; a stack per tenant multiplies lock and state cost for no isolation gain.
- **Pulumi Cloud for preview, policy, drift or cost.** Rejected: self-managed only.
- **Bicep fallback or silent Bicep-to-Pulumi migration on upgrade.** Rejected: #1033
  pin-for-life.
- **Auto-revert of drift.** Rejected for v1; in BYO it would overwrite customer changes.
- **Automatic shard creation.** Rejected for v1: new spend needs an operator.

## Constraints

- Self-managed Pulumi only: Automation API, Blob state, Key Vault secrets provider.
- No prices, rates or customer details in public repositories; tiers by name only.
- No new workflow files.
- Shared stays off in production; ADR-0019 gates and the public-copy gate stand.
- Engine DBs stay Standard S0.

## Consequences

- Control gains a profile schema and revision history; the instance stores both.
- `ElsaEngineStack` is refactored once; a no-diff gate protects live Pulumi engines.
- A third Pulumi project (`elsa-shard`) is added; `elsa-platform` and `elsa-engine` are
  unchanged.
- Hub gains a preview and approval step for profile changes.
- An open Elsa 3 item still gates a production shard, as in ADR-0019.

## Deferred

- Hourly restore points in a second EU region (a possible higher-tier option; ADR-0014
  follow-up).
- Custom domain on Shared.
- `dbTier` values other than Standard S0.
- Auto-scaling the fleet and automatic shard creation.
- Trial lock, retention and delete schedule (awaiting Sipke).
- Non-Azure providers.

## Open questions for Sipke

1. Which tier names unlock which options (the §1 table proposes a mapping)?
2. Trial end: lock, keep data 15 days (proposal), then delete. Accept the day count?
3. Is the BYO subscription option offered at first public launch, or after?
4. Is a second approver required for every operator change, or only data-destructive ones?
5. Default for migrating Bicep engines: recreate on upgrade, with import only by exception?
6. Is hourly cross-region restore a tier option you want to plan for?

Refs #1010, #1014, #1017, #1033, #633, #436, [ADR-0017](0017-one-placement-record-for-dedicated-byo-and-shared.md),
[ADR-0019](0019-shared-hosted-engine-tenancy-model.md), [ADR-0021](0021-pulumi-provisioning-module.md).
