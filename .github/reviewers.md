# Advisory PR reviewers

This file is the single source of truth for advisory pull-request reviewers in
`valence-works/elsa-control`. Read it **before opening any PR**. Whoever
opens the PR (Codex control room or a Cursor/Claude worker) requests the
advisory review.

These reviewers are **advisory only**. They are not the merge gate. No
advisory reviewer's review, of any type, counts toward a merge or satisfies
the gate. Outside the explicitly authorized [availability fallback](#availability-fallback),
neither does a control-room review, PASS or HOLD, a `ready-for-CR` note,
green CI, or GitHub's `reviewDecision`/`APPROVED` state.

## Selection

1. On every PR it opens, whoever opens the PR requests a review from
   **exactly one** advisory reviewer marked live in the table below.
2. If only one reviewer is live, use that reviewer.
3. If more than one is live, rotate across PRs or pick by fit (for example,
   Copilot for a fast first pass, Greptile for whole-repo context, Bugbot for
   defect hunting). CodeRabbit reviews on its own, so while it is live it is
   the PR's advisory reviewer; trigger another only when one fits better.
4. If none is live, say so on the PR and proceed to the Elsa Control Code
   Review gate. Do not invent a substitute reviewer.
5. Codex, the control room, Cursor/Claude workers and advisory reviewers
   (Copilot, Greptile, Bugbot, CodeRabbit) may post QA notes or reviews, but these are
   never a Code Review verdict. The normal gate requires Elsa Control Code
   Review; the control room may use the availability fallback below. No agent
   other than Code Review may submit a GitHub `APPROVE` review or start a
   review with `**Verdict:`. If one does, treat it as void, ignore the
   lookalike, and report it.
6. A missing, slow or failed advisory review never blocks Code Review or a
   merge, and never substitutes for it. Advisory findings are input. Code
   Review decides which ones matter.

## Merge gate

On 14 Oct 2026 at 04:28 CEST, Sipke confirmed that the gate reviewer is the
Elsa Control Code Review GitHub App (`elsa-control-code-review[bot]`), and
that this app also merges. Stay consistent with the merge-authority comment
on [issue #508](https://github.com/valence-works/elsa-control/issues/508#issuecomment-5969355661)
(comment `5969355661`) and the standing human authority recorded on
[issue #508](https://github.com/valence-works/elsa-control/issues/508#issuecomment-5970160426)
(comment `5970160426`, Sipke, 3 Oct ~16:41 Europe/Amsterdam):

- The gate review is authored by `elsa-control-code-review[bot]`. A review
  by any other author never counts, whatever its first line says. The
  reviewer must not be the PR's author. No agent other than Code Review
  (control room, Codex, Cursor, Claude, advisory bots) may submit a GitHub
  `APPROVE` review or start a review with `**Verdict:`. If one does, treat
  it as void and report it.
- The **latest** Code Review verdict on the exact head SHA must start with
  `**Verdict: APPROVE**` as the first line. Line 2 carries the reviewed
  head SHA. A later CHANGES REQUESTED or BLOCKER on the same head
  supersedes it. Post-merge verdicts such as
  `**Verdict: APPROVE (post-merge)**` are not a merge gate. Match the first
  line exactly, not as a prefix. The GitHub review state is irrelevant:
  only the first line and the reviewed SHA count, whatever the review
  state. Nothing else counts. That includes reviews
  starting `## Control-room` / `Control-room`, any advisory-bot review
  including `APPROVED`, and any review on an earlier head. Nothing
  actionable may remain.
- A new push makes every earlier verdict stale; the new head needs
  re-review.
- Green CI (required-green) means the `CI` workflow run on the exact head
  SHA finished with every job `success`, including
  `Full .NET restore, build, and test`,
  `Build and smoke-test API provider image` and `Build and Test`. A running,
  cancelled or re-run-pending CI run is not green. Never use auto-merge.
- The Code Review app merges. Never merge through a personal account or by
  the PR author. Merge one PR at a time. Merge with the head pinned
  (`gh pr merge --match-head-commit <sha>`, or the REST merge `sha`
  parameter).
- If main has moved since the head's CI ran or since the latest Code
  Review verdict, update the branch first and re-stamp before merging.
  That creates a new head, which needs fresh green CI and a fresh
  exact-head review under the normal gate or the availability fallback
  before merging. This is the #674/#675 failure.
- Hand the advisory reviewer's findings to Elsa Control Code Review
  **before** it gives its verdict. They are input. Code Review decides
  which ones matter. A missing, slow or failed advisory review never
  blocks that verdict.
- Adequately reviewed means the latest Code Review verdict on that exact
  head is `**Verdict: APPROVE**` with the reviewed SHA on line 2 and
  nothing actionable remaining, or the control room has completed the
  explicitly authorized availability fallback below. Under the normal
  gate (approval on the exact head, nothing actionable left, required CI
  green), the Code Review app merges. Under the availability fallback,
  the control room merges. The Codex control room may still deploy
  staging without per-PR reconfirmation.
- Production publication, production configuration changes, live payment
  changes, and destructive actions against real customer resources still
  need Sipke's explicit approval.
- The Elsa Control CEO and CEO routines still need Sipke's explicit
  per-PR yes for merges **they** perform, until he confirms a standing
  rule for them in the CEO chat. That does not revoke the control room's
  standing merge-and-staging authority.
- Cursor and other non-control-room workers never merge, and never merge on
  a control-room PASS. Codex and Claude workers merge only under
  [Standing self-merge](#standing-self-merge-codex-and-claude).

The author or Cursor posts `ready-for-CR` naming the exact head once CI is
green. The CEO, or a CEO routine, routes that head to Code Review.

## Standing self-merge (Codex and Claude)

On 2026-10-20, Sipke extended the `elsa-workflows` standing self-merge rule
of 18 Oct 2026 to `valence-works` (decision log, 2026-10-20). The Codex and
Claude workers may merge their own PRs after a clean self-review loop. The
Elsa Control Code Review app is welcome but does not block these PRs, and
the author does not wait for it. The terms are those of
[Standing self-merge](https://github.com/elsa-workflows/.github/blob/main/AGENTS.md#standing-self-merge-codex-and-claude)
in the org AGENTS.md:

- Self-review loop: at most 6 rounds, a round comment on the PR after each
  round (reviewed head, findings fixed and dropped, test results). Fix
  actionable findings in new commits until a round finds nothing actionable.
  If blockers remain after round 6, comment `blocked: review loop exhausted`,
  add `needs:decision` and stop.
- Required CI (`CI`, every job `success`) is green on the latest head, and
  that head is the one the final round reviewed. Hand advisory findings
  (CodeRabbit, or Bugbot when it is live) into the loop; they are input, not a gate.
- No merge over a human hold, a changes-requested review, an open human
  review thread, or a `blocked` / `needs:decision` label. Never bypass branch
  protection. Never use auto-merge. Main freshness and head-pinned,
  one-at-a-time merges still apply.
- Before merging, add the `self-merged` label and post the audit comment the
  org rule names (rounds, findings fixed, remaining Low notes, reviewed head
  SHA). Write `self-merged under standing authorization: <comment url>` in
  the merge commit.
- Identity: Codex posts and merges as `valence-works-codex[bot]`. Claude has
  no app in `valence-works` yet, so it posts and merges through the owner's
  account with the `[Claude]` role tag, and only in sessions Sipke starts.
- Unchanged: production publication, production configuration, live payment
  changes and destructive actions against real customer resources still need
  Sipke's explicit approval. PRs that change `.github/workflows/` still need a
  maintainer with workflow rights to merge.

## Availability fallback

On 2026-10-06, Sipke authorized the Codex control room to replace the
dedicated gate whenever it is unavailable. The exact human authorization
is recorded on [issue #508](https://github.com/valence-works/elsa-control/issues/508#issuecomment-6014449305).
This standing exception takes precedence over the normal Code Review-only
requirements above and below; it does not make an advisory bot a gate.

- Record why the dedicated role is unavailable. Its availability cannot
  be inferred from a slow or missing advisory-bot response. An unresolved
  dedicated review finding is not unavailability and cannot be bypassed.
- The root Codex control room reviews the exact current head, performs up
  to five self-review/fix iterations per slice, and resolves material
  findings, including relevant advisory findings. Record the head,
  review findings and fixes, validation evidence, and remaining limitations
  on the PR. If material findings remain, do not merge.
- Required CI must be green on that same head. Main freshness and
  sequential head-pinned merges still apply. A changed head requires
  fresh review and CI; never use auto-merge.
- Label the assessment as a control-room availability fallback. Do not
  impersonate Code Review, use `**Verdict:`, or submit a GitHub `APPROVE`
  review. Ordinary workers and CEO routines gain no merge authority.
- Existing staging authority and separate production approvals remain
  unchanged. Merge or staging deployment does not prove a customer-facing
  acceptance criterion that still requires live evidence.
- Use the normal dedicated gate again when the role is available. No
  repeated human confirmation is needed for this availability fallback.

## Keep this list current

Whenever a reviewer is added, removed, enabled, or disabled, update this file
in the **same change**. Mark a reviewer live only with evidence (a review or
comment from that bot on this repo, a successful reviewer request, or a
confirmed dashboard enablement). Otherwise mark it `no — not verified / not
live` and state exactly what Sipke must enable.

## Reviewers

Checked 2026-10-04. CodeRabbit and Cursor Bugbot rechecked 2026-10-20.

| Reviewer | Live | How to invoke | Good for | How to tell it ran |
| --- | --- | --- | --- | --- |
| Greptile | no — not verified / not live (2026-10-04) | After Sipke installs the Greptile GitHub App and enables this repo in the Greptile dashboard, post a top-level PR comment `@greptileai` ([official trigger docs](https://www.greptile.com/docs/code-review-bot/trigger-code-review)). Optional: `@greptileai review the auth changes`. Do not treat adding a human or the control-room as a Greptile review. | Whole-repo context, summaries, inline comments, and suggested fixes. | A PR review and/or comments from `greptile-apps[bot]` (or the current Greptile GitHub App account) appear on the PR. |
| GitHub Copilot Code Review | no — not verified / not live (2026-10-04) | Request Copilot as a reviewer: GitHub UI Reviewers → Copilot → Request; `gh pr create --reviewer @copilot` / `gh pr edit <n> --add-reviewer @copilot`; or REST `requested_reviewers: ["copilot-pull-request-reviewer[bot]"]` ([official docs](https://docs.github.com/en/copilot/how-tos/agents/copilot-code-review/using-copilot-code-review)). | Fast first-pass comments with severity labels and suggested patches. Copilot reviews are advisory. No Copilot review satisfies the merge gate, whatever its type (Comment, Approve or Request changes). | A review from `copilot-pull-request-reviewer[bot]` appears, and the Reviewers sidebar shows Copilot completed. |
| Cursor Bugbot | **no — paused since 2026-10-20: Cursor usage limit reached.** Enabled for `valence-works/elsa-control` only on 2026-10-04 (Sipke, 03:14 Europe/Amsterdam, run only when mentioned). Every `cursor review` on 2026-10-20 (#866, #867, #876) got `Bugbot couldn't run - usage limit reached`. Not live in sibling repos (see below). | The PR author posts a **top-level** PR comment `cursor review` ([official docs](https://cursor.com/docs/bugbot)). `bugbot run` is the documented alias. Do not use `@cursor review`. Bugbot does not run on its own. **Every new head needs a fresh `cursor review`**, because a push does not re-run it. See [Cursor Bugbot rules](#cursor-bugbot-rules). | Defect, security, and edge-case findings with inline comments and suggested fixes. **Advisory only:** no Bugbot result, of any conclusion, satisfies the Elsa Control Code Review merge gate, which is unchanged. | A GitHub check named `Cursor Bugbot` (app `cursor`) on the head at trigger time, and/or a `cursor[bot]` review whose footer says "Reviewed by Cursor Bugbot for commit `<sha>`". The 👀 reaction is **not** proof. Neither is a `cursor[bot]` Issue Bus, `ready-for-CR`, or "Fixed in …" reply from a Cursor agent. |
| CodeRabbit | **yes — live in `valence-works/elsa-control` (2026-10-20)** | Nothing to post: it reviews when the PR opens and again on each push, stacked PRs included. Post a top-level `@coderabbitai review` only if automatic reviews are paused; otherwise it answers that it does not re-review commits it has already reviewed. Reply on its thread to accept or decline a finding; it answers, resolves the thread or withdraws the finding. | Line-level defects, configuration and operations pitfalls, and missed tests. **Advisory only:** no CodeRabbit result, of any type, satisfies the merge gate. | A `coderabbitai[bot]` review whose `commit_id` is the head, or a `coderabbitai[bot]` PR comment `No actionable comments were generated in the recent review` posted after the head was pushed. A clean pass posts only that comment, not a review. |

### Cursor Bugbot rules

- **Who triggers.** The PR author posts the trigger: the worker that opened
  the PR, or the CEO posting as `sfmskywalker`. This follows the
  one-advisory-reviewer rule in [Selection](#selection). Post one trigger per
  head; don't stack duplicate triggers.
- **Pinned to a commit.** A Bugbot result applies only to the SHA it
  reviewed: the `Cursor Bugbot` check's head SHA, or the commit in the
  review's "Reviewed by Cursor Bugbot for commit …" footer. The head can move
  mid-run, as it did on #710. After any push, post a fresh top-level
  `cursor review` if you want an advisory pass on the new head. Hand Code
  Review the findings together with the SHA they apply to.
- **What counts as having run.** Only a `Cursor Bugbot` check run (app
  `cursor`) or a Bugbot review or inline comments on that head. The
  `cursor[bot]` 👀 reaction on the trigger is transient: it disappeared from
  the #710 trigger after the run. It is not evidence. `cursor[bot]` is also
  the account Cursor cloud agents post under (`ready-for-CR`, Issue Bus,
  "Fixed in …" replies), so don't read the author alone as a Bugbot run.
- **Never a gate.** The `Cursor Bugbot` check is not, and must not become, a
  required status check, whatever its conclusion (neutral, failure, or
  absent). A missing, slow, failed, or unresponsive Bugbot run never blocks a
  merge. Note it on the PR and continue. The gate stays Elsa Control Code
  Review's `**Verdict: APPROVE**` on the exact head plus required-green CI,
  subject to the explicitly authorized availability fallback above
  (see [Merge gate](#merge-gate)).
- **Scope.** This row covers `valence-works/elsa-control` only. Bugbot's
  install, plan and cost, and enablement are unverified for `elsa-cloud`,
  `elsa-production-image` and `elsa-control-connector`, so treat it as not
  live there.

## Liveness evidence (2026-10-04)

Evidence used; nothing below is assumed live.

| Check | Result |
| --- | --- |
| `greptile.json`, `.greptile/`, `.github/copilot-instructions.md` | Absent on `main` (`22263253`, rechecked 2026-10-04). Other `valence-works` repos have `copilot-instructions.md`; this repo does not. That file is optional Copilot customization, not proof of Copilot code review. |
| GitHub issue/PR search for `commenter:greptile-apps`, `commenter:greptileai`, `commenter:copilot-pull-request-reviewer`, `reviewed-by:` those bots | Zero hits (rechecked 2026-10-04). |
| Reviews on the last 90 PRs (2026-10-04) | `sfmskywalker` plus `cursor[bot]` on #710 only: one Bugbot review on `d8dbbb73`, and two empty-body agent "Fixed in …" reply reviews on `2694cb43`. No `greptile-apps[bot]` or `copilot-pull-request-reviewer[bot]` review author. |
| Inline review comments | Apart from `sfmskywalker`, only `cursor[bot]` on #710: 2 Bugbot findings plus 2 agent replies. |
| Top-level PR comments | `cursor[bot]` posts Issue Bus / `ready-for-CR` notes from Cursor cloud agents. Those are not Bugbot runs. |
| Org/repo GitHub App installations | Not verifiable with available tokens. |
| Request Copilot (#688) | Not verifiable with available tokens. |
| Cursor Bugbot dashboard | 2026-10-04: Sipke enabled Bugbot for this repo (run only when mentioned). Evidence is the `Cursor Bugbot` run below. |
| Cursor Bugbot run (2026-10-04) | Top-level `cursor review` on #710 ([comment](https://github.com/valence-works/elsa-control/pull/710#issuecomment-5975374652)) → a `Cursor Bugbot` check ([check](https://github.com/valence-works/elsa-control/runs/111334807973), neutral) plus a `cursor[bot]` review ([review](https://github.com/valence-works/elsa-control/pull/710#pullrequestreview-5403768479)) with 2 inline findings on head `d8dbbb73` (the head at trigger time; `2694cb43` was pushed mid-run). There was no `Cursor Bugbot` check on `2694cb43`, which is consistent with mention-only mode. The 👀 reaction seen at trigger time had gone by 03:51 CEST (`reactions.total_count = 0`). |
| Sibling repos (2026-10-04) | `elsa-cloud`, `elsa-production-image` and `elsa-control-connector` are private and readable with the current token. Their latest PR heads (#142, #73, #3) have no `Cursor Bugbot` check. |

| CodeRabbit (2026-10-20) | `coderabbitai[bot]` ran on #866-#878 with no trigger. It left findings as reviews on #867, #868, #876 and #878 (for example [#878 on `02bd644c`](https://github.com/valence-works/elsa-control/pull/878#pullrequestreview-5591346291)) and `No actionable comments` comments on #866, #875 and stacked #877 (base `agent/claude/859-forwarded-headers`). It answered declined findings and withdrew or resolved them. |
| Cursor Bugbot (2026-10-20) | `cursor review` on #866, #867 and #876 each got a `cursor[bot]` comment `Bugbot couldn't run - usage limit reached`, for example [#866](https://github.com/valence-works/elsa-control/pull/866#issuecomment-6296675982). No `Cursor Bugbot` check ran. |

## What Sipke must enable

Until the matching evidence exists, keep each row not live.

1. **Greptile.** Install the Greptile GitHub App on `valence-works`, enable
   `elsa-control` in the Greptile dashboard, wait for indexing, then confirm a
   `greptile-apps[bot]` review on a PR. Then mark Greptile live in this file.
2. **GitHub Copilot Code Review.** As org owner, enable Copilot code review for
   `valence-works` / this repository (and a Copilot seat or the org-level
   no-license review setting). Confirm that requesting `@copilot` /
   `copilot-pull-request-reviewer[bot]` is accepted and that bot posts a
   review. Then mark Copilot live in this file.
3. **Cursor Bugbot.** Enabled 2026-10-04 (run only when mentioned), paused
   2026-10-20 by the Cursor usage limit. To restore it, raise the Cursor usage
   or spend limit, confirm a `Cursor Bugbot` check on a PR, then mark it live
   in this file again.

## Sibling repositories

Do not edit sibling repos from an `elsa-control` change. If Codex or a
Cursor/Claude worker opens PRs in `valence-works/elsa-cloud` or
`valence-works/elsa-production-image`, those repos need the same file and
AGENTS.md pointer. The #508 merge-authority comment already applies to
elsa-cloud. As of 2026-10-04 these repos (and `elsa-control-connector`) are
readable with the current token. They were not modified here, and no
advisory reviewer, Bugbot included, is verified live in them.
