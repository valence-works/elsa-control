namespace ElsaControl.Provisioning.Core;

/// <summary>
/// Stable Container Apps ingress host for an engine. The stack emits
/// <c>Configuration.Ingress.Fqdn</c> (<c>{app}.{env}.azurecontainerapps.io</c>),
/// not the per-revision <c>LatestRevisionFqdn</c>
/// (<c>{app}--{suffix}.{env}.azurecontainerapps.io</c>). The readiness
/// verifier accepts only that app host.
/// </summary>
public static class ProvisioningEngineIngress
{
    public const string AzureContainerAppsSuffix = ".azurecontainerapps.io";
    public const string MockEnvironmentDomain = "example.azurecontainerapps.io";

    public static string Origin(string appName, string environmentDomain)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appName);
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentDomain);
        var domain = environmentDomain.Trim().TrimStart('.');
        return $"https://{appName}.{domain}";
    }

    public static string OriginForInstance(Guid instanceId, string environmentDomain) =>
        Origin(ProvisioningEngineContainerApp.ForInstance(instanceId), environmentDomain);

    public static string RevisionOrigin(string appName, string revisionSuffix, string environmentDomain)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appName);
        ArgumentException.ThrowIfNullOrWhiteSpace(revisionSuffix);
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentDomain);
        var domain = environmentDomain.Trim().TrimStart('.');
        return $"https://{appName}--{revisionSuffix}.{domain}";
    }

    public static bool HostMatchesApp(string endpoint, string appName)
    {
        if (string.IsNullOrWhiteSpace(appName))
            return false;
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var origin)
            || origin.HostNameType != UriHostNameType.Dns
            || !string.Equals(origin.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return false;

        var host = origin.Host;
        if (!host.EndsWith(AzureContainerAppsSuffix, StringComparison.OrdinalIgnoreCase))
            return false;

        // Stable app host: {app}.{env}.azurecontainerapps.io.
        // Revision-label hosts are {app}--{suffix}.{env}.azurecontainerapps.io.
        return host.StartsWith(appName + ".", StringComparison.OrdinalIgnoreCase);
    }
}
