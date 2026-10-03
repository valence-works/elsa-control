using System.Diagnostics;
using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Core.Instances;
using ElsaControl.Deployment.Core.Telemetry;
using ElsaControl.Deployment.Core.Workspace;
using Xunit;

namespace ElsaControl.Deployment.Core.Tests;

[Collection(ManagedLifecycleTelemetryTestCollection.CollectionName)]
public sealed class ManagedLifecycleRecoveryRequiredAlertTests
{
    private static readonly Guid WorkspaceId = Guid.Parse("10000000-0000-0000-0000-000000000011");
    private static readonly Guid OrganizationId = Guid.Parse("20000000-0000-0000-0000-000000000011");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-29T10:00:00Z");

    [Fact]
    public async Task Mark_recovery_required_writes_exactly_one_entry_event()
    {
        using var capture = new AlertCapture();
        var store = new InMemoryElsaInstanceLifecycleStore();
        var created = await CreateAsync(store);

        store.MarkRecoveryRequired(created.Operation.Id);
        store.MarkRecoveryRequired(created.Operation.Id);

        Assert.Equal(ElsaInstanceOperationState.RecoveryRequired, store.Operations.Single().State);
        Assert.Single(capture.Entered);
        AssertAlert(capture.Entered[0], created.Instance.Id, created.Operation.Id);
    }

    [Fact]
    public async Task Provider_submission_handoff_and_reconcile_ticks_do_not_write_the_alert_event()
    {
        using var capture = new AlertCapture();
        var store = new InMemoryElsaInstanceLifecycleStore();
        var created = await CreateAsync(store);
        store.MarkRecoveryRequired(created.Operation.Id);
        capture.Clear();

        await ReconcileAsync(store, created.Operation.Id);
        await ReconcileAsync(store, created.Operation.Id);

        Assert.Empty(capture.Entered);
    }

    [Fact]
    public async Task Recover_then_a_second_entry_writes_exactly_one_more_event()
    {
        using var capture = new AlertCapture();
        var store = new InMemoryElsaInstanceLifecycleStore(new StaticTimeProvider(Now));
        var service = new ElsaInstanceLifecycleService(store, new StaticTimeProvider(Now));
        var created = await CreateAsync(store, service);
        store.MarkRecoveryRequired(created.Operation.Id);
        await ReconcileAsync(store, created.Operation.Id);
        Assert.Single(capture.Entered);

        var recovered = await service.RecoverAsync(new(
            WorkspaceId, created.Instance.Id, store.Instances.Single().Version, "recover-alert-1"));
        Assert.Equal(ElsaInstanceOperationState.Queued, recovered.Operation.State);
        Assert.Single(capture.Entered);

        store.MarkRecoveryRequired(created.Operation.Id);

        Assert.Equal(2, capture.Entered.Count);
        Assert.All(capture.Entered, activity => AssertAlert(activity, created.Instance.Id, created.Operation.Id));
    }

    [Fact]
    public void Health_evaluator_reads_do_not_write_the_alert_event()
    {
        using var capture = new AlertCapture();
        var workspaceId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa1");
        var instanceId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbb1");
        var operationId = Guid.Parse("cccccccc-cccc-cccc-cccc-ccccccccccc1");
        var snapshot = new ManagedLifecycleOperationalHealthSnapshot(
            workspaceId,
            instanceId,
            ElsaDesiredLifecycle.Running,
            ElsaObservedLifecycle.Unknown,
            ElsaInstanceHealth.Unknown,
            operation: new ManagedLifecycleOperationSnapshot(
                operationId,
                ElsaInstanceOperationState.RecoveryRequired,
                1,
                Now));
        var evaluator = new ManagedLifecycleOperationalHealthEvaluator();

        _ = evaluator.Evaluate(snapshot);
        _ = evaluator.Evaluate(snapshot);

        Assert.Empty(capture.Entered);
    }

