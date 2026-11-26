namespace ElsaControl.Provisioning.Providers.Azure;

/// <summary>
/// Registered Pulumi output keys. The .NET SDK uses the property name unless
/// <c>[Output]</c> sets one, so these camelCase names are the StackReference
/// contract.
/// </summary>
public static class PulumiStackOutputs
{
    public const string ContainerEnvironmentId = "containerEnvironmentId";
    public const string ContainerEnvironmentKind = "containerEnvironmentKind";
    public const string ContainerEnvironmentDefaultDomain = "containerEnvironmentDefaultDomain";
    public const string RegistryId = "registryId";
    public const string RegistryLoginServer = "registryLoginServer";
}
