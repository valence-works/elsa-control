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

The repository-owned claim command is the only supported pickup path:

```bash
python3 scripts/issue_bus.py claim <issue-number> --worker codex
python3 scripts/issue_bus.py claim <issue-number> --worker claude
python3 scripts/issue_bus.py claim <issue-number> --worker cursor
```

Use `--dry-run` when inspecting a candidate without changing GitHub. Before
writing code, the command performs all of these preflight checks and hard-skips
the issue when any check fails:

1. The issue is open, is a leaf (`type:task`, `type:bug`, or `type:spike`), has `ready-for-agent`, has neither `blocked` nor `needs:decision`, is unassigned, has no active claim, and has no linked open PR.
2. The Project item exists with Status exactly `Ready` and Agent State exactly `Agent Ready`.
3. Every `worker:*` label is compatible with the requested lane. A lane label for another worker is a hard skip; an issue without a worker lane remains eligible to Codex and Claude.
4. The command obtains the authenticated `gh` login and posts exactly `claim: <worker> starting`. The immutable GitHub comment `node_id` returned by the authenticated API is the unique machine-readable lease identity.
5. The command re-reads the issue after the claim comment. The earliest active claim, ordered by `createdAt` and then comment id, wins. A losing session posts `claim-abandoned: <claim-comment-id>` and stops without touching assignment or claim-state fields.
6. A winning session assigns the authenticated login, then must complete all three claim-state mutations: remove `ready-for-agent`, set Project Status to `In Progress`, and set Agent State to `Assigned`. These are mandatory together; none is an alternative to another.
7. The command re-reads the issue and Project item after all three mutations. It only reports success when the lease, assignment, label removal, Status, and Agent State remain intact.
8. The worker takes only this one Task for the session.

An open pull request linked from the issue is always a preflight hard skip,
including when its worker lane appears available. Generic `worker:*` lanes are
data, not a hard-coded allow-list; every lane other than the requested lane is
conflicting.

If any claim mutation or permission check fails, implementation is forbidden. The command records `claim-abandoned` and reverses the mutations it can verify. If complete safe rollback is not possible, it follows the canonical blocked path: comment `blocked: claim failed - <reason>`, add `blocked`, set Project Status to `Blocked`, set Agent State to `Not Ready`, unassign if possible, and stop. Only an operator may requeue that issue by removing `blocked` and restoring `ready-for-agent`, Status `Ready`, and Agent State `Agent Ready`.

Arbitration is client-side and deterministic for v1. Each client re-reads and
selects the earliest active lease, then abandons a losing or partial claim.
Server-side serialization is deliberately deferred until a suitable
Project-capable GitHub App or organization token exists; this repository does
not provision credentials or an unusable hosted workflow.

An active claim expires after 15 minutes only while the issue still has
`ready-for-agent`, Project Status `Ready`, and Agent State `Agent Ready`; a
missing or malformed timestamp remains active. If an issue is assigned but has
no active claim and still shows that fully-ready state, it is an orphaned claim
from an interrupted session. Operator reconciliation must unassign it before
requeueing. The command never auto-unassigns an assignee before it has reserved
the issue, because that mutation is unsafe during a race.

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

## Drift checks

Control-room reconciliation can inspect one issue or the entire Project item
set without mutating GitHub:

```bash
python3 scripts/issue_bus.py drift 371
python3 scripts/issue_bus.py drift --all --json
```

Single-issue claim and drift reads use targeted GitHub REST requests: the issue
and paginated comments, the canonical linked pull request, and Project #7's
`fields` plus `items?q=<issue-number>&fields=<Status-id>,<Agent-State-id>`
endpoints. They never enumerate the board. Project mutations use the numeric
REST item/field IDs and one PATCH containing both claim-state field updates.
All these calls send `X-GitHub-Api-Version: 2026-03-10`. `drift --all` is the
explicit whole-board operation and uses the paginated Project REST items
adapter (100 items per page), then checks linked PRs only for rows carrying
`ready-for-agent`; it is intentionally not part of the claim path.

The check reports at least `ready-for-agent` with Status `In Progress`,
`ready-for-agent` with a linked open PR, and Status `Done` with Agent State
`Review Required`. It also reports multiple worker lanes and issues that
cannot be read safely. These checks cover the contradictory label, board, and
linked-PR state observed in the #371 collision.

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
| `claim: <worker> starting` (the immutable comment `node_id` is the lease identity) | Immediately on claim |
| `claim-abandoned: <claim-comment-id>` | When a previously posted claim loses collision resolution or no longer remains intact |
| `pr: <url>` | When the implementing PR is open |
| `blocked: <reason>` | When work cannot continue |

## Relationship to the operating model

- Definition of Ready still decides whether `ready-for-agent` may be applied.
- Definition of Done still decides whether the PR is mergeable.
- Worker Assignment still requires one objective, one PR, no merge by the worker, and program-lead review.
- Status semantics still apply: claimed work is `In Progress`; a posted PR is `In Review`; `blocked:` maps to `Blocked`; merge + verification is `Done`.
- The Elsa Commercial Platform project board (#7) is the source of truth for those Status values across Codex, Claude, and Cursor lanes.
