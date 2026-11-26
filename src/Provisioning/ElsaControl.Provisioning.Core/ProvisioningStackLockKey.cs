using ElsaControl.Provisioning.Abstractions;

namespace ElsaControl.Provisioning.Core;

/// <summary>
/// Lock and stack identity key. Pulumi scopes stacks per project, so the key
/// is <c>project/stack</c>, never the stack name alone.
/// </summary>
public static class ProvisioningStackLockKey
{
    public static string Format(ProvisioningStackKind kind, string stackName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stackName);
        return $"{ProvisioningProjects.For(kind)}/{stackName.Trim()}";
    }

    public static string Format(string projectName, string stackName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectName);
        ArgumentException.ThrowIfNullOrWhiteSpace(stackName);
        return $"{projectName.Trim()}/{stackName.Trim()}";
    }
}
