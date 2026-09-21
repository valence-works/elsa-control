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
- Created, updated, and completed timestamps.

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

- `state`: `queued`, `active`, `stale`, `ready`, `failed`, or `unavailable`.
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
- The snapshot contains no provider or lifecycle operation IDs.

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
| Create accepted/waiting/queued/entitlement-held and no provider phase | `queued` | `request-accepted` current; later stages pending |
| Provider accepted/queued/running with known phase | `active` | mapped stage current; earlier complete; later pending |
| Create recovery-required | `stale` | last known stage blocked; requires-attention diagnostic |
| Create succeeded and instance Ready | `ready` | every stage complete |
| Create failed/cancelled | `failed` | last known stage blocked; safe failure diagnostic |
| Instance exists but Create/provider history cannot be safely correlated | `unavailable` | known lifecycle remains visible; detailed progress unavailable |

## Retention

Progress is a derived view. It remains available while the associated managed instance and its existing lifecycle/provider history remain retained. This feature adds no independent retention schedule.
