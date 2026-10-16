using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;

namespace ElsaControl.Api.Authentication;

/// <summary>
/// Bridges the hosting-layer handoff ports to the persistence implementation.
/// The EF project intentionally does not reference the API assembly.
/// </summary>
public sealed class EfCoreManagedElsaHandoffReplayStore(EfCoreManagedElsaHandoffStore store) : IManagedElsaHandoffReplayStore
{
    public ValueTask<bool> TryConsumeAsync(
        string jti,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default) =>
        store.TryConsumeAsync(jti, expiresAt, cancellationToken);
}

public sealed class EfCoreManagedElsaHandoffAuditSink(EfCoreManagedElsaHandoffStore store) : IManagedElsaHandoffAuditSink
{
    public ValueTask RecordAsync(
        ManagedElsaHandoffAuditEvent auditEvent,
        CancellationToken cancellationToken = default) =>
        store.RecordAsync(
            new ManagedElsaHandoffAuditRecord(
                auditEvent.Action,
                auditEvent.Jti,
                auditEvent.AccountId,
                auditEvent.OrganizationId,
                auditEvent.InstanceId,
                auditEvent.Audience,
                auditEvent.BindingVersion,
                auditEvent.CorrelationId,
                auditEvent.OccurredAt,
                auditEvent.KeyRingSnapshotId,
                auditEvent.KeyRingPreviousSnapshotId),
            cancellationToken);

    public ValueTask RecordKeyRingTransitionAsync(
        string activeKeyId,
        IReadOnlyList<string> previousKeyIds,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default) =>
        new(store.RecordKeyRingSnapshotAsync(
            new ManagedElsaHandoffKeyRingSnapshotWrite(
                activeKeyId,
                previousKeyIds,
                occurredAt,
                ManagedElsaHandoffDefaults.KeyRingActiveAuditActionPrefix,
                ManagedElsaHandoffDefaults.KeyRingPreviousAuditActionPrefix,
                ManagedElsaHandoffDefaults.KeyRingRetiredAuditActionPrefix,
                ManagedElsaHandoffDefaults.KeyRingRetiredRevokedSuffix),
            cancellationToken));

    public async ValueTask<ManagedElsaHandoffTrustedKeySnapshot> GetLastTrustedSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        var actions = await store.GetLatestKeyRingSnapshotActionsAsync(
            ManagedElsaHandoffDefaults.KeyRingActiveAuditActionPrefix,
            ManagedElsaHandoffDefaults.KeyRingPreviousAuditActionPrefix,
            cancellationToken);
        return ManagedElsaHandoffKeyRingAudit.ParseTrustedSnapshot(actions);
    }
}
