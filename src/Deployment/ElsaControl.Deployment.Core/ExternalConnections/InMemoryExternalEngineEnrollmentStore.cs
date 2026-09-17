using System.Security.Cryptography;
using System.Text;

namespace ElsaControl.Deployment.Core.ExternalConnections;

/// <summary>
/// Deterministic reference implementation for domain tests and local composition.
/// Production registration must replace this with the durable store delivered by #489.
/// </summary>
public sealed class InMemoryExternalEngineEnrollmentStore :
    IExternalEngineEnrollmentStore,
    IExternalEngineConnectorProofNonceStore
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, ExternalEngineEnrollmentChallenge> _challenges = [];
    private readonly Dictionary<Guid, ExternalEngineConnectorIdentity> _identities = [];
    private readonly Dictionary<(Guid OrganizationId, Guid WorkspaceId, Guid ConnectionId, Guid IdentityId, int KeyVersion, string NonceHash), DateTimeOffset> _nonces = [];

    public Task StoreChallengeAsync(
        ExternalEngineEnrollmentChallenge challenge,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_challenges.TryAdd(challenge.Id, challenge))
                throw new InvalidOperationException("Enrollment challenge identity is already in use.");
        }

        return Task.CompletedTask;
    }

    public Task<ExternalEngineEnrollmentChallenge?> FindChallengeAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid connectionId,
        Guid challengeId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var challenge = _challenges.GetValueOrDefault(challengeId);
            return Task.FromResult(challenge is not null
                                   && challenge.OrganizationId == organizationId
                                   && challenge.WorkspaceId == workspaceId
                                   && challenge.ConnectionId == connectionId
                ? challenge
                : null);
        }
    }

    public Task<ExternalEngineEnrollmentStoreRedeemResult> TryRedeemAsync(
        ExternalEngineEnrollmentChallenge expectedChallenge,
        string presentedChallengeHash,
        ExternalEngineConnectorIdentity identity,
        DateTimeOffset redeemedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedChallenge);
        ArgumentNullException.ThrowIfNull(identity);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_challenges.TryGetValue(expectedChallenge.Id, out var stored))
                return Task.FromResult(ExternalEngineEnrollmentStoreRedeemResult.Denied(ExternalEngineEnrollmentStoreRedeemFailure.Missing));
            if (!HasSameScope(stored, expectedChallenge) || !HasSameScope(stored, identity))
                return Task.FromResult(ExternalEngineEnrollmentStoreRedeemResult.Denied(ExternalEngineEnrollmentStoreRedeemFailure.ScopeMismatch));
            if (stored.RedeemedAt.HasValue)
                return Task.FromResult(ExternalEngineEnrollmentStoreRedeemResult.Denied(ExternalEngineEnrollmentStoreRedeemFailure.Replay));
            if (redeemedAt >= stored.ExpiresAt)
                return Task.FromResult(ExternalEngineEnrollmentStoreRedeemResult.Denied(ExternalEngineEnrollmentStoreRedeemFailure.Expired));
            if (!FixedTimeEquals(stored.ChallengeHash, presentedChallengeHash))
                return Task.FromResult(ExternalEngineEnrollmentStoreRedeemResult.Denied(ExternalEngineEnrollmentStoreRedeemFailure.HashMismatch));
            var existing = _identities.Values.SingleOrDefault(existing =>
                existing.OrganizationId == identity.OrganizationId
                && existing.WorkspaceId == identity.WorkspaceId
                && existing.ConnectionId == identity.ConnectionId);
            if (existing is not null && existing.RevokedAt is null)
                return Task.FromResult(ExternalEngineEnrollmentStoreRedeemResult.Denied(ExternalEngineEnrollmentStoreRedeemFailure.AlreadyEnrolled));
            if (existing?.RevokedAt is { } revokedAt && stored.IssuedAt <= revokedAt)
                return Task.FromResult(ExternalEngineEnrollmentStoreRedeemResult.Denied(ExternalEngineEnrollmentStoreRedeemFailure.PredatesRevocation));

            var consumed = stored with { RedeemedAt = redeemedAt.ToUniversalTime() };
            _challenges[stored.Id] = consumed;
            if (existing is null)
            {
                _identities.Add(identity.Id, identity);
                return Task.FromResult(ExternalEngineEnrollmentStoreRedeemResult.Success(identity));
            }

            var repaired = identity with
            {
                Id = existing.Id,
                KeyVersion = checked(existing.KeyVersion + 1),
                PreviousKeyVersion = null,
                PreviousPublicKey = null,
                PreviousPublicKeyThumbprint = null,
                PreviousKeyValidUntil = null,
                RotatedAt = null,
                RevokedAt = null
            };
            _identities[existing.Id] = repaired;
            return Task.FromResult(ExternalEngineEnrollmentStoreRedeemResult.Success(repaired));
        }
    }

    public Task<ExternalEngineConnectorIdentity?> FindIdentityAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid connectionId,
        Guid identityId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var identity = _identities.GetValueOrDefault(identityId);
            return Task.FromResult(identity is not null
                                   && identity.OrganizationId == organizationId
                                   && identity.WorkspaceId == workspaceId
                                   && identity.ConnectionId == connectionId
                ? identity
                : null);
        }
    }

    public Task<ExternalEngineConnectorIdentity?> TryRotateIdentityAsync(
        ExternalEngineConnectorIdentity expectedIdentity,
        string newPublicKey,
        string newPublicKeyThumbprint,
        DateTimeOffset rotatedAt,
        DateTimeOffset previousKeyValidUntil,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedIdentity);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_identities.TryGetValue(expectedIdentity.Id, out var stored)
                || !HasSameScope(stored, expectedIdentity)
                || stored.KeyVersion != expectedIdentity.KeyVersion
                || stored.RevokedAt is not null
                || stored.PreviousKeyValidUntil > rotatedAt)
                return Task.FromResult<ExternalEngineConnectorIdentity?>(null);

            var updated = stored with
            {
                KeyVersion = checked(stored.KeyVersion + 1),
                PublicKey = newPublicKey,
                PublicKeyThumbprint = newPublicKeyThumbprint,
                PreviousKeyVersion = stored.KeyVersion,
                PreviousPublicKey = stored.PublicKey,
                PreviousPublicKeyThumbprint = stored.PublicKeyThumbprint,
                PreviousKeyValidUntil = previousKeyValidUntil.ToUniversalTime(),
                RotatedAt = rotatedAt.ToUniversalTime()
            };
            _identities[stored.Id] = updated;
            return Task.FromResult<ExternalEngineConnectorIdentity?>(updated);
        }
    }

    public Task<bool> TryRevokeIdentityAsync(
        ExternalEngineConnectorIdentity expectedIdentity,
        DateTimeOffset revokedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedIdentity);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_identities.TryGetValue(expectedIdentity.Id, out var stored)
                || !HasSameScope(stored, expectedIdentity)
                || stored.KeyVersion != expectedIdentity.KeyVersion
                || stored.RevokedAt is not null)
                return Task.FromResult(false);

            _identities[stored.Id] = stored with { RevokedAt = revokedAt.ToUniversalTime() };
            return Task.FromResult(true);
        }
    }

    public Task<bool> TryConsumeAsync(
        ExternalEngineConnectorProofNonce nonce,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nonce);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            foreach (var expired in _nonces.Where(x => x.Value <= nonce.ConsumedAt).Select(x => x.Key).ToArray())
                _nonces.Remove(expired);

            if (!_identities.TryGetValue(nonce.IdentityId, out var identity)
                || identity.OrganizationId != nonce.OrganizationId
                || identity.WorkspaceId != nonce.WorkspaceId
                || identity.ConnectionId != nonce.ConnectionId
                || identity.RevokedAt is not null
                || !IsAcceptedKeyVersion(identity, nonce.KeyVersion, nonce.ConsumedAt))
                return Task.FromResult(false);

            return Task.FromResult(_nonces.TryAdd((
                nonce.OrganizationId,
                nonce.WorkspaceId,
                nonce.ConnectionId,
                nonce.IdentityId,
                nonce.KeyVersion,
                nonce.NonceHash), nonce.ExpiresAt));
        }
    }

    private static bool HasSameScope(ExternalEngineEnrollmentChallenge left, ExternalEngineEnrollmentChallenge right) =>
        left.OrganizationId == right.OrganizationId
        && left.WorkspaceId == right.WorkspaceId
        && left.ConnectionId == right.ConnectionId
        && string.Equals(left.Purpose, right.Purpose, StringComparison.Ordinal)
        && string.Equals(left.Audience, right.Audience, StringComparison.Ordinal);

    private static bool HasSameScope(ExternalEngineEnrollmentChallenge challenge, ExternalEngineConnectorIdentity identity) =>
        challenge.OrganizationId == identity.OrganizationId
        && challenge.WorkspaceId == identity.WorkspaceId
        && challenge.ConnectionId == identity.ConnectionId
        && string.Equals(challenge.Audience, identity.Audience, StringComparison.Ordinal);

    private static bool HasSameScope(ExternalEngineConnectorIdentity left, ExternalEngineConnectorIdentity right) =>
        left.OrganizationId == right.OrganizationId
        && left.WorkspaceId == right.WorkspaceId
        && left.ConnectionId == right.ConnectionId
        && string.Equals(left.Audience, right.Audience, StringComparison.Ordinal);

    private static bool IsAcceptedKeyVersion(
        ExternalEngineConnectorIdentity identity,
        int keyVersion,
        DateTimeOffset at) =>
        identity.KeyVersion == keyVersion
        || identity.PreviousKeyVersion == keyVersion
        && identity.PreviousKeyValidUntil > at;

    private static bool FixedTimeEquals(string expected, string actual)
    {
        var expectedBytes = Encoding.ASCII.GetBytes(expected);
        var actualBytes = Encoding.ASCII.GetBytes(actual);
        return expectedBytes.Length == actualBytes.Length
               && CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }
}
