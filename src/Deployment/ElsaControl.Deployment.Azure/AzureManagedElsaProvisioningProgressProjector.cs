using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Core.Instances;

namespace ElsaControl.Deployment.Azure;

/// <summary>
/// The already-correlated inputs required to build the customer-safe progress view.
/// Correlation, authorization, and persistence remain outside this pure projector.
/// </summary>
public sealed record AzureManagedElsaProvisioningProgressProjectionInput(
    ElsaInstanceLifecycleTopologySnapshot? Topology,
    ElsaInstanceLifecycleTopologyOperation? LifecycleOperation,
    AzureProviderOperation? ProviderOperation,
    IReadOnlyList<AzureProviderOperationTransition>? Transitions = null,
    bool HistoryUnavailable = false);

[Flags]
internal enum AzureManagedElsaProvisioningMappingAnomaly
{
    None = 0,
    UnknownPhase = 1,
    NonMonotonicStage = 2
}

/// <summary>
/// Maps private lifecycle/provider records into the narrow managed provisioning contract.
/// No provider identifiers, messages, diagnostics, resource references, or operation
/// identifiers are copied into the result.
/// </summary>
public static class AzureManagedElsaProvisioningProgressProjector
{
    private const string Provider = "azure";

    internal static AzureManagedElsaProvisioningMappingAnomaly DetectMappingAnomalies(
        AzureProviderOperation? provider,
        IReadOnlyList<AzureProviderOperationTransition>? transitions)
    {
        var result = AzureManagedElsaProvisioningMappingAnomaly.None;
        var highestStage = -1;
        foreach (var transition in (transitions ?? Array.Empty<AzureProviderOperationTransition>())
                     .OrderBy(item => item.Sequence)
                     .ThenBy(item => item.OccurredAt))
        {
            Observe(transition.Phase);
        }

        if (provider is not null)
            Observe(provider.Phase);

        return result;

        void Observe(AzureProviderOperationPhase phase)
        {
            var stage = MapStage(phase);
            if (stage is null)
            {
                result |= AzureManagedElsaProvisioningMappingAnomaly.UnknownPhase;
                return;
            }

            var index = StageIndex(stage);
            if (index < highestStage)
                result |= AzureManagedElsaProvisioningMappingAnomaly.NonMonotonicStage;
            else
                highestStage = index;
        }
    }

