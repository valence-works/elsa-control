using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ElsaControl.Api.Authentication;
using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Core.Instances;
using ElsaControl.Deployment.Core.Workspace;
using ElsaControl.PackageCatalog.Core.Accounts;
using ElsaControl.RuntimeBuilder.Abstractions.ReleaseCatalog;
using Microsoft.AspNetCore.Mvc;

namespace ElsaControl.Api.Workspace;

/// <summary>
/// Customer-shaped Cloud BFF routes for the hosted instance overview. Operator
/// routes stay off the allowlist; these handlers only project existing lifecycle,
/// health, catalog and audit data.
/// </summary>
public static class ManagedElsaInstanceOverviewEndpoints
{
    internal const string RequiresAttentionCode = "health.requires-attention";
    internal const string ReleaseNotAvailableCode = "instance.release-not-available";
    internal const string ReleaseAlreadyCurrentCode = "instance.release-already-current";
    internal const string PermissionRequiredCode = "instance.permission-required";
    internal const string OperationActiveCode = "instance.operation-active";
    internal const string ActivityLimitInvalidCode = "instance.activity-limit-invalid";
    internal const string DeploymentStatusUnclearMessage = "Deployment status unclear. We're still confirming the result.";
    internal const string CheckingDeploymentStatusMessage = "Checking deployment status";
    internal const string SubmissionUncertainCode = "provider.submission.uncertain";
    internal const string UnknownFieldCode = "request.unknown-field";
    internal const string ApplyReleaseInvalidCode = "instance.apply-release-invalid";
    internal const string RestartReason = "Restart";
    internal const string InstanceNotReadyCode = "instance.not-ready";
    internal const string InstanceDeletingCode = "instance.deleting";
    internal const string InstanceFailedCode = "instance.failed";
    internal const string InstanceProvisioningCode = "instance.provisioning";
    internal const string InstanceUnknownCode = "instance.unknown";
    internal const int DefaultActivityLimit = 25;
    internal const int MaxActivityLimit = 100;
    internal const int MaxAvailableReleases = 20;
    private const int ActivityLookupWindow = 500;

