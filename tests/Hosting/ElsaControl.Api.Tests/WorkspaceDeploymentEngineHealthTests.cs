using System.Net;
using ElsaControl.Api.Workspace;
using ElsaControl.Deployment.Core.Cockpit;
using ElsaControl.Deployment.Core.Workspace;
using ElsaControl.PackageCatalog.Core.Accounts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ElsaControl.Api.Tests;

public sealed class WorkspaceDeploymentEngineHealthTests
{
    [Fact]
    public async Task Register_engine_verifies_health_immediately()
    {
        await using var app = new ControlApiTestApplication(configureServices: services =>
        {
            services.RemoveAll<IEngineHealthProbe>();
            services.AddSingleton<IEngineHealthProbe>(new StubProbe(new EngineHealthProbeResult(
                true,
                "Elsa 4.2.0",
                CertificateStatus.Trusted,
                CredentialVerificationStatus.Verified,
                "Endpoint responded successfully.")));
        });
        await app.SeedAsync(_ => Task.CompletedTask);
        var owner = app.CreateTrustedWorkspaceClient("register-health-owner");
        var workspaceId = await owner.GetDefaultWorkspaceIdAsync();
        var environment = await SeedEnvironmentAsync(app, workspaceId);

        var response = await owner.PostControlJsonAsync(
            $"/api/workspaces/{workspaceId}/deployments/environments/{environment.Id}/engines",
            EngineRequest("claims-prod"));
        var created = await response.Content.ReadControlJsonAsync<WorkspaceWorkflowEngine>();
        var cockpit = await owner.GetControlJsonAsync<DeploymentCockpit>($"/api/workspaces/{workspaceId}/deployments/cockpit");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(DeploymentHealth.Healthy, created!.Health);
        Assert.NotNull(created.LastVerificationAt);
        Assert.Single(cockpit!.Engines, x =>
            x.Name == "claims-prod"
            && x.Health == DeploymentHealth.Healthy
            && x.Endpoint.Version == "Elsa 4.2.0"
            && x.LastVerificationAt != null
            && x.VerificationMessage == "Endpoint responded successfully.");
    }

    [Fact]
    public async Task Update_engine_verifies_health_immediately()
    {
        await using var app = new ControlApiTestApplication(configureServices: services =>
        {
            services.RemoveAll<IEngineHealthProbe>();
            services.AddSingleton<IEngineHealthProbe>(new StubProbe(new EngineHealthProbeResult(
                true,
                "Elsa 4.3.0",
                CertificateStatus.Trusted,
                CredentialVerificationStatus.Verified,
                "Endpoint responded successfully.")));
        });
        await app.SeedAsync(_ => Task.CompletedTask);
        var owner = app.CreateTrustedWorkspaceClient("update-health-owner");
        var workspaceId = await owner.GetDefaultWorkspaceIdAsync();
        var engine = await SeedEngineAsync(app, workspaceId);

        var response = await owner.PutControlJsonAsync(
            $"/api/workspaces/{workspaceId}/deployments/engines/{engine.Id}",
            EngineRequest("claims-prod-updated"));
        var updated = await response.Content.ReadControlJsonAsync<WorkspaceWorkflowEngine>();
        var cockpit = await owner.GetControlJsonAsync<DeploymentCockpit>($"/api/workspaces/{workspaceId}/deployments/cockpit");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(DeploymentHealth.Healthy, updated!.Health);
        Assert.NotNull(updated.LastVerificationAt);
        Assert.Single(cockpit!.Engines, x =>
            x.Id == engine.Id.ToString("D")
            && x.Name == "claims-prod-updated"
            && x.Health == DeploymentHealth.Healthy
            && x.Endpoint.Version == "Elsa 4.3.0"
            && x.LastVerificationAt != null
            && x.VerificationMessage == "Endpoint responded successfully.");
    }

