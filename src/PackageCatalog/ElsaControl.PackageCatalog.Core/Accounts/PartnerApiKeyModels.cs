using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace ElsaControl.PackageCatalog.Core.Accounts;

public static class PartnerApiKeyFormat
{
    public const string Prefix = "ecp";
    public const int KeyIdByteCount = 16;
    public const int SecretByteCount = 32;
    public const int KeyIdLength = KeyIdByteCount * 2;
    public const int SecretHashLength = 64;
    public const int MaxNameLength = 128;
    public const int MaxActiveKeysPerPartner = 2;

    public static string HashSecret(ReadOnlySpan<char> secret)
    {
        Span<byte> utf8 = stackalloc byte[Encoding.UTF8.GetMaxByteCount(secret.Length)];
        var written = Encoding.UTF8.GetBytes(secret, utf8);
        return Convert.ToHexStringLower(SHA256.HashData(utf8[..written]));
    }

    public static byte[] HashSecretBytes(ReadOnlySpan<char> secret)
    {
        Span<byte> utf8 = stackalloc byte[Encoding.UTF8.GetMaxByteCount(secret.Length)];
        var written = Encoding.UTF8.GetBytes(secret, utf8);
        return SHA256.HashData(utf8[..written]);
    }

    public static bool TryParse(string? value, out PartnerApiKeyCredential credential)
    {
        credential = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var token = value.Trim();
        var first = token.IndexOf('_');
        if (first <= 0)
            return false;
        var second = token.IndexOf('_', first + 1);
        if (second <= first + 1)
            return false;
        var third = token.IndexOf('_', second + 1);
        if (third <= second + 1 || third == token.Length - 1)
            return false;

        var prefix = token[..first];
        var environment = token[(first + 1)..second];
        var keyId = token[(second + 1)..third];
        var secret = token[(third + 1)..];
        if (!string.Equals(prefix, Prefix, StringComparison.Ordinal) ||
            !IsEnvironment(environment) ||
            !IsKeyId(keyId) ||
            !IsSecret(secret))
            return false;

        credential = new PartnerApiKeyCredential(environment, keyId, secret);
        return true;
    }

    public static PartnerApiKeyCredential Issue(string environment)
    {
        if (!IsEnvironment(environment))
            throw new ArgumentException("Partner API key environment is invalid.", nameof(environment));

        var keyId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(KeyIdByteCount));
        var secret = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(SecretByteCount));
        return new PartnerApiKeyCredential(environment, keyId, secret);
    }

    public static bool IsEnvironment(string? value) =>
        !string.IsNullOrEmpty(value) &&
        value.Length is >= 2 and <= 16 &&
        value.All(character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character));

    public static bool IsKeyId(string? value)
    {
        if (value is null || value.Length != KeyIdLength)
            return false;

        foreach (var character in value)
        {
            if (character is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
                return false;
        }

        return true;
    }

    private static bool IsSecret(string secret)
    {
        if (secret.Length is < 40 or > 64)
            return false;

        foreach (var character in secret)
        {
            if (char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
                continue;
            return false;
        }

        return true;
    }
}

public readonly record struct PartnerApiKeyCredential(string Environment, string KeyId, string Secret)
{
    public string Token => $"{PartnerApiKeyFormat.Prefix}_{Environment}_{KeyId}_{Secret}";

    public override string ToString() => $"{PartnerApiKeyFormat.Prefix}_{Environment}_{KeyId}";
}

public static class PartnerApiKeyScopes
{
    public const string EnginesRead = "engines:read";
    public const string EnginesWrite = "engines:write";
    public const string EnginesDelete = "engines:delete";
    public const string ReleasesApply = "releases:apply";
    public const string StudioSessions = "studio:sessions";
    public const string WebhooksManage = "webhooks:manage";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        EnginesRead,
        EnginesWrite,
        EnginesDelete,
        ReleasesApply,
        StudioSessions,
        WebhooksManage
    };

    public static bool TryNormalize(IReadOnlyList<string>? scopes, out IReadOnlyList<string> normalized, out string? error)
    {
        normalized = [];
        error = null;
        if (scopes is null || scopes.Count == 0)
        {
            error = "At least one scope is required.";
            return false;
        }

        var unique = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var scope in scopes)
        {
            var value = scope?.Trim();
            if (string.IsNullOrEmpty(value) || !All.Contains(value))
            {
                error = "One or more scopes are not allowed.";
                return false;
            }

            unique.Add(value);
        }

        normalized = unique.ToArray();
        return true;
    }
}

public enum PartnerApiKeyState
{
    Active,
    Expired,
    Revoked
}

