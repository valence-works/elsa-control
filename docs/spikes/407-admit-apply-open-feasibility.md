# Spike #407 — Admit / Apply / Open engineering feasibility

**Status:** Evidence complete. Q1 is a CEO/Sipke decision gate; this note recommends
identity bump (drawer A), not a supersede API.

**Date:** 2026-09-13

This spike answers Product UX questions Q1–Q6 from in-repo contracts. It makes no
SLO or sellable availability claims. It does not implement the #402 Releases
module.

Live context that this evidence explains: admit of image build 151 returned HTTP
409 `releaseCatalog.identity.conflict` against admitted build 149, both carrying
`ReleaseVersion` `3.8.0-preview.5567`.

## Decision gate (Q1)

**Recommended product decision: keep the current catalog identity tuple. Have
preview builds mint a unique `releaseVersion` per CI build (drawer A). Do not
build an audited supersede/replace admin API for Internal Alpha.**

Identity today is exactly `(distributionId, generation, releaseLine,
releaseVersion, registryClass)`. That tuple is hashed as `CatalogIdentityHash`,
enforced unique in EF, and is the only admit collision key besides
`(manifestDigest, registryClass)`.

```359:364:src/PackageCatalog/ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore/GovernedReleaseCatalogStore.cs
    private static string CatalogIdentity(GovernedReleaseCatalogEntry entry) => Fingerprint(string.Join('\n',
        Normalize(entry.Distribution.Id),
        Normalize(entry.Distribution.Generation),
        Normalize(entry.Distribution.ReleaseLine),
        Normalize(entry.Distribution.ReleaseVersion),
        Normalize(entry.RegistryClass)));
```

```181:183:src/PackageCatalog/ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore/Models/GovernedReleaseCatalogEntity.cs
        builder.HasIndex(x => x.CatalogIdentityHash).IsUnique();
        builder.HasIndex(x => new { x.ManifestDigest, x.RegistryClass }).IsUnique();
        builder.HasIndex(x => new { x.DistributionId, x.Generation, x.ReleaseLine, x.ReleaseVersion, x.RegistryClass }).IsUnique();
```

Admit is append-only. Same identity + same projection fingerprint returns
`Unchanged`. Same identity + different fingerprint returns
`catalog.identity.conflict`. There is no replace, retire, or supersede route on
`POST /api/admin/release-catalog/manifests`.

```43:49:src/Hosting/ElsaControl.Api/Admin/ReleaseCatalog/AdminReleaseCatalogEndpoints.cs
            if (!result.Accepted)
            {
                var status = result.WriteStatus == GovernedReleaseCatalogWriteStatus.Conflict
                    ? StatusCodes.Status409Conflict
                    : StatusCodes.Status422UnprocessableEntity;
                var code = result.WriteStatus == GovernedReleaseCatalogWriteStatus.Conflict
                    ? "releaseCatalog.identity.conflict"
```

```143:149:src/PackageCatalog/ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore/GovernedReleaseCatalogStore.cs
    private static GovernedReleaseCatalogWriteResult Existing(
        IReadOnlyList<GovernedReleaseCatalogEntity> existing,
        string fingerprint) =>
        existing.Count == 1
        && string.Equals(existing[0].ProjectionFingerprint, fingerprint, StringComparison.Ordinal)
            ? new(GovernedReleaseCatalogWriteStatus.Unchanged, ToEntries(existing[0]))
            : new(GovernedReleaseCatalogWriteStatus.Conflict, [], "catalog.identity.conflict");
```

```179:181:src/PackageCatalog/ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore/Models/GovernedReleaseCatalogEntity.cs
            // The application supplies immutable Control policy for this admitted
            // catalog identity; a policy change requires a separate transition path.
            CatalogLifecycle = Normalize(first.CatalogLifecycle),
```

Why **`releaseVersion` bump**, not **`generation` bump**:

