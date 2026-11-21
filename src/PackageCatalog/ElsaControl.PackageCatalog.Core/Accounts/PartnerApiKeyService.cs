using System.Security.Cryptography;

namespace ElsaControl.PackageCatalog.Core.Accounts;

public sealed class PartnerApiKeyService(
    IPartnerApiKeyStore store,
    IPartnerApiKeyCache cache,
    IPartnerMintedTokenRevoker mintedTokenRevoker)
{
    private static readonly byte[] DummyStoredHash = SHA256.HashData("partner-api-key-unknown"u8.ToArray());
    private static readonly byte[] DummySuppliedHash = SHA256.HashData("partner-api-key-unparsed"u8.ToArray());

    public Task<PartnerApiKeyCreateResult> CreateAsync(
        CreatePartnerApiKeyRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        store.CreateAsync(request, now, cancellationToken);

    public Task<IReadOnlyList<PartnerApiKeyRecord>> ListAsync(
        Guid organizationId,
        CancellationToken cancellationToken = default) =>
        store.ListByOrganizationAsync(organizationId, cancellationToken);

    public async Task<PartnerApiKeyRevokeResult> RevokeAsync(
        Guid organizationId,
        string keyId,
        Guid? actorAccountId,
        string? operatorSubject,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var result = await store.RevokeAsync(
            organizationId,
            keyId,
            actorAccountId,
            operatorSubject,
            now,
            cancellationToken);
        if (!result.Succeeded)
            return result;

        await cache.InvalidateAsync(result.Record!.KeyId, cancellationToken);
        await mintedTokenRevoker.RevokeTokensMintedByKeyAsync(result.Record.KeyId, cancellationToken);
        return result;
    }

    public async Task<PartnerApiKeyAuthentication?> AuthenticateAsync(
        string? bearerToken,
        string expectedEnvironment,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var parsed = PartnerApiKeyFormat.TryParse(bearerToken, out var credential);
        var environmentMatch = parsed &&
                               string.Equals(credential.Environment, expectedEnvironment, StringComparison.Ordinal);
        var keyId = parsed ? credential.KeyId : "";
        var secret = parsed ? credential.Secret : "";

        PartnerApiKeyRecord? record = null;
        if (parsed && environmentMatch)
            record = await store.FindByKeyIdAsync(keyId, cancellationToken);

        var storedHash = TryDecodeHash(record?.SecretHash, out var decoded)
            ? decoded
            : DummyStoredHash;
        var suppliedHash = parsed ? PartnerApiKeyFormat.HashSecretBytes(secret) : DummySuppliedHash;
        var hashMatch = CryptographicOperations.FixedTimeEquals(storedHash, suppliedHash);
        if (!environmentMatch || !hashMatch || record is null || record.StateAt(now) != PartnerApiKeyState.Active)
            return null;

        if (record.LastUsedAt is null || now - record.LastUsedAt.Value >= TimeSpan.FromSeconds(30))
            await store.TouchLastUsedAsync(record.KeyId, now, cancellationToken);

        return new PartnerApiKeyAuthentication(record, record.Scopes);
    }

    private static bool TryDecodeHash(string? hash, out byte[] bytes)
    {
        bytes = DummyStoredHash;
        if (hash is null || hash.Length != PartnerApiKeyFormat.SecretHashLength)
            return false;

        try
        {
            bytes = Convert.FromHexString(hash);
            return bytes.Length == 32;
        }
        catch (FormatException)
        {
            bytes = DummyStoredHash;
            return false;
        }
    }
}
