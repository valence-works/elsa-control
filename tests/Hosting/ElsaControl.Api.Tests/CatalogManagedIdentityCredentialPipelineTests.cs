using System.Net;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Identity;
using Xunit;
using ElsaControl.Api.Catalog;

namespace ElsaControl.Api.Tests;

public sealed class CatalogManagedIdentityCredentialPipelineTests
{
    [Fact]
    public async Task Actual_credential_reaches_token_after_unsupported_capability_without_probe_retries()
    {
        MsalManagedIdentityDiscovery.Reset();
        using var handler = new ImdsHandler();
        using var client = new HttpClient(handler);
        var delay = new RecordingDelay();
        var options = new ManagedIdentityCredentialOptions(
            ManagedIdentityId.FromUserAssignedClientId("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"))
        {
            RetryPolicy = new ManagedIdentityProbeRetryPolicy(delay),
            Transport = new HttpClientTransport(client)
        };
        var credential = new ManagedIdentityCredential(options);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var token = await credential.GetTokenAsync(
            new TokenRequestContext(["https://database.windows.net/.default"]), timeout.Token);

        Assert.Equal("synthetic-test-token", token.Token);
        Assert.Equal("capability", handler.Stages[0]);
        Assert.Equal("token", handler.Stages[^1]);
        Assert.Equal(1, handler.Stages.Count(x => x == "availability"));
        Assert.Equal(1, handler.Stages.Count(x => x == "token"));
        // MSAL 4.84.x probes IMDS compute metadata (mTLS binding strength) after detecting IMDSv1. Hosts such as
        // ACI answer 404; the probe is optional and must not consume managed-identity token retries.
        Assert.Equal(1, handler.Stages.Count(x => x == "compute"));
        Assert.DoesNotContain(handler.Stages, x => x.StartsWith("unexpected:", StringComparison.Ordinal));
        Assert.Equal(0, delay.Attempts);
        // The SDK may re-detect capabilities when it switches from its availability probe to MSAL.
        // Re-detection is permitted; retry waits are not (delay.Attempts above), and the exact
        // sync/async 404 predicate tests separately pin each suppressed probe shape.
        Assert.InRange(handler.Stages.Count(x => x == "capability"), 1, 2);
        Assert.DoesNotContain(handler.Stages.Zip(handler.Stages.Skip(1)), pair =>
            pair.First == "capability" && pair.Second == "capability");
    }

    private sealed class ImdsHandler : HttpMessageHandler
    {
        public List<string> Stages { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var metadata = request.Headers.Contains("Metadata");
            // Exceptions thrown here are swallowed by MSAL's optional probes, so record unexpected shapes instead.
            if (uri.Host != "169.254.169.254" || request.Method != HttpMethod.Get)
                return NotFound($"unexpected:{request.Method}:{uri.Host}");
            if (uri.AbsolutePath == "/metadata/identity/getplatformmetadata")
                return NotFound(!metadata && uri.Query == "?cred-api-version=2.0&client_id=aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"
                    ? "capability"
                    : "unexpected:capability-shape");
            if (uri.AbsolutePath == "/metadata/instance/compute")
                return NotFound(metadata && uri.Query == "?api-version=2021-02-01" ? "compute" : "unexpected:compute-shape");
            if (uri.AbsolutePath != "/metadata/identity/oauth2/token")
                return NotFound($"unexpected:{uri.AbsolutePath}");
            if (!metadata)
            {
                Stages.Add("availability");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest));
            }

            Stages.Add("token");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"access_token":"synthetic-test-token","expires_on":"4102444800","resource":"https://database.windows.net/","token_type":"Bearer"}
                    """)
            });
        }

        private Task<HttpResponseMessage> NotFound(string stage)
        {
            Stages.Add(stage);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