    [Fact]
    public async Task Owner_can_verify_engine_health_and_reload_cockpit_metadata()
    {
        await using var app = new ControlApiTestApplication(configureServices: services =>
        {
            services.RemoveAll<IEngineHealthProbe>();
            services.AddSingleton<IEngineHealthProbe>(new StubProbe(new EngineHealthProbeResult(
                true,
                "Elsa 4.1.0",
                CertificateStatus.Trusted,
                CredentialVerificationStatus.Verified,
                "Endpoint responded successfully.")));
        });
        await app.SeedAsync(_ => Task.CompletedTask);
        var owner = app.CreateTrustedWorkspaceClient("health-owner");
        var workspaceId = await owner.GetDefaultWorkspaceIdAsync();
        var engine = await SeedEngineAsync(app, workspaceId);

        var response = await owner.PostAsync($"/api/workspaces/{workspaceId}/deployments/engines/{engine.Id}/verify", null);
        var result = await response.Content.ReadControlJsonAsync<EngineHealthResult>();
        var cockpit = await owner.GetControlJsonAsync<DeploymentCockpit>($"/api/workspaces/{workspaceId}/deployments/cockpit");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(DeploymentHealth.Healthy, result!.Health);
        Assert.Equal("Elsa 4.1.0", result.Version);
        Assert.Single(cockpit!.Engines, x =>
            x.Id == engine.Id.ToString("D")
            && x.Health == DeploymentHealth.Healthy
            && x.VerificationMessage == "Endpoint responded successfully.");
    }

