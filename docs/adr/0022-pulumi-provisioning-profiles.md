# ADR-0022: Pulumi Provisioning Profiles for the Shared and Dedicated Tiers

## Status

Proposed, with Sipke's decisions of 27 Nov 2026 applied (see *Decisions*). Becomes
Accepted on merge. Design only: it creates no Azure resource, and the issues it lists
are filed but not Ready.

## Date

2026-11-26; decisions applied 2026-11-27

## Context

[ADR-0021](0021-pulumi-provisioning-module.md) made Pulumi (Automation API, state in
our Azure Blob backend, no Pulumi Cloud) the engine realization path, behind
`IProvisioningService`. Epic #1010 is proving it on staging (#1014 AC4). The step
ledger (#1032) is merged as 9b176ae3, and staging infrastructure (#1035, which
closed #1034) is merged as 8059c57b. Only the dispatcher (#1033, PR #1039) remains
open.

Sipke's intent (26 Nov 2026): use Pulumi as much as possible, behind options that
Control curates. Customers never see Pulumi. Control decides which options exist,
validates them and maps them to stack inputs; Pulumi applies them. Pulumi also
provisions the **Shared** tier (customers placed on one of our managed multi-tenant
engines), alongside **Dedicated** (own engine) and **Enterprise** (curated options,
with a BYO subscription, an own engine in the customer's Azure, after launch).

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
- Pin-for-life is decided and landing with #1033 (PR #1039): each instance will be
  pinned to one backend (`bicep` or `pulumi`) for life, with no fallback.

## Decision

The smallest design that serves the first public launch: one versioned profile
schema owned by Control, four reusable Pulumi components, a Shared fleet of shard
stacks, and preview, drift and policy run locally through the Automation API.

### 1. Provisioning profiles: a versioned, allowlisted schema owned by Control

- A **profile** is a Control document `{schemaVersion, options}` stored with the
  instance, next to the placement record (ADR-0017).
- Every applied profile is an immutable **profile version** identified by
  `profileDigest` = sha256 over the RFC 8785 (JCS) canonical JSON of
  `{schemaVersion, invariantOptions}` (the ADR-0017 fingerprint canonicalization).
  Release-invariant options (`engineSize`, `replicas`, `dbTier`, `privateNetworking`,
  `maintenanceWindow`) are digested; placement-bound values (`region`, `subnetRef`,
  `customDomain`, `byoSubscription`) are recorded beside the version, not in the digest.
- Preview, apply, undo, the step ledger (#1032) and the delete receipt (#1033) all
  reference `profileDigest`. A version is never edited or rebuilt; undo and rollback
  **re-activate** an earlier version by digest (§4).
- Control validates the profile against the schema version, the instance's placement
  kind and the organisation's **effective entitlements** (below), then **maps** it to
  stack config through one allowlist per schema version. The mapping is the only writer
  of stack config.
- **Raw Pulumi config never comes from customers.** Unknown options, unknown values
  and unknown schema versions are rejected (`profile.option.unknown`,
  `profile.value.rejected`), never ignored or passed through.
- The provisioning module still knows nothing about customers (ADR-0021 §3): it
  receives the mapped engine spec, not the profile.

First option set (schema v1):

| Option | Validation | Unlocked by (tier names only) | Stack input |
|---|---|---|---|
| `region` | A curated region choice from the admitted EU set (`AdmittedLocations`); never a raw Azure location | Enterprise. Shared and Dedicated: Control assigns the region | `location`; on Shared, shard selection only |
| `engineSize` | Enum `small`, `medium`, `large` (curated names, never raw CPU or memory) | Dedicated, Enterprise. Shared has no size option | Container App CPU and memory from a Control table per size |
| `replicas` | A curated scaling range, 1 up to a per-size maximum; more than 1 needs the horizontal scale-out readiness gate | Enterprise. Dedicated runs one replica | `minReplicas`, `maxReplicas` |
| `dbTier` | v1 admits **Standard S0** only | Dedicated, Enterprise; Shared uses the shard's pool | `databaseSku`; anything else is rejected by schema and by policy (§6) |
| `privateNetworking` | Boolean; BYO also needs a customer-supplied network reference that passes a preflight | Enterprise | `networkMode`, `subnetRef` into the networking component |
| `customDomain` | FQDN, not under our own domain; ownership proved by DNS TXT before apply | Dedicated, Enterprise. Shared: deferred | `customDomain`, managed certificate binding |
| `maintenanceWindow` | One of a curated set of weekly windows | Enterprise | None: Control schedules release applies and upgrades inside it |
| `byoSubscription` | A Lighthouse-delegated subscription reference (#436) that passes a preflight; immutable after first Create | Enterprise, as **early access after launch** (deferred) | Target credential reference and `subscriptionId` (never shown to customers) |

Size tables, range limits and entitlement flags live in Control configuration.
Commercial terms are not in this ADR.

**Tiers are capability entitlements, not code paths.** Control implements every
capability; a tier only decides which ones are exposed, through the Cloud UX layer.
Customers never choose raw sizes or regions.

| Capability | Shared | Dedicated | Enterprise |
|---|---|---|---|
| Placement | Tenant on our managed engines and database (§3) | Own engine in our subscription | Own engine; BYO subscription as early access after launch |
| Infrastructure control | None | Size from the short curated list; safe actions only (start, stop, resize within the list, Apply release, delete) | Curated profile options: region, scaling range, maintenance window, private networking; BYO later |
| Custom domain | Deferred | Yes | Yes |
| Dedicated Control | No | No | Only for Enterprise or BYO |

**Effective entitlements.** The "Unlocked by" column resolves from **one
effective-entitlement view per organization** (tier name, option flags, limits and
quotas), built from the existing entitlement records (`OrganizationInternalEntitlement`,
billing quantity, `EntitlementHeld`; supersedes the capacity part of #349). Schema
validation, the policy pack inputs (§6) and the Hub all read this one view.

**Lapse rules.**
- *Paid entitlement lapses:* new changes are held (`EntitlementHeld`), running engines
  are not touched, and nothing is deleted except by the existing billing deletion rule.
- *Trial ends:* trials are self-serve, with quotas from the entitlement view. At trial
  end the instance is **locked** (changes held, engine stopped), data is **kept for
  15 days**, then removed by the existing proven delete (ADR-0019 L10, #1033 receipt).
  Choosing a plan inside the 15 days lifts the lock with data intact.

**Schema lifecycle.** A schema version and its mapping and size tables go draft, then
reviewed (PR plus Code Review APPROVE), then **published, and are immutable** after
that. Compatibility rule: v(n+1) accepts every published v(n) profile, or ships an
explicit migration that produces a *draft* version for preview and approval; it never
rewrites an applied version. Each schema version declares the `elsa-engine` /
`elsa-shard` program version range it needs; preview fails closed with
`profile.program.incompatible` outside it.

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
- Every run (preview, up, refresh, destroy) goes through the provisioning module's **SQL-backed queue and per-stack lock with a fencing token** (ADR-0021, #1014). The Blob state lock is a second guard only, and a conflict there ends the run as `provider.stack.locked`. That SQL-backed queue and lock are the production store; SQLite and in-memory are for tests only.
- Lifecycle decisions carried from ADR-0021, #1014 and #1033: Cancel stops an in-flight run only; idle Cancel is rejected (`operation.nothing-to-cancel`) and never destroys. Delete is destroy, then the resource-group absence probe, then the receipt. `IsComplete` requires `SeedVaultUri`.

Component refactoring must produce **no diff** on an existing Pulumi engine stack
(Pulumi `aliases` where URNs move); that is a test gate.

### 3. The Shared fleet on Pulumi

- **Stack names.** Dedicated stacks stay `engine-{instanceId}` (ADR-0021 §5; #1033).
  Shared uses project `elsa-shard`, stack `shard-{shardId}`, with `shardId` the
  immutable ADR-0017 shard record ID. One Elsa version per shard (ADR-0019).
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
- **Noisy-neighbour protection is built in.** Every Shared tenant runs inside a
  per-tenant envelope (quotas and limits from the effective-entitlement view): shell
  quotas, a per-host rate limit and a per-database cap (#953). Placement counts each
  tenant's envelope, not its current use, against the shard cap, so one busy tenant
  can't push a shard past it.
- **Shared to Dedicated is planned now** (P3, #954): quiesce, copy the tenant database
  into a new Dedicated engine created through the #1033 dispatcher (owner `pulumi`),
  verify, flip the assignment and tenant address, then remove the tenant from the shard.
  The shard stack is never run for this. A tenant entry on a shard reuses
  `ElsaObservedLifecycle`; no new state enum.

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
- A preview is bound to its `profileDigest` and the stack's state version. Apply
  refuses if either moved (`profile.preview.stale`).

**Change lifecycle:** draft, then preview, then approve, then apply. One approver
suffices, except for **data-destructive** changes (flagged by preview): a customer
confirms them explicitly, and an operator-initiated one needs a **second operator**.
Requester is never the second approver, enforced in the domain, not the UI. Broader
four-eyes for teams that want it is an optional opt-in, deferred.

**Undo** re-activates the previous profile version by digest through the same
lifecycle. Before re-activation the target is re-checked against current policy (a
revoked image digest, a retired option or schema version, a missing secret slot); if it
fails, the answer is 409 `profile.version.not-restorable` and the customer creates a new
version instead. Undo cannot reverse a data-destructive change.

**A bad change never takes the engine down.** Traffic stays pinned to the serving
Container App revision; it shifts to a new revision only after that revision is ready
and passes the health probe. Otherwise the old revision keeps serving and the step
ledger records a `rejected` outcome with its reason. Rollback moves traffic back to the
retained prior revision. Today engines route `LatestRevision=true` (`ElsaEngineStack`);
QA's #1033 staging proof checks whether that can reach a revision that isn't ready. If
it can, health-gated cutover moves before the first public Hosted launch (#508).

**Health and traffic ownership for Pulumi engines** (#1039). Pulumi-owned engines do not
reuse the Bicep Health or Promotion steps, and the Bicep resource-group and workload scope
check is not widened to cover them; this replaces the #1039 r2 hand-off to Bicep.

- *Traffic:* the stack is the only owner of Container Apps traffic (latest revision for
  now), with no CLI weight changes.
- *Health:* a Pulumi-side HTTP probe of `{endpoint}/health`, with the endpoint taken
  from the stack's `EngineEndpoint` output (`AzurePulumiReadinessVerifier`). That output
  is the stable Container App ingress host, never a per-revision host such as
  `LatestRevisionFqdn`, so the ledger and Hub keep one address across revisions. It uses the Bicep Promotion
  curl contract (`--fail`, `--retry 30 --retry-delay 5 --max-time 10`,
  `AzureBicepProviderRunner.ExecuteHealthProbeAsync`) inside `ObservationAttempts` (60)
  attempts `ObservationDelay` (5 s) apart (`AzureProviderRunnerOptions`). A runtime
  report of `Healthy` or `Degraded` passes; `Unhealthy` or an unknown report keeps
  polling, and no pass within the budget fails with `provisioning.timeout.health`. The
  Container App `properties.healthState` check in the Bicep Health step
  (`RunHealthAsync`) and the Ready monitor (`ElsaInstanceHealthMonitor`) are not the
  contract.
- *Promotion:* a read-only check that traffic is on the latest ready revision.
- *Allowed operations until #1046:* Create; Retry of a Create whose instance has never
  recorded Ready (`HasReachedReadyAsync` is false); and Delete. Every other lifecycle
  action (Reconcile, UpdateIntent, Start, Stop, Restart, upgrades, Recover, and Retry after
  Ready) fails closed with `provider.pulumi.operation-unsupported` (needs a person, no
  auto-resume) and never runs `pulumi up` or Bicep. `provider.dispatch-failed` is not
  reused because it auto-resumes.
- *Cancel:* not a lifecycle action. Cancelling an in-flight allowed run uses the #1059
  cancel route only: it stops the run, never destroys resources and never calls Bicep. A
  cancelled Create stays never-Ready, so it can only be retried or deleted.
- *#1046* (stack-pinned revision traffic, promote after health) must land before the
  worker is enabled for any lifecycle action outside the allowed set above.

**Runtime versions** are not a profile option. Customers choose when to take a new
runtime version; the default is stable (#988), and Apply release stays a separate
Hub control (ADR-0021).

### 5. Drift detection and refresh

- A scheduled job runs `refresh` in **preview mode** (`refresh --preview-only`
  semantics; no state write) for every Pulumi stack: platform, engines, shards.
- Any diff raises an operator alert with a sanitized resource-type summary and sets
  the instance's drift flag.
- **Our subscriptions:** an operator decides to accept (`refresh`, then reconcile the
  profile) or revert (`up` with the current profile version). No silent auto-revert in v1.
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
  an operator as a separate migration operation (#1017's Import path, a data-destructive
  class change that needs a second operator): import, prove `preview`
  shows no changes, flip the owner to `pulumi`, audit. It is never a profile edit and
  never automatic. Mid-flight or Failed Bicep engines stay on Bicep (ADR-0021).
- **Re-home** is a separate, explicit operation, **only between Pulumi-owned
  placements**: data move, new stack, cutover of the tenant address, then destroy of
  the old placement. Shared to Dedicated is #954 (§3, P3); Dedicated to Dedicated in
  another region is P4; to BYO follows BYO early access. Bicep to Pulumi is not covered
  by this promise.
- A schema upgrade or an import never rewrites a customer's applied profile version;
  it produces a draft that goes through preview and approval.

### 8. Phased plan and issues

Every phase starts **after #1010's staging proof (#1014 AC4)**. The step ledger
(#1032, 9b176ae3) and staging infrastructure (#1035, 8059c57b, which closed #1034)
are merged. Do not disrupt the still-open dispatcher (#1033, PR #1039). Issues are
filed under #1010 (27 Nov 2026), **not Ready**; the Lead arms them in phase order.
Launch scope: one engine per placement, no Test-to-Prod promotion product.

| Phase | Issue | Scope | Depends on |
|---|---|---|---|
| P1 | #1040 Profile schema v1: digest-identified profile versions and allowlist mapper | Schema v1, immutable versions with `profileDigest`, invariant and placement-bound split, strict mapping, rejection codes | #1014 AC4 |
| P1 | #1041 Profile schema lifecycle | Published versions immutable, compatibility test, program version range (`profile.program.incompatible`) | #1040 |
| P1 | #1042 Effective-entitlement view and lapse rules | One view per organization, the tier table, paid-lapse rules | #1014 AC4 |
| P1 | #1043 Split `ElsaEngineStack` into components | Engine, Database, Networking, KeyVault; no-diff test on an existing stack | #1014 AC4 |
| P1 | #1044 Local CrossGuard policy pack | The three v1 guardrails on preview and up via Automation API | #1043 |
| P2 | #1045 Profile preview and change lifecycle | Draft, preview, approve, apply; undo by digest re-activation; `profile.version.not-restorable` | #1040, #1042, #1016 |
| P2 | #1046 Health-gated revision cutover | Traffic pinned until ready and probed; ledger `rejected` reason; rollback to retained revision. Moves pre-#508 if the #1033 proof fails | #1033 proof |
| P2 | #1047 Second approver for data-destructive changes | Requester ≠ second operator, domain-enforced; customer explicit confirmation | #1045 |
| P2 | #1048 Trial lifecycle | Self-serve trial with quotas; lock at end, keep 15 days, proven delete | #1042 |
| P2 | #1049 Cost estimate from private rate table | Control-side estimate per tier and option; table outside public repos | #1045 |
| P2 | #1050 Scheduled drift refresh | Preview-only refresh, operator alerts, BYO reporting | #1016 |
| P3 | #1051 `elsa-shard` program and shard stacks | Shard provisioning from components, staging only | #1043, ADR-0019 gates |
| P3 | #1052 Shared placement, fill and per-tenant envelope | Least-loaded open shard, cap at about 70% of tested peak, envelope accounting, operator-approved new shard | #1051, #1042 |
| P3 | #954 Shared to Dedicated move (existing) | Pulumi target via #1033 (§3) | #1052 |
| P3 | #953 Noisy-neighbour quotas (existing) | Envelope enforcement and load test (§3) | #1052 |
| P4 | #1053 Re-home between Pulumi Dedicated placements | Region move: data move, new stack, cutover, destroy old | #1045, ADR-0014 |
| P4 | #1017 Bicep import (existing) | Import path with no-diff proof, second operator, audit | #1016 |

Not filed (deferred): BYO options, cross-region restore, four-eyes opt-in.

## Patterns considered

Patterns from the market, weighed for the first public launch:

| Pattern | Verdict | Where |
|---|---|---|
| A tenant address per customer (`<tenant>.<our domain>`), same on Shared and Dedicated | **Adopt** | §3; custom domain is an option on top |
| A tier ladder of capability entitlements | **Adopt**, as Shared, Dedicated, Enterprise; BYO is an Enterprise option | §1 tier table |
| Choose where it runs, and change it later | **Adopt** as an explicit Pulumi-to-Pulumi re-home operation, not a profile edit | §7, P4 |
| Change lifecycle: draft, preview, approve, apply, one-step undo | **Adopt**; undo re-activates the previous version and cannot reverse data-destructive changes | §4 |
| Customers choose when to take new runtime versions | **Adopt**; already our stable-by-default release model | §4 |
| Hourly restore points in a second EU region, as a higher-tier option | **Defer** | Deferred |
| A trial that locks at the end, keeps data for a grace period, then deletes | **Adopt** (decided): lock, 15 days, proven delete; self-serve with quotas | §1 lapse rules |
| Immutable releases with a content digest; rollback re-activates, never rebuilds | **Adapt** as digest-identified profile versions | §1, §4 |
| Runtime keeps the previous release when a new one fails, with a recorded reason | **Adapt** as health-gated revision cutover; engines never pull infrastructure | §4 |
| Rollback targets re-validated against current policy | **Adopt** (`profile.version.not-restorable`) | §4 |
| Published schema versions immutable, with a compatibility check and a declared platform range | **Adopt** for the profile schema; no general content catalog | §1 |
| One entitlement source with expiry and grace | **Adapt** as the effective-entitlement view; grace is bounded and holds changes | §1 |
| Approval matrix per change type and party | **Skip** for v1 (decided): second approver for data-destructive changes only | §4 |
| Ordered stage promotion | **Skip** for launch: no Test-to-Prod promotion product yet | Deferred |

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
- **A per-change-type approval matrix in v1.** Rejected (decided 27 Nov): more process
  than launch needs; only data-destructive changes get a second approver.
- **Tier-specific code paths.** Rejected: tiers are entitlements over one capability set.

## Constraints

- Self-managed Pulumi only: Automation API, Blob state, Key Vault secrets provider.
- No prices, rates or customer details in public repositories; tiers by name only.
- No new workflow files.
- Shared stays off in production; ADR-0019 gates and the public-copy gate stand.
- Production `Provisioning:WorkerEnabled` stays false until a later rollout
  (ADR-0021 §9). The Shared flag is not a substitute.
- Engine DBs stay Standard S0.

## Consequences

- Control gains a profile schema and digest-identified version history; the instance
  stores both, and the ledger and delete receipt carry the digest.
- One effective-entitlement view becomes the only source for option unlocks and quotas.
- `ElsaEngineStack` is refactored once; a no-diff gate protects live Pulumi engines.
- A third Pulumi project (`elsa-shard`) is added; `elsa-platform` and `elsa-engine` are
  unchanged.
- Hub gains a preview and approval step for profile changes.
- An open Elsa 3 item still gates a production shard, as in ADR-0019.

## Decisions (27 Nov 2026)

Sipke's answers, 11:18 CET, relayed by Elsa Control Lead. All six questions are closed.

| # | Question | Decision |
|---|---|---|
| Q1 | Which tiers unlock which options | The §1 tier table: Shared, Dedicated, Enterprise as capability entitlements. Customers never choose raw sizes or regions |
| Q2 | Trial end | Lock the instance, keep data 15 days, then delete. Trials are self-serve, with quotas |
| Q3 | BYO subscription at launch | No. Early access right after launch, as an Enterprise option |
| Q4 | Second approver | Only for data-destructive changes. Broader four-eyes is an optional opt-in, deferred |
| Q5 | Migrating Bicep engines | Recreate on upgrade; import only by exception (#1017) |
| Q6 | Cross-region restore | Later, as a top-tier option. Deferred |

Launch scope: one engine per placement; no Test-to-Prod promotion product yet; a
dedicated Control only for Enterprise or BYO. ADR edits applied: digest-identified
profile versions, health-gated cutover, entitlement lapse, immutable published
schemas. Not applied: the approval matrix.

## Deferred

- BYO subscription option (early access right after launch; #436).
- Hourly cross-region restore, as a top-tier option (ADR-0014 follow-up).
- Opt-in four-eyes for teams that want it; any per-organization approval policy.
- A Test-to-Prod promotion product and more than one engine per placement.
- Custom domain on Shared.
- `dbTier` values other than Standard S0.
- Auto-scaling the fleet and automatic shard creation.
- Non-Azure providers.

Refs #1010, #1014, #1017, #1033, #633, #436, [ADR-0017](0017-one-placement-record-for-dedicated-byo-and-shared.md),
[ADR-0019](0019-shared-hosted-engine-tenancy-model.md), [ADR-0021](0021-pulumi-provisioning-module.md).
