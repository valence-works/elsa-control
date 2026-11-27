namespace ElsaControl.Provisioning.Core;

/// <summary>
/// Owned engine Container App identity. The program, health probe, and
/// traffic observation must resolve the same name.
/// </summary>
public static class ProvisioningEngineContainerApp
{
    public const string Prefix = "ca-eng-";
    public const int MaximumLength = 32;

    public static string ForInstance(Guid instanceId)
    {
        if (instanceId == Guid.Empty)
            throw new ArgumentException("Engine container apps require a non-empty instance ID.", nameof(instanceId));
        var name = Prefix + instanceId.ToString("N");
        return name.Length <= MaximumLength ? name : name[..MaximumLength];
    }
}
