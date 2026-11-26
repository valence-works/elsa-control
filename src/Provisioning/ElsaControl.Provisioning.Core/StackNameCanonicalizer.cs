using System.Text;
using ElsaControl.Provisioning.Abstractions;

namespace ElsaControl.Provisioning.Core;

/// <summary>
/// Pulumi stack names allow [A-Za-z0-9-_]. Other characters collapse to a hyphen.
/// </summary>
public static class StackNameCanonicalizer
{
    public static StackIdentity Canonicalize(StackIdentity identity) =>
        new(identity.Project, Canonicalize(identity.Name));

    public static string Canonicalize(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var builder = new StringBuilder(name.Length);
        var lastHyphen = false;
        foreach (var ch in name.Trim())
        {
            if (char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-')
            {
                builder.Append(ch);
                lastHyphen = ch == '-';
                continue;
            }

            if (!lastHyphen && builder.Length > 0)
            {
                builder.Append('-');
                lastHyphen = true;
            }
        }

        var canonical = builder.ToString().Trim('-');
        if (canonical.Length == 0)
            throw new ArgumentException("Stack name canonicalises to empty.", nameof(name));
        return canonical;
    }
}