    public static ManagedElsaProvisioningProgress Project(
        AzureManagedElsaProvisioningProgressProjectionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var lifecycle = input.LifecycleOperation is { Action: ElsaInstanceOperationAction.Create }
            ? input.LifecycleOperation
            : null;
        var provider = input.ProviderOperation;
        var events = (input.Transitions ?? Array.Empty<AzureProviderOperationTransition>())
            .OrderBy(transition => transition.Sequence)
            .ThenBy(transition => transition.OccurredAt)
            .Select(MapActivity)
            .Where(mapped => mapped is not null)
            .Select(mapped => mapped!)
            .ToList();

        var providerStage = provider is null ? null : MapStage(provider.Phase);
        var eventStage = events
            .Select(activity => StageIndex(activity.Stage))
            .Where(index => index >= 0)
            .Select(index => (int?)index)
            .DefaultIfEmpty()
            .Max();
        var knownStage = Max(providerStage is null ? null : StageIndex(providerStage), eventStage);

        var startedAt = lifecycle?.AcceptedAt.ToUniversalTime() ?? provider?.CreatedAt.ToUniversalTime();
        var lastUpdatedAt = Latest(
            lifecycle?.AcceptedAt,
            lifecycle?.StartedAt,
            lifecycle?.CompletedAt,
            provider?.CreatedAt,
            provider?.UpdatedAt,
            provider?.CompletedAt,
            input.Transitions?.Select(transition => transition.OccurredAt).ToArray());

        if (input.HistoryUnavailable)
            return Unavailable(startedAt, lastUpdatedAt, lifecycle?.CompletedAt, lifecycle);

        var completedAt = lifecycle?.CompletedAt?.ToUniversalTime();
        var lifecycleState = lifecycle?.State;
        var observedReady = input.Topology?.ObservedLifecycle == ElsaObservedLifecycle.Ready;
        var lifecycleTerminal = lifecycleState is ElsaInstanceOperationState.Succeeded or
            ElsaInstanceOperationState.Failed or
            ElsaInstanceOperationState.Cancelled;

        string state;
        string? diagnosticCode = null;
        var blocked = false;
        var allReady = false;

        // Lifecycle terminal state is authoritative whenever it is available.
        if (lifecycleState is ElsaInstanceOperationState.Failed or ElsaInstanceOperationState.Cancelled)
        {
            state = ManagedElsaProvisioningProgressStates.Failed;
            diagnosticCode = lifecycleState == ElsaInstanceOperationState.Cancelled
                ? ManagedElsaProvisioningProgressDiagnostics.Cancelled
                : ManagedElsaProvisioningProgressDiagnostics.Failed;
            blocked = true;
        }
        else if (lifecycleState == ElsaInstanceOperationState.RecoveryRequired ||
                 (!lifecycleTerminal && provider?.Status == AzureProviderOperationStatus.RecoveryRequired))
        {
            state = ManagedElsaProvisioningProgressStates.Stale;
            diagnosticCode = ManagedElsaProvisioningProgressDiagnostics.RequiresAttention;
            blocked = true;
        }
        else if (lifecycleState == ElsaInstanceOperationState.Succeeded && observedReady)
        {
            state = ManagedElsaProvisioningProgressStates.Ready;
            allReady = true;
            completedAt ??= provider?.CompletedAt?.ToUniversalTime();
        }
        else if (!lifecycleTerminal && provider?.Status is AzureProviderOperationStatus.Failed or AzureProviderOperationStatus.Cancelled)
        {
            state = ManagedElsaProvisioningProgressStates.Failed;
            diagnosticCode = provider.Status == AzureProviderOperationStatus.Cancelled
                ? ManagedElsaProvisioningProgressDiagnostics.Cancelled
                : ManagedElsaProvisioningProgressDiagnostics.Failed;
            blocked = true;
            completedAt ??= provider.CompletedAt?.ToUniversalTime();
        }
        else if (!lifecycleTerminal && provider?.Status == AzureProviderOperationStatus.Succeeded && observedReady)
        {
            state = ManagedElsaProvisioningProgressStates.Ready;
            allReady = true;
            completedAt ??= provider.CompletedAt?.ToUniversalTime();
        }
        else if (lifecycle is null && provider is null)
        {
            return Unavailable();
        }
        else if (provider is null && lifecycleState is ElsaInstanceOperationState.Accepted or
                 ElsaInstanceOperationState.WaitingForPriorOperation or
                 ElsaInstanceOperationState.Queued or ElsaInstanceOperationState.EntitlementHeld)
        {
            state = ManagedElsaProvisioningProgressStates.Queued;
            knownStage ??= 0;
        }
        else if (provider is not null)
        {
            state = ManagedElsaProvisioningProgressStates.Active;
        }
        else
        {
            return Unavailable(startedAt, lastUpdatedAt, lifecycle?.CompletedAt, lifecycle);
        }

        if (state == ManagedElsaProvisioningProgressStates.Ready)
        {
            knownStage = ManagedElsaProvisioningProgressStages.Ordered.Count - 1;
        }
        if (lifecycle is not null && !events.Any(activity => activity.MessageCode == "request.accepted"))
        {
            events.Insert(0, new MappedActivity(
                0,
                ManagedElsaProvisioningProgressStages.RequestAccepted,
                ManagedElsaProvisioningProgressActivityStatuses.Started,
                "request.accepted",
                lifecycle.AcceptedAt.ToUniversalTime()));
        }

        if (allReady)
        {
            var sequence = NextSequence(events);
            events.Add(new MappedActivity(
                sequence,
                ManagedElsaProvisioningProgressStages.Ready,
                ManagedElsaProvisioningProgressActivityStatuses.Ready,
                "engine.ready",
                (completedAt ?? lastUpdatedAt ?? startedAt ?? DateTimeOffset.UtcNow).ToUniversalTime()));
        }
        else if (blocked)
        {
            var sequence = NextSequence(events);
            var stage = StageAt(knownStage ?? 0);
            events.Add(new MappedActivity(
                sequence,
                stage,
                ManagedElsaProvisioningProgressActivityStatuses.Blocked,
                diagnosticCode == ManagedElsaProvisioningProgressDiagnostics.RequiresAttention
                    ? ManagedElsaProvisioningProgressDiagnostics.RequiresAttention
                    : diagnosticCode == ManagedElsaProvisioningProgressDiagnostics.Cancelled
                        ? ManagedElsaProvisioningProgressDiagnostics.Cancelled
                        : ManagedElsaProvisioningProgressDiagnostics.Failed,
                (completedAt ?? lastUpdatedAt ?? startedAt ?? DateTimeOffset.UtcNow).ToUniversalTime()));
        }

        var activity = Coalesce(events)
            .OrderBy(entry => entry.Sequence)
            .ThenBy(entry => entry.OccurredAt)
            .Select(entry => new ManagedElsaProvisioningActivity(
                entry.Sequence,
                entry.Stage,
                entry.Status,
                entry.MessageCode,
                entry.OccurredAt.ToUniversalTime()))
            .ToArray();

        var stageTimes = events
            .GroupBy(entry => StageIndex(entry.Stage))
            .ToDictionary(group => group.Key, group => (
                StartedAt: group.Min(entry => entry.OccurredAt).ToUniversalTime(),
                CompletedAt: group.Max(entry => entry.OccurredAt).ToUniversalTime()));

        var stages = BuildStages(
            state,
            knownStage,
            blocked,
            allReady,
            stageTimes,
            completedAt,
            startedAt,
            lastUpdatedAt);

        var currentStage = allReady || knownStage is null ? null : StageAt(knownStage.Value);
        return new ManagedElsaProvisioningProgress(
            state,
            Provider,
            currentStage,
            startedAt,
            lastUpdatedAt,
            completedAt,
            diagnosticCode,
            stages,
            activity);
    }

