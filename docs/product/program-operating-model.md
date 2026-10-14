# Elsa Commercial Platform Program Operating Model

## Work Hierarchy

Use native GitHub sub-issues for `Program → Epic → Feature → Task`; Bugs, Spikes and ADRs may stand beside the hierarchy. `Part of`, `Blocked by` and `Blocks` body links remain useful for readable and cross-cutting relationships, but do not replace the native parent. Phase, area, priority and execution state are metadata, not extra hierarchy.

## Definition of Ready

A task is `Agent Ready` only when:

- objective, scope and non-goals are unambiguous;
- observable acceptance criteria and validation commands/flow exist;
- dependencies are complete or explicitly satisfied;
- governing PRD/ADR context is linked;
- affected repository boundaries are known;
- no product-owner decision blocks implementation;
- it fits one coherent, independently reviewable PR or evidence report.

## Definition of Done

Work is done only when applicable implementation, tests, integration/E2E evidence, documentation, contract updates and review are complete; the PR is coherent; governing ADRs remain satisfied; discoveries are captured; linked issue closure is verified; and parent/project status is updated. IaC compilation alone does not prove deployed behavior.

## Worker Assignment

[Issue Bus Lite](https://github.com/elsa-workflows/.github/blob/main/AGENTS.md#issue-bus-lite) (pointer: [`issue-bus.md`](issue-bus.md)) is the shared pickup, claim, and evidence protocol. Codex and Claude self-serve Agent Ready tasks; Cursor cloud agents are CEO/Launch Ops dispatched when `worker:cursor` is set. This section remains the program-level assignment policy; Issue Bus implements it on GitHub.

1. Pick an unblocked issue labelled `ready-for-agent` (plus the matching `worker:*` label) per Issue Bus Lite.
2. Post a `claim` comment on the issue before implementation.
3. Provide one objective, issue, PRD/ADRs, repo boundaries, non-goals, acceptance criteria and validation.
4. Workers produce a branch and PR and report assumption-invalidating discoveries; they do not merge or expand into neighboring tasks.
5. Program lead reviews architecture/integration, runs or verifies checks, and merges according to repository policy. Progress is recorded with labels and `claim` / `pr` / `blocked` issue comments.

## Pull Request Discipline

- One coherent task/slice per PR.
- Link the implemented issue; reference, but do not close, parent features/epics.
- State objective, architecture followed, changes, validation, discoveries and follow-ups.
- Require a clean high-priority self-review after the last fix.
- Prefer shared feature branches only for deliberately coordinated multi-slice delivery; planning/documentation work may land through a normal focused PR.

## Status Semantics

Status lives in labels and Issue Bus Lite comments: `ready-for-agent` means dispatchable, a `claim` comment means in progress, a `pr` comment means in review, a `blocked` comment means blocked, and closure means done.

The project board `Status` field (`Backlog`, `Ready`, `In Progress`, `In Review`, `Blocked`, `Done`) and `Agent State` field (`Not Ready`, `Agent Ready`, `Assigned`, `Review Required`) are optional views. Agents are not required to update them and they are not authoritative.

## Program Cadence

No mandatory sprints or story points. Maintain the critical path, dependency facts, risks and milestone demonstrations continuously. Update the PRD only for material product requirement changes; use ADRs for expensive architectural choices and the decision log for smaller product/program policy.
