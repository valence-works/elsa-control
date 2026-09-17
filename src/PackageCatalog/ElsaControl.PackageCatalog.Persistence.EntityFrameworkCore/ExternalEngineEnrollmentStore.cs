using System.Data;
using System.Security.Cryptography;
using System.Text;
using ElsaControl.Deployment.Core.ExternalConnections;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Models;
using Microsoft.EntityFrameworkCore;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;

public sealed class EfCoreExternalEngineEnrollmentStore(CatalogDbContext dbContext) :
    IExternalEngineEnrollmentStore,
    IExternalEngineEnrollmentAuditStore,
    IExternalEngineConnectorProofNonceStore
{
    public async Task StoreChallengeAsync(
        ExternalEngineEnrollmentChallenge challenge,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        if (challenge.Id == Guid.Empty || challenge.OrganizationId == Guid.Empty || challenge.WorkspaceId == Guid.Empty
            || challenge.ConnectionId == Guid.Empty
            || !string.Equals(challenge.Purpose, ExternalEngineEnrollmentDefaults.PairingPurpose, StringComparison.Ordinal)
            || !string.Equals(challenge.Audience, ExternalEngineEnrollmentDefaults.AudienceFor(challenge.ConnectionId), StringComparison.Ordinal)
            || challenge.IssuedAt >= challenge.ExpiresAt || challenge.RedeemedAt is not null
            || !ExternalEngineEnrollmentProtocol.IsSha256Digest(challenge.ChallengeHash))
            throw new ArgumentException("Enrollment challenge metadata is invalid.", nameof(challenge));
        dbContext.ExternalEngineEnrollmentChallenges.Add(ToEntity(challenge));
        AddAudit(
            challenge.OrganizationId,
            challenge.WorkspaceId,
            challenge.ConnectionId,
            challenge.Id,
            null,
            ExternalEngineEnrollmentAuditAction.ChallengeIssued,
            ExternalEngineEnrollmentAuditReason.None,
            challenge.IssuedAt);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<ExternalEngineEnrollmentChallenge?> FindChallengeAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid connectionId,
        Guid challengeId,
        CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.ExternalEngineEnrollmentChallenges
            .AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.OrganizationId == organizationId
                     && x.WorkspaceId == workspaceId
                     && x.ConnectionId == connectionId
                     && x.Id == challengeId,
                cancellationToken);
        return entity is null ? null : ToDomain(entity);
    }

    public async Task<ExternalEngineEnrollmentStoreRedeemResult> TryRedeemAsync(
        ExternalEngineEnrollmentChallenge expectedChallenge,
        string presentedChallengeHash,
        ExternalEngineConnectorIdentity identity,
        DateTimeOffset redeemedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedChallenge);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(presentedChallengeHash);
        ValidateIdentity(identity);
        var normalizedRedeemedAt = redeemedAt.ToUniversalTime();
        if (identity.OrganizationId != expectedChallenge.OrganizationId
            || identity.WorkspaceId != expectedChallenge.WorkspaceId
            || identity.ConnectionId != expectedChallenge.ConnectionId
            || !string.Equals(identity.Audience, expectedChallenge.Audience, StringComparison.Ordinal))
        {
            await RecordRejectionAsync(
                expectedChallenge,
                identity.Id,
                ExternalEngineEnrollmentStoreRedeemFailure.ScopeMismatch,
                normalizedRedeemedAt,
                cancellationToken);
            return ExternalEngineEnrollmentStoreRedeemResult.Denied(ExternalEngineEnrollmentStoreRedeemFailure.ScopeMismatch);
        }

        try
        {
            return await dbContext.ExecuteInTransactionAsync(
                IsolationLevel.Serializable,
                async () =>
                {
                    var challenge = await dbContext.ExternalEngineEnrollmentChallenges
                        .SingleOrDefaultAsync(
                            x => x.OrganizationId == expectedChallenge.OrganizationId
                                 && x.WorkspaceId == expectedChallenge.WorkspaceId
                                 && x.ConnectionId == expectedChallenge.ConnectionId
                                 && x.Id == expectedChallenge.Id,
                            cancellationToken);

                    var failure = ClassifyChallenge(challenge, expectedChallenge, presentedChallengeHash, normalizedRedeemedAt);
                    if (failure is not null)
                        return await RejectAsync(expectedChallenge, identity.Id, failure.Value, normalizedRedeemedAt, cancellationToken);

                    if (await dbContext.ExternalEngineConnectorIdentities.AnyAsync(
                            x => x.OrganizationId == identity.OrganizationId
                                 && x.WorkspaceId == identity.WorkspaceId
                                 && x.ConnectionId == identity.ConnectionId,
                            cancellationToken))
                        return await RejectAsync(
                            expectedChallenge,
                            identity.Id,
                            ExternalEngineEnrollmentStoreRedeemFailure.AlreadyEnrolled,
                            normalizedRedeemedAt,
                            cancellationToken);

                    challenge!.RedeemedAt = normalizedRedeemedAt;
                    dbContext.ExternalEngineConnectorIdentities.Add(ToEntity(identity));
                    AddAudit(
                        identity.OrganizationId,
                        identity.WorkspaceId,
                        identity.ConnectionId,
                        expectedChallenge.Id,
                        identity.Id,
                        ExternalEngineEnrollmentAuditAction.RedemptionSucceeded,
                        ExternalEngineEnrollmentAuditReason.None,
                        normalizedRedeemedAt);
                    await dbContext.SaveChangesAsync(cancellationToken);
                    return ExternalEngineEnrollmentStoreRedeemResult.Success(identity);
                },
                async (result, attemptCancellationToken) =>
                    result.Succeeded
                    && await dbContext.ExternalEngineConnectorIdentities.AsNoTracking().AnyAsync(
                        x => x.OrganizationId == identity.OrganizationId
                             && x.WorkspaceId == identity.WorkspaceId
                             && x.ConnectionId == identity.ConnectionId
                             && x.Id == identity.Id,
                        attemptCancellationToken),
                cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return await ClassifyConcurrentLossAsync(expectedChallenge, identity.Id, normalizedRedeemedAt, cancellationToken);
        }
        catch (DbUpdateException exception) when (EfCoreDatabaseExceptionPolicy.IsUniqueViolation(exception))
        {
            return await ClassifyConcurrentLossAsync(expectedChallenge, identity.Id, normalizedRedeemedAt, cancellationToken);
        }
    }

    public async Task<ExternalEngineConnectorIdentity?> FindIdentityAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid connectionId,
        Guid identityId,
        CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.ExternalEngineConnectorIdentities
            .AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.OrganizationId == organizationId
                     && x.WorkspaceId == workspaceId
                     && x.ConnectionId == connectionId
                     && x.Id == identityId,
                cancellationToken);
        return entity is null ? null : ToDomain(entity);
    }

    public async Task RecordAsync(
        ExternalEngineEnrollmentAuditRecord audit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audit);
        if (audit.Id == Guid.Empty || audit.OccurredAt == default
            || !Enum.IsDefined(audit.Action) || !Enum.IsDefined(audit.Reason))
            throw new ArgumentException("Enrollment audit action and reason must be allowlisted.", nameof(audit));

        if (audit.Action == ExternalEngineEnrollmentAuditAction.RedemptionRejected)
        {
            if (audit.OrganizationId == Guid.Empty || audit.WorkspaceId == Guid.Empty
                || audit.ConnectionId == Guid.Empty || audit.ChallengeId is null)
                return;

            var challengeExists = await dbContext.ExternalEngineEnrollmentChallenges.AsNoTracking().AnyAsync(
                x => x.OrganizationId == audit.OrganizationId
                     && x.WorkspaceId == audit.WorkspaceId
                     && x.ConnectionId == audit.ConnectionId
                     && x.Id == audit.ChallengeId,
                cancellationToken);
            if (!challengeExists)
                return;
        }

        dbContext.ExternalEngineEnrollmentAuditEvents.Add(ToEntity(audit));
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> TryConsumeAsync(
        ExternalEngineConnectorProofNonce nonce,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nonce);
        if (!ExternalEngineEnrollmentProtocol.IsSha256Digest(nonce.NonceHash))
            throw new ArgumentException("Proof nonce hash must be a SHA-256 digest.", nameof(nonce));
        if (nonce.Id == Guid.Empty || nonce.IdentityId == Guid.Empty || nonce.KeyVersion <= 0
            || nonce.IssuedAt >= nonce.ExpiresAt || nonce.ConsumedAt < nonce.IssuedAt || nonce.ConsumedAt >= nonce.ExpiresAt)
            throw new ArgumentException("Proof nonce metadata is invalid.", nameof(nonce));
        if (!await dbContext.ExternalEngineConnectorIdentities.AsNoTracking().AnyAsync(
                x => x.OrganizationId == nonce.OrganizationId
                     && x.WorkspaceId == nonce.WorkspaceId
                     && x.ConnectionId == nonce.ConnectionId
                     && x.Id == nonce.IdentityId
                     && x.KeyVersion == nonce.KeyVersion,
                cancellationToken))
            return false;
        var entity = new ExternalEngineConnectorProofNonceEntity
        {
            Id = nonce.Id,
            IdentityId = nonce.IdentityId,
            OrganizationId = nonce.OrganizationId,
            WorkspaceId = nonce.WorkspaceId,
            ConnectionId = nonce.ConnectionId,
            KeyVersion = nonce.KeyVersion,
            NonceHash = nonce.NonceHash,
            IssuedAt = nonce.IssuedAt.ToUniversalTime(),
            ExpiresAt = nonce.ExpiresAt.ToUniversalTime(),
            ConsumedAt = nonce.ConsumedAt.ToUniversalTime()
        };
        dbContext.ExternalEngineConnectorProofNonces.Add(entity);
        AddAudit(
            nonce.OrganizationId,
            nonce.WorkspaceId,
            nonce.ConnectionId,
            null,
            nonce.IdentityId,
            ExternalEngineEnrollmentAuditAction.ProofNonceConsumed,
            ExternalEngineEnrollmentAuditReason.None,
            nonce.ConsumedAt);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException exception) when (EfCoreDatabaseExceptionPolicy.IsUniqueViolation(exception))
        {
            dbContext.ChangeTracker.Clear();
            return false;
        }
    }

    private async Task<ExternalEngineEnrollmentStoreRedeemResult> ClassifyConcurrentLossAsync(
        ExternalEngineEnrollmentChallenge expectedChallenge,
        Guid identityId,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();
        var challenge = await dbContext.ExternalEngineEnrollmentChallenges
            .AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.OrganizationId == expectedChallenge.OrganizationId
                     && x.WorkspaceId == expectedChallenge.WorkspaceId
                     && x.ConnectionId == expectedChallenge.ConnectionId
                     && x.Id == expectedChallenge.Id,
                cancellationToken);
        var failure = challenge?.RedeemedAt is not null
            ? ExternalEngineEnrollmentStoreRedeemFailure.Replay
            : await dbContext.ExternalEngineConnectorIdentities.AsNoTracking().AnyAsync(
                x => x.OrganizationId == expectedChallenge.OrganizationId
                     && x.WorkspaceId == expectedChallenge.WorkspaceId
                     && x.ConnectionId == expectedChallenge.ConnectionId,
                cancellationToken)
                ? ExternalEngineEnrollmentStoreRedeemFailure.AlreadyEnrolled
                : ExternalEngineEnrollmentStoreRedeemFailure.Missing;
        await RecordRejectionAsync(expectedChallenge, identityId, failure, occurredAt, cancellationToken);
        return ExternalEngineEnrollmentStoreRedeemResult.Denied(failure);
    }

    private async Task<ExternalEngineEnrollmentStoreRedeemResult> RejectAsync(
        ExternalEngineEnrollmentChallenge challenge,
        Guid identityId,
        ExternalEngineEnrollmentStoreRedeemFailure failure,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        AddRejection(challenge, identityId, failure, occurredAt);
        await dbContext.SaveChangesAsync(cancellationToken);
        return ExternalEngineEnrollmentStoreRedeemResult.Denied(failure);
    }

    private async Task RecordRejectionAsync(
        ExternalEngineEnrollmentChallenge challenge,
        Guid identityId,
        ExternalEngineEnrollmentStoreRedeemFailure failure,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        AddRejection(challenge, identityId, failure, occurredAt);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private void AddRejection(
        ExternalEngineEnrollmentChallenge challenge,
        Guid identityId,
        ExternalEngineEnrollmentStoreRedeemFailure failure,
        DateTimeOffset occurredAt) =>
        AddAudit(
            challenge.OrganizationId,
            challenge.WorkspaceId,
            challenge.ConnectionId,
            challenge.Id,
            identityId == Guid.Empty ? null : identityId,
            ExternalEngineEnrollmentAuditAction.RedemptionRejected,
            failure switch
            {
                ExternalEngineEnrollmentStoreRedeemFailure.Expired => ExternalEngineEnrollmentAuditReason.Expired,
                ExternalEngineEnrollmentStoreRedeemFailure.Replay => ExternalEngineEnrollmentAuditReason.Replay,
                ExternalEngineEnrollmentStoreRedeemFailure.AlreadyEnrolled => ExternalEngineEnrollmentAuditReason.AlreadyEnrolled,
                _ => ExternalEngineEnrollmentAuditReason.InvalidRequest
            },
            occurredAt);

    private void AddAudit(
        Guid organizationId,
        Guid workspaceId,
        Guid connectionId,
        Guid? challengeId,
        Guid? identityId,
        ExternalEngineEnrollmentAuditAction action,
        ExternalEngineEnrollmentAuditReason reason,
        DateTimeOffset occurredAt) =>
        dbContext.ExternalEngineEnrollmentAuditEvents.Add(new ExternalEngineEnrollmentAuditEventEntity
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            WorkspaceId = workspaceId,
            ConnectionId = connectionId,
            ChallengeId = challengeId,
            IdentityId = identityId,
            Action = action.ToString(),
            Reason = reason.ToString(),
            OccurredAt = occurredAt.ToUniversalTime()
        });

    private static ExternalEngineEnrollmentStoreRedeemFailure? ClassifyChallenge(
        ExternalEngineEnrollmentChallengeEntity? stored,
        ExternalEngineEnrollmentChallenge expected,
        string presentedChallengeHash,
        DateTimeOffset redeemedAt)
    {
        if (stored is null)
            return ExternalEngineEnrollmentStoreRedeemFailure.Missing;
        if (!string.Equals(stored.Purpose, expected.Purpose, StringComparison.Ordinal)
            || !string.Equals(stored.Audience, expected.Audience, StringComparison.Ordinal))
            return ExternalEngineEnrollmentStoreRedeemFailure.ScopeMismatch;
        if (stored.RedeemedAt.HasValue)
            return ExternalEngineEnrollmentStoreRedeemFailure.Replay;
        if (redeemedAt >= stored.ExpiresAt)
            return ExternalEngineEnrollmentStoreRedeemFailure.Expired;
        if (!FixedTimeEquals(stored.ChallengeHash, presentedChallengeHash))
            return ExternalEngineEnrollmentStoreRedeemFailure.HashMismatch;
        return null;
    }

    private static bool FixedTimeEquals(string expected, string actual)
    {
        var expectedBytes = Encoding.ASCII.GetBytes(expected);
        var actualBytes = Encoding.ASCII.GetBytes(actual);
        return expectedBytes.Length == actualBytes.Length
               && CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }

    private static void ValidateIdentity(ExternalEngineConnectorIdentity identity)
    {
        if (identity.Id == Guid.Empty || identity.OrganizationId == Guid.Empty || identity.WorkspaceId == Guid.Empty
            || identity.ConnectionId == Guid.Empty || identity.KeyVersion <= 0
            || !string.Equals(identity.KeyAlgorithm, ExternalEngineEnrollmentDefaults.KeyAlgorithm, StringComparison.Ordinal)
            || !string.Equals(identity.Audience, ExternalEngineEnrollmentDefaults.AudienceFor(identity.ConnectionId), StringComparison.Ordinal))
            throw new ArgumentException("Connector identity metadata is invalid.", nameof(identity));

        var expectedThumbprint = ExternalEngineEnrollmentProtocol.PublicKeyThumbprint(identity.PublicKey);
        if (!FixedTimeEquals(expectedThumbprint, identity.PublicKeyThumbprint))
            throw new ArgumentException("Connector public-key thumbprint is invalid.", nameof(identity));
    }

    private static ExternalEngineEnrollmentChallengeEntity ToEntity(ExternalEngineEnrollmentChallenge challenge) => new()
    {
        Id = challenge.Id,
        OrganizationId = challenge.OrganizationId,
        WorkspaceId = challenge.WorkspaceId,
        ConnectionId = challenge.ConnectionId,
        Purpose = challenge.Purpose,
        Audience = challenge.Audience,
        ChallengeHash = challenge.ChallengeHash,
        IssuedAt = challenge.IssuedAt.ToUniversalTime(),
        ExpiresAt = challenge.ExpiresAt.ToUniversalTime(),
        RedeemedAt = challenge.RedeemedAt?.ToUniversalTime()
    };

    private static ExternalEngineEnrollmentChallenge ToDomain(ExternalEngineEnrollmentChallengeEntity challenge) => new(
        challenge.Id,
        challenge.OrganizationId,
        challenge.WorkspaceId,
        challenge.ConnectionId,
        challenge.Purpose,
        challenge.Audience,
        challenge.ChallengeHash,
        challenge.IssuedAt,
        challenge.ExpiresAt,
        challenge.RedeemedAt);

    private static ExternalEngineConnectorIdentityEntity ToEntity(ExternalEngineConnectorIdentity identity) => new()
    {
        Id = identity.Id,
        OrganizationId = identity.OrganizationId,
        WorkspaceId = identity.WorkspaceId,
        ConnectionId = identity.ConnectionId,
        Audience = identity.Audience,
        KeyAlgorithm = identity.KeyAlgorithm,
        KeyVersion = identity.KeyVersion,
        PublicKey = identity.PublicKey,
        PublicKeyThumbprint = identity.PublicKeyThumbprint,
        EnrolledAt = identity.EnrolledAt.ToUniversalTime()
    };

    private static ExternalEngineConnectorIdentity ToDomain(ExternalEngineConnectorIdentityEntity identity) => new(
        identity.Id,
        identity.OrganizationId,
        identity.WorkspaceId,
        identity.ConnectionId,
        identity.Audience,
        identity.KeyAlgorithm,
        identity.KeyVersion,
        identity.PublicKey,
        identity.PublicKeyThumbprint,
        identity.EnrolledAt);

    private static ExternalEngineEnrollmentAuditEventEntity ToEntity(ExternalEngineEnrollmentAuditRecord audit) => new()
    {
        Id = audit.Id,
        OrganizationId = audit.OrganizationId,
        WorkspaceId = audit.WorkspaceId,
        ConnectionId = audit.ConnectionId,
        ChallengeId = audit.ChallengeId,
        IdentityId = audit.IdentityId,
        Action = audit.Action.ToString(),
        Reason = audit.Reason.ToString(),
        OccurredAt = audit.OccurredAt.ToUniversalTime()
    };
}
