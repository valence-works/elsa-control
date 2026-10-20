using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace ElsaControl.Api.Tests;

public sealed class ControlForwardedHeadersTests
{
    // A non-loopback peer proves KnownIPNetworks/KnownProxies are cleared: the defaults
    // trust only loopback, so a pinned list would ignore X-Forwarded-For from this peer.
    private const string FrontEndPeer = "198.51.100.200";

    [Theory]
    [InlineData("203.0.113.9, 198.51.100.7", "198.51.100.7")]
    [InlineData("198.51.100.7", "198.51.100.7")]
    [InlineData("192.0.2.1, 203.0.113.9, 198.51.100.7", "198.51.100.7")]
    public async Task Remote_ip_address_is_the_rightmost_forwarded_for_entry(string forwardedFor, string expected)
    {
        var (app, observed) = CreateApplication();
        await using var _ = app;
        await app.SeedAsync(_ => Task.CompletedTask);

        var request = await SendAsync(app, observed, forwardedFor: forwardedFor);

        Assert.Equal(IPAddress.Parse(expected), request.RemoteIpAddress);
    }

    [Fact]
    public async Task Host_forwarded_headers_switch_does_not_add_a_second_pass()
    {
        var (app, observed) = CreateApplication(new Dictionary<string, string?> { ["ForwardedHeaders_Enabled"] = "true" });
        await using var _ = app;
        await app.SeedAsync(_ => Task.CompletedTask);

        var request = await SendAsync(app, observed, forwardedFor: "203.0.113.9, 198.51.100.7");

        Assert.Equal(IPAddress.Parse("198.51.100.7"), request.RemoteIpAddress);
    }

    [Fact]
    public async Task Remote_ip_address_is_the_peer_without_forwarded_for()
    {
        var (app, observed) = CreateApplication();
        await using var _ = app;
        await app.SeedAsync(_ => Task.CompletedTask);

        var request = await SendAsync(app, observed);

        Assert.Equal(IPAddress.Parse(FrontEndPeer), request.RemoteIpAddress);
    }

    [Fact]
    public async Task Forwarded_proto_sets_the_request_scheme()
    {
        var (app, observed) = CreateApplication();
        await using var _ = app;
        await app.SeedAsync(_ => Task.CompletedTask);

        var forwarded = await SendAsync(app, observed, forwardedProto: "https");
        var direct = await SendAsync(app, observed);

        Assert.Equal("https", forwarded.Scheme);
        Assert.Equal("http", direct.Scheme);
    }

    private static (ControlApiTestApplication App, ConcurrentDictionary<string, ObservedRequest> Observed) CreateApplication(
        IReadOnlyDictionary<string, string?>? configuration = null)
    {
        var observed = new ConcurrentDictionary<string, ObservedRequest>();
        var app = new ControlApiTestApplication(
            configuration,
            configureServices: services => services.AddSingleton<IStartupFilter>(new ObservingStartupFilter(observed)));
        return (app, observed);
    }

    private static async Task<ObservedRequest> SendAsync(
        ControlApiTestApplication app,
        ConcurrentDictionary<string, ObservedRequest> observed,
        string? forwardedFor = null,
        string? forwardedProto = null)
    {
        var marker = Guid.NewGuid().ToString("N");
        using var client = app.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add(ObservingStartupFilter.MarkerHeader, marker);
        request.Headers.Add(ControlApiTestApplication.TestRemoteIpHeader, FrontEndPeer);
        if (forwardedFor is not null)
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", forwardedFor);
        if (forwardedProto is not null)
            request.Headers.Add("X-Forwarded-Proto", forwardedProto);

        using var _ = await client.SendAsync(request);

        return Assert.Contains(marker, (IDictionary<string, ObservedRequest>)observed);
    }

    private sealed record ObservedRequest(IPAddress? RemoteIpAddress, string Scheme);

    // Registered after the test peer filter and before the application pipeline, so it reads
    // the connection after the application's forwarded-headers middleware has run.
    private sealed class ObservingStartupFilter(ConcurrentDictionary<string, ObservedRequest> observed) : IStartupFilter
    {
        public const string MarkerHeader = "X-Test-Observe";

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            app =>
            {
                app.Use(async (context, nextMiddleware) =>
                {
                    await nextMiddleware();
                    var marker = context.Request.Headers[MarkerHeader].FirstOrDefault();
                    if (!string.IsNullOrEmpty(marker))
                        observed[marker] = new ObservedRequest(context.Connection.RemoteIpAddress, context.Request.Scheme);
                });
                next(app);
            };
    }
}
