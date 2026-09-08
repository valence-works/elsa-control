# Opt-in production Azure lifecycle proof

`ProductionAzureLifecycleProofTests` is the bounded, destructive integration proof for the
managed Azure instance path. It is deliberately skipped unless the opt-in gate is set. The
test uses `WebApplicationFactory<Program>` with `Production`, the real `Program` registrations,
the configured Azure runner, hosted lifecycle workers, and the SQLite migration assembly. It
does not use `ProofHost`, a fake provider, a fake health probe, or a fake secret resolver.

## Invocation

Run this from an Azure-hosted test process with the candidate API/CLI image and a user-assigned
managed identity attached. Before starting the testhost, stage the approved normal production
settings plus harness settings as `appsettings.Production.json` directly inside the absolute API
content root. The early environment and staged filename are part of the fail-closed packaging
contract; a differently named configuration file is rejected even if its contents are equivalent.
For the Linux proof package, invoke its reviewed wrapper rather than calling the testhost directly:

```sh
/bin/bash /run/elsa-control/run-proof.sh
```

The packaged wrapper sets `ASPNETCORE_ENVIRONMENT=Production`, unsets conflicting
`DOTNET_ENVIRONMENT`, sets the opt-in gate, and pins the API content root and staged configuration
path before starting the testhost. It supplies a transient test API key and the validated outbound
SQL-bootstrap IP required by the enabled runner, applies the overall timeout, and enforces the
result-capture and evidence-retention gates below. The reference Linux package layout uses
`/src/src/Hosting/ElsaControl.Api` as its content root and the SDK's VSTest entrypoint against
`/run/elsa-control/tests/api/ElsaControl.Api.Tests.dll`. These are package inputs, not paths to infer
from a developer checkout. A direct testhost invocation without the wrapper's inputs and result
gates is a diagnostic run, not an accepted lifecycle proof.

When the gate is absent, xUnit reports an explicit skipped test; it never silently passes.
`ELSA_CONTROL_LIVE_AZURE_LIFECYCLE_PROOF_CONFIG` is required once the gate is enabled. The
configuration file must contain the normal production settings plus these harness settings:

| Key | Requirement |
| --- | --- |
| `LiveProof:CatalogEntryPath` | Absolute or config-relative JSON file containing one trusted, previously admitted `GovernedReleaseCatalogEntry`; this bypasses producer admission by design. |
| `LiveProof:EvidenceDirectory` | Writable directory for value-free evidence on success or failure. |
| `LiveProof:InstanceId` | Required fresh canonical GUID for this run; use a new isolated database for reruns. |
| `LiveProof:ActorIssuer` / `LiveProof:ActorSubject` | Fixture identity used to create the owner account. |
| `LiveProof:ActorEmail` | Fixture account email; no credential. |
| `LiveProof:InstanceName` / `LiveProof:InstanceSlug` | Fresh instance identity. |
| `LiveProof:TimeoutSeconds` | Optional bound, 30–7200 seconds; default 1800. |
| `LiveProof:PollSeconds` | Optional bound, 1–30 seconds; default 5. |

The normal production configuration must also provide `ControlPlane:Origin` as an HTTPS origin,
`Database:Provider=Sqlite`, and an isolated absolute `ConnectionStrings:Catalog` SQLite path whose
filename contains `live-proof`. The path must not exist before the run (the harness also rejects
SQLite WAL/SHM sidecars), so an accidental rerun cannot attach to an existing catalog or instance.
For example, `elsa265-ninth-live-proof.db` satisfies the filename guard, while
`elsa265-ninth-proof.db` does not. Exercise the actual input guard offline against the packaged
configuration before granting temporary Azure execution permissions; merely compiling the proof
does not validate its deployment-specific inputs.
It also requires
`DataProtection:KeysPath` on durable storage, all lifecycle and
provider workers enabled, the v1 instance-provider scope, pinned CLI/sqlcmd/curl/template paths,
and the three provider-owned secret instructions. The database slot uses
`secret://azure-managed/sql-connection`; signing-key and
admin-password use `secret://azure-managed/identity-signing-key` and
`secret://azure-managed/admin-password`. The managed resolver generates those credentials
per authorized instance and seeds only that instance's provider-owned target vault. They are
lifetime-stable for the assignment; rotation, restore, or rebind does not regenerate or
overwrite existing or ambiguous target metadata and fails closed until a dedicated operation
exists. External versioned Key Vault references remain supported for other slots,
including the pre-existing external SQL-reference path, but production composition
rejects external admin/signing credential locators and raw values. This production
proof uses the passwordless SQL instruction rather than an external SQL source.
The provider-owned instructions bind to the governed target names. The production
configuration uses exact provider-owned credential instructions.
Raw secret values are not accepted.
HTTPS configuration locators are normalized to canonical `secret://` plan references.
Set `RuntimeBuilder:InstancePlans:DefaultEgress=unrestricted` explicitly for this Azure
profile; the default remains `restricted`, which this profile cannot realize. The admitted
release must declare exact `Elsa.Persistence.EFCore.SqlServer` and
`Elsa.Scheduling.Quartz.EFCore.SqlServer` package versions. Validate the catalog-to-plan-to-Azure
translation offline before provisioning; never infer missing versions from the Elsa release.
Configure `Deployment:AzureProvider:Runner:ReleaseFeedServiceIndex` to the verified
producer feed that actually contains those exact package versions. The default
is NuGet.org; the Build 147 SQL preview proof uses
`https://f.feedz.io/elsa-workflows/elsa-3/nuget/index.json`. This credential-free
HTTPS locator is server configuration bound to execution authority, not a
caller-supplied package source or evidence of production release admission.

