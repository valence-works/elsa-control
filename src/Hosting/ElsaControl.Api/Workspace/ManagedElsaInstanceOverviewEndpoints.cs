using System.Collections.Frozen;
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
    internal const string PermissionRequiredCode = "instance.permission-required";
    internal const string OperationActiveCode = "instance.operation-active";
    internal const string ActivityLimitInvalidCode = "instance.activity-limit-invalid";
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
        "lifecycle.operation-updated"
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
                workspaceId, instance.Id, page: 1, pageSize: 1, cancellationToken, access.OrganizationId);
            var lastOperation = lastPage.Items.Count > 0 ? lastPage.Items[0] : null;
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
            var actionByOperationId = operations.Items.ToDictionary(operation => operation.Id, operation => operation.Action);
            var projected = events
                .Where(item => CustomerActivityEventTypes.Contains(item.EventType))
                .Select(item => ManagedElsaInstanceOverviewProjection.ToActivityItem(item, actionByOperationId))
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
            CancellationToken cancellationToken) =>
        {
            var prepared = await PrepareMutationAsync(
                context, workspaceId, instanceId, store, commercialGate,
                ElsaInstanceOperationAction.Restart, cancellationToken);
            if (prepared.Error is not null)
                return prepared.Error;
            try
            {
                var accepted = await lifecycle.RestartAsync(
                    new ElsaInstanceLifecycleRequest(
                        WorkspaceId: workspaceId,
                        InstanceId: instanceId,
                        ExpectedVersion: prepared.ExpectedVersion,
                        IdempotencyKey: prepared.IdempotencyKey,
                        Reason: "Restart",
                        ActorAccountId: prepared.Access!.AccountId),
                    cancellationToken);
                return AcceptedOperation(accepted);
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
                    "The requested restart is invalid.",
                    StatusCodes.Status422UnprocessableEntity);
            }
        }).RequireWorkspaceAccess(WorkspaceOperation.MutateWorkspaceResource).AllowCloudBff();

        group.MapPost("/{instanceId:guid}/apply-release", async (
            Guid workspaceId,
            Guid instanceId,
            ManagedElsaInstanceApplyReleaseRequest request,
            HttpContext context,
            IElsaInstanceCommercialGate commercialGate,
            IGovernedReleaseCatalogStore catalog,
            ElsaInstanceLifecycleService lifecycle,
            IElsaInstanceLifecycleStore store,
            CancellationToken cancellationToken) =>
        {
            var prepared = await PrepareMutationAsync(
                context, workspaceId, instanceId, store, commercialGate,
                ElsaInstanceOperationAction.UpdateIntent, cancellationToken);
            if (prepared.Error is not null)
                return prepared.Error;
            var instance = prepared.Instance!;
            var selected = (await ListAvailableReleasesAsync(catalog, instance, cancellationToken))
                .FirstOrDefault(release =>
                    string.Equals(release.Version, request.Version?.Trim(), StringComparison.OrdinalIgnoreCase));
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
                    channel: selected.Channel,
                    patchUpdates: instance.Intent.Release.PatchUpdates,
                    minorUpdates: instance.Intent.Release.MinorUpdates,
                    majorMigrations: instance.Intent.Release.MajorMigrations)
            };
            var update = new ElsaInstanceIntentUpdateRequest(
                WorkspaceId: workspaceId,
                InstanceId: instanceId,
                Intent: nextIntent,
                ExpectedVersion: prepared.ExpectedVersion,
                IdempotencyKey: prepared.IdempotencyKey,
                Reason: $"Apply release {selected.Version}",
                ActorAccountId: prepared.Access!.AccountId);
            try
            {
                var accepted = selected.ChangeKind == "minor"
                    ? await lifecycle.ApproveMinorUpgradeAsync(update, cancellationToken)
                    : await lifecycle.UpdateIntentAsync(update, cancellationToken);
                return AcceptedOperation(accepted);
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
                    "The requested release change is invalid.",
                    StatusCodes.Status422UnprocessableEntity);
            }
        }).RequireWorkspaceAccess(WorkspaceOperation.MutateWorkspaceResource).AllowCloudBff();

        return group;
    }

    internal static async Task<IReadOnlyList<ManagedElsaInstanceAvailableReleaseResponse>> ListAvailableReleasesAsync(
        IGovernedReleaseCatalogStore catalog,
        ElsaInstance instance,
        CancellationToken cancellationToken)
    {
        var entries = await catalog.QueryAsync(new GovernedReleaseCatalogQuery(
            DistributionId: instance.Intent.Release.DistributionId,
            CatalogLifecycle: "supported",
            RegistryClass: "paid",
            TopologyId: instance.Intent.Application.TopologyId), cancellationToken);
        var currentVersion = instance.CurrentResolvedRelease?.Version
            ?? instance.Intent.Release.RequestedVersion
            ?? instance.Intent.Release.ReleaseLine;
        if (!TryReadMajor(instance.Intent.Release.ReleaseLine, out var currentMajor))
            return [];

        return entries
            .Where(entry =>
                string.Equals(entry.CatalogLifecycle, "supported", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(entry.RegistryClass, "paid", StringComparison.OrdinalIgnoreCase) &&
                TryReadMajor(entry.Distribution.ReleaseLine, out var major) &&
                major == currentMajor)
            .Select(entry =>
            {
                var sameLine = string.Equals(
                    entry.Distribution.ReleaseLine, instance.Intent.Release.ReleaseLine, StringComparison.OrdinalIgnoreCase);
                return new ManagedElsaInstanceAvailableReleaseResponse(
                    entry.Distribution.ReleaseLine,
                    entry.Distribution.ReleaseVersion,
                    entry.Distribution.Channel,
                    string.Equals(entry.Distribution.ReleaseVersion, currentVersion, StringComparison.OrdinalIgnoreCase),
                    sameLine ? "patch" : "minor");
            })
            .GroupBy(release => release.Version, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderByDescending(release => release.Version, ReleaseVersionComparer.Instance)
            .ThenBy(release => release.ReleaseLine, StringComparer.OrdinalIgnoreCase)
            .Take(MaxAvailableReleases)
            .ToList();
    }

    private static async Task<PreparedMutation> PrepareMutationAsync(
        HttpContext context,
        Guid workspaceId,
        Guid instanceId,
        IElsaInstanceLifecycleStore store,
        IElsaInstanceCommercialGate commercialGate,
        ElsaInstanceOperationAction action,
        CancellationToken cancellationToken)
    {
        var keyResult = ManagedElsaInstanceEndpoints.ReadIdempotencyKey(context);
        if (keyResult.State == IdempotencyKeyState.Missing)
            return PreparedMutation.Fail(ManagedElsaInstanceEndpoints.Problem(
                "instance.idempotency-key-required",
                "Idempotency-Key is required for instance operations.",
                StatusCodes.Status400BadRequest));
        if (keyResult.State == IdempotencyKeyState.Invalid)
            return PreparedMutation.Fail(ManagedElsaInstanceEndpoints.Problem(
                "instance.idempotency-key-invalid",
                "Idempotency-Key must be a safe token of at most 128 characters.",
                StatusCodes.Status400BadRequest));
        var expectedVersion = ManagedElsaInstanceEndpoints.ReadIfMatch(context.Request);
        if (expectedVersion is null)
            return PreparedMutation.Fail(ManagedElsaInstanceEndpoints.Problem(
                "instance.if-match-required",
                "A strong If-Match header is required for instance operations.",
                StatusCodes.Status428PreconditionRequired));

        var instance = await store.GetInstanceAsync(workspaceId, instanceId, cancellationToken);
        if (instance is null)
            return PreparedMutation.Fail(Results.NotFound());

        var access = context.GetWorkspaceAccess();
        var commercialDecision = await commercialGate.EvaluateAsync(
            access.OrganizationId, action, cancellationToken: cancellationToken);
        if (!commercialDecision.Allowed)
            return PreparedMutation.Fail(ManagedElsaInstanceEndpoints.Problem(
                commercialDecision.Code, commercialDecision.Summary, StatusCodes.Status422UnprocessableEntity));

        return new PreparedMutation(null, instance, access, keyResult.Value!, expectedVersion.Value);
    }

    private static IResult AcceptedOperation(ElsaInstanceLifecycleAcceptance accepted) =>
        Results.Json(
            new ManagedElsaInstanceOverviewOperationResponse(
                accepted.Operation.Id,
                accepted.Operation.Action,
                accepted.Operation.State,
                accepted.Operation.AcceptedAt),
            statusCode: StatusCodes.Status202Accepted);

    internal static string StrongETag(int version) => $"\"{version}\"";

    internal static string CustomerDiagnosticCode(string? code) =>
        code is not null && CustomerHealthCodes.Contains(code) ? code : RequiresAttentionCode;

    internal static bool TryReadMajor(string releaseLine, out int major)
    {
        var firstPart = releaseLine.Split('.', 2)[0];
        return int.TryParse(firstPart, out major);
    }

    private sealed record PreparedMutation(
        IResult? Error,
        ElsaInstance? Instance = null,
        WorkspaceAccess? Access = null,
        string IdempotencyKey = "",
        int ExpectedVersion = 0)
    {
        public static PreparedMutation Fail(IResult error) => new(error);
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
                ActionDecision(canMutate, hasActiveOperation, restartGate),
                ActionDecision(canMutate, hasActiveOperation, applyGate),
                new ManagedElsaInstanceOverviewActionDecisionResponse(canOpen, canOpen ? null : unavailableReason)));
    }

    internal static ManagedElsaInstanceActivityItemResponse ToActivityItem(
        ElsaInstanceAuditEventSummary item,
        IReadOnlyDictionary<Guid, ElsaInstanceOperationAction> actionByOperationId) =>
        new(
            item.Sequence,
            item.EventType,
            item.OccurredAt,
            item.OperationId is { } operationId && actionByOperationId.TryGetValue(operationId, out var action)
                ? action
                : null,
            item.PriorState,
            item.NewState,
            item.DiagnosticCode is null
                ? null
                : ManagedElsaInstanceOverviewEndpoints.CustomerDiagnosticCode(item.DiagnosticCode),
            item.OperatorSubject is not null ? "support" : item.ActorAccountId is not null ? "customer" : "system");

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
        ElsaInstanceCommercialGateDecision gate)
    {
        if (!canMutate)
            return new(false, ManagedElsaInstanceOverviewEndpoints.PermissionRequiredCode);
        if (hasActiveOperation)
            return new(false, ManagedElsaInstanceOverviewEndpoints.OperationActiveCode);
        if (!gate.Allowed)
            return new(false, gate.Code);
        return new(true, null);
    }

    private static string? CustomerFailureCode(string? failureCode)
    {
        if (failureCode is null)
            return null;
        return ManagedLifecycleOperationalHealthDiagnosticCodes.IsSafe(failureCode) &&
               !failureCode.Contains("provider.", StringComparison.Ordinal)
            ? failureCode
            : ManagedElsaInstanceOverviewEndpoints.RequiresAttentionCode;
    }
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
    bool IsCurrent,
    string ChangeKind);

public sealed record ManagedElsaInstanceActivityResponse(
    IReadOnlyList<ManagedElsaInstanceActivityItemResponse> Items,
    bool HasMore);

public sealed record ManagedElsaInstanceActivityItemResponse(
    long Sequence,
    string EventType,
    DateTimeOffset OccurredAt,
    ElsaInstanceOperationAction? Action,
    string? PriorState,
    string? NewState,
    string? DiagnosticCode,
    string ActorKind);

public sealed record ManagedElsaInstanceOverviewOperationResponse(
    Guid OperationId,
    ElsaInstanceOperationAction Action,
    ElsaInstanceOperationState State,
    DateTimeOffset AcceptedAt);
