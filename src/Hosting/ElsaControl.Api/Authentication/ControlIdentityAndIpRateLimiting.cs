using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.RateLimiting;

namespace ElsaControl.Api.Authentication;

internal static class ControlRateLimitWindowRules
{
    public const int MinPermitLimit = 1;
    public const int MaxPermitLimit = 10_000;
    public static readonly TimeSpan MinWindow = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaxWindow = TimeSpan.FromHours(1);

    public static IEnumerable<string> Validate(int permitLimit, TimeSpan window, string prefix)
    {
        if (permitLimit < MinPermitLimit || permitLimit > MaxPermitLimit)
            yield return $"{prefix}:PermitLimit must be between {MinPermitLimit} and {MaxPermitLimit}.";
        if (window < MinWindow || window > MaxWindow)
            yield return $"{prefix}:Window must be between {MinWindow} and {MaxWindow}.";
    }
}

public readonly record struct ControlRateLimitWindow(int PermitLimit, TimeSpan Window);

internal sealed class ControlIdentityAndIpRateLimiter : RateLimiter
{
    internal const string AnonymousIdentityPartition = "anonymous";
    internal const string UnresolvedIdentityPartitionPrefix = "unresolved";
    internal const string BffIpPartitionPrefix = "bff:";
    internal const int UnresolvedIdentityPermitLimit = 1;
    private const string CachedPrincipalItemKey = "__control.rate-limit.principal";

    private static readonly AsyncLocal<HttpContext?> Current = new();

    private readonly PartitionedRateLimiter<HttpContext> _inner;

    public ControlIdentityAndIpRateLimiter(
        ControlRateLimitWindow perIp,
        ControlRateLimitWindow perIdentity,
        ControlIdentityOptions controlIdentity,
        CloudAccountIdentityOptions cloudAccount,
        HubAccountIdentityOptions hubAccount,
        bool limitIdentityForNonBff,
        ControlRateLimitWindow? bffPerIp = null)
    {
        var unresolvedIdentity = new ControlRateLimitWindow(
            UnresolvedIdentityPermitLimit,
            perIdentity.Window);
        var resolvedBffPerIp = bffPerIp ?? perIp;
        _inner = PartitionedRateLimiter.CreateChained(
            PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                var ipKey = GetIpPartitionKey(context);
                var principal = GetAuthenticatedPrincipal(context);
                var identity = ControlRateLimitIdentity.PartitionKey(
                    principal, controlIdentity, cloudAccount, hubAccount);
                if (identity is not null && IsBffSession(principal, cloudAccount, hubAccount))
                    return RateLimitPartition.GetFixedWindowLimiter(
                        BffIpPartitionPrefix + ipKey,
                        _ => CreateWindow(resolvedBffPerIp));

                return RateLimitPartition.GetFixedWindowLimiter(ipKey, _ => CreateWindow(perIp));
            }),
            PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                var principal = GetAuthenticatedPrincipal(context);
                var identity = ControlRateLimitIdentity.PartitionKey(
                    principal, controlIdentity, cloudAccount, hubAccount);
                if (identity is not null)
                {
                    if (limitIdentityForNonBff || IsBffSession(principal, cloudAccount, hubAccount))
                        return RateLimitPartition.GetFixedWindowLimiter(identity, _ => CreateWindow(perIdentity));

                    return RateLimitPartition.GetNoLimiter<string>(AnonymousIdentityPartition);
                }

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
    // IPv6 addresses are grouped on the /64 prefix, matching cloud-bootstrap.
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

    internal static string UnresolvedIdentityKey(HttpContext context) =>
        ControlRateLimitIdentity.Format(UnresolvedIdentityPartitionPrefix, GetIpPartitionKey(context));

    private static bool IsBffSession(
        ClaimsPrincipal? principal,
        CloudAccountIdentityOptions cloudAccount,
        HubAccountIdentityOptions hubAccount) =>
        HubAccountTokenSelector.IsValidatedHubAccount(principal, hubAccount) ||
        CloudAccountTokenSelector.IsValidatedCloudAccount(principal, cloudAccount);

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
            "Identity and IP rate limiting requires the current HTTP context.");

    private static FixedWindowRateLimiterOptions CreateWindow(ControlRateLimitWindow window) =>
        new()
        {
            PermitLimit = window.PermitLimit,
            Window = window.Window,
            QueueLimit = 0,
            AutoReplenishment = true
        };
}

public abstract class ControlIdentityAndIpRateLimiterPolicy : IRateLimiterPolicy<string>
{
    private readonly ControlIdentityAndIpRateLimiter _limiter;
    private readonly string _policyName;

    protected ControlIdentityAndIpRateLimiterPolicy(
        string policyName,
        ControlRateLimitWindow perIp,
        ControlRateLimitWindow perIdentity,
        ControlIdentityOptions controlIdentity,
        CloudAccountIdentityOptions cloudAccount,
        HubAccountIdentityOptions hubAccount,
        bool limitIdentityForNonBff,
        ControlRateLimitWindow? bffPerIp = null)
    {
        _policyName = policyName;
        _limiter = new ControlIdentityAndIpRateLimiter(
            perIp, perIdentity, controlIdentity, cloudAccount, hubAccount, limitIdentityForNonBff, bffPerIp);
    }

    public Func<OnRejectedContext, CancellationToken, ValueTask>? OnRejected => null;

    public RateLimitPartition<string> GetPartition(HttpContext httpContext)
    {
        // Named IRateLimiterPolicy factories cannot take HttpContext on Acquire.
        // GetPartition runs on the same call stack as AttemptAcquire, so AsyncLocal
        // carries the context into the shared limiter. IdleDuration = TimeSpan.Zero
        // keeps this partition from being evicted; the inner chained limiter owns eviction.
        ControlIdentityAndIpRateLimiter.SetCurrent(httpContext);
        return RateLimitPartition.Get(_policyName, _ => _limiter);
    }
}
