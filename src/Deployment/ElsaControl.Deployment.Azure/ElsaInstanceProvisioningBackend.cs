namespace ElsaControl.Deployment.Azure;

/// <summary>
/// Durable owner of an engine's realization backend. Pinned at the first
/// admitted Create and never changed for the instance's life.
/// </summary>
public enum ElsaInstanceProvisioningBackend
{
    Bicep = 0,
    Pulumi = 1
}

public static class ElsaInstanceProvisioningBackendText
{
    public const string Bicep = "bicep";
    public const string Pulumi = "pulumi";
    public const int MaxLength = 16;

    public static string Format(ElsaInstanceProvisioningBackend value) =>
        value == ElsaInstanceProvisioningBackend.Pulumi ? Pulumi : Bicep;

    public static ElsaInstanceProvisioningBackend Parse(string? value) =>
        string.Equals(value, Pulumi, StringComparison.OrdinalIgnoreCase)
            ? ElsaInstanceProvisioningBackend.Pulumi
            : ElsaInstanceProvisioningBackend.Bicep;
}
