using ElsaControl.Provisioning.Abstractions;

namespace ElsaControl.Provisioning.Providers.Azure;

/// <summary>
/// Fully qualified stack names for self-managed backends. DIY (Blob or file)
/// backends resolve <c>StackReference</c> as
/// <c>{organization}/{project}/{stack}</c>.
/// </summary>
public static class PulumiStackReferenceName
{
    public const string DefaultOrganization = ProvisioningStackReference.DefaultOrganization;

    public static string Platform(string environmentName, string? organization = null) =>
        ProvisioningStackReference.Platform(environmentName, organization);

    public static string Engine(string stackName, string? organization = null) =>
        ProvisioningStackReference.Engine(stackName, organization);
}
