# Elsa Control Artifact Deployment Execution Plan

Status: current execution baseline

Last updated: 2026-06-01

## Product Model

Elsa Control is an extensible deployment control plane for Elsa solutions. It is not the workflow runtime and does not execute workflows.

The settled architecture is:

```text
Producer integration
  -> submit immutable artifact

Elsa Control
  -> registry, desired state, promotion, validation, deployment run, audit

Runtime/provider integration
  -> consume deployment command, apply artifact, report result
```

For the first-class Elsa workflow path:

- Elsa Studio remains the authoring and single-engine runtime inspection surface.
- Elsa Control-integrated Studio uses **Submit to Elsa Control** to create an immutable deployable artifact.
- **Submit to Elsa Control** does not mean release, promotion, deployment, or immediate runtime execution.
- Elsa runtimes remain responsible for executing deployed artifacts and owning runtime state.
- Runtime integrations consume durable platform-owned deployment commands by pull/sync by default, webhook-triggered fetch where useful, or direct push where explicitly configured.

## Current Implementation Baseline

Completed:

- Identity and workspace tenancy: `specs/021-identity-tenancy` has all tasks completed. Focused smoke coverage is recorded in the quickstart; live external provider browser sign-in remains environment-specific.
- Deployment UX and durable queued runs: `specs/022-deployment-ux` has 110 completed tasks and no open checklist tasks. It already includes durable deployment runs, confirmations, queue worker, rollback, runtime controls, permissions, persistence, API, and console flows.
- Engine health verification: `specs/023-engine-health-verification` has all tasks completed. Core, persistence, API, console, and typecheck results are recorded in the quickstart.
- Artifact registry: `specs/024-artifact-registry` has all tasks completed. Core, persistence, API, and console focused checks are recorded in the quickstart.
- Custom deployment tiers: `specs/025-custom-deployment-tiers` has all tasks completed. Tier capability semantics, migration backfill, bounded-query verification, and console/API coverage are recorded in the quickstart.
- Artifact envelope and types: `specs/026-artifact-envelope-and-types` has all tasks completed. The envelope/type contracts live in `ElsaControl.Deployment.Artifacts`, and the workspace artifact registry now stores type, producer, safe display metadata, compatibility hints, and payload references.
- Studio Submit to Elsa Control: `specs/027-studio-submit-to-elsa-control` has all platform-side tasks completed. The Elsa Control submit package has configuration, safe result states, deterministic workflow snapshot packaging, and a concrete Elsa Control artifact registration HTTP client. The companion Elsa Studio integration is merged to `elsa-studio/main` with **Submit to Elsa Control** actions in the workflow editor toolbar, workflow definition list bulk menu, and workflow definition row menu.
- Runtime command sync: `specs/028-runtime-command-sync` now has backend command models, EF persistence, SQLite/SQL Server migrations, runtime pull/sync APIs, claim/lease behavior, heartbeat/progress/completion reporting, stale recovery, idempotency, webhook notification records, disabled-by-default advisory webhook dispatch, runtime transport trust guidance, in-process worker compatibility, and focused core/persistence/API coverage.
- Workflow artifact runtime applier: `specs/029-workflow-artifact-runtime-applier` has all tasks completed. The first runtime consumer package can poll/claim commands, fetch workflow artifact payloads, validate digest/schema/capabilities, apply through a runtime store boundary, guard idempotency with an apply journal, and report safe results to Elsa Control.
- Artifact-backed promotion: `specs/030-artifact-backed-promotion` has all tasks completed. Desired-state revisions can reference artifacts, promotion previews compare safe artifact metadata/configuration/runtime compatibility, deployment commands carry safe artifact references, and rollback can redeploy a known-good artifact-backed revision.

Architectural model now implemented by the completed slices:

- Immutable workflow artifacts can enter Elsa Control from Studio's **Submit to Elsa Control** producer path.
- Artifact registry records are metadata/reference-only and keep raw workflow payload content outside catalog tables.
- Desired-state revisions can carry artifact references as deployable input.
- Deployment execution creates platform-owned command records linked to deployment runs while keeping the platform-local in-process worker compatible.
- External runtime sync workers can poll, claim, heartbeat, progress, complete, fail, or reject commands through the runtime command API.
- Runtime applier packages own artifact interpretation and local runtime mutation; Elsa Control remains agnostic about workflow internals beyond artifact type, digest, compatibility hints, and safe metadata.

## Spec Work Needed

No new platform architecture specs are currently required for the artifact-driven workflow path. The existing focused specs through `030-artifact-backed-promotion` are complete enough for the next validation milestone.

Historical alignment work completed:

1. `specs/024-artifact-registry`
   - Extend from metadata-only registry toward typed artifact ingestion.
   - Keep catalog tables metadata/reference-only.
   - Add explicit artifact type and producer concepts where missing.
   - Align contracts with Studio submission as a future ingestion path.

