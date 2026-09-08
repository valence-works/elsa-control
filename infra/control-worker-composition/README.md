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
| `worker-rollback.json` | Turns the three worker switches off. Apply it as-is; it has no parameters. |

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
At the time of writing `SqlBootstrapIp` waits on #310 (one static egress address for the API) and the
release feed, verification identity, blob redirect host and producer identity wait on #311.
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