- Instance intent does not carry `generation`. Catalog eligibility for create/apply
  matches `distributionId + releaseLine + requestedVersion + channel + paid
  registry + topology` and requires **exactly one** eligible row.
- Two admitted generations that share `3.8.0-preview.5567` would make
  `HasOneEligibleCatalogMatchAsync` return false (`instance.catalog-selection-unavailable`).
- A version such as `3.8.0-preview.5567-build.151` still belongs to release line
  `3.8` (`BelongsToLine` is a prefix check). That is the apply-compatible bump.
- #397 already published signed release `3.8.0-preview.5567-build.151`; the
  collision is that catalog identity used `release.version` `3.8.0-preview.5567`
  for both 149 and 151.

```445:466:src/Hosting/ElsaControl.Api/Workspace/ManagedElsaInstanceEndpoints.cs
    private static async Task<bool> HasOneEligibleCatalogMatchAsync(
        IGovernedReleaseCatalogStore catalog,
        ElsaInstanceIntent intent,
        CancellationToken cancellationToken)
    {
        var previewConsentDigest = intent.Release.PreviewManifestDigest;
        var entries = await catalog.QueryAsync(new GovernedReleaseCatalogQuery(
            DistributionId: intent.Release.DistributionId,
            ReleaseLine: intent.Release.ReleaseLine,
            ReleaseVersion: intent.Release.RequestedVersion,
            Channel: intent.Release.Channel,
            CatalogLifecycle: previewConsentDigest is null ? "supported" : null,
            RegistryClass: "paid",
            TopologyId: intent.Application.TopologyId), cancellationToken);
        var eligible = entries
            .Where(entry => IsEligibleCatalogLifecycle(entry.CatalogLifecycle, previewConsentDigest is not null))
            .Take(2)
            .ToArray();
        return eligible.Length == 1 &&
               (previewConsentDigest is null ||
                string.Equals(eligible[0].ManifestDigest, previewConsentDigest, StringComparison.OrdinalIgnoreCase));
    }
```

```917:921:src/Deployment/ElsaControl.Deployment.Abstractions/Instances/ElsaInstanceModels.cs
internal static class ElsaReleaseVersions
{
    public static bool BelongsToLine(string releaseLine, string version) =>
        string.Equals(releaseLine, version, StringComparison.OrdinalIgnoreCase) ||
        version.StartsWith(releaseLine + ".", StringComparison.OrdinalIgnoreCase);
}
```

Supersede (drawer C) is a later product if operators must mutate an already-sold
identity in place. It does not exist today. Building it now is a new audited
write path plus catalog-lifecycle transition, and it is the wrong recovery for
preview CI collisions.

## Q2 — Apply path

**Yes: `UpdateIntent` (PATCH instance) plus the existing lifecycle worker is the
supported “apply release X to instance Y” contract. There is no dedicated
`ApplyRelease` operation. Console should not invent one.**

- `PATCH /api/workspaces/{workspaceId}/instances/{instanceId}` accepts a new
  intent and calls `UpdateIntentAsync`. Same-line version change is a Patch
  transition and does not need extra approval.
- `POST .../operations` **rejects** `UpdateIntent`. That route is for Start,
  Stop, Restart, Reconcile, Recover, Retry, Delete, ApproveMinorUpgrade, and
  MajorMigration.
- Acceptance writes an outbox row. `ElsaInstanceLifecycleWorker` claims that
  outbox, resolves the plan from current intent, and submits provider work.
  `Reconcile` is the same worker path without changing intent.

```225:266:src/Hosting/ElsaControl.Api/Workspace/ManagedElsaInstanceEndpoints.cs
        group.MapMethods("/{instanceId:guid}", [HttpMethods.Patch], async (
            // ...
                var accepted = await lifecycle.UpdateIntentAsync(new ElsaInstanceIntentUpdateRequest(
                    workspaceId, instanceId, request.Intent, precondition.Value, key, request.Name, request.Reason,
                    access.AccountId), cancellationToken);
```

