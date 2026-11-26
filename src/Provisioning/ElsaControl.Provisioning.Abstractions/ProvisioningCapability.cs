namespace ElsaControl.Provisioning.Abstractions;

/// <summary>
/// Cloud-neutral capability identifiers used by the engine spec and by providers.
/// Providers declare which of these they support; the spec never names a cloud.
/// </summary>
public static class ProvisioningCapability
{
    public const string ContainerRuntime = "container-runtime";
    public const string ManagedSql = "managed-sql";
    public const string Secrets = "secrets";
    public const string Ingress = "ingress";
    public const string Scale = "scale";
    public const string SharedEnvironment = "shared-environment";
    public const string SharedRegistry = "shared-registry";
}
