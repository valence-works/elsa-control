using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using ElsaControl.Deployment.Core.ExternalConnections;
using Xunit;

namespace ElsaControl.Deployment.Core.Tests.ExternalConnections;

internal static class ExternalEngineConnectorProtocolGoldenVectors
{
    public const string RelativePath = "docs/protocol/external-engine-connector/v1/vectors.json";
    public const string RegenerateEnvironmentVariable = "ELSA_CONTROL_REGENERATE_CONNECTOR_PROTOCOL_VECTORS";

    // TEST ONLY – synthetic PKCS#8 P-256 fixtures for committed protocol vectors. Never use outside this file.
    private const string CurrentPrivateKeyPkcs8 =
        "MIGHAgEAMBMGByqGSM49AgEGCCqGSM49AwEHBG0wawIBAQQgydYykJaRoI4JF8JwsNIDz2f6mwypHtPTNm66bghs9eGhRANCAASYBAgiLJ-ViubfEQ4jH71s07S-RRpZfeDLg_U_vud_XBF_KZeRjiqrGv6yreJf269-LBbET1AQ26FUGLsDEOC8";
    private const string NextPrivateKeyPkcs8 =
        "MIGHAgEAMBMGByqGSM49AgEGCCqGSM49AwEHBG0wawIBAQQgWQkjbVlJF1C3Rs9SItv-taQfMQ_1wsLFKf9dy8xM5XShRANCAARMB1WaNZbMsZjkiwFMQEqhbOA0CwQ2WJ5iH4sDWgXXxH1vDWfp6CD98KrgeF-Xnw0N6IT-W2dOv-fQ6HnIBAX9";

    private static readonly Guid OrganizationId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid WorkspaceId = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid ConnectionId = Guid.Parse("30000000-0000-0000-0000-000000000001");
    private static readonly Guid ChallengeId = Guid.Parse("40000000-0000-0000-0000-000000000001");
    private static readonly Guid IdentityId = Guid.Parse("50000000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-17T08:00:00Z");
    private static readonly DateTimeOffset HeartbeatObservedAt = DateTimeOffset.Parse("2026-09-17T10:00:00Z");
    private static readonly TimeSpan RotationOverlap = TimeSpan.FromMinutes(2);

    public static FileInfo CommittedFile(DirectoryInfo repoRoot) =>
        new(Path.Combine(repoRoot.FullName, RelativePath.Replace('/', Path.DirectorySeparatorChar)));

    public static string GenerateJson(IReadOnlyDictionary<string, string>? committedSignatures = null)
    {
        using var currentKey = ImportPkcs8(CurrentPrivateKeyPkcs8);
        using var nextKey = ImportPkcs8(NextPrivateKeyPkcs8);
        var currentPublicKey = ExternalEngineEnrollmentProtocol.ExportPublicKey(currentKey);
        var nextPublicKey = ExternalEngineEnrollmentProtocol.ExportPublicKey(nextKey);
        var document = new Document(
            SchemaVersion: 1,
            ProtocolVersion: ExternalEngineHeartbeatService.CurrentProtocol,
            GeneratedFrom: "ElsaControl.Deployment.Core",
            Algorithms: new Algorithms(
                "ECDSA-P256-SHA256",
                "nistP256",
                "IEEE-P1363-r||s",
                "SHA-256",
                "uint32be-length || utf8-bytes, domain first"),
            Clock: new Clock(
                FormatTimestamp(Now),
                (int)ExternalEngineEnrollmentDefaults.MaximumProofAge.TotalSeconds,
                (int)ExternalEngineEnrollmentDefaults.MaximumProofFutureSkew.TotalSeconds),
            Keys: new Keys(
                DescribeKey(CurrentPrivateKeyPkcs8, currentPublicKey),
                DescribeKey(NextPrivateKeyPkcs8, nextPublicKey)),
            Scope: new Scope(
                OrganizationId.ToString("D"),
                WorkspaceId.ToString("D"),
                ConnectionId.ToString("D"),
                IdentityId.ToString("D"),
                ChallengeId.ToString("D"),
                ExternalEngineEnrollmentDefaults.AudienceFor(ConnectionId),
                ExternalEngineEnrollmentDefaults.PairingPurpose),
            Vectors: BuildVectors(currentKey, currentPublicKey, nextPublicKey, committedSignatures));
        return Serialize(document);
    }