    private static readonly FrozenSet<string> CustomerActivityEventTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "lifecycle.accepted",
        "lifecycle.reconciled",
        "lifecycle.resolved",
        "lifecycle.deleted",
        "lifecycle.failed",
        "lifecycle.health-changed",
        "lifecycle.operation-updated",
        "lifecycle.entitlement-held",
        "lifecycle.entitlement-resumed",
        "lifecycle.deletion-recovery-required",
        "lifecycle.recovery-superseded-by-delete"
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> CustomerHealthCodes = new HashSet<string>(StringComparer.Ordinal)
    {
        ManagedLifecycleOperationalHealthDiagnosticCodes.Healthy,
        ManagedLifecycleOperationalHealthDiagnosticCodes.Degraded,
        ManagedLifecycleOperationalHealthDiagnosticCodes.Failed,
        ManagedLifecycleOperationalHealthDiagnosticCodes.Unknown,
        ManagedLifecycleOperationalHealthDiagnosticCodes.ProviderUnknown,
        ManagedLifecycleOperationalHealthDiagnosticCodes.Stale,
        ManagedLifecycleOperationalHealthDiagnosticCodes.StaleWork,
        ManagedLifecycleOperationalHealthDiagnosticCodes.ReconciliationUnknown,
        ManagedLifecycleOperationalHealthDiagnosticCodes.ReconciliationStale,
        ManagedLifecycleOperationalHealthDiagnosticCodes.UnhealthyEndpoint,
        ManagedLifecycleOperationalHealthDiagnosticCodes.RecoveryRequired,
        ManagedLifecycleOperationalHealthDiagnosticCodes.OperationFailed,
        ManagedLifecycleOperationalHealthDiagnosticCodes.RunFailed,
        ManagedLifecycleOperationalHealthDiagnosticCodes.WorkActive,
        ManagedLifecycleOperationalHealthDiagnosticCodes.RetryExhausted
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> InformationalActivityCodes = new HashSet<string>(StringComparer.Ordinal)
    {
        ManagedLifecycleOperationalHealthDiagnosticCodes.Healthy,
        ManagedLifecycleOperationalHealthDiagnosticCodes.WorkActive,
        ManagedLifecycleOperationalHealthDiagnosticCodes.Unknown,
        ManagedLifecycleOperationalHealthDiagnosticCodes.ProviderUnknown,
        ManagedLifecycleOperationalHealthDiagnosticCodes.ReconciliationUnknown,
        ElsaInstanceProviderReconciliationService.ConvergedCode,
        ElsaInstanceProviderReconciliationService.InProgressCode,
        ElsaInstanceProviderReconciliationService.UnavailableCode,
        ElsaInstanceProviderReconciliationService.UnknownCode,
        ElsaInstanceProviderReconciliationService.HealthUnknownCode,
        "instance.entitlement-restored"
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> WarningActivityCodes = new HashSet<string>(StringComparer.Ordinal)
    {
        ElsaInstanceProviderReconciliationService.AmbiguousCode,
        ElsaInstanceProviderReconciliationService.RetrySafeCode,
        ElsaInstanceProviderReconciliationService.CorrelationMismatchCode,
        SubmissionUncertainCode
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> FailedActivityMappedCodes = new HashSet<string>(StringComparer.Ordinal)
    {
        ManagedLifecycleOperationalHealthDiagnosticCodes.Failed,
        ManagedLifecycleOperationalHealthDiagnosticCodes.OperationFailed,
        ManagedLifecycleOperationalHealthDiagnosticCodes.RunFailed,
        ManagedLifecycleOperationalHealthDiagnosticCodes.RetryExhausted
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenDictionary<string, string> ActionableActivityCodes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ManagedLifecycleOperationalHealthDiagnosticCodes.Failed] =
                ManagedLifecycleOperationalHealthDiagnosticCodes.Failed,
            [ManagedLifecycleOperationalHealthDiagnosticCodes.Degraded] =
                ManagedLifecycleOperationalHealthDiagnosticCodes.Degraded,
            [ManagedLifecycleOperationalHealthDiagnosticCodes.Stale] =
                ManagedLifecycleOperationalHealthDiagnosticCodes.Stale,
            [ManagedLifecycleOperationalHealthDiagnosticCodes.StaleWork] =
                ManagedLifecycleOperationalHealthDiagnosticCodes.StaleWork,
            [ManagedLifecycleOperationalHealthDiagnosticCodes.ReconciliationStale] =
                ManagedLifecycleOperationalHealthDiagnosticCodes.ReconciliationStale,
            [ManagedLifecycleOperationalHealthDiagnosticCodes.UnhealthyEndpoint] =
                ManagedLifecycleOperationalHealthDiagnosticCodes.UnhealthyEndpoint,
            [ManagedLifecycleOperationalHealthDiagnosticCodes.RecoveryRequired] =
                ManagedLifecycleOperationalHealthDiagnosticCodes.RecoveryRequired,
            [ManagedLifecycleOperationalHealthDiagnosticCodes.OperationFailed] =
                ManagedLifecycleOperationalHealthDiagnosticCodes.OperationFailed,
            [ManagedLifecycleOperationalHealthDiagnosticCodes.RunFailed] =
                ManagedLifecycleOperationalHealthDiagnosticCodes.RunFailed,
            [ManagedLifecycleOperationalHealthDiagnosticCodes.RetryExhausted] =
                ManagedLifecycleOperationalHealthDiagnosticCodes.RetryExhausted,
            [ElsaInstanceProviderReconciliationService.FailedCode] =
                ManagedLifecycleOperationalHealthDiagnosticCodes.Failed,
            [ElsaInstanceProviderReconciliationService.HealthFailedCode] =
                ManagedLifecycleOperationalHealthDiagnosticCodes.Failed,
            [ElsaInstanceCommercialOperation.EntitlementRequired] =
                ElsaInstanceCommercialOperation.EntitlementRequired,
            [ElsaInstanceCommercialOperation.EntitlementExpired] =
                ElsaInstanceCommercialOperation.EntitlementExpired,
            [ElsaInstanceCommercialOperation.SubscriptionStateRequired] =
                ElsaInstanceCommercialOperation.SubscriptionStateRequired,
            [ElsaInstanceCommercialOperation.LifecycleConstrained] =
                ElsaInstanceCommercialOperation.LifecycleConstrained,
            [ElsaInstanceCommercialOperation.InstanceLimitReached] =
                ElsaInstanceCommercialOperation.InstanceLimitReached
        }.ToFrozenDictionary(StringComparer.Ordinal);

    internal static RouteGroupBuilder MapManagedElsaInstanceOverviewEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/{instanceId:guid}/overview", async (
            Guid workspaceId,
            Guid instanceId,
            HttpContext context,
            WorkspacePermissionService permissions,
            IElsaInstanceLifecycleStore lifecycle,
            IManagedElsaInstanceApiStore queries,
            IManagedElsaInstanceOperationalStore operationalStore,
            [FromServices] IManagedElsaInstanceIdentityStore identities,
            IElsaInstanceCommercialGate commercialGate,
            ManagedLifecycleOperationalHealthEvaluator healthEvaluator,
            CancellationToken cancellationToken) =>
        {
            var access = context.GetWorkspaceAccess();
            var instance = await lifecycle.GetInstanceAsync(workspaceId, instanceId, cancellationToken);
            if (instance is null)
                return Results.NotFound();

            var canOpen = (await permissions.GetEffectivePermissionsAsync(workspaceId, access.AccountId, cancellationToken))
                .Has(ManagedElsaInstancePermissions.Open);
            var activeOperations = await queries.GetActiveOperationsAsync(workspaceId, [instance.Id], cancellationToken);
            var activeOperation = activeOperations.GetValueOrDefault(instance.Id);
            var lastPage = await queries.ListOperationsAsync(
                workspaceId, instance.Id, page: 1, pageSize: 100, cancellationToken, access.OrganizationId);
            var lastOperation = lastPage.Items.FirstOrDefault(operation => operation.CompletedAt is not null);
            var snapshot = await operationalStore.GetSnapshotAsync(workspaceId, instanceId, cancellationToken)
                ?? new ManagedLifecycleOperationalHealthSnapshot(
                    workspaceId,
                    instanceId,
                    instance.DesiredLifecycle,
                    instance.ObservedLifecycle,
                    instance.Health);
            var health = healthEvaluator.Evaluate(snapshot);
            var identity = canOpen
                ? await identities.FindOpenableAsync(instance.OrganizationId, instance.Id, cancellationToken)
                : null;
            var restartGate = await commercialGate.EvaluateAsync(
                access.OrganizationId, ElsaInstanceOperationAction.Restart, cancellationToken: cancellationToken);
            var applyGate = await commercialGate.EvaluateAsync(
                access.OrganizationId, ElsaInstanceOperationAction.UpdateIntent, cancellationToken: cancellationToken);
            var response = ManagedElsaInstanceOverviewProjection.ToOverview(
                instance,
                workspaceId,
                access.Role,
                canOpen,
                identity,
                activeOperation,
                lastOperation,
                health,
                restartGate,
                applyGate);
            context.Response.Headers.ETag = StrongETag(instance.Version);
            return Results.Ok(response);
        }).RequireWorkspaceAccess().AllowCloudBff();

