using ElsaControl.Deployment.Core.ExternalConnections;

namespace ElsaControl.Api.Workspace;

public sealed record CreateExternalEngineConnectionRequest(string DisplayName);

public sealed record ConfirmExternalEngineStudioDestinationRequest(Guid CandidateId);

public sealed record ExternalEngineConnectionListResponse(IReadOnlyList<ExternalEngineConnectionResponse> Items);

public sealed record ExternalEngineConnectionResponse(
    Guid Id,
    Guid WorkspaceId,
    string DisplayName,
    string OwnershipMode,
    ExternalEngineConnectionStatus Status,
    ExternalEngineRuntimeHealth RuntimeHealth,
    ExternalEngineConnectorReachability ConnectorReachability,
    DateTimeOffset? LastAuthenticatedAt,
    string? ConnectorProtocol,
    string? ConnectorVersion,
    string? ObservedDistribution,
    string? ObservedVersion,
    string? ObservedRuntimeKind,
    ExternalEngineReleaseEvidenceLevel ReleaseEvidenceLevel,
    string? ReleaseEvidenceReference,
    ExternalEngineConnectorCompatibilityStatus ConnectorCompatibilityStatus,
    DateTimeOffset? ConnectorCompatibilityObservedAt,
    string? StudioDestinationCandidate,
    Guid? StudioDestinationCandidateId,
    DateTimeOffset? StudioDestinationConfirmedAt,
    Guid? StudioDestinationConfirmedByAccountId,
    string? StudioDestination,
    IReadOnlyList<string> Capabilities,
    DateTimeOffset? CapabilitiesObservedAt,
    long? LastHeartbeatSequence,
    DateTimeOffset? LastHeartbeatObservedAt,
    ExternalEngineHeartbeatFreshness HeartbeatFreshness,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? RevokedAt);

public sealed record ExternalEnginePairingAttemptResponse(
    ExternalEngineConnectionResponse Connection,
    ExternalEngineEnrollmentResponse Enrollment,
    bool ReplayedConnection);

public sealed record ExternalEngineEnrollmentResponse(
    Guid ChallengeId,
    Guid ConnectionId,
    string Purpose,
    string Audience,
    string Challenge,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt)
{
    public override string ToString() =>
        $"ExternalEngineEnrollmentResponse {{ ChallengeId = {ChallengeId}, ConnectionId = {ConnectionId}, Purpose = {Purpose}, Audience = {Audience}, Challenge = [REDACTED], IssuedAt = {IssuedAt:O}, ExpiresAt = {ExpiresAt:O} }}";
}

public sealed record ExternalEnginePairingProgressResponse(
    Guid ConnectionId,
    Guid? ChallengeId,
    ExternalEnginePairingState State,
    DateTimeOffset? IssuedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? RedeemedAt);

public sealed record ExternalEngineConnectorIdentityResponse(
    Guid ConnectionId,
    Guid IdentityId,
    int KeyVersion,
    DateTimeOffset EnrolledAt,
    DateTimeOffset? RotatedAt,
    DateTimeOffset? RevokedAt);
