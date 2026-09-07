using System.Net;
using Azure.Core;
using Azure.Core.Pipeline;
using Xunit;
using ElsaControl.Api.Catalog;

namespace ElsaControl.Api.Tests;

public sealed class CatalogManagedIdentityRetryPipelineTests
{
    [Theory]
    [InlineData("http://169.254.169.254/metadata/instance/compute?api-version=2021-02-01")]
    [InlineData("http://169.254.169.254/metadata/instance/compute/location?api-version=2020-06-01&format=text")]
    public async Task Actual_pipeline_does_not_retry_unsupported_instance_metadata_probe(string uri)
    {
        using var handler = new StatusHandler(404);
        using var client = new HttpClient(handler);
        var delay = new RecordingDelay();
        var policy = new ManagedIdentityProbeRetryPolicy(delay);
        var pipeline = new HttpPipeline(new HttpClientTransport(client), [policy], new ResponseClassifier());
        using var message = pipeline.CreateMessage();
        message.Request.Uri.Reset(new Uri(uri));
        message.Request.Headers.Add("Metadata", "true");

        await pipeline.SendAsync(message, CancellationToken.None);

        Assert.Equal(1, handler.Attempts);
        Assert.Equal(0, delay.Attempts);
    }

    [Theory]
    [InlineData(404, 6)]
    [InlineData(410, 6)]
    [InlineData(429, 6)]
    [InlineData(502, 1)]
    [InlineData(503, 6)]
    public async Task Actual_pipeline_keeps_bounded_token_retries(int status, int attempts)
    {
        using var handler = new StatusHandler(status);
        using var client = new HttpClient(handler);
        var delay = new RecordingDelay();
        var policy = new ManagedIdentityProbeRetryPolicy(delay);
        var pipeline = new HttpPipeline(new HttpClientTransport(client), [policy], new ResponseClassifier());
        using var message = pipeline.CreateMessage();
        message.Request.Uri.Reset(new Uri("http://169.254.169.254/metadata/identity/oauth2/token"));
        message.Request.Headers.Add("Metadata", "true");

        await pipeline.SendAsync(message, CancellationToken.None);

        Assert.Equal(attempts, handler.Attempts);
        Assert.Equal(attempts - 1, delay.Attempts);
    }

    [Fact]
    public async Task Cancelled_pipeline_does_not_make_a_request()
    {
        using var handler = new StatusHandler(503);
        using var client = new HttpClient(handler);
        var pipeline = new HttpPipeline(new HttpClientTransport(client), [new ManagedIdentityProbeRetryPolicy()], new ResponseClassifier());
        using var message = pipeline.CreateMessage();
        message.Request.Uri.Reset(new Uri("http://169.254.169.254/metadata/identity/oauth2/token"));
        message.Request.Headers.Add("Metadata", "true");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pipeline.SendAsync(message, cancellation.Token).AsTask());
        Assert.Equal(0, handler.Attempts);
    }

    [Theory]
    [InlineData(410)]
    [InlineData(503)]
    public async Task Cancellation_after_first_response_interrupts_retry_wait(int status)
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new StatusHandler(status, responseSent: () => cancellation.CancelAfter(TimeSpan.FromMilliseconds(100)));
        using var client = new HttpClient(handler);
        var pipeline = new HttpPipeline(new HttpClientTransport(client), [new ManagedIdentityProbeRetryPolicy()], new ResponseClassifier());
        using var message = pipeline.CreateMessage();
        message.Request.Uri.Reset(new Uri("http://169.254.169.254/metadata/identity/oauth2/token"));
        message.Request.Headers.Add("Metadata", "true");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            pipeline.SendAsync(message, cancellation.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, handler.Attempts);
    }

    [Fact]
    public async Task Delay_preserves_server_retry_after_even_above_normal_cap()
    {
        using var handler = new StatusHandler(410, retryAfter: true);
        using var client = new HttpClient(handler);
        var pipeline = new HttpPipeline(new HttpClientTransport(client), [], new ResponseClassifier());
        using var message = pipeline.CreateMessage();
        message.Request.Uri.Reset(new Uri("http://169.254.169.254/metadata/identity/oauth2/token"));
        await pipeline.SendAsync(message, CancellationToken.None);
        Assert.Equal(TimeSpan.FromSeconds(120), new ManagedIdentityRetryDelay().GetNextDelay(message.Response, 1));
    }

    private sealed class StatusHandler(int status, bool retryAfter = false, Action? responseSent = null) : HttpMessageHandler
    {
        public int Attempts { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Attempts++;
            var response = new HttpResponseMessage((HttpStatusCode)status);
            if (retryAfter)
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
            responseSent?.Invoke();
            return Task.FromResult(response);
        }
    }
}