2. `specs/022-deployment-ux`
   - Preserve first-slice queued runs.
   - Add follow-on tasks for deployment command records and runtime sync compatibility.
   - Move desired-state examples from inline workflow records toward artifact references where appropriate.

3. `specs/025-custom-deployment-tiers`
   - Finish current tier implementation.
   - Ensure promotion and deployment safeguards use tier capabilities, not tier names.
   - Keep environment technical capabilities separate from tier policy capabilities.

Focused specs:

1. `026-artifact-envelope-and-types`
   - Completed. Shared artifact envelope, artifact type IDs, payload reference model, digest rules, safe metadata, producer metadata, and target capability hints live in `ElsaControl.Deployment.Artifacts`.

2. `027-studio-submit-to-elsa-control`
   - Completed for Elsa Control-side package contracts and HTTP submission behavior.
   - The companion Studio UI integration is merged in `elsa-studio` (now elsa-core) and injects **Submit to Elsa Control** beside existing publish surfaces through neutral workflow zones.
   - Host-specific authentication and configuration remain packaging/documentation hardening, not a missing platform model slice.

3. `028-runtime-command-sync`
   - Backend implementation completed for platform deployment command records, runtime pull/sync API, claim/lease, idempotent completion, progress reporting, stale recovery, safe diagnostics, webhook notification records, and the first disabled-by-default HTTP webhook dispatcher.
   - Runtime credential bootstrap, rotation expectations, artifact payload trust, and webhook network trust policy are documented in [Runtime Transport Trust Policy](runtime-transport-trust-policy.md).

4. `029-workflow-artifact-runtime-applier`
   - Completed for the first Elsa Workflows runtime integration package boundary.
   - Runtime command HTTP client polls, claims, heartbeats, reports progress, completes, fails, and rejects commands through Elsa Control APIs.
   - Payload loading, digest/schema validation, capability checks, local apply boundary, idempotency journal, and safe diagnostics are covered by focused tests.

5. `030-artifact-backed-promotion`
   - Completed to replace or augment structured workflow desired-state records with artifact references.
   - Promotion preview and deployment validation operate on artifacts plus environment-specific configuration.
   - Rollback redeploys known-good artifact-backed revisions.

## Execution Phases

### Phase 0: Documentation And Spec Alignment

Goal: make the repo narrative consistent before expanding implementation.

Tasks:

- Keep `README.md`, `specs/021-identity-tenancy`, `specs/022-deployment-ux`, and `specs/024-artifact-registry` aligned with the artifact/control-plane model.
- Add the new Spec Kit specs listed above.
- Mark `docs/elsa-control-deployment-phased-strategy.md` as historical and point readers to this current artifact-driven execution plan. Completed.

Exit criteria:

- New specs are created with clear boundaries.
- Existing specs no longer imply that Elsa Control queries Studio/runtime databases for workflow definitions.
- The terms **Submit to Elsa Control**, artifact submission, deployment command, runtime sync worker, and webhook notification are used consistently.

### Phase 1: Close Current Open Work

Goal: stabilize the code already in flight before adding runtime sync.

Tasks:

- Run and record identity quickstart smoke checks for `021`. Completed.
- Complete remaining engine-health verification checks for `023`. Completed.
- Complete artifact-registry missing tests for core validation, EF persistence, API permissions/isolation, refresh behavior, and focused test runs. Completed.
- Complete custom-tier test coverage, migration backfill, bounded-query verification, quickstart updates, and final contract updates. Completed.

Exit criteria:

- `021`, `023`, `024`, and `025` task lists are closed or have explicit deferred items.
- Focused tests for workspace deployment, artifact registry, engine health, and tiers pass.
- `git diff --check` passes.

### Phase 2: Artifact Envelope And Registry Upgrade

Goal: make artifacts typed and producer-neutral.

Tasks:

- Add shared artifact envelope contracts.
- Add artifact type identifiers such as `elsa.workflow-definition`.
- Add producer metadata for Studio, CLI, CI, and manual registration.
- Add safe metadata schema for display/search without storing raw payload content in catalog tables.
- Update registry API and console to show artifact type, producer, compatibility hints, and submission status.

Exit criteria:

- Artifact records can represent a Studio-submitted workflow artifact without raw workflow content in catalog tables.
- Existing manual artifact registration remains compatible.
- Tests prove duplicate artifact identity, digest mismatch, unsafe metadata, and cross-workspace access fail closed.

### Phase 3: Deployment Command Contract

Goal: separate durable deployment intent from transport.

Tasks:

- Add deployment command records linked to deployment runs. Completed.
- Model command action, artifact/revision reference, target runtime, idempotency key, lease/claim state, expiration, progress, and safe diagnostics. Completed.
- Add API endpoints for runtime integrations to poll, claim, heartbeat, complete, fail, or reject commands. Completed.
- Keep the legacy in-process queue worker from applying command-backed deployments. Completed by disabling it by default and limiting it to stale-run recovery when enabled.
- Add duplicate poll/claim and stale-recovery tests proving no duplicate apply. Completed for core, persistence, and API claim lifecycle; provider webhook delivery remains a future transport concern.