    [Fact]
    public void Entry_event_is_an_internal_span_exported_as_app_dependencies()
    {
        using var capture = new AlertCapture();
        var workspaceId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa3");
        var instanceId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbb3");
        var operationId = Guid.Parse("cccccccc-cccc-cccc-cccc-ccccccccccc3");

        ManagedLifecycleRecoveryRequiredAlert.RecordEntered(workspaceId, instanceId, operationId, 1);

        var activity = Assert.Single(capture.Entered);
        Assert.Equal(ActivityKind.Internal, activity.Kind);
        Assert.Equal(
            ManagedLifecycleTelemetry.RecoveryRequiredEnteredActivityName,
            activity.OperationName);
        Assert.Equal("AppDependencies", ManagedLifecycleTelemetry.AppDependenciesTableName);
        Assert.Equal(
            ManagedLifecycleTelemetry.RecoveryRequiredEnteredActivityName,
            ManagedLifecycleRecoveryRequiredAlert.EventName);
    }

    [Fact]
    public void Configured_environment_is_written_on_the_entry_event()
    {
        using var capture = new AlertCapture();
        var workspaceId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa4");
        var instanceId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbb4");
        var operationId = Guid.Parse("cccccccc-cccc-cccc-cccc-ccccccccccc4");
        try
        {
            ManagedLifecycleTelemetry.ConfigureAlertEnvironment(ManagedLifecycleTelemetry.StagingEnvironment);
            ManagedLifecycleRecoveryRequiredAlert.RecordEntered(workspaceId, instanceId, operationId, 1);

            var activity = Assert.Single(capture.Entered);
            Assert.Equal(
                ManagedLifecycleTelemetry.StagingEnvironment,
                activity.GetTagItem(ManagedLifecycleTelemetry.EnvironmentTag));
            Assert.Contains(ManagedLifecycleTelemetry.EnvironmentTag, activity.Tags.Select(tag => tag.Key));
        }
        finally
        {
            ManagedLifecycleTelemetry.ConfigureAlertEnvironment(null);
        }
    }

    [Fact]
    public void Activity_sender_without_transport_ack_is_not_delivery()
    {
        using var capture = new AlertCapture();
        var sender = new ActivityRecoveryRequiredAlertSender();
        var workspaceId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa5");
        var instanceId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbb5");
        var operationId = Guid.Parse("cccccccc-cccc-cccc-cccc-ccccccccccc5");
        var runId = Guid.Parse("dddddddd-dddd-dddd-dddd-ddddddddddd5");
        var dedupe = ManagedLifecycleRecoveryRequiredAlert.ComputeDedupeIdentity(
            workspaceId, instanceId, operationId, 1, runId);

        Assert.False(sender.Send(new RecoveryRequiredAlertDispatch(
            workspaceId, instanceId, operationId, 1, runId, dedupe)));
        var activity = Assert.Single(capture.Entered);
        Assert.Equal(dedupe, activity.GetTagItem(ManagedLifecycleTelemetry.DedupeIdentityTag));
    }

    [Fact]
    public void Activity_sender_acks_only_when_the_exporter_accepts_that_identity()
    {
        var workspaceId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa6");
        var instanceId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbb6");
        var operationId = Guid.Parse("cccccccc-cccc-cccc-cccc-ccccccccccc6");
        var item = new RecoveryRequiredAlertDispatch(
            workspaceId,
            instanceId,
            operationId,
            1,
            null,
            ManagedLifecycleRecoveryRequiredAlert.ComputeDedupeIdentity(
                workspaceId, instanceId, operationId, 1));

        Assert.True(new ActivityRecoveryRequiredAlertSender(new StaticAck(true)).Send(item));
        Assert.False(new ActivityRecoveryRequiredAlertSender(new StaticAck(false)).Send(item));
        Assert.False(new ActivityRecoveryRequiredAlertSender().Send(item with { DedupeIdentity = " " }));
    }

    [Fact]
    public void Export_wait_and_lease_are_derived_from_one_timeout_budget()
    {
        Assert.Equal(RecoveryRequiredAlertBackoff.AckTimeout, RecoveryRequiredAlertBackoff.ExportTimeout);
        Assert.Equal(RecoveryRequiredAlertBackoff.AckTimeout, RecoveryRequiredAlertBackoff.WaitTimeout);
        Assert.Equal(RecoveryRequiredAlertBackoff.WaitTimeout, RecoveryRequiredAlertBackoff.SendTimeout);
        Assert.True(RecoveryRequiredAlertBackoff.ExportTimeout <= RecoveryRequiredAlertBackoff.WaitTimeout);
        Assert.True(RecoveryRequiredAlertBackoff.WaitTimeout < RecoveryRequiredAlertBackoff.LeaseDuration);
    }

