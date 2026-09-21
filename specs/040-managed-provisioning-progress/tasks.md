# Tasks: Managed Engine Provisioning Progress

**Input**: Design documents from `specs/040-managed-provisioning-progress/`

**Tests**: Required by the approved PRD. Contract redaction, authorization, polling, exceptional states, accessibility, responsive behavior, and live Azure proof are release gates.

**Organization**: Tasks are grouped by user story and delivery slice. Each slice is independently reviewable, uses one Issue Bus leaf and one scoped PR, and runs up to five self-review/fix iterations before completion.

## Phase 1: Setup and tracking

**Purpose**: Establish clean cross-repository delivery context after roadmap approval.

- [x] T001 Create the four approved child issues under #529 and add them to Project #7 using `specs/040-managed-provisioning-progress/roadmap.md`
- [x] T002 Record child issue links, Project #7 URL/visibility, PRD, plan, roadmap, and tasks links in GitHub issue #529
- [x] T003 Verify no matching open PR or active competing claim exists for the first eligible slice using `scripts/issue_bus.py`
- [x] T004 Create clean isolated worktrees from current `origin/main` for the claimed Control or Cloud slice without touching dirty primary checkouts

---

## Phase 2: Foundational Control progress contract — S1

**Purpose**: Provide the customer-safe, provider-neutral progress source required by every UI story.

**⚠️ CRITICAL**: No Cloud progress UI work starts until this slice is merged or its exact contract head is available for coordinated integration.

### Contract and mapping tests

- [x] T005 [P] Add queued, phase-mapping, monotonicity, duplicate-transition, RecoveryRequired, failed, cancelled, Ready, unknown-phase, and retained-history tests in `tests/Deployment/ElsaControl.Deployment.Azure.Tests/AzureManagedElsaProvisioningProgressProjectorTests.cs` and `AzureManagedElsaProvisioningProgressReaderTests.cs`
- [x] T006 [P] Add scoped lifecycle-correlation and transition-order persistence tests in `tests/PackageCatalog/ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests/AzureProviderOperationPersistenceTests.cs`
- [x] T007 [P] Add owner, anonymous, Cloud BFF, cross-workspace, topology-change, terminal, and serialized-redaction tests in `tests/Hosting/ElsaControl.Api.Tests/ManagedElsaInstanceApiTests.cs`
- [x] T008 [P] Add additive capability and exact compatibility response tests in `tests/Hosting/ElsaControl.Api.Tests/CloudBffAuthorizationTests.cs`

### Contract implementation

- [x] T009 Define stable overall, stage, activity, diagnostic, and snapshot models in `src/Deployment/ElsaControl.Deployment.Core/Instances/ManagedElsaProvisioningProgressModels.cs`
- [x] T010 Add a dedicated Azure operation read port with a workspace/instance/lifecycle-correlated lookup in `src/Deployment/ElsaControl.Deployment.Azure/IAzureManagedElsaProvisioningOperationStore.cs`
- [x] T011 Implement indexed scoped correlation without exposing internal identity in `src/PackageCatalog/ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore/AzureProviderOperationStore.cs`
- [x] T012 Implement the seven-stage Azure phase projector, terminal precedence, safe codes, deduplication, activity mapping, and protected detection of unknown or non-monotonic provider mappings in `src/Deployment/ElsaControl.Deployment.Azure/AzureManagedElsaProvisioningProgressProjector.cs` and `AzureManagedElsaProvisioningProgressReader.cs`
- [x] T013 Register the progress reader in the existing API composition in `src/Hosting/ElsaControl.Api/Program.cs`
- [x] T014 Map the private/no-store customer route and response projection in `src/Hosting/ElsaControl.Api/Workspace/ManagedElsaInstanceEndpoints.cs`, with protected request telemetry supplied by the existing ASP.NET Core request pipeline and protected mapping diagnostics in the progress reader
- [x] T015 Add `hosted.instances.provisioning-progress.v1` to `src/Hosting/ElsaControl.Api/Cloud/CloudCompatibilityEndpoints.cs`

### S1 validation and review

- [x] T016 Run the focused provider, persistence, and API tests from `specs/040-managed-provisioning-progress/quickstart.md` through the shared `dotnet` build-slot wrapper
- [x] T017 Run S1 self-review/fix iterations, `git diff --check`, and the required repository validation; record results on the S1 issue
- [x] T018 Open the S1 Control PR with `Fixes #<S1>` and `Refs #529`, verify its Development link, comment `pr: <url>`, and move S1 to In Review / Review Required

