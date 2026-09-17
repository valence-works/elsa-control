using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using ElsaControl.Deployment.Core.ExternalConnections;

return await Rehearsal.RunAsync(args);

internal static class Rehearsal
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var options = Options.Parse(args);
            var rawBundle = (await Console.In.ReadToEndAsync()).Trim();
            var bundle = JsonSerializer.Deserialize<PairingBundle>(rawBundle, Json)
                ?? throw new InvalidOperationException("Pairing bundle was not supplied on standard input.");
            bundle.Validate();
            ValidateMode(options, bundle, DateTimeOffset.UtcNow);

            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using var client = new HttpClient(handler)
            {
                BaseAddress = bundle.ControlEndpoint,
                Timeout = TimeSpan.FromSeconds(30)
            };
            using var currentKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var redemption = CreateRedemption(bundle, currentKey);
            using var redeemed = await client.PostAsJsonAsync(
                RuntimePath(bundle.ConnectionId, "enrollment/redeem"), redemption, Json);

            if (options.ExpectExpired)
            {
                Evidence.Write("elapsed_pairing_denied", redeemed.StatusCode == HttpStatusCode.Unauthorized ? "passed" : "failed",
                    redeemed.StatusCode, bundle.ConnectionId);
                return redeemed.StatusCode == HttpStatusCode.Unauthorized ? 0 : 1;
            }

            if (redeemed.StatusCode != HttpStatusCode.OK)
                return Evidence.Fail("pairing_redeem", redeemed.StatusCode, bundle.ConnectionId);

            var identity = await redeemed.Content.ReadFromJsonAsync<ConnectorIdentity>(Json)
                ?? throw new InvalidOperationException("Control returned no connector identity.");
            Evidence.Write("pairing_redeemed", "passed", redeemed.StatusCode, bundle.ConnectionId, identity.IdentityId,
                identity.KeyVersion);

            var authentication = CreateProof(bundle, identity, ExternalEngineConnectionService.AuthenticationOperation,
                ExternalEngineConnectionService.AuthenticationPayloadDigest(), currentKey);
            using var authenticated = await client.PostAsJsonAsync(
                RuntimePath(bundle.ConnectionId, "authenticate"), authentication, Json);
            if (authenticated.StatusCode != HttpStatusCode.OK)
                return Evidence.Fail("connector_authenticated", authenticated.StatusCode, bundle.ConnectionId, identity.IdentityId);
            Evidence.Write("connector_authenticated", "passed", authenticated.StatusCode, bundle.ConnectionId,
                identity.IdentityId, identity.KeyVersion);

            var unsupportedReport = CreateReport(options, 1, connectorProtocol: "2");
            var unsupportedRequest = new ExternalEngineHeartbeatRequest(
                CreateProof(bundle, identity, ExternalEngineHeartbeatService.HeartbeatOperation,
                    ExternalEngineHeartbeatService.CreatePayloadDigest(unsupportedReport), currentKey),
                unsupportedReport);
            using var unsupported = await client.PostAsJsonAsync(
                RuntimePath(bundle.ConnectionId, "heartbeat"), unsupportedRequest, Json);
            if (unsupported.StatusCode != HttpStatusCode.UpgradeRequired)
                return Evidence.Fail("unsupported_protocol_rejected", unsupported.StatusCode, bundle.ConnectionId,
                    identity.IdentityId, sequence: unsupportedReport.Sequence);
            Evidence.Write("unsupported_protocol_rejected", "passed", unsupported.StatusCode, bundle.ConnectionId,
                identity.IdentityId, identity.KeyVersion, unsupportedReport.Sequence);

            await Task.Delay(TimeSpan.FromSeconds(6));
            var report = CreateReport(options, 2, ExternalEngineHeartbeatService.CurrentProtocol);
            var heartbeatRequest = new ExternalEngineHeartbeatRequest(
                CreateProof(bundle, identity, ExternalEngineHeartbeatService.HeartbeatOperation,
                    ExternalEngineHeartbeatService.CreatePayloadDigest(report), currentKey),
                report);
            using var heartbeat = await client.PostAsJsonAsync(
                RuntimePath(bundle.ConnectionId, "heartbeat"), heartbeatRequest, Json);
            if (heartbeat.StatusCode != HttpStatusCode.OK)
                return Evidence.Fail("heartbeat_accepted", heartbeat.StatusCode, bundle.ConnectionId, identity.IdentityId,
                    sequence: report.Sequence);
            Evidence.Write("heartbeat_accepted", "passed", heartbeat.StatusCode, bundle.ConnectionId, identity.IdentityId,
                identity.KeyVersion, report.Sequence);

            using var replay = await client.PostAsJsonAsync(
                RuntimePath(bundle.ConnectionId, "heartbeat"), heartbeatRequest, Json);
            if (replay.StatusCode != HttpStatusCode.Unauthorized)
                return Evidence.Fail("proof_replay_rejected", replay.StatusCode, bundle.ConnectionId, identity.IdentityId,
                    sequence: report.Sequence);
            Evidence.Write("proof_replay_rejected", "passed", replay.StatusCode, bundle.ConnectionId, identity.IdentityId,
                identity.KeyVersion, report.Sequence);

            if (options.HoldSeconds > 0)
            {
                Evidence.Write("offline_window_started", "observing", null, bundle.ConnectionId, identity.IdentityId,
                    identity.KeyVersion, report.Sequence);
                await Task.Delay(TimeSpan.FromSeconds(options.HoldSeconds));
                var reconnectReport = CreateReport(options, 3, ExternalEngineHeartbeatService.CurrentProtocol);
                var reconnectRequest = new ExternalEngineHeartbeatRequest(
                    CreateProof(bundle, identity, ExternalEngineHeartbeatService.HeartbeatOperation,
                        ExternalEngineHeartbeatService.CreatePayloadDigest(reconnectReport), currentKey),
                    reconnectReport);
                using var reconnect = await client.PostAsJsonAsync(
                    RuntimePath(bundle.ConnectionId, "heartbeat"), reconnectRequest, Json);
                if (reconnect.StatusCode != HttpStatusCode.OK)
                    return Evidence.Fail("heartbeat_reconnected", reconnect.StatusCode, bundle.ConnectionId,
                        identity.IdentityId, sequence: reconnectReport.Sequence);
                Evidence.Write("heartbeat_reconnected", "passed", reconnect.StatusCode, bundle.ConnectionId,
                    identity.IdentityId, identity.KeyVersion, reconnectReport.Sequence);
            }

            using var nextKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var nextPublicKey = ExternalEngineEnrollmentProtocol.ExportPublicKey(nextKey);
            var overlap = TimeSpan.FromMinutes(1);
            var rotation = new ExternalEngineConnectorKeyRotationRequest(
                CreateProof(bundle, identity, ExternalEngineEnrollmentDefaults.RotationOperation,
                    ExternalEngineEnrollmentProtocol.CreateRotationPayloadDigest(nextPublicKey, overlap), currentKey),
                nextPublicKey,
                overlap);
            using var rotatedResponse = await client.PostAsJsonAsync(
                RuntimePath(bundle.ConnectionId, "identity/rotate"), rotation, Json);
            if (rotatedResponse.StatusCode != HttpStatusCode.OK)
                return Evidence.Fail("identity_rotated", rotatedResponse.StatusCode, bundle.ConnectionId, identity.IdentityId);
            var rotated = await rotatedResponse.Content.ReadFromJsonAsync<ConnectorIdentity>(Json)
                ?? throw new InvalidOperationException("Control returned no rotated connector identity.");
            Evidence.Write("identity_rotated", "passed", rotatedResponse.StatusCode, bundle.ConnectionId,
                rotated.IdentityId, rotated.KeyVersion);

            var revocation = CreateProof(bundle, rotated, ExternalEngineEnrollmentDefaults.RevocationOperation,
                ExternalEngineEnrollmentProtocol.CreateRevocationPayloadDigest(), nextKey);
            using var revoked = await client.PostAsJsonAsync(
                RuntimePath(bundle.ConnectionId, "identity/revoke"), revocation, Json);
            if (revoked.StatusCode != HttpStatusCode.OK)
                return Evidence.Fail("identity_revoked", revoked.StatusCode, bundle.ConnectionId, rotated.IdentityId);
            Evidence.Write("identity_revoked", "passed", revoked.StatusCode, bundle.ConnectionId, rotated.IdentityId,
                rotated.KeyVersion);

            var afterRevoke = CreateProof(bundle, rotated, ExternalEngineConnectionService.AuthenticationOperation,
                ExternalEngineConnectionService.AuthenticationPayloadDigest(), nextKey);
            using var denied = await client.PostAsJsonAsync(
                RuntimePath(bundle.ConnectionId, "authenticate"), afterRevoke, Json);
            if (denied.StatusCode is not (HttpStatusCode.NotFound or HttpStatusCode.Unauthorized))
                return Evidence.Fail("revocation_enforced", denied.StatusCode, bundle.ConnectionId, rotated.IdentityId);
            Evidence.Write("revocation_enforced", "passed", denied.StatusCode, bundle.ConnectionId, rotated.IdentityId,
                rotated.KeyVersion);
            return 0;
        }
        catch (Exception exception)
        {
            Evidence.Write("rehearsal_failed", "failed", null, null, reason: exception.GetType().Name);
            return 1;
        }
    }

    private static ExternalEngineEnrollmentRedeemRequest CreateRedemption(PairingBundle bundle, ECDsa key)
    {
        var publicKey = ExternalEngineEnrollmentProtocol.ExportPublicKey(key);
        var payload = ExternalEngineEnrollmentProtocol.CreateRedemptionPayload(
            bundle.ChallengeId,
            bundle.OrganizationId,
            bundle.WorkspaceId,
            bundle.ConnectionId,
            bundle.Purpose,
            bundle.Audience,
            ExternalEngineEnrollmentProtocol.HashChallenge(bundle.Challenge),
            ExternalEngineEnrollmentProtocol.PublicKeyThumbprint(publicKey));
        return new ExternalEngineEnrollmentRedeemRequest(
            bundle.ChallengeId,
            bundle.OrganizationId,
            bundle.WorkspaceId,
            bundle.ConnectionId,
            bundle.Purpose,
            bundle.Audience,
            bundle.Challenge,
            publicKey,
            ExternalEngineEnrollmentProtocol.Sign(key, payload));
    }

    private static ExternalEngineConnectorProof CreateProof(
        PairingBundle bundle,
        ConnectorIdentity identity,
        string operation,
        string payloadDigest,
        ECDsa key)
    {
        var unsigned = new ExternalEngineConnectorProof(
            identity.IdentityId,
            bundle.OrganizationId,
            bundle.WorkspaceId,
            bundle.ConnectionId,
            bundle.Audience,
            identity.KeyVersion,
            operation,
            payloadDigest,
            DateTimeOffset.UtcNow,
            ExternalEngineEnrollmentProtocol.Base64UrlEncode(RandomNumberGenerator.GetBytes(32)),
            string.Empty);
        return unsigned with
        {
            Signature = ExternalEngineEnrollmentProtocol.Sign(
                key, ExternalEngineEnrollmentProtocol.CreateConnectorProofPayload(unsigned))
        };
    }

    private static ExternalEngineHeartbeatReport CreateReport(Options options, long sequence, string connectorProtocol)
    {
        var capabilities = options.StudioUrl is null
            ? new[] { ExternalEngineHeartbeatService.StatusCapability }
            : new[] { ExternalEngineHeartbeatService.StatusCapability, ExternalEngineHeartbeatService.StudioCapability };
        return new ExternalEngineHeartbeatReport(
            sequence,
            DateTimeOffset.UtcNow,
            connectorProtocol,
            options.ConnectorVersion,
            ExternalEngineRuntimeHealth.Healthy,
            options.RuntimeKind,
            options.Distribution,
            options.RuntimeVersion,
            options.StudioUrl,
            capabilities,
            []);
    }

    internal static void ValidateMode(Options options, PairingBundle bundle, DateTimeOffset now)
    {
        if (options.ExpectExpired && bundle.ExpiresAt > now)
            throw new InvalidOperationException("Refusing to redeem a pairing bundle that has not expired.");
    }

    internal static string RuntimePath(Guid connectionId, string suffix) =>
        $"api/runtime/external-engine-connections/{connectionId:D}/{suffix}";
}

