using System.Net;
using System.Text;
using ElsaControl.Deployment.Core.Cockpit;
using ElsaControl.Deployment.Core.Workspace;
using Xunit;

namespace ElsaControl.Deployment.Core.Tests;

public sealed class EngineEndpointHttpClientPolicyTests
{
    [Theory]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://127.1/")]
    [InlineData("http://2130706433/")]
    [InlineData("http://0177.0.0.1/")]
    [InlineData("http://0x7f000001/")]
    [InlineData("http://0x7f.0.0.1/")]
    [InlineData("https://[::1]/")]
    [InlineData("https://localhost/")]
    [InlineData("https://localhost./")]
    [InlineData("https://metadata.google.internal/")]
    [InlineData("https://user:secret@example.com/")]
    [InlineData("https://example.com/?token=secret")]
    [InlineData("https://example.com/#fragment")]
    [InlineData("file:///etc/passwd")]
    public void Validate_endpoint_rejects_unsafe_addresses_without_echoing_values(string value)
    {
        Assert.False(EngineEndpointHttpClientPolicy.TryValidateEndpoint(new Uri(value), out var message));
        Assert.False(string.IsNullOrWhiteSpace(message));
        Assert.DoesNotContain("secret", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("example.com", message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("https://8.8.8.8/", true)]
    [InlineData("https://[2606:4700:4700::1111]/", true)]
    [InlineData("http://engine.example.test:5000/elsa", true)]
    public void Validate_endpoint_allows_public_http_and_https_targets(string value, bool expected)
    {
        Assert.Equal(expected, EngineEndpointHttpClientPolicy.TryValidateEndpoint(new Uri(value), out _));
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("10.1.2.3")]
    [InlineData("100.64.0.1")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("172.16.0.1")]
    [InlineData("192.168.1.1")]
    [InlineData("198.18.0.1")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("::")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("fc00::1")]
    [InlineData("fe80::1")]
    [InlineData("ff02::1")]
    [InlineData("2001:db8::1")]
    [InlineData("3fff::1")]
    public void Globally_routable_check_rejects_prohibited_ip_ranges(string value)
    {
        Assert.False(EngineEndpointHttpClientPolicy.IsGloballyRoutable(IPAddress.Parse(value)));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("2606:4700:4700::1111")]
    public void Globally_routable_check_accepts_public_ip_ranges(string value)
    {
        Assert.True(EngineEndpointHttpClientPolicy.IsGloballyRoutable(IPAddress.Parse(value)));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.20.30.40")]
    [InlineData("169.254.169.254")]
    [InlineData("fd00::1")]
    [InlineData("fe80::1")]
    public async Task Connect_callback_rejects_private_or_metadata_dns_results_before_connecting(string address)
    {
        var connectorCalled = false;
        var policy = new EngineEndpointHttpClientPolicy(
            (_, _) => Task.FromResult(new[] { IPAddress.Parse(address) }),
            (_, _, _) =>
            {
                connectorCalled = true;
                return ValueTask.FromResult<Stream>(new MemoryStream());
            });

        var exception = await Assert.ThrowsAnyAsync<HttpRequestException>(() =>
            policy.ConnectEndpointAsync(new DnsEndPoint("public-looking.example", 443), CancellationToken.None).AsTask());

        Assert.DoesNotContain(address, exception.Message, StringComparison.Ordinal);
        Assert.False(connectorCalled);
    }

    [Fact]
    public async Task Connect_callback_resolves_at_connect_time_and_pins_the_checked_address()
    {
        var resolution = 0;
        var connectedAddresses = new List<IPAddress>();
        var policy = new EngineEndpointHttpClientPolicy(
            (_, _) => Task.FromResult(new[]
            {
                IPAddress.Parse(Interlocked.Increment(ref resolution) == 1 ? "8.8.8.8" : "127.0.0.1")
            }),
            (address, _, _) =>
            {
                connectedAddresses.Add(address);
                return ValueTask.FromResult<Stream>(new MemoryStream());
            });

        using var firstConnection = await policy.ConnectEndpointAsync(new DnsEndPoint("rebound.example", 443), CancellationToken.None);
        await Assert.ThrowsAnyAsync<HttpRequestException>(() =>
            policy.ConnectEndpointAsync(new DnsEndPoint("rebound.example", 443), CancellationToken.None).AsTask());

        Assert.Equal(IPAddress.Parse("8.8.8.8"), Assert.Single(connectedAddresses));
        Assert.Equal(2, resolution);
    }

    [Fact]
    public async Task Mixed_public_and_private_dns_answers_are_rejected_as_a_set()
    {
        var connectorCalled = false;
        var policy = new EngineEndpointHttpClientPolicy(
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8"), IPAddress.Parse("10.0.0.8") }),
            (_, _, _) =>
            {
                connectorCalled = true;
                return ValueTask.FromResult<Stream>(new MemoryStream());
            });
        await Assert.ThrowsAnyAsync<HttpRequestException>(() =>
            policy.ConnectEndpointAsync(new DnsEndPoint("mixed-answer.example", 443), CancellationToken.None).AsTask());

        Assert.False(connectorCalled);
    }

    [Fact]
    public async Task Probe_projects_dns_rejection_without_exposing_the_resolved_address()
    {
        var policy = new EngineEndpointHttpClientPolicy(
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("169.254.169.254") }),
            (_, _, _) => throw new InvalidOperationException("The connector must not be called."));
        using var handler = policy.CreateHandler();
        using var httpClient = new HttpClient(handler);

        var result = await new HttpEngineHealthProbe(httpClient).ProbeAsync(Engine("http://public-looking.example/health"));

        Assert.False(result.Reachable);
        Assert.Equal(CertificateStatus.Untrusted, result.CertificateStatus);
        Assert.Equal(CredentialVerificationStatus.Unverified, result.CredentialVerificationStatus);
        Assert.Equal("Endpoint address is not publicly routable.", result.Message);
        Assert.DoesNotContain("169.254.169.254", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Redirect_is_reported_and_never_connected_to_the_redirect_target()
    {
        var resolutionCount = 0;
        var connectionCount = 0;
        var policy = new EngineEndpointHttpClientPolicy(
            (_, _) =>
            {
                resolutionCount++;
                return Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") });
            },
            (_, _, _) =>
            {
                connectionCount++;
                return ValueTask.FromResult<Stream>(new StaticHttpResponseStream(
                    "HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1/private\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
            });
        using var handler = policy.CreateHandler();
        using var httpClient = new HttpClient(handler);

        var result = await new HttpEngineHealthProbe(httpClient).ProbeAsync(Engine("http://public.example.test/health"));

        Assert.True(result.Reachable);
        Assert.Contains("redirects are disabled", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("HTTP 302", result.Message, StringComparison.Ordinal);
        Assert.Equal(1, resolutionCount);
        Assert.Equal(1, connectionCount);
    }

    [Fact]
    public void Handler_disables_redirects_and_environment_proxies()
    {
        using var handler = new EngineEndpointHttpClientPolicy().CreateHandler();

        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseProxy);
        Assert.False(handler.UseCookies);
        Assert.Equal(TimeSpan.FromSeconds(3), handler.ConnectTimeout);
        Assert.Equal(16, handler.MaxResponseHeadersLength);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, "HTTP 200", CertificateStatus.Trusted)]
    [InlineData(HttpStatusCode.Unauthorized, "requires authentication (HTTP 401)", CertificateStatus.Trusted)]
    [InlineData(HttpStatusCode.Forbidden, "requires authentication (HTTP 403)", CertificateStatus.Trusted)]
    [InlineData(HttpStatusCode.NotFound, "path was not found (HTTP 404)", CertificateStatus.Trusted)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "server error (HTTP 503)", CertificateStatus.Trusted)]
    [InlineData(HttpStatusCode.Redirect, "redirect (HTTP 302)", CertificateStatus.Trusted)]
    public async Task Http_response_status_is_distinct_but_never_verifies_unsent_credentials(
        HttpStatusCode statusCode,
        string expectedMessage,
        CertificateStatus expectedCertificateStatus)
    {
        var responseHandler = new StaticResponseHandler(_ => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent("secret response body")
        });
        using var httpClient = new HttpClient(responseHandler);
        var result = await new HttpEngineHealthProbe(httpClient).ProbeAsync(Engine());

        Assert.True(result.Reachable);
        Assert.Equal(expectedCertificateStatus, result.CertificateStatus);
        Assert.Equal(CredentialVerificationStatus.Unverified, result.CredentialVerificationStatus);
        Assert.Contains(expectedMessage, result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret response body", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Plain_http_response_is_reachable_but_tls_is_untrusted_and_credentials_unverified()
    {
        using var httpClient = new HttpClient(new StaticResponseHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        var result = await new HttpEngineHealthProbe(httpClient).ProbeAsync(Engine("http://engine.example.test:5000/elsa"));

        Assert.True(result.Reachable);
        Assert.Equal(CertificateStatus.Untrusted, result.CertificateStatus);
        Assert.Equal(CredentialVerificationStatus.Unverified, result.CredentialVerificationStatus);
    }

    [Fact]
    public async Task Untrusted_or_oversized_version_headers_do_not_replace_the_stored_version()
    {
        using var httpClient = new HttpClient(new StaticResponseHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Headers.TryAddWithoutValidation("X-Elsa-Version", $"4.1.0 secret/{new string('x', 128)}");
            return response;
        }));

        var result = await new HttpEngineHealthProbe(httpClient).ProbeAsync(Engine());

        Assert.Equal("Elsa 4.1.0", result.Version);
    }

    [Fact]
    public async Task Tls_failure_is_reported_separately_without_leaking_exception_details()
    {
        using var httpClient = new HttpClient(new StaticResponseHandler(_ =>
            throw new HttpRequestException("sensitive remote details", new System.Security.Authentication.AuthenticationException("certificate secret"))));

        var result = await new HttpEngineHealthProbe(httpClient).ProbeAsync(Engine());

        Assert.True(result.Reachable);
        Assert.Equal(CertificateStatus.Untrusted, result.CertificateStatus);
        Assert.Equal(CredentialVerificationStatus.Unverified, result.CredentialVerificationStatus);
        Assert.Contains("TLS certificate validation failed", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Timeout_is_reported_without_leaking_exception_details()
    {
        using var httpClient = new HttpClient(new StaticResponseHandler(_ => throw new TaskCanceledException("secret timeout detail")));

        var result = await new HttpEngineHealthProbe(httpClient).ProbeAsync(Engine());

        Assert.False(result.Reachable);
        Assert.Equal(CredentialVerificationStatus.Unverified, result.CredentialVerificationStatus);
        Assert.Contains("timed out", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Caller_cancellation_is_rethrown_instead_of_projected_as_a_timeout()
    {
        using var cancellation = new CancellationTokenSource();
        using var httpClient = new HttpClient(new StaticResponseHandler(_ => throw new OperationCanceledException()));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new HttpEngineHealthProbe(httpClient).ProbeAsync(Engine(), cancellation.Token));
    }

    [Fact]
    public async Task Invalid_target_is_rejected_before_http_send_and_diagnostic_does_not_echo_target()
    {
        var responseHandler = new StaticResponseHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var httpClient = new HttpClient(responseHandler);
        var result = await new HttpEngineHealthProbe(httpClient).ProbeAsync(Engine("https://user:secret@example.test/path?token=secret"));

        Assert.False(result.Reachable);
        Assert.Equal(CredentialVerificationStatus.Unverified, result.CredentialVerificationStatus);
        Assert.False(responseHandler.WasCalled);
        Assert.DoesNotContain("secret", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("example.test", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static WorkspaceWorkflowEngine Engine(string? baseUrl = null) => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        Guid.NewGuid(),
        "Test engine",
        baseUrl ?? "https://engine.example.test/elsa",
        "weu",
        "Elsa 4.1.0",
        CertificateStatus.Trusted,
        "Credential store",
        "secret-reference",
        CredentialVerificationStatus.Unverified,
        null,
        DeploymentHealth.Unreachable,
        null,
        null,
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow);

    private sealed class StaticResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public bool WasCalled { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            WasCalled = true;
            return Task.FromResult(responseFactory(request));
        }
    }

    private sealed class StaticHttpResponseStream(string response) : Stream
    {
        private readonly MemoryStream _input = new(Encoding.ASCII.GetBytes(response));
        private readonly MemoryStream _output = new();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => _output.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _input.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _input.ReadAsync(buffer, cancellationToken);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => _input.ReadAsync(buffer, offset, count, cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) => _output.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => _output.WriteAsync(buffer, cancellationToken);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => _output.WriteAsync(buffer, offset, count, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _input.Dispose();
                _output.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
