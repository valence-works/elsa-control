using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ElsaControl.Api.Authentication;
using ElsaControl.Api.Cloud;
using ElsaControl.Billing.Stripe;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ElsaControl.Api.Tests;

public sealed class CloudCompatibilityStagingFixtureTests
{
    private const string ClientId = "elsa-cloud-lovable-bff";
    private const string Scope = CloudBffDefaults.DefaultScope;
    private const string CloudAccountIssuer = "https://cloud-account.test/auth/v1";

    private static readonly string[] CurrentCapabilities =
    [
        "cloud.bootstrap.v1",
        "hosted.instances.list.v1",
        "hosted.instances.create.v1",
        "hosted.instances.status.v1",
        "hosted.instances.provisioning-progress.v1",
        "hosted.instances.overview.v1",
        "hosted.studio.handoff.issue.v1",
        "hosted.instances.quota-problem.v1",
        "hosted.instances.confirmed-delete.v1",
        "hosted.instances.reconciliation-cleanup.v1",
        "hosted.subscription.manage.v1",
        "hosted.deployments.audit.v1"
    ];

    private static readonly string[] MissingCapabilityCapabilities =
    [
        "cloud.bootstrap.v1",
        "hosted.instances.list.v1",
        "hosted.instances.create.v1",
        "hosted.instances.status.v1",
        "hosted.instances.overview.v1",
        "hosted.studio.handoff.issue.v1",
        "hosted.instances.quota-problem.v1",
        "hosted.instances.confirmed-delete.v1",
        "hosted.instances.reconciliation-cleanup.v1",
        "hosted.subscription.manage.v1",
        "hosted.deployments.audit.v1"
    ];

    [Fact]
    public async Task Unset_fixture_returns_exact_twelve_capability_list()
    {
        await using var app = CreateBffApplication();
        using var client = CreateBffClient(app);

        using var response = await client.GetAsync("/api/cloud/compatibility");

        await AssertCompatibilityAsync(response, 1, CurrentCapabilities);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Blank_fixture_values_match_the_unset_contract(string? fixture)
    {
        await using var app = CreateBffApplication(Fixture(fixture));
        using var client = CreateBffClient(app);

        using var response = await client.GetAsync("/api/cloud/compatibility");

        await AssertCompatibilityAsync(response, 1, CurrentCapabilities);
    }

    [Fact]
    public async Task Missing_capability_drops_only_provisioning_progress()
    {
        await using var app = CreateBffApplication(Fixture(CloudCompatibilityStagingFixture.MissingCapability));
        using var client = CreateBffClient(app);

        using var response = await client.GetAsync("/api/cloud/compatibility");

        await AssertCompatibilityAsync(response, 1, MissingCapabilityCapabilities);
    }

    [Fact]
    public async Task Older_contract_returns_version_zero_with_the_normal_list()
    {
        await using var app = CreateBffApplication(Fixture(CloudCompatibilityStagingFixture.OlderContract));
        using var client = CreateBffClient(app);

        using var response = await client.GetAsync("/api/cloud/compatibility");

        await AssertCompatibilityAsync(response, 0, CurrentCapabilities);
    }

    [Fact]
    public async Task Unknown_fixture_value_fails_startup_and_logs_the_reason_without_the_value()
    {
        const string unknown = "not-a-supported-fixture";
        var logger = new CollectingLogger<CloudCompatibilityStagingFixtureValidator>();
        var validator = new CloudCompatibilityStagingFixtureValidator(
            Options.Create(new CloudCompatibilityOptions { StagingFixture = unknown }),
            Options.Create(new StripeBillingOptions { ExpectedMode = StripeBillingOptions.TestMode }),
            logger);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => validator.StartAsync(CancellationToken.None));

        Assert.Equal(CloudCompatibilityStagingFixtureValidator.UnrecognizedValueReason, error.Message);
        Assert.DoesNotContain(unknown, error.Message, StringComparison.Ordinal);
        var log = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, log.Level);
        Assert.Equal(CloudCompatibilityStagingFixtureValidator.UnrecognizedValueReason, log.Message);
        Assert.DoesNotContain(unknown, log.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("live")]
    [InlineData("production")]
    public async Task Armed_fixture_fails_startup_outside_the_test_environment(string? expectedMode)
    {
        var logger = new CollectingLogger<CloudCompatibilityStagingFixtureValidator>();
        var validator = new CloudCompatibilityStagingFixtureValidator(
            Options.Create(new CloudCompatibilityOptions
            {
                StagingFixture = CloudCompatibilityStagingFixture.MissingCapability
            }),
            Options.Create(new StripeBillingOptions { ExpectedMode = expectedMode }),
            logger);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => validator.StartAsync(CancellationToken.None));

