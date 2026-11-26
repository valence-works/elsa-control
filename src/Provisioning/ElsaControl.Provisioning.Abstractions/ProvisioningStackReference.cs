namespace ElsaControl.Provisioning.Abstractions;

/// <summary>
/// Fully qualified stack names for self-managed backends. DIY (Blob or file)
/// backends resolve <c>StackReference</c> as
/// <c>{organization}/{project}/{stack}</c>.
/// </summary>
public static class ProvisioningStackReference
{
    public const string DefaultOrganization = "organization";

    public static string Platform(string environmentName, string? organization = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentName);
        return $"{Organization(organization)}/{ProvisioningProjects.Platform}/{environmentName.Trim()}";
    }

    public static string Engine(string stackName, string? organization = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stackName);
        return $"{Organization(organization)}/{ProvisioningProjects.Engine}/{stackName.Trim()}";
    }

    public static string Organization(string? organization) =>
        string.IsNullOrWhiteSpace(organization) ? DefaultOrganization : organization.Trim();
}
