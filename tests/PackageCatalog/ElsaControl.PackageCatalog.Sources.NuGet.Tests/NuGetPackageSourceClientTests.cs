using ElsaControl.PackageCatalog.Core.Packaging;
using ElsaControl.PackageCatalog.Core.Packages;
using ElsaControl.PackageCatalog.Core.Sources;
using Microsoft.Extensions.Logging;
using NuGet.Versioning;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ElsaControl.PackageCatalog.Sources.NuGet.Tests;

public sealed class NuGetPackageSourceClientTests
{
    [Theory]
    [InlineData("Elsa.")]
    [InlineData("Elsa.*")]
    public async Task Prefix_sources_discover_matching_package_ids(string includePattern)
    {
        await using var feed = await LoopbackNuGetFeed.StartAsync();
        var client = new NuGetPackageSourceClient(new PackageSourcePatternMatcher());
        var source = new PackageSource
        {
            Url = feed.ServiceIndexUrl,
            IncludePatterns = [includePattern],
            ExcludePatterns = ["*.Tests"]
        };

        var versions = await client.FindPackageVersionsAsync(source);

        Assert.Equal([
            new DiscoveredPackageVersion("Elsa.Email", "1.0.0"),
            new DiscoveredPackageVersion("Elsa.Workflows", "2.0.0")
        ], versions);
        Assert.Contains("Elsa.", feed.SearchQueries);
    }

    [Fact]
    public async Task Leading_wildcard_only_sources_do_not_trigger_broad_feed_crawling()
    {
        var client = new NuGetPackageSourceClient(new PackageSourcePatternMatcher());
        var source = new PackageSource
        {
            Url = "https://example.invalid/v3/index.json",
            IncludePatterns = ["*.Elsa"]
        };

        var act = () => client.FindPackageVersionsAsync(source);

        var exception = await Assert.ThrowsAsync<NotSupportedException>(act);
        Assert.Contains("Leading wildcard-only sources are not crawled", exception.Message);
    }

    [Fact]
    public async Task Prefix_wildcard_sources_require_feed_search_support()
    {
        await using var feed = await LoopbackNuGetFeed.StartAsync(advertiseSearch: false);
        var client = new NuGetPackageSourceClient(new PackageSourcePatternMatcher());
        var source = new PackageSource
        {
            Url = feed.ServiceIndexUrl,
            IncludePatterns = ["Elsa."]
        };

        var act = () => client.FindPackageVersionsAsync(source);

        var exception = await Assert.ThrowsAsync<NotSupportedException>(act);
        Assert.Contains("advertises a NuGet search service", exception.Message);
    }

    [Fact]
    public async Task Exact_package_version_lookup_finds_published_versions_and_skips_missing_ones()
    {
        await using var feed = await LoopbackNuGetFeed.StartAsync();
        var client = new NuGetPackageSourceClient(new PackageSourcePatternMatcher());

        Assert.True(await client.PackageVersionExistsAsync(feed.ServiceIndexUrl, "Elsa.Email", "1.0.0"));
        Assert.False(await client.PackageVersionExistsAsync(feed.ServiceIndexUrl, "Elsa.Email", "9.9.9"));
        Assert.False(await client.PackageVersionExistsAsync(feed.ServiceIndexUrl, "Elsa.Missing", "1.0.0"));
        Assert.False(await client.PackageVersionExistsAsync(feed.ServiceIndexUrl, "Elsa.Email", "not-a-version"));
    }

    [Fact]
    public async Task Exact_package_version_lookup_fails_closed_without_naming_the_feed()
    {
        var client = new NuGetPackageSourceClient(new PackageSourcePatternMatcher());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.PackageVersionExistsAsync("https://127.0.0.1:1/v3/index.json", "Elsa.Email", "1.0.0"));

        Assert.Equal("The package feed is unavailable.", exception.Message);
        Assert.DoesNotContain("127.0.0.1", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("index.json", exception.Message, StringComparison.Ordinal);
    }

    private sealed class LoopbackNuGetFeed : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stopped = new();
        private readonly bool _advertiseSearch;
        private readonly Task _requests;

        private LoopbackNuGetFeed(bool advertiseSearch)
        {
            _advertiseSearch = advertiseSearch;
            _listener.Start();
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            BaseUrl = $"http://127.0.0.1:{port}/";
            _requests = Task.Run(ProcessRequestsAsync);
        }

        public string BaseUrl { get; }
        public string ServiceIndexUrl => $"{BaseUrl}v3/index.json";
        public List<string> SearchQueries { get; } = [];

        public static Task<LoopbackNuGetFeed> StartAsync(bool advertiseSearch = true) =>
            Task.FromResult(new LoopbackNuGetFeed(advertiseSearch));

        public async ValueTask DisposeAsync()
        {
            await _stopped.CancelAsync();
            _listener.Stop();
            try
            {
                await _requests;
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or IOException)
            {
            }
            _stopped.Dispose();
        }

