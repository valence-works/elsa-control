using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using ElsaControl.Api.Admin.Organizations;
using ElsaControl.Api.Authentication;
using ElsaControl.PackageCatalog.Core.Accounts;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ElsaControl.Api.Tests;

public sealed class AdminOrganizationApiTests : IClassFixture<AdminOrganizationApiTests.Fixture>, IAsyncLifetime
{
    private static readonly Guid OwnerAccountId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid OperatorAccountId = Guid.Parse("10000000-0000-0000-0000-000000000002");
    private readonly Fixture _fixture;
    private readonly ControlApiTestApplication _app;
    private readonly HttpClient _apiKeyClient;

    public AdminOrganizationApiTests(Fixture fixture)
    {
        _fixture = fixture;
        _app = fixture.Application;
        _apiKeyClient = _app.CreateClient();
        _apiKeyClient.DefaultRequestHeaders.Add(ApiKeyAuthenticationDefaults.HeaderName, "local-dev-key");
    }

    public async Task InitializeAsync() => await _app.SeedAsync(db =>
    {
        db.Accounts.AddRange(
            new Account { Id = OwnerAccountId, DisplayName = "Owner", Email = "owner@example.test" },
            new Account { Id = OperatorAccountId, DisplayName = "Operator", Email = "operator@example.test" });
        db.ExternalIdentities.Add(new ExternalIdentity
        {
            Issuer = ControlApiTestApplication.TestControlIdentityIssuer,
            Subject = "admin-user",
            AccountId = OperatorAccountId,
            DisplayName = "Operator",
            Email = "operator@example.test"
        });
        return Task.CompletedTask;
    });

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Api_key_admin_creates_named_organization_workspace_owner_and_audit_without_entitlement()
    {
        var response = await CreateAsync(_apiKeyClient, "Stripe dry-run", OwnerAccountId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var created = await response.Content.ReadControlJsonAsync<AdminOrganizationCreateResponse>();
        Assert.NotNull(created);
        Assert.Equal(OwnerAccountId, created.OwnerAccountId);

        var state = await ReadAsync(db => new
        {
            Organization = db.Organizations.AsNoTracking().Single(x => x.Id == created.OrganizationId),
            Workspace = db.Workspaces.AsNoTracking().Single(x => x.Id == created.WorkspaceId),
            OrganizationMembership = db.OrganizationMemberships.AsNoTracking().Single(x => x.OrganizationId == created.OrganizationId),
            WorkspaceMembership = db.WorkspaceMemberships.AsNoTracking().Single(x => x.WorkspaceId == created.WorkspaceId),
            Audit = db.OrganizationAuditRecords.AsNoTracking().Single(x => x.OrganizationId == created.OrganizationId),
            EntitlementCount = db.OrganizationEntitlementSnapshots.Count(x => x.OrganizationId == created.OrganizationId),
            SubscriptionCount = db.OrganizationSubscriptions.Count(x => x.OrganizationId == created.OrganizationId)
        });
        var internalEntitlement = await _apiKeyClient.GetControlJsonAsync<AdminInternalEntitlementResponse>(
            $"/api/admin/organizations/{created.OrganizationId}/internal-entitlement");

        Assert.Equal("Stripe dry-run", state.Organization.Name);
        Assert.Null(state.Organization.CreatedByAccountId);
        Assert.Equal(created.OrganizationId, state.Workspace.OrganizationId);
        Assert.Equal(WorkspaceKind.Shared, state.Workspace.Kind);
        Assert.Equal(OrganizationRole.Owner, state.OrganizationMembership.Role);
        Assert.Equal(WorkspaceRole.Owner, state.WorkspaceMembership.Role);
        Assert.Equal(OwnerAccountId, state.OrganizationMembership.AccountId);
        Assert.Equal(OwnerAccountId, state.WorkspaceMembership.AccountId);
        Assert.Equal(OrganizationAuditAction.OrganizationCreated, state.Audit.Action);
        Assert.Null(state.Audit.ActorAccountId);
        Assert.Equal(OrganizationInternalEntitlementPolicy.FingerprintOperatorSubject("api-key"), state.Audit.OperatorSubject);
        Assert.NotEqual(default, state.Audit.CreatedAt);
        Assert.Equal("organization", state.Audit.TargetType);
        Assert.Equal(created.OrganizationId.ToString("D"), state.Audit.TargetId);
        Assert.Contains(OwnerAccountId.ToString("D"), state.Audit.Summary, StringComparison.Ordinal);
        Assert.Contains(created.WorkspaceId.ToString("D"), state.Audit.Summary, StringComparison.Ordinal);
        Assert.Equal(0, state.EntitlementCount);
        Assert.Equal(0, state.SubscriptionCount);
        Assert.NotNull(internalEntitlement);
        Assert.Equal(OrganizationInternalEntitlementState.None, internalEntitlement!.State);
    }

    [Fact]
    public async Task Control_admin_cookie_defaults_owner_to_current_operator_account()
    {
        var client = _app.CreateClient();
        _app.AddControlSessionCookie(client, new Claim("role", AdminAuthorization.ControlAdminRole));
        client.DefaultRequestHeaders.Add("Origin", "http://localhost");

        var response = await CreateAsync(client, "Operator organization", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var created = await response.Content.ReadControlJsonAsync<AdminOrganizationCreateResponse>();
        Assert.Equal(OperatorAccountId, created!.OwnerAccountId);
        Assert.Equal(OperatorAccountId, (await ReadAsync(db => db.Organizations.AsNoTracking().Single(x => x.Id == created.OrganizationId).CreatedByAccountId)));
        var audit = await ReadAsync(db => db.OrganizationAuditRecords.AsNoTracking().Single(x => x.OrganizationId == created.OrganizationId));
        Assert.Equal(OperatorAccountId, audit.ActorAccountId);
        Assert.Equal(OrganizationInternalEntitlementPolicy.FingerprintOperatorSubject("admin-user"), audit.OperatorSubject);
        Assert.NotEqual(default, audit.CreatedAt);
        Assert.Equal("organization", audit.TargetType);
        Assert.Equal(created.OrganizationId.ToString("D"), audit.TargetId);
        Assert.Contains(created.WorkspaceId.ToString("D"), audit.Summary, StringComparison.Ordinal);
        Assert.Contains(OperatorAccountId.ToString("D"), audit.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Anonymous_post_is_unauthorized_without_rows()
    {
        var response = await CreateAsync(_app.CreateClient(), "Anonymous", OwnerAccountId);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertNoOrganizationRowsAsync();
    }

    [Fact]
    public async Task Control_admin_without_existing_account_cannot_default_owner_or_write_rows()
    {
        var client = _app.CreateClient();
        _app.AddControlSessionCookie(
            client,
            subject: "unprovisioned-admin",
            expiresUtc: DateTimeOffset.UtcNow.AddHours(1),
            new Claim("role", AdminAuthorization.ControlAdminRole));
        client.DefaultRequestHeaders.Add("Origin", "http://localhost");

        var response = await CreateAsync(client, "No account", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertNoOrganizationRowsAsync();
    }

    [Fact]
    public async Task Missing_name_is_rejected_without_rows()
    {
        var response = await CreateAsync(_apiKeyClient, " ", OwnerAccountId);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertNoOrganizationRowsAsync();
    }

    [Fact]
    public async Task Unknown_owner_is_rejected_without_rows()
    {
        var response = await CreateAsync(_apiKeyClient, "Unknown owner", Guid.NewGuid());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertNoOrganizationRowsAsync();
    }

    [Fact]
    public async Task Api_key_requires_explicit_owner_account()
    {
        var response = await CreateAsync(_apiKeyClient, "API-key organization", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertNoOrganizationRowsAsync();
    }

    [Fact]
    public async Task Non_admin_customer_is_forbidden_without_rows()
    {
        var client = _app.CreateClient();
        _app.AddControlSessionCookie(client, subject: "customer-user", expiresUtc: DateTimeOffset.UtcNow.AddHours(1));

        var response = await CreateAsync(client, "Denied", OwnerAccountId);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertNoOrganizationRowsAsync();
    }

    private static Task<HttpResponseMessage> CreateAsync(HttpClient client, string? name, Guid? ownerAccountId) =>
        client.PostAsJsonAsync("/api/admin/organizations", new { name, ownerAccountId }, ControlApiTestApplication.JsonOptions);

    private async Task AssertNoOrganizationRowsAsync()
    {
        var counts = await ReadAsync(db => new
        {
            Accounts = db.Accounts.Count(),
            ExternalIdentities = db.ExternalIdentities.Count(),
            Organizations = db.Organizations.Count(),
            Workspaces = db.Workspaces.Count(),
            Audits = db.OrganizationAuditRecords.Count()
        });
        Assert.Equal(2, counts.Accounts);
        Assert.Equal(1, counts.ExternalIdentities);
        Assert.Equal(0, counts.Organizations);
        Assert.Equal(0, counts.Workspaces);
        Assert.Equal(0, counts.Audits);
    }

    private async Task<T> ReadAsync<T>(Func<CatalogDbContext, T> query)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        return query(scope.ServiceProvider.GetRequiredService<CatalogDbContext>());
    }

    public sealed class Fixture : IAsyncLifetime
    {
        internal ControlApiTestApplication Application { get; } = new();
        public Task InitializeAsync() => Task.CompletedTask;
        public async Task DisposeAsync() => await ((IAsyncDisposable)Application).DisposeAsync();
    }
}