    public static void VerifySignatures(string json)
    {
        using var document = JsonDocument.Parse(json);
        var currentPublicKey = document.RootElement.GetProperty("keys").GetProperty("current").GetProperty("publicKeySpkiBase64Url").GetString()
            ?? throw new InvalidOperationException("Current public key is missing.");
        foreach (var vector in document.RootElement.GetProperty("vectors").EnumerateArray())
        {
            var payload = Convert.FromHexString(vector.GetProperty("canonicalPayloadHex").GetString()
                ?? throw new InvalidOperationException("canonicalPayloadHex is required."));
            var signature = vector.GetProperty("signatureBase64Url").GetString()
                ?? throw new InvalidOperationException("signatureBase64Url is required.");
            var expected = vector.GetProperty("expected").GetProperty("signatureValid").GetBoolean();
            Assert.Equal(expected, ExternalEngineEnrollmentProtocol.Verify(currentPublicKey, payload, signature));
        }
    }

    private static IReadOnlyList<Vector> BuildVectors(
        ECDsa currentKey,
        string currentPublicKey,
        string nextPublicKey,
        IReadOnlyDictionary<string, string>? committedSignatures)
    {
        var challenge = ExternalEngineEnrollmentProtocol.Base64UrlEncode(Repeat(0xAA, 32));
        var challengeHash = ExternalEngineEnrollmentProtocol.HashChallenge(challenge);
        var publicKeyThumbprint = ExternalEngineEnrollmentProtocol.PublicKeyThumbprint(currentPublicKey);
        var redeemPayload = ExternalEngineEnrollmentProtocol.CreateRedemptionPayload(
            ChallengeId,
            OrganizationId,
            WorkspaceId,
            ConnectionId,
            ExternalEngineEnrollmentDefaults.PairingPurpose,
            ExternalEngineEnrollmentDefaults.AudienceFor(ConnectionId),
            challengeHash,
            publicKeyThumbprint);

        var heartbeatReport = new ExternalEngineHeartbeatReport(
            1,
            HeartbeatObservedAt,
            ExternalEngineHeartbeatService.CurrentProtocol,
            "1.4.0",
            ExternalEngineRuntimeHealth.Healthy,
            "server",
            "valence-runtime",
            "3.8.1",
            "https://studio.example.test/elsa/",
            [ExternalEngineHeartbeatService.StudioCapability, ExternalEngineHeartbeatService.StatusCapability],
            [
                new("worker", "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"),
                new("runtime", "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")
            ]);
        var heartbeatReportBytes = ExternalEngineHeartbeatService.CreateCanonicalPayload(heartbeatReport);
        var heartbeatDigest = ExternalEngineHeartbeatService.CreatePayloadDigest(heartbeatReport);
        var heartbeatProof = Proof(ExternalEngineHeartbeatService.HeartbeatOperation, heartbeatDigest, Repeat(0x11, 32));
        var heartbeatPayload = ExternalEngineEnrollmentProtocol.CreateConnectorProofPayload(heartbeatProof);

        var rotationDigest = ExternalEngineEnrollmentProtocol.CreateRotationPayloadDigest(nextPublicKey, RotationOverlap);
        var rotateProof = Proof(ExternalEngineEnrollmentDefaults.RotationOperation, rotationDigest, Repeat(0x22, 32));
        var rotatePayload = ExternalEngineEnrollmentProtocol.CreateConnectorProofPayload(rotateProof);

        var revokeDigest = ExternalEngineEnrollmentProtocol.CreateRevocationPayloadDigest();
        var revokeProof = Proof(ExternalEngineEnrollmentDefaults.RevocationOperation, revokeDigest, Repeat(0x33, 32));
        var revokePayload = ExternalEngineEnrollmentProtocol.CreateConnectorProofPayload(revokeProof);

        return
        [
            Complete(
                "enroll-redeem.valid",
                "enroll-redeem",
                "Valid redemption message signed by the enrolled P-256 key.",
                ExternalEngineEnrollmentProtocol.RedemptionDomain,
                RedeemInputs(challenge, challengeHash, currentPublicKey, publicKeyThumbprint),
                redeemPayload,
                currentKey,
                mutateSignature: false,
                committedSignatures),
            Complete(
                "enroll-redeem.invalid-signature",
                "enroll-redeem",
                "Well-formed redemption signature that must not verify.",
                ExternalEngineEnrollmentProtocol.RedemptionDomain,
                RedeemInputs(challenge, challengeHash, currentPublicKey, publicKeyThumbprint),
                redeemPayload,
                currentKey,
                mutateSignature: true,
                committedSignatures),
            Complete(
                "heartbeat.valid",
                "heartbeat",
                "Valid heartbeat proof over the protocol-v1 canonical report.",
                ExternalEngineEnrollmentProtocol.ConnectorProofDomain,
                HeartbeatInputs(heartbeatProof, heartbeatReportBytes, heartbeatDigest),
                heartbeatPayload,
                currentKey,
                mutateSignature: false,
                committedSignatures,
                heartbeatReportBytes,
                heartbeatDigest),
            Complete(
                "heartbeat.invalid-signature",
                "heartbeat",
                "Well-formed heartbeat signature that must not verify.",
                ExternalEngineEnrollmentProtocol.ConnectorProofDomain,
                HeartbeatInputs(heartbeatProof, heartbeatReportBytes, heartbeatDigest),
                heartbeatPayload,
                currentKey,
                mutateSignature: true,
                committedSignatures,
                heartbeatReportBytes,
                heartbeatDigest),
            Complete(
                "rotate.valid",
                "rotate",
                "Valid current-key rotation proof bound to the next public key and overlap.",
                ExternalEngineEnrollmentProtocol.ConnectorProofDomain,
                RotateInputs(rotateProof, nextPublicKey, rotationDigest),
                rotatePayload,
                currentKey,
                mutateSignature: false,
                committedSignatures,
                payloadDigest: rotationDigest),
            Complete(
                "rotate.invalid-signature",
                "rotate",
                "Well-formed rotation signature that must not verify.",
                ExternalEngineEnrollmentProtocol.ConnectorProofDomain,
                RotateInputs(rotateProof, nextPublicKey, rotationDigest),
                rotatePayload,
                currentKey,
                mutateSignature: true,
                committedSignatures,
                payloadDigest: rotationDigest),
            Complete(
                "revoke.valid",
                "revoke",
                "Valid current-key revocation proof.",
                ExternalEngineEnrollmentProtocol.ConnectorProofDomain,
                RevokeInputs(revokeProof, revokeDigest),
                revokePayload,
                currentKey,
                mutateSignature: false,
                committedSignatures,
                payloadDigest: revokeDigest),
            Complete(
                "revoke.invalid-signature",
                "revoke",
                "Well-formed revocation signature that must not verify.",
                ExternalEngineEnrollmentProtocol.ConnectorProofDomain,
                RevokeInputs(revokeProof, revokeDigest),
                revokePayload,
                currentKey,
                mutateSignature: true,
                committedSignatures,
                payloadDigest: revokeDigest)
        ];
    }