```297:298:src/Hosting/ElsaControl.Api/Workspace/ManagedElsaInstanceEndpoints.cs
            if (!Enum.IsDefined(request.Action) || request.Action is ElsaInstanceOperationAction.Create or ElsaInstanceOperationAction.UpdateIntent)
                return Problem("instance.operation-invalid", "The requested operation is not supported on this route.", StatusCodes.Status422UnprocessableEntity);
```

```566:569:src/Deployment/ElsaControl.Deployment.Abstractions/Instances/ElsaInstanceStateMachine.cs
            case ElsaInstanceOperationAction.UpdateIntent:
                EnsureNotDeleted(instance, action);
                next = ApplyIntentUpdate(instance, requestedIntent ?? throw new ArgumentNullException(nameof(requestedIntent)),
                    minorApproved: false, migrationAuthorized: false);
```

```779:786:src/Deployment/ElsaControl.Deployment.Abstractions/Instances/ElsaInstanceStateMachine.cs
        var kind = !sameDistribution
            ? ElsaReleaseTransitionKind.Major
            : sameLine
                ? string.Equals(current.Version, target.Version, StringComparison.OrdinalIgnoreCase)
                    ? ElsaReleaseTransitionKind.None
                    : ElsaReleaseTransitionKind.Patch
```

Console today can create (`managedElsaApi.ts`) but has no PATCH/apply client.
#402 can wrap PATCH + operation polling. Use `Reconcile` only when intent is
unchanged (same `requestedVersion`) and the operator needs a re-resolve.

If CEO later chooses in-place supersede, apply would be `Reconcile` (transition
kind `None`), not `UpdateIntent`.

## Q3 — Entitlement capability fields

**Do not treat this as unfinished #120 work.** Organization entitlement snapshots
already exist. #384 is the Stripe trial→paid **live dry-run** over those
snapshots.

`OrganizationEntitlementSnapshot` fields:

| Field | Role |
| --- | --- |
| `CanCreateCustomSources` | catalog capability |
| `MaxSources` | limit |
| `MaxWorkspaces` | limit |
| `MaxInstances` | managed-instance cap |
| `MaxPackagesIndexed` | optional limit |
| `MaxVersionsPerPackage` | optional limit |
| `MaxSyncsPerDay` | optional limit |
| `PrivateFeedsEnabled` | catalog capability |
| `ManagedHostingEnabled` | managed-hosting capability |
| `ManagedHostingExpiresAt` | clock-checked expiry; null = no expiry |
| `DeploymentTargetsEnabled` | deployment-target capability |
| `SubscriptionState` | projected lifecycle (`Trial`/`Active`/…) |
| `SubscriptionId` | billing subscription link |
| timestamps | `SyncedAt`, `CreatedAt`, `UpdatedAt` |

Trial vs paid is **`SubscriptionState`**, not a second capability schema. Billing
projection writes only `SubscriptionState` / `SubscriptionId` and preserves
capability and limit fields.

Commercial gate for create/update requires `ManagedHostingEnabled`, unexpired
`ManagedHostingExpiresAt`, a projected `SubscriptionState`, and denies
`Constrained` / `Suspended` / `Retained` / `Deleted`. `Trial` and `Active` both
pass that gate. Stop/Delete are ungated.

Billing API capability strings derived from the snapshot:
`managed-hosting`, `deployment-targets`, `custom-sources`, `private-feeds`.

