namespace ElsaControl.Provisioning.Core;

/// <summary>
/// Per-process replica identity for fenced stack locks. Lease length is the
/// heartbeat window; only expired leases may be reclaimed.
/// </summary>
public sealed class ProvisioningReplicaIdentity
{
    public const int DefaultLeaseSeconds = 30;

    public ProvisioningReplicaIdentity(string? id = null, TimeSpan? lease = null)
    {
        Id = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id.Trim();
        Lease = lease is { } value && value > TimeSpan.Zero
            ? value
            : TimeSpan.FromSeconds(DefaultLeaseSeconds);
    }

    public string Id { get; }

    public TimeSpan Lease { get; }

    public DateTimeOffset ExpiresAt(DateTimeOffset now) => now + Lease;
}
