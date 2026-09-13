using System.Diagnostics;
using System.Diagnostics.Metrics;
using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Core.Instances;
using ElsaControl.Deployment.Core.Telemetry;
using ElsaControl.RuntimeBuilder.Abstractions.Plans;
using Xunit;

namespace ElsaControl.Deployment.Core.Tests;

[CollectionDefinition("Managed lifecycle telemetry", DisableParallelization = true)]
public sealed class ManagedLifecycleTelemetryTestCollection
{
    public const string CollectionName = "Managed lifecycle telemetry";
}

[Collection(ManagedLifecycleTelemetryTestCollection.CollectionName)]
public sealed class ManagedLifecycleTelemetryTests
{
    [Fact]
    public void Completion_emits_activity_and_counter_with_only_bounded_dimensions()
    {
        using var capture = new TelemetryCapture();
        using var operation = ManagedLifecycleTelemetry.StartOperation(
            ManagedLifecycleTelemetry.WorkerActivityName,
            ElsaInstanceOperationAction.Start,
            ElsaDesiredLifecycle.Running,
            ElsaObservedLifecycle.Provisioning,
            ElsaInstanceHealth.Unknown,
            ElsaInstanceOperationState.Accepted);

        operation.Complete(
            outcome: "succeeded",
            desiredLifecycle: ElsaDesiredLifecycle.Running,
            observedLifecycle: ElsaObservedLifecycle.Ready,
            health: ElsaInstanceHealth.Healthy,
            operationState: ElsaInstanceOperationState.Succeeded,
            diagnosticCode: "provider failed at https://provider.example.test/secret?token=redacted");
        operation.Dispose();

        var activity = Assert.Single(capture.Activities, activity =>
            activity.OperationName == ManagedLifecycleTelemetry.WorkerActivityName &&
            activity.GetTagItem(ManagedLifecycleTelemetry.ActionTag)?.ToString() == "start" &&
            activity.GetTagItem(ManagedLifecycleTelemetry.OutcomeTag)?.ToString() == "succeeded" &&
            activity.GetTagItem(ManagedLifecycleTelemetry.DiagnosticCodeTag)?.ToString() == "unknown");
        Assert.Equal(ManagedLifecycleTelemetry.WorkerActivityName, activity.OperationName);
        Assert.Equal("start", activity.GetTagItem(ManagedLifecycleTelemetry.ActionTag));
        Assert.Equal("succeeded", activity.GetTagItem(ManagedLifecycleTelemetry.OutcomeTag));
        Assert.Equal("running", activity.GetTagItem(ManagedLifecycleTelemetry.DesiredLifecycleTag));
        Assert.Equal("ready", activity.GetTagItem(ManagedLifecycleTelemetry.ObservedLifecycleTag));
        Assert.Equal("healthy", activity.GetTagItem(ManagedLifecycleTelemetry.HealthTag));
        Assert.Equal("succeeded", activity.GetTagItem(ManagedLifecycleTelemetry.OperationStateTag));
        Assert.Equal("unknown", activity.GetTagItem(ManagedLifecycleTelemetry.DiagnosticCodeTag));
        Assert.DoesNotContain(activity.Tags, tag =>
            tag.Key.Contains("id", StringComparison.OrdinalIgnoreCase) ||
            tag.Key.Contains("endpoint", StringComparison.OrdinalIgnoreCase) ||
            tag.Key.Contains("provider", StringComparison.OrdinalIgnoreCase) ||
            tag.Key.Contains("resource", StringComparison.OrdinalIgnoreCase) ||
            tag.Key.Contains("message", StringComparison.OrdinalIgnoreCase));

        var measurement = Assert.Single(capture.Measurements, x =>
            x.InstrumentName == ManagedLifecycleTelemetry.CompletionCounterName &&
            x.Tags.Any(tag => tag.Key == ManagedLifecycleTelemetry.ActionTag && tag.Value?.ToString() == "start") &&
            x.Tags.Any(tag => tag.Key == ManagedLifecycleTelemetry.OutcomeTag && tag.Value?.ToString() == "succeeded"));
        Assert.Equal(1, measurement.Value);
        Assert.DoesNotContain(measurement.Tags, tag => tag.Key == ManagedLifecycleTelemetry.DiagnosticCodeTag);
        Assert.Equal(
            new[]
            {
                ManagedLifecycleTelemetry.ActionTag,
                ManagedLifecycleTelemetry.OutcomeTag,
                ManagedLifecycleTelemetry.DesiredLifecycleTag,
                ManagedLifecycleTelemetry.ObservedLifecycleTag,
                ManagedLifecycleTelemetry.HealthTag,
                ManagedLifecycleTelemetry.OperationStateTag
            },
            measurement.Tags.Select(x => x.Key).ToArray());
        Assert.DoesNotContain(measurement.Tags, tag =>
            tag.Value?.ToString()?.Contains("https://", StringComparison.OrdinalIgnoreCase) == true ||
            tag.Value?.ToString()?.Contains("token", StringComparison.OrdinalIgnoreCase) == true ||
            tag.Value?.ToString()?.Contains("secret", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public void Metric_dimensions_map_undefined_enum_values_to_unknown()
    {
        using var capture = new TelemetryCapture();
        using var operation = ManagedLifecycleTelemetry.StartOperation(
            ManagedLifecycleTelemetry.WorkerActivityName,
            (ElsaInstanceOperationAction)999,
            (ElsaDesiredLifecycle)999,
            (ElsaObservedLifecycle)999,
            (ElsaInstanceHealth)999,
            (ElsaInstanceOperationState)999);

        operation.Complete(
            outcome: "succeeded",
            desiredLifecycle: (ElsaDesiredLifecycle)999,
            observedLifecycle: (ElsaObservedLifecycle)999,
            health: (ElsaInstanceHealth)999,
            operationState: (ElsaInstanceOperationState)999);

        var measurement = Assert.Single(capture.Measurements, x =>
            x.InstrumentName == ManagedLifecycleTelemetry.CompletionCounterName);
        Assert.Equal(
            new Dictionary<string, string>
            {
                [ManagedLifecycleTelemetry.ActionTag] = "unknown",
                [ManagedLifecycleTelemetry.OutcomeTag] = "succeeded",
                [ManagedLifecycleTelemetry.DesiredLifecycleTag] = "unknown",
                [ManagedLifecycleTelemetry.ObservedLifecycleTag] = "unknown",
                [ManagedLifecycleTelemetry.HealthTag] = "unknown",
                [ManagedLifecycleTelemetry.OperationStateTag] = "unknown"
            },
            measurement.Tags.ToDictionary(x => x.Key, x => x.Value?.ToString() ?? string.Empty));
        Assert.DoesNotContain(measurement.Tags, tag => tag.Key == ManagedLifecycleTelemetry.DiagnosticCodeTag);
    }

    [Fact]
    public void All_metric_instruments_use_the_exact_bounded_dimension_set()
    {
        using var capture = new TelemetryCapture();
        using var operation = ManagedLifecycleTelemetry.StartOperation(
            ManagedLifecycleTelemetry.ReconciliationActivityName,
            ElsaInstanceOperationAction.Reconcile,
            ElsaDesiredLifecycle.Running,
            ElsaObservedLifecycle.Unknown,
            ElsaInstanceHealth.Unknown,
            ElsaInstanceOperationState.Queued,
            attemptNumber: 2);

        operation.RecordError(
            ElsaDesiredLifecycle.Running,
            ElsaObservedLifecycle.Unknown,
            ElsaInstanceHealth.Unknown,
            ElsaInstanceOperationState.Queued,
            "provider.reconciliation.failed");
        operation.RecordTransition(
            ElsaDesiredLifecycle.Running,
            ElsaObservedLifecycle.Ready,
            ElsaInstanceHealth.Healthy,
            ElsaInstanceOperationState.Succeeded,
            "provider.reconciliation.converged");
        operation.Complete(
            "converged",
            ElsaDesiredLifecycle.Running,
            ElsaObservedLifecycle.Ready,
            ElsaInstanceHealth.Healthy,
            ElsaInstanceOperationState.Succeeded,
            "provider.reconciliation.converged");

        var measurements = capture.Measurements
            .Where(measurement => IsMetricInstrument(measurement.InstrumentName))
            .ToArray();
        Assert.Equal(
            new[]
            {
                ManagedLifecycleTelemetry.CompletionCounterName,
                ManagedLifecycleTelemetry.ErrorCounterName,
                ManagedLifecycleTelemetry.TransitionCounterName,
                ManagedLifecycleTelemetry.RetryCounterName,
                ManagedLifecycleTelemetry.DurationHistogramName,
                ManagedLifecycleTelemetry.EndpointHealthCounterName
            }.Order(),
            measurements.Select(measurement => measurement.InstrumentName).Distinct().Order());
        Assert.All(measurements, AssertExactMetricDimensions);
    }

    [Fact]
    public void Dispose_records_only_duration_with_metric_dimensions()
    {
        using var capture = new TelemetryCapture();
        using (ManagedLifecycleTelemetry.StartOperation(
                   ManagedLifecycleTelemetry.WorkerActivityName,
                   ElsaInstanceOperationAction.Start,
                   ElsaDesiredLifecycle.Running,
                   ElsaObservedLifecycle.Provisioning,
                   ElsaInstanceHealth.Unknown,
                   ElsaInstanceOperationState.Accepted))
        {
        }

        var measurement = Assert.Single(capture.Measurements);
        Assert.Equal(ManagedLifecycleTelemetry.DurationHistogramName, measurement.InstrumentName);
        AssertExactMetricDimensions(measurement);
    }

    [Fact]
    public void Error_and_transition_emit_bounded_counters_without_exception_details()
    {
        using var capture = new TelemetryCapture();
        using var operation = ManagedLifecycleTelemetry.StartOperation(
            ManagedLifecycleTelemetry.ReconciliationActivityName,
            ElsaInstanceOperationAction.Reconcile,
            ElsaDesiredLifecycle.Running,
            ElsaObservedLifecycle.Unknown,
            ElsaInstanceHealth.Unknown,
            ElsaInstanceOperationState.Queued);

        operation.RecordError(
            ElsaDesiredLifecycle.Running,
            ElsaObservedLifecycle.Unknown,
            ElsaInstanceHealth.Unknown,
            ElsaInstanceOperationState.Queued,
            "provider error: operation 01020304 at https://provider.example.test");
        operation.RecordTransition(
            ElsaDesiredLifecycle.Running,
            ElsaObservedLifecycle.Ready,
            ElsaInstanceHealth.Healthy,
            ElsaInstanceOperationState.Succeeded,
            "provider.reconciliation.converged");

        Assert.Contains(capture.Measurements, measurement =>
            measurement.InstrumentName == ManagedLifecycleTelemetry.ErrorCounterName && measurement.Value == 1);
        Assert.Contains(capture.Measurements, measurement =>
            measurement.InstrumentName == ManagedLifecycleTelemetry.TransitionCounterName && measurement.Value == 1);
        Assert.DoesNotContain(capture.Measurements.SelectMany(x => x.Tags), tag =>
            tag.Value?.ToString()?.Contains("01020304", StringComparison.OrdinalIgnoreCase) == true ||
            tag.Value?.ToString()?.Contains("https://", StringComparison.OrdinalIgnoreCase) == true ||
            tag.Value?.ToString()?.Contains("operation", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public async Task Reconciliation_service_records_its_completion_and_state_transition()
    {
        using var capture = new TelemetryCapture();
        var workspaceId = Guid.Parse("10000000-0000-0000-0000-000000000001");
        var instanceId = Guid.Parse("20000000-0000-0000-0000-000000000001");
        var operationId = Guid.Parse("30000000-0000-0000-0000-000000000001");
        var instance = TestInstance(instanceId, workspaceId);
        var operation = ElsaInstanceOperation.Hydrate(
            operationId,
            instanceId,
            ElsaInstanceOperationAction.Reconcile,
            "instances",
            "reconcile-telemetry",
            new string('a', 64),
            1,
            ElsaInstanceOperationState.RecoveryRequired,
            1,
            DateTimeOffset.UtcNow);
        var target = new ElsaInstanceProviderReconciliationTarget(instance, operation, 0);
        var service = new ElsaInstanceProviderReconciliationService(
            new RecordingReconciliationStore(target),
            new ConfirmingProvider());

        var result = await service.ReconcileAsync(workspaceId, operationId);

        Assert.Equal(ElsaInstanceProviderReconciliationOutcome.Converged, result.Outcome);
        Assert.Contains(capture.Activities, activity =>
            activity.OperationName == ManagedLifecycleTelemetry.ReconciliationActivityName &&
            activity.GetTagItem(ManagedLifecycleTelemetry.ActionTag)?.ToString() == "reconcile" &&
            activity.GetTagItem(ManagedLifecycleTelemetry.OutcomeTag)?.ToString() == "converged" &&
            activity.GetTagItem(ManagedLifecycleTelemetry.DesiredLifecycleTag)?.ToString() == "running" &&
            activity.GetTagItem(ManagedLifecycleTelemetry.ObservedLifecycleTag)?.ToString() == "ready" &&
            activity.GetTagItem(ManagedLifecycleTelemetry.HealthTag)?.ToString() == "healthy" &&
            activity.GetTagItem(ManagedLifecycleTelemetry.OperationStateTag)?.ToString() == "succeeded" &&
            activity.GetTagItem(ManagedLifecycleTelemetry.DiagnosticCodeTag)?.ToString() ==
            "provider.reconciliation.converged");
        Assert.Contains(capture.Measurements, measurement =>
            measurement.InstrumentName == ManagedLifecycleTelemetry.CompletionCounterName &&
            measurement.Tags.All(tag => AllowedTagKeys.Contains(tag.Key)));
        Assert.Contains(capture.Measurements, measurement =>
            measurement.InstrumentName == ManagedLifecycleTelemetry.TransitionCounterName &&
            measurement.Tags.All(tag => AllowedTagKeys.Contains(tag.Key)));
        Assert.Contains(capture.Measurements, measurement =>
            measurement.InstrumentName == ManagedLifecycleTelemetry.DurationHistogramName &&
            measurement.Value >= 0 &&
            measurement.Tags.All(tag => AllowedTagKeys.Contains(tag.Key)));
        Assert.Contains(capture.Measurements, measurement =>
            measurement.InstrumentName == ManagedLifecycleTelemetry.EndpointHealthCounterName &&
            measurement.Tags.Any(tag =>
                tag.Key == ManagedLifecycleTelemetry.HealthTag && tag.Value?.ToString() == "healthy") &&
            measurement.Tags.All(tag => AllowedTagKeys.Contains(tag.Key)));
    }

    [Fact]
    public async Task Replayed_reconciliation_is_traceable_without_recounting_completed_work()
    {
        using var capture = new TelemetryCapture();
        var workspaceId = Guid.Parse("10000000-0000-0000-0000-000000000003");
        var instanceId = Guid.Parse("20000000-0000-0000-0000-000000000003");
        var operationId = Guid.Parse("30000000-0000-0000-0000-000000000003");
        var replay = new ElsaInstanceProviderReconciliationResult(
            ElsaInstanceProviderReconciliationOutcome.Converged,
            new(
                workspaceId,
                instanceId,
                operationId,
                1,
                ElsaObservedLifecycle.Ready,
                ElsaInstanceHealth.Healthy,
                2,
                ElsaInstanceOperationState.Succeeded),
            ElsaInstanceProviderReconciliationService.ConvergedCode,
            false,
            false,
            DateTimeOffset.UtcNow);
        var service = new ElsaInstanceProviderReconciliationService(
            new ReplayReconciliationStore(replay),
            new ConfirmingProvider());

        var result = await service.ReconcileAsync(workspaceId, operationId);

        Assert.True(result.Replayed);
        Assert.Contains(capture.Activities, activity =>
            activity.OperationName == ManagedLifecycleTelemetry.ReconciliationActivityName &&
            activity.GetTagItem(ManagedLifecycleTelemetry.OutcomeTag)?.ToString() == "already_completed" &&
            activity.GetTagItem(ManagedLifecycleTelemetry.WorkspaceIdTag)?.ToString() == workspaceId.ToString("D") &&
            activity.GetTagItem(ManagedLifecycleTelemetry.InstanceIdTag)?.ToString() == instanceId.ToString("D") &&
            activity.GetTagItem(ManagedLifecycleTelemetry.OperationIdTag)?.ToString() == operationId.ToString("D"));
        Assert.DoesNotContain(capture.Measurements, measurement =>
            measurement.InstrumentName is ManagedLifecycleTelemetry.CompletionCounterName or
                ManagedLifecycleTelemetry.EndpointHealthCounterName or
                ManagedLifecycleTelemetry.DurationHistogramName);
    }

    [Fact]
    public async Task Lifecycle_worker_records_failed_completion_without_leaking_work_item_details()
    {
        using var capture = new TelemetryCapture();
        var workspaceId = Guid.Parse("10000000-0000-0000-0000-000000000002");
        var instanceId = Guid.Parse("20000000-0000-0000-0000-000000000002");
        var operationId = Guid.Parse("30000000-0000-0000-0000-000000000002");
        var instance = TestInstance(instanceId, workspaceId);
        var operation = ElsaInstanceOperation.Create(
            instanceId,
            ElsaInstanceOperationAction.Start,
            "instances",
            "worker-telemetry",
            new string('a', 64),
            1,
            operationId,
            DateTimeOffset.UtcNow);
        var item = new ElsaInstanceLifecycleWorkItem(
            new(
                Guid.Parse("40000000-0000-0000-0000-000000000002"),
                workspaceId,
                instanceId,
                operationId,
                operation.Action,
                operation.RequestHash,
                DateTimeOffset.UtcNow),
            operation,
            instance,
            null!,
            new string('b', 64),
            1);
        var worker = new ElsaInstanceLifecycleWorker(new FailedWorkerStore(item), new NeverResolver());

        var result = await worker.ProcessAvailableAsync("worker-telemetry");

        Assert.Equal(ElsaInstanceLifecycleWorkerOutcome.Failed, Assert.Single(result.Results).Outcome);
        Assert.Contains(capture.Activities, activity =>
            activity.OperationName == ManagedLifecycleTelemetry.WorkerActivityName &&
            activity.GetTagItem(ManagedLifecycleTelemetry.ActionTag)?.ToString() == "start" &&
            activity.GetTagItem(ManagedLifecycleTelemetry.OutcomeTag)?.ToString() == "failed" &&
            activity.GetTagItem(ManagedLifecycleTelemetry.DiagnosticCodeTag)?.ToString() == "resolution.input-unavailable" &&
            activity.GetTagItem(ManagedLifecycleTelemetry.OrganizationIdTag)?.ToString() ==
            item.Instance.OrganizationId.ToString("D") &&
            activity.GetTagItem(ManagedLifecycleTelemetry.WorkspaceIdTag)?.ToString() == workspaceId.ToString("D") &&
            activity.GetTagItem(ManagedLifecycleTelemetry.InstanceIdTag)?.ToString() == instanceId.ToString("D") &&
            activity.GetTagItem(ManagedLifecycleTelemetry.OperationIdTag)?.ToString() == operationId.ToString("D") &&
            activity.Tags.All(tag => AllowedActivityTagKeys.Contains(tag.Key)));
        Assert.Contains(capture.Measurements, measurement =>
            measurement.InstrumentName == ManagedLifecycleTelemetry.ErrorCounterName &&
            measurement.Tags.All(tag => AllowedTagKeys.Contains(tag.Key)));
        Assert.DoesNotContain(capture.Measurements.SelectMany(measurement => measurement.Tags), tag =>
            tag.Key is ManagedLifecycleTelemetry.OrganizationIdTag or
                ManagedLifecycleTelemetry.WorkspaceIdTag or
                ManagedLifecycleTelemetry.InstanceIdTag or
                ManagedLifecycleTelemetry.OperationIdTag);
    }

    [Fact]
    public void Retry_metric_is_low_cardinality_and_contains_no_correlation_identifiers()
    {
        using var capture = new TelemetryCapture();
        using var operation = ManagedLifecycleTelemetry.StartOperation(
            ManagedLifecycleTelemetry.WorkerActivityName,
            ElsaInstanceOperationAction.Retry,
            ElsaDesiredLifecycle.Running,
            ElsaObservedLifecycle.Unknown,
            ElsaInstanceHealth.Unknown,
            ElsaInstanceOperationState.Queued,
            organizationId: Guid.NewGuid(),
            workspaceId: Guid.NewGuid(),
            instanceId: Guid.NewGuid(),
            operationId: Guid.NewGuid(),
            attemptNumber: 2);

        var retry = Assert.Single(capture.Measurements, measurement =>
            measurement.InstrumentName == ManagedLifecycleTelemetry.RetryCounterName);
        Assert.Equal(1, retry.Value);
        Assert.All(retry.Tags, tag => Assert.Contains(tag.Key, AllowedTagKeys));
    }

    private static readonly string[] ExpectedMetricTagKeys =
    [
        ManagedLifecycleTelemetry.ActionTag,
        ManagedLifecycleTelemetry.OutcomeTag,
        ManagedLifecycleTelemetry.DesiredLifecycleTag,
        ManagedLifecycleTelemetry.ObservedLifecycleTag,
        ManagedLifecycleTelemetry.HealthTag,
        ManagedLifecycleTelemetry.OperationStateTag
    ];

    private static readonly HashSet<string> AllowedTagKeys =
    [
        .. ExpectedMetricTagKeys
    ];

    private static readonly HashSet<string> AllowedActivityTagKeys =
    [
        .. AllowedTagKeys,
        ManagedLifecycleTelemetry.DiagnosticCodeTag,
        ManagedLifecycleTelemetry.OrganizationIdTag,
        ManagedLifecycleTelemetry.WorkspaceIdTag,
        ManagedLifecycleTelemetry.InstanceIdTag,
        ManagedLifecycleTelemetry.OperationIdTag
    ];

    private static bool IsMetricInstrument(string instrumentName) => instrumentName is
        ManagedLifecycleTelemetry.CompletionCounterName or
        ManagedLifecycleTelemetry.ErrorCounterName or
        ManagedLifecycleTelemetry.TransitionCounterName or
        ManagedLifecycleTelemetry.RetryCounterName or
        ManagedLifecycleTelemetry.DurationHistogramName or
        ManagedLifecycleTelemetry.EndpointHealthCounterName;

    private static void AssertExactMetricDimensions(Measurement measurement)
    {
        Assert.Equal(ExpectedMetricTagKeys, measurement.Tags.Select(tag => tag.Key).ToArray());
        Assert.DoesNotContain(measurement.Tags, tag => tag.Key == ManagedLifecycleTelemetry.DiagnosticCodeTag);
    }

    private static ElsaInstance TestInstance(Guid instanceId, Guid workspaceId) => ElsaInstance.Hydrate(
        instanceId,
        Guid.Parse("40000000-0000-0000-0000-000000000001"),
        workspaceId,
        "telemetry-test",
        "telemetry-test",
        new(
            new("commercial", "5.0", "5.0.0-preview.1", "preview"),
            new("server-studio"),
            new("managed", "westeurope", "dedicated", "standard-small", "public", "managed")),
        ElsaObservedLifecycle.Unknown,
        ElsaInstanceHealth.Unknown,
        1);

    private sealed class RecordingReconciliationStore(ElsaInstanceProviderReconciliationTarget target)
        : IElsaInstanceProviderReconciliationStore
    {
        public Task<ElsaInstanceProviderReconciliationTarget?> GetTargetAsync(
            Guid workspaceId,
            Guid operationId,
            CancellationToken cancellationToken = default) => Task.FromResult<ElsaInstanceProviderReconciliationTarget?>(target);

        public Task<ElsaInstanceProviderReconciliationResult?> GetResultAsync(
            Guid workspaceId,
            Guid operationId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ElsaInstanceProviderReconciliationResult?>(null);

        public Task<ElsaInstanceProviderReconciliationResult> CommitAsync(
            ElsaInstanceProviderReconciliationCommit commit,
            CancellationToken cancellationToken = default) => Task.FromResult(
            new ElsaInstanceProviderReconciliationResult(
                ElsaInstanceProviderReconciliationOutcome.Converged,
                new(
                    commit.WorkspaceId,
                    commit.InstanceId,
                    commit.OperationId,
                    commit.ExpectedAttemptNumber,
                    commit.Instance.ObservedLifecycle,
                    commit.Instance.Health,
                    commit.Instance.Version,
                    commit.Operation.State),
                commit.DiagnosticCode,
                commit.RetrySafe,
                false,
                commit.ReconciledAt));
    }

    private sealed class ConfirmingProvider : IElsaInstanceProviderReconciliationPort
    {
        public Task<ElsaInstanceProviderObservation> ObserveAsync(
            ElsaInstanceProviderReconciliationRequest request,
            CancellationToken cancellationToken = default) => Task.FromResult(
            new ElsaInstanceProviderObservation(
                ElsaInstanceProviderObservationKind.Confirmed,
                ElsaObservedLifecycle.Ready,
                ElsaInstanceProviderHealthGate.Passed,
                "telemetry-observation").Correlate(request));
    }

    private sealed class ReplayReconciliationStore(ElsaInstanceProviderReconciliationResult replay)
        : IElsaInstanceProviderReconciliationStore
    {
        public Task<ElsaInstanceProviderReconciliationTarget?> GetTargetAsync(
            Guid workspaceId,
            Guid operationId,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A replay must not load a reconciliation target.");

        public Task<ElsaInstanceProviderReconciliationResult?> GetResultAsync(
            Guid workspaceId,
            Guid operationId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ElsaInstanceProviderReconciliationResult?>(replay);

        public Task<ElsaInstanceProviderReconciliationResult> CommitAsync(
            ElsaInstanceProviderReconciliationCommit commit,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A replay must not commit reconciliation state.");
    }

    private sealed class FailedWorkerStore(ElsaInstanceLifecycleWorkItem item) : IElsaInstanceLifecycleWorkerStore
    {
        private int _claimsRemaining = 1;

        public Task<ElsaInstanceLifecycleWorkItem?> TryClaimNextAsync(
            string workerId,
            DateTimeOffset now,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Interlocked.Exchange(ref _claimsRemaining, 0) == 1 ? item : null);

        public Task<ElsaInstanceLifecycleWorkerResult> CommitResolvedAsync(
            ElsaInstanceLifecycleResolutionCommit commit,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The malformed work item must fail before resolution.");

        public Task<ElsaInstanceLifecycleWorkerResult> FailResolutionAsync(
            ElsaInstanceLifecycleResolutionFailure failure,
            CancellationToken cancellationToken = default) => Task.FromResult(
            new ElsaInstanceLifecycleWorkerResult(
                ElsaInstanceLifecycleWorkerOutcome.Failed,
                item.Operation.TransitionTo(ElsaInstanceOperationState.Failed),
                item.Instance,
                FailureCode: failure.Code,
                FailureSummary: failure.Summary));
    }

    private sealed class NeverResolver : IElsaInstancePlanResolver
    {
        public Task<ElsaInstancePlanResolutionResult> ResolveAsync(
            ElsaInstancePlanResolutionRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The malformed work item must fail before resolution.");
    }

    private sealed class TelemetryCapture : IDisposable
    {
        private readonly ActivityListener _activityListener;
        private readonly MeterListener _meterListener;
        private readonly object _gate = new();
        private readonly List<Activity> _activities = [];
        private readonly List<Measurement> _measurements = [];

        public TelemetryCapture()
        {
            _activityListener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == ManagedLifecycleTelemetry.ActivitySourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStopped = activity =>
                {
                    lock (_gate)
                        _activities.Add(activity);
                }
            };
            ActivitySource.AddActivityListener(_activityListener);

            _meterListener = new MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (instrument.Meter.Name == ManagedLifecycleTelemetry.MeterName)
                        listener.EnableMeasurementEvents(instrument);
                }
            };
            _meterListener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            {
                lock (_gate)
                    _measurements.Add(new(instrument.Name, value, tags.ToArray()));
            });
            _meterListener.Start();
        }

        public IReadOnlyList<Activity> Activities
        {
            get
            {
                lock (_gate)
                    return _activities.ToArray();
            }
        }

        public IReadOnlyList<Measurement> Measurements
        {
            get
            {
                lock (_gate)
                    return _measurements.ToArray();
            }
        }

        public void Dispose()
        {
            _meterListener.Dispose();
            _activityListener.Dispose();
        }
    }

    private sealed record Measurement(
        string InstrumentName,
        long Value,
        KeyValuePair<string, object?>[] Tags);
}
