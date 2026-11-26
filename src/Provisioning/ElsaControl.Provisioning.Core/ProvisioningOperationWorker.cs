using System.Collections.Concurrent;
using ElsaControl.Provisioning.Abstractions;
using ElsaControl.Provisioning.Core.Stores;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ElsaControl.Provisioning.Core;

/// <summary>
/// Drains the module's own queue. Provider work happens here, never on the
/// API request thread. Cancel and host stop run a graceful provider cancel
/// and leave a flag when the Pulumi state lock may still be held.
/// </summary>
public sealed class ProvisioningOperationWorker(
    IProvisioningOperationStore operations,
    IProvisioningEventStore events,
    IInfrastructureProvider provider,
    ProvisioningRunRegistry? runs = null,
    TimeProvider? timeProvider = null,
    ILogger<ProvisioningOperationWorker>? logger = null,
    TimeSpan? heartbeat = null,
    IProvisioningLiveEventSink? liveEvents = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly ProvisioningRunRegistry _runs = runs ?? new ProvisioningRunRegistry();
    private readonly ILogger<ProvisioningOperationWorker> _logger = logger ?? NullLogger<ProvisioningOperationWorker>.Instance;
    private readonly TimeSpan _heartbeat = heartbeat is { } value && value > TimeSpan.Zero
        ? value
        : TimeSpan.FromSeconds(ProvisioningReplicaIdentity.DefaultHeartbeatSeconds);
    private readonly TimeSpan _stateLockRetryBackoff = TimeSpan.FromSeconds(
        ProvisioningReplicaIdentity.DefaultLeaseSeconds);
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _stateLockRetryAfter = new();

    public async Task<int> ProcessOnceAsync(int batchSize = 10, CancellationToken cancellationToken = default)
    {
        if (batchSize is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(batchSize));

        var runnable = await operations.ListRunnableAsync(batchSize, cancellationToken).ConfigureAwait(false);
        var processed = 0;
        foreach (var queued in runnable)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var claimed = await operations.ClaimAsync(queued.Id, _timeProvider.GetUtcNow(), cancellationToken)
                .ConfigureAwait(false);
            if (claimed is null)
                continue;
            if (claimed.State == ProvisioningOperationState.LockConflict)
            {
                await events.AppendAsync(
                    [
                        new ProvisioningEvent(
                            claimed.Id,
                            ProvisioningEventKind.LockConflicted,
                            ProvisioningCapability.ContainerRuntime,
                            "Stack lock conflict.",
                            _timeProvider.GetUtcNow())
                    ],
                    cancellationToken).ConfigureAwait(false);
                processed++;
                continue;
            }

            processed++;
            var request = await operations.GetRequestAsync(claimed.Id, cancellationToken).ConfigureAwait(false);
            if (request is null)
            {
                await FailAsync(
                        claimed,
                        ProvisioningFailureCodes.RequestMissing,
                        stateLockMayBeHeld: true,
                        cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            using var registration = claimed.Command == ProvisioningCommand.Cancel
                ? null
                : _runs.Register(claimed, linked);
            using var live = liveEvents?.Bind(claimed, request);
            var lostFence = false;

            try
            {
                await PublishAsync(
                        claimed.Id,
                        [
                            new ProvisioningEvent(
                                claimed.Id,
                                ProvisioningEventKind.OperationStarted,
                                claimed.StackKind == ProvisioningStackKind.Platform
                                    ? ProvisioningCapability.SharedEnvironment
                                    : ProvisioningCapability.ContainerRuntime,
                                "Operation started.",
                                _timeProvider.GetUtcNow())
                        ],
                        cancellationToken)
                    .ConfigureAwait(false);

                var result = await RunWithHeartbeatAsync(
                        claimed,
                        request,
                        linked,
                        cancellationToken,
                        () => lostFence = true)
                    .ConfigureAwait(false);
                if (lostFence)
                    continue;

                await PublishAsync(claimed.Id, result.Events, cancellationToken).ConfigureAwait(false);
                var terminal = TerminalState(claimed, result, linked, cancellationToken);
                var needsCancel = terminal != ProvisioningOperationState.Succeeded;
                var pulumiReleased = !needsCancel
                    || await TryCancelProviderAsync(claimed, request).ConfigureAwait(false);

                await PublishAsync(
                        claimed.Id,
                        [
                            TerminalEvent(
                                claimed,
                                terminal,
                                _timeProvider.GetUtcNow(),
                                terminal == ProvisioningOperationState.Succeeded
                                && result.FailureCode == ProvisioningFailureCodes.NothingToCancel
                                    ? "Nothing to cancel."
                                    : null)
                        ],
                        cancellationToken)
                    .ConfigureAwait(false);
                await operations.CompleteAsync(
                    claimed.Id,
                    terminal,
                    _timeProvider.GetUtcNow(),
                    result.FailureCode,
                    cancellationToken,
                    stateLockMayBeHeld: needsCancel && !pulumiReleased,
                    fencingToken: claimed.FencingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lostFence)
            {
                _logger.LogWarning(
                    "Provisioning lease lost. OperationId={OperationId}",
                    claimed.Id);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                var pulumiReleased = await TryCancelProviderAsync(claimed, request).ConfigureAwait(false);
                await CompleteTerminalAsync(
                    claimed,
                    ProvisioningOperationState.Cancelled,
                    ProvisioningFailureCodes.Interrupted,
                    stateLockMayBeHeld: !pulumiReleased,
                    CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            catch (OperationCanceledException)
            {
                var pulumiReleased = await TryCancelProviderAsync(claimed, request).ConfigureAwait(false);
                await CompleteTerminalAsync(
                    claimed,
                    ProvisioningOperationState.Cancelled,
                    ProvisioningFailureCodes.Cancelled,
                    stateLockMayBeHeld: !pulumiReleased,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    "Provisioning provider failed. OperationId={OperationId} ExceptionType={ExceptionType}",
                    claimed.Id,
                    exception.GetType().FullName);
                var pulumiReleased = await TryCancelProviderAsync(claimed, request).ConfigureAwait(false);
                await FailAsync(
                        claimed,
                        ProvisioningFailureCodes.ProviderFailed,
                        stateLockMayBeHeld: !pulumiReleased,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return processed;
    }

    public async Task ReconcileInterruptedAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = _timeProvider.GetUtcNow();
        var expired = await operations
            .FailInterruptedAsync(now, CancellationToken.None)
            .ConfigureAwait(false);
        var pending = await operations.ListStateLockPendingAsync(CancellationToken.None).ConfigureAwait(false);
        var seen = new HashSet<Guid>();
        foreach (var operation in expired.Concat(pending))
        {
            if (!seen.Add(operation.Id))
                continue;
            if (_stateLockRetryAfter.TryGetValue(operation.Id, out var retryAfter) && retryAfter > now)
                continue;

            var request = await operations.GetRequestAsync(operation.Id, CancellationToken.None)
                .ConfigureAwait(false);
            var pulumiReleased = request is not null
                && await TryCancelProviderAsync(operation, request).ConfigureAwait(false);
            if (!pulumiReleased)
            {
                _stateLockRetryAfter[operation.Id] = now + _stateLockRetryBackoff;
                _logger.LogWarning(
                    "Provisioning state-lock release deferred. OperationId={OperationId}",
                    operation.Id);
                continue;
            }

            _stateLockRetryAfter.TryRemove(operation.Id, out _);
            await operations.MarkStateLockClearedAsync(
                    operation.Id,
                    CancellationToken.None,
                    operation.FencingToken)
                .ConfigureAwait(false);

            if (expired.Any(item => item.Id == operation.Id)
                && operation.State == ProvisioningOperationState.Failed
                && operation.FailureCode == ProvisioningFailureCodes.Interrupted)
            {
                var interrupted = new ProvisioningEvent(
                    operation.Id,
                    ProvisioningEventKind.OperationFailed,
                    operation.StackKind == ProvisioningStackKind.Platform
                        ? ProvisioningCapability.SharedEnvironment
                        : ProvisioningCapability.ContainerRuntime,
                    "Operation interrupted.",
                    now);
                if (liveEvents is not null)
                {
                    await liveEvents
                        .PublishTerminalAsync(operation, request, [interrupted], CancellationToken.None)
                        .ConfigureAwait(false);
                }
                else
                {
                    await events.AppendAsync([interrupted], CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
    }

    private async Task<ProviderRunResult> RunWithHeartbeatAsync(
        ProvisioningOperation operation,
        ProvisioningRequest request,
        CancellationTokenSource linked,
        CancellationToken hostToken,
        Action onLostFence)
    {
        var run = RunProviderAsync(operation, request, linked.Token);
        while (!run.IsCompleted)
        {
            var delay = Task.Delay(_heartbeat, linked.Token);
            var finished = await Task.WhenAny(run, delay).ConfigureAwait(false);
            if (finished == run)
                break;

            if (hostToken.IsCancellationRequested)
                break;

            var renewed = await operations
                .RenewLeaseAsync(operation.Id, _timeProvider.GetUtcNow(), CancellationToken.None)
                .ConfigureAwait(false);
            if (!renewed)
            {
                onLostFence();
                await linked.CancelAsync().ConfigureAwait(false);
                break;
            }

            if (liveEvents is not null)
            {
                await liveEvents.HeartbeatAsync(operation.Id, CancellationToken.None).ConfigureAwait(false);
            }

            if (await operations.IsCancelRequestedAsync(operation.Id, CancellationToken.None).ConfigureAwait(false))
                await linked.CancelAsync().ConfigureAwait(false);
        }

        return await run.ConfigureAwait(false);
    }

    private static ProvisioningOperationState TerminalState(
        ProvisioningOperation operation,
        ProviderRunResult result,
        CancellationTokenSource linked,
        CancellationToken hostToken)
    {
        if (operation.Command == ProvisioningCommand.Cancel
            && result.FailureCode == ProvisioningFailureCodes.NothingToCancel)
        {
            return ProvisioningOperationState.Succeeded;
        }

        if (operation.Command == ProvisioningCommand.Cancel
            || result.FailureCode == ProvisioningFailureCodes.Cancelled
            || (linked.IsCancellationRequested && !hostToken.IsCancellationRequested))
        {
            return result.Succeeded || result.FailureCode == ProvisioningFailureCodes.Cancelled
                ? ProvisioningOperationState.Cancelled
                : ProvisioningOperationState.Failed;
        }

        return result.Succeeded
            ? ProvisioningOperationState.Succeeded
            : ProvisioningOperationState.Failed;
    }

    private Task PublishAsync(
        Guid operationId,
        IReadOnlyList<ProvisioningEvent> values,
        CancellationToken cancellationToken) =>
        liveEvents is not null
            ? liveEvents.PublishAsync(operationId, values, cancellationToken)
            : events.AppendAsync(values, cancellationToken);

    private Task<ProviderRunResult> RunProviderAsync(
        ProvisioningOperation operation,
        ProvisioningRequest request,
        CancellationToken cancellationToken) =>
        operation.Command switch
        {
            ProvisioningCommand.Preview => provider.PreviewAsync(operation, request, cancellationToken),
            ProvisioningCommand.Provision => provider.ProvisionAsync(operation, request, cancellationToken),
            ProvisioningCommand.Update => provider.UpdateAsync(operation, request, cancellationToken),
            ProvisioningCommand.Refresh => provider.RefreshAsync(operation, request, cancellationToken),
            ProvisioningCommand.Cancel => provider.CancelAsync(operation, request, cancellationToken),
            ProvisioningCommand.Destroy => provider.DestroyAsync(operation, request, cancellationToken),
            _ => Task.FromResult(ProviderRunResult.Fail([], ProvisioningFailureCodes.UnknownCommand))
        };

    private async Task<bool> TryCancelProviderAsync(ProvisioningOperation operation, ProvisioningRequest request)
    {
        if (operation.Command != ProvisioningCommand.Cancel
            && !await operations.HoldsFenceAsync(operation.Id, operation.FencingToken, CancellationToken.None)
                .ConfigureAwait(false))
        {
            return false;
        }

        try
        {
            var result = await provider.CancelAsync(operation, request, CancellationToken.None)
                .ConfigureAwait(false);
            return result.Succeeded;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                "Provisioning state-lock release failed. OperationId={OperationId} ExceptionType={ExceptionType}",
                operation.Id,
                exception.GetType().FullName);
            return false;
        }
    }

    private static ProvisioningEvent TerminalEvent(
        ProvisioningOperation operation,
        ProvisioningOperationState state,
        DateTimeOffset now,
        string? succeededMessage = null) =>
        new(
            operation.Id,
            state == ProvisioningOperationState.Succeeded
                ? ProvisioningEventKind.OperationSucceeded
                : state == ProvisioningOperationState.Cancelled
                    ? ProvisioningEventKind.OperationCancelled
                    : ProvisioningEventKind.OperationFailed,
            operation.StackKind == ProvisioningStackKind.Platform
                ? ProvisioningCapability.SharedEnvironment
                : ProvisioningCapability.ContainerRuntime,
            state == ProvisioningOperationState.Succeeded
                ? succeededMessage ?? "Operation succeeded."
                : state == ProvisioningOperationState.Cancelled
                    ? "Operation cancelled."
                    : "Operation failed.",
            now);

    private async Task CompleteTerminalAsync(
        ProvisioningOperation operation,
        ProvisioningOperationState state,
        string failureCode,
        bool stateLockMayBeHeld,
        CancellationToken cancellationToken)
    {
        await PublishAsync(
            operation.Id,
            [TerminalEvent(operation, state, _timeProvider.GetUtcNow())],
            cancellationToken).ConfigureAwait(false);
        await operations.CompleteAsync(
            operation.Id,
            state,
            _timeProvider.GetUtcNow(),
            failureCode,
            cancellationToken,
            stateLockMayBeHeld,
            operation.FencingToken).ConfigureAwait(false);
    }

    private async Task FailAsync(
        ProvisioningOperation operation,
        string failureCode,
        bool stateLockMayBeHeld,
        CancellationToken cancellationToken)
    {
        var current = await operations.GetAsync(operation.Id, CancellationToken.None).ConfigureAwait(false);
        if (current is null || ProvisioningOperationStateMachine.IsTerminal(current.State))
            return;

        await PublishAsync(
            operation.Id,
            [TerminalEvent(operation, ProvisioningOperationState.Failed, _timeProvider.GetUtcNow())],
            cancellationToken).ConfigureAwait(false);
        await operations.CompleteAsync(
            operation.Id,
            ProvisioningOperationState.Failed,
            _timeProvider.GetUtcNow(),
            failureCode,
            cancellationToken,
            stateLockMayBeHeld,
            operation.FencingToken).ConfigureAwait(false);
    }
}
