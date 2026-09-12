using ElsaControl.Api.Authentication;
using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Core.Instances;
using ElsaControl.Deployment.Core.Workspace;
using ElsaControl.PackageCatalog.Core.Accounts;
using ElsaControl.RuntimeBuilder.Abstractions.ReleaseCatalog;
using Microsoft.AspNetCore.Mvc;

namespace ElsaControl.Api.Workspace;

/// <summary>
/// Customer-facing managed-instance routes. The legacy managed-elsa list is kept
/// for the console during migration; the workspace instances routes are canonical.
/// </summary>
public static class ManagedElsaInstanceEndpoints
{
    internal const string HandoffUnavailableReason = "Managed sign-in is not configured for this instance's current deployment.";

    private static readonly ManagedElsaInstanceLaunchProfile InitialLaunchProfile = new(
        "West Europe Dedicated", "Managed hosting in West Europe.",
        "managed", "westeurope", "dedicated", "standard-small", "public", "managed");

    public static IEndpointRouteBuilder MapManagedElsaInstanceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        MapLegacyList(endpoints);

        var group = endpoints.MapGroup("/api/workspaces/{workspaceId:guid}/instances")
            .WithTags("Managed Elsa Instances");
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "private, no-store";
            context.HttpContext.Response.Headers.Pragma = "no-cache";
            return await next(context);
        });

        group.MapGet("", async (
            Guid workspaceId,
            int? page,
            int? pageSize,
            HttpContext context,
            WorkspacePermissionService permissions,
            IManagedElsaInstanceApiStore instances,
            [FromServices] IManagedElsaInstanceIdentityStore identities,
            CancellationToken cancellationToken) =>
        {
            var access = context.GetWorkspaceAccess();
            var canOpen = (await permissions.GetEffectivePermissionsAsync(workspaceId, access.AccountId, cancellationToken))
                .Has(ManagedElsaInstancePermissions.Open);
            var currentPage = Math.Max(page ?? 1, 1);
            var currentPageSize = Math.Clamp(pageSize ?? 50, 1, 100);
            var offset = (long)(currentPage - 1) * currentPageSize;
            var pageResult = await instances.ListInstancesAsync(workspaceId, currentPage, currentPageSize, cancellationToken);
            // Identity bindings are only ever surfaced when the caller can open
            // instances, so skip the lookup entirely otherwise, and resolve the
            // whole page's bindings in a single query rather than one per item.
            var identityByInstanceId = canOpen
                ? await identities.FindOpenableManyAsync(
                    access.OrganizationId,
                    pageResult.Items.Select(x => x.Id).ToArray(),
                    cancellationToken)
                : new Dictionary<Guid, ManagedElsaInstanceIdentity>();
            var items = new List<ManagedElsaInstanceResponse>(pageResult.Items.Count);
            foreach (var instance in pageResult.Items)
                items.Add(ToResponse(instance, canOpen, workspaceId,
                    identityByInstanceId.GetValueOrDefault(instance.Id)));
            return Results.Ok(new ManagedElsaInstanceListResponse(items, currentPage, currentPageSize, pageResult.TotalCount,
                offset + currentPageSize < pageResult.TotalCount));
        }).RequireWorkspaceAccess();

        group.MapGet("/onboarding-options", async (
            Guid workspaceId,
            HttpContext context,
            IElsaInstanceCommercialGate commercialGate,
            IGovernedReleaseCatalogStore catalog,
            CancellationToken cancellationToken) =>
        {
            var access = context.GetWorkspaceAccess();
            var commercialDecision = await commercialGate.EvaluateAsync(
                access.OrganizationId, ElsaInstanceOperationAction.Create, cancellationToken: cancellationToken);
            if (!commercialDecision.Allowed)
                return Problem(commercialDecision.Code, commercialDecision.Summary, StatusCodes.Status422UnprocessableEntity);

            // Match the lifecycle resolver's fail-closed eligibility boundary so
            // every option shown here can be resolved when the customer submits it.
            var entries = await catalog.QueryAsync(new GovernedReleaseCatalogQuery(
                CatalogLifecycle: "supported",
                RegistryClass: "paid"), cancellationToken);
            var supportedIdentities = entries
                .Select(ReleaseIdentity)
                .ToHashSet();
            // The durable resolver requires one exact governed row. Do not offer
            // a display-equivalent choice when multiple immutable admissions
            // would make that later resolution ambiguous.
            var releases = entries
                .GroupBy(ReleaseIdentity)
                .Where(group => group.Take(2).Count() == 1)
                .Select(group => group.Single())
                .Select(entry => new ManagedElsaInstanceReleaseOption(
                    entry.Distribution.Id,
                    entry.Distribution.ReleaseLine,
                    entry.Distribution.ReleaseVersion,
                    entry.Distribution.Channel,
                    entry.Topology.Id))
                .OrderBy(x => x.ReleaseLine, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Version, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.DistributionId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.TopologyId, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var previewEntries = await catalog.QueryAsync(new GovernedReleaseCatalogQuery(
                CatalogLifecycle: "preview",
                RegistryClass: "paid"), cancellationToken);
            var previewReleases = previewEntries
                .GroupBy(ReleaseIdentity)
                .Where(group => group.Take(2).Count() == 1 &&
                                !supportedIdentities.Contains(group.Key))
                .Select(group => group.Single())
                .Select(entry => new ManagedElsaInstancePreviewReleaseOption(
                    entry.Distribution.Id,
                    entry.Distribution.ReleaseLine,
                    entry.Distribution.ReleaseVersion,
                    entry.Distribution.Channel,
                    entry.Topology.Id,
                    entry.ManifestDigest))
                .OrderBy(x => x.ReleaseLine, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Version, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.DistributionId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.TopologyId, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return Results.Ok(new ManagedElsaInstanceOnboardingOptionsResponse(releases, InitialLaunchProfile, previewReleases));
        }).RequireWorkspaceAccess();

        group.MapPost("", async (
            Guid workspaceId,
            ManagedElsaInstanceCreateRequest request,
            HttpContext context,
            IElsaInstanceCommercialGate commercialGate,
            IGovernedReleaseCatalogStore catalog,
            WorkspacePermissionService permissions,
            ElsaInstanceLifecycleService lifecycle,
            IElsaInstanceLifecycleStore lifecycleStore,
            IManagedElsaInstanceApiStore queries,
            [FromServices] IManagedElsaInstanceIdentityStore identities,
            CancellationToken cancellationToken) =>
        {
            var keyResult = ReadIdempotencyKey(context);
            if (keyResult.State == IdempotencyKeyState.Missing)
                return Problem("instance.idempotency-key-required", "Idempotency-Key is required for instance creation.", StatusCodes.Status400BadRequest);
            if (keyResult.State == IdempotencyKeyState.Invalid)
                return InvalidIdempotencyKey();
            var key = keyResult.Value!;
            if (request.Intent is null || string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Slug))
                return Problem("instance.shape-invalid", "Instance name, slug and intent are required.", StatusCodes.Status422UnprocessableEntity);

            var access = context.GetWorkspaceAccess();
            var commercialDecision = await commercialGate.EvaluateAsync(
                access.OrganizationId, ElsaInstanceOperationAction.Create, cancellationToken: cancellationToken);
            if (!commercialDecision.Allowed)
                return Problem(commercialDecision.Code, commercialDecision.Summary, StatusCodes.Status422UnprocessableEntity);
            if (!MatchesInitialLaunchProfile(request.Intent.Placement) ||
                !await HasOneEligibleCatalogMatchAsync(catalog, request.Intent, cancellationToken))
                return Problem("instance.catalog-selection-unavailable", "The selected managed release or launch profile is unavailable.", StatusCodes.Status422UnprocessableEntity);

            try
            {
                var normalizedSlug = ElsaInstanceSlug.Normalize(request.Slug);
                // Let the lifecycle service own idempotent replay. A slug preflight
                // must not reject a retry of the original request after its first
                // commit made the slug visible.
                var existingOperation = await lifecycleStore.FindOperationByKeyAsync(
                    workspaceId, key, action: ElsaInstanceOperationAction.Create,
                    idempotencyScope: ElsaInstanceLifecycleService.CreateIdempotencyScope,
                    cancellationToken: cancellationToken);
                if (existingOperation is null && await queries.SlugExistsAsync(workspaceId, normalizedSlug, cancellationToken))
                    return Problem("instance.slug-conflict", "The instance slug is already in use in this workspace.", StatusCodes.Status409Conflict);

                var accepted = await lifecycle.CreateAsync(new ElsaInstanceCreateRequest(
                    access.OrganizationId, workspaceId, request.Name, normalizedSlug, request.Intent, key,
                    ActorAccountId: access.AccountId), cancellationToken);
                return await AcceptedAsync(workspaceId, accepted, queries, permissions, identities, access.AccountId, cancellationToken);
            }
            catch (ElsaInstanceLifecycleConflictException exception)
            {
                return Problem(ConflictCode(exception), "The request conflicts with the current instance state.", ConflictStatusCode(exception));
            }
            catch (ArgumentException)
            {
                return Problem("instance.shape-invalid", "The instance request is invalid.", StatusCodes.Status422UnprocessableEntity);
            }
        }).RequireWorkspaceAccess(WorkspaceOperation.MutateWorkspaceResource);

        group.MapGet("/{instanceId:guid}", async (
            Guid workspaceId,
            Guid instanceId,
            HttpContext context,
            WorkspacePermissionService permissions,
            IElsaInstanceLifecycleStore lifecycle,
            [FromServices] IManagedElsaInstanceIdentityStore identities,
            CancellationToken cancellationToken) =>
        {
            var instance = await lifecycle.GetInstanceAsync(workspaceId, instanceId, cancellationToken);
            if (instance is null)
                return Results.NotFound();
            var canOpen = (await permissions.GetEffectivePermissionsAsync(workspaceId, context.GetWorkspaceAccess().AccountId, cancellationToken))
                .Has(ManagedElsaInstancePermissions.Open);
            var response = await ToResponseAsync(instance, canOpen, workspaceId, identities, cancellationToken);
            context.Response.Headers.ETag = response.ETag;
            return Results.Ok(response);
        }).RequireWorkspaceAccess();

        group.MapGet("/{instanceId:guid}/health", async (
            Guid workspaceId,
            Guid instanceId,
            IManagedElsaInstanceOperationalStore operationalStore,
            ManagedLifecycleOperationalHealthEvaluator evaluator,
            CancellationToken cancellationToken) =>
        {
            var snapshot = await operationalStore.GetSnapshotAsync(workspaceId, instanceId, cancellationToken);
            if (snapshot is null)
                return Results.NotFound();

            return Results.Ok(ToOperationalHealthResponse(snapshot, evaluator.Evaluate(snapshot)));
        }).RequireWorkspaceAccess();

        group.MapMethods("/{instanceId:guid}", [HttpMethods.Patch], async (
            Guid workspaceId,
            Guid instanceId,
            ManagedElsaInstancePatchRequest request,
            HttpContext context,
            IElsaInstanceCommercialGate commercialGate,
            IGovernedReleaseCatalogStore catalog,
            WorkspacePermissionService permissions,
            IElsaInstanceLifecycleStore store,
            ElsaInstanceLifecycleService lifecycle,
            IManagedElsaInstanceApiStore queries,
            [FromServices] IManagedElsaInstanceIdentityStore identities,
            CancellationToken cancellationToken) =>
        {
            var precondition = ReadIfMatch(context.Request);
            if (precondition is null)
                return Problem("instance.if-match-required", "If-Match is required for instance updates.", StatusCodes.Status428PreconditionRequired);
            var keyResult = ReadIdempotencyKey(context);
            if (keyResult.State == IdempotencyKeyState.Missing)
                return Problem("instance.idempotency-key-required", "Idempotency-Key is required for instance updates.", StatusCodes.Status400BadRequest);
            if (keyResult.State == IdempotencyKeyState.Invalid)
                return InvalidIdempotencyKey();
            var key = keyResult.Value!;
            var existing = await store.GetInstanceAsync(workspaceId, instanceId, cancellationToken);
            if (existing is null)
                return Results.NotFound();
            if (request.Intent is null && request.Name is null)
                return Problem("instance.shape-invalid", "At least one mutable instance field is required.", StatusCodes.Status422UnprocessableEntity);
            var access = context.GetWorkspaceAccess();
            var commercialDecision = await commercialGate.EvaluateAsync(
                access.OrganizationId, ElsaInstanceOperationAction.UpdateIntent, cancellationToken: cancellationToken);
            if (!commercialDecision.Allowed)
                return Problem(commercialDecision.Code, commercialDecision.Summary, StatusCodes.Status422UnprocessableEntity);
            if (request.Intent is not null &&
                !await HasOneEligibleCatalogMatchAsync(catalog, request.Intent, cancellationToken))
                return Problem("instance.catalog-selection-unavailable", "The selected managed release is unavailable.", StatusCodes.Status422UnprocessableEntity);
            try
            {
                var accepted = await lifecycle.UpdateIntentAsync(new ElsaInstanceIntentUpdateRequest(
                    workspaceId, instanceId, request.Intent, precondition.Value, key, request.Name, request.Reason,
                    access.AccountId), cancellationToken);
                return await AcceptedAsync(workspaceId, accepted, queries, permissions, identities, access.AccountId, cancellationToken);
            }
            catch (ElsaInstanceLifecycleConflictException exception)
            {
                return Problem(ConflictCode(exception), "The request conflicts with the current instance state.", ConflictStatusCode(exception));
            }
            catch (ArgumentException)
            {
                return Problem("instance.shape-invalid", "The instance request is invalid.", StatusCodes.Status422UnprocessableEntity);
            }
        }).RequireWorkspaceAccess(WorkspaceOperation.MutateWorkspaceResource);

        group.MapPost("/{instanceId:guid}/operations", async (
            Guid workspaceId,
            Guid instanceId,
            ManagedElsaInstanceOperationRequest request,
            HttpContext context,
            IElsaInstanceCommercialGate commercialGate,
            IGovernedReleaseCatalogStore catalog,
            WorkspacePermissionService permissions,
            ElsaInstanceLifecycleService lifecycle,
            IManagedElsaInstanceApiStore queries,
            [FromServices] IManagedElsaInstanceIdentityStore identities,
            CancellationToken cancellationToken) =>
        {
            var keyResult = ReadIdempotencyKey(context);
            if (keyResult.State == IdempotencyKeyState.Missing)
                return Problem("instance.idempotency-key-required", "Idempotency-Key is required for instance operations.", StatusCodes.Status400BadRequest);
            if (keyResult.State == IdempotencyKeyState.Invalid)
                return InvalidIdempotencyKey();
            var key = keyResult.Value!;
            if (!Enum.IsDefined(request.Action) || request.Action is ElsaInstanceOperationAction.Create or ElsaInstanceOperationAction.UpdateIntent)
                return Problem("instance.operation-invalid", "The requested operation is not supported on this route.", StatusCodes.Status422UnprocessableEntity);
            if (!HasValidOperationShape(request))
                return Problem("instance.operation-shape-invalid", "The operation body contains fields that do not apply to this action.", StatusCodes.Status422UnprocessableEntity);
            var expectedVersion = ReadIfMatch(context.Request);
            if (expectedVersion is null)
                return Problem("instance.if-match-required", "A strong If-Match header is required for instance operations.", StatusCodes.Status428PreconditionRequired);
            if (request.ExpectedVersion is { } bodyVersion && expectedVersion.Value != bodyVersion)
                return Problem("instance.version-conflict", "If-Match and expectedVersion do not agree.", StatusCodes.Status412PreconditionFailed);
            var access = context.GetWorkspaceAccess();

            if (request.Action == ElsaInstanceOperationAction.Delete)
            {
                if (!(await permissions.GetEffectivePermissionsAsync(workspaceId, access.AccountId, cancellationToken))
                    .Has(ManagedElsaInstancePermissions.Delete))
                    return Problem("instance.delete-permission-required", "Delete permission is required.", StatusCodes.Status403Forbidden);
                if (request.DeleteConfirmationId is null || request.DeleteConfirmationId == Guid.Empty)
                    return Problem("instance.delete-confirmation-required", "A delete confirmation is required.", StatusCodes.Status400BadRequest);

            }

            var commercialDecision = await commercialGate.EvaluateAsync(
                access.OrganizationId, request.Action, cancellationToken: cancellationToken);
            if (!commercialDecision.Allowed)
                return Problem(commercialDecision.Code, commercialDecision.Summary, StatusCodes.Status422UnprocessableEntity);
            if (request.Intent is not null &&
                !await HasOneEligibleCatalogMatchAsync(catalog, request.Intent, cancellationToken))
                return Problem("instance.catalog-selection-unavailable", "The selected managed release is unavailable.", StatusCodes.Status422UnprocessableEntity);

            try
            {
                var actorAccountId = context.GetWorkspaceAccess().AccountId;
                var operationRequest = new ElsaInstanceLifecycleRequest(
                    workspaceId, instanceId, expectedVersion.Value, key, request.Reason,
                    request.DeleteConfirmationId, actorAccountId);
                ElsaInstanceLifecycleAcceptance accepted;
                if (request.Action is ElsaInstanceOperationAction.ApproveMinorUpgrade or ElsaInstanceOperationAction.MajorMigration)
                {
                    if (request.Intent is null)
                        return Problem("instance.intent-required", "This operation requires a new intent.", StatusCodes.Status422UnprocessableEntity);
                    var intentRequest = new ElsaInstanceIntentUpdateRequest(workspaceId, instanceId, request.Intent,
                        expectedVersion.Value, key, request.Name, request.Reason, actorAccountId);
                    accepted = request.Action == ElsaInstanceOperationAction.ApproveMinorUpgrade
                        ? await lifecycle.ApproveMinorUpgradeAsync(intentRequest, cancellationToken)
                        : await lifecycle.MajorMigrationAsync(intentRequest, cancellationToken);
                }
                else
                {
                    accepted = request.Action switch
                    {
                        ElsaInstanceOperationAction.Start => await lifecycle.StartAsync(operationRequest, cancellationToken),
                        ElsaInstanceOperationAction.Stop => await lifecycle.StopAsync(operationRequest, cancellationToken),
                        ElsaInstanceOperationAction.Restart => await lifecycle.RestartAsync(operationRequest, cancellationToken),
                        ElsaInstanceOperationAction.Reconcile => await lifecycle.ReconcileAsync(operationRequest, cancellationToken),
                        ElsaInstanceOperationAction.Recover => await lifecycle.RecoverAsync(operationRequest, cancellationToken),
                        ElsaInstanceOperationAction.Retry => await lifecycle.RetryAsync(operationRequest, cancellationToken),
                        ElsaInstanceOperationAction.Delete => await lifecycle.DeleteAsync(operationRequest, cancellationToken),
                        _ => throw new ArgumentOutOfRangeException(nameof(request.Action))
                    };
                }
                return await AcceptedAsync(workspaceId, accepted, queries, permissions, identities, access.AccountId, cancellationToken);
            }
            catch (ElsaInstanceLifecycleConflictException exception)
            {
                return Problem(ConflictCode(exception), "The request conflicts with the current instance state.", ConflictStatusCode(exception));
            }
            catch (ElsaInstanceDeleteConfirmationException)
            {
                return Problem("instance.delete-confirmation-invalid", "The delete confirmation is invalid or unavailable.", StatusCodes.Status409Conflict);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
            catch (ArgumentException)
            {
                return Problem("instance.operation-invalid", "The requested operation is invalid.", StatusCodes.Status422UnprocessableEntity);
            }
        }).RequireWorkspaceAccess(WorkspaceOperation.MutateWorkspaceResource);

        group.MapGet("/{instanceId:guid}/operations/{operationId:guid}", async (
            Guid workspaceId,
            Guid instanceId,
            Guid operationId,
            HttpContext context,
            IElsaInstanceLifecycleStore lifecycle,
            IManagedElsaInstanceApiStore queries,
            CancellationToken cancellationToken) =>
        {
            var operation = await queries.GetOperationAsync(workspaceId, instanceId, operationId, cancellationToken);
            if (operation is null)
                return Results.NotFound();
            var instance = await lifecycle.GetInstanceAsync(workspaceId, instanceId, cancellationToken);
            if (instance is null)
                return Results.NotFound();
            context.Response.Headers.ETag = ETag(instance.Version);
            return Results.Ok(ToOperationResponse(workspaceId, instanceId, operation));
        }).RequireWorkspaceAccess();

        group.MapGet("/{instanceId:guid}/revisions", async (
            Guid workspaceId, Guid instanceId,
            IElsaInstanceLifecycleStore lifecycle,
            IManagedElsaInstanceApiStore queries,
            CancellationToken cancellationToken) =>
        {
            if (await lifecycle.GetInstanceAsync(workspaceId, instanceId, cancellationToken) is null)
                return Results.NotFound();
            return Results.Ok(new ManagedElsaInstanceRevisionsResponse(await queries.ListRevisionsAsync(workspaceId, instanceId, cancellationToken)));
        }).RequireWorkspaceAccess();

        group.MapGet("/{instanceId:guid}/resolved-plans/{planId}", async (
            Guid workspaceId, Guid instanceId, string planId,
            IElsaInstanceLifecycleStore lifecycle,
            IManagedElsaInstanceApiStore queries,
            CancellationToken cancellationToken) =>
        {
            if (await lifecycle.GetInstanceAsync(workspaceId, instanceId, cancellationToken) is null)
                return Results.NotFound();
            var plan = await queries.GetResolvedPlanAsync(workspaceId, instanceId, planId, cancellationToken);
            return plan is null ? Results.NotFound() : Results.Ok(plan);
        }).RequireWorkspaceAccess();

        group.MapGet("/{instanceId:guid}/deployments", async (
            Guid workspaceId, Guid instanceId,
            IElsaInstanceLifecycleStore lifecycle,
            IManagedElsaInstanceApiStore queries,
            CancellationToken cancellationToken) =>
        {
            if (await lifecycle.GetInstanceAsync(workspaceId, instanceId, cancellationToken) is null)
                return Results.NotFound();
            return Results.Ok(new ManagedElsaInstanceDeploymentsResponse(await queries.ListDeploymentsAsync(workspaceId, instanceId, cancellationToken)));
        }).RequireWorkspaceAccess();

        group.MapGet("/{instanceId:guid}/audit", async (
            Guid workspaceId, Guid instanceId,
            IElsaInstanceLifecycleStore lifecycle,
            IManagedElsaInstanceApiStore queries,
            CancellationToken cancellationToken) =>
        {
            if (await lifecycle.GetInstanceAsync(workspaceId, instanceId, cancellationToken) is null)
                return Results.NotFound();
            var events = await queries.ListAuditAsync(workspaceId, instanceId, cancellationToken);
            return Results.Ok(new ManagedElsaInstanceAuditResponse(events.Select(RedactAudit).ToList()));
        }).RequireWorkspaceAccess();

        return endpoints;
    }

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

    private static bool IsEligibleCatalogLifecycle(string lifecycle, bool allowPreview) =>
        string.Equals(lifecycle, "supported", StringComparison.OrdinalIgnoreCase) ||
        allowPreview && string.Equals(lifecycle, "preview", StringComparison.OrdinalIgnoreCase);

    private static (string DistributionId, string ReleaseLine, string Version, string Channel, string TopologyId)
        ReleaseIdentity(GovernedReleaseCatalogEntry entry) =>
        (
            entry.Distribution.Id.Trim().ToUpperInvariant(),
            entry.Distribution.ReleaseLine.Trim().ToUpperInvariant(),
            entry.Distribution.ReleaseVersion.Trim().ToUpperInvariant(),
            entry.Distribution.Channel.Trim().ToUpperInvariant(),
            entry.Topology.Id.Trim().ToUpperInvariant());

    private static ManagedElsaInstanceOperationalHealthResponse ToOperationalHealthResponse(
        ManagedLifecycleOperationalHealthSnapshot snapshot,
        ManagedLifecycleOperationalHealthResult result) =>
        new(
            result.Status,
            result.DiagnosticCode,
            result.EvaluatedAt,
            snapshot.ReconciledAt,
            snapshot.Operation is null
                ? null
                : new ManagedElsaInstanceOperationalOperationResponse(
                    snapshot.Operation.Id,
                    snapshot.Operation.State,
                    snapshot.Operation.AttemptNumber,
                    snapshot.Operation.AcceptedAt,
                    snapshot.Operation.StartedAt,
                    snapshot.Operation.HeartbeatAt,
                    snapshot.Operation.LastProgressAt,
                    snapshot.Operation.DiagnosticCode),
            snapshot.Run is null
                ? null
                : new ManagedElsaInstanceOperationalRunResponse(
                    snapshot.Run.Id,
                    snapshot.Run.Status,
                    snapshot.Run.AttemptNumber,
                    snapshot.Run.QueuedAt,
                    snapshot.Run.StartedAt,
                    snapshot.Run.HeartbeatAt,
                    snapshot.Run.LastProgressAt,
                    snapshot.Run.DiagnosticCode),
            result.Alerts.Select(alert => new ManagedElsaInstanceOperationalAlertResponse(
                alert.Code,
                alert.Severity,
                alert.DedupeIdentity)).ToArray());

    private static bool MatchesInitialLaunchProfile(ElsaPlacementIntent placement) =>
        Equal(placement.TargetMode, InitialLaunchProfile.TargetMode) &&
        Equal(placement.RegionCode, InitialLaunchProfile.RegionCode) &&
        Equal(placement.IsolationProfile, InitialLaunchProfile.IsolationProfile) &&
        Equal(placement.CapacityProfile, InitialLaunchProfile.CapacityProfile) &&
        Equal(placement.NetworkOutcome, InitialLaunchProfile.NetworkOutcome) &&
        Equal(placement.DomainOutcome, InitialLaunchProfile.DomainOutcome);

    private static bool Equal(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static void MapLegacyList(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/workspaces/{workspaceId:guid}/managed-elsa/instances", async (
            Guid workspaceId, HttpContext context, WorkspacePermissionService permissions,
            IManagedElsaInstanceCatalog instances, CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            context.Response.Headers.Pragma = "no-cache";
            var canOpen = (await permissions.GetEffectivePermissionsAsync(workspaceId, context.GetWorkspaceAccess().AccountId, cancellationToken))
                .Has(ManagedElsaInstancePermissions.Open);
            var summaries = await instances.ListAsync(workspaceId, cancellationToken);
            return Results.Ok(summaries.Select(summary => ToLegacyResponse(summary, canOpen)).ToList());
        }).WithTags("Managed Elsa Instances").RequireWorkspaceAccess();
    }

    private static async Task<IResult> AcceptedAsync(
        Guid workspaceId,
        ElsaInstanceLifecycleAcceptance accepted,
        IManagedElsaInstanceApiStore queries,
        WorkspacePermissionService permissions,
        IManagedElsaInstanceIdentityStore identities,
        Guid accountId,
        CancellationToken cancellationToken)
    {
        var location = $"/api/workspaces/{workspaceId:D}/instances/{accepted.Instance.Id:D}/operations/{accepted.Operation.Id:D}";
        var operation = await queries.GetOperationAsync(
            workspaceId, accepted.Instance.Id, accepted.Operation.Id, cancellationToken) ??
            new ElsaInstanceOperationSummary(accepted.Operation.Id, accepted.Operation.InstanceId,
                accepted.Operation.Action, accepted.Operation.State, accepted.Operation.ExpectedVersion,
                accepted.Operation.AttemptNumber, accepted.Operation.AcceptedAt, null, null,
                null, null,
                null, null, null, null);
        return Results.Accepted(location, new ManagedElsaInstanceAcceptedResponse(
            await ToResponseAsync(accepted.Instance,
                (await permissions.GetEffectivePermissionsAsync(workspaceId, accountId, cancellationToken))
                .Has(ManagedElsaInstancePermissions.Open),
                workspaceId, identities, cancellationToken),
            ToOperationResponse(workspaceId, accepted.Instance.Id, operation),
            new Dictionary<string, string> { ["self"] = location }));
    }

    private static async Task<ManagedElsaInstanceResponse> ToResponseAsync(
        ElsaInstance instance,
        bool canOpen,
        Guid workspaceId,
        IManagedElsaInstanceIdentityStore identities,
        CancellationToken cancellationToken)
    {
        var healthy = instance.DesiredLifecycle == ElsaDesiredLifecycle.Running &&
                      instance.ObservedLifecycle == ElsaObservedLifecycle.Ready &&
                      instance.Health == ElsaInstanceHealth.Healthy;
        var identity = canOpen && healthy
            ? await identities.FindOpenableAsync(instance.OrganizationId, instance.Id, cancellationToken)
            : null;
        return ToResponse(instance, canOpen, workspaceId, identity);
    }

    internal static ManagedElsaInstanceResponse ToResponse(
        ElsaInstance instance,
        bool canOpen,
        Guid workspaceId,
        ManagedElsaInstanceIdentity? identity = null)
    {
        var healthy = instance.DesiredLifecycle == ElsaDesiredLifecycle.Running &&
                      instance.ObservedLifecycle == ElsaObservedLifecycle.Ready &&
                      instance.Health == ElsaInstanceHealth.Healthy;
        var currentIdentity = identity is { } candidate && candidate.OrganizationId == instance.OrganizationId &&
                              candidate.WorkspaceId == workspaceId && candidate.InstanceId == instance.Id
            ? candidate
            : null;
        // Open lands on the runtime's own handoff endpoint, which exists only when the provider configured the
        // current deployment for it (a release declaring managed-elsa-handoff-v1, deployed with Control's inputs).
        var handoffConfigured = instance.CurrentDeploymentReference?.ManagedHandoff == true;
        var openable = canOpen && healthy && handoffConfigured && currentIdentity is not null;
        return new ManagedElsaInstanceResponse(instance.OrganizationId, instance.Id, instance.Name, instance.Slug,
            instance.DesiredLifecycle, instance.ObservedLifecycle, instance.Health, openable,
            openable ? currentIdentity!.Audience : null,
            openable ? currentIdentity!.CallbackUri.AbsoluteUri : null,
            !canOpen ? "Not authorized to open this instance." : !healthy ? "This instance is not currently available." : !handoffConfigured ? HandoffUnavailableReason : currentIdentity is null ? "The current identity binding is unavailable." : null)
        {
            Version = instance.Version,
            ETag = ETag(instance.Version),
            DesiredStateRevisionId = instance.DesiredStateRevisionId?.Value,
            ResolvedPlan = instance.ResolvedPlanReference,
            CurrentResolvedRelease = instance.CurrentResolvedRelease,
            CurrentDeployment = instance.CurrentDeploymentReference,
            IdentityBinding = openable ? new ManagedElsaInstanceIdentityBindingResponse(
                currentIdentity!.Audience, currentIdentity.CallbackUri.AbsoluteUri,
                currentIdentity.CallbackUri.GetLeftPart(UriPartial.Authority), currentIdentity.BindingVersion,
                currentIdentity.ChangedAt) : null,
            IdentityBindingState = !canOpen ? "not-authorized" : !healthy ? "instance-unavailable" : !handoffConfigured ? "handoff-unavailable" : currentIdentity is null ? "identity-unavailable" : "available",
            Intent = instance.Intent,
            Links = new Dictionary<string, string>
            {
                ["self"] = $"/api/workspaces/{workspaceId:D}/instances/{instance.Id:D}",
                ["operations"] = $"/api/workspaces/{workspaceId:D}/instances/{instance.Id:D}/operations",
                ["health"] = $"/api/workspaces/{workspaceId:D}/instances/{instance.Id:D}/health",
                ["revisions"] = $"/api/workspaces/{workspaceId:D}/instances/{instance.Id:D}/revisions",
                ["deployments"] = $"/api/workspaces/{workspaceId:D}/instances/{instance.Id:D}/deployments",
                ["audit"] = $"/api/workspaces/{workspaceId:D}/instances/{instance.Id:D}/audit"
            }
        };
    }

    private static ManagedElsaInstanceOperationResponse ToOperationResponse(Guid workspaceId, Guid instanceId, ElsaInstanceOperationSummary operation) =>
        new(operation.Id, instanceId, operation.Action, operation.State, operation.ExpectedVersion, operation.AttemptNumber,
            operation.AcceptedAt, operation.StartedAt, operation.CompletedAt, operation.DesiredStateRevisionId,
            operation.ResolvedPlanId, operation.DeploymentRunId, operation.FailureCode, operation.ReconciledObservedLifecycle,
            operation.ReconciledHealth, new Dictionary<string, string>
            {
                ["self"] = $"/api/workspaces/{workspaceId:D}/instances/{instanceId:D}/operations/{operation.Id:D}",
                ["instance"] = $"/api/workspaces/{workspaceId:D}/instances/{instanceId:D}"
            });

    internal static ElsaInstanceAuditEventSummary RedactAudit(ElsaInstanceAuditEventSummary audit) =>
        audit with { OperatorSubject = null };

    private static ManagedElsaInstanceResponse ToLegacyResponse(ManagedElsaInstanceSummary summary, bool canOpen)
    {
        var healthy = summary.DesiredLifecycle == ElsaDesiredLifecycle.Running && summary.ObservedLifecycle == ElsaObservedLifecycle.Ready && summary.Health == ElsaInstanceHealth.Healthy;
        var openable = canOpen && healthy && summary.Audience is not null && summary.CallbackUri is not null;
        return new ManagedElsaInstanceResponse(summary.OrganizationId, summary.InstanceId, summary.Name, summary.Slug,
            summary.DesiredLifecycle, summary.ObservedLifecycle, summary.Health, openable,
            openable ? summary.Audience : null, openable ? summary.CallbackUri!.OriginalString : null,
            !canOpen ? "Not authorized to open this instance." : !healthy ? "This instance is not currently available." : !openable ? "The current instance binding is unavailable." : null);
    }

    private static IdempotencyKeyReadResult ReadIdempotencyKey(HttpContext context)
    {
        var values = context.Request.Headers["Idempotency-Key"];
        if (values.Count == 0 || values.Count == 1 && string.IsNullOrWhiteSpace(values[0]))
            return new(IdempotencyKeyState.Missing, null);
        if (values.Count != 1)
            return new(IdempotencyKeyState.Invalid, null);
        try
        {
            return new(IdempotencyKeyState.Valid, ElsaInstanceIdempotencyKey.Normalize(values[0]));
        }
        catch (ArgumentException)
        {
            return new(IdempotencyKeyState.Invalid, null);
        }
    }

    private static IResult InvalidIdempotencyKey() =>
        Problem("instance.idempotency-key-invalid", "Idempotency-Key must be a safe token of at most 128 characters.", StatusCodes.Status400BadRequest);

    private static bool HasValidOperationShape(ManagedElsaInstanceOperationRequest request)
    {
        var isUpgrade = request.Action is ElsaInstanceOperationAction.ApproveMinorUpgrade or ElsaInstanceOperationAction.MajorMigration;
        return (isUpgrade || request.Intent is null && request.Name is null) &&
               (request.Action == ElsaInstanceOperationAction.Delete || request.DeleteConfirmationId is null);
    }

    private static int? ReadIfMatch(HttpRequest request)
    {
        var values = request.Headers.IfMatch;
        if (values.Count != 1)
            return null;
        var value = values[0]?.Trim();
        if (string.IsNullOrWhiteSpace(value) || value == "*" || value.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
            return null;
        if (value.Length < 3 || value[0] != '"' || value[^1] != '"')
            return null;
        value = value[1..^1];
        return int.TryParse(value, out var version) && version > 0 ? version : null;
    }

    private static string ETag(int version) => $"\"{version}\"";

    internal static string ConflictCode(ElsaInstanceLifecycleConflictException exception) => exception.Reason switch
    {
        ElsaInstanceLifecycleConflictReason.VersionConflict => "instance.version-conflict",
        ElsaInstanceLifecycleConflictReason.SlugConflict => "instance.slug-conflict",
        ElsaInstanceLifecycleConflictReason.OperationActive => "instance.operation-active",
        ElsaInstanceLifecycleConflictReason.IdempotencyConflict => "instance.idempotency-conflict",
        ElsaInstanceLifecycleConflictReason.CommercialDenied when exception.CommercialCode is not null => exception.CommercialCode,
        _ => "instance.invalid-state",
    };

    private static int ConflictStatusCode(ElsaInstanceLifecycleConflictException exception) =>
        exception.Reason == ElsaInstanceLifecycleConflictReason.VersionConflict
            ? StatusCodes.Status412PreconditionFailed
            : exception.Reason == ElsaInstanceLifecycleConflictReason.CommercialDenied
                ? StatusCodes.Status422UnprocessableEntity
                : StatusCodes.Status409Conflict;

    private static IResult Problem(string code, string title, int statusCode) => Results.Problem(title: title, statusCode: statusCode,
        extensions: new Dictionary<string, object?> { ["code"] = code });
}

