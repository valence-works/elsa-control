using ElsaControl.Provisioning.Abstractions;

namespace ElsaControl.Provisioning.Core;

public sealed class ProvisioningService(
    IProvisioningOperationStore operations,
    IProvisioningEventStore events,
    IProvisioningStackLockStore locks,
    IEnumerable<IInfrastructureProvider> providers,
    TimeProvider? timeProvider = null) : IProvisioningService
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public Task<ProvisioningCommandResult> PreviewAsync(
        ProvisioningCommand command,
        CancellationToken cancellationToken = default) =>
        EnqueueAsync(command with { Kind = ProvisioningOperationKind.Preview }, cancellationToken);

    public Task<ProvisioningCommandResult> ProvisionAsync(
        ProvisioningCommand command,
        CancellationToken cancellationToken = default) =>
        EnqueueAsync(command with { Kind = ProvisioningOperationKind.Provision }, cancellationToken);

    public Task<ProvisioningCommandResult> UpdateAsync(
        ProvisioningCommand command,
        CancellationToken cancellationToken = default) =>
        EnqueueAsync(command with { Kind = ProvisioningOperationKind.Update }, cancellationToken);

    public Task<ProvisioningCommandResult> RefreshAsync(
        ProvisioningCommand command,
        CancellationToken cancellationToken = default) =>
        EnqueueAsync(command with { Kind = ProvisioningOperationKind.Refresh }, cancellationToken);

    public Task<ProvisioningCommandResult> DestroyAsync(
        ProvisioningCommand command,
        CancellationToken cancellationToken = default) =>
        EnqueueAsync(command with { Kind = ProvisioningOperationKind.Destroy }, cancellationToken);

    public async Task<ProvisioningCommandResult> CancelAsync(
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        var operation = await operations.GetAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (operation is null)
            return new ProvisioningCommandResult.Conflict(ProvisioningReasonCodes.ProviderUnavailable);

        if (ProvisioningOperationStateMachine.IsTerminal(operation.Status))
            return new ProvisioningCommandResult.Replayed(operation);

        var now = _time.GetUtcNow();
        var next = ProvisioningOperationStateMachine.OnCancelRequested(operation.Status);
        var updated = operation with
        {
            Status = next,
            CompletedAt = next == ProvisioningOperationStatus.Cancelled ? now : operation.CompletedAt,
            ReasonCode = ProvisioningReasonCodes.CancelRequested
        };
        await operations.UpdateAsync(updated, cancellationToken).ConfigureAwait(false);
        await events.AppendAsync(
            new ProvisioningEvent(
                updated.Id,
                next == ProvisioningOperationStatus.Cancelled
                    ? ProvisioningEventKind.OperationCancelled
                    : ProvisioningEventKind.OperationStarted,
                next == ProvisioningOperationStatus.Cancelled
                    ? ProvisioningReasonCodes.OperationCancelled
                    : ProvisioningReasonCodes.CancelRequested,
                now),
            cancellationToken).ConfigureAwait(false);
        if (next == ProvisioningOperationStatus.Cancelled)
            await locks.ReleaseAsync(updated.StackKey, updated.Id, cancellationToken).ConfigureAwait(false);
        else if (next == ProvisioningOperationStatus.Cancelling)
        {
            var provider = providers.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, updated.ProviderId, StringComparison.Ordinal));
            if (provider is not null)
                await provider.CancelAsync(updated.Id, cancellationToken).ConfigureAwait(false);
        }

        return new ProvisioningCommandResult.Accepted(updated);
    }

    public Task<ProvisioningOperation?> GetAsync(
        Guid operationId,
        CancellationToken cancellationToken = default) =>
        operations.GetAsync(operationId, cancellationToken);

    public Task<IReadOnlyList<ProvisioningEvent>> ReadEventsAsync(
        Guid operationId,
        CancellationToken cancellationToken = default) =>
        events.ListAsync(operationId, cancellationToken);

    private async Task<ProvisioningCommandResult> EnqueueAsync(
        ProvisioningCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var stack = StackNameCanonicalizer.Canonicalize(command.Target.Stack);
        var platform = command.Target.PlatformStack is { } named
            ? StackNameCanonicalizer.Canonicalize(named)
            : null;
        var canonical = command with
        {
            Target = command.Target with { Stack = stack, PlatformStack = platform }
        };
        var hash = ProvisioningRequestHash.Compute(canonical);
        var existing = await operations.GetByIdempotencyKeyAsync(canonical.IdempotencyKey, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            if (!string.Equals(existing.RequestHash, hash, StringComparison.Ordinal))
                return new ProvisioningCommandResult.Conflict(ProvisioningReasonCodes.IdempotencyConflict);
            return new ProvisioningCommandResult.Replayed(existing);
        }

        if (!providers.Any(provider => string.Equals(provider.Id, canonical.Target.ProviderId, StringComparison.Ordinal)))
            return new ProvisioningCommandResult.Conflict(ProvisioningReasonCodes.ProviderUnavailable);

        var now = _time.GetUtcNow();
        var operation = new ProvisioningOperation(
            Guid.NewGuid(),
            canonical.Kind,
            ProvisioningOperationStatus.Queued,
            canonical.IdempotencyKey,
            hash,
            stack.Key,
            canonical.Target.ProviderId,
            now);

        var acquired = await locks.TryAcquireAsync(stack.Key, operation.Id, now, cancellationToken)
            .ConfigureAwait(false);
        if (!acquired)
        {
            var held = await locks.GetAsync(stack.Key, cancellationToken).ConfigureAwait(false);
            return new ProvisioningCommandResult.StackLocked(stack.Key, held?.OperationId ?? Guid.Empty);
        }

        await operations.AddAsync(operation, cancellationToken).ConfigureAwait(false);
        await events.AppendAsync(
            new ProvisioningEvent(
                operation.Id,
                ProvisioningEventKind.OperationQueued,
                ProvisioningReasonCodes.OperationQueued,
                now),
            cancellationToken).ConfigureAwait(false);
        ProvisioningWorkBuffer.Remember(operation.Id, canonical);
        return new ProvisioningCommandResult.Accepted(operation);
    }
}

/// <summary>
/// Request-side command body kept off the operation table. The skeleton holds
/// it in process so the worker can run without a second persistence project.
/// </summary>
internal static class ProvisioningWorkBuffer
{
    private static readonly Dictionary<Guid, ProvisioningCommand> Commands = [];
    private static readonly object Gate = new();

    public static void Remember(Guid operationId, ProvisioningCommand command)
    {
        lock (Gate)
            Commands[operationId] = command;
    }

    public static ProvisioningCommand? Take(Guid operationId)
    {
        lock (Gate)
        {
            if (!Commands.TryGetValue(operationId, out var command))
                return null;
            Commands.Remove(operationId);
            return command;
        }
    }
}