        Assert.Equal(CloudCompatibilityStagingFixtureValidator.NonTestEnvironmentReason, error.Message);
        Assert.DoesNotContain(CloudCompatibilityStagingFixture.MissingCapability, error.Message, StringComparison.Ordinal);
        var log = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, log.Level);
        Assert.Equal(CloudCompatibilityStagingFixtureValidator.NonTestEnvironmentReason, log.Message);
        Assert.DoesNotContain(CloudCompatibilityStagingFixture.MissingCapability, log.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_fixture_value_prevents_the_host_from_starting()
    {
        const string unknown = "mystery-mode";
        await using var app = CreateBffApplication(Fixture(unknown));

        var error = await Assert.ThrowsAnyAsync<Exception>(() => StartHostAsync(app));

        Assert.Contains(
            CloudCompatibilityStagingFixtureValidator.UnrecognizedValueReason,
            Flatten(error),
            StringComparison.Ordinal);
        Assert.DoesNotContain(unknown, Flatten(error), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Armed_fixture_prevents_the_host_from_starting_outside_the_test_environment()
    {
        await using var app = CreateBffApplication(new Dictionary<string, string?>
        {
            [CloudCompatibilityOptions.StagingFixtureKey] = CloudCompatibilityStagingFixture.OlderContract,
            [StripeBillingOptions.ExpectedModeConfigurationKey] = StripeBillingOptions.LiveMode
        });

        var error = await Assert.ThrowsAnyAsync<Exception>(() => StartHostAsync(app));

        Assert.Contains(
            CloudCompatibilityStagingFixtureValidator.NonTestEnvironmentReason,
            Flatten(error),
            StringComparison.Ordinal);
        Assert.DoesNotContain(CloudCompatibilityStagingFixture.OlderContract, Flatten(error), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(CloudCompatibilityStagingFixture.MissingCapability)]
    [InlineData(CloudCompatibilityStagingFixture.OlderContract)]
    public async Task Other_cloud_endpoints_are_unaffected_while_the_fixture_is_on(string mode)
    {
        await using var app = CreateBffApplication(Fixture(mode));
        await app.SeedAsync(_ => Task.CompletedTask);
        using var client = CreateBffClient(app);

        using var bootstrap = await client.PostAsJsonAsync(
            "/api/cloud/bootstrap",
            new CloudBootstrapRequest(),
            ControlApiTestApplication.JsonOptions);
        using var organizations = await client.GetAsync("/api/me/organizations");
        using var workspaces = await client.GetAsync("/api/me/workspaces");
        using var compatibility = await client.GetAsync("/api/cloud/compatibility");

        Assert.Equal(HttpStatusCode.OK, bootstrap.StatusCode);
        Assert.True(bootstrap.Headers.CacheControl?.NoStore);
        var bootstrapBody = await bootstrap.Content.ReadFromJsonAsync<CloudBootstrapResponse>(
            ControlApiTestApplication.JsonOptions);
        Assert.NotNull(bootstrapBody);
        Assert.NotEqual(Guid.Empty, bootstrapBody.OrganizationId);
        Assert.NotEqual(Guid.Empty, bootstrapBody.WorkspaceId);

        Assert.Equal(HttpStatusCode.OK, organizations.StatusCode);
        Assert.Equal(HttpStatusCode.OK, workspaces.StatusCode);

        if (mode == CloudCompatibilityStagingFixture.MissingCapability)
            await AssertCompatibilityAsync(compatibility, 1, MissingCapabilityCapabilities);
        else
            await AssertCompatibilityAsync(compatibility, 0, CurrentCapabilities);
    }

    [Fact]
    public async Task Health_reports_null_compatibility_fixture_when_unset()
    {
        await using var app = CreateBffApplication();
        using var response = await app.CreateClient().GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ok", payload.RootElement.GetProperty("status").GetString());
        Assert.True(payload.RootElement.TryGetProperty("compatibilityFixture", out var fixture));
        Assert.Equal(JsonValueKind.Null, fixture.ValueKind);
    }

    [Theory]
    [InlineData(CloudCompatibilityStagingFixture.MissingCapability)]
    [InlineData(CloudCompatibilityStagingFixture.OlderContract)]
    public async Task Health_advertises_the_known_fixture_the_process_is_serving(string mode)
    {
        await using var app = CreateBffApplication(Fixture(mode));
        using var response = await app.CreateClient().GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ok", payload.RootElement.GetProperty("status").GetString());
        Assert.Equal(mode, payload.RootElement.GetProperty("compatibilityFixture").GetString());
    }

    [Fact]
    public void Test_environment_is_the_deploy_pipeline_expected_mode()
    {
        Assert.True(CloudCompatibilityStagingFixtureValidator.IsTestEnvironment(
            new StripeBillingOptions { ExpectedMode = StripeBillingOptions.TestMode }));
        Assert.True(CloudCompatibilityStagingFixtureValidator.IsTestEnvironment(
            new StripeBillingOptions { ExpectedMode = "staging" }));
        Assert.False(CloudCompatibilityStagingFixtureValidator.IsTestEnvironment(
            new StripeBillingOptions { ExpectedMode = StripeBillingOptions.LiveMode }));
        Assert.False(CloudCompatibilityStagingFixtureValidator.IsTestEnvironment(
            new StripeBillingOptions()));
    }

    private static async Task AssertCompatibilityAsync(
        HttpResponseMessage response,
        int contractVersion,
        IReadOnlyList<string> capabilities)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Contains("no-cache", response.Headers.Pragma.ToString(), StringComparison.OrdinalIgnoreCase);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        Assert.Equal(["contractVersion", "capabilities"],
            root.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal(contractVersion, root.GetProperty("contractVersion").GetInt32());
        Assert.Equal(
            capabilities,
            root.GetProperty("capabilities").EnumerateArray().Select(value => value.GetString()!).ToArray());
        Assert.DoesNotContain("environment", root.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("customer", root.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("provider", root.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("StagingFixture", root.GetRawText(), StringComparison.Ordinal);
    }

    private static Dictionary<string, string?> Fixture(string? value) => new()
    {
        [CloudCompatibilityOptions.StagingFixtureKey] = value
    };

    private static ControlApiTestApplication CreateBffApplication(
        IReadOnlyDictionary<string, string?>? additionalConfiguration = null)
    {
        var configuration = new Dictionary<string, string?>
        {
            [$"{CloudBffOptions.ConfigurationSection}:Enabled"] = "true",
            [$"{CloudBffOptions.ConfigurationSection}:ClientId"] = ClientId,
            [$"{CloudBffOptions.ConfigurationSection}:Scope"] = Scope,
            [$"{CloudAccountIdentityDefaults.ConfigurationSection}:Enabled"] = "true",
            [$"{CloudAccountIdentityDefaults.ConfigurationSection}:Issuer"] = CloudAccountIssuer,
            [$"{CloudAccountIdentityDefaults.ConfigurationSection}:Audience"] = "authenticated",
            [$"{CloudAccountIdentityDefaults.ConfigurationSection}:TestSigningKey"] =
                ControlApiTestApplication.TestControlIdentitySigningKey
        };
        if (additionalConfiguration is not null)
        {
            foreach (var (key, value) in additionalConfiguration)
                configuration[key] = value;
        }

        return new ControlApiTestApplication(configuration);
    }

    private static HttpClient CreateBffClient(ControlApiTestApplication app)
    {
        return app.CreateControlIdentityClient(
            subject: "bff-user",
            claims: new Dictionary<string, string>
            {
                ["azp"] = ClientId,
                ["scp"] = Scope
            });
    }

    private static Task StartHostAsync(ControlApiTestApplication app)
    {
        _ = app.Services.GetRequiredService<IOptions<CloudCompatibilityOptions>>();
        return Task.CompletedTask;
    }

    private static string Flatten(Exception error)
    {
        var messages = new List<string>();
        for (var current = error; current is not null; current = current.InnerException)
            messages.Add(current.Message);
        return string.Join(" ", messages);
    }

    private sealed class CollectingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
