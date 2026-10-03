using ElsaControl.Api.Workspace;
using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Azure;
using ElsaControl.Deployment.Core.Cockpit;
using ElsaControl.Deployment.Core.Instances;
using ElsaControl.Deployment.Core.Workspace;
using ElsaControl.PackageCatalog.Core.Accounts;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using ElsaControl.RuntimeBuilder.Abstractions;
using ElsaControl.RuntimeBuilder.Abstractions.Plans;
using ElsaControl.RuntimeBuilder.Abstractions.ReleaseManifests;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using CatalogWorkspace = ElsaControl.PackageCatalog.Core.Accounts.Workspace;

namespace ElsaControl.Api.Tests;

public sealed partial class ElsaInstanceProviderReconciliationHostedServiceTests
{
    private static readonly Guid WorkspaceId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OperationId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task RecoveryRequired_scan_failure_does_not_block_reconciliation()
    {
        var provider = new RecordingSubmissionPort();
        var reconciler = new RecordingReconciliationService
        {
            ClockScanFailure = new InvalidOperationException("The RecoveryRequired clock scan failed.")
        };
        await using var services = CreateServices(provider, reconciler, [Pending(withSubmission: true)]);
        var logger = new RecordingLogger();

        await CreateHostedService(services, logger: logger).ProcessPendingAsync(CancellationToken.None);

        Assert.Equal(1, reconciler.ClockScanCalls);
        Assert.Equal(1, provider.Calls);
        Assert.Equal(1, reconciler.Calls);
        var error = Assert.Single(logger.Messages, message => message.Level == LogLevel.Error);
        Assert.Equal("Managed Elsa RecoveryRequired clock scan failed.", error.Message);
        Assert.IsType<InvalidOperationException>(error.Exception);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Invalid_pending_submission_emits_a_safe_warning_without_dispatch(bool mismatchedIdentity)
    {
        var provider = new RecordingSubmissionPort();
        var reconciler = new RecordingReconciliationService();
        var submission = RecoverySubmission() with
        {
            OperationId = mismatchedIdentity ? Guid.NewGuid() : OperationId,
            AttemptNumber = mismatchedIdentity ? 1 : 0,
            Location = "sensitive-input-must-not-be-logged"
        };
        await using var services = CreateServices(provider, reconciler,
            [new ElsaInstanceProviderPendingOperation(WorkspaceId, OperationId, submission)]);
        var logger = new RecordingLogger();

        await CreateHostedService(services, logger: logger).ProcessPendingAsync(CancellationToken.None);

        var warning = Assert.Single(logger.Messages);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Equal($"Managed Elsa provider submission is invalid for pending operation {OperationId}.", warning.Message);
        Assert.Null(warning.Exception);
        Assert.Equal(0, provider.Calls);
        Assert.Equal(0, reconciler.Calls);
    }

    private sealed class RecordingLogger : ILogger<ElsaInstanceProviderReconciliationHostedService>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add((logLevel, formatter(state, exception), exception));
    }

    [Fact]
    public async Task Corrupt_stored_handoff_does_not_downgrade_to_ordinary_reconciliation()
    {
        var provider = new RecordingSubmissionPort();
        var recoveryProvider = new RecordingRecoveryPort();
        var reconciler = new RecordingReconciliationService();
        await using var services = CreateServices(provider, reconciler,
            [new ElsaInstanceProviderPendingOperation(WorkspaceId, OperationId) { HandoffInvalid = true }],
            recoveryProvider);
        var logger = new RecordingLogger();

        await CreateHostedService(services, logger: logger).ProcessPendingAsync(CancellationToken.None);

        var warning = Assert.Single(logger.Messages);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Equal($"Managed Elsa provider hand-off metadata is invalid for pending operation {OperationId}.", warning.Message);
        Assert.Null(warning.Exception);
        Assert.Equal(0, provider.Calls);
        Assert.Equal(0, recoveryProvider.Calls);
        Assert.Equal(0, reconciler.Calls);
    }

