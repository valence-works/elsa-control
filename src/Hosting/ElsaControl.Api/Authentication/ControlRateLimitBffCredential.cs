using System.Security.Cryptography;
using System.Text;
using ElsaControl.Api.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace ElsaControl.Api.Authentication;

public static class ControlRateLimitBffCredentialSources
{
    public const string Cloud = "cloud";
    public const string Hub = "hub";

    internal static string? Normalize(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        Cloud => Cloud,
        Hub => Hub,
        _ => null
    };
}

/// <summary>
/// Per-BFF credentials for <c>RateLimiting:BffCredentials</c>. The Cloud and Hub BFFs send their
/// secret in <see cref="HeaderName"/>. A value that matches a source only selects that source's
/// <c>BffPerIp</c> window, and only for a validated session of the same type. It grants no access,
/// adds no claims and changes no endpoint result. When <see cref="Enabled"/> is false the section
/// is ignored and the header has no effect.
/// </summary>
public sealed class ControlRateLimitBffCredentialOptions
{
    public const string ConfigurationSection = "RateLimiting:BffCredentials";
    public const string HeaderName = "X-Elsa-Bff-Credential";
    public const int MinSecretLength = 32;

    public bool Enabled { get; set; }

    public List<ControlRateLimitBffCredentialSourceOptions> Sources { get; set; } = [];

    // Messages name the configuration key only. A configured value never appears in them.
    internal IEnumerable<string> Validate()
    {
        if (!Enabled)
            yield break;

        if (Sources.Count == 0)
        {
            yield return $"{ConfigurationSection}:Sources must list at least one source when {ConfigurationSection}:Enabled is true.";
            yield break;
        }

        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var values = new Dictionary<string, (int SourceIndex, string Key)>(StringComparer.Ordinal);
        for (var i = 0; i < Sources.Count; i++)
        {
            var source = Sources[i];
            var prefix = $"{ConfigurationSection}:Sources:{i}";
            var name = ControlRateLimitBffCredentialSources.Normalize(source.Name);
            if (name is null)
                yield return
                    $"{prefix}:Name must be '{ControlRateLimitBffCredentialSources.Cloud}' or '{ControlRateLimitBffCredentialSources.Hub}'.";
            else if (!names.TryAdd(name, prefix))
                yield return $"{prefix}:Name repeats source '{name}', already set at {names[name]}:Name.";

            var secretKey = $"{prefix}:Secret";
            foreach (var error in ValidateValue(source.Secret, secretKey))
                yield return error;

            var previousKey = $"{prefix}:PreviousSecret";
            if (!string.IsNullOrEmpty(source.PreviousSecret))
            {
                foreach (var error in ValidateValue(source.PreviousSecret, previousKey))
                    yield return error;
                if (string.Equals(source.PreviousSecret, source.Secret, StringComparison.Ordinal))
                    yield return $"{previousKey} must differ from {secretKey}.";
            }

            foreach (var (value, key) in new[] { (source.Secret, secretKey), (source.PreviousSecret, previousKey) })
            {
                if (string.IsNullOrEmpty(value))
                    continue;
                if (values.TryGetValue(value, out var existing))
                {
                    if (existing.SourceIndex != i)
                        yield return $"{key} shares its value with {existing.Key}; each source needs its own values.";
                }
                else
                {
                    values[value] = (i, key);
                }
            }
        }
    }

    private static IEnumerable<string> ValidateValue(string? value, string key)
    {
        if (string.IsNullOrEmpty(value))
        {
            yield return $"{key} is required.";
            yield break;
        }

        if (value.TrimStart().StartsWith(UnresolvedKeyVaultReferenceGuard.ReferencePrefix, StringComparison.Ordinal))
        {
            yield return $"{key} still holds an unresolved Key Vault reference.";
            yield break;
        }

        if (value.Length < MinSecretLength)
            yield return $"{key} must be at least {MinSecretLength} characters.";
        if (value.Any(char.IsWhiteSpace))
            yield return $"{key} must not contain whitespace.";
    }
}

public sealed class ControlRateLimitBffCredentialSourceOptions
{
    public string? Name { get; set; }
    public string? Secret { get; set; }
    public string? PreviousSecret { get; set; }
}

/// <summary>
/// Matches a presented <see cref="ControlRateLimitBffCredentialOptions.HeaderName"/> value to a
/// configured source. The presented value and every configured <c>Secret</c> and
/// <c>PreviousSecret</c> are hashed with SHA-256 and compared with
/// <see cref="CryptographicOperations.FixedTimeEquals"/>. Every configured value is compared, with
/// no early return, so the time taken does not depend on which value matched.
/// </summary>
internal sealed class ControlRateLimitBffCredentialMatcher
{
    private readonly (string Source, byte[] Hash)[] _entries;

    public ControlRateLimitBffCredentialMatcher(ControlRateLimitBffCredentialOptions? options)
    {
        var entries = new List<(string Source, byte[] Hash)>();
        if (options is { Enabled: true })
        {
            foreach (var source in options.Sources)
            {
                var name = ControlRateLimitBffCredentialSources.Normalize(source.Name);
                if (name is null)
                    continue;
                foreach (var value in new[] { source.Secret, source.PreviousSecret })
                {
                    if (!string.IsNullOrEmpty(value))
                        entries.Add((name, Hash(value)));
                }
            }
        }

        _entries = entries.ToArray();
    }

    /// <summary>Returns the matched source name, or <see langword="null"/> for a missing, repeated or unknown value.</summary>
    public string? Match(StringValues presented)
    {
        if (_entries.Length == 0 || presented.Count != 1 || string.IsNullOrEmpty(presented[0]))
            return null;

        var hash = Hash(presented[0]!);
        string? matched = null;
        foreach (var entry in _entries)
        {
            var equal = CryptographicOperations.FixedTimeEquals(hash, entry.Hash);
            matched = equal && matched is null ? entry.Source : matched;
        }

        return matched;
    }

    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
}

public sealed class ControlRateLimitBffCredentialConfigurationValidator(
    IOptions<ControlRateLimitBffCredentialOptions> options) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var errors = options.Value.Validate().ToArray();
        if (errors.Length > 0)
            throw new InvalidOperationException(
                $"Rate limiting BFF credential configuration is invalid: {string.Join(" ", errors)}");

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
