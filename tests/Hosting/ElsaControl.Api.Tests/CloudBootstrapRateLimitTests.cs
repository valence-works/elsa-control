using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ElsaControl.Api.Authentication;
using ElsaControl.Api.Cloud;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace ElsaControl.Api.Tests;

public sealed class CloudBootstrapRateLimitTests
{
    [Fact]
    public async Task Under_both_limits_bootstrap_is_unchanged()
    {
        await using var app = CreateApplication(perIp: 5, perIdentity: 5);
        await app.SeedAsync(_ => Task.CompletedTask);
        using var client = Client(app, "bootstrap-under-limit", "203.0.113.10");

        using var first = await PostBootstrapAsync(client);
        using var second = await PostBootstrapAsync(client);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.True(first.Headers.CacheControl?.Private);
        Assert.True(first.Headers.CacheControl?.NoStore);
        var firstBody = await first.Content.ReadFromJsonAsync<CloudBootstrapResponse>(
            ControlApiTestApplication.JsonOptions);
        var secondBody = await second.Content.ReadFromJsonAsync<CloudBootstrapResponse>(
            ControlApiTestApplication.JsonOptions);
        Assert.Equal(firstBody, secondBody);
        Assert.NotNull(firstBody);
        Assert.NotEqual(Guid.Empty, firstBody.OrganizationId);
        Assert.NotEqual(Guid.Empty, firstBody.WorkspaceId);
    }

    [Fact]
    public async Task Over_the_per_ip_limit_returns_rate_limit_exceeded()
    {
        await using var app = CreateApplication(perIp: 2, perIdentity: 20);
        await app.SeedAsync(_ => Task.CompletedTask);
        using var first = Client(app, "ip-limit-one", "203.0.113.20");
        using var second = Client(app, "ip-limit-two", "203.0.113.20");
        using var third = Client(app, "ip-limit-three", "203.0.113.20");

        Assert.Equal(HttpStatusCode.OK, (await PostBootstrapAsync(first)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostBootstrapAsync(second)).StatusCode);
        using var rejected = await PostBootstrapAsync(third);

        await AssertRateLimitExceeded(rejected);
    }

