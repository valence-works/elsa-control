using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ElsaControl.Api.Admin.Staging;
using ElsaControl.Api.Authentication;
using ElsaControl.Api.OrganizationBilling;
using ElsaControl.Billing.Stripe;
using ElsaControl.Deployment.Azure;
using ElsaControl.PackageCatalog.Core.Accounts;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
    public async Task Enabled_lever_with_a_live_stripe_key_refuses_to_start()
    {
        await using var app = CreateApp(
            enabled: true,
            allowlisted: AllowlistedInstanceId,
            stripeSecretKey: LiveSecretKey);

        var exception = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            _ = app.Services;
            await Task.CompletedTask;
        });

        Assert.Contains("test-mode", FlattenException(exception), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sk_live", FlattenException(exception), StringComparison.Ordinal);
    }

    [Fact]
    public void Audit_write_records_outcome_mode_and_fingerprinted_operator()
    {
        var logs = new ConcurrentBag<string>();
        var logger = new CapturingLogger(logs);

        StagingProvisioningHealthLeverAudit.Write(
            logger,
            StagingProvisioningHealthLeverOutcome.Armed,
            StagingProvisioningHealthFailureMode.CrashLoop,
            "api-key");
        StagingProvisioningHealthLeverAudit.Write(
            logger,
            StagingProvisioningHealthLeverOutcome.Reset,
            StagingProvisioningHealthFailureMode.None,
            "api-key");

        Assert.Contains(logs, message =>
            message.Contains(StagingProvisioningHealthLeverDefaults.CrashLoopCode, StringComparison.Ordinal) &&
            message.Contains(StagingProvisioningHealthLeverDefaults.CrashLoopMode, StringComparison.Ordinal) &&
            message.Contains(
                OrganizationInternalEntitlementPolicy.FingerprintOperatorSubject("api-key")!,
                StringComparison.Ordinal));
        Assert.Contains(logs, message =>
            message.Contains(StagingProvisioningHealthLeverDefaults.ResetCode, StringComparison.Ordinal));
        Assert.All(logs, message =>
        {
            Assert.DoesNotContain(AllowlistedInstanceId.ToString("D"), message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("api-key", message, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Arm_and_reset_write_an_audit_log_without_instance_ids()
    {
        var logs = new ConcurrentBag<string>();
        await using var app = CreateApp(
            enabled: true,
            allowlisted: AllowlistedInstanceId,
            configureServices: services => services.AddSingleton<ILoggerProvider>(new CapturingLoggerProvider(logs)));
        var client = Operator(app);

        (await PostCrashLoopAsync(client, AllowlistedInstanceId)).EnsureSuccessStatusCode();
        (await PostResetAsync(client, AllowlistedInstanceId)).EnsureSuccessStatusCode();

        var entries = logs.Where(message =>
                message.Contains("staging provisioning health lever", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.Contains(entries, message =>
            message.Contains(StagingProvisioningHealthLeverDefaults.CrashLoopCode, StringComparison.Ordinal) &&
            message.Contains(StagingProvisioningHealthLeverDefaults.CrashLoopMode, StringComparison.Ordinal) &&
            message.Contains(
                OrganizationInternalEntitlementPolicy.FingerprintOperatorSubject("api-key")!,
                StringComparison.Ordinal));
        Assert.Contains(entries, message =>
            message.Contains(StagingProvisioningHealthLeverDefaults.ResetCode, StringComparison.Ordinal) &&
            message.Contains("none", StringComparison.Ordinal));
        Assert.All(entries, message =>
        {
            Assert.DoesNotContain(AllowlistedInstanceId.ToString("D"), message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("api-key", message, StringComparison.Ordinal);
        });
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
        IReadOnlyDictionary<string, string?>? additionalConfiguration = null,
        Action<IServiceCollection>? configureServices = null)
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

        return new ControlApiTestApplication(configuration, configureServices);
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

    private static string FlattenException(Exception exception)
    {
        var parts = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException)
            parts.Add(current.Message);
        return string.Join(" ", parts);
    }

    private sealed class CapturingLoggerProvider(ConcurrentBag<string> messages) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(messages);

        public void Dispose()
        {
        }
    }

    private sealed class CapturingLogger(ConcurrentBag<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            messages.Add(formatter(state, exception));
    }
}
