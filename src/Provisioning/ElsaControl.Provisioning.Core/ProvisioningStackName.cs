using System.Text;

namespace ElsaControl.Provisioning.Core;

/// <summary>
/// Canonicalises stack names to Pulumi's allowed charset: letters, digits,
/// hyphens, underscores, and periods.
/// </summary>
public static class ProvisioningStackName
{
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
