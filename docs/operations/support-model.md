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
3. Route reports about a managed instance's failed, degraded, stale, or
   uncertain state through the [incident response procedure](incident-response.md).
   Use the stable health and alert codes in the [managed lifecycle runbook](../product/managed-lifecycle-operations-runbook.md#status-and-stable-codes)
   to describe what is known.
4. Route a general product question or public-safe bug to the appropriate
   support channel above. Do not put credentials, customer-only details, raw
   logs, or provider identifiers in a public issue.
5. If a response or decision needs Sipke, place it in the needs-Sipke queue
   with a visible **due date** on the next business day, Monday through Friday
   in Europe/Amsterdam. Keep the queue and its reader as readiness gaps until
   the live workflow is demonstrated; this document does not name a queue
   system or assign a person to read it.

The 15-minute target is the agent acknowledgement. It does not create a
one-hour human response promise, an on-call rota, or a 24/7 human service.
There is no support credit commitment here.

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
