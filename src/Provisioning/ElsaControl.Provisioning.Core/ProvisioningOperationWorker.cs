using ElsaControl.Provisioning.Abstractions;
using ElsaControl.Provisioning.Core.Stores;

namespace ElsaControl.Provisioning.Core;

/// <summary>
/// Drains the module's own queue. Provider work happens here, never on the
/// API request thread.
/// </summary>
public sealed class ProvisioningOperationWorker(
    IProvisioningOperationStore operations,
    IProvisioningEventStore events,
    IInfrastructureProvider provider,
    ProvisioningRunRegistry? runs = null,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly ProvisioningRunRegistry _runs = runs ?? new ProvisioningRunRegistry();

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
            await events.AppendAsync(
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
                cancellationToken).ConfigureAwait(false);

            var request = await operations.GetRequestAsync(claimed.Id, cancellationToken).ConfigureAwait(false);
            if (request is null)
            {
                await FailAsync(claimed, ProvisioningFailureCodes.RequestMissing, cancellationToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                using var registration = claimed.Command == ProvisioningCommand.Cancel
                    ? null
                    : _runs.Register(claimed, linked);

                var result = await RunProviderAsync(claimed, request, linked.Token).ConfigureAwait(false);
                await events.AppendAsync(result.Events, cancellationToken).ConfigureAwait(false);
                var terminal = TerminalState(claimed, result, linked, cancellationToken);

                var completed = await operations.CompleteAsync(
                    claimed.Id,
                    terminal,
                    _timeProvider.GetUtcNow(),
                    result.FailureCode,
                    cancellationToken).ConfigureAwait(false);
                if (completed is not null)
                {
                    await events.AppendAsync(
                        [
                            new ProvisioningEvent(
                                completed.Id,
                                terminal == ProvisioningOperationState.Succeeded
                                    ? ProvisioningEventKind.OperationSucceeded
                                    : terminal == ProvisioningOperationState.Cancelled
                                        ? ProvisioningEventKind.OperationCancelled
                                        : ProvisioningEventKind.OperationFailed,
                                completed.StackKind == ProvisioningStackKind.Platform
                                    ? ProvisioningCapability.SharedEnvironment
                                    : ProvisioningCapability.ContainerRuntime,
                                terminal == ProvisioningOperationState.Succeeded
                                    ? result.FailureCode == ProvisioningFailureCodes.NothingToCancel
                                        ? "Nothing to cancel."
                                        : "Operation succeeded."
                                    : terminal == ProvisioningOperationState.Cancelled
                                        ? "Operation cancelled."
                                        : "Operation failed.",
                                _timeProvider.GetUtcNow())
                        ],
                        cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await FailAsync(claimed, ProvisioningFailureCodes.Interrupted, CancellationToken.None)
                    .ConfigureAwait(false);
                throw;
            }
            catch (OperationCanceledException)
            {
                await CompleteCancelledAsync(claimed, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                await FailAsync(claimed, ProvisioningFailureCodes.ProviderFailed, cancellationToken).ConfigureAwait(false);
            }
        }

        return processed;
    }

    public async Task ReconcileInterruptedAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var interrupted = await operations
            .FailInterruptedAsync(_timeProvider.GetUtcNow(), CancellationToken.None)
            .ConfigureAwait(false);
        foreach (var operation in interrupted)
        {
            await events.AppendAsync(
                [
                    new ProvisioningEvent(
                        operation.Id,
                        ProvisioningEventKind.OperationFailed,
                        operation.StackKind == ProvisioningStackKind.Platform
                            ? ProvisioningCapability.SharedEnvironment
                            : ProvisioningCapability.ContainerRuntime,
                        "Operation interrupted.",
                        _timeProvider.GetUtcNow())
                ],
                CancellationToken.None).ConfigureAwait(false);
        }
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

    private async Task CompleteCancelledAsync(ProvisioningOperation operation, CancellationToken cancellationToken)
    {
        await operations.CompleteAsync(
            operation.Id,
            ProvisioningOperationState.Cancelled,
            _timeProvider.GetUtcNow(),
            ProvisioningFailureCodes.Cancelled,
            cancellationToken).ConfigureAwait(false);
        await events.AppendAsync(
            [
                new ProvisioningEvent(
                    operation.Id,
                    ProvisioningEventKind.OperationCancelled,
                    ProvisioningCapability.ContainerRuntime,
                    "Operation cancelled.",
                    _timeProvider.GetUtcNow())
            ],
            cancellationToken).ConfigureAwait(false);
    }

    private async Task FailAsync(ProvisioningOperation operation, string failureCode, CancellationToken cancellationToken)
    {
        var current = await operations.GetAsync(operation.Id, CancellationToken.None).ConfigureAwait(false);
        if (current is null || ProvisioningOperationStateMachine.IsTerminal(current.State))
            return;

        await operations.CompleteAsync(
            operation.Id,
            ProvisioningOperationState.Failed,
            _timeProvider.GetUtcNow(),
            failureCode,
            cancellationToken).ConfigureAwait(false);
        await events.AppendAsync(
            [
                new ProvisioningEvent(
                    operation.Id,
                    ProvisioningEventKind.OperationFailed,
                    ProvisioningCapability.ContainerRuntime,
                    "Operation failed.",
                    _timeProvider.GetUtcNow())
            ],
            cancellationToken).ConfigureAwait(false);
    }
}
