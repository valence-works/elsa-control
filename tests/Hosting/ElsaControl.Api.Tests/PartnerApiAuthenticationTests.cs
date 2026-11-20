using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using ElsaControl.Api.Admin.Organizations;
using ElsaControl.Api.Authentication;
using ElsaControl.Api.Partner;
using ElsaControl.PackageCatalog.Core.Accounts;
using WorkspaceEntity = ElsaControl.PackageCatalog.Core.Accounts.Workspace;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ElsaControl.Api.Tests;

public sealed class PartnerApiAuthenticationTests
{
    public const string OpenApiRelativePath = "contracts/partner-api/openapi.v1.json";
    public const string RegenerateEnvironmentVariable = "ELSA_CONTROL_REGENERATE_PARTNER_API_OPENAPI";

    [Fact]
    public async Task Create_shows_the_secret_once_and_stores_only_the_sha256_hash()
    {
        await using var app = await SeededAsync();
        var created = await CreateKeyAsync(app, OrganizationA, ["engines:read"]);

        Assert.StartsWith("ecp_test_", created.Token, StringComparison.Ordinal);
        Assert.True(PartnerApiKeyFormat.TryParse(created.Token, out var credential));
        Assert.Equal(created.KeyId, credential.KeyId);

        await using var scope = app.Services.CreateAsyncScope();
        var stored = await scope.ServiceProvider.GetRequiredService<CatalogDbContext>().PartnerApiKeys.SingleAsync();
        Assert.Equal(PartnerApiKeyFormat.HashSecret(credential.Secret), stored.SecretHash);
        Assert.DoesNotContain(credential.Secret, stored.SecretHash, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await ListKeysAsync(app, OrganizationA)).StatusCode);
        var listed = await (await ListKeysAsync(app, OrganizationA)).Content.ReadControlJsonAsync<AdminPartnerApiKeyListItem[]>();
        Assert.DoesNotContain(credential.Secret, JsonSerializer.Serialize(listed), StringComparison.Ordinal);
        Assert.DoesNotContain(stored.SecretHash, JsonSerializer.Serialize(listed), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_partner_may_have_at_most_two_active_keys()
    {
        await using var app = await SeededAsync();
        Assert.Equal(HttpStatusCode.Created, (await CreateKeyRawAsync(app, OrganizationA, ["engines:read"])).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await CreateKeyRawAsync(app, OrganizationA, ["engines:write"])).StatusCode);

        var third = await CreateKeyRawAsync(app, OrganizationA, ["engines:delete"]);
        Assert.Equal(HttpStatusCode.Conflict, third.StatusCode);
        Assert.Equal("partner.key-limit", await ProblemCodeAsync(third));
        Assert.DoesNotContain("ecp_test_", await third.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Revoke_invalidates_every_instance_immediately_and_does_not_touch_engines()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"partner-api-keys-{Guid.NewGuid():N}.db");
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var revoker = new RecordingMintedTokenRevoker();
        await using var app1 = new ControlApiTestApplication(
            databasePath: databasePath,
            configureServices: services => Share(services, cache, revoker));
        await using var app2 = new ControlApiTestApplication(
            databasePath: databasePath,
            configureServices: services => Share(services, cache, revoker));
        await SeedAsync(app1);
        var created = await CreateKeyAsync(app1, OrganizationA, ["engines:read"]);

        Assert.Equal(HttpStatusCode.OK, (await CallAccountAsync(app1, created.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await CallAccountAsync(app2, created.Token)).StatusCode);
        var instancesBefore = await CountInstancesAsync(app1);

        var revoked = await RevokeAsync(app1, OrganizationA, created.KeyId);
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        Assert.Equal(created.KeyId, Assert.Single(revoker.RevokedKeyIds));

        var body1 = await CallAccountAsync(app1, created.Token);
        var body2 = await CallAccountAsync(app2, created.Token);
        await AssertIdenticalUnauthorizedAsync(body1, body2);
        Assert.Equal(instancesBefore, await CountInstancesAsync(app1));
        Assert.Equal(instancesBefore, await CountInstancesAsync(app2));
    }

    [Fact]
    public async Task Unknown_and_revoked_keys_return_the_same_401_and_never_echo_the_secret()
    {
        await using var app = await SeededAsync();
        var created = await CreateKeyAsync(app, OrganizationA, ["engines:read"]);
        await RevokeAsync(app, OrganizationA, created.KeyId);
        var unknown = PartnerApiKeyFormat.Issue("test").Token;

        var revoked = await CallAccountAsync(app, created.Token, "203.0.113.21");
        var missing = await CallAccountAsync(app, unknown, "203.0.113.22");
        await AssertIdenticalUnauthorizedAsync(revoked, missing);
        var revokedBody = await revoked.Content.ReadAsStringAsync();
        var missingBody = await missing.Content.ReadAsStringAsync();
        Assert.DoesNotContain(created.Token, revokedBody, StringComparison.Ordinal);
        Assert.DoesNotContain(unknown, missingBody, StringComparison.Ordinal);
        PartnerApiKeyFormat.TryParse(created.Token, out var credential);
        Assert.DoesNotContain(credential.Secret, revokedBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failed_attempts_are_throttled_per_ip_with_the_low_permit_fallback()
    {
        await using var app = new ControlApiTestApplication();
        await SeedAsync(app);
        var unknown = PartnerApiKeyFormat.Issue("test").Token;
        using var client = PartnerClient(app, unknown, "203.0.113.10");

        var first = await client.GetAsync("/partner/v1/account");
        var second = await client.GetAsync("/partner/v1/account");

        Assert.Equal(HttpStatusCode.Unauthorized, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        var problem = await second.Content.ReadControlJsonAsync<ProblemDetails>();
        Assert.Equal("rate-limit.exceeded", problem!.Extensions["code"]?.ToString());
        Assert.DoesNotContain(unknown, await second.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scopes_are_enforced_and_a_key_cannot_reach_non_partner_or_key_management_routes()
    {
        await using var app = await SeededAsync();
        var reader = await CreateKeyAsync(app, OrganizationA, ["engines:read"]);
        var writer = await CreateKeyAsync(app, OrganizationA, ["engines:write"]);

        Assert.Equal(HttpStatusCode.OK, (await CallAccountAsync(app, reader.Token)).StatusCode);
        var missingScope = await CallWorkspaceAsync(app, reader.Token);
        Assert.Equal(HttpStatusCode.Forbidden, missingScope.StatusCode);
        Assert.Equal(PartnerApiKeyDefaults.InsufficientScopeCode, await ProblemCodeAsync(missingScope));
        Assert.Equal(HttpStatusCode.OK, (await CallWorkspaceAsync(app, writer.Token)).StatusCode);

        using var admin = PartnerClient(app, writer.Token);
        var keyManagement = await admin.GetAsync($"/api/admin/organizations/{OrganizationA:D}/partner/keys");
        var workspace = await admin.GetAsync($"/api/workspaces/{WorkspaceA:D}/sources");
        var health = await admin.GetAsync("/health");
        Assert.Equal(HttpStatusCode.Forbidden, keyManagement.StatusCode);
        Assert.Equal(PartnerApiKeyDefaults.RouteForbiddenCode, await ProblemCodeAsync(keyManagement));
        Assert.Equal(HttpStatusCode.Forbidden, workspace.StatusCode);
        Assert.Equal(PartnerApiKeyDefaults.RouteForbiddenCode, await ProblemCodeAsync(health));
    }

    [Fact]
    public async Task Two_partners_cannot_see_each_other_and_foreign_ids_are_404()
    {
        await using var app = await SeededAsync();
        var alpha = await CreateKeyAsync(app, OrganizationA, ["engines:read", "engines:write"]);
        var beta = await CreateKeyAsync(app, OrganizationB, ["engines:read", "engines:write"]);

        var alphaAccount = await (await CallAccountAsync(app, alpha.Token)).Content.ReadControlJsonAsync<PartnerAccountResponse>();
        var betaAccount = await (await CallAccountAsync(app, beta.Token)).Content.ReadControlJsonAsync<PartnerAccountResponse>();
        Assert.Equal(OrganizationA, alphaAccount!.OrganizationId);
        Assert.Equal(OrganizationB, betaAccount!.OrganizationId);
        Assert.NotEqual(alphaAccount.WorkspaceId, betaAccount.WorkspaceId);

        using var alphaClient = PartnerClient(app, alpha.Token);
        var foreignEngine = await alphaClient.GetAsync($"/partner/v1/engines/{Guid.NewGuid():D}");
        var foreignKeys = await AdminClient(app).GetAsync($"/api/admin/organizations/{OrganizationB:D}/partner/keys");
        var alphaOnBeta = await RevokeAsync(app, OrganizationB, alpha.KeyId);
        Assert.Equal(HttpStatusCode.NotFound, foreignEngine.StatusCode);
        Assert.Equal(HttpStatusCode.OK, foreignKeys.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, alphaOnBeta.StatusCode);
        Assert.DoesNotContain(alpha.Token, await foreignEngine.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Audit_records_key_id_only_and_logs_never_contain_the_secret()
    {
        var entries = new ConcurrentQueue<string>();
        await using var app = new ControlApiTestApplication(
            configureServices: services => services.AddSingleton<ILoggerProvider>(new SecretRecordingLoggerProvider(entries)));
        await SeedAsync(app);
        var created = await CreateKeyAsync(app, OrganizationA, ["engines:read"]);
        Assert.True(PartnerApiKeyFormat.TryParse(created.Token, out var credential));
        await CallAccountAsync(app, created.Token);
        await RevokeAsync(app, OrganizationA, created.KeyId);
        await CallAccountAsync(app, created.Token, "203.0.113.23");

        await using var scope = app.Services.CreateAsyncScope();
        var audits = await scope.ServiceProvider.GetRequiredService<CatalogDbContext>().OrganizationAuditRecords
            .Where(x => x.Action == OrganizationAuditAction.PartnerApiKeyCreated || x.Action == OrganizationAuditAction.PartnerApiKeyRevoked)
            .ToListAsync();
        Assert.Equal(2, audits.Count);
        Assert.All(audits, audit =>
        {
            Assert.Equal(created.KeyId, audit.TargetId);
            Assert.DoesNotContain(credential.Secret, audit.Summary, StringComparison.Ordinal);
            Assert.DoesNotContain(created.Token, audit.Summary, StringComparison.Ordinal);
        });
        Assert.All(entries, message =>
        {
            Assert.DoesNotContain(credential.Secret, message, StringComparison.Ordinal);
            Assert.DoesNotContain(created.Token, message, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Partner_openapi_document_matches_the_committed_file_and_contains_no_prices_or_secrets()
    {
        await using var app = new ControlApiTestApplication();
        using var client = app.CreateClient();
        var response = await client.GetAsync($"/openapi/{PartnerApiKeyDefaults.OpenApiDocumentName}.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var actual = CanonicalJson(await response.Content.ReadAsStringAsync());
        var file = new FileInfo(Path.Combine(RepositoryRoot(), OpenApiRelativePath.Replace('/', Path.DirectorySeparatorChar)));
        var regenerate = IsTruthy(Environment.GetEnvironmentVariable(RegenerateEnvironmentVariable));
        if (regenerate && (IsTruthy(Environment.GetEnvironmentVariable("CI")) || IsTruthy(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"))))
            Assert.Fail("Partner OpenAPI regeneration is disabled in CI.");
        if (regenerate)
        {
            Directory.CreateDirectory(file.DirectoryName!);
            await File.WriteAllTextAsync(file.FullName, actual + Environment.NewLine);
        }

        Assert.True(file.Exists, $"Committed OpenAPI file is missing at {OpenApiRelativePath}.");
        Assert.Equal(CanonicalJson(await File.ReadAllTextAsync(file.FullName)), actual);
        Assert.DoesNotContain("price_", actual, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sk_test", actual, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ecp_test_", actual, StringComparison.Ordinal);
        Assert.Contains("/partner/v1/account", actual, StringComparison.Ordinal);
    }

    private static readonly Guid OrganizationA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid OrganizationB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid WorkspaceA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid WorkspaceB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static async Task<ControlApiTestApplication> SeededAsync()
    {
        var app = new ControlApiTestApplication();
        await SeedAsync(app);
        return app;
    }

    private static Task SeedAsync(ControlApiTestApplication app) =>
        app.SeedAsync(db =>
        {
            db.Organizations.Add(new Organization { Id = OrganizationA, Name = "Organization A" });
            db.Organizations.Add(new Organization { Id = OrganizationB, Name = "Organization B" });
            db.Workspaces.Add(new WorkspaceEntity { Id = WorkspaceA, OrganizationId = OrganizationA, Name = "Workspace A", Kind = WorkspaceKind.Shared });
            db.Workspaces.Add(new WorkspaceEntity { Id = WorkspaceB, OrganizationId = OrganizationB, Name = "Workspace B", Kind = WorkspaceKind.Shared });
            return Task.CompletedTask;
        });

    private static HttpClient AdminClient(ControlApiTestApplication app)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyAuthenticationDefaults.HeaderName, "local-dev-key");
        return client;
    }

    private static HttpClient PartnerClient(ControlApiTestApplication app, string token, string? ip = null)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (ip is not null)
            client.DefaultRequestHeaders.Add(ControlApiTestApplication.TestRemoteIpHeader, ip);
        return client;
    }

    private static async Task<AdminPartnerApiKeyCreatedResponse> CreateKeyAsync(
        ControlApiTestApplication app,
        Guid organizationId,
        IReadOnlyList<string> scopes)
    {
        var response = await CreateKeyRawAsync(app, organizationId, scopes);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadControlJsonAsync<AdminPartnerApiKeyCreatedResponse>())!;
    }

    private static Task<HttpResponseMessage> CreateKeyRawAsync(
        ControlApiTestApplication app,
        Guid organizationId,
        IReadOnlyList<string> scopes) =>
        AdminClient(app).PostControlJsonAsync(
            $"/api/admin/organizations/{organizationId:D}/partner/keys",
            new { name = "primary", scopes });

    private static Task<HttpResponseMessage> ListKeysAsync(ControlApiTestApplication app, Guid organizationId) =>
        AdminClient(app).GetAsync($"/api/admin/organizations/{organizationId:D}/partner/keys");

    private static Task<HttpResponseMessage> RevokeAsync(ControlApiTestApplication app, Guid organizationId, string keyId) =>
        AdminClient(app).PostAsJsonAsync(
            $"/api/admin/organizations/{organizationId:D}/partner/keys/{keyId}/revoke",
            new { });

    private static Task<HttpResponseMessage> CallAccountAsync(
        ControlApiTestApplication app,
        string token,
        string? ip = null) =>
        PartnerClient(app, token, ip).GetAsync("/partner/v1/account");

    private static Task<HttpResponseMessage> CallWorkspaceAsync(ControlApiTestApplication app, string token) =>
        PartnerClient(app, token).GetAsync("/partner/v1/account/workspace");

    private static async Task AssertIdenticalUnauthorizedAsync(HttpResponseMessage left, HttpResponseMessage right)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, left.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, right.StatusCode);
        Assert.Equal("application/problem+json", left.Content.Headers.ContentType?.MediaType);
        Assert.Equal(PartnerApiKeyDefaults.UnauthorizedCode, await ProblemCodeAsync(left));
        Assert.Equal(await CanonicalProblemAsync(left), await CanonicalProblemAsync(right));
    }

    private static async Task<string> CanonicalProblemAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        return CanonicalJson(new
        {
            status = root.GetProperty("status").GetInt32(),
            title = root.GetProperty("title").GetString(),
            code = root.GetProperty("code").GetString()
        });
    }

    private static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    private static async Task<int> CountInstancesAsync(ControlApiTestApplication app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<CatalogDbContext>().ElsaInstances.CountAsync();
    }

    private static void Share(IServiceCollection services, IDistributedCache cache, RecordingMintedTokenRevoker revoker)
    {
        services.RemoveAll<IDistributedCache>();
        services.AddSingleton(cache);
        services.RemoveAll<IPartnerMintedTokenRevoker>();
        services.AddSingleton<IPartnerMintedTokenRevoker>(revoker);
    }

    private static string CanonicalJson(object value) =>
        CanonicalJson(JsonSerializer.Serialize(value, ControlApiTestApplication.JsonOptions));

    private static string CanonicalJson(string json)
    {
        var node = JsonNode.Parse(json) ?? throw new InvalidOperationException("JSON was empty.");
        return node.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ElsaControl.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Could not locate repository root.");
    }

    private static bool IsTruthy(string? value) =>
        string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    private sealed class RecordingMintedTokenRevoker : IPartnerMintedTokenRevoker
    {
        public ConcurrentQueue<string> RevokedKeyIds { get; } = [];

        public Task RevokeTokensMintedByKeyAsync(string keyId, CancellationToken cancellationToken = default)
        {
            RevokedKeyIds.Enqueue(keyId);
            return Task.CompletedTask;
        }
    }

    private sealed class SecretRecordingLoggerProvider(ConcurrentQueue<string> entries) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Logger(entries);

        public void Dispose()
        {
        }

        private sealed class Logger(ConcurrentQueue<string> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                entries.Enqueue(formatter(state, exception) + exception);
        }
    }
}
