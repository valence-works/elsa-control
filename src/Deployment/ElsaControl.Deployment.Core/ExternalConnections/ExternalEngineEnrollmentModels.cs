namespace ElsaControl.Deployment.Core.ExternalConnections;

public static class ExternalEngineEnrollmentDefaults
{
    public const string PairingPurpose = "external-engine.pair";
    public const string KeyAlgorithm = "ECDSA-P256-SHA256";
    public const string RotationOperation = "external-engine.identity.rotate";
    public const string RevocationOperation = "external-engine.identity.revoke";
    public const int InitialKeyVersion = 1;
    public static readonly TimeSpan DefaultChallengeLifetime = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan MaximumChallengeLifetime = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan MaximumProofAge = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan MaximumRotationOverlap = TimeSpan.FromMinutes(5);

    public static string AudienceFor(Guid connectionId)
    {
        if (connectionId == Guid.Empty)
            throw new ArgumentException("Connection ID is required.", nameof(connectionId));

        return $"urn:elsa:external-engine-connector:{connectionId:D}";
    }
}

public sealed record ExternalEngineEnrollmentIssueRequest(
    Guid OrganizationId,
    Guid WorkspaceId,
    Guid ConnectionId,
    TimeSpan? Lifetime = null,
    DateTimeOffset? IssuedAfter = null);

/// <summary>
/// The only enrollment value that contains the raw challenge. Callers must return it
/// once to the setup client and must never persist, log, audit, or echo it later.
/// </summary>
public sealed record ExternalEngineEnrollmentIssueResult(
    Guid ChallengeId,
    Guid OrganizationId,
    Guid WorkspaceId,
    Guid ConnectionId,
    string Purpose,
    string Audience,
    string Challenge,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt)
{
    public override string ToString() =>
        $"ExternalEngineEnrollmentIssueResult {{ ChallengeId = {ChallengeId}, OrganizationId = {OrganizationId}, WorkspaceId = {WorkspaceId}, ConnectionId = {ConnectionId}, Purpose = {Purpose}, Audience = {Audience}, Challenge = [REDACTED], IssuedAt = {IssuedAt:O}, ExpiresAt = {ExpiresAt:O} }}";
}

/// <summary>
/// Safe persisted challenge projection. ChallengeHash is SHA-256 over the random
/// challenge bytes encoded as base64url; the raw challenge is intentionally absent.
/// </summary>
public sealed record ExternalEngineEnrollmentChallenge(
    Guid Id,
    Guid OrganizationId,
    Guid WorkspaceId,
    Guid ConnectionId,
    string Purpose,
    string Audience,
    string ChallengeHash,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? RedeemedAt);

public sealed record ExternalEngineEnrollmentRedeemRequest(
    Guid ChallengeId,
    Guid OrganizationId,
    Guid WorkspaceId,
    Guid ConnectionId,
    string Purpose,
    string Audience,
    string Challenge,
    string PublicKey,
    string Signature)
{
    public override string ToString() =>
        $"ExternalEngineEnrollmentRedeemRequest {{ ChallengeId = {ChallengeId}, OrganizationId = {OrganizationId}, WorkspaceId = {WorkspaceId}, ConnectionId = {ConnectionId}, Purpose = {Purpose}, Audience = {Audience}, Challenge = [REDACTED], PublicKey = [REDACTED], Signature = [REDACTED] }}";
}

public enum ExternalEngineEnrollmentRedeemFailure
{
    InvalidRequest,
    InvalidProof,
    Expired,
    Replay,
    AlreadyEnrolled
}

public sealed record ExternalEngineConnectorIdentity(
    Guid Id,
    Guid OrganizationId,
    Guid WorkspaceId,
    Guid ConnectionId,
    string Audience,
    string KeyAlgorithm,
    int KeyVersion,
    string PublicKey,
    string PublicKeyThumbprint,
    DateTimeOffset EnrolledAt,
    int? PreviousKeyVersion = null,
    string? PreviousPublicKey = null,
    string? PreviousPublicKeyThumbprint = null,
    DateTimeOffset? PreviousKeyValidUntil = null,
    DateTimeOffset? RotatedAt = null,
    DateTimeOffset? RevokedAt = null);

public sealed record ExternalEngineEnrollmentRedeemResult(
    ExternalEngineConnectorIdentity? Identity,
    ExternalEngineEnrollmentRedeemFailure? Failure)
{
    public bool Succeeded => Identity is not null && Failure is null;

    public static ExternalEngineEnrollmentRedeemResult Success(ExternalEngineConnectorIdentity identity) =>
        new(identity, null);

    public static ExternalEngineEnrollmentRedeemResult Denied(ExternalEngineEnrollmentRedeemFailure failure) =>
        new(null, failure);
}

public sealed record ExternalEngineConnectorProof(
    Guid IdentityId,
    Guid OrganizationId,
    Guid WorkspaceId,
    Guid ConnectionId,
    string Audience,
    int KeyVersion,
    string Operation,
    string PayloadDigest,
    DateTimeOffset IssuedAt,
    string Nonce,
    string Signature)
{
    public override string ToString() =>
        $"ExternalEngineConnectorProof {{ IdentityId = {IdentityId}, OrganizationId = {OrganizationId}, WorkspaceId = {WorkspaceId}, ConnectionId = {ConnectionId}, Audience = {Audience}, KeyVersion = {KeyVersion}, Operation = {Operation}, PayloadDigest = {PayloadDigest}, IssuedAt = {IssuedAt:O}, Nonce = [REDACTED], Signature = [REDACTED] }}";
}

