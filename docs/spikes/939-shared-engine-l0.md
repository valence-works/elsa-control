# L0 spike — shared Elsa 3 engine density and two-shell isolation (#939)

Parent: #633. Design: ADR-0019. Pin: elsa-core `main` `59b14969`. CShells `0.0.28`.

Harness: `spikes/939-shared-engine/`. Not in `ElsaControl.sln`. No new GitHub Actions workflows.

## Verdict

**GO** for model B (one CShells shell and one database per tenant) in staging.

The discriminator fallback (model A) is **not** triggered. On this pin G1 applies, so it is no longer a G1-blocker for A; G4, G6 and G18 still block A. Production remains **NO-GO** until PG-1–PG-6 and Sipke's spend/pricing/limits.

An open Elsa 3 item stays named only as that. Diagnostics modules stay disabled on shared engines. Proto.Actor is not enabled.

## AC mapping

| AC | Result | Evidence |
|---|---|---|
| AC1 two shells, two DBs, one process; isolation matrix | **Pass** | `IsolationTests` on shard host: API list/get, HTTP endpoint, bookmark resume, timers, Studio login token and API keys. Unknown host → empty `Default`. |
| AC2 G1 filter in shell mode; G2 uniqueness | **Pass** | `TenantFilterTests`: `TenantsOptions.IsEnabled` is true; `ManagementElsaDbContext.IsTenantFilteringEnabled`; two tenants with the same `DefinitionId` are filtered. G2 index is `(DefinitionId, TenantId, Version)` and two rows persist with `IgnoreQueryFilters`. |
| AC3 numbers | **Pass** | `evidence/ac3-measurements.json` (sqlite). |
| AC4 write verdict into ADR-0019 §3.4 | **Pass** | This document plus ADR-0019 §3.4 / §3.5. |

`dotnet test Spike.sln --configuration Release`: 15 passed, 0 failed.

## AC3 numbers (sqlite)

Copied from `spikes/939-shared-engine/evidence/ac3-measurements.json`:

- Pin `59b14969027b5cff1273f54970d90da189f4ff0f`, CShells `0.0.28`, persistence `sqlite`.
- Idle working set of the test process after Default + tenant-a + tenant-b: **423,301,120 bytes**. Includes the xUnit host; not a clean per-shell RSS.
- Activation including migrations: Default **35 ms**, tenant-a **376 ms**, tenant-b **383 ms**.
- `Store<,>.Semaphore` is **static** (G8).
- 20 sequential publishes on one tenant: **471 ms**. Concurrent 10 publishes per tenant (20 total): **448 ms** — G8 serialises cross-tenant writes.
- Same `DefinitionId` published on both tenants concurrently: **38 ms** (G19 name-only locks at N=2).
- CShells host lookup, 64 probe shells, 10,000 samples: p50 **0.11 ms**, p95 **0.19 ms**, p99 **0.26 ms**.

## Image re-check

`valence-works/elsa-production-image` was not readable (404). Public analogue `Elsa.ModularServer.Web` at the pin:

- Project references `Elsa.Diagnostics.*` and enables `ConsoleLogsDashboardFeature` / `StructuredLogsDashboardFeature`.
- Does not reference Proto.Actor / `Elsa.Actors.ProtoActor`.
- Default runtime lock provider is `FileDistributedSynchronizationProvider` under `App_Data/locks`.

The spike host does not enable diagnostics modules or Proto.Actor. Shared engines use the file lock provider with `AllowLocalLockProviderInDistributedRuntime` acknowledged for single-host tests.

## Azure SQL

Not provisioned. This worker had no Azure managed identity (IMDS timeout). Operator-only bicep: `spikes/939-shared-engine/infra/main.bicep`. Staging resource group, one logical server, at most three Standard S0 databases, tags `purpose=shared-engine-spike` and `issue=939`. Tear down within 7 days of the verdict unless a later leaf takes them over.

AC1 used two sqlite files as the two databases.

## CShells 0.0.28 workarounds (spike-only)

Not elsa-core patches:

1. Duplicate FastEndpoints names across shells fail matcher construction. The spike prefixes endpoint names with the shell id and stamps `HostAttribute` from `WebRouting:Host`.
2. Identical paths without Host metadata are ambiguous. A host-level `IEndpointSelectorPolicy` keeps the shell that CShells host routing resolved.
3. `IMiddlewareShellFeature` (`UseWorkflows`) registered at shell activation does not enter an already-started ASP.NET pipeline. A host-level HTTP gate forwards `/workflows` into Elsa's HTTP middleware using the current shell `RequestServices`.

TestServer clients must use `BaseAddress = http://{tenant-host}/` so `Request.Host` matches CShells host routing.

## How to run

```bash
cd spikes/939-shared-engine
./run-spike.sh
```

Optional: `export SPIKE_SQL_CONNECTION='...'` after applying the operator bicep.
