using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ElsaControl.Api.Authentication;
using ElsaControl.Api.OrganizationDeployments;
using ElsaControl.Api.Workspace;
using ElsaControl.PackageCatalog.Core.Accounts;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ElsaControl.Api.Tests;

public sealed class OrganizationDeploymentAuditApiTests : IAsyncLifetime
{
    private const string BffClientId = "elsa-cloud-lovable-bff";
    private readonly InMemoryOrganizationDeploymentAuditStore _store = new();
    private readonly ControlApiTestApplication _app;

    public OrganizationDeploymentAuditApiTests()
    {
        _app = new(
            new Dictionary<string, string?>
            {
                [$"{CloudBffOptions.ConfigurationSection}:Enabled"] = "true",
                [$"{CloudBffOptions.ConfigurationSection}:ClientId"] = BffClientId,
                [$"{CloudBffOptions.ConfigurationSection}:Scope"] = CloudBffDefaults.DefaultScope
            },
            services =>
            {
                services.RemoveAll<IOrganizationDeploymentAuditStore>();
                services.AddSingleton<IOrganizationDeploymentAuditStore>(_store);
            });
    }

    [Fact]
    public async Task Empty_organization_returns_a_no_store_empty_page()
    {
        await _app.SeedAsync(_ => Task.CompletedTask);
        var owner = CreateBffClient("audit-empty-owner");
        var organizationId = await OrganizationIdAsync(owner);

        using var response = await owner.GetAsync($"/api/organizations/{organizationId:D}/deployments/audit");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Contains("no-cache", response.Headers.Pragma.ToString(), StringComparison.OrdinalIgnoreCase);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        AssertExactPageShape(document.RootElement, page: 1, pageSize: 50, totalCount: 0);
        Assert.Empty(document.RootElement.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Member_receives_newest_first_sanitized_items()
    {
        await _app.SeedAsync(_ => Task.CompletedTask);
        var owner = CreateBffClient("audit-member-owner");
        var organizationId = await OrganizationIdAsync(owner);
        await AddMemberAsync(organizationId, "audit-member", OrganizationRole.Member);
        var older = Record(organizationId, "older-deploy", "2026-09-19T09:00:00Z", "staging", "failed");
        var newer = Record(organizationId, "newer-deploy", "2026-09-20T10:15:30Z", "production", "succeeded");
        var leaky = Record(
            organizationId,
            "cus_hosted_owner",
            "2026-09-21T11:00:00Z",
            "production",
            "succeeded",
            functionName: "ops@elsacloud.app",
            sourceRevision: "https://user:token@github.com/org/repo",
            scope: ["sk_live_secret", "Ada Lovelace"]);
        _store.Seed(organizationId, leaky, older, newer);
        var member = CreateBffClient("audit-member");

        using var response = await member.GetAsync(
            $"/api/organizations/{organizationId:D}/deployments/audit?page=1&pageSize=50");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadControlJsonAsync<OrganizationDeploymentAuditPageResponse>();
        Assert.NotNull(page);
        Assert.Equal(1, page!.Page);
        Assert.Equal(50, page.PageSize);
        Assert.Equal(2, page.TotalCount);
        Assert.Equal(["newer-deploy", "older-deploy"], page.Items.Select(item => item.Id).ToArray());
        Assert.Equal("2026-09-20T10:15:30Z", page.Items[0].OccurredAt);
        Assert.Equal(["function-only", "control-bff", "no-frontend-publish"], page.Items[0].ApprovedScope);
        var serialized = await response.Content.ReadAsStringAsync();
        AssertNoLeak(serialized);
        using var document = JsonDocument.Parse(serialized);
        AssertExactItemShape(document.RootElement.GetProperty("items")[0]);
    }

    [Fact]
    public async Task Pagination_uses_the_requested_page()
    {
        await _app.SeedAsync(_ => Task.CompletedTask);
        var owner = CreateBffClient("audit-page-owner");
        var organizationId = await OrganizationIdAsync(owner);
        _store.Seed(
            organizationId,
            Record(organizationId, "first", "2026-09-20T10:00:00Z"),
            Record(organizationId, "second", "2026-09-20T11:00:00Z"),
            Record(organizationId, "third", "2026-09-20T12:00:00Z"));

        using var response = await owner.GetAsync(
            $"/api/organizations/{organizationId:D}/deployments/audit?page=2&pageSize=2");

        var page = await response.Content.ReadControlJsonAsync<OrganizationDeploymentAuditPageResponse>();
        Assert.Equal(2, page!.Page);
        Assert.Equal(2, page.PageSize);
        Assert.Equal(3, page.TotalCount);
        Assert.Equal(["first"], page.Items.Select(item => item.Id).ToArray());
    }

    [Theory]
    [InlineData(0, 50)]
    [InlineData(1, 0)]
    [InlineData(1, 101)]
    public async Task Invalid_page_arguments_return_problem_details(int page, int pageSize)
    {
        await _app.SeedAsync(_ => Task.CompletedTask);
        var owner = CreateBffClient($"audit-invalid-{page}-{pageSize}");
        var organizationId = await OrganizationIdAsync(owner);

        using var response = await owner.GetAsync(
            $"/api/organizations/{organizationId:D}/deployments/audit?page={page}&pageSize={pageSize}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadControlJsonAsync<ProblemDetails>();
        Assert.Equal("deployments.audit-invalid", problem!.Extensions["code"]!.ToString());
        AssertNoLeak(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Non_member_is_concealed_as_not_found()
    {
        await _app.SeedAsync(_ => Task.CompletedTask);
        var owner = CreateBffClient("audit-tenant-owner");
        var stranger = CreateBffClient("audit-tenant-stranger");
        var organizationId = await OrganizationIdAsync(owner);
        _store.Seed(organizationId, Record(organizationId, "hidden-deploy", "2026-09-20T10:15:30Z"));

        using var response = await stranger.GetAsync($"/api/organizations/{organizationId:D}/deployments/audit");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(
            "organization.not-found",
            (await response.Content.ReadFromJsonAsync<Dictionary<string, string>>())!["code"]);
        AssertNoLeak(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Anonymous_and_admin_api_key_cannot_use_the_customer_route()
    {
        await _app.SeedAsync(_ => Task.CompletedTask);
        var owner = CreateBffClient("audit-admin-owner");
        var organizationId = await OrganizationIdAsync(owner);
        using var anonymous = _app.CreateClient();
        using var admin = _app.CreateClient();
        admin.DefaultRequestHeaders.Add(ApiKeyAuthenticationDefaults.HeaderName, "local-dev-key");

        using var anonymousResponse = await anonymous.GetAsync(
            $"/api/organizations/{organizationId:D}/deployments/audit");
        using var adminResponse = await admin.GetAsync(
            $"/api/organizations/{organizationId:D}/deployments/audit");

        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, adminResponse.StatusCode);
    }

    [Fact]
    public async Task Ordinary_control_bearer_can_read_the_same_membership_contract()
    {
        await _app.SeedAsync(_ => Task.CompletedTask);
        using var owner = _app.CreateControlIdentityClient(subject: "audit-ordinary-owner");
        var organizationId = await OrganizationIdAsync(owner);

        using var response = await owner.GetAsync($"/api/organizations/{organizationId:D}/deployments/audit");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadControlJsonAsync<OrganizationDeploymentAuditPageResponse>();
        Assert.Empty(page!.Items);
        Assert.Equal(0, page.TotalCount);
    }

    [Fact]
    public async Task Store_failures_use_problem_details_without_upstream_leakage()
    {
        await using var app = new ControlApiTestApplication(
            new Dictionary<string, string?>
            {
                [$"{CloudBffOptions.ConfigurationSection}:Enabled"] = "true",
                [$"{CloudBffOptions.ConfigurationSection}:ClientId"] = BffClientId,
                [$"{CloudBffOptions.ConfigurationSection}:Scope"] = CloudBffDefaults.DefaultScope
            },
            services =>
            {
                services.RemoveAll<IOrganizationDeploymentAuditStore>();
                services.AddSingleton<IOrganizationDeploymentAuditStore>(new FailingOrganizationDeploymentAuditStore());
            });
        await app.SeedAsync(_ => Task.CompletedTask);
        var owner = app.CreateControlIdentityClient(
            subject: "audit-fail-owner",
            claims: new Dictionary<string, string>
            {
                ["azp"] = BffClientId,
                ["scp"] = CloudBffDefaults.DefaultScope
            });
        var organizationId = await OrganizationIdAsync(owner);

        using var response = await owner.GetAsync($"/api/organizations/{organizationId:D}/deployments/audit");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var problem = JsonSerializer.Deserialize<ProblemDetails>(body, ControlApiTestApplication.JsonOptions);
        Assert.Equal("deployments.audit-unavailable", problem!.Extensions["code"]!.ToString());
        AssertNoLeak(body);
        Assert.DoesNotContain("sk_live_upstream", body, StringComparison.Ordinal);
        Assert.DoesNotContain("stripe", body, StringComparison.OrdinalIgnoreCase);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await ((IAsyncDisposable)_app).DisposeAsync();

    private HttpClient CreateBffClient(string subject) =>
        _app.CreateControlIdentityClient(
            subject: subject,
            claims: new Dictionary<string, string>
            {
                ["azp"] = BffClientId,
                ["scp"] = CloudBffDefaults.DefaultScope
            });

    private static async Task<Guid> OrganizationIdAsync(HttpClient client) =>
        (await client.GetControlJsonAsync<MeWorkspacesResponse>("/api/me/workspaces"))!.Organizations.Single().Id;

    private async Task AddMemberAsync(Guid organizationId, string subject, OrganizationRole role)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var account = new Account { DisplayName = subject, Email = $"{subject}@example.test" };
        account.ExternalIdentities.Add(new ExternalIdentity
        {
            Account = account,
            Issuer = ControlApiTestApplication.TestControlIdentityIssuer,
            Subject = subject,
            DisplayName = subject,
            Email = account.Email
        });
        db.Accounts.Add(account);
        db.OrganizationMemberships.Add(new OrganizationMembership
        {
            Account = account,
            OrganizationId = organizationId,
            Role = role
        });
        await db.SaveChangesAsync();
    }

    private static OrganizationDeploymentAuditRecord Record(
        Guid organizationId,
        string id,
        string occurredAt,
        string environment = "production",
        string outcome = "succeeded",
        string functionName = "control-bff",
        string sourceRevision = "78bcf459aa11bb22cc33dd44ee55ff6677889900",
        IReadOnlyList<string>? scope = null) =>
        new(
            organizationId,
            id,
            functionName,
            sourceRevision,
            environment,
            DateTimeOffset.Parse(occurredAt),
            scope ?? ["function-only", "control-bff", "no-frontend-publish"],
            outcome);

    private static void AssertExactPageShape(JsonElement root, int page, int pageSize, int totalCount)
    {
        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        Assert.Equal(["items", "page", "pageSize", "totalCount"],
            root.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal(page, root.GetProperty("page").GetInt32());
        Assert.Equal(pageSize, root.GetProperty("pageSize").GetInt32());
        Assert.Equal(totalCount, root.GetProperty("totalCount").GetInt32());
    }

    private static void AssertExactItemShape(JsonElement item)
    {
        Assert.Equal(
        [
            "id",
            "functionName",
            "sourceRevision",
            "targetEnvironment",
            "occurredAt",
            "approvedScope",
            "outcome"
        ],
            item.EnumerateObject().Select(property => property.Name).ToArray());
    }

    private static void AssertNoLeak(string payload)
    {
        Assert.DoesNotContain("sk_live", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rk_live", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cus_", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sub_", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@example.test", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("elsacloud.app", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("203.0.113.", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("Ada Lovelace", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("actor", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("email", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("connectionString", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("user:token", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("github.com", payload, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class FailingOrganizationDeploymentAuditStore : IOrganizationDeploymentAuditStore
    {
        public Task<IReadOnlyList<OrganizationDeploymentAuditRecord>> ListAsync(
            Guid organizationId,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "upstream stripe cus_hosted_owner ada@example.test 203.0.113.10 sk_live_upstream");
    }
}
