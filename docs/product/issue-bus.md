# Elsa Control Issue Bus v1

This is the GitHub Issue Bus protocol for [`valence-works/elsa-control`](https://github.com/valence-works/elsa-control). Codex and Claude agents use it to pick, claim, and finish leaf work without an operator middleman. Cursor cloud agents follow the same claim, PR, comment, and board rules as a peer worker lane; CEO / Launch Ops dispatches them.

Issue Bus **extends** the program operating model. It does not replace Definition of Ready, Definition of Done, Worker Assignment, pull-request discipline, or status semantics in [`program-operating-model.md`](program-operating-model.md).

## Purpose

- Codex and Claude auto-pick the oldest unblocked Agent Ready leaf issue after standing prompts.
- Cursor cloud agents do not self-poll GitHub search. CEO / Launch Ops launches a Cursor session only for an issue that already has `ready-for-agent` and `worker:cursor`.
- One agent session owns one Task and opens one PR.
- Claim, progress, and blockage are visible on the issue so other agents skip it.
- Features and Epics stay with program lead and operators. Agents never take them.

## Pickup

Canonical repository query, oldest first:

```text
repo:valence-works/elsa-control is:issue is:open label:ready-for-agent -label:blocked -label:"needs:decision" sort:created-asc
```

This query is authoritative for repository-local labels and issue openness. Project Status and Agent State remain required preflight checks.

Optional worker-lane preference filters when the session is bound to one implementer:

- Codex: first try `label:worker:codex`; if nothing is ready, retry with `-label:worker:claude -label:worker:cursor`
- Claude: first try `label:worker:claude`; if nothing is ready, retry with `-label:worker:codex -label:worker:cursor`
- Cursor: not a self-serve search. CEO / Launch Ops is the dispatcher and launches a Cursor cloud agent only when the issue already has `ready-for-agent` + `worker:cursor`. Do not launch speculative Cursor agents without those labels.

If the session is lane-bound, prefer a matching lane issue. Do not take an issue labeled for another worker. An issue with `ready-for-agent` and no `worker:*` label is available to Codex or Claude. It is not a Cursor dispatch target.

Skip an issue that does not have project Status `Ready` and Agent State `Agent Ready`, is already assigned, or is a Feature, Epic, or Program. Treat a `claim:` comment as active only until a later `claim-abandoned: <claim-comment-id>` for that claim, a later `blocked:` comment, claim expiry (15 minutes with no removal of `ready-for-agent`, no Status move to `In Progress`, and no Agent State move to `Assigned`), or until an operator removes `blocked` and restores `ready-for-agent`, Status `Ready`, and Agent State `Agent Ready` for requeue. `type:task` is the usual leaf, and a `type:bug` or `type:spike` that satisfies the same readiness checks is also valid.

## Project board

The GitHub org project [Elsa Commercial Platform](https://github.com/orgs/valence-works/projects/7) (#7) is the source of truth for execution state. All worker lanes (`worker:codex`, `worker:claude`, `worker:cursor`) keep it updated.

Status values: `Backlog`, `Ready`, `In Progress`, `In Review`, `Blocked`, `Done`.

Views that dispatchers and workers must honor: Execution, Agent Queue, Blocked.

Required Status transitions for every lane:

- Claim: `In Progress` (Agent State `Assigned`)
- PR open: `In Review` (Agent State `Review Required`); link the claimed issue on the PR
- Merge + verification: `Done`; remove `ready-for-agent` if it is still present
- Mid-flight stop: `Blocked` (Agent State `Not Ready`)

## Claim

Before writing code:

1. Confirm the issue still matches the pickup query (or, for Cursor, that CEO / Launch Ops dispatched this session for a `ready-for-agent` + `worker:cursor` issue), is unassigned, and has no active `claim:` comment.
2. Self-assign the issue to the agent identity used for this session. For Cursor, the agent or CEO on its behalf may complete the claim mutations.
3. Comment exactly `claim: <codex|claude|cursor> starting`.
4. Re-read the issue before any further mutation. The earliest active `claim:` comment wins; a losing session must comment `claim-abandoned: <claim-comment-id>`, unassign itself if needed, and stop before changing issue state or writing code.
5. Remove `ready-for-agent`, move project Status to `In Progress`, and set Agent State to `Assigned` so other agents skip it. Prefer this over leaving `ready-for-agent` on a claimed issue.
6. Re-read the issue after all claim mutations. If your claim state is no longer intact, comment `claim-abandoned: <claim-comment-id>`, unassign yourself if needed, and stop.
7. Take only this one Task for the session.

If any claim mutation fails (assignment, claim comment, label removal, Status update, or Agent State update), do not start work. Revert the claim completely if you can. Otherwise comment `blocked: claim failed - <reason>`, add `blocked`, move project Status to `Blocked`, set Agent State to `Not Ready`, unassign if possible, and stop. Only an operator may requeue that issue by removing `blocked` and restoring `ready-for-agent`, Status `Ready`, and Agent State `Agent Ready`.

If an issue is assigned but has no active `claim:` comment and still shows `ready-for-agent`, Status `Ready`, and Agent State `Agent Ready` for 15 minutes, treat it as an orphaned claim from an interrupted session: unassign it, then continue the claim flow from step 1. This recovery applies only when no session completed the failure path above.

## While in flight

- Do not expand into neighboring Tasks, parent Features, or Epics.
- Do not merge the PR.
- Report assumption-invalidating discoveries on the issue.

If blocked mid-flight:

1. Comment `blocked: <reason>`.
2. Add the `blocked` label, move project Status to `Blocked`, and set Agent State to `Not Ready`.
3. Unassign.
4. Stop. Do not keep the branch as an implicit claim.

## Delivery

- One PR per claimed Task.
- The PR body uses `Fixes #<task>` for the claimed leaf issue only. Reference parent Features and Epics (`Part of #…`, `Refs #…`) but never `Fixes` or `Closes` a parent Feature or Epic.
- After the PR exists, link the claimed issue, move project Status to `In Review`, set Agent State to `Review Required`, and comment `pr: <url>` on the claimed issue.
- Attach or describe the validation evidence required by the Task and by Definition of Done.
- After merge and verification, program lead or operator moves project Status to `Done` and removes `ready-for-agent` if it is still present. Workers do not merge.

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
| CEO / Launch Ops | Priority, outcome, and worker lane | Priority, `ready-for-agent`, initial project Status `Ready`, initial Agent State `Agent Ready`, and `worker:codex`, `worker:claude`, or `worker:cursor` when a lane is required. For `worker:cursor`, also dispatch the Cursor cloud agent and, if needed, post the claim on its behalf |
| Sipke (operator) | Operator steps only | Secrets, live-account actions, and environment facts agents cannot obtain |
| Agents | Protocol comments and claim-state mutations | Self-assignment, `ready-for-agent` removal, Status/Agent State transitions for claim/block/review, and `pr:` evidence. Agents do not set `ready-for-agent` or priority |

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
- Repository boundaries:
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
| `type:bug` | Implementation-ready bug fix work unit |
| `type:spike` | Implementation-ready investigation work unit |
| `worker:codex` | Preferred implementer: Codex |
| `worker:claude` | Preferred implementer: Claude |
| `worker:cursor` | Preferred implementer: Cursor cloud agent (CEO-launched via Cursor Cloud Agents; same Issue Bus claim/PR/comment rules as other workers) |

## Protocol comments

Machine-readable comments on the claimed issue:

| Comment | When |
|---------|------|
| `claim: codex starting`, `claim: claude starting`, or `claim: cursor starting` | Immediately on claim |
| `claim-abandoned: <claim-comment-id>` | When a previously posted claim loses collision resolution or no longer remains intact |
| `pr: <url>` | When the implementing PR is open |
| `blocked: <reason>` | When work cannot continue |

## Relationship to the operating model

- Definition of Ready still decides whether `ready-for-agent` may be applied.
- Definition of Done still decides whether the PR is mergeable.
- Worker Assignment still requires one objective, one PR, no merge by the worker, and program-lead review.
- Status semantics still apply: claimed work is `In Progress`; a posted PR is `In Review`; `blocked:` maps to `Blocked`; merge + verification is `Done`.
- The Elsa Commercial Platform project board (#7) is the source of truth for those Status values across Codex, Claude, and Cursor lanes.
