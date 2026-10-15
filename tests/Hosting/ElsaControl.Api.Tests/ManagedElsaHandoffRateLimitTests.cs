using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ElsaControl.Api.Authentication;
using ElsaControl.Api.Cloud;
using Microsoft.Extensions.Options;

namespace ElsaControl.Api.Tests;

public sealed class ManagedElsaHandoffRateLimitTests
{
    [Fact]
    public async Task Cloud_bff_sessions_from_one_ip_are_limited_by_per_ip()
    {
        const string issuer = "https://cloud-account.test/auth/v1";
        const string sharedIp = "203.0.113.22";
        await using var app = CreateCloudApplication(perIp: 1, perIdentity: 20, issuer);
        await app.SeedAsync(_ => Task.CompletedTask);
        using var first = CloudClient(app, issuer, "11111111-1111-4111-8111-111111111111", sharedIp);
        using var second = CloudClient(app, issuer, "22222222-2222-4222-8222-222222222222", sharedIp);
        using var otherIp = CloudClient(app, issuer, "33333333-3333-4333-8333-333333333333", "203.0.113.23");

        using var firstAllowed = await PostIssueAsync(first);
        using var rejected = await PostIssueAsync(second);
        using var allowed = await PostIssueAsync(otherIp);

        Assert.NotEqual(HttpStatusCode.TooManyRequests, firstAllowed.StatusCode);
        await AssertHandoffRateLimited(rejected);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, allowed.StatusCode);
    }

    [Fact]
    public async Task Distinct_cloud_session_identities_from_one_ip_are_isolated_by_identity()
    {
        const string issuer = "https://cloud-account.test/auth/v1";
        const string sharedIp = "203.0.113.24";
        const string firstSubject = "11111111-1111-4111-8111-111111111111";
        await using var app = CreateCloudApplication(perIp: 20, perIdentity: 1, issuer);
        await app.SeedAsync(_ => Task.CompletedTask);
        using var first = CloudClient(app, issuer, firstSubject, sharedIp);
        using var second = CloudClient(app, issuer, "22222222-2222-4222-8222-222222222222", sharedIp);
        using var firstAgain = CloudClient(app, issuer, firstSubject, sharedIp);

        using var firstAllowed = await PostIssueAsync(first);
        using var secondAllowed = await PostIssueAsync(second);
        using var firstRejected = await PostIssueAsync(firstAgain);

        Assert.NotEqual(HttpStatusCode.TooManyRequests, firstAllowed.StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, secondAllowed.StatusCode);
        await AssertHandoffRateLimited(firstRejected);
    }

    [Fact]
    public async Task Cloud_account_sessions_share_the_per_identity_limit_across_ips()
    {
        const string issuer = "https://cloud-account.test/auth/v1";
        await using var app = CreateCloudApplication(perIp: 20, perIdentity: 1, issuer);
        await app.SeedAsync(_ => Task.CompletedTask);
        using var first = CloudClient(app, issuer, "b6f73e84-5b47-4f4d-ad52-e3c4c9a815c6", "203.0.113.31");
        using var second = CloudClient(app, issuer, "b6f73e84-5b47-4f4d-ad52-e3c4c9a815c6", "203.0.113.32");
        using var other = CloudClient(app, issuer, "8ffab131-51f3-48f6-a777-0f07ae2b281c", "203.0.113.31");

        using var firstAllowed = await PostIssueAsync(first);
        using var rejected = await PostIssueAsync(second);
        using var allowed = await PostIssueAsync(other);

        Assert.NotEqual(HttpStatusCode.TooManyRequests, firstAllowed.StatusCode);
        await AssertHandoffRateLimited(rejected);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, allowed.StatusCode);
    }

    [Fact]
    public async Task Hub_bff_sessions_from_one_ip_are_limited_by_per_ip()
    {
        const string issuer = "https://hub-account.test/auth/v1";
        const string sharedIp = "203.0.113.25";
        await using var app = CreateHubApplication(perIp: 1, perIdentity: 20, issuer);
        await app.SeedAsync(_ => Task.CompletedTask);
        using var first = HubClient(app, issuer, "c0a80101-0000-4000-8000-000000000021", sharedIp);
        using var second = HubClient(app, issuer, "c0a80101-0000-4000-8000-000000000022", sharedIp);
        using var otherIp = HubClient(app, issuer, "c0a80101-0000-4000-8000-000000000023", "203.0.113.26");

        using var firstAllowed = await PostIssueAsync(first);
        using var rejected = await PostIssueAsync(second);
        using var allowed = await PostIssueAsync(otherIp);

        Assert.NotEqual(HttpStatusCode.TooManyRequests, firstAllowed.StatusCode);
        await AssertHandoffRateLimited(rejected);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, allowed.StatusCode);
    }

    [Fact]
    public async Task Distinct_hub_session_identities_from_one_ip_are_isolated_by_identity()
    {
        const string issuer = "https://hub-account.test/auth/v1";
        const string sharedIp = "203.0.113.23";
        const string firstSubject = "c0a80101-0000-4000-8000-000000000021";
        await using var app = CreateHubApplication(perIp: 20, perIdentity: 1, issuer);
        await app.SeedAsync(_ => Task.CompletedTask);
        using var first = HubClient(app, issuer, firstSubject, sharedIp);
        using var second = HubClient(app, issuer, "c0a80101-0000-4000-8000-000000000022", sharedIp);
        using var firstAgain = HubClient(app, issuer, firstSubject, sharedIp);

        using var firstAllowed = await PostIssueAsync(first);
        using var secondAllowed = await PostIssueAsync(second);
        using var firstRejected = await PostIssueAsync(firstAgain);

        Assert.NotEqual(HttpStatusCode.TooManyRequests, firstAllowed.StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, secondAllowed.StatusCode);
        await AssertHandoffRateLimited(firstRejected);
    }

    [Fact]
    public async Task Hub_account_sessions_share_the_per_identity_limit_across_ips()
    {
        const string issuer = "https://hub-account.test/auth/v1";
        await using var app = CreateHubApplication(perIp: 20, perIdentity: 1, issuer);
        await app.SeedAsync(_ => Task.CompletedTask);
        using var first = HubClient(app, issuer, "c0a80101-0000-4000-8000-000000000031", "203.0.113.33");
        using var second = HubClient(app, issuer, "c0a80101-0000-4000-8000-000000000031", "203.0.113.34");
        using var other = HubClient(app, issuer, "c0a80101-0000-4000-8000-000000000032", "203.0.113.33");

        using var firstAllowed = await PostIssueAsync(first);
        using var rejected = await PostIssueAsync(second);
        using var allowed = await PostIssueAsync(other);

        Assert.NotEqual(HttpStatusCode.TooManyRequests, firstAllowed.StatusCode);
        await AssertHandoffRateLimited(rejected);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, allowed.StatusCode);
    }

    [Fact]
    public async Task Same_subject_under_cloud_and_hub_issuers_uses_separate_buckets()
    {
        const string cloudIssuer = "https://cloud-account.test/auth/v1";
        const string hubIssuer = "https://hub-account.test/auth/v1";
        const string subject = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
        await using var app = CreateCloudAndHubApplication(perIp: 20, perIdentity: 1, cloudIssuer, hubIssuer);
        await app.SeedAsync(_ => Task.CompletedTask);
        using var cloud = CloudClient(app, cloudIssuer, subject, "203.0.113.81");
        using var hub = HubClient(app, hubIssuer, subject, "203.0.113.82");
        using var cloudAgain = CloudClient(app, cloudIssuer, subject, "203.0.113.83");

        using var cloudAllowed = await PostIssueAsync(cloud);
        using var hubAllowed = await PostIssueAsync(hub);
        using var cloudRejected = await PostIssueAsync(cloudAgain);

        Assert.NotEqual(HttpStatusCode.TooManyRequests, cloudAllowed.StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, hubAllowed.StatusCode);
        await AssertHandoffRateLimited(cloudRejected);
    }

    [Fact]
    public async Task Control_identity_callers_still_share_the_per_ip_limit()
    {
        await using var app = CreateApplication(perIp: 1, perIdentity: 20);
        await app.SeedAsync(_ => Task.CompletedTask);
        using var first = ControlClient(app, "handoff-ip-one", "203.0.113.41");
        using var second = ControlClient(app, "handoff-ip-two", "203.0.113.41");
        using var otherIp = ControlClient(app, "handoff-ip-three", "203.0.113.42");

        using var firstAllowed = await PostIssueAsync(first);
        using var rejected = await PostIssueAsync(second);
        using var allowed = await PostIssueAsync(otherIp);

        Assert.NotEqual(HttpStatusCode.TooManyRequests, firstAllowed.StatusCode);
        await AssertHandoffRateLimited(rejected);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, allowed.StatusCode);
    }

    [Fact]
    public async Task Cookie_session_without_identity_falls_back_to_per_ip()
    {
        await using var app = CreateApplication(perIp: 1, perIdentity: 20);
        await app.SeedAsync(_ => Task.CompletedTask);
        using var first = CookieClient(app, subject: "", "203.0.113.91");
        using var second = CookieClient(app, subject: "", "203.0.113.91");
        using var otherIp = CookieClient(app, subject: "", "203.0.113.92");

        using var firstAllowed = await PostIssueAsync(first);
        using var rejected = await PostIssueAsync(second);
        using var allowed = await PostIssueAsync(otherIp);

        Assert.NotEqual(HttpStatusCode.TooManyRequests, firstAllowed.StatusCode);
        await AssertHandoffRateLimited(rejected);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, allowed.StatusCode);
    }

    [Fact]
    public async Task Cookie_session_without_identity_uses_shared_fallback_bucket()
    {
        await using var app = CreateApplication(perIp: 20, perIdentity: 1);
        await app.SeedAsync(_ => Task.CompletedTask);
        using var first = CookieClient(app, subject: "", "203.0.113.93");
        using var second = CookieClient(app, subject: "", "203.0.113.94");
        using var resolved = ControlClient(app, "handoff-resolved", "203.0.113.93");
        using var cookieResolved = CookieClient(app, "cookie-resolved", "203.0.113.93");

        using var firstAllowed = await PostIssueAsync(first);
        using var rejected = await PostIssueAsync(second);
        using var resolvedAllowed = await PostIssueAsync(resolved);
        using var cookieAllowed = await PostIssueAsync(cookieResolved);

        Assert.NotEqual(HttpStatusCode.TooManyRequests, firstAllowed.StatusCode);
        await AssertHandoffRateLimited(rejected);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, resolvedAllowed.StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, cookieAllowed.StatusCode);
    }

    [Theory]
    [InlineData(0, "00:01:00", "PerIp:PermitLimit")]
    [InlineData(-1, "00:01:00", "PerIp:PermitLimit")]
    [InlineData(10001, "00:01:00", "PerIp:PermitLimit")]
    [InlineData(1, "00:00:00", "PerIp:Window")]
    [InlineData(1, "01:00:01", "PerIp:Window")]
    public async Task Invalid_per_ip_configuration_is_refused(int permitLimit, string window, string expected)
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateValidator(perIpLimit: permitLimit, perIpWindow: TimeSpan.Parse(window)).StartAsync(CancellationToken.None));

        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, "00:01:00", "PerIdentity:PermitLimit")]
    [InlineData(10001, "00:01:00", "PerIdentity:PermitLimit")]
    [InlineData(1, "00:00:00", "PerIdentity:Window")]
    [InlineData(1, "3650.00:00:00", "PerIdentity:Window")]
    public async Task Invalid_per_identity_configuration_is_refused(int permitLimit, string window, string expected)
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateValidator(perIdentityLimit: permitLimit, perIdentityWindow: TimeSpan.Parse(window))
                .StartAsync(CancellationToken.None));

        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, "00:00:01")]
    [InlineData(20, "00:01:00")]
    [InlineData(10000, "01:00:00")]
    public async Task Valid_limits_are_accepted(int permitLimit, string window)
    {
        await CreateValidator(
            perIpLimit: permitLimit,
            perIpWindow: TimeSpan.Parse(window),
            perIdentityLimit: permitLimit,
            perIdentityWindow: TimeSpan.Parse(window)).StartAsync(CancellationToken.None);
    }

    private static ControlApiTestApplication CreateApplication(
        int perIp,
        int perIdentity,
        IReadOnlyDictionary<string, string?>? extra = null)
    {
        var configuration = new Dictionary<string, string?>
        {
            [$"{ManagedElsaHandoffDefaults.ConfigurationSection}:Enabled"] = "true",
            [$"{ManagedElsaHandoffRateLimitOptions.ConfigurationSection}:PerIp:PermitLimit"] = perIp.ToString(),
            [$"{ManagedElsaHandoffRateLimitOptions.ConfigurationSection}:PerIp:Window"] = "00:01:00",
            [$"{ManagedElsaHandoffRateLimitOptions.ConfigurationSection}:PerIdentity:PermitLimit"] = perIdentity.ToString(),
            [$"{ManagedElsaHandoffRateLimitOptions.ConfigurationSection}:PerIdentity:Window"] = "00:01:00"
        };
        if (extra is not null)
        {
            foreach (var (key, value) in extra)
                configuration[key] = value;
        }

        return new ControlApiTestApplication(configuration);
    }

    private static ControlApiTestApplication CreateCloudApplication(int perIp, int perIdentity, string issuer) =>
        CreateApplication(
            perIp,
            perIdentity,
            extra: new Dictionary<string, string?>
            {
                [$"{CloudAccountIdentityDefaults.ConfigurationSection}:Enabled"] = "true",
                [$"{CloudAccountIdentityDefaults.ConfigurationSection}:Issuer"] = issuer,
                [$"{CloudAccountIdentityDefaults.ConfigurationSection}:Audience"] = "authenticated",
                [$"{CloudAccountIdentityDefaults.ConfigurationSection}:TestSigningKey"] =
                    ControlApiTestApplication.TestControlIdentitySigningKey
            });

    private static ControlApiTestApplication CreateHubApplication(int perIp, int perIdentity, string issuer) =>
        CreateApplication(
            perIp,
            perIdentity,
            extra: new Dictionary<string, string?>
            {
                [$"{HubAccountIdentityDefaults.ConfigurationSection}:Enabled"] = "true",
                [$"{HubAccountIdentityDefaults.ConfigurationSection}:Issuer"] = issuer,
                [$"{HubAccountIdentityDefaults.ConfigurationSection}:Audience"] = "authenticated",
                [$"{HubAccountIdentityDefaults.ConfigurationSection}:TestSigningKey"] =
                    ControlApiTestApplication.TestControlIdentitySigningKey
            });

    private static ControlApiTestApplication CreateCloudAndHubApplication(
        int perIp,
        int perIdentity,
        string cloudIssuer,
        string hubIssuer) =>
        CreateApplication(
            perIp,
            perIdentity,
            extra: new Dictionary<string, string?>
            {
                [$"{CloudAccountIdentityDefaults.ConfigurationSection}:Enabled"] = "true",
                [$"{CloudAccountIdentityDefaults.ConfigurationSection}:Issuer"] = cloudIssuer,
                [$"{CloudAccountIdentityDefaults.ConfigurationSection}:Audience"] = "authenticated",
                [$"{CloudAccountIdentityDefaults.ConfigurationSection}:TestSigningKey"] =
                    ControlApiTestApplication.TestControlIdentitySigningKey,
                [$"{HubAccountIdentityDefaults.ConfigurationSection}:Enabled"] = "true",
                [$"{HubAccountIdentityDefaults.ConfigurationSection}:Issuer"] = hubIssuer,
                [$"{HubAccountIdentityDefaults.ConfigurationSection}:Audience"] = "authenticated",
                [$"{HubAccountIdentityDefaults.ConfigurationSection}:TestSigningKey"] =
                    ControlApiTestApplication.TestControlIdentitySigningKey
            });

    private static ManagedElsaHandoffRateLimitConfigurationValidator CreateValidator(
        int perIpLimit = 20,
        TimeSpan? perIpWindow = null,
        int perIdentityLimit = 20,
        TimeSpan? perIdentityWindow = null) =>
        new(Options.Create(new ManagedElsaHandoffRateLimitOptions
        {
            PerIp = new ManagedElsaHandoffFixedWindowRateLimitOptions
            {
                PermitLimit = perIpLimit,
                Window = perIpWindow ?? TimeSpan.FromMinutes(1)
            },
            PerIdentity = new ManagedElsaHandoffFixedWindowRateLimitOptions
            {
                PermitLimit = perIdentityLimit,
                Window = perIdentityWindow ?? TimeSpan.FromMinutes(1)
            }
        }));

    private static HttpClient ControlClient(ControlApiTestApplication app, string subject, string ip)
    {
        var client = app.CreateControlIdentityClient(subject: subject);
        SetRemoteIp(client, ip);
        return client;
    }

    private static HttpClient CookieClient(ControlApiTestApplication app, string subject, string ip)
    {
        var client = app.CreateClient();
        app.AddControlSessionCookie(client, subject, DateTimeOffset.UtcNow.AddHours(2));
        SetRemoteIp(client, ip);
        return client;
    }

    private static HttpClient CloudClient(ControlApiTestApplication app, string issuer, string subject, string ip)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer",
            TestWorkspaceIdentity.CreateToken(
                subject,
                issuer,
                "authenticated",
                claims: new Dictionary<string, string> { ["role"] = "authenticated", ["email"] = "buyer@example.test" }));
        SetRemoteIp(client, ip);
        return client;
    }

    private static HttpClient HubClient(ControlApiTestApplication app, string issuer, string subject, string ip) =>
        CloudClient(app, issuer, subject, ip);

    private static void SetRemoteIp(HttpClient client, string ip)
    {
        client.DefaultRequestHeaders.Remove(ControlApiTestApplication.TestRemoteIpHeader);
        client.DefaultRequestHeaders.TryAddWithoutValidation(ControlApiTestApplication.TestRemoteIpHeader, ip);
    }

    private static Task<HttpResponseMessage> PostIssueAsync(HttpClient client) =>
        client.PostAsJsonAsync("/api/managed-elsa/handoff/issue", new { });

    private static async Task AssertHandoffRateLimited(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Null(response.Headers.RetryAfter);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(ApiRateLimitRejection.HandoffExceededCode, body.RootElement.GetProperty("code").GetString());
        Assert.Equal(ApiRateLimitRejection.HandoffExceededTitle, body.RootElement.GetProperty("title").GetString());
        Assert.Equal(429, body.RootElement.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("correlationId").GetString()));
    }
}