public enum ExternalEngineConnectorProofFailure
{
    InvalidRequest,
    InvalidProof,
    Expired,
    Future,
    Replay,
    ScopeMismatch,
    KeyVersionMismatch,
    Revoked
}

public sealed record ExternalEngineConnectorProofResult(
    ExternalEngineConnectorIdentity? Identity,
    ExternalEngineConnectorProofFailure? Failure)
{
    public bool Succeeded => Identity is not null && Failure is null;

    public static ExternalEngineConnectorProofResult Success(ExternalEngineConnectorIdentity identity) =>
        new(identity, null);

    public static ExternalEngineConnectorProofResult Denied(ExternalEngineConnectorProofFailure failure) =>
        new(null, failure);
}

public sealed record ExternalEngineConnectorKeyRotationRequest(
    ExternalEngineConnectorProof Proof,
    string NewPublicKey,
    TimeSpan Overlap);

public sealed record ExternalEngineConnectorKeyRotationResult(
    ExternalEngineConnectorIdentity? Identity,
    ExternalEngineConnectorProofFailure? Failure)
{
    public bool Succeeded => Identity is not null && Failure is null;

    public static ExternalEngineConnectorKeyRotationResult Success(ExternalEngineConnectorIdentity identity) =>
        new(identity, null);

    public static ExternalEngineConnectorKeyRotationResult Denied(ExternalEngineConnectorProofFailure failure) =>
        new(null, failure);
}

public enum ExternalEngineEnrollmentStoreRedeemFailure
{
    Missing,
    ScopeMismatch,
    HashMismatch,
    Expired,
    Replay,
    AlreadyEnrolled,
    PredatesRevocation
}

public sealed record ExternalEngineEnrollmentStoreRedeemResult(
    ExternalEngineConnectorIdentity? Identity,
    ExternalEngineEnrollmentStoreRedeemFailure? Failure)
{
    public bool Succeeded => Identity is not null && Failure is null;

    public static ExternalEngineEnrollmentStoreRedeemResult Success(ExternalEngineConnectorIdentity identity) =>
        new(identity, null);

    public static ExternalEngineEnrollmentStoreRedeemResult Denied(ExternalEngineEnrollmentStoreRedeemFailure failure) =>
        new(null, failure);
}

public interface IExternalEngineEnrollmentStore
{
    Task StoreChallengeAsync(
        ExternalEngineEnrollmentChallenge challenge,
        CancellationToken cancellationToken = default);

    Task<ExternalEngineEnrollmentChallenge?> FindChallengeAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid connectionId,
        Guid challengeId,
        CancellationToken cancellationToken = default);

    Task<ExternalEngineEnrollmentStoreRedeemResult> TryRedeemAsync(
        ExternalEngineEnrollmentChallenge expectedChallenge,
        string presentedChallengeHash,
        ExternalEngineConnectorIdentity identity,
        DateTimeOffset redeemedAt,
        CancellationToken cancellationToken = default);

    Task<ExternalEngineConnectorIdentity?> FindIdentityAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid connectionId,
        Guid identityId,
        CancellationToken cancellationToken = default);

    Task<ExternalEngineConnectorIdentity?> TryRotateIdentityAsync(
        ExternalEngineConnectorIdentity expectedIdentity,
        string newPublicKey,
        string newPublicKeyThumbprint,
        DateTimeOffset rotatedAt,
        DateTimeOffset previousKeyValidUntil,
        CancellationToken cancellationToken = default);

    Task<bool> TryRevokeIdentityAsync(
        ExternalEngineConnectorIdentity expectedIdentity,
        DateTimeOffset revokedAt,
        CancellationToken cancellationToken = default);
}

public enum ExternalEngineEnrollmentAuditAction
{
    ChallengeIssued,
    RedemptionSucceeded,
    RedemptionRejected,
    ProofNonceConsumed,
    ConnectorProofRejected,
    KeyRotated,
    IdentityRevoked,
    IdentityRepaired
}

public enum ExternalEngineEnrollmentAuditReason
{
    None,
    InvalidRequest,
    InvalidProof,
    Expired,
    Future,
    Replay,
    AlreadyEnrolled,
    ScopeMismatch,
    KeyVersionMismatch,
    Revoked
}

/// <summary>
/// Safe enrollment audit metadata. Bearer values, signatures, endpoint addresses,
/// authorization headers, and raw exception text are intentionally absent.
/// </summary>
public sealed record ExternalEngineEnrollmentAuditRecord(
    Guid Id,
    Guid OrganizationId,
    Guid WorkspaceId,
    Guid ConnectionId,
    Guid? ChallengeId,
    Guid? IdentityId,
    ExternalEngineEnrollmentAuditAction Action,
    ExternalEngineEnrollmentAuditReason Reason,
    DateTimeOffset OccurredAt);

public interface IExternalEngineEnrollmentAuditStore
{
    Task RecordAsync(
        ExternalEngineEnrollmentAuditRecord audit,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Hash-only replay marker persisted for the request-proof verifier delivered by #490.
/// </summary>
public sealed record ExternalEngineConnectorProofNonce(
    Guid Id,
    Guid IdentityId,
    Guid OrganizationId,
    Guid WorkspaceId,
    Guid ConnectionId,
    int KeyVersion,
    string NonceHash,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset ConsumedAt);

public interface IExternalEngineConnectorProofNonceStore
{
    Task<bool> TryConsumeAsync(
        ExternalEngineConnectorProofNonce nonce,
        CancellationToken cancellationToken = default);
}