## Azure prerequisites

The host identity must be the same identity selected by `Runner:AzureCliClientId` and must be
attached to the host. It needs:

- subscription-level permission to create/delete the generated v1 sibling resource group and
  mutate its descendants;
- the governed ACR resource-group permissions, including the exact `AcrPull` role assignment;
- no source Key Vault access is required for the provider-owned credential slots;
- SQL Entra bootstrap permission, with `Runner:SqlBootstrapObjectId` and the approved bootstrap
  login matching the identity;
- access to the immutable runtime image and its governed catalog projection.

The production host must report the pinned Azure CLI managed-identity account shape during
preflight. A developer's interactive `az login` is not a valid substitute. Production trusted
workspace headers must remain disabled; the fixture actor ID is persisted through the actual
account/workspace stores and passed to the lifecycle service.

## Promotion readiness expectation

Since #267's health-before-promotion correction, every traffic promotion the workers perform
probes the candidate revision on its own revision-scoped host (`{revision}.{environment domain}`)
and requires the plain-text `Healthy` report before `traffic set`. A live upgrade proof therefore
records the candidate probe preceding the traffic shift and, for the injected unhealthy candidate,
one of `azure.promotion.candidate-not-ready`, `azure.promotion.candidate-readiness-invalid`,
`azure.promotion.candidate-unready` or `azure.promotion.candidate-endpoint-invalid` with the
stable revision still serving. The two-release live proof
remains open on #267 until it runs against the dogfood instance.

## What the test proves

The test seeds an isolated actor, organization entitlement, and catalog entry using the real
stores, then creates a managed instance through `ElsaInstanceLifecycleService`. The accepted
operation is processed by the real hosted workers until the Azure provider reaches `Ready` and
`Healthy`. It then disposes and recreates the production factory against the same SQLite database,
submits a fresh reconcile, and verifies that the durable provider assignment is retained. Finally
it creates and consumes a real delete confirmation, waits for `Deleted`, and verifies the v1
assignment is `Deleted` with only its immutable resource-group identity retained and no
remaining workload-resource inventory.

The accepted asynchronous hand-off deliberately uses lifecycle `RecoveryRequired` while the
provider is pending. The proof polls the exact assigned provider operation rather than failing
on that lifecycle state alone. Provider `Accepted`/`Running` remains pending; provider
`RecoveryRequired`, failure, cancellation, or invalid correlation fails the run. Provider success
alone is insufficient: the lifecycle must still reach the healthy or confirmed-deleted state.
The runner's command timeout defaults to 15 minutes and can be explicitly configured with
`Deployment:AzureProvider:Runner:CommandTimeout` for a bounded cold-start allowance; the harness's
overall bound is separate. A local command timeout does not establish that Azure stopped working.
The external test-process limit must cover both the primary proof budget and its independent
failure-cleanup budget, plus shutdown headroom. For a 7200-second proof budget, use a bounded
15000-second test-process limit rather than allowing only a few minutes for failure cleanup.
Evidence upload has its own bounded allowance after the test process exits. If the outer limit
expires, retain the host and exact cleanup authority until owned resources and available evidence
are accounted for; never infer cleanup from the process being terminated.

Every polling loop is bounded. A failure attempts product cleanup once more through the lifecycle
service. If the exact predecessor and its assigned provider operation both require explicit
recovery, a waiting Delete is reported as blocked cleanup promptly rather than waiting out the
full proof timeout. This neither releases the reservation nor replays uncertain provider work.
The catalog database is never deleted. The evidence file contains only safe organization,
workspace, account, instance, operation, assignment, and provider-operation IDs plus stage and
cleanup status and scope (`none`, `local`, or `provider`); provider exceptions, configuration,
connection strings, secret values, and raw Azure output are not written.
If resolution fails before an assignment exists, successful local deletion is recorded as
local-only cleanup. It cannot satisfy the overall proof, which requires provider-scoped cleanup.

This proof covers provider apply/reconcile/reload/delete ownership and correlation. It does not
claim release-manifest producer admission or public customer authentication: the fixture starts
from an already admitted catalog projection and does not exercise the administrator admission
endpoint. Production signature verification is a separate explicitly configured adapter and
remains fail-closed by default.

## External result capture

A wrapper must require one passing result for the exact live test name, not just a zero testhost
exit code; an empty filter selection or skipped test can otherwise appear successful. Retain only
fixed outcome fields from a bounded result parser, and do not upload raw test output or TRX files.
Overall success also requires the isolated catalog and the product's success evidence with
`cleanupSucceeded=true` and `cleanupScope=provider`.

Input validation runs before the application and may fail before any catalog or product evidence
file exists. Always retain a separate value-free runner-result record for that case. A successful
upload command over an empty source set is not evidence that a catalog or result was retained.
Unset transient authentication credentials before evidence upload. Record temporary role IDs and
resource targets before execution, and independently verify their removal after bounded evidence
retention; emergency cleanup is never a product lifecycle pass.
