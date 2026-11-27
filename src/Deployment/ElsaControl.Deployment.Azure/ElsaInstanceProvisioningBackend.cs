namespace ElsaControl.Deployment.Azure;

/// <summary>
/// Durable owner of an engine's realization backend. Pinned at the first
/// admitted Create and never changed for the instance's life.
/// </summary>
public enum ElsaInstanceProvisioningBackend
{
    Bicep = 0,
    Pulumi = 1,
    Unknown = 2
}

public static class ElsaInstanceProvisioningBackendText
{
    public const string Bicep = "bicep";
    public const string Pulumi = "pulumi";
    public const int MaxLength = 16;

    public static string Format(ElsaInstanceProvisioningBackend value) => value switch
    {
        ElsaInstanceProvisioningBackend.Pulumi => Pulumi,
        ElsaInstanceProvisioningBackend.Bicep => Bicep,
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    public static bool TryParse(string? value, out ElsaInstanceProvisioningBackend backend)
    {
        if (string.IsNullOrWhiteSpace(value)
            || string.Equals(value, Bicep, StringComparison.OrdinalIgnoreCase))
        {
            backend = ElsaInstanceProvisioningBackend.Bicep;
            return true;
        }

        if (string.Equals(value, Pulumi, StringComparison.OrdinalIgnoreCase))
        {
            backend = ElsaInstanceProvisioningBackend.Pulumi;
            return true;
        }

        backend = ElsaInstanceProvisioningBackend.Unknown;
        return false;
    }

    public static ElsaInstanceProvisioningBackend Parse(string? value) =>
        TryParse(value, out var backend) ? backend : ElsaInstanceProvisioningBackend.Unknown;
}
