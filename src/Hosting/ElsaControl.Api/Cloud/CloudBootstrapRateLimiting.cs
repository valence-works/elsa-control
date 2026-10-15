using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using ElsaControl.Api.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace ElsaControl.Api.Cloud;

public static class CloudBootstrapRateLimitDefaults
{
    public const string PolicyName = "cloud-bootstrap";
    public const string ConfigurationSection = "RateLimiting:CloudBootstrap";
    public const string ExceededCode = "rate-limit.exceeded";
    public const string ExceededTitle = "Cloud bootstrap rate limit was exceeded.";
    public const int DefaultPermitLimit = 60;
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(1);
}

public sealed class CloudBootstrapFixedWindowRateLimitOptions
{
    public int PermitLimit { get; init; } = CloudBootstrapRateLimitDefaults.DefaultPermitLimit;
    public TimeSpan Window { get; init; } = CloudBootstrapRateLimitDefaults.DefaultWindow;
}

public sealed class CloudBootstrapRateLimitOptions
{
    public const string ConfigurationSection = CloudBootstrapRateLimitDefaults.ConfigurationSection;

    public CloudBootstrapFixedWindowRateLimitOptions PerIp { get; init; } = new();
    public CloudBootstrapFixedWindowRateLimitOptions PerIdentity { get; init; } = new();

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
        if (value.PermitLimit <= 0)
            yield return $"{prefix}:PermitLimit must be a positive integer.";
        if (value.Window <= TimeSpan.Zero)
            yield return $"{prefix}:Window must be a positive duration.";
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

public sealed class CloudBootstrapRateLimiterPolicy : IRateLimiterPolicy<string>
{
    internal const string AnonymousIdentityPartition = "anonymous";

    private readonly CloudBootstrapChainedRateLimiter _limiter;

    public CloudBootstrapRateLimiterPolicy(
        IOptions<CloudBootstrapRateLimitOptions> options,
        IOptions<ControlIdentityOptions> controlIdentity,
        IOptions<CloudAccountIdentityOptions> cloudAccount)
    {
        _limiter = new CloudBootstrapChainedRateLimiter(options.Value, controlIdentity.Value, cloudAccount.Value);
    }

    public Func<OnRejectedContext, CancellationToken, ValueTask>? OnRejected => null;

    public RateLimitPartition<string> GetPartition(HttpContext httpContext)
    {
        CloudBootstrapChainedRateLimiter.SetCurrent(httpContext);
        return RateLimitPartition.Get(CloudBootstrapRateLimitDefaults.PolicyName, _ => _limiter);
    }
}

internal sealed class CloudBootstrapChainedRateLimiter : RateLimiter
{
    private static readonly AsyncLocal<HttpContext?> Current = new();

    private readonly PartitionedRateLimiter<HttpContext> _inner;

    public CloudBootstrapChainedRateLimiter(
        CloudBootstrapRateLimitOptions options,
        ControlIdentityOptions controlIdentity,
        CloudAccountIdentityOptions cloudAccount)
    {
        _inner = PartitionedRateLimiter.CreateChained(
            PartitionedRateLimiter.Create<HttpContext, string>(context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    GetIpPartitionKey(context),
                    _ => CreateWindow(options.PerIp))),
            PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                var identity = GetIdentityPartitionKey(context, controlIdentity, cloudAccount);
                return identity is null
                    ? RateLimitPartition.GetNoLimiter<string>(CloudBootstrapRateLimiterPolicy.AnonymousIdentityPartition)
                    : RateLimitPartition.GetFixedWindowLimiter(identity, _ => CreateWindow(options.PerIdentity));
            }));
    }

    public override TimeSpan? IdleDuration => TimeSpan.Zero;

    public override RateLimiterStatistics? GetStatistics() => null;

    public static void SetCurrent(HttpContext context) => Current.Value = context;

    protected override RateLimitLease AttemptAcquireCore(int permitCount) =>
        _inner.AttemptAcquire(RequireCurrent(), permitCount);

    protected override ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken) =>
        _inner.AcquireAsync(RequireCurrent(), permitCount, cancellationToken);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _inner.Dispose();
        base.Dispose(disposing);
    }

    protected override ValueTask DisposeAsyncCore()
    {
        _inner.Dispose();
        return ValueTask.CompletedTask;
    }

    internal static string GetIpPartitionKey(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    internal static string? GetIdentityPartitionKey(
        HttpContext context,
        ControlIdentityOptions controlIdentity,
        CloudAccountIdentityOptions cloudAccount)
    {
        var principal = GetAuthenticatedPrincipal(context);
        if (principal is null)
            return null;

        var identity = CloudAccountTokenSelector.IsValidatedCloudAccount(principal, cloudAccount)
            ? ControlClaimsIdentityMapper.ToCloudTrustedWorkspaceIdentity(principal)
            : ControlClaimsIdentityMapper.ToTrustedWorkspaceIdentity(principal, controlIdentity);
        if (identity is null || string.IsNullOrWhiteSpace(identity.Issuer) || string.IsNullOrWhiteSpace(identity.Subject))
            return null;

        return string.Concat(identity.Issuer, "\u001f", identity.Subject);
    }

    private static ClaimsPrincipal? GetAuthenticatedPrincipal(HttpContext context)
    {
        if (context.User.Identity is { IsAuthenticated: true })
            return context.User;

        if (!context.Request.Cookies.ContainsKey(CustomerAuthenticationDefaults.CookieName))
            return null;

        // Hub cookies are not the default authenticate scheme, and the partitioner is sync.
        var result = context.AuthenticateAsync(CustomerAuthenticationDefaults.CookieScheme)
            .ConfigureAwait(false)
            .GetAwaiter()
            .GetResult();
        return result.Succeeded && result.Principal?.Identity is { IsAuthenticated: true }
            ? result.Principal
            : null;
    }

    private static HttpContext RequireCurrent() =>
        Current.Value ?? throw new InvalidOperationException(
            "Cloud bootstrap rate limiting requires the current HTTP context.");

    private static FixedWindowRateLimiterOptions CreateWindow(CloudBootstrapFixedWindowRateLimitOptions window) =>
        new()
        {
            PermitLimit = window.PermitLimit,
            Window = window.Window,
            QueueLimit = 0,
            AutoReplenishment = true
        };
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
        if (string.Equals(policyName, CloudBootstrapRateLimitDefaults.PolicyName, StringComparison.Ordinal))
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