    [Fact]
    public async Task Over_the_per_identity_limit_from_different_ips_rejects_that_identity_only()
    {
        await using var app = CreateApplication(perIp: 20, perIdentity: 2);
        await app.SeedAsync(_ => Task.CompletedTask);
        using var first = Client(app, "shared-identity", "203.0.113.31");
        using var second = Client(app, "shared-identity", "203.0.113.32");
        using var third = Client(app, "shared-identity", "203.0.113.33");
        using var other = Client(app, "other-identity", "203.0.113.31");

        Assert.Equal(HttpStatusCode.OK, (await PostBootstrapAsync(first)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostBootstrapAsync(second)).StatusCode);
        using var rejected = await PostBootstrapAsync(third);
        using var allowed = await PostBootstrapAsync(other);

        await AssertRateLimitExceeded(rejected);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [Fact]
    public async Task Per_ip_and_per_identity_limits_are_independent()
    {
        await using var app = CreateApplication(perIp: 1, perIdentity: 2);
        await app.SeedAsync(_ => Task.CompletedTask);
        using var ipOneIdentityOne = Client(app, "independent-one", "203.0.113.41");
        using var ipOneIdentityTwo = Client(app, "independent-two", "203.0.113.41");
        using var ipTwoIdentityOne = Client(app, "independent-one", "203.0.113.42");
        using var ipThreeIdentityThree = Client(app, "independent-three", "203.0.113.43");

        Assert.Equal(HttpStatusCode.OK, (await PostBootstrapAsync(ipOneIdentityOne)).StatusCode);
        using var sameIpRejected = await PostBootstrapAsync(ipOneIdentityTwo);
        using var sameIdentityAllowed = await PostBootstrapAsync(ipTwoIdentityOne);
        using var newIdentityAllowed = await PostBootstrapAsync(ipThreeIdentityThree);

        await AssertRateLimitExceeded(sameIpRejected);
        Assert.Equal(HttpStatusCode.OK, sameIdentityAllowed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, newIdentityAllowed.StatusCode);
    }

    [Fact]
    public async Task Exhausting_the_identity_limit_does_not_consume_another_identity_ip_budget()
    {
        await using var app = CreateApplication(perIp: 5, perIdentity: 1);
        await app.SeedAsync(_ => Task.CompletedTask);
        using var firstIp = Client(app, "identity-exhausted", "203.0.113.51");
        using var secondIp = Client(app, "identity-exhausted", "203.0.113.52");
        using var otherIdentitySameIp = Client(app, "identity-fresh", "203.0.113.51");

        Assert.Equal(HttpStatusCode.OK, (await PostBootstrapAsync(firstIp)).StatusCode);
        using var identityRejected = await PostBootstrapAsync(secondIp);
        using var otherAllowed = await PostBootstrapAsync(otherIdentitySameIp);

        await AssertRateLimitExceeded(identityRejected);
        Assert.Equal(HttpStatusCode.OK, otherAllowed.StatusCode);
    }

    [Fact]
    public async Task Forwarded_client_ips_are_limited_separately()
    {
        await using var app = CreateApplication(perIp: 1, perIdentity: 20);
        await app.SeedAsync(_ => Task.CompletedTask);
        using var clientA = Client(app, "forwarded-a", "198.51.100.10");
        using var clientB = Client(app, "forwarded-b", "198.51.100.11");

        Assert.Equal(HttpStatusCode.OK, (await PostBootstrapAsync(clientA)).StatusCode);
        using var clientARejected = await PostBootstrapAsync(clientA);
        using var clientBAllowed = await PostBootstrapAsync(clientB);

        await AssertRateLimitExceeded(clientARejected);
        Assert.Equal(HttpStatusCode.OK, clientBAllowed.StatusCode);
    }

    [Fact]
    public async Task Cookie_sessions_share_the_per_identity_limit_across_ips()
    {
        await using var app = CreateApplication(perIp: 20, perIdentity: 1);
        await app.SeedAsync(_ => Task.CompletedTask);
        using var first = CookieClient(app, "cookie-customer", "203.0.113.61");
        using var second = CookieClient(app, "cookie-customer", "203.0.113.62");
        using var other = CookieClient(app, "cookie-other", "203.0.113.61");

        Assert.Equal(HttpStatusCode.OK, (await PostBootstrapAsync(first)).StatusCode);
        using var rejected = await PostBootstrapAsync(second);
        using var allowed = await PostBootstrapAsync(other);

        await AssertRateLimitExceeded(rejected);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [Fact]
    public async Task Cookie_session_without_identity_falls_back_to_per_ip()
    {
        await using var app = CreateApplication(perIp: 1, perIdentity: 20);
        await app.SeedAsync(_ => Task.CompletedTask);
        using var first = CookieClient(app, subject: "", "203.0.113.91");
        using var second = CookieClient(app, subject: "", "203.0.113.91");
        using var otherIp = CookieClient(app, subject: "", "203.0.113.92");

        using var firstAllowed = await PostBootstrapAsync(first);
        using var rejected = await PostBootstrapAsync(second);
        using var allowed = await PostBootstrapAsync(otherIp);

        Assert.NotEqual(HttpStatusCode.TooManyRequests, firstAllowed.StatusCode);
        await AssertRateLimitExceeded(rejected);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, allowed.StatusCode);
    }

    [Fact]
    public async Task Cloud_account_sessions_share_the_per_identity_limit_across_ips()
    {
        const string issuer = "https://cloud-account.test/auth/v1";
        await using var app = CreateCloudApplication(perIp: 20, perIdentity: 1, issuer);
        await app.SeedAsync(_ => Task.CompletedTask);
        using var first = CloudClient(app, issuer, "b6f73e84-5b47-4f4d-ad52-e3c4c9a815c6", "203.0.113.71");
        using var second = CloudClient(app, issuer, "b6f73e84-5b47-4f4d-ad52-e3c4c9a815c6", "203.0.113.72");
        using var other = CloudClient(app, issuer, "8ffab131-51f3-48f6-a777-0f07ae2b281c", "203.0.113.71");

        Assert.Equal(HttpStatusCode.OK, (await PostBootstrapAsync(first)).StatusCode);
        using var rejected = await PostBootstrapAsync(second);
        using var allowed = await PostBootstrapAsync(other);

        await AssertRateLimitExceeded(rejected);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [Fact]
    public async Task Validated_bff_sessions_are_counted_per_ip()
    {
        const string issuer = "https://cloud-account.test/auth/v1";
        const string subject = "11111111-1111-4111-8111-111111111111";
        await using var app = CreateCloudApplication(perIp: 1, perIdentity: 20, issuer);
        await app.SeedAsync(_ => Task.CompletedTask);
        using var first = CloudClient(app, issuer, subject, "203.0.113.73");
        using var sameIp = CloudClient(app, issuer, subject, "203.0.113.73");
        using var otherIp = CloudClient(app, issuer, subject, "203.0.113.74");

        Assert.Equal(HttpStatusCode.OK, (await PostBootstrapAsync(first)).StatusCode);
        using var sameIpRejected = await PostBootstrapAsync(sameIp);
        using var otherIpAllowed = await PostBootstrapAsync(otherIp);

        await AssertRateLimitExceeded(sameIpRejected);
        Assert.Equal(HttpStatusCode.OK, otherIpAllowed.StatusCode);
    }

    [Fact]
    public async Task Hub_account_sessions_share_the_per_identity_limit_across_ips()
    {
        const string issuer = "https://hub-account.test/auth/v1";
        await using var app = CreateHubApplication(perIp: 20, perIdentity: 1, issuer);
        await app.SeedAsync(_ => Task.CompletedTask);
        using var first = HubClient(app, issuer, "c0a80101-0000-4000-8000-000000000001", "203.0.113.75");
        using var second = HubClient(app, issuer, "c0a80101-0000-4000-8000-000000000001", "203.0.113.76");
        using var other = HubClient(app, issuer, "c0a80101-0000-4000-8000-000000000002", "203.0.113.75");

        Assert.Equal(HttpStatusCode.OK, (await PostBootstrapAsync(first)).StatusCode);
        using var rejected = await PostBootstrapAsync(second);
        using var allowed = await PostBootstrapAsync(other);

        await AssertRateLimitExceeded(rejected);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [Fact]
    public async Task Hub_and_cloud_sessions_on_one_ip_have_separate_identities_and_share_per_ip()
    {
        const string hubIssuer = "https://hub-account.test/auth/v1";
        const string cloudIssuer = "https://cloud-account.test/auth/v1";
        const string sharedIp = "203.0.113.77";
        await using var app = CreateCloudAndHubApplication(perIp: 2, perIdentity: 1, hubIssuer, cloudIssuer);
        await app.SeedAsync(_ => Task.CompletedTask);
        using var hub = HubClient(app, hubIssuer, "c0a80101-0000-4000-8000-000000000011", sharedIp);
        using var cloud = CloudClient(app, cloudIssuer, "c0a80101-0000-4000-8000-000000000021", sharedIp);
        using var hubAgain = HubClient(app, hubIssuer, "c0a80101-0000-4000-8000-000000000011", sharedIp);
        using var third = CloudClient(app, cloudIssuer, "c0a80101-0000-4000-8000-000000000031", sharedIp);

        Assert.Equal(HttpStatusCode.OK, (await PostBootstrapAsync(hub)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostBootstrapAsync(cloud)).StatusCode);
        using var hubIdentityRejected = await PostBootstrapAsync(hubAgain);
        using var ipRejected = await PostBootstrapAsync(third);

        await AssertRateLimitExceeded(hubIdentityRejected);
        await AssertRateLimitExceeded(ipRejected);
    }

    [Fact]
    public async Task Cookie_session_without_sub_uses_the_unresolved_identity_fallback()
    {
        const string sharedIp = "203.0.113.78";
        await using var app = CreateApplication(perIp: 20, perIdentity: 20);
        await app.SeedAsync(_ => Task.CompletedTask);
        using var first = IncompleteCookieClient(app, sharedIp, subject: null, issuer: ControlApiTestApplication.TestControlIdentityIssuer);
        using var second = IncompleteCookieClient(app, sharedIp, subject: null, issuer: ControlApiTestApplication.TestControlIdentityIssuer);
        using var signedIn = CookieClient(app, "cookie-complete", sharedIp);
        using var otherIp = IncompleteCookieClient(app, "203.0.113.79", subject: null, issuer: ControlApiTestApplication.TestControlIdentityIssuer);

        Assert.Equal(HttpStatusCode.Unauthorized, (await PostBootstrapAsync(first)).StatusCode);
        using var fallbackRejected = await PostBootstrapAsync(second);
        using var signedInAllowed = await PostBootstrapAsync(signedIn);
        using var otherIpUnauthorized = await PostBootstrapAsync(otherIp);

        await AssertRateLimitExceeded(fallbackRejected);
        Assert.Equal(HttpStatusCode.OK, signedInAllowed.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, otherIpUnauthorized.StatusCode);
    }

    [Fact]
    public async Task Cookie_session_without_issuer_uses_the_unresolved_identity_fallback()
    {
        const string sharedIp = "203.0.113.80";
        await using var app = CreateApplication(
            perIp: 20,
            perIdentity: 20,
            extra: new Dictionary<string, string?>
            {
                [$"{ControlIdentityDefaults.ConfigurationSection}:Issuer"] = "",
                [$"{ControlIdentityDefaults.ConfigurationSection}:Authority"] = ""
            });
        await app.SeedAsync(_ => Task.CompletedTask);
        using var first = IncompleteCookieClient(app, sharedIp, subject: "cookie-no-issuer", issuer: null);
        using var second = IncompleteCookieClient(app, sharedIp, subject: "cookie-no-issuer", issuer: null);
        using var otherIp = IncompleteCookieClient(app, "203.0.113.83", subject: "cookie-no-issuer-other", issuer: null);

        Assert.Equal(HttpStatusCode.Unauthorized, (await PostBootstrapAsync(first)).StatusCode);
        using var fallbackRejected = await PostBootstrapAsync(second);
        using var otherIpUnauthorized = await PostBootstrapAsync(otherIp);

        await AssertRateLimitExceeded(fallbackRejected);
        Assert.Equal(HttpStatusCode.Unauthorized, otherIpUnauthorized.StatusCode);
    }

    [Fact]
    public async Task Unauthenticated_requests_are_limited_by_ip_only()
    {
        await using var app = CreateApplication(perIp: 1, perIdentity: 1);
        await app.SeedAsync(_ => Task.CompletedTask);
        using var anonymous = AnonymousClient(app, "203.0.113.81");
        using var signedIn = Client(app, "after-anonymous", "203.0.113.81");
        using var otherIp = Client(app, "after-anonymous", "203.0.113.82");

        using var first = await PostBootstrapAsync(anonymous);
        using var second = await PostBootstrapAsync(anonymous);
        using var sameIpSignedIn = await PostBootstrapAsync(signedIn);
        using var otherIpSignedIn = await PostBootstrapAsync(otherIp);

        Assert.Equal(HttpStatusCode.Unauthorized, first.StatusCode);
        await AssertRateLimitExceeded(second);
        await AssertRateLimitExceeded(sameIpSignedIn);
        Assert.Equal(HttpStatusCode.OK, otherIpSignedIn.StatusCode);
    }

    [Theory]
    [InlineData(0, "00:01:00", "PerIp:PermitLimit")]
    [InlineData(-1, "00:01:00", "PerIp:PermitLimit")]
    [InlineData(10001, "00:01:00", "PerIp:PermitLimit")]
    [InlineData(int.MaxValue, "00:01:00", "PerIp:PermitLimit")]
    [InlineData(1, "00:00:00", "PerIp:Window")]
    [InlineData(1, "01:00:01", "PerIp:Window")]
    [InlineData(1, "3650.00:00:00", "PerIp:Window")]
    public async Task Invalid_per_ip_configuration_is_refused(
        int permitLimit,
        string window,
        string expected)
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateValidator(perIpLimit: permitLimit, perIpWindow: TimeSpan.Parse(window)).StartAsync(CancellationToken.None));

        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, "00:01:00", "PerIdentity:PermitLimit")]
    [InlineData(-3, "00:01:00", "PerIdentity:PermitLimit")]
    [InlineData(10001, "00:01:00", "PerIdentity:PermitLimit")]
    [InlineData(int.MaxValue, "00:01:00", "PerIdentity:PermitLimit")]
    [InlineData(1, "00:00:00", "PerIdentity:Window")]
    [InlineData(1, "01:00:01", "PerIdentity:Window")]
    [InlineData(1, "3650.00:00:00", "PerIdentity:Window")]
    public async Task Invalid_per_identity_configuration_is_refused(
        int permitLimit,
        string window,
        string expected)
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateValidator(perIdentityLimit: permitLimit, perIdentityWindow: TimeSpan.Parse(window))
                .StartAsync(CancellationToken.None));

        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Default_per_ip_permit_is_ten_times_per_identity()
    {
        var options = new CloudBootstrapRateLimitOptions();

        Assert.Equal(CloudBootstrapRateLimitDefaults.DefaultPerIdentityPermitLimit, options.PerIdentity.PermitLimit);
        Assert.Equal(CloudBootstrapRateLimitDefaults.DefaultPerIpPermitLimit, options.PerIp.PermitLimit);
        Assert.Equal(options.PerIdentity.PermitLimit * 10, options.PerIp.PermitLimit);
    }

    [Theory]
    [InlineData(1, "00:00:01")]
    [InlineData(60, "00:01:00")]
    [InlineData(600, "00:01:00")]
    [InlineData(10000, "01:00:00")]
    public async Task Valid_limits_are_accepted(int permitLimit, string window)
    {
        await CreateValidator(
            perIpLimit: permitLimit,
            perIpWindow: TimeSpan.Parse(window),
            perIdentityLimit: permitLimit,
            perIdentityWindow: TimeSpan.Parse(window)).StartAsync(CancellationToken.None);
    }

    [Fact]
    public void Ipv6_clients_share_a_64_prefix_key()
    {
        Assert.Equal(
            GetIpKey("2001:db8:1:1::1"),
            GetIpKey("2001:db8:1:1::2"));
        Assert.NotEqual(
            GetIpKey("2001:db8:1:1::1"),
            GetIpKey("2001:db8:1:2::1"));
        Assert.Equal("203.0.113.20", GetIpKey("203.0.113.20"));
        Assert.Equal("203.0.113.20", GetIpKey("::ffff:203.0.113.20"));
        Assert.NotEqual(GetIpKey("192.0.2.1"), GetIpKey("192.0.2.2"));
    }

    [Fact]
    public async Task Ipv6_addresses_in_the_same_64_share_the_per_ip_limit()
    {
        await using var app = CreateApplication(perIp: 1, perIdentity: 20);
        await app.SeedAsync(_ => Task.CompletedTask);
        using var first = Client(app, "ipv6-one", "2001:db8:1:1::1");
        using var second = Client(app, "ipv6-two", "2001:db8:1:1::2");
        using var otherPrefix = Client(app, "ipv6-three", "2001:db8:1:2::1");

        Assert.Equal(HttpStatusCode.OK, (await PostBootstrapAsync(first)).StatusCode);
        using var samePrefixRejected = await PostBootstrapAsync(second);
        using var otherPrefixAllowed = await PostBootstrapAsync(otherPrefix);

        await AssertRateLimitExceeded(samePrefixRejected);
        Assert.Equal(HttpStatusCode.OK, otherPrefixAllowed.StatusCode);
    }

    [Fact]
    public async Task Host_refuses_non_positive_limits_at_startup()
    {
        await using var app = CreateApplication(perIp: 0, perIdentity: 10);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => app.SeedAsync(_ => Task.CompletedTask));

        Assert.Contains("PerIp:PermitLimit", exception.Message, StringComparison.Ordinal);
    }

    private static ControlApiTestApplication CreateApplication(
        int perIp,
        int perIdentity,
        IReadOnlyDictionary<string, string?>? extra = null)
    {
        var configuration = new Dictionary<string, string?>
        {
            [$"{CloudBootstrapRateLimitOptions.ConfigurationSection}:PerIp:PermitLimit"] = perIp.ToString(),
            [$"{CloudBootstrapRateLimitOptions.ConfigurationSection}:PerIp:Window"] = "00:01:00",
            [$"{CloudBootstrapRateLimitOptions.ConfigurationSection}:PerIdentity:PermitLimit"] = perIdentity.ToString(),
            [$"{CloudBootstrapRateLimitOptions.ConfigurationSection}:PerIdentity:Window"] = "00:01:00"
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
        string hubIssuer,
        string cloudIssuer) =>
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

    private static CloudBootstrapRateLimitConfigurationValidator CreateValidator(
        int perIpLimit = 60,
        TimeSpan? perIpWindow = null,
        int perIdentityLimit = 60,
        TimeSpan? perIdentityWindow = null) =>
        new(Options.Create(new CloudBootstrapRateLimitOptions
        {
            PerIp = new CloudBootstrapFixedWindowRateLimitOptions
            {
                PermitLimit = perIpLimit,
                Window = perIpWindow ?? TimeSpan.FromMinutes(1)
            },
            PerIdentity = new CloudBootstrapFixedWindowRateLimitOptions
            {
                PermitLimit = perIdentityLimit,
                Window = perIdentityWindow ?? TimeSpan.FromMinutes(1)
            }
        }));

    private static HttpClient Client(ControlApiTestApplication app, string subject, string ip)
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

    private static HttpClient IncompleteCookieClient(
        ControlApiTestApplication app,
        string ip,
        string? subject,
        string? issuer)
    {
        var client = app.CreateClient();
        app.AddControlSessionCookie(client, subject, issuer, DateTimeOffset.UtcNow.AddHours(2));
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

    private static HttpClient AnonymousClient(ControlApiTestApplication app, string ip)
    {
        var client = app.CreateClient();
        SetRemoteIp(client, ip);
        return client;
    }

    private static void SetRemoteIp(HttpClient client, string ip)
    {
        client.DefaultRequestHeaders.Remove(ControlApiTestApplication.TestRemoteIpHeader);
        client.DefaultRequestHeaders.TryAddWithoutValidation(ControlApiTestApplication.TestRemoteIpHeader, ip);
    }

    private static string GetIpKey(string address)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(address);
        return CloudBootstrapChainedRateLimiter.GetIpPartitionKey(context);
    }

    private static Task<HttpResponseMessage> PostBootstrapAsync(HttpClient client) =>
        client.PostAsJsonAsync("/api/cloud/bootstrap", new { });

    private static async Task AssertRateLimitExceeded(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.NotNull(response.Headers.RetryAfter);
        var retryAfterSeconds = response.Headers.RetryAfter.Delta?.TotalSeconds
                                ?? (int.TryParse(response.Headers.RetryAfter.ToString(), out var parsed) ? parsed : 0);
        Assert.InRange(retryAfterSeconds, 1, 60);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(CloudBootstrapRateLimitDefaults.ExceededCode, body.RootElement.GetProperty("code").GetString());
        Assert.Equal(CloudBootstrapRateLimitDefaults.ExceededTitle, body.RootElement.GetProperty("title").GetString());
        Assert.Equal(429, body.RootElement.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("correlationId").GetString()));
        Assert.False(body.RootElement.TryGetProperty("retryAfterSeconds", out _));
    }
}
