using System.Collections.Concurrent;
using ElsaControl.Provisioning.Abstractions;
using ElsaControl.Provisioning.Core;
using Microsoft.Extensions.DependencyInjection;

namespace ElsaControl.Deployment.Azure;

/// <summary>
/// Writes sanitised Pulumi events onto the existing Azure provider step ledger
/// so Hub can poll live progress. Correlation comes from stack config. A lost
/// Azure lease or a duplicate engine sequence does not write a new row.
/// The Azure lease token is derived from the module operation id so a replica
/// or restart can reclaim a Running step. Sequence dedupe is process-local and
/// evicted on a terminal event, a rejected terminal write, or a store throw.
/// </summary>
public sealed class AzureProviderOperationStepLedger : IProvisioningStepLedger
{
    public const string WorkerId = "provisioning-ledger";
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(
        ProvisioningReplicaIdentity.DefaultLeaseSeconds);

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
        try
        {
            foreach (var value in events.OrderBy(item => item.EngineSequence ?? int.MaxValue).ThenBy(item => item.OccurredAt))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = await store.GetAsync(workspaceId, providerOperationId, cancellationToken)
                    .ConfigureAwait(false);
                if (current is null)
                    return Stale();

                var checkpoint = current.Action == AzureProviderOperationAction.Delete
                    && value.Kind == ProvisioningEventKind.OperationSucceeded
                    ? PulumiStepLedgerMapper.DeleteSucceeded()
                    : PulumiStepLedgerMapper.TryMap(value, current.Phase);
                if (checkpoint is null || !PulumiStepLedgerMapper.IsCustomerSafe(checkpoint))
                    continue;

                if (current.Status is AzureProviderOperationStatus.Failed
                    or AzureProviderOperationStatus.Cancelled
                    or AzureProviderOperationStatus.Succeeded)
                {
                    if (checkpoint.Finalize && checkpoint.Status == current.Status)
                    {
                        written = ProvisioningStepLedgerResult.Duplicate;
                        continue;
                    }

                    return checkpoint.Finalize ? Stale() : written;
                }

                current = await EnsureClaimedAsync(store, operation.Id, workspaceId, providerOperationId, cancellationToken)
                    .ConfigureAwait(false);
                if (current is null)
                    return Stale();

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
                    if (checkpoint.Status == AzureProviderOperationStatus.Succeeded
                        && current.Action == AzureProviderOperationAction.Reconcile)
                    {
                        var ready = await AdvanceReadyAsync(
                                store,
                                workspaceId,
                                providerOperationId,
                                lease.Token,
                                current,
                                cancellationToken)
                            .ConfigureAwait(false);
                        if (ready is null)
                            return Stale();
                        current = ready;
                        now = _timeProvider.GetUtcNow();
                    }

                    if (AzureProviderOperationPhaseOrdering.Compare(checkpoint.Phase, current.Phase) > 0)
                    {
                        var advanced = await TryCheckpointAsync(
                                store,
                                workspaceId,
                                providerOperationId,
                                lease.Token,
                                checkpoint,
                                current,
                                now,
                                cancellationToken)
                            .ConfigureAwait(false);
                        if (advanced is null)
                            return Stale();
                        current = advanced;
                        now = _timeProvider.GetUtcNow();
                    }

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
                        return Stale();
                    if (checkpoint.Status == AzureProviderOperationStatus.Failed)
                    {
                        await store.RecordArmObservationClockAsync(
                                workspaceId,
                                providerOperationId,
                                now,
                                0,
                                checkpoint.Code,
                                AzureTransientArmFailure.FromPulumi(operation.FailureCode),
                                cancellationToken)
                            .ConfigureAwait(false);
                    }

                    Forget(operation.Id);
                    written = ProvisioningStepLedgerResult.Written;
                    continue;
                }

                AzureProviderOperation? saved;
                try
                {
                    saved = await TryCheckpointAsync(
                            store,
                            workspaceId,
                            providerOperationId,
                            lease.Token,
                            checkpoint,
                            current,
                            now,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                    continue;
                }

                if (saved is null)
                    return Stale();
                written = ProvisioningStepLedgerResult.Written;
                if (value.Kind is ProvisioningEventKind.OperationSucceeded
                    or ProvisioningEventKind.OperationFailed
                    or ProvisioningEventKind.OperationCancelled)
                {
                    Forget(operation.Id);
                }
            }

