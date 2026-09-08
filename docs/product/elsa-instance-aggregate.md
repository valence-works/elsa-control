# Elsa Instance Aggregate and Lifecycle Contract

**Status:** Accepted architecture (issue [#114](https://github.com/valence-works/elsa-control/issues/114)); initial lifecycle, API and provider slices were implemented by the follow-up tasks listed below
**Date:** 2026-08-29
**Scope:** Provider-neutral domain, persistence/API migration and follow-up delivery slices

This document defines the customer-visible `ElsaInstance` boundary. It is an
implementation contract for the first lifecycle slices; it does not add the
persistence or API implementation in this task.

The contract is governed by [ADR-0006](../adr/0006-unified-commercial-product-family.md),
[ADR-0007](../adr/0007-provider-neutral-elsa-application-desired-state.md),
[ADR-0004](../adr/0004-deployment-engine-typed-reconciliation-hybrid.md), and the
[resolved application-plan contract](resolved-elsa-application-plan.md).

## Decision summary

- `ElsaInstance` is a new customer-facing aggregate root under an existing
  `Organization` and `Workspace`. It is not a rename of a deployment application,
  environment, or runtime engine.
- One instance represents one managed Elsa environment. It owns customer intent
  and lifecycle-operation identity; it does not own provider resources or workflow
  execution state.
- `WorkspaceDeploymentApplication` remains the logical grouping used by the current
  deployment subsystem. `WorkspaceDeploymentEnvironment` remains the target context
  used by that subsystem. A managed instance maps to one environment through an
  explicit link, initially one-to-one.
- `WorkspaceDesiredStateRevision` remains the immutable source revision. Resolution
  produces an immutable `ResolvedElsaApplicationPlan`; provider deployment and
  runtime command records remain execution records below the instance boundary.
- Elsa version identity is data. `3.8`, `3.9`, `3.10`, `4.0`, `4.1`, `5.0`, and any
  later release lines are catalog rows, not enum members, switch branches, or schema
  columns. The instance pins a minor line while its current resolved plan records the
  exact patch/version and immutable component identities.
- Placement intent is expressed as region, isolation, capacity, network and target
  outcomes. Provider resource identifiers, credentials, infrastructure topology and
  deployment commands remain below the provider boundary.
- Desired lifecycle and observed lifecycle are separate. A successful API mutation
  records intent and queues reconciliation; it does not claim that a remote workload
  is synchronously ready.

## Boundaries and ownership

The customer hierarchy is:

```text
Organization (customer, billing and entitlement boundary)
└── Workspace (operational authorization and isolation boundary)
    └── ElsaInstance (one customer-visible managed environment)
        ├── desired-state revision -> resolved application plan
        ├── placement intent -> provider placement assignment
        ├── deployment attempts -> runs/commands
        └── runtime tenant reference -> Elsa application tenant
```

| Concept | Owner and responsibility | Not its responsibility |
|---|---|---|
| Organization | Customer tenant, memberships, subscription/entitlements and organization audit | Direct access to workspace resources without an explicit authorization rule |
| Workspace | Existing operational scope for applications, environments, engines, artifacts, revisions and deployment permissions | Replacing Organization or becoming a managed workload |
| Elsa Instance | Customer-visible desired managed state, lifecycle intent, release/topology/features, placement requirements, current safe status and references | Provider resources, raw secrets, workflow execution state, or billing plan names |
| Elsa Tenant | Elsa runtime application-level tenancy and runtime identity | Control-plane organization, workspace, subscription, or instance authorization |
| Workflow application | Existing grouping for related deployment environments | A managed-instance lifecycle aggregate |
| Deployment environment | Existing named deployment context and target for runs/revisions/engines | The authoritative customer-facing instance identity |
| Desired-state revision | Immutable structured customer intent and content hash | Mutable status or provider apply state |
| Resolved application plan | Exact release/topology/package/configuration outcomes and evidence | Provider resources, credentials, or direct execution |
| Placement assignment | Provider-side realization of a provider-neutral placement request | Customer release or application intent |
| Deployment/run/command | One validated attempt, remote command, lease and safe outcome | The desired state or long-lived instance identity |

An instance has both `OrganizationId` and `WorkspaceId` for indexed authorization
and integrity checks. The workspace must belong to that organization. Organization
membership is necessary but, consistent with the organization-tenancy contract, does
not by itself grant workspace or instance access.

## Aggregate shape

The following is a logical contract, not a C# declaration. Names are intentionally
stable and provider-neutral; storage may normalize fields or use safe versioned JSON
for extension fields.

```text
ElsaInstance
  Id: opaque stable identifier (Guid in the current control plane)
  OrganizationId: owning organization
  WorkspaceId: owning workspace
  Name: customer display name
  Slug: immutable/unique navigation key within the workspace

  ReleaseIntent
    DistributionId: catalog/distribution identity
    ReleaseLine: non-empty catalog value such as "3.8" or "4.1"
    RequestedVersion: optional exact catalog version for initial selection/approval
    PreviewManifestDigest: optional explicit consent bound to one immutable manifest
    Channel: catalog value such as "preview" or "stable"
    PatchUpdates: policy value, initially automatic only after rollout validation
    MinorUpdates: explicit approval policy
    MajorMigrations: explicit migration policy

  ApplicationIntent
    TopologyId: catalog value such as "combined" or "server-studio"
    FeaturePresetId: optional governed preset
    FeatureOverrides: safe, typed override metadata only
    PackagePolicy: governed package/extension policy reference
    ConfigurationShapeRevisionId: optional safe configuration-shape source

  PlacementIntent
    TargetMode: managed, customer-owned, or self-hosted
    RegionCode: product region value, for example "westeurope"
    IsolationProfile: catalog/profile value, for example "Dedicated"
    CapacityProfile: governed capacity value, for example "standard-small"
    NetworkOutcome: public, private, or provider-capability outcome
    DomainOutcome: managed or custom-domain intent; domain credentials stay external

  DesiredLifecycle: Running | Stopped | Deleting
  ObservedLifecycle: Pending | Provisioning | Ready | Updating | Degraded |
                    Stopping | Stopped | Deleting | Failed | Unknown | Deleted
  Health: Healthy | Degraded | Unreachable | Unknown

  DesiredStateRevisionId: immutable structured intent source
  ResolvedPlanReference: plan ID, schema version, content hash and API URI
  CurrentResolvedRelease: exact release/version/digest plus dereferenceable plan URI
  CurrentDeploymentReference: safe deployment/revision/endpoint references
  PlacementAssignmentReference: Control-owned opaque assignment ID, if assigned
  ElsaTenantReference: runtime tenant ID/audience reference, if assigned
  IdentityBinding
    Audience: immutable exact audience derived from Id
    CanonicalCallbackUri: persisted exact callback binding, if an endpoint exists
    BindingVersion: monotonic callback/domain binding version
  LastOperationId: latest lifecycle operation

  Version: optimistic-concurrency token
  CreatedAt, UpdatedAt, DeletedAt
```

### Invariants

1. `Id` never changes, is not reused, and is the subject of authorization checks;
   display name and slug are not identity. `Name` is a mutable display label and
   need not be unique. `Slug` is a normalized, immutable navigation key generated
   from (or explicitly supplied alongside) the name and is unique among non-deleted
   instances in a workspace. A create conflict therefore means a slug conflict,
   not a display-name conflict; callers may use the same display name when their
   slugs differ.
2. `OrganizationId` and `WorkspaceId` resolve to one active ownership path. A
   workspace cannot be reparented while it has active instances without an explicit
   migration flow.
3. `ReleaseLine`, `TopologyId`, `FeaturePresetId`, `IsolationProfile`,
   `CapacityProfile`, and policy values are catalog data. No C# enum or frontend
   branch may encode the known version set.
4. `RequestedVersion`, when present, must belong to the selected release line and
   pass catalog admission. The resolver may select a newer eligible patch in that
   same minor line only when the policy permits it. A minor-line or major change is a
   distinct, explicit operation.
5. An instance cannot point to a resolved plan whose source revision, release line,
   topology, package policy or placement requirements differ from the recorded
   intent. Resolution creates a new immutable plan instead of mutating history.
6. An instance has at most one active lifecycle operation and at most one active
   deployment run for its mapped environment. Concurrent requests converge on the
   same operation or receive a conflict. The reservation is atomic and durable: the
   operation/run insert and reservation occur in one database transaction, and a
   filtered unique index is the final race protection. `Queued`, `Running`, and
   `RecoveryRequired` deployment runs all hold the reservation; a recovery-required
   run is not replaced by a retry until provider state has been reconciled and the
   run is explicitly completed.
7. `DesiredLifecycle = Deleting` is a terminal customer intent. Once the provider
   confirms cleanup, `ObservedLifecycle = Deleted` is terminal and the row is retained
   as a tombstone for the configured retention period; provider resources and runtime
   traffic must be absent before final purge.
8. No raw secret, provider token, workflow definition, package payload, local path,
   or provider resource ID is stored in instance intent, customer API responses,
   command/history records, or instance audit events.
9. The instance's identity audience is
   `urn:elsa:instance:{lowercase-canonical(Id)}` and is persisted with a unique
   constraint. The audience does not come from a request, host header,
   runtime tenant, or provider resource. The canonical callback is an exact,
   normalized HTTPS URI built from the control-approved customer-visible endpoint
   origin and the fixed handoff path `/managed-elsa/handoff/callback`; it is persisted
   with a binding version. There are no wildcard callbacks. A missing endpoint means
   handoff is unavailable and fails closed.
10. A major migration never overwrites the only source reference before cutover.
    A durable migration record retains the source's exact release/deployment
    reference as read-only/stopped for 30 days after successful cutover (or until an
    explicitly authorized early release), so rollback and support evidence do not
    depend on provider discovery.

### Desired versus observed state

The aggregate deliberately separates the state a customer requested from facts
reported by a provider or runtime:

| Customer intent | Reconciliation fact |
|---|---|
| `DesiredLifecycle = Running` | `ObservedLifecycle = Provisioning`, `Ready`, `Degraded`, or `Failed` |
| release line and upgrade policy | exact version/digests in the resolved plan and current deployment |
| region/profile/capacity/network outcomes | assigned placement status and safe endpoint/health facts |
| requested features/packages/configuration shape | resolver findings and provider/runtime diagnostics |

An API may return both projections, but a `Ready` response is emitted only after the
provider reports the required health gate. A command enqueue or successful database
write is never presented as a healthy deployment.

## Version and application intent

The instance stores a selection policy; it does not duplicate the whole resolved
plan. The release catalog owns availability, lifecycle, compatibility, signed
manifest evidence and exact component identities. The instance references that
catalog data through values and the plan resolver:

```json
{
  "release": {
    "distributionId": "valence-runtime",
    "releaseLine": "3.8",
    "requestedVersion": "3.8.0-preview.5413",
    "channel": "preview",
    "patchUpdates": "automatic-within-minor",
    "minorUpdates": "explicit-approval",
    "majorMigrations": "explicit-migration"
  },
  "application": {
    "topologyId": "combined",
    "featurePresetId": "starter",
    "featureOverrides": [],
    "packagePolicy": "valence-approved"
  }
}
```

`requestedVersion` may be absent when the customer selects a release line and the
catalog chooses the eligible patch. The resolved plan always records the exact
version, release-manifest digest, component topology, package identities and
compatibility evidence. The schema remains unchanged when the catalog contains
3.9, 3.10, 4.0, 4.1, 5.0, or any number of subsequent release lines.

### Explicit Preview consent

Managed-instance selection is Supported-only unless the release intent includes
`previewManifestDigest`. A channel named `preview` or a preview version suffix is
not consent and does not determine the catalog lifecycle. The optional consent
must be a canonical lowercase `sha256:` digest with 64 hexadecimal digits and
requires an explicit `requestedVersion`.

Consent permits one unambiguous paid catalog admission in either Supported or
Preview lifecycle, with the exact selected distribution, release line, version,
channel, topology and manifest digest. It never permits Maintenance or EndOfSupport
entries. Ambiguity is rejected before digest matching; consent cannot select one
row from conflicting admissions. Promoting that same immutable admission to
Supported does not invalidate the intent. Changing its digest requires new consent.

The onboarding API keeps its existing `releases` list Supported-only and exposes
Preview choices separately in `previewReleases`, including their safe manifest
digest. The create console requires an initially unchecked acknowledgment that
Preview is for evaluation and carries no availability SLO. A change of workspace,
release selection or manifest digest clears the acknowledgment. Neither the
console selection nor catalog admission upgrades a Preview release to Supported.

Consent is persisted on both the current instance and its immutable intent
revision, contributes to the canonical intent hash, and is returned with revision
metadata. Null consent is omitted from canonical JSON so existing no-consent
revision bytes and hashes remain unchanged. API preflight rejects unavailable
selections; the durable worker revalidates catalog eligibility after reload and
remains the final authority before provider execution.

The current projection must make the exact release dereferenceable, not merely
repeat a display version. `ResolvedPlanReference` is:

```json
{
  "planId": "plan_01J...",
  "planUri": "/api/workspaces/{workspaceId}/instances/{instanceId}/resolved-plans/{planId}",
  "schemaVersion": 1,
  "contentHash": "sha256:..."
}
```

`CurrentResolvedRelease` repeats the safe, immutable identity needed in a list/detail
projection and is always backed by that URI:

```json
{
  "planId": "plan_01J...",
  "planUri": "/api/workspaces/{workspaceId}/instances/{instanceId}/resolved-plans/{planId}",
  "distributionId": "valence-runtime",
  "releaseLine": "3.8",
  "version": "3.8.0-preview.5413",
  "manifestDigest": "sha256:...",
  "componentDigests": [{ "componentId": "combined", "digest": "sha256:..." }]
}
```

The URI returns the immutable resolved plan and its evidence; it is not a provider
resource URL and contains no raw manifest payload, credentials or infrastructure
identifier. `CurrentResolvedRelease` changes only when a health-gated deployment
projects a different resolved plan. A deployment history record retains the previous
exact reference for audit and rollback.

`Elsa Tenant` is created or discovered by the runtime/provider adapter. Its stable
reference and audience may be associated with the instance for authorization and
handoff, but tenant users, workflow definitions and execution state remain in the
runtime data plane. This preserves the [managed handoff contract](../spikes/127-managed-elsa-identity-handoff.md).

### Canonical identity and callback binding

The managed handoff authorizer must obtain identity values from the persisted
instance, as required by [spike #127](../spikes/127-managed-elsa-identity-handoff.md#authorization-boundary):

```text
Audience            = "urn:elsa:instance:" + lowercase-canonical(instance.Id)
CallbackOrigin      = the currently verified customer-visible endpoint origin
CanonicalCallbackUri = normalize(CallbackOrigin + "/managed-elsa/handoff/callback")
```

For the current Guid identity representation, `lowercase-canonical` means the
lowercase invariant `D` format with hyphens and no braces. For example:

```text
Id       = 550E8400-E29B-41D4-A716-446655440000
Audience = urn:elsa:instance:550e8400-e29b-41d4-a716-446655440000
```

The domain test vector must assert that uppercase, braced and alternate Guid
representations produce this one audience and that a different instance ID never
does.

`Audience` is generated once from the immutable instance ID and persisted in the
identity-binding record. It is never accepted from a browser, host header, runtime
tenant or provider adapter. The canonical callback is persisted as an exact URI,
rather than reconstructed from an incoming redirect or an infrastructure URL. The
origin can be a product-managed endpoint or a verified customer domain represented
by the safe `DomainOutcome`; resource groups, subscriptions, workload names and
provider IDs are not part of this value. URI normalization rejects fragments,
userinfo, wildcard hosts and non-HTTPS schemes (except localhost HTTP in local
development), and a callback-domain collision is a `409` domain conflict (separate
from the workspace slug conflict).

For the initial managed Azure path, `CurrentDeploymentReference.EndpointUri` is the
canonical direct Container Apps managed-TLS **origin**, not a health URL or provider
resource locator. It therefore uses HTTPS, has a non-empty host, and has no path,
userinfo, query or fragment. The provider appends `/health` only while verifying the
runtime; that probe path is not persisted or exposed as the customer endpoint. Before
a candidate revision receives traffic, the provider probes the candidate's own
revision-scoped host (`{revision}.{environment domain}`, which the stable revision
cannot answer) and requires the exact plain-text `Healthy` report; a missing or foreign
host, a non-answering route, a `Degraded`/`Unhealthy` report or any other body fails
the promotion closed with the stable revision untouched
(`azure.promotion.candidate-endpoint-invalid`, `candidate-unready`, `candidate-not-ready`,
`candidate-readiness-invalid`). A
missing or malformed origin projects provider uncertainty and cannot create an
identity binding or an openable instance. Front Door, WAF, custom DNS, private
ingress and multi-region routing remain later endpoint realizations of the same
provider-neutral origin contract.

Customer-readable provider-operation status exposes only lifecycle status, health,
the canonical safe origin, stable diagnostic codes and timestamps. Provider
resource handles, workload keys, request hashes, worker/lease state and transition
messages remain internal. A legacy path-bearing endpoint is treated as unavailable
and cleared on the next unrelated instance write; it is never converted into a
trusted origin.

The identity binding contains `BindingVersion`, `Audience`, `CanonicalCallbackUri`,
the verified endpoint-origin reference and `ChangedAt`. Endpoint or custom-domain
rotation atomically increments `BindingVersion` and replaces the exact callback;
the old URI is no longer accepted. The audience remains stable because the instance
ID remains stable. If an audience rotation is ever required for incident response,
the old audience is accepted only during a bounded overlap no longer than the
handoff maximum token lifetime, then revoked; that exceptional rotation is audited.

At issue and redemption, the authorizer loads the organization, instance ID,
audience, callback URI and binding version from the same current record. The signed
one-time code includes the exact audience, `redirect_uri`, `instance_id`, `org_id`,
PKCE challenge and binding version. Redemption compares every value exactly, checks
current organization/workspace/instance permission, consumes `jti` atomically and
rejects a stale binding version. Consequently a code for one instance cannot be
redeemed at another instance or after an endpoint/domain rotation. No caller-supplied
callback is treated as an allowlist.

Handoff is unavailable until a verified endpoint origin is projected. The API may
return `identityBinding: null` with a safe `identity-unavailable` state; it never
guesses a callback or falls back to a shared audience.

### Major migration source retention

A major migration is a durable side-by-side transition, not an in-place version
assignment. Persist an `ElsaInstanceMigration` record (or equivalent durable child
aggregate) with:

```text
MigrationId, InstanceId, SourceReleaseReference, TargetReleaseReference
Phase: Planned | ProvisioningTarget | Validating | Cutover | RetainingSource |
       RolledBack | Released | Failed
SourceAccess: Running | ReadOnly | Stopped
CutoverAt, SourceRetainUntil, EarlyReleaseApprovedAt, ReleasedAt
```

`SourceReleaseReference` includes the exact source release line, version, manifest
digest and safe deployment reference. After a successful cutover, the source is
stopped/read-only and remains retained for 30 days (`SourceRetainUntil`) unless an
explicitly authorized early release is recorded. The target becomes current only
after its health gate succeeds. Workflow execution state is excluded by default as
specified by the product decisions; a migration adapter must explicitly certify any
state transition. Source release and retention events are audited, and an early
release is blocked while rollback/support evidence requires the source.

## Placement boundary

`PlacementIntent` says what the customer is entitled to request and what the
provider must realize. It does not contain infrastructure vocabulary. For example,
`regionCode = westeurope`, `isolationProfile = Dedicated`, and
`capacityProfile = standard-small` are valid intent values; resource-group names,
subscription IDs, database server names, workload app names, identity IDs and
secret-store resource IDs are not.

The provider adapter owns a separate placement assignment containing its own
resource graph and provider identifiers. The instance stores only a Control-owned
opaque `PlacementAssignmentReference` and safe outcome facts needed by the customer
or operator. This keeps the customer contract portable to a local provider, a
customer-owned target, or a future provider without making the instance schema
provider-shaped.

## Lifecycle and operations

### State machine

`DesiredLifecycle` is a small, customer-controlled intent state. `ObservedLifecycle`
is a provider/reconciler projection and may lag or fail:

```text
create -> Pending -> Provisioning -> Ready
                              ├──> Degraded
                              └──> Failed

Ready/Degraded --desired update or approved upgrade--> Updating -> Ready/Degraded/Failed
Ready/Degraded --stop--> Stopping -> Stopped
Stopped --start--> Provisioning -> Ready/Degraded/Failed
Ready/Degraded/Stopped/Failed --delete--> Deleting -> Deleted

Any provider report that cannot be correlated to a known run may project
`ObservedLifecycle = Unknown`; it never projects `Ready` and does not release the
deployment reservation. A run whose submission or outcome is uncertain is marked
`RecoveryRequired` and remains the single reserved run until reconciliation reaches
a terminal result.
```

Allowed API operations and semantics:

| Operation | Preconditions | State change and rule |
|---|---|---|
| Create | Managed-hosting entitlement, valid catalog selection and workspace create permission | The request may select `DesiredLifecycle` and defaults to `Running`; atomically creates instance intent plus an operation; observed state starts `Pending`; returns `202` operation status |
| Reconcile/provision | Instance is not `Deleted`; no conflicting active operation | Resolves the plan, creates provider work and eventually projects `Ready`, `Degraded`, or `Failed` |
| Update intent | Active instance, `If-Match` and desired-state permission | Creates a new immutable revision/plan path; observed state becomes `Updating` only when reconciliation begins |
| Start | `DesiredLifecycle = Stopped` or a failed stopped request | Sets desired state to `Running` and queues reconciliation; repeated start is an idempotent read of the existing result |
| Stop | Not `Deleted`; policy allows quiescence | Sets desired state to `Stopped`, waits for provider/runtime acknowledgement, then reports `Stopped` |
| Restart | Not `Deleted` | A named operation that preserves desired `Running`, performs a safe provider/runtime restart and requires a health gate before `Ready` |
| Approve minor upgrade | Current release line has an eligible target and compatibility evidence | Records explicit approval and starts a staged operation; it cannot silently change a minor line |
| Major migration | Explicit migration request and governed transition adapter | Creates a side-by-side migration operation; persisted/running workflow state is excluded unless the catalog certifies its transition |
| Delete | Explicit confirmation and delete permission; instance is not already `Deleted` | Sets desired state `Deleting`, revokes new operations, deprovisions through the provider, then retains a `Deleted` tombstone. Delete is idempotent while already pending. If another operation owns the active slot, the delete request is recorded as `WaitingForPriorOperation` and activated only after that operation/run is terminal or recovered; it never bypasses an uncertain run. |
| Retry/recover | `Failed`, `Degraded`, or `RecoveryRequired` operation | Reuses the same desired intent with a new operation attempt; never blindly duplicates an uncertain provider apply |

The instance service owns validation and transition authorization. The provider
consumer owns remote execution and safe reports. Existing remote command semantics
remain authoritative for command leases, duplicate completion and final-state
handling.

### Delete preconditions and state-specific behavior

Deletion is a durable intent and cleanup workflow. It is accepted from every
non-deleted observed state, but `Deleted` is not projected until the mapped
environment has no active/uncertain run and the provider/runtime reports cleanup.
The following rules make cancellation and uncertainty explicit:

| Observed state | Delete behavior | Completion precondition |
|---|---|---|
| `Pending` | Record `Deleting`; cancel an accepted/queued operation before any provider submission. No provider delete run is needed if no placement was assigned. | No queued operation/run and no placement/resource outcome remains to clean. |
| `Provisioning` | Record `Deleting`; request cancellation when the current operation is cancellable. If work may have reached the provider, retain the same run and reconcile it before cleanup; never start a second run. | Provider confirms absence (or a safe compensating delete) and runtime traffic is stopped. |
| `Updating` | Record `Deleting` as the terminal desired intent. Cancel before submission where possible; otherwise let the current run reach a terminal/reconciled result, then enqueue cleanup behind the existing reservation. | Update run is terminal and cleanup has a provider/runtime absence report. |
| `Stopping` | Let the stop/reconciliation finish under the existing run reservation, then perform cleanup. If stopping becomes uncertain, transition the run to `RecoveryRequired`. | Stop and delete cleanup are both acknowledged; no active/uncertain run remains. |
| `Unknown` | Accept and persist deletion intent, but fail closed for remote deletion until a provider-state read establishes the target. Keep `Unknown` (and the run `RecoveryRequired` where applicable) while awaiting reconciliation or operator recovery. | Positive provider absence/cleanup evidence; a timeout alone is not deletion. |
| `Failed` / `Degraded` / `Stopped` / `Ready` | Queue the normal cleanup operation, using the existing run reservation and provider capability. | Provider/runtime cleanup and absence of active traffic. |
| `Deleting` | Return the existing delete operation for the same idempotency key; a different request conflicts while cleanup is active. If cleanup is waiting on a prior operation, return that durable successor. | Same as the original delete workflow. |
| `Deleted` | Do not recreate or issue remote work. Return the retained tombstone for authorized reads and an idempotent terminal result for a repeated delete. | Purge is a separate retention-controlled operation. |

If cancellation is unsupported, the service does not pretend it succeeded: it
records a safe `deletion-pending`/`recovery-required` outcome and waits for the
provider report. An operator may unblock or authorize an early source release only
through an audited recovery action. This preserves the one-run reservation and
prevents a delete/retry race from causing duplicate remote applies.

### Concurrency and idempotency

- Every mutable instance response carries an opaque ETag derived from `Version`.
  Mutations require `If-Match`. A stale precondition returns `412 Precondition
  Failed`; a valid request that conflicts with an active operation returns `409
  Conflict` with the current operation reference and no private data about another
  instance.
- Every mutating endpoint requires `Idempotency-Key`. The key is scoped to the
  authenticated workspace and operation route. The operation row stores the
  canonical request hash and a safe response reference. A retry with the same key
  and hash returns the original operation/result. Reuse with a different hash
  returns `409`.
- Create idempotency is scoped to workspace plus route because no instance ID exists
  yet. The server always allocates the instance ID; the public create contract does
  not accept caller-selected identity. Once the instance exists, operations are
  scoped to instance plus route.
- An accepted intent mutation increments `Version` exactly once. Retries that find
  the same idempotency record do not increment it or append duplicate audit events.
- The instance operation has a durable state (`Accepted`, `WaitingForPriorOperation`,
  `Queued`, `Running`, `Succeeded`, `Failed`, `RecoveryRequired`, `Cancelled`) and
  attempt number. `WaitingForPriorOperation` is not active and is used only for a
  delete intent that must follow an existing operation. A
  worker lease may expire, but expiration does not authorize a second concurrent
  apply; recovery must first reconcile provider state or explicitly mark the
  operation recoverable. A provider submission/outcome that cannot be proven is
  represented by `RecoveryRequired` (and may project observed `Unknown`); neither
  status is silently collapsed into `Failed` or cleared by a retry.
- The current environment rule is enforced by an atomic reservation. The API
  transaction first commits instance intent, its immutable source revision, an
  `Accepted` operation and an outbox record; asynchronous resolution is not allowed
  to keep that request transaction open. After the worker claims the operation, it
  resolves the plan. In one subsequent database transaction, it rechecks the
  operation claim and ownership, persists the immutable resolved plan, transitions
  the operation to `Queued`, inserts the deployment run and reserves the target
  `(WorkspaceId, EnvironmentId)`. A filtered unique index on that pair for `Queued`,
  `Running`, and `RecoveryRequired` is mandatory (not an application-only `Any`
  check). A concurrent insert loses the uniqueness race and returns the existing
  operation/run as `409` or an idempotent replay. The provider is called only after
  that reservation transaction commits.
  The reservation is released only when the run reaches `Succeeded`, `Failed`,
  `Blocked`, `Cancelled`, or `RolledBack` after its provider outcome is known;
  `RecoveryRequired` continues to block a new run until explicit recovery.
- The instance operation's active-operation reservation uses the same conditional
  insert/unique-index pattern. The database is the final authority under concurrent
  workers; read-then-insert checks alone are insufficient.
- Provider command idempotency remains derived from the immutable operation/revision
  and target identity. The instance operation key is not substituted for the
  existing command lease/idempotency contract.

### Reconciliation sequence

```text
authorized API mutation
  -> synchronous preflight: ownership, entitlement, shape and catalog selection
  -> transaction: instance intent + revision link + operation + audit/outbox record
  -> resolver: catalog admission and ResolvedElsaApplicationPlan
  -> provider capability validation and explainable provider plan
  -> durable DeploymentRun / remote command
  -> provider/runtime lease, progress and safe final report
  -> projection: observed lifecycle, endpoint, health and deployment reference
  -> append outcome audit event; complete operation
```

The ownership, entitlement, shape and basic catalog-selection checks are synchronous
and can return `422` before the transaction. Resolution of compatibility evidence,
plan construction and provider capability is asynchronous; those failures complete
the operation as `Failed` with a safe diagnostic and do not produce a `Ready` state.
If the control process stops after committing the operation, a worker can resume it.
If it stops after remote submission, the provider adapter must read/reconcile its own
safe state before retrying. No step stores a raw workflow payload or secret in an
instance operation.

## Mapping to existing Elsa Control models

| Existing model/table | Mapping to the instance boundary | Migration rule |
|---|---|---|
| `Organization` / `OrganizationMembership` | Direct customer owner and authorization ancestor | Resolve organization from the server-side workspace; never trust caller-supplied ownership IDs |
| `Workspace` / `WorkspaceMembership` | Direct operational owner and permission scope | Preserve existing workspace IDs and membership behavior |
| `WorkspaceDeploymentApplication` / `DeploymentApplicationEntity` | Logical grouping (`ApplicationId`) | Do not rename or reinterpret; an instance maps to an existing or newly created grouping through an explicit binding |
| `WorkspaceDeploymentEnvironment` / `DeploymentEnvironmentEntity` | One managed instance's operational target (`EnvironmentId`) | Add a nullable managed-instance link and enforce a filtered unique one-to-one index on `ElsaInstanceId` where the link is not null; retain workspace/org integrity checks and leave unbound customer targets valid |
| `WorkspaceDesiredStateRevision` / `DesiredStateRevisionEntity` | Immutable source desired state (`DesiredStateRevisionId`) | Reuse revision content/hash and add managed-instance association through its mapped environment or nullable link; never mutate an existing revision |
| `StructuredDesiredStateRecordEntity` | Versioned workflow/feature/configuration/secret-reference intent inputs | Keep structured safe records; secret values and provider tokens remain excluded |
| `ResolvedElsaApplicationPlan` | Exact resolved realization input (`ResolvedPlanReference`) | Persist/reference immutable plan identity, API URI, exact release line/version and manifest/component digests; do not store provider resource IDs in the plan |
| `WorkspaceDeploymentEnvironment.DesiredRevisionId` | Compatibility projection of instance desired revision | During migration, instance link is canonical and the environment pointer is updated transactionally |
| `WorkspaceDeploymentEnvironment.DeployedRevisionId`, status and drift | Compatibility projection of observed deployment facts | Provider/runtime reports update the projection; it is not customer intent |
| `WorkspaceWorkflowEngine` / `WorkflowEngineEntity` | Runtime endpoint, capabilities, credential-reference metadata and health | Provider/agent may create or update the observed registration; instance intent never embeds engine URL or credential |
| `DeploymentTierDefinition` | Workspace-defined tier/capability policy input | Use only as policy/admission input; do not make a tier row the instance identity |
| `DeploymentRun` / `DeploymentRunEntity` | Auditable attempt for a revision and target | Link operation and instance through the target environment; preserve append-only history and one-active-run rule |
| `DeploymentCommand` / command leases | Remote execution transport | Link to operation/run; preserve existing safe metadata, lease and deterministic final-state behavior |
| `WorkspacePermissionGrant` | Action authorization (`read`, `manage desired state`, `execute deployment`, etc.) | Add instance action permissions only if a narrower grant is required; workspace remains the default boundary |
| `WorkspaceDeploymentSecretStore` / credential references | External secret locators | Instance may reference a safe credential-reference ID; never copy protected values or provider tokens into instance JSON |

The managed environment binding carries both `WorkspaceId` and `ElsaInstanceId` and
is validated against the instance's `OrganizationId`. The binding, instance,
environment, application and workspace rows must resolve to the same workspace and
organization in the create/attach transaction. Use composite foreign keys or a
denormalized ownership key where the existing schema permits it; where the legacy
tables cannot express a cross-table check constraint, the store transaction must
lock and verify the ownership path and reject any mismatch. Never rely on a caller's
organization ID or on a name match. A foreign key to an instance alone is not enough
because it can still permit a cross-workspace binding.

The mapping intentionally allows legacy deployment applications and environments to
continue serving artifact delivery and customer-owned targets. Managed-instance
creation must not infer an instance from `HostingProvider`, a base URL, a tier name,
or an image tag.

## Persistence migration path

The first implementation should use additive schema changes in the existing catalog
EF Core store and produce both SQLite and SQL Server migrations.

### Proposed records

`ElsaInstance` (normalized intent and projections):

- identity/ownership: `Id`, `OrganizationId`, `WorkspaceId`, `Name`, `Slug`;
- intent: release line/version/channel/policies, topology, feature/package policy,
  region, isolation, capacity, network and domain outcomes;
- state: desired/observed lifecycle, health, desired revision, resolved-plan URI and
  hash, exact current release/version/manifest/component digests, current deployment
  reference, placement-assignment reference and tenant reference;
- concurrency/audit: `Version`, timestamps, delete/tombstone timestamp and last
  operation ID.

`ElsaInstanceIdentityBinding` (one current binding per instance):

- `InstanceId`, immutable `Audience`, normalized exact `CanonicalCallbackUri`,
  verified endpoint-origin reference, `BindingVersion`, `ChangedAt` and safe audit
  metadata;
- unique `Audience` and `CanonicalCallbackUri` values so two instances cannot claim
  the same custom domain/callback;
- audience is persisted even though it is deterministically derived from the
  immutable instance ID; persisting it makes corruption/rotation detectable and
  gives the handoff authorizer one authoritative row. Callback rotation updates the
  URI and increments the version atomically.

`ElsaInstanceMigration` (durable side-by-side major-migration child aggregate):

- `MigrationId`, `InstanceId`, source/target immutable plan/deployment references,
  phase, source access mode, cutover timestamp, `SourceRetainUntil` (30 days after
  successful cutover), early-release approval and release timestamp;
- source references contain safe exact release/version/digest values only. The row
  remains while the source is retained and is never reconstructed from provider
  discovery.

`ElsaInstanceOperation` (durable command envelope):

- `Id`, workspace/instance reference (nullable only for create before assignment),
  action, canonical idempotency scope, request hash, idempotency key, expected
  aggregate version, operation state, attempt/lease metadata, linked revision/plan/run
  and safe failure summary;
- unique `(WorkspaceId, IdempotencyScope, IdempotencyKey)` and a filtered
  active-operation index per instance. Active includes `Accepted`, `Queued`, `Running`
  and `RecoveryRequired`; the unique index is the final protection against two
  workers accepting the same instance operation. Operation rows are durable
  idempotency/audit records and cannot be deleted; the EF save guard and both
  provider migrations enforce retention at the database boundary.

`ElsaInstanceRecoveryRequest` (append-only recovery request envelope):

- Each accepted recovery request keeps its own immutable operation ID, attempt,
  route scope, idempotency key, request hash and acceptance timestamp. The
  envelope is authoritative when an older recovery key is replayed, while the
  operation projection reports the current state and attempt.
- Recovery envelopes share the `(WorkspaceId, IdempotencyScope, IdempotencyKey)`
  namespace with normal operation requests. A key may therefore be reused only
  across distinct route scopes (for example `instances`,
  `instance/{id}/operations`, and `instance/{id}/UpdateIntent`); within one scope
  it is a conflict in either direction. Envelopes are unique by route scope/key
  and by `(OperationId, AttemptNumber)` and are never updated or deleted.

`DeploymentEnvironment` managed binding and `DeploymentRun` reservation constraints:

- `DeploymentEnvironment.ElsaInstanceId` is nullable for legacy/customer-owned
  environments. Add a filtered unique index on `ElsaInstanceId` alone with predicate
  `ElsaInstanceId IS NOT NULL`; the instance ID is globally opaque and this makes
  the one-to-one managed binding instance-global rather than accidentally unique only
  within a workspace. Retain `WorkspaceId` on the binding for query authorization.
- Add a filtered unique index on `(WorkspaceId, EnvironmentId)` for deployment runs
  whose persisted status is `Queued`, `Running`, or `RecoveryRequired`. The status
  predicate must match the provider's stored string conversion in both SQLite and
  SQL Server migrations (partial/filtered index syntax per provider). `RecoveryRequired`
  intentionally keeps the slot reserved until reconciliation closes it.
- The operation/run reservation, resolved-plan link and linked
  instance/environment projection are committed in the worker transaction described
  above. A uniqueness violation is translated to an active operation/run conflict
  and never retried as a second provider apply. Application preflight is useful for
  a friendly response but cannot replace the database constraint. If resolution
  fails, the worker completes the operation as `Failed` without inserting a run; if
  the worker or provider submission becomes uncertain, it keeps the claimed
  operation and/or run in `RecoveryRequired` and does not create a replacement
  reservation.

`ElsaInstanceAuditEvent` (append-only safe history):

- `Id`, organization/workspace/instance IDs, sequence, event type, actor account or
  operator subject, operation/run IDs, prior/new state, revision/plan reference,
  safe diagnostic code/summary, request-key hash and timestamp;
- unique `(InstanceId, Sequence)` and indexed `(WorkspaceId, OccurredAt)`;
- no raw payload, token, secret, local path or provider resource identifier.

Provider placement assignments should remain in the provider adapter's persistence
boundary. If the provider needs a relational reference, store only the instance's
opaque assignment ID and safe outcome facts in the control-plane projection.

### Ordered migration

1. Add domain contracts, value validation and state-transition tests with no schema
   change. Define the canonical serializer/hash behavior for instance intent.
2. Add `ElsaInstance`, `ElsaInstanceOperation`, `ElsaInstanceAuditEvent`,
   `ElsaInstanceIdentityBinding`, and `ElsaInstanceMigration` tables, concurrency
   indexes and SQLite/SQL Server migrations. Add a nullable, indexed
   `ElsaInstanceId` binding to deployment environments; existing rows remain unbound.
   Include the filtered one-to-one environment index and filtered active/uncertain
   run reservation index in both providers.
3. Add a store adapter that creates a managed instance and its application/environment
   binding in one transaction. Existing applications/environments are not renamed;
   attachment of an existing environment requires an explicit ownership check and
   leaves customer-owned registrations untouched.
4. Add read projections and authorization. The managed-instance API becomes the
   canonical customer surface; legacy deployment APIs continue to serve unbound
   application/environment targets and compatible reads during migration.
5. Link immutable desired-state revisions and resolved plans. Backfill only when an
   explicit managed binding exists; never guess from names, hosting-provider strings,
   image tags, or endpoint URLs. Project the exact current plan URI/version/digest
   and create the identity binding only from a verified endpoint origin.
6. Add operation/outbox/worker orchestration and project provider/runtime reports into
   observed instance state. Reuse `DeploymentRun`/command leases rather than making a
   second apply engine.
7. Migrate the console and identity handoff to instance routes. Keep the existing
   engine/tenant identity distinct, resolve exact audience/callback/version from the
   binding row, and preserve all supported release lines/topologies.
8. After managed clients have moved, deprecate only the managed aliases of old
   application/environment routes. Keep the generic deployment subsystem for
   customer-owned and self-hosted targets.

## API contract (planned)

All routes are workspace-authorized and derive organization/entitlements from the
server-side workspace context. Responses use safe summaries and include `ETag`,
`Version`, `DesiredLifecycle`, `ObservedLifecycle`, `Health`, current plan/deployment
references, the exact `CurrentResolvedRelease` and `IdentityBinding` (or an explicit
unavailable state), and a `self` link. Provider resource identifiers and secret
values never appear in customer DTOs. Display `Name` is not a uniqueness key;
`Slug` is the navigation key and is the value used for conflict detection.

| Method and route | Purpose | Response |
|---|---|---|
| `POST /api/workspaces/{workspaceId}/instances` | Create instance intent and initial operation | `202 Accepted` with instance summary and operation link; `409` for idempotency/slug/domain conflict; `422` for synchronous shape/catalog-selection/entitlement preflight (later resolver failure is an operation result) |
| `GET /api/workspaces/{workspaceId}/instances` | List visible instances | `200` safe summaries, paginated |
| `GET /api/workspaces/{workspaceId}/instances/{instanceId}` | Read one instance | `200`; indistinguishable `404` for an inaccessible instance |
| `PATCH /api/workspaces/{workspaceId}/instances/{instanceId}` | Change name, release/topology/features/placement intent or desired lifecycle policy | Requires `If-Match` and `Idempotency-Key`; `202` when reconciliation is needed |
| `POST /api/workspaces/{workspaceId}/instances/{instanceId}/operations` | Start, stop, restart, reconcile, approve upgrade, recover, or delete | `202` operation resource; `409` active-operation/precondition conflict |
| `GET /api/workspaces/{workspaceId}/instances/{instanceId}/operations/{operationId}` | Poll durable lifecycle operation | `200` status, safe progress and linked deployment/run |
| `GET /api/workspaces/{workspaceId}/instances/{instanceId}/revisions` | List immutable intent revisions | `200` revision summaries; no raw secret values |
| `GET /api/workspaces/{workspaceId}/instances/{instanceId}/resolved-plans/{planId}` | Dereference one immutable resolved application plan | `200` exact release line/version/manifest/component digests, schema/content hash and safe evidence; no provider IDs or secrets |
| `GET /api/workspaces/{workspaceId}/instances/{instanceId}/deployments` | List provider/runtime deployment attempts | `200` safe run/history projections |
| `GET /api/workspaces/{workspaceId}/instances/{instanceId}/audit` | Read append-only instance events | `200` for permitted users; support elevation separately audited |

`DELETE` is intentionally represented as an explicit operation so confirmation,
retention and asynchronous provider cleanup cannot be mistaken for an immediate row
delete. A delete body carries `deleteConfirmationId`, created for the
`DeleteManagedInstance` confirmation action and canonical instance-ID target. The
caller must also hold `instances.delete`; exact replays bind the same confirmation
ID into the idempotency fingerprint without consuming it twice. First-use
confirmation consumption and durable delete acceptance commit in the same database
transaction, so a concurrency or persistence failure leaves the confirmation
unused. Fields that do not apply to the selected action are rejected rather than
ignored. `POST /operations`
should accept an action-specific body, for example:

```json
{
  "action": "reconcile",
  "expectedVersion": 7,
  "reason": "apply approved release patch"
}
```

The API returns stable problem details with a safe code (`instance.version-conflict`,
`instance.operation-active`, `instance.release-line-unsupported`,
`instance.plan-not-resolved`, or `instance.deletion-pending`). It does not disclose
whether a caller can access another organization's instance.

## Audit and authorization

Every accepted mutation appends exactly one intent event and every operation outcome
appends a separate lifecycle event. The minimum event set is:

- `InstanceCreated`, `IntentChanged`, `DesiredRevisionLinked`, `PlanResolved`;
- `OperationAccepted`, `OperationStarted`, `OperationRecovered`, `OperationSucceeded`,
  `OperationFailed`, `OperationCancelled`;
- `PlacementAssigned`, `EndpointChanged`, `IdentityBindingChanged`, `HealthChanged`,
  `DeploymentProjected`;
- `StartRequested`, `StopRequested`, `RestartRequested`, `UpgradeApproved`,
  `MajorMigrationStarted`, `MigrationCutover`, `MigrationSourceRetained`,
  `MigrationSourceReleased`, `Deleted`.

Events contain actor type/account or operator subject, organization/workspace/instance
IDs, operation/run/revision/plan references, prior/new safe state, diagnostic code,
a one-way SHA-256 reason fingerprint and timestamp. Reasons are bounded safe summaries
at the API boundary but are not retained as raw customer text. Events do not contain the request body, authorization token,
secret value, workflow definition, package payload, provider command body, or provider
resource ID.

Authorization is evaluated in this order:

```text
authenticated Control identity
  -> resolve workspace and owning organization
  -> active organization membership
  -> active workspace membership/permission grant
  -> instance belongs to that workspace
  -> entitlement/capability check for the requested action
  -> explicit confirmation for destructive/risky actions
```

The identity handoff uses the instance ID and canonical audience resolved from this
aggregate. It must check organization/workspace/instance authorization again at
redemption, as specified by the handoff spike. It must not turn an Elsa runtime tenant
into a second Control identity system.

## Examples and validation matrix

The same aggregate shape supports these launch and future cases:

| Scenario | Release intent | Topology | Placement intent | Expected rule |
|---|---|---|---|---|
| Dev preview | release line `3.8`, preview channel, automatic eligible patch | `combined` | `westeurope`, `Dedicated`, `standard-small`, public outcome | Explicit preview opt-in; reaches `Ready` only after health |
| Test candidate | release line `3.8`, requested exact candidate or eligible patch | `server-studio` | `westeurope`, `Dedicated`, `standard-small`, public outcome | Exact plan is immutable; a changed candidate creates a new revision |
| Production | supported catalog release line, patch-only automatic policy | `combined` or `server-studio` | `westeurope`, `Dedicated`, entitled capacity outcome | Minor changes require approval; upgrades use backup/health/cutover policy |
| Future Elsa 4/5 | line `4.0`, `4.1`, `5.0`, or later catalog row | any catalog topology | any entitled provider-neutral target | No schema or frontend branch changes; admission depends on release evidence |
| Customer-owned target | any admitted line/topology | any admitted topology | target mode `customer-owned`, customer region/profile outcomes | Provider/agent IDs stay below the boundary; outbound trust follows ADR-0011 |

Validation must cover:

1. Two organizations with same instance name cannot cross-read or cross-mutate.
2. Two workspaces in one organization remain isolated even for organization members
   without workspace access.
3. A `3.8` instance can receive an eligible patch without changing its release line;
   selecting `3.9` requires explicit approval and a new plan/revision.
4. Catalog rows for `3.8`, `3.9`, `3.10`, `4.0`, `4.1`, and `5.0` resolve through
   the same contract; adding a row requires no code or migration.
5. Topology can change from one Combined component to a Server plus Studio
   composition when compatibility evidence permits it; no one-container invariant
   is introduced.
6. Duplicate create/update/start/stop/delete requests return one operation and one
   state transition; mismatched idempotency payloads conflict.
7. Stale `If-Match` requests cannot overwrite a newer intent; the filtered unique
   operation and environment-run reservations prevent two active/uncertain records
   even under concurrent transactions.
8. Interrupted reconciliation resumes or enters `RecoveryRequired` without a blind
   duplicate apply; a failed health gate never projects `Ready`; all state-specific
   delete behavior waits for positive cleanup evidence.
9. A handoff token contains the exact persisted audience, callback and binding
   version; a rotated domain rejects the old callback and cross-instance redemption
   fails closed.
10. The current projection contains a dereferenceable plan URI and exact
    release/version/manifest digest, and a major migration retains its source for
    30 days or until audited early release.
11. Existing unbound application/environment/engine deployments continue to function
   with their current IDs and authorization.
12. API, operation, command, history, audit and telemetry outputs contain no raw
    secret, token, workflow payload, local path or provider resource identifier.

## PR-sized implementation tasks

These tasks are the intended native sub-issue sequence under #114. Each is one
coherent PR or independently reviewable evidence slice; the parent architecture
document remains the source of the boundary decisions.

| Order | Task | Depends on | Definition of done |
|---:|---|---|---|
| 1 | [#154](https://github.com/valence-works/elsa-control/issues/154) — domain contract and instance state machine | #114 accepted | Domain/value contracts, cardinality-neutral release validation, transition matrix, canonical intent hash, stable identity audience/callback value contract, and unit tests; no persistence/API/provider SDK |
| 2 | [#156](https://github.com/valence-works/elsa-control/issues/156) — catalog/plan adapter for instance intent | 1, #105, #106 | Resolve release/topology/features into an immutable `ResolvedElsaApplicationPlan`; reject unsupported lifecycle/compatibility and prove 3.8 plus synthetic later release lines; expose exact version/digest evidence for the plan URI |
| 3 | [#155](https://github.com/valence-works/elsa-control/issues/155) — EF persistence and legacy environment binding | 1 | Add instance/operation/audit/identity-binding/migration tables, SQLite and SQL Server migrations, nullable environment binding, filtered unique managed-environment and active/uncertain-run indexes, ownership integrity, optimistic concurrency and migration tests |
| 4 | [#157](https://github.com/valence-works/elsa-control/issues/157) — instance store/service and idempotent operations | 1, 3, #107 | Implement authorized create/update/start/stop/restart/delete/reconcile/recover operation records, `If-Match`, idempotency replay/conflict, atomic reservation and one-active-operation/run behavior; cover state-specific deletion and 30-day source retention |
| 5 | [#153](https://github.com/valence-works/elsa-control/issues/153) — API contracts and authorization/audit projection | 4, #127 | Expose list/detail/mutation/operation/revision/resolved-plan/deployment/audit routes, safe DTOs/problem details, workspace/organization authorization, ETags, exact identity binding and release/version/digest projections, and audit assertions |
| 6 | [#125](https://github.com/valence-works/elsa-control/issues/125) — provider/reconciliation adapter and observed-state projection | 2, 4 | Consume the provider-neutral plan, link runs/commands, recover interrupted operations, project safe endpoint/health/deployment facts, and prove no provider details enter intent/history |
| 7 | [#143](https://github.com/valence-works/elsa-control/issues/143) / [#144](https://github.com/valence-works/elsa-control/issues/144) — console and managed handoff integration | 5, 6 | Instance lifecycle UX, progress/retry/error states, exact instance audience/callback lookup and browser-to-runtime proof across supported catalog data |
| 8 | [#129](https://github.com/valence-works/elsa-control/issues/129), [#131](https://github.com/valence-works/elsa-control/issues/131), [#132](https://github.com/valence-works/elsa-control/issues/132) — recovery, upgrade and deletion acceptance | 6 | Backup/restore-to-new, staged upgrade/rollback, retention/tombstone, isolation and placement evidence; update launch runbooks and support controls |

Tasks 1–7 are closed and the Azure provider/Milestone B proof is accepted. #144, #185
and [elsa-production-image#35](https://github.com/valence-works/elsa-production-image/issues/35)
completed the real Azure browser proof and capability-based multi-release/topology
integration; #249 added workflow creation, publication and execution evidence and
closed the walking skeleton. #129 consumes the stable revision/deployment/instance
ownership mapping and has accepted executable restore-to-new evidence. It remains a
recovery-contract proof, not a claim that production backup operations are shipped.

## Non-goals and deferred choices

- No persistence, API, provider SDK, console or runtime hook implementation belongs
  in this architecture task.
- No renaming or wholesale replacement of the current deployment application,
  environment, engine, revision, run, command, organization or workspace models.
- No workflow execution/queue/bookmark/tenant data reconciliation in Elsa Control.
- No provider-specific infrastructure IDs, raw credentials, or secret resolution in
  customer intent.
- No fixed major-version enum, finite release list, or separate Elsa 3/Elsa 4 schema.
- No assertion that Shared, Data-isolated, Private, Sovereign, multi-region, custom
  domain, HA, backup/restore, or customer-agent guarantees are launch-complete; each
  remains governed by its own evidence and follow-up work.
