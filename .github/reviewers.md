# Advisory PR reviewers

This file is the single source of truth for advisory pull-request reviewers in
`valence-works/elsa-control`. The Codex control-room must read it **before
opening any PR**.

These reviewers are **advisory only**. They are not the merge gate.

## Selection

1. On every PR it opens, Codex requests a review from **exactly one** advisory
   reviewer marked live in the table below.
2. If only one reviewer is live, use that reviewer.
3. If more than one is live, rotate across PRs or pick by fit (for example,
   Copilot for a fast first pass, Greptile for whole-repo context, Bugbot for
   defect hunting).
4. If none is live, say so on the PR and proceed to the Elsa Control Code
   Review gate. Do not invent a substitute reviewer.
5. Codex never reviews or approves its own PRs.

## Merge gate

Stay consistent with the merge-authority comment on
[issue #508](https://github.com/valence-works/elsa-control/issues/508#issuecomment-5969355661)
(comment `5969355661`):

- A PR may merge only with an **APPROVE from the separate Elsa Control Code
  Review agent** posted as a GitHub PR review on the **exact head SHA**, plus
  **green required CI on that SHA**.
- The control-room's own review never counts as approval.
- A push after an APPROVE makes that approval stale; the new head needs
  re-review.
- Hand the advisory reviewer's findings to Elsa Control Code Review **before**
  it gives its verdict.
- Merges also need Sipke's explicit yes unless and until he has confirmed a
  standing rule in the CEO chat.
- Deploys, live billing, and destructive actions always need his explicit yes.

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
| GitHub Copilot Code Review | no — not verified / not live (2026-10-03) | Request Copilot as a reviewer: GitHub UI Reviewers → Copilot → Request; `gh pr create --reviewer @copilot` / `gh pr edit <n> --add-reviewer @copilot`; or REST `requested_reviewers: ["copilot-pull-request-reviewer[bot]"]` ([official docs](https://docs.github.com/en/copilot/how-tos/agents/copilot-code-review/using-copilot-code-review)). | Fast first-pass comments with severity labels and suggested patches. Default Copilot reviews are Comment reviews and do not satisfy the merge gate. | A review from `copilot-pull-request-reviewer[bot]` appears, and the Reviewers sidebar shows Copilot completed. |
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
| Org/repo GitHub App installations | `GET /orgs/valence-works/installations` returned 403 (`Resource not accessible by integration`). `GET /repos/valence-works/elsa-control/installation` returned 401. Could not list whether Greptile or Copilot apps are installed. |
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

Do not edit sibling repos from an `elsa-control` change. If Codex opens PRs in
`valence-works/elsa-cloud` or `valence-works/elsa-production-image`, those
repos need the same file and AGENTS.md pointer. The #508 merge-authority
comment already applies to elsa-cloud. This environment received 404 for both
sibling repos, so they were not modified here.
