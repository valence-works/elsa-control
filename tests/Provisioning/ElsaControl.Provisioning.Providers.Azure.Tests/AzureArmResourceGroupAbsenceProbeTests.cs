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
