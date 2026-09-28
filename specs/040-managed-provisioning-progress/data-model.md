# Data Model: Managed Engine Provisioning Progress

No new persistent entities are required. The feature derives a customer projection from existing durable records.

## Existing Authoritative Records

### Managed Instance

Relevant fields:

- Workspace and organization ownership.
- Instance identity.
- Desired and observed lifecycle.
- Health.
- Last lifecycle operation reference.

The scoped managed instance establishes customer ownership. Deleted instances remain governed by existing lifecycle retention and are outside Create progress display.

### Lifecycle Create Operation

Relevant fields:

- Operation identity, held only server-side.
- Instance and workspace ownership.
- Action (`Create`).
- State: accepted, waiting, queued, entitlement-held, running, recovery-required, succeeded, failed, cancelled, or unknown.
- Accepted, started, and completed timestamps.
- Safe terminal failure code where available.

The lifecycle operation supplies immediate request-accepted progress before provider work exists and supplies authoritative terminal state.

### Azure Provider Operation

Relevant fields:

- Server-side correlation to workspace, instance, lifecycle action, and idempotency key.
- Provider status and phase.
- Checkpoint sequence.
- Created, updated, completed, and status-changed timestamps. `StatusChangedAt` is written only when `Status` changes and is backfilled from `UpdatedAt`.

Provider IDs, target keys, resource references, endpoints, fingerprints, images, worker/lease data, and diagnostics are never copied into the customer projection.

### Azure Provider Transition

Relevant fields:

- Durable sequence.
- Provider status and phase.
- Occurrence timestamp.

Transition message and provider operation identity remain private. The phase and status are mapped into known product-owned codes.

## Customer Projection

### Provisioning Progress Snapshot

Fields:

- `state`: `queued`, `active`, `waiting-for-prior-operation`, `entitlement-held`, `stale`, `ready`, `failed`, or `unavailable`.
- `provider`: optional allowlisted display value such as `azure`.
- `currentStage`: one stable stage code, or null when no safe current stage is known.
- `startedAt`: lifecycle acceptance timestamp when available.
- `lastUpdatedAt`: latest durable customer-relevant transition timestamp when available.
- `completedAt`: terminal lifecycle timestamp when available.
- `diagnosticCode`: optional stable public diagnostic code.
- `stages`: exactly seven ordered stage projections.
- `activity`: ordered, deduplicated customer activity entries.

Validation rules:

- The route scope, not a response/request body identifier, establishes workspace and instance ownership.
- A terminal lifecycle state wins over a nonterminal provider snapshot.
- A known later stage implies earlier stages completed.
- Unknown or out-of-order provider phases cannot regress completed customer stages.
- The serialized snapshot contains no provider or lifecycle operation IDs. A waiting snapshot carries the blocker id and stage only as non-serialized projector fields.

### Provisioning Stage

Fields:

- `code`: `request-accepted`, `hosting-foundation`, `configuration`, `runtime-deployment`, `health-verification`, `traffic-routing`, or `ready`.
- `status`: `pending`, `current`, `completed`, `blocked`, or `unknown`.
- `startedAt`: optional first mapped transition time.
- `completedAt`: optional time a later durable stage or terminal state proved completion.

State invariants:

- At most one stage is `current` or `blocked`.
- Every stage before the current/blocked stage is `completed`.
- Every known stage after it is `pending` unless the overall state is `unavailable`.
- Overall `ready` makes all stages completed.
- Overall `failed` blocks the last known stage and preserves earlier completed stages.

### Provisioning Activity Entry

Fields:

- `sequence`: monotonic customer activity sequence derived from durable order, not an internal database identifier.
- `stage`: stable customer stage code.
- `status`: `started`, `completed`, `blocked`, or `ready`.
- `messageCode`: stable allowlisted product-copy key.
- `occurredAt`: durable timestamp.

Validation rules:

- Duplicate provider transitions mapping to the same customer outcome may be coalesced.
- Entries are returned in ascending sequence order.
- Raw transition messages are never returned.
- Unknown provider phases do not create an activity entry.

## State Derivation

| Source condition | Public state | Stage behavior |
|---|---|---|
| Create accepted/queued and no provider phase | `queued` | `request-accepted` current; later stages pending |
| Create `WaitingForPriorOperation` and no provider phase | `waiting-for-prior-operation` | distinct current stage (`waiting-for-delete` / `waiting-for-update` when the blocker kind is known); no clock of its own. The snapshot serializes the blocking operation id, stage, and stale reason when present. The blocker is projected with its own provider operation, so a Create that is still Running does not make the waiter stale. If the blocker is stale, this snapshot is `stale` with reason `blocking-operation-stale`. A foreign, mismatched, or unresolvable blocker id fails closed as `stale`. Blocker resolution is one level, same organization and instance. The store only makes Delete wait; the reader still projects Create. |
| Create `EntitlementHeld` and no provider phase | `entitlement-held` | distinct `entitlement-held` current stage; no clock. Control copy on Create: "New engines aren't available right now. Resolve this on elsacloud.app." Control copy on a held change to an existing engine: "New changes to this engine aren't available right now. Resolve this on elsacloud.app." Delete is never projected as entitlement-held. |
| Create recovery-required, null FailureCode, current run reason `provider.submission.accepted` or `provider.reconciliation.in-progress`, no provider operation yet | `queued` | `request-accepted` current; later stages pending |
| Same healthy-continuation reasons with provider Accepted/Queued/Running | `active` | mapped stage current (`request-accepted` while Accepted/Queued); earlier complete; later pending. Stage never moves backwards. |
| Healthy continuation (`accepted`, `in-progress`, or `provider.reconciliation.health-unknown`) with provider Succeeded before Ready, or `health-unknown` during verification | `active` | `max(known stage, health-verification)` current |
| Transient uncertainty (`provider.reconciliation.unavailable` or `provider.reconciliation.unknown`), null FailureCode | `active` | last known stage held (never moves backwards) until the progress bound |
| No provider `Status` change for 10:00 or more while Create has not finished (healthy and transient groups). Clock is `StatusChangedAt` (written only when `Status` changes; backfilled from `UpdatedAt`), or lifecycle `AcceptedAt` when there is no provider row. Inclusive: exactly 10:00 is `stale`. Skipped while the provider is `Running` and once Create has finished. With no provider row the clock applies only to Accepted/Queued for any operation kind, including Delete, plus the RecoveryRequired hand-off. Lifecycle `WaitingForPriorOperation` and `EntitlementHeld` are exempt. Also applies to provider Accepted/Queued and Succeeded-before-Ready. | `stale` | last known stage blocked; requires-attention diagnostic |
| Create recovery-required with any FailureCode; current reason `provider.submission.uncertain`, `provider.reconciliation.ambiguous`, `provider.reconciliation.correlation-mismatch`, `provider.reconciliation.retry-safe`, or any unrecognised reason; or provider recovery-required | `stale` | last known stage blocked; requires-attention diagnostic |
| Healthy continuation with provider Failed/Cancelled | `failed` | last known stage blocked; safe failure diagnostic |
| Healthy continuation with provider Succeeded and instance Ready | `ready` | every stage complete |
| Create succeeded and instance Ready | `ready` | every stage complete |
| Create failed/cancelled | `failed` | last known stage blocked; safe failure diagnostic |
| Instance exists but Create/provider history cannot be safely correlated | `unavailable` | known lifecycle remains visible; detailed progress unavailable |

## Retention

Progress is a derived view. It remains available while the associated managed instance and its existing lifecycle/provider history remain retained. This feature adds no independent retention schedule.
