using System.Globalization;
using System.Threading.RateLimiting;
using ElsaControl.Api.Authentication;
using ElsaControl.Api.Workspace;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace ElsaControl.Api.Cloud;

public static class CloudBootstrapRateLimitDefaults
{
    public const string PolicyName = "cloud-bootstrap";
    public const string ConfigurationSection = "RateLimiting:CloudBootstrap";
    public const string ExceededCode = "rate-limit.exceeded";
    public const string ExceededTitle = "Cloud bootstrap rate limit was exceeded.";
    public const int DefaultPerIdentityPermitLimit = 60;
    public const int DefaultPerIpPermitLimit = DefaultPerIdentityPermitLimit * 10;
    public const int UnresolvedIdentityPermitLimit = 1;
    public const int MinPermitLimit = 1;
    public const int MaxPermitLimit = 10_000;
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan MinWindow = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaxWindow = TimeSpan.FromHours(1);
}

public sealed class CloudBootstrapFixedWindowRateLimitOptions
{
    public int PermitLimit { get; init; } = CloudBootstrapRateLimitDefaults.DefaultPerIdentityPermitLimit;
    public TimeSpan Window { get; init; } = CloudBootstrapRateLimitDefaults.DefaultWindow;
}

public sealed class CloudBootstrapRateLimitOptions
{
    public const string ConfigurationSection = CloudBootstrapRateLimitDefaults.ConfigurationSection;

    /// <summary>
    /// Fixed window applied to every request, including validated Cloud/Hub BFF
    /// sessions and authenticated cookies. The default permit is ten times the
    /// per-identity default so shared BFF egress has aggregate headroom while
    /// per-identity remains the binding limit for signed-in callers.
    /// </summary>
    public CloudBootstrapFixedWindowRateLimitOptions PerIp { get; init; } = new()
    {
        PermitLimit = CloudBootstrapRateLimitDefaults.DefaultPerIpPermitLimit
    };

    /// <summary>
    /// Fixed window keyed on validated issuer plus subject. This is the main
    /// limit for signed-in callers. Authenticated sessions that cannot resolve
    /// both values use a separate low-permit fallback bucket partitioned by IP.
    /// Unauthenticated requests use the per-IP window only.
    /// </summary>
    public CloudBootstrapFixedWindowRateLimitOptions PerIdentity { get; init; } = new()
    {
        PermitLimit = CloudBootstrapRateLimitDefaults.DefaultPerIdentityPermitLimit
    };

    internal IEnumerable<string> Validate()
    {
        foreach (var error in ValidateWindow(PerIp, $"{ConfigurationSection}:PerIp"))
            yield return error;
        foreach (var error in ValidateWindow(PerIdentity, $"{ConfigurationSection}:PerIdentity"))
            yield return error;
    }

    private static IEnumerable<string> ValidateWindow(
        CloudBootstrapFixedWindowRateLimitOptions? window,
        string prefix)
    {
        var value = window ?? new CloudBootstrapFixedWindowRateLimitOptions { PermitLimit = 0, Window = TimeSpan.Zero };
        return ControlRateLimitWindowRules.Validate(value.PermitLimit, value.Window, prefix);
    }
}

public sealed class CloudBootstrapRateLimitConfigurationValidator(
    IOptions<CloudBootstrapRateLimitOptions> options) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var errors = options.Value.Validate().ToArray();
        if (errors.Length > 0)
            throw new InvalidOperationException(
                $"Cloud bootstrap rate limiting configuration is invalid: {string.Join(" ", errors)}");

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class CloudBootstrapRateLimiterPolicy : ControlIdentityAndIpRateLimiterPolicy
{
    public CloudBootstrapRateLimiterPolicy(
        IOptions<CloudBootstrapRateLimitOptions> options,
        IOptions<ControlIdentityOptions> controlIdentity,
        IOptions<CloudAccountIdentityOptions> cloudAccount,
        IOptions<HubAccountIdentityOptions> hubAccount)
        : base(
            CloudBootstrapRateLimitDefaults.PolicyName,
            new ControlRateLimitWindow(options.Value.PerIp.PermitLimit, options.Value.PerIp.Window),
            new ControlRateLimitWindow(options.Value.PerIdentity.PermitLimit, options.Value.PerIdentity.Window),
            controlIdentity.Value,
            cloudAccount.Value,
            hubAccount.Value,
            limitIdentityForNonBff: true)
    {
    }
}

internal static class CloudBootstrapChainedRateLimiter
{
    internal static string GetIpPartitionKey(HttpContext context) =>
        ControlIdentityAndIpRateLimiter.GetIpPartitionKey(context);

    internal static string UnresolvedIdentityKey(HttpContext context) =>
        ControlIdentityAndIpRateLimiter.UnresolvedIdentityKey(context);
}

internal static class ApiRateLimitRejection
{
    public const string ManagedElsaHandoffPolicyName = "managed-elsa-handoff";
    public const string ExternalEngineHeartbeatPolicyName = "external-engine-heartbeat";
    public const string HandoffExceededTitle = "Managed Elsa identity handoff rate limit was exceeded.";
    public const string HandoffExceededCode = "handoff.rate-limited";
    public const string HeartbeatExceededTitle = "External engine heartbeat rate limit was exceeded.";
    public const string HeartbeatExceededCode = "external-engine.heartbeat.rate-limited";

    public static async ValueTask OnRejectedAsync(OnRejectedContext context, CancellationToken _)
    {
        var policyName = context.HttpContext.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName;
        var (title, code) = CodeFor(policyName);
        if (string.Equals(policyName, CloudBootstrapRateLimitDefaults.PolicyName, StringComparison.Ordinal) ||
            string.Equals(policyName, ManagedElsaInstanceLifecycleRateLimitDefaults.PolicyName, StringComparison.Ordinal))
        {
            context.HttpContext.Response.Headers.RetryAfter = RetryAfterSeconds(context.Lease)
                .ToString(CultureInfo.InvariantCulture);
            context.HttpContext.Response.Headers.CacheControl = "no-store";
        }

        await Results.Problem(
                statusCode: StatusCodes.Status429TooManyRequests,
                title: title,
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = code,
                    ["correlationId"] = context.HttpContext.TraceIdentifier
                })
            .ExecuteAsync(context.HttpContext);
    }

    internal static (string Title, string Code) CodeFor(string? policyName) =>
        policyName switch
        {
            CloudBootstrapRateLimitDefaults.PolicyName => (
                CloudBootstrapRateLimitDefaults.ExceededTitle,
                CloudBootstrapRateLimitDefaults.ExceededCode),
            ManagedElsaInstanceLifecycleRateLimitDefaults.PolicyName => (
                ManagedElsaInstanceLifecycleRateLimitDefaults.ExceededTitle,
                ManagedElsaInstanceLifecycleRateLimitDefaults.ExceededCode),
            ExternalEngineHeartbeatPolicyName => (HeartbeatExceededTitle, HeartbeatExceededCode),
            _ => (HandoffExceededTitle, HandoffExceededCode)
        };

    private static int RetryAfterSeconds(RateLimitLease lease)
    {
        if (lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retryAfter))
            return Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));

        return 1;
    }
}
