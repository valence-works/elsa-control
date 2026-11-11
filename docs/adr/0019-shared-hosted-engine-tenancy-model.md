# ADR-0019: Shared Hosted Engines on Elsa 3: One Shell and One Database per Tenant

## Status

Proposed (#633). Design and decision record only. It creates no Azure resource and
changes no code. It conforms to ADR-0017 and ADR-0009 and amends neither (§3.3).

## Date

2026-11-11

## Context

Sipke lifted the post-launch freeze for #633 on 11 Nov 2026 (12:52 CET, recorded on
#633 and #868). The Shared shard is built now, alongside #508's open items
(#914, #913, #887), staging only. A production shard (new Azure spend) and the
Shared-tier pricing and limits stay with Sipke and are **not** decided here. Elsa 4
is out of scope; this ADR covers Elsa 3 shards only.

Sipke's starting model: one shared Elsa 3 engine, or a cluster of them, with
multitenancy on. A customer gets a tenant record and a tenant ID, and that ID is the
record-level discriminator in one shared database. The alternative this record had to
weigh: one database per tenant on a shared SQL server or elastic pool.

ADR-0017 already fixes the Control side: one placement record with a `shared-shard`
kind (`{shardId, region, tenantId, shardClass, packageSetDigest}`), and it leaves the
data layout open on purpose ("a property of the shard record, not of the placement
assignment", §9). ADR-0009 fixes the code policy: no customer packages on Shared.

### Constraints that apply to this ADR and every leaf under it

1. **No custom code on Shared** (ADR-0009, #633 Q8). The governed Valence image and
   built-in packages only. On Elsa 3 the ADR-0017 `packageSetDigest` is always `base`.
2. **One Elsa version per shard** (ADR-0017 I7). A shard upgrade moves all its tenants
   together.
3. **Conformance to ADR-0017.** See §3.3: it conforms; two additive clarifications are
   recorded, and no amendment is needed.
4. **Public-copy gate.** Until a Shared production shard exists, Sipke has set pricing,
   and the proof passes, **no public page, Hub string or sales message may mention a
   Shared or multi-tenant plan.** Launch byte-checks any Shared copy before it ships.
   Any Hub or Cloud UI for Shared stays behind a flag that is **off by default**. This
   ADR is design text in a public repository: it carries no prices and no Shared limits.
5. **Staging only, synthetic data.** Nothing here touches production or production data.
6. **Engine line: Elsa 3.11.** The Elsa 3 Lead directs that the shared shard is built on
   Elsa **3.11** (elsa-core `main`), not 3.10. `main` carries no 3.10 or 3.11 tag yet
   (checked 11 Nov 2026), so slice 1 pins an exact commit or package version and moves
   to the 3.11 release when it ships.

## Evidence base, and what it does not cover

Engine-side claims cite public `elsa-workflows/elsa-core`. The first pass read tag
**`3.9.0`** (commit `6436609`, 4 Oct 2026). Because the Lead set the build line to 3.11,
I then re-checked the decisive items on `main` at **`1dd11d95b`** (10 Nov 2026), which
is the 3.11 line. In §4, "**3.9.0**" means "read at that tag" and "**main**" means
"re-checked on `main`". Items not marked "main" were not re-read there. `main` moved its
modules under `core/src/modules/`; paths below are relative to `src/modules/` (3.9.0) or
`core/src/modules/` (main). Shell behaviour cites `sfmskywalker/cshells` tag `0.0.28`.
Control-side claims cite this repository at `5711978`.

**What I could not verify:**

- The Architect's GitHub App has no access to `valence-works/elsa-production-image`
  (private). The image facts here (the shell feature list, `ConfigChangeShellReloader`,
  the managed handoff, the shell configuration wiki) come from an **11 Oct 2026 copy** at
  `b8353d1`, which pins Elsa **3.8.1**, CShells **0.0.28**. The current image tree, its
  pin, and the move to 3.11 were **not** read. Leaf L0 re-verifies them on the current image
  before anything is built. The Lead should grant the App read access.
- The Quartz scheduler adapter (`Elsa.Scheduling.Quartz.*`) is not in elsa-core 3.9.0.
  Its per-tenant job keys are unverified.
- Nothing here was run. The findings are from reading code and issues; the spike turns
  the decisive ones into tests (§5).
- The Elsa 3 Lead is filing the 3.11 tenancy items on the elsa-core milestone. The
  issue and PR numbers cited here were read on 11 Nov 2026. The Lead's list and mine may
  differ, so leaves link existing numbers and do not duplicate them. Rows marked "No
  issue found" had no match in a search of elsa-core issues; the Lead should confirm
  before anything is filed.

## Decision

### 3.1 Recommended tenancy model

**One CShells shell per tenant, one database per tenant, on a shared SQL Server or
elastic pool, in a shared engine process.** Tenant databases hold no shared rows.
Each tenant is reached by its own host name. The Elsa tenant discriminator
(`Elsa.Tenants`, `TenantId` column, query filters) is **not** enabled on Shared.

Why this model:

- **Elsa 3.9 supports it as designed.** The connection string is fixed per shell
  feature when the shell is built (`Elsa.Persistence.EFCore.Common/PersistenceShellFeatureBase.cs:54-69`).
  `Tenant.GetConnectionString` exists but nothing calls it
  (`Elsa.Persistence.EFCore.Common/Extensions/TenantExtensions.cs:7`). So a database per
  tenant means a shell per tenant. The image's own wiki says so: "CShells can run
  several isolated Elsa instances in one host, each with its own database, identity and
  features — the basis for multi-tenancy" (image copy, `staging/runtime/wiki/Configuration.md`, "Multiple shells").
- **CShells adds, updates and removes shells at runtime** without a restart
  (`IShellManager`, `wiki/Runtime-Shell-Management.md:25-80`): the desired state is
  recorded first and a successor runtime goes live only when ready (lines 44-48, 81).
  Host-based routing is built in (`wiki/Shell-Resolution.md:57-77`).
- **It needs no change in elsa-core to be safe.** Data separation is a database
  boundary, not a query filter. The discriminator model needs several upstream fixes
  first (§4).
- **The properties that decide Shared's economics and trust are far better:** provable
  delete, per-tenant backup and restore, per-tenant export, and a cheap move to
  Dedicated (§3.2).

**Tenant resolution.** The tenant is picked by **host**, through CShells shell
resolution (`wiki/Shell-Resolution.md:57-77`), and the session identity carries the same
tenant as a **claim** (leaf L2), so a request and its token must agree. The **header**
resolver is rejected for Shared: it reads a client-set `X-Tenant-Id`
(`Elsa.Tenants.AspNetCore/Resolvers/HeaderTenantResolver.cs:21`, 3.9.0) and trusts it.
Elsa's own host resolver scans all tenants on each request (G16). With a shell per
tenant, CShells does the routing and that scan is off the request path. L0 measures
CShells' own lookup at the intended tenant count.

What it costs, stated plainly: each shell carries its own DI container, recurring and
background tasks (`Elsa.Common/Multitenancy/Implementations/TenantTaskLifecycleCoordinator.cs:43-49`),
and runs its database migrations when it activates
(`PersistenceShellFeatureBase.cs:101`). Density per engine process is therefore lower
than with a single shared database. **The size of that overhead is unmeasured.** Leaf
L0 measures it. The fallback in §3.4 exists in case the number is unviable.

This is *data-layer* isolation. Tenants still share one process, memory, thread pool
and static caches (for example `Store<,>.Semaphore`, `Elsa.Persistence.EFCore.Common/Store.cs:30`).
It protects against a missed query filter and not against a compromise of the engine
process. That is why "no custom code on Shared" (ADR-0009) is load-bearing and stays.

### 3.2 Comparison

| Criterion | A. Shared DB, `TenantId` discriminator | B. Database per tenant, shell per tenant (recommended) |
|---|---|---|
| **Isolation** | An EF global query filter (`Elsa.Persistence.EFCore.Common/EntityHandlers/SetTenantIdFilter.cs:14-60`), applied only when `TenantsOptions.IsEnabled` (line 23). Bulk upserts use raw SQL that bypasses it (`Store.cs:195`, `:243`). | A separate database per tenant, reached through its own connection (`PersistenceShellFeatureBase.cs:54-69`). A wrong query cannot reach another tenant's rows. |
| **Blast radius of a tenant-boundary bug** | Every customer on the shard: all rows share tables and a unique index space (§4, G2, G4). | A data bug reaches one tenant's database. A process-level compromise still reaches every database the process can open. |
| **Cost per tenant** | Lowest: a row set, no extra process state. Instant onboarding. | Higher: a shell (DI container, tasks) plus a database share of the pool. Onboarding is a database create plus a shell activation, seconds to a minute (unmeasured). |
| **Export** | Row extraction across about 15 entity types, with no tenant-scoped export code in 3.9. | A database export (bacpac or copy) of one tenant. |
| **Delete, and proof of delete** | Deleting a tenant removes the tenant record and unschedules timers only (`Elsa.Scheduling/Handlers/UpdateTenantSchedules.cs:21-32`). **No data is purged** (it is the only `ITenantDeletedEvent` handler). Proof means proving zero rows in every table. | Remove the shell, drop the database, verify absence. One checkable fact. |
| **Backup and restore per tenant** | Not possible: a restore is for the whole database. | Per-database point-in-time restore on the SQL platform. Restores go to a new database (ADR-0014). |
| **Migration to Dedicated** | Copy rows by `TenantId`, re-create them in a new database. | Copy or restore the one database into the Dedicated engine's database, then flip the assignment (ADR-0017 I1). |
| **Noisy neighbour** | Same process-level exposure as B, plus one shared database: locks, log and tempdb are common. | Same process-level exposure. The database layer can be metered and limited per database in a pool. |
| **Per-version upgrade** | One schema migration for the whole shard. | One migration per tenant database, run at shell activation. Needs expand-contract migrations and a staged rollout. |
| **Engine readiness** | Not ready on 3.9.0 (§4: G1, G2, G4, G6). On 3.11: G2 is fixed; G1, G4, G6, G15, G18 remain. | Ready for a staging build, with gaps that are ours (image, Control) or common to both models (§4). |

### 3.3 Conformance to ADR-0017 and ADR-0009

ADR-0017 holds as written. This ADR adds two *clarifications* that ADR-0017 §9 already
reserved for #633, so no amendment is needed:

1. **The shard record carries a data layout.** `ElsaSharedShardEntity` today has
   `Id`, `RegionCode`, `ShardClass`, `Capacity`
   (`src/PackageCatalog/ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore/Models/ElsaPlacementAssignmentEntities.cs:29-35`). It gains a `DataLayout` value:
   `database-per-tenant` for every shard built under this ADR; `shared-database` is
   reserved for the fallback (§3.4).
2. **For `database-per-tenant`, the ADR-0017 `tenantId` is the shell name**, allocated
   **by the shard** when Control's reserve step asks it to, and never reused on that
   shard, even after `Retired` (ADR-0017 §1, "What `tenantId` is"). It becomes the
   left-most label of the tenant host name and the database name suffix. Control keys
   everything durable by `InstanceId`, as ADR-0017 requires.
   `ElsaPlacementAssignmentEntity` has no `tenantId` or target fingerprint fields yet
   (same file, lines 10-22); leaf L3 adds them.

Also:

- **Profile vocabulary.** The isolation threat model calls "pooled compute with a
  dedicated database per instance" the *Data-isolated* profile
  (`docs/product/isolation-threat-model.md:113`) and "pooled data" the *Shared* profile
  (line 112). This ADR **implements the Shared product profile with a Data-isolated-grade
  data boundary**. Customers see only "Shared" (ADR-0017 §1). Shared claims stay at
  least as strong as the Shared row; `SH-01`–`SH-04` and `DI-01` are the proof gates.
  Whether a distinct customer-visible Data-isolated tier ever exists is **deferred**
  (ADR-0017 §9) and needs its own ADR. Leaf L16 records the layout in the threat model.
- **`IsAvailableForManagedLaunch` stays Dedicated-only**
  (`src/RuntimeBuilder/ElsaControl.RuntimeBuilder.Abstractions/Plans/ResolvedExtensionPolicy.cs:18-19`)
  until the staging proof passes and the public-copy gate (constraint 4) is met. Opening
  it is a flag, off by default (leaf L6).
- **The commercial gate already exists.** `MaxSharedTenants`, the counting assignment
  and the placement-aware allowance are implemented
  (`ElsaInstanceCommercialGate.cs:80-125`; `ElsaInstanceAllowanceAccounting.cs`).
  `OrganizationBillingStore.cs:480` hard-codes `MaxInstances = 1` for Stripe and is a
  Dedicated limit only. Neither is changed here. No Shared limit value is set.
- **`IManagedEnginePlatform`** (ADR-0017 §4) is not in code. The real seam today is
  `IEngineProvisioningModule`
  (`src/Deployment/ElsaControl.Deployment.Core/Provisioning/IEngineProvisioningModule.cs:8`),
  with `AzureEngineProvisioningModule` as its only implementation
  (`src/Hosting/ElsaControl.Api/Workspace/AzureInstanceLifecycleComposition.cs:48`).
  The Shared tenant lifecycle is a second implementation behind the same seam, selected
  by placement kind (ADR-0017 §4, I2), not a second code path in the Azure runner.

### 3.4 Fallback, and when it triggers

Keep the shared-database discriminator model **only** as a measured fallback, and only
if leaf L0 shows the per-shell overhead makes tenants-per-shard unviable for the
entry tier. It is **not built** under this ADR. If L0 triggers it, a new ADR must be
written, and it is gated on G1, G4, G6, G18 and the other model-A rows in §4. The shard record's `DataLayout`
field is what makes that switch non-breaking for Control.

### 3.5 Go / no-go

| Question | Verdict |
|---|---|
| Model B (database per tenant, shell per tenant): build in **staging** | **GO.** Conditional on leaf L0 passing its two acceptance criteria (§5). |
| Model B: a **production** shard | **NO-GO until** gates PG-1 to PG-6 (§5) pass **and** Sipke approves the spend, pricing and limits. |
| Model A (discriminator) on Elsa 3.9.0 and 3.11 `main` for paying tenants | **NO-GO as it stands.** Blocked by G1, G4, G6, G18 and, in a multi-node shard, G15 (§4). G2 is fixed on 3.11. |
| Anything customer-visible about Shared | **NO-GO** until constraint 4 is met. |

## 4. Elsa 3 multitenancy readiness: cited gaps

Each row says where the gap was read, which model it blocks, and whether it blocks the
**first staging slice** or a **production** shard. "Slice 1" is the single-node staging
shard in leaves L0 to L11, L13 and L14 (the leaf plan the Architect hands to the Lead; no issues are filed by this ADR). Owner for every engine-side row is the **Elsa 3 crew
(elsa-core)**; rows with an existing issue or PR link it and are not to be re-filed.

**Blocks slice 1 = the staging slice cannot start or cannot be trusted without it.**
For model B, nothing in the engine blocks slice 1 outright. Two items need a shard-side
mitigation that our own leaves deliver (G3, G9), and the rest are measured in L0.

### 4.1 Gaps found by reading code

| # | Gap | Evidence | Models | Slice 1 | Production |
|---|---|---|---|---|---|
| G1 | **The shell-mode `TenantsFeature` never turns tenancy on.** The code-first feature sets `TenantsOptions.IsEnabled = true`; the shell feature registers only the resolver pipeline. `IsEnabled` gates the EF query filter (`ElsaDbContextBase.cs:55`, main), tenant stamping and the bulk-upsert ownership check. I found no binding of it in shell mode in elsa-core or CShells. **Unproven by a run**: L0 AC2. | 3.9.0 and **main**: `Elsa.Tenants/Features/TenantsFeature.cs:58` vs `Elsa.Tenants/ShellFeatures/TenantsFeature.cs:15-27`; gates `SetTenantIdFilter.cs:23`, `ApplyTenantId.cs:18`, `Store.cs:195`. Component tests build the code-first host (`WorkflowServer.cs:139-144`, 3.9.0). elsa-core #9223 (milestone 3.11). | A | No (B) | No (B). Blocks A. |
| G2 | **Definition uniqueness ignored the tenant.** `(DefinitionId, Version)` was one unique index with no `TenantId`: a collision and an existence oracle. | 3.9.0: `Elsa.Persistence.EFCore/Modules/Management/Configurations.cs:24`. **Fixed on main**: the index is now `(DefinitionId, TenantId, Version)` (same file, line 24). elsa-core #9163 (merged 5 Nov, 3.11), #9162 (closed). #7539 is still open on the 3.11 milestone. | A | No | No on 3.11. Re-check on the pinned build. |
| G3 | **Unresolved or unknown tenant falls open to the default tenant.** A resolver that names an unknown tenant returns `null`; the middleware still opens a scope and the ambient tenant becomes `""`. | 3.9.0: `Elsa.Tenants/Services/DefaultTenantResolverPipelineInvoker.cs:35`; `Elsa.Tenants.AspNetCore/Middleware/TenantResolutionMiddleware.cs:37`; `DefaultTenantAccessor.cs:10`. In B, CShells' strict `Default` fallback applies (`cshells wiki/Shell-Resolution.md:203-209`): every shard configures an explicit, empty `Default` shell (leaf L1). elsa-core #9222 (milestone 3.11). | A; B mitigated by our config | Mitigated in L1, tested in L11 | Same |
| G4 | **Tenant delete purges no data.** It removes the tenant record and unschedules timers and bookmarks; nothing deletes definitions, instances, logs, secrets or key-values. | 3.9.0 and **main**: `Elsa.Tenants/Endpoints/Tenants/Delete/Endpoint.cs:18`; `Elsa.Scheduling/Handlers/UpdateTenantSchedules.cs` is the only non-core `ITenantDeletedEvent` implementer (`ITenantDeletedEvent.cs`). elsa-core #9224 (milestone 3.11). | A | No (B: drop the database) | No (B). Blocks A. |
| G6 | **Raw-SQL bulk paths bypass the filter**, with a documented check-then-write window; `AnyAsync(..., ignoreQueryFilters)` ignores its flag. | 3.9.0: `Store.cs:195`, `Store.cs:243`, `Store.cs:800-806`. Related open: #8440 (EF key-value: a named tenant can overwrite a `*` key, 3.10.1). | A | No | No (B). Blocks A. |
| G7 | **No per-tenant execution limits.** No per-tenant cap on concurrent instances, execution time, queue depth or request rate. The only throttle is a bookmark-queue debounce. | 3.9.0: `Elsa.Workflows.Runtime/Extensions/RateLimitedFuncExtensions.cs`. elsa-core #9219 (milestone 3.11). | A, B | No | **Yes**: required for gate PG-2 (§5). Controls partly outside the engine (§6, Q2). |
| G8 | **A static write semaphore serialises writes per entity type across the whole process**, so one tenant's burst queues every other tenant. | 3.9.0 and **main**: `Store.cs:30`, used at `Store.cs:90,138,185`. elsa-core #9218 (moves to 3.11 if L0 measures contention). | A, B | No. L0 measures it. | **Yes** if L0 shows material contention. |
| G9 | **An open Elsa 3 item**, tracked with the Elsa 3 crew. Details are held outside this repo. | Elsa 3 crew. | A, B | **Mitigated** in L1 through the Shared shard template | **Yes**, until the Elsa 3 item is resolved or the L1 mitigation is proven by test (L11) |
| G10 | **The Studio handoff principal carries no tenant claim**; its ticket store is in-process and single-replica; its routes are host-global and need a root-path shell. (Image copy, 11 Oct.) | image `ManagedElsaRuntimeHandoff.cs:573-599`; image `README.md` (managed handoff). | A, B | Ours: leaf L2 | Ours |
| G11 | **No shell-create admin API.** `Elsa.Shells.Api` only reloads; the image reloads **all** shells on any config change, so adding a tenant would disturb every tenant. (Image copy.) | `Elsa.Shells.Api/Endpoints/Shells/Reload/Endpoint.cs:16` (3.9.0); image `ConfigChangeShellReloader.cs`. | B | Ours (image): leaf L1 | Ours |
| G13 | **Isolation proof is thin.** The multitenancy component test covers ID normalisation only; the store-level ownership tests do not run through shell mode. | 3.9.0: `test/component/.../Scenarios/Multitenancy/MultitenancyTests.cs`; `test/integration/Elsa.Workflows.Persistence.ConformanceTests/EFCoreSaveManyTenantOwnershipTests.cs`. Related: #8172, #8173 (Memory and EF conformance for leftover stores), #8439. | A, B | Ours: L11 | Ours: L11 |
| G14 | **Unverified:** the Quartz adapter's per-tenant job keys; HTTP route-table behaviour when two tenants share a path (`TenantPrefixHttpEndpointRoutesProvider.cs:27` adds an un-prefixed route for every tenant); the blob workflow provider shows no tenant handling (`Elsa.WorkflowProviders.BlobStorage`). | | A. B is unaffected: routes and schedulers are per shell. | L0 confirms | L0 confirms |

### 4.2 Gaps added by the Elsa 3 Lead, checked against code and issues

| # | Gap | Evidence | Models | Slice 1 | Production |
|---|---|---|---|---|---|
| G15 | **Runtime tenant add is node-local, and in-place edits are ignored.** `POST /tenants` stores and refreshes; `POST /tenants/refresh` calls `RefreshAsync` on **this node's** in-memory dictionary. `RefreshAsync` reconciles added and removed IDs only, so an edited tenant name or configuration is not applied. A multi-node shard needs a cross-node refresh signal. | **main**: `Elsa.Tenants/Endpoints/Tenants/Add/Endpoint.cs:14`, `Refresh/Endpoint.cs:11,17`; `Elsa.Common/Multitenancy/Implementations/DefaultTenantService.cs:18-19` (per-process dictionaries). **elsa-core #8178** (open, no milestone). | A. B uses `IShellManager`, which is also per node. | **No**: slice 1 is a **single-node shard** (the handoff already requires single-replica, G10). | **Yes for any multi-node shard**: a cross-node signal for shell add and remove is needed (our L1 follow-up). Deferred with multi-replica shards (§8). |
| G16 | **Tenant lookup is O(N) per request** in Elsa's host resolver: `FindAsync(filter)` applies the filter over all tenants. The tenant provider also loads every tenant (`ListAsync`). | **main**: `DefaultTenantService.cs:43-46`; `HostTenantResolver.cs` (`FindTenant`). **elsa-core #7173** (open, milestone 3.12). | A. B routes by CShells host resolution, not this path. | No. L0 measures the CShells lookup. | Only if L0 shows an issue at the intended tenant count. |
| G17 | **Proto.Actor drops the tenant.** `PropagateTenant` discards the result of `MessageEnvelope.WithHeader`, so a tenant context never reaches the receiving actor. | **elsa-core #8533** (open, milestone 3.11): `Elsa.Actors.ProtoActor/Middleware/TenantScopeMiddleware.cs`. The 11 Oct image copy has no Proto.Actor reference; L0 re-checks the current image. | A, B if the image uses Proto.Actor | **Avoid**: the shard runtime does not enable Proto.Actor. | Avoid, or wait for the #8533 fix and test it. |
| G18 | **EF tenant ID validation mismatch.** An entity with an explicit tenant ID could bypass validation when the ambient tenant was empty (the default tenant). The fix aligns EF with the Dapper store. | **elsa-core PR #9210** (open, milestone 3.11). | A. B has no discriminator in use. | No. Needed for L0 AC2 (the fallback measurement). | No (B). Blocks A. |
| G19 | **Cache tokens and some locks are global.** The change-token signaler is a per-shell singleton keyed by plain strings (`Elsa.Caching/ShellFeatures/MemoryCacheFeature.cs:26`). Several distributed locks are named by definition ID or filter hash alone, with no tenant: `Elsa.Workflows.Api/Endpoints/WorkflowDefinitions/Post/Endpoint.cs:43`, `Elsa.Workflows.Runtime/Services/TriggerIndexer.cs:108`, `WorkflowResumer.cs:90`. Behind a process-wide lock provider, two tenants publishing the same definition ID contend. (The bookmark-queue lock is tenant-keyed: `DistributedBookmarkQueueWorker.cs:50`.) Which locks are really shared depends on the lock provider wiring in the image, which I did not read. | **main**, as cited. elsa-core #9218 (moves to 3.11 if L0 measures contention). | A, B | No. L0 measures the cross-tenant effect. | **Yes** if L0 shows cross-tenant contention. Elsa 3 crew ask: tenant-key the lock names. |

### 4.2a Summary: what blocks what (model B)

- **Blocks the first staging slice:** no engine gap. Slice 1 is a single-node shard that
  avoids Proto.Actor (G17), mitigates G3 and G9 in our own image config, and measures
  G8, G16 and G19 in L0.
- **Blocks production:** G7 (per-tenant limits), G9 (an open Elsa 3 item, or a
  tested L1 mitigation), G15 for any multi-node shard, and G8 or G19 if L0 shows
  cross-tenant contention. G17 stays "avoid" until #8533 is fixed and tested.
- **Blocks only the discriminator fallback (model A):** G1, G4, G6, G18, G13, G14, plus
  the G2 re-check. Those are not on the critical path unless §3.4 triggers.

### 4.3 What is sound

On 3.9.0: tenant resolution by host, route prefix, header, claim and current user
exists; the entity base type carries `TenantId`, and every persisted entity derives from
`Entity` (`Elsa.Common/Entities/Entity.cs`) so it receives the filter *when enabled*;
HTTP cache keys and the bookmark-queue lock are tenant-prefixed
(`Elsa.Http/Services/CachingHttpWorkflowLookupService.cs:21`;
`DistributedBookmarkQueueWorker.cs:38-50`); tenant add and remove at runtime is
supported in model A. Resolution by claim exists (`Elsa.Identity/Multitenancy/ClaimsTenantResolver.cs:21`,
on main too).

## 5. Acceptance gates

**L0 (spike) acceptance, on the real image, in CI or staging.** The model B go/no-go is
confirmed or reversed by these, not by this ADR's reading of code.

- **AC1 (model B).** Two shells with two databases on one engine process. A request to
  tenant 1's host cannot read, write, list or schedule anything of tenant 2's, through
  API, HTTP workflow endpoint, bookmark resume, timers and Studio session. An unknown
  host reaches an empty `Default` shell and nothing else. Per-shell idle memory, activation
  time including migrations, and the contention from `Store<,>.Semaphore` under a
  two-tenant write burst are recorded as numbers. **No money figures.**
- **AC2 (model A, evidence for the fallback only).** In shell mode, with tenants
  configured, prove or refute G1 (does the filter apply?) and G2 (collision).

**Production gates, all required before Sipke is asked for a production shard.**

| Gate | Proof |
|---|---|
| PG-1 | `SH-01`, `SH-04` and `DI-01` pass on a staging shard with two tenants (negative matrix, indistinguishable denials). |
| PG-2 | `SH-03` / `DI-03` noisy-neighbour run: tenant A hits its ceiling while B stays in its envelope (the #350 pattern). Requires per-tenant limits (L12). |
| PG-3 | A proven tenant delete: shell gone, database gone, no residue, name and allowance reusable. |
| PG-4 | A shard upgrade and rollback with at least two tenants, including a failed tenant migration. |
| PG-5 | Shared to Dedicated move proven on one tenant (database copy, flip, source cleanup). |
| PG-6 | Evidence, telemetry and uptime are per shard, projected per tenant (#632), labelled shard-level. |

## 6. Answers to #633's questions

1. **Isolation and security.** Data: a database per tenant (model B), so a filter bug
   cannot cross tenants. Secrets and definitions live in that database. HTTP endpoints
   and timers are per shell (own route table, own scheduler). Blob storage: per-tenant
   prefix, to be proven. Not isolated: process, memory, thread pool, static caches
   (G8). Blast radius: a data bug is one tenant; a process compromise is the shard.
   Under model A the answer is "one filter", which is why it is the fallback.
2. **Noisy neighbours.** 3.9 has no per-tenant limits (G7, G8). Plan: Control-derived
   per-tenant quotas written into the tenant's shell settings (#349 capacity profiles),
   a front-door rate limit per host, and a database-level cap per database in the pool.
   Detect heavy tenants from per-tenant telemetry (#632) and move them to Dedicated
   (ADR-0017 move). The engine-side limits are an Elsa 3 crew ask (G7, G8, G19).
3. **Lifecycle.** One version per shard. Upgrade is a new revision of the shard app with
   all shells; tenant maintenance windows are shard windows. Per-tenant migrations run
   at shell activation, so migrations must be expand-contract and rolled out in stages.
   Rollback is the previous revision, with the same rule (no destructive migrations
   until the old revision is retired). Shared to Dedicated: quiesce the shell, copy
   the database to the Dedicated engine, verify, flip the assignment (ADR-0017 I1),
   retire the old tenant. Dedicated to Shared is the reverse and is deferred.
4. **Control model.** A tenant **is** an `ElsaInstance` (ADR-0013) with a
   `shared-shard` / `standard` placement assignment (ADR-0017). It is not a new
   resource type. A **shard** is a platform record (the existing shard table,
   plus `DataLayout`). Provisioning, deletion and the customer overview show the same
   instance views with a kind label. The operator holds the shard-level view in the
   Console (L8). A customer never sees a shard ID, tenant ID or host (ADR-0017 §1).
5. **Evidence and trust.** The governed Valence image (release-manifest verification, ADR-0015).
   Uptime and health are per shard and projected to each tenant, labelled
   "shard-level" (ADR-0017 §7, #632).
6. **Identity and Studio.** One host name per tenant, resolved by CShells host
   routing, so each shell is a root-path shell as the managed handoff requires. The
   handoff issues a session for that instance; the engine mints a principal scoped to
   that shell (G10). Custom domains are deferred; they map to the same shell by host.
7. **Commercial.** Unit economics need two measured numbers from L0 and the load test:
   per-shell overhead and tenants per shard at the ADR-0017 I6 ceiling (about 70% of
   measured peak). Pricing, trial limits, the support boundary and the SLA are
   **Sipke's call** and are not in this record. The allowance mechanism is built
   (`MaxSharedTenants`, ADR-0017 Amendment 1).
8. **No custom code on Shared.** The shard shell template is fixed by Control.
   Tenants cannot choose features or load packages. Shell-admin endpoints are not
   exposed to tenants. Custom images stay Dedicated or BYO (#629).

## 7. Rejected alternatives

- **Shared database with a `TenantId` discriminator as the primary model.** Rejected
  for now: it needs engine fixes first (G1, G4, G6, G18; G2 is fixed on 3.11), it cannot prove a tenant
  delete, restore one tenant, or export one tenant cheaply. The threat model already
  says never to rely on a discriminator alone for the Dedicated claim
  (`docs/product/isolation-threat-model.md:178-180`), and Shared is marked deferred
  (line 112). Kept as the measured fallback (§3.4).
- **A new process or container per tenant on the shard.** This is Dedicated with extra
  steps; it loses the density that justifies Shared.
- **Header-based tenant resolution.** The client sets it (§3.1). Rejected for both models.
- **A separate database server per tenant.** Cost and onboarding time defeat the
  purpose; a pool gives per-database limits without it.
- **A schema per tenant in one database.** Elsa 3.9 fixes the schema per feature
  (`ElsaDbContextBase.ElsaSchema`, `Elsa.Persistence.EFCore.Common/ElsaDbContextBase.cs:28`),
  it has no tenant-keyed schema support, and it still shares a database for restore.
- **Tenant records in a shared Control-side catalog only (no engine tenant).** The
  engine must itself separate tenants; Control cannot enforce a boundary the engine
  does not have.
- **Elsa 4 shells or shell groups for this tier.** Out of scope. #798's per-tenant
  loading design is for Elsa 4.

## 8. Deferred

- Production shard and its Azure spend; Shared-tier pricing, trial limits, quotas
  and the SLA (all Sipke).
- Dedicated to Shared moves; a customer-visible Data-isolated tier.
- Custom domains for Shared tenants.
- Multi-replica shards: the handoff ticket store is single-replica (G10), and
  horizontal scale-out is already a prerequisite for shards in the delivery plan
  (ADR-0017 Inputs). The first slice runs one replica.
- Elsa 4 shards, shell groups and Integrations on Shared (#795, #798).
- Any customer-visible text about Shared (constraint 4).

## Consequences

- A leaf plan for the first slice and the engine-side asks (L0 to L19) goes to the Lead
  with this ADR. This ADR files no issue. Engine-side items link existing elsa-core
  issues and PRs and are owned by the Elsa 3 crew.
- ADR-0017 gains no amendment. The shard record and placement assignment get the fields
  in §3.3.
- The Shared allowance and the Dedicated limit stay independent (ADR-0017 Amendment 1).
- The image gains a shard mode with a shell admin surface (L1). That is code in the
  governed image, not customer code, and is consistent with ADR-0009.
- Shared copy is blocked by a flag and a byte-check until the public-copy gate opens.

## Test sketches

- **Two-tenant negative matrix (L11).** Seed A and B with the same workflow name and
  the same `DefinitionId`. As A, try: B's host, B's database name, a forged host
  header, an unknown host, the default shell, a forged tenant claim, a replayed
  handoff ticket for B. Every attempt returns a denial or empty result that is
  byte-identical to a missing resource (#348 pattern).
- **Delete proof (L10).** Seed a tenant with definitions, instances, bookmarks, timers,
  secrets, logs and blobs. Delete. Assert: the shell is not routable; the database does
  not exist; no row for that tenant exists anywhere; the host name 404s like an unknown
  one; the allowance is freed; the same name can be re-created and starts empty.
- **Provisioning idempotency (L5).** Kill the worker after the database create and again
  after the shell registration. Recovery completes or cleans up with no orphan database
  or shell.
- **Fail-closed default (L1).** With no `Default` shell configured, startup fails; with
  an empty one, unknown hosts reach it and it holds no data.
