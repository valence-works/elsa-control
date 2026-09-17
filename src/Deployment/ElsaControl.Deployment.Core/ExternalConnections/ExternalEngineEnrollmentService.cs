using System.Security.Cryptography;

namespace ElsaControl.Deployment.Core.ExternalConnections;

public sealed class ExternalEngineEnrollmentService(
    IExternalEngineEnrollmentStore store,
    TimeProvider timeProvider)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<ExternalEngineEnrollmentIssueResult> IssueAsync(
        ExternalEngineEnrollmentIssueRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateScope(request.OrganizationId, request.WorkspaceId, request.ConnectionId);
        var lifetime = request.Lifetime ?? ExternalEngineEnrollmentDefaults.DefaultChallengeLifetime;
        if (lifetime <= TimeSpan.Zero || lifetime > ExternalEngineEnrollmentDefaults.MaximumChallengeLifetime)
            throw new ArgumentOutOfRangeException(nameof(ExternalEngineEnrollmentIssueRequest.Lifetime), "Enrollment challenge lifetime must be between one tick and 15 minutes.");

        var challengeBytes = RandomNumberGenerator.GetBytes(32);
        var challenge = ExternalEngineEnrollmentProtocol.Base64UrlEncode(challengeBytes);
        var challengeHash = ExternalEngineEnrollmentProtocol.Base64UrlEncode(SHA256.HashData(challengeBytes));
        CryptographicOperations.ZeroMemory(challengeBytes);
        var now = _timeProvider.GetUtcNow();
        var audience = ExternalEngineEnrollmentDefaults.AudienceFor(request.ConnectionId);
        var record = new ExternalEngineEnrollmentChallenge(
            Guid.NewGuid(),
            request.OrganizationId,
            request.WorkspaceId,
            request.ConnectionId,
            ExternalEngineEnrollmentDefaults.PairingPurpose,
            audience,
            challengeHash,
            now,
            now.Add(lifetime),
            null);
        await store.StoreChallengeAsync(record, cancellationToken);

        return new ExternalEngineEnrollmentIssueResult(
            record.Id,
            record.OrganizationId,
            record.WorkspaceId,
            record.ConnectionId,
            record.Purpose,
            record.Audience,
            challenge,
            record.IssuedAt,
            record.ExpiresAt);
    }

    public async Task<ExternalEngineEnrollmentRedeemResult> RedeemAsync(
        ExternalEngineEnrollmentRedeemRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!HasValidScope(request.OrganizationId, request.WorkspaceId, request.ConnectionId)
            || request.ChallengeId == Guid.Empty
            || !string.Equals(request.Purpose, ExternalEngineEnrollmentDefaults.PairingPurpose, StringComparison.Ordinal)
            || !string.Equals(request.Audience, ExternalEngineEnrollmentDefaults.AudienceFor(request.ConnectionId), StringComparison.Ordinal))
            return ExternalEngineEnrollmentRedeemResult.Denied(ExternalEngineEnrollmentRedeemFailure.InvalidRequest);

        string challengeHash;
        string publicKeyThumbprint;
        byte[] payload;
        try
        {
            challengeHash = ExternalEngineEnrollmentProtocol.HashChallenge(request.Challenge);
            publicKeyThumbprint = ExternalEngineEnrollmentProtocol.PublicKeyThumbprint(request.PublicKey);
            payload = ExternalEngineEnrollmentProtocol.CreateRedemptionPayload(
                request.ChallengeId,
                request.OrganizationId,
                request.WorkspaceId,
                request.ConnectionId,
                request.Purpose,
                request.Audience,
                challengeHash,
                publicKeyThumbprint);
        }
        catch (Exception exception) when (exception is ArgumentException or CryptographicException or FormatException)
        {
            return ExternalEngineEnrollmentRedeemResult.Denied(ExternalEngineEnrollmentRedeemFailure.InvalidRequest);
        }

        if (!ExternalEngineEnrollmentProtocol.Verify(request.PublicKey, payload, request.Signature))
            return ExternalEngineEnrollmentRedeemResult.Denied(ExternalEngineEnrollmentRedeemFailure.InvalidProof);

        var challenge = await store.FindChallengeAsync(request.ChallengeId, cancellationToken);
        if (challenge is null)
            return ExternalEngineEnrollmentRedeemResult.Denied(ExternalEngineEnrollmentRedeemFailure.InvalidRequest);

        var now = _timeProvider.GetUtcNow();
        var identity = new ExternalEngineConnectorIdentity(
            Guid.NewGuid(),
            request.OrganizationId,
            request.WorkspaceId,
            request.ConnectionId,
            request.Audience,
            ExternalEngineEnrollmentDefaults.KeyAlgorithm,
            ExternalEngineEnrollmentDefaults.InitialKeyVersion,
            request.PublicKey,
            publicKeyThumbprint,
            now);
        var stored = await store.TryRedeemAsync(challenge, challengeHash, identity, now, cancellationToken);
        if (stored.Succeeded)
            return ExternalEngineEnrollmentRedeemResult.Success(stored.Identity!);

        return ExternalEngineEnrollmentRedeemResult.Denied(stored.Failure switch
        {
            ExternalEngineEnrollmentStoreRedeemFailure.Expired => ExternalEngineEnrollmentRedeemFailure.Expired,
            ExternalEngineEnrollmentStoreRedeemFailure.Replay => ExternalEngineEnrollmentRedeemFailure.Replay,
            ExternalEngineEnrollmentStoreRedeemFailure.AlreadyEnrolled => ExternalEngineEnrollmentRedeemFailure.AlreadyEnrolled,
            _ => ExternalEngineEnrollmentRedeemFailure.InvalidRequest
        });
    }

    /// <summary>
    /// Verifies the cryptographic signature and immutable identity binding only.
    /// Timestamp windows and durable nonce consumption are deliberately added by #490;
    /// callers must not use this primitive as route authorization before that work lands.
    /// </summary>
    public async Task<bool> VerifyConnectorSignatureAsync(
        ExternalEngineConnectorProof proof,
        string expectedOperation,
        string expectedPayloadDigest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proof);
        if (!HasValidScope(proof.OrganizationId, proof.WorkspaceId, proof.ConnectionId)
            || proof.IdentityId == Guid.Empty
            || proof.KeyVersion <= 0
            || !string.Equals(proof.Audience, ExternalEngineEnrollmentDefaults.AudienceFor(proof.ConnectionId), StringComparison.Ordinal)
            || !string.Equals(proof.Operation, expectedOperation, StringComparison.Ordinal)
            || !string.Equals(proof.PayloadDigest, expectedPayloadDigest, StringComparison.Ordinal))
            return false;

        var identity = await store.FindIdentityAsync(
            proof.OrganizationId,
            proof.WorkspaceId,
            proof.ConnectionId,
            proof.IdentityId,
            cancellationToken);
        if (identity is null
            || identity.KeyVersion != proof.KeyVersion
            || !string.Equals(identity.Audience, proof.Audience, StringComparison.Ordinal))
            return false;

        try
        {
            var payload = ExternalEngineEnrollmentProtocol.CreateConnectorProofPayload(proof);
            return ExternalEngineEnrollmentProtocol.Verify(identity.PublicKey, payload, proof.Signature);
        }
        catch (Exception exception) when (exception is ArgumentException or CryptographicException or FormatException)
        {
            return false;
        }
    }

    private static void ValidateScope(Guid organizationId, Guid workspaceId, Guid connectionId)
    {
        if (!HasValidScope(organizationId, workspaceId, connectionId))
            throw new ArgumentException("Organization, workspace, and connection IDs are required.");
    }

    private static bool HasValidScope(Guid organizationId, Guid workspaceId, Guid connectionId) =>
        organizationId != Guid.Empty && workspaceId != Guid.Empty && connectionId != Guid.Empty;
}
