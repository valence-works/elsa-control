using System.Collections.Concurrent;
using ElsaControl.Provisioning.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace ElsaControl.Deployment.Azure;

/// <summary>
/// Writes sanitised Pulumi events onto the existing Azure provider step ledger
/// so Hub can poll live progress. Correlation comes from stack config. A lost
/// Azure lease or a duplicate engine sequence does not write a new row.
/// </summary>
public sealed class AzureProviderOperationStepLedger : IProvisioningStepLedger
{
    public const string WorkerId = "provisioning-ledger";
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(15);

    private readonly Func<StoreSession> _open;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<Guid, BoundLease> _leases = new();
    private readonly ConcurrentDictionary<(Guid OperationId, int Sequence, ProvisioningEventKind Kind, string ResourceKind), byte> _seen = new();

    [ActivatorUtilitiesConstructor]
    public AzureProviderOperationStepLedger(
        IServiceScopeFactory scopes,
        TimeProvider? timeProvider = null)
        : this(() => new StoreSession(scopes.CreateScope(), dispose: true), timeProvider)
    {
        ArgumentNullException.ThrowIfNull(scopes);
    }

    public AzureProviderOperationStepLedger(
        IAzureProviderOperationStore store,
        TimeProvider? timeProvider = null)
        : this(() => new StoreSession(store), timeProvider)
    {
        ArgumentNullException.ThrowIfNull(store);
    }

    private AzureProviderOperationStepLedger(Func<StoreSession> open, TimeProvider? timeProvider)
    {
        _open = open;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ProvisioningStepLedgerResult> TryWriteAsync(
        ProvisioningOperation operation,
        ProvisioningRequest? request,
        IReadOnlyList<ProvisioningEvent> events,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count == 0
            || request is null
            || !PulumiStepLedgerMapper.TryReadCorrelation(request.StackConfig, out var workspaceId, out var providerOperationId))
        {
            return ProvisioningStepLedgerResult.Ignored;
        }

        var written = ProvisioningStepLedgerResult.Ignored;
        using var session = _open();
        var store = session.Store;
        foreach (var value in events.OrderBy(item => item.EngineSequence ?? int.MaxValue).ThenBy(item => item.OccurredAt))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = await EnsureClaimedAsync(store, operation.Id, workspaceId, providerOperationId, cancellationToken)
                .ConfigureAwait(false);
            if (current is null)
                return ProvisioningStepLedgerResult.StaleFence;

            var checkpoint = PulumiStepLedgerMapper.TryMap(value, current.Phase);
            if (checkpoint is null || !PulumiStepLedgerMapper.IsCustomerSafe(checkpoint))
                continue;

            if (value.EngineSequence is { } sequence
                && !_seen.TryAdd((operation.Id, sequence, value.Kind, value.ResourceKind), 0))
            {
                written = ProvisioningStepLedgerResult.Duplicate;
                continue;
            }

            var lease = RequireLease(operation.Id);
            var now = _timeProvider.GetUtcNow();
            if (checkpoint.Finalize)
            {
                var finalized = await store.FinalizeAsync(
                        workspaceId,
                        providerOperationId,
                        lease.Token,
                        checkpoint.Status,
                        checkpoint.Code,
                        now,
                        current.Version,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (finalized is null)
                    return ProvisioningStepLedgerResult.StaleFence;
                _leases.TryRemove(operation.Id, out _);
                written = ProvisioningStepLedgerResult.Written;
                continue;
            }

            AzureProviderOperation? saved;
            try
            {
                saved = await store.CheckpointAsync(
                        workspaceId,
                        providerOperationId,
                        lease.Token,
                        new AzureProviderCheckpoint(
                            checkpoint.Phase,
                            checkpoint.Code,
                            checkpoint.Message,
                            current.Resources,
                            current.Endpoint,
                            current.Health,
                            [],
                            AttemptedStep: checkpoint.AttemptedStep),
                        now,
                        current.Version,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            if (saved is null)
                return ProvisioningStepLedgerResult.StaleFence;
            written = ProvisioningStepLedgerResult.Written;
        }

        return written;
    }

    public async Task HeartbeatAsync(
        ProvisioningOperation operation,
        ProvisioningRequest? request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(operation);
        if (request is null
            || !PulumiStepLedgerMapper.TryReadCorrelation(request.StackConfig, out var workspaceId, out var providerOperationId)
            || !_leases.TryGetValue(operation.Id, out var lease))
        {
            return;
        }

        using var session = _open();
        var renewed = await session.Store.HeartbeatAsync(
                workspaceId,
                providerOperationId,
                lease.Token,
                LeaseDuration,
                _timeProvider.GetUtcNow(),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (renewed is null)
            _leases.TryRemove(operation.Id, out _);
    }

    private async Task<AzureProviderOperation?> EnsureClaimedAsync(
        IAzureProviderOperationStore store,
        Guid provisioningOperationId,
        Guid workspaceId,
        Guid providerOperationId,
        CancellationToken cancellationToken)
    {
        var current = await store.GetAsync(workspaceId, providerOperationId, cancellationToken)
            .ConfigureAwait(false);
        if (current is null)
            return null;
        if (current.Status == AzureProviderOperationStatus.Running
            && _leases.TryGetValue(provisioningOperationId, out _))
        {
            return current;
        }

        if (current.Status is AzureProviderOperationStatus.Failed
            or AzureProviderOperationStatus.Cancelled
            or AzureProviderOperationStatus.Succeeded)
        {
            return null;
        }

        var token = Guid.NewGuid().ToString("N");
        var claimed = current.Status == AzureProviderOperationStatus.RecoveryRequired
            ? await store.ClaimRecoveryAsync(
                    workspaceId,
                    providerOperationId,
                    WorkerId,
                    token,
                    LeaseDuration,
                    _timeProvider.GetUtcNow(),
                    current.Version,
                    cancellationToken)
                .ConfigureAwait(false)
            : await store.ClaimAsync(
                    workspaceId,
                    providerOperationId,
                    WorkerId,
                    token,
                    LeaseDuration,
                    _timeProvider.GetUtcNow(),
                    current.Version,
                    cancellationToken)
                .ConfigureAwait(false);
        if (claimed is null)
            return null;
        _leases[provisioningOperationId] = new BoundLease(token);
        return claimed;
    }

    private BoundLease RequireLease(Guid provisioningOperationId) =>
        _leases.TryGetValue(provisioningOperationId, out var lease)
            ? lease
            : throw new InvalidOperationException("The Azure provider operation lease is missing.");

    private sealed record BoundLease(string Token);

    private sealed class StoreSession : IDisposable
    {
        private readonly IServiceScope? _scope;

        public StoreSession(IServiceScope scope, bool dispose)
        {
            _scope = dispose ? scope : null;
            Store = scope.ServiceProvider.GetRequiredService<IAzureProviderOperationStore>();
        }

        public StoreSession(IAzureProviderOperationStore store)
        {
            Store = store;
        }

        public IAzureProviderOperationStore Store { get; }

        public void Dispose() => _scope?.Dispose();
    }
}
