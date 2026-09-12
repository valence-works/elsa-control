# Azure workload-plan adapter

This project is the boundary between a governed
`ResolvedElsaApplicationPlan` and Azure workload realization. Translation is
pure; provider execution is performed by an injected runner, and durable
operation state is owned by the catalog store. The project performs no
unmediated Azure calls and starts no processes itself. Checked-in Bicep owns
the resource model; provider lifecycle code consumes the accepted intent.

The first admitted provider capability is deliberately narrow:

- West Europe (`westeurope`), North Europe (`northeurope`), or Sweden Central (`swedencentral`)
- Dedicated isolation
- Combined topology with one component
- Elsa release line 3.8
- Paid images from the governed `valenceruntimeimages.azurecr.io` authority
- Public HTTPS/TLS endpoints with unrestricted egress and no private connectivity
- Workload capacity with an exact Azure Container Apps consumption size
  (`AzureContainerAppsCapacity`); the runner never rounds or falls back to
  template defaults

Release line and exact version remain strings in the provider-neutral schema.
The capability check rejects an unsupported later line with a provider finding;
it does not introduce a closed Elsa-version enum or change the schema.

Translation fails closed unless the resolved plan is valid, image identity is
immutable, and the admitted release carries matching `release-manifest` plus
safe `release-manifest-signature` evidence. Output contains only immutable
identities, non-secret placement facts and `secret://` references. It never
contains secret values, credentials, manifest payloads or signer identities.

The fingerprint is SHA-256 over a versioned, canonical projection of the typed
workload intent and normalized Azure target facts. Equivalent plans therefore
produce the same fingerprint, and changes to resource-affecting governed inputs
produce a different one. The unhashed canonical input is not exposed. Workload
capacity is part of that projection and of the persisted operation, so a
restarted worker deploys exactly the admitted sizing; an operation retained
before capacity existed restores without it and cannot deploy a workload.

## Durable provider execution

`AzureProviderExecutor` is the durable orchestration seam for the accepted
workload profile. It creates or reuses an operation by its idempotency key,
claims it with a lease, and checkpoints the following runner steps:

1. foundation deployment from `infra/azure-workload-proof/main.bicep`;
2. exact ACR pull assignment from `acr-pull-role.bicep`;
3. Key Vault reference seeding;
4. the contained-identity SQL bootstrap from `sql-bootstrap.sql`;
5. workload deployment, candidate health verification and traffic promotion.

The runner receives only the typed plan, safe resource references and secret
locators. Secret values and raw ARM/Bicep payloads are not representable in the
executor contract. Evidence references are absolute OCI/HTTPS locators with a
separately retained strict `sha256` digest; embedded OCI digests, when present,
must match. Foundation substeps are idempotent by contract, so an
interruption safely repeats the current substep group from its durable
checkpoint. A lease heartbeat runs while a remote step is active; loss of the
lease stops local mutation and leaves the operation for its current owner.

Promotion failures invoke the runner's stable-traffic restoration step. An
uncertain promotion or cleanup remains `RecoveryRequired` until its external
effect is confirmed. Cleanup only succeeds when the runner reports exact
proof-owned resource absence (no resource references, endpoint or health fact).
If no reconcile snapshot exists, an empty reference set is valid only for a
never-provisioned target; the executor does not infer absence from that empty
set. The trusted runner must independently prove exact absence from the plan
and return `OwnedResourcesAbsent`, otherwise cleanup remains failed or requires
recovery.

`AzureProviderOperationService` is the API/worker admission seam. It accepts
only the typed provider-safe projection, persists the immutable evidence
locators, digests and secret locators needed for recovery, and never accepts a
raw resolved-plan payload. `AzureProviderOperationWorker` polls accepted or
recoverable operations and uses `PersistedAzureProviderPlanSource` to rebuild
that projection after restart. The hosted API worker is disabled by default;
an application enables it only when an approved `IAzureProviderRunner`
implementation is registered. The default runner fails closed, which keeps a
misconfigured host from mutating Azure.

`AzureProviderProofAdapter` implements the provider-neutral deployment-proof
contract for a disposable Azure run. A live proof host supplies the admitted
plan factory, workflow probe and concrete runner; the adapter preserves exact
selection identity, durable operation idempotency and cleanup semantics.

See [ADR-0004](../../../docs/adr/0004-deployment-engine-typed-reconciliation-hybrid.md),
[ADR-0007](../../../docs/adr/0007-provider-neutral-elsa-application-desired-state.md)
and [ADR-0010](../../../docs/adr/0010-initial-azure-workload-platform.md).