    private static ManagedElsaProvisioningProgress Unavailable(
        DateTimeOffset? startedAt = null,
        DateTimeOffset? lastUpdatedAt = null,
        DateTimeOffset? completedAt = null,
        ElsaInstanceLifecycleTopologyOperation? lifecycle = null) =>
        new(
            ManagedElsaProvisioningProgressStates.Unavailable,
            Provider,
            null,
            startedAt?.ToUniversalTime(),
            lastUpdatedAt?.ToUniversalTime(),
            completedAt?.ToUniversalTime(),
            ManagedElsaProvisioningProgressDiagnostics.HistoryUnavailable,
            ManagedElsaProvisioningProgressStages.Ordered
                .Select(code => new ManagedElsaProvisioningStage(
                    code,
                    ManagedElsaProvisioningProgressStageStatuses.Unknown,
                    null,
                    null))
                .ToArray(),
            lifecycle is null
                ? Array.Empty<ManagedElsaProvisioningActivity>()
                :
                [
                    new ManagedElsaProvisioningActivity(
                        0,
                        ManagedElsaProvisioningProgressStages.RequestAccepted,
                        ManagedElsaProvisioningProgressActivityStatuses.Started,
                        "request.accepted",
                        lifecycle.AcceptedAt.ToUniversalTime())
                ]);

