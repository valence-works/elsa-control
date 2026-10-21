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
    internal const string BffCredentialPartitionPrefix = "bff:source:";
    internal const int UnresolvedIdentityPermitLimit = 1;
    private const string CachedPrincipalItemKey = "__control.rate-limit.principal";
    private const string CachedBffCredentialSourceItemKey = "__control.rate-limit.bff-credential-source";

    private static readonly AsyncLocal<HttpContext?> Current = new();

    // The shared window (PerIp, bff:<ip> or bff:source:<name>) and the per-identity window.
    // They are acquired in the order that keeps a rejection from spending the other window (#883).
    private readonly PartitionedRateLimiter<HttpContext> _shared;
    private readonly PartitionedRateLimiter<HttpContext> _identity;

    // Per instance: each policy has its own windows, so a cached partition must not cross policies.
    private readonly object _sharedPartitionItemKey = new();

    public ControlIdentityAndIpRateLimiter(
        ControlRateLimitWindow perIp,
        ControlRateLimitWindow perIdentity,
        ControlIdentityOptions controlIdentity,
        CloudAccountIdentityOptions cloudAccount,
        HubAccountIdentityOptions hubAccount,
        bool limitIdentityForNonBff,
        ControlRateLimitWindow? bffPerIp = null,
        IReadOnlyList<IPNetwork>? bffEgressNetworks = null,
        ControlRateLimitBffSourceDiagnostic? bffSourceDiagnostic = null,
        ControlRateLimitBffCredentialOptions? bffCredentials = null)
    {
        var unresolvedIdentity = new ControlRateLimitWindow(
            UnresolvedIdentityPermitLimit,
            perIdentity.Window);
        var allowlistedEgress = bffEgressNetworks ?? [];
        var credentialMatcher = new ControlRateLimitBffCredentialMatcher(bffCredentials);
        _shared = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            CachedSharedPartition(context, () =>
            {
                var ipKey = GetIpPartitionKey(context);
                var principal = GetAuthenticatedPrincipal(context);
                var identity = ControlRateLimitIdentity.PartitionKey(
                    principal, controlIdentity, cloudAccount, hubAccount);
                if (bffPerIp is { } bffWindow &&
                    identity is not null &&
                    IsBffSession(principal, cloudAccount, hubAccount))
                {
                    var remoteIpAddress = context.Connection.RemoteIpAddress;
                    var credentialSource = GetBffCredentialSource(context, credentialMatcher);
                    var egressMatched = ControlRateLimitBffEgress.Contains(remoteIpAddress, allowlistedEgress);
                    bffSourceDiagnostic?.Record(remoteIpAddress, egressMatched, credentialSource);

                    // A matched credential wins over the egress match, so each BFF has one window
                    // whatever address it leaves from. It selects that window only for a session
                    // of its own type: a cloud credential with a Hub session stays on PerIp.
                    if (CredentialMatchesSession(credentialSource, principal, cloudAccount, hubAccount))
                        return RateLimitPartition.GetFixedWindowLimiter(
                            BffCredentialPartitionPrefix + credentialSource,
                            _ => CreateWindow(bffWindow));

                    if (egressMatched)
                        return RateLimitPartition.GetFixedWindowLimiter(
                            BffIpPartitionPrefix + ipKey,
                            _ => CreateWindow(bffWindow));
                }

                return RateLimitPartition.GetFixedWindowLimiter(ipKey, _ => CreateWindow(perIp));
            }));
        _identity = PartitionedRateLimiter.Create<HttpContext, string>(context =>
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
            });
    }

    public override TimeSpan? IdleDuration => TimeSpan.Zero;

    public override RateLimiterStatistics? GetStatistics() => null;

    public static void SetCurrent(HttpContext context) => Current.Value = context;

    protected override RateLimitLease AttemptAcquireCore(int permitCount)
    {
        var context = RequireCurrent();
        var (first, second) = AcquireOrder(context, permitCount);
        var firstLease = first.AttemptAcquire(context, permitCount);
        if (!firstLease.IsAcquired)
            return firstLease;

        RateLimitLease secondLease;
        try
        {
            secondLease = second.AttemptAcquire(context, permitCount);
        }
        catch
        {
            firstLease.Dispose();
            throw;
        }

        return Combine(firstLease, secondLease);
    }

    protected override async ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken)
    {
        var context = RequireCurrent();
        var (first, second) = AcquireOrder(context, permitCount);
        var firstLease = await first.AcquireAsync(context, permitCount, cancellationToken).ConfigureAwait(false);
        if (!firstLease.IsAcquired)
            return firstLease;

        RateLimitLease secondLease;
        try
        {
            secondLease = await second.AcquireAsync(context, permitCount, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            firstLease.Dispose();
            throw;
        }

        return Combine(firstLease, secondLease);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _shared.Dispose();
            _identity.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override ValueTask DisposeAsyncCore()
    {
        _shared.Dispose();
        _identity.Dispose();
        return ValueTask.CompletedTask;
    }

    // A fixed-window permit is not returned when a later limiter rejects, so the first
    // limiter's permit is spent either way. While the shared window has room, the identity
    // window goes first: a caller already at its own limit is refused without spending the
    // shared window that other callers on the same address or BFF depend on. Once the shared
    // window is exhausted it goes first, so its refusal does not spend the caller's identity
    // permits. The statistics read and the acquire are not atomic: each request that races
    // the moment the shared window runs out can still spend one identity permit.
    private (PartitionedRateLimiter<HttpContext> First, PartitionedRateLimiter<HttpContext> Second) AcquireOrder(
        HttpContext context,
        int permitCount) =>
        _shared.GetStatistics(context)?.CurrentAvailablePermits >= permitCount
            ? (_identity, _shared)
            : (_shared, _identity);

    private static RateLimitLease Combine(RateLimitLease first, RateLimitLease second)
    {
        if (second.IsAcquired)
            return new CombinedLease(first, second);

        first.Dispose();
        return second;
    }

    // The shared partition is chosen once per request: the statistics read and the acquire
    // both resolve it, and the BFF source diagnostic must record a request only once.
    private RateLimitPartition<string> CachedSharedPartition(
        HttpContext context,
        Func<RateLimitPartition<string>> select)
    {
        if (context.Items.TryGetValue(_sharedPartitionItemKey, out var cached) &&
            cached is RateLimitPartition<string> partition)
            return partition;

        partition = select();
        context.Items[_sharedPartitionItemKey] = partition;
        return partition;
    }

    private sealed class CombinedLease(RateLimitLease first, RateLimitLease second) : RateLimitLease
    {
        public override bool IsAcquired => true;

        public override IEnumerable<string> MetadataNames =>
            first.MetadataNames.Concat(second.MetadataNames).Distinct(StringComparer.Ordinal);

        public override bool TryGetMetadata(string metadataName, out object? metadata) =>
            first.TryGetMetadata(metadataName, out metadata) || second.TryGetMetadata(metadataName, out metadata);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                first.Dispose();
                second.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    // Uses Connection.RemoteIpAddress, which is the rightmost X-Forwarded-For entry:
    // Program.cs registers explicit ForwardedHeadersOptions with ForwardLimit = 1, so
    // only the entry the App Service front end appends is read and left entries a caller
    // writes are ignored. Without X-Forwarded-For it is the connection's peer address.
    // IPv6 addresses are grouped on the /64 prefix, matching cloud-bootstrap.
    // BffEgressNetworks is matched against this same address.
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

    private static bool CredentialMatchesSession(
        string? credentialSource,
        ClaimsPrincipal? principal,
        CloudAccountIdentityOptions cloudAccount,
        HubAccountIdentityOptions hubAccount) =>
        credentialSource switch
        {
            ControlRateLimitBffCredentialSources.Cloud =>
                CloudAccountTokenSelector.IsValidatedCloudAccount(principal, cloudAccount),
            ControlRateLimitBffCredentialSources.Hub =>
                HubAccountTokenSelector.IsValidatedHubAccount(principal, hubAccount),
            _ => false
        };

    // Matching runs once per request; the result (a source name or null) is cached like the principal.
    private static string? GetBffCredentialSource(HttpContext context, ControlRateLimitBffCredentialMatcher matcher)
    {
        if (context.Items.TryGetValue(CachedBffCredentialSourceItemKey, out var cached))
            return cached as string;

        var source = matcher.Match(context.Request.Headers[ControlRateLimitBffCredentialOptions.HeaderName]);
        context.Items[CachedBffCredentialSourceItemKey] = source;
        return source;
    }

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
        ControlRateLimitWindow? bffPerIp = null,
        IReadOnlyList<IPNetwork>? bffEgressNetworks = null,
        ILoggerFactory? bffSourceLoggerFactory = null,
        ControlRateLimitBffCredentialOptions? bffCredentials = null)
    {
        _policyName = policyName;
        _limiter = new ControlIdentityAndIpRateLimiter(
            perIp,
            perIdentity,
            controlIdentity,
            cloudAccount,
            hubAccount,
            limitIdentityForNonBff,
            bffPerIp,
            bffEgressNetworks,
            bffSourceLoggerFactory is null
                ? null
                : new ControlRateLimitBffSourceDiagnostic(
                    bffSourceLoggerFactory.CreateLogger(ControlRateLimitBffSourceDiagnostic.LoggerCategory),
                    policyName),
            bffCredentials);
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