**Checkpoint**: The exact S1 PR head exposes a complete redacted progress contract and compatibility capability without any Cloud change.

---

## Phase 3: User Story 1 — Follow provisioning at a glance (Priority: P1) — S2

**Goal**: Show immediate, truthful Step N of 7 progress on each managed-engine card.

**Independent Test**: Render queued through Ready snapshots, advance them under fake timers, refresh with a later snapshot, and verify the timeline never regresses or blocks the engine list.

### Tests for User Story 1

- [ ] T019 [P] [US1] Add strict allowlist, malformed-payload, sensitive-field, and compatibility-capability tests in `elsa-cloud/supabase/functions/_shared/managedProvisioningProgressContract.test.ts`
- [ ] T020 [P] [US1] Add API client mapping and safe-error tests in `elsa-cloud/src/lib/controlApi.test.ts`
- [ ] T021 [P] [US1] Add queued, active, advancing, refreshed, Ready, unavailable, visibility-pause, terminal-stop, and reduced-motion tests in `elsa-cloud/src/pages/Dashboard.test.tsx`

### Implementation for User Story 1

- [ ] T022 [P] [US1] Implement the BFF request/response schemas and runtime allowlist in `elsa-cloud/supabase/functions/_shared/managedProvisioningProgressContract.ts`
- [ ] T023 [US1] Add the required capability and scoped `getInstanceProvisioningProgress` BFF action in `elsa-cloud/supabase/functions/_shared/cloudCompatibility.ts` and `elsa-cloud/supabase/functions/control-bff/index.ts`
- [ ] T024 [P] [US1] Add normalized progress types and client call in `elsa-cloud/src/lib/controlApi.ts`
- [ ] T025 [US1] Implement independent per-engine progress loading, five-second foreground polling, visibility refresh, terminal stop, and late-response guards in `elsa-cloud/src/pages/Dashboard.tsx`
- [ ] T026 [US1] Render the compact seven-stage timeline, elapsed time, last update, indeterminate state, and progress-unavailable fallback in `elsa-cloud/src/pages/Dashboard.tsx`
- [ ] T027 [US1] Add desktop/mobile timeline and reduced-motion styles in `elsa-cloud/src/pages/dashboard.css`

### S2 validation and review

- [ ] T028 [US1] Run focused Cloud contract/client/dashboard tests, lint, build, and desktop/390 x 844 component checks from `specs/040-managed-provisioning-progress/quickstart.md`
- [ ] T029 [US1] Run S2 self-review/fix iterations and `git diff --check`; record version-skew, rollback, and validation evidence on the S2 issue
- [ ] T030 [US1] Open the S2 Cloud PR with `Fixes valence-works/elsa-control#<S2>` and `Refs valence-works/elsa-control#529`, verify its Development link where available, comment `pr: <url>`, and move S2 to In Review / Review Required

**Checkpoint**: User Story 1 works independently with a compact timeline. S3 can be deferred without losing the essential release benefit.

---

## Phase 4: User Story 2 — Inspect sanitized activity (Priority: P2) — S3

**Goal**: Let customers inspect durable timestamped activity using product-owned copy.

**Independent Test**: Expand activity for an active engine, compare the same ordered entries after refresh, and prove no upstream message or unknown code is rendered.

### Tests for User Story 2

- [ ] T031 [P] [US2] Add activity-code copy, chronological ordering, duplicate coalescing, refresh, unknown-code, and upstream-message exclusion tests in `elsa-cloud/src/pages/Dashboard.test.tsx`
- [ ] T032 [P] [US2] Add keyboard disclosure, `aria-expanded`, associated-region, and non-repeating live-announcement assertions in `elsa-cloud/src/pages/Dashboard.test.tsx`

### Implementation for User Story 2

- [ ] T033 [US2] Add the collapsed-by-default activity disclosure and product-copy map in `elsa-cloud/src/pages/Dashboard.tsx`
- [ ] T034 [US2] Render local and elapsed timestamps without command-input or live-shell semantics in `elsa-cloud/src/pages/Dashboard.tsx`
- [ ] T035 [US2] Add contained terminal-style activity layout and long-line wrapping in `elsa-cloud/src/pages/dashboard.css`

### User Story 3 — Understand delay or failure (Priority: P2)

**Independent Test**: Render stale, failed, and temporary-unavailable snapshots; verify active animation stops, completed history remains, and safe actions/copy appear.

