namespace ElsaControl.Provisioning.Abstractions;

/// <summary>
/// Pulumi project names. A project is the program; stacks are independently
/// configured runs. Engine stacks are never named from org or slug.
/// </summary>
public static class ProvisioningProjects
{
    public const string Platform = "elsa-platform";
    public const string Engine = "elsa-engine";

    public static string For(ProvisioningStackKind kind) =>
        kind switch
        {
            ProvisioningStackKind.Platform => Platform,
            ProvisioningStackKind.Engine => Engine,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
}