        group.MapGet("/{instanceId:guid}/available-releases", async (
            Guid workspaceId,
            Guid instanceId,
            HttpContext context,
            IElsaInstanceLifecycleStore lifecycle,
            IGovernedReleaseCatalogStore catalog,
            CancellationToken cancellationToken) =>
        {
            var instance = await lifecycle.GetInstanceAsync(workspaceId, instanceId, cancellationToken);
            if (instance is null)
                return Results.NotFound();
            return Results.Ok(new ManagedElsaInstanceAvailableReleasesResponse(
                await ListAvailableReleasesAsync(catalog, instance, cancellationToken)));
        }).RequireWorkspaceAccess().AllowCloudBff();

        group.MapGet("/{instanceId:guid}/activity", async (
            Guid workspaceId,
            Guid instanceId,
            int? limit,
            HttpContext context,
            IElsaInstanceLifecycleStore lifecycle,
            IManagedElsaInstanceApiStore queries,
            CancellationToken cancellationToken) =>
        {
            if (await lifecycle.GetInstanceAsync(workspaceId, instanceId, cancellationToken) is null)
                return Results.NotFound();
            var currentLimit = limit ?? DefaultActivityLimit;
            if (currentLimit < 1 || currentLimit > MaxActivityLimit)
                return ManagedElsaInstanceEndpoints.Problem(
                    ActivityLimitInvalidCode,
                    "Activity limit must be between 1 and 100.",
                    StatusCodes.Status400BadRequest);

            var access = context.GetWorkspaceAccess();
            var events = await queries.ListAuditAsync(
                workspaceId, instanceId, cancellationToken, ActivityLookupWindow, access.OrganizationId);
            var operations = await queries.ListOperationsAsync(
                workspaceId, instanceId, page: 1, pageSize: 100, cancellationToken, access.OrganizationId);
            var operationsById = operations.Items.ToDictionary(operation => operation.Id);
            var projected = events
                .Where(item => CustomerActivityEventTypes.Contains(item.EventType))
                .Select(item => ManagedElsaInstanceOverviewProjection.ToActivityItem(item, operationsById))
                .ToList();
            return Results.Ok(new ManagedElsaInstanceActivityResponse(
                projected.Take(currentLimit).ToList(),
                projected.Count > currentLimit || events.Count >= ActivityLookupWindow));
        }).RequireWorkspaceAccess().AllowCloudBff();

        group.MapPost("/{instanceId:guid}/restart", async (
            Guid workspaceId,
            Guid instanceId,
            HttpContext context,
            IElsaInstanceCommercialGate commercialGate,
            ElsaInstanceLifecycleService lifecycle,
            IElsaInstanceLifecycleStore store,
            IManagedElsaInstanceApiStore queries,
            CancellationToken cancellationToken) =>
        {
            return await ExecuteCustomerMutationAsync(
                context, workspaceId, instanceId, store, queries, commercialGate, lifecycle,
                catalog: null, version: null, cancellationToken);
        }).RequireWorkspaceAccess(WorkspaceOperation.MutateWorkspaceResource).AllowCloudBff();

        group.MapPost("/{instanceId:guid}/apply-release", async (
            Guid workspaceId,
            Guid instanceId,
            HttpContext context,
            IElsaInstanceCommercialGate commercialGate,
            IGovernedReleaseCatalogStore catalog,
            ElsaInstanceLifecycleService lifecycle,
            IElsaInstanceLifecycleStore store,
            IManagedElsaInstanceApiStore queries,
            CancellationToken cancellationToken) =>
        {
            var body = await ReadApplyReleaseRequestAsync(context);
            if (body.Error is not null)
                return body.Error;
            return await ExecuteCustomerMutationAsync(
                context, workspaceId, instanceId, store, queries, commercialGate, lifecycle,
                catalog, body.Version, cancellationToken);
        }).RequireWorkspaceAccess(WorkspaceOperation.MutateWorkspaceResource).AllowCloudBff();

