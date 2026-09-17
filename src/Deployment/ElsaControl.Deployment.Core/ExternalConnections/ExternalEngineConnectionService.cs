using System.Security.Cryptography;
using System.Text;

namespace ElsaControl.Deployment.Core.ExternalConnections;

public sealed class ExternalEngineConnectionService(
    IExternalEngineConnectionStore connections,
    IExternalEngineEnrollmentStore enrollmentStore,
    ExternalEngineEnrollmentService enrollment,
    TimeProvider timeProvider)
{
    public const string AuthenticationOperation = "external-engine.identity.authenticate";
    private const string AuthenticationPayload = "elsa-control.external-engine-connector.authenticate.v1";

    public Task<IReadOnlyList<ExternalEngineConnection>> ListAsync(
        Guid organizationId,
        Guid workspaceId,
        CancellationToken cancellationToken = default) =>
        connections.ListAsync(organizationId, workspaceId, cancellationToken);

    public Task<ExternalEngineConnection?> FindAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid connectionId,
        CancellationToken cancellationToken = default) =>
        connections.FindAsync(organizationId, workspaceId, connectionId, cancellationToken);

    public async Task<ExternalEnginePairingAttempt> CreatePairingAsync(
        ExternalEngineConnectionCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateScope(request.OrganizationId, request.WorkspaceId);
        var displayName = NormalizeDisplayName(request.DisplayName);
        var idempotencyKey = NormalizeIdempotencyKey(request.IdempotencyKey);
        var digest = Digest(displayName);
        var now = timeProvider.GetUtcNow();
        var proposed = NewConnection(request.OrganizationId, request.WorkspaceId, displayName, now);
        var created = await connections.TryCreateAsync(proposed, idempotencyKey, digest, cancellationToken);
        if (!created.Succeeded)
            throw new ExternalEngineConnectionConflictException("The idempotency key was already used for another external engine connection request.");

        var connection = created.Connection!;
        if (connection.Status == ExternalEngineConnectionStatus.Revoked)
            throw new ExternalEngineConnectionConflictException("A disconnected external engine connection cannot be replayed.");

        var issue = await enrollment.IssueAsync(
            new ExternalEngineEnrollmentIssueRequest(connection.OrganizationId, connection.WorkspaceId, connection.Id),
            cancellationToken);
        connection = await connections.TrySetPairingChallengeAsync(connection, issue.ChallengeId, issue.IssuedAt, cancellationToken)
                     ?? throw ConcurrentPairingChange();
        return new ExternalEnginePairingAttempt(connection, issue, created.Replayed);
    }

    public async Task<ExternalEnginePairingProgress?> GetPairingProgressAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid connectionId,
        CancellationToken cancellationToken = default)
    {
        var connection = await connections.FindAsync(organizationId, workspaceId, connectionId, cancellationToken);
        if (connection is null)
            return null;
        if (connection.Status == ExternalEngineConnectionStatus.Revoked)
            return new(connection.Id, connection.LastChallengeId, ExternalEnginePairingState.Revoked, null, null, null);
        if (connection.ActiveIdentityId is not null)
            return new(connection.Id, connection.LastChallengeId, ExternalEnginePairingState.Redeemed, null, null, null);
        if (connection.LastChallengeId is not { } challengeId)
            return new(connection.Id, null, ExternalEnginePairingState.Waiting, null, null, null);

        var challenge = await enrollmentStore.FindChallengeAsync(
            organizationId, workspaceId, connectionId, challengeId, cancellationToken);
        if (challenge is null)
            return new(connection.Id, challengeId, ExternalEnginePairingState.Waiting, null, null, null);
        var state = challenge.RedeemedAt is not null
            ? ExternalEnginePairingState.Redeemed
            : challenge.ExpiresAt <= timeProvider.GetUtcNow()
                ? ExternalEnginePairingState.Expired
                : ExternalEnginePairingState.Waiting;
        return new(connection.Id, challenge.Id, state, challenge.IssuedAt, challenge.ExpiresAt, challenge.RedeemedAt);
    }

    public async Task<ExternalEnginePairingAttempt?> RepairAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid connectionId,
        CancellationToken cancellationToken = default)
    {
        var current = await connections.FindAsync(organizationId, workspaceId, connectionId, cancellationToken);
        if (current is null || current.Status == ExternalEngineConnectionStatus.Revoked)
            return null;

        DateTimeOffset? revokedAt = null;
        if (current.ActiveIdentityId is { } identityId)
        {
            var revoked = await RevokeForRepairAsync(
                organizationId, workspaceId, connectionId, identityId, cancellationToken);
            if (revoked is null)
                throw ConcurrentPairingChange();
            revokedAt = revoked.RevokedAt;
        }

        var pending = await connections.TryPrepareRepairAsync(current, timeProvider.GetUtcNow(), cancellationToken)
                      ?? throw ConcurrentPairingChange();
        var issue = await enrollment.IssueAsync(
            new ExternalEngineEnrollmentIssueRequest(
                organizationId,
                workspaceId,
                connectionId,
                IssuedAfter: revokedAt),
            cancellationToken);
        pending = await connections.TrySetPairingChallengeAsync(pending, issue.ChallengeId, issue.IssuedAt, cancellationToken)
                  ?? throw ConcurrentPairingChange();
        return new(pending, issue, false);
    }

    public async Task<ExternalEngineConnection?> DisconnectAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid connectionId,
        CancellationToken cancellationToken = default)
    {
        var current = await connections.FindAsync(organizationId, workspaceId, connectionId, cancellationToken);
        if (current is null)
            return null;
        if (current.Status == ExternalEngineConnectionStatus.Revoked)
            return current;
        if (current.ActiveIdentityId is { } identityId)
            await enrollment.RevokeConnectorForRecoveryAsync(organizationId, workspaceId, connectionId, identityId, cancellationToken);
        return await DisconnectCurrentAsync(current, cancellationToken);
    }

    public async Task<ExternalEngineEnrollmentRedeemResult?> RedeemAsync(
        ExternalEngineEnrollmentRedeemRequest request,
        CancellationToken cancellationToken = default)
    {
        var connection = await connections.FindAsync(
            request.OrganizationId, request.WorkspaceId, request.ConnectionId, cancellationToken);
        if (connection is null || connection.Status == ExternalEngineConnectionStatus.Revoked)
            return null;
        if (connection.LastChallengeId != request.ChallengeId)
            return ExternalEngineEnrollmentRedeemResult.Denied(ExternalEngineEnrollmentRedeemFailure.InvalidRequest);

        var result = await enrollment.RedeemAsync(request, cancellationToken);
        if (!result.Succeeded)
            return result;
        var linked = await connections.TrySetActiveIdentityAsync(
            connection, result.Identity!.Id, timeProvider.GetUtcNow(), cancellationToken);
        if (linked is not null)
            return result;

        await enrollment.RevokeConnectorForRecoveryAsync(
            request.OrganizationId,
            request.WorkspaceId,
            request.ConnectionId,
            result.Identity.Id,
            cancellationToken);
        return ExternalEngineEnrollmentRedeemResult.Denied(ExternalEngineEnrollmentRedeemFailure.InvalidRequest);
    }

    public async Task<ExternalEngineConnectorProofResult?> AuthenticateAsync(
        ExternalEngineConnectorProof proof,
        CancellationToken cancellationToken = default)
    {
        var connection = await connections.FindAsync(
            proof.OrganizationId, proof.WorkspaceId, proof.ConnectionId, cancellationToken);
        if (connection is null || connection.Status == ExternalEngineConnectionStatus.Revoked)
            return null;
        if (connection.ActiveIdentityId != proof.IdentityId)
            return ExternalEngineConnectorProofResult.Denied(ExternalEngineConnectorProofFailure.ScopeMismatch);
        return await enrollment.VerifyConnectorProofAsync(
            proof, AuthenticationOperation, AuthenticationPayloadDigest(), cancellationToken);
    }

    public async Task<ExternalEngineConnectorKeyRotationResult?> RotateAsync(
        ExternalEngineConnectorKeyRotationRequest request,
        CancellationToken cancellationToken = default)
    {
        var proof = request.Proof;
        var connection = await connections.FindAsync(
            proof.OrganizationId, proof.WorkspaceId, proof.ConnectionId, cancellationToken);
        if (connection is null || connection.Status == ExternalEngineConnectionStatus.Revoked)
            return null;
        if (connection.ActiveIdentityId != proof.IdentityId)
            return ExternalEngineConnectorKeyRotationResult.Denied(ExternalEngineConnectorProofFailure.ScopeMismatch);
        return await enrollment.RotateConnectorKeyAsync(request, cancellationToken);
    }

    public async Task<ExternalEngineConnectorProofResult?> RevokeAsync(
        ExternalEngineConnectorProof proof,
        CancellationToken cancellationToken = default)
    {
        var connection = await connections.FindAsync(
            proof.OrganizationId, proof.WorkspaceId, proof.ConnectionId, cancellationToken);
        if (connection is null || connection.Status == ExternalEngineConnectionStatus.Revoked)
            return null;
        if (connection.ActiveIdentityId != proof.IdentityId)
            return ExternalEngineConnectorProofResult.Denied(ExternalEngineConnectorProofFailure.ScopeMismatch);
        var result = await enrollment.RevokeConnectorAsync(proof, cancellationToken);
        if (result.Succeeded)
            await DisconnectCurrentAsync(connection, cancellationToken);
        return result;
    }

    public static string AuthenticationPayloadDigest() => Digest(AuthenticationPayload);

    private static ExternalEngineConnectionConflictException ConcurrentPairingChange() =>
        new("The external engine pairing changed concurrently. Retry to receive the current challenge.");

    private async Task<ExternalEngineConnectorIdentity?> RevokeForRepairAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid connectionId,
        Guid identityId,
        CancellationToken cancellationToken)
    {
        var revoked = await enrollment.RevokeConnectorForRecoveryAsync(
            organizationId, workspaceId, connectionId, identityId, cancellationToken);
        return revoked ?? await enrollment.RevokeConnectorForRecoveryAsync(
            organizationId, workspaceId, connectionId, identityId, cancellationToken);
    }

    private async Task<ExternalEngineConnection?> DisconnectCurrentAsync(
        ExternalEngineConnection current,
        CancellationToken cancellationToken)
    {
        var disconnected = await connections.TryDisconnectAsync(
            current, timeProvider.GetUtcNow(), cancellationToken);
        if (disconnected is not null)
            return disconnected;

        var latest = await connections.FindAsync(
            current.OrganizationId, current.WorkspaceId, current.Id, cancellationToken);
        if (latest is null || latest.Status == ExternalEngineConnectionStatus.Revoked)
            return latest;
        return await connections.TryDisconnectAsync(latest, timeProvider.GetUtcNow(), cancellationToken)
               ?? await connections.FindAsync(latest.OrganizationId, latest.WorkspaceId, latest.Id, cancellationToken);
    }

    private static ExternalEngineConnection NewConnection(
        Guid organizationId,
        Guid workspaceId,
        string displayName,
        DateTimeOffset now) =>
        new(
            Guid.NewGuid(), organizationId, workspaceId, displayName,
            ExternalEngineConnectionStatus.Pending,
            ExternalEngineRuntimeHealth.Unknown,
            ExternalEngineConnectorReachability.Unknown,
            null, null, null, null, null,
            ExternalEngineReleaseEvidenceLevel.None,
            null, [], null, null, null, now, now, null, 1);

    private static string NormalizeDisplayName(string value)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > 200 || normalized.Any(char.IsControl))
            throw new ArgumentException("Display name must contain between 1 and 200 safe characters.", nameof(value));
        return normalized;
    }

    private static string NormalizeIdempotencyKey(string value)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > 128 || normalized.Any(char.IsWhiteSpace) || normalized.Any(char.IsControl))
            throw new ArgumentException("A bounded idempotency key is required.", nameof(value));
        return normalized;
    }

    private static string Digest(string value) =>
        ExternalEngineEnrollmentProtocol.Base64UrlEncode(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static void ValidateScope(Guid organizationId, Guid workspaceId)
    {
        if (organizationId == Guid.Empty || workspaceId == Guid.Empty)
            throw new ArgumentException("Organization and workspace IDs are required.");
    }
}
