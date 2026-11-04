# Elsa Cloud Support Model

**Scope:** customer support for Elsa Cloud only. Valence Runtime support terms
are unchanged. This document records the accepted policy and the procedure to
follow; it does not prove that an inbox, reader, queue, or timed response is
operating.

## Intake channels

| Audience | Accepted channel | Current evidence |
| --- | --- | --- |
| Paying Elsa Cloud customers | `support@valence.works` | Designated address for the planned support inbox. Mailbox configuration, agent access, and reader ownership are not verified by this document. |
| Public Elsa Cloud bugs and questions | [Issues in `valence-works/elsa-control`](https://github.com/valence-works/elsa-control/issues) | Repository was public when checked on 2026-11-04. Use it only for information safe to make public. |

The Elsa Hub intake form and Discord are later work, not current intake
channels. Do not tell a customer that either route is staffed or monitored.
The paid-customer address and public repository were selected in [#815](https://github.com/valence-works/elsa-control/issues/815)
and [#326](https://github.com/valence-works/elsa-control/issues/326).

## Acknowledge, classify, and route

1. Record when and where the report arrived, using a UTC timestamp and the
   sender's stated time zone where it affects the incident timeline.
2. An agent must acknowledge each report within 15 minutes, at any hour. This
   is the accepted policy target; timed operating evidence for email and GitHub
   intake remains open under [#815](https://github.com/valence-works/elsa-control/issues/815).
3. Use the severity matrix below for managed-instance reports. Its stable codes
   and severity values come from the [managed lifecycle runbook](../product/managed-lifecycle-operations-runbook.md#status-and-stable-codes).
4. Choose the next action from the three [routing outcomes](#routing-outcomes).
   Keep customer-only details out of public issues. Do not put credentials,
   raw logs, or provider identifiers in a public issue.

The 15-minute target is the agent acknowledgement. It does not create a
one-hour human response promise, an on-call rota, or a 24/7 human service.
There is no support credit commitment here.

## Severity matrix

Give each report a support classification. For managed-instance incidents, use
the health projection and stable alert code. The runtime Warning and Critical
values below are the existing runbook values; Routine is a support-triage
classification only.

| Health or report signal | Classification | Handling |
| --- | --- | --- |
| `managed.lifecycle.recovery-required`, `managed.lifecycle.operation-failed`, `managed.lifecycle.run-failed`, or `managed.lifecycle.retry-exhausted` | Critical | Route to [incident response](incident-response.md) and follow the matching [runbook response path](../product/managed-lifecycle-operations-runbook.md#response-paths). |
| `managed.lifecycle.stale-work`, `managed.lifecycle.reconciliation-stale`, or `managed.lifecycle.reconciliation-unknown` | Warning | Route to [incident response](incident-response.md) and inspect the exact operation and provider correlation. A stale or unknown observation is not permission to retry or cancel. |
| `managed.lifecycle.unhealthy-endpoint` | Warning when degraded; Critical when unreachable or failed | Route to [incident response](incident-response.md) and follow the [unhealthy endpoint path](../product/managed-lifecycle-operations-runbook.md#unhealthy-endpoint). |
| General product question, documentation request, or bug confirmed to have no active incident | Routine (support triage only) | Use the agent-answer or product-issue route below. This is a documented support classification, not an API-emitted health severity or a new response/SLA promise. |

Use Routine only after confirming there is no active managed-instance incident.
If impact is uncertain, gather facts and follow incident response as needed;
never classify it as Routine just because an alert email is missing. Do not
infer a new runtime severity from a customer's description alone. The health
evaluator reports healthy only when desired lifecycle is `Running`, observed
lifecycle is `Ready`, health is `Healthy`, and no active work remains. See
[Status and stable codes](../product/managed-lifecycle-operations-runbook.md#status-and-stable-codes).
For other result diagnostic codes without a corresponding alert severity,
record the returned code and follow the existing runbook; this matrix assigns
no additional runtime severity. The runtime alert mapping remains Warning or
Critical as shown above; Routine applies only to support triage.

## Routing outcomes

Choose the route that owns the next action. The support record must capture the
route, next action, and reply status from the [minimal safe audit trail](#minimal-safe-audit-trail).

| Outcome | Use when | Next action and audit | Due date |
| --- | --- | --- | --- |
| **Agent answers** | The question can be answered from current, source-grounded product information and needs no owner decision or product change. | After #815 verifies the private route, send the answer there; for a public-safe question, reply on GitHub. Do not assume the support mailbox is monitored before verification. Record the reply time and brief outcome. | No separate answer deadline is defined here. The agent acknowledgement target still applies. |
| **Product issue filed** | A product defect or change is needed. | File a public-safe issue in [`valence-works/elsa-control`](https://github.com/valence-works/elsa-control/issues); record its URL and current next action. Keep customer-only details in an already approved private record, not in the public issue. | No product-fix deadline is defined here. |
| **Needs Sipke** | A reply or decision requires Sipke. | Required next action: add the item to the needs-Sipke queue and record its reference, next action, and visible due date. The actual queue system and reader are not verified here. | Next business day, Monday–Friday in Europe/Amsterdam. No time-of-day deadline or holiday calendar is defined. |

A product issue may be linked from a needs-Sipke item when both routes apply;
filing the issue does not remove the human follow-up due date.

### Proposed acknowledgement copy

> Thanks, we have received your Elsa Cloud support request. An agent will triage
> it. If it needs Sipke, you will receive a reply by [next business date], the
> next business day in Europe/Amsterdam.

This is proposed wording, not deployed or measured copy. It may be exercised
only within an explicitly authorized staging drill after inbox and agent access
are verified. This document does not authorize a customer-facing send, and the
template does not prove timed acknowledgement. No time-of-day deadline or
holiday calendar is defined.

## Next-business-day examples

Apply the local Europe/Amsterdam date, not the weekday in the sender's time
zone. The policy defines a date only; it does not define a time-of-day deadline
or a holiday calendar.

- A report received Friday 2026-11-06 at 23:30 UTC is Saturday 2026-11-07 at
  00:30 in Europe/Amsterdam. If it needs Sipke, show Monday 2026-11-09 as the
  due date under the Monday–Friday rule.
- A report received Sunday 2026-11-08 at 23:30 UTC is Monday 2026-11-09 at
  00:30 in Europe/Amsterdam. If it needs Sipke, show Tuesday 2026-11-10 as the
  due date under the same rule.

These examples apply the stated weekday rule. Do not infer an additional
holiday rule or add an exact reply time.

## Minimal safe audit trail

For each report, the support record should show:

- received time in UTC and intake channel;
- a private case reference or public issue URL, as appropriate;
- acknowledgement time and the agent role that acknowledged it;
- severity or issue category, using a safe diagnostic code when available;
- route, current next action, and the responsible role if one is actually
  assigned;
- a visible next-business-day due date when Sipke's response is needed; and
- reply time and a short outcome summary.

Keep customer identity, credentials, payment details, raw provider output,
secret values, and exception payloads out of public issues and out of this
minimal audit summary. Use only an already approved private record location;
this document does not establish one or set its retention period.

## Operational evidence still required

[#815](https://github.com/valence-works/elsa-control/issues/815) owns proof of
inbox and agent access, timed intake and acknowledgement on both current
channels, a visibly queued next-business-day due date, and the staging incident
drill. Do not describe the designated support address as a configured or
staffed mailbox, or describe the response target as measured, until that
evidence is recorded.
