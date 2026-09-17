using System.Security.Cryptography;
using System.Text;

namespace ElsaControl.Deployment.Core.ExternalConnections;

/// <summary>
/// Deterministic reference implementation for domain tests and local composition.
/// Production registration must replace this with the durable store delivered by #489.
/// </summary>
public sealed class InMemoryExternalEngineEnrollmentStore : IExternalEngineEnrollmentStore
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, ExternalEngineEnrollmentChallenge> _challenges = [];
    private readonly Dictionary<Guid, ExternalEngineConnectorIdentity> _identities = [];

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
        Guid challengeId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
            return Task.FromResult(_challenges.GetValueOrDefault(challengeId));
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
            if (_identities.Values.Any(existing =>
                    existing.OrganizationId == identity.OrganizationId
                    && existing.WorkspaceId == identity.WorkspaceId
                    && existing.ConnectionId == identity.ConnectionId))
                return Task.FromResult(ExternalEngineEnrollmentStoreRedeemResult.Denied(ExternalEngineEnrollmentStoreRedeemFailure.AlreadyEnrolled));

            var consumed = stored with { RedeemedAt = redeemedAt.ToUniversalTime() };
            _challenges[stored.Id] = consumed;
            _identities.Add(identity.Id, identity);
            return Task.FromResult(ExternalEngineEnrollmentStoreRedeemResult.Success(identity));
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

    private static bool FixedTimeEquals(string expected, string actual)
    {
        var expectedBytes = Encoding.ASCII.GetBytes(expected);
        var actualBytes = Encoding.ASCII.GetBytes(actual);
        return expectedBytes.Length == actualBytes.Length
               && CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }
}