            return written;
        }
        catch
        {
            Forget(operation.Id);
            throw;
        }

        ProvisioningStepLedgerResult Stale()
        {
            Forget(operation.Id);
            return ProvisioningStepLedgerResult.StaleFence;
        }
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

    private async Task<AzureProviderOperation?> AdvanceReadyAsync(
        IAzureProviderOperationStore store,
        Guid workspaceId,
        Guid providerOperationId,
        string leaseToken,
        AzureProviderOperation current,
        CancellationToken cancellationToken)
    {
        if (AzureProviderOperationPhaseOrdering.Compare(current.Phase, AzureProviderOperationPhase.HealthVerified) < 0)
        {
            var health = await TryCheckpointAsync(
                    store,
                    workspaceId,
                    providerOperationId,
                    leaseToken,
                    PulumiStepLedgerMapper.HealthVerified(),
                    current,
                    _timeProvider.GetUtcNow(),
                    cancellationToken)
                .ConfigureAwait(false);
            if (health is null)
                return null;
            current = health;
        }

        if (AzureProviderOperationPhaseOrdering.Compare(current.Phase, AzureProviderOperationPhase.TrafficPromoted) < 0)
        {
            var traffic = await TryCheckpointAsync(
                    store,
                    workspaceId,
                    providerOperationId,
                    leaseToken,
                    PulumiStepLedgerMapper.TrafficPromoted(),
                    current,
                    _timeProvider.GetUtcNow(),
                    cancellationToken)
                .ConfigureAwait(false);
            if (traffic is null)
                return null;
            current = traffic;
        }

        return current;
    }

    private static async Task<AzureProviderOperation?> TryCheckpointAsync(
        IAzureProviderOperationStore store,
        Guid workspaceId,
        Guid providerOperationId,
        string leaseToken,
        PulumiLedgerCheckpoint checkpoint,
        AzureProviderOperation current,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        try
        {
            return await store.CheckpointAsync(
                    workspaceId,
                    providerOperationId,
                    leaseToken,
                    new AzureProviderCheckpoint(
                        checkpoint.Phase,
                        checkpoint.Code,
                        checkpoint.Message,
                        current.Resources,
                        checkpoint.Endpoint ?? current.Endpoint,
                        checkpoint.Health == AzureProviderHealth.Unknown ? current.Health : checkpoint.Health,
                        [],
                        AttemptedStep: checkpoint.AttemptedStep),
                    now,
                    current.Version,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            return current;
        }
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
        if (current.Status is AzureProviderOperationStatus.Failed
            or AzureProviderOperationStatus.Cancelled
            or AzureProviderOperationStatus.Succeeded)
        {
            return null;
        }

        var token = LeaseTokenFor(provisioningOperationId);
        var now = _timeProvider.GetUtcNow();
        if (current.Status == AzureProviderOperationStatus.Running
            && string.Equals(current.WorkerId, WorkerId, StringComparison.Ordinal)
            && current.LeaseExpiresAt is { } expires
            && expires > now)
        {
            _leases[provisioningOperationId] = new BoundLease(token);
            return current;
        }

        var claimed = current.Status == AzureProviderOperationStatus.RecoveryRequired
            ? await store.ClaimRecoveryAsync(
                    workspaceId,
                    providerOperationId,
                    WorkerId,
                    token,
                    LeaseDuration,
                    now,
                    current.Version,
                    cancellationToken)
                .ConfigureAwait(false)
            : await store.ClaimAsync(
                    workspaceId,
                    providerOperationId,
                    WorkerId,
                    token,
                    LeaseDuration,
                    now,
                    current.Version,
                    cancellationToken)
                .ConfigureAwait(false);
        if (claimed is null)
            return null;
        _leases[provisioningOperationId] = new BoundLease(token);
        return claimed;
    }

    private void Forget(Guid provisioningOperationId)
    {
        _leases.TryRemove(provisioningOperationId, out _);
        foreach (var key in _seen.Keys)
        {
            if (key.OperationId == provisioningOperationId)
                _seen.TryRemove(key, out _);
        }
    }

    internal static string LeaseTokenFor(Guid provisioningOperationId) =>
        provisioningOperationId.ToString("N");

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
