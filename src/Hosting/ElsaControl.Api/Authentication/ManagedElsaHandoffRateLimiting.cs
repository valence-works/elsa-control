using Microsoft.Extensions.Options;

namespace ElsaControl.Api.Authentication;

public static class ManagedElsaHandoffRateLimitDefaults
{
    public const string PolicyName = "managed-elsa-handoff";
    public const string ConfigurationSection = "RateLimiting:ManagedElsaHandoff";
    public const int DefaultPerIdentityPermitLimit = 20;
    public const int DefaultPerIpPermitLimit = DefaultPerIdentityPermitLimit * 10;
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
    /// Fixed window applied to every request, including validated Cloud/Hub BFF
    /// sessions, Control JWT callers, and authenticated cookies. The default
    /// permit is ten times the per-identity default so shared BFF egress has
    /// aggregate headroom while per-identity remains the binding limit for
    /// BFF sessions. Control JWT and cookie callers with a resolvable identity
    /// stay IP-only at this ceiling.
    /// </summary>
    public ManagedElsaHandoffFixedWindowRateLimitOptions PerIp { get; init; } = new()
    {
        PermitLimit = ManagedElsaHandoffRateLimitDefaults.DefaultPerIpPermitLimit
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
        IOptions<HubAccountIdentityOptions> hubAccount)
        : base(
            ManagedElsaHandoffRateLimitDefaults.PolicyName,
            new ControlRateLimitWindow(options.Value.PerIp.PermitLimit, options.Value.PerIp.Window),
            new ControlRateLimitWindow(options.Value.PerIdentity.PermitLimit, options.Value.PerIdentity.Window),
            controlIdentity.Value,
            cloudAccount.Value,
            hubAccount.Value,
            limitIdentityForNonBff: false)
    {
    }
}