Internal dogfood (#312) grants `ManagedHostingEnabled` + `MaxInstances` +
expiry on the same snapshot. That is not Stripe conversion evidence; #384 is.

Workspace snapshots (`WorkspaceEntitlementSnapshot`) remain the older
source/package-feed limits only. They are not the managed-hosting contract.

## Q4 — Admit auth in production

**Yes. With `AllowAuthenticatedCustomerSession=false`, Admit / Releases UI must
use a `control_admin` session or the admin API key. An ordinary authenticated
customer session is forbidden.**

```53:61:src/Hosting/ElsaControl.Api/Authentication/AdminAuthorization.cs
            if (HasApiKeyIdentity(context.User) || HasControlAdminRole(context.User))
            {
                context.Succeed(requirement);
                return Task.CompletedTask;
            }

            if (options.Value.AllowAuthenticatedCustomerSession &&
                HasAuthenticatedIdentity(context.User))
                context.Succeed(requirement);
```

Production config and published AppHost both force the flag off. Admit is
`RequireAuthorization(AdminAuthorization.Policy)`. Tests already prove a
customer session cannot `POST /api/admin/release-catalog/manifests`.

#402 Releases/Admit UI is an admin-console surface, not a customer workspace
surface.

## Q5 — #396 tombstone / slug `dogfood`

**`dogfood` can be reused only after finalize sets `DeletedAt`.** The unique
index is filtered `DeletedAt IS NULL`. Create preflight also ignores rows with
`DeletedAt`.

A row stuck in **Deleting** (Azure gone, `DeletedAt` still null) continues to
reserve the slug. That is the #396 bug. There is no force-finalize admin API in
this investigation.

```1063:1065:src/PackageCatalog/ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore/Models/CatalogModelConfiguration.cs
        builder.HasIndex(x => new { x.WorkspaceId, x.Slug })
            .IsUnique()
            .HasFilter("DeletedAt IS NULL");
```

The create wizard slugifies the display name and reports 409 as “That instance
address is already in use.” It does **not** auto-suggest an alternate. Product
may add `dogfood-2` suggestion as UX only; it is not required by the store.
Application names already include the instance id so a later reusable slug does
not collide on the deployment-application name.

## Q6 — `canOpen` after #397

**`canOpen` in the instance JSON is the computed openable flag, not the raw Open
permission.** It stays false for Control-detectable incompleteness. It does
**not** observe image-side cookie-scheme failures.

Openable requires all of:

1. caller has `ManagedElsaInstancePermissions.Open`
2. desired `Running` + observed `Ready` + `Healthy`
3. `CurrentDeploymentReference.ManagedHandoff == true`
4. a current identity binding

Otherwise the API returns `canOpen: false` plus
`identityBindingState` / `unavailableReason`
(`not-authorized`, `instance-unavailable`, `handoff-unavailable`,
`identity-unavailable`).

#397 is an image-side missing
`ElsaStudio.ExternalAuthentication.Cookie` scheme. Control never reads that
scheme. After the image fix is admitted and applied, `canOpen` can become true
as soon as the four Control gates hold — even before live Open is re-proved.
V5 honesty: treat `canOpen` as Control-side readiness only. Image-side auth
failures remain a live-proof residual, not a `canOpen=false` signal.

## Implications for Releases conflict drawer (#402 A/B/C)

| Choice | Exists today? | Copy for #402 |
| --- | --- | --- |
| **A. New identity** | Yes, after producer bumps `releaseVersion` (preferred: include `build.N`) | Primary recovery for 151 vs 149. “Cannot Apply until Admitted.” Link producer identity bump. Do not offer generation-only bump as a working apply path. |
| **B. Idempotent retry** | Yes | Show only when incoming projection fingerprint matches the existing row (`Unchanged`). Safe retry. |
| **C. Governed supersede** | **No** | Hide or disable. There is no audited replace API. Never offer silent overwrite. Park C until a later CEO decision after Internal Alpha. |

Happy path remains **Admit → Apply (PATCH UpdateIntent) → Open**. Apply cannot
start until admit returns `Stored` or `Unchanged`.

## Unknowns

- Exact producer mapper defaults (`release.generation` → `producer-2.0.0`,
  `release.id` vs `release.version`) live in `elsa-production-image`, not this
  repo. Live 149/151 collision matches that split; this repo cannot cite the
  mapper file.
- Whether a later sold/stable identity ever needs in-place supersede remains a
  CEO product call. Not required to unblock preview admit.
- #396 force-finalize contract is still open; this spike only states when the
  current unique index releases a slug.
