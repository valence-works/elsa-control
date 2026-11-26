using ElsaControl.Provisioning.Abstractions;
using ElsaControl.Provisioning.Core.Stores;

namespace ElsaControl.Provisioning.Core;

/// <summary>
/// Enqueues work onto the module's own tables. Never runs a provider on the
/// caller's thread.
/// </summary>
public sealed class ProvisioningService(
    IProvisioningOperationStore operations,
    IProvisioningEventStore events,
    TimeProvider? timeProvider = null) : IProvisioningService
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public Task<ProvisioningEnqueueResult> PreviewAsync(ProvisioningRequest request, CancellationToken cancellationToken = default) =>
        EnqueueAsync(request, ProvisioningCommand.Preview, cancellationToken);

    public Task<ProvisioningEnqueueResult> ProvisionAsync(ProvisioningRequest request, CancellationToken cancellationToken = default) =>
        EnqueueAsync(request, ProvisioningCommand.Provision, cancellationToken);

    public Task<ProvisioningEnqueueResult> UpdateAsync(ProvisioningRequest request, CancellationToken cancellationToken = default) =>
        EnqueueAsync(request, ProvisioningCommand.Update, cancellationToken);

    public Task<ProvisioningEnqueueResult> RefreshAsync(ProvisioningRequest request, CancellationToken cancellationToken = default) =>
        EnqueueAsync(request, ProvisioningCommand.Refresh, cancellationToken);

    public Task<ProvisioningEnqueueResult> CancelAsync(ProvisioningRequest request, CancellationToken cancellationToken = default) =>
        EnqueueAsync(request, ProvisioningCommand.Cancel, cancellationToken);

    public Task<ProvisioningEnqueueResult> DestroyAsync(ProvisioningRequest request, CancellationToken cancellationToken = default) =>
        EnqueueAsync(request, ProvisioningCommand.Destroy, cancellationToken);

    public Task<ProvisioningOperation?> GetOperationAsync(Guid operationId, CancellationToken cancellationToken = default) =>
        operations.GetAsync(operationId, cancellationToken);

    public Task<IReadOnlyList<ProvisioningEvent>> ListEventsAsync(Guid operationId, CancellationToken cancellationToken = default) =>
        events.ListAsync(operationId, cancellationToken);

    public async IAsyncEnumerable<ProvisioningEvent> WatchAsync(
        Guid operationId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var seen = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var current = await events.ListAsync(operationId, cancellationToken).ConfigureAwait(false);
            for (; seen < current.Count; seen++)
                yield return current[seen];

            var operation = await operations.GetAsync(operationId, cancellationToken).ConfigureAwait(false);
            if (operation is null || (operation.IsTerminal && seen >= current.Count))
                yield break;

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                yield break;
            }
        }
    }

    private async Task<ProvisioningEnqueueResult> EnqueueAsync(
        ProvisioningRequest request,
        ProvisioningCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Command != command)
        {
            request = new ProvisioningRequest(
                command,
                request.Spec,
                request.Target,
                request.IdempotencyKey,
                request.StackConfig);
        }

        foreach (var pair in request.StackConfig)
        {
            if (ProvisioningEventSanitizer.ContainsSensitiveToken(pair.Key)
                || ProvisioningEventSanitizer.ContainsSensitiveToken(pair.Value))
            {
                throw new InvalidOperationException("Stack config must not contain secrets or cloud identifiers.");
            }
        }

        var hash = InMemoryProvisioningStore.HashRequest(request);
        var result = await operations.CreateOrGetAsync(request, hash, _timeProvider.GetUtcNow(), cancellationToken)
            .ConfigureAwait(false);
        if (result.Replayed)
            return result;

        ProvisioningRequestBuffer.Put(result.OperationId, request);
        var kind = result.LockConflict
            ? ProvisioningEventKind.LockConflicted
            : ProvisioningEventKind.OperationQueued;
        var message = result.LockConflict ? "Stack lock conflict." : "Operation queued.";
        await events.AppendAsync(
            [
                new ProvisioningEvent(
                    result.OperationId,
                    kind,
                    request.Target.StackKind == ProvisioningStackKind.Platform
                        ? ProvisioningCapability.SharedEnvironment
                        : ProvisioningCapability.ContainerRuntime,
                    message,
                    _timeProvider.GetUtcNow())
            ],
            cancellationToken).ConfigureAwait(false);
        return result;
    }
}