    [Fact]
    public void Activity_sender_watches_before_emitting_the_span()
    {
        using var capture = new AlertCapture();
        var ack = new OrderAck();
        var workspaceId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa8");
        var instanceId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbb8");
        var operationId = Guid.Parse("cccccccc-cccc-cccc-cccc-ccccccccccc8");
        var dedupe = ManagedLifecycleRecoveryRequiredAlert.ComputeDedupeIdentity(
            workspaceId, instanceId, operationId, 1);

        Assert.True(new ActivityRecoveryRequiredAlertSender(ack).Send(new RecoveryRequiredAlertDispatch(
            workspaceId, instanceId, operationId, 1, null, dedupe)));
        Assert.Equal(["watch", "ack"], ack.Events);
        Assert.Single(capture.Entered);
        Assert.Equal(dedupe, ack.LastIdentity);
    }

    [Fact]
    public void Activity_sender_asks_the_ack_for_the_persisted_dedupe_identity()
    {
        var workspaceId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa7");
        var instanceId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbb7");
        var operationId = Guid.Parse("cccccccc-cccc-cccc-cccc-ccccccccccc7");
        var dedupe = ManagedLifecycleRecoveryRequiredAlert.ComputeDedupeIdentity(
            workspaceId, instanceId, operationId, 2);
        var ack = new RecordingAck();

        Assert.True(new ActivityRecoveryRequiredAlertSender(ack).Send(new RecoveryRequiredAlertDispatch(
            workspaceId, instanceId, operationId, 2, null, dedupe)));
        Assert.Equal(dedupe, ack.LastIdentity);
    }

    [Fact]
    public void Entry_event_carries_only_the_fixed_reason_and_opaque_ids()
    {
        using var capture = new AlertCapture();
        var workspaceId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa2");
        var instanceId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbb2");
        var operationId = Guid.Parse("cccccccc-cccc-cccc-cccc-ccccccccccc2");

        ManagedLifecycleRecoveryRequiredAlert.RecordEntered(workspaceId, instanceId, operationId, 1);
        ManagedLifecycleRecoveryRequiredAlert.RecordEntered(workspaceId, instanceId, operationId, 1);

        Assert.Equal(2, capture.Entered.Count);
        var activity = capture.Entered[0];
        AssertAlert(activity, instanceId, operationId, workspaceId);
        Assert.DoesNotContain(activity.Tags, tag =>
            tag.Value?.ToString()?.Contains('@', StringComparison.Ordinal) == true ||
            tag.Value?.ToString()?.Contains("secret", StringComparison.OrdinalIgnoreCase) == true ||
            tag.Value?.ToString()?.Contains("https://", StringComparison.OrdinalIgnoreCase) == true);
        Assert.Equal(
            new[]
            {
                ManagedLifecycleTelemetry.WorkspaceIdTag,
                ManagedLifecycleTelemetry.InstanceIdTag,
                ManagedLifecycleTelemetry.OperationIdTag,
                ManagedLifecycleTelemetry.DiagnosticCodeTag,
                ManagedLifecycleTelemetry.DedupeIdentityTag
            },
            activity.Tags.Select(tag => tag.Key).ToArray());
    }

    private static async Task<ElsaInstanceLifecycleAcceptance> CreateAsync(
        InMemoryElsaInstanceLifecycleStore store,
        ElsaInstanceLifecycleService? service = null)
    {
        service ??= new ElsaInstanceLifecycleService(store, new StaticTimeProvider(Now));
        return await service.CreateAsync(new ElsaInstanceCreateRequest(
            OrganizationId, WorkspaceId, "Alert Engine", "alert-engine", Intent(), $"create-{Guid.NewGuid():N}"));
    }