public sealed record ManagedElsaInstanceCreateRequest(string? Name, string? Slug, ElsaInstanceIntent? Intent);
public sealed record ManagedElsaInstancePatchRequest(ElsaInstanceIntent? Intent = null, string? Name = null, string? Reason = null);
public sealed record ManagedElsaInstanceOperationRequest(ElsaInstanceOperationAction Action, int? ExpectedVersion = null, string? Reason = null, ElsaInstanceIntent? Intent = null, string? Name = null, Guid? DeleteConfirmationId = null);
public sealed record ManagedElsaInstanceListResponse(IReadOnlyList<ManagedElsaInstanceResponse> Items, int Page, int PageSize, int TotalCount, bool HasMore);
public sealed record ManagedElsaInstanceOnboardingOptionsResponse(
    IReadOnlyList<ManagedElsaInstanceReleaseOption> Releases,
    ManagedElsaInstanceLaunchProfile LaunchProfile,
    IReadOnlyList<ManagedElsaInstancePreviewReleaseOption>? PreviewReleases = null);
public sealed record ManagedElsaInstanceReleaseOption(
    string DistributionId,
    string ReleaseLine,
    string Version,
    string Channel,
    string TopologyId);
public sealed record ManagedElsaInstancePreviewReleaseOption(
    string DistributionId,
    string ReleaseLine,
    string Version,
    string Channel,
    string TopologyId,
    string ManifestDigest);
