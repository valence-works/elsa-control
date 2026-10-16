using System.Globalization;
using System.Net;
using System.Net.Sockets;
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
        if (value.PermitLimit < CloudBootstrapRateLimitDefaults.MinPermitLimit ||
            value.PermitLimit > CloudBootstrapRateLimitDefaults.MaxPermitLimit)
            yield return $"{prefix}:PermitLimit must be between {CloudBootstrapRateLimitDefaults.MinPermitLimit} and {CloudBootstrapRateLimitDefaults.MaxPermitLimit}.";
        if (value.Window < CloudBootstrapRateLimitDefaults.MinWindow ||
            value.Window > CloudBootstrapRateLimitDefaults.MaxWindow)
            yield return $"{prefix}:Window must be between {CloudBootstrapRateLimitDefaults.MinWindow} and {CloudBootstrapRateLimitDefaults.MaxWindow}.";
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
    private readonly CloudBootstrapChainedRateLimiter _limiter;

    public CloudBootstrapRateLimiterPolicy(
        IOptions<CloudBootstrapRateLimitOptions> options,
        IOptions<ControlIdentityOptions> controlIdentity,
        IOptions<CloudAccountIdentityOptions> cloudAccount,
        IOptions<HubAccountIdentityOptions> hubAccount)
    {
        _limiter = new CloudBootstrapChainedRateLimiter(
            options.Value, controlIdentity.Value, cloudAccount.Value, hubAccount.Value);
    }

    public Func<OnRejectedContext, CancellationToken, ValueTask>? OnRejected => null;

    public RateLimitPartition<string> GetPartition(HttpContext httpContext)
    {
        // Named IRateLimiterPolicy factories cannot take HttpContext on Acquire.
        // GetPartition runs on the same call stack as AttemptAcquire, so AsyncLocal
        // carries the context into the shared limiter. IdleDuration = TimeSpan.Zero
        // keeps this partition from being evicted; the inner chained limiter owns eviction.
        CloudBootstrapChainedRateLimiter.SetCurrent(httpContext);
        return RateLimitPartition.Get(CloudBootstrapRateLimitDefaults.PolicyName, _ => _limiter);
    }
}

internal sealed class CloudBootstrapChainedRateLimiter : RateLimiter
{
    internal const string UnresolvedIdentityPartitionPrefix = "unresolved";
    internal const string AnonymousIdentityPartition = "anonymous";
    private const string CachedPrincipalItemKey = "__cloud-bootstrap.principal";

    private static readonly AsyncLocal<HttpContext?> Current = new();

    private readonly PartitionedRateLimiter<HttpContext> _inner;

    public CloudBootstrapChainedRateLimiter(
        CloudBootstrapRateLimitOptions options,
        ControlIdentityOptions controlIdentity,
        CloudAccountIdentityOptions cloudAccount,
        HubAccountIdentityOptions hubAccount)
    {
        var unresolvedIdentity = new CloudBootstrapFixedWindowRateLimitOptions
        {
            PermitLimit = CloudBootstrapRateLimitDefaults.UnresolvedIdentityPermitLimit,
            Window = options.PerIdentity.Window
        };
        _inner = PartitionedRateLimiter.CreateChained(
            PartitionedRateLimiter.Create<HttpContext, string>(context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    GetIpPartitionKey(context),
                    _ => CreateWindow(options.PerIp))),
            PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                var principal = GetAuthenticatedPrincipal(context);
                var identity = ControlRateLimitIdentity.PartitionKey(
                    principal, controlIdentity, cloudAccount, hubAccount);
                if (identity is not null)
                    return RateLimitPartition.GetFixedWindowLimiter(identity, _ => CreateWindow(options.PerIdentity));
                if (principal is not null)
                    return RateLimitPartition.GetFixedWindowLimiter(
                        UnresolvedIdentityKey(context),
                        _ => CreateWindow(unresolvedIdentity));
                return RateLimitPartition.GetNoLimiter<string>(AnonymousIdentityPartition);
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

    // Uses Connection.RemoteIpAddress. KnownProxies and KnownNetworks are unchanged.
    internal static string GetIpPartitionKey(HttpContext context)
    {
        var address = context.Connection.RemoteIpAddress;
        if (address is null)
            return "unknown";

        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (address.AddressFamily == AddressFamily.InterNetwork)
            return address.ToString();

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();
            Array.Clear(bytes, 8, 8);
            return new IPAddress(bytes).ToString();
        }

        return address.ToString();
    }

    internal static string? GetIdentityPartitionKey(
        HttpContext context,
        ControlIdentityOptions controlIdentity,
        CloudAccountIdentityOptions cloudAccount,
        HubAccountIdentityOptions hubAccount)
    {
        return ControlRateLimitIdentity.PartitionKey(
            GetAuthenticatedPrincipal(context),
            controlIdentity,
            cloudAccount,
            hubAccount);
    }

    internal static string UnresolvedIdentityKey(HttpContext context) =>
        ControlRateLimitIdentity.Format(UnresolvedIdentityPartitionPrefix, GetIpPartitionKey(context));

    private static ClaimsPrincipal? GetAuthenticatedPrincipal(HttpContext context)
    {
        if (context.Items.TryGetValue(CachedPrincipalItemKey, out var cached))
            return cached as ClaimsPrincipal;

        ClaimsPrincipal? principal;
        if (context.User.Identity is { IsAuthenticated: true })
            principal = context.User;
        else if (!context.Request.Cookies.ContainsKey(CustomerAuthenticationDefaults.CookieName))
            principal = null;
        else
        {
            // CustomerSession is not the default authenticate scheme. The partitioner is
            // synchronous, so cookie decryption runs inline. That blocks a thread for the
            // duration of unprotect, which is acceptable for this request rate.
            var result = context.AuthenticateAsync(CustomerAuthenticationDefaults.CookieScheme)
                .ConfigureAwait(false)
                .GetAwaiter()
                .GetResult();
            principal = result.Succeeded && result.Principal?.Identity is { IsAuthenticated: true }
                ? result.Principal
                : null;
        }

        context.Items[CachedPrincipalItemKey] = principal;
        return principal;
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
