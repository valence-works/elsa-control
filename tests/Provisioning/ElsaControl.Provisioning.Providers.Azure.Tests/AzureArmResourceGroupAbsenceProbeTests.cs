using System.Net;

namespace ElsaControl.Provisioning.Providers.Azure.Tests;

public sealed class AzureArmResourceGroupAbsenceProbeTests
{
    [Fact]
    public void Status_mapping_is_value_free()
    {
        Assert.Equal(ResourceGroupPresence.Absent, AzureArmResourceGroupAbsenceProbe.MapStatus(HttpStatusCode.NotFound));
        Assert.Equal(ResourceGroupPresence.Present, AzureArmResourceGroupAbsenceProbe.MapStatus(HttpStatusCode.OK));
        Assert.Equal(ResourceGroupPresence.Unknown, AzureArmResourceGroupAbsenceProbe.MapStatus(HttpStatusCode.Forbidden));
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
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                RequestMessage = request,
                Content = new StringContent("{}")
            });
        }
    }
}