    private static Vector Complete(
        string id,
        string kind,
        string description,
        string domain,
        IReadOnlyDictionary<string, JsonElement> inputs,
        byte[] payload,
        ECDsa currentKey,
        bool mutateSignature,
        IReadOnlyDictionary<string, string>? committedSignatures,
        byte[]? reportCanonicalPayload = null,
        string? payloadDigest = null)
    {
        var publicKey = ExternalEngineEnrollmentProtocol.ExportPublicKey(currentKey);
        string signature;
        if (committedSignatures is not null
            && committedSignatures.TryGetValue(id, out var committed)
            && ExternalEngineEnrollmentProtocol.Verify(publicKey, payload, committed) == !mutateSignature)
        {
            signature = committed;
        }
        else
        {
            signature = ExternalEngineEnrollmentProtocol.Sign(currentKey, payload);
            if (mutateSignature)
                signature = MutateSignature(signature);
        }

        return new Vector(
            id,
            kind,
            description,
            domain,
            inputs,
            Convert.ToHexString(payload),
            reportCanonicalPayload is null ? null : Convert.ToHexString(reportCanonicalPayload),
            reportCanonicalPayload is null ? null : Encoding.UTF8.GetString(reportCanonicalPayload),
            payloadDigest,
            signature,
            new Expected(ExternalEngineEnrollmentProtocol.Verify(publicKey, payload, signature)));
    }