    private static IReadOnlyList<ManagedElsaProvisioningStage> BuildStages(
        string state,
        int? knownStage,
        bool blocked,
        bool allReady,
        IReadOnlyDictionary<int, (DateTimeOffset StartedAt, DateTimeOffset CompletedAt)> times,
        DateTimeOffset? completedAt,
        DateTimeOffset? startedAt,
        DateTimeOffset? lastUpdatedAt)
    {
        var stages = new List<ManagedElsaProvisioningStage>(ManagedElsaProvisioningProgressStages.Ordered.Count);
        var current = knownStage;
        for (var index = 0; index < ManagedElsaProvisioningProgressStages.Ordered.Count; index++)
        {
            var code = StageAt(index);
            times.TryGetValue(index, out var stageTime);
            DateTimeOffset? stageStartedAt = stageTime.StartedAt == default ? null : stageTime.StartedAt;
            if (index == 0)
                stageStartedAt ??= startedAt?.ToUniversalTime();

            string status;
            DateTimeOffset? stageCompletedAt = null;
            if (allReady)
            {
                status = ManagedElsaProvisioningProgressStageStatuses.Completed;
                stageCompletedAt = stageTime.CompletedAt == default
                    ? completedAt ?? lastUpdatedAt
                    : stageTime.CompletedAt;
            }
            else if (blocked && index == current)
            {
                status = ManagedElsaProvisioningProgressStageStatuses.Blocked;
            }
            else if (current is not null && index < current)
            {
                status = ManagedElsaProvisioningProgressStageStatuses.Completed;
                stageCompletedAt = stageTime.CompletedAt == default
                    ? (index + 1 < ManagedElsaProvisioningProgressStages.Ordered.Count
                        ? StageStart(times, index + 1)
                        : completedAt ?? lastUpdatedAt)
                    : stageTime.CompletedAt;
            }
            else if (current == index)
            {
                status = ManagedElsaProvisioningProgressStageStatuses.Current;
            }
            else
            {
                status = ManagedElsaProvisioningProgressStageStatuses.Pending;
            }

            stages.Add(new ManagedElsaProvisioningStage(code, status, stageStartedAt, stageCompletedAt));
        }

        return stages;
    }

    private static DateTimeOffset? StageStart(
        IReadOnlyDictionary<int, (DateTimeOffset StartedAt, DateTimeOffset CompletedAt)> times,
        int index) => times.TryGetValue(index, out var time) ? time.StartedAt : null;

    private static IEnumerable<MappedActivity> Coalesce(IEnumerable<MappedActivity> entries)
    {
        var seen = new HashSet<(string Stage, string Status, string MessageCode)>();
        foreach (var entry in entries.OrderBy(item => item.Sequence).ThenBy(item => item.OccurredAt))
        {
            if (seen.Add((entry.Stage, entry.Status, entry.MessageCode)))
                yield return entry;
        }
    }

    private static MappedActivity? MapActivity(AzureProviderOperationTransition transition)
    {
        var stage = MapStage(transition.Phase);
        var messageCode = MapMessageCode(transition.Phase, transition.Status);
        if (stage is null || messageCode is null)
            return null;

        var status = messageCode is "request.accepted" or "foundation.preparing" or "runtime.deploying" or
            "health.verifying" or "traffic.routing"
            ? ManagedElsaProvisioningProgressActivityStatuses.Started
            : ManagedElsaProvisioningProgressActivityStatuses.Completed;

        if (transition.Status is AzureProviderOperationStatus.Failed or AzureProviderOperationStatus.Cancelled or
            AzureProviderOperationStatus.RecoveryRequired)
        {
            status = ManagedElsaProvisioningProgressActivityStatuses.Blocked;
            messageCode = transition.Status == AzureProviderOperationStatus.Cancelled
                ? ManagedElsaProvisioningProgressDiagnostics.Cancelled
                : transition.Status == AzureProviderOperationStatus.RecoveryRequired
                    ? ManagedElsaProvisioningProgressDiagnostics.RequiresAttention
                    : ManagedElsaProvisioningProgressDiagnostics.Failed;
        }

        return new MappedActivity(
            transition.Sequence,
            stage,
            status,
            messageCode,
            transition.OccurredAt.ToUniversalTime());
    }

