# Research: Managed Engine Provisioning Progress

## Decision 1: Project durable transitions instead of streaming logs

**Decision**: Build progress from existing lifecycle operations, Azure provider operations, and append-only transition records.

**Rationale**: These records survive refresh and restart, already define ordered progress, and can be reduced to a safe customer contract. A log stream would be volatile, harder to authorize, and likely to expose provider details.

**Alternatives considered**:

- Stream raw Azure CLI or ARM output: rejected because output is unstable and can contain resource identities, endpoints, tenant details, and arbitrary messages.
- Show only a spinner: rejected because it improves motion but does not answer whether work is advancing.
- Infer progress in the browser from elapsed time: rejected because provider stage duration is non-linear and the browser is not authoritative.

## Decision 2: Use stable product stages without percentages

**Decision**: Map provider phases into seven ordered customer stages and show completed/current/pending state plus indeterminate activity.

**Rationale**: Stage position is truthful even when duration is unpredictable. A percentage would imply a calibrated estimate the platform does not yet have.

**Alternatives considered**:

- Equal-weight percentage by stage: rejected because long SQL, foundation, and workload phases would make the number misleading.
- Provider-specific phase names in the UI: rejected because they couple customer copy to Azure and make future providers inconsistent.

## Decision 3: Resolve lifecycle-to-provider correlation on the server

**Decision**: The progress reader resolves the scoped managed instance, its applicable Create lifecycle operation, and its correlated Azure operation from server-owned records.

**Rationale**: Accepting an operation ID from the browser as authority would create an avoidable object-reference and cross-workspace risk. Existing provider records already include instance/action/idempotency correlation.

**Alternatives considered**:

- Put provider operation IDs in the engine-list response: rejected because the identifier is internal and unnecessary for the customer journey.
- Let the BFF call the operator Azure-operation endpoint: rejected because that route has a broader deployment permission and provider-shaped contract.

## Decision 4: Use a dedicated customer contract

**Decision**: Add a managed-instance provisioning-progress response rather than reusing operational health, audit, topology, or the existing Azure-operation response.

**Rationale**: Existing operator projections contain extra identifiers, diagnostic surfaces, or semantics that are inappropriate for the Hosted browser. A purpose-built response can be audited as a strict allowlist.

**Alternatives considered**:

- Extend the engine-list item with full activity: rejected because it delays the list, grows every poll response, and couples base listing to provider history.
- Reuse admin topology: rejected because it is explicitly admin-only and contains operational identity.

## Decision 5: Normalize and strip again in the Cloud BFF

**Decision**: The BFF parses the upstream response into a new object containing only known enums, codes, sequence numbers, and valid timestamps.

**Rationale**: Defense in depth prevents additive upstream fields from silently reaching browser code and matches the existing Cloud compatibility and delete-contract pattern.

**Alternatives considered**:

- Pass through the Control JSON: rejected because future Control fields would bypass browser-boundary review.
- Trust TypeScript static types: rejected because types do not validate runtime network payloads.

## Decision 6: Poll rather than introduce streaming transport

**Decision**: Poll every five seconds only while progress is actively nonterminal, pause when hidden, refresh on visibility return, and stop on terminal states.

**Rationale**: Provisioning spans minutes, the existing dashboard already polls lifecycle state, and a five-second interval satisfies the 15-second observation target with low load. No new streaming infrastructure is needed.

**Alternatives considered**:

- Server-Sent Events or WebSockets: rejected for this version because they add connection lifecycle, scaling, and authentication complexity without improving correctness.
- Poll the complete engine list more frequently: rejected because progress can load independently and should not delay or churn unrelated dashboard data.

## Decision 7: Treat recovery-required as attention needed

**Decision**: A Create operation in `RecoveryRequired` maps to the public `stale` outcome with a stable requires-attention diagnostic. No age-only browser heuristic declares staleness.

**Rationale**: RecoveryRequired is authoritative evidence that automatic advancement stopped. A fixed elapsed-time threshold could mark healthy long-running Azure work as stalled.

**Alternatives considered**:

- Mark Running stale after a fixed number of minutes: rejected until historical provider timing and heartbeat semantics justify such a threshold.
- Continue animating RecoveryRequired as active: rejected because it falsely implies autonomous progress.

## Decision 8: Release capability first

**Decision**: Add `hosted.instances.provisioning-progress.v1` to Control's additive compatibility response, deploy Control first, then require it from the Cloud BFF/UI release.

**Rationale**: The existing compatibility contract already provides fail-closed version-skew behavior. Older Cloud releases ignore additive capabilities; newer Cloud releases must not call a route absent from old Control.

**Alternatives considered**:

- Silently fall back when the capability is absent: rejected because it can mask partial rollout and weakens exact-version rehearsal.
- Increase the whole contract version: rejected because this is an additive capability, not a breaking replacement.

## Decision 9: No new storage or migration

**Decision**: Query existing lifecycle, provider-operation, and transition records; derive the customer projection on read.

**Rationale**: The durable source data already exists. Persisting duplicate customer stages creates drift and new migration/reconciliation work.

**Alternatives considered**:

- Persist a second customer activity ledger: rejected for the initial feature because it duplicates existing authoritative transitions.
- Store activity in the browser: rejected because it fails refresh and multi-tab consistency.
