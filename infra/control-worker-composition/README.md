# Control API worker composition

The production Control API runs with its managed-instance lifecycle and Azure provider workers
disabled. This directory is the only reviewed way to turn them on (#315, part of #264): a settings
template, a production parameter file holding non-secret identifiers, a rollback file, and a
renderer that refuses to produce a payload until every referenced decision is made.

| File | Purpose |
| --- | --- |
| `worker-settings.template.json` | Every App Service setting the workers need; `${Name}` placeholders resolve from the parameter file. |
| `release-verification.template.json` | Governed release-manifest signature verification for the dogfood release admission (#311). Pins the linux/amd64 cosign digest; the production App Service host is amd64. |
| `worker-settings.parameters.production.json` | Production identifiers. A value shaped `{ "pending": "#N" }` is an undecided input and blocks rendering. |
| `worker-rollback.json` | Turns the three worker switches and the health monitor off. Apply it as-is; it has no parameters. |

Contract gates: `python3 scripts/tests/test_control_worker_composition.py` (renderer and file shape) and
`ProductionWorkerCompositionContractTests` in `tests/Hosting/ElsaControl.Api.Tests` (the rendered
settings compose through the same `AzureProviderRunnerComposition`, `AzureInstanceLifecycleComposition`,
`ElsaInstancePlanResolutionComposition` and `ManagedAzureProviderConfigurationValidator` seams that
`Program.cs` uses, against the `infra/azure-production` template authority the API image ships; the
release-verification template is composed through `ReleaseManifestVerifierComposition` the same way,
with the image-owned cosign and trust-root files stood in by digest-matched fixtures).
`dev/regenerate-infra.sh` preserves this directory.

## What the template binds

- Lifecycle worker, provider worker and instance provider are enabled together; the startup validator
  rejects any other combination.
- The Ready-instance health monitor (`Deployment__ElsaInstanceHealthMonitor__Enabled`, #394) is on. It
  may only run with the three workers (renderer and startup validator both refuse it alone); its code
  default is off. See [Instance health monitor](#instance-health-monitor-394).
- Runner identity is the provisioner `mi-elsa-cloud-provisioner-prod-weu` (already attached to the API),
  which is also the SQL bootstrap principal and login. Target scope is the anchor resource group in the
  **Elsa Cloud — Customer Workloads** subscription; sibling per-instance groups derive from it (v1 naming).
  The registry scope is `valenceruntimeimages` in the Control subscription with the Narrow registry
  authority IDs read back from `infra/azure-customer-subscription/registry-authority.bicep`.
- Secrets are the three provider-owned instructions only (`secret://azure-managed/...`); raw values and
  external admin/signing locators are rejected at composition.
- `RuntimeBuilder__InstancePlans__DefaultEgress=unrestricted` (the initial public Azure profile) and
  `ControlPlane__Origin` (the plan authority) are set explicitly; neither exists in production today.
- Tool paths and `TemplateRoot` are **not** in the template. The API image sets them (`Dockerfile` ENV) and
  the renderer refuses them, so an app setting can never retarget the runner's tools.

## Pending decisions

`python3 scripts/render-worker-settings.py status` lists resolved and pending parameters without values.
The release inputs are decided on #311, with the feed moved to nuget.org on #439 once the runtime images
pinned stable Elsa releases: nuget.org as the release feed, the API identity (which
already holds AcrPull on the governed registry), the observed blob redirect host, and the keyless
signing identity of the `elsa-production-image` build workflow on `main` with the GitHub Actions
issuer. Tighten the signer to version tags before any release is marked Supported.
Release verification reads the governed runtime registry `valenceruntimeimages` (resource group
`rg-valence-runtime`) as the API identity. That identity's `AcrPull` assignment on that registry
(assignment name `eae590a3-2208-4b1a-9500-7ac9feaeff41`, created 2026-09-05 for the #270
verification work) is a live prerequisite that is not declared in this repository's infrastructure;
the `elsa-control` module only grants pull on the Control registry to the image-pull identity.
Without it, admission fails closed. Re-check it before enabling verification.
`SqlBootstrapIp` is the static NAT address of the #310 egress deployment in Belgium Central; the
API site routes all outbound traffic through it. No parameter is pending.
A pending parameter is filled only by editing the parameter file in a reviewed PR. The renderer has
no command-line override and reads no other parameter file, so a value that was never reviewed
cannot be rendered. Rehearsals against disposable targets use the #265 proof harness configuration,
not this renderer.

## Enabling workers in production (#313)

1. Confirm production is healthy and note the running image digest; confirm no parameter is pending.
2. Render both payloads into a private directory (mode 0600, outside the checkout):
   ```sh
   python3 scripts/render-worker-settings.py workers --output ~/.elsa-control-ops/worker-settings.json
   python3 scripts/render-worker-settings.py rollback --output ~/.elsa-control-ops/worker-rollback.json
   ```
   The renderer prints setting names and the payload SHA-256 only.
3. Rehearse the payload against a disposable host first (the #265 proof harness composes the same seams).
4. Apply to the API and restart:
   ```sh
   az webapp config appsettings set --subscription 8e23037a-420f-4ad0-9594-9d194de29e84 \
     --resource-group rg-valence-control-prod --name api-m5uymkuaf222o \
     --settings @$HOME/.elsa-control-ops/worker-settings.json --query "[].name" --output tsv
   az webapp restart --subscription 8e23037a-420f-4ad0-9594-9d194de29e84 \
     --resource-group rg-valence-control-prod --name api-m5uymkuaf222o
   ```
5. Verify startup health, the read-only authority preflight result in the sink, and an unchanged
   resource inventory in the customer workload subscription (workers idle create nothing).

Rollback is the same `appsettings set` with `worker-rollback.json` followed by a restart; the other
settings are inert while the switches are off and are removed only through a reviewed change.

## Instance health monitor (#394)

Without the monitor, Control's `Health` for a Ready instance is the observation of its last lifecycle
operation. With it, every `Interval` the API re-probes each instance whose desired lifecycle is
`Running` and observed lifecycle is `Ready`, that is managed, not tombstoned, has a current deployment
endpoint and has no blocking lifecycle operation (`Accepted`, `WaitingForPriorOperation`, `Queued`,
`EntitlementHeld`, `Running`, `RecoveryRequired`). Deleting, deleted, stopped and busy instances are
never probed. The Azure probe is the promotion probe's `curl --fail` of `{origin}/health` on the
instance's own verified Container Apps origin with the byte-exact `Healthy` classification, but a single
attempt bounded by `ProbeTimeout` and no retries.

`Health` changes to the probe's classification (`Degraded`, `Unreachable` for no answer or a timeout,
`Unknown` when Control cannot classify) only after `UnhealthyThreshold` consecutive failed probes, and
back to `Healthy` only after `HealthyThreshold` consecutive healthy probes. The observed lifecycle stays
`Ready`. A change commits only over the exact instance version the probes ran against, so a concurrent
lifecycle change always wins; each change appends one `lifecycle.health-changed` audit event, and every
probe emits one `managed_lifecycle.endpoint.health.evaluations` measurement.

| Setting (`Deployment__ElsaInstanceHealthMonitor__…`) | Default | Bounds |
| --- | --- | --- |
| `Enabled` | `false` (the template sets `true`) | requires the three worker switches |
| `Interval` | `00:01:00` | 15 s – 1 h |
| `MaxJitter` | `00:00:15` | 0 – half the interval; each instance keeps a stable offset |
| `ProbeTimeout` | `00:00:10` | 1 s – 1 min, and `MaxJitter + ProbeTimeout` below the interval |
| `MaxConcurrency` | `4` | 1 – 32 probes at a time |
| `UnhealthyThreshold` | `3` | 1 – 10 consecutive failures |
| `HealthyThreshold` | `2` | 1 – 10 consecutive successes |

Operational notes:

- A runtime whose capacity lets it scale to zero (minimum replicas 0) is kept warm by a probe every
  minute, because Container Apps only scales in after an idle cooldown. Enabling the monitor therefore
  changes that runtime's cost and load; raise `Interval` to trade freshness for load.
- `Open` requires `Healthy`. An instance the monitor records non-healthy cannot be opened until it
  recovers, including when Control itself cannot probe (for example the runner's curl is unavailable):
  that fails closed after `UnhealthyThreshold` cycles.
- Turning the monitor off freezes `Health` at its last value. An instance it left non-healthy returns
  to `Healthy` only through a `Reconcile` operation or by turning the monitor back on.
- Hysteresis streaks are in-process: a restart starts them over, and each scaled-out API instance probes
  independently (commits stay safe through the version check).
