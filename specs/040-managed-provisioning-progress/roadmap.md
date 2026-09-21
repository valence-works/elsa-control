# Roadmap: Managed Engine Provisioning Progress

Parent: [#529](https://github.com/valence-works/elsa-control/issues/529)

The parent remains a Feature. Each implementation slice becomes a separate Issue Bus leaf after roadmap approval. Slices are executed in order and use one scoped PR each.

## S1 — Control customer-safe provisioning progress

**Outcome**: Elsa Control exposes an authorized, provider-neutral, redacted Create progress snapshot and advertises its compatibility capability.

**Scope**: Progress models and mapping; server-side correlation; existing-store query; customer route; compatibility capability; monotonicity, terminal-history, authorization, topology-race, and redaction tests.

**Non-scope**: Cloud changes, raw provider messages, other lifecycle actions, or schema changes.

**Dependencies**: Approved PRD and plan only.

**Acceptance criteria**:

- The route returns the documented contract for queued, active, stale, Ready, failed, and unavailable Create shapes.
- Correlation uses scoped server records and never trusts browser provider/operation identity.
- Serialization contains none of the explicit sensitive fields or values.
- Cross-workspace and unauthorized reads fail safely.
- Compatibility advertises `hosted.instances.provisioning-progress.v1`.

**Validation**: Focused Azure provider, EF persistence, managed-instance API, and Cloud BFF authorization tests; `git diff --check`.

**Expected areas**: Deployment Core, Deployment Azure, EF persistence adapter, Control API, API/provider/persistence tests.

**Risks and rollback**: Incorrect mapping could misstate progress; exhaustive fixtures and monotonicity tests contain this. The additive endpoint/capability can be rolled back without changing engine-list behavior.

**GitHub mapping**: Create after roadmap approval; child of #529; Project #7; Type Task; target order 1.

## S2 — Elsa Cloud compact progress timeline

**Outcome**: Hosted customers see truthful Step N of 7 progress, elapsed time, and last update on the engine card.

**Scope**: Capability adoption; BFF action and runtime normalizer; Cloud API model; independent progress loading and foreground polling; compact timeline; contract and dashboard tests.

**Non-scope**: Expandable activity, raw provider messages, other lifecycle actions, or production proof.

**Dependencies**: S1 Control contract.

**Acceptance criteria**:

- The BFF returns only known progress fields and fails closed without the capability.
- A provisioning engine shows the correct current stage without delaying the list.
- Polling pauses while hidden, resumes on visibility, and stops at terminal state.
- Temporary progress failure preserves the engine and last good page-session snapshot.

**Validation**: BFF normalizer tests, API client tests, Dashboard tests, lint, build, desktop and 390 x 844 component check.

**Expected areas**: Cloud compatibility helper, Control BFF, API client, Dashboard, CSS, and tests.

**Risks and rollback**: Version skew could disable managed actions; release order and compatibility fixtures cover forward and rollback paths. Rollback restores the lifecycle label while Control's additive endpoint remains harmless.

**GitHub mapping**: Create after roadmap approval; child of #529; Project #7; Type Task; target order 2; PR in `valence-works/elsa-cloud` with reciprocal issue reference.

## S3 — Expandable activity and exceptional-state UX

**Outcome**: Customers inspect sanitized timestamped activity and understand stale or failed provisioning accessibly on desktop and mobile.

**Scope**: Collapsed activity disclosure; product copy; stale/failed/unavailable guidance; keyboard, live-region, reduced-motion, wrapping, and mobile behavior; regression tests.

**Non-scope**: Command input, raw logs, recovery mutations, or other lifecycle actions.

**Dependencies**: S2 timeline and normalized model.

**Acceptance criteria**:

- Activity is chronological, refresh-safe, product-owned, and contains no upstream message.
- RecoveryRequired does not animate as ordinary active work.
- Disclosure and stage updates are keyboard and screen-reader usable.
- Long activity remains contained at 390 x 844.

**Validation**: Dashboard tests, accessibility assertions, reduced-motion check, lint, build, desktop/mobile browser check.

**Expected areas**: Dashboard component/tests and CSS in `valence-works/elsa-cloud`.

**Risks and rollback**: Activity volume could overwhelm the card; collapsed default and bounded entries contain this. The disclosure can be removed independently while retaining S2.

**GitHub mapping**: Create after roadmap approval; child of #529; Project #7; Type Task; target order 3.

## S4 — Ordered rollout and live Azure proof

**Outcome**: Production visibly advances a real Hosted Create through Ready on exact reviewed Control and Cloud revisions.

**Scope**: Release-order documentation; compatibility smoke; exact-revision desktop/mobile proof; refresh, activity, Ready, network-redaction, and regression evidence; final tracking reconciliation.

**Non-scope**: New implementation discovered during rehearsal or unrelated billing, deletion, and identity changes.

**Dependencies**: S1-S3 merged and deployable; authorized Hosted account and Azure create.

**Acceptance criteria**:

- Control publishes capability before Cloud relies on it.
- Compatibility/version-skew smoke passes.
- Live Create shows accepted, at least two intermediate advances, refresh recovery, activity, and Ready.
- Captured browser responses contain no excluded sensitive data.
- Exact revisions and evidence are recorded, and cleanup uses the separately confirmed Delete path.

**Validation**: Production health/build checks, compatibility smoke, desktop/mobile browser proof, network payload inspection, final issue/board audit.

**Expected areas**: Cloud release documentation and GitHub evidence; no product code unless a defect returns to S1-S3.

**Risks and rollback**: Azure duration/cost and production availability. Reuse an authorized Hosted create, avoid duplicates, and use the separate confirmed Delete boundary for cleanup.

**GitHub mapping**: Create after roadmap approval; child of #529; Project #7; Type Task with `needs:live-proof`; target order 4.
