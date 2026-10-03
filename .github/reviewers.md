# Advisory PR reviewers

This file is the single source of truth for advisory pull-request reviewers in
`valence-works/elsa-control`. Read it **before opening any PR**. Whoever
opens the PR (Codex control room or a Cursor/Claude worker) requests the
advisory review.

These reviewers are **advisory only**. They are not the merge gate. No
advisory reviewer's review, of any type, counts toward a merge or satisfies
the gate. Neither does a control-room review, PASS or HOLD, a `ready-for-CR`
note, green CI, or GitHub's `reviewDecision`/`APPROVED` state.

## Selection

1. On every PR it opens, whoever opens the PR requests a review from
   **exactly one** advisory reviewer marked live in the table below.
2. If only one reviewer is live, use that reviewer.
3. If more than one is live, rotate across PRs or pick by fit (for example,
   Copilot for a fast first pass, Greptile for whole-repo context, Bugbot for
   defect hunting).
4. If none is live, say so on the PR and proceed to the Elsa Control Code
   Review gate. Do not invent a substitute reviewer.
5. Codex and the control room may post QA notes or reviews, but these are
   never approval. They must never submit a GitHub `APPROVE` review or post a
   review whose first line is `**Verdict:`.
6. A missing, slow or failed advisory review never blocks Code Review or a
   merge, and never substitutes for it. Advisory findings are input. Code
   Review decides which ones matter.

## Merge gate

Stay consistent with the merge-authority comment on
[issue #508](https://github.com/valence-works/elsa-control/issues/508#issuecomment-5969355661)
(comment `5969355661`):

- The gate is a PR review on the exact head SHA whose first line is
  `**Verdict: APPROVE**`, posted by Elsa Control Code Review. The GitHub
  review state is irrelevant: these are COMMENT reviews. Nothing else
  counts. That includes reviews starting `## Control-room` / `Control-room`,
  any advisory-bot review including `APPROVED`, and any review on an
  earlier head.
- Green CI means the `CI` workflow run on the exact head SHA finished with
  every job `success`, including `Full .NET restore, build, and test`,
  `Build and smoke-test API provider image` and `Build and Test`. A running,
  cancelled or re-run-pending CI run is not green. Never use auto-merge.
- A push after a `**Verdict: APPROVE**` makes that verdict stale; the new
  head needs re-review.
- Hand the advisory reviewer's findings to Elsa Control Code Review
  **before** it gives its verdict. They are input. Code Review decides
  which ones matter. A missing, slow or failed advisory review never
  blocks that verdict.
- Merges also need Sipke's explicit yes unless and until he has confirmed
  a standing rule in the CEO chat.
- The Elsa Control CEO merges after Code Review's `**Verdict: APPROVE**` on
  the exact head plus Sipke's per-PR yes. The control room, Codex, Cursor
  and other workers never merge, and never merge on a control-room PASS.
- Deploys, live billing, and destructive actions always need his explicit
  yes.

The author or Cursor posts `ready-for-CR` naming the exact head once CI is
green. The CEO, or a CEO routine, routes that head to Code Review.

## Keep this list current

Whenever a reviewer is added, removed, enabled, or disabled, update this file
in the **same change**. Mark a reviewer live only with evidence (a review or
comment from that bot on this repo, a successful reviewer request, or a
confirmed dashboard enablement). Otherwise mark it `no — not verified / not
live` and state exactly what Sipke must enable.

## Reviewers

Checked 2026-10-03.

| Reviewer | Live | How to invoke | Good for | How to tell it ran |
| --- | --- | --- | --- | --- |
| Greptile | no — not verified / not live (2026-10-03) | After Sipke installs the Greptile GitHub App and enables this repo in the Greptile dashboard, post a top-level PR comment `@greptileai` ([official trigger docs](https://www.greptile.com/docs/code-review-bot/trigger-code-review)). Optional: `@greptileai review the auth changes`. Do not treat adding a human or the control-room as a Greptile review. | Whole-repo context, summaries, inline comments, and suggested fixes. | A PR review and/or comments from `greptile-apps[bot]` (or the current Greptile GitHub App account) appear on the PR. |
| GitHub Copilot Code Review | no — not verified / not live (2026-10-03) | Request Copilot as a reviewer: GitHub UI Reviewers → Copilot → Request; `gh pr create --reviewer @copilot` / `gh pr edit <n> --add-reviewer @copilot`; or REST `requested_reviewers: ["copilot-pull-request-reviewer[bot]"]` ([official docs](https://docs.github.com/en/copilot/how-tos/agents/copilot-code-review/using-copilot-code-review)). | Fast first-pass comments with severity labels and suggested patches. Copilot reviews are advisory. No Copilot review satisfies the merge gate, whatever its type (Comment, Approve or Request changes). | A review from `copilot-pull-request-reviewer[bot]` appears, and the Reviewers sidebar shows Copilot completed. |
| Cursor Bugbot | no — not live (2026-10-03). Sipke has not enabled it in the Cursor dashboard. | After Sipke enables Bugbot for this repo at [cursor.com/dashboard](https://cursor.com/dashboard) (Integrations, then Bugbot under Automations), post a **top-level** PR comment `cursor review` ([official docs](https://cursor.com/docs/bugbot)). `bugbot run` is the documented alias. Do not use `@cursor review`. | Defect, security, and edge-case findings with inline comments and suggested fixes. | Inline comments plus a GitHub check named `Cursor Bugbot`. A `cursor[bot]` Issue Bus or `ready-for-CR` comment is **not** a Bugbot review. |

## Liveness evidence (2026-10-03)

Evidence used; nothing below is assumed live.

| Check | Result |
| --- | --- |
| `greptile.json`, `.greptile/`, `.github/copilot-instructions.md` | Absent on `main` (`4b714c97`). Other `valence-works` repos have `copilot-instructions.md`; this repo does not. That file is optional Copilot customization, not proof of Copilot code review. |
| GitHub issue/PR search for `commenter:greptile-apps`, `commenter:greptileai`, `commenter:copilot-pull-request-reviewer`, `reviewed-by:` those bots | Zero hits. |
| Reviews on the last ~90 PRs | Only `sfmskywalker`. No `greptile-apps[bot]`, `copilot-pull-request-reviewer[bot]`, or Bugbot review author. |
| Inline review comments on a 40-PR sample | No non-Sipke review-comment authors. |
| Top-level PR comments | `cursor[bot]` posts Issue Bus / `ready-for-CR` notes from Cursor cloud agents. Those are not Bugbot runs. |
| Org/repo GitHub App installations | Not verifiable with available tokens. |
| Request Copilot on this PR (#688) | Not verifiable with available tokens. |
| Cursor Bugbot dashboard | Sipke has not enabled Bugbot for this repo. Official enablement is dashboard-only; this environment cannot flip it. |

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
3. **Cursor Bugbot.** In [cursor.com/dashboard](https://cursor.com/dashboard),
   connect GitHub under Integrations and enable Bugbot for
   `valence-works/elsa-control` under Automations. Confirm that a top-level
   `cursor review` produces a `Cursor Bugbot` check and inline findings. Then
   mark Bugbot live in this file.

## Sibling repositories

Do not edit sibling repos from an `elsa-control` change. If Codex or a
Cursor/Claude worker opens PRs in `valence-works/elsa-cloud` or
`valence-works/elsa-production-image`, those repos need the same file and
AGENTS.md pointer. The #508 merge-authority comment already applies to
elsa-cloud. This environment received 404 for both sibling repos, so they
were not modified here.