        return group;
    }

    internal static async Task<IReadOnlyList<ManagedElsaInstanceAvailableReleaseResponse>> ListAvailableReleasesAsync(
        IGovernedReleaseCatalogStore catalog,
        ElsaInstance instance,
        CancellationToken cancellationToken)
    {
        var instanceChannel = instance.Intent.Release.Channel;
        var entries = await catalog.QueryAsync(new GovernedReleaseCatalogQuery(
            DistributionId: instance.Intent.Release.DistributionId,
            CatalogLifecycle: "supported",
            RegistryClass: "paid",
            Channel: instanceChannel,
            TopologyId: instance.Intent.Application.TopologyId), cancellationToken);
        var currentVersion = CurrentReleaseVersion(instance);
        if (!TryReadMajor(instance.Intent.Release.ReleaseLine, out var currentMajor))
            return [];

        return entries
            .Where(entry =>
                string.Equals(entry.CatalogLifecycle, "supported", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(entry.RegistryClass, "paid", StringComparison.OrdinalIgnoreCase) &&
                IsCustomerChannel(entry.Distribution.Channel, instanceChannel) &&
                TryReadMajor(entry.Distribution.ReleaseLine, out var major) &&
                major == currentMajor &&
                ReleaseVersionComparer.Instance.Compare(entry.Distribution.ReleaseVersion, currentVersion) > 0)
            .Select(entry =>
            {
                var sameLine = string.Equals(
                    entry.Distribution.ReleaseLine, instance.Intent.Release.ReleaseLine, StringComparison.OrdinalIgnoreCase);
                return new ManagedElsaInstanceAvailableReleaseResponse(
                    entry.Distribution.ReleaseLine,
                    entry.Distribution.ReleaseVersion,
                    entry.Distribution.Channel,
                    sameLine ? "patch" : "minor");
            })
            .GroupBy(release => release.Version, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderByDescending(release => release.Version, ReleaseVersionComparer.Instance)
            .ThenBy(release => release.ReleaseLine, StringComparer.OrdinalIgnoreCase)
            .Take(MaxAvailableReleases)
            .ToList();
    }

    private static async Task<IResult> ExecuteCustomerMutationAsync(
        HttpContext context,
        Guid workspaceId,
        Guid instanceId,
        IElsaInstanceLifecycleStore store,
        IManagedElsaInstanceApiStore queries,
        IElsaInstanceCommercialGate commercialGate,
        ElsaInstanceLifecycleService lifecycle,
        IGovernedReleaseCatalogStore? catalog,
        string? version,
        CancellationToken cancellationToken)
    {
        var isApply = version is not null;
        var keyResult = ManagedElsaInstanceEndpoints.ReadIdempotencyKey(context);
        if (keyResult.State == IdempotencyKeyState.Missing)
            return ManagedElsaInstanceEndpoints.Problem(
                "instance.idempotency-key-required",
                "Idempotency-Key is required for instance operations.",
                StatusCodes.Status400BadRequest);
        if (keyResult.State == IdempotencyKeyState.Invalid)
            return ManagedElsaInstanceEndpoints.Problem(
                "instance.idempotency-key-invalid",
                "Idempotency-Key must be a safe token of at most 128 characters.",
                StatusCodes.Status400BadRequest);

        var instance = await store.GetInstanceAsync(workspaceId, instanceId, cancellationToken);
        if (instance is null)
            return Results.NotFound();

        var access = context.GetWorkspaceAccess();
        var existing = await store.FindOperationByKeyAsync(
            workspaceId,
            keyResult.Value!,
            instanceId,
            action: null,
            idempotencyScope: CustomerOverviewScope(instanceId),
            cancellationToken: cancellationToken);
        if (existing is not null)
        {
            if (await IsSameCustomerRequestAsync(
                    existing, isApply, version, workspaceId, instanceId, access.OrganizationId, queries, cancellationToken))
                return AcceptedOperation(existing);
            return ManagedElsaInstanceEndpoints.Problem(
                "instance.idempotency-conflict",
                "The request conflicts with the current instance state.",
                StatusCodes.Status409Conflict);
        }

        var expectedVersion = ManagedElsaInstanceEndpoints.ReadIfMatch(context.Request);
        if (expectedVersion is null)
            return ManagedElsaInstanceEndpoints.Problem(
                "instance.if-match-required",
                "A strong If-Match header is required for instance operations.",
                StatusCodes.Status428PreconditionRequired);

        var commercialAction = isApply
            ? ElsaInstanceOperationAction.UpdateIntent
            : ElsaInstanceOperationAction.Restart;
        var commercialDecision = await commercialGate.EvaluateAsync(
            access.OrganizationId, commercialAction, cancellationToken: cancellationToken);
        if (!commercialDecision.Allowed)
            return ManagedElsaInstanceEndpoints.Problem(
                commercialDecision.Code, commercialDecision.Summary, StatusCodes.Status422UnprocessableEntity);

        if (CustomerMutationDenialCode(instance) is { } mutationDenial)
            return ManagedElsaInstanceEndpoints.Problem(
                mutationDenial,
                "The requested operation is not valid for the current instance state.",
                StatusCodes.Status409Conflict);

        try
        {
            if (!isApply)
            {
                var accepted = await lifecycle.RestartAsync(
                    new ElsaInstanceLifecycleRequest(
                        WorkspaceId: workspaceId,
                        InstanceId: instanceId,
                        ExpectedVersion: expectedVersion.Value,
                        IdempotencyKey: keyResult.Value!,
                        Reason: RestartReason,
                        ActorAccountId: access.AccountId),
                    cancellationToken);
                return AcceptedOperation(accepted.Operation);
            }

            if (string.Equals(version, CurrentReleaseVersion(instance), StringComparison.OrdinalIgnoreCase))
                return ManagedElsaInstanceEndpoints.Problem(
                    ReleaseAlreadyCurrentCode,
                    "The selected release is already current.",
                    StatusCodes.Status409Conflict);

            var selected = (await ListAvailableReleasesAsync(catalog!, instance, cancellationToken))
                .FirstOrDefault(release =>
                    string.Equals(release.Version, version, StringComparison.OrdinalIgnoreCase));
            if (selected is null)
                return ManagedElsaInstanceEndpoints.Problem(
                    ReleaseNotAvailableCode,
                    "The selected release is not available for this instance.",
                    StatusCodes.Status422UnprocessableEntity);

            var nextIntent = instance.Intent with
            {
                Release = new ElsaReleaseIntent(
                    distributionId: instance.Intent.Release.DistributionId,
                    releaseLine: selected.ReleaseLine,
                    requestedVersion: selected.Version,
                    channel: instance.Intent.Release.Channel,
                    patchUpdates: instance.Intent.Release.PatchUpdates,
                    minorUpdates: instance.Intent.Release.MinorUpdates,
                    majorMigrations: instance.Intent.Release.MajorMigrations)
            };
            var acceptedApply = await lifecycle.ApproveMinorUpgradeAsync(
                new ElsaInstanceIntentUpdateRequest(
                    WorkspaceId: workspaceId,
                    InstanceId: instanceId,
                    Intent: nextIntent,
                    ExpectedVersion: expectedVersion.Value,
                    IdempotencyKey: keyResult.Value!,
                    Reason: ApplyReleaseReason(selected.Version),
                    ActorAccountId: access.AccountId),
                cancellationToken);
            return AcceptedOperation(acceptedApply.Operation);
        }
        catch (ElsaInstanceLifecycleConflictException exception)
        {
            return ManagedElsaInstanceEndpoints.ConflictProblem(exception);
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound();
        }
        catch (ArgumentException)
        {
            return ManagedElsaInstanceEndpoints.Problem(
                "instance.operation-invalid",
                isApply ? "The requested release change is invalid." : "The requested restart is invalid.",
                StatusCodes.Status422UnprocessableEntity);
        }
    }

    private static async Task<(IResult? Error, string Version)> ReadApplyReleaseRequestAsync(HttpContext context)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(context.Request.Body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return (ManagedElsaInstanceEndpoints.Problem(
                    ApplyReleaseInvalidCode,
                    "Apply-release requires a JSON object with a version.",
                    StatusCodes.Status400BadRequest), "");

            string? version = null;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!property.NameEquals("version"))
                    return (ManagedElsaInstanceEndpoints.Problem(
                        UnknownFieldCode,
                        "Apply-release accepts only a version.",
                        StatusCodes.Status400BadRequest), "");
                if (property.Value.ValueKind != JsonValueKind.String)
                    return (ManagedElsaInstanceEndpoints.Problem(
                        ApplyReleaseInvalidCode,
                        "Apply-release requires a version.",
                        StatusCodes.Status400BadRequest), "");
                version = property.Value.GetString();
            }

            var normalized = version?.Trim();
            if (string.IsNullOrWhiteSpace(normalized))
                return (ManagedElsaInstanceEndpoints.Problem(
                    ApplyReleaseInvalidCode,
                    "Apply-release requires a version.",
                    StatusCodes.Status400BadRequest), "");
            return (null, normalized);
        }
        catch (JsonException)
        {
            return (ManagedElsaInstanceEndpoints.Problem(
                ApplyReleaseInvalidCode,
                "Apply-release requires a JSON object with a version.",
                StatusCodes.Status400BadRequest), "");
        }
    }

    private static async Task<bool> IsSameCustomerRequestAsync(
        ElsaInstanceOperation existing,
        bool isApply,
        string? version,
        Guid workspaceId,
        Guid instanceId,
        Guid organizationId,
        IManagedElsaInstanceApiStore queries,
        CancellationToken cancellationToken)
    {
        if (!isApply)
            return existing.Action == ElsaInstanceOperationAction.Restart;
        if (existing.Action is not (ElsaInstanceOperationAction.ApproveMinorUpgrade or ElsaInstanceOperationAction.UpdateIntent) ||
            version is null)
            return false;

        var events = await queries.ListAuditAsync(
            workspaceId, instanceId, cancellationToken, 100, organizationId);
        var accepted = events.FirstOrDefault(item =>
            item.OperationId == existing.Id &&
            string.Equals(item.EventType, "lifecycle.accepted", StringComparison.Ordinal));
        return accepted?.Summary is not null &&
               string.Equals(accepted.Summary, HashReason(ApplyReleaseReason(version)), StringComparison.Ordinal);
    }

    internal static string CustomerOverviewScope(Guid instanceId) => $"instance/{instanceId:D}/operations";

    internal static string ApplyReleaseReason(string version) => $"Apply release {version}";

    internal static string CurrentReleaseVersion(ElsaInstance instance) =>
        instance.CurrentResolvedRelease?.Version
        ?? instance.Intent.Release.RequestedVersion
        ?? instance.Intent.Release.ReleaseLine;

    internal static bool IsCustomerChannel(string channel, string instanceChannel) =>
        string.Equals(channel, instanceChannel, StringComparison.OrdinalIgnoreCase) &&
        !IsPreviewChannel(channel);

    internal static bool IsPreviewChannel(string channel) =>
        channel.Contains("preview", StringComparison.OrdinalIgnoreCase) ||
        channel.Contains("nightly", StringComparison.OrdinalIgnoreCase) ||
        channel.Equals("beta", StringComparison.OrdinalIgnoreCase) ||
        channel.Equals("alpha", StringComparison.OrdinalIgnoreCase) ||
        channel.Contains(".rc", StringComparison.OrdinalIgnoreCase) ||
        channel.StartsWith("rc", StringComparison.OrdinalIgnoreCase);

    private static string HashReason(string reason) =>
        "reason.sha256." + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(reason)));

    private static IResult AcceptedOperation(ElsaInstanceOperation operation) =>
        Results.Json(
            new ManagedElsaInstanceOverviewOperationResponse(
                operation.Id,
                operation.Action,
                operation.State,
                operation.AcceptedAt),
            statusCode: StatusCodes.Status202Accepted);

    internal static string StrongETag(int version) => $"\"{version}\"";

    internal static string CustomerDiagnosticCode(string? code) =>
        code is not null && CustomerHealthCodes.Contains(code) ? code : RequiresAttentionCode;

    internal static string? CustomerActivityDiagnosticCode(string? code) =>
        ClassifyCustomerActivity(code).DiagnosticCode;

    internal static CustomerActivityClassification ClassifyCustomerActivity(
        string? diagnosticCode,
        string? eventType = null,
        string? newState = null,
        string? operationFailureCode = null)
    {
        if (IsWarningActivityCode(diagnosticCode) || IsWarningActivityCode(operationFailureCode))
            return new(null, ManagedElsaInstanceActivitySeverity.Warning, DeploymentStatusUnclearMessage);

        if (IsFailedReasonCode(diagnosticCode) || IsFailedReasonCode(operationFailureCode))
            return FailedActivity(diagnosticCode ?? operationFailureCode);

        if (IsInformationalActivityCode(diagnosticCode) || IsInformationalActivityCode(operationFailureCode))
        {
            var source = IsInformationalActivityCode(diagnosticCode) ? diagnosticCode : operationFailureCode;
            var informational = string.Equals(
                source, ElsaInstanceProviderReconciliationService.UnavailableCode, StringComparison.Ordinal)
                ? CheckingDeploymentStatusMessage
                : null;
            return new(null, ManagedElsaInstanceActivitySeverity.Informational, informational);
        }

        if (IsTerminalFailed(eventType, newState) || !string.IsNullOrWhiteSpace(operationFailureCode))
            return FailedActivity(diagnosticCode ?? operationFailureCode);

        if (!string.IsNullOrWhiteSpace(diagnosticCode) &&
            ActionableActivityCodes.TryGetValue(diagnosticCode, out var mapped))
        {
            var severity = FailedActivityMappedCodes.Contains(mapped)
                ? ManagedElsaInstanceActivitySeverity.Failed
                : ManagedElsaInstanceActivitySeverity.Warning;
            return new(mapped, severity, null);
        }

        return new(null, ManagedElsaInstanceActivitySeverity.Informational, null);
    }

    private static bool IsWarningActivityCode(string? code) =>
        !string.IsNullOrWhiteSpace(code) && WarningActivityCodes.Contains(code);

    private static bool IsInformationalActivityCode(string? code) =>
        !string.IsNullOrWhiteSpace(code) && InformationalActivityCodes.Contains(code);

    private static bool IsFailedReasonCode(string? code) =>
        string.Equals(code, ElsaInstanceProviderReconciliationService.FailedCode, StringComparison.Ordinal) ||
        string.Equals(code, ElsaInstanceProviderReconciliationService.HealthFailedCode, StringComparison.Ordinal) ||
        string.Equals(code, ManagedLifecycleOperationalHealthDiagnosticCodes.Failed, StringComparison.Ordinal) ||
        string.Equals(code, ManagedLifecycleOperationalHealthDiagnosticCodes.OperationFailed, StringComparison.Ordinal);

    private static bool IsTerminalFailed(string? eventType, string? newState) =>
        string.Equals(eventType, "lifecycle.failed", StringComparison.Ordinal) ||
        string.Equals(newState, ElsaObservedLifecycle.Failed.ToString(), StringComparison.Ordinal);

    private static CustomerActivityClassification FailedActivity(string? code)
    {
        if (!string.IsNullOrWhiteSpace(code) && ActionableActivityCodes.TryGetValue(code, out var mapped))
            return new(mapped, ManagedElsaInstanceActivitySeverity.Failed, null);
        return new(
            ManagedLifecycleOperationalHealthDiagnosticCodes.OperationFailed,
            ManagedElsaInstanceActivitySeverity.Failed,
            null);
    }

    internal static IEnumerable<string> EnumerateCustomerActivityCopyLabels()
    {
        foreach (var code in InformationalActivityCodes
                     .Concat(WarningActivityCodes)
                     .Concat(ActionableActivityCodes.Keys)
                     .Append(null)
                     .Append("provider.internal.mystery"))
        {
            var mapped = ClassifyCustomerActivity(code);
            if (mapped.Severity is ManagedElsaInstanceActivitySeverity.Informational
                    or ManagedElsaInstanceActivitySeverity.Warning &&
                !string.IsNullOrWhiteSpace(mapped.Message))
                yield return mapped.Message;
        }
    }

    internal static string? CustomerMutationDenialCode(ElsaInstance instance)
    {
        if (instance.ObservedLifecycle == ElsaObservedLifecycle.Deleted ||
            instance.Intent.DesiredLifecycle == ElsaDesiredLifecycle.Deleting)
            return InstanceDeletingCode;
        if (instance.ObservedLifecycle == ElsaObservedLifecycle.Failed)
            return InstanceFailedCode;
        if (ManagedElsaInstanceCustomerProjection.IsKnownInProgress(instance.ObservedLifecycle))
            return InstanceProvisioningCode;
        if (instance.ObservedLifecycle == ElsaObservedLifecycle.Unknown)
            return InstanceUnknownCode;
        return ElsaInstanceStateMachine.CanRestart(instance) ? null : InstanceNotReadyCode;
    }

    internal static bool TryReadMajor(string releaseLine, out int major)
    {
        var firstPart = releaseLine.Split('.', 2)[0];
        return int.TryParse(firstPart, out major);
    }
}