Exit criteria:

- Runtime pull can claim and complete commands without inbound runtime access. Completed.
- Stale claimed commands move to recovery-required without automatic duplicate apply. Completed.
- Deployment history remains the console-facing source of truth. Completed through command event projection into run history.

### Phase 4: Studio Submit Integration

Goal: create the first artifact producer package.

Tasks:

- Create Studio integration package. Completed.
- Add **Submit to Elsa Control** command and configuration. Completed.
- Package workflow definition snapshot, source IDs, display metadata, schema version, and content digest. Completed.
- Submit artifact metadata and payload reference/content according to the artifact envelope spec. Completed.
- Add user-facing states for submitted, failed, unauthorized, and duplicate artifact. Completed.
- Inject **Submit to Elsa Control** in Studio's workflow editor toolbar, definition list bulk menu, and definition row menu through neutral workflow zones. Completed in `elsa-studio/main` (now elsa-core).

Exit criteria:

- A workflow authored in Studio can be submitted to Elsa Control as an immutable artifact. Completed.
- Submission does not deploy or make the workflow immediately executable. Completed.
- Direct runtime Publish behavior is clearly separate when present. Completed.

### Phase 5: Runtime Sync And Workflow Artifact Applier

Goal: create the first artifact consumer package.

Tasks:

- Create Elsa Workflows runtime integration package. Completed.
- Add runtime capability advertisement for workflow artifact support. Completed.
- Add outbound command client and lease/retry policy for pending deployment commands. Completed.
- Fetch artifact metadata/payload, verify digest, validate runtime compatibility, and apply supported workflow artifacts through a runtime store boundary. Completed.
- Report progress, validation result, apply result, observed digest, runtime reference, and safe diagnostics. Completed.

Exit criteria:

- Elsa Control can deploy an `elsa.workflow-definition` artifact to a registered runtime via outbound runtime pull at the package-contract level. Completed.
- Runtime does not need to expose an inbound endpoint. Completed.
- Failed validation/apply results are visible in Elsa Control deployment history. Completed.

### Phase 6: Artifact-Backed Promotion And Rollback

Goal: make promotion/deployment flows artifact-first.

Tasks:

- Desired-state revisions reference workflow artifact versions instead of embedding workflow definition intent directly. Completed.
- Promotion preview compares artifact-backed revisions and environment-specific configuration. Completed.
- Tier capabilities drive safeguards. Completed.
- Environment/runtime capabilities drive technical compatibility. Completed.
- Rollback redeploys a known-good artifact-backed revision. Completed.

Exit criteria:

- Users can submit a workflow artifact, promote it across environments, deploy it to a runtime, and roll back from Elsa Control at the API/package-contract level. Completed.
- Elsa Control remains agnostic about workflow internals beyond safe metadata, artifact type, digest, and compatibility hints. Completed.

### Phase 7: End-To-End Smoke And Packaging Hardening

Goal: prove the completed slices operate as one product path and prepare the integrations for consumption outside the development workspace.

Tasks:

- Add or run an end-to-end smoke scenario that follows one workflow artifact through Studio submission, artifact registry, artifact-backed desired state, promotion, deployment command creation, runtime applier claim/apply/report, and rollback.
- Capture the smoke scenario in `docs/elsa-control-artifact-workflow-e2e-smoke.md` so future changes can validate the complete control-plane path without rediscovering the sequence.
- Decide the packaging home for the Studio integration and runtime applier packages, including NuGet IDs, host registration examples, auth configuration, and supported Elsa Control API version range.
- Add samples or quickstart host wiring for a Elsa Control-integrated Studio and a runtime-integrated Elsa Workflows app.
- Keep production transport guidance current as real providers are added. Initial credential bootstrap, rotation, payload trust, and webhook trust policy are documented in [Runtime Transport Trust Policy](runtime-transport-trust-policy.md).

Exit criteria:

- A reviewer can follow one documented smoke path from **Submit to Elsa Control** through runtime apply and rollback.
- Any failure in the E2E path is captured as a specific implementation issue rather than an architectural gap.
- Package consumers have enough configuration guidance to install the producer and consumer integrations without reading implementation tests.

## Immediate Next Actions

1. Use `docs/elsa-control-artifact-workflow-e2e-smoke.md` as the ongoing regression path for artifact-driven workflow deployment.
2. Keep `docs/elsa-control-integration-packaging.md`, `samples/`, and `docs/runtime-transport-trust-policy.md` aligned as package APIs and provider transports harden.
3. Open a focused issue/spec before adding provider-specific credential issuers, webhook authentication handlers, direct-push transports, or production artifact payload providers.
