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
    bool HistoryUnavailable = false,
    string? RecoveryReason = null,
    bool ResolveBlockingOperation = true);

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
    internal const string ProviderSubmissionAccepted = "provider.submission.accepted";
    internal const string ProviderSubmissionUncertain = "provider.submission.uncertain";
    internal const string ProviderReconciliationInProgress = ElsaInstanceProviderReconciliationService.InProgressCode;
    internal const string ProviderReconciliationHealthUnknown = ElsaInstanceProviderReconciliationService.HealthUnknownCode;
    internal const string ProviderReconciliationUnavailable = ElsaInstanceProviderReconciliationService.UnavailableCode;
    internal const string ProviderReconciliationUnknown = ElsaInstanceProviderReconciliationService.UnknownCode;
    internal const string ProviderReconciliationAmbiguous = ElsaInstanceProviderReconciliationService.AmbiguousCode;
    internal const string ProviderReconciliationCorrelationMismatch = ElsaInstanceProviderReconciliationService.CorrelationMismatchCode;
    internal const string ProviderReconciliationRetrySafe = ElsaInstanceProviderReconciliationService.RetrySafeCode;
    /// <summary>
    /// Inclusive: a snapshot with no provider progress for exactly 10:00 is <c>stale</c>.
    /// </summary>
    internal static readonly TimeSpan ProviderProgressStaleAfter = TimeSpan.FromMinutes(10);

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
        AzureManagedElsaProvisioningProgressProjectionInput input,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        var clock = timeProvider ?? TimeProvider.System;
        var now = clock.GetUtcNow();

        var lifecycle = input.LifecycleOperation;
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
        var recoveryRequired = lifecycleState == ElsaInstanceOperationState.RecoveryRequired;
        var hasFailureCode = !string.IsNullOrEmpty(lifecycle?.FailureCode);
        var reasonGroup = ClassifyRecoveryReason(input.RecoveryReason);
        var healthyContinuation = recoveryRequired && !hasFailureCode &&
            reasonGroup == RecoveryReasonGroup.HealthyContinuation;
        var transientUncertainty = recoveryRequired && !hasFailureCode &&
            reasonGroup == RecoveryReasonGroup.TransientUncertainty;
        var definiteProblem = recoveryRequired &&
            (hasFailureCode || reasonGroup == RecoveryReasonGroup.DefiniteProblem);
        var providerAcceptedOrQueued = provider?.Status is AzureProviderOperationStatus.Accepted
            or AzureProviderOperationStatus.Queued;
        Guid? blockingOperationId = null;
        string? blockingOperationStage = null;
        string? staleReason = null;
        string? waitingStage = null;
        var inheritedBlockerStale = false;
        if (lifecycleState == ElsaInstanceOperationState.WaitingForPriorOperation &&
            input.ResolveBlockingOperation)
        {
            if (!TryResolveBlockingOperation(input, lifecycle, out var blocker))
            {
                inheritedBlockerStale = true;
                staleReason = ManagedElsaProvisioningProgressStaleReasons.BlockingOperationUnresolvable;
            }
            else
            {
                var blockerProgress = Project(
                    input with
                    {
                        LifecycleOperation = blocker,
                        ProviderOperation = null,
                        Transitions = null,
                        RecoveryReason = blocker.RecoveryReason,
                        ResolveBlockingOperation = false
                    },
                    clock);
                blockingOperationId = blocker.Id;
                blockingOperationStage = blockerProgress.CurrentStage;
                waitingStage = WaitingStage(blocker.Action);
                if (blockerProgress.State == ManagedElsaProvisioningProgressStates.Stale)
                {
                    inheritedBlockerStale = true;
                    staleReason = ManagedElsaProvisioningProgressStaleReasons.BlockingOperationStale;
                }
            }
        }

        var providerProgressStale = !inheritedBlockerStale &&
            !lifecycleTerminal &&
            HasExceededProviderProgressBound(now, lifecycle, provider);

        // Lifecycle terminal state is authoritative whenever it is available.
        if (lifecycleState is ElsaInstanceOperationState.Failed or ElsaInstanceOperationState.Cancelled)
        {
            state = ManagedElsaProvisioningProgressStates.Failed;
            diagnosticCode = lifecycleState == ElsaInstanceOperationState.Cancelled
                ? ManagedElsaProvisioningProgressDiagnostics.Cancelled
                : ManagedElsaProvisioningProgressDiagnostics.Failed;
            blocked = true;
        }
        else if (lifecycleState == ElsaInstanceOperationState.Succeeded && observedReady)
        {
            // Create has finished. The 10-minute provider-progress bound does not apply.
            state = ManagedElsaProvisioningProgressStates.Ready;
            allReady = true;
            completedAt ??= provider?.CompletedAt?.ToUniversalTime();
        }
        else if (!lifecycleTerminal && provider?.Status == AzureProviderOperationStatus.Succeeded && observedReady)
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
        else if ((!lifecycleTerminal && provider?.Status == AzureProviderOperationStatus.RecoveryRequired) ||
                 definiteProblem ||
                 providerProgressStale ||
                 inheritedBlockerStale)
        {
            // Immediate stale: provider RecoveryRequired, any FailureCode, uncertain /
            // ambiguous / correlation-mismatch / retry-safe / unrecognised reasons,
            // an unresolvable or stale blocker, or the inclusive 10-minute bound.
            // The bound uses StatusChangedAt (or lifecycle AcceptedAt when no
            // provider row exists). Heartbeats, UpdatedAt, and reason-write time
            // do not reset it. Skipped while Running, WaitingForPriorOperation,
            // and EntitlementHeld. Applies to any Accepted/Queued operation kind.
            state = ManagedElsaProvisioningProgressStates.Stale;
            diagnosticCode = ManagedElsaProvisioningProgressDiagnostics.RequiresAttention;
            blocked = true;
        }
        else if (lifecycle is null && provider is null)
        {
            return Unavailable();
        }
        else if (lifecycleState == ElsaInstanceOperationState.WaitingForPriorOperation)
        {
            state = ManagedElsaProvisioningProgressStates.WaitingForPriorOperation;
            waitingStage ??= WaitingStage(null);
            knownStage ??= 0;
        }
        else if (lifecycleState == ElsaInstanceOperationState.EntitlementHeld)
        {
            state = ManagedElsaProvisioningProgressStates.EntitlementHeld;
            knownStage ??= 0;
        }
        else if (provider is null && (healthyContinuation || transientUncertainty ||
                 lifecycleState is ElsaInstanceOperationState.Accepted or
                 ElsaInstanceOperationState.Queued))
        {
            state = healthyContinuation ||
                    lifecycleState is ElsaInstanceOperationState.Accepted or
                    ElsaInstanceOperationState.Queued
                ? ManagedElsaProvisioningProgressStates.Queued
                : ManagedElsaProvisioningProgressStates.Active;
            knownStage ??= 0;
            if (healthyContinuation &&
                string.Equals(input.RecoveryReason, ProviderReconciliationHealthUnknown, StringComparison.Ordinal))
            {
                state = ManagedElsaProvisioningProgressStates.Active;
                knownStage = Max(knownStage, StageIndex(ManagedElsaProvisioningProgressStages.HealthVerification));
            }
        }
        else if (provider is not null)
        {
            state = ManagedElsaProvisioningProgressStates.Active;
            if (healthyContinuation && providerAcceptedOrQueued)
                knownStage = Max(knownStage, StageIndex(ManagedElsaProvisioningProgressStages.RequestAccepted));
            if (healthyContinuation &&
                (string.Equals(input.RecoveryReason, ProviderReconciliationHealthUnknown, StringComparison.Ordinal) ||
                 (provider.Status == AzureProviderOperationStatus.Succeeded && !observedReady)))
            {
                // Advance to health-verification when that is the current step, but
                // never move the stage backwards (e.g. TrafficPromoted already seen).
                knownStage = Max(knownStage, StageIndex(ManagedElsaProvisioningProgressStages.HealthVerification));
            }
        }
        else
        {
            return Unavailable(startedAt, lastUpdatedAt, lifecycle?.CompletedAt, lifecycle);
        }

        if (state == ManagedElsaProvisioningProgressStates.Ready)
        {
            knownStage = ManagedElsaProvisioningProgressStages.Ordered.Count - 1;
        }
        else if (state == ManagedElsaProvisioningProgressStates.Stale)
        {
            knownStage ??= 0;
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
                (completedAt ?? lastUpdatedAt ?? startedAt ?? now).ToUniversalTime()));
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
                (completedAt ?? lastUpdatedAt ?? startedAt ?? now).ToUniversalTime()));
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
        if (state == ManagedElsaProvisioningProgressStates.WaitingForPriorOperation)
            currentStage = waitingStage ?? ManagedElsaProvisioningProgressStages.WaitingForPriorOperation;
        else if (state == ManagedElsaProvisioningProgressStates.EntitlementHeld)
            currentStage = ManagedElsaProvisioningProgressStages.EntitlementHeld;
        else if (inheritedBlockerStale && blockingOperationStage is not null)
            currentStage = blockingOperationStage;
        return new ManagedElsaProvisioningProgress(
            state,
            Provider,
            currentStage,
            startedAt,
            lastUpdatedAt,
            completedAt,
            diagnosticCode,
            stages,
            activity,
            blockingOperationId,
            blockingOperationStage,
            staleReason);
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

    private static RecoveryReasonGroup ClassifyRecoveryReason(string? recoveryReason)
    {
        if (string.Equals(recoveryReason, ProviderSubmissionAccepted, StringComparison.Ordinal) ||
            string.Equals(recoveryReason, ProviderReconciliationInProgress, StringComparison.Ordinal) ||
            string.Equals(recoveryReason, ProviderReconciliationHealthUnknown, StringComparison.Ordinal))
            return RecoveryReasonGroup.HealthyContinuation;

        if (string.Equals(recoveryReason, ProviderReconciliationUnavailable, StringComparison.Ordinal) ||
            string.Equals(recoveryReason, ProviderReconciliationUnknown, StringComparison.Ordinal))
            return RecoveryReasonGroup.TransientUncertainty;

        // Uncertain, ambiguous, correlation-mismatch, retry-safe, null, and any
        // unrecognised reason fail closed to a definite problem.
        return RecoveryReasonGroup.DefiniteProblem;
    }

    /// <summary>
    /// The 10-minute clock starts from the provider operation's last status
    /// change (<see cref="AzureProviderOperation.StatusChangedAt"/>, backfilled
    /// from <see cref="AzureProviderOperation.UpdatedAt"/> so an in-flight
    /// Succeeded-before-Ready row does not inherit a CreatedAt older than 10
    /// minutes). Heartbeats, run <c>UpdatedAt</c>, reason-write time, and later
    /// provider <c>UpdatedAt</c> writes do not reset it. With no provider row,
    /// lifecycle <c>AcceptedAt</c> is used and the clock applies only to
    /// Accepted/Queued (any operation kind, including Delete) and the
    /// RecoveryRequired hand-off. Lifecycle <c>WaitingForPriorOperation</c> and
    /// <c>EntitlementHeld</c> are exempt and have no clock of their own. The
    /// clock is skipped while the provider is <c>Running</c> and once Create has
    /// finished. Inclusive: elapsed == 10:00 is stale. It covers Accepted,
    /// Queued, and Succeeded-before-Ready.
    /// </summary>
    private static bool HasExceededProviderProgressBound(
        DateTimeOffset now,
        ElsaInstanceLifecycleTopologyOperation? lifecycle,
        AzureProviderOperation? provider)
    {
        if (lifecycle?.State is ElsaInstanceOperationState.WaitingForPriorOperation
            or ElsaInstanceOperationState.EntitlementHeld)
            return false;
        if (provider?.Status == AzureProviderOperationStatus.Running)
            return false;
        if (provider?.Status is AzureProviderOperationStatus.Failed or
            AzureProviderOperationStatus.Cancelled or
            AzureProviderOperationStatus.RecoveryRequired or
            AzureProviderOperationStatus.EntitlementHeld)
            return false;
        if (provider is not null &&
            provider.Status is not (AzureProviderOperationStatus.Accepted or
                AzureProviderOperationStatus.Queued or
                AzureProviderOperationStatus.Succeeded))
            return false;
        if (provider is null &&
            lifecycle?.State is not (ElsaInstanceOperationState.Accepted or
                ElsaInstanceOperationState.Queued or
                ElsaInstanceOperationState.RecoveryRequired))
            return false;

        var clockStart = provider is not null
            ? (provider.StatusChangedAt ?? provider.CreatedAt)
            : lifecycle?.AcceptedAt;
        return clockStart is { } origin &&
               now - origin.ToUniversalTime() >= ProviderProgressStaleAfter;
    }

    private static bool TryResolveBlockingOperation(
        AzureManagedElsaProvisioningProgressProjectionInput input,
        ElsaInstanceLifecycleTopologyOperation? waiting,
        out ElsaInstanceLifecycleTopologyOperation blocker)
    {
        blocker = null!;
        if (waiting is null ||
            waiting.BlockingOperationId is not { } blockerId ||
            blockerId == Guid.Empty ||
            blockerId == waiting.Id)
            return false;

        var candidate = input.Topology?.Operations
            .FirstOrDefault(operation => operation.Id == blockerId);
        if (candidate is null)
            return false;
        if (waiting.OrganizationId != Guid.Empty &&
            candidate.OrganizationId != waiting.OrganizationId)
            return false;

        blocker = candidate;
        return true;
    }

    private static string WaitingStage(ElsaInstanceOperationAction? blockerAction) =>
        blockerAction switch
        {
            ElsaInstanceOperationAction.Delete => ManagedElsaProvisioningProgressStages.WaitingForDelete,
            ElsaInstanceOperationAction.Create => ManagedElsaProvisioningProgressStages.WaitingForPriorOperation,
            null => ManagedElsaProvisioningProgressStages.WaitingForPriorOperation,
            _ => ManagedElsaProvisioningProgressStages.WaitingForUpdate
        };

    private enum RecoveryReasonGroup
    {
        HealthyContinuation,
        TransientUncertainty,
        DefiniteProblem
    }

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
