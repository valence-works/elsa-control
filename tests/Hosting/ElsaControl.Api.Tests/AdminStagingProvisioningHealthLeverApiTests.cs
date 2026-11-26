using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ElsaControl.Api.Admin.Staging;
using ElsaControl.Api.Authentication;
using ElsaControl.Api.OrganizationBilling;
using ElsaControl.Billing.Stripe;
using ElsaControl.Deployment.Azure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace ElsaControl.Api.Tests;

public sealed class AdminStagingProvisioningHealthLeverApiTests
{
    private static readonly Guid AllowlistedInstanceId = Guid.Parse("50000000-0000-0000-0000-000000000055");
    private static readonly Guid OtherInstanceId = Guid.Parse("50000000-0000-0000-0000-000000000066");
    private static readonly Guid SmokeOwnerInstanceId = Guid.Parse("50000000-0000-0000-0000-000000000099");
    private const string BffClientId = "elsa-cloud-bff";
    private const string BffScope = CloudBffDefaults.DefaultScope;
    private const string TestSecretKey = "sk_test_harness";
    private const string LiveSecretKey = "sk_live_harness";

    [Fact]
    public async Task Flag_off_refuses_even_for_an_allowlisted_instance()
    {
        await using var app = CreateApp(enabled: false, allowlisted: AllowlistedInstanceId);

        var response = await PostCrashLoopAsync(Operator(app), AllowlistedInstanceId);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(StagingProvisioningHealthLeverDefaults.DisabledCode, await ProblemCodeAsync(response));
        Assert.False(app.Services.GetRequiredService<StagingProvisioningHealthLever>()
            .TryGetArmedFailure(AllowlistedInstanceId, out _));
    }

