using System.Net;
using ElsaControl.Api.Workspace;
using ElsaControl.Deployment.Core.Provisioning;
using Microsoft.Extensions.DependencyInjection;

namespace ElsaControl.Api.Tests;

public sealed class EngineProvisioningApiTests
{
    [Fact]
    public async Task Providers_are_empty_when_no_provisioning_module_is_composed()
    {
        await using var app = new ControlApiTestApplication();
        await app.SeedAsync(_ => Task.CompletedTask);
        using var client = app.CreateTrustedWorkspaceClient("provisioning-discovery-empty");
        var workspaceId = await client.GetDefaultWorkspaceIdAsync();

        using var response = await client.GetAsync($"/api/workspaces/{workspaceId}/engine-provisioning/providers");
        var body = await response.Content.ReadControlJsonAsync<EngineProvisioningProvidersResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(body);
        Assert.Empty(body!.Providers);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("no-cache", response.Headers.Pragma.ToString());
    }

    [Fact]
    public async Task Providers_expose_descriptors_from_composed_modules()
    {
        await using var app = new ControlApiTestApplication(configureServices: services =>
            services.AddSingleton<IEngineProvisioningModule, TestProvisioningModule>());
        await app.SeedAsync(_ => Task.CompletedTask);
        using var client = app.CreateTrustedWorkspaceClient("provisioning-discovery-enabled");
        var workspaceId = await client.GetDefaultWorkspaceIdAsync();

        using var response = await client.GetAsync($"/api/workspaces/{workspaceId}/engine-provisioning/providers");
        var body = await response.Content.ReadControlJsonAsync<EngineProvisioningProvidersResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var provider = Assert.Single(body!.Providers);
        Assert.Equal("test", provider.Id);
        Assert.Equal("Test provider", provider.DisplayName);
    }

    [Fact]
    public async Task Providers_reject_anonymous_callers()
    {
        await using var app = new ControlApiTestApplication();
        await app.SeedAsync(_ => Task.CompletedTask);
        using var client = app.CreateClient();
        var workspaceId = Guid.NewGuid();

        using var response = await client.GetAsync($"/api/workspaces/{workspaceId}/engine-provisioning/providers");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private sealed class TestProvisioningModule : IEngineProvisioningModule
    {
        public string Id => "test";

        public string DisplayName => "Test provider";
    }
}
