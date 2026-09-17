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

public enum ExternalEngineHeartbeatFreshness
{
    Waiting,
    Fresh,
    Stale,
    Revoked
}

public enum ExternalEngineReleaseEvidenceLevel
{
    None,
    SelfReported,
    SupportedRelease,
    VerifiedManifest
}

public enum ExternalEngineConnectorCompatibilityStatus
{
    Unknown,
    Compatible,
    UnsupportedProtocol
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
    int Version,
    string? ObservedRuntimeKind = null,
    string? ReleaseEvidenceReference = null,
    long? LastHeartbeatSequence = null,
    DateTimeOffset? LastHeartbeatObservedAt = null,
    string? StudioDestinationCandidate = null,
    Guid? StudioDestinationCandidateId = null,
    DateTimeOffset? StudioDestinationConfirmedAt = null,
    Guid? StudioDestinationConfirmedByAccountId = null,
    ExternalEngineConnectorCompatibilityStatus ConnectorCompatibilityStatus = ExternalEngineConnectorCompatibilityStatus.Unknown,
    DateTimeOffset? ConnectorCompatibilityObservedAt = null)
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

    Task<ExternalEngineConnection?> TryConfirmStudioDestinationAsync(
        ExternalEngineConnection expected,
        Guid candidateId,
        Guid accountId,
        DateTimeOffset confirmedAt,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The connection store does not support Studio destination confirmation.");

    Task<ExternalEngineHeartbeatStoreResult> TryApplyHeartbeatAsync(
        ExternalEngineConnection expected,
        ExternalEngineHeartbeatProjection projection,
        Guid identityId,
        DateTimeOffset receivedAt,
        TimeSpan minimumInterval,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The connection store does not support authenticated heartbeat persistence.");

    Task<ExternalEngineHeartbeatStoreResult> TryRecordUnsupportedProtocolAsync(
        ExternalEngineConnection expected,
        long sequence,
        DateTimeOffset observedAt,
        Guid identityId,
        DateTimeOffset receivedAt,
        TimeSpan minimumInterval,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The connection store does not support connector compatibility diagnostics.");
}

public sealed record ExternalEngineComponentObservation(string Id, string ImageDigest);

public sealed record ExternalEngineHeartbeatReport(
    long Sequence,
    DateTimeOffset ObservedAt,
    string ConnectorProtocol,
    string ConnectorVersion,
    ExternalEngineRuntimeHealth RuntimeHealth,
    string RuntimeKind,
    string? ObservedDistribution,
    string? ObservedVersion,
    string? StudioDestination,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<ExternalEngineComponentObservation> Components);

public sealed record ExternalEngineHeartbeatRequest(
    ExternalEngineConnectorProof Proof,
    ExternalEngineHeartbeatReport Report);

public sealed record ExternalEngineHeartbeatProjection(
    long Sequence,
    DateTimeOffset ObservedAt,
    ExternalEngineConnectionStatus Status,
    ExternalEngineRuntimeHealth RuntimeHealth,
    ExternalEngineConnectorReachability ConnectorReachability,
    string ConnectorProtocol,
    string ConnectorVersion,
    string? ObservedDistribution,
    string? ObservedVersion,
    string? ObservedRuntimeKind,
    ExternalEngineReleaseEvidenceLevel ReleaseEvidenceLevel,
    string? ReleaseEvidenceReference,
    string? StudioDestinationCandidate,
    IReadOnlyList<string> Capabilities);

public enum ExternalEngineStudioDestinationConfirmationStatus
{
    Confirmed,
    Conflict
}

public sealed record ExternalEngineStudioDestinationConfirmationResult(
    ExternalEngineStudioDestinationConfirmationStatus Status,
    ExternalEngineConnection? Connection)
{
    public bool Confirmed => Status == ExternalEngineStudioDestinationConfirmationStatus.Confirmed;
}

public enum ExternalEngineHeartbeatStoreStatus
{
    Applied,
    Concurrent,
    OutOfOrder,
    RateLimited,
    Revoked,
    ScopeMismatch
}

public sealed record ExternalEngineHeartbeatStoreResult(
    ExternalEngineHeartbeatStoreStatus Status,
    ExternalEngineConnection? Connection,
    TimeSpan? RetryAfter = null);

public enum ExternalEngineHeartbeatStatus
{
    Accepted,
    Degraded,
    InvalidReport,
    ProofDenied,
    OutOfOrder,
    RateLimited,
    Revoked,
    Conflict,
    UnsupportedProtocol
}

public sealed record ExternalEngineHeartbeatResult(
    ExternalEngineHeartbeatStatus Status,
    ExternalEngineConnection? Connection = null,
    ExternalEngineConnectorProofFailure? ProofFailure = null,
    TimeSpan? RetryAfter = null)
{
    public bool Accepted => Status is ExternalEngineHeartbeatStatus.Accepted or ExternalEngineHeartbeatStatus.Degraded;
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