    [Fact]
    public async Task Billing_and_recovery_flags_do_not_arm_provisioning_health()
    {
        await using var app = CreateApp(
            enabled: false,
            allowlisted: AllowlistedInstanceId,
            additionalConfiguration: new Dictionary<string, string?>
            {
                [$"{StagingBillingLifecycleLeverOptions.ConfigurationSection}:Enabled"] = "true",
                [$"{StagingRecoveryLifecycleLeverOptions.ConfigurationSection}:Enabled"] = "true",
                [$"{StagingLateSuccessLeverOptions.ConfigurationSection}:Enabled"] = "true"
            });

        var response = await PostCrashLoopAsync(Operator(app), AllowlistedInstanceId);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(StagingProvisioningHealthLeverDefaults.DisabledCode, await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task Allowlisted_instance_with_a_stripe_test_key_arms_crash_loop()
    {
        await using var app = CreateApp(enabled: true, allowlisted: AllowlistedInstanceId);

        var response = await PostCrashLoopAsync(Operator(app), AllowlistedInstanceId);
        if (response.StatusCode != HttpStatusCode.OK)
            Assert.Fail($"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        var body = await response.Content.ReadControlJsonAsync<StagingProvisioningHealthLeverResponse>();
        Assert.NotNull(body);
        Assert.Equal(AllowlistedInstanceId, body.InstanceId);
        Assert.Equal(StagingProvisioningHealthLeverDefaults.CrashLoopMode, body.Mode);
        Assert.Equal(StagingProvisioningHealthLeverDefaults.CrashLoopCode, body.Code);
        Assert.True(app.Services.GetRequiredService<StagingProvisioningHealthLever>()
            .TryGetArmedFailure(AllowlistedInstanceId, out var mode));
        Assert.Equal(StagingProvisioningHealthFailureMode.CrashLoop, mode);
    }

    [Fact]
    public async Task Allowlisted_instance_with_a_stripe_test_key_arms_health_timeout()
    {
        await using var app = CreateApp(enabled: true, allowlisted: AllowlistedInstanceId);

        var response = await PostTimeoutAsync(Operator(app), AllowlistedInstanceId);
        if (response.StatusCode != HttpStatusCode.OK)
            Assert.Fail($"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        var body = await response.Content.ReadControlJsonAsync<StagingProvisioningHealthLeverResponse>();
        Assert.NotNull(body);
        Assert.Equal(StagingProvisioningHealthLeverDefaults.TimeoutMode, body.Mode);
        Assert.Equal(StagingProvisioningHealthLeverDefaults.TimeoutCode, body.Code);
        Assert.True(app.Services.GetRequiredService<StagingProvisioningHealthLever>()
            .TryGetArmedFailure(AllowlistedInstanceId, out var mode));
        Assert.Equal(StagingProvisioningHealthFailureMode.Timeout, mode);
    }

    [Fact]
    public async Task Reset_clears_an_armed_inject()
    {
        await using var app = CreateApp(enabled: true, allowlisted: AllowlistedInstanceId);
        var client = Operator(app);
        (await PostCrashLoopAsync(client, AllowlistedInstanceId)).EnsureSuccessStatusCode();

        var response = await PostResetAsync(client, AllowlistedInstanceId);
        var body = await response.Content.ReadControlJsonAsync<StagingProvisioningHealthLeverResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(StagingProvisioningHealthLeverDefaults.ResetCode, body!.Code);
        Assert.Equal("none", body.Mode);
        Assert.False(app.Services.GetRequiredService<StagingProvisioningHealthLever>()
            .TryGetArmedFailure(AllowlistedInstanceId, out _));
    }

    [Fact]
    public async Task Live_stripe_key_leaves_the_gate_closed()
    {
        await using var app = CreateApp(
            enabled: true,
            allowlisted: AllowlistedInstanceId,
            stripeSecretKey: LiveSecretKey);

        var response = await PostCrashLoopAsync(Operator(app), AllowlistedInstanceId);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(StagingProvisioningHealthLeverDefaults.DisabledCode, await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task Instance_not_on_the_allowlist_is_refused()
    {
        await using var app = CreateApp(enabled: true, allowlisted: AllowlistedInstanceId);

        var response = await PostCrashLoopAsync(Operator(app), OtherInstanceId);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(StagingProvisioningHealthLeverDefaults.InstanceNotAllowedCode, await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task Smoke_owner_instance_is_refused_even_when_allowlisted()
    {
        await using var app = CreateApp(
            enabled: true,
            allowlisted: SmokeOwnerInstanceId,
            smokeOwnerInstanceId: SmokeOwnerInstanceId);

        var response = await PostCrashLoopAsync(Operator(app), SmokeOwnerInstanceId);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(StagingProvisioningHealthLeverDefaults.InstanceNotAllowedCode, await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task Cloud_bff_caller_is_denied_and_the_route_is_not_allowlisted()
    {
        await using var app = CreateApp(
            enabled: true,
            allowlisted: AllowlistedInstanceId,
            additionalConfiguration: new Dictionary<string, string?>
            {
                [$"{CloudBffOptions.ConfigurationSection}:Enabled"] = "true",
                [$"{CloudBffOptions.ConfigurationSection}:ClientId"] = BffClientId,
                [$"{CloudBffOptions.ConfigurationSection}:Scope"] = BffScope
            });
        var bff = app.CreateControlIdentityClient(
            subject: "bff-user",
            claims: new Dictionary<string, string> { ["azp"] = BffClientId, ["scp"] = BffScope });

        var response = await PostCrashLoopAsync(bff, AllowlistedInstanceId);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("cloud-bff.denied", await ProblemCodeAsync(response));

        var allowed = app.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.Metadata.GetMetadata<CloudBffAllowedEndpointMetadata>() is not null)
            .SelectMany(endpoint => endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods
                .Select(method => $"{method} {endpoint.RoutePattern.RawText}") ?? []);
        Assert.DoesNotContain(
            "POST /api/staging/provisioning-health-lever/{instanceId:guid}/crash-loop",
            allowed,
            StringComparer.Ordinal);
        Assert.DoesNotContain(
            "POST /api/staging/provisioning-health-lever/{instanceId:guid}/health-timeout",
            allowed,
            StringComparer.Ordinal);
        Assert.DoesNotContain(
            "POST /api/staging/provisioning-health-lever/{instanceId:guid}/reset",
            allowed,
            StringComparer.Ordinal);
    }

    [Fact]
    public async Task Anonymous_and_customer_callers_are_denied()
    {
        await using var app = CreateApp(enabled: true, allowlisted: AllowlistedInstanceId);

        var anonymous = await PostCrashLoopAsync(app.CreateClient(), AllowlistedInstanceId);
        var customer = app.CreateClient(new() { AllowAutoRedirect = false });
        app.AddControlSessionCookie(customer, subject: "customer-user", expiresUtc: DateTimeOffset.UtcNow.AddHours(2));
        customer.DefaultRequestHeaders.Add("Origin", "http://localhost");
        var customerResponse = await PostCrashLoopAsync(customer, AllowlistedInstanceId);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, customerResponse.StatusCode);
        Assert.False(app.Services.GetRequiredService<StagingProvisioningHealthLever>()
            .TryGetArmedFailure(AllowlistedInstanceId, out _));
    }

    private static ControlApiTestApplication CreateApp(
        bool enabled,
        Guid? allowlisted = null,
        Guid? smokeOwnerInstanceId = null,
        string? stripeSecretKey = TestSecretKey,
        IReadOnlyDictionary<string, string?>? additionalConfiguration = null)
    {
        var stripeMode = StripeBillingOptions.GetKeyMode(stripeSecretKey);
        var configuration = new Dictionary<string, string?>
        {
            [$"{StagingProvisioningHealthLeverOptions.ConfigurationSection}:Enabled"] = enabled ? "true" : "false",
            ["Billing:Stripe:Enabled"] = "true",
            [StripeBillingOptions.ExpectedModeConfigurationKey] = stripeMode ?? StripeBillingOptions.TestMode,
            ["Billing:Stripe:SecretKey"] = stripeSecretKey
        };
        if (allowlisted is { } instanceId)
            configuration[$"{StagingProvisioningHealthLeverOptions.ConfigurationSection}:AllowedInstanceIds:0"] = instanceId.ToString("D");
        if (smokeOwnerInstanceId is { } smoke)
            configuration[$"{StagingProvisioningHealthLeverOptions.ConfigurationSection}:SmokeOwnerInstanceId"] = smoke.ToString("D");
        if (additionalConfiguration is not null)
        {
            foreach (var (key, value) in additionalConfiguration)
                configuration[key] = value;
        }

        return new ControlApiTestApplication(configuration);
    }

    private static HttpClient Operator(ControlApiTestApplication app)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyAuthenticationDefaults.HeaderName, "local-dev-key");
        return client;
    }

    private static Task<HttpResponseMessage> PostCrashLoopAsync(HttpClient client, Guid instanceId) =>
        client.PostAsync($"/api/staging/provisioning-health-lever/{instanceId:D}/crash-loop", null);

    private static Task<HttpResponseMessage> PostTimeoutAsync(HttpClient client, Guid instanceId) =>
        client.PostAsync($"/api/staging/provisioning-health-lever/{instanceId:D}/health-timeout", null);

    private static Task<HttpResponseMessage> PostResetAsync(HttpClient client, Guid instanceId) =>
        client.PostAsync($"/api/staging/provisioning-health-lever/{instanceId:D}/reset", null);

    private static async Task<string?> ProblemCodeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString();
}
