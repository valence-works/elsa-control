using System.Net;

namespace ElsaControl.Provisioning.Providers.Azure.Tests;

public sealed class AzureArmResourceGroupAbsenceProbeTests
{
    [Fact]
    public void Status_mapping_is_value_free()
    {
        Assert.Equal(
            ResourceGroupPresence.Absent,
            AzureArmResourceGroupAbsenceProbe.MapResponse(
                HttpStatusCode.NotFound,
                """{"error":{"code":"ResourceGroupNotFound"}}"""));
        Assert.Equal(ResourceGroupPresence.Present, AzureArmResourceGroupAbsenceProbe.MapResponse(HttpStatusCode.OK, "{}"));
        Assert.Equal(ResourceGroupPresence.Unknown, AzureArmResourceGroupAbsenceProbe.MapResponse(HttpStatusCode.Forbidden, "{}"));
        Assert.Equal(
            ResourceGroupPresence.Unknown,
            AzureArmResourceGroupAbsenceProbe.MapResponse(HttpStatusCode.NotFound, """{"error":{"code":"SubscriptionNotFound"}}"""));
        Assert.Equal(
            ResourceGroupPresence.Unknown,
            AzureArmResourceGroupAbsenceProbe.MapResponse(HttpStatusCode.NotFound, "{}"));
    }

    [Fact]
    public async Task Missing_subscription_is_unknown()
    {
        using var probe = new AzureArmResourceGroupAbsenceProbe(
            new StubHandler(HttpStatusCode.OK),
            () => null,
            static _ => Task.FromResult("token"));
        Assert.Equal(
            ResourceGroupPresence.Unknown,
            await probe.ObserveAsync("elsa-engine-aaaaaaaabbbbccccddddeeeeeeeeeeee"));
    }

    [Fact]
    public async Task Not_found_is_absent()
    {
        using var probe = new AzureArmResourceGroupAbsenceProbe(
            new StubHandler(HttpStatusCode.NotFound),
            () => Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee").ToString("D"),
            static _ => Task.FromResult("token"));
        Assert.Equal(
            ResourceGroupPresence.Absent,
            await probe.ObserveAsync("elsa-engine-aaaaaaaabbbbccccddddeeeeeeeeeeee"));
    }

    [Fact]
    public async Task Forbidden_read_is_unknown_not_absent()
    {
        using var probe = new AzureArmResourceGroupAbsenceProbe(
            new StubHandler(HttpStatusCode.Forbidden),
            () => Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee").ToString("D"),
            static _ => Task.FromResult("token"));
        Assert.Equal(
            ResourceGroupPresence.Unknown,
            await probe.ObserveAsync("elsa-engine-aaaaaaaabbbbccccddddeeeeeeeeeeee"));
    }

    [Fact]
    public void Production_probe_uses_the_provisioner_identity_when_configured()
    {
        Assert.IsType<global::Azure.Identity.ManagedIdentityCredential>(
            AzureArmResourceGroupAbsenceProbe.CreateCredential("d57a6279-2806-4289-9442-d862b53451a1"));
        Assert.IsType<global::Azure.Identity.DefaultAzureCredential>(
            AzureArmResourceGroupAbsenceProbe.CreateCredential(null));
    }

    [Theory]
    [InlineData("""{"error":{"code":"AuthorizationFailed","message":"secret-ish detail"}}""", "AuthorizationFailed")]
    [InlineData("""{"error":{"code":"ResourceGroupNotFound"}}""", "ResourceGroupNotFound")]
    [InlineData("{}", null)]
    [InlineData("not json", null)]
    [InlineData("""{"error":{"code":"has spaces"}}""", null)]
    public void Error_code_is_read_without_the_message(string body, string? expected) =>
        Assert.Equal(expected, AzureArmResourceGroupAbsenceProbe.ReadErrorCode(body));

    private sealed class StubHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            var body = statusCode == HttpStatusCode.NotFound
                ? """{"error":{"code":"ResourceGroupNotFound"}}"""
                : "{}";
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                RequestMessage = request,
                Content = new StringContent(body)
            });
        }
    }
}
