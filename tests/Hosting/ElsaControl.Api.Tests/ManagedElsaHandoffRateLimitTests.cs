using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ElsaControl.Api.Authentication;
using ElsaControl.Api.Cloud;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
    public async Task Identity_at_its_limit_does_not_spend_the_shared_bff_source_window()
    {
        // #883: with BffPerIp = 3 and PerIdentity = 1, the identity's three refused retries
        // used to spend the shared window, so the next identity on the same BFF was refused.
        const string issuer = "https://cloud-account.test/auth/v1";
        await using var app = CreateCloudApplication(
            perIp: 1, perIdentity: 1, issuer, bffPerIp: 3, bffCredentials: true);
        await app.SeedAsync(_ => Task.CompletedTask);
        using var exhausted = CloudClient(app, issuer, "11111111-1111-4111-8111-111111111411", "203.0.113.141");
        using var other = CloudClient(app, issuer, "22222222-2222-4222-8222-222222222412", "203.0.113.142");
        TestBffCredentials.Set(exhausted, TestBffCredentials.Cloud);
        TestBffCredentials.Set(other, TestBffCredentials.Cloud);

        using var allowed = await PostIssueAsync(exhausted);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, allowed.StatusCode);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var refused = await PostIssueAsync(exhausted);
            await AssertHandoffRateLimited(refused);
        }

        using var otherAllowed = await PostIssueAsync(other);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, otherAllowed.StatusCode);
    }

    [Fact]
    public async Task Refusal_by_an_exhausted_shared_window_does_not_spend_the_identity_window()
    {
        const string issuer = "https://cloud-account.test/auth/v1";
        const string sharedIp = "203.0.113.143";
        const string subject = "11111111-1111-4111-8111-111111111413";
        await using var app = CreateCloudApplication(perIp: 1, perIdentity: 2, issuer);
        await app.SeedAsync(_ => Task.CompletedTask);
        using var other = CloudClient(app, issuer, "22222222-2222-4222-8222-222222222414", sharedIp);
        using var sameIp = CloudClient(app, issuer, subject, sharedIp);
        using var firstElsewhere = CloudClient(app, issuer, subject, "203.0.113.144");
        using var secondElsewhere = CloudClient(app, issuer, subject, "203.0.113.145");

        using var otherAllowed = await PostIssueAsync(other);
        using var firstRefused = await PostIssueAsync(sameIp);
        using var secondRefused = await PostIssueAsync(sameIp);
        using var firstAllowed = await PostIssueAsync(firstElsewhere);
        using var secondAllowed = await PostIssueAsync(secondElsewhere);

        Assert.NotEqual(HttpStatusCode.TooManyRequests, otherAllowed.StatusCode);
        await AssertHandoffRateLimited(firstRefused);
        await AssertHandoffRateLimited(secondRefused);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, firstAllowed.StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, secondAllowed.StatusCode);
    }

    [Fact]
    public async Task Bff_source_diagnostic_records_a_refused_request_once()
    {
        const string issuer = "https://cloud-account.test/auth/v1";
        const string listedIp = "198.51.100.8";
        var entries = new ConcurrentQueue<RecordedLogEntry>();
        await using var app = CreateCloudApplication(
            perIp: 20,
            perIdentity: 1,
            issuer,
            bffPerIp: 20,
            bffEgressNetworks: [$"{listedIp}/32"],
            logLevel: "Debug",
            configureServices: services => services.AddSingleton<ILoggerProvider>(new RecordingLoggerProvider(entries)));
        await app.SeedAsync(_ => Task.CompletedTask);
        using var client = CloudClient(app, issuer, "11111111-1111-4111-8111-111111111421", listedIp);

        using var allowed = await PostIssueAsync(client);
        using var refused = await PostIssueAsync(client);

        Assert.NotEqual(HttpStatusCode.TooManyRequests, allowed.StatusCode);
        await AssertHandoffRateLimited(refused);
        Assert.Equal(2, entries.Count(entry => entry.Category == ControlRateLimitBffSourceDiagnostic.LoggerCategory));
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
    public async Task Resolved_bff_identities_on_shared_egress_are_not_capped_by_per_ip()
    {
        const string cloudIssuer = "https://cloud-account.test/auth/v1";
        const string hubIssuer = "https://hub-account.test/auth/v1";
        const string sharedIp = "203.0.113.25";
        await using var app = CreateCloudAndHubApplication(
            perIp: 1,
            perIdentity: 1,
            cloudIssuer,
            hubIssuer,
            bffPerIp: 20,
            bffEgressNetworks: [$"{sharedIp}/32"]);
        await app.SeedAsync(_ => Task.CompletedTask);
        using var cloud = CloudClient(app, cloudIssuer, "11111111-1111-4111-8111-111111111141", sharedIp);
        using var hub = HubClient(app, hubIssuer, "c0a80101-0000-4000-8000-000000000141", sharedIp);
        using var cloudAgain = CloudClient(app, cloudIssuer, "11111111-1111-4111-8111-111111111141", sharedIp);

        using var cloudAllowed = await PostIssueAsync(cloud);
        using var hubAllowed = await PostIssueAsync(hub);
        using var cloudRejected = await PostIssueAsync(cloudAgain);

        Assert.NotEqual(HttpStatusCode.TooManyRequests, cloudAllowed.StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, hubAllowed.StatusCode);
        await AssertHandoffRateLimited(cloudRejected);
    }

    [Fact]
    public async Task Validated_bff_session_without_egress_allowlist_uses_per_ip()
    {
        const string issuer = "https://cloud-account.test/auth/v1";
        const string sharedIp = "203.0.113.27";
        await using var app = CreateCloudApplication(perIp: 1, perIdentity: 20, issuer, bffPerIp: 20);
        await app.SeedAsync(_ => Task.CompletedTask);
        using var first = CloudClient(app, issuer, "11111111-1111-4111-8111-111111111151", sharedIp);
        using var second = CloudClient(app, issuer, "22222222-2222-4222-8222-222222222252", sharedIp);

        using var firstAllowed = await PostIssueAsync(first);
        using var rejected = await PostIssueAsync(second);

        Assert.NotEqual(HttpStatusCode.TooManyRequests, firstAllowed.StatusCode);
        await AssertHandoffRateLimited(rejected);
    }

    [Theory]
    [InlineData("cloud")]
    [InlineData("hub")]
    public async Task Validated_bff_session_uses_per_ip_outside_egress_list_and_bff_per_ip_inside(string account)
    {
        const string cloudIssuer = "https://cloud-account.test/auth/v1";
        const string hubIssuer = "https://hub-account.test/auth/v1";
        const string insideIp = "203.0.113.28";
        const string outsideIp = "203.0.113.29";
        var issuer = account == "cloud" ? cloudIssuer : hubIssuer;
        await using var app = CreateCloudAndHubApplication(
            perIp: 1,
            perIdentity: 20,
            cloudIssuer,
            hubIssuer,
            bffPerIp: 20,
            bffEgressNetworks: [$"{insideIp}/32"]);
        await app.SeedAsync(_ => Task.CompletedTask);
        using var outsideFirst = ClientFor(account, app, issuer, "11111111-1111-4111-8111-111111111161", outsideIp);
        using var outsideSecond = ClientFor(account, app, issuer, "22222222-2222-4222-8222-222222222262", outsideIp);
        using var insideFirst = ClientFor(account, app, issuer, "33333333-3333-4333-8333-333333333363", insideIp);
        using var insideSecond = ClientFor(account, app, issuer, "44444444-4444-4444-8444-444444444464", insideIp);

        using var outsideAllowed = await PostIssueAsync(outsideFirst);
        using var outsideRejected = await PostIssueAsync(outsideSecond);
        using var insideAllowed = await PostIssueAsync(insideFirst);
        using var insideAlsoAllowed = await PostIssueAsync(insideSecond);

        Assert.NotEqual(HttpStatusCode.TooManyRequests, outsideAllowed.StatusCode);
        await AssertHandoffRateLimited(outsideRejected);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, insideAllowed.StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, insideAlsoAllowed.StatusCode);
    }

    [Fact]
    public async Task Spoofed_left_forwarded_for_entries_share_the_rightmost_entry_partition()
    {
        const string issuer = "https://cloud-account.test/auth/v1";
        await using var app = CreateCloudApplication(perIp: 1, perIdentity: 20, issuer);
        await app.SeedAsync(_ => Task.CompletedTask);
        using var first = CloudClient(app, issuer, "11111111-1111-4111-8111-111111111171", "198.51.100.200");
        using var spoofed = CloudClient(app, issuer, "22222222-2222-4222-8222-222222222272", "198.51.100.201");
        using var otherClient = CloudClient(app, issuer, "33333333-3333-4333-8333-333333333373", "198.51.100.200");
        SetForwardedFor(first, "203.0.113.9, 198.51.100.7");
        SetForwardedFor(spoofed, "192.0.2.44, 198.51.100.7");
        SetForwardedFor(otherClient, "203.0.113.9, 198.51.100.8");

        using var firstAllowed = await PostIssueAsync(first);
        using var spoofedRejected = await PostIssueAsync(spoofed);
        using var otherClientAllowed = await PostIssueAsync(otherClient);

        Assert.NotEqual(HttpStatusCode.TooManyRequests, firstAllowed.StatusCode);
        await AssertHandoffRateLimited(spoofedRejected);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, otherClientAllowed.StatusCode);
    }

    [Theory]
    [InlineData("cloud")]
    [InlineData("hub")]
    public async Task Bff_per_ip_applies_only_when_the_listed_address_is_the_rightmost_forwarded_for_entry(string account)
    {
        const string cloudIssuer = "https://cloud-account.test/auth/v1";
        const string hubIssuer = "https://hub-account.test/auth/v1";
        const string listedIp = "198.51.100.7";
        var issuer = account == "cloud" ? cloudIssuer : hubIssuer;
        await using var app = CreateCloudAndHubApplication(
            perIp: 1,
            perIdentity: 20,
            cloudIssuer,
            hubIssuer,
            bffPerIp: 20,
            bffEgressNetworks: [$"{listedIp}/32"]);
        await app.SeedAsync(_ => Task.CompletedTask);
        using var rightmostFirst = ClientFor(account, app, issuer, "11111111-1111-4111-8111-111111111181", "198.51.100.200");
        using var rightmostSecond = ClientFor(account, app, issuer, "22222222-2222-4222-8222-222222222282", "198.51.100.200");
        using var leftFirst = ClientFor(account, app, issuer, "33333333-3333-4333-8333-333333333383", "198.51.100.200");
        using var leftSecond = ClientFor(account, app, issuer, "44444444-4444-4444-8444-444444444484", "198.51.100.200");
        SetForwardedFor(rightmostFirst, $"203.0.113.9, {listedIp}");
        SetForwardedFor(rightmostSecond, $"192.0.2.44, {listedIp}");
        SetForwardedFor(leftFirst, $"{listedIp}, 203.0.113.31");
        SetForwardedFor(leftSecond, $"{listedIp}, 203.0.113.31");

        using var rightmostAllowed = await PostIssueAsync(rightmostFirst);
        using var rightmostAlsoAllowed = await PostIssueAsync(rightmostSecond);
        using var leftAllowed = await PostIssueAsync(leftFirst);
        using var leftRejected = await PostIssueAsync(leftSecond);

        Assert.NotEqual(HttpStatusCode.TooManyRequests, rightmostAllowed.StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, rightmostAlsoAllowed.StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, leftAllowed.StatusCode);
        await AssertHandoffRateLimited(leftRejected);
    }

    [Fact]
    public async Task Bff_source_diagnostic_logs_address_match_and_policy_at_debug_only()
    {
        const string issuer = "https://cloud-account.test/auth/v1";
        const string listedIp = "198.51.100.7";
        const string cookieValue = "diagnostic-cookie-value-must-not-be-logged";
        var debugEntries = new ConcurrentQueue<RecordedLogEntry>();
        var informationEntries = new ConcurrentQueue<RecordedLogEntry>();
        await using var debugApp = CreateCloudApplication(
            perIp: 20,
            perIdentity: 20,
            issuer,
            bffPerIp: 20,
            bffEgressNetworks: [$"{listedIp}/32"],
            logLevel: "Debug",
            configureServices: services => services.AddSingleton<ILoggerProvider>(new RecordingLoggerProvider(debugEntries)));
        await using var informationApp = CreateCloudApplication(
            perIp: 20,
            perIdentity: 20,
            issuer,
            bffPerIp: 20,
            bffEgressNetworks: [$"{listedIp}/32"],
            configureServices: services => services.AddSingleton<ILoggerProvider>(new RecordingLoggerProvider(informationEntries)));
        await debugApp.SeedAsync(_ => Task.CompletedTask);
        await informationApp.SeedAsync(_ => Task.CompletedTask);
        using var matched = CloudClient(debugApp, issuer, "11111111-1111-4111-8111-111111111191", "198.51.100.200");
        using var unmatched = CloudClient(debugApp, issuer, "22222222-2222-4222-8222-222222222292", "198.51.100.200");
        using var silent = CloudClient(informationApp, issuer, "33333333-3333-4333-8333-333333333393", "198.51.100.200");
        foreach (var client in new[] { matched, unmatched, silent })
            client.DefaultRequestHeaders.Add("Cookie", $"diagnostic={cookieValue}");
        SetForwardedFor(matched, $"203.0.113.9, {listedIp}");
        SetForwardedFor(unmatched, $"{listedIp}, 203.0.113.32");
        SetForwardedFor(silent, $"203.0.113.9, {listedIp}");

        using var _ = await PostIssueAsync(matched);
        using var __ = await PostIssueAsync(unmatched);
        using var ___ = await PostIssueAsync(silent);

        var diagnostics = debugEntries
            .Where(entry => entry.Category == ControlRateLimitBffSourceDiagnostic.LoggerCategory)
            .ToArray();
        Assert.Equal(2, diagnostics.Length);
        Assert.All(diagnostics, entry =>
        {
            Assert.Equal(LogLevel.Debug, entry.Level);
            Assert.Equal(ManagedElsaHandoffRateLimitDefaults.PolicyName, entry.State["PolicyName"]);
        });
        Assert.Equal(listedIp, diagnostics[0].State["RemoteIpAddress"]);
        Assert.True(Assert.IsType<bool>(diagnostics[0].State["Matched"]));
        Assert.Equal("203.0.113.32", diagnostics[1].State["RemoteIpAddress"]);
        Assert.False(Assert.IsType<bool>(diagnostics[1].State["Matched"]));
        var token = matched.DefaultRequestHeaders.Authorization!.Parameter!;
        foreach (var entry in diagnostics)
        {
            var text = entry.Message + string.Join(" ", entry.State.Values);
            Assert.DoesNotContain(token, text, StringComparison.Ordinal);
            Assert.DoesNotContain(cookieValue, text, StringComparison.Ordinal);
            Assert.DoesNotContain("11111111-1111-4111-8111-111111111191", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Authorization", text, StringComparison.OrdinalIgnoreCase);
        }

        Assert.DoesNotContain(
            informationEntries,
            entry => entry.Category == ControlRateLimitBffSourceDiagnostic.LoggerCategory);
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
        using var first = ClientFor(account, app, issuer, "11111111-1111-4111-8111-111111111301", "203.0.113.101");
        using var second = ClientFor(account, app, issuer, "22222222-2222-4222-8222-222222222302", "203.0.113.101");
        using var third = ClientFor(account, app, issuer, "33333333-3333-4333-8333-333333333303", "203.0.113.102");
        foreach (var client in new[] { first, second, third })
            TestBffCredentials.Set(client, header);

        using var firstResponse = await PostIssueAsync(first);
        using var secondResponse = await PostIssueAsync(second);
        using var thirdResponse = await PostIssueAsync(third);

        Assert.NotEqual(HttpStatusCode.TooManyRequests, firstResponse.StatusCode);
        if (expectSourceWindow)
        {
            Assert.NotEqual(HttpStatusCode.TooManyRequests, secondResponse.StatusCode);
            await AssertHandoffRateLimited(thirdResponse);
        }
        else
        {
            await AssertHandoffRateLimited(secondResponse);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, thirdResponse.StatusCode);
        }
    }

    [Fact]
    public async Task Cloud_credential_shares_one_bff_window_across_addresses_while_per_identity_still_applies()
    {
        const string issuer = "https://cloud-account.test/auth/v1";
        await using var sharedWindowApp = CreateCloudApplication(
            perIp: 1, perIdentity: 20, issuer, bffPerIp: 2, bffCredentials: true);
        await using var perIdentityApp = CreateCloudApplication(
            perIp: 1, perIdentity: 1, issuer, bffPerIp: 20, bffCredentials: true);
        await sharedWindowApp.SeedAsync(_ => Task.CompletedTask);
        await perIdentityApp.SeedAsync(_ => Task.CompletedTask);
        using var first = CloudClient(sharedWindowApp, issuer, "11111111-1111-4111-8111-111111111311", "203.0.113.111");
        using var second = CloudClient(sharedWindowApp, issuer, "22222222-2222-4222-8222-222222222312", "203.0.113.112");
        using var third = CloudClient(sharedWindowApp, issuer, "33333333-3333-4333-8333-333333333313", "203.0.113.113");
        using var identity = CloudClient(perIdentityApp, issuer, "44444444-4444-4444-8444-444444444314", "203.0.113.114");
        using var identityElsewhere = CloudClient(perIdentityApp, issuer, "44444444-4444-4444-8444-444444444314", "203.0.113.115");
        using var otherIdentitySameAddress = CloudClient(perIdentityApp, issuer, "55555555-5555-4555-8555-555555555315", "203.0.113.114");
        foreach (var client in new[] { first, second, third, identity, identityElsewhere, otherIdentitySameAddress })
            TestBffCredentials.Set(client, TestBffCredentials.Cloud);

        using var firstAllowed = await PostIssueAsync(first);
        using var secondAllowed = await PostIssueAsync(second);
        using var thirdRejected = await PostIssueAsync(third);
        using var identityAllowed = await PostIssueAsync(identity);
        using var identityRejected = await PostIssueAsync(identityElsewhere);
        using var otherIdentityAllowed = await PostIssueAsync(otherIdentitySameAddress);

        Assert.NotEqual(HttpStatusCode.TooManyRequests, firstAllowed.StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, secondAllowed.StatusCode);
        await AssertHandoffRateLimited(thirdRejected);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, identityAllowed.StatusCode);
        await AssertHandoffRateLimited(identityRejected);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, otherIdentityAllowed.StatusCode);
    }

    [Fact]
    public async Task Credential_window_wins_when_the_egress_list_also_matches()
    {
        // With BffPerIp = 2: if the egress key won, the two calls from the listed address would use
        // bff:<address> and the unlisted one bff:source:cloud, so all three would pass.
        const string issuer = "https://cloud-account.test/auth/v1";
        const string listedIp = "203.0.113.121";
        await using var app = CreateCloudApplication(
            perIp: 1,
            perIdentity: 20,
            issuer,
            bffPerIp: 2,
            bffEgressNetworks: [$"{listedIp}/32"],
            bffCredentials: true);
        await app.SeedAsync(_ => Task.CompletedTask);
        using var listed = CloudClient(app, issuer, "11111111-1111-4111-8111-111111111321", listedIp);
        using var unlisted = CloudClient(app, issuer, "22222222-2222-4222-8222-222222222322", "203.0.113.122");
        using var listedAgain = CloudClient(app, issuer, "33333333-3333-4333-8333-333333333323", listedIp);
        foreach (var client in new[] { listed, unlisted, listedAgain })
            TestBffCredentials.Set(client, TestBffCredentials.Cloud);

        using var listedAllowed = await PostIssueAsync(listed);
        using var unlistedAllowed = await PostIssueAsync(unlisted);
        using var listedRejected = await PostIssueAsync(listedAgain);

        Assert.NotEqual(HttpStatusCode.TooManyRequests, listedAllowed.StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, unlistedAllowed.StatusCode);
        await AssertHandoffRateLimited(listedRejected);
    }

    // An unresolved caller is not covered here: its unresolved-identity window (limit 1, keyed by
    // address) refuses a second call from the same address whatever the per-address layer does.
    // Unresolved_callers_presenting_one_credential_do_not_share_the_bff_source_window covers it.
    [Theory]
    [InlineData("anonymous")]
    [InlineData("control")]
    public async Task Valid_credential_without_a_validated_bff_session_stays_on_per_ip(string caller)
    {
        const string cloudIssuer = "https://cloud-account.test/auth/v1";
        const string hubIssuer = "https://hub-account.test/auth/v1";
        const string sharedIp = "203.0.113.131";
        await using var app = CreateCloudAndHubApplication(
            perIp: 1,
            perIdentity: 20,
            cloudIssuer,
            hubIssuer,
            bffPerIp: 20,
            bffCredentials: true);
        await app.SeedAsync(_ => Task.CompletedTask);
        using var first = CallerClient(app, caller, "credential-caller-one", sharedIp);
        using var second = CallerClient(app, caller, "credential-caller-two", sharedIp);
        TestBffCredentials.Set(first, TestBffCredentials.Cloud);
        TestBffCredentials.Set(second, TestBffCredentials.Hub);

        using var firstAllowed = await PostIssueAsync(first);
        using var rejected = await PostIssueAsync(second);

        Assert.NotEqual(HttpStatusCode.TooManyRequests, firstAllowed.StatusCode);
        await AssertHandoffRateLimited(rejected);
    }

    [Fact]
    public async Task Unresolved_callers_presenting_one_credential_do_not_share_the_bff_source_window()
    {
        // BffPerIp = 1: if the credential alone selected bff:source:cloud, the second caller would
        // share the first caller's window and be refused. On PerIp each address has its own window.
        const string cloudIssuer = "https://cloud-account.test/auth/v1";
        const string hubIssuer = "https://hub-account.test/auth/v1";
        await using var app = CreateCloudAndHubApplication(
            perIp: 20,
            perIdentity: 20,
            cloudIssuer,
            hubIssuer,
            bffPerIp: 1,
            bffCredentials: true);
        await app.SeedAsync(_ => Task.CompletedTask);
        using var first = CallerClient(app, "unresolved", "credential-caller-one", "203.0.113.132");
        using var second = CallerClient(app, "unresolved", "credential-caller-two", "203.0.113.133");
        TestBffCredentials.Set(first, TestBffCredentials.Cloud);
        TestBffCredentials.Set(second, TestBffCredentials.Cloud);

        using var firstAllowed = await PostIssueAsync(first);
        using var secondAllowed = await PostIssueAsync(second);

        Assert.NotEqual(HttpStatusCode.TooManyRequests, firstAllowed.StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, secondAllowed.StatusCode);
    }

    [Fact]
    public async Task Bff_source_diagnostic_records_the_credential_source_name_and_no_value_is_logged_at_trace()
    {
        const string issuer = "https://cloud-account.test/auth/v1";
        var entries = new ConcurrentQueue<RecordedLogEntry>();
        await using var app = CreateCloudApplication(
            perIp: 20,
            perIdentity: 20,
            issuer,
            bffPerIp: 20,
            bffCredentials: true,
            logLevel: "Trace",
            configureServices: services => AddTraceRecording(services, entries));
        await app.SeedAsync(_ => Task.CompletedTask);
        using var valid = CloudClient(app, issuer, "11111111-1111-4111-8111-111111111341", "203.0.113.141");
        using var invalid = CloudClient(app, issuer, "22222222-2222-4222-8222-222222222342", "203.0.113.142");
        using var missing = CloudClient(app, issuer, "33333333-3333-4333-8333-333333333343", "203.0.113.143");
        TestBffCredentials.Set(valid, TestBffCredentials.Cloud);
        TestBffCredentials.Set(invalid, TestBffCredentials.Invalid);

        using var _ = await PostIssueAsync(valid);
        using var __ = await PostIssueAsync(invalid);
        using var ___ = await PostIssueAsync(missing);

        var diagnostics = entries
            .Where(entry => entry.Category == ControlRateLimitBffSourceDiagnostic.LoggerCategory)
            .ToArray();
        Assert.Equal(3, diagnostics.Length);
        Assert.Equal(ControlRateLimitBffCredentialSources.Cloud, diagnostics[0].State["BffCredentialSource"]);
        Assert.Equal(ControlRateLimitBffSourceDiagnostic.NoCredentialSource, diagnostics[1].State["BffCredentialSource"]);
        Assert.Equal(ControlRateLimitBffSourceDiagnostic.NoCredentialSource, diagnostics[2].State["BffCredentialSource"]);
        Assert.Contains(entries, entry => entry.Level == LogLevel.Trace);
        Assert.Contains(entries, entry => entry.Category.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));
        AssertNoCredentialValueLogged(entries);
    }

    [Fact]
    public async Task Startup_validation_failure_logs_and_throws_without_the_credential_value()
    {
        const string sharedValue = "shared-bff-credential-value-must-not-leak-0001";
        var entries = new ConcurrentQueue<RecordedLogEntry>();
        await using var app = CreateApplication(
            perIp: 20,
            perIdentity: 20,
            extra: new Dictionary<string, string?>
            {
                [$"{ControlRateLimitBffCredentialOptions.ConfigurationSection}:Enabled"] = "true",
                [$"{ControlRateLimitBffCredentialOptions.ConfigurationSection}:Sources:0:Name"] = "cloud",
                [$"{ControlRateLimitBffCredentialOptions.ConfigurationSection}:Sources:0:Secret"] = sharedValue,
                [$"{ControlRateLimitBffCredentialOptions.ConfigurationSection}:Sources:1:Name"] = "hub",
                [$"{ControlRateLimitBffCredentialOptions.ConfigurationSection}:Sources:1:Secret"] = sharedValue
            },
            configureServices: services => AddTraceRecording(services, entries));

        var exception = Assert.Throws<InvalidOperationException>(() => app.CreateClient());

        var text = exception.ToString();
        Assert.Contains($"{ControlRateLimitBffCredentialOptions.ConfigurationSection}:Sources:1:Secret", text, StringComparison.Ordinal);
        Assert.DoesNotContain(sharedValue, text, StringComparison.Ordinal);
        Assert.Contains(entries, entry => entry.Exception?.Contains("Rate limiting BFF credential configuration is invalid", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(entries, entry => EntryText(entry).Contains(sharedValue, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Bff_source_diagnostic_is_enabled_by_the_dot_free_log_level_setting()
    {
        // App Service on Linux turns '.' into '_' in app setting names, so operators cannot
        // reach Logging:LogLevel:ElsaControl.Api.RateLimiting there; this key has no dots.
        var entries = await RecordBffSourceDiagnosticsAsync(sourceDiagnosticLogLevel: "Debug");

        var diagnostic = Assert.Single(entries);
        Assert.Equal(LogLevel.Debug, diagnostic.Level);
        Assert.Equal("198.51.100.8", diagnostic.State["RemoteIpAddress"]);
        Assert.True(Assert.IsType<bool>(diagnostic.State["Matched"]));
    }

    [Fact]
    public async Task Bff_source_diagnostic_stays_off_when_the_dot_free_log_level_setting_is_unset()
    {
        var entries = await RecordBffSourceDiagnosticsAsync(sourceDiagnosticLogLevel: null);

        Assert.Empty(entries);
    }

    [Theory]
    [InlineData("Verbose")]
    [InlineData("99")]
    public async Task Invalid_bff_source_diagnostic_log_level_refuses_startup_naming_the_key(string value)
    {
        await using var app = CreateCloudApplication(
            perIp: 20,
            perIdentity: 20,
            "https://cloud-account.test/auth/v1",
            sourceDiagnosticLogLevel: value);

        var exception = Assert.Throws<InvalidOperationException>(() => app.CreateClient());

        Assert.Contains("RateLimiting:SourceDiagnosticLogLevel", exception.Message, StringComparison.Ordinal);
    }

    private static async Task<RecordedLogEntry[]> RecordBffSourceDiagnosticsAsync(string? sourceDiagnosticLogLevel)
    {
        const string issuer = "https://cloud-account.test/auth/v1";
        const string listedIp = "198.51.100.8";
        var entries = new ConcurrentQueue<RecordedLogEntry>();
        await using (var app = CreateCloudApplication(
                         perIp: 20,
                         perIdentity: 20,
                         issuer,
                         bffPerIp: 20,
                         bffEgressNetworks: [$"{listedIp}/32"],
                         sourceDiagnosticLogLevel: sourceDiagnosticLogLevel,
                         configureServices: services =>
                             services.AddSingleton<ILoggerProvider>(new RecordingLoggerProvider(entries))))
        {
            await app.SeedAsync(_ => Task.CompletedTask);
            using var client = CloudClient(app, issuer, "44444444-4444-4444-8444-444444444494", "198.51.100.200");
            SetForwardedFor(client, $"203.0.113.9, {listedIp}");
            using var _ = await PostIssueAsync(client);
        }

        return entries
            .Where(entry => entry.Category == ControlRateLimitBffSourceDiagnostic.LoggerCategory)
            .ToArray();
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
    public async Task Control_identity_callers_are_not_limited_by_identity()
    {
        await using var app = CreateApplication(perIp: 20, perIdentity: 1);
        await app.SeedAsync(_ => Task.CompletedTask);
        using var first = ControlClient(app, "handoff-id-one", "203.0.113.43");
        using var second = ControlClient(app, "handoff-id-one", "203.0.113.44");
        using var other = ControlClient(app, "handoff-id-two", "203.0.113.43");

        using var firstAllowed = await PostIssueAsync(first);
        using var secondAllowed = await PostIssueAsync(second);
        using var otherAllowed = await PostIssueAsync(other);

        Assert.NotEqual(HttpStatusCode.TooManyRequests, firstAllowed.StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, secondAllowed.StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, otherAllowed.StatusCode);
    }

    [Fact]
    public async Task Cookie_sessions_with_identity_are_not_limited_by_identity()
    {
        await using var app = CreateApplication(perIp: 20, perIdentity: 1);
        await app.SeedAsync(_ => Task.CompletedTask);
        using var first = CookieClient(app, "cookie-handoff-one", "203.0.113.45");
        using var second = CookieClient(app, "cookie-handoff-one", "203.0.113.46");
        using var other = CookieClient(app, "cookie-handoff-two", "203.0.113.45");

        using var firstAllowed = await PostIssueAsync(first);
        using var secondAllowed = await PostIssueAsync(second);
        using var otherAllowed = await PostIssueAsync(other);

        Assert.NotEqual(HttpStatusCode.TooManyRequests, firstAllowed.StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, secondAllowed.StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, otherAllowed.StatusCode);
    }

    [Fact]
    public async Task Cookie_session_without_identity_falls_back_to_per_ip()
    {
        await using var app = CreateApplication(perIp: 1, perIdentity: 20);
        await app.SeedAsync(_ => Task.CompletedTask);
        using var first = IncompleteCookieClient(app, "203.0.113.91");
        using var second = IncompleteCookieClient(app, "203.0.113.91");
        using var otherIp = IncompleteCookieClient(app, "203.0.113.92");

        using var firstAllowed = await PostIssueAsync(first);
        using var rejected = await PostIssueAsync(second);
        using var allowed = await PostIssueAsync(otherIp);

        Assert.NotEqual(HttpStatusCode.TooManyRequests, firstAllowed.StatusCode);
        await AssertHandoffRateLimited(rejected);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, allowed.StatusCode);
    }

    [Fact]
    public async Task Cookie_session_without_identity_uses_the_unresolved_identity_fallback()
    {
        const string sharedIp = "203.0.113.93";
        await using var app = CreateApplication(perIp: 20, perIdentity: 20);
        await app.SeedAsync(_ => Task.CompletedTask);
        using var first = IncompleteCookieClient(app, sharedIp);
        using var second = IncompleteCookieClient(app, sharedIp);
        using var resolved = ControlClient(app, "handoff-resolved", sharedIp);
        using var cookieResolved = CookieClient(app, "cookie-resolved", sharedIp);
        using var otherIp = IncompleteCookieClient(app, "203.0.113.94");

        using var firstAllowed = await PostIssueAsync(first);
        using var rejected = await PostIssueAsync(second);
        using var resolvedAllowed = await PostIssueAsync(resolved);
        using var cookieAllowed = await PostIssueAsync(cookieResolved);
        using var otherIpAllowed = await PostIssueAsync(otherIp);

        Assert.NotEqual(HttpStatusCode.TooManyRequests, firstAllowed.StatusCode);
        await AssertHandoffRateLimited(rejected);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, resolvedAllowed.StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, cookieAllowed.StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, otherIpAllowed.StatusCode);
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
    [InlineData(20, "00:01:00")]
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
        var options = new ManagedElsaHandoffRateLimitOptions();

        Assert.Equal(ManagedElsaHandoffRateLimitDefaults.DefaultPerIdentityPermitLimit, options.PerIdentity.PermitLimit);
        Assert.Equal(ManagedElsaHandoffRateLimitDefaults.DefaultPerIpPermitLimit, options.PerIp.PermitLimit);
        Assert.Equal(options.PerIdentity.PermitLimit * 10, options.PerIp.PermitLimit);
        Assert.Equal(ManagedElsaHandoffRateLimitDefaults.DefaultBffPerIpPermitLimit, options.BffPerIp.PermitLimit);
        Assert.Equal(ControlRateLimitWindowRules.MaxPermitLimit, options.BffPerIp.PermitLimit);
    }

    private static ControlApiTestApplication CreateApplication(
        int perIp,
        int perIdentity,
        IReadOnlyDictionary<string, string?>? extra = null,
        int? bffPerIp = null,
        IReadOnlyList<string>? bffEgressNetworks = null,
        Action<IServiceCollection>? configureServices = null,
        bool bffCredentials = false)
    {
        var bffIp = bffPerIp ?? perIp;
        var configuration = new Dictionary<string, string?>
        {
            [$"{ManagedElsaHandoffDefaults.ConfigurationSection}:Enabled"] = "true",
            [$"{ManagedElsaHandoffRateLimitOptions.ConfigurationSection}:PerIp:PermitLimit"] = perIp.ToString(),
            [$"{ManagedElsaHandoffRateLimitOptions.ConfigurationSection}:PerIp:Window"] = "00:01:00",
            [$"{ManagedElsaHandoffRateLimitOptions.ConfigurationSection}:BffPerIp:PermitLimit"] = bffIp.ToString(),
            [$"{ManagedElsaHandoffRateLimitOptions.ConfigurationSection}:BffPerIp:Window"] = "00:01:00",
            [$"{ManagedElsaHandoffRateLimitOptions.ConfigurationSection}:PerIdentity:PermitLimit"] = perIdentity.ToString(),
            [$"{ManagedElsaHandoffRateLimitOptions.ConfigurationSection}:PerIdentity:Window"] = "00:01:00"
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

        return new ControlApiTestApplication(configuration, configureServices);
    }

    private static ControlApiTestApplication CreateCloudApplication(
        int perIp,
        int perIdentity,
        string issuer,
        int? bffPerIp = null,
        IReadOnlyList<string>? bffEgressNetworks = null,
        string? logLevel = null,
        Action<IServiceCollection>? configureServices = null,
        bool bffCredentials = false,
        string? sourceDiagnosticLogLevel = null)
    {
        var extra = new Dictionary<string, string?>
        {
            // The default category level the Control API ships with, so only the setting under test enables Debug.
            ["Logging:LogLevel:Default"] = "Information",
            [$"{CloudAccountIdentityDefaults.ConfigurationSection}:Enabled"] = "true",
            [$"{CloudAccountIdentityDefaults.ConfigurationSection}:Issuer"] = issuer,
            [$"{CloudAccountIdentityDefaults.ConfigurationSection}:Audience"] = "authenticated",
            [$"{CloudAccountIdentityDefaults.ConfigurationSection}:TestSigningKey"] =
                ControlApiTestApplication.TestControlIdentitySigningKey
        };
        if (logLevel is not null)
            extra[$"Logging:LogLevel:{ControlRateLimitBffSourceDiagnostic.LoggerCategory}"] = logLevel;
        if (sourceDiagnosticLogLevel is not null)
            extra["RateLimiting:SourceDiagnosticLogLevel"] = sourceDiagnosticLogLevel;

        return CreateApplication(
            perIp,
            perIdentity,
            bffPerIp: bffPerIp,
            bffEgressNetworks: bffEgressNetworks,
            extra: extra,
            configureServices: configureServices,
            bffCredentials: bffCredentials);
    }

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

    private static ManagedElsaHandoffRateLimitConfigurationValidator CreateValidator(
        int perIpLimit = 20,
        TimeSpan? perIpWindow = null,
        int perIdentityLimit = 20,
        TimeSpan? perIdentityWindow = null,
        int bffPerIpLimit = ManagedElsaHandoffRateLimitDefaults.DefaultBffPerIpPermitLimit,
        TimeSpan? bffPerIpWindow = null) =>
        new(Options.Create(new ManagedElsaHandoffRateLimitOptions
        {
            PerIp = new ManagedElsaHandoffFixedWindowRateLimitOptions
            {
                PermitLimit = perIpLimit,
                Window = perIpWindow ?? TimeSpan.FromMinutes(1)
            },
            BffPerIp = new ManagedElsaHandoffFixedWindowRateLimitOptions
            {
                PermitLimit = bffPerIpLimit,
                Window = bffPerIpWindow ?? TimeSpan.FromMinutes(1)
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

    private static HttpClient IncompleteCookieClient(ControlApiTestApplication app, string ip)
    {
        var client = app.CreateClient();
        app.AddControlSessionCookie(
            client,
            subject: null,
            issuer: ControlApiTestApplication.TestControlIdentityIssuer,
            DateTimeOffset.UtcNow.AddHours(2));
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

    private static HttpClient CallerClient(ControlApiTestApplication app, string caller, string subject, string ip)
    {
        switch (caller)
        {
            case "anonymous":
                var anonymous = app.CreateClient();
                SetRemoteIp(anonymous, ip);
                return anonymous;
            case "unresolved":
                return IncompleteCookieClient(app, ip);
            case "control":
                return ControlClient(app, subject, ip);
            default:
                throw new ArgumentOutOfRangeException(nameof(caller), caller, null);
        }
    }

    // A provider-specific rule outranks the appsettings category rules, so the recorder sees every
    // category at Trace.
    private static void AddTraceRecording(IServiceCollection services, ConcurrentQueue<RecordedLogEntry> entries)
    {
        services.AddSingleton<ILoggerProvider>(new RecordingLoggerProvider(entries));
        services.AddLogging(logging => logging.AddFilter<RecordingLoggerProvider>(null, LogLevel.Trace));
    }

    private static void AssertNoCredentialValueLogged(IEnumerable<RecordedLogEntry> entries)
    {
        foreach (var entry in entries)
        {
            var text = EntryText(entry);
            foreach (var value in TestBffCredentials.AllValues)
                Assert.DoesNotContain(value, text, StringComparison.Ordinal);
        }
    }

    private static string EntryText(RecordedLogEntry entry) =>
        entry.Message + " " + string.Join(" ", entry.State.Values) + " " + entry.Exception;

    private static void SetRemoteIp(HttpClient client, string ip)
    {
        client.DefaultRequestHeaders.Remove(ControlApiTestApplication.TestRemoteIpHeader);
        client.DefaultRequestHeaders.TryAddWithoutValidation(ControlApiTestApplication.TestRemoteIpHeader, ip);
    }

    private static void SetForwardedFor(HttpClient client, string forwardedFor) =>
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Forwarded-For", forwardedFor);

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

    private sealed record RecordedLogEntry(
        string Category,
        LogLevel Level,
        string Message,
        IReadOnlyDictionary<string, object?> State,
        string? Exception = null);

    private sealed class RecordingLoggerProvider(ConcurrentQueue<RecordedLogEntry> entries) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new RecordingLogger(categoryName, entries);

        public void Dispose()
        {
        }
    }

    private sealed class RecordingLogger(string category, ConcurrentQueue<RecordedLogEntry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var values = state as IEnumerable<KeyValuePair<string, object?>> ?? [];
            entries.Enqueue(new RecordedLogEntry(
                category,
                logLevel,
                formatter(state, exception),
                values.ToDictionary(pair => pair.Key, pair => pair.Value),
                exception?.ToString()));
        }
    }
}
