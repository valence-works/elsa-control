namespace ElsaControl.Provisioning.Abstractions;

/// <summary>
/// Canonical Pulumi project names. A project is one C# program; stacks are
/// independently configured runs of that program. There is never a project per engine.
/// </summary>
public static class ProvisioningProjects
{
    public const string Platform = "elsa-platform";
    public const string Engine = "elsa-engine";
}
