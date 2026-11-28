using ElsaControl.Provisioning.Abstractions;

namespace ElsaControl.Provisioning.Core;

/// <summary>
/// Rebuilds provider settings on a stored request from the current host
/// config. Snapshot locators go stale after a settings fix; config wins.
/// </summary>
public static class ProvisioningRequestRefresh
{
    public static ProvisioningRequest Apply(ProvisioningRequest request, ProvisioningOptions options)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);

        var target = request.Target;
        var credentials = OverlayCredential(target.Credentials, options.CredentialLocator);
        var backend = OverlayBackend(target.StateBackend, options.StateBackendLocator);
        var secrets = OverlaySecrets(target.SecretsProvider, options.SecretsProviderLocator);
        var config = target.StackKind == ProvisioningStackKind.Engine
            ? OverlayRegistry(request.StackConfig, options)
            : request.StackConfig;
        if (ReferenceEquals(credentials, target.Credentials)
            && ReferenceEquals(backend, target.StateBackend)
            && ReferenceEquals(secrets, target.SecretsProvider)
            && ReferenceEquals(config, request.StackConfig))
        {
            return request;
        }

        return new ProvisioningRequest(
            request.Command,
            request.Spec,
            new ProvisioningTarget(
                target.ProviderId,
                target.StackKind,
                target.StackName,
                credentials,
                backend,
                secrets,
                target.PlatformStackName),
            request.IdempotencyKey,
            config);
    }

    public static IReadOnlyDictionary<string, string> OverlayRegistry(
        IReadOnlyDictionary<string, string> stackConfig,
        ProvisioningOptions options)
    {
        ArgumentNullException.ThrowIfNull(stackConfig);
        ArgumentNullException.ThrowIfNull(options);
        var login = Trim(options.RegistryLoginServer);
        var registryId = Trim(options.RegistryId);
        if (login is null && registryId is null)
            return stackConfig;

        var config = new Dictionary<string, string>(stackConfig, StringComparer.Ordinal);
        if (login is not null)
            config["registryLoginServer"] = login;
        if (registryId is not null)
            config["registryId"] = registryId;
        return config;
    }

    private static CredentialReference OverlayCredential(CredentialReference current, string? locator)
    {
        var trimmed = Trim(locator);
        return trimmed is null || string.Equals(current.Locator, trimmed, StringComparison.Ordinal)
            ? current
            : new CredentialReference(current.Kind, trimmed);
    }

    private static StateBackendReference OverlayBackend(StateBackendReference current, string? locator)
    {
        var trimmed = Trim(locator);
        return trimmed is null || string.Equals(current.Locator, trimmed, StringComparison.Ordinal)
            ? current
            : new StateBackendReference(current.Kind, trimmed, current.LockingEnabled);
    }

    private static SecretsProviderReference OverlaySecrets(SecretsProviderReference current, string? locator)
    {
        var trimmed = Trim(locator);
        return trimmed is null || string.Equals(current.Locator, trimmed, StringComparison.Ordinal)
            ? current
            : new SecretsProviderReference(current.Kind, trimmed);
    }

    private static string? Trim(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
