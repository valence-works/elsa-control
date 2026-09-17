using System.Security.Cryptography;
using ElsaControl.Deployment.Core.ExternalConnections;
using Xunit;

namespace ElsaControl.Deployment.Core.Tests.ExternalConnections;

public sealed class ExternalEngineConnectionServiceTests
{
    private static readonly Guid OrganizationId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid WorkspaceId = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid ConnectionId = Guid.Parse("30000000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-17T08:00:00Z");

    [Fact]
    public async Task Redemption_revokes_the_new_identity_when_linking_loses_concurrency()
    {
        var enrollmentStore = new InMemoryExternalEngineEnrollmentStore();
        var time = new FixedTimeProvider(Now);
        var enrollment = new ExternalEngineEnrollmentService(enrollmentStore, time);
        var issued = await enrollment.IssueAsync(
            new ExternalEngineEnrollmentIssueRequest(OrganizationId, WorkspaceId, ConnectionId));
        var connectionStore = new LinkLosingConnectionStore(Connection(issued.ChallengeId));
        var service = new ExternalEngineConnectionService(connectionStore, enrollmentStore, enrollment, time);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var result = await service.RedeemAsync(Redemption(issued, key));

        Assert.NotNull(result);
        Assert.False(result.Succeeded);
        Assert.Equal(ExternalEngineEnrollmentRedeemFailure.InvalidRequest, result.Failure);
        Assert.NotNull(connectionStore.AttemptedIdentityId);
        var identity = await enrollmentStore.FindIdentityAsync(
            OrganizationId, WorkspaceId, ConnectionId, connectionStore.AttemptedIdentityId!.Value);
        Assert.NotNull(identity?.RevokedAt);
    }

    [Fact]
    public async Task Repair_retries_revocation_after_a_concurrent_key_rotation()
    {
        var innerEnrollmentStore = new InMemoryExternalEngineEnrollmentStore();
        var enrollmentStore = new RotatingOnceEnrollmentStore(innerEnrollmentStore);
        var time = new FixedTimeProvider(Now);
        var enrollment = new ExternalEngineEnrollmentService(enrollmentStore, time);
        var issued = await enrollment.IssueAsync(
            new ExternalEngineEnrollmentIssueRequest(OrganizationId, WorkspaceId, ConnectionId));
        var connectionStore = new RepairConnectionStore(Connection(issued.ChallengeId));
        var service = new ExternalEngineConnectionService(connectionStore, enrollmentStore, enrollment, time);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var redeemed = await service.RedeemAsync(Redemption(issued, key));
        Assert.True(redeemed!.Succeeded);

        var repaired = await service.RepairAsync(OrganizationId, WorkspaceId, ConnectionId);

        Assert.NotNull(repaired);
        Assert.True(enrollmentStore.RotationInjected);
        Assert.Equal(1, connectionStore.PrepareRepairCalls);
        Assert.Null(repaired.Connection.ActiveIdentityId);
        var identity = await innerEnrollmentStore.FindIdentityAsync(
            OrganizationId, WorkspaceId, ConnectionId, redeemed.Identity!.Id);
        Assert.NotNull(identity?.RevokedAt);
    }

    private static ExternalEngineConnection Connection(Guid challengeId) =>
        new(
            ConnectionId,
            OrganizationId,
            WorkspaceId,
            "Customer engine",
            ExternalEngineConnectionStatus.Pending,
            ExternalEngineRuntimeHealth.Unknown,
            ExternalEngineConnectorReachability.Unknown,
            null,
            null,
            null,
            null,
            null,
            ExternalEngineReleaseEvidenceLevel.None,
            null,
            [],
            null,
            null,
            challengeId,
            Now,
            Now,
            null,
            1);