internal sealed record PairingBundle(
    string Schema,
    string ControlBaseUrl,
    Guid ChallengeId,
    Guid OrganizationId,
    Guid WorkspaceId,
    Guid ConnectionId,
    string Purpose,
    string Audience,
    string Challenge,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt)
{
    public Uri ControlEndpoint => new(ControlBaseUrl.TrimEnd('/') + "/", UriKind.Absolute);

    public void Validate()
    {
        if (!string.Equals(Schema, "elsa-control.external-engine-pairing.v1", StringComparison.Ordinal))
            throw new InvalidOperationException("Unsupported pairing bundle schema.");
        if (!Uri.TryCreate(ControlBaseUrl, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrWhiteSpace(endpoint.Host)
            || !string.IsNullOrEmpty(endpoint.UserInfo)
            || !string.IsNullOrEmpty(endpoint.Query)
            || !string.IsNullOrEmpty(endpoint.Fragment))
            throw new InvalidOperationException("Control base URL must be an uncredentialed HTTPS address.");
        if (ChallengeId == Guid.Empty || OrganizationId == Guid.Empty || WorkspaceId == Guid.Empty || ConnectionId == Guid.Empty)
            throw new InvalidOperationException("Pairing bundle identifiers are incomplete.");
        if (!string.Equals(Purpose, ExternalEngineEnrollmentDefaults.PairingPurpose, StringComparison.Ordinal)
            || !string.Equals(Audience, ExternalEngineEnrollmentDefaults.AudienceFor(ConnectionId), StringComparison.Ordinal))
            throw new InvalidOperationException("Pairing bundle scope is invalid.");
        _ = ExternalEngineEnrollmentProtocol.HashChallenge(Challenge);
    }

    public override string ToString() =>
        $"PairingBundle {{ Schema = {Schema}, ControlBaseUrl = {ControlBaseUrl}, ChallengeId = {ChallengeId}, OrganizationId = {OrganizationId}, WorkspaceId = {WorkspaceId}, ConnectionId = {ConnectionId}, Purpose = {Purpose}, Audience = {Audience}, Challenge = [REDACTED], IssuedAt = {IssuedAt:O}, ExpiresAt = {ExpiresAt:O} }}";
}

internal sealed record ConnectorIdentity(
    Guid ConnectionId,
    Guid IdentityId,
    int KeyVersion,
    DateTimeOffset EnrolledAt,
    DateTimeOffset? RotatedAt,
    DateTimeOffset? RevokedAt);

internal sealed record Options(
    string? StudioUrl,
    int HoldSeconds,
    bool ExpectExpired,
    string ConnectorVersion,
    string RuntimeKind,
    string Distribution,
    string RuntimeVersion)
{
    public static Options Parse(string[] args)
    {
        string? studioUrl = null;
        var holdSeconds = 0;
        var expectExpired = false;
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--studio-url":
                    studioUrl = RequiredValue(args, ref index);
                    if (!Uri.TryCreate(studioUrl, UriKind.Absolute, out var uri)
                        || uri.Scheme != Uri.UriSchemeHttps
                        || string.IsNullOrWhiteSpace(uri.Host)
                        || !string.IsNullOrEmpty(uri.UserInfo)
                        || !string.IsNullOrEmpty(uri.Query)
                        || !string.IsNullOrEmpty(uri.Fragment))
                        throw new ArgumentException("Studio URL must be an uncredentialed HTTPS address without a query or fragment.");
                    break;
                case "--hold-seconds":
                    if (!int.TryParse(RequiredValue(args, ref index), out holdSeconds)
                        || holdSeconds is < 0 or > 900
                        || holdSeconds is > 0 and < 6)
                        throw new ArgumentException("Hold seconds must be 0 or between 6 and 900.");
                    break;
                case "--expect-expired":
                    expectExpired = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown option at position {index + 1}.");
            }
        }

        return new Options(studioUrl, holdSeconds, expectExpired, "rehearsal-1", "rehearsal",
            "customer-managed", "rehearsal");
    }

    private static string RequiredValue(string[] args, ref int index)
    {
        if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
            throw new ArgumentException("Option value is required.");
        return args[index];
    }
}

internal static class Evidence
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static void Write(
        string @event,
        string outcome,
        HttpStatusCode? status,
        Guid? connectionId,
        Guid? identityId = null,
        int? keyVersion = null,
        long? sequence = null,
        string? reason = null) =>
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            timestamp = DateTimeOffset.UtcNow,
            @event,
            outcome,
            httpStatus = status is null ? (int?)null : (int)status,
            connectionId,
            identityId,
            keyVersion,
            sequence,
            reason
        }, Json));

    public static int Fail(
        string @event,
        HttpStatusCode status,
        Guid connectionId,
        Guid? identityId = null,
        int? keyVersion = null,
        long? sequence = null)
    {
        Write(@event, "failed", status, connectionId, identityId, keyVersion, sequence);
        return 1;
    }
}
