using Microsoft.Extensions.Options;

namespace ElsaControl.Api.Authentication;

public static class ManagedElsaHandoffRateLimitDefaults
{
    public const string PolicyName = "managed-elsa-handoff";
    public const string ConfigurationSection = "RateLimiting:ManagedElsaHandoff";
    public const int DefaultPermitLimit = 20;
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(1);
}

public sealed class ManagedElsaHandoffFixedWindowRateLimitOptions
{
    public int PermitLimit { get; init; } = ManagedElsaHandoffRateLimitDefaults.DefaultPermitLimit;
    public TimeSpan Window { get; init; } = ManagedElsaHandoffRateLimitDefaults.DefaultWindow;
}

public sealed class ManagedElsaHandoffRateLimitOptions
{
    public const string ConfigurationSection = ManagedElsaHandoffRateLimitDefaults.ConfigurationSection;

    public ManagedElsaHandoffFixedWindowRateLimitOptions PerIp { get; init; } = new();
    public ManagedElsaHandoffFixedWindowRateLimitOptions PerIdentity { get; init; } = new();

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
