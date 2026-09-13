using System.Net;
using System.Security.Claims;
using ElsaControl.Api.Authentication;
using ElsaControl.Api.Workspace;
using ElsaControl.PackageCatalog.Core.Accounts;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace ElsaControl.Api.Tests;

/// <summary>
/// Multi-tenant Microsoft Entra sign-in: each token is validated against its own tenant's
/// issuer, customer accounts are keyed by tenant issuer and object id, a customer tenant's first
/// sign-in mints its organization, and Valence dogfood tenants keep their existing identity.
/// </summary>
public sealed class EntraMultiTenantIdentityTests :
    IClassFixture<EntraMultiTenantIdentityTests.Fixture>,
    IAsyncLifetime
{
    private const string Section = ControlIdentityDefaults.ConfigurationSection;
    private const string CustomerTenantId = "0b6f5c4e-2d8a-4f3b-9c1e-7a5d3e2f1b0c";
    private const string OtherTenantId = "5d2e8f1a-3b4c-4d5e-8f6a-7b8c9d0e1f2a";
    private const string DogfoodTenantId = "c3a1e2d4-5f6b-4c7d-8e9f-0a1b2c3d4e5f";
    private const string ConsumerTenantId = "9188040d-6c67-4c5b-b112-36a304b66dad";

    private readonly ControlApiTestApplication _app;

    public EntraMultiTenantIdentityTests(Fixture fixture) => _app = fixture.Application;

    public static TheoryData<string, string?, string?> ForeignTenantTokens => new()
    {
        { EntraMultiTenantIdentity.IssuerFor(OtherTenantId), CustomerTenantId, "customer-oid" },
        { $"https://sts.windows.net/{CustomerTenantId}/", CustomerTenantId, "customer-oid" },
        { EntraMultiTenantIdentity.IssuerFor(CustomerTenantId), null, "customer-oid" },
        { EntraMultiTenantIdentity.IssuerFor(CustomerTenantId), "", "customer-oid" },
        { EntraMultiTenantIdentity.IssuerFor(ConsumerTenantId), ConsumerTenantId, "customer-oid" },
        { "https://login.microsoftonline.com/contoso/v2.0", "contoso", "customer-oid" },
        { EntraMultiTenantIdentity.IssuerFor(CustomerTenantId), CustomerTenantId, null }
    };

    public static TheoryData<string, string?, string> UnsafeMultiTenantSettings => new()
    {
        { $"{Section}:Provider", nameof(ControlIdentityProviderKind.GenericOidc), "requires Provider MicrosoftEntra" },
        { $"{Section}:Issuer", EntraMultiTenantIdentity.IssuerFor(DogfoodTenantId), "Issuer must be empty" },
        { $"{Section}:Entra:DogfoodTenantIds:0", "valence", "DogfoodTenantIds must contain" },
        { $"{Section}:Entra:DogfoodTenantIds:0", ConsumerTenantId, "DogfoodTenantIds must contain" },
        { "Authentication:Admin:AllowAuthenticatedCustomerSession", "true", "AllowAuthenticatedCustomerSession must be false" }
    };

    public Task InitializeAsync() => _app.SeedAsync(_ => Task.CompletedTask);

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Foreign_tenant_sign_in_mints_its_organization_with_the_binder_as_owner()
    {
        var response = await MeAsync(EntraClient(CustomerTenantId, "binder-oid"));

        var organization = Assert.Single(response.Organizations);
        var workspace = Assert.Single(response.Workspaces);
        Assert.Equal(OrganizationRole.Owner, organization.Role);
        Assert.Equal((WorkspaceKind.Shared, WorkspaceRole.Owner, organization.Id), (workspace.Kind, workspace.Role, workspace.OrganizationId));
        var binding = await QueryAsync(db => db.OrganizationIdentityBindings.SingleAsync());
        Assert.Equal((organization.Id, CustomerTenantId), (binding.OrganizationId, binding.EntraTenantId));
    }

    [Fact]
    public async Task Second_user_from_the_same_tenant_joins_the_tenant_organization()
    {
        var binder = await MeAsync(EntraClient(CustomerTenantId, "binder-oid"));

        var member = await MeAsync(EntraClient(CustomerTenantId, "member-oid"));

        Assert.Equal(new OrganizationContextResponse(binder.Organizations.Single().Id, binder.Organizations.Single().Name, OrganizationRole.Member), Assert.Single(member.Organizations));
        Assert.Empty(member.Workspaces);
        Assert.Equal(1, await QueryAsync(db => db.Organizations.CountAsync()));
    }

    [Fact]
    public async Task Customer_account_is_keyed_by_tenant_issuer_and_object_id_never_by_display_claims()
    {
        var first = await MeAsync(EntraClient(CustomerTenantId, "binder-oid", subject: "pairwise-sub"));

        var renamed = await MeAsync(EntraClient(CustomerTenantId, "binder-oid", subject: "rotated-sub", email: "ada.lovelace@fabrikam.example"));

        var identity = await QueryAsync(db => db.ExternalIdentities.SingleAsync());
        Assert.Equal((EntraMultiTenantIdentity.IssuerFor(CustomerTenantId), "binder-oid"), (identity.Issuer, identity.Subject));
        Assert.Equal(first.Account.Id, renamed.Account.Id);
        Assert.Equal("ada.lovelace@fabrikam.example", renamed.Account.Email);
    }

    [Theory]
    [MemberData(nameof(ForeignTenantTokens))]
    public async Task Token_that_does_not_prove_its_own_work_or_school_tenant_is_rejected(string issuer, string? tenantId, string? objectId)
    {
        var claims = new Dictionary<string, string> { ["name"] = "Mallory", ["email"] = "mallory@contoso.example" };
        if (tenantId is not null)
            claims[EntraMultiTenantIdentity.TenantIdClaim] = tenantId;
        if (objectId is not null)
            claims[EntraMultiTenantIdentity.ObjectIdClaim] = objectId;

        var response = await _app.CreateControlIdentityClient("pairwise-sub", issuer, claims: claims).GetAsync("/api/me/organizations");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, await QueryAsync(db => db.Accounts.CountAsync()));
    }

    [Fact]
    public async Task Dogfood_tenant_keeps_its_subject_keyed_identity_and_personal_workspace()
    {
        var response = await MeAsync(EntraClient(DogfoodTenantId, "dogfood-oid", subject: "dogfood-sub"));

        var identity = await QueryAsync(db => db.ExternalIdentities.SingleAsync());
        Assert.Equal((EntraMultiTenantIdentity.IssuerFor(DogfoodTenantId), "dogfood-sub"), (identity.Issuer, identity.Subject));
        Assert.Equal(WorkspaceKind.Personal, Assert.Single(response.Workspaces).Kind);
        Assert.Equal(0, await QueryAsync(db => db.OrganizationIdentityBindings.CountAsync()));
    }

    [Theory]
    [InlineData(DogfoodTenantId, true, HttpStatusCode.NotFound)]
    [InlineData(CustomerTenantId, false, HttpStatusCode.Forbidden)]
    public async Task Operator_role_is_honored_only_from_a_dogfood_tenant(string tenantId, bool isAdmin, HttpStatusCode adminApiStatus)
    {
        var client = _app.CreateClient();
        _app.AddControlSessionCookie(
            client,
            new Claim(EntraMultiTenantIdentity.TenantIdClaim, tenantId),
            new Claim(EntraMultiTenantIdentity.ObjectIdClaim, "operator-oid"),
            new Claim("roles", AdminAuthorization.ControlAdminRole));

        var session = await client.GetControlJsonAsync<CustomerAuthSessionResponse>(CustomerAuthenticationDefaults.SessionPath);
        var adminApi = await client.GetAsync($"/api/admin/organizations/{Guid.NewGuid()}/internal-entitlement");

        Assert.Equal(isAdmin, session!.IsAdmin);
        Assert.Equal(adminApiStatus, adminApi.StatusCode);
    }

    [Fact]
    public void Customer_oidc_validates_each_token_against_its_own_tenant_issuer()
    {
        var options = new OpenIdConnectOptions();
        CustomerOidcOptionsConfigurator.Configure(options, _app.Services.GetRequiredService<IOptions<ControlIdentityOptions>>().Value);
        var parameters = options.TokenValidationParameters;
        var token = new JsonWebToken(new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Claims = new Dictionary<string, object> { [EntraMultiTenantIdentity.TenantIdClaim] = CustomerTenantId }
        }));

        Assert.True(parameters.ValidateIssuer);
        Assert.Null(parameters.ValidIssuer);
        Assert.Equal(EntraMultiTenantIdentity.IssuerFor(CustomerTenantId), parameters.IssuerValidator(EntraMultiTenantIdentity.IssuerFor(CustomerTenantId), token, parameters));
        Assert.Throws<SecurityTokenInvalidIssuerException>(() => parameters.IssuerValidator(EntraMultiTenantIdentity.IssuerFor(OtherTenantId), token, parameters));
    }

    [Theory]
    [MemberData(nameof(UnsafeMultiTenantSettings))]
    public async Task Multi_tenant_configuration_refuses_unsafe_settings(string key, string? value, string expectedError)
    {
        var settings = new Dictionary<string, string?>(Fixture.MultiTenantConfiguration) { [key] = value };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var validator = new ControlIdentityConfigurationValidator(
            new HostingEnvironment { EnvironmentName = "Testing" },
            configuration,
            Options.Create(configuration.GetSection(Section).Get<ControlIdentityOptions>()!));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => validator.StartAsync(CancellationToken.None));

        Assert.Contains(expectedError, error.Message, StringComparison.Ordinal);
    }

    private HttpClient EntraClient(
        string tenantId,
        string objectId,
        string subject = "pairwise-sub",
        string email = "ada@contoso.example") =>
        _app.CreateControlIdentityClient(subject, EntraMultiTenantIdentity.IssuerFor(tenantId), claims: new Dictionary<string, string>
        {
            [EntraMultiTenantIdentity.TenantIdClaim] = tenantId,
            [EntraMultiTenantIdentity.ObjectIdClaim] = objectId,
            ["name"] = "Ada",
            ["email"] = email
        });

    private static async Task<MeWorkspacesResponse> MeAsync(HttpClient client) =>
        (await client.GetControlJsonAsync<MeWorkspacesResponse>("/api/me/organizations"))!;

    private async Task<T> QueryAsync<T>(Func<CatalogDbContext, Task<T>> query)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<CatalogDbContext>());
    }

    public sealed class Fixture : IAsyncLifetime
    {
        public static readonly IReadOnlyDictionary<string, string?> MultiTenantConfiguration = new Dictionary<string, string?>
        {
            [$"{Section}:Provider"] = nameof(ControlIdentityProviderKind.MicrosoftEntra),
            [$"{Section}:Issuer"] = "",
            [$"{Section}:Entra:MultiTenant"] = "true",
            [$"{Section}:Entra:DogfoodTenantIds:0"] = DogfoodTenantId
        };

        internal ControlApiTestApplication Application { get; } = new(MultiTenantConfiguration);

        public Task InitializeAsync() => Task.CompletedTask;

        public async Task DisposeAsync() => await ((IAsyncDisposable)Application).DisposeAsync();
    }
}
