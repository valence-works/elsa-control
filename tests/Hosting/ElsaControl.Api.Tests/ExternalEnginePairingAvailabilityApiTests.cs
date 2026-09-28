using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using ElsaControl.Api.Workspace;
using ElsaControl.Deployment.Core.ExternalConnections;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ElsaControl.Api.Tests;

public sealed class ExternalEnginePairingAvailabilityApiTests
{
    [Fact]
    public async Task Empty_or_missing_allowlist_refuses_create_and_repair_without_side_effects()
    {
        await using var app = new ControlApiTestApplication();
        app.ExternalEnginePairing.AllowAll = false;
        await app.SeedAsync(_ => Task.CompletedTask);
        using var owner = app.CreateTrustedWorkspaceClient("pairing-gate-empty");
        using var bearer = app.CreateControlIdentityClient("pairing-gate-empty-token");
        var workspace = (await owner.GetControlJsonAsync<MeWorkspacesResponse>("/api/me/workspaces"))!
            .Workspaces.Single();
        var bearerWorkspace = (await bearer.GetControlJsonAsync<MeWorkspacesResponse>("/api/me/workspaces"))!
            .Workspaces.Single();

        using var create = await CreateAsync(owner, workspace.Id, "Refused engine", "empty-create");
        using var bearerCreate = await CreateAsync(bearer, bearerWorkspace.Id, "Refused token engine", "empty-token-create");
        using var repair = await owner.PostAsJsonAsync(
            $"/api/workspaces/{workspace.Id:D}/external-engine-connections/{Guid.NewGuid():D}/repair",
            new { }, ControlApiTestApplication.JsonOptions);

        await AssertPairingUnavailableAsync(create);
        await AssertPairingUnavailableAsync(bearerCreate);
        await AssertPairingUnavailableAsync(repair);
        await AssertNoPairingSideEffectsAsync(app, workspace.OrganizationId, workspace.Id);
        await AssertNoPairingSideEffectsAsync(app, bearerWorkspace.OrganizationId, bearerWorkspace.Id);
    }

    [Fact]
    public async Task Unlisted_organization_cannot_create_or_repair_and_creates_nothing()
    {
        await using var app = new ControlApiTestApplication(new Dictionary<string, string?>
        {
            [$"{ExternalEngineOptions.ConfigurationSection}:PairingAllowedOrganizationIds:0"] = Guid.NewGuid().ToString("D")
        });
        app.ExternalEnginePairing.AllowAll = false;
        await app.SeedAsync(_ => Task.CompletedTask);
        using var owner = app.CreateTrustedWorkspaceClient("pairing-gate-unlisted");
        var workspace = (await owner.GetControlJsonAsync<MeWorkspacesResponse>("/api/me/workspaces"))!
            .Workspaces.Single();

        using var create = await CreateAsync(owner, workspace.Id, "Unlisted engine", "unlisted-create");
        using var repair = await owner.PostAsJsonAsync(
            $"/api/workspaces/{workspace.Id:D}/external-engine-connections/{Guid.NewGuid():D}/repair",
            new { }, ControlApiTestApplication.JsonOptions);

        await AssertPairingUnavailableAsync(create);
        await AssertPairingUnavailableAsync(repair);
        await AssertNoPairingSideEffectsAsync(app, workspace.OrganizationId, workspace.Id);
    }

    [Fact]
    public async Task Allowlisted_organization_can_create_and_repair()
    {
        await using var app = new ControlApiTestApplication();
        app.ExternalEnginePairing.AllowAll = false;
        await app.SeedAsync(_ => Task.CompletedTask);
        using var owner = app.CreateTrustedWorkspaceClient("pairing-gate-allowlisted");
        var workspace = (await owner.GetControlJsonAsync<MeWorkspacesResponse>("/api/me/workspaces"))!
            .Workspaces.Single();
        app.ExternalEnginePairing.Allow(workspace.OrganizationId);

        using var created = await CreateAsync(owner, workspace.Id, "Allowlisted engine", "allowlisted-create");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var pairing = await created.Content.ReadControlJsonAsync<ExternalEnginePairingAttemptResponse>();
        Assert.NotNull(pairing);
        Assert.False(string.IsNullOrWhiteSpace(pairing.Enrollment.Challenge));

        using var repaired = await owner.PostAsJsonAsync(
            $"/api/workspaces/{workspace.Id:D}/external-engine-connections/{pairing.Connection.Id:D}/repair",
            new { }, ControlApiTestApplication.JsonOptions);
        Assert.Equal(HttpStatusCode.OK, repaired.StatusCode);
        var repairedPairing = await repaired.Content.ReadControlJsonAsync<ExternalEnginePairingAttemptResponse>();
        Assert.NotNull(repairedPairing);
        Assert.Equal(pairing.Connection.Id, repairedPairing.Connection.Id);
        Assert.NotEqual(pairing.Enrollment.ChallengeId, repairedPairing.Enrollment.ChallengeId);
        Assert.False(string.IsNullOrWhiteSpace(repairedPairing.Enrollment.Challenge));
    }

