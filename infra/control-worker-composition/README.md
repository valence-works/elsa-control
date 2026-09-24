# Control API worker composition

The Control API runs with its managed-instance lifecycle and Azure provider workers disabled by
default. This directory is the reviewed way to turn them on (#315, part of #264): a settings
template, environment-specific parameter files holding non-secret identifiers, a rollback file,
and a renderer that refuses to produce a payload until every referenced decision is made.

| File | Purpose |
| --- | --- |
| `worker-settings.template.json` | Every App Service setting the workers need; `${Name}` placeholders resolve from the parameter file. |
| `release-verification.template.json` | Governed release-manifest signature verification for the dogfood release admission (#311). Pins the linux/amd64 cosign digest; the production App Service host is amd64. |
| `worker-settings.parameters.production.json` | Production identifiers. A value shaped `{ "pending": "#N" }` is an undecided input and blocks rendering. |
| `worker-settings.parameters.staging.json` | Resolved staging authority for the dedicated workload subscription and provisioner identity. |
| `worker-rollback.json` | Turns the three worker switches and the health monitor off. Apply it as-is; it has no parameters. |
| `handoff-settings.staging.json` | Non-secret staging handoff issuer and console continuation settings. The signing key is excluded. |
| `handoff-rollback.json` | Turns the staging handoff switch off while preserving the signing key and other settings. |

Contract gates: `python3 scripts/tests/test_control_worker_composition.py` (renderer and file shape) and
`ProductionWorkerCompositionContractTests` in `tests/Hosting/ElsaControl.Api.Tests` (the rendered
settings compose through the same `AzureProviderRunnerComposition`, `AzureInstanceLifecycleComposition`,
`ElsaInstancePlanResolutionComposition` and `ManagedAzureProviderConfigurationValidator` seams that
`Program.cs` uses, against the `infra/azure-production` template authority the API image ships; the
release-verification template is composed through `ReleaseManifestVerifierComposition` the same way,
with the image-owned cosign and trust-root files stood in by digest-matched fixtures).
`dev/regenerate-infra.sh` preserves this directory.

## Isolated staging composition (#561)

`python3 scripts/render-worker-settings.py status --environment staging` lists decisions without
values. Both `workers` and `release-verification` accept `--environment staging`; production remains
the default. The staging renderer refuses **either** payload while any staging decision is pending.
It also rejects the production workload subscription, provisioner identity, registry role assignments,
release-verification identity, or Control origin in the staging profile. There is no arbitrary
parameter-file or value override.

The Azure provider creates a sibling resource group per engine. A staging resource group beside a
production group in the same subscription is therefore insufficient isolation: the provider needs
subscription-scope resource and role-assignment authority. The staging profile uses a dedicated
**workload subscription** with its own budget alert and a separate provisioner managed identity.
Grant that identity only the required workload-subscription roles and
the registry's narrow metadata/pull administration described in
`infra/azure-customer-subscription/README.md`. Do not attach the production provisioner or target the
production workload subscription. A budget alert is not a hard spending cap; bound the rehearsal to
one engine and remove run-scoped resources promptly. The `test` infrastructure deploy now requires
`AZURE_PROVISIONER_IDENTITY_ID`, so it cannot silently omit the staging identity attachment.
The current staging allocation is two EUR 250 monthly alert budgets: one for the existing Control
staging subscription and one for the dedicated workload subscription. Together they match the EUR 500
incremental staging ceiling, but Azure budgets only notify and cost data can lag. Check actual spend
before each rehearsal and stop or clean up if the projection would cross the ceiling.

Review the subscription, identity, SQL bootstrap egress, registry authority and release-verification
identity against their **non-secret** identifiers in the staging parameter file.
Confirm `ControlPlaneOrigin` is the isolated staging Control API. Then render to a private directory
outside the checkout:

```sh
python3 scripts/render-worker-settings.py workers --environment staging --output ~/.elsa-control-ops/staging-workers.json
python3 scripts/render-worker-settings.py release-verification --environment staging --output ~/.elsa-control-ops/staging-verification.json
python3 scripts/render-worker-settings.py rollback --output ~/.elsa-control-ops/staging-workers-off.json
```

The staging worker payload sets provider `BatchSize=1` to bound concurrent spend and a 45-minute
command timeout for a cold Container Apps environment. Production retains its 15-minute setting;
the staging bound stays below the runner's one-hour validation limit. A timed-out local command
does not prove Azure stopped working: inspect provider state and ownership before any recovery.
The admin-only `GET /api/admin/workspaces/{workspaceId}/instances/{instanceId}/operations/provider-current`
returns only the correlated provider status, phase, attempted step, checkpoint, and safe diagnostic
codes. Use it with the lifecycle topology and Azure deployment state before calling the guarded
recovery endpoint. A 409 recovery rejection is a stop signal, not a reason to submit Create again.
Before applying settings, capture the staging Web App's current image digest, worker switch values,
identity attachment, and health result. Verify the Azure CLI subscription/resource group/Web App
targets are the isolated staging Control API, and that the identity has **no** workload authority in
production. Apply the staging verification settings first. Apply the worker settings only after the
same-image staging startup and authority preflight succeed in a disposable rehearsal. Workers start
claiming queued work when enabled; do not use the production deployment workflow or a production
profile to perform this step. Before opening the customer Create flow, use the staging Control
console's **Releases** page to admit a reviewed producer manifest from the governed registry by
its immutable OCI reference, digest, and exact payload. The staging API verifies the signature
against its server-owned signer policy and assigns the catalog lifecycle; a registry tag or an
unverified local fixture is not a customer option. Confirm that the admitted paid Preview release
appears in the staging workspace's onboarding options. Keep the raw manifest payload and registry
credentials out of issues, CI output, and browser logs. Do not seed the catalog directly or copy
production catalog rows into staging. Verify one synthetic engine through Ready, Studio,
second-create denial, and confirmed Delete to provider absence. Confirm the included engine
allowance is reusable.

Rollback stops claims by applying `staging-workers-off.json` and restarting the staging Web App.
Preserve durable operation rows; do not truncate the staging catalog or queue. Restore the captured
image and configuration if startup or live verification fails, then check `/health` and that no new
provider operation is claimed. Remove only run-scoped staging resources after deletion reaches
provider absence. Record sanitized evidence in #561; never include rendered payloads, credentials,
customer identifiers, or provider resource IDs in an issue or CI artifact.

### Staging managed Elsa handoff (#561)

The handoff settings are governed separately from the provider workers. The checked-in staging profile
contains only `Enabled`, `Issuer`, and `CloudContinuationUrl`, and the renderer pins both URLs to the
staging API and console. The private signing key remains in the staging App Service configuration;
neither renderer command reads, exports, replaces, or prints it. Do not include it in an app-settings
payload, issue, or CI output.

Before enabling, confirm the staging API is healthy and the private signing key remains configured in
the staging Web App. Render the reviewed profile and apply only that payload to the staging Web App;
these commands print setting names and a payload digest, never values:

```sh
set -eu
: "${STAGING_CONTROL_SUBSCRIPTION:?Set the isolated staging Control subscription}"
test "${STAGING_CONTROL_RESOURCE_GROUP:?}" = "rg-valence-control-staging"
test "${STAGING_CONTROL_WEBAPP:?}" = "api-tud53zotij43k"
site_id=$(az webapp show --subscription "$STAGING_CONTROL_SUBSCRIPTION" \
  --resource-group "$STAGING_CONTROL_RESOURCE_GROUP" --name "$STAGING_CONTROL_WEBAPP" --query id --output tsv)
test "$site_id" = "/subscriptions/$STAGING_CONTROL_SUBSCRIPTION/resourceGroups/rg-valence-control-staging/providers/Microsoft.Web/sites/api-tud53zotij43k"
test "$(az webapp show --subscription "$STAGING_CONTROL_SUBSCRIPTION" \
  --resource-group "$STAGING_CONTROL_RESOURCE_GROUP" --name "$STAGING_CONTROL_WEBAPP" --query defaultHostName --output tsv)" = \
  "api-tud53zotij43k.azurewebsites.net"
mkdir -p ~/.elsa-control-ops
python3 scripts/render-worker-settings.py handoff --output ~/.elsa-control-ops/staging-handoff.json
az webapp config appsettings set --subscription "$STAGING_CONTROL_SUBSCRIPTION" \
  --resource-group "$STAGING_CONTROL_RESOURCE_GROUP" --name "$STAGING_CONTROL_WEBAPP" \
  --settings @"$HOME/.elsa-control-ops/staging-handoff.json" --query "[].name" --output tsv
az webapp restart --subscription "$STAGING_CONTROL_SUBSCRIPTION" \
  --resource-group "$STAGING_CONTROL_RESOURCE_GROUP" --name "$STAGING_CONTROL_WEBAPP"
```

Use the isolated staging Control API and console to verify a managed-instance handoff end to end,
including Studio and Structured Logs access. If startup or verification fails, render and apply the
handoff rollback, restart the same staging Web App, then confirm `/health` and that handoff is
unavailable. Rollback changes only `ManagedElsa__Handoff__Enabled`; it leaves the private signing key
and worker settings intact. Never apply either payload to production:

```sh
set -eu
: "${STAGING_CONTROL_SUBSCRIPTION:?Set the isolated staging Control subscription}"
test "${STAGING_CONTROL_RESOURCE_GROUP:?}" = "rg-valence-control-staging"
test "${STAGING_CONTROL_WEBAPP:?}" = "api-tud53zotij43k"
site_id=$(az webapp show --subscription "$STAGING_CONTROL_SUBSCRIPTION" \
  --resource-group "$STAGING_CONTROL_RESOURCE_GROUP" --name "$STAGING_CONTROL_WEBAPP" --query id --output tsv)
test "$site_id" = "/subscriptions/$STAGING_CONTROL_SUBSCRIPTION/resourceGroups/rg-valence-control-staging/providers/Microsoft.Web/sites/api-tud53zotij43k"
test "$(az webapp show --subscription "$STAGING_CONTROL_SUBSCRIPTION" \
  --resource-group "$STAGING_CONTROL_RESOURCE_GROUP" --name "$STAGING_CONTROL_WEBAPP" --query defaultHostName --output tsv)" = \
  "api-tud53zotij43k.azurewebsites.net"
mkdir -p ~/.elsa-control-ops
python3 scripts/render-worker-settings.py handoff-rollback --output ~/.elsa-control-ops/staging-handoff-off.json
az webapp config appsettings set --subscription "$STAGING_CONTROL_SUBSCRIPTION" \
  --resource-group "$STAGING_CONTROL_RESOURCE_GROUP" --name "$STAGING_CONTROL_WEBAPP" \
  --settings @"$HOME/.elsa-control-ops/staging-handoff-off.json" --query "[].name" --output tsv
az webapp restart --subscription "$STAGING_CONTROL_SUBSCRIPTION" \
  --resource-group "$STAGING_CONTROL_RESOURCE_GROUP" --name "$STAGING_CONTROL_WEBAPP"
```

## What the template binds

- Lifecycle worker, provider worker and instance provider are enabled together; the startup validator
  rejects any other combination.
- The Ready-instance health monitor (`Deployment__ElsaInstanceHealthMonitor__Enabled`, #394) is on. It
  may only run with the three workers (renderer and startup validator both refuse it alone); its code
  default is off. See [Instance health monitor](#instance-health-monitor-394).
- Cleanup observes Azure resource-group absence for up to 30 minutes
  (`Deployment__AzureProvider__Runner__CleanupObservationAttempts=360` at the five-second observation
  interval) before entering explicit recovery. Other deployment and health observations retain their
  existing five-minute budget.
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
The release inputs are decided on #311: the release feed (nuget.org since #439, which followed the
runtime images' earlier move to stable Elsa releases in valence-works/elsa-production-image#57 and
valence-works/elsa-production-image#60), the API identity (which already holds AcrPull on the governed
registry), the observed blob redirect host, and the keyless signing identity of the
`elsa-production-image` build workflow on `main` with the GitHub Actions issuer. Tighten the signer to
version tags before any release is marked Supported.
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
