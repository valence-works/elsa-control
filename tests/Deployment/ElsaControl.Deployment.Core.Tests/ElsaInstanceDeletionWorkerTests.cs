using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Core.Instances;
using Xunit;

namespace ElsaControl.Deployment.Core.Tests;

public sealed class ElsaInstanceDeletionWorkerTests
{
    private static readonly Guid WorkspaceId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Pending_instance_without_owned_resources_finalizes_locally()
    {
        var store = new RecordingStore(WorkItem(local: true));
        var port = new RecordingPort(new(ElsaInstanceCleanupObservationKind.Unavailable,
            store.Item.Operation.Id, 1, "deletion.provider.unavailable"));

        var result = await new ElsaInstanceDeletionWorker(store, port, new FixedTimeProvider(Now))
            .ProcessAvailableAsync("delete-worker");

        Assert.Equal(0, result.ProviderInvocations);
        Assert.Equal(ElsaObservedLifecycle.Deleted, store.Commit!.Instance.ObservedLifecycle);
        Assert.Equal(Now, store.Commit.Instance.DeletedAt);
        Assert.Equal("deletion.local.absent", store.Commit.DiagnosticCode);
        Assert.Null(store.Failure);
    }

    [Fact]
    public async Task Correlated_confirmed_absence_finalizes_and_retains_only_safe_evidence()
    {
        var item = WorkItem(local: false);
        var evidence = new ElsaInstanceCleanupEvidence("https://evidence.example/deletions/proof", Digest('a'));
        var store = new RecordingStore(item);
        var port = new RecordingPort(new(ElsaInstanceCleanupObservationKind.ConfirmedAbsent,
            item.Operation.Id, item.Operation.AttemptNumber, "deletion.provider.absent", evidence));

        var result = await new ElsaInstanceDeletionWorker(store, port, new FixedTimeProvider(Now))
            .ProcessAvailableAsync("delete-worker");

        Assert.Equal(1, result.ProviderInvocations);
        Assert.Equal(evidence.Reference, store.Commit!.EvidenceReference);
        Assert.Equal(evidence.Digest, store.Commit.EvidenceDigest);
        Assert.Equal(64, store.Commit.EvidenceFingerprint.Length);
        Assert.DoesNotContain("proof", store.Commit.EvidenceFingerprint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Confirmed_absence_finalizes_retained_delete_with_stale_ready_observation()
    {
        var item = WorkItem(local: false, observedLifecycle: ElsaObservedLifecycle.Ready);
        var store = new RecordingStore(item);
        var port = new RecordingPort(new(ElsaInstanceCleanupObservationKind.ConfirmedAbsent,
            item.Operation.Id, item.Operation.AttemptNumber, "deletion.provider-confirmed-absent"));

        var result = await new ElsaInstanceDeletionWorker(store, port, new FixedTimeProvider(Now))
            .ProcessAvailableAsync("delete-worker");

        Assert.Equal(1, result.ProviderInvocations);
        Assert.Equal(ElsaInstanceLifecycleWorkerOutcome.Deleted, Assert.Single(result.Results).Outcome);
        Assert.Equal(ElsaObservedLifecycle.Deleted, store.Commit!.Instance.ObservedLifecycle);
        Assert.Null(store.Failure);
    }

    [Fact]
    public async Task Stale_ready_observation_without_absence_proof_does_not_finalize()
    {
        var item = WorkItem(local: false, observedLifecycle: ElsaObservedLifecycle.Ready);
        var store = new RecordingStore(item);
        var port = new RecordingPort(new(ElsaInstanceCleanupObservationKind.Unknown,
            item.Operation.Id, item.Operation.AttemptNumber, "deletion.provider.unknown"));

        await new ElsaInstanceDeletionWorker(store, port, new FixedTimeProvider(Now))
            .ProcessAvailableAsync("delete-worker");

        Assert.Null(store.Commit);
        Assert.Equal("deletion.provider.unknown", store.Failure!.DiagnosticCode);
    }

    [Fact]
    public async Task In_progress_cleanup_is_deferred_then_same_delete_completes_on_next_poll()
    {
        var item = WorkItem(local: false);
        var store = new DeferredStore(item, Now);
        var port = new RecordingPort(
            new(ElsaInstanceCleanupObservationKind.InProgress, item.Operation.Id,
                item.Operation.AttemptNumber, "deletion.provider-cleanup-pending"),
            new ElsaInstanceCleanupObservation(ElsaInstanceCleanupObservationKind.ConfirmedAbsent, item.Operation.Id,
                item.Operation.AttemptNumber, "deletion.provider-confirmed-absent"));

        var first = await new ElsaInstanceDeletionWorker(store, port, new FixedTimeProvider(Now))
            .ProcessAvailableAsync("delete-worker");

        Assert.Empty(first.Results);
        Assert.Equal(1, first.ProviderInvocations);
        Assert.Equal(1, store.Deferrals);
        Assert.Null(store.Failure);
        Assert.Equal("deletion.provider-cleanup-pending", store.DeferredDiagnosticCode);
        Assert.Equal(2, store.Claims);

        var second = await new ElsaInstanceDeletionWorker(store, port, new FixedTimeProvider(Now.AddMinutes(1)))
            .ProcessAvailableAsync("delete-worker");

        Assert.Single(second.Results);
        Assert.Equal(ElsaInstanceLifecycleWorkerOutcome.Deleted, second.Results[0].Outcome);
        Assert.Equal(1, store.CommitAttempts);
        Assert.Equal(1, second.ProviderInvocations);
        Assert.Equal(4, store.Claims);
    }

    [Theory]
    [InlineData(ElsaInstanceCleanupObservationKind.Unknown)]
    [InlineData(ElsaInstanceCleanupObservationKind.Ambiguous)]
    [InlineData(ElsaInstanceCleanupObservationKind.Unavailable)]
    [InlineData(ElsaInstanceCleanupObservationKind.UnsupportedCancellation)]
    public async Task Uncertain_cleanup_never_projects_deleted(ElsaInstanceCleanupObservationKind kind)
    {
        var item = WorkItem(local: false);
        var store = new RecordingStore(item);
        var port = new RecordingPort(new(kind, item.Operation.Id, item.Operation.AttemptNumber, "deletion.provider.uncertain"));

        await new ElsaInstanceDeletionWorker(store, port, new FixedTimeProvider(Now))
            .ProcessAvailableAsync("delete-worker");

        Assert.Null(store.Commit);
        Assert.NotNull(store.Failure);
        Assert.Equal(ElsaObservedLifecycle.Deleting, item.Instance.ObservedLifecycle);
    }

    [Fact]
    public async Task Mismatched_provider_correlation_fails_closed()
    {
        var item = WorkItem(local: false);
        var store = new RecordingStore(item);
        var port = new RecordingPort(new(ElsaInstanceCleanupObservationKind.ConfirmedAbsent,
            Guid.NewGuid(), item.Operation.AttemptNumber, "deletion.provider.absent"));

        await new ElsaInstanceDeletionWorker(store, port, new FixedTimeProvider(Now))
            .ProcessAvailableAsync("delete-worker");

        Assert.Null(store.Commit);
        Assert.Equal("deletion.correlation.invalid", store.Failure!.DiagnosticCode);
    }

    [Fact]
    public async Task Mismatched_in_progress_correlation_requires_recovery_without_deferral()
    {
        var item = WorkItem(local: false);
        var store = new RecordingStore(item);
        var port = new RecordingPort(new ElsaInstanceCleanupObservation(
            ElsaInstanceCleanupObservationKind.InProgress, Guid.NewGuid(),
            item.Operation.AttemptNumber, "deletion.provider-cleanup-pending"));

        await new ElsaInstanceDeletionWorker(store, port, new FixedTimeProvider(Now))
            .ProcessAvailableAsync("delete-worker");

        Assert.Equal(0, store.Deferrals);
        Assert.Null(store.Commit);
        Assert.Equal("deletion.correlation.invalid", store.Failure!.DiagnosticCode);
    }

    [Fact]
    public async Task Lost_deletion_lease_during_in_progress_deferral_reports_conflict_without_recovery()
    {
        var item = WorkItem(local: false);
        var store = new RecordingStore(item);
        var port = new RecordingPort(new ElsaInstanceCleanupObservation(
            ElsaInstanceCleanupObservationKind.InProgress, item.Operation.Id,
            item.Operation.AttemptNumber, "deletion.provider-cleanup-pending"));

        var result = await new ElsaInstanceDeletionWorker(store, port, new FixedTimeProvider(Now))
            .ProcessAvailableAsync("delete-worker");

        Assert.Single(result.Results);
        Assert.Equal(ElsaInstanceLifecycleWorkerOutcome.Conflict, result.Results[0].Outcome);
        Assert.Equal(1, store.Deferrals);
        Assert.Null(store.Commit);
        Assert.Null(store.Failure);
    }

    [Fact]
    public async Task Unknown_without_persisted_targets_requires_positive_provider_absence()
    {
        var item = WorkItem(local: false, observedLifecycle: ElsaObservedLifecycle.Unknown, includeDeployment: false);
        var store = new RecordingStore(item);
        var port = new RecordingPort(new(ElsaInstanceCleanupObservationKind.Unknown,
            item.Operation.Id, item.Operation.AttemptNumber, "deletion.provider.unknown"));

        var result = await new ElsaInstanceDeletionWorker(store, port, new FixedTimeProvider(Now))
            .ProcessAvailableAsync("delete-worker");

        Assert.Equal(1, result.ProviderInvocations);
        Assert.Null(store.Commit);
        Assert.NotNull(store.Failure);
    }

    [Fact]
    public async Task Runtime_tenant_reference_is_forwarded_to_cleanup_and_prevents_local_finalization()
    {
        var tenant = new ElsaTenantReference("tenant-safe", "urn:elsa:instance:tenant-safe");
        var item = WorkItem(local: false, includeDeployment: false, tenant: tenant);
        var store = new RecordingStore(item);
        var port = new RecordingPort(new(ElsaInstanceCleanupObservationKind.ConfirmedAbsent,
            item.Operation.Id, item.Operation.AttemptNumber, "deletion.provider.absent"));

        await new ElsaInstanceDeletionWorker(store, port, new FixedTimeProvider(Now))
            .ProcessAvailableAsync("delete-worker");

        Assert.Equal(tenant, port.Request!.Tenant);
        Assert.NotNull(store.Commit);
    }

    [Fact]
    public async Task Commit_conflict_is_isolated_and_later_deletion_work_continues()
    {
        var first = WorkItem(local: true);
        var second = WorkItem(local: true);
        var store = new ConflictThenSuccessStore(first, second);
        var port = new RecordingPort(new(ElsaInstanceCleanupObservationKind.Unavailable,
            first.Operation.Id, first.Operation.AttemptNumber, "deletion.provider.unavailable"));

        var result = await new ElsaInstanceDeletionWorker(store, port, new FixedTimeProvider(Now))
            .ProcessAvailableAsync("delete-worker");

        Assert.Equal(2, result.Results.Count);
        Assert.Equal(ElsaInstanceLifecycleWorkerOutcome.Conflict, result.Results[0].Outcome);
        Assert.Equal(ElsaInstanceLifecycleWorkerOutcome.Deleted, result.Results[1].Outcome);
        Assert.Equal(second.Operation.Id, result.Results[1].Operation.Id);
        Assert.Equal(2, store.CommitAttempts);
    }

    [Fact]
    public async Task Structurally_corrupt_item_is_skipped_and_later_work_continues()
    {
        var corrupt = WorkItem(local: true) with { Operation = null! };
        var valid = WorkItem(local: true);
        var store = new ConflictThenSuccessStore(false, corrupt, valid);
        var port = new RecordingPort(new(ElsaInstanceCleanupObservationKind.Unavailable,
            valid.Operation.Id, valid.Operation.AttemptNumber, "deletion.provider.unavailable"));

        var result = await new ElsaInstanceDeletionWorker(store, port, new FixedTimeProvider(Now))
            .ProcessAvailableAsync("delete-worker");

        Assert.Single(result.Results);
        Assert.Equal(valid.Operation.Id, result.Results[0].Operation.Id);
        Assert.Equal(ElsaInstanceLifecycleWorkerOutcome.Deleted, result.Results[0].Outcome);
    }

    [Fact]
    public async Task Cancellation_is_checked_before_claiming_another_item()
    {
        using var cancellation = new CancellationTokenSource();
        var item = WorkItem(local: true);
        var store = new CancellingStore(item, cancellation);
        var port = new RecordingPort(new(ElsaInstanceCleanupObservationKind.Unavailable,
            item.Operation.Id, item.Operation.AttemptNumber, "deletion.provider.unavailable"));

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new ElsaInstanceDeletionWorker(store, port, new FixedTimeProvider(Now))
                .ProcessAvailableAsync("delete-worker", cancellation.Token));

        Assert.Equal(1, store.Claims);
    }

    [Theory]
    [InlineData("https://user:secret@evidence.example/proof")]
    [InlineData("https://evidence.example/proof?token=secret")]
    [InlineData("file:///tmp/proof")]
    public void Cleanup_evidence_rejects_unsafe_references(string reference) =>
        Assert.Throws<ArgumentException>(() => new ElsaInstanceCleanupEvidence(reference, Digest('a')));

    [Fact]
    public void Cleanup_evidence_requires_strict_lowercase_sha256() =>
        Assert.Throws<ArgumentException>(() => new ElsaInstanceCleanupEvidence(
            "https://evidence.example/proof", "sha256:" + new string('A', 64)));

    [Fact]
    public void Cleanup_request_accepts_partial_persisted_targets()
    {
        var request = new ElsaInstanceCleanupRequest(WorkspaceId, Guid.NewGuid(), Guid.NewGuid(), 1,
            new ElsaCurrentDeploymentReference("deployment-safe"), null, null);

        request.Validate();
    }

    private static ElsaInstanceDeletionWorkItem WorkItem(
        bool local,
        ElsaObservedLifecycle observedLifecycle = ElsaObservedLifecycle.Deleting,
        bool includeDeployment = true,
        ElsaTenantReference? tenant = null)
    {
        var instanceId = Guid.NewGuid();
        var intent = Intent() with { DesiredLifecycle = ElsaDesiredLifecycle.Deleting };
        var instance = ElsaInstance.Hydrate(instanceId, Guid.NewGuid(), WorkspaceId, "Delete", "delete", intent,
            observedLifecycle, ElsaInstanceHealth.Unknown, 2,
            currentDeploymentReference: !local && includeDeployment ? new("deployment-safe") : null,
            placementAssignmentReference: !local && includeDeployment ? new("placement-safe") : null,
            elsaTenantReference: tenant);
        var operation = ElsaInstanceOperation.Create(instanceId, ElsaInstanceOperationAction.Delete,
            $"instance/{instanceId:D}/Delete", "delete-key", new string('a', 64), 1);
        var outbox = new ElsaInstanceLifecycleOutboxMessage(Guid.NewGuid(), WorkspaceId, instanceId,
            operation.Id, ElsaInstanceOperationAction.Delete, operation.RequestHash, Now.AddMinutes(-1));
        return new(outbox, operation, instance, local, local ? null : Guid.NewGuid(),
            new string('b', 64), 1);
    }

    private static ElsaInstanceIntent Intent() => new(
        new ElsaReleaseIntent("runtime", "3.8", null, "stable", "automatic", "approval", "migration"),
        new ElsaApplicationIntent("combined", "starter", new Dictionary<string, ElsaFeatureOverride>(), "approved"),
        new ElsaPlacementIntent("managed", "westeurope", "dedicated", "small", "public", "managed"));

    private static string Digest(char value) => "sha256:" + new string(value, 64);

    private sealed class RecordingPort(
        ElsaInstanceCleanupObservation first,
        params ElsaInstanceCleanupObservation[] additional) : IElsaInstanceProviderCleanupPort
    {
        private readonly Queue<ElsaInstanceCleanupObservation> _observations =
            new Queue<ElsaInstanceCleanupObservation>(new[] { first }.Concat(additional));

        public ElsaInstanceCleanupRequest? Request { get; private set; }

        public Task<ElsaInstanceCleanupObservation> CleanupAsync(ElsaInstanceCleanupRequest request,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            return Task.FromResult(_observations.Dequeue());
        }
    }

    private sealed class RecordingStore(ElsaInstanceDeletionWorkItem item) : IElsaInstanceDeletionStore
    {
        private bool _claimed;
        public ElsaInstanceDeletionWorkItem Item => item;
        public ElsaInstanceDeletionCommit? Commit { get; private set; }
        public ElsaInstanceDeletionFailure? Failure { get; private set; }
        public int Deferrals { get; private set; }

        public Task<ElsaInstanceDeletionWorkItem?> TryClaimNextDeletionAsync(string workerId, DateTimeOffset now,
            CancellationToken cancellationToken = default)
        {
            if (_claimed) return Task.FromResult<ElsaInstanceDeletionWorkItem?>(null);
            _claimed = true;
            return Task.FromResult<ElsaInstanceDeletionWorkItem?>(item);
        }

        public Task<ElsaInstanceDeletionResult> CommitDeletionAsync(ElsaInstanceDeletionCommit commit,
            CancellationToken cancellationToken = default)
        {
            commit.Validate();
            Commit = commit;
            return Task.FromResult(new ElsaInstanceDeletionResult(ElsaInstanceDeletionOutcome.Deleted,
                commit.Operation, commit.Instance, commit.DiagnosticCode, commit.EvidenceFingerprint, false));
        }

        public Task<bool> RenewDeletionLeaseAsync(ElsaInstanceDeletionWorkItem item, string workerId, DateTimeOffset now,
            CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<bool> DeferDeletionAsync(ElsaInstanceDeletionWorkItem item, string workerId, DateTimeOffset now,
            string diagnosticCode, CancellationToken cancellationToken = default)
        {
            Deferrals++;
            return Task.FromResult(false);
        }

        public Task<ElsaInstanceDeletionResult> RequireDeletionRecoveryAsync(ElsaInstanceDeletionFailure failure,
            CancellationToken cancellationToken = default)
        {
            failure.Validate();
            Failure = failure;
            var recovery = item.Operation.TransitionTo(ElsaInstanceOperationState.Queued)
                .TransitionTo(ElsaInstanceOperationState.RecoveryRequired);
            return Task.FromResult(new ElsaInstanceDeletionResult(ElsaInstanceDeletionOutcome.RecoveryRequired,
                recovery, item.Instance, failure.DiagnosticCode, failure.EvidenceFingerprint, false));
        }
    }

    private sealed class ConflictThenSuccessStore : IElsaInstanceDeletionStore
    {
        private readonly Queue<ElsaInstanceDeletionWorkItem> _items;
        private readonly bool _conflictFirst;

        public ConflictThenSuccessStore(params ElsaInstanceDeletionWorkItem[] items) : this(true, items) { }

        public ConflictThenSuccessStore(bool conflictFirst, params ElsaInstanceDeletionWorkItem[] items)
        {
            _conflictFirst = conflictFirst;
            _items = new(items);
        }

        public int CommitAttempts { get; private set; }

        public Task<ElsaInstanceDeletionWorkItem?> TryClaimNextDeletionAsync(string workerId, DateTimeOffset now,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_items.Count == 0 ? null : _items.Dequeue());

        public Task<ElsaInstanceDeletionResult> CommitDeletionAsync(ElsaInstanceDeletionCommit commit,
            CancellationToken cancellationToken = default)
        {
            CommitAttempts++;
            if (_conflictFirst && CommitAttempts == 1)
                throw new ElsaInstanceLifecycleConflictException("Lease changed.");
            return Task.FromResult(new ElsaInstanceDeletionResult(ElsaInstanceDeletionOutcome.Deleted,
                commit.Operation, commit.Instance, commit.DiagnosticCode, commit.EvidenceFingerprint, false));
        }

        public Task<bool> RenewDeletionLeaseAsync(ElsaInstanceDeletionWorkItem item, string workerId, DateTimeOffset now,
            CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<bool> DeferDeletionAsync(ElsaInstanceDeletionWorkItem item, string workerId, DateTimeOffset now,
            string diagnosticCode, CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task<ElsaInstanceDeletionResult> RequireDeletionRecoveryAsync(ElsaInstanceDeletionFailure failure,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class CancellingStore(
        ElsaInstanceDeletionWorkItem item,
        CancellationTokenSource cancellation) : IElsaInstanceDeletionStore
    {
        public int Claims { get; private set; }

        public Task<ElsaInstanceDeletionWorkItem?> TryClaimNextDeletionAsync(string workerId, DateTimeOffset now,
            CancellationToken cancellationToken = default)
        {
            Claims++;
            cancellation.Cancel();
            return Task.FromResult<ElsaInstanceDeletionWorkItem?>(item);
        }

        public Task<ElsaInstanceDeletionResult> CommitDeletionAsync(ElsaInstanceDeletionCommit commit,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ElsaInstanceDeletionResult(ElsaInstanceDeletionOutcome.Deleted,
                commit.Operation, commit.Instance, commit.DiagnosticCode, commit.EvidenceFingerprint, false));

        public Task<bool> RenewDeletionLeaseAsync(ElsaInstanceDeletionWorkItem item, string workerId, DateTimeOffset now,
            CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<bool> DeferDeletionAsync(ElsaInstanceDeletionWorkItem item, string workerId, DateTimeOffset now,
            string diagnosticCode, CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task<ElsaInstanceDeletionResult> RequireDeletionRecoveryAsync(ElsaInstanceDeletionFailure failure,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class DeferredStore(ElsaInstanceDeletionWorkItem item, DateTimeOffset initialDueAt)
        : IElsaInstanceDeletionStore
    {
        private DateTimeOffset _dueAt = initialDueAt;
        private bool _completed;

        public int Claims { get; private set; }
        public int Deferrals { get; private set; }
        public int CommitAttempts { get; private set; }
        public string? DeferredDiagnosticCode { get; private set; }
        public ElsaInstanceDeletionFailure? Failure { get; private set; }

        public Task<ElsaInstanceDeletionWorkItem?> TryClaimNextDeletionAsync(string workerId, DateTimeOffset now,
            CancellationToken cancellationToken = default)
        {
            Claims++;
            if (_completed || now < _dueAt)
                return Task.FromResult<ElsaInstanceDeletionWorkItem?>(null);
            return Task.FromResult<ElsaInstanceDeletionWorkItem?>(item);
        }

        public Task<bool> DeferDeletionAsync(ElsaInstanceDeletionWorkItem item, string workerId, DateTimeOffset now,
            string diagnosticCode, CancellationToken cancellationToken = default)
        {
            Deferrals++;
            DeferredDiagnosticCode = diagnosticCode;
            _dueAt = now.AddMinutes(1);
            return Task.FromResult(true);
        }

        public Task<bool> RenewDeletionLeaseAsync(ElsaInstanceDeletionWorkItem item, string workerId, DateTimeOffset now,
            CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<ElsaInstanceDeletionResult> CommitDeletionAsync(ElsaInstanceDeletionCommit commit,
            CancellationToken cancellationToken = default)
        {
            CommitAttempts++;
            _completed = true;
            return Task.FromResult(new ElsaInstanceDeletionResult(ElsaInstanceDeletionOutcome.Deleted,
                commit.Operation, commit.Instance, commit.DiagnosticCode, commit.EvidenceFingerprint, false));
        }

        public Task<ElsaInstanceDeletionResult> RequireDeletionRecoveryAsync(ElsaInstanceDeletionFailure failure,
            CancellationToken cancellationToken = default)
        {
            Failure = failure;
            return Task.FromResult(new ElsaInstanceDeletionResult(ElsaInstanceDeletionOutcome.RecoveryRequired,
                item.Operation.TransitionTo(ElsaInstanceOperationState.Queued)
                    .TransitionTo(ElsaInstanceOperationState.RecoveryRequired), item.Instance,
                failure.DiagnosticCode, failure.EvidenceFingerprint, false));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
