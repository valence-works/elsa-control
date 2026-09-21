# Implementation Plan: Managed Engine Provisioning Progress

**Branch**: `codex/529-provisioning-progress` | **Date**: 2026-09-21 | **Spec**: [spec.md](./spec.md)

**Input**: Approved feature specification from `specs/040-managed-provisioning-progress/spec.md`

## Summary

Project the existing durable Azure lifecycle transitions into a narrow, provider-neutral managed-engine progress contract. Elsa Control will resolve the customer engine's Create operation server-side, map known provider phases into seven stable customer stages, and expose only allowlisted stage state, timestamps, and diagnostic codes through a Cloud-BFF-authorized endpoint. Elsa Cloud will add a server-side BFF action, schema-normalized client model, compact polling timeline, and a collapsed-by-default terminal-style activity disclosure. Existing provider messages and identifiers never cross the customer boundary.

Delivery spans two repositories in release order: `valence-works/elsa-control` publishes the additive contract and compatibility capability first; `valence-works/elsa-cloud` then adopts the capability, BFF action, and UI.

## Technical Context

**Language/Version**: C# on .NET 10 for Control; TypeScript 5.8, React 18, Deno-compatible Supabase Edge Functions, and Vite 5 for Elsa Cloud.

**Primary Dependencies**: ASP.NET Core minimal APIs and authorization, existing managed-instance lifecycle services, existing Azure provider-operation store and durable transitions, EF Core catalog persistence, React, TanStack Query where useful, Supabase Edge Functions, Zod, Vitest, Testing Library, and existing CSS design tokens.

**Storage**: Existing catalog tables for managed-instance lifecycle operations, Azure provider operations, and append-only provider transitions. No schema or migration change is planned. Elsa Cloud keeps only transient display state; durable progress remains server-owned.

**Testing**: xUnit with built-in assertions for Control core/provider/API/persistence coverage; Vitest and Testing Library for Cloud contract, BFF, polling, accessibility, and responsive component coverage; browser verification for desktop and 390 x 844; one live Azure Hosted Create-to-Ready proof on exact published revisions.

**Target platform**: Elsa Control ASP.NET Core API deployed in Azure; Elsa Cloud React web application and Supabase Edge Function BFF at `elsacloud.app`.

**Project Type**: Cross-repository web-service and web-application feature.

**Performance Goals**: Engine list content renders without waiting for progress details. A progress read performs bounded indexed reads for one workspace/instance. Foreground polling reflects a newly durable stage within 15 seconds under normal service availability and stops after a terminal state.

**Constraints**: The customer response is strictly allowlisted and private/no-store. Browser input never selects provider-operation identity. No raw provider message, identifier, secret-bearing metadata, worker state, or arbitrary failure text may cross Control or the BFF. No fabricated percentages or browser-only history. Existing Cloud compatibility gating and release ordering remain authoritative. All .NET build, test, restore, and related heavy commands use the machine-wide `dotnet` build-slot wrapper already first on `PATH`.

**Scale/Scope**: First-public Hosted permits one active managed engine per subscription. The contract and endpoint remain correct for repeated reads, multiple tabs, and future larger workspace lists, but Create progress for one engine is the only lifecycle action in scope.

## Constitution Check

*Pre-design gate: PASS. Post-design gate: PASS.*

- **Control Plane First**: Pass. The feature exposes deployment control-plane progress. It does not inspect workflow instances, bookmarks, queues, locks, runtime logs, or other Elsa data-plane state.
- **Bounded Subsystems**: Pass. Provider-neutral customer models live at the managed-instance boundary; Azure phase interpretation remains in the Azure deployment adapter. API and persistence remain adapters and do not move provider logic into browser code.
- **Contract Stability**: Pass. The additive v1 progress contract and capability are documented before implementation. Unknown phases fail safe and do not silently change existing stage semantics.
- **Safety By Design**: Pass. The response is constructed from allowlisted values. Raw provider messages, IDs, endpoints, fingerprints, leases, secret references, and exception text are excluded in Control and stripped again by the BFF.
- **Incremental Verifiability**: Pass. Control contract, compact timeline, expanded activity, and live rollout are independent slices with focused gates and rollback paths.
- **Engineering Standards**: Pass. Existing .NET, API, BFF, React, Vitest, and CSS conventions are reused. No new persistence subsystem or frontend state library is introduced.

## Architecture

```text
Azure provider transitions (durable, private)
        │
        ▼
Azure Create progress projector
  - resolves provider operation from server-owned lifecycle correlation
  - maps phase/status into stable customer stages
  - discards provider messages and identities
        │
        ▼
GET /api/workspaces/{workspaceId}/instances/{instanceId}/provisioning-progress
  - workspace authorized
  - Cloud BFF allowlisted
  - private, no-store
        │
        ▼
Elsa Cloud control-bff action
  - validates organization/workspace/instance scope
  - schema allowlists every field and enum
  - strips unknown fields
        │
        ▼
Dashboard engine progress
  - compact seven-stage timeline
  - foreground polling while nonterminal
  - expandable sanitized activity
```

### Control projection

The API resolves the scoped managed instance and its latest applicable Create operation. The Azure adapter then resolves the provider operation through the lifecycle correlation already persisted in the provider operation's instance/action/idempotency metadata. The browser supplies only workspace and instance identity through the authorized route.

The projector maps:

| Customer stage | Durable source phases |
|---|---|
| `request-accepted` | lifecycle Create accepted, waiting, queued, entitlement-held, or provider `Planned` |
| `hosting-foundation` | `FoundationSubmitted`, `FoundationObserved` |
| `configuration` | `AcrPullObserved`, `SeedSecretsObserved`, `SqlFirewallReady`, `SqlBootstrapReady`, `FoundationReady` |
| `runtime-deployment` | `WorkloadSubmitted`, `WorkloadReady` |
| `health-verification` | `HealthVerified` |
| `traffic-routing` | `TrafficPromoted` before terminal reconciliation |
| `ready` | authoritative Create success and Ready observation |

Known later stages imply earlier stages completed. Repeated phases are deduplicated by durable sequence. Unknown phases do not advance the customer stage. Create `RecoveryRequired` becomes a server-declared `stale`/requires-attention outcome rather than indefinite animation. Terminal failure blocks the last known stage with a stable customer diagnostic code.

### Cloud adoption

Control adds `hosted.instances.provisioning-progress.v1` to the compatibility capabilities. The existing Elsa Cloud compatibility check adds it only when the BFF and UI are ready to use the new route. Release ordering stays Control, then BFF, then UI. An older UI ignores the additive capability; the new UI fails closed against an older Control deployment.

The BFF never forwards an upstream object directly. It accepts only the defined overall states, stage IDs, stage states, numeric sequence, ISO timestamps, provider display value, and allowlisted diagnostic/message codes. Invalid required fields make the progress action fail safely; invalid optional activity entries are discarded.

### Polling and display

- The engine list renders first; progress loads independently for managed engines.
- Foreground nonterminal progress polls every 5 seconds, satisfying the 15-second visibility target with retry allowance.
- Polling pauses when the document is hidden and refreshes immediately when visibility returns.
- Polling stops for `ready` and `failed`; `stale` uses a slower explicit refresh path rather than continuous active animation.
- A temporary read failure retains the engine and last successfully rendered snapshot for the current page session, labels progress unavailable, and never changes lifecycle actions.
- The activity disclosure is collapsed by default and uses product copy keyed by stable message codes. It is not a command console.

## Project Structure

### Documentation

```text
specs/040-managed-provisioning-progress/
├── spec.md
├── plan.md
├── research.md
├── data-model.md
├── roadmap.md
├── quickstart.md
├── contracts/
│   ├── managed-provisioning-progress-api.md
│   └── cloud-progress-ux.md
├── checklists/
│   └── requirements.md
└── tasks.md
```

### Elsa Control source

```text
src/Deployment/ElsaControl.Deployment.Core/Instances/
  ManagedElsaProvisioningProgressModels.cs

src/Deployment/ElsaControl.Deployment.Azure/
  AzureManagedElsaProvisioningProgressReader.cs
  IAzureProviderOperationStore.cs

src/PackageCatalog/ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore/
  AzureProviderOperationStore.cs

src/Hosting/ElsaControl.Api/
  Cloud/CloudCompatibilityEndpoints.cs
  Workspace/ManagedElsaInstanceEndpoints.cs

tests/Deployment/ElsaControl.Deployment.Azure.Tests/
  AzureManagedElsaProvisioningProgressReaderTests.cs

tests/PackageCatalog/ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests/
  AzureProviderOperationPersistenceTests.cs

tests/Hosting/ElsaControl.Api.Tests/
  ManagedElsaInstanceApiTests.cs
  CloudBffAuthorizationTests.cs
```

### Elsa Cloud source

```text
supabase/functions/_shared/
  managedProvisioningProgressContract.ts
  managedProvisioningProgressContract.test.ts

supabase/functions/control-bff/
  index.ts

src/lib/
  controlApi.ts
  controlApi.test.ts

src/pages/
  Dashboard.tsx
  Dashboard.test.tsx
  dashboard.css

docs/
  cloud-control-release-order.md
```

**Structure Decision**: Keep the canonical spec and Control contract in `elsa-control`, the repository that owns provider authority. Deliver the BFF and customer experience in `elsa-cloud`. Use separate implementation issues and PRs per repository, linked to parent #529, so release order and rollback remain independently reviewable.

## Phase Plan

### Phase 1: Control customer-safe progress contract

Outcome: An authorized customer can read a stable seven-stage Create progress snapshot without learning provider internals.

Exit gate: Provider mapping, correlation, monotonicity, persistence, authorization, cross-workspace denial, redaction, compatibility capability, and refresh-safe terminal history tests pass.

### Phase 2: Cloud compact timeline MVP

Outcome: A Hosted customer sees accepted/current/completed/pending Create progress and update time directly on the engine card.

Exit gate: BFF schema tests prove allowlisting; UI tests prove queued, active, refresh, Ready, temporary-unavailable, polling, and reduced-motion behavior.

### Phase 3: Expandable activity and exceptional-state UX

Outcome: A customer can inspect ordered terminal-style activity and understand stale or failed outcomes on desktop and mobile.

Exit gate: Activity ordering/copy, blocked/unknown behavior, keyboard disclosure, screen-reader updates, and 390 x 844 layout tests pass.

### Phase 4: Rollout and live Azure proof

Outcome: Exact published Control and Cloud revisions visibly progress through a real Hosted Create to Ready.

Exit gate: Control capability is live before Cloud adoption; compatibility smoke passes; desktop and 390 x 844 evidence captures progression, refresh recovery, terminal history, and no sensitive browser payload fields.

## Complexity Tracking

No constitution violations require justification.
