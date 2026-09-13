using System.Net;
using System.Net.Http.Json;
using ElsaControl.Api.Workspace;
using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Core.Cockpit;
using ElsaControl.Deployment.Core.Provisioning;
using ElsaControl.Deployment.Core.Workspace;
using ElsaControl.PackageCatalog.Core.Accounts;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using ElsaControl.RuntimeBuilder.Abstractions;
using ElsaControl.RuntimeBuilder.Abstractions.Plans;
using ElsaControl.RuntimeBuilder.Abstractions.ReleaseCatalog;
using ElsaControl.RuntimeBuilder.Abstractions.ReleaseManifests;
using ElsaControl.RuntimeBuilder.Core.RuntimeConfigurations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ElsaControl.Api.Tests;

public sealed class EngineProvisioningWorkflowApiTests(EngineProvisioningWorkflowApiTests.Fixture fixture)
    : IClassFixture<EngineProvisioningWorkflowApiTests.Fixture>
{
    [Fact]
    public async Task Preview_resolves_without_creating_and_acceptance_replays_one_registered_engine()
    {
        var (client, workspace, request) = await fixture.PrepareAsync();
        using (client)
        {
            var targets = await client.GetControlJsonAsync<EngineProvisioningTargetsResponse>(
                $"/api/workspaces/{workspace}/engine-provisioning/targets");
            Assert.Equal(request.EnvironmentId, Assert.Single(targets!.Targets).EnvironmentId);
            request = request with { BuilderIntent = DefaultBuilder() with
            { Image = new("export-runtime", "export-tag", 18000, null), Target = "docker-compose" } };
            var preview = await PreviewAsync(client, workspace, request);
            Assert.True(preview.CanProvision, string.Join("; ", preview.Findings.Select(x => x.Code)));
            Assert.NotNull(preview.ConfigurationDigest);
            Assert.Empty((await client.GetControlJsonAsync<DeploymentCockpit>($"/api/workspaces/{workspace}/deployments/cockpit"))!.Engines);
            var reviewed = request with { PreviewDigest = preview.PreviewDigest, BuilderIntent = preview.BuilderIntent };
            using var first = await CreateAsync(client, workspace, reviewed, "create-engine");
            Assert.True(first.StatusCode == HttpStatusCode.Accepted, await first.Content.ReadAsStringAsync());
            var accepted = await first.Content.ReadControlJsonAsync<ManagedElsaInstanceAcceptedResponse>();
            using var replay = await CreateAsync(client, workspace, reviewed, "create-engine");
            Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
            var repeated = await replay.Content.ReadControlJsonAsync<ManagedElsaInstanceAcceptedResponse>();
            Assert.Equal(accepted!.Operation.Id, repeated!.Operation.Id);
            Assert.Equal(accepted.Links["engine"], repeated.Links["engine"]);
            var cockpit = await client.GetControlJsonAsync<DeploymentCockpit>($"/api/workspaces/{workspace}/deployments/cockpit");
            var engine = Assert.Single(cockpit!.Engines);
            targets = await client.GetControlJsonAsync<EngineProvisioningTargetsResponse>(
                $"/api/workspaces/{workspace}/engine-provisioning/targets");
            Assert.Empty(targets!.Targets);
            Assert.NotEqual(DeploymentHealth.Healthy, engine.Health);
            Assert.DoesNotContain("https://", engine.Endpoint.BaseUrl);
            using var occupied = await client.PostControlJsonAsync($"/api/workspaces/{workspace}/engine-provisioning/preview", request);
            var blocked = await occupied.Content.ReadControlJsonAsync<EngineProvisioningPreviewResponse>();
            Assert.False(blocked!.CanProvision);
            Assert.Contains(blocked.Findings, x => x.Code == "provisioning.target.occupied");
        }
    }

    [Theory]
    [InlineData(200, true)]
    [InlineData(201, false)]
    public async Task Review_and_registration_use_the_same_engine_name_limit(int length, bool allowed)
    {
        var (client, workspace, request) = await fixture.PrepareAsync();
        using (client)
        {
            request = request with { Name = new string('a', length) };
            var preview = await PreviewAsync(client, workspace, request);
            Assert.Equal(allowed, preview.CanProvision);
            if (!allowed)
            {
                Assert.Contains(preview.Findings, x => x.Code == "provisioning.request.invalid");
                return;
            }
            using var response = await CreateAsync(client, workspace,
                request with { PreviewDigest = preview.PreviewDigest }, "name-limit");
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            var cockpit = await client.GetControlJsonAsync<DeploymentCockpit>(
                $"/api/workspaces/{workspace}/deployments/cockpit");
            Assert.Equal(request.Name, Assert.Single(cockpit!.Engines).Name);
        }
    }

    [Fact]
    public async Task Review_is_required_and_stale_names_are_rejected_before_acceptance()
    {
        var (client, workspace, request) = await fixture.PrepareAsync();
        using (client)
        {
            using var missing = await CreateAsync(client, workspace, request, "missing-review");
            Assert.Equal(HttpStatusCode.UnprocessableEntity, missing.StatusCode);
            var preview = await PreviewAsync(client, workspace, request);
            using var stale = await CreateAsync(client, workspace, request with { Name = "Changed", PreviewDigest = preview.PreviewDigest }, "stale-review");
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            Assert.Contains("provisioning.preview-stale", await stale.Content.ReadAsStringAsync());
            Assert.Empty((await client.GetControlJsonAsync<DeploymentCockpit>($"/api/workspaces/{workspace}/deployments/cockpit"))!.Engines);
        }
    }

    [Fact]
    public async Task Incompatible_configuration_is_rejected_without_echoing_raw_values()
    {
        var (client, workspace, request) = await fixture.PrepareAsync();
        using (client)
        {
            var unsafeIntent = DefaultBuilder() with { Image = new("runtime", null, null, new Dictionary<string, string> { ["PASSWORD"] = "do-not-echo-this" }) };
            using var response = await client.PostControlJsonAsync($"/api/workspaces/{workspace}/engine-provisioning/preview", request with { BuilderIntent = unsafeIntent });
            var text = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("do-not-echo-this", text);
            var result = await response.Content.ReadControlJsonAsync<EngineProvisioningPreviewResponse>();
            Assert.False(result!.CanProvision);
            Assert.Contains(result.Findings, x => x.Code == "builder.image.environment.unsupported");
        }
    }

    [Fact]
    public async Task Custom_packages_must_resolve_through_the_workspace_catalog()
    {
        var (client, workspace, request) = await fixture.PrepareAsync();
        using (client)
        {
            var builder = DefaultBuilder() with
            {
                Packages = [new(Guid.NewGuid(), "Unapproved.Package", "1.0.0", [], null)]
            };
            var preview = await PreviewAsync(client, workspace, request with { BuilderIntent = builder });
            Assert.False(preview.CanProvision);
            Assert.Contains(preview.Findings, x => x.Severity == "error");
            Assert.Null(preview.PreviewDigest);
        }
    }

    [Fact]
    public async Task Saved_configuration_is_workspace_scoped_and_changes_invalidate_review()
    {
        var (client, workspace, request) = await fixture.PrepareAsync();
        using (client)
        {
            await using var scope = fixture.Application.Services.CreateAsyncScope();
            var configurations = scope.ServiceProvider.GetRequiredService<RuntimeConfigurationService>();
            var saved = await configurations.CreateAsync(workspace, "My runtime", null, DefaultBuilder());
            request = request with { RuntimeConfigurationId = saved.Id };
            var preview = await PreviewAsync(client, workspace, request);
            Assert.True(preview.CanProvision);
            await configurations.UpdateAsync(workspace, saved.Id, "Edited runtime", null,
                DefaultBuilder() with { Target = "docker" });
            using var stale = await CreateAsync(client, workspace, request with { PreviewDigest = preview.PreviewDigest }, "saved-review");
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            var missing = await PreviewAsync(client, workspace, request with { RuntimeConfigurationId = Guid.NewGuid() });
            Assert.False(missing.CanProvision);
            Assert.Contains(missing.Findings, x => x.Code == "provisioning.configuration.unavailable");
        }
    }

    [Fact]
    public async Task Accepted_request_replays_after_saved_configuration_changes_but_rejects_changed_payload()
    {
        var (client, workspace, request) = await fixture.PrepareAsync();
        using (client)
        {
            await using var scope = fixture.Application.Services.CreateAsyncScope();
            var configurations = scope.ServiceProvider.GetRequiredService<RuntimeConfigurationService>();
            var saved = await configurations.CreateAsync(workspace, "Saved runtime", null, DefaultBuilder());
            request = request with { RuntimeConfigurationId = saved.Id };
            var preview = await PreviewAsync(client, workspace, request);
            request = request with { PreviewDigest = preview.PreviewDigest };
            using var first = await CreateAsync(client, workspace, request, "retry-saved");
            Assert.True(first.StatusCode == HttpStatusCode.Accepted, await first.Content.ReadAsStringAsync());
            var accepted = await first.Content.ReadControlJsonAsync<ManagedElsaInstanceAcceptedResponse>();
            await configurations.UpdateAsync(workspace, saved.Id, "Later edits", null, DefaultBuilder() with { Target = "docker" });
            using var replay = await CreateAsync(client, workspace, request, "retry-saved");
            Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
            Assert.Equal(accepted!.Operation.Id, (await replay.Content.ReadControlJsonAsync<ManagedElsaInstanceAcceptedResponse>())!.Operation.Id);
            using var mismatch = await CreateAsync(client, workspace, request with { Name = "Different engine" }, "retry-saved");
            Assert.Equal(HttpStatusCode.Conflict, mismatch.StatusCode);
        }
    }

    [Fact]
    public async Task Foreign_target_and_caller_cannot_provision()
    {
        var (client, workspace, request) = await fixture.PrepareAsync();
        using (client)
        using (var outsider = fixture.Application.CreateTrustedWorkspaceClient("outsider"))
        {
            var invalid = await PreviewAsync(client, workspace, request with { ApplicationId = Guid.NewGuid() });
            Assert.False(invalid.CanProvision);
            using var response = await outsider.PostControlJsonAsync($"/api/workspaces/{workspace}/engine-provisioning/preview", request);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    [Fact]
    public async Task Workspace_reader_requires_setup_permission_for_review_and_acceptance()
    {
        var (owner, workspace, request) = await fixture.PrepareAsync();
        using (owner)
        {
            await fixture.Application.AddWorkspaceMemberAsync(workspace, "provision-reader", WorkspaceRole.Reader);
            using var reader = fixture.Application.CreateTrustedWorkspaceClient("provision-reader");
            using var preview = await reader.PostControlJsonAsync($"/api/workspaces/{workspace}/engine-provisioning/preview", request);
            using var create = await CreateAsync(reader, workspace, request, "reader-create");
            using var targets = await reader.GetAsync($"/api/workspaces/{workspace}/engine-provisioning/targets");
            Assert.Equal(HttpStatusCode.Forbidden, targets.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, preview.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        }
    }

    [Fact]
    public async Task Disabled_module_blocks_provisioning_endpoints()
    {
        await using var app = new ControlApiTestApplication();
        await app.SeedAsync(_ => Task.CompletedTask);
        using var client = app.CreateTrustedWorkspaceClient("disabled");
        var workspace = await client.GetDefaultWorkspaceIdAsync();
        var request = new EngineProvisioningRequest("Engine", "engine", Guid.NewGuid(), Guid.NewGuid(), Intent());
        using var preview = await client.PostControlJsonAsync($"/api/workspaces/{workspace}/engine-provisioning/preview", request);
        using var create = await CreateAsync(client, workspace, request, "disabled");
        using var targets = await client.GetAsync($"/api/workspaces/{workspace}/engine-provisioning/targets");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, targets.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, preview.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, create.StatusCode);
    }

    [Theory]
    [InlineData("INFO", true)]
    [InlineData("WaRnInG", true)]
    [InlineData("notice", false)]
    [InlineData("ERROR", false)]
    public async Task Provider_findings_allow_only_known_nonblocking_severities(string severity, bool canProvision)
    {
        var (client, workspace, request) = await fixture.PrepareAsync();
        using (client)
        {
            var module = Assert.IsType<TestModule>(
                fixture.Application.Services.GetRequiredService<IEngineProvisioningModule>());
            module.Findings = [new(severity, "provider.test", "Provider test finding.", "provider")];

            var preview = await PreviewAsync(client, workspace, request);

            Assert.Equal(canProvision, preview.CanProvision);
            Assert.Contains(preview.Findings, finding => finding.Code == "provider.test");
        }
    }

    private static async Task<EngineProvisioningPreviewResponse> PreviewAsync(HttpClient client, Guid workspace, EngineProvisioningRequest request)
    {
        using var response = await client.PostControlJsonAsync($"/api/workspaces/{workspace}/engine-provisioning/preview", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        return (await response.Content.ReadControlJsonAsync<EngineProvisioningPreviewResponse>())!;
    }

    private static async Task<HttpResponseMessage> CreateAsync(HttpClient client, Guid workspace, EngineProvisioningRequest body, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/workspaces/{workspace}/engine-provisioning")
        { Content = JsonContent.Create(body, options: ControlApiTestApplication.JsonOptions) };
        request.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(request);
    }

    private static RuntimeBuilderIntent DefaultBuilder() => new(new("runtime", null, null, null), [], [], [], null);
    private static ElsaInstanceIntent Intent() => new(
        new("future-runtime", "5.0", "5.0.0"), new("combined", "starter", null, "approved"),
        new("managed", "westeurope", "dedicated", "standard-small", "public", "managed"));

    public sealed class Fixture : IAsyncLifetime
    {
        internal ControlApiTestApplication Application { get; } = new(configureServices: services =>
        {
            services.AddSingleton<IEngineProvisioningModule, TestModule>();
            var catalog = new ManagedElsaInstanceApiTests.CapturingReleaseCatalogStore();
            catalog.SetEntries([CatalogEntry()]);
            services.RemoveAll<IGovernedReleaseCatalogStore>();
            services.AddSingleton<IGovernedReleaseCatalogStore>(catalog);
            services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new ElsaInstancePlanAuthorityOptions { Origin = "https://control.example.test" }));
            services.AddSingleton(new EngineProvisioningPlanContext(new Dictionary<string, string>
            {
                ["database:connectionstring"] = "secret://vault/database-connection",
                ["identity:signingkey"] = "secret://vault/identity-signing-key",
                ["admin:password"] = "secret://vault/admin-password"
            }));
        });

        internal async Task<(HttpClient Client, Guid Workspace, EngineProvisioningRequest Request)> PrepareAsync()
        {
            var module = (TestModule)Application.Services.GetRequiredService<IEngineProvisioningModule>();
            module.Findings = [];
            await Application.SeedAsync(_ => Task.CompletedTask);
            var client = Application.CreateTrustedWorkspaceClient("provision-owner");
            var workspace = await client.GetDefaultWorkspaceIdAsync();
            await using var scope = Application.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            var organization = await db.Workspaces.Where(x => x.Id == workspace).Select(x => x.OrganizationId).SingleAsync();
            db.OrganizationEntitlementSnapshots.Add(new OrganizationEntitlementSnapshot
            {
                OrganizationId = organization, ManagedHostingEnabled = true, MaxSources = 5,
                MaxWorkspaces = 5, MaxInstances = 10, SubscriptionState = OrganizationSubscriptionState.Active
            });
            await db.SaveChangesAsync();
            var deployments = scope.ServiceProvider.GetRequiredService<WorkspaceDeploymentService>();
            var application = await deployments.CreateApplicationAsync(workspace, new("My app", null, null));
            var environment = await deployments.CreateEnvironmentAsync(workspace, new(application.Id, "Development", EnvironmentTier.Dev));
            return (client, workspace, new("My engine", "my-engine", application.Id, environment.Id, Intent()));
        }

        public Task InitializeAsync() => Task.CompletedTask;
        public Task DisposeAsync() => ((IAsyncDisposable)Application).DisposeAsync().AsTask();
    }

    private sealed class TestModule : IEngineProvisioningModule
    {
        public string Id => "test";
        public string DisplayName => "Test provider";
        public IReadOnlyList<ElsaInstancePlanResolutionFinding> Findings { get; set; } = [];

        public IReadOnlyList<ElsaInstancePlanResolutionFinding> ValidatePlan(ResolvedElsaApplicationPlan plan, string region) => Findings;
    }

    private static GovernedReleaseCatalogEntry CatalogEntry() => new(
        "2.0.0", "https://catalog.example.test/manifests/5.0.0.json", Digest('c'), Digest('d'),
        "https://catalog.example.test/signatures/5.0.0.sig", Digest('e'), "paid",
        new("future-runtime", "commercial", "5.0", "5.0.0", "stable", "stable", "commercial",
            "https://github.com/example/runtime", new string('a', 40), "run-1"),
        new("combined", "1", ["elsa.server"], [], [new("server", "5.0.0")],
            [new("server", "valenceruntimeimages.azurecr.io/runtime-combined@" + Digest('a'), Digest('a'),
                new Dictionary<string, string>(), ["server"], [], [], null)],
            [new(ReleaseManifestEvidenceKinds.Sbom, "https://catalog.example.test/evidence/sbom", Digest('1')),
             new(ReleaseManifestEvidenceKinds.Provenance, "https://catalog.example.test/evidence/provenance", Digest('2')),
             new(ReleaseManifestEvidenceKinds.VulnerabilityScan, "https://catalog.example.test/evidence/scan", Digest('3'))]),
        "supported", DateTimeOffset.UtcNow,
        new("central-package-declarations-v1", Digest('f'),
            [new("Elsa.Persistence.EFCore.SqlServer", "5.0.0"), new("Elsa.Scheduling.Quartz.EFCore.SqlServer", "5.0.0")]));
    private static string Digest(char marker) => "sha256:" + new string(marker, 64);
}
