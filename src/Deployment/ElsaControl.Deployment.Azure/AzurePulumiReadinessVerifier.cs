using System.Text.Json;
using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Core.Instances;
using ElsaControl.Provisioning.Core;

namespace ElsaControl.Deployment.Azure;

/// <summary>
/// Claims a queued Pulumi WorkloadReady operation, probes <c>{endpoint}/health</c>
/// with the Bicep curl contract, then reads Container App traffic without writing
/// it. A pass finalizes Succeeded/Healthy so observation can move the engine to Ready.
/// </summary>
public sealed class AzurePulumiReadinessVerifier : IAzurePulumiReadinessVerifier
{
    public const string WorkerId = "pulumi-readiness";
    private const int MaximumHealthReportCharacters = 64;
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromHours(1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly AzureProviderRunnerOptions _options;
    private readonly AzureProviderTargetScope _scope;
    private readonly IAzureProviderOperationStore _store;
    private readonly IAzureCommandProcess _process;
    private readonly TimeProvider _timeProvider;

    public AzurePulumiReadinessVerifier(
        AzureProviderRunnerOptions options,
        AzureProviderTargetScope scope,
        IAzureProviderOperationStore store,
        TimeProvider? timeProvider = null)
        : this(
            options,
            scope,
            store,
            new AzureCommandProcess(options.CommandTimeout, options.MaximumOutputCharacters),
            timeProvider)
    {
    }

    internal AzurePulumiReadinessVerifier(
        AzureProviderRunnerOptions options,
        AzureProviderTargetScope scope,
        IAzureProviderOperationStore store,
        IAzureCommandProcess process,
        TimeProvider? timeProvider = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _scope = scope ?? throw new ArgumentNullException(nameof(scope));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _process = process ?? throw new ArgumentNullException(nameof(process));
        _timeProvider = timeProvider ?? TimeProvider.System;
        AzureProviderOperationValidation.ValidateWorkerId(WorkerId);
    }

    public async Task VerifyAsync(AzureProviderOperation operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();
        var now = _timeProvider.GetUtcNow();
        var leaseToken = Guid.NewGuid().ToString("N");
        var claimed = await _store.ClaimAsync(
                operation.WorkspaceId,
                operation.Id,
                WorkerId,
                leaseToken,
                LeaseDuration,
                now,
                operation.Version,
                cancellationToken)
            .ConfigureAwait(false);
        if (claimed is null)
            return;

        try
        {
            await VerifyClaimedAsync(claimed, leaseToken, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await FinalizeAsync(
                    claimed,
                    leaseToken,
                    AzureProviderOperationStatus.Failed,
                    ManagedElsaReasonCodeCatalog.ProviderDispatchFailed,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task VerifyClaimedAsync(
        AzureProviderOperation operation,
        string leaseToken,
        CancellationToken cancellationToken)
    {
        if (operation.InstanceId is not { } instanceId || instanceId == Guid.Empty)
        {
            await FinalizeAsync(
                    operation,
                    leaseToken,
                    AzureProviderOperationStatus.Failed,
                    ManagedElsaReasonCodeCatalog.ProviderDispatchFailed,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (!ElsaManagedEndpointOrigin.TryCreate(operation.Endpoint, out var origin))
        {
            await FinalizeAsync(
                    operation,
                    leaseToken,
                    AzureProviderOperationStatus.Failed,
                    ManagedElsaReasonCodeCatalog.AzureRecoveryWorkloadOutputsInvalid,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var appName = ProvisioningEngineContainerApp.ForInstance(instanceId);
        if (!HostMatchesApp(origin.Value, appName))
        {
            await FinalizeAsync(
                    operation,
                    leaseToken,
                    AzureProviderOperationStatus.Failed,
                    ManagedElsaReasonCodeCatalog.AzureRecoveryWorkloadOutputsInvalid,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var health = await ProbeHealthAsync(origin.Value, cancellationToken).ConfigureAwait(false);
        if (health is null)
        {
            await FinalizeAsync(
                    operation,
                    leaseToken,
                    AzureProviderOperationStatus.Failed,
                    ManagedElsaReasonCodeCatalog.ProvisioningTimeoutHealth,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var healthVerified = await CheckpointAsync(
                operation,
                leaseToken,
                new AzureProviderCheckpoint(
                    AzureProviderOperationPhase.HealthVerified,
                    PulumiStepLedgerMapper.HealthVerifiedCode,
                    PulumiStepLedgerMapper.HealthVerifiedMessage,
                    operation.Resources,
                    origin.Value,
                    health.Value,
                    []),
                cancellationToken)
            .ConfigureAwait(false);
        if (healthVerified is null)
            return;

        var resourceGroup = healthVerified.Resources.ResourceGroupName
            ?? ProvisioningEngineResourceGroup.ForInstance(instanceId);
        if (!await TrafficPointsAtLatestAsync(
                    resourceGroup,
                    appName,
                    healthVerified.Resources.WorkloadRevisionName,
                    cancellationToken)
                .ConfigureAwait(false))
        {
            await FinalizeAsync(
                    healthVerified,
                    leaseToken,
                    AzureProviderOperationStatus.Failed,
                    ManagedElsaReasonCodeCatalog.AzurePromotionUncertain,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var promoted = await CheckpointAsync(
                healthVerified,
                leaseToken,
                new AzureProviderCheckpoint(
                    AzureProviderOperationPhase.TrafficPromoted,
                    PulumiStepLedgerMapper.TrafficRoutedCode,
                    PulumiStepLedgerMapper.TrafficRoutedMessage,
                    healthVerified.Resources with
                    {
                        StableTrafficRevisionName = healthVerified.Resources.WorkloadRevisionName
                    },
                    origin.Value,
                    health.Value,
                    []),
                cancellationToken)
            .ConfigureAwait(false);
        if (promoted is null)
            return;

        await FinalizeAsync(
                promoted,
                leaseToken,
                AzureProviderOperationStatus.Succeeded,
                PulumiStepLedgerMapper.OperationSucceededCode,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<AzureProviderHealth?> ProbeHealthAsync(string endpoint, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < _options.ObservationAttempts; attempt++)
        {
            var probe = await _process.ExecuteAsync(
                    new AzureCommandProcessRequest(
                        _options.CurlPath,
                        new[]
                        {
                            "--fail", "--silent", "--show-error",
                            "--retry", "30", "--retry-all-errors", "--retry-delay", "5", "--max-time", "10",
                            $"{endpoint.TrimEnd('/')}/health"
                        }.Select(AzureCommandArgument.Safe).ToArray()),
                    ParseHealthReport,
                    cancellationToken)
                .ConfigureAwait(false);
            if (probe.Succeeded)
            {
                var classified = ClassifyRuntimeHealth(probe.Value?.Value);
                if (classified is AzureProviderHealth.Healthy or AzureProviderHealth.Degraded)
                    return classified;
            }

            if (probe.Status == AzureCommandProcessStatus.Cancelled || cancellationToken.IsCancellationRequested)
                return null;
            if (attempt + 1 < _options.ObservationAttempts)
                await Task.Delay(_options.ObservationDelay, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    private async Task<bool> TrafficPointsAtLatestAsync(
        string resourceGroup,
        string appName,
        string? revisionName,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < _options.ObservationAttempts; attempt++)
        {
            var traffic = await _process.ExecuteAsync(
                    new AzureCommandProcessRequest(
                        _options.AzureCliPath,
                        new[]
                        {
                            "containerapp", "show",
                            "--subscription", _scope.SubscriptionId,
                            "--resource-group", resourceGroup,
                            "--name", appName,
                            "--query", "properties.configuration.ingress.traffic",
                            "--output", "json",
                            "--only-show-errors"
                        }.Select(AzureCommandArgument.Safe).ToArray()),
                    ParseTraffic,
                    cancellationToken)
                .ConfigureAwait(false);
            if (traffic.Succeeded && traffic.Value is not null && AcceptsLatest(traffic.Value.Value, revisionName))
                return true;
            if (traffic.Status == AzureCommandProcessStatus.Cancelled || cancellationToken.IsCancellationRequested)
                return false;
            if (attempt + 1 < _options.ObservationAttempts)
                await Task.Delay(_options.ObservationDelay, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private async Task<AzureProviderOperation?> CheckpointAsync(
        AzureProviderOperation operation,
        string leaseToken,
        AzureProviderCheckpoint checkpoint,
        CancellationToken cancellationToken) =>
        await _store.CheckpointAsync(
                operation.WorkspaceId,
                operation.Id,
                leaseToken,
                checkpoint,
                _timeProvider.GetUtcNow(),
                operation.Version,
                cancellationToken)
            .ConfigureAwait(false);

    private async Task FinalizeAsync(
        AzureProviderOperation operation,
        string leaseToken,
        AzureProviderOperationStatus status,
        string code,
        CancellationToken cancellationToken) =>
        await _store.FinalizeAsync(
                operation.WorkspaceId,
                operation.Id,
                leaseToken,
                status,
                code,
                _timeProvider.GetUtcNow(),
                operation.Version,
                cancellationToken)
            .ConfigureAwait(false);

    private static bool HostMatchesApp(string endpoint, string appName)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var origin) || origin.HostNameType != UriHostNameType.Dns)
            return false;
        if (!origin.Host.EndsWith(".azurecontainerapps.io", StringComparison.OrdinalIgnoreCase))
            return true;
        return origin.Host.StartsWith(appName + ".", StringComparison.OrdinalIgnoreCase);
    }

    private static bool AcceptsLatest(IReadOnlyList<TrafficEntry> traffic, string? revisionName)
    {
        if (traffic.Count == 0 || traffic.Sum(entry => entry.Weight) != 100)
            return false;
        if (traffic.Any(entry => entry.LatestRevision && entry.Weight == 100)
            && !traffic.Any(entry => !entry.LatestRevision && entry.Weight != 0))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(revisionName))
            return false;
        var desired = traffic.Where(entry =>
            string.Equals(entry.RevisionName, revisionName, StringComparison.OrdinalIgnoreCase)).ToArray();
        var nonDesired = traffic.Any(entry =>
            !string.Equals(entry.RevisionName, revisionName, StringComparison.OrdinalIgnoreCase) && entry.Weight != 0);
        return desired.Length == 1 && desired[0].Weight == 100 && !nonDesired;
    }

    private static SafeValue<string> ParseHealthReport(ReadOnlyMemory<char> output) =>
        output.Length <= MaximumHealthReportCharacters ? new(output.ToString()) : throw new FormatException();

    private static SafeValue<IReadOnlyList<TrafficEntry>> ParseTraffic(ReadOnlyMemory<char> output) =>
        new(JsonSerializer.Deserialize<List<TrafficEntry>>(output.Span, JsonOptions) ?? throw new FormatException());

    private static AzureProviderHealth ClassifyRuntimeHealth(string? report) => report switch
    {
        "Healthy" => AzureProviderHealth.Healthy,
        "Degraded" => AzureProviderHealth.Degraded,
        "Unhealthy" => AzureProviderHealth.Failed,
        _ => AzureProviderHealth.Unknown
    };

    private sealed class TrafficEntry
    {
        public string? RevisionName { get; set; }
        public bool LatestRevision { get; set; }
        public int Weight { get; set; }
    }

    private sealed class SafeValue<T>(T value) : AzureCommandSafeOutput
    {
        public T Value { get; } = value;
    }
}
