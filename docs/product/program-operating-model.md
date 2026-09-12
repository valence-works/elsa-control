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

Issue Bus v1 ([`issue-bus.md`](issue-bus.md)) is the pickup, claim, and evidence protocol agents use to take Agent Ready tasks without an operator middleman. This section remains the program-level assignment policy; Issue Bus implements it on GitHub.

1. Select an unblocked Agent Ready task from the project.
2. Mark it assigned/in progress before implementation.
3. Provide one objective, issue, PRD/ADRs, repo boundaries, non-goals, acceptance criteria and validation.
4. Workers produce a branch and PR and report assumption-invalidating discoveries; they do not merge or expand into neighboring tasks.
5. Program lead reviews architecture/integration, runs or verifies checks, updates the board and merges according to repository policy.

## Pull Request Discipline

- One coherent task/slice per PR.
- Link the implemented issue; reference, but do not close, parent features/epics.
- State objective, architecture followed, changes, validation, discoveries and follow-ups.
- Require a clean high-priority self-review after the last fix.
- Prefer shared feature branches only for deliberately coordinated multi-slice delivery; planning/documentation work may land through a normal focused PR.

## Status Semantics

- `Backlog`: intentionally not ready or not scheduled.
- `Ready`: dependencies/decisions satisfied and candidate for dispatch.
- `In Progress`: actively assigned.
- `In Review`: PR/evidence awaiting review or required checks.
- `Blocked`: cannot progress without an external decision/dependency.
- `Done`: acceptance and closure verified.

Agent State is independent: `Not Ready`, `Agent Ready`, `Assigned`, `Review Required`.

## Program Cadence

No mandatory sprints or story points. Maintain the critical path, dependency facts, risks and milestone demonstrations continuously. Update the PRD only for material product requirement changes; use ADRs for expensive architectural choices and the decision log for smaller product/program policy.
