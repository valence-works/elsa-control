# Azure provider worker composition

The API composes `AzureBicepProviderRunner` only when
`Deployment:AzureProvider:WorkerEnabled=true` and the concrete runner is
explicitly enabled and fully validated. The default API configuration keeps the
provider fail-closed.

## Optional provisioning capability

The Azure deployment project contributes `AzureEngineProvisioningModule` through
the provider-neutral `IEngineProvisioningModule` contract. The API registers it
only when `Deployment:AzureProvider:InstanceLifecycle:Enabled=true` and the
existing Azure runner authority and lifecycle options validate successfully.
There is no separate console feature flag that can enable the provider.

Authenticated workspace members can discover enabled modules through
`GET /api/workspaces/{workspaceId}/engine-provisioning/providers`. Its uncached
response is `{ "providers": [] }` when none are enabled, or contains
`{ "id": "azure", "displayName": "Azure" }` when Azure is composed. This is a
capability signal; workspace permissions, commercial eligibility, and governed
release admission still apply to creation.

The console shows **Provision engine** beside **Connect engine** only after Azure
discovery succeeds, and links to the shared
[Provision engine / Runtime Builder flow](engine-provisioning-flow.md).
When no provisioning module is composed, creation and onboarding-options requests
return `503` with code `engine-provisioning.disabled`. Existing instance read
routes remain available. Discovery does not itself establish the managed
instance's deployment-environment and workflow-engine binding.

## Runner configuration

An enabled worker host must provide absolute, non-symbolic paths for the pinned
`az`, `sqlcmd`, and `curl` executables, plus the checked-in Bicep/SQL template
root. It must also provide the exact target subscription/resource-group/registry
scope under `Deployment:AzureProvider:Runner:TargetScope`. Startup rejects
partial configuration; it never silently falls back to an unconfigured runner.
When managed-instance lifecycle integration is enabled, its template and
provider-scope fingerprints are derived directly from this validated runner
authority. The host rejects lifecycle enablement without the concrete worker;
there is no duplicated or shipped fallback fingerprint that can become stale.

`Deployment:AzureProvider:Runner:ReleaseFeedServiceIndex` selects the trusted
server-governed NuGet service index used for the admitted release's exact optional
SQL package versions. It defaults to `https://api.nuget.org/v3/index.json`; a host
using producer packages from another feed must explicitly configure that feed.
The locator must be safe HTTPS without credentials, query strings, fragments, or
non-default ports. It is passed to both production deployment phases and bound
into provider execution authority, so it must not change during an operation.
This host setting does not admit packages, infer versions, or replace release
signature verification. The standalone disposable-proof template keeps its
separate feed contract.

SQL bootstrap invokes the pinned `sqlcmd` with `-b` in both production and
disposable-proof modes. SQL batch errors must produce a nonzero process result;
printed SQL output is never interpreted as success or retained as diagnostics.
An unconfirmed bootstrap remains uncertain. After confirmed firewall creation,
the temporary rule must still be deleted and verified absent when SQL execution
fails. An uncertain firewall-create result remains recovery-owned; it does not
establish that the rule is absent.

