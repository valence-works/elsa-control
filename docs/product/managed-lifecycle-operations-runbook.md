# Managed Lifecycle Operations Runbook

**Scope:** managed Elsa instance health, lifecycle work, reconciliation, and
operator response. This is the operational baseline for [#222](https://github.com/valence-works/elsa-control/issues/222).

**Important:** the controlled fixtures and tests behind this runbook are not
production availability evidence. They do not establish a 99.9% SLO, an alert
delivery guarantee, or a provider-specific production support commitment.

## Operator contract

Start with the workspace-scoped, read-only health projection:

```text
GET /api/workspaces/{workspaceId}/instances/{instanceId}/health
```

Use an authenticated session with access to the exact workspace. The response
contains the evaluated status and stable diagnostic code, UTC evaluation and
reconciliation timestamps, safe operation/run state and attempt metadata, and
safe alert records (`code`, `severity`, and an opaque deduplication identity).
It does not perform a lifecycle mutation. The operation and audit projections
are available at:

```text
GET /api/workspaces/{workspaceId}/instances/{instanceId}/operations/{operationId}
GET /api/workspaces/{workspaceId}/instances/{instanceId}/audit
```

Lifecycle mutations use the existing state machine through:

```text
POST /api/workspaces/{workspaceId}/instances/{instanceId}/operations
```

They require the current strong `If-Match` version and a fresh
`Idempotency-Key`. A health result is an observation, not permission to create
a second operation while an existing operation or deployment run is still
blocking.

## Status and stable codes

The evaluator gives durable `RecoveryRequired` and explicit operation/run
failure precedence over provider inference. A healthy result is emitted only
when desired lifecycle is `Running`, observed lifecycle is `Ready`, health is
`Healthy`, and no active work remains.

### Result diagnostic codes

| Code | Meaning and operator interpretation |
|---|---|
| `managed.lifecycle.healthy` | Running/Ready/Healthy with no active work. |
| `managed.lifecycle.degraded` | Known but not healthy; inspect endpoint and active work projections. |
| `managed.lifecycle.failed` | Provider projection is failed or unreachable. |
| `managed.lifecycle.unknown` | No trustworthy runtime health is available (including a deleted/tombstoned projection). |
| `managed.lifecycle.provider-unknown` | The provider observation is unknown or ambiguous. |
| `managed.lifecycle.stale` | An active operation or run exceeded its configured deadline. |
| `managed.lifecycle.reconciliation-stale` | The unknown/ambiguous reconciliation timestamp exceeded its deadline. |
| `managed.lifecycle.recovery-required` | Durable recovery is required; this is the single recovery gate. |
| `managed.lifecycle.operation-failed` | Fallback code when an operation failed without a more specific safe code. |
| `managed.lifecycle.run-failed` | Fallback code when a deployment run failed without a more specific safe code. |
| `managed.lifecycle.work-active` | Work is active but not yet stale or failed; allow the existing reservation to finish. |

An operation, run, or provider may supply a more specific safe diagnostic code
(for example, `provider.apply.failed`). Such a code is still bounded value-free
data; it is not a substitute for the stable alert codes below.

### Alert codes

| Code | Severity | Meaning and operator interpretation |
|---|---|---|
| `managed.lifecycle.recovery-required` | Critical | Provider state or cleanup is uncertain; reconcile before any replay. |
| `managed.lifecycle.operation-failed` | Critical | A lifecycle operation reached `Failed`. |
| `managed.lifecycle.run-failed` | Critical | Its correlated deployment run reached `Failed`. |
| `managed.lifecycle.stale-work` | Warning | A blocking operation or active queued/running run has no recent progress. |
| `managed.lifecycle.reconciliation-stale` | Warning | Unknown/ambiguous provider reconciliation is older than the boundary. |
| `managed.lifecycle.reconciliation-unknown` | Warning | Provider observation is unknown or ambiguous but not yet stale. |
| `managed.lifecycle.unhealthy-endpoint` | Warning or Critical | Endpoint/lifecycle projection is degraded (Warning), unreachable, or failed (Critical). |
| `managed.lifecycle.retry-exhausted` | Critical | Attempt number is at or above the configured maximum for non-terminal work. |

Alert deduplication identities are deterministic SHA-256 values derived from
opaque workspace/instance/work identifiers and the alert code. They are safe
correlation keys, not diagnostic payloads. The same snapshot and code must not
produce a new alert identity merely because evaluation time changed.

## Telemetry signals

The provider-neutral activity source and meter are
`ElsaControl.ManagedLifecycle`. Worker spans use
`managed_lifecycle.worker`; reconciliation spans use
`managed_lifecycle.reconciliation`. Counters are:

- `managed_lifecycle.operations.completed` for completed operations;
- `managed_lifecycle.operations.errors` for errors crossing an operation boundary;
- `managed_lifecycle.operations.transitions` for operation-state transitions;
- `managed_lifecycle.operations.retries` for attempts after the first; and
- `managed_lifecycle.endpoint.health.evaluations` for reconciliation health outcomes.

`managed_lifecycle.operations.duration` is the operation-duration histogram in
milliseconds.

Durably completed reconciliation replays remain visible as spans with outcome
`already_completed`, but do not increment completion, duration, or endpoint
health measurements again.

Use the action, operation outcome, lifecycle/health state, and operation state
to filter the metric signals. Activity and trace diagnostic code tags use an
explicit fixed-code allow-list and collapse anything else to `unknown`;
diagnostics are not metric labels. The allowed tag list and redaction rules are
defined in
[Tenant and redaction rules](#tenant-and-redaction-rules).

## Deadline and retry boundaries

The current API composition constructs the evaluator with its implementation
defaults. The options are `ManagedLifecycleOperationalHealthOptions`; any host
override must preserve these boundaries and update this runbook and its
controlled-fixture evidence.

| Policy | Current boundary | Clock used |
|---|---:|---|
| `OperationDeadline` | 10 minutes | Latest of heartbeat, last progress, started, or accepted time for blocking operation work. |
| `RunDeadline` | 10 minutes | Latest of heartbeat, last progress, started, or queued time for queued/running deployment runs. |
| `ReconciliationDeadline` | 10 minutes | `ReconciledAt`, but only when provider observation, observed lifecycle, or health is unknown/ambiguous. |
| `MaxAttempts` | 3 attempts | A non-terminal operation/run at or above this attempt count emits `managed.lifecycle.retry-exhausted`. |

The deadline comparison is strict: elapsed time must be **greater than** the
configured boundary. Exactly 10 minutes is not stale; the first evaluation
after 10 minutes is. A recent heartbeat or progress timestamp resets the
operation/run staleness clock. A confirmed old healthy projection is not stale
reconciliation. `RecoveryRequired` remains the result even if a stale-work or
retry-exhausted alert is also emitted.

The lifecycle worker's `Deployment:ElsaInstanceLifecycle:PollInterval` (five
seconds by default) controls polling cadence only; it is not an operational
deadline.

## Response paths

For every alert, capture only the UTC timestamps, status/code, severity, opaque
operation/run IDs, attempt, deduplication identity, and trace correlation. Check
the operation and audit projections in the same workspace before acting.

### Failed operation or run

1. Confirm the operation/run is terminal `Failed` and inspect its stable code and
   audit history.
2. Determine whether the provider boundary was definitely rejected or remains
   uncertain. A failed local validation/rejection is not the same as an
   unknown remote apply.
3. If no remote mutation could have been accepted and the observed lifecycle is
   `Failed` or `Degraded`, use the existing `Retry` action after the instance is
   no longer blocked, with the current `If-Match` and a new idempotency key. If
   that precondition is not met, use the state-machine-appropriate `Reconcile`
   path instead. Re-check health afterward.
4. If remote acceptance is uncertain, do not retry directly. Preserve the
   existing operation and move through the `RecoveryRequired` path below.

### Stale work or stale reconciliation

`Stale` is detection, not authorization to cancel or duplicate work.

- For stale operation/run work, inspect worker ownership, lease/progress and
  correlated provider state. Do not issue `Retry`, `Start`, or another mutation
  while the existing reservation is blocking. If the durable operation becomes
  `RecoveryRequired`, use the explicit recovery path; otherwise allow the
  current worker/reconciler to complete or escalate the incident.
- For stale unknown reconciliation with no blocking operation, request the
  existing `Reconcile` action with the current version and a new idempotency
  key. The provider must establish a concrete state before the instance is
  treated as healthy or eligible for a new action.

### Recovery required

`RecoveryRequired` is the single durable recovery gate. Stop new lifecycle
mutations, retain the operation/run and its correlation, and reconcile provider
state before making another remote call. Use the existing `Recover` action only
after that reconciliation, with the current `If-Match` and a new recovery
idempotency key. `Recover` resumes the same operation and increments its attempt;
it does not create a competing operation or silently replay an uncertain apply.

When provider state cannot be proven, or when the source is not safe to continue,
escalate to the sealed recovery-point and restore-to-new contract in
[#129](https://github.com/valence-works/elsa-control/issues/129) and
[Managed Elsa Instance Recovery Contract](managed-instance-recovery.md).

### Azure recovery observation acceptance

The Azure persistence boundary retains immutable recovery observations, not raw
provider responses. An observation binds the exact organization, workspace,
instance, lifecycle operation/attempt, provider operation/attempt/version/checkpoint,
assignment, and retained resolved plan. The assignment must still belong to that
provider operation. Stored natural keys and record digests are recomputed on reads
and replay; unchanged concurrent polls return one immutable receipt.

For non-Delete Azure recovery, a URL describing retry evidence is insufficient.
Acceptance requires the canonical opaque observation reference and its digest,
backed by the retained ledger row. Azure authority is identified by the correlated
provider operation or bound Azure assignment, not by assuming every provider-neutral
placement token is an Azure GUID. Reconciliation advances the observed instance
version once; recovery must bind that exact reconciled version and advances it again.
Unrelated instance drift or changed provider authority invalidates the observation.

Confirmed Delete recovery preserves its original operation and confirmation
boundary; it does not require a non-Delete retry observation or fresh confirmation.
An ordinary Delete acceptance/replay without the explicit recovery tuple still
requires its confirmation context.

For an uncertain Azure Delete, acceptance records an immutable authority snapshot
on the existing recovery-request ledger. It binds the same provider operation,
assignment, attempt, version, checkpoint, and plan/template/scope fingerprints to
the accepted lifecycle attempt and committed instance version. The deletion worker
must present its current lease when consuming that authority. An attempt number
alone is not authorization, and ordinary provider polling never resumes an uncertain
Delete automatically.

Recovery runs in an independent provider scope so its database operations do not
share an EF context with the lifecycle lease heartbeat. A repeated dispatch observes
the exact claimed successor rather than creating another operation. If that successor
becomes uncertain again, it requires a fresh explicit `Recover`; the original ledger
entry cannot authorize another provider attempt. A successful operation status alone
does not prove absence: the correlated assignment must be deleted, both inventories
must contain only the retained resource-group name, and the operation must retain
`CleanupVerified` with no endpoint.

If the worker stops after persisting `CleanupVerified` but before recording success,
explicit recovery may finish the same Delete without calling Azure again. This
finalization-only path requires no retained attempted step, cleared inventories on
both the operation and its exact correlated assignment, and no endpoint. The
assignment may be `Deleted` after lease expiry or `Unknown` after explicit
uncertainty; neither state alone grants recovery authority. Capture, claim, and
executor assignment loading all enforce the same evidence predicate. Changed
ownership, remaining resources, or invalid retained metadata fail closed.

The recovery-authority column is nullable for existing ledger rows. Legacy absence
of this snapshot does not grant Azure replay authority. The migration preserves
append-only guards; SQLite rollback uses native column removal to keep those guards
intact. Local migration and lifecycle tests are not a substitute for the managed-
identity fault/recovery and confirmed-cleanup demonstration.

These persistence rules do not activate an Azure observer or authorize blind replay.
Provider recovery execution and live fault/recovery proof remain under
[#271](https://github.com/valence-works/elsa-control/issues/271). An accepted observation
must be validated against the retained recovery request before claiming the provider
attempt; it is not a reusable authorization after provider state changes.

#### Azure staged SQL recovery

The Azure SQL boundary uses three distinct provider steps. The legacy composite
`SqlBootstrap` step is not eligible for this staged recovery path and is never
replayed as a substitute for one of these steps:

| Retained attempted step | Read-only recovery evidence | Durable checkpoint authorized |
|---|---|---|
| `SqlFirewallCreate` | Exactly one `elsa-bootstrap` rule in the retained subscription, resource group, and server, with both IPs equal to the configured bootstrap IP. | Exact presence confirms `SqlFirewallReady`. Absence after an uncertain create remains unknown because a late create may still arrive; it does not authorize a new create from observation. |
| `SqlBootstrapScript` | The SQL endpoint reports exactly one contained principal named for the retained workload, type `E`, with the retained managed-identity client ID/SID and `db_datareader`, `db_datawriter`, and `db_ddladmin` memberships. | The complete postcondition confirms `SqlBootstrapReady`; the script is never rerun by observation. |
| `SqlFirewallCleanup` | The exact temporary rule is absent, or the rule remains present while the same exact SQL script postcondition is independently proven. | Absence confirms firewall cleanup. The present-plus-script case confirms only the script checkpoint and permits the narrow cleanup-only resume; it does not replay the script. |

The observer performs no firewall create, firewall delete, or SQL bootstrap
script execution. A denied, malformed, duplicate, conflicting, or otherwise
ambiguous firewall inventory fails closed before mutation. A successful staged
script deliberately leaves the temporary rule for the separate durable cleanup
step. If script preflight or execution fails after that rule exists, the runner
attempts exact cleanup and verifies absence before returning a terminal failure;
failure to prove cleanup remains `RecoveryRequired`/uncertain. An uncertain SQL
script is not automatically retried, because its partial remote effects must be
resolved by the read-only postcondition path first.

These checkpoints prove only the named SQL postcondition. They do not establish
workload completion, endpoint health, traffic promotion, lifecycle `Ready`, or
confirmed `Delete`; the normal workload, health, traffic, and lifecycle gates
remain required after SQL recovery. The staged contract and controlled tests are
not live Azure acceptance evidence. Live acceptance still requires the opt-in
production proof to exercise late-visible firewall/script outcomes, cleanup
verification, restart/recovery correlation, and the complete lifecycle result.

### Unhealthy endpoint

For `managed.lifecycle.unhealthy-endpoint`, confirm whether the projection is
`Degraded`, unreachable, or failed and use the safe provider/runtime health
diagnostics available to the operator. Do not mark an instance Ready, healthy,
or cutover-eligible from a control-plane enqueue alone. Once the endpoint is
known healthy and no operation is blocking, use `Reconcile` to refresh the
provider-neutral projection. A controlled `Restart` is an explicit lifecycle
operation and must not be used to bypass an uncertain provider result.

### Retry exhaustion

`managed.lifecycle.retry-exhausted` is critical at attempt 3 (or the configured
maximum) for non-terminal work. Stop automatic replay, inspect the exact
operation/run and provider correlation, and classify the failure as deterministic
or uncertain. Deterministic failure may proceed through the normal bounded
`Retry` path after the reservation is released; uncertain failure requires
`RecoveryRequired`. If a known-good revision exists, follow the rollback path
below. If the source cannot be trusted or rollback cannot safely restore service,
use #129 restore-to-new escalation.

## Rollback and restore-to-new escalation

Rollback is a controlled deployment action against a known-good successful
revision. It requires the existing deployment validation, explicit confirmation,
one active-run reservation, and the current workspace authorization boundary.
Use the existing deployment/run history and rollback flow described by the
[deployment UX specification](../../specs/022-deployment-ux/spec.md) and
[Elsa Instance aggregate contract](elsa-instance-aggregate.md). Rollback must
not be used to guess the outcome of an uncertain provider mutation; reconcile
that operation first.

Escalate to #129 when the source remains unhealthy or uncertain after bounded
reconciliation, rollback is unavailable or unsafe, or repeated retries have
exhausted. The restore-to-new response is:

1. Preserve the source and select one sealed, unexpired recovery point. For the
   initial objective, reject a point older than 24 hours.
2. Create a distinct isolated target and restore the relational state. Never
   reuse the source instance identity, database, endpoint, or placement.
3. Rebind the required secret references at the provider boundary; never copy or
   expose secret values.
4. Validate immutable inputs, target health, and restored workflow behavior
   before any cutover decision. The source remains authoritative and traffic is
   not mutated by the proof.
5. If the target operation or cleanup is uncertain, keep it recovery-owned and
   resume from the same immutable recovery checkpoint. Do not discover a newer
   point or delete the source to resolve uncertainty.

The recovery contract targets a 24-hour RPO and four-hour RTO for its initial
Dedicated profile, subject to its documented proof and limitations. It does not
promise multi-region DR, zero data loss, in-place rollback, or automatic
cutover.

## Tenant and redaction rules

- Organization is the customer tenant; Workspace remains the operational
  authorization and resource-isolation boundary. Every read and mutation must
  resolve the requested instance through the supplied workspace and deny or
  hide cross-workspace access. See [Organization tenancy](../../specs/031-organization-tenancy/spec.md).
- API responses and authorized operator records may contain safe enum state,
  UTC timestamps, attempt counts, opaque control-plane IDs, trace IDs, stable
  diagnostic codes, and deterministic deduplication identities.
- Metrics are low-cardinality. The managed lifecycle meter allows exactly
  `action`, `outcome`, `desired_lifecycle`, `observed_lifecycle`, `health`, and
  `operation_state`; `diagnostic_code` is activity/trace-only and never a metric
  label. Never add organization, workspace, instance, operation, provider,
  endpoint, resource, name, or message labels.
- Logs/traces may correlate an authorized incident with opaque IDs where the
  access policy permits it, but must not include customer names, provider/Azure
  resource IDs, endpoint URLs or credentials, tokens, passwords, secret values,
  workflow/package/artifact payloads, request bodies, local paths, or raw
  exception/provider diagnostics.
- A diagnostic code is a bounded lower-case value-free token. Free-form failure
  text is not an operational code and must not be copied into alerts, metrics,
  API projections, or audit history.

## Authenticated telemetry rollout (#266)

The separate [managed telemetry sink template](../../infra/managed-telemetry/README.md)
prepares workspace-based Application Insights with local authentication disabled
and an exact-resource publisher grant for the existing Control API managed
identity. It does not modify API deployment mode, enable workers, or prove
ingestion. The Aspire dashboard web app and its OTLP sidecar are not provisioned
in production (#302); local development keeps the dashboard. The deploy identity Aspire
created beside the dashboard (`infra/control-deploy-identity`) is the GitHub Actions login
and must never be removed as part of dashboard cleanup. The Azure service endpoints are Entra/RBAC
protected; private-link ingestion is not implied.

The opt-in API exporter requires `ManagedLifecycleTelemetry:AzureMonitor:Enabled`,
an explicit `ConnectionString` with its ingestion endpoint, and the matching
`ManagedIdentityClientId` in the same section. Keep it disabled until the sink and
identity grant have been verified. Set the process environment variable
`APPLICATIONINSIGHTS_STATSBEAT_DISABLED=true` before API startup: the pinned Azure
Monitor SDK exposes its auxiliary statistics opt-out through the environment,
not public exporter options. Startup fails closed if this opt-out is absent.
The host does not silently change process-wide SDK settings. Live metrics,
performance counters, standard metrics and offline storage are disabled; only
the managed lifecycle meter and activity source are registered with this sink.
Normal component connection metadata may include `LiveEndpoint` and `ApplicationId`;
these are validated but excluded from the connection string given to the exporter.
Token acquisition and ingestion each have a separate ten-second cancellation
budget, with ingestion retries disabled. Provider shutdown shares a five-second
drain budget rather than starting separate flush and shutdown grace periods.
Host cancellation suppresses any subsequent drain wait; an already-running
synchronous SDK shutdown call cannot be interrupted through this API.
These are cooperative SDK deadlines, not a promise that the entire host can stop
within five seconds under every failure. An expired export is missing evidence,
not a successful observation; offline replay is disabled.
Metrics use delta temporality, matching the Azure Monitor exporter contract:
each interval contains new counts rather than replaying the cumulative total.

The API owns one Azure Monitor sink for the process lifetime. Do not register a
second Azure Monitor exporter against the same connection string or hot-replace
the sink: the pinned SDK caches the first transmitter and its credential/options.
Apply changes through an API restart/promotion. Existing ServiceDefaults OTLP
export is preserved; when `OTEL_EXPORTER_OTLP_ENDPOINT` is configured, managed
lifecycle signals reach that explicitly configured destination as well as this
Azure Monitor sink. Review both destinations during operational acceptance.

The bounded observation window is produced by
`scripts/managed-lifecycle-slo-sampler.py`. It probes the managed runtime health
endpoint itself at a fixed interval for the whole window (Control's stored
operational health can be recorded alongside but never substitutes for a fresh
probe), optionally counts the named `managed_lifecycle.*` signals that reached the
sink during the same UTC window through the Entra-authenticated query boundary, and
prints one JSON line with the window timestamps and healthy/unhealthy/unknown
counts. Unreachable, timed-out, capped or missing samples are `unknown`, never
healthy; a failed sink query marks the window incomplete. The output carries no
URL, host, token, body or error text, so it is safe acceptance evidence as-is.

Treat exporter startup, actual signal ingestion, authenticated operator access to
the workspace, and the fresh five-minute observation window as separate gates. The
operator observation surface is Application Insights/Log Analytics behind Entra RBAC,
with the API's App Service console/HTTP/platform/app logs delivered to the same
workspace by the `api-logs-to-managed-workspace` diagnostic setting; an anonymous
denial alone is not positive operator-access proof. Stored instance health alone is not a fresh endpoint sample. A missing or capped telemetry window
remains unknown/incomplete, never healthy. Keep the metric and authorized trace
contracts above unchanged throughout rollout.

## Controlled-fixture validation

Validate this runbook against controlled persistence/API fixtures for:

- Healthy, degraded, failed, unknown, stale, and recovery-required results;
- exact-deadline versus one-tick-past-deadline behavior;
- heartbeat/progress freshness and reconciliation freshness;
- retry exhaustion and stable alert deduplication across evaluation times;
- interrupted work with no duplicate provider mutation;
- cross-workspace read denial and redaction of unsafe diagnostics; and
- rollback selection and restore-to-new escalation without source mutation.

These checks prove deterministic contracts and safe response paths. They are not
production SLO measurements and do not establish Azure Monitor, PagerDuty, or
another vendor's alerting/delivery behavior.

## Opt-in production-composition Azure lifecycle proof (#265)

The live proof is an explicitly gated `WebApplicationFactory<Program>` run. It
uses the production `Program` registrations, real migrated SQLite persistence,
the in-process lifecycle and provider workers, and the real Azure CLI/SQL
bootstrap tools. It is not the standalone proof host and does not replace
producer admission or public API/ingress proof.

Before starting the host, provision a fresh isolated instance identity and
resource-group scope, attach the canonical managed identity, and grant only
the capabilities required by the provider preflight: subscription-level
resource-group creation and descendant mutation, registry pull, private blob
evidence upload, Key Vault secret retrieval, and the SQL bootstrap authority.
The staged production configuration contains two versioned external Key Vault
references (signing and admin) plus the provider-owned
`secret://azure-managed/sql-connection` sentinel. It contains no secret
values. Supply a previously admitted, externally verified catalog-entry
projection as the fixture; producer admission is an upstream prerequisite, not
part of this proof.

The initial public Azure profile requires unrestricted egress. Set the stamp's
server-owned `RuntimeBuilder:InstancePlans:DefaultEgress` to `unrestricted`
explicitly when using that profile. Omission retains the platform's `restricted`
default; enabling Azure does not relax it automatically. Unsupported policy
values fail startup. This is not a claim that the initial Azure profile enforces
restricted outbound connectivity.

The test content root and production settings file are an early-startup
packaging contract. Stage the supplied safe configuration at
`<api-content-root>/appsettings.Production.json` before starting the test
process, and point the test configuration variable at that exact absolute
file. Set the environment before `WebApplicationFactory` is created:

```bash
export ASPNETCORE_ENVIRONMENT=Production
unset DOTNET_ENVIRONMENT
export ASPNETCORE_TEST_CONTENTROOT_ELSACONTROL_API=/src/src/Hosting/ElsaControl.Api
export ELSA_CONTROL_LIVE_AZURE_LIFECYCLE_PROOF=1
export ELSA_CONTROL_LIVE_AZURE_LIFECYCLE_PROOF_CONFIG=/src/src/Hosting/ElsaControl.Api/appsettings.Production.json
dotnet /usr/share/dotnet/sdk/10.0.300/vstest.console.dll \
  /run/elsa-control/tests/ElsaControl.Api.Tests.dll \
  '--TestCaseFilter:FullyQualifiedName~ProductionAzureLifecycleProofTests.Production_composition_applies_reconciles_reloads_and_deletes_one_instance' \
  '--Logger:console;verbosity=minimal'
```

The test rejects a non-production initial environment, a conflicting
`DOTNET_ENVIRONMENT`, a relative or missing test content root, or a
configuration path other than that content root's
`appsettings.Production.json`. This is intentional: a late
`ConfigureWebHost`/`ConfigureAppConfiguration` callback cannot supply values
needed by imperative reads during minimal-hosting startup. The normal staged
production file must be the authority. The gate is explicitly skipped when
`ELSA_CONTROL_LIVE_AZURE_LIFECYCLE_PROOF` is absent; it must never silently
fall back to mocks or a test-only DI composition.

Each wait is bounded by `LiveProof:TimeoutSeconds` (30 seconds to two hours)
and `LiveProof:PollSeconds` (one to 30 seconds). The proof creates, reconciles,
disposes and reloads the application, reconciles again, and confirms deletion
through the lifecycle service. Failure cleanup is always attempted. Retain
only the isolated database and value-free evidence privately; do not upload
configuration, keys, tokens, logs, exception messages, or provider payloads.

`ControlPlane:Origin` must be an HTTPS origin so persisted plan URIs are
well-formed. The lifecycle provider reads the persisted plan in-process; this
proof does not dereference that URI and therefore does not establish an
externally reachable plan endpoint, public API acceptance, or ingress behavior.

### Asynchronous delete completion

A correlated provider Delete in Accepted, Queued or Running is normal cleanup in
progress, not an operator recovery event. The lifecycle worker retains its deletion
reservation and defers the next observation for one minute. Confirmed absence may
tombstone the instance only when the provider evidence belongs to the same lifecycle
Delete, assignment, workspace, organization, target and provider scope.

Provider RecoveryRequired, failed/unavailable observations and correlation failures
remain on the explicit recovery path; the ordinary worker does not replay them.
Delete idempotency lineage accepts the exact lifecycle root and canonical retry-ID
segments. Very long legacy retry chains that collapse into an unattributable hashed
key require explicit operator recovery, even if provider cleanup succeeded. Do not
force lifecycle completion merely because resource absence is visible in Azure.
