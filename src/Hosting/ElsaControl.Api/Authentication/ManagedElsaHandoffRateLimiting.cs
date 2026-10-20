using Microsoft.Extensions.Options;

namespace ElsaControl.Api.Authentication;

public static class ManagedElsaHandoffRateLimitDefaults
{
    public const string PolicyName = "managed-elsa-handoff";
    public const string ConfigurationSection = "RateLimiting:ManagedElsaHandoff";
    public const int DefaultPerIdentityPermitLimit = 20;
    public const int DefaultPerIpPermitLimit = DefaultPerIdentityPermitLimit * 10;
    public const int DefaultBffPerIpPermitLimit = ControlRateLimitWindowRules.MaxPermitLimit;
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(1);
}

public sealed class ManagedElsaHandoffFixedWindowRateLimitOptions
{
    public int PermitLimit { get; init; } = ManagedElsaHandoffRateLimitDefaults.DefaultPerIdentityPermitLimit;
    public TimeSpan Window { get; init; } = ManagedElsaHandoffRateLimitDefaults.DefaultWindow;
}

public sealed class ManagedElsaHandoffRateLimitOptions
{
    public const string ConfigurationSection = ManagedElsaHandoffRateLimitDefaults.ConfigurationSection;

    /// <summary>
    /// Fixed window for non-BFF callers and for authenticated sessions that
    /// cannot resolve issuer plus subject. Default is ten times the per-identity
    /// permit. Control JWT and cookie callers with a resolvable identity stay
    /// IP-only at this ceiling.
    /// </summary>
    public ManagedElsaHandoffFixedWindowRateLimitOptions PerIp { get; init; } = new()
    {
        PermitLimit = ManagedElsaHandoffRateLimitDefaults.DefaultPerIpPermitLimit
    };

    /// <summary>
    /// Fixed window for validated Cloud/Hub sessions whose
    /// <c>Connection.RemoteIpAddress</c> is inside
    /// <see cref="ControlRateLimitBffEgressOptions.ConfigurationKey"/>.
    /// <c>RemoteIpAddress</c> is the rightmost <c>X-Forwarded-For</c> entry, set by the
    /// explicit <c>ForwardedHeadersOptions</c> with <c>ForwardLimit = 1</c>. Those
    /// calls share a BFF egress address, so the default is the maximum allowed
    /// permit and per-identity remains binding. Every other caller, including
    /// Cloud/Hub sessions from addresses outside the list and unresolved
    /// identities, uses <see cref="PerIp"/>. An empty list (the default) means
    /// everyone uses <see cref="PerIp"/>.
    /// </summary>
    public ManagedElsaHandoffFixedWindowRateLimitOptions BffPerIp { get; init; } = new()
    {
        PermitLimit = ManagedElsaHandoffRateLimitDefaults.DefaultBffPerIpPermitLimit
    };

    /// <summary>
    /// Fixed window keyed on validated issuer plus subject. Applied to Cloud/Hub
    /// BFF sessions. Authenticated sessions that cannot resolve both values use
    /// a separate low-permit fallback bucket partitioned by IP. Unauthenticated
    /// requests and resolved Control JWT / cookie callers use the per-IP window only.
    /// </summary>
    public ManagedElsaHandoffFixedWindowRateLimitOptions PerIdentity { get; init; } = new()
    {
        PermitLimit = ManagedElsaHandoffRateLimitDefaults.DefaultPerIdentityPermitLimit
    };

    internal IEnumerable<string> Validate()
    {
        foreach (var error in ValidateWindow(PerIp, $"{ConfigurationSection}:PerIp"))
            yield return error;
        foreach (var error in ValidateWindow(BffPerIp, $"{ConfigurationSection}:BffPerIp"))
            yield return error;
        foreach (var error in ValidateWindow(PerIdentity, $"{ConfigurationSection}:PerIdentity"))
            yield return error;
    }

    private static IEnumerable<string> ValidateWindow(
        ManagedElsaHandoffFixedWindowRateLimitOptions? window,
        string prefix)
    {
        var value = window ?? new ManagedElsaHandoffFixedWindowRateLimitOptions { PermitLimit = 0, Window = TimeSpan.Zero };
        return ControlRateLimitWindowRules.Validate(value.PermitLimit, value.Window, prefix);
    }
}

public sealed class ManagedElsaHandoffRateLimitConfigurationValidator(
    IOptions<ManagedElsaHandoffRateLimitOptions> options) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var errors = options.Value.Validate().ToArray();
        if (errors.Length > 0)
            throw new InvalidOperationException(
                $"Managed Elsa handoff rate limiting configuration is invalid: {string.Join(" ", errors)}");

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class ManagedElsaHandoffRateLimiterPolicy : ControlIdentityAndIpRateLimiterPolicy
{
    public ManagedElsaHandoffRateLimiterPolicy(
        IOptions<ManagedElsaHandoffRateLimitOptions> options,
        IOptions<ControlIdentityOptions> controlIdentity,
        IOptions<CloudAccountIdentityOptions> cloudAccount,
        IOptions<HubAccountIdentityOptions> hubAccount,
        IOptions<ControlRateLimitBffEgressOptions> bffEgress,
        ILoggerFactory loggerFactory)
        : base(
            ManagedElsaHandoffRateLimitDefaults.PolicyName,
            new ControlRateLimitWindow(options.Value.PerIp.PermitLimit, options.Value.PerIp.Window),
            new ControlRateLimitWindow(options.Value.PerIdentity.PermitLimit, options.Value.PerIdentity.Window),
            controlIdentity.Value,
            cloudAccount.Value,
            hubAccount.Value,
            limitIdentityForNonBff: false,
            bffPerIp: new ControlRateLimitWindow(options.Value.BffPerIp.PermitLimit, options.Value.BffPerIp.Window),
            bffEgressNetworks: ControlRateLimitBffEgress.Parse(bffEgress.Value.Networks),
            bffSourceLoggerFactory: loggerFactory)
    {
    }
}
