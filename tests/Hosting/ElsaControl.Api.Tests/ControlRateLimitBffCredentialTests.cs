using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using ElsaControl.Api.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace ElsaControl.Api.Tests;

public sealed class ControlRateLimitBffCredentialTests
{
    private const string Section = ControlRateLimitBffCredentialOptions.ConfigurationSection;
    private const string CloudIssuer = "https://cloud-account.test/auth/v1";
    private const string HubIssuer = "https://hub-account.test/auth/v1";

    [Theory]
    [InlineData(TestBffCredentials.Cloud, ControlRateLimitBffCredentialSources.Cloud)]
    [InlineData(TestBffCredentials.CloudPrevious, ControlRateLimitBffCredentialSources.Cloud)]
    [InlineData(TestBffCredentials.Hub, ControlRateLimitBffCredentialSources.Hub)]
    [InlineData(TestBffCredentials.HubPrevious, ControlRateLimitBffCredentialSources.Hub)]
    public void Matcher_selects_the_source_for_its_current_or_previous_value(string presented, string expected)
    {
        var matcher = new ControlRateLimitBffCredentialMatcher(ValidOptions());

        Assert.Equal(expected, matcher.Match(new StringValues(presented)));
    }

    [Theory]
    [InlineData(TestBffCredentials.Invalid)]
    [InlineData("")]
    [InlineData(TestBffCredentials.Cloud + " ")]
    [InlineData(" " + TestBffCredentials.Cloud)]
    public void Matcher_returns_no_source_for_an_unknown_value(string presented)
    {
        var matcher = new ControlRateLimitBffCredentialMatcher(ValidOptions());

        Assert.Null(matcher.Match(new StringValues(presented)));
    }

    [Fact]
    public void Matcher_returns_no_source_for_a_missing_or_repeated_header()
    {
        var matcher = new ControlRateLimitBffCredentialMatcher(ValidOptions());

        Assert.Null(matcher.Match(StringValues.Empty));
        Assert.Null(matcher.Match(new StringValues([TestBffCredentials.Cloud, TestBffCredentials.Cloud])));
    }

    [Fact]
    public void Matcher_ignores_the_section_when_disabled()
    {
        var options = ValidOptions();
        options.Enabled = false;
        var matcher = new ControlRateLimitBffCredentialMatcher(options);

        Assert.Null(matcher.Match(new StringValues(TestBffCredentials.Cloud)));
        Assert.Null(new ControlRateLimitBffCredentialMatcher(null).Match(new StringValues(TestBffCredentials.Cloud)));
    }

    [Theory]
    [InlineData("no-sources", "RateLimiting:BffCredentials:Sources must list at least one source")]
    [InlineData("unknown-name", "RateLimiting:BffCredentials:Sources:0:Name must be 'cloud' or 'hub'")]
    [InlineData("missing-name", "RateLimiting:BffCredentials:Sources:0:Name must be 'cloud' or 'hub'")]
    [InlineData("duplicate-name", "RateLimiting:BffCredentials:Sources:1:Name repeats source 'cloud'")]
    [InlineData("missing-secret", "RateLimiting:BffCredentials:Sources:0:Secret is required")]
    [InlineData("short-secret", "RateLimiting:BffCredentials:Sources:0:Secret must be at least 32 characters")]
    [InlineData("whitespace-secret", "RateLimiting:BffCredentials:Sources:0:Secret must not contain whitespace")]
    [InlineData("unresolved-secret", "RateLimiting:BffCredentials:Sources:0:Secret still holds an unresolved Key Vault reference")]
    [InlineData("short-previous", "RateLimiting:BffCredentials:Sources:0:PreviousSecret must be at least 32 characters")]
    [InlineData("whitespace-previous", "RateLimiting:BffCredentials:Sources:0:PreviousSecret must not contain whitespace")]
    [InlineData("previous-equals-secret", "RateLimiting:BffCredentials:Sources:0:PreviousSecret must differ from RateLimiting:BffCredentials:Sources:0:Secret")]
    [InlineData("unresolved-previous", "RateLimiting:BffCredentials:Sources:0:PreviousSecret still holds an unresolved Key Vault reference")]
    [InlineData("shared-secret", "RateLimiting:BffCredentials:Sources:1:Secret shares its value with RateLimiting:BffCredentials:Sources:0:Secret")]
    [InlineData("shared-previous", "RateLimiting:BffCredentials:Sources:1:PreviousSecret shares its value with RateLimiting:BffCredentials:Sources:0:Secret")]
    public async Task Startup_validation_refuses_the_configuration_naming_the_key_but_not_the_value(
        string scenario,
        string expected)
    {
        var options = InvalidOptions(scenario);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new ControlRateLimitBffCredentialConfigurationValidator(Options.Create(options))
                .StartAsync(CancellationToken.None));

