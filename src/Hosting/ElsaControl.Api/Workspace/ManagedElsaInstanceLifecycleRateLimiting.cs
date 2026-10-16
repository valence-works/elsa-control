using ElsaControl.Api.Authentication;
using Microsoft.Extensions.Options;

namespace ElsaControl.Api.Workspace;

public static class ManagedElsaInstanceLifecycleRateLimitDefaults
{
    public const string PolicyName = "managed-elsa-instance-lifecycle";
    public const string ConfigurationSection = "RateLimiting:ManagedElsaInstanceLifecycle";
    public const string ExceededCode = "rate-limit.exceeded";
    public const string ExceededTitle = "Managed Elsa instance lifecycle rate limit was exceeded.";
    public const int DefaultPerIdentityPermitLimit = 10;
    public const int DefaultPerIpPermitLimit = DefaultPerIdentityPermitLimit * 10;
    public const int DefaultBffPerIpPermitLimit = ControlRateLimitWindowRules.MaxPermitLimit;
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(1);
}

public sealed class ManagedElsaInstanceLifecycleFixedWindowRateLimitOptions
{
    public int PermitLimit { get; init; } = ManagedElsaInstanceLifecycleRateLimitDefaults.DefaultPerIdentityPermitLimit;
    public TimeSpan Window { get; init; } = ManagedElsaInstanceLifecycleRateLimitDefaults.DefaultWindow;
}

public sealed class ManagedElsaInstanceLifecycleRateLimitOptions
{
    public const string ConfigurationSection = ManagedElsaInstanceLifecycleRateLimitDefaults.ConfigurationSection;

    /// <summary>
    /// Fixed window for non-BFF callers and for authenticated sessions that
    /// cannot resolve issuer plus subject. Default is ten times the per-identity
    /// permit.
    /// </summary>
    public ManagedElsaInstanceLifecycleFixedWindowRateLimitOptions PerIp { get; init; } = new()
    {
        PermitLimit = ManagedElsaInstanceLifecycleRateLimitDefaults.DefaultPerIpPermitLimit
    };

    /// <summary>
    /// Fixed window for validated Cloud/Hub BFF sessions that resolve issuer
    /// plus subject. Those calls reach Control from the BFF host, so many
    /// identities share one egress address. The default is the maximum allowed
    /// permit so this ceiling is not a global cap across BFF users; per-identity
    /// remains binding. Unresolved BFF sessions still use <see cref="PerIp"/>
    /// plus the 1-permit unresolved fallback.
    /// </summary>
    public ManagedElsaInstanceLifecycleFixedWindowRateLimitOptions BffPerIp { get; init; } = new()
    {
        PermitLimit = ManagedElsaInstanceLifecycleRateLimitDefaults.DefaultBffPerIpPermitLimit
    };

    /// <summary>
    /// Fixed window keyed on validated issuer plus subject. This is the main
    /// limit for signed-in callers. Authenticated sessions that cannot resolve
    /// both values use a separate low-permit fallback bucket partitioned by IP.
    /// Unauthenticated requests use the per-IP window only.
    /// </summary>
    public ManagedElsaInstanceLifecycleFixedWindowRateLimitOptions PerIdentity { get; init; } = new()
    {
        PermitLimit = ManagedElsaInstanceLifecycleRateLimitDefaults.DefaultPerIdentityPermitLimit
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
        ManagedElsaInstanceLifecycleFixedWindowRateLimitOptions? window,
        string prefix)
    {
        var value = window ?? new ManagedElsaInstanceLifecycleFixedWindowRateLimitOptions
        {
            PermitLimit = 0,
            Window = TimeSpan.Zero
        };
        return ControlRateLimitWindowRules.Validate(value.PermitLimit, value.Window, prefix);
    }
}

public sealed class ManagedElsaInstanceLifecycleRateLimitConfigurationValidator(
    IOptions<ManagedElsaInstanceLifecycleRateLimitOptions> options) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var errors = options.Value.Validate().ToArray();
        if (errors.Length > 0)
            throw new InvalidOperationException(
                $"Managed Elsa instance lifecycle rate limiting configuration is invalid: {string.Join(" ", errors)}");

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class ManagedElsaInstanceLifecycleRateLimiterPolicy : ControlIdentityAndIpRateLimiterPolicy
{
    public ManagedElsaInstanceLifecycleRateLimiterPolicy(
        IOptions<ManagedElsaInstanceLifecycleRateLimitOptions> options,
        IOptions<ControlIdentityOptions> controlIdentity,
        IOptions<CloudAccountIdentityOptions> cloudAccount,
        IOptions<HubAccountIdentityOptions> hubAccount)
        : base(
            ManagedElsaInstanceLifecycleRateLimitDefaults.PolicyName,
            new ControlRateLimitWindow(options.Value.PerIp.PermitLimit, options.Value.PerIp.Window),
            new ControlRateLimitWindow(options.Value.PerIdentity.PermitLimit, options.Value.PerIdentity.Window),
            controlIdentity.Value,
            cloudAccount.Value,
            hubAccount.Value,
            limitIdentityForNonBff: true,
            bffPerIp: new ControlRateLimitWindow(options.Value.BffPerIp.PermitLimit, options.Value.BffPerIp.Window))
    {
    }
}
