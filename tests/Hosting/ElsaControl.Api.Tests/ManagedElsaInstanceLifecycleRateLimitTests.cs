using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ElsaControl.Api.Authentication;
using ElsaControl.Api.Workspace;
using Microsoft.Extensions.Options;

namespace ElsaControl.Api.Tests;

public sealed class ManagedElsaInstanceLifecycleRateLimitTests
{
    [Theory]
    [InlineData("restart")]
    [InlineData("apply-release")]
    public async Task Cloud_bff_sessions_from_one_ip_are_limited_by_per_ip(string action)
    {
        const string issuer = "https://cloud-account.test/auth/v1";
        const string sharedIp = "203.0.113.52";
        await using var app = CreateCloudApplication(perIp: 1, perIdentity: 20, issuer);
        await app.SeedAsync(_ => Task.CompletedTask);
        using var first = CloudClient(app, issuer, "11111111-1111-4111-8111-111111111211", sharedIp);
        using var second = CloudClient(app, issuer, "22222222-2222-4222-8222-222222222212", sharedIp);
        using var otherIp = CloudClient(app, issuer, "33333333-3333-4333-8333-333333333213", "203.0.113.54");

        using var firstAllowed = await PostActionAsync(first, action);
        using var rejected = await PostActionAsync(second, action);
        using var allowed = await PostActionAsync(otherIp, action);

        Assert.NotEqual(HttpStatusCode.TooManyRequests, firstAllowed.StatusCode);
        await AssertLifecycleRateLimited(rejected);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, allowed.StatusCode);
    }

    [Theory]
    [InlineData("restart")]
    [InlineData("apply-release")]
    public async Task Distinct_cloud_session_identities_from_one_ip_are_isolated_by_identity(string action)
    {
        const string issuer = "https://cloud-account.test/auth/v1";
        const string sharedIp = "203.0.113.52";
        const string firstSubject = "11111111-1111-4111-8111-111111111211";
        await using var app = CreateCloudApplication(perIp: 20, perIdentity: 1, issuer);
        await app.SeedAsync(_ => Task.CompletedTask);
        using var first = CloudClient(app, issuer, firstSubject, sharedIp);
        using var second = CloudClient(app, issuer, "22222222-2222-4222-8222-222222222212", sharedIp);
        using var firstAgain = CloudClient(app, issuer, firstSubject, sharedIp);

        using var firstAllowed = await PostActionAsync(first, action);
        using var secondAllowed = await PostActionAsync(second, action);
        using var firstRejected = await PostActionAsync(firstAgain, action);

        Assert.NotEqual(HttpStatusCode.TooManyRequests, firstAllowed.StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, secondAllowed.StatusCode);
        await AssertLifecycleRateLimited(firstRejected);
    }

    [Theory]
    [InlineData("restart")]
    [InlineData("apply-release")]
    public async Task Over_the_per_identity_limit_from_different_ips_rejects_that_identity_only(string action)
    {
        const string issuer = "https://cloud-account.test/auth/v1";
        await using var app = CreateCloudApplication(perIp: 20, perIdentity: 1, issuer);
        await app.SeedAsync(_ => Task.CompletedTask);
        using var first = CloudClient(app, issuer, "b6f73e84-5b47-4f4d-ad52-e3c4c9a815c6", "203.0.113.61");
        using var second = CloudClient(app, issuer, "b6f73e84-5b47-4f4d-ad52-e3c4c9a815c6", "203.0.113.62");
        using var other = CloudClient(app, issuer, "8ffab131-51f3-48f6-a777-0f07ae2b281c", "203.0.113.61");

        using var firstAllowed = await PostActionAsync(first, action);
        using var rejected = await PostActionAsync(second, action);
        using var allowed = await PostActionAsync(other, action);

        Assert.NotEqual(HttpStatusCode.TooManyRequests, firstAllowed.StatusCode);
        await AssertLifecycleRateLimited(rejected);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, allowed.StatusCode);
    }

    [Theory]
    [InlineData("restart")]
    [InlineData("apply-release")]
    public async Task Hub_bff_sessions_from_one_ip_are_limited_by_per_ip(string action)
    {
        const string issuer = "https://hub-account.test/auth/v1";
        const string sharedIp = "203.0.113.53";
        await using var app = CreateHubApplication(perIp: 1, perIdentity: 20, issuer);
        await app.SeedAsync(_ => Task.CompletedTask);
        using var first = HubClient(app, issuer, "c0a80101-0000-4000-8000-000000000041", sharedIp);
        using var second = HubClient(app, issuer, "c0a80101-0000-4000-8000-000000000042", sharedIp);
        using var otherIp = HubClient(app, issuer, "c0a80101-0000-4000-8000-000000000043", "203.0.113.55");

        using var firstAllowed = await PostActionAsync(first, action);
        using var rejected = await PostActionAsync(second, action);
        using var allowed = await PostActionAsync(otherIp, action);

        Assert.NotEqual(HttpStatusCode.TooManyRequests, firstAllowed.StatusCode);
        await AssertLifecycleRateLimited(rejected);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, allowed.StatusCode);
    }

    [Theory]
    [InlineData("restart")]
    [InlineData("apply-release")]
    public async Task Distinct_hub_session_identities_from_one_ip_are_isolated_by_identity(string action)
    {
        const string issuer = "https://hub-account.test/auth/v1";
        const string sharedIp = "203.0.113.53";
        const string firstSubject = "c0a80101-0000-4000-8000-000000000041";
        await using var app = CreateHubApplication(perIp: 20, perIdentity: 1, issuer);
        await app.SeedAsync(_ => Task.CompletedTask);
        using var first = HubClient(app, issuer, firstSubject, sharedIp);
        using var second = HubClient(app, issuer, "c0a80101-0000-4000-8000-000000000042", sharedIp);
        using var firstAgain = HubClient(app, issuer, firstSubject, sharedIp);

        using var firstAllowed = await PostActionAsync(first, action);
        using var secondAllowed = await PostActionAsync(second, action);
        using var firstRejected = await PostActionAsync(firstAgain, action);

        Assert.NotEqual(HttpStatusCode.TooManyRequests, firstAllowed.StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, secondAllowed.StatusCode);
        await AssertLifecycleRateLimited(firstRejected);
    }

    [Theory]
    [InlineData("restart")]
    [InlineData("apply-release")]
    public async Task Resolved_bff_identities_on_shared_egress_are_not_capped_by_per_ip(string action)
    {
        const string cloudIssuer = "https://cloud-account.test/auth/v1";
        const string hubIssuer = "https://hub-account.test/auth/v1";
        const string sharedIp = "203.0.113.56";
        await using var app = CreateCloudAndHubApplication(
            perIp: 1,
            perIdentity: 1,
            cloudIssuer,
            hubIssuer,
            bffPerIp: 20,
            bffEgressNetworks: [$"{sharedIp}/32"]);
        await app.SeedAsync(_ => Task.CompletedTask);
        using var cloud = CloudClient(app, cloudIssuer, "11111111-1111-4111-8111-111111111241", sharedIp);
        using var hub = HubClient(app, hubIssuer, "c0a80101-0000-4000-8000-000000000241", sharedIp);
        using var cloudAgain = CloudClient(app, cloudIssuer, "11111111-1111-4111-8111-111111111241", sharedIp);

        using var cloudAllowed = await PostActionAsync(cloud, action);
        using var hubAllowed = await PostActionAsync(hub, action);
        using var cloudRejected = await PostActionAsync(cloudAgain, action);

        Assert.NotEqual(HttpStatusCode.TooManyRequests, cloudAllowed.StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, hubAllowed.StatusCode);
        await AssertLifecycleRateLimited(cloudRejected);
    }

    [Theory]
    [InlineData("restart")]
    [InlineData("apply-release")]
    public async Task Validated_bff_session_without_egress_allowlist_uses_per_ip(string action)
    {
        const string issuer = "https://cloud-account.test/auth/v1";
        const string sharedIp = "203.0.113.57";
        await using var app = CreateCloudApplication(perIp: 1, perIdentity: 20, issuer, bffPerIp: 20);
        await app.SeedAsync(_ => Task.CompletedTask);
        using var first = CloudClient(app, issuer, "11111111-1111-4111-8111-111111111251", sharedIp);
        using var second = CloudClient(app, issuer, "22222222-2222-4222-8222-222222222252", sharedIp);

        using var firstAllowed = await PostActionAsync(first, action);
        using var rejected = await PostActionAsync(second, action);

        Assert.NotEqual(HttpStatusCode.TooManyRequests, firstAllowed.StatusCode);
        await AssertLifecycleRateLimited(rejected);
    }

    [Theory]
    [InlineData("restart", "cloud")]
    [InlineData("restart", "hub")]
    [InlineData("apply-release", "cloud")]
    [InlineData("apply-release", "hub")]
    public async Task Validated_bff_session_uses_per_ip_outside_egress_list_and_bff_per_ip_inside(
        string action,
        string account)
    {
        const string cloudIssuer = "https://cloud-account.test/auth/v1";
        const string hubIssuer = "https://hub-account.test/auth/v1";
        const string insideIp = "203.0.113.58";
        const string outsideIp = "203.0.113.59";
        var issuer = account == "cloud" ? cloudIssuer : hubIssuer;
        await using var app = CreateCloudAndHubApplication(
            perIp: 1,
            perIdentity: 20,
            cloudIssuer,
            hubIssuer,
            bffPerIp: 20,
            bffEgressNetworks: [$"{insideIp}/32"]);
        await app.SeedAsync(_ => Task.CompletedTask);
        using var outsideFirst = ClientFor(account, app, issuer, "11111111-1111-4111-8111-111111111261", outsideIp);
        using var outsideSecond = ClientFor(account, app, issuer, "22222222-2222-4222-8222-222222222262", outsideIp);
        using var insideFirst = ClientFor(account, app, issuer, "33333333-3333-4333-8333-333333333363", insideIp);
        using var insideSecond = ClientFor(account, app, issuer, "44444444-4444-4444-8444-444444444464", insideIp);

        using var outsideAllowed = await PostActionAsync(outsideFirst, action);
        using var outsideRejected = await PostActionAsync(outsideSecond, action);
        using var insideAllowed = await PostActionAsync(insideFirst, action);
        using var insideAlsoAllowed = await PostActionAsync(insideSecond, action);

        Assert.NotEqual(HttpStatusCode.TooManyRequests, outsideAllowed.StatusCode);
        await AssertLifecycleRateLimited(outsideRejected);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, insideAllowed.StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, insideAlsoAllowed.StatusCode);
    }

    [Theory]
    [InlineData("cloud", "current", true)]
    [InlineData("cloud", "previous", true)]
    [InlineData("cloud", "invalid", false)]
    [InlineData("cloud", "missing", false)]
    [InlineData("cloud", "mismatched", false)]
    [InlineData("hub", "current", true)]
    [InlineData("hub", "previous", true)]
    [InlineData("hub", "invalid", false)]
    [InlineData("hub", "missing", false)]
    [InlineData("hub", "mismatched", false)]
    public async Task Bff_credential_selects_one_window_per_source_only_for_its_own_session_type(
        string account,
        string credential,
        bool expectSourceWindow)
    {
        // PerIp = 1 and BffPerIp = 2. On bff:source:<name>, two calls from one address pass and a
        // third from another address is refused, because the window is per BFF, not per address.
        // On PerIp, the second call from one address is refused and the other address passes.
        const string cloudIssuer = "https://cloud-account.test/auth/v1";
        const string hubIssuer = "https://hub-account.test/auth/v1";
        const string action = "restart";
        var issuer = account == "cloud" ? cloudIssuer : hubIssuer;
        await using var app = CreateCloudAndHubApplication(
            perIp: 1,
            perIdentity: 20,
            cloudIssuer,
            hubIssuer,
            bffPerIp: 2,
            bffCredentials: true);
        await app.SeedAsync(_ => Task.CompletedTask);
        var header = TestBffCredentials.HeaderValue(account, credential);
        using var first = ClientFor(account, app, issuer, "11111111-1111-4111-8111-111111111401", "203.0.113.151");
        using var second = ClientFor(account, app, issuer, "22222222-2222-4222-8222-222222222402", "203.0.113.151");
        using var third = ClientFor(account, app, issuer, "33333333-3333-4333-8333-333333333403", "203.0.113.152");
        foreach (var client in new[] { first, second, third })
            TestBffCredentials.Set(client, header);

        using var firstResponse = await PostActionAsync(first, action);
        using var secondResponse = await PostActionAsync(second, action);
        using var thirdResponse = await PostActionAsync(third, action);

        Assert.NotEqual(HttpStatusCode.TooManyRequests, firstResponse.StatusCode);
        if (expectSourceWindow)
        {
            Assert.NotEqual(HttpStatusCode.TooManyRequests, secondResponse.StatusCode);
            await AssertLifecycleRateLimited(thirdResponse);
        }
        else
        {
            await AssertLifecycleRateLimited(secondResponse);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, thirdResponse.StatusCode);
        }
    }

    [Theory]
    [InlineData("restart")]
    [InlineData("apply-release")]
    public async Task Hub_account_sessions_share_the_per_identity_limit_across_ips(string action)
    {
        const string issuer = "https://hub-account.test/auth/v1";
        await using var app = CreateHubApplication(perIp: 20, perIdentity: 1, issuer);
        await app.SeedAsync(_ => Task.CompletedTask);
        using var first = HubClient(app, issuer, "c0a80101-0000-4000-8000-000000000051", "203.0.113.63");
        using var second = HubClient(app, issuer, "c0a80101-0000-4000-8000-000000000051", "203.0.113.64");
        using var other = HubClient(app, issuer, "c0a80101-0000-4000-8000-000000000052", "203.0.113.63");

        using var firstAllowed = await PostActionAsync(first, action);
        using var rejected = await PostActionAsync(second, action);
        using var allowed = await PostActionAsync(other, action);

        Assert.NotEqual(HttpStatusCode.TooManyRequests, firstAllowed.StatusCode);
        await AssertLifecycleRateLimited(rejected);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, allowed.StatusCode);
    }

    [Theory]
    [InlineData("restart")]
    [InlineData("apply-release")]
    public async Task Control_identity_callers_are_limited_by_ip(string action)
    {
        await using var app = CreateApplication(perIp: 1, perIdentity: 20);
        await app.SeedAsync(_ => Task.CompletedTask);
        using var first = ControlClient(app, "lifecycle-ip-one", "203.0.113.71");
        using var second = ControlClient(app, "lifecycle-ip-two", "203.0.113.71");
        using var otherIp = ControlClient(app, "lifecycle-ip-three", "203.0.113.72");

        using var firstAllowed = await PostActionAsync(first, action);
        using var rejected = await PostActionAsync(second, action);
        using var allowed = await PostActionAsync(otherIp, action);

        Assert.NotEqual(HttpStatusCode.TooManyRequests, firstAllowed.StatusCode);
        await AssertLifecycleRateLimited(rejected);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, allowed.StatusCode);
    }

    [Theory]
    [InlineData("restart")]
    [InlineData("apply-release")]
    public async Task Control_identity_callers_are_limited_by_identity(string action)
    {
        await using var app = CreateApplication(perIp: 20, perIdentity: 1);
        await app.SeedAsync(_ => Task.CompletedTask);
        using var first = ControlClient(app, "lifecycle-id-one", "203.0.113.73");
        using var second = ControlClient(app, "lifecycle-id-one", "203.0.113.74");
        using var other = ControlClient(app, "lifecycle-id-two", "203.0.113.73");

        using var firstAllowed = await PostActionAsync(first, action);
        using var rejected = await PostActionAsync(second, action);
        using var allowed = await PostActionAsync(other, action);

        Assert.NotEqual(HttpStatusCode.TooManyRequests, firstAllowed.StatusCode);
        await AssertLifecycleRateLimited(rejected);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, allowed.StatusCode);
    }

    [Theory]
    [InlineData("restart")]
    [InlineData("apply-release")]
    public async Task Cookie_sessions_are_isolated_by_identity(string action)
    {
        await using var app = CreateApplication(perIp: 20, perIdentity: 1);
        await app.SeedAsync(_ => Task.CompletedTask);
        using var first = CookieClient(app, "cookie-lifecycle-one", "203.0.113.75");
        using var second = CookieClient(app, "cookie-lifecycle-one", "203.0.113.76");
        using var other = CookieClient(app, "cookie-lifecycle-two", "203.0.113.75");

        using var firstAllowed = await PostActionAsync(first, action);
        using var rejected = await PostActionAsync(second, action);
        using var allowed = await PostActionAsync(other, action);

        Assert.NotEqual(HttpStatusCode.TooManyRequests, firstAllowed.StatusCode);
        await AssertLifecycleRateLimited(rejected);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, allowed.StatusCode);
    }

    [Theory]
    [InlineData("restart")]
    [InlineData("apply-release")]
    public async Task Cookie_session_without_identity_falls_back_to_per_ip(string action)
    {
        await using var app = CreateApplication(perIp: 1, perIdentity: 20);
        await app.SeedAsync(_ => Task.CompletedTask);
        using var first = IncompleteCookieClient(app, "203.0.113.91");
        using var second = IncompleteCookieClient(app, "203.0.113.91");
        using var otherIp = IncompleteCookieClient(app, "203.0.113.92");

        using var firstAllowed = await PostActionAsync(first, action);
        using var rejected = await PostActionAsync(second, action);
        using var allowed = await PostActionAsync(otherIp, action);

        Assert.NotEqual(HttpStatusCode.TooManyRequests, firstAllowed.StatusCode);
        await AssertLifecycleRateLimited(rejected);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, allowed.StatusCode);
    }

    [Theory]
    [InlineData("restart")]
    [InlineData("apply-release")]
    public async Task Cookie_session_without_identity_uses_the_unresolved_identity_fallback(string action)
    {
        const string sharedIp = "203.0.113.93";
        await using var app = CreateApplication(perIp: 20, perIdentity: 20);
        await app.SeedAsync(_ => Task.CompletedTask);
        using var first = IncompleteCookieClient(app, sharedIp);
        using var second = IncompleteCookieClient(app, sharedIp);
        using var resolved = ControlClient(app, "lifecycle-resolved", sharedIp);
        using var cookieResolved = CookieClient(app, "cookie-resolved", sharedIp);
        using var otherIp = IncompleteCookieClient(app, "203.0.113.94");

        using var firstAllowed = await PostActionAsync(first, action);
        using var rejected = await PostActionAsync(second, action);
        using var resolvedAllowed = await PostActionAsync(resolved, action);
        using var cookieAllowed = await PostActionAsync(cookieResolved, action);
        using var otherIpAllowed = await PostActionAsync(otherIp, action);

        Assert.NotEqual(HttpStatusCode.TooManyRequests, firstAllowed.StatusCode);
        await AssertLifecycleRateLimited(rejected);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, resolvedAllowed.StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, cookieAllowed.StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, otherIpAllowed.StatusCode);
    }

    [Theory]
    [InlineData("restart")]
    [InlineData("apply-release")]
    public async Task Same_subject_under_cloud_and_hub_issuers_uses_separate_buckets(string action)
    {
        const string cloudIssuer = "https://cloud-account.test/auth/v1";
        const string hubIssuer = "https://hub-account.test/auth/v1";
        const string subject = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
        await using var app = CreateCloudAndHubApplication(perIp: 20, perIdentity: 1, cloudIssuer, hubIssuer);
        await app.SeedAsync(_ => Task.CompletedTask);
        using var cloud = CloudClient(app, cloudIssuer, subject, "203.0.113.81");
        using var hub = HubClient(app, hubIssuer, subject, "203.0.113.82");
        using var cloudAgain = CloudClient(app, cloudIssuer, subject, "203.0.113.83");

        using var cloudAllowed = await PostActionAsync(cloud, action);
        using var hubAllowed = await PostActionAsync(hub, action);
        using var cloudRejected = await PostActionAsync(cloudAgain, action);

        Assert.NotEqual(HttpStatusCode.TooManyRequests, cloudAllowed.StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, hubAllowed.StatusCode);
        await AssertLifecycleRateLimited(cloudRejected);
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
    [InlineData(0, "00:01:00", "BffPerIp:PermitLimit")]
    [InlineData(-1, "00:01:00", "BffPerIp:PermitLimit")]
    [InlineData(10001, "00:01:00", "BffPerIp:PermitLimit")]
    [InlineData(1, "00:00:00", "BffPerIp:Window")]
    [InlineData(1, "01:00:01", "BffPerIp:Window")]
    public async Task Invalid_bff_per_ip_configuration_is_refused(int permitLimit, string window, string expected)
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateValidator(bffPerIpLimit: permitLimit, bffPerIpWindow: TimeSpan.Parse(window))
                .StartAsync(CancellationToken.None));

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
    [InlineData(10, "00:01:00")]
    [InlineData(10000, "01:00:00")]
    public async Task Valid_limits_are_accepted(int permitLimit, string window)
    {
        await CreateValidator(
            perIpLimit: permitLimit,
            perIpWindow: TimeSpan.Parse(window),
            bffPerIpLimit: permitLimit,
            bffPerIpWindow: TimeSpan.Parse(window),
            perIdentityLimit: permitLimit,
            perIdentityWindow: TimeSpan.Parse(window)).StartAsync(CancellationToken.None);
    }

    [Fact]
    public void Default_per_ip_permit_is_ten_times_per_identity()
    {
        var options = new ManagedElsaInstanceLifecycleRateLimitOptions();

        Assert.Equal(ManagedElsaInstanceLifecycleRateLimitDefaults.DefaultPerIdentityPermitLimit, options.PerIdentity.PermitLimit);
        Assert.Equal(ManagedElsaInstanceLifecycleRateLimitDefaults.DefaultPerIpPermitLimit, options.PerIp.PermitLimit);
        Assert.Equal(options.PerIdentity.PermitLimit * 10, options.PerIp.PermitLimit);
        Assert.Equal(ManagedElsaInstanceLifecycleRateLimitDefaults.DefaultBffPerIpPermitLimit, options.BffPerIp.PermitLimit);
        Assert.Equal(ControlRateLimitWindowRules.MaxPermitLimit, options.BffPerIp.PermitLimit);
    }

    private static ControlApiTestApplication CreateApplication(
        int perIp,
        int perIdentity,
        IReadOnlyDictionary<string, string?>? extra = null,
        int? bffPerIp = null,
        IReadOnlyList<string>? bffEgressNetworks = null,
        bool bffCredentials = false)
    {
        var bffIp = bffPerIp ?? perIp;
        var configuration = new Dictionary<string, string?>
        {
            [$"{ManagedElsaInstanceLifecycleRateLimitOptions.ConfigurationSection}:PerIp:PermitLimit"] = perIp.ToString(),
            [$"{ManagedElsaInstanceLifecycleRateLimitOptions.ConfigurationSection}:PerIp:Window"] = "00:01:00",
            [$"{ManagedElsaInstanceLifecycleRateLimitOptions.ConfigurationSection}:BffPerIp:PermitLimit"] = bffIp.ToString(),
            [$"{ManagedElsaInstanceLifecycleRateLimitOptions.ConfigurationSection}:BffPerIp:Window"] = "00:01:00",
            [$"{ManagedElsaInstanceLifecycleRateLimitOptions.ConfigurationSection}:PerIdentity:PermitLimit"] = perIdentity.ToString(),
            [$"{ManagedElsaInstanceLifecycleRateLimitOptions.ConfigurationSection}:PerIdentity:Window"] = "00:01:00"
        };
        if (bffEgressNetworks is not null)
        {
            for (var i = 0; i < bffEgressNetworks.Count; i++)
                configuration[$"{ControlRateLimitBffEgressOptions.ConfigurationKey}:{i}"] = bffEgressNetworks[i];
        }

        if (bffCredentials)
        {
            foreach (var (key, value) in TestBffCredentials.Configuration())
                configuration[key] = value;
        }

        if (extra is not null)
        {
            foreach (var (key, value) in extra)
                configuration[key] = value;
        }

        return new ControlApiTestApplication(configuration);
    }

    private static ControlApiTestApplication CreateCloudApplication(
        int perIp,
        int perIdentity,
        string issuer,
        int? bffPerIp = null,
        IReadOnlyList<string>? bffEgressNetworks = null) =>
        CreateApplication(
            perIp,
            perIdentity,
            bffPerIp: bffPerIp,
            bffEgressNetworks: bffEgressNetworks,
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
        string hubIssuer,
        int? bffPerIp = null,
        IReadOnlyList<string>? bffEgressNetworks = null,
        bool bffCredentials = false) =>
        CreateApplication(
            perIp,
            perIdentity,
            bffPerIp: bffPerIp,
            bffEgressNetworks: bffEgressNetworks,
            bffCredentials: bffCredentials,
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

    private static ManagedElsaInstanceLifecycleRateLimitConfigurationValidator CreateValidator(
        int perIpLimit = 10,
        TimeSpan? perIpWindow = null,
        int perIdentityLimit = 10,
        TimeSpan? perIdentityWindow = null,
        int bffPerIpLimit = ManagedElsaInstanceLifecycleRateLimitDefaults.DefaultBffPerIpPermitLimit,
        TimeSpan? bffPerIpWindow = null) =>
        new(Options.Create(new ManagedElsaInstanceLifecycleRateLimitOptions
        {
            PerIp = new ManagedElsaInstanceLifecycleFixedWindowRateLimitOptions
            {
                PermitLimit = perIpLimit,
                Window = perIpWindow ?? TimeSpan.FromMinutes(1)
            },
            BffPerIp = new ManagedElsaInstanceLifecycleFixedWindowRateLimitOptions
            {
                PermitLimit = bffPerIpLimit,
                Window = bffPerIpWindow ?? TimeSpan.FromMinutes(1)
            },
            PerIdentity = new ManagedElsaInstanceLifecycleFixedWindowRateLimitOptions
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
        client.DefaultRequestHeaders.Add("Origin", client.BaseAddress?.GetLeftPart(UriPartial.Authority) ?? "http://localhost");
        SetRemoteIp(client, ip);
        return client;
    }

    private static HttpClient IncompleteCookieClient(ControlApiTestApplication app, string ip)
    {
        var client = app.CreateClient();
        app.AddControlSessionCookie(
            client,
            subject: null,
            issuer: ControlApiTestApplication.TestControlIdentityIssuer,
            DateTimeOffset.UtcNow.AddHours(2));
        client.DefaultRequestHeaders.Add("Origin", client.BaseAddress?.GetLeftPart(UriPartial.Authority) ?? "http://localhost");
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

    private static HttpClient ClientFor(
        string account,
        ControlApiTestApplication app,
        string issuer,
        string subject,
        string ip) =>
        account == "hub" ? HubClient(app, issuer, subject, ip) : CloudClient(app, issuer, subject, ip);

    private static void SetRemoteIp(HttpClient client, string ip)
    {
        client.DefaultRequestHeaders.Remove(ControlApiTestApplication.TestRemoteIpHeader);
        client.DefaultRequestHeaders.TryAddWithoutValidation(ControlApiTestApplication.TestRemoteIpHeader, ip);
    }

    private static Task<HttpResponseMessage> PostActionAsync(HttpClient client, string action)
    {
        var workspaceId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var instanceId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var path = $"/api/workspaces/{workspaceId:D}/instances/{instanceId:D}/{action}";
        return action == "apply-release"
            ? client.PostAsJsonAsync(path, new { version = "3.8.5" })
            : client.PostAsJsonAsync(path, new { });
    }

    private static async Task AssertLifecycleRateLimited(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.NotNull(response.Headers.RetryAfter);
        var retryAfterSeconds = response.Headers.RetryAfter.Delta?.TotalSeconds
                                ?? (int.TryParse(response.Headers.RetryAfter.ToString(), out var parsed) ? parsed : 0);
        Assert.InRange(retryAfterSeconds, 1, 60);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            ManagedElsaInstanceLifecycleRateLimitDefaults.ExceededCode,
            body.RootElement.GetProperty("code").GetString());
        Assert.Equal(
            ManagedElsaInstanceLifecycleRateLimitDefaults.ExceededTitle,
            body.RootElement.GetProperty("title").GetString());
        Assert.Equal(429, body.RootElement.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("correlationId").GetString()));
        Assert.False(body.RootElement.TryGetProperty("retryAfterSeconds", out _));
    }
}