    [Fact]
    public async Task Manual_verification_requires_setup_permission_for_readers()
    {
        await using var app = new ControlApiTestApplication(configureServices: services =>
        {
            services.RemoveAll<IEngineHealthProbe>();
            services.AddSingleton<IEngineHealthProbe>(new StubProbe(new EngineHealthProbeResult(
                true,
                null,
                CertificateStatus.Trusted,
                CredentialVerificationStatus.Verified,
                "Endpoint responded successfully.")));
        });
        await app.SeedAsync(_ => Task.CompletedTask);
        var owner = app.CreateTrustedWorkspaceClient("health-owner");
        var workspaceId = await owner.GetDefaultWorkspaceIdAsync();
        var engine = await SeedEngineAsync(app, workspaceId);
        await app.AddWorkspaceMemberAsync(workspaceId, "health-reader", WorkspaceRole.Reader);

        var response = await app.CreateTrustedWorkspaceClient("health-reader")
            .PostAsync($"/api/workspaces/{workspaceId}/deployments/engines/{engine.Id}/verify", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Manual_verification_rejects_cross_workspace_engine_ids()
    {
        await using var app = new ControlApiTestApplication(configureServices: services =>
        {
            services.RemoveAll<IEngineHealthProbe>();
            services.AddSingleton<IEngineHealthProbe>(new StubProbe(new EngineHealthProbeResult(
                false,
                null,
                CertificateStatus.Untrusted,
                CredentialVerificationStatus.Unverified,
                "Endpoint address is not publicly routable.")));
        });
        await app.SeedAsync(_ => Task.CompletedTask);
        var owner = app.CreateTrustedWorkspaceClient("health-owner");
        var workspaceId = await owner.GetDefaultWorkspaceIdAsync();
        var engine = await SeedEngineAsync(app, workspaceId);
        var otherOwner = app.CreateTrustedWorkspaceClient("other-health-owner");
        var otherWorkspaceId = await otherOwner.GetDefaultWorkspaceIdAsync();

        var response = await otherOwner.PostAsync(
            $"/api/workspaces/{otherWorkspaceId}/deployments/engines/{engine.Id}/verify",
            null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Manual_verification_persists_and_projects_only_the_safe_probe_diagnostic()
    {
        const string safeDiagnostic = "Endpoint address is not publicly routable.";
        await using var app = new ControlApiTestApplication(configureServices: services =>
        {
            services.RemoveAll<IEngineHealthProbe>();
            services.AddSingleton<IEngineHealthProbe>(new StubProbe(new EngineHealthProbeResult(
                false,
                null,
                CertificateStatus.Untrusted,
                CredentialVerificationStatus.Unverified,
                safeDiagnostic)));
        });
        await app.SeedAsync(_ => Task.CompletedTask);
        var owner = app.CreateTrustedWorkspaceClient("safe-diagnostic-owner");
        var workspaceId = await owner.GetDefaultWorkspaceIdAsync();
        var engine = await SeedEngineAsync(app, workspaceId);

        var response = await owner.PostAsync(
            $"/api/workspaces/{workspaceId}/deployments/engines/{engine.Id}/verify",
            null);
        var result = await response.Content.ReadControlJsonAsync<EngineHealthResult>();
        var cockpit = await owner.GetControlJsonAsync<DeploymentCockpit>($"/api/workspaces/{workspaceId}/deployments/cockpit");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(safeDiagnostic, result!.Message);
        Assert.DoesNotContain(engine.BaseUrl, result.Message, StringComparison.Ordinal);
        Assert.Single(cockpit!.Engines, x => x.Id == engine.Id.ToString("D") && x.VerificationMessage == safeDiagnostic);
    }

    [Fact]
    public async Task Heartbeat_rejects_stale_updates()
    {
        await using var app = new ControlApiTestApplication();
        await app.SeedAsync(_ => Task.CompletedTask);
        var owner = app.CreateTrustedWorkspaceClient("heartbeat-owner");
        var workspaceId = await owner.GetDefaultWorkspaceIdAsync();
        var engine = await SeedEngineAsync(app, workspaceId);
        var heartbeatAt = DateTimeOffset.Parse("2026-05-26T10:00:00Z");
        var request = new WorkspaceEngineHeartbeatRequest(
            engine.EnvironmentId,
            "Elsa 4.1.0",
            CertificateStatus.Trusted,
            CredentialVerificationStatus.Verified,
            heartbeatAt,
            null,
            "Heartbeat accepted.");

        var accepted = await owner.PostControlJsonAsync($"/api/workspaces/{workspaceId}/deployments/engines/{engine.Id}/heartbeat", request);
        var stale = await owner.PostControlJsonAsync($"/api/workspaces/{workspaceId}/deployments/engines/{engine.Id}/heartbeat", request);

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }

    [Fact]
    public async Task Heartbeat_rejects_cross_workspace_engine_ids()
    {
        await using var app = new ControlApiTestApplication();
        await app.SeedAsync(_ => Task.CompletedTask);
        var owner = app.CreateTrustedWorkspaceClient("heartbeat-owner");
        var workspaceId = await owner.GetDefaultWorkspaceIdAsync();
        var engine = await SeedEngineAsync(app, workspaceId);
        var otherOwner = app.CreateTrustedWorkspaceClient("other-heartbeat-owner");
        var otherWorkspaceId = await otherOwner.GetDefaultWorkspaceIdAsync();

        var response = await otherOwner.PostControlJsonAsync(
            $"/api/workspaces/{otherWorkspaceId}/deployments/engines/{engine.Id}/heartbeat",
            new WorkspaceEngineHeartbeatRequest(
                engine.EnvironmentId,
                "Elsa 4.1.0",
                CertificateStatus.Trusted,
                CredentialVerificationStatus.Verified,
                DateTimeOffset.Parse("2026-05-26T10:00:00Z"),
                null,
                "Heartbeat accepted."));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static async Task<WorkspaceWorkflowEngine> SeedEngineAsync(ControlApiTestApplication app, Guid workspaceId)
    {
        var environment = await SeedEnvironmentAsync(app, workspaceId);
        await using var scope = app.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IWorkspaceDeploymentStore>();
        return await store.RegisterEngineAsync(
            workspaceId,
            new RegisterWorkflowEngineRequest(
                environment.Id,
                "claims-prod",
                "https://workflows.example.test/elsa",
                "westeurope",
                "Azure Key Vault",
                "kv://claims/prod/elsa-api",
                [new EngineCapability("engine.reload-configuration", "Reload engine configuration", CapabilityBoundary.EngineApi)],
                [new RuntimeControl("reload-configuration", "Reload Configuration", CapabilityBoundary.EngineApi, "engine.reload-configuration", "Reloads engine API configuration.")],
                null));
    }

    private static async Task<WorkspaceDeploymentEnvironment> SeedEnvironmentAsync(ControlApiTestApplication app, Guid workspaceId)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IWorkspaceDeploymentStore>();
        var application = await store.CreateApplicationAsync(workspaceId, new CreateWorkflowApplicationRequest("Claims Operations", null, null));
        return await store.CreateEnvironmentAsync(workspaceId, new CreateDeploymentEnvironmentRequest(application.Id, "Prod", EnvironmentTier.Production));
    }

    private static WorkspaceWorkflowEngineRequest EngineRequest(string name) =>
        new(
            name,
            "https://workflows.example.test/elsa",
            "westeurope",
            "Azure Key Vault",
            "kv://claims/prod/elsa-api",
            [new EngineCapability("engine.reload-configuration", "Reload engine configuration", CapabilityBoundary.EngineApi)],
            [new RuntimeControl("reload-configuration", "Reload Configuration", CapabilityBoundary.EngineApi, "engine.reload-configuration", "Reloads engine API configuration.")],
            null);

    private sealed class StubProbe(EngineHealthProbeResult result) : IEngineHealthProbe
    {
        public Task<EngineHealthProbeResult> ProbeAsync(WorkspaceWorkflowEngine engine, CancellationToken cancellationToken = default) =>
            Task.FromResult(result);
    }
}
