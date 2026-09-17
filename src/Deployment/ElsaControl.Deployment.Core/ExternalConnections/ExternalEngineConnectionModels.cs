namespace ElsaControl.Deployment.Core.ExternalConnections;

public enum ExternalEngineConnectionStatus
{
    Pending,
    Connected,
    Degraded,
    Revoked
}

public enum ExternalEngineRuntimeHealth
{
    Unknown,
    Healthy,
    Unhealthy
}

public enum ExternalEngineConnectorReachability
{
    Unknown,
    Reachable,
    Unreachable
}

public enum ExternalEngineReleaseEvidenceLevel
{
    None,
    SelfReported,
    VerifiedManifest
}

public sealed record ExternalEngineConnection(
    Guid Id,
    Guid OrganizationId,
    Guid WorkspaceId,
    string DisplayName,
    ExternalEngineConnectionStatus Status,
    ExternalEngineRuntimeHealth RuntimeHealth,
    ExternalEngineConnectorReachability ConnectorReachability,
    DateTimeOffset? LastAuthenticatedAt,
    string? ConnectorProtocol,
    string? ConnectorVersion,
    string? ObservedDistribution,
    string? ObservedVersion,
    ExternalEngineReleaseEvidenceLevel ReleaseEvidenceLevel,
    string? StudioDestination,
    IReadOnlyList<string> Capabilities,
    DateTimeOffset? CapabilitiesObservedAt,
    Guid? ActiveIdentityId,
    Guid? LastChallengeId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? RevokedAt,
    int Version)
{
    public const string OwnershipMode = "CustomerOperated";
}

public sealed record ExternalEngineConnectionCreateRequest(
    Guid OrganizationId,
    Guid WorkspaceId,
    string DisplayName,
    string IdempotencyKey);

public enum ExternalEngineConnectionCreateFailure
{
    Conflict
}

public sealed record ExternalEngineConnectionCreateResult(
    ExternalEngineConnection? Connection,
    ExternalEngineConnectionCreateFailure? Failure,
    bool Replayed)
{
    public bool Succeeded => Connection is not null && Failure is null;

    public static ExternalEngineConnectionCreateResult Created(ExternalEngineConnection connection) =>
        new(connection, null, false);

    public static ExternalEngineConnectionCreateResult Replay(ExternalEngineConnection connection) =>
        new(connection, null, true);

    public static ExternalEngineConnectionCreateResult Conflict() =>
        new(null, ExternalEngineConnectionCreateFailure.Conflict, false);
}

public interface IExternalEngineConnectionStore
{
    Task<IReadOnlyList<ExternalEngineConnection>> ListAsync(
        Guid organizationId,
        Guid workspaceId,
        CancellationToken cancellationToken = default);

    Task<ExternalEngineConnection?> FindAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid connectionId,
        CancellationToken cancellationToken = default);

    Task<ExternalEngineConnectionCreateResult> TryCreateAsync(
        ExternalEngineConnection connection,
        string idempotencyKey,
        string requestDigest,
        CancellationToken cancellationToken = default);

    Task<ExternalEngineConnection?> TrySetPairingChallengeAsync(
        ExternalEngineConnection expected,
        Guid challengeId,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default);

    Task<ExternalEngineConnection?> TrySetActiveIdentityAsync(
        ExternalEngineConnection expected,
        Guid identityId,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default);

    Task<ExternalEngineConnection?> TryPrepareRepairAsync(
        ExternalEngineConnection expected,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default);

    Task<ExternalEngineConnection?> TryDisconnectAsync(
        ExternalEngineConnection expected,
        DateTimeOffset revokedAt,
        CancellationToken cancellationToken = default);
}

public sealed record ExternalEnginePairingAttempt(
    ExternalEngineConnection Connection,
    ExternalEngineEnrollmentIssueResult Enrollment,
    bool ReplayedConnection);

public enum ExternalEnginePairingState
{
    Waiting,
    Redeemed,
    Expired,
    Revoked
}

public sealed record ExternalEnginePairingProgress(
    Guid ConnectionId,
    Guid? ChallengeId,
    ExternalEnginePairingState State,
    DateTimeOffset? IssuedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? RedeemedAt);

public sealed class ExternalEngineConnectionConflictException(string message) : InvalidOperationException(message);
