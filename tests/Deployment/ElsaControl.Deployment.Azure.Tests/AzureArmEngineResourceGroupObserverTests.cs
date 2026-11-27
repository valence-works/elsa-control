using System.Net;
using Azure.Core;

namespace ElsaControl.Deployment.Azure.Tests;

public sealed class AzureArmEngineResourceGroupObserverTests
{
    [Fact]
    public void Status_mapping_is_value_free()
    {
        Assert.Equal(
            AzureProviderFreshResourceGroupState.Absent,
            AzureArmResourceGroupPresence.MapResponse(
                HttpStatusCode.NotFound,
                """{"error":{"code":"ResourceGroupNotFound"}}"""));
        Assert.Equal(
            AzureProviderFreshResourceGroupState.Present,
            AzureArmResourceGroupPresence.MapResponse(HttpStatusCode.OK, "{}"));
        Assert.Equal(
            AzureProviderFreshResourceGroupState.Unknown,
            AzureArmResourceGroupPresence.MapResponse(HttpStatusCode.Forbidden, "{}"));
        Assert.Equal(
            AzureProviderFreshResourceGroupState.Unknown,
            AzureArmResourceGroupPresence.MapResponse(
                HttpStatusCode.NotFound,
                """{"error":{"code":"SubscriptionNotFound"}}"""));
        Assert.Equal(
            AzureProviderFreshResourceGroupState.Unknown,
            AzureArmResourceGroupPresence.MapResponse(HttpStatusCode.NotFound, "{}"));
        Assert.Equal(
            AzureProviderFreshResourceGroupState.Unknown,
            AzureArmResourceGroupPresence.MapResponse(HttpStatusCode.InternalServerError, "{}"));
    }

    [Fact]
    public async Task Not_found_resource_group_is_absent()
    {
        var handler = new StubHandler(HttpStatusCode.NotFound, """{"error":{"code":"ResourceGroupNotFound"}}""");
        using var observer = new AzureArmEngineResourceGroupObserver(new StubCredential(), handler);

        var state = await observer.ObserveAsync(
            "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
            "rg-elsa-cloud-workloads-platform-prod-weu-c0139c55c0139c55c0139c55");

        Assert.Equal(AzureProviderFreshResourceGroupState.Absent, state);
        Assert.Equal(HttpMethod.Get, handler.Method);
        Assert.Equal(
            "https://management.azure.com/subscriptions/aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee/resourcegroups/rg-elsa-cloud-workloads-platform-prod-weu-c0139c55c0139c55c0139c55?api-version=2021-04-01",
            handler.Uri);
        Assert.Equal("Bearer", handler.AuthorizationScheme);
        Assert.DoesNotContain("keyvault", handler.Uri, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sqlServers", handler.Uri, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sites", handler.Uri, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Present_or_ambiguous_observations_are_not_absence()
    {
        using var present = new AzureArmEngineResourceGroupObserver(
            new StubCredential(), new StubHandler(HttpStatusCode.OK, "{}"));
        using var forbidden = new AzureArmEngineResourceGroupObserver(
            new StubCredential(), new StubHandler(HttpStatusCode.Forbidden, "{}"));
        using var subscriptionMissing = new AzureArmEngineResourceGroupObserver(
            new StubCredential(),
            new StubHandler(HttpStatusCode.NotFound, """{"error":{"code":"SubscriptionNotFound"}}"""));

        Assert.Equal(
            AzureProviderFreshResourceGroupState.Present,
            await present.ObserveAsync("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", "rg-engine"));
        Assert.Equal(
            AzureProviderFreshResourceGroupState.Unknown,
            await forbidden.ObserveAsync("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", "rg-engine"));
        Assert.Equal(
            AzureProviderFreshResourceGroupState.Unknown,
            await subscriptionMissing.ObserveAsync("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", "rg-engine"));
    }

    [Fact]
    public async Task Invalid_binding_or_transport_failure_is_unknown()
    {
        using var observer = new AzureArmEngineResourceGroupObserver(
            new StubCredential(), new ThrowingHandler());

        Assert.Equal(
            AzureProviderFreshResourceGroupState.Unknown,
            await observer.ObserveAsync("not-a-guid", "rg-engine"));
        Assert.Equal(
            AzureProviderFreshResourceGroupState.Unknown,
            await observer.ObserveAsync("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", ""));
        Assert.Equal(
            AzureProviderFreshResourceGroupState.Unknown,
            await observer.ObserveAsync("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", "rg-engine"));
    }

    private sealed class StubCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("token", DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken) =>
            new(GetToken(requestContext, cancellationToken));
    }

    private sealed class StubHandler(HttpStatusCode statusCode, string body) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public string? Uri { get; private set; }
        public string? AuthorizationScheme { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Method = request.Method;
            Uri = request.RequestUri?.ToString();
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                RequestMessage = request,
                Content = new StringContent(body)
            });
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new HttpRequestException("transport failed");
    }
}
