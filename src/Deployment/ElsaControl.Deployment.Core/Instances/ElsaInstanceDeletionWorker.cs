using ElsaControl.Deployment.Abstractions.Instances;
using System.Security.Cryptography;
using System.Text;

namespace ElsaControl.Deployment.Core.Instances;

public sealed class ElsaInstanceDeletionWorker(
    IElsaInstanceDeletionStore store,
    IElsaInstanceProviderCleanupPort cleanupPort,
    TimeProvider? timeProvider = null,
    IElsaInstanceProviderDeleteRecoveryPort? deleteRecoveryPort = null)
{
    /// <summary>
    /// Escalate only when verified provider progress has been silent.
    /// QA's ~50-minute Delete (#508) was wall-clock Azure cleanup, including
    /// Log Analytics workspace deletion. The cleanup runner does not persist
    /// live ARM remaining-resource lists during that wait, so Status, Phase,
    /// and the durable persisted-resource receipt can stay unchanged until
    /// CleanupVerified. A 30-minute bound would park that healthy path. 60
    /// minutes covers the observed 50-minute window with margin. Heartbeats,
    /// CreatedAt, restamped ARM reads, and metadata-only checkpoints never
    /// reset this clock.
    /// </summary>
    public static readonly TimeSpan ProviderProgressStaleAfter = TimeSpan.FromMinutes(60);

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly IElsaInstanceProviderDeleteRecoveryPort? _deleteRecoveryPort = deleteRecoveryPort;

    public async Task<ElsaInstanceLifecycleWorkerBatchResult> ProcessAvailableAsync(string workerId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workerId))
            throw new ArgumentException("Deletion worker identity is required.", nameof(workerId));
        var results = new List<ElsaInstanceLifecycleWorkerResult>();
        var providerInvocations = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = await store.TryClaimNextDeletionAsync(workerId.Trim(), _timeProvider.GetUtcNow(), cancellationToken);
            if (item is null)
                break;
            try
            {
                var processed = await ProcessClaimedAsync(
                    item, workerId.Trim(), () => providerInvocations++, cancellationToken);
                if (processed is not null)
                    results.Add(Map(processed));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (ElsaInstanceLifecycleConflictException)
            {
                if (TryConflict(item) is { } conflict)
                    results.Add(conflict);
            }
            catch (Exception)
            {
                var failure = TryInvalidFailure(item, workerId.Trim());
                if (failure is null)
                    continue;
                try
                {
                    results.Add(Map(await store.RequireDeletionRecoveryAsync(failure, cancellationToken)));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception)
                {
                    if (TryConflict(item) is { } conflict)
                        results.Add(conflict);
                }
            }
        }
        return new(results, providerInvocations);
    }

    private async Task<ElsaInstanceDeletionResult?> ProcessClaimedAsync(
        ElsaInstanceDeletionWorkItem item,
        string workerId,
        Action providerInvoked,
        CancellationToken cancellationToken)
    {
        item.Validate();
        ElsaInstanceCleanupObservation observation;
        if (item.CanFinalizeLocally)
        {
            observation = new(ElsaInstanceCleanupObservationKind.ConfirmedAbsent, item.Operation.Id,
                item.Operation.AttemptNumber, ElsaInstanceDeletionDiagnosticCodes.LocalAbsent);
        }
        else
        {
            var request = new ElsaInstanceCleanupRequest(item.Instance.WorkspaceId, item.Instance.Id,
                item.Operation.Id, item.Operation.AttemptNumber, item.Instance.CurrentDeploymentReference,
                item.Instance.PlacementAssignmentReference, item.Instance.ElsaTenantReference);
            request.Validate();
            try
            {
                if (item.RecoveryRequestId is not null && _deleteRecoveryPort is null)
                    throw new InvalidOperationException("The accepted Azure delete recovery capability is unavailable.");
                providerInvoked();
                if (item.RecoveryRequestId is { } recoveryRequestId)
                {
                    observation = await RecoverDeleteWithLeaseAsync(
                        item,
                        workerId,
                        new ElsaInstanceDeleteRecoveryRequest(
                            request,
                            recoveryRequestId,
                            item.Instance.Version,
                            workerId,
                            item.LeaseToken,
                            item.LeaseVersion),
                        cancellationToken);
                }
                else
                    observation = await CleanupWithLeaseAsync(item, workerId, request, cancellationToken);
                observation.Validate();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception)
            {
                observation = new(ElsaInstanceCleanupObservationKind.Unavailable, item.Operation.Id,
                    item.Operation.AttemptNumber, ElsaInstanceDeletionDiagnosticCodes.ProviderUnavailable);
            }
        }

        var correlated = observation.OperationId == item.Operation.Id &&
                         observation.AttemptNumber == item.Operation.AttemptNumber;
        var fingerprint = observation.ComputeFingerprint();
        if (correlated && observation.Kind == ElsaInstanceCleanupObservationKind.InProgress)
        {
            var now = _timeProvider.GetUtcNow();
            var progressing = ApplyVerifiedProgress(item, observation, now);
            if (HasStaleProviderProgress(progressing, now))
            {
                return await store.RequireDeletionRecoveryAsync(new(
                    item.Instance.WorkspaceId, item.Instance.Id, item.Operation.Id, item.Outbox.Id,
                    item.Instance.Version, item.Operation.AttemptNumber, item.CorrelatedRunId, workerId,
                    item.LeaseToken, item.LeaseVersion, fingerprint,
                    StaleProgressCode(observation), now), cancellationToken);
            }

            if (!await store.DeferDeletionAsync(progressing, workerId, now,
                    observation.DiagnosticCode, cancellationToken))
                throw new ElsaInstanceLifecycleConflictException(
                    "Deletion work item changed before async cleanup could be deferred.");
            return null;
        }
        if (correlated && observation.Kind == ElsaInstanceCleanupObservationKind.ConfirmedAbsent)
        {
            var deletedAt = _timeProvider.GetUtcNow();
            var result = await store.CommitDeletionAsync(new(
                item.Instance.WorkspaceId, item.Instance.Id, item.Operation.Id, item.Outbox.Id,
                item.Instance.Version, item.Operation.AttemptNumber, item.CorrelatedRunId, workerId,
                item.LeaseToken, item.LeaseVersion, fingerprint,
                item.CanFinalizeLocally ? ElsaInstanceDeletionProofKind.LocalNoOwnedResources :
                    ElsaInstanceDeletionProofKind.ProviderConfirmedAbsent,
                observation.DiagnosticCode,
                observation.Evidence?.Reference, observation.Evidence?.Digest,
                ElsaInstanceStateMachine.FinalizeDeletion(item.Instance, deletedAt),
                item.Operation.TransitionTo(ElsaInstanceOperationState.Succeeded), deletedAt), cancellationToken);
            return result;
        }

        var code = correlated ? observation.DiagnosticCode : ElsaInstanceDeletionDiagnosticCodes.CorrelationInvalid;
        return await store.RequireDeletionRecoveryAsync(new(
            item.Instance.WorkspaceId, item.Instance.Id, item.Operation.Id, item.Outbox.Id,
            item.Instance.Version, item.Operation.AttemptNumber, item.CorrelatedRunId, workerId,
            item.LeaseToken, item.LeaseVersion, fingerprint, code, _timeProvider.GetUtcNow()), cancellationToken);
    }

    private ElsaInstanceDeletionFailure? TryInvalidFailure(ElsaInstanceDeletionWorkItem item, string workerId)
    {
        if (item.Outbox is null || item.Operation is null || item.Instance is null ||
            item.Outbox.Id == Guid.Empty || item.Operation.Id == Guid.Empty || item.Instance.Id == Guid.Empty ||
            item.Instance.WorkspaceId == Guid.Empty || string.IsNullOrWhiteSpace(item.LeaseToken) || item.LeaseVersion < 1)
            return null;
        var canonical = $"{ElsaInstanceDeletionDiagnosticCodes.ItemInvalid}\n{item.Outbox.Id:D}\n{item.Operation.Id:D}\n{item.Instance.Id:D}\n";
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        return new(item.Instance.WorkspaceId, item.Instance.Id, item.Operation.Id, item.Outbox.Id,
            Math.Max(1, item.Instance.Version), Math.Max(1, item.Operation.AttemptNumber), item.CorrelatedRunId,
            workerId, item.LeaseToken, Math.Max(1, item.LeaseVersion), fingerprint,
            ElsaInstanceDeletionDiagnosticCodes.ItemInvalid, _timeProvider.GetUtcNow());
    }

    private async Task<ElsaInstanceCleanupObservation> CleanupWithLeaseAsync(
        ElsaInstanceDeletionWorkItem item,
        string workerId,
        ElsaInstanceCleanupRequest request,
        CancellationToken cancellationToken)
    {
        return await RunProviderWithLeaseAsync(
            item,
            workerId,
            token => cleanupPort.CleanupAsync(request, token),
            cancellationToken);
    }

    private async Task<ElsaInstanceCleanupObservation> RunProviderWithLeaseAsync(
        ElsaInstanceDeletionWorkItem item,
        string workerId,
        Func<CancellationToken, Task<ElsaInstanceCleanupObservation>> providerCall,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(providerCall);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var cleanup = providerCall(cancellation.Token);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), _timeProvider);
        while (await Task.WhenAny(cleanup, timer.WaitForNextTickAsync(cancellationToken).AsTask()) != cleanup)
        {
            if (!await store.RenewDeletionLeaseAsync(item, workerId, _timeProvider.GetUtcNow(), cancellationToken))
            {
                cancellation.Cancel();
                if (!cleanup.IsCompleted)
                    _ = cleanup.ContinueWith(static task => _ = task.Exception,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
                throw new InvalidOperationException("Deletion lease could not be renewed.");
            }
        }
        return await cleanup;
    }

    private async Task<ElsaInstanceCleanupObservation> RecoverDeleteWithLeaseAsync(
        ElsaInstanceDeletionWorkItem item,
        string workerId,
        ElsaInstanceDeleteRecoveryRequest request,
        CancellationToken cancellationToken)
    {
        request.Validate();
        return await RunProviderWithLeaseAsync(
            item,
            workerId,
            token => _deleteRecoveryPort!.RecoverDeleteAsync(request, token),
            cancellationToken);
    }

    private static ElsaInstanceLifecycleWorkerResult Map(ElsaInstanceDeletionResult result) => new(
        result.Outcome switch
        {
            ElsaInstanceDeletionOutcome.Deleted => ElsaInstanceLifecycleWorkerOutcome.Deleted,
            ElsaInstanceDeletionOutcome.AlreadyCompleted => ElsaInstanceLifecycleWorkerOutcome.AlreadyCompleted,
            ElsaInstanceDeletionOutcome.RecoveryRequired => ElsaInstanceLifecycleWorkerOutcome.Failed,
            _ => ElsaInstanceLifecycleWorkerOutcome.Conflict
        }, result.Operation, result.Instance, FailureCode: result.DiagnosticCode);

    public static ElsaInstanceDeletionWorkItem ApplyVerifiedProgress(
        ElsaInstanceDeletionWorkItem item,
        ElsaInstanceCleanupObservation observation,
        DateTimeOffset now)
    {
        var lastVerified = item.LastVerifiedProgressAt;
        var receipt = item.LastVerifiedProgressReceipt;
        var nowUtc = now.ToUniversalTime();
        DateTimeOffset? candidate = null;

        if (!string.IsNullOrWhiteSpace(observation.ProgressReceipt) &&
            !string.Equals(observation.ProgressReceipt, receipt, StringComparison.Ordinal))
        {
            // A differing receipt is verified progress at the observation time.
            // Do not adopt an older or future Status/Phase stamp from the same
            // observation: that would rewind the monotonic clock, treat a real
            // inventory change as already stale, or extend the bound by skew.
            receipt = observation.ProgressReceipt;
            candidate = nowUtc;
        }
        else if (observation.LastProviderProgressAt is { } progress)
        {
            var progressUtc = progress.ToUniversalTime();
            if (progressUtc <= nowUtc)
                candidate = progressUtc;
        }

        if (candidate is { } verified &&
            (lastVerified is null || verified > lastVerified.Value.ToUniversalTime()))
            lastVerified = verified;

        return item with
        {
            LastVerifiedProgressAt = lastVerified,
            LastVerifiedProgressReceipt = receipt
        };
    }

    public static bool HasStaleProviderProgress(
        ElsaInstanceDeletionWorkItem item,
        DateTimeOffset now)
    {
        var origin = item.LastVerifiedProgressAt ?? item.RunningSince;
        if (origin is null)
            return false;
        if (item.RunningSince is { } running && running.ToUniversalTime() > origin.Value.ToUniversalTime())
            origin = running;
        return now.ToUniversalTime() - origin.Value.ToUniversalTime() > ProviderProgressStaleAfter;
    }

    private static string StaleProgressCode(ElsaInstanceCleanupObservation observation) =>
        string.Equals(observation.DiagnosticCode, ManagedElsaReasonCodeCatalog.AssignmentRebindOperationsInFlight, StringComparison.Ordinal) ||
        string.Equals(observation.DiagnosticCode, ElsaInstanceDeletionDiagnosticCodes.BlockedByOperationInFlight, StringComparison.Ordinal)
            ? ElsaInstanceDeletionDiagnosticCodes.BlockedByOperationInFlight
            : ElsaInstanceDeletionDiagnosticCodes.ProviderProgressStale;

    private static ElsaInstanceLifecycleWorkerResult? TryConflict(ElsaInstanceDeletionWorkItem item) =>
        item.Operation is null || item.Instance is null ? null : new(
            ElsaInstanceLifecycleWorkerOutcome.Conflict,
            item.Operation,
            item.Instance,
            FailureCode: ElsaInstanceDeletionDiagnosticCodes.ClaimConflict,
            FailureSummary: "Deletion work item ownership changed before completion.");
}
