using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace ElsaControl.Deployment.Core.ExternalConnections;

public static class ExternalEngineEnrollmentProtocol
{
    public const string RedemptionDomain = "elsa-control.external-engine-enrollment.redeem.v1";
    public const string ConnectorProofDomain = "elsa-control.external-engine-connector.proof.v1";

    public static string ExportPublicKey(ECDsa key)
    {
        ArgumentNullException.ThrowIfNull(key);
        EnsureP256(key);
        return Base64UrlEncode(key.ExportSubjectPublicKeyInfo());
    }

    public static string PublicKeyThumbprint(string publicKey)
    {
        var keyBytes = ImportPublicKeyBytes(publicKey);
        return Base64UrlEncode(SHA256.HashData(keyBytes));
    }

    public static string HashChallenge(string challenge)
    {
        if (!TryDecodeBounded(challenge, 32, 32, out var challengeBytes))
            throw new ArgumentException("Enrollment challenge is invalid.", nameof(challenge));

        return Base64UrlEncode(SHA256.HashData(challengeBytes));
    }

    public static byte[] CreateRedemptionPayload(
        Guid challengeId,
        Guid organizationId,
        Guid workspaceId,
        Guid connectionId,
        string purpose,
        string audience,
        string challengeHash,
        string publicKeyThumbprint) =>
        EncodeCanonical(
            RedemptionDomain,
            RequiredGuid(challengeId, nameof(challengeId)),
            RequiredGuid(organizationId, nameof(organizationId)),
            RequiredGuid(workspaceId, nameof(workspaceId)),
            RequiredGuid(connectionId, nameof(connectionId)),
            RequiredText(purpose, 128, nameof(purpose)),
            RequiredText(audience, 512, nameof(audience)),
            RequiredBase64Url(challengeHash, 32, nameof(challengeHash)),
            RequiredBase64Url(publicKeyThumbprint, 32, nameof(publicKeyThumbprint)));

    public static byte[] CreateConnectorProofPayload(ExternalEngineConnectorProof proof)
    {
        ArgumentNullException.ThrowIfNull(proof);
        return EncodeCanonical(
            ConnectorProofDomain,
            RequiredGuid(proof.IdentityId, nameof(proof.IdentityId)),
            RequiredGuid(proof.OrganizationId, nameof(proof.OrganizationId)),
            RequiredGuid(proof.WorkspaceId, nameof(proof.WorkspaceId)),
            RequiredGuid(proof.ConnectionId, nameof(proof.ConnectionId)),
            RequiredText(proof.Audience, 512, nameof(proof.Audience)),
            proof.KeyVersion > 0 ? proof.KeyVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) : throw new ArgumentOutOfRangeException(nameof(proof.KeyVersion)),
            RequiredText(proof.Operation, 128, nameof(proof.Operation)),
            RequiredBase64Url(proof.PayloadDigest, 32, nameof(proof.PayloadDigest)),
            proof.IssuedAt.ToUniversalTime().ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
            RequiredBase64Url(proof.Nonce, 16, 64, nameof(proof.Nonce)));
    }

    public static string Sign(ECDsa privateKey, ReadOnlySpan<byte> payload)
    {
        ArgumentNullException.ThrowIfNull(privateKey);
        EnsureP256(privateKey);
        var signature = privateKey.SignData(
            payload,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return Base64UrlEncode(signature);
    }

    public static bool Verify(string publicKey, ReadOnlySpan<byte> payload, string signature)
    {
        if (!TryDecodeBounded(signature, 64, 64, out var signatureBytes))
            return false;

        try
        {
            using var key = ImportPublicKey(publicKey);
            return key.VerifyData(
                payload,
                signatureBytes,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static string Base64UrlEncode(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static ECDsa ImportPublicKey(string value)
    {
        var bytes = ImportPublicKeyBytes(value);
        var key = ECDsa.Create();
        try
        {
            key.ImportSubjectPublicKeyInfo(bytes, out var bytesRead);
            if (bytesRead != bytes.Length)
                throw new CryptographicException("Connector public key contains trailing data.");
            EnsureP256(key);
            return key;
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    private static byte[] ImportPublicKeyBytes(string value)
    {
        if (!TryDecodeBounded(value, 64, 256, out var bytes))
            throw new ArgumentException("Connector public key is invalid.", nameof(value));

        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(bytes, out var bytesRead);
        if (bytesRead != bytes.Length)
            throw new CryptographicException("Connector public key contains trailing data.");
        EnsureP256(key);
        return bytes;
    }

    private static void EnsureP256(ECDsa key)
    {
        var parameters = key.ExportParameters(includePrivateParameters: false);
        if (key.KeySize != 256
            || !string.Equals(parameters.Curve.Oid.Value, ECCurve.NamedCurves.nistP256.Oid.Value, StringComparison.Ordinal))
            throw new CryptographicException("Connector keys must use ECDSA P-256.");
    }

    private static string RequiredGuid(Guid value, string parameterName) =>
        value != Guid.Empty
            ? value.ToString("D").ToLowerInvariant()
            : throw new ArgumentException("A non-empty identifier is required.", parameterName);

    private static string RequiredText(string? value, int maximumLength, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength || value.Any(char.IsControl))
            throw new ArgumentException("A bounded text value is required.", parameterName);
        return value;
    }

    private static string RequiredBase64Url(string value, int decodedLength, string parameterName) =>
        RequiredBase64Url(value, decodedLength, decodedLength, parameterName);

    private static string RequiredBase64Url(string value, int minimumDecodedLength, int maximumDecodedLength, string parameterName)
    {
        if (!TryDecodeBounded(value, minimumDecodedLength, maximumDecodedLength, out _))
            throw new ArgumentException("A bounded base64url value is required.", parameterName);
        return value;
    }

    private static byte[] EncodeCanonical(string domain, params string[] values)
    {
        using var stream = new MemoryStream();
        WriteField(stream, domain);
        foreach (var value in values)
            WriteField(stream, value);
        return stream.ToArray();
    }

    private static void WriteField(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        stream.Write(length);
        stream.Write(bytes);
    }

    private static bool TryDecodeBounded(string? value, int minimumLength, int maximumLength, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrWhiteSpace(value) || value.Length > ((maximumLength + 2) / 3 * 4))
            return false;

        try
        {
            var normalized = value.Replace('-', '+').Replace('_', '/');
            normalized += (normalized.Length % 4) switch
            {
                0 => string.Empty,
                2 => "==",
                3 => "=",
                _ => throw new FormatException()
            };
            bytes = Convert.FromBase64String(normalized);
            return bytes.Length >= minimumLength && bytes.Length <= maximumLength
                && string.Equals(Base64UrlEncode(bytes), value, StringComparison.Ordinal);
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
    }
}