public sealed record ManagedElsaInstanceLaunchProfile(
    string Name,
    string Description,
    string TargetMode,
    string RegionCode,
    string IsolationProfile,
    string CapacityProfile,
    string NetworkOutcome,
    string DomainOutcome);
public sealed record ManagedElsaInstanceAcceptedResponse(ManagedElsaInstanceResponse Instance, ManagedElsaInstanceOperationResponse Operation, IReadOnlyDictionary<string, string> Links);
public sealed record ManagedElsaInstanceOperationResponse(Guid Id, Guid InstanceId, ElsaInstanceOperationAction Action, ElsaInstanceOperationState State, int ExpectedVersion, int AttemptNumber, DateTimeOffset AcceptedAt, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, string? DesiredStateRevisionId, string? ResolvedPlanId, Guid? DeploymentRunId, string? FailureCode, ElsaObservedLifecycle? ReconciledObservedLifecycle, ElsaInstanceHealth? ReconciledHealth, IReadOnlyDictionary<string, string> Links);
public sealed record ManagedElsaInstanceIdentityBindingResponse(string Audience, string CanonicalCallbackUri, string VerifiedEndpointOrigin, int BindingVersion, DateTimeOffset ChangedAt);
public sealed record ManagedElsaInstanceRevisionsResponse(IReadOnlyList<ElsaInstanceIntentRevisionSummary> Items);
public sealed record ManagedElsaInstanceDeploymentsResponse(IReadOnlyList<ElsaInstanceDeploymentSummary> Items);
public sealed record ManagedElsaInstanceAuditResponse(IReadOnlyList<ElsaInstanceAuditEventSummary> Items);
public sealed record ManagedElsaInstanceOperationalHealthResponse(
    ManagedLifecycleOperationalHealthStatus Status,
    string DiagnosticCode,
    DateTimeOffset EvaluatedAt,
    DateTimeOffset? ReconciledAt,
    ManagedElsaInstanceOperationalOperationResponse? Operation,
    ManagedElsaInstanceOperationalRunResponse? Run,
    IReadOnlyList<ManagedElsaInstanceOperationalAlertResponse> Alerts);
