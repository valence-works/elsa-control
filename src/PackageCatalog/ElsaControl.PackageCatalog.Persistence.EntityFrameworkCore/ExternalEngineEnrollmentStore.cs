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

                    var existingIdentity = await dbContext.ExternalEngineConnectorIdentities.SingleOrDefaultAsync(
                        x => x.OrganizationId == identity.OrganizationId
                             && x.WorkspaceId == identity.WorkspaceId
                             && x.ConnectionId == identity.ConnectionId,
                        cancellationToken);
                    if (existingIdentity is not null && existingIdentity.RevokedAt is null)
                        return await RejectAsync(
                            expectedChallenge,
                            identity.Id,
                            ExternalEngineEnrollmentStoreRedeemFailure.AlreadyEnrolled,
                            normalizedRedeemedAt,
                            cancellationToken);
                    if (existingIdentity?.RevokedAt is { } revokedAt && challenge!.IssuedAt <= revokedAt)
                        return await RejectAsync(
                            expectedChallenge,
                            identity.Id,
                            ExternalEngineEnrollmentStoreRedeemFailure.PredatesRevocation,
                            normalizedRedeemedAt,
                            cancellationToken);

                    challenge!.RedeemedAt = normalizedRedeemedAt;
                    ExternalEngineConnectorIdentity storedIdentity;
                    if (existingIdentity is null)
                    {
                        dbContext.ExternalEngineConnectorIdentities.Add(ToEntity(identity));
                        storedIdentity = identity;
                    }
                    else
                    {
                        existingIdentity.KeyVersion = checked(existingIdentity.KeyVersion + 1);
                        existingIdentity.PublicKey = identity.PublicKey;
                        existingIdentity.PublicKeyThumbprint = identity.PublicKeyThumbprint;
                        existingIdentity.EnrolledAt = normalizedRedeemedAt;
                        existingIdentity.PreviousKeyVersion = null;
                        existingIdentity.PreviousPublicKey = null;
                        existingIdentity.PreviousPublicKeyThumbprint = null;
                        existingIdentity.PreviousKeyValidUntil = null;
                        existingIdentity.RotatedAt = null;
                        existingIdentity.RevokedAt = null;
                        storedIdentity = ToDomain(existingIdentity);
                        AddAudit(
                            identity.OrganizationId,
                            identity.WorkspaceId,
                            identity.ConnectionId,
                            expectedChallenge.Id,
                            existingIdentity.Id,
                            ExternalEngineEnrollmentAuditAction.IdentityRepaired,
                            ExternalEngineEnrollmentAuditReason.None,
                            normalizedRedeemedAt);
                    }
                    AddAudit(
                        storedIdentity.OrganizationId,
                        storedIdentity.WorkspaceId,
                        storedIdentity.ConnectionId,
                        expectedChallenge.Id,
                        storedIdentity.Id,
                        ExternalEngineEnrollmentAuditAction.RedemptionSucceeded,
                        ExternalEngineEnrollmentAuditReason.None,
                        normalizedRedeemedAt);
                    await dbContext.SaveChangesAsync(cancellationToken);
                    return ExternalEngineEnrollmentStoreRedeemResult.Success(storedIdentity);
                },
                async (result, attemptCancellationToken) =>
                    result.Succeeded
                    && await dbContext.ExternalEngineConnectorIdentities.AsNoTracking().AnyAsync(
                        x => x.OrganizationId == result.Identity!.OrganizationId
                             && x.WorkspaceId == result.Identity.WorkspaceId
                             && x.ConnectionId == result.Identity.ConnectionId
                             && x.Id == result.Identity.Id
                             && x.KeyVersion == result.Identity.KeyVersion
                             && x.PublicKeyThumbprint == result.Identity.PublicKeyThumbprint
                             && x.RevokedAt == null,
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

    public async Task<ExternalEngineConnectorIdentity?> TryRotateIdentityAsync(
        ExternalEngineConnectorIdentity expectedIdentity,
        string newPublicKey,
        string newPublicKeyThumbprint,
        DateTimeOffset rotatedAt,
        DateTimeOffset previousKeyValidUntil,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedIdentity);
        var normalizedRotatedAt = rotatedAt.ToUniversalTime();
        var normalizedPreviousKeyValidUntil = previousKeyValidUntil.ToUniversalTime();
        if (normalizedPreviousKeyValidUntil <= normalizedRotatedAt
            || normalizedPreviousKeyValidUntil - normalizedRotatedAt > ExternalEngineEnrollmentDefaults.MaximumRotationOverlap)
            throw new ArgumentException("Connector key overlap is invalid.", nameof(previousKeyValidUntil));
        var candidate = expectedIdentity with
        {
            KeyVersion = checked(expectedIdentity.KeyVersion + 1),
            PublicKey = newPublicKey,
            PublicKeyThumbprint = newPublicKeyThumbprint,
            PreviousKeyVersion = expectedIdentity.KeyVersion,
            PreviousPublicKey = expectedIdentity.PublicKey,
            PreviousPublicKeyThumbprint = expectedIdentity.PublicKeyThumbprint,
            PreviousKeyValidUntil = normalizedPreviousKeyValidUntil,
            RotatedAt = normalizedRotatedAt
        };
        ValidateIdentity(candidate);

        try
        {
            return await dbContext.ExecuteInTransactionAsync(
                IsolationLevel.Serializable,
                async () =>
                {
                    var entity = await dbContext.ExternalEngineConnectorIdentities.SingleOrDefaultAsync(
                        x => x.OrganizationId == expectedIdentity.OrganizationId
                             && x.WorkspaceId == expectedIdentity.WorkspaceId
                             && x.ConnectionId == expectedIdentity.ConnectionId
                             && x.Id == expectedIdentity.Id
                             && x.PublicKeyThumbprint == expectedIdentity.PublicKeyThumbprint,
                        cancellationToken);
                    if (entity is null || entity.RevokedAt is not null
                        || entity.KeyVersion != expectedIdentity.KeyVersion
                        || entity.PreviousKeyValidUntil > normalizedRotatedAt)
                        return null;

                    entity.PreviousKeyVersion = entity.KeyVersion;
                    entity.PreviousPublicKey = entity.PublicKey;
                    entity.PreviousPublicKeyThumbprint = entity.PublicKeyThumbprint;
                    entity.PreviousKeyValidUntil = normalizedPreviousKeyValidUntil;
                    entity.KeyVersion = checked(entity.KeyVersion + 1);
                    entity.PublicKey = newPublicKey;
                    entity.PublicKeyThumbprint = newPublicKeyThumbprint;
                    entity.RotatedAt = normalizedRotatedAt;
                    AddAudit(
                        entity.OrganizationId,
                        entity.WorkspaceId,
                        entity.ConnectionId,
                        null,
                        entity.Id,
                        ExternalEngineEnrollmentAuditAction.KeyRotated,
                        ExternalEngineEnrollmentAuditReason.None,
                        normalizedRotatedAt);
                    await dbContext.SaveChangesAsync(cancellationToken);
                    return ToDomain(entity);
                },
                async (result, attemptCancellationToken) =>
                    result is not null
                    && await dbContext.ExternalEngineConnectorIdentities.AsNoTracking().AnyAsync(
                        x => x.OrganizationId == result.OrganizationId
                             && x.WorkspaceId == result.WorkspaceId
                             && x.ConnectionId == result.ConnectionId
                             && x.Id == result.Id
                             && x.KeyVersion == result.KeyVersion
                             && x.PublicKeyThumbprint == result.PublicKeyThumbprint,
                        attemptCancellationToken),
                cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            dbContext.ChangeTracker.Clear();
            return null;
        }
    }

    public async Task<bool> TryRevokeIdentityAsync(
        ExternalEngineConnectorIdentity expectedIdentity,
        DateTimeOffset revokedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedIdentity);
        var normalizedRevokedAt = revokedAt.ToUniversalTime();
        try
        {
            return await dbContext.ExecuteInTransactionAsync(
                IsolationLevel.Serializable,
                async () =>
                {
                    var entity = await dbContext.ExternalEngineConnectorIdentities.SingleOrDefaultAsync(
                        x => x.OrganizationId == expectedIdentity.OrganizationId
                             && x.WorkspaceId == expectedIdentity.WorkspaceId
                             && x.ConnectionId == expectedIdentity.ConnectionId
                             && x.Id == expectedIdentity.Id
                             && x.PublicKeyThumbprint == expectedIdentity.PublicKeyThumbprint,
                        cancellationToken);
                    if (entity is null || entity.RevokedAt is not null || entity.KeyVersion != expectedIdentity.KeyVersion)
                        return false;

                    entity.RevokedAt = normalizedRevokedAt;
                    AddAudit(
                        entity.OrganizationId,
                        entity.WorkspaceId,
                        entity.ConnectionId,
                        null,
                        entity.Id,
                        ExternalEngineEnrollmentAuditAction.IdentityRevoked,
                        ExternalEngineEnrollmentAuditReason.None,
                        normalizedRevokedAt);
                    await dbContext.SaveChangesAsync(cancellationToken);
                    return true;
                },
                async (result, attemptCancellationToken) =>
                    result
                    && await dbContext.ExternalEngineConnectorIdentities.AsNoTracking().AnyAsync(
                        x => x.OrganizationId == expectedIdentity.OrganizationId
                             && x.WorkspaceId == expectedIdentity.WorkspaceId
                             && x.ConnectionId == expectedIdentity.ConnectionId
                             && x.Id == expectedIdentity.Id
                             && x.KeyVersion == expectedIdentity.KeyVersion
                             && x.RevokedAt == normalizedRevokedAt,
                        attemptCancellationToken),
                cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            dbContext.ChangeTracker.Clear();
            return false;
        }
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
        else if (audit.Action == ExternalEngineEnrollmentAuditAction.ConnectorProofRejected)
        {
            if (audit.OrganizationId == Guid.Empty || audit.WorkspaceId == Guid.Empty
                || audit.ConnectionId == Guid.Empty || audit.IdentityId is null)
                return;

            var identityExists = await dbContext.ExternalEngineConnectorIdentities.AsNoTracking().AnyAsync(
                x => x.OrganizationId == audit.OrganizationId
                     && x.WorkspaceId == audit.WorkspaceId
                     && x.ConnectionId == audit.ConnectionId
                     && x.Id == audit.IdentityId,
                cancellationToken);
            if (!identityExists)
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
        try
        {
            return await dbContext.ExecuteInTransactionAsync(
                IsolationLevel.Serializable,
                async () =>
                {
                    await dbContext.ExternalEngineConnectorProofNonces
                        .Where(x => x.ExpiresAt <= nonce.ConsumedAt)
                        .ExecuteDeleteAsync(cancellationToken);

                    var identityAcceptsKey = await dbContext.ExternalEngineConnectorIdentities.AsNoTracking().AnyAsync(
                        x => x.OrganizationId == nonce.OrganizationId
                             && x.WorkspaceId == nonce.WorkspaceId
                             && x.ConnectionId == nonce.ConnectionId
                             && x.Id == nonce.IdentityId
                             && x.RevokedAt == null
                             && (x.KeyVersion == nonce.KeyVersion
                                 || x.PreviousKeyVersion == nonce.KeyVersion
                                 && x.PreviousKeyValidUntil > nonce.ConsumedAt),
                        cancellationToken);
                    if (!identityAcceptsKey)
                        return false;

                    dbContext.ExternalEngineConnectorProofNonces.Add(new ExternalEngineConnectorProofNonceEntity
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
                    });
                    AddAudit(
                        nonce.OrganizationId,
                        nonce.WorkspaceId,
                        nonce.ConnectionId,
                        null,
                        nonce.IdentityId,
                        ExternalEngineEnrollmentAuditAction.ProofNonceConsumed,
                        ExternalEngineEnrollmentAuditReason.None,
                        nonce.ConsumedAt);
                    await dbContext.SaveChangesAsync(cancellationToken);
                    return true;
                },
                async (result, attemptCancellationToken) =>
                    result
                    && await dbContext.ExternalEngineConnectorProofNonces.AsNoTracking().AnyAsync(
                        x => x.OrganizationId == nonce.OrganizationId
                             && x.WorkspaceId == nonce.WorkspaceId
                             && x.ConnectionId == nonce.ConnectionId
                             && x.IdentityId == nonce.IdentityId
                             && x.KeyVersion == nonce.KeyVersion
                             && x.NonceHash == nonce.NonceHash,
                        attemptCancellationToken),
                cancellationToken);
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

        var hasPreviousKey = identity.PreviousKeyVersion is not null
                             || identity.PreviousPublicKey is not null
                             || identity.PreviousPublicKeyThumbprint is not null
                             || identity.PreviousKeyValidUntil is not null;
        if (hasPreviousKey
            && (identity.PreviousKeyVersion is null or <= 0
                || identity.PreviousKeyVersion >= identity.KeyVersion
                || identity.PreviousPublicKey is null
                || identity.PreviousPublicKeyThumbprint is null
                || identity.PreviousKeyValidUntil is null))
            throw new ArgumentException("Connector previous-key metadata is invalid.", nameof(identity));
        if (identity.PreviousPublicKey is not null)
        {
            var expectedPreviousThumbprint = ExternalEngineEnrollmentProtocol.PublicKeyThumbprint(identity.PreviousPublicKey);
            if (!FixedTimeEquals(expectedPreviousThumbprint, identity.PreviousPublicKeyThumbprint!))
                throw new ArgumentException("Connector previous-key thumbprint is invalid.", nameof(identity));
        }
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
        EnrolledAt = identity.EnrolledAt.ToUniversalTime(),
        PreviousKeyVersion = identity.PreviousKeyVersion,
        PreviousPublicKey = identity.PreviousPublicKey,
        PreviousPublicKeyThumbprint = identity.PreviousPublicKeyThumbprint,
        PreviousKeyValidUntil = identity.PreviousKeyValidUntil?.ToUniversalTime(),
        RotatedAt = identity.RotatedAt?.ToUniversalTime(),
        RevokedAt = identity.RevokedAt?.ToUniversalTime()
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
        identity.EnrolledAt,
        identity.PreviousKeyVersion,
        identity.PreviousPublicKey,
        identity.PreviousPublicKeyThumbprint,
        identity.PreviousKeyValidUntil,
        identity.RotatedAt,
        identity.RevokedAt);

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