    private static IReadOnlyDictionary<string, JsonElement> RedeemInputs(
        string challenge,
        string challengeHash,
        string publicKey,
        string publicKeyThumbprint) =>
        Object(
            ("challengeId", ChallengeId.ToString("D")),
            ("organizationId", OrganizationId.ToString("D")),
            ("workspaceId", WorkspaceId.ToString("D")),
            ("connectionId", ConnectionId.ToString("D")),
            ("purpose", ExternalEngineEnrollmentDefaults.PairingPurpose),
            ("audience", ExternalEngineEnrollmentDefaults.AudienceFor(ConnectionId)),
            ("challenge", challenge),
            ("challengeHash", challengeHash),
            ("publicKey", publicKey),
            ("publicKeyThumbprint", publicKeyThumbprint));

    private static IReadOnlyDictionary<string, JsonElement> HeartbeatInputs(
        ExternalEngineConnectorProof proof,
        byte[] reportBytes,
        string digest) =>
        Object(
            ("identityId", proof.IdentityId.ToString("D")),
            ("organizationId", proof.OrganizationId.ToString("D")),
            ("workspaceId", proof.WorkspaceId.ToString("D")),
            ("connectionId", proof.ConnectionId.ToString("D")),
            ("audience", proof.Audience),
            ("keyVersion", proof.KeyVersion),
            ("operation", proof.Operation),
            ("payloadDigest", digest),
            ("issuedAt", FormatTimestamp(proof.IssuedAt)),
            ("issuedAtUnixMilliseconds", proof.IssuedAt.ToUnixTimeMilliseconds()),
            ("nonce", proof.Nonce),
            ("report", CloneJson(reportBytes)));

    private static IReadOnlyDictionary<string, JsonElement> RotateInputs(
        ExternalEngineConnectorProof proof,
        string nextPublicKey,
        string digest) =>
        Object(
            ("identityId", proof.IdentityId.ToString("D")),
            ("organizationId", proof.OrganizationId.ToString("D")),
            ("workspaceId", proof.WorkspaceId.ToString("D")),
            ("connectionId", proof.ConnectionId.ToString("D")),
            ("audience", proof.Audience),
            ("keyVersion", proof.KeyVersion),
            ("operation", proof.Operation),
            ("payloadDigest", digest),
            ("issuedAt", FormatTimestamp(proof.IssuedAt)),
            ("issuedAtUnixMilliseconds", proof.IssuedAt.ToUnixTimeMilliseconds()),
            ("nonce", proof.Nonce),
            ("nextPublicKey", nextPublicKey),
            ("nextPublicKeyThumbprint", ExternalEngineEnrollmentProtocol.PublicKeyThumbprint(nextPublicKey)),
            ("overlapSeconds", (int)RotationOverlap.TotalSeconds));

    private static IReadOnlyDictionary<string, JsonElement> RevokeInputs(
        ExternalEngineConnectorProof proof,
        string digest) =>
        Object(
            ("identityId", proof.IdentityId.ToString("D")),
            ("organizationId", proof.OrganizationId.ToString("D")),
            ("workspaceId", proof.WorkspaceId.ToString("D")),
            ("connectionId", proof.ConnectionId.ToString("D")),
            ("audience", proof.Audience),
            ("keyVersion", proof.KeyVersion),
            ("operation", proof.Operation),
            ("payloadDigest", digest),
            ("issuedAt", FormatTimestamp(proof.IssuedAt)),
            ("issuedAtUnixMilliseconds", proof.IssuedAt.ToUnixTimeMilliseconds()),
            ("nonce", proof.Nonce));

