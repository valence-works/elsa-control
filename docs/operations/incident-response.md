# Elsa Cloud Managed-Instance Incident Response

**Scope:** the support and operator procedure for incidents involving managed
Elsa Cloud instances. Follow the detailed [managed lifecycle operations
runbook](../product/managed-lifecycle-operations-runbook.md) for state-machine,
provider, recovery, rollback, and cleanup rules. This document records required
handling; it is not a production service or alert-delivery guarantee.

## Response and routing policy

An agent must acknowledge a customer report within 15 minutes, at any hour.
When a decision or reply needs Sipke, record it in the needs-Sipke queue with a
due date on the next business day, Monday through Friday in
Europe/Amsterdam. These are accepted Elsa Cloud policy requirements; the inbox,
agent access, timed acknowledgement, and visible due-date queue still need
operating evidence under [#815](https://github.com/valence-works/elsa-control/issues/815).
They do not promise a one-hour human response, on-call coverage, 24/7 human
support, or credits. Valence Runtime terms are unchanged.

## Classify and route

| Observed signal | Classification | Route |
| --- | --- | --- |
| `managed.lifecycle.recovery-required`, `managed.lifecycle.operation-failed`, `managed.lifecycle.run-failed`, or `managed.lifecycle.retry-exhausted` | Critical | Start this incident procedure and follow the matching [runbook response path](../product/managed-lifecycle-operations-runbook.md#response-paths). |
| `managed.lifecycle.stale-work`, `managed.lifecycle.reconciliation-stale`, or `managed.lifecycle.reconciliation-unknown` | Warning | Inspect the exact operation and provider correlation. Staleness or an unknown observation is not permission to retry or cancel. |
| `managed.lifecycle.unhealthy-endpoint` | Warning or Critical, according to the health projection | Check the endpoint and active work using the [unhealthy endpoint path](../product/managed-lifecycle-operations-runbook.md#unhealthy-endpoint). |
| A product question or bug without an active managed-instance incident | Support request | Follow the [support model](support-model.md); use GitHub only for public-safe details. |

Use the runbook's stable codes and severity definitions in
[Status and stable codes](../product/managed-lifecycle-operations-runbook.md#status-and-stable-codes).
Do not invent a new severity or customer impact from a missing alert email.

## Procedure

### 1. Detect and preserve evidence

Use an authenticated session with access to the exact workspace and the
read-only instance health projection:

```text
GET /api/workspaces/{workspaceId}/instances/{instanceId}/health
```

Record only the UTC observation time, status and stable diagnostic code,
severity, opaque operation/run IDs, attempt, deduplication identity, and trace
correlation. Check the operation and audit projections in that same workspace.
Preserve the exact desired revision ID and canonical hash relevant to the
incident; do not substitute a mutable latest revision.

### 2. Acknowledge and route

Follow the agent acknowledgement target and needs-Sipke queue rule in the
[support model](support-model.md). State what was observed and when. If no
operational evidence confirms a notification was delivered, do not tell the
customer they “have been alerted.” The actual RecoveryRequired email receipt
remains unverified under [#657](https://github.com/valence-works/elsa-control/issues/657)
and [#659](https://github.com/valence-works/elsa-control/issues/659).

### 3. Contain without duplicating work

- For `RecoveryRequired`, stop new lifecycle mutations and retain the existing
  operation/run and its correlation. Reconcile provider state before another
  remote call. Use `Recover` only after reconciliation, with the current
  `If-Match` version and a new recovery idempotency key, as specified in
  [Recovery required](../product/managed-lifecycle-operations-runbook.md#recovery-required).
- For stale work, inspect worker ownership, lease/progress, and provider
  correlation. Do not issue `Retry`, `Start`, another mutation, or a duplicate
  operation while the existing reservation blocks it.
- If remote acceptance is uncertain, do not retry directly or delete resources
  by guess. Use the guarded observation and recovery rules in
  [Azure recovery observation acceptance](../product/managed-lifecycle-operations-runbook.md#azure-recovery-observation-acceptance).
- If a client or worker is interrupted after a mutation may have reached the
  provider, treat the result as uncertain. For an unknown Create or Delete
  response, use the exact read-only idempotency lookup in [Unknown Create and
  Delete responses](../product/managed-delete-cleanup-receipt.md#unknown-create-and-delete-responses);
  a `404` is not permission to resubmit the mutation.
- This runbook is not authorization. Respect existing workspace and deployment
  permissions. Production configuration, live payment changes, and destructive
  actions against real customer resources require Sipke's explicit approval.

### 4. Communicate confirmed state

Tell the customer the observed state, the safe action in progress, and the next
update only when that timing is supported by the policy above. Keep provider
identifiers, credentials, private logs, and payment details out of public
issues. Do not equate an alert rule firing, a configured recipient, or a
sample notification with delivery of an incident alert.

### 5. Resolve and verify

- For a failed operation or run, determine whether the provider definitely
  rejected the mutation or whether acceptance remains uncertain; then follow
  [Failed operation or run](../product/managed-lifecycle-operations-runbook.md#failed-operation-or-run).
- Use rollback only for a known-good successful revision and only through the
  validated deployment flow with its explicit confirmation and current
  authorization boundary. Do not use rollback to guess the outcome of an
  uncertain provider mutation. See [Rollback and restore-to-new escalation](../product/managed-lifecycle-operations-runbook.md#rollback-and-restore-to-new-escalation).
- If the source cannot be safely continued, use the sealed recovery-point and
  restore-to-new contract in [Managed Elsa Instance Recovery](../product/managed-instance-recovery.md).
- For Delete cleanup, bind evidence to the exact lifecycle operation and
  ownership path. A historical receipt is not a fresh provider observation and
  does not authorize another cleanup action; see the
  [managed Delete cleanup receipt](../product/managed-delete-cleanup-receipt.md).
- Treat an instance as healthy only when the runbook's healthy projection is
  satisfied and no active work remains. Record the verifying observation and
  its timestamp.

### 6. Review and close the incident record

Retain a concise timeline of detection, acknowledgement, route, actions,
customer replies, and verification. Include exact revision identity/hash and
safe operation/run references where relevant. Record what remains unknown and
the linked issue that owns follow-up. Do not copy secrets, raw provider
responses, customer payloads, or local paths into public evidence.

## Alert and drill evidence status

The [managed lifecycle runbook's RecoveryRequired alert section](../product/managed-lifecycle-operations-runbook.md#recoveryrequired-operator-alert-657)
describes the event and alert mechanism. It also requires a business-day check
until live receipt proof passes. As recorded in [#657](https://github.com/valence-works/elsa-control/issues/657#issuecomment-6143377213),
the staging recipient was verified and a notification-only sample arrived at
2026-10-13 10:14 UTC. The two original transition-linked RecoveryRequired
receipts remain unverified; the sample does not prove those transitions or
duplicate suppression. [#659](https://github.com/valence-works/elsa-control/issues/659#issuecomment-6143395891)
preserves the same distinction. Do not repeat the earlier personal-mailbox
search or backfill the missing transition receipts from the sample.

The currently accepted gap is proof, not a reason to infer ownership: #657
still has an open acceptance gap for the staging ops-mailbox reader and a fresh
authorized transition-linked rehearsal. No person is assigned here. The
support-inbox reader and timed intake proofs remain with #815. The timed
staging incident drill is the last #815 acceptance step. Until those items are
evidenced, do not claim that incidents are being automatically alerted, that a
named person monitors either inbox, or that the staging drill passed. This
documentation closes only its own documentation scope; it does not close #815,
#657, #659, or #508.