internal static class ManagedElsaInstanceOverviewProjection
{
    internal static ManagedElsaInstanceOverviewResponse ToOverview(
        ElsaInstance instance,
        Guid workspaceId,
        WorkspaceRole role,
        bool canOpenPermission,
        ManagedElsaInstanceIdentity? identity,
        ElsaInstanceOperationSummary? activeOperation,
        ElsaInstanceOperationSummary? lastOperation,
        ManagedLifecycleOperationalHealthResult health,
        ElsaInstanceCommercialGateDecision restartGate,
        ElsaInstanceCommercialGateDecision applyGate)
    {
        var observed = ManagedElsaInstanceCustomerProjection.ProjectObservedLifecycle(instance, activeOperation);
        var healthy = instance.DesiredLifecycle == ElsaDesiredLifecycle.Running &&
                      instance.ObservedLifecycle == ElsaObservedLifecycle.Ready &&
                      instance.Health == ElsaInstanceHealth.Healthy;
        var currentIdentity = identity is { } candidate &&
                              candidate.OrganizationId == instance.OrganizationId &&
                              candidate.WorkspaceId == workspaceId &&
                              candidate.InstanceId == instance.Id
            ? candidate
            : null;
        var handoffConfigured = instance.CurrentDeploymentReference?.ManagedHandoff == true;
        var canOpen = canOpenPermission && healthy && handoffConfigured && currentIdentity is not null;
        var unavailableReason = UnavailableReasonCode(
            canOpenPermission, healthy, handoffConfigured, currentIdentity is not null, observed);
        var canMutate = role is WorkspaceRole.Owner or WorkspaceRole.SourceAdmin;
        var hasActiveOperation = activeOperation is not null &&
                                 ElsaInstanceOperationGuard.IsBlocking(activeOperation.State);
        var mutationDenial = ManagedElsaInstanceOverviewEndpoints.CustomerMutationDenialCode(instance);

        return new ManagedElsaInstanceOverviewResponse(
            new ManagedElsaInstanceOverviewSummaryResponse(
                instance.Id,
                instance.Name,
                instance.Slug,
                instance.DesiredLifecycle,
                observed,
                instance.CreatedAt,
                instance.UpdatedAt,
                instance.Version,
                canOpen,
                unavailableReason),
            new ManagedElsaInstanceOverviewHealthResponse(
                health.Status,
                ManagedElsaInstanceOverviewEndpoints.CustomerDiagnosticCode(health.DiagnosticCode),
                health.EvaluatedAt,
                health.Alerts.Select(alert => new ManagedElsaInstanceOverviewAlertResponse(
                    ManagedElsaInstanceOverviewEndpoints.CustomerDiagnosticCode(alert.Code),
                    alert.Severity)).ToArray()),
            new ManagedElsaInstanceOverviewReleaseResponse(
                instance.Intent.Release.DistributionId,
                instance.Intent.Release.ReleaseLine,
                instance.CurrentResolvedRelease?.Version ?? instance.Intent.Release.RequestedVersion,
                instance.Intent.Release.Channel),
            new ManagedElsaInstanceOverviewPolicyResponse(
                instance.Intent.Release.PatchUpdates,
                instance.Intent.Release.MinorUpdates,
                instance.Intent.Release.MajorMigrations),
            (instance.CurrentResolvedRelease?.ComponentDigests ?? [])
                .Select(component => new ManagedElsaInstanceOverviewComponentResponse(
                    component.ComponentId, component.Digest))
                .ToArray(),
            activeOperation is null ? null : new ManagedElsaInstanceOverviewActiveOperationResponse(
                OperationId: activeOperation.Id,
                Action: activeOperation.Action,
                State: activeOperation.State,
                AcceptedAt: activeOperation.AcceptedAt,
                StartedAt: activeOperation.StartedAt)
            {
                Progress = new ManagedElsaInstanceOverviewProgressResponse()
            },
            lastOperation is null ? null : new ManagedElsaInstanceOverviewLastOperationResponse(
                lastOperation.Action,
                lastOperation.State,
                lastOperation.CompletedAt,
                CustomerFailureCode(lastOperation.FailureCode)),
            new ManagedElsaInstanceOverviewAllowedActionsResponse(
                ActionDecision(canMutate, hasActiveOperation, mutationDenial, restartGate),
                ActionDecision(canMutate, hasActiveOperation, mutationDenial, applyGate),
                new ManagedElsaInstanceOverviewActionDecisionResponse(canOpen, canOpen ? null : unavailableReason)));
    }