    [Fact]
    public async Task Non_member_still_receives_the_workspace_denial_not_the_pairing_code()
    {
        await using var app = new ControlApiTestApplication();
        app.ExternalEnginePairing.AllowAll = false;
        await app.SeedAsync(_ => Task.CompletedTask);
        using var owner = app.CreateTrustedWorkspaceClient("pairing-gate-owner");
        var workspaceId = await owner.GetDefaultWorkspaceIdAsync();
        using var outsider = app.CreateTrustedWorkspaceClient("pairing-gate-outsider");
        _ = await outsider.GetDefaultWorkspaceIdAsync();

        using var create = await CreateAsync(outsider, workspaceId, "Cross workspace", "cross-create");
        using var repair = await outsider.PostAsJsonAsync(
            $"/api/workspaces/{workspaceId:D}/external-engine-connections/{Guid.NewGuid():D}/repair",
            new { }, ControlApiTestApplication.JsonOptions);

        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, repair.StatusCode);
        Assert.DoesNotContain(
            ExternalEngineDefaults.PairingUnavailableCode,
            await create.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            ExternalEngineDefaults.PairingUnavailableCode,
            await repair.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
        var ownerWorkspace = (await owner.GetControlJsonAsync<MeWorkspacesResponse>("/api/me/workspaces"))!
            .Workspaces.Single();
        await AssertNoPairingSideEffectsAsync(app, ownerWorkspace.OrganizationId, ownerWorkspace.Id);
    }

    [Fact]
    public async Task Existing_connection_in_an_unlisted_organization_can_still_be_listed_fetched_disconnected_and_revoked()
    {
        await using var app = new ControlApiTestApplication();
        await app.SeedAsync(_ => Task.CompletedTask);
        using var owner = app.CreateTrustedWorkspaceClient("pairing-gate-existing");
        var context = await owner.GetControlJsonAsync<MeWorkspacesResponse>("/api/me/workspaces");
        var workspace = context!.Workspaces.Single();
        using var created = await CreateAsync(owner, workspace.Id, "Existing engine", "existing-create");
        var pairing = (await created.Content.ReadControlJsonAsync<ExternalEnginePairingAttemptResponse>())!;
        using var currentKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var redemption = Redemption(pairing, workspace.OrganizationId, workspace.Id, currentKey);
        using var redeemed = await owner.PostControlJsonAsync(
            $"/api/runtime/external-engine-connections/{pairing.Connection.Id:D}/enrollment/redeem",
            redemption);
        Assert.Equal(HttpStatusCode.OK, redeemed.StatusCode);
        var identity = (await redeemed.Content.ReadControlJsonAsync<ExternalEngineConnectorIdentityResponse>())!;

        app.ExternalEnginePairing.AllowAll = false;
        app.ExternalEnginePairing.ClearAllowed();

        using var list = await owner.GetAsync($"/api/workspaces/{workspace.Id:D}/external-engine-connections");
        using var get = await owner.GetAsync(
            $"/api/workspaces/{workspace.Id:D}/external-engine-connections/{pairing.Connection.Id:D}");
        using var progress = await owner.GetAsync(
            $"/api/workspaces/{workspace.Id:D}/external-engine-connections/{pairing.Connection.Id:D}/pairing");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(HttpStatusCode.OK, progress.StatusCode);

        var authentication = Proof(
            identity,
            workspace.OrganizationId,
            workspace.Id,
            pairing.Connection.Id,
            pairing.Enrollment.Audience,
            ExternalEngineConnectionService.AuthenticationOperation,
            ExternalEngineConnectionService.AuthenticationPayloadDigest(),
            currentKey);
        using var authenticated = await owner.PostControlJsonAsync(
            $"/api/runtime/external-engine-connections/{pairing.Connection.Id:D}/authenticate",
            authentication);
        Assert.Equal(HttpStatusCode.OK, authenticated.StatusCode);

        var revokeProof = Proof(
            identity,
            workspace.OrganizationId,
            workspace.Id,
            pairing.Connection.Id,
            pairing.Enrollment.Audience,
            ExternalEngineEnrollmentDefaults.RevocationOperation,
            ExternalEngineEnrollmentProtocol.CreateRevocationPayloadDigest(),
            currentKey);
        using var revoked = await owner.PostControlJsonAsync(
            $"/api/runtime/external-engine-connections/{pairing.Connection.Id:D}/identity/revoke",
            revokeProof);
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);

