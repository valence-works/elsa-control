using System.Security.Cryptography;

namespace ElsaControl.Deployment.Core.ExternalConnections;

public sealed class ExternalEngineEnrollmentService(
    IExternalEngineEnrollmentStore store,
    TimeProvider timeProvider,
    IExternalEngineEnrollmentAuditStore? auditStore = null,
    IExternalEngineConnectorProofNonceStore? nonceStore = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly IExternalEngineConnectorProofNonceStore? _nonceStore = nonceStore ?? store as IExternalEngineConnectorProofNonceStore;

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
        if (request.IssuedAfter is { } issuedAfter && now <= issuedAfter.ToUniversalTime())
            now = issuedAfter.ToUniversalTime().AddTicks(1);
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
            return await RejectAsync(request, ExternalEngineEnrollmentRedeemFailure.InvalidRequest, cancellationToken);

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
            return await RejectAsync(request, ExternalEngineEnrollmentRedeemFailure.InvalidRequest, cancellationToken);
        }

        if (!ExternalEngineEnrollmentProtocol.Verify(request.PublicKey, payload, request.Signature))
            return await RejectAsync(request, ExternalEngineEnrollmentRedeemFailure.InvalidProof, cancellationToken);

        var challenge = await store.FindChallengeAsync(
            request.OrganizationId,
            request.WorkspaceId,
            request.ConnectionId,
            request.ChallengeId,
            cancellationToken);
        if (challenge is null)
            return await RejectAsync(request, ExternalEngineEnrollmentRedeemFailure.InvalidRequest, cancellationToken);

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

    public async Task<bool> VerifyConnectorSignatureAsync(
        ExternalEngineConnectorProof proof,
        string expectedOperation,
        string expectedPayloadDigest,
        CancellationToken cancellationToken = default) =>
        (await VerifyConnectorProofAsync(proof, expectedOperation, expectedPayloadDigest, cancellationToken)).Succeeded;

    public Task<ExternalEngineConnectorProofResult> VerifyConnectorProofAsync(
        ExternalEngineConnectorProof proof,
        string expectedOperation,
        string expectedPayloadDigest,
        CancellationToken cancellationToken = default) =>
        VerifyConnectorProofCoreAsync(
            proof,
            expectedOperation,
            expectedPayloadDigest,
            requireCurrentKey: false,
            cancellationToken);

    public async Task<ExternalEngineConnectorKeyRotationResult> RotateConnectorKeyAsync(
        ExternalEngineConnectorKeyRotationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        string digest;
        string thumbprint;
        try
        {
            digest = ExternalEngineEnrollmentProtocol.CreateRotationPayloadDigest(request.NewPublicKey, request.Overlap);
            thumbprint = ExternalEngineEnrollmentProtocol.PublicKeyThumbprint(request.NewPublicKey);
        }
        catch (Exception exception) when (exception is ArgumentException or CryptographicException or FormatException)
        {
            await RecordProofRejectionAsync(request.Proof, ExternalEngineConnectorProofFailure.InvalidRequest, cancellationToken);
            return ExternalEngineConnectorKeyRotationResult.Denied(ExternalEngineConnectorProofFailure.InvalidRequest);
        }

        var verified = await VerifyConnectorProofCoreAsync(
            request.Proof,
            ExternalEngineEnrollmentDefaults.RotationOperation,
            digest,
            requireCurrentKey: true,
            cancellationToken);
        if (!verified.Succeeded)
            return ExternalEngineConnectorKeyRotationResult.Denied(verified.Failure!.Value);
        if (string.Equals(verified.Identity!.PublicKeyThumbprint, thumbprint, StringComparison.Ordinal))
        {
            await RecordProofRejectionAsync(request.Proof, ExternalEngineConnectorProofFailure.InvalidRequest, cancellationToken);
            return ExternalEngineConnectorKeyRotationResult.Denied(ExternalEngineConnectorProofFailure.InvalidRequest);
        }

        var now = _timeProvider.GetUtcNow();
        var updated = await store.TryRotateIdentityAsync(
            verified.Identity,
            request.NewPublicKey,
            thumbprint,
            now,
            now.Add(request.Overlap),
            cancellationToken);
        if (updated is not null)
            return ExternalEngineConnectorKeyRotationResult.Success(updated);

        var failure = await ClassifyIdentityMutationFailureAsync(
            request.Proof,
            verified.Identity.KeyVersion,
            now,
            requireCurrentKey: true,
            activeOverlapIsInvalid: true,
            cancellationToken);
        await RecordProofRejectionAsync(request.Proof, failure, cancellationToken);
        return ExternalEngineConnectorKeyRotationResult.Denied(failure);
    }

    public async Task<ExternalEngineConnectorProofResult> RevokeConnectorAsync(
        ExternalEngineConnectorProof proof,
        CancellationToken cancellationToken = default)
    {
        var verified = await VerifyConnectorProofCoreAsync(
            proof,
            ExternalEngineEnrollmentDefaults.RevocationOperation,
            ExternalEngineEnrollmentProtocol.CreateRevocationPayloadDigest(),
            requireCurrentKey: true,
            cancellationToken);
        if (!verified.Succeeded)
            return verified;

        var now = _timeProvider.GetUtcNow();
        if (await store.TryRevokeIdentityAsync(verified.Identity!, now, cancellationToken))
        {
            var revoked = await store.FindIdentityAsync(
                proof.OrganizationId,
                proof.WorkspaceId,
                proof.ConnectionId,
                proof.IdentityId,
                cancellationToken);
            return ExternalEngineConnectorProofResult.Success(revoked!);
        }

        var failure = await ClassifyIdentityMutationFailureAsync(
            proof,
            verified.Identity!.KeyVersion,
            now,
            requireCurrentKey: true,
            activeOverlapIsInvalid: false,
            cancellationToken);
        await RecordProofRejectionAsync(proof, failure, cancellationToken);
        return ExternalEngineConnectorProofResult.Denied(failure);
    }

    /// <summary>
    /// Revokes an identity whose private key is unavailable so that a fresh pairing challenge can repair it.
    /// The route layer must authorize the caller for workspace setup management before invoking this operation.
    /// </summary>
    public async Task<ExternalEngineConnectorIdentity?> RevokeConnectorForRecoveryAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid connectionId,
        Guid identityId,
        CancellationToken cancellationToken = default)
    {
        ValidateScope(organizationId, workspaceId, connectionId);
        if (identityId == Guid.Empty)
            throw new ArgumentException("Connector identity is required.", nameof(identityId));

        var identity = await store.FindIdentityAsync(
            organizationId,
            workspaceId,
            connectionId,
            identityId,
            cancellationToken);
        if (identity is null || identity.RevokedAt is not null)
            return identity;

        await store.TryRevokeIdentityAsync(identity, _timeProvider.GetUtcNow(), cancellationToken);
        var current = await store.FindIdentityAsync(
            organizationId,
            workspaceId,
            connectionId,
            identityId,
            cancellationToken);
        return current?.RevokedAt is not null ? current : null;
    }

    private async Task<ExternalEngineConnectorProofResult> VerifyConnectorProofCoreAsync(
        ExternalEngineConnectorProof proof,
        string expectedOperation,
        string expectedPayloadDigest,
        bool requireCurrentKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proof);
        byte[] payload;
        string nonceHash;
        try
        {
            if (!HasValidScope(proof.OrganizationId, proof.WorkspaceId, proof.ConnectionId)
                || proof.IdentityId == Guid.Empty
                || proof.KeyVersion <= 0
                || !string.Equals(proof.Audience, ExternalEngineEnrollmentDefaults.AudienceFor(proof.ConnectionId), StringComparison.Ordinal)
                || !string.Equals(proof.Operation, expectedOperation, StringComparison.Ordinal)
                || !string.Equals(proof.PayloadDigest, expectedPayloadDigest, StringComparison.Ordinal)
                || !ExternalEngineEnrollmentProtocol.IsSha256Digest(proof.PayloadDigest))
                return await RejectProofAsync(proof, ExternalEngineConnectorProofFailure.InvalidRequest, cancellationToken);

            payload = ExternalEngineEnrollmentProtocol.CreateConnectorProofPayload(proof);
            nonceHash = ExternalEngineEnrollmentProtocol.HashNonce(proof.Nonce);
        }
        catch (Exception exception) when (exception is ArgumentException or CryptographicException or FormatException)
        {
            return await RejectProofAsync(proof, ExternalEngineConnectorProofFailure.InvalidRequest, cancellationToken);
        }

        var now = _timeProvider.GetUtcNow();
        var issuedAt = DateTimeOffset.FromUnixTimeMilliseconds(proof.IssuedAt.ToUniversalTime().ToUnixTimeMilliseconds());
        if (issuedAt > now)
            return await RejectProofAsync(proof, ExternalEngineConnectorProofFailure.Future, cancellationToken);
        if (issuedAt <= now.Subtract(ExternalEngineEnrollmentDefaults.MaximumProofAge))
            return await RejectProofAsync(proof, ExternalEngineConnectorProofFailure.Expired, cancellationToken);

        var identity = await store.FindIdentityAsync(
            proof.OrganizationId,
            proof.WorkspaceId,
            proof.ConnectionId,
            proof.IdentityId,
            cancellationToken);
        if (identity is null || !string.Equals(identity.Audience, proof.Audience, StringComparison.Ordinal))
            return await RejectProofAsync(proof, ExternalEngineConnectorProofFailure.ScopeMismatch, cancellationToken);
        if (identity.RevokedAt is not null)
            return await RejectProofAsync(proof, ExternalEngineConnectorProofFailure.Revoked, cancellationToken);

        var publicKey = ResolvePublicKey(identity, proof.KeyVersion, now, requireCurrentKey);
        if (publicKey is null)
            return await RejectProofAsync(proof, ExternalEngineConnectorProofFailure.KeyVersionMismatch, cancellationToken);
        if (!ExternalEngineEnrollmentProtocol.Verify(publicKey, payload, proof.Signature))
            return await RejectProofAsync(proof, ExternalEngineConnectorProofFailure.InvalidProof, cancellationToken);
        if (_nonceStore is null)
            return await RejectProofAsync(proof, ExternalEngineConnectorProofFailure.InvalidRequest, cancellationToken);

        var consumed = await _nonceStore.TryConsumeAsync(
            new ExternalEngineConnectorProofNonce(
                Guid.NewGuid(),
                identity.Id,
                identity.OrganizationId,
                identity.WorkspaceId,
                identity.ConnectionId,
                proof.KeyVersion,
                nonceHash,
                issuedAt,
                issuedAt.Add(ExternalEngineEnrollmentDefaults.MaximumProofAge),
                now),
            cancellationToken);
        if (consumed)
            return ExternalEngineConnectorProofResult.Success(identity);

        var failure = await ClassifyIdentityMutationFailureAsync(
            proof,
            proof.KeyVersion,
            now,
            requireCurrentKey,
            activeOverlapIsInvalid: false,
            cancellationToken);
        return await RejectProofAsync(proof, failure, cancellationToken);
    }

    private async Task<ExternalEngineConnectorProofFailure> ClassifyIdentityMutationFailureAsync(
        ExternalEngineConnectorProof proof,
        int expectedKeyVersion,
        DateTimeOffset now,
        bool requireCurrentKey,
        bool activeOverlapIsInvalid,
        CancellationToken cancellationToken)
    {
        var current = await store.FindIdentityAsync(
            proof.OrganizationId,
            proof.WorkspaceId,
            proof.ConnectionId,
            proof.IdentityId,
            cancellationToken);
        if (current is null)
            return ExternalEngineConnectorProofFailure.ScopeMismatch;
        if (current.RevokedAt is not null)
            return ExternalEngineConnectorProofFailure.Revoked;
        if (activeOverlapIsInvalid
            && current.KeyVersion == expectedKeyVersion
            && current.PreviousKeyValidUntil > now)
            return ExternalEngineConnectorProofFailure.InvalidRequest;
        if (current.KeyVersion != expectedKeyVersion
            && (requireCurrentKey
                || current.PreviousKeyVersion != expectedKeyVersion
                || current.PreviousKeyValidUntil <= now))
            return ExternalEngineConnectorProofFailure.KeyVersionMismatch;
        return ExternalEngineConnectorProofFailure.Replay;
    }

    private static string? ResolvePublicKey(
        ExternalEngineConnectorIdentity identity,
        int keyVersion,
        DateTimeOffset now,
        bool requireCurrentKey)
    {
        if (identity.KeyVersion == keyVersion)
            return identity.PublicKey;
        if (!requireCurrentKey
            && identity.PreviousKeyVersion == keyVersion
            && identity.PreviousKeyValidUntil > now)
            return identity.PreviousPublicKey;
        return null;
    }

    private async Task<ExternalEngineConnectorProofResult> RejectProofAsync(
        ExternalEngineConnectorProof proof,
        ExternalEngineConnectorProofFailure failure,
        CancellationToken cancellationToken)
    {
        await RecordProofRejectionAsync(proof, failure, cancellationToken);
        return ExternalEngineConnectorProofResult.Denied(failure);
    }

    private async Task RecordProofRejectionAsync(
        ExternalEngineConnectorProof proof,
        ExternalEngineConnectorProofFailure failure,
        CancellationToken cancellationToken)
    {
        if (auditStore is null)
            return;

        await auditStore.RecordAsync(
            new ExternalEngineEnrollmentAuditRecord(
                Guid.NewGuid(),
                proof.OrganizationId,
                proof.WorkspaceId,
                proof.ConnectionId,
                null,
                proof.IdentityId == Guid.Empty ? null : proof.IdentityId,
                ExternalEngineEnrollmentAuditAction.ConnectorProofRejected,
                failure switch
                {
                    ExternalEngineConnectorProofFailure.InvalidProof => ExternalEngineEnrollmentAuditReason.InvalidProof,
                    ExternalEngineConnectorProofFailure.Expired => ExternalEngineEnrollmentAuditReason.Expired,
                    ExternalEngineConnectorProofFailure.Future => ExternalEngineEnrollmentAuditReason.Future,
                    ExternalEngineConnectorProofFailure.Replay => ExternalEngineEnrollmentAuditReason.Replay,
                    ExternalEngineConnectorProofFailure.ScopeMismatch => ExternalEngineEnrollmentAuditReason.ScopeMismatch,
                    ExternalEngineConnectorProofFailure.KeyVersionMismatch => ExternalEngineEnrollmentAuditReason.KeyVersionMismatch,
                    ExternalEngineConnectorProofFailure.Revoked => ExternalEngineEnrollmentAuditReason.Revoked,
                    _ => ExternalEngineEnrollmentAuditReason.InvalidRequest
                },
                _timeProvider.GetUtcNow()),
            cancellationToken);
    }

    private static void ValidateScope(Guid organizationId, Guid workspaceId, Guid connectionId)
    {
        if (!HasValidScope(organizationId, workspaceId, connectionId))
            throw new ArgumentException("Organization, workspace, and connection IDs are required.");
    }

    private static bool HasValidScope(Guid organizationId, Guid workspaceId, Guid connectionId) =>
        organizationId != Guid.Empty && workspaceId != Guid.Empty && connectionId != Guid.Empty;

    private async Task<ExternalEngineEnrollmentRedeemResult> RejectAsync(
        ExternalEngineEnrollmentRedeemRequest request,
        ExternalEngineEnrollmentRedeemFailure failure,
        CancellationToken cancellationToken)
    {
        if (auditStore is not null)
        {
            await auditStore.RecordAsync(
                new ExternalEngineEnrollmentAuditRecord(
                    Guid.NewGuid(),
                    request.OrganizationId,
                    request.WorkspaceId,
                    request.ConnectionId,
                    request.ChallengeId == Guid.Empty ? null : request.ChallengeId,
                    null,
                    ExternalEngineEnrollmentAuditAction.RedemptionRejected,
                    failure switch
                    {
                        ExternalEngineEnrollmentRedeemFailure.InvalidProof => ExternalEngineEnrollmentAuditReason.InvalidProof,
                        ExternalEngineEnrollmentRedeemFailure.Expired => ExternalEngineEnrollmentAuditReason.Expired,
                        ExternalEngineEnrollmentRedeemFailure.Replay => ExternalEngineEnrollmentAuditReason.Replay,
                        ExternalEngineEnrollmentRedeemFailure.AlreadyEnrolled => ExternalEngineEnrollmentAuditReason.AlreadyEnrolled,
                        _ => ExternalEngineEnrollmentAuditReason.InvalidRequest
                    },
                    _timeProvider.GetUtcNow()),
                cancellationToken);
        }

        return ExternalEngineEnrollmentRedeemResult.Denied(failure);
    }
}
