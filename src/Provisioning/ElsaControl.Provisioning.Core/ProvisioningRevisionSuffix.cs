namespace ElsaControl.Provisioning.Core;

/// <summary>
/// Container Apps revision suffixes must start with a letter. Fingerprints are
/// hex and may start with a digit, so generated suffixes are prefixed.
/// Azure rejects a revision name longer than 54 characters
/// (<c>ContainerAppInvalidRevisionName</c>). The name is
/// <c>{app}--{suffix}</c>, and the engine app name is always 32 characters, so
/// the suffix itself may be at most 20.
/// </summary>
public static class ProvisioningRevisionSuffix
{
    public const string Prefix = "r";
    public const string Separator = "--";

    /// <summary>Azure limit on <c>{app}--{suffix}</c>.</summary>
    public const int MaximumRevisionNameLength = 54;

    /// <summary>
    /// Longest suffix that still fits with a maximum-length engine app name
    /// (54 - 32 - "--".Length).
    /// </summary>
    public const int MaximumLength =
        MaximumRevisionNameLength - ProvisioningEngineContainerApp.MaximumLength - 2;

    public const int FingerprintLength = MaximumLength - 1;

    public static string FromFingerprint(string fingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        var hex = fingerprint.Trim().ToLowerInvariant();
        if (hex.Length != 64 || !hex.All(Uri.IsHexDigit))
            throw new ArgumentException("Plan fingerprint is invalid.", nameof(fingerprint));
        var suffix = Prefix + hex[..FingerprintLength];
        Validate(suffix);
        return suffix;
    }

    public static string RevisionName(string appName, string suffix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appName);
        Validate(suffix);
        var name = appName.Trim() + Separator + suffix.Trim();
        if (name.Length > MaximumRevisionNameLength)
            throw new InvalidOperationException("Revision name exceeds the Azure 54-character limit.");
        return name;
    }

    public static void Validate(string suffix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(suffix);
        var value = suffix.Trim();
        if (value.Length is < 2 || value.Length > MaximumLength
            || !char.IsAsciiLetter(value[0])
            || value.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-')
            || value[^1] == '-')
        {
            throw new InvalidOperationException("Revision suffix is invalid.");
        }
    }
}