    private static ExternalEngineConnectorProof Proof(string operation, string payloadDigest, byte[] nonce) =>
        new(
            IdentityId,
            OrganizationId,
            WorkspaceId,
            ConnectionId,
            ExternalEngineEnrollmentDefaults.AudienceFor(ConnectionId),
            ExternalEngineEnrollmentDefaults.InitialKeyVersion,
            operation,
            payloadDigest,
            Now,
            ExternalEngineEnrollmentProtocol.Base64UrlEncode(nonce),
            string.Empty);

    private static KeyMaterial DescribeKey(string privateKey, string publicKey) =>
        new(privateKey, publicKey, ExternalEngineEnrollmentProtocol.PublicKeyThumbprint(publicKey));

    private static string Serialize(Document document)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", document.SchemaVersion);
            writer.WriteString("protocolVersion", document.ProtocolVersion);
            writer.WriteString("generatedFrom", document.GeneratedFrom);
            writer.WritePropertyName("algorithms");
            writer.WriteStartObject();
            writer.WriteString("key", document.Algorithms.Key);
            writer.WriteString("curve", document.Algorithms.Curve);
            writer.WriteString("signature", document.Algorithms.Signature);
            writer.WriteString("hash", document.Algorithms.Hash);
            writer.WriteString("canonicalFields", document.Algorithms.CanonicalFields);
            writer.WriteEndObject();
            writer.WritePropertyName("clock");
            writer.WriteStartObject();
            writer.WriteString("now", document.Clock.Now);
            writer.WriteNumber("maximumProofAgeSeconds", document.Clock.MaximumProofAgeSeconds);
            writer.WriteNumber("maximumProofFutureSkewSeconds", document.Clock.MaximumProofFutureSkewSeconds);
            writer.WriteEndObject();
            writer.WritePropertyName("keys");
            writer.WriteStartObject();
            writer.WriteBoolean("testOnly", true);
            writer.WriteString("description", "TEST ONLY – never use outside these protocol vectors.");
            WriteKey(writer, "current", document.Keys.Current);
            WriteKey(writer, "next", document.Keys.Next);
            writer.WriteEndObject();
            writer.WritePropertyName("scope");
            writer.WriteStartObject();
            writer.WriteString("organizationId", document.Scope.OrganizationId);
            writer.WriteString("workspaceId", document.Scope.WorkspaceId);
            writer.WriteString("connectionId", document.Scope.ConnectionId);
            writer.WriteString("identityId", document.Scope.IdentityId);
            writer.WriteString("challengeId", document.Scope.ChallengeId);
            writer.WriteString("audience", document.Scope.Audience);
            writer.WriteString("purpose", document.Scope.Purpose);
            writer.WriteEndObject();
            writer.WritePropertyName("vectors");
            writer.WriteStartArray();
            foreach (var vector in document.Vectors)
            {
                writer.WriteStartObject();
                writer.WriteString("id", vector.Id);
                writer.WriteString("kind", vector.Kind);
                writer.WriteString("description", vector.Description);
                writer.WriteString("domain", vector.Domain);
                writer.WritePropertyName("inputs");
                writer.WriteStartObject();
                foreach (var (name, value) in vector.Inputs)
                {
                    writer.WritePropertyName(name);
                    value.WriteTo(writer);
                }

                writer.WriteEndObject();
                writer.WriteString("canonicalPayloadHex", vector.CanonicalPayloadHex);
                if (vector.ReportCanonicalPayloadHex is not null)
                    writer.WriteString("reportCanonicalPayloadHex", vector.ReportCanonicalPayloadHex);
                if (vector.ReportCanonicalPayloadUtf8 is not null)
                    writer.WriteString("reportCanonicalPayloadUtf8", vector.ReportCanonicalPayloadUtf8);
                if (vector.PayloadDigest is not null)
                    writer.WriteString("payloadDigest", vector.PayloadDigest);
                writer.WriteString("signatureBase64Url", vector.SignatureBase64Url);
                writer.WritePropertyName("expected");
                writer.WriteStartObject();
                writer.WriteBoolean("signatureValid", vector.Expected.SignatureValid);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    private static void WriteKey(Utf8JsonWriter writer, string name, KeyMaterial key)
    {
        writer.WritePropertyName(name);
        writer.WriteStartObject();
        writer.WriteString("privateKeyPkcs8Base64Url", key.PrivateKeyPkcs8Base64Url);
        writer.WriteString("publicKeySpkiBase64Url", key.PublicKeySpkiBase64Url);
        writer.WriteString("publicKeyThumbprint", key.PublicKeyThumbprint);
        writer.WriteEndObject();
    }

    private static JsonElement CloneJson(byte[] json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static IReadOnlyDictionary<string, JsonElement> Object(params (string Name, object Value)[] values)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (name, value) in values)
        {
            result[name] = value switch
            {
                JsonElement element => element,
                string text => JsonSerializer.SerializeToElement(text),
                int number => JsonSerializer.SerializeToElement(number),
                long number => JsonSerializer.SerializeToElement(number),
                _ => throw new InvalidOperationException($"Unsupported golden-vector input type for {name}.")
            };
        }

        return result;
    }