        using var createdAfterRemoval = await CreateAsync(owner, workspace.Id, "Blocked after removal", "blocked-after");
        await AssertPairingUnavailableAsync(createdAfterRemoval);

        using var repairedAfterRemoval = await owner.PostAsJsonAsync(
            $"/api/workspaces/{workspace.Id:D}/external-engine-connections/{pairing.Connection.Id:D}/repair",
            new { }, ControlApiTestApplication.JsonOptions);
        await AssertPairingUnavailableAsync(repairedAfterRemoval);

        using var disconnected = await owner.PostAsJsonAsync(
            $"/api/workspaces/{workspace.Id:D}/external-engine-connections/{pairing.Connection.Id:D}/disconnect",
            new { }, ControlApiTestApplication.JsonOptions);
        Assert.Equal(HttpStatusCode.OK, disconnected.StatusCode);
        var tombstone = await disconnected.Content.ReadControlJsonAsync<ExternalEngineConnectionResponse>();
        Assert.Equal(ExternalEngineConnectionStatus.Revoked, tombstone!.Status);
    }

    [Fact]
    public async Task Repair_against_a_real_connection_in_an_unlisted_organization_is_refused()
    {
        await using var app = new ControlApiTestApplication();
        await app.SeedAsync(_ => Task.CompletedTask);
        using var owner = app.CreateTrustedWorkspaceClient("pairing-gate-repair-unlisted");
        var workspace = (await owner.GetControlJsonAsync<MeWorkspacesResponse>("/api/me/workspaces"))!
            .Workspaces.Single();
        using var created = await CreateAsync(owner, workspace.Id, "Repair target", "repair-unlisted-create");
        var pairing = (await created.Content.ReadControlJsonAsync<ExternalEnginePairingAttemptResponse>())!;

        using var progressBefore = await owner.GetAsync(
            $"/api/workspaces/{workspace.Id:D}/external-engine-connections/{pairing.Connection.Id:D}/pairing");
        Assert.Equal(HttpStatusCode.OK, progressBefore.StatusCode);
        var before = await progressBefore.Content.ReadControlJsonAsync<ExternalEnginePairingProgressResponse>();

        app.ExternalEnginePairing.AllowAll = false;
        app.ExternalEnginePairing.ClearAllowed();

        using var repair = await owner.PostAsJsonAsync(
            $"/api/workspaces/{workspace.Id:D}/external-engine-connections/{pairing.Connection.Id:D}/repair",
            new { }, ControlApiTestApplication.JsonOptions);
        await AssertPairingUnavailableAsync(repair);

        using var progressAfter = await owner.GetAsync(
            $"/api/workspaces/{workspace.Id:D}/external-engine-connections/{pairing.Connection.Id:D}/pairing");
        Assert.Equal(HttpStatusCode.OK, progressAfter.StatusCode);
        var after = await progressAfter.Content.ReadControlJsonAsync<ExternalEnginePairingProgressResponse>();
        Assert.Equal(before!.ChallengeId, after!.ChallengeId);
        Assert.Equal(before.State, after.State);
        Assert.Equal(before.IssuedAt, after.IssuedAt);
        Assert.Equal(pairing.Enrollment.ChallengeId, after.ChallengeId);

        await using var scope = app.Services.CreateAsyncScope();
        var connections = scope.ServiceProvider.GetRequiredService<IExternalEngineConnectionStore>();
        var remaining = await connections.ListAsync(workspace.OrganizationId, workspace.Id);
        Assert.Single(remaining);
        Assert.Equal(pairing.Connection.Id, remaining[0].Id);

        var enrollment = scope.ServiceProvider.GetRequiredService<IExternalEngineEnrollmentStore>();
        var challenge = await enrollment.FindChallengeAsync(
            workspace.OrganizationId,
            workspace.Id,
            pairing.Connection.Id,
            pairing.Enrollment.ChallengeId);
        Assert.NotNull(challenge);
        Assert.Equal(pairing.Enrollment.ChallengeId, challenge.Id);
    }

    [Fact]
    public async Task Configured_allowlist_is_honored_without_the_test_override()
    {
        var allowedOrganizationId = Guid.NewGuid();
        await using var app = new ControlApiTestApplication(new Dictionary<string, string?>
        {
            [$"{ExternalEngineOptions.ConfigurationSection}:PairingAllowedOrganizationIds:0"] =
                allowedOrganizationId.ToString("D")
        });
        app.ExternalEnginePairing.AllowAll = false;
        await app.SeedAsync(_ => Task.CompletedTask);

        Assert.True(app.Services.GetRequiredService<IOptions<ExternalEngineOptions>>().Value
            .AllowsPairing(allowedOrganizationId));
        Assert.False(app.Services.GetRequiredService<IOptions<ExternalEngineOptions>>().Value
            .AllowsPairing(Guid.NewGuid()));
        Assert.True(app.Services.GetRequiredService<IExternalEnginePairingAvailability>()
            .IsPairingAllowed(allowedOrganizationId));
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData(" 11111111-1111-1111-1111-111111111111")]
    public async Task Malformed_organization_id_fails_startup_validation(string value)
    {
        var validator = new ExternalEngineConfigurationValidator(Options.Create(new ExternalEngineOptions
        {
            PairingAllowedOrganizationIds = [value]
        }));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            validator.StartAsync(CancellationToken.None));

        Assert.Contains("PairingAllowedOrganizationIds", exception.Message, StringComparison.Ordinal);
        Assert.Contains("GUID", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Host_startup_fails_closed_for_a_malformed_allowlist_entry()
    {
        await using var app = new ControlApiTestApplication(new Dictionary<string, string?>
        {
            [$"{ExternalEngineOptions.ConfigurationSection}:PairingAllowedOrganizationIds:0"] = "not-a-guid"
        });

        var exception = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            _ = app.Services;
            await Task.CompletedTask;
        });

        Assert.Contains(
            "External engine configuration is invalid",
            FlattenException(exception),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_allowlist_refuses_every_organization()
    {
        var options = new ExternalEngineOptions();
        Assert.Empty(options.Validate());
        Assert.False(options.AllowsPairing(Guid.NewGuid()));
        Assert.False(options.AllowsPairing(Guid.Empty));
    }

    private static async Task AssertPairingUnavailableAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var problem = await response.Content.ReadControlJsonAsync<ProblemDetails>();
        Assert.Equal(ExternalEngineDefaults.PairingUnavailableCode, problem!.Extensions["code"]?.ToString());
        Assert.Equal("External engine pairing is not available.", problem.Title);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(document.RootElement.TryGetProperty("detail", out var detail) &&
                     detail.ValueKind == JsonValueKind.String &&
                     (detail.GetString()!.Contains("organization", StringComparison.OrdinalIgnoreCase) ||
                      detail.GetString()!.Contains("connection", StringComparison.OrdinalIgnoreCase)));
    }

    private static async Task AssertNoPairingSideEffectsAsync(
        ControlApiTestApplication app,
        Guid organizationId,
        Guid workspaceId)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var connections = scope.ServiceProvider.GetRequiredService<IExternalEngineConnectionStore>();
        Assert.Empty(await connections.ListAsync(organizationId, workspaceId));
    }

    private static string FlattenException(Exception exception)
    {
        var messages = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException)
            messages.Add(current.Message);
        if (exception is AggregateException aggregate)
        {
            foreach (var inner in aggregate.Flatten().InnerExceptions)
                messages.Add(inner.Message);
        }

        return string.Join(" ", messages);
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