public sealed class PartnerApiKey
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string KeyId { get; set; } = "";
    public Guid OrganizationId { get; set; }
    public Organization? Organization { get; set; }
    public Guid WorkspaceId { get; set; }
    public Workspace? Workspace { get; set; }
    public Guid ProvisionerAccountId { get; set; }
    public Account? ProvisionerAccount { get; set; }
    public string SecretHash { get; set; } = "";
    public string? Name { get; set; }
    public List<string> Scopes { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedByAccountId { get; set; }
    public string? CreatedByOperatorSubject { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public Guid? RevokedByAccountId { get; set; }
    public string? RevokedByOperatorSubject { get; set; }

    public PartnerApiKeyState StateAt(DateTimeOffset now)
    {
        if (RevokedAt.HasValue)
            return PartnerApiKeyState.Revoked;
        if (ExpiresAt.HasValue && ExpiresAt.Value <= now)
            return PartnerApiKeyState.Expired;
        return PartnerApiKeyState.Active;
    }
}

public sealed record PartnerApiKeyRecord(
    string KeyId,
    Guid OrganizationId,
    Guid WorkspaceId,
    Guid ProvisionerAccountId,
    string SecretHash,
    string? Name,
    IReadOnlyList<string> Scopes,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? RevokedAt)
{
    public PartnerApiKeyState StateAt(DateTimeOffset now)
    {
        if (RevokedAt.HasValue)
            return PartnerApiKeyState.Revoked;
        if (ExpiresAt.HasValue && ExpiresAt.Value <= now)
            return PartnerApiKeyState.Expired;
        return PartnerApiKeyState.Active;
    }
}

public sealed record PartnerApiKeyCacheEntry(
    string KeyId,
    Guid OrganizationId,
    Guid WorkspaceId,
    Guid ProvisionerAccountId,
    string SecretHash,
    IReadOnlyList<string> Scopes,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? RevokedAt);

public sealed record CreatePartnerApiKeyRequest(
    Guid OrganizationId,
    string Environment,
    string? Name,
    IReadOnlyList<string> Scopes,
    DateTimeOffset? ExpiresAt,
    Guid? ActorAccountId,
    string? OperatorSubject);

public sealed record CreatedPartnerApiKey(
    PartnerApiKeyRecord Record,
    string Token);

public enum PartnerApiKeyCreateFailure
{
    OrganizationNotFound,
    InvalidEnvironment,
    InvalidName,
    InvalidScopes,
    InvalidExpiry,
    ActiveKeyLimitReached
}

public sealed record PartnerApiKeyCreateResult(
    CreatedPartnerApiKey? Created,
    PartnerApiKeyCreateFailure? Failure)
{
    public bool Succeeded => Created is not null && Failure is null;

    public static PartnerApiKeyCreateResult Success(CreatedPartnerApiKey created) => new(created, null);

    public static PartnerApiKeyCreateResult Denied(PartnerApiKeyCreateFailure failure) => new(null, failure);
}

public enum PartnerApiKeyRevokeFailure
{
    OrganizationNotFound,
    KeyNotFound,
    AlreadyRevoked
}

public sealed record PartnerApiKeyRevokeResult(PartnerApiKeyRecord? Record, PartnerApiKeyRevokeFailure? Failure)
{
    public bool Succeeded => Record is not null && Failure is null;

    public static PartnerApiKeyRevokeResult Success(PartnerApiKeyRecord record) => new(record, null);

    public static PartnerApiKeyRevokeResult Denied(PartnerApiKeyRevokeFailure failure) => new(null, failure);
}

public sealed record PartnerApiKeyAuthentication(
    PartnerApiKeyRecord Record,
    IReadOnlyList<string> Scopes);

public interface IPartnerApiKeyCache
{
    static readonly TimeSpan ValidEntryLifetime = TimeSpan.FromSeconds(30);

    Task<PartnerApiKeyCacheEntry?> GetAsync(string keyId, CancellationToken cancellationToken = default);

    Task SetAsync(PartnerApiKeyCacheEntry entry, CancellationToken cancellationToken = default);

    Task InvalidateAsync(string keyId, CancellationToken cancellationToken = default);
}

public interface IPartnerMintedTokenRevoker
{
    Task RevokeTokensMintedByKeyAsync(string keyId, CancellationToken cancellationToken = default);
}

public sealed class NoOpPartnerMintedTokenRevoker : IPartnerMintedTokenRevoker
{
    public Task RevokeTokensMintedByKeyAsync(string keyId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

public interface IPartnerApiKeyStore
{
    Task<PartnerApiKeyRecord?> FindByKeyIdAsync(string keyId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PartnerApiKeyRecord>> ListByOrganizationAsync(
        Guid organizationId,
        CancellationToken cancellationToken = default);

    Task<PartnerApiKeyCreateResult> CreateAsync(
        CreatePartnerApiKeyRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<PartnerApiKeyRevokeResult> RevokeAsync(
        Guid organizationId,
        string keyId,
        Guid? actorAccountId,
        string? operatorSubject,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task TouchLastUsedAsync(string keyId, DateTimeOffset now, CancellationToken cancellationToken = default);
}
