using System.Text;
using ElsaControl.Provisioning.Abstractions;

namespace ElsaControl.Provisioning.Core;

/// <summary>
/// Stack names. Engine stacks derive only from the immutable managed-instance
/// ID. Org and slug are never part of the name.
/// </summary>
public static class ProvisioningStackName
{
    public const string EnginePrefix = "engine-";

    public static string ForEngine(Guid instanceId)
    {
        if (instanceId == Guid.Empty)
            throw new ArgumentException("Engine stack names require a non-empty instance ID.", nameof(instanceId));
        return EnginePrefix + instanceId.ToString("N").ToLowerInvariant();
    }

    public static string ForPlatform(string environmentName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentName);
        return Canonicalize(environmentName).ToLowerInvariant();
    }

    public static string Canonicalize(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var builder = new StringBuilder(value.Length);
        var lastWasSeparator = true;
        foreach (var ch in value.Trim())
        {
            if (char.IsAsciiLetterOrDigit(ch) || ch is '_' or '.')
            {
                builder.Append(ch);
                lastWasSeparator = false;
                continue;
            }

            if (lastWasSeparator)
                continue;
            builder.Append('-');
            lastWasSeparator = true;
        }

        var canonical = builder.ToString().Trim('-');
        if (canonical.Length == 0)
            throw new ArgumentException("Stack name contains no Pulumi-allowed characters.", nameof(value));
        return canonical;
    }
}
