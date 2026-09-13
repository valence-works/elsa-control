using ElsaControl.Deployment.Azure;
using ElsaControl.Deployment.Core.Instances;
using ElsaControl.Deployment.Core.Provisioning;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ElsaControl.Api.Workspace;

/// <summary>
/// Composes the optional Azure lifecycle adapter only when its explicit authority
/// is enabled and valid. Keeping this decision in one production seam makes it
/// testable without starting the complete API host.
/// </summary>
internal static class AzureInstanceLifecycleComposition
{
    public static bool AddProviderPorts(
        IServiceCollection services,
        IConfiguration configuration,
        AzureProviderRunnerAuthority? runnerAuthority)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(AzureElsaInstanceProviderOptions.ConfigurationSection);
        var enabled = section.GetValue<bool>(nameof(AzureElsaInstanceProviderOptions.Enabled));
        if (!enabled)
            return false;
        if (runnerAuthority is null)
            throw new InvalidOperationException(
                "Managed instance lifecycle requires the enabled concrete Azure provider worker authority.");

        // Derive, rather than duplicate, both fingerprints from the validated
        // runner authority so lifecycle reservations cannot drift from execution.
        var options = new AzureElsaInstanceProviderOptions
        {
            Enabled = true,
            TemplateFingerprint = runnerAuthority.TemplateFingerprint,
            ProviderScopeFingerprint = runnerAuthority.ProviderScopeFingerprint,
            SubscriptionId = runnerAuthority.Scope.SubscriptionId,
            ResourceGroupNamePrefix = runnerAuthority.Scope.ResourceGroupName,
            ResourceGroupNamingVersion = 1
        };
        options.Validate();

        services.AddSingleton(options);
        services.AddSingleton<IEngineProvisioningModule, AzureEngineProvisioningModule>();
        services.AddScoped<AzureElsaInstanceProvider>();
        services.AddScoped<IElsaInstanceProviderSubmissionPort>(provider =>
            provider.GetRequiredService<AzureElsaInstanceProvider>());
        services.AddScoped<IElsaInstanceProviderReconciliationPort>(provider =>
            provider.GetRequiredService<AzureElsaInstanceProvider>());
        services.AddScoped<IElsaInstanceProviderCleanupPort>(provider =>
            provider.GetRequiredService<AzureElsaInstanceProvider>());
        services.AddScoped<IElsaInstanceProviderDeleteRecoveryPort, ScopedAzureInstanceProviderDeleteRecoveryPort>();
        services.AddScoped<IElsaInstanceProviderRecoveryPort>(provider =>
            provider.GetRequiredService<AzureElsaInstanceProvider>());
        return true;
    }
}
