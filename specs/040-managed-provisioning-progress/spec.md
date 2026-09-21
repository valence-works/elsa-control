# Feature Specification: Managed Engine Provisioning Progress

**Feature Branch**: `codex/529-provisioning-progress`

**Created**: 2026-09-21

**Status**: Draft for product approval

**Input**: GitHub issue [#529](https://github.com/valence-works/elsa-control/issues/529): show truthful managed-engine provisioning progress and sanitized activity in Elsa Cloud.

## Problem Statement

Creating a managed engine can take many minutes. During that time Elsa Cloud currently presents a single `Provisioning` state, so a customer cannot distinguish a healthy deployment that is advancing from a stalled or failed request. The lack of visible progress reduces trust, invites repeated actions, and creates avoidable support questions.

Elsa Control already observes ordered provider progress. Elsa Cloud needs a customer-safe view of that progress without exposing Azure internals, secrets, raw logs, or implementation-specific identifiers.

## Goals

- Give a customer immediate, truthful feedback after an engine create request is accepted.
- Show the current provisioning stage and the stages already completed.
- Preserve the same progress history across refreshes and sign-in sessions.
- Offer an expandable, terminal-style activity view using sanitized product-owned messages.
- Explain delayed, failed, and completed provisioning states with a safe next action.
- Keep the customer contract provider-neutral enough to support providers beyond Azure.

## Non-Goals

- Streaming raw shell, Azure CLI, ARM, platform, or application logs.
- Showing a percentage or completion-time estimate before reliable historical estimates exist.
- Exposing provider resource identifiers, deployment internals, credentials, secrets, or unrestricted diagnostic messages.
- Adding progress experiences for update, repair, or delete operations in this feature. The contract may accommodate those actions later, but Create is the only required journey.
- Replacing the advanced Control operations experience.

## Personas and Actors

- **Hosted customer**: Creates a managed Elsa engine and wants confidence that provisioning is advancing.
- **Customer administrator**: Refreshes or returns later and needs the authoritative state and history for the workspace's engine.
- **Support operator**: Uses existing advanced operational tools. This feature does not broaden the customer's access to those tools.

## User Scenarios & Testing

### User Story 1 - Follow provisioning at a glance (Priority: P1)

After creating an engine, a Hosted customer sees a compact ordered timeline showing what has completed, what is happening now, and what remains.

**Why this priority**: The timeline directly resolves the uncertainty caused by a static `Provisioning` label and is the minimum valuable first-public experience.

**Independent Test**: Start a managed-engine create and verify that the card immediately shows `Request accepted`, then advances through durable stages to `Ready` without showing a percentage.

**Acceptance Scenarios**:

1. **Given** a valid create request has been accepted, **when** the dashboard renders the engine, **then** the timeline identifies `Request accepted` as completed or current and shows an active indicator.
2. **Given** the provider reports a later known phase, **when** Elsa Cloud obtains the next progress snapshot, **then** the corresponding customer stage becomes current and earlier stages remain completed.
3. **Given** no new phase has been reported, **when** the dashboard refreshes, **then** it preserves the authoritative current stage and does not simulate advancement.
4. **Given** provisioning reaches Ready, **when** the terminal snapshot is obtained, **then** every completed stage remains visible and the engine's existing Ready action becomes available.

---

### User Story 2 - Inspect sanitized activity (Priority: P2)

A customer can expand the engine card to inspect a timestamped, terminal-style activity history written in customer language.

**Why this priority**: Activity history builds confidence during long stages and gives the customer useful context without exposing provider logs.

**Independent Test**: Expand an actively provisioning engine and verify that ordered sanitized events are shown, then compare the same history after a full page refresh.

**Acceptance Scenarios**:

1. **Given** at least one provisioning event exists, **when** the customer expands activity, **then** events appear in stable chronological order with a time and product-owned message.
2. **Given** provider events contain internal identifiers or arbitrary diagnostic text, **when** activity is returned to the browser, **then** only allowlisted stage data and customer-safe copy are present.
3. **Given** the page is refreshed or reopened, **when** the same engine loads, **then** its durable activity history is reconstructed without relying on browser storage.

---

### User Story 3 - Understand delay or failure (Priority: P2)

A customer can tell when provisioning is waiting, no longer receiving expected updates, or has failed, and sees a safe next action.

**Why this priority**: Truthful exceptional states prevent an indefinitely animated experience and make failures actionable.

**Independent Test**: Present queued, server-declared stale, and failed snapshots and verify that each renders distinct copy, preserves completed history, and offers the expected retry or support path.

**Acceptance Scenarios**:

1. **Given** work is accepted but has not started, **when** the dashboard renders it, **then** it distinguishes queued or waiting work from active provider work.
2. **Given** the server identifies an active operation as stale, **when** the dashboard renders it, **then** the current stage remains visible and the UI explains that the latest update is delayed.
3. **Given** provisioning fails, **when** the terminal snapshot is obtained, **then** completed history remains visible, the failed stage is identified, and only safe diagnostic guidance is shown.
4. **Given** progress data is temporarily unavailable, **when** the engine itself is still known, **then** Elsa Cloud keeps the engine visible and shows a recoverable progress-unavailable state.

---

### User Story 4 - Use progress accessibly on small screens (Priority: P3)

A customer can understand and operate the progress experience on desktop, keyboard, assistive technology, and a 390 x 844 mobile viewport.

**Why this priority**: The Hosted dashboard is already used on mobile, and long activity text must not break the engine card.

**Independent Test**: Exercise the timeline and activity disclosure at desktop and 390 x 844, using keyboard navigation and screen-reader semantics.

**Acceptance Scenarios**:

1. **Given** a 390 x 844 viewport, **when** the customer views or expands progress, **then** stages and activity remain readable without page-level horizontal scrolling.
2. **Given** keyboard-only navigation, **when** the customer toggles activity, **then** focus, expanded state, and the disclosure relationship are conveyed correctly.
3. **Given** progress changes while the page is open, **when** the current stage advances, **then** the update is announced without repeatedly reading the entire history.

## Edge Cases and Failure Handling

- A provider phase is missing, repeated, received late, or unknown to the current customer-stage mapping.
- A later snapshot omits transition history that appeared in an earlier snapshot.
- The engine reaches Ready before the browser observes every intermediate stage.
- The create operation fails before provider work begins.
- Progress retrieval is unauthorized, forbidden for a different workspace, unavailable, or times out.
- The customer signs out, changes workspace, backgrounds the page, or refreshes during provisioning.
- Multiple dashboard tabs observe the same engine.
- Timestamps are missing, equal, or arrive with clock skew.
- A known engine has no correlated Create activity because it predates this feature.
- Long localized messages or many historical events are displayed on mobile.

The experience MUST favor an explicit `Unknown` or progress-unavailable state over inferred or fabricated advancement. A terminal Ready or Failed state MUST remain authoritative even when intermediate activity is incomplete.

## Requirements

### Functional Requirements

- **FR-001**: The system MUST expose a customer-authorized provisioning progress snapshot scoped to a workspace and managed engine.
- **FR-002**: The system MUST resolve the relevant Create operation and provider activity from the managed engine; the browser MUST NOT choose provider, deployment, or operation identifiers as authority.
- **FR-003**: The progress snapshot MUST use stable provider-neutral stage codes for request acceptance, hosting foundation, configuration, runtime deployment, health verification, traffic routing, and Ready.
- **FR-004**: Every stage MUST have one of these states: `pending`, `current`, `completed`, `blocked`, or `unknown`.
- **FR-005**: Stage progression MUST be monotonic for a single Create operation. A later snapshot MUST NOT move a completed stage back to pending or current.
- **FR-006**: The progress snapshot MUST include an authoritative overall state and the time of its latest durable update when available.
- **FR-007**: The system MUST preserve terminal Create history for as long as the associated managed engine and its customer-visible lifecycle history are retained.
- **FR-008**: The system MUST map only known durable provider observations to customer stages. Missing or unrecognized observations MUST produce `pending` or `unknown`, never invented progress.
- **FR-009**: The customer contract MUST allowlist fields and safe diagnostic codes. Unknown upstream fields MUST be discarded before data reaches browser code.
- **FR-010**: Elsa Cloud MUST render a compact timeline for every managed engine whose Create operation is nonterminal or has retained terminal history.
- **FR-011**: The compact timeline MUST identify completed, current, pending, blocked, unknown, Ready, and Failed outcomes without relying on color alone.
- **FR-012**: Elsa Cloud MUST show indeterminate activity for active work and MUST NOT show a numeric percentage or predicted completion time.
- **FR-013**: The engine card MUST show elapsed time and the last known update time when timestamps are available.
- **FR-014**: A customer MUST be able to expand and collapse a terminal-style activity history containing ordered timestamped customer messages.
- **FR-015**: The activity history MUST be derived from durable server data and MUST survive page refresh, a second browser tab, and a later sign-in.
- **FR-016**: Elsa Cloud MUST poll progress only while it is nonterminal, reduce or pause polling while the page is in the background, and refresh promptly when the page becomes active again.
- **FR-017**: A queued, server-declared stale, failed, or progress-unavailable state MUST provide distinct customer guidance while keeping the engine visible.
- **FR-018**: A Ready engine MUST retain its completed progress history without delaying or replacing the existing Open Studio action.
- **FR-019**: The experience MUST work at desktop and 390 x 844 mobile widths and MUST expose correct disclosure, progress, focus, and live-update semantics to assistive technology.
- **FR-020**: Cross-workspace or cross-account progress requests MUST be denied without revealing whether the target engine or activity exists.

### Security and Privacy Requirements

- **SPR-001**: Customer responses MUST NOT contain provider resource IDs, resource-group or deployment names, subscription or tenant identifiers, endpoints, raw provider payloads, or unrestricted logs.
- **SPR-002**: Customer responses MUST NOT contain worker identities, leases, heartbeats, request hashes, plan or template fingerprints, secret references, credentials, or encryption material.
- **SPR-003**: Customer responses MUST NOT contain raw exceptions, arbitrary provider messages, or internal recovery reasons.
- **SPR-004**: Customer-facing copy MUST be selected from product-owned messages associated with allowlisted stage and diagnostic codes.
- **SPR-005**: Progress authorization MUST use the same customer workspace boundary as the managed-engine list and lifecycle actions.

### Reliability and Performance Requirements

- **RPR-001**: After the dashboard receives a successful create response, visible accepted progress MUST appear without requiring a manual refresh.
- **RPR-002**: While foreground polling is active, a newly available durable stage MUST appear within 15 seconds under normal service availability.
- **RPR-003**: Refreshing the page MUST reproduce the same or a later authoritative stage sequence; browser-only state MUST NOT be required.
- **RPR-004**: Temporary progress-endpoint failure MUST NOT hide the engine, erase previously rendered history, or enable an action that the authoritative engine state does not allow.
- **RPR-005**: The progress experience MUST not materially delay the initial rendering of the engine list; progress may load independently after known engines are visible.

### Observability Requirements

- **OR-001**: The service MUST record whether a progress request succeeded, was denied, or failed without recording customer-visible secrets or provider payloads.
- **OR-002**: Unknown provider phases and invalid non-monotonic mappings MUST be detectable by operators through existing protected observability.
- **OR-003**: Live release evidence MUST identify the exact published Control and Elsa Cloud revisions used for Create-to-Ready verification.

## Key Entities

- **Provisioning progress snapshot**: The authoritative customer-safe view of one managed engine's Create progress, including overall state, ordered stages, latest update time, and optional safe guidance.
- **Provisioning stage**: A stable product-owned milestone with a code, state, optional start/completion timestamps, and customer-safe message key.
- **Activity entry**: A durable, timestamped customer-safe observation associated with a stage. It is presentation data, not a raw log line.
- **Diagnostic guidance**: An allowlisted code that Elsa Cloud translates into customer guidance for blocked, stale, unavailable, or failed states.

## Data and Integration Requirements

- Existing managed-engine identity and workspace authorization remain the source of scope.
- Existing durable lifecycle, deployment, and provider observations remain the source of progress; browser state is not authoritative.
- The customer-facing integration returns only the feature's explicit allowlisted contract.
- The Hosted server-side boundary validates scope and normalizes the contract before returning it to browser code.
- Existing engine listing, creation, handoff, billing, and deletion behavior remain compatible.
- The initial provider mapping covers Azure, while customer stage codes and display behavior remain provider-neutral.

## Acceptance Criteria

- [ ] A newly accepted Create immediately shows visible accepted progress instead of only a static status.
- [ ] Durable Azure phases advance the seven customer stages monotonically through Ready.
- [ ] No numeric percentage or predicted completion time is presented.
- [ ] The expandable activity view shows sanitized events in stable chronological order and survives refresh.
- [ ] Queued, active, server-declared stale, failed, progress-unavailable, refreshed, and Ready states are covered.
- [ ] Workspace authorization and cross-account denial are verified.
- [ ] Contract tests prove unknown fields and sensitive provider metadata never reach browser code.
- [ ] The timeline and disclosure pass desktop, keyboard, assistive-technology, and 390 x 844 mobile checks.
- [ ] Existing managed create, list, handoff, billing, and deletion journeys remain unaffected.
- [ ] A live Azure Hosted create proves visible progression through Ready on exact published revisions.

## Success Criteria

### Measurable Outcomes

- **SC-001**: In acceptance testing, 100% of accepted Create requests display an acknowledged current stage without a manual refresh.
- **SC-002**: Every durable known phase observed during the live Azure proof is reflected as the same or a later customer stage within 15 seconds while the dashboard is foregrounded.
- **SC-003**: Refresh tests at every supported progress state reproduce the same or a later stage sequence in 100% of runs.
- **SC-004**: Automated contract checks find zero provider identifiers, raw messages, secrets, unknown fields, or percentages in customer-facing responses.
- **SC-005**: All defined progress states remain understandable and operable at 390 x 844 without page-level horizontal scrolling.
- **SC-006**: The exact published revisions complete one observed Create-to-Ready journey with an ordered, customer-visible history and no use of Control Console.

## Rollout and Migration

- The feature is additive and requires no customer data migration.
- Existing engines without correlated retained activity show their authoritative lifecycle plus a clear `Detailed progress is unavailable for this engine` message.
- The customer-facing contract should be published before the Hosted UI begins relying on it.
- The Hosted server-side boundary must fail closed if the new contract is absent or incompatible; existing engine status remains visible.
- Rollback removes the enhanced timeline and activity presentation while preserving the existing authoritative engine state and lifecycle actions.
- Live proof uses a normal Hosted Create and records exact revisions. It must not expose provider details in captured browser evidence.

## Assumptions

- Issue #529 remains Create-focused; Update, Repair, and Delete progress are follow-up work.
- The seven customer stages are sufficient for the first public experience even when some providers skip intermediate observations.
- The terminal-style activity view is expandable and collapsed by default so the engine card remains compact.
- Provider name may be displayed as a safe label, but stage codes and customer messages do not depend on Azure terminology.
- Existing durable observations are sufficient for the initial timeline; the feature does not require raw log ingestion.
- Staleness is server-declared from authoritative operation knowledge rather than guessed solely from a browser timer.
- English customer copy ships first using the product's existing localization approach; no new localization platform is introduced.

## Open Questions

None required for PRD approval. Any implementation-specific choices belong in the subsequent plan and must preserve these requirements.
