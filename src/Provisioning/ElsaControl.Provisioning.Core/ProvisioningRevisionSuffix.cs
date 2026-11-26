namespace ElsaControl.Provisioning.Core;

/// <summary>
/// Container Apps revision suffixes must start with a letter. Fingerprints are
/// hex and may start with a digit, so generated suffixes are prefixed.
/// </summary>
public static class ProvisioningRevisionSuffix
{
    public const string Prefix = "r";
    public const int FingerprintLength = 23;

    public static string FromFingerprint(string fingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        var hex = fingerprint.Trim().ToLowerInvariant();
        if (hex.Length != 64 || !hex.All(Uri.IsHexDigit))
            throw new ArgumentException("Plan fingerprint is invalid.", nameof(fingerprint));
        return Prefix + hex[..FingerprintLength];
    }

    public static void Validate(string suffix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(suffix);
        var value = suffix.Trim();
        if (value.Length is < 2 or > 64
            || !char.IsAsciiLetter(value[0])
            || value.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-')
            || value[^1] == '-')
        {
            throw new InvalidOperationException("Revision suffix is invalid.");
        }
    }
}
