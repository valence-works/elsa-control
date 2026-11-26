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
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

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

            // The worker owns the request envelope for this skeleton: it reconstructs
            // a command-only request from the operation. Tests that need spec/target
            // pass them through the in-memory pending map.
            if (!ProvisioningRequestBuffer.TryTake(claimed.Id, out var request))
            {
                await FailAsync(claimed, "operation.request-missing", cancellationToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                if (claimed.Command == ProvisioningCommand.Cancel)
                {
                    var cancelling = await operations.CompleteAsync(
                        claimed.Id,
                        ProvisioningOperationState.Cancelling,
                        _timeProvider.GetUtcNow(),
                        failureCode: null,
                        cancellationToken).ConfigureAwait(false);
                    if (cancelling is not null)
                        claimed = cancelling;
                }

                var result = await RunProviderAsync(claimed, request, cancellationToken).ConfigureAwait(false);
                await events.AppendAsync(result.Events, cancellationToken).ConfigureAwait(false);
                var terminal = claimed.Command == ProvisioningCommand.Cancel
                    ? result.Succeeded
                        ? ProvisioningOperationState.Cancelled
                        : ProvisioningOperationState.Failed
                    : result.Succeeded
                        ? ProvisioningOperationState.Succeeded
                        : ProvisioningOperationState.Failed;
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
                                    ? "Operation succeeded."
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
                throw;
            }
            catch (Exception)
            {
                await FailAsync(claimed, "provider.failed", cancellationToken).ConfigureAwait(false);
            }
        }

        return processed;
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
            ProvisioningCommand.Cancel => CancelAsync(operation, request, cancellationToken),
            ProvisioningCommand.Destroy => provider.DestroyAsync(operation, request, cancellationToken),
            _ => Task.FromResult(ProviderRunResult.Fail([], "operation.unknown-command"))
        };

    private async Task<ProviderRunResult> CancelAsync(
        ProvisioningOperation operation,
        ProvisioningRequest request,
        CancellationToken cancellationToken)
    {
        await provider.CancelAsync(operation, cancellationToken).ConfigureAwait(false);
        return await provider.DestroyAsync(operation, request, cancellationToken).ConfigureAwait(false);
    }

    private async Task FailAsync(ProvisioningOperation operation, string failureCode, CancellationToken cancellationToken)
    {
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
