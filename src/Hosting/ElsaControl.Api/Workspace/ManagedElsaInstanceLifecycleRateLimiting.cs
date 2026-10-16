using ElsaControl.Api.Authentication;
using Microsoft.Extensions.Options;

namespace ElsaControl.Api.Workspace;

public static class ManagedElsaInstanceLifecycleRateLimitDefaults
{
    public const string PolicyName = "managed-elsa-instance-lifecycle";
    public const string ConfigurationSection = "RateLimiting:ManagedElsaInstanceLifecycle";
    public const string ExceededCode = "rate-limit.exceeded";
    public const string ExceededTitle = "Managed Elsa instance lifecycle rate limit was exceeded.";
    public const int DefaultPermitLimit = 10;
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(1);
}

public sealed class ManagedElsaInstanceLifecycleFixedWindowRateLimitOptions
{
    public int PermitLimit { get; init; } = ManagedElsaInstanceLifecycleRateLimitDefaults.DefaultPermitLimit;
    public TimeSpan Window { get; init; } = ManagedElsaInstanceLifecycleRateLimitDefaults.DefaultWindow;
}

public sealed class ManagedElsaInstanceLifecycleRateLimitOptions
{
    public const string ConfigurationSection = ManagedElsaInstanceLifecycleRateLimitDefaults.ConfigurationSection;

    public ManagedElsaInstanceLifecycleFixedWindowRateLimitOptions PerIp { get; init; } = new();
    public ManagedElsaInstanceLifecycleFixedWindowRateLimitOptions PerIdentity { get; init; } = new();

    internal IEnumerable<string> Validate()
    {
        foreach (var error in ValidateWindow(PerIp, $"{ConfigurationSection}:PerIp"))
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
            limitIdentityForNonBff: true)
    {
    }
}
