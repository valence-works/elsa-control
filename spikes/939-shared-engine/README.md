# #939 shared-engine L0 spike

Staging-only harness for ADR-0019 leaf L0. Pins `elsa-workflows/elsa-core` at `59b14969` and CShells `0.0.28`. Not part of `ElsaControl.sln`; existing CI workflows are unchanged.

```bash
./restore-elsa-core.sh
./run-spike.sh
```

`ElsaCoreRoot` defaults to `/tmp/spike/elsa-core` so Elsa projects do not pick up this folder's `Directory.Packages.props`.

Optional Azure SQL (at most three Standard S0 databases on one staging server):

```bash
export SPIKE_SQL_CONNECTION='Server=...;Database=master;...'
./run-spike.sh
```

Operator bicep: `infra/main.bicep`. Tags `purpose=shared-engine-spike` and `issue=939`. Staging only.

Diagnostics modules stay disabled. Proto.Actor is not enabled. An open Elsa 3 item stays named only as that.

CShells 0.0.28 host routing needs the spike's endpoint-name/Host stamping, host selector policy, and HTTP gate. Those are documented in `docs/spikes/939-shared-engine-l0.md`.
