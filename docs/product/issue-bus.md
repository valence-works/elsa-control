# Elsa Control Issue Bus v1

This is the GitHub Issue Bus protocol for [`valence-works/elsa-control`](https://github.com/valence-works/elsa-control). Codex and Claude agents use it to pick, claim, and finish leaf work without an operator middleman.

Issue Bus **extends** the program operating model. It does not replace Definition of Ready, Definition of Done, Worker Assignment, pull-request discipline, or status semantics in [`program-operating-model.md`](program-operating-model.md).

## Purpose

- Agents auto-pick the oldest unblocked Agent Ready leaf issue.
- One agent session owns one Task and opens one PR.
- Claim, progress, and blockage are visible on the issue so other agents skip it.
- Features and Epics stay with program lead and operators. Agents never take them.

## Pickup

Canonical query, oldest first:

```text
repo:valence-works/elsa-control is:issue is:open label:ready-for-agent -label:blocked -label:needs:decision sort:created-asc
```

Optional worker-lane filters when the session is bound to one implementer:

- Codex: add `label:worker:codex`
- Claude: add `label:worker:claude`

If the session is lane-bound, prefer a matching lane issue. Do not take an issue labeled for the other worker. An issue with `ready-for-agent` and no `worker:*` label is available to either worker.

Skip an issue that is already assigned, already has a `claim:` comment, or is a Feature, Epic, or Program. The pickup query is authoritative for eligibility; `type:task` is the usual leaf, and a `type:bug` or `type:spike` that matches the query is also valid.

## Claim

Before writing code:

1. Confirm the issue still matches the pickup query and is unassigned.
2. Self-assign the issue to the agent identity used for this session.
3. Comment exactly `claim: <codex|claude> starting`.
4. Remove `ready-for-agent` and move project Status to `In Progress` so other agents skip it. Prefer this over leaving `ready-for-agent` on a claimed issue.
5. Take only this one Task for the session.

If assignment or the claim comment fails, do not start work.

## While in flight

- Do not expand into neighboring Tasks, parent Features, or Epics.
- Do not merge the PR.
- Report assumption-invalidating discoveries on the issue.

If blocked mid-flight:

1. Comment `blocked: <reason>`.
2. Add the `blocked` label.
3. Unassign.
4. Stop. Do not keep the branch as an implicit claim.

## Delivery

- One PR per claimed Task.
- The PR body uses `Fixes #<task>` for the claimed leaf issue only. Reference parent Features and Epics (`Part of #…`, `Refs #…`) but never `Fixes` or `Closes` a parent Feature or Epic.
- After the PR exists, comment `pr: <url>` on the claimed issue.
- Attach or describe the validation evidence required by the Task and by Definition of Done.

## Hard skips

Never pick up or continue:

- Features, Epics, Programs, or other parent issues
- `blocked`
- `needs:decision`
- Issues that fail Definition of Ready in [`program-operating-model.md`](program-operating-model.md)
- A second Task in the same session

`needs:live-proof` is not an automatic skip. Take it only when the session can run the required live proof; otherwise leave it for an operator or a later session.

## Who writes tickets

| Role | Writes | Marks |
|------|--------|-------|
| CEO / Launch Ops | Priority, outcome, and worker lane | Priority, `ready-for-agent`, and `worker:codex` or `worker:claude` when a lane is required |
| Sipke (operator) | Operator steps only | Secrets, live-account actions, and environment facts agents cannot obtain |
| Agents | Protocol comments only | Claim → PR → `pr:` evidence. Agents do not set `ready-for-agent` or priority |

Agents do not invent a new Agent Ready ticket as a substitute for claiming an existing one.

## Issue body template

New Agent Ready Tasks use this body. Existing tickets that already meet Definition of Ready need not be rewritten solely to match headings.

```markdown
## Objective

What done looks like in one or two sentences.

## Scope

- In-scope work.

## Non-goals

- Adjacent work that must not land in this PR.

## Acceptance criteria

- [ ] Observable, reviewable outcomes.

## Validation

Commands, tests, or flows that prove the acceptance criteria.

## Context

- PRD:
- ADR:
- Parent Feature/Epic:
- Related issues:

## Operator steps

Sipke only. Secrets, live subscriptions, portal clicks, or credentials. Leave empty or `None` when the agent can finish unattended.

## Out of scope

Work that belongs to another Task or a later slice.
```

## Labels

These labels already exist. Do not recreate them.

| Label | Meaning |
|-------|---------|
| `ready-for-agent` | Meets Definition of Ready; eligible for pickup |
| `blocked` | Cannot proceed; agents must skip or stop |
| `needs:decision` | Operator, product, or legal decision required |
| `needs:live-proof` | Requires a live Azure or external-account proof run |
| `size:S` / `size:M` / `size:L` | Effort band |
| `type:task` | Implementation-ready work unit |
| `worker:codex` | Preferred implementer: Codex |
| `worker:claude` | Preferred implementer: Claude |

## Protocol comments

Machine-readable comments on the claimed issue:

| Comment | When |
|---------|------|
| `claim: codex starting` or `claim: claude starting` | Immediately on claim |
| `pr: <url>` | When the implementing PR is open |
| `blocked: <reason>` | When work cannot continue |

## Relationship to the operating model

- Definition of Ready still decides whether `ready-for-agent` may be applied.
- Definition of Done still decides whether the PR is mergeable.
- Worker Assignment still requires one objective, one PR, no merge by the worker, and program-lead review.
- Status semantics still apply: claimed work is `In Progress`; a posted PR is `In Review`; `blocked:` maps to `Blocked`.