    private static string? MapStage(AzureProviderOperationPhase phase) => phase switch
    {
        AzureProviderOperationPhase.Planned => ManagedElsaProvisioningProgressStages.RequestAccepted,
        AzureProviderOperationPhase.FoundationSubmitted or AzureProviderOperationPhase.FoundationObserved => ManagedElsaProvisioningProgressStages.HostingFoundation,
        AzureProviderOperationPhase.AcrPullObserved or AzureProviderOperationPhase.SeedSecretsObserved or
            AzureProviderOperationPhase.SqlFirewallReady or AzureProviderOperationPhase.SqlBootstrapReady or
            AzureProviderOperationPhase.FoundationReady => ManagedElsaProvisioningProgressStages.Configuration,
        AzureProviderOperationPhase.WorkloadSubmitted or AzureProviderOperationPhase.WorkloadReady => ManagedElsaProvisioningProgressStages.RuntimeDeployment,
        AzureProviderOperationPhase.HealthVerified => ManagedElsaProvisioningProgressStages.HealthVerification,
        AzureProviderOperationPhase.TrafficPromoted => ManagedElsaProvisioningProgressStages.TrafficRouting,
        _ => null
    };

    private static string? MapMessageCode(AzureProviderOperationPhase phase, AzureProviderOperationStatus status) => phase switch
    {
        AzureProviderOperationPhase.Planned => "request.accepted",
        AzureProviderOperationPhase.FoundationSubmitted => "foundation.preparing",
        AzureProviderOperationPhase.FoundationObserved or AzureProviderOperationPhase.FoundationReady => "foundation.ready",
        AzureProviderOperationPhase.AcrPullObserved => "configuration.registry-ready",
        AzureProviderOperationPhase.SeedSecretsObserved => "configuration.secrets-ready",
        AzureProviderOperationPhase.SqlFirewallReady or AzureProviderOperationPhase.SqlBootstrapReady => "configuration.database-ready",
        AzureProviderOperationPhase.WorkloadSubmitted => "runtime.deploying",
        AzureProviderOperationPhase.WorkloadReady => "runtime.deployed",
        AzureProviderOperationPhase.HealthVerified => status == AzureProviderOperationStatus.Running
            ? "health.verifying"
            : "health.verified",
        AzureProviderOperationPhase.TrafficPromoted => status == AzureProviderOperationStatus.Running
            ? "traffic.routing"
            : "traffic.routed",
        _ => null
    };

    private static int StageIndex(string stage)
    {
        for (var index = 0; index < ManagedElsaProvisioningProgressStages.Ordered.Count; index++)
        {
            if (string.Equals(ManagedElsaProvisioningProgressStages.Ordered[index], stage, StringComparison.Ordinal))
                return index;
        }

        return -1;
    }

    private static int? Max(int? left, int? right) => left is null ? right : right is null ? left : Math.Max(left.Value, right.Value);

    private static string StageAt(int index) => ManagedElsaProvisioningProgressStages.Ordered[index];

    private static long NextSequence(IEnumerable<MappedActivity> entries) =>
        entries.Select(entry => entry.Sequence).DefaultIfEmpty(0).Max() + 1;

    private static DateTimeOffset? Latest(params object?[] values)
    {
        var timestamps = values
            .SelectMany(value => value switch
            {
                DateTimeOffset timestamp => [timestamp],
                IEnumerable<DateTimeOffset> collection => collection,
                _ => Array.Empty<DateTimeOffset>()
            })
            .Select(value => value.ToUniversalTime())
            .ToArray();
        return timestamps.Length == 0 ? null : timestamps.Max();
    }

    private sealed record MappedActivity(
        long Sequence,
        string Stage,
        string Status,
        string MessageCode,
        DateTimeOffset OccurredAt);
}