        private async Task ProcessRequestsAsync()
        {
            while (!_stopped.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stopped.Token);
                }
                catch (Exception ex) when (_stopped.IsCancellationRequested && ex is OperationCanceledException or SocketException or ObjectDisposedException)
                {
                    return;
                }

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await RespondAsync(client);
                    }
                    catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
                    {
                    }
                }, _stopped.Token);
            }
        }

        private async Task RespondAsync(TcpClient client)
        {
            using var connection = client;
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(requestLine))
                return;

            while (!string.IsNullOrEmpty(await reader.ReadLineAsync()))
            {
            }

            var target = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1) ?? "/";
            var requestUri = new Uri(new Uri(BaseUrl), target);
            var path = requestUri.AbsolutePath;
            var json = path switch
            {
                "/v3/index.json" => ServiceIndexJson(),
                "/query" => SearchJson(GetQueryValue(requestUri, "q")),
                "/flat/elsa.email/index.json" => VersionIndexJson("1.0.0"),
                "/flat/elsa.workflows/index.json" => VersionIndexJson("2.0.0"),
                _ => "{}"
            };

            var knownPackageIndex = path is "/flat/elsa.email/index.json" or "/flat/elsa.workflows/index.json";
            var status = path.StartsWith("/flat/", StringComparison.OrdinalIgnoreCase) && !knownPackageIndex
                ? "404 Not Found"
                : "200 OK";
            var body = Encoding.UTF8.GetBytes(json);
            var header = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {status}\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header);
            await stream.WriteAsync(body);
        }

        private string ServiceIndexJson() =>
            $$"""
            {
              "@context": {
                "@vocab": "http://schema.nuget.org/services#",
                "comment": "http://www.w3.org/2000/01/rdf-schema#comment"
              },
              "version": "3.0.0",
              "resources": [
                {{SearchServiceResourceJson()}}
                { "@id": "{{BaseUrl}}flat/", "@type": "PackageBaseAddress/3.0.0" }
              ]
            }
            """;

        private string SearchJson(string query)
        {
            SearchQueries.Add(query);
            return """
            {
              "totalHits": 3,
              "data": [
                { "id": "Elsa.Email", "version": "1.0.0" },
                { "id": "Elsa.Tests", "version": "1.0.0" },
                { "id": "Elsa.Workflows", "version": "2.0.0" }
              ]
            }
            """;
        }

        private string SearchServiceResourceJson() =>
            _advertiseSearch ? $$""" { "@id": "{{BaseUrl}}query", "@type": "SearchQueryService/3.0.0-beta" },""" : "";

        private static string VersionIndexJson(string version) =>
            $$"""
            { "versions": ["{{version}}"] }
            """;

        private static string GetQueryValue(Uri uri, string name)
        {
            var pairs = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
            foreach (var pair in pairs)
            {
                var parts = pair.Split('=', 2);
                if (parts.Length == 2 && parts[0] == name)
                    return Uri.UnescapeDataString(parts[1].Replace('+', ' '));
            }

            return "";
        }
    }

    [Fact]
    public void Latest_stable_policy_selects_highest_non_prerelease_version()
    {
        var selected = Select(
            PackageSourceVersionDiscoveryPolicy.LatestStable,
            "3.0.2",
            "3.7.0-preview.4512",
            "2.9.0");

        Assert.Equal("3.0.2", Assert.Single(selected));
    }

    [Fact]
    public void Latest_stable_policy_logs_when_all_versions_are_prerelease()
    {
        var logger = new ListLogger<NuGetPackageSourceClient>();
        var selected = NuGetPackageSourceClient
            .SelectVersionsForPackage(
                new PackageSource
                {
                    Name = "NuGet",
                    VersionDiscoveryPolicy = PackageSourceVersionDiscoveryPolicy.LatestStable
                },
                "Elsa",
                [NuGetVersion.Parse("3.7.0-preview.4511"), NuGetVersion.Parse("3.7.0-preview.4512")],
                logger);

        Assert.Empty(selected);
        Assert.Single(logger.Messages, message =>
            message.Level == LogLevel.Warning &&
            message.Text.Contains("only prerelease versions", StringComparison.Ordinal) &&
            message.Text.Contains("LatestStable", StringComparison.Ordinal));
    }

    [Fact]
    public void Latest_prerelease_policy_selects_highest_version_including_previews()
    {
        var selected = Select(
            PackageSourceVersionDiscoveryPolicy.LatestIncludingPrerelease,
            "3.0.2",
            "3.7.0-preview.4512",
            "2.9.0");

        Assert.Equal("3.7.0-preview.4512", Assert.Single(selected));
    }

    [Fact]
    public void Latest_preview_policy_selects_highest_preview_prerelease_version()
    {
        var selected = Select(
            PackageSourceVersionDiscoveryPolicy.LatestPreview,
            "4.0.0",
            "4.1.0-alpha.1",
            "4.1.0-previewfoo.9",
            "4.1.0-preview.2",
            "4.1.0-PREVIEW.3",
            "4.1.0-rc.1");

        Assert.Equal("4.1.0-PREVIEW.3", Assert.Single(selected));
    }

    [Fact]
    public void Latest_preview_policy_skips_packages_without_preview_prerelease_versions()
    {
        var selected = Select(
            PackageSourceVersionDiscoveryPolicy.LatestPreview,
            "4.0.0",
            "4.1.0-alpha.1",
            "4.1.0-rc.1");

        Assert.Empty(selected);
    }

    [Fact]
    public void All_versions_policy_preserves_discovered_versions()
    {
        var selected = Select(
            PackageSourceVersionDiscoveryPolicy.AllVersions,
            "1.0.0",
            "2.0.0-preview.1",
            "2.0.0");

        Assert.Equal(["1.0.0", "2.0.0-preview.1", "2.0.0"], selected);
    }

    private static IReadOnlyList<string> Select(PackageSourceVersionDiscoveryPolicy policy, params string[] versions) =>
        NuGetPackageSourceClient
            .SelectVersions(policy, versions.Select(NuGetVersion.Parse))
            .Select(version => version.ToNormalizedString())
            .ToList();

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<LogMessage> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(new LogMessage(logLevel, formatter(state, exception)));
        }
    }

    private sealed record LogMessage(LogLevel Level, string Text);
}
