using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using Xunit;
using ElsaControl.Api.Catalog;

namespace ElsaControl.Api.Tests;

public sealed class ManagedIdentityProbeRetryPolicyTests
{
    private const string Probe = "http://169.254.169.254/metadata/identity/getplatformmetadata?cred-api-version=2.0";
    private const string RegionProbe = "http://169.254.169.254/metadata/instance/compute/location?api-version=2020-06-01&format=text";
    private const string ComputeProbe = "http://169.254.169.254/metadata/instance/compute?api-version=2021-02-01";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unsupported_capability_probe_does_not_retry(bool async)
    {
        using var message = Message(Probe, 404);
        Assert.False(await new ExposedPolicy().Evaluate(message, async));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task User_assigned_capability_probe_does_not_retry(bool async)
    {
        using var message = Message(Probe + "&client_id=aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", 404);
        Assert.False(await new ExposedPolicy().Evaluate(message, async));
    }

    [Theory]
    [InlineData(RegionProbe, false)]
    [InlineData(RegionProbe, true)]
    [InlineData(ComputeProbe, false)]
    [InlineData(ComputeProbe, true)]
    public async Task Unsupported_instance_metadata_probe_does_not_retry(string uri, bool async)
    {
        using var message = Message(uri, 404, metadata: "true");
        Assert.False(await new ExposedPolicy().Evaluate(message, async));
    }

    [Theory]
    [InlineData("http://169.254.169.254/metadata/identity/oauth2/token", "GET", 404, "true")]
    [InlineData(Probe, "GET", 429, null)]
    [InlineData(Probe, "GET", 500, null)]
    [InlineData(Probe, "GET", 503, null)]
    [InlineData(Probe, "POST", 404, null)]
    [InlineData(Probe, "GET", 404, "true")]
    [InlineData("http://169.254.169.254/metadata/identity/getplatformmetadata", "GET", 404, null)]
    [InlineData(Probe + "&extra=true", "GET", 404, null)]
    [InlineData(Probe + "&client_id=", "GET", 404, null)]
    [InlineData(Probe + "&client_id=00000000-0000-0000-0000-000000000000", "GET", 404, null)]
    [InlineData(Probe + "&client_id=not-a-guid", "GET", 404, null)]
    [InlineData(Probe + "&client_id=aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee&extra=true", "GET", 404, null)]
    [InlineData(Probe + "#fragment", "GET", 404, null)]
    [InlineData("http://169.254.169.254/metadata/identity/getplatformmetadata?cred-api-version=3.0", "GET", 404, null)]
    [InlineData("http://169.254.169.254/metadata/identity/other", "GET", 404, null)]
    [InlineData("https://example.test/metadata/identity/getplatformmetadata", "GET", 404, null)]
    [InlineData("http://169.254.169.254:8080/metadata/identity/getplatformmetadata", "GET", 404, null)]
    [InlineData(RegionProbe, "GET", 410, "true")]
    [InlineData(RegionProbe, "GET", 500, "true")]
    [InlineData(RegionProbe, "POST", 404, "true")]
    [InlineData(RegionProbe, "GET", 404, null)]
    [InlineData(RegionProbe, "GET", 404, "false")]
    [InlineData("http://169.254.169.254/metadata/instance/compute/location", "GET", 404, "true")]
    [InlineData(RegionProbe + "&extra=true", "GET", 404, "true")]
    [InlineData(RegionProbe + "#fragment", "GET", 404, "true")]
    [InlineData("https://example.test/metadata/instance/compute/location?api-version=2020-06-01&format=text", "GET", 404, "true")]
    [InlineData(ComputeProbe, "GET", 410, "true")]
    [InlineData(ComputeProbe, "GET", 500, "true")]
    [InlineData(ComputeProbe, "POST", 404, "true")]
    [InlineData(ComputeProbe, "GET", 404, null)]
    [InlineData(ComputeProbe, "GET", 404, "false")]
    [InlineData("http://169.254.169.254/metadata/instance/compute", "GET", 404, "true")]
    [InlineData("http://169.254.169.254/metadata/instance/compute?api-version=2020-06-01", "GET", 404, "true")]
    [InlineData("http://169.254.169.254/metadata/instance/compute/?api-version=2021-02-01", "GET", 404, "true")]
    [InlineData("http://169.254.169.254/metadata/instance?api-version=2021-02-01", "GET", 404, "true")]
    [InlineData(ComputeProbe + "&format=json", "GET", 404, "true")]
    [InlineData(ComputeProbe + "#fragment", "GET", 404, "true")]
    [InlineData("https://example.test/metadata/instance/compute?api-version=2021-02-01", "GET", 404, "true")]
    [InlineData("http://169.254.169.254:8080/metadata/instance/compute?api-version=2021-02-01", "GET", 404, "true")]
    public async Task Unrelated_or_transient_response_preserves_classifier_retry(string uri, string method, int status, string? metadata)
    {
        using var message = Message(uri, status, method, metadata);
        var policy = new ExposedPolicy();
        Assert.True(await policy.Evaluate(message, false));
        Assert.True(await policy.Evaluate(message, true));
    }

    [Fact]
    public async Task Transport_failure_with_prior_probe_response_is_not_suppressed()
    {
        using var message = Message(Probe, 404);
        var policy = new ExposedPolicy();
        var error = new IOException();
        Assert.True(await policy.Evaluate(message, false, error));
        Assert.True(await policy.Evaluate(message, true, error));
    }

    [Theory]
    [InlineData(404, true)]
    [InlineData(410, true)]
    [InlineData(429, true)]
    [InlineData(500, true)]
    [InlineData(502, false)]
    [InlineData(503, true)]
    [InlineData(400, false)]
    [InlineData(401, false)]
    public async Task Token_response_preserves_managed_identity_retry_classification(int status, bool expected)
    {
        using var message = Message("http://169.254.169.254/metadata/identity/oauth2/token", status, metadata: "true");
        message.ResponseClassifier = new ResponseClassifier();
        var policy = new ExposedPolicy();
        Assert.Equal(expected, await policy.Evaluate(message, false));
        Assert.Equal(expected, await policy.Evaluate(message, true));
    }

    [Fact]
    public async Task Existing_token_availability_probe_still_does_not_retry()
    {
        using var message = Message("http://169.254.169.254/metadata/identity/oauth2/token", 404);
        var policy = new ExposedPolicy();
        Assert.False(await policy.Evaluate(message, false));
        Assert.False(await policy.Evaluate(message, true));
    }

    private static HttpMessage Message(string uri, int status, string method = "GET", string? metadata = null)
    {
        var request = HttpClientTransport.Shared.CreateRequest();
        request.Uri.Reset(new Uri(uri));
        request.Method = new RequestMethod(method);
        if (metadata is not null) request.Headers.Add("Metadata", metadata);
        return new HttpMessage(request, new RetryClassifier()) { Response = new StatusResponse(status) };
    }

    [Fact]
    public void Imds_410_keeps_at_least_seventy_seconds_of_retry_budget_without_sleeping()
    {
        using var response = new StatusResponse(410);
        var delay = new ManagedIdentityRetryDelay();
        var total = Enumerable.Range(1, 5).Sum(n => delay.GetNextDelay(response, n).TotalSeconds);
        Assert.InRange(total, 74.4, 111.6);
    }

    [Theory]
    [InlineData(404)]
    [InlineData(429)]
    [InlineData(503)]
    public void Other_failures_keep_normal_exponential_delay(int status)
    {
        using var response = new StatusResponse(status);
        var delay = new ManagedIdentityRetryDelay();
        for (var retry = 1; retry <= 5; retry++)
        {
            var baseline = 0.8 * Math.Pow(2, retry - 1);
            Assert.InRange(delay.GetNextDelay(response, retry).TotalSeconds, baseline * 0.8, baseline * 1.2);
        }
    }

    private sealed class ExposedPolicy : ManagedIdentityProbeRetryPolicy
    {
        public ValueTask<bool> Evaluate(HttpMessage message, bool async, Exception? error = null) =>
            async ? ShouldRetryAsync(message, error) : ValueTask.FromResult(ShouldRetry(message, error));
    }

    // MI's outer pipeline can classify404 as retryable. Preserve that contract except the precise probe.
    private sealed class RetryClassifier : ResponseClassifier
    {
        public override bool IsRetriableResponse(HttpMessage message) => true;
        public override bool IsRetriableException(Exception exception) => exception is IOException;
    }

    private sealed class StatusResponse(int status) : Response
    {
        public override int Status => status;
        public override string ReasonPhrase => "";
        public override Stream? ContentStream { get; set; }
        public override string ClientRequestId { get; set; } = "";
        public override void Dispose() => ContentStream?.Dispose();
        protected override bool ContainsHeader(string name) => false;
        protected override IEnumerable<HttpHeader> EnumerateHeaders() => [];
        protected override bool TryGetHeader(string name, out string value) { value = ""; return false; }
        protected override bool TryGetHeaderValues(string name, out IEnumerable<string> values) { values = []; return false; }
    }
}
