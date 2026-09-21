# Quickstart: Managed Engine Provisioning Progress

## Prerequisites

- Clean isolated worktrees for `valence-works/elsa-control` and `valence-works/elsa-cloud`.
- .NET 10 SDK reached through `~/.local/share/dotnet-build-slots/bin/dotnet` first on `PATH`.
- Node.js and the Elsa Cloud lockfile dependencies.
- Authenticated test identities for workspace-owner, different-workspace, Cloud BFF, and anonymous cases.
- Live Hosted entitlement and Azure environment only for the final production proof.

## Scenario 1: Control projection and redaction

1. Seed one managed instance with an accepted Create operation and no provider operation.
2. Verify `queued`, `request-accepted`, and six pending later stages.
3. Add ordered Azure transitions through foundation, configuration, runtime, health, and traffic phases.
4. Verify each later snapshot is monotonic and activity is chronologically stable.
5. Add resource IDs, endpoints, provider messages, hashes, worker/lease values, diagnostics, and secret references to underlying records.
6. Serialize the customer response and verify none of those values or field names appear.
7. Mark Create succeeded and the instance Ready; verify all stages complete and history remains.

## Scenario 2: Authorization and correlation

1. Read the progress route as the owning workspace user and the configured Cloud BFF identity; expect success.
2. Read anonymously; expect authentication failure.
3. Read through a different workspace/account; expect the established concealed-resource denial.
4. Seed another provider operation with matching-looking client data and verify server-owned correlation still selects the correct operation.
5. Change lifecycle/provider topology during a consistency-sensitive read and verify the safe retry response.

## Scenario 3: Cloud BFF normalization

1. Return a valid Control progress object plus unknown top-level, stage, activity, provider, and secret-like fields.
2. Invoke `getInstanceProvisioningProgress` through the BFF.
3. Verify the returned JSON contains only the contract allowlist.
4. Repeat with malformed timestamps, enum values, and sequences; verify required corruption fails safely and invalid optional activity is omitted.
5. Remove `hosted.instances.provisioning-progress.v1`; verify the new action fails closed with the stable update response.

## Scenario 4: Timeline polling and refresh

1. Render a queued engine and verify Step 1 of 7 appears after the first progress result.
2. Advance fake responses through active stages and verify completed/current/pending markers.
3. Verify polling runs every five seconds only while visible and nonterminal.
4. Hide and restore the document; verify polling pauses and immediately refreshes on return.
5. Refresh with a later durable snapshot and verify no stage regression.
6. Return Ready; verify polling stops and Open Studio remains available.

## Scenario 5: Activity, failures, and accessibility

1. Expand activity and verify product-owned timestamped messages in stable order.
2. Verify provider messages are never rendered.
3. Render RecoveryRequired and Failed snapshots; verify animation stops, the correct stage is blocked, and safe guidance appears.
4. Simulate a temporary progress read failure after a good snapshot; verify the engine and previous progress remain visible with freshness guidance.
5. Exercise disclosure and refresh using keyboard navigation and screen-reader roles.
6. Render every state at desktop and 390 x 844 with reduced motion enabled; verify no page-level horizontal overflow.

## Focused Validation

From `elsa-control`:

```bash
dotnet test tests/Deployment/ElsaControl.Deployment.Azure.Tests/ElsaControl.Deployment.Azure.Tests.csproj --filter FullyQualifiedName~ManagedElsaProvisioningProgress
dotnet test tests/PackageCatalog/ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests/ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests.csproj --filter FullyQualifiedName~AzureProviderOperation
dotnet test tests/Hosting/ElsaControl.Api.Tests/ElsaControl.Api.Tests.csproj --filter 'FullyQualifiedName~ManagedElsaInstanceApiTests|FullyQualifiedName~CloudBffAuthorizationTests'
```

From `elsa-cloud`:

```bash
npm test -- --run src/lib/controlApi.test.ts supabase/functions/_shared/managedProvisioningProgressContract.test.ts src/pages/Dashboard.test.tsx
npm run lint
npm run build
```

All .NET commands must resolve the shared build-slot wrapper before running.

## Live Azure Release Proof

1. Publish Control with the new capability and progress endpoint.
2. Verify production compatibility includes `hosted.instances.provisioning-progress.v1` without extra browser-visible data.
3. Publish the Cloud BFF and UI from exact reviewed revisions.
4. Create one Hosted managed engine through `elsacloud.app`.
5. Capture accepted progress, at least two advancing intermediate stages, refresh recovery, activity expansion, and Ready on desktop and 390 x 844.
6. Inspect browser network responses and verify the explicit exclusion list does not appear.
7. Record exact revisions, build identifiers, timestamps, and outcomes on the rollout slice and parent #529.

No additional paid resource is created solely for testing if an already authorized Hosted create can supply the evidence. Cleanup follows the existing confirmed Delete flow and its separate authorization boundary.
