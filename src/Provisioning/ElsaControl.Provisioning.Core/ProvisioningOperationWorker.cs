using ElsaControl.Provisioning.Abstractions;

namespace ElsaControl.Provisioning.Core;

/// <summary>
/// Drains the module's own queue. Claiming and locks stay in the store; this
/// worker only supplies a process-once drain so the API request thread never
/// runs a provider.
/// </summary>
public sealed class ProvisioningOperationWorker(
    IProvisioningOperationStore operations,
    IProvisioningEventStore events,
    IProvisioningStackLockStore locks,
    IEnumerable<IInfrastructureProvider> providers,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<int> ProcessOnceAsync(int batchSize = 10, CancellationToken cancellationToken = default)
    {
        if (batchSize is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(batchSize));

        var runnable = await operations.ListRunnableAsync(batchSize, cancellationToken).ConfigureAwait(false);
        var processed = 0;
        foreach (var operation in runnable)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ProcessOneAsync(operation, cancellationToken).ConfigureAwait(false);
            processed++;
        }

        return processed;
    }

    private async Task ProcessOneAsync(ProvisioningOperation operation, CancellationToken cancellationToken)
    {
        var command = ProvisioningWorkBuffer.Take(operation.Id);
        var provider = providers.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, operation.ProviderId, StringComparison.Ordinal));
        var now = _time.GetUtcNow();

        if (operation.Status == ProvisioningOperationStatus.Cancelling)
        {
            if (provider is not null)
                await provider.CancelAsync(operation.Id, cancellationToken).ConfigureAwait(false);
            await CompleteAsync(operation, ProvisioningOperationStatus.Cancelled, now, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (command is null || provider is null)
        {
            await CompleteAsync(
                    operation with { ReasonCode = ProvisioningReasonCodes.ProviderUnavailable },
                    ProvisioningOperationStatus.Failed,
                    now,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var running = operation with
        {
            Status = ProvisioningOperationStateMachine.OnStart(operation.Status),
            StartedAt = now
        };
        await operations.UpdateAsync(running, cancellationToken).ConfigureAwait(false);
        await events.AppendAsync(
            new ProvisioningEvent(
                running.Id,
                ProvisioningEventKind.OperationStarted,
                ProvisioningReasonCodes.OperationStarted,
                now),
            cancellationToken).ConfigureAwait(false);

        var sink = new StoreEventSink(events);
        try
        {
            await provider.ExecuteAsync(
                    new ProviderRunRequest(running.Id, running.Kind, command.Spec, command.Target),
                    sink,
                    cancellationToken)
                .ConfigureAwait(false);
            var latest = await operations.GetAsync(running.Id, cancellationToken).ConfigureAwait(false) ?? running;
            if (latest.Status == ProvisioningOperationStatus.Cancelling)
            {
                await CompleteAsync(latest, ProvisioningOperationStatus.Cancelled, _time.GetUtcNow(), cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            await CompleteAsync(running, ProvisioningOperationStatus.Succeeded, _time.GetUtcNow(), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await CompleteAsync(running, ProvisioningOperationStatus.Cancelled, _time.GetUtcNow(), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            await CompleteAsync(running, ProvisioningOperationStatus.Failed, _time.GetUtcNow(), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task CompleteAsync(
        ProvisioningOperation operation,
        ProvisioningOperationStatus status,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var kind = status switch
        {
            ProvisioningOperationStatus.Succeeded => ProvisioningEventKind.OperationSucceeded,
            ProvisioningOperationStatus.Cancelled => ProvisioningEventKind.OperationCancelled,
            _ => ProvisioningEventKind.OperationFailed
        };
        var code = status switch
        {
            ProvisioningOperationStatus.Succeeded => ProvisioningReasonCodes.OperationSucceeded,
            ProvisioningOperationStatus.Cancelled => ProvisioningReasonCodes.OperationCancelled,
            _ => operation.ReasonCode ?? ProvisioningReasonCodes.OperationFailed
        };
        var completed = operation with { Status = status, CompletedAt = now, ReasonCode = code };
        await operations.UpdateAsync(completed, cancellationToken).ConfigureAwait(false);
        await events.AppendAsync(
            new ProvisioningEvent(completed.Id, kind, code, now),
            cancellationToken).ConfigureAwait(false);
        await locks.ReleaseAsync(completed.StackKey, completed.Id, cancellationToken).ConfigureAwait(false);
    }
}