- [ ] T036 [P] [US3] Add stale, failed-at-stage, cancelled, progress-unavailable-after-good-snapshot, and manual-check tests in `elsa-cloud/src/pages/Dashboard.test.tsx`
- [ ] T037 [US3] Implement stale/requires-attention, failed-stage, cancelled, and temporary-unavailable guidance in `elsa-cloud/src/pages/Dashboard.tsx`
- [ ] T038 [US3] Ensure exceptional progress state never changes authoritative Open, Delete, quota, or billing actions in `elsa-cloud/src/pages/Dashboard.tsx`

### User Story 4 — Accessible small-screen progress (Priority: P3)

**Independent Test**: Exercise all stage and activity states at 390 x 844 with keyboard and reduced motion; verify text semantics and no page-level overflow.

- [ ] T039 [P] [US4] Add accessibility and 390 x 844 layout assertions for stage labels, live updates, focus, wrapping, and reduced motion in `elsa-cloud/src/pages/Dashboard.test.tsx`
- [ ] T040 [US4] Finalize mobile stacking, focus-visible, non-color state markers, and reduced-motion rules in `elsa-cloud/src/pages/dashboard.css`

### S3 validation and review

- [ ] T041 Run focused Dashboard tests, full Cloud tests, lint, build, and desktop/390 x 844 browser checks from `specs/040-managed-provisioning-progress/quickstart.md`
- [ ] T042 Run S3 self-review/fix iterations and `git diff --check`; record activity redaction and accessibility evidence on the S3 issue
- [ ] T043 Open the S3 Cloud PR with `Fixes valence-works/elsa-control#<S3>` and `Refs valence-works/elsa-control#529`, verify its Development link where available, comment `pr: <url>`, and move S3 to In Review / Review Required

**Checkpoint**: User Stories 2-4 add activity and exceptional-state polish without changing the S2 contract or engine actions.

---

## Phase 5: Ordered rollout and live Azure proof — S4

**Purpose**: Prove the reviewed cross-repository feature in production and reconcile tracking.

- [ ] T044 Update Control-before-BFF-before-UI publication and rollback checks in `elsa-cloud/docs/cloud-control-release-order.md`
- [ ] T045 Add or update the compatibility smoke to require `hosted.instances.provisioning-progress.v1` in `elsa-cloud/scripts/smoke-cloud-compatibility.mjs`
- [ ] T046 Run S4 self-review of release instructions, exact PR heads, CI results, and rollback behavior; resolve every justified finding before publication
- [ ] T047 Publish the merged Control revision, verify health/build identity, and prove the production compatibility capability before Cloud publication
- [ ] T048 Publish the merged Cloud BFF/UI revision and run the production compatibility smoke
- [ ] T049 Execute one authorized Hosted Azure Create and capture accepted, two or more intermediate stages, refresh recovery, expanded activity, and Ready on desktop and 390 x 844
- [ ] T050 Inspect browser responses for every explicit exclusion and record exact revisions, builds, timestamps, and evidence on the S4 issue and parent #529
- [ ] T051 Use the existing separately confirmed Delete flow for cleanup only when currently authorized, then verify no duplicate paid customer workload remains
- [ ] T052 Reconcile all required slice issues, PR Development links, task checkboxes, and Project #7 fields; close #529 only when every required acceptance criterion is proven

---

## Dependencies and Execution Order

```text
PRD approved
  └─ S1 Control contract
       └─ S2 Cloud compact timeline (US1 MVP)
            └─ S3 Cloud activity + exceptional states + accessibility (US2-US4)
                 └─ S4 ordered rollout and live Azure proof
```

- S1 must merge and publish before a production Cloud release requires its capability.
- S2 can begin against the exact reviewed S1 contract head, but its production publication waits for S1.
- S3 depends on S2's normalized model and timeline component.
- S4 depends on all required code slices merged and deployable.
- Existing active Issue Bus work must finish or be explicitly released before an implementation slice is claimed.

## Parallel Opportunities

- T005-T008 can be authored in parallel because they cover separate Control test boundaries.
- T019-T021 can be authored in parallel after the S1 contract is fixed.
- T022 and T024 can proceed in parallel before Dashboard integration.
- T031, T032, T036, and T039 can use independent fixtures before their corresponding UI implementation.
- Implementation PRs remain sequential by slice to preserve release and review order.

## Implementation Strategy

1. Deliver S1 as the safe additive backend contract.
2. Deliver S2 as the independently valuable first-public MVP.
3. Deliver S3 as incremental activity and accessibility capability.
4. Deliver S4 as evidence and rollout work, returning any discovered defect to its owning slice.

Every slice must run up to five bounded self-review/fix iterations, stop when no actionable findings remain, and never claim skipped validation as passed.