    private static string MutateSignature(string signature)
    {
        var bytes = DecodeBase64Url(signature);
        bytes[^1] ^= 0x01;
        return ExternalEngineEnrollmentProtocol.Base64UrlEncode(bytes);
    }

    private static ECDsa ImportPkcs8(string value)
    {
        var bytes = DecodeBase64Url(value);
        var key = ECDsa.Create();
        try
        {
            key.ImportPkcs8PrivateKey(bytes, out var bytesRead);
            if (bytesRead != bytes.Length)
                throw new CryptographicException("Connector golden-vector key contains trailing data.");
            return key;
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    private static byte[] DecodeBase64Url(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized += (normalized.Length % 4) switch
        {
            0 => string.Empty,
            2 => "==",
            3 => "=",
            _ => throw new FormatException("Golden-vector base64url value is invalid.")
        };
        return Convert.FromBase64String(normalized);
    }

    private static byte[] Repeat(byte value, int count)
    {
        var bytes = new byte[count];
        Array.Fill(bytes, value);
        return bytes;
    }

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", System.Globalization.CultureInfo.InvariantCulture);

    public static IReadOnlyDictionary<string, string> ReadSignatures(string json)
    {
        using var document = JsonDocument.Parse(json);
        var signatures = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var vector in document.RootElement.GetProperty("vectors").EnumerateArray())
        {
            var id = vector.GetProperty("id").GetString() ?? throw new InvalidOperationException("Vector id is required.");
            signatures[id] = vector.GetProperty("signatureBase64Url").GetString()
                ?? throw new InvalidOperationException($"Signature for {id} is required.");
        }

        return signatures;
    }

    private sealed record Document(
        int SchemaVersion,
        string ProtocolVersion,
        string GeneratedFrom,
        Algorithms Algorithms,
        Clock Clock,
        Keys Keys,
        Scope Scope,
        IReadOnlyList<Vector> Vectors);

    private sealed record Algorithms(string Key, string Curve, string Signature, string Hash, string CanonicalFields);

    private sealed record Clock(string Now, int MaximumProofAgeSeconds, int MaximumProofFutureSkewSeconds);

    private sealed record Keys(KeyMaterial Current, KeyMaterial Next);

    private sealed record KeyMaterial(string PrivateKeyPkcs8Base64Url, string PublicKeySpkiBase64Url, string PublicKeyThumbprint);

    private sealed record Scope(
        string OrganizationId,
        string WorkspaceId,
        string ConnectionId,
        string IdentityId,
        string ChallengeId,
        string Audience,
        string Purpose);

    private sealed record Vector(
        string Id,
        string Kind,
        string Description,
        string Domain,
        IReadOnlyDictionary<string, JsonElement> Inputs,
        string CanonicalPayloadHex,
        string? ReportCanonicalPayloadHex,
        string? ReportCanonicalPayloadUtf8,
        string? PayloadDigest,
        string SignatureBase64Url,
        Expected Expected);

    private sealed record Expected(bool SignatureValid);
}