    private static Task ReconcileAsync(InMemoryElsaInstanceLifecycleStore store, Guid operationId) =>
        new ElsaInstanceProviderReconciliationService(
                store,
                new StaticProviderPort(new(
                    ElsaInstanceProviderObservationKind.Unknown,
                    ElsaObservedLifecycle.Unknown,
                    ElsaInstanceProviderHealthGate.Unknown,
                    "recovery-proof",
                    new ElsaInstanceProviderRetryEvidence(
                        "https://evidence.example.test/recovery/retry-proof",
                        "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"))),
                new StaticTimeProvider(Now))
            .ReconcileAsync(WorkspaceId, operationId);

    private static void AssertAlert(Activity activity, Guid instanceId, Guid operationId, Guid? workspaceId = null)
    {
        Assert.Equal(ManagedLifecycleTelemetry.RecoveryRequiredEnteredActivityName, activity.OperationName);
        Assert.Equal(
            ManagedLifecycleOperationalHealthDiagnosticCodes.RecoveryRequired,
            activity.GetTagItem(ManagedLifecycleTelemetry.DiagnosticCodeTag));
        Assert.Equal(instanceId.ToString("D"), activity.GetTagItem(ManagedLifecycleTelemetry.InstanceIdTag));
        Assert.Equal(operationId.ToString("D"), activity.GetTagItem(ManagedLifecycleTelemetry.OperationIdTag));
        if (workspaceId is { } id)
            Assert.Equal(id.ToString("D"), activity.GetTagItem(ManagedLifecycleTelemetry.WorkspaceIdTag));
        var dedupe = Assert.IsType<string>(activity.GetTagItem(ManagedLifecycleTelemetry.DedupeIdentityTag));
        Assert.Equal(64, dedupe.Length);
        Assert.All(dedupe, character => Assert.True(char.IsAsciiHexDigit(character)));
    }

    private sealed class StaticAck(bool acknowledged) : IRecoveryRequiredAlertTransportAck
    {
        public bool TryAcknowledge(string dedupeIdentity) =>
            acknowledged && !string.IsNullOrWhiteSpace(dedupeIdentity);
    }

    private sealed class RecordingAck : IRecoveryRequiredAlertTransportAck
    {
        public string? LastIdentity { get; private set; }

        public bool TryAcknowledge(string dedupeIdentity)
        {
            LastIdentity = dedupeIdentity;
            return !string.IsNullOrWhiteSpace(dedupeIdentity);
        }
    }

    private sealed class OrderAck : IRecoveryRequiredAlertTransportAck
    {
        public List<string> Events { get; } = [];
        public string? LastIdentity { get; private set; }

        public void Watch(string dedupeIdentity) => Events.Add("watch");

        public bool TryAcknowledge(string dedupeIdentity)
        {
            LastIdentity = dedupeIdentity;
            Events.Add("ack");
            return !string.IsNullOrWhiteSpace(dedupeIdentity);
        }
    }

    private static ElsaInstanceIntent Intent() => new(
        new ElsaReleaseIntent("valence-runtime", "3.8", channel: "stable"),
        new ElsaApplicationIntent("combined", "starter", packagePolicy: "approved"),
        new ElsaPlacementIntent("managed", "westeurope", "dedicated", "standard-small", "public", "managed"),
        ElsaDesiredLifecycle.Running);

    private sealed class StaticTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class StaticProviderPort(ElsaInstanceProviderObservation observation)
        : IElsaInstanceProviderReconciliationPort
    {
        public Task<ElsaInstanceProviderObservation> ObserveAsync(
            ElsaInstanceProviderReconciliationRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(observation.Correlate(request));
    }

    private sealed class AlertCapture : IDisposable
    {
        private readonly ActivityListener _listener;
        private readonly object _gate = new();
        private readonly List<Activity> _entered = [];

        public AlertCapture()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == ManagedLifecycleTelemetry.ActivitySourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStopped = activity =>
                {
                    if (activity.OperationName != ManagedLifecycleTelemetry.RecoveryRequiredEnteredActivityName)
                        return;
                    lock (_gate)
                        _entered.Add(activity);
                }
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public IReadOnlyList<Activity> Entered
        {
            get
            {
                lock (_gate)
                    return _entered.ToArray();
            }
        }

        public void Clear()
        {
            lock (_gate)
                _entered.Clear();
        }

        public void Dispose() => _listener.Dispose();
    }
}
