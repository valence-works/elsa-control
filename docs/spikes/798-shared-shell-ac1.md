# Spike #798 AC1: Shared shell memory and cold start

Date: 2026-10-15

Status: Complete — CShells-only numbers recorded. **Do not treat these as
production Shared Elsa 4 cost.** Re-run on Elsa.Foundation.Host
([elsa-foundation#2757](https://github.com/elsa-workflows/elsa-foundation/issues/2757))
before locking the production cap.

## Question

What is the per-shell memory and cold-start cost of the Architect's Shared
model on #798 (one CShells shell per tenant group, one collectible ALC per
verified package-set revision), and what shard cap follows at ~70% of the
stated 1.5 GiB headroom?

## What this repo actually hosts

`elsa-control` is the control plane. It does **not** construct `IShell`,
Nuplane, or `AssemblyLoadContext`. Managed launch is Dedicated-only
(`ResolvedExtensionPolicy.IsAvailableForManagedLaunch`); Shared is refused
with `isolation.profile.unavailable`. The live engine image is Elsa 3.8
Combined with one CShells `Default` shell and `HostIntegrated` Nuplane loads.

The Architect's model (comment 6153908034) is therefore measured here with
the **CShells 0.0.30** host Elsa 4 already uses, plus a representative
verified activity package in a collectible ALC. That is the faithful
Control-side spike. A full Elsa 4 Workbench / `Elsa.Activities.Runtime` /
workflow-persistence host is **absent** from this repository
(`Elsa.Foundation.Host` is an app, not a NuGet package).

## Method

Harness: `scripts/shared-shell-ac1/` (not in `ElsaControl.sln`).

- Tenant-group shell = CShells `GetOrActivateAsync` with host-owned
  `TenantGroupRuntime`: per-shell DI, EF SQLite catalog, `MemoryCache`,
  `JsonSerializerOptions`, named `HttpClient` + allowlist handler.
- Verified package = 12 activity types in a separate DLL, loaded with
  `AssemblyLoadContext(isCollectible: true)` from a memory stream. One ALC per
  revision, `ShareSingletonWithShells`, ref-counted, unloaded when the last
  shell terminator runs.
- Each (mode, group count) is a **fresh process**. 1 warmup + 10 iterations.
- Memory: forced full blocking+compacting GC × 4, then VmRSS, private bytes,
  `GC.GetTotalMemory`, `GC.GetGCMemoryInfo().HeapSizeBytes`.
- Cold start: `GetOrActivateAsync` → first `Verified.Ready.Execute`.
- Unload: `WeakReference` to the collectible context and assembly after host
  dispose.

Raw JSON: `scripts/shared-shell-ac1/results/ac1-20261015T171051Z.json`.

## Environment

| Item | Value |
| --- | --- |
| CPU | Intel Xeon (generic KVM), 4 processors |
| RAM | 16 GiB (MemTotal 16 780 628 KiB) |
| OS | Ubuntu 24.04.5 LTS, kernel 6.12.94+ |
| .NET | Runtime 10.0.13, SDK 10.0.402 |
| GC | Server GC, Interactive |
| CShells | 0.0.30 |
| Persistence | per-shell SQLite file (EnsureCreated on activate) |
| Host | Cursor Cloud Agent VM, not Shared-shard hardware |

## Results

10 iterations after 1 warmup, mean / p50 / p95.

### Density (settled, one process per group count)

| Scenario | Working set mean (MB) | Working set p95 (MB) | Managed heap mean (MB) | Managed heap p95 (MB) |
| --- | ---: | ---: | ---: | ---: |
| 1 shell | 107.4 | 111.4 | 2.91 | 2.99 |
| 5 shells | 119.2 | 125.7 | 3.65 | 3.84 |
| 20 shells | 153.9 | 172.2 | 6.23 | 6.63 |
| **Per extra shell (5→20)** | **2.31** | **3.11** | **0.17** | **0.19** |

Host + first shell is ~107 MB working set (CShells, SQLite native, JIT). Extra
groups add about **2.3 MB RSS / 0.17 MB managed heap** in this harness.

### Cold start (shell create → first ALC-backed activity ready)

| Timing | Mean | p50 | p95 |
| --- | ---: | ---: | ---: |
| 1 group, first shell total | 16.7 ms | 10.8 ms | 44.2 ms |
| 5 groups, first shell total | 12.1 ms | 11.1 ms | 15.9 ms |
| 20 groups, first shell total | 11.3 ms | 11.2 ms | 12.9 ms |
| Mean shell-build (20 groups) | 10.1 ms | 10.2 ms | 11.1 ms |
| Mean first-workflow (20 groups) | 0.07 ms | 0.07 ms | 0.07 ms |
| Reload first shell → first run (1 group) | 3.6 ms | 3.5 ms | 4.2 ms |

Largest phase: **shell build** (DI + EF `EnsureCreated` + catalog seed). First
activity execute is sub-millisecond after that.

### Collectible ALC unload

| Check | Result |
| --- | --- |
| `WeakReference` to collectible context | collected |
| `WeakReference` to package assembly | collected |
| Refcount at unload | 1 → 0 |
| Managed heap loaded → after | 2.24 MB → 1.72 MB |
| Working set loaded → after | 99.9 MB → 100.3 MB |

The collectible ALC unloads. Working set does **not** return to the 33 MB
process baseline: CShells, EF, SQLite native and JIT in the default context
stay. Replica recycle is still required when unload is not confirmed (ADR-0009
Amendment 1, 30 s).

## Conclusion

1. **Replace 60–150 MB / 2–6 s for this CShells-only slice only:** extra
   tenant-group shells cost ~**2.3 MB RSS** and ~**11–16 ms** to first
   activity. That is **not** Elsa 4 Workbench/runtime cost. The Architect's
   60–150 MB / 2–6 s estimate is about a full Elsa 4 shell (catalog, workflow
   runtime, EF persistence model, HTTP) and remains **unmeasured**.
2. **Keep the provisional Shared shard cap at 10 package-set groups.** At
   70% of 1.5 GiB headroom (1075 MiB) this harness would numerically allow
   ~400 extra CShells groups. Using that as a production cap would ignore the
   missing Elsa 4 runtime. CShells + collectible ALC is **not** the density
   limiter; the Elsa 4 host will be. Do not raise 10 until
   elsa-foundation#2757.
3. **Collectible ALC unload works** for a verified activity package with no
   mutable statics when the last shell releases it. Confirm with
   `WeakReference`. Working-set reclaim of the host is a separate problem.

## Blocking gaps for a faithful Shared-shard number

- No `Elsa.Foundation.Host` / Workbench in this repo.
- No Nuplane collectible production loader (foundation: test fixtures only;
  production loads are HostIntegrated).
- No Shared SKU in Control (Dedicated `standard-small` is 0.5 vCPU / 1 GiB).
- The 4 GiB replica / 1.5 GiB headroom figure is an unmeasured Architect
  estimate.
- CI VM vs Shared hardware: burstable Xeon, noisy neighbour, warm page cache,
  no ACA memory limit, no concurrent tenant traffic.

## Re-run

```bash
./scripts/shared-shell-ac1/run.sh
```