    [Fact]
    public async Task Normal_replay_preserves_the_returned_assignment_binding()
    {
        var provider = new RecordingSubmissionPort();
        var store = new SubmissionStore();
        await using var services = CreateServices(provider, new RecordingReconciliationService(),
            [Pending(true)], submissionStore: store);
        await CreateHostedService(services).ProcessPendingAsync(CancellationToken.None);
        Assert.Equal("d0000000-0000-0000-0000-000000000001", Assert.Single(store.Commits).PlacementAssignmentId);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Mismatched_pending_identity_cannot_dispatch_or_reconcile(bool recovery, bool workspaceMismatch)
    {
        var submission = RecoverySubmission();
        var pending = new ElsaInstanceProviderPendingOperation(
            workspaceMismatch ? Guid.NewGuid() : WorkspaceId,
            workspaceMismatch ? OperationId : Guid.NewGuid(),
            submission, recovery ? RecoveryEnvelope(submission) : null);
        var provider = new RecordingSubmissionPort();
        var recoveryProvider = new RecordingRecoveryPort();
        var reconciler = new RecordingReconciliationService();
        await using var services = CreateServices(provider, reconciler, [pending], recoveryProvider);
        await CreateHostedService(services).ProcessPendingAsync(CancellationToken.None);
        Assert.Equal(0, provider.Calls);
        Assert.Equal(0, recoveryProvider.Calls);
        Assert.Equal(0, reconciler.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Invalid_or_delete_normal_submission_cannot_dispatch(bool delete)
    {
        var submission = delete
            ? RecoverySubmission(ElsaInstanceOperationAction.Delete)
            : RecoverySubmission() with { InstanceId = Guid.Empty };
        var provider = new RecordingSubmissionPort();
        var reconciler = new RecordingReconciliationService();
        await using var services = CreateServices(provider, reconciler,
            [new ElsaInstanceProviderPendingOperation(WorkspaceId, OperationId, submission)]);
        await CreateHostedService(services).ProcessPendingAsync(CancellationToken.None);
        Assert.Equal(0, provider.Calls);
        Assert.Equal(0, reconciler.Calls);
    }

    [Fact]
    public async Task Provider_cancellation_is_propagated_from_the_per_operation_boundary()
    {
        using var cancellation = new CancellationTokenSource();
        var provider = new RecordingSubmissionPort(new OperationCanceledException());
        var reconciler = new RecordingReconciliationService();
        await using var services = CreateServices(provider, reconciler, [Pending(withSubmission: true)]);
        var hosted = CreateHostedService(services);

        var error = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            hosted.ProcessPendingAsync(cancellation.Token));

        Assert.Same(provider.Cancellation, error);
        Assert.Equal(0, reconciler.Calls);
    }

    [Fact]
    public async Task Replay_submission_failure_still_reconciles_the_same_operation()
    {
        var provider = new RecordingSubmissionPort(new InvalidOperationException("provider detail must not escape"));
        var reconciler = new RecordingReconciliationService();
        await using var services = CreateServices(provider, reconciler, [Pending(withSubmission: true)]);
        var hosted = CreateHostedService(services);

        await hosted.ProcessPendingAsync(CancellationToken.None);

        Assert.Equal(1, provider.Calls);
        Assert.Equal(1, reconciler.Calls);
        Assert.Equal((WorkspaceId, OperationId), Assert.Single(reconciler.Requests));
    }

    [Fact]
    public async Task Outcome_unknown_submission_still_reconciles_the_same_operation()
    {
        var provider = new RecordingSubmissionPort(new ElsaInstanceProviderSubmissionException(
            ElsaInstanceProviderSubmissionFailureKind.OutcomeUnknown));
        var reconciler = new RecordingReconciliationService();
        await using var services = CreateServices(provider, reconciler, [Pending(withSubmission: true)]);
        var hosted = CreateHostedService(services);

        await hosted.ProcessPendingAsync(CancellationToken.None);

        Assert.Equal(1, provider.Calls);
        Assert.Equal(1, reconciler.Calls);
        Assert.Equal((WorkspaceId, OperationId), Assert.Single(reconciler.Requests));
    }

    [Fact]
    public async Task Rejected_submission_remains_retryable_without_reconciling_nonexistent_provider_work()
    {
        var provider = new RecordingSubmissionPort(new ElsaInstanceProviderSubmissionException(
            ElsaInstanceProviderSubmissionFailureKind.Rejected));
        var reconciler = new RecordingReconciliationService();
        await using var services = CreateServices(provider, reconciler, [Pending(withSubmission: true)]);
        var hosted = CreateHostedService(services);

        await hosted.ProcessPendingAsync(CancellationToken.None);
        await hosted.ProcessPendingAsync(CancellationToken.None);

        Assert.Equal(2, provider.Calls);
        Assert.Equal(0, reconciler.Calls);
    }

    [Fact]
    public async Task Accepted_recovery_uses_the_recovery_port_and_never_replays_submission()
    {
        var submission = RecoverySubmission();
        var recovery = RecoveryEnvelope(submission);
        var provider = new RecordingSubmissionPort();
        var recoveryProvider = new RecordingRecoveryPort();
        var reconciler = new RecordingReconciliationService();
        var submissionStore = new SubmissionStore();
        await using var services = CreateServices(
            provider,
            reconciler,
            [new ElsaInstanceProviderPendingOperation(WorkspaceId, OperationId, submission, recovery)],
            recoveryProvider,
            submissionStore: submissionStore);
        var hosted = CreateHostedService(services);

        await hosted.ProcessPendingAsync(CancellationToken.None);

        Assert.Equal(0, provider.Calls);
        Assert.Equal(1, recoveryProvider.Calls);
        Assert.Same(submission, recoveryProvider.Request!.Submission);
        Assert.Same(recovery, recoveryProvider.Request.Envelope);
        var commit = Assert.Single(submissionStore.Commits);
        Assert.Equal(submission.WorkspaceId, commit.WorkspaceId);
        Assert.Equal(submission.InstanceId, commit.InstanceId);
        Assert.Equal(submission.OperationId, commit.OperationId);
        Assert.Equal(submission.AttemptNumber, commit.AttemptNumber);
        Assert.Equal("provider.recovery.succeeded", commit.CorrelationId);
        Assert.Equal(1, reconciler.Calls);
        Assert.Equal((WorkspaceId, OperationId), Assert.Single(reconciler.Requests));
    }

    [Theory]
    [InlineData(ElsaInstanceProviderRecoveryOutcome.InProgress, "provider.recovery.in-progress", true)]
    [InlineData(ElsaInstanceProviderRecoveryOutcome.RecoveryRequired, "provider.recovery.required", true)]
    [InlineData(ElsaInstanceProviderRecoveryOutcome.Failed, "provider.recovery.failed", true)]
    [InlineData(ElsaInstanceProviderRecoveryOutcome.Rejected, "provider.recovery.rejected", false)]
    public async Task Recovery_outcome_controls_durable_handoff_and_reconciliation(
        ElsaInstanceProviderRecoveryOutcome outcome,
        string code,
        bool shouldReconcile)
    {
        var submission = RecoverySubmission();
        var provider = new RecordingSubmissionPort();
        var recoveryProvider = new RecordingRecoveryPort(outcome: outcome, code: code);
        var submissionStore = new SubmissionStore();
        var reconciler = new RecordingReconciliationService();
        await using var services = CreateServices(
            provider,
            reconciler,
            [new ElsaInstanceProviderPendingOperation(WorkspaceId, OperationId, submission, RecoveryEnvelope(submission))],
            recoveryProvider,
            submissionStore: submissionStore);
        var hosted = CreateHostedService(services);

        await hosted.ProcessPendingAsync(CancellationToken.None);

        Assert.Equal(0, provider.Calls);
        Assert.Equal(shouldReconcile ? 1 : 0, submissionStore.Commits.Count);
        Assert.Equal(shouldReconcile ? 1 : 0, reconciler.Calls);
    }

    [Fact]
    public async Task Invalid_recovery_envelope_fails_closed_before_provider_or_reconciliation()
    {
        var submission = RecoverySubmission();
        var invalidRecovery = RecoveryEnvelope(submission) with
        {
            AcceptedLifecycleAttemptNumber = submission.AttemptNumber + 1
        };
        var provider = new RecordingSubmissionPort();
        var recoveryProvider = new RecordingRecoveryPort();
        var reconciler = new RecordingReconciliationService();
        await using var services = CreateServices(
            provider,
            reconciler,
            [new ElsaInstanceProviderPendingOperation(WorkspaceId, OperationId, submission, invalidRecovery)],
            recoveryProvider);
        var hosted = CreateHostedService(services);

        await hosted.ProcessPendingAsync(CancellationToken.None);

        Assert.Equal(0, provider.Calls);
        Assert.Equal(0, recoveryProvider.Calls);
        Assert.Equal(0, reconciler.Calls);
    }

    [Fact]
    public async Task Recovery_provider_failure_does_not_reconcile_without_a_durable_handoff()
    {
        var submission = RecoverySubmission();
        var provider = new RecordingSubmissionPort();
        var recoveryProvider = new RecordingRecoveryPort(new InvalidOperationException("provider detail"));
        var reconciler = new RecordingReconciliationService();
        await using var services = CreateServices(
            provider,
            reconciler,
            [new ElsaInstanceProviderPendingOperation(WorkspaceId, OperationId, submission, RecoveryEnvelope(submission))],
            recoveryProvider);
        var hosted = CreateHostedService(services);

        await hosted.ProcessPendingAsync(CancellationToken.None);

        Assert.Equal(0, provider.Calls);
        Assert.Equal(1, recoveryProvider.Calls);
        Assert.Equal(0, reconciler.Calls);
    }

    [Fact]
    public async Task Recovery_provider_cancellation_is_propagated_without_reconciliation()
    {
        var cancellation = new OperationCanceledException();
        var submission = RecoverySubmission();
        var provider = new RecordingSubmissionPort();
        var recoveryProvider = new RecordingRecoveryPort(cancellation);
        var reconciler = new RecordingReconciliationService();
        await using var services = CreateServices(
            provider,
            reconciler,
            [new ElsaInstanceProviderPendingOperation(WorkspaceId, OperationId, submission, RecoveryEnvelope(submission))],
            recoveryProvider);
        var hosted = CreateHostedService(services);

        var error = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            hosted.ProcessPendingAsync(CancellationToken.None));

        Assert.Same(cancellation, error);
        Assert.Equal(0, provider.Calls);
        Assert.Equal(0, reconciler.Calls);
    }

    [Fact]
    public async Task Recovery_denied_by_commercial_gate_does_not_call_provider_or_reconcile()
    {
        var submission = RecoverySubmission();
        var provider = new RecordingSubmissionPort();
        var recoveryProvider = new RecordingRecoveryPort();
        var commercialGate = new RecordingCommercialGate(DeniedCommercialDecision());
        var reconciler = new RecordingReconciliationService();
        await using var services = CreateServices(
            provider,
            reconciler,
            [new ElsaInstanceProviderPendingOperation(WorkspaceId, OperationId, submission, RecoveryEnvelope(submission))],
            recoveryProvider,
            commercialGate);
        var hosted = CreateHostedService(services);

        await hosted.ProcessPendingAsync(CancellationToken.None);

        Assert.Equal(1, commercialGate.Calls);
        Assert.Equal(0, provider.Calls);
        Assert.Equal(0, recoveryProvider.Calls);
        Assert.Equal(0, reconciler.Calls);
    }

    [Fact]
    public async Task Recovery_denied_by_entitlement_hold_does_not_call_provider_or_reconcile()
    {
        var submission = RecoverySubmission();
        var provider = new RecordingSubmissionPort();
        var recoveryProvider = new RecordingRecoveryPort();
        var entitlementHoldStore = new RecordingEntitlementHoldStore(DeniedCommercialDecision());
        var reconciler = new RecordingReconciliationService();
        await using var services = CreateServices(
            provider,
            reconciler,
            [new ElsaInstanceProviderPendingOperation(WorkspaceId, OperationId, submission, RecoveryEnvelope(submission))],
            recoveryProvider,
            entitlementHoldStore: entitlementHoldStore);
        var hosted = CreateHostedService(services);

        await hosted.ProcessPendingAsync(CancellationToken.None);

        Assert.Equal(1, entitlementHoldStore.Calls);
        Assert.Equal(0, provider.Calls);
        Assert.Equal(0, recoveryProvider.Calls);
        Assert.Equal(0, reconciler.Calls);
    }

    [Fact]
    public async Task Delete_recovery_stays_on_the_cleanup_path()
    {
        var submission = RecoverySubmission(
            ElsaControl.Deployment.Abstractions.Instances.ElsaInstanceOperationAction.Delete);
        var provider = new RecordingSubmissionPort();
        var recoveryProvider = new RecordingRecoveryPort();
        var reconciler = new RecordingReconciliationService();
        await using var services = CreateServices(
            provider,
            reconciler,
            [new ElsaInstanceProviderPendingOperation(WorkspaceId, OperationId, submission, RecoveryEnvelope(submission))],
            recoveryProvider);
        var hosted = CreateHostedService(services);

        await hosted.ProcessPendingAsync(CancellationToken.None);

        Assert.Equal(0, provider.Calls);
        Assert.Equal(0, recoveryProvider.Calls);
        Assert.Equal(0, reconciler.Calls);
    }

    [Fact]
    public async Task Delete_without_provider_submission_keeps_existing_reconciliation_path()
    {
        var provider = new RecordingSubmissionPort();
        var recoveryProvider = new RecordingRecoveryPort();
        var reconciler = new RecordingReconciliationService();
        await using var services = CreateServices(
            provider,
            reconciler,
            [new ElsaInstanceProviderPendingOperation(WorkspaceId, OperationId)],
            recoveryProvider);
        var hosted = CreateHostedService(services);

        await hosted.ProcessPendingAsync(CancellationToken.None);

        Assert.Equal(0, provider.Calls);
        Assert.Equal(0, recoveryProvider.Calls);
        Assert.Equal(1, reconciler.Calls);
    }

    [Fact]
    public async Task Recovery_without_a_valid_provider_boundary_fails_closed_without_submission_replay()
    {
        var submission = RecoverySubmission();
        var provider = new RecordingSubmissionPort();
        var reconciler = new RecordingReconciliationService();
        await using var services = CreateServices(
            provider,
            reconciler,
            [new ElsaInstanceProviderPendingOperation(WorkspaceId, OperationId, submission, RecoveryEnvelope(submission))]);
        var hosted = CreateHostedService(services);

        await hosted.ProcessPendingAsync(CancellationToken.None);

        Assert.Equal(0, provider.Calls);
        Assert.Equal(0, reconciler.Calls);
    }

    [Fact]
    public async Task Disabled_lifecycle_options_do_not_process_pending_provider_work()
    {
        var provider = new RecordingSubmissionPort();
        var reconciler = new RecordingReconciliationService();
        await using var services = CreateServices(provider, reconciler, [Pending(withSubmission: true)]);
        var hosted = CreateHostedService(services, enabled: false);

        await hosted.ProcessPendingAsync(CancellationToken.None);

        Assert.Equal(0, provider.Calls);
        Assert.Equal(0, reconciler.Calls);
    }

    [Fact]
    public async Task Accepted_recovery_survives_restart_and_reconciles_through_real_ef_store()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var setupDb = CreateMigratedContext(connection);
        await setupDb.Database.MigrateAsync();

        var workspace = await CreateWorkspaceAsync(setupDb, "Hosted recovery EF workspace");
        var instanceId = Guid.NewGuid();
        var accepted = await new ElsaInstanceLifecycleService(
                new EfCoreElsaInstanceLifecycleStore(setupDb, EmptyResolutionInputSource.Instance),
                new FixedTimeProvider(Now))
            .CreateAsync(new ElsaInstanceCreateRequest(
                workspace.OrganizationId,
                workspace.Id,
                "Managed Elsa",
                "hosted-recovery-ef",
                WorkerIntent(),
                "hosted-recovery-ef-create",
                instanceId));

        var deploymentStore = new DeploymentWorkspaceStore(setupDb);
        var application = await deploymentStore.CreateApplicationAsync(
            workspace.Id,
            new CreateWorkflowApplicationRequest("Hosted recovery application", null, null));
        var environment = await deploymentStore.CreateEnvironmentAsync(
            workspace.Id,
            new CreateDeploymentEnvironmentRequest(application.Id, "Production", EnvironmentTier.Production));
        // This fixture creates the managed target directly. Preserve the same
        // instance binding required by the production resolution transaction.
        Assert.Equal(1, await setupDb.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE DeploymentEnvironments SET ElsaInstanceId = {instanceId} WHERE Id = {environment.Id} AND WorkspaceId = {workspace.Id}"));
        var target = new ElsaInstanceLifecycleDeploymentTarget(
            application.Id,
            environment.Id,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid());
        var resolution = SuccessfulResolution(workspace.Id, instanceId);
        var lifecycleStore = new EfCoreElsaInstanceLifecycleStore(
            setupDb,
            new StaticResolutionInputSource(target),
            new FixedTimeProvider(Now),
            recoveryObservationStore: new HostedRecoveryObservationStore());
        var queued = await new ElsaInstanceLifecycleWorker(
                lifecycleStore,
                new StaticResolver(resolution),
                new FixedTimeProvider(Now))
            .ProcessAvailableAsync("hosted-recovery-resolver");
        var queuedResult = Assert.Single(queued.Results);
        Assert.True(queuedResult.Outcome == ElsaInstanceLifecycleWorkerOutcome.Queued,
            $"Unexpected worker outcome {queuedResult.Outcome}: {queuedResult.FailureCode}");

        await lifecycleStore.CommitProviderSubmissionAsync(new(
            workspace.Id,
            instanceId,
            accepted.Operation.Id,
            accepted.Operation.AttemptNumber,
            "provider-operation-accepted",
            Now));
        var uncertain = await new ElsaInstanceProviderReconciliationService(
                lifecycleStore,
                new FixedObservationPort(
                    ElsaInstanceProviderObservationKind.Ambiguous,
                    "provider-observation-before-recovery",
                    new ElsaInstanceProviderRetryEvidence(
                        HostedRecoveryObservationStore.Reference,
                        HostedRecoveryObservationStore.Digest)),
                new FixedTimeProvider(Now.AddMinutes(1)))
            .ReconcileAsync(workspace.Id, accepted.Operation.Id);
        Assert.Equal(ElsaInstanceProviderReconciliationOutcome.RecoveryRequired, uncertain.Outcome);
        Assert.True(uncertain.RetrySafe);

        setupDb.ChangeTracker.Clear();
        var current = await lifecycleStore.GetInstanceAsync(workspace.Id, instanceId)
            ?? throw new InvalidOperationException("Expected the managed instance after reconciliation.");
        var recovered = await new ElsaInstanceLifecycleService(
                lifecycleStore,
                new FixedTimeProvider(Now.AddMinutes(2)))
            .RecoverAsync(new(
                workspace.Id,
                instanceId,
                current.Version,
                "hosted-recovery-ef-resume"));
        Assert.Equal(ElsaInstanceOperationState.Queued, recovered.Operation.State);
        Assert.NotNull(recovered.Operation.RecoveryIdempotencyKey);
        Assert.Equal(current.Version + 1, recovered.Instance.Version);

        // Reconstruct both the typed submission and provider recovery envelope
        // through the real persistence projection. This guards the durable
        // restart path against fabricating an envelope from the mutable row.
        var persistedPending = Assert.Single(await lifecycleStore.ListPendingProviderOperationsAsync(16));
        var pendingSubmission = persistedPending.Submission
            ?? throw new InvalidOperationException("Expected the queued provider submission after recovery.");
        var recovery = persistedPending.Recovery
            ?? throw new InvalidOperationException("Expected the retained provider recovery envelope after recovery.");
        recovery.Validate();
        Assert.Equal(recovered.Operation.AttemptNumber, recovery.AcceptedLifecycleAttemptNumber);
        Assert.Equal(recovered.Instance.Version, recovery.AcceptedInstanceVersion);
        var pending = new ElsaInstanceProviderPendingOperation(
            workspace.Id,
            accepted.Operation.Id,
            pendingSubmission,
            recovery);
        ElsaInstanceProviderPendingOperation restartedPending;
        var priorSubmissionEvents = (await new EfCoreManagedElsaInstanceApiStore(setupDb)
                .ListAuditAsync(workspace.Id, instanceId))
            .Count(x => x.OperationId == accepted.Operation.Id && x.EventType == "lifecycle.provider-submitted");
        Assert.Equal(1, priorSubmissionEvents);

        // A crash after recovery was accepted but before the lifecycle hand-off
        // leaves the exact envelope queued. A fresh context must retry that
        // envelope, not replay ordinary provider submission.
        await using (var crashedDb = CreateMigratedContext(connection))
        {
            var crashedStore = new EfCoreElsaInstanceLifecycleStore(
                crashedDb,
                EmptyResolutionInputSource.Instance,
                new FixedTimeProvider(Now.AddMinutes(3)));
            var failedRecovery = new RecordingRecoveryPort(new InvalidOperationException("simulated crash"));
            var reconciler = new ElsaInstanceProviderReconciliationService(
                crashedStore,
                new FixedObservationPort(
                    ElsaInstanceProviderObservationKind.Confirmed,
                    "provider-observation-after-restart"),
                new FixedTimeProvider(Now.AddMinutes(3)));
            var normalProvider = new RecordingSubmissionPort();
            await using var services = CreateServices(
                normalProvider,
                reconciler,
                [pending],
                failedRecovery,
                submissionStore: crashedStore);

            await CreateHostedService(services).ProcessPendingAsync(CancellationToken.None);

            Assert.Equal(1, failedRecovery.Calls);
            Assert.Equal(0, normalProvider.Calls);
            var crashedApi = new EfCoreManagedElsaInstanceApiStore(crashedDb);
            var crashedOperation = await crashedApi.GetOperationAsync(
                workspace.Id, instanceId, accepted.Operation.Id);
            Assert.Equal(ElsaInstanceOperationState.Queued, crashedOperation!.State);
            Assert.Equal(WorkspaceDeploymentRunStatus.Queued,
                Assert.Single(await crashedApi.ListDeploymentsAsync(workspace.Id, instanceId)).Status);
            Assert.Equal(priorSubmissionEvents,
                (await crashedApi.ListAuditAsync(workspace.Id, instanceId))
                .Count(x => x.OperationId == accepted.Operation.Id && x.EventType == "lifecycle.provider-submitted"));
        }

        // Rehydrate the pending item from a new EF context after the failed
        // attempt. The restart must consume the durable envelope, not the
        // in-memory object that was present before the simulated crash.
        await using (var reloadDb = CreateMigratedContext(connection))
        {
            restartedPending = Assert.Single(
                await new EfCoreElsaInstanceLifecycleStore(
                    reloadDb,
                    EmptyResolutionInputSource.Instance,
                    new FixedTimeProvider(Now.AddMinutes(3)))
                    .ListPendingProviderOperationsAsync(16));
            Assert.NotNull(restartedPending.Submission);
            Assert.NotNull(restartedPending.Recovery);
            restartedPending.Recovery!.Validate();
        }

        await using (var resumedDb = CreateMigratedContext(connection))
        {
            var resumedStore = new EfCoreElsaInstanceLifecycleStore(
                resumedDb,
                EmptyResolutionInputSource.Instance,
                new FixedTimeProvider(Now.AddMinutes(4)));
            var recoveredProvider = new RecordingRecoveryPort();
            var providerObservation = new FixedObservationPort(
                ElsaInstanceProviderObservationKind.Confirmed,
                "provider-observation-terminal");
            var reconciler = new ElsaInstanceProviderReconciliationService(
                resumedStore,
                providerObservation,
                new FixedTimeProvider(Now.AddMinutes(4)));
            var normalProvider = new RecordingSubmissionPort();
            await using var services = CreateServices(
                normalProvider,
                reconciler,
                [restartedPending],
                recoveredProvider,
                submissionStore: resumedStore);

            await CreateHostedService(services).ProcessPendingAsync(CancellationToken.None);

            Assert.Equal(1, recoveredProvider.Calls);
            Assert.Equal(0, normalProvider.Calls);
            var resumedApi = new EfCoreManagedElsaInstanceApiStore(resumedDb);
            var operation = await resumedApi.GetOperationAsync(
                workspace.Id, instanceId, accepted.Operation.Id);
            Assert.Equal(ElsaInstanceOperationState.Succeeded, operation!.State);
            Assert.Equal(WorkspaceDeploymentRunStatus.Succeeded,
                Assert.Single(await resumedApi.ListDeploymentsAsync(workspace.Id, instanceId)).Status);
            Assert.Equal(1, providerObservation.Calls);
        }
    }

    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-06T10:00:00Z");

    private static CatalogDbContext CreateMigratedContext(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseSqlite(connection, sqlite =>
                sqlite.MigrationsAssembly(CatalogDatabaseServiceCollectionExtensions.SqliteMigrationsAssembly))
            .Options;
        return new CatalogDbContext(options);
    }

    private static async Task<CatalogWorkspace> CreateWorkspaceAsync(CatalogDbContext db, string name)
    {
        var workspace = new CatalogWorkspace { Name = name };
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();
        db.OrganizationEntitlementSnapshots.Add(new OrganizationEntitlementSnapshot
        {
            OrganizationId = workspace.OrganizationId,
            ManagedHostingEnabled = true,
            SubscriptionState = OrganizationSubscriptionState.Active,
            MaxInstances = int.MaxValue,
            SyncedAt = Now,
            CreatedAt = Now,
            UpdatedAt = Now
        });
        await db.SaveChangesAsync();
        return workspace;
    }

    private static ElsaInstanceIntent WorkerIntent() => new(
        new ElsaReleaseIntent("future-runtime", "5.0", "5.0.0-preview.1", "preview"),
        new ElsaApplicationIntent("server-studio"),
        new ElsaPlacementIntent("managed", "westeurope", "dedicated", "standard-small", "public", "managed"));

    private static ElsaInstanceLifecycleResolutionInput ResolutionInput(
        ElsaInstance requestedInstance,
        ElsaInstanceLifecycleDeploymentTarget target) => new(
        new ElsaInstancePlanResolutionRequest(
            requestedInstance.Intent,
            new(new("future-runtime", null, null, null), [], [], [], null),
            AdmittedManifest(),
            "plan_worker_01",
            $"https://control.example.test/api/workspaces/{requestedInstance.WorkspaceId:D}/instances/{requestedInstance.Id:D}/resolved-plans/plan_worker_01",
            requestedInstance.WorkspaceId),
        target);

    private static ReleaseManifestAdmissionResult AdmittedManifest() =>
        new(
            true,
            "https://example.test/manifests/5.0.0-preview.1.json",
            "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
            new(
                "1",
                new("future-runtime", "commercial", "5.0", "5.0.0-preview.1", "preview", "preview",
                    new("https://example.test/repo", "0123456789abcdef0123456789abcdef01234567", "build", "1")),
                [new(
                    "server-studio",
                    ["elsa.server"],
                    [new("paid", "registry.example.test/elsa/server@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")],
                    new Dictionary<string, string> { ["server"] = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" },
                    new Dictionary<string, string>(),
                    new("1", []),
                    new(null, null, [], null))]),
            new("https://example.test/signatures/5.0.0-preview.1.sig", "sha256:dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd"),
            "paid",
            "server-studio",
            []);

    private static ElsaInstancePlanResolutionResult SuccessfulResolution(Guid workspaceId, Guid instanceId)
    {
        var plan = new ResolvedElsaApplicationPlan(
            "1",
            new("future-runtime", "5.0", "5.0.0-preview.1", "https://example.test/repo", "0123456789abcdef0123456789abcdef01234567", "https://example.test/manifests/5.0.0-preview.1.json", "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"),
            new("server-studio", [new("server", ["server"], new("paid", "registry.example.test/elsa", "registry.example.test/elsa/server@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), ["elsa.server"], [], [])]),
            [],
            new([]),
            new([], []),
            new("public", "public", false, [], []),
            "dedicated",
            new("preview", "preview", "stable", "automatic-within-minor", "explicit-approval", "explicit-migration"),
            [],
            [new(ReleaseManifestEvidenceKinds.Manifest, "https://example.test/manifests/5.0.0-preview.1.json", "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc", ReleaseManifestEvidenceContract.DescriptionFor(ReleaseManifestEvidenceKinds.Manifest))]);
        var reference = new ElsaResolvedPlanReference(
            "plan_worker_01",
            1,
            ResolvedElsaApplicationPlanSerialization.ComputeContentHash(plan),
            $"https://control.example.test/api/workspaces/{workspaceId:D}/instances/{instanceId:D}/resolved-plans/plan_worker_01");
        return new(
            true,
            plan,
            reference,
            new(reference, "future-runtime", "5.0", "5.0.0-preview.1", "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc", [new("server", "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")]),
            []);
    }

    private static ServiceProvider CreateServices(
        IElsaInstanceProviderSubmissionPort provider,
        IElsaInstanceProviderReconciliationService reconciler,
        IReadOnlyList<ElsaInstanceProviderPendingOperation> pending,
        IElsaInstanceProviderRecoveryPort? recoveryProvider = null,
        IElsaInstanceCommercialGate? commercialGate = null,
        IElsaInstanceEntitlementHoldStore? entitlementHoldStore = null,
        IElsaInstanceProviderSubmissionStore? submissionStore = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IElsaInstanceProviderPendingOperationStore>(new PendingStore(pending));
        services.AddSingleton<IElsaInstanceProviderSubmissionPort>(provider);
        if (recoveryProvider is not null)
            services.AddSingleton<IElsaInstanceProviderRecoveryPort>(recoveryProvider);
        if (commercialGate is not null)
            services.AddSingleton<IElsaInstanceCommercialGate>(commercialGate);
        if (entitlementHoldStore is not null)
            services.AddSingleton<IElsaInstanceEntitlementHoldStore>(entitlementHoldStore);
        services.AddSingleton<IElsaInstanceProviderSubmissionStore>(submissionStore ?? new SubmissionStore());
        services.AddSingleton<IElsaInstanceProviderReconciliationService>(reconciler);
        return services.BuildServiceProvider();
    }

    private static ElsaInstanceProviderReconciliationHostedService CreateHostedService(
        ServiceProvider services,
        bool enabled = true,
        ILogger<ElsaInstanceProviderReconciliationHostedService>? logger = null) =>
        new(
            services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new ElsaInstanceLifecycleWorkerOptions { Enabled = enabled }),
            logger ?? NullLogger<ElsaInstanceProviderReconciliationHostedService>.Instance);

    private static ElsaInstanceProviderSubmission RecoverySubmission(
        ElsaControl.Deployment.Abstractions.Instances.ElsaInstanceOperationAction action =
            ElsaControl.Deployment.Abstractions.Instances.ElsaInstanceOperationAction.Create) =>
        new(
            WorkspaceId,
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            OperationId,
            2,
            ElsaControl.Deployment.Abstractions.Instances.ElsaDesiredLifecycle.Running,
            new ResolvedElsaApplicationPlan(
                ResolvedElsaApplicationPlanSchema.CurrentVersion,
                new(
                    "distribution",
                    "3.8",
                    "3.8.0",
                    "https://example.test/source",
                    "commit",
                    "https://example.test/release",
                    "sha256:" + new string('a', 64)),
                new("combined", []),
                [],
                new([]),
                new([], []),
                new("private", "restricted", false, [], []),
                "isolated",
                new("stable", "production", "standard", "automatic", "explicit", "explicit"),
                [],
                []),
            new(
                Guid.Parse("40000000-0000-0000-0000-000000000001"),
                Guid.Parse("50000000-0000-0000-0000-000000000001"),
                Guid.Parse("60000000-0000-0000-0000-000000000001"),
                Guid.Parse("70000000-0000-0000-0000-000000000001"),
                Guid.Parse("80000000-0000-0000-0000-000000000001"),
                Guid.Parse("90000000-0000-0000-0000-000000000001")),
            "westeurope",
            Guid.Parse("90000000-0000-0000-0000-000000000002"),
            action,
            "a0000000-0000-0000-0000-000000000001");

    private static ElsaInstanceCommercialGateDecision DeniedCommercialDecision() =>
        new(false, "commercial.denied", "The managed-instance operation is not entitled.");

    private static ElsaInstanceProviderRecoveryEnvelope RecoveryEnvelope(
        ElsaInstanceProviderSubmission submission) =>
        new(
            Guid.Parse("b0000000-0000-0000-0000-000000000001"),
            submission.OrganizationId!.Value,
            submission.WorkspaceId,
            submission.InstanceId,
            submission.OperationId,
            1,
            1,
            submission.AttemptNumber,
            2,
            "instances/operations",
            "recovery-key",
            new string('b', 64),
            ElsaInstanceProviderRecoveryObservationReference.Create(
                Guid.Parse("c0000000-0000-0000-0000-000000000001"),
                "sha256:" + new string('a', 64)),
            "sha256:" + new string('a', 64));

    private static ElsaInstanceProviderPendingOperation Pending(bool withSubmission) =>
        new(
            WorkspaceId,
            OperationId,
            withSubmission
                ? RecoverySubmission() with { AttemptNumber = 1 }
                : null);

    private sealed class PendingStore(IReadOnlyList<ElsaInstanceProviderPendingOperation> pending)
        : IElsaInstanceProviderPendingOperationStore
    {
        public Task<IReadOnlyList<ElsaInstanceProviderPendingOperation>> ListPendingProviderOperationsAsync(
            int limit,
            CancellationToken cancellationToken = default) => Task.FromResult(pending);
    }

    private sealed class SubmissionStore : IElsaInstanceProviderSubmissionStore
    {
        public List<ElsaInstanceProviderSubmissionCommit> Commits { get; } = [];

        public Task CommitProviderSubmissionAsync(
            ElsaInstanceProviderSubmissionCommit commit,
            CancellationToken cancellationToken = default)
        {
            Commits.Add(commit);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingSubmissionPort(Exception? failure = null) : IElsaInstanceProviderSubmissionPort
    {
        public int Calls { get; private set; }
        public OperationCanceledException? Cancellation { get; } = failure as OperationCanceledException;

        public Task<ElsaInstanceProviderSubmissionResult> SubmitAsync(
            ElsaInstanceProviderSubmission request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            if (failure is not null)
                return Task.FromException<ElsaInstanceProviderSubmissionResult>(failure);
            return Task.FromResult(new ElsaInstanceProviderSubmissionResult("provider-operation-1", false, "d0000000-0000-0000-0000-000000000001"));
        }
    }

    private sealed class RecordingRecoveryPort(
        Exception? failure = null,
        ElsaInstanceProviderRecoveryOutcome outcome = ElsaInstanceProviderRecoveryOutcome.Succeeded,
        string code = "provider.recovery.succeeded") : IElsaInstanceProviderRecoveryPort
    {
        public int Calls { get; private set; }
        public ElsaInstanceProviderRecoveryRequest? Request { get; private set; }

        public Task<ElsaInstanceProviderRecoveryResult> RecoverAsync(
            ElsaInstanceProviderRecoveryRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            Request = request;
            if (failure is not null)
                return Task.FromException<ElsaInstanceProviderRecoveryResult>(failure);
            return Task.FromResult(new ElsaInstanceProviderRecoveryResult(
                outcome,
                code));
        }
    }

    private sealed class RecordingCommercialGate(ElsaInstanceCommercialGateDecision decision) : IElsaInstanceCommercialGate
    {
        public int Calls { get; private set; }

        public Task<ElsaInstanceCommercialGateDecision> EvaluateAsync(
            Guid organizationId,
            ElsaControl.Deployment.Abstractions.Instances.ElsaInstanceOperationAction action,
            int? activeInstanceCount = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(decision);
        }
    }

    private sealed class RecordingEntitlementHoldStore(ElsaInstanceCommercialGateDecision decision) : IElsaInstanceEntitlementHoldStore
    {
        public int Calls { get; private set; }

        public Task<ElsaInstanceCommercialGateDecision> AuthorizeProviderSubmissionAsync(
            Guid workspaceId,
            Guid instanceId,
            Guid operationId,
            DateTimeOffset authorizedAt,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(decision);
        }
    }

    private sealed class RecordingReconciliationService : IElsaInstanceProviderReconciliationService
    {
        public int Calls { get; private set; }
        public int ClockScanCalls { get; private set; }
        public Exception? ClockScanFailure { get; init; }
        public List<(Guid WorkspaceId, Guid OperationId)> Requests { get; } = [];

        public Task<int> AdvanceDueHumanRequiredClocksAsync(CancellationToken cancellationToken = default)
        {
            ClockScanCalls++;
            if (ClockScanFailure is not null)
                return Task.FromException<int>(ClockScanFailure);
            return Task.FromResult(0);
        }

        public Task<ElsaInstanceProviderReconciliationResult> ReconcileAsync(
            Guid workspaceId,
            Guid operationId,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            Requests.Add((workspaceId, operationId));
            return Task.FromResult<ElsaInstanceProviderReconciliationResult>(null!);
        }
    }

    private sealed class StaticResolutionInputSource(ElsaInstanceLifecycleDeploymentTarget target)
        : IElsaInstanceLifecycleResolutionInputSource
    {
        public Task<ElsaInstanceLifecycleResolutionInput?> GetAsync(
            ElsaInstance instance,
            ElsaInstanceOperation operation,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ElsaInstanceLifecycleResolutionInput?>(ResolutionInput(instance, target));
    }

    private sealed class EmptyResolutionInputSource : IElsaInstanceLifecycleResolutionInputSource
    {
        public static EmptyResolutionInputSource Instance { get; } = new();

        public Task<ElsaInstanceLifecycleResolutionInput?> GetAsync(
            ElsaInstance instance,
            ElsaInstanceOperation operation,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ElsaInstanceLifecycleResolutionInput?>(null);
    }

    private sealed class StaticResolver(ElsaInstancePlanResolutionResult result) : IElsaInstancePlanResolver
    {
        public Task<ElsaInstancePlanResolutionResult> ResolveAsync(
            ElsaInstancePlanResolutionRequest request,
            CancellationToken cancellationToken = default) => Task.FromResult(result);
    }

    private sealed class FixedObservationPort(
        ElsaInstanceProviderObservationKind kind,
        string correlationId,
        ElsaInstanceProviderRetryEvidence? retryEvidence = null) : IElsaInstanceProviderReconciliationPort
    {
        public int Calls { get; private set; }

        public Task<ElsaInstanceProviderObservation> ObserveAsync(
            ElsaInstanceProviderReconciliationRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new ElsaInstanceProviderObservation(
                kind,
                kind == ElsaInstanceProviderObservationKind.Confirmed
                    ? ElsaObservedLifecycle.Ready
                    : ElsaObservedLifecycle.Unknown,
                kind == ElsaInstanceProviderObservationKind.Confirmed
                    ? ElsaInstanceProviderHealthGate.Passed
                    : ElsaInstanceProviderHealthGate.Unknown,
                request.OperationId,
                request.AttemptNumber,
                correlationId,
                retryEvidence));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>
    /// Test-only provider boundary for the durable EF recovery projection. The
    /// production Azure operation store owns these records; this fixture only
    /// supplies the already-correlated immutable observation needed to exercise
    /// the hosted hand-off across fresh DbContexts.
    /// </summary>
    private sealed class HostedRecoveryObservationStore : IAzureProviderRecoveryObservationStore
    {
        public const string Digest = "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
        public static readonly string Reference = ElsaInstanceProviderRecoveryObservationReference.Create(
            Guid.Parse("c0000000-0000-0000-0000-000000000001"), Digest);

        public Task<AzureProviderRecoveryObservationReceipt> CreateOrGetAsync(
            AzureProviderRecoveryObservationRecord observation,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The hosted integration fixture reads an existing observation only.");

        public Task<AzureProviderRecoveryObservationRecord?> GetAndValidateRecordedAsync(
            Guid organizationId,
            Guid workspaceId,
            Guid instanceId,
            Guid lifecycleOperationId,
            int observedLifecycleAttemptNumber,
            string reference,
            string digest,
            CancellationToken cancellationToken = default)
        {
            if (organizationId == Guid.Empty || workspaceId == Guid.Empty || instanceId == Guid.Empty ||
                lifecycleOperationId == Guid.Empty || observedLifecycleAttemptNumber < 1 ||
                !string.Equals(reference, Reference, StringComparison.Ordinal) ||
                !string.Equals(digest, Digest, StringComparison.Ordinal))
                return Task.FromResult<AzureProviderRecoveryObservationRecord?>(null);

            var observation = new AzureProviderRecoveryObservationRecord(
                organizationId,
                workspaceId,
                instanceId,
                lifecycleOperationId,
                ElsaInstanceOperationAction.Create,
                observedLifecycleAttemptNumber,
                // The EF acceptance boundary is called after the reconciliation
                // projection has advanced the aggregate once; the ledger records
                // the immediately preceding provider observation version.
                ObservedInstanceVersion: 2,
                Guid.Parse("c1000000-0000-0000-0000-000000000001"),
                "provider-operation-accepted",
                new string('d', 64),
                1,
                1,
                1,
                Guid.Parse("c2000000-0000-0000-0000-000000000001"),
                "hosted-recovery-ef",
                null,
                "plan_worker_01",
                1,
                "https://control.example.test/api/recovery/plan_worker_01",
                "sha256:eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee",
                new string('f', 64),
                new string('a', 64),
                AzureProviderRunnerStep.Foundation,
                AzureProviderOperationPhase.FoundationObserved,
                AzureProviderHealth.Unknown,
                new string('b', 64),
                new string('c', 64),
                Now);
            observation.Validate();
            return Task.FromResult<AzureProviderRecoveryObservationRecord?>(observation);
        }

        public Task<AzureProviderRecoveryObservationRecord?> GetAndValidateForAcceptedRecoveryAsync(
            AzureProviderRecoveryObservationBinding binding,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The hosted integration fixture does not consume post-acceptance proofs.");

        public Task<AzureProviderRecoveryObservationRecord?> GetAndValidateForAcceptedRecoveryReplayAsync(
            AzureProviderRecoveryObservationBinding binding,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The hosted integration fixture does not consume post-claim proofs.");
    }
}