    internal static ManagedElsaInstanceActivityItemResponse ToActivityItem(
        ElsaInstanceAuditEventSummary item,
        IReadOnlyDictionary<Guid, ElsaInstanceOperationSummary> operationsById)
    {
        var operation = item.OperationId is { } operationId &&
                        operationsById.TryGetValue(operationId, out var matched)
            ? matched
            : null;
        var classification = ManagedElsaInstanceOverviewEndpoints.ClassifyCustomerActivity(
            item.DiagnosticCode,
            item.EventType,
            item.NewState,
            operation?.FailureCode);
        return new(
            item.Sequence,
            item.EventType,
            item.OccurredAt,
            operation?.Action,
            item.PriorState,
            item.NewState,
            classification.DiagnosticCode,
            classification.Severity,
            classification.Message,
            item.OperatorSubject is not null ? "support" : item.ActorAccountId is not null ? "customer" : "system");
    }

    internal static string? UnavailableReasonCode(
        bool canOpen,
        bool healthy,
        bool handoffConfigured,
        bool hasIdentity,
        ElsaObservedLifecycle observedLifecycle)
    {
        if (!canOpen)
            return "not-authorized";
        if (ManagedElsaInstanceCustomerProjection.IsKnownInProgress(observedLifecycle))
            return "instance.provisioning";
        if (observedLifecycle == ElsaObservedLifecycle.Failed)
            return "instance.failed";
        if (observedLifecycle == ElsaObservedLifecycle.Unknown)
            return "instance.unknown";
        if (!healthy)
            return "instance.unavailable";
        if (!handoffConfigured)
            return "handoff-unavailable";
        if (!hasIdentity)
            return "identity-unavailable";
        return null;
    }

