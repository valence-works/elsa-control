using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using ElsaControl.Api.Workspace;
using ElsaControl.Deployment.Core.ExternalConnections;
using ElsaControl.Deployment.Core.Workspace;
using ElsaControl.PackageCatalog.Core.Accounts;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ElsaControl.Api.Tests;

public sealed class ExternalEngineConnectionApiTests
{
    [Fact]
    public async Task Pairing_is_idempotent_and_list_read_progress_never_echo_the_challenge()
    {
        await using var app = new ControlApiTestApplication();
        await app.SeedAsync(_ => Task.CompletedTask);
        using var owner = app.CreateTrustedWorkspaceClient("external-connection-owner");
        var workspaceId = await owner.GetDefaultWorkspaceIdAsync();

        using var first = await CreateAsync(owner, workspaceId, "Customer engine", "pair-engine");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.True(first.Headers.CacheControl?.NoStore);
        var accepted = await first.Content.ReadControlJsonAsync<ExternalEnginePairingAttemptResponse>();
        Assert.NotNull(accepted);
        Assert.False(string.IsNullOrWhiteSpace(accepted.Enrollment.Challenge));
        Assert.Equal(ExternalEngineConnectionStatus.Pending, accepted.Connection.Status);
        Assert.Equal(ExternalEngineConnection.OwnershipMode, accepted.Connection.OwnershipMode);
        Assert.Empty(accepted.Connection.Capabilities);

        using var replay = await CreateAsync(owner, workspaceId, "Customer engine", "pair-engine");
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var replayed = await replay.Content.ReadControlJsonAsync<ExternalEnginePairingAttemptResponse>();
        Assert.Equal(accepted.Connection.Id, replayed!.Connection.Id);
        Assert.True(replayed.ReplayedConnection);
        Assert.NotEqual(accepted.Enrollment.ChallengeId, replayed.Enrollment.ChallengeId);
        Assert.NotEqual(accepted.Enrollment.Challenge, replayed.Enrollment.Challenge);

        var listJson = await owner.GetStringAsync($"/api/workspaces/{workspaceId:D}/external-engine-connections");
        var readJson = await owner.GetStringAsync($"/api/workspaces/{workspaceId:D}/external-engine-connections/{accepted.Connection.Id:D}");
        var progressJson = await owner.GetStringAsync($"/api/workspaces/{workspaceId:D}/external-engine-connections/{accepted.Connection.Id:D}/pairing");
        Assert.DoesNotContain(accepted.Enrollment.Challenge, listJson, StringComparison.Ordinal);
        Assert.DoesNotContain(replayed.Enrollment.Challenge, listJson, StringComparison.Ordinal);
        Assert.DoesNotContain(replayed.Enrollment.Challenge, readJson, StringComparison.Ordinal);
        Assert.DoesNotContain(replayed.Enrollment.Challenge, progressJson, StringComparison.Ordinal);
        Assert.DoesNotContain("activeIdentityId", listJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("lastChallengeId", listJson, StringComparison.OrdinalIgnoreCase);

        using var conflict = await CreateAsync(owner, workspaceId, "Different engine", "pair-engine");
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
    }

    [Fact]
    public async Task Browser_fields_cannot_assert_runtime_facts()
    {
        await using var app = new ControlApiTestApplication();
        await app.SeedAsync(_ => Task.CompletedTask);
        using var owner = app.CreateTrustedWorkspaceClient("external-connection-browser-fields");
        var workspaceId = await owner.GetDefaultWorkspaceIdAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/workspaces/{workspaceId:D}/external-engine-connections")
        {
            Content = JsonContent.Create(new
            {
                displayName = "Untrusted browser",
                status = "Connected",
                ownershipMode = "ValenceManaged",
                capabilities = new[] { "engine.delete", "engine.admin" },
                connectorProtocol = "forged",
                observedDistribution = "Valence Runtime",
                releaseEvidenceLevel = "VerifiedManifest",
                studioDestination = "https://attacker.example"
            })
        };
        request.Headers.Add("Idempotency-Key", "browser-cannot-attest");

        using var response = await owner.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var result = await response.Content.ReadControlJsonAsync<ExternalEnginePairingAttemptResponse>();
        Assert.Equal(ExternalEngineConnectionStatus.Pending, result!.Connection.Status);
        Assert.Equal(ExternalEngineConnection.OwnershipMode, result.Connection.OwnershipMode);
        Assert.Empty(result.Connection.Capabilities);
        Assert.Null(result.Connection.ConnectorProtocol);
        Assert.Null(result.Connection.ObservedDistribution);
        Assert.Equal(ExternalEngineReleaseEvidenceLevel.None, result.Connection.ReleaseEvidenceLevel);
        Assert.Null(result.Connection.StudioDestination);
    }

    [Fact]
    public async Task Superseded_pairing_challenge_cannot_enroll()
    {
        await using var app = new ControlApiTestApplication();
        await app.SeedAsync(_ => Task.CompletedTask);
        using var owner = app.CreateTrustedWorkspaceClient("external-stale-challenge");
        var context = await owner.GetControlJsonAsync<MeWorkspacesResponse>("/api/me/workspaces");
        var workspace = context!.Workspaces.Single();

        using var first = await CreateAsync(owner, workspace.Id, "Customer engine", "stale-challenge");
        var superseded = (await first.Content.ReadControlJsonAsync<ExternalEnginePairingAttemptResponse>())!;
        using var replay = await CreateAsync(owner, workspace.Id, "Customer engine", "stale-challenge");
        var current = (await replay.Content.ReadControlJsonAsync<ExternalEnginePairingAttemptResponse>())!;
        Assert.NotEqual(superseded.Enrollment.ChallengeId, current.Enrollment.ChallengeId);

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var redemption = Redemption(superseded, workspace.OrganizationId, workspace.Id, key);
        using var denied = await owner.PostControlJsonAsync(
            $"/api/runtime/external-engine-connections/{superseded.Connection.Id:D}/enrollment/redeem", redemption);

        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
    }

    [Fact]
    public async Task Read_and_manage_setup_permissions_are_separate_and_cross_workspace_access_fails()
    {
        await using var app = new ControlApiTestApplication();
        await app.SeedAsync(_ => Task.CompletedTask);
        using var owner = app.CreateTrustedWorkspaceClient("external-permission-owner");
        var workspaceId = await owner.GetDefaultWorkspaceIdAsync();
        var readerId = await app.AddWorkspaceMemberAsync(workspaceId, "external-permission-reader", WorkspaceRole.Reader);
        using var reader = app.CreateTrustedWorkspaceClient("external-permission-reader");

        using var deniedCreate = await CreateAsync(reader, workspaceId, "Denied", "reader-denied");
        Assert.Equal(HttpStatusCode.Forbidden, deniedCreate.StatusCode);

        using var grant = await owner.PostControlJsonAsync(
            $"/api/workspaces/{workspaceId:D}/permissions/grants",
            new WorkspacePermissionGrantRequest(readerId, WorkspaceDeploymentPermissions.Read));
        grant.EnsureSuccessStatusCode();
        using var allowedRead = await reader.GetAsync($"/api/workspaces/{workspaceId:D}/external-engine-connections");
        Assert.Equal(HttpStatusCode.OK, allowedRead.StatusCode);
        using var stillDeniedCreate = await CreateAsync(reader, workspaceId, "Denied", "reader-still-denied");
        Assert.Equal(HttpStatusCode.Forbidden, stillDeniedCreate.StatusCode);

        using var outsider = app.CreateTrustedWorkspaceClient("external-permission-outsider");
        _ = await outsider.GetDefaultWorkspaceIdAsync();
        using var crossWorkspace = await outsider.GetAsync($"/api/workspaces/{workspaceId:D}/external-engine-connections");
        Assert.Equal(HttpStatusCode.Forbidden, crossWorkspace.StatusCode);
    }

    [Fact]
    public async Task Disabled_organization_membership_and_disconnect_fail_closed_with_a_tombstone()
    {
        await using var app = new ControlApiTestApplication();
        await app.SeedAsync(_ => Task.CompletedTask);
        using var owner = app.CreateTrustedWorkspaceClient("external-disabled-owner");
        var context = await owner.GetControlJsonAsync<MeWorkspacesResponse>("/api/me/workspaces");
        var workspaceId = context!.Workspaces.Single().Id;
        using var created = await CreateAsync(owner, workspaceId, "Disconnect me", "disconnect-engine");
        var connection = (await created.Content.ReadControlJsonAsync<ExternalEnginePairingAttemptResponse>())!.Connection;

        using var disconnected = await owner.PostAsJsonAsync(
            $"/api/workspaces/{workspaceId:D}/external-engine-connections/{connection.Id:D}/disconnect",
            new { }, ControlApiTestApplication.JsonOptions);
        Assert.Equal(HttpStatusCode.OK, disconnected.StatusCode);
        var tombstone = await disconnected.Content.ReadControlJsonAsync<ExternalEngineConnectionResponse>();
        Assert.Equal(ExternalEngineConnectionStatus.Revoked, tombstone!.Status);
        Assert.NotNull(tombstone.RevokedAt);
        using var repeated = await owner.PostAsJsonAsync(
            $"/api/workspaces/{workspaceId:D}/external-engine-connections/{connection.Id:D}/disconnect",
            new { }, ControlApiTestApplication.JsonOptions);
        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            var membership = await db.OrganizationMemberships.SingleAsync(x => x.AccountId == context.Account.Id);
            membership.DisabledAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }
        using var denied = await owner.GetAsync($"/api/workspaces/{workspaceId:D}/external-engine-connections");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Fact]
    public async Task Runtime_redeem_authenticate_rotate_and_revoke_are_proof_bound_and_responses_are_safe()
    {
        await using var app = new ControlApiTestApplication();
        await app.SeedAsync(_ => Task.CompletedTask);
        using var owner = app.CreateTrustedWorkspaceClient("external-runtime-owner");
        var context = await owner.GetControlJsonAsync<MeWorkspacesResponse>("/api/me/workspaces");
        var workspaceId = context!.Workspaces.Single().Id;
        using var created = await CreateAsync(owner, workspaceId, "Runtime engine", "runtime-engine");
        var pairing = (await created.Content.ReadControlJsonAsync<ExternalEnginePairingAttemptResponse>())!;
        using var currentKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = ExternalEngineEnrollmentProtocol.ExportPublicKey(currentKey);
        var redemption = Redemption(
            pairing, context.Workspaces.Single().OrganizationId, workspaceId, currentKey);

        using var redeemed = await owner.PostControlJsonAsync(
            $"/api/runtime/external-engine-connections/{pairing.Connection.Id:D}/enrollment/redeem", redemption);
        Assert.Equal(HttpStatusCode.OK, redeemed.StatusCode);
        var identity = await redeemed.Content.ReadControlJsonAsync<ExternalEngineConnectorIdentityResponse>();
        Assert.NotNull(identity);
        var redeemedJson = await redeemed.Content.ReadAsStringAsync();
        Assert.DoesNotContain(pairing.Enrollment.Challenge, redeemedJson, StringComparison.Ordinal);
        Assert.DoesNotContain(publicKey, redeemedJson, StringComparison.Ordinal);
        Assert.DoesNotContain(redemption.Signature, redeemedJson, StringComparison.Ordinal);

        var authentication = Proof(
            identity!,
            context.Workspaces.Single().OrganizationId,
            workspaceId,
            pairing.Connection.Id,
            pairing.Enrollment.Audience,
            ExternalEngineConnectionService.AuthenticationOperation,
            ExternalEngineConnectionService.AuthenticationPayloadDigest(),
            currentKey);
        using var authenticated = await owner.PostControlJsonAsync(
            $"/api/runtime/external-engine-connections/{pairing.Connection.Id:D}/authenticate", authentication);
        Assert.Equal(HttpStatusCode.OK, authenticated.StatusCode);

        using var nextKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var nextPublicKey = ExternalEngineEnrollmentProtocol.ExportPublicKey(nextKey);
        var overlap = TimeSpan.FromMinutes(1);
        var rotationProof = Proof(
            identity,
            context.Workspaces.Single().OrganizationId,
            workspaceId,
            pairing.Connection.Id,
            pairing.Enrollment.Audience,
            ExternalEngineEnrollmentDefaults.RotationOperation,
            ExternalEngineEnrollmentProtocol.CreateRotationPayloadDigest(nextPublicKey, overlap),
            currentKey);
        using var rotated = await owner.PostControlJsonAsync(
            $"/api/runtime/external-engine-connections/{pairing.Connection.Id:D}/identity/rotate",
            new ExternalEngineConnectorKeyRotationRequest(rotationProof, nextPublicKey, overlap));
        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
        var rotatedIdentity = await rotated.Content.ReadControlJsonAsync<ExternalEngineConnectorIdentityResponse>();
        Assert.Equal(2, rotatedIdentity!.KeyVersion);
        Assert.DoesNotContain(nextPublicKey, await rotated.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var revocationProof = Proof(
            rotatedIdentity,
            context.Workspaces.Single().OrganizationId,
            workspaceId,
            pairing.Connection.Id,
            pairing.Enrollment.Audience,
            ExternalEngineEnrollmentDefaults.RevocationOperation,
            ExternalEngineEnrollmentProtocol.CreateRevocationPayloadDigest(),
            nextKey);
        using var revoked = await owner.PostControlJsonAsync(
            $"/api/runtime/external-engine-connections/{pairing.Connection.Id:D}/identity/revoke", revocationProof);
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        var tombstone = await owner.GetControlJsonAsync<ExternalEngineConnectionResponse>(
            $"/api/workspaces/{workspaceId:D}/external-engine-connections/{pairing.Connection.Id:D}");
        Assert.Equal(ExternalEngineConnectionStatus.Revoked, tombstone!.Status);

        var deniedProof = Proof(
            rotatedIdentity,
            context.Workspaces.Single().OrganizationId,
            workspaceId,
            pairing.Connection.Id,
            pairing.Enrollment.Audience,
            ExternalEngineConnectionService.AuthenticationOperation,
            ExternalEngineConnectionService.AuthenticationPayloadDigest(),
            nextKey);
        using var denied = await owner.PostControlJsonAsync(
            $"/api/runtime/external-engine-connections/{pairing.Connection.Id:D}/authenticate", deniedProof);
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
    }

    private static ExternalEngineConnectorProof Proof(
        ExternalEngineConnectorIdentityResponse identity,
        Guid organizationId,
        Guid workspaceId,
        Guid connectionId,
        string audience,
        string operation,
        string payloadDigest,
        ECDsa key)
    {
        var unsigned = new ExternalEngineConnectorProof(
            identity.IdentityId,
            organizationId,
            workspaceId,
            connectionId,
            audience,
            identity.KeyVersion,
            operation,
            payloadDigest,
            DateTimeOffset.UtcNow,
            ExternalEngineEnrollmentProtocol.Base64UrlEncode(RandomNumberGenerator.GetBytes(16)),
            "");
        return unsigned with
        {
            Signature = ExternalEngineEnrollmentProtocol.Sign(
                key,
                ExternalEngineEnrollmentProtocol.CreateConnectorProofPayload(unsigned))
        };
    }

    private static ExternalEngineEnrollmentRedeemRequest Redemption(
        ExternalEnginePairingAttemptResponse pairing,
        Guid organizationId,
        Guid workspaceId,
        ECDsa key)
    {
        var publicKey = ExternalEngineEnrollmentProtocol.ExportPublicKey(key);
        var unsigned = new ExternalEngineEnrollmentRedeemRequest(
            pairing.Enrollment.ChallengeId,
            organizationId,
            workspaceId,
            pairing.Connection.Id,
            pairing.Enrollment.Purpose,
            pairing.Enrollment.Audience,
            pairing.Enrollment.Challenge,
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

    private static Task<HttpResponseMessage> CreateAsync(
        HttpClient client,
        Guid workspaceId,
        string displayName,
        string idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/workspaces/{workspaceId:D}/external-engine-connections")
        {
            Content = JsonContent.Create(new CreateExternalEngineConnectionRequest(displayName),
                options: ControlApiTestApplication.JsonOptions)
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return client.SendAsync(request);
    }
}