        Assert.StartsWith("Rate limiting BFF credential configuration is invalid:", exception.Message, StringComparison.Ordinal);
        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
        foreach (var source in options.Sources)
        {
            foreach (var value in new[] { source.Secret, source.PreviousSecret })
            {
                if (!string.IsNullOrWhiteSpace(value))
                    Assert.DoesNotContain(value.Trim(), exception.Message, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task Startup_validation_accepts_one_source()
    {
        var options = new ControlRateLimitBffCredentialOptions
        {
            Enabled = true,
            Sources = [new() { Name = "cloud", Secret = TestBffCredentials.Cloud }]
        };

        await new ControlRateLimitBffCredentialConfigurationValidator(Options.Create(options))
            .StartAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Startup_validation_accepts_two_sources_with_previous_secrets()
    {
        await new ControlRateLimitBffCredentialConfigurationValidator(Options.Create(ValidOptions()))
            .StartAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Startup_validation_ignores_the_section_when_disabled()
    {
        var options = InvalidOptions("shared-secret");
        options.Enabled = false;

        await new ControlRateLimitBffCredentialConfigurationValidator(Options.Create(options))
            .StartAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Host_binds_the_section_and_refuses_an_unresolved_key_vault_reference()
    {
        const string reference =
            "@Microsoft.KeyVault(SecretUri=https://vault.example.vault.azure.net/secrets/RateLimiting-BffCredentials-cloud-Secret)";
        await using var app = new ControlApiTestApplication(new Dictionary<string, string?>
        {
            [$"{Section}:Enabled"] = "true",
            [$"{Section}:Sources:0:Name"] = "cloud",
            [$"{Section}:Sources:0:Secret"] = reference
        });

        var exception = Assert.Throws<InvalidOperationException>(() => app.CreateClient());

        Assert.Contains($"{Section}:Sources:0:Secret still holds an unresolved Key Vault reference", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("SecretUri=", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("cloud-endpoint-cloud-session", HttpStatusCode.NotFound)]
    [InlineData("cloud-endpoint-hub-session", HttpStatusCode.Forbidden)]
    [InlineData("cloud-endpoint-anonymous", HttpStatusCode.Unauthorized)]
    [InlineData("hub-endpoint-hub-session", HttpStatusCode.OK)]
    [InlineData("hub-endpoint-cloud-session", HttpStatusCode.OK)]
    [InlineData("hub-endpoint-anonymous", HttpStatusCode.Unauthorized)]
    public async Task Bff_credential_header_does_not_change_any_endpoint_result(string scenario, HttpStatusCode expected)
    {
        await using var app = new ControlApiTestApplication(new Dictionary<string, string?>(TestBffCredentials.Configuration())
        {
            [$"{CloudAccountIdentityDefaults.ConfigurationSection}:Enabled"] = "true",
            [$"{CloudAccountIdentityDefaults.ConfigurationSection}:Issuer"] = CloudIssuer,
            [$"{CloudAccountIdentityDefaults.ConfigurationSection}:Audience"] = "authenticated",
            [$"{CloudAccountIdentityDefaults.ConfigurationSection}:TestSigningKey"] =
                ControlApiTestApplication.TestControlIdentitySigningKey,
            [$"{HubAccountIdentityDefaults.ConfigurationSection}:Enabled"] = "true",
            [$"{HubAccountIdentityDefaults.ConfigurationSection}:Issuer"] = HubIssuer,
            [$"{HubAccountIdentityDefaults.ConfigurationSection}:Audience"] = "authenticated",
            [$"{HubAccountIdentityDefaults.ConfigurationSection}:TestSigningKey"] =
                ControlApiTestApplication.TestControlIdentitySigningKey
        });
        await app.SeedAsync(_ => Task.CompletedTask);
        var path = scenario.StartsWith("cloud-endpoint", StringComparison.Ordinal)
            ? $"/api/organizations/{Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc"):D}/workspaces"
            : "/api/me/workspace-memberships";
        string? issuer = scenario.EndsWith("cloud-session", StringComparison.Ordinal) ? CloudIssuer
            : scenario.EndsWith("hub-session", StringComparison.Ordinal) ? HubIssuer
            : null;

        var results = new List<(HttpStatusCode Status, string Body)>();
        foreach (var header in new[] { null, TestBffCredentials.Cloud, TestBffCredentials.Hub, TestBffCredentials.Invalid })
        {
            using var client = app.CreateClient();
            if (issuer is not null)
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                    "Bearer",
                    TestWorkspaceIdentity.CreateToken(
                        "dddddddd-dddd-4ddd-8ddd-dddddddddddd",
                        issuer,
                        "authenticated",
                        claims: new Dictionary<string, string> { ["role"] = "authenticated", ["email"] = "buyer@example.test" }));
            TestBffCredentials.Set(client, header);
            using var response = await client.GetAsync(path);
            results.Add((response.StatusCode, NormalizeBody(await response.Content.ReadAsStringAsync())));
        }

        Assert.All(results, result => Assert.Equal(expected, result.Status));
        Assert.All(results, result => Assert.Equal(results[0].Body, result.Body));
    }

    [Fact]
    public async Task Development_console_proxy_does_not_forward_the_bff_credential()
    {
        await using var console = await HeaderEchoConsole.StartAsync();
        await using var app = new ControlApiTestApplication(new Dictionary<string, string?>
        {
            [AdminDashboardAuthenticationDefaults.DevelopmentUrlConfigurationKey] = console.Url
        });
        using var client = app.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Proxy-Probe", "forwarded");
        TestBffCredentials.Set(client, TestBffCredentials.Cloud);

        using var response = await client.GetAsync("/admin/overview");
        var forwarded = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("x-proxy-probe", forwarded, StringComparison.Ordinal);
        Assert.DoesNotContain(ControlRateLimitBffCredentialOptions.HeaderName.ToLowerInvariant(), forwarded, StringComparison.Ordinal);
        Assert.DoesNotContain(TestBffCredentials.Cloud, forwarded, StringComparison.Ordinal);
    }

    private static ControlRateLimitBffCredentialOptions ValidOptions() =>
        new()
        {
            Enabled = true,
            Sources =
            [
                new()
                {
                    Name = "cloud",
                    Secret = TestBffCredentials.Cloud,
                    PreviousSecret = TestBffCredentials.CloudPrevious
                },
                new()
                {
                    Name = "hub",
                    Secret = TestBffCredentials.Hub,
                    PreviousSecret = TestBffCredentials.HubPrevious
                }
            ]
        };

    private static ControlRateLimitBffCredentialOptions InvalidOptions(string scenario)
    {
        var options = ValidOptions();
        var cloud = options.Sources[0];
        var hub = options.Sources[1];
        switch (scenario)
        {
            case "no-sources":
                options.Sources.Clear();
                break;
            case "unknown-name":
                cloud.Name = "elsa";
                break;
            case "missing-name":
                cloud.Name = null;
                break;
            case "duplicate-name":
                hub.Name = "Cloud";
                break;
            case "missing-secret":
                cloud.Secret = null;
                break;
            case "short-secret":
                cloud.Secret = "short-bff-credential-value-31ch";
                break;
            case "whitespace-secret":
                cloud.Secret = "cloud-bff-credential with-a-space-inside";
                break;
            case "unresolved-secret":
                cloud.Secret =
                    "@Microsoft.KeyVault(SecretUri=https://vault.example.vault.azure.net/secrets/RateLimiting-BffCredentials-cloud-Secret)";
                break;
            case "short-previous":
                cloud.PreviousSecret = "short-bff-previous-value-31char";
                break;
            case "whitespace-previous":
                cloud.PreviousSecret = "cloud-bff-previous\twith-a-tab-inside-it";
                break;
            case "previous-equals-secret":
                cloud.PreviousSecret = cloud.Secret;
                break;
            case "unresolved-previous":
                cloud.PreviousSecret =
                    "@Microsoft.KeyVault(SecretUri=https://vault.example.vault.azure.net/secrets/RateLimiting-BffCredentials-cloud-PreviousSecret)";
                break;
            case "shared-secret":
                hub.Secret = cloud.Secret;
                break;
            case "shared-previous":
                hub.PreviousSecret = cloud.Secret;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
        }

        return options;
    }

    private static string NormalizeBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return body;

        try
        {
            if (JsonNode.Parse(body) is JsonObject json)
            {
                json.Remove("traceId");
                json.Remove("correlationId");
                return json.ToJsonString();
            }
        }
        catch (JsonException)
        {
        }

        return body;
    }

    private sealed class HeaderEchoConsole(WebApplication app, string url) : IAsyncDisposable
    {
        public string Url { get; } = url;

        public static async Task<HeaderEchoConsole> StartAsync()
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
            var console = builder.Build();
            console.MapGet("/admin/{*path}", (HttpRequest request) =>
                Results.Text(
                    string.Join("\n", request.Headers.Select(header =>
                        $"{header.Key.ToLowerInvariant()}: {header.Value}")),
                    "text/plain"));
            await console.StartAsync();

            var addresses = console.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!;
            return new HeaderEchoConsole(console, addresses.Addresses.Single());
        }

        public async ValueTask DisposeAsync() => await app.DisposeAsync();
    }
}

/// <summary>Test-only BFF credential values; none of them is a real secret.</summary>
internal static class TestBffCredentials
{
    public const string Cloud = "cloud-bff-credential-current-value-000001";
    public const string CloudPrevious = "cloud-bff-credential-previous-value-00001";
    public const string Hub = "hub-bff-credential-current-value-0000001";
    public const string HubPrevious = "hub-bff-credential-previous-value-000001";
    public const string Invalid = "not-a-configured-bff-credential-value-01";

    public static readonly string[] AllValues = [Cloud, CloudPrevious, Hub, HubPrevious, Invalid];

    public static IReadOnlyDictionary<string, string?> Configuration() =>
        new Dictionary<string, string?>
        {
            [$"{ControlRateLimitBffCredentialOptions.ConfigurationSection}:Enabled"] = "true",
            [$"{ControlRateLimitBffCredentialOptions.ConfigurationSection}:Sources:0:Name"] = ControlRateLimitBffCredentialSources.Cloud,
            [$"{ControlRateLimitBffCredentialOptions.ConfigurationSection}:Sources:0:Secret"] = Cloud,
            [$"{ControlRateLimitBffCredentialOptions.ConfigurationSection}:Sources:0:PreviousSecret"] = CloudPrevious,
            [$"{ControlRateLimitBffCredentialOptions.ConfigurationSection}:Sources:1:Name"] = ControlRateLimitBffCredentialSources.Hub,
            [$"{ControlRateLimitBffCredentialOptions.ConfigurationSection}:Sources:1:Secret"] = Hub,
            [$"{ControlRateLimitBffCredentialOptions.ConfigurationSection}:Sources:1:PreviousSecret"] = HubPrevious
        };

    /// <summary>
    /// The header a <paramref name="account"/> session sends for a test case: its own current or
    /// previous value, an unknown value, no header, or the other BFF's value.
    /// </summary>
    public static string? HeaderValue(string account, string credential) => credential switch
    {
        "current" => account == "hub" ? Hub : Cloud,
        "previous" => account == "hub" ? HubPrevious : CloudPrevious,
        "invalid" => Invalid,
        "missing" => null,
        "mismatched" => account == "hub" ? Cloud : Hub,
        _ => throw new ArgumentOutOfRangeException(nameof(credential), credential, null)
    };

    public static void Set(HttpClient client, string? value)
    {
        client.DefaultRequestHeaders.Remove(ControlRateLimitBffCredentialOptions.HeaderName);
        if (value is not null)
            client.DefaultRequestHeaders.TryAddWithoutValidation(ControlRateLimitBffCredentialOptions.HeaderName, value);
    }
}
