namespace ElsaControl.SharedShellAc1.VerifiedPackage;

/// <summary>
/// Stand-in for a Valence-verified activity package that Shared may load into a collectible
/// AssemblyLoadContext. The types are activities only: no IShellFeature, no hosted services,
/// no EF, no mutable statics (Architect Q2 / ADR-0009 Amendment 1).
/// </summary>
public interface IVerifiedActivity
{
    string TypeName { get; }
    string Execute(string input);
}

public static class VerifiedPackageManifest
{
    public const string PackageId = "Elsa.Integrations.Verified.Echo";
    public const string Version = "1.0.0";
    public const string RevisionHash = "ac1-verified-echo-v1";

    public static readonly string[] ActivityTypeNames =
    [
        "Verified.Echo",
        "Verified.Upper",
        "Verified.Checksum",
        "Verified.Pad",
        "Verified.Repeat",
        "Verified.Trim",
        "Verified.Reverse",
        "Verified.Count",
        "Verified.Wrap",
        "Verified.Join",
        "Verified.HashFold",
        "Verified.Ready",
    ];
}

public sealed class EchoActivity : IVerifiedActivity
{
    public string TypeName => "Verified.Echo";
    public string Execute(string input) => input ?? string.Empty;
}

public sealed class UpperActivity : IVerifiedActivity
{
    public string TypeName => "Verified.Upper";
    public string Execute(string input) => (input ?? string.Empty).ToUpperInvariant();
}

public sealed class ChecksumActivity : IVerifiedActivity
{
    public string TypeName => "Verified.Checksum";
    public string Execute(string input)
    {
        input ??= string.Empty;
        var hash = 2166136261;
        foreach (var ch in input)
            hash = (hash ^ ch) * 16777619;
        return hash.ToString("x8");
    }
}

public sealed class PadActivity : IVerifiedActivity
{
    public string TypeName => "Verified.Pad";
    public string Execute(string input) => (input ?? string.Empty).PadRight(32, '.');
}

public sealed class RepeatActivity : IVerifiedActivity
{
    public string TypeName => "Verified.Repeat";
    public string Execute(string input)
    {
        input ??= string.Empty;
        return string.Concat(input, input);
    }
}

public sealed class TrimActivity : IVerifiedActivity
{
    public string TypeName => "Verified.Trim";
    public string Execute(string input) => (input ?? string.Empty).Trim();
}

public sealed class ReverseActivity : IVerifiedActivity
{
    public string TypeName => "Verified.Reverse";
    public string Execute(string input)
    {
        input ??= string.Empty;
        var chars = input.ToCharArray();
        Array.Reverse(chars);
        return new string(chars);
    }
}

public sealed class CountActivity : IVerifiedActivity
{
    public string TypeName => "Verified.Count";
    public string Execute(string input) => (input ?? string.Empty).Length.ToString();
}

public sealed class WrapActivity : IVerifiedActivity
{
    public string TypeName => "Verified.Wrap";
    public string Execute(string input) => $"[{input}]";
}

public sealed class JoinActivity : IVerifiedActivity
{
    public string TypeName => "Verified.Join";
    public string Execute(string input) => string.Join('-', (input ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries));
}

public sealed class HashFoldActivity : IVerifiedActivity
{
    public string TypeName => "Verified.HashFold";
    public string Execute(string input)
    {
        input ??= string.Empty;
        var acc = 0L;
        for (var i = 0; i < input.Length; i++)
            acc = (acc * 33) + input[i] + i;
        return acc.ToString();
    }
}

public sealed class ReadyActivity : IVerifiedActivity
{
    public string TypeName => "Verified.Ready";
    public string Execute(string input) => $"{input}:ready";
}
