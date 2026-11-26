namespace ElsaControl.Provisioning.Core;

/// <summary>
/// Owned engine resource-group identity. The program, mapper, and destroy
/// probe must resolve the same name.
/// </summary>
public static class ProvisioningEngineResourceGroup
{
    public const string Prefix = "elsa-engine";

    public static string ForInstance(Guid instanceId)
    {
        if (instanceId == Guid.Empty)
            throw new ArgumentException("Engine resource groups require a non-empty instance ID.", nameof(instanceId));
        return $"{Prefix}-{instanceId:N}";
    }

    public static string Resolve(string stackName, IReadOnlyDictionary<string, string> stackConfig)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stackName);
        ArgumentNullException.ThrowIfNull(stackConfig);
        if (stackConfig.TryGetValue("resourceGroupName", out var configured)
            && !string.IsNullOrWhiteSpace(configured))
        {
            return configured.Trim();
        }

        if (stackConfig.TryGetValue("instanceId", out var instance)
            && Guid.TryParse(instance, out var parsed)
            && parsed != Guid.Empty)
        {
            return ForInstance(parsed);
        }

        if (ProvisioningStackName.TryParseEngineInstanceId(stackName, out var fromStack))
            return ForInstance(fromStack);

        throw new InvalidOperationException("Owned resource group name could not be resolved.");
    }
}