public sealed record ManagedElsaInstanceOperationalOperationResponse(
    Guid Id,
    ElsaInstanceOperationState State,
    int AttemptNumber,
    DateTimeOffset AcceptedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? HeartbeatAt,
    DateTimeOffset? LastProgressAt,
    string? DiagnosticCode);
public sealed record ManagedElsaInstanceOperationalRunResponse(
    Guid Id,
    WorkspaceDeploymentRunStatus Status,
    int AttemptNumber,
    DateTimeOffset QueuedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? HeartbeatAt,
    DateTimeOffset? LastProgressAt,
    string? DiagnosticCode);
public sealed record ManagedElsaInstanceOperationalAlertResponse(
    string Code,
    ManagedLifecycleOperationalHealthAlertSeverity Severity,
    string DedupeIdentity);

internal enum IdempotencyKeyState { Missing, Invalid, Valid }
internal sealed record IdempotencyKeyReadResult(IdempotencyKeyState State, string? Value);

public sealed record ManagedElsaInstanceResponse(Guid OrganizationId, Guid InstanceId, string Name, string Slug, ElsaDesiredLifecycle DesiredLifecycle, ElsaObservedLifecycle ObservedLifecycle, ElsaInstanceHealth Health, bool CanOpen, string? Audience, string? RedirectUri, string? UnavailableReason)
{
    public int Version { get; init; }
    public string ETag { get; init; } = "";
    public string? DesiredStateRevisionId { get; init; }
    public ElsaResolvedPlanReference? ResolvedPlan { get; init; }
    public ElsaCurrentResolvedRelease? CurrentResolvedRelease { get; init; }
    public ElsaCurrentDeploymentReference? CurrentDeployment { get; init; }
    public ManagedElsaInstanceIdentityBindingResponse? IdentityBinding { get; init; }
    public string IdentityBindingState { get; init; } = "identity-unavailable";
    public ElsaInstanceIntent? Intent { get; init; }
    public IReadOnlyDictionary<string, string> Links { get; init; } = new Dictionary<string, string>();
}