    private static ManagedElsaInstanceOverviewActionDecisionResponse ActionDecision(
        bool canMutate,
        bool hasActiveOperation,
        string? mutationDenial,
        ElsaInstanceCommercialGateDecision gate)
    {
        if (!canMutate)
            return new(false, ManagedElsaInstanceOverviewEndpoints.PermissionRequiredCode);
        if (hasActiveOperation)
            return new(false, ManagedElsaInstanceOverviewEndpoints.OperationActiveCode);
        if (mutationDenial is not null)
            return new(false, mutationDenial);
        if (!gate.Allowed)
            return new(false, gate.Code);
        return new(true, null);
    }

    private static string? CustomerFailureCode(string? failureCode) =>
        ManagedElsaInstanceOverviewEndpoints.ClassifyCustomerActivity(
            diagnosticCode: null,
            operationFailureCode: failureCode).DiagnosticCode;
}

internal sealed class ReleaseVersionComparer : IComparer<string>
{
    public static ReleaseVersionComparer Instance { get; } = new();

    public int Compare(string? left, string? right)
    {
        if (ReferenceEquals(left, right))
            return 0;
        if (left is null)
            return -1;
        if (right is null)
            return 1;

        var leftParts = left.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var rightParts = right.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var length = Math.Max(leftParts.Length, rightParts.Length);
        for (var index = 0; index < length; index++)
        {
            var leftValue = index < leftParts.Length && int.TryParse(TakeNumericPrefix(leftParts[index]), out var parsedLeft)
                ? parsedLeft
                : 0;
            var rightValue = index < rightParts.Length && int.TryParse(TakeNumericPrefix(rightParts[index]), out var parsedRight)
                ? parsedRight
                : 0;
            var compared = leftValue.CompareTo(rightValue);
            if (compared != 0)
                return compared;
        }

        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static string TakeNumericPrefix(string value)
    {
        var length = 0;
        while (length < value.Length && char.IsAsciiDigit(value[length]))
            length++;
        return length == 0 ? "0" : value[..length];
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ManagedElsaInstanceApplyReleaseRequest(string? Version);

public sealed record ManagedElsaInstanceOverviewResponse(
    ManagedElsaInstanceOverviewSummaryResponse Summary,
    ManagedElsaInstanceOverviewHealthResponse Health,
    ManagedElsaInstanceOverviewReleaseResponse Release,
    ManagedElsaInstanceOverviewPolicyResponse Policy,
    IReadOnlyList<ManagedElsaInstanceOverviewComponentResponse> Components,
    ManagedElsaInstanceOverviewActiveOperationResponse? ActiveOperation,
    ManagedElsaInstanceOverviewLastOperationResponse? LastOperation,
    ManagedElsaInstanceOverviewAllowedActionsResponse AllowedActions);

public sealed record ManagedElsaInstanceOverviewSummaryResponse(
    Guid InstanceId,
    string Name,
    string Slug,
    ElsaDesiredLifecycle DesiredLifecycle,
    ElsaObservedLifecycle ObservedLifecycle,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int Version,
    bool CanOpen,
    string? UnavailableReason);

public sealed record ManagedElsaInstanceOverviewHealthResponse(
    ManagedLifecycleOperationalHealthStatus Status,
    string DiagnosticCode,
    DateTimeOffset EvaluatedAt,
    IReadOnlyList<ManagedElsaInstanceOverviewAlertResponse> Alerts);

public sealed record ManagedElsaInstanceOverviewAlertResponse(
    string Code,
    ManagedLifecycleOperationalHealthAlertSeverity Severity);

public sealed record ManagedElsaInstanceOverviewReleaseResponse(
    string DistributionId,
    string ReleaseLine,
    string? Version,
    string Channel);

public sealed record ManagedElsaInstanceOverviewPolicyResponse(
    string PatchUpdates,
    string MinorUpdates,
    string MajorMigrations);

public sealed record ManagedElsaInstanceOverviewComponentResponse(string ComponentId, string Digest);

public sealed record ManagedElsaInstanceOverviewActiveOperationResponse(
    Guid OperationId,
    ElsaInstanceOperationAction Action,
    ElsaInstanceOperationState State,
    DateTimeOffset AcceptedAt,
    DateTimeOffset? StartedAt)
{
    public ManagedElsaInstanceOverviewProgressResponse? Progress { get; init; }
}

public sealed record ManagedElsaInstanceOverviewProgressResponse
{
    public string? Phase { get; init; }
    public string? AttemptedStep { get; init; }
    public int? AttemptNumber { get; init; }
    public DateTimeOffset? AttemptStartedAt { get; init; }
}

public sealed record ManagedElsaInstanceOverviewLastOperationResponse(
    ElsaInstanceOperationAction Action,
    ElsaInstanceOperationState State,
    DateTimeOffset? CompletedAt,
    string? FailureCode);

public sealed record ManagedElsaInstanceOverviewAllowedActionsResponse(
    ManagedElsaInstanceOverviewActionDecisionResponse Restart,
    ManagedElsaInstanceOverviewActionDecisionResponse ApplyRelease,
    ManagedElsaInstanceOverviewActionDecisionResponse Open);

public sealed record ManagedElsaInstanceOverviewActionDecisionResponse(bool Allowed, string? ReasonCode);

public sealed record ManagedElsaInstanceAvailableReleasesResponse(
    IReadOnlyList<ManagedElsaInstanceAvailableReleaseResponse> Items);

public sealed record ManagedElsaInstanceAvailableReleaseResponse(
    string ReleaseLine,
    string Version,
    string Channel,
    string ChangeKind);

public sealed record ManagedElsaInstanceActivityResponse(
    IReadOnlyList<ManagedElsaInstanceActivityItemResponse> Items,
    bool HasMore);

public enum ManagedElsaInstanceActivitySeverity
{
    Informational,
    Warning,
    Failed
}

internal sealed record CustomerActivityClassification(
    string? DiagnosticCode,
    ManagedElsaInstanceActivitySeverity Severity,
    string? Message);

public sealed record ManagedElsaInstanceActivityItemResponse(
    long Sequence,
    string EventType,
    DateTimeOffset OccurredAt,
    ElsaInstanceOperationAction? Action,
    string? PriorState,
    string? NewState,
    string? DiagnosticCode,
    ManagedElsaInstanceActivitySeverity Severity,
    string? Message,
    string ActorKind);

public sealed record ManagedElsaInstanceOverviewOperationResponse(
    Guid OperationId,
    ElsaInstanceOperationAction Action,
    ElsaInstanceOperationState State,
    DateTimeOffset AcceptedAt);
