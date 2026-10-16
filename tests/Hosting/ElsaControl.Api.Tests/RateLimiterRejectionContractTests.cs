using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ElsaControl.Api.Authentication;
using ElsaControl.Api.Cloud;

namespace ElsaControl.Api.Tests;

public sealed class RateLimiterRejectionContractTests
{
    [Fact]
    public async Task Handoff_middleware_rejection_keeps_the_existing_problem_contract()
    {
        await using var app = new ControlApiTestApplication(new Dictionary<string, string?>
        {
            [$"{ManagedElsaHandoffRateLimitOptions.ConfigurationSection}:PerIp:PermitLimit"] = "20",
            [$"{ManagedElsaHandoffRateLimitOptions.ConfigurationSection}:PerIp:Window"] = "00:01:00",
            [$"{ManagedElsaHandoffRateLimitOptions.ConfigurationSection}:BffPerIp:PermitLimit"] = "20",
            [$"{ManagedElsaHandoffRateLimitOptions.ConfigurationSection}:BffPerIp:Window"] = "00:01:00",
            [$"{ManagedElsaHandoffRateLimitOptions.ConfigurationSection}:PerIdentity:PermitLimit"] = "20",
            [$"{ManagedElsaHandoffRateLimitOptions.ConfigurationSection}:PerIdentity:Window"] = "00:01:00"
        });
        await app.SeedAsync(_ => Task.CompletedTask);
        using var client = app.CreateControlIdentityClient("handoff-rejection-contract");
        using var rejected = await RejectAsync(
            () => client.PostAsJsonAsync("/api/managed-elsa/handoff/issue", new { }),
            limit: 20);

        await AssertExistingPolicyRejection(
            rejected,
            ApiRateLimitRejection.HandoffExceededTitle,
            ApiRateLimitRejection.HandoffExceededCode);
    }

    [Fact]
    public async Task Heartbeat_middleware_rejection_keeps_the_existing_problem_contract()
    {
        await using var app = new ControlApiTestApplication();
        await app.SeedAsync(_ => Task.CompletedTask);
        using var client = app.CreateClient();
        var path = $"/api/runtime/external-engine-connections/{Guid.NewGuid():D}/heartbeat";
        using var rejected = await RejectAsync(
            () => client.PostAsJsonAsync(path, new { }),
            limit: 120);

        await AssertExistingPolicyRejection(
            rejected,
            ApiRateLimitRejection.HeartbeatExceededTitle,
            ApiRateLimitRejection.HeartbeatExceededCode);
    }

    private static async Task<HttpResponseMessage> RejectAsync(Func<Task<HttpResponseMessage>> send, int limit)
    {
        HttpResponseMessage? response = null;
        for (var attempt = 0; attempt < limit + 1; attempt++)
        {
            response?.Dispose();
            response = await send();
        }

        return response ?? throw new InvalidOperationException("Expected a rate-limit response.");
    }

    private static async Task AssertExistingPolicyRejection(HttpResponseMessage response, string title, string code)
    {
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Null(response.Headers.RetryAfter);
        Assert.Null(response.Headers.CacheControl);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var json = await response.Content.ReadAsStringAsync();
        using var body = JsonDocument.Parse(json);
        Assert.Equal(
            ["code", "correlationId", "status", "title", "traceId"],
            body.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(title, body.RootElement.GetProperty("title").GetString());
        Assert.Equal(code, body.RootElement.GetProperty("code").GetString());
        Assert.Equal(429, body.RootElement.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("correlationId").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("traceId").GetString()));
        Assert.DoesNotContain("rate-limit.exceeded", json, StringComparison.Ordinal);
    }
}