    private static ExternalEngineEnrollmentRedeemRequest Redemption(
        ExternalEngineEnrollmentIssueResult issued,
        ECDsa key)
    {
        var publicKey = ExternalEngineEnrollmentProtocol.ExportPublicKey(key);
        var unsigned = new ExternalEngineEnrollmentRedeemRequest(
            issued.ChallengeId,
            issued.OrganizationId,
            issued.WorkspaceId,
            issued.ConnectionId,
            issued.Purpose,
            issued.Audience,
            issued.Challenge,
            publicKey,
            "");
        return unsigned with
        {
            Signature = ExternalEngineEnrollmentProtocol.Sign(
                key,
                ExternalEngineEnrollmentProtocol.CreateRedemptionPayload(
                    unsigned.ChallengeId,
                    unsigned.OrganizationId,
                    unsigned.WorkspaceId,
                    unsigned.ConnectionId,
                    unsigned.Purpose,
                    unsigned.Audience,
                    ExternalEngineEnrollmentProtocol.HashChallenge(unsigned.Challenge),
                    ExternalEngineEnrollmentProtocol.PublicKeyThumbprint(publicKey)))
        };
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class LinkLosingConnectionStore(ExternalEngineConnection connection) : IExternalEngineConnectionStore
    {
        public Guid? AttemptedIdentityId { get; private set; }

        public Task<IReadOnlyList<ExternalEngineConnection>> ListAsync(
            Guid organizationId,
            Guid workspaceId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ExternalEngineConnection>>([connection]);

        public Task<ExternalEngineConnection?> FindAsync(
            Guid organizationId,
            Guid workspaceId,
            Guid connectionId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ExternalEngineConnection?>(
                organizationId == connection.OrganizationId
                && workspaceId == connection.WorkspaceId
                && connectionId == connection.Id
                    ? connection
                    : null);

        public Task<ExternalEngineConnectionCreateResult> TryCreateAsync(
            ExternalEngineConnection proposed,
            string idempotencyKey,
            string requestDigest,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ExternalEngineConnection?> TrySetPairingChallengeAsync(
            ExternalEngineConnection expected,
            Guid challengeId,
            DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ExternalEngineConnection?> TrySetActiveIdentityAsync(
            ExternalEngineConnection expected,
            Guid identityId,
            DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default)
        {
            AttemptedIdentityId = identityId;
            return Task.FromResult<ExternalEngineConnection?>(null);
        }

        public Task<ExternalEngineConnection?> TryPrepareRepairAsync(
            ExternalEngineConnection expected,
            DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ExternalEngineConnection?> TryDisconnectAsync(
            ExternalEngineConnection expected,
            DateTimeOffset revokedAt,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RepairConnectionStore(ExternalEngineConnection connection) : IExternalEngineConnectionStore
    {
        private ExternalEngineConnection _connection = connection;
        public int PrepareRepairCalls { get; private set; }

        public Task<IReadOnlyList<ExternalEngineConnection>> ListAsync(
            Guid organizationId,
            Guid workspaceId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ExternalEngineConnection>>([_connection]);

        public Task<ExternalEngineConnection?> FindAsync(
            Guid organizationId,
            Guid workspaceId,
            Guid connectionId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ExternalEngineConnection?>(
                organizationId == _connection.OrganizationId
                && workspaceId == _connection.WorkspaceId
                && connectionId == _connection.Id
                    ? _connection
                    : null);

        public Task<ExternalEngineConnectionCreateResult> TryCreateAsync(
            ExternalEngineConnection proposed,
            string idempotencyKey,
            string requestDigest,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ExternalEngineConnection?> TrySetPairingChallengeAsync(
            ExternalEngineConnection expected,
            Guid challengeId,
            DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default)
        {
            _connection = _connection with
            {
                LastChallengeId = challengeId,
                UpdatedAt = updatedAt,
                Version = checked(_connection.Version + 1)
            };
            return Task.FromResult<ExternalEngineConnection?>(_connection);
        }

        public Task<ExternalEngineConnection?> TrySetActiveIdentityAsync(
            ExternalEngineConnection expected,
            Guid identityId,
            DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default)
        {
            _connection = _connection with
            {
                ActiveIdentityId = identityId,
                UpdatedAt = updatedAt,
                Version = checked(_connection.Version + 1)
            };
            return Task.FromResult<ExternalEngineConnection?>(_connection);
        }

        public Task<ExternalEngineConnection?> TryPrepareRepairAsync(
            ExternalEngineConnection expected,
            DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default)
        {
            PrepareRepairCalls++;
            _connection = _connection with
            {
                Status = ExternalEngineConnectionStatus.Pending,
                ActiveIdentityId = null,
                LastChallengeId = null,
                UpdatedAt = updatedAt,
                Version = checked(_connection.Version + 1)
            };
            return Task.FromResult<ExternalEngineConnection?>(_connection);
        }

        public Task<ExternalEngineConnection?> TryDisconnectAsync(
            ExternalEngineConnection expected,
            DateTimeOffset revokedAt,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RotatingOnceEnrollmentStore(InMemoryExternalEngineEnrollmentStore inner) : IExternalEngineEnrollmentStore
    {
        public bool RotationInjected { get; private set; }

        public Task StoreChallengeAsync(
            ExternalEngineEnrollmentChallenge challenge,
            CancellationToken cancellationToken = default) =>
            inner.StoreChallengeAsync(challenge, cancellationToken);

        public Task<ExternalEngineEnrollmentChallenge?> FindChallengeAsync(
            Guid organizationId,
            Guid workspaceId,
            Guid connectionId,
            Guid challengeId,
            CancellationToken cancellationToken = default) =>
            inner.FindChallengeAsync(organizationId, workspaceId, connectionId, challengeId, cancellationToken);

        public Task<ExternalEngineEnrollmentStoreRedeemResult> TryRedeemAsync(
            ExternalEngineEnrollmentChallenge expectedChallenge,
            string presentedChallengeHash,
            ExternalEngineConnectorIdentity identity,
            DateTimeOffset redeemedAt,
            CancellationToken cancellationToken = default) =>
            inner.TryRedeemAsync(expectedChallenge, presentedChallengeHash, identity, redeemedAt, cancellationToken);

        public Task<ExternalEngineConnectorIdentity?> FindIdentityAsync(
            Guid organizationId,
            Guid workspaceId,
            Guid connectionId,
            Guid identityId,
            CancellationToken cancellationToken = default) =>
            inner.FindIdentityAsync(organizationId, workspaceId, connectionId, identityId, cancellationToken);

        public Task<ExternalEngineConnectorIdentity?> TryRotateIdentityAsync(
            ExternalEngineConnectorIdentity expectedIdentity,
            string newPublicKey,
            string newPublicKeyThumbprint,
            DateTimeOffset rotatedAt,
            DateTimeOffset previousKeyValidUntil,
            CancellationToken cancellationToken = default) =>
            inner.TryRotateIdentityAsync(
                expectedIdentity,
                newPublicKey,
                newPublicKeyThumbprint,
                rotatedAt,
                previousKeyValidUntil,
                cancellationToken);

        public async Task<bool> TryRevokeIdentityAsync(
            ExternalEngineConnectorIdentity expectedIdentity,
            DateTimeOffset revokedAt,
            CancellationToken cancellationToken = default)
        {
            if (!RotationInjected)
            {
                RotationInjected = true;
                using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                var publicKey = ExternalEngineEnrollmentProtocol.ExportPublicKey(key);
                _ = await inner.TryRotateIdentityAsync(
                    expectedIdentity,
                    publicKey,
                    ExternalEngineEnrollmentProtocol.PublicKeyThumbprint(publicKey),
                    revokedAt,
                    revokedAt.AddMinutes(1),
                    cancellationToken);
                return false;
            }

            return await inner.TryRevokeIdentityAsync(expectedIdentity, revokedAt, cancellationToken);
        }
    }
}
