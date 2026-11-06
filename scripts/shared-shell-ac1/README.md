# AC1 Shared-shell density harness (#798)

Repeatable process harness for issue
[798](https://github.com/valence-works/elsa-control/issues/798) AC1:
per-shell memory and cold-start on the **Architect's Shared model** (one CShells
shell per tenant group, one collectible `AssemblyLoadContext` per verified
package-set revision, shared and ref-counted across those shells).

This repository does **not** host Elsa 4. Managed launch is Dedicated Elsa 3.8
Combined; Shared is refused at plan admission. The harness therefore uses the
same shell host Elsa 4 uses (**CShells 0.0.30**) plus a representative
verified activity package loaded from disk into a collectible ALC. It does not
boot `Elsa.Foundation.Host`, Workbench, Nuplane, or `Elsa.Activities.Runtime`.

## What it measures

| Scenario | Definition |
| --- | --- |
| Density | Settled working set (VmRSS), private bytes, managed heap, and GC heap for **1, 5 and 20** tenant-group shells in one process. Each group has its own CShells `IServiceProvider`, EF SQLite catalog, `MemoryCache`, JSON options, and named `HttpClient`. |
| Cold start | Wall time from `IShellRegistry.GetOrActivateAsync` to first ALC-backed activity `Execute` (`Verified.Ready`). Also mean shell-build time, first-workflow time, and reload → first run. |
| Unload | Last shell terminator drops the ALC refcount to zero; `WeakReference` to the collectible context and assembly must die after forced GC. |

Each (mode, group count) runs in a **fresh process**. Default: 1 discarded
warmup + 10 iterations. Report mean, p50 and p95.

## Run

From the repo root (requires .NET 10 SDK and the CShells feed in `NuGet.config`):

```bash
./scripts/shared-shell-ac1/run.sh
```

Overrides: `ITERATIONS=10 WARMUP=1 AC1_GROUPS=1,5,20 CONFIG=Release`.
Do not use `GROUPS`; some agent images set that to an unrelated value.

JSON is written to `scripts/shared-shell-ac1/results/ac1-<utc>.json` and linked
as `results/latest.json`.

```bash
dotnet run --project scripts/shared-shell-ac1/ElsaControl.SharedShellAc1.csproj -c Release -- \
  --package scripts/shared-shell-ac1/verified-package/bin/Release/net10.0/ElsaControl.SharedShellAc1.VerifiedPackage.dll \
  --mode density --groups 5 --iterations 10 --warmup 1
```

## Shard cap

The Architect's working example on #798 is a **4 GiB replica with ~1.5 GiB
headroom**, cap at **~70% of headroom**. That SKU is not implemented in this
repo (Dedicated default is `standard-small`, 0.5 vCPU / 1 GiB). The harness
prints the measured per-shell delta; the recommended cap is

`floor(0.70 * 1536 MiB / per-shell working-set delta)`.

Treat that as an **upper bound on package-set groups**, not on tenants: tenants
that share a package set share a shell.

## Not in CI

The harness is not in `ElsaControl.sln`. A full 1/5/20 run is a multi-minute
process test and is not part of `dotnet test ElsaControl.sln`.