The worker receives only admitted immutable plan identities and governed secret
references. Secret aliases are configured under
`Deployment:AzureProvider:Secrets:<index>:Name` and `Reference`. The
name binds the governed reference to a required resolved-plan configuration slot;
only the reference crosses into lifecycle resolution and durable provider
records. External names bind immutable Key Vault locators; the provider-owned
SQL, identity-signing-key, and admin-password instructions are the internal
exceptions. Names must already be canonical
lower-case keys, and no two names may collapse to the same Azure secret name
after `:` and `_` are mapped to `-`. A
missing, unsafe, or ambiguous named alias fails startup closed when managed
instance lifecycle is enabled. External configuration accepts absolute, versioned
Key Vault HTTPS secret locators without credentials, query strings, or fragments.
They are normalized to `secret://<vault>.vault.azure.net/secrets/<name>/<version>`
before entering plans and durable operations; that exact canonical form is also
accepted in configuration. HTTPS/canonical aliases of the same locator cannot be
assigned to two names. The managed resolver requires the exact persisted canonical
reference, and converts it to the fixed HTTPS vault origin only when reading the secret.
The external source secret name must match the slot's governed Azure name, using the
same case-insensitive binding at startup and during durable authorization. In particular,
`database:connectionstring` binds `sql-connection` for an external SQL reference.
A mismatched source name fails startup before provisioning; it is not silently
remapped or authorized later. Production admin and signing slots require the
provider-owned instructions below, not external source locators.
An enabled production worker rejects configured `Value` entries and disposable
proof mode. Its managed identity resolves values only after checking the durable
workspace, organization, instance, assignment and running-operation authorization.
Secret values remain runtime-only and never enter provider contracts, diagnostics,
or durable records. The local configured-value resolver is not the production
composition.

For a normal production workload, the canonical `database:connectionstring`
slot may use the fixed provider-owned reference
`secret://azure-managed/sql-connection`. This is an internal instruction, not a
Key Vault locator: during the post-Foundation `SeedSecrets` step the managed
identity resolver authorizes the current durable assignment and materializes a
passwordless SQL connection from its persisted SQL server and workload identity
resource references. The caller-provided resource snapshot is not trusted.
The `identity:signingkey` and `admin:password` slots use the exact provider-owned
instructions `secret://azure-managed/identity-signing-key` and
`secret://azure-managed/admin-password`; the resolver generates distinct
cryptographic material for each authorized instance and the runner seeds it into
that instance's provider-owned target vault. Each internal reference is rejected
for every other slot.

Generated credentials are retained for the assignment lifetime. Reuse requires
exact provider-assignment, instance, slot and generation tags, read through the
metadata-only secret list API. Missing, foreign or ambiguous metadata never
authorizes overwriting an existing value. An absent generated credential during
resume requires explicit recovery rather than silent regeneration. A create retry
is not a rotation operation. Secret tags bind to the placement-stable assignment
identity, not the template fingerprint, so a governed rebind does not need to
re-tag existing secrets.

A template-only or tool-digest change rotates the provider-scope fingerprint
without changing subscription, resource group, identity, or assigned resources.
The assignment store then performs an explicit, audited rebind: it records the
old and new fingerprints and the lifecycle trigger, and updates the live
assignment and its ownership key to the current scope. Admitted operations keep
the fingerprint they were submitted with; observe, cleanup, and recovery accept
that lineage. Rebind refuses when an Accepted, Queued, EntitlementHeld, or
Running operation must drain first. RecoveryRequired operations are rebound so
parked recovery can resume on the new Control build. A placement change is
never rebound. These gates do not replace the separate two-instance negative
authentication and confirmed-cleanup acceptance proof tracked in #287.

## Secret-seeding generation guard

The production runner binds every transient secret-resolution request to the
accepted provider operation's trusted `OperationId` and `AttemptNumber`. The
managed resolver compares both values with the current durable operation, in
addition to the workspace, organization, instance, assignment, running lease,
phase, resource and reference checks. A request from an old claim or a missing
generation is rejected before secret material is read or generated. The legacy
request-constructor shape remains available for local and proof callers, but a
durable managed resolver fails closed when its operation generation is absent.

Immediately before `keyvault secret set`, the runner performs a second,
non-materializing authorization check with the same request. This closes the
stale-generation window when a generation change is observed before submission,
without resolving or retaining a second secret. The lease may still change after
that check. It is a durable admission check, not physical remote fencing:
an Azure request already submitted, or already in flight when a lease is lost,
cannot be withdrawn by this process.

This guard is one #287 gate. It does not replace #271's durable attempted-step,
read-only provider observation, replay and live-recovery gates. In particular,
an uncertain seed is not silently regenerated, and recovery must still observe
the provider's retained state before any permitted resume.
