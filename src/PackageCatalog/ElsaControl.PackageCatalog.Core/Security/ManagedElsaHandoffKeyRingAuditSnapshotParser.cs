namespace ElsaControl.PackageCatalog.Core.Security;

/// <summary>
/// A trusted-key snapshot reconstructed from safe key-ring audit actions.
/// </summary>
public sealed record ManagedElsaHandoffKeyRingAuditSnapshot(
    string? ActiveKeyId,
    IReadOnlyList<string> TrustedKeyIds);

/// <summary>
/// Validates and parses persisted key-ring audit actions consistently across
/// the hosting and persistence layers. Audit rows remain observational: callers
/// must use configured keys as their trust authority.
/// </summary>
public static class ManagedElsaHandoffKeyRingAuditSnapshotParser
{
    public const int MaximumKeyIdLength = 64;

    public static bool IsAllowedKeyId(string? keyId)
    {
        if (keyId is not { Length: >= 1 and <= MaximumKeyIdLength })
            return false;
        if (keyId.Contains("__", StringComparison.Ordinal))
            return false;
        if (!IsAlphaNumeric(keyId[0]))
            return false;
        if (keyId.Length > 1 && !IsAlphaNumeric(keyId[^1]))
            return false;
        for (var index = 1; index < keyId.Length - 1; index++)
        {
            if (!IsKeyIdBodyChar(keyId[index]))
                return false;
        }

        return true;
    }

    public static ManagedElsaHandoffKeyRingAuditSnapshot Parse(
        IEnumerable<string> actions,
        string activePrefix,
        string previousPrefix)
    {
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentException.ThrowIfNullOrWhiteSpace(activePrefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(previousPrefix);

        string? activeKeyId = null;
        var trusted = new List<string>();
        foreach (var action in actions)
        {
            if (TryReadKeyId(action, activePrefix, out var activeKeyIdFromAction))
            {
                activeKeyId = activeKeyIdFromAction;
                AddDistinct(trusted, activeKeyIdFromAction);
            }
            else if (TryReadKeyId(action, previousPrefix, out var previousKeyIdFromAction))
            {
                AddDistinct(trusted, previousKeyIdFromAction);
            }
        }

        return new ManagedElsaHandoffKeyRingAuditSnapshot(activeKeyId, trusted);
    }

    private static bool TryReadKeyId(string action, string prefix, out string keyId)
    {
        keyId = "";
        if (action is null || !action.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        var candidate = action[prefix.Length..];
        if (!IsAllowedKeyId(candidate))
            return false;

        keyId = candidate;
        return true;
    }

    private static void AddDistinct(List<string> trustedKeyIds, string keyId)
    {
        if (!trustedKeyIds.Contains(keyId, StringComparer.Ordinal))
            trustedKeyIds.Add(keyId);
    }

    private static bool IsAlphaNumeric(char value) =>
        value is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9');

    private static bool IsKeyIdBodyChar(char value) =>
        IsAlphaNumeric(value) || value is '.' or '_' or '-';
}
