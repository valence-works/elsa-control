using System.Security.Cryptography;
using System.Text.Json;
using ElsaControl.Deployment.Core.ExternalConnections;
using Xunit;

namespace ElsaControl.Deployment.Core.Tests.ExternalConnections;

public sealed class ExternalEngineEnrollmentServiceTests
{
    private static readonly Guid OrganizationId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid WorkspaceId = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid ConnectionId = Guid.Parse("30000000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-17T08:00:00Z");

    [Fact]
    public async Task Issue_persists_only_a_hash_and_redacts_the_one_time_challenge_from_diagnostics()
    {
        var fixture = new Fixture();

        var issued = await fixture.Service.IssueAsync(Request());
        var stored = await fixture.Store.FindChallengeAsync(
            issued.OrganizationId,
            issued.WorkspaceId,
            issued.ConnectionId,
            issued.ChallengeId);
        var serialized = JsonSerializer.Serialize(stored);

        Assert.NotNull(stored);
        Assert.Equal(43, issued.Challenge.Length);
        Assert.Equal(ExternalEngineEnrollmentProtocol.HashChallenge(issued.Challenge), stored.ChallengeHash);
        Assert.DoesNotContain(issued.Challenge, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(issued.Challenge, issued.ToString(), StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", issued.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Issue_rejects_lifetimes_beyond_fifteen_minutes()
    {
        var fixture = new Fixture();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            fixture.Service.IssueAsync(Request(TimeSpan.FromMinutes(15).Add(TimeSpan.FromTicks(1)))));
    }

    [Fact]
    public async Task Recovery_challenge_is_issued_strictly_after_the_revocation_floor()
    {
        var fixture = new Fixture();

        var issued = await fixture.Service.IssueAsync(
            new ExternalEngineEnrollmentIssueRequest(
                OrganizationId,
                WorkspaceId,
                ConnectionId,
                IssuedAfter: Now));

        Assert.True(issued.IssuedAt > Now);
        Assert.Equal(Now.AddTicks(1), issued.IssuedAt);
    }

    [Fact]
    public async Task Connector_can_redeem_once_with_a_p256_proof()
    {
        var fixture = new Fixture();
        var issued = await fixture.Service.IssueAsync(Request());
        using var connectorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var redeemed = await fixture.Service.RedeemAsync(Redemption(issued, connectorKey));

        Assert.True(redeemed.Succeeded);
        Assert.Equal(OrganizationId, redeemed.Identity!.OrganizationId);
        Assert.Equal(WorkspaceId, redeemed.Identity.WorkspaceId);
        Assert.Equal(ConnectionId, redeemed.Identity.ConnectionId);
        Assert.Equal(ExternalEngineEnrollmentDefaults.InitialKeyVersion, redeemed.Identity.KeyVersion);
        Assert.Equal(ExternalEngineEnrollmentDefaults.KeyAlgorithm, redeemed.Identity.KeyAlgorithm);
        Assert.DoesNotContain("PRIVATE", redeemed.Identity.PublicKey, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Redeemed_challenge_is_rejected_on_replay()
    {
        var fixture = new Fixture();
        var issued = await fixture.Service.IssueAsync(Request());
        using var connectorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = Redemption(issued, connectorKey);

        var first = await fixture.Service.RedeemAsync(request);
        var replay = await fixture.Service.RedeemAsync(request);

        Assert.True(first.Succeeded);
        Assert.Equal(ExternalEngineEnrollmentRedeemFailure.Replay, replay.Failure);
    }

    [Fact]
    public async Task Concurrent_redemption_has_exactly_one_winner()
    {
        var fixture = new Fixture();
        var issued = await fixture.Service.IssueAsync(Request());
        using var connectorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = Redemption(issued, connectorKey);

        var results = await Task.WhenAll(
            fixture.Service.RedeemAsync(request),
            fixture.Service.RedeemAsync(request));

        Assert.Single(results, result => result.Succeeded);
        Assert.Single(results, result => result.Failure == ExternalEngineEnrollmentRedeemFailure.Replay);
    }

    [Fact]
    public async Task Expired_challenge_is_rejected()
    {
        var fixture = new Fixture();
        var issued = await fixture.Service.IssueAsync(Request(TimeSpan.FromMinutes(1)));
        using var connectorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        fixture.Time.Advance(TimeSpan.FromMinutes(1));

        var result = await fixture.Service.RedeemAsync(Redemption(issued, connectorKey));

        Assert.Equal(ExternalEngineEnrollmentRedeemFailure.Expired, result.Failure);
    }

    [Theory]
    [InlineData("organization")]
    [InlineData("workspace")]
    [InlineData("connection")]
    public async Task Cross_scope_redemption_is_rejected(string changedField)
    {
        var fixture = new Fixture();
        var issued = await fixture.Service.IssueAsync(Request());
        using var connectorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = Redemption(issued, connectorKey);
        var changedConnectionId = Guid.NewGuid();
        request = changedField switch
        {
            "organization" => Resign(request with { OrganizationId = Guid.NewGuid() }, connectorKey),
            "workspace" => Resign(request with { WorkspaceId = Guid.NewGuid() }, connectorKey),
            "connection" => Resign(request with
            {
                ConnectionId = changedConnectionId,
                Audience = ExternalEngineEnrollmentDefaults.AudienceFor(changedConnectionId)
            }, connectorKey),
            _ => throw new ArgumentOutOfRangeException(nameof(changedField))
        };

        var result = await fixture.Service.RedeemAsync(request);

        Assert.False(result.Succeeded);
        Assert.Equal(ExternalEngineEnrollmentRedeemFailure.InvalidRequest, result.Failure);
    }

    [Fact]
    public async Task Wrong_purpose_or_audience_is_rejected_before_store_consumption()
    {
        var fixture = new Fixture();
        var issued = await fixture.Service.IssueAsync(Request());
        using var connectorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var valid = Redemption(issued, connectorKey);

        var wrongPurpose = await fixture.Service.RedeemAsync(Resign(valid with { Purpose = "infrastructure-agent.enroll" }, connectorKey));
        var wrongAudience = await fixture.Service.RedeemAsync(Resign(valid with { Audience = "urn:elsa:another-audience" }, connectorKey));
        var accepted = await fixture.Service.RedeemAsync(valid);

        Assert.Equal(ExternalEngineEnrollmentRedeemFailure.InvalidRequest, wrongPurpose.Failure);
        Assert.Equal(ExternalEngineEnrollmentRedeemFailure.InvalidRequest, wrongAudience.Failure);
        Assert.True(accepted.Succeeded);
    }

    [Fact]
    public async Task Wrong_challenge_or_signature_cannot_redeem()
    {
        var fixture = new Fixture();
        var issued = await fixture.Service.IssueAsync(Request());
        using var connectorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var attackerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var valid = Redemption(issued, connectorKey);
        var wrongChallenge = Resign(valid with { Challenge = Challenge() }, connectorKey);
        var wrongSignature = valid with
        {
            Signature = ExternalEngineEnrollmentProtocol.Sign(
                attackerKey,
                RedemptionPayload(valid))
        };

        var challengeResult = await fixture.Service.RedeemAsync(wrongChallenge);
        var signatureResult = await fixture.Service.RedeemAsync(wrongSignature);
        var accepted = await fixture.Service.RedeemAsync(valid);

        Assert.Equal(ExternalEngineEnrollmentRedeemFailure.InvalidRequest, challengeResult.Failure);
        Assert.Equal(ExternalEngineEnrollmentRedeemFailure.InvalidProof, signatureResult.Failure);
        Assert.True(accepted.Succeeded);
    }

    [Fact]
    public async Task Malformed_or_non_p256_public_keys_are_rejected()
    {
        var fixture = new Fixture();
        var issued = await fixture.Service.IssueAsync(Request());
        using var p256 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var valid = Redemption(issued, p256);

        var malformed = await fixture.Service.RedeemAsync(valid with { PublicKey = "not-base64url", Signature = "not-a-signature" });
        var p384Bytes = p384.ExportSubjectPublicKeyInfo();
        var p384Request = valid with { PublicKey = ExternalEngineEnrollmentProtocol.Base64UrlEncode(p384Bytes) };
        var p384Payload = ExternalEngineEnrollmentProtocol.CreateRedemptionPayload(
            p384Request.ChallengeId,
            p384Request.OrganizationId,
            p384Request.WorkspaceId,
            p384Request.ConnectionId,
            p384Request.Purpose,
            p384Request.Audience,
            ExternalEngineEnrollmentProtocol.HashChallenge(p384Request.Challenge),
            ExternalEngineEnrollmentProtocol.Base64UrlEncode(SHA256.HashData(p384Bytes)));
        var unsupported = await fixture.Service.RedeemAsync(p384Request with
        {
            Signature = ExternalEngineEnrollmentProtocol.Base64UrlEncode(p384.SignData(
                p384Payload,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
        });

        Assert.Equal(ExternalEngineEnrollmentRedeemFailure.InvalidRequest, malformed.Failure);
        Assert.Equal(ExternalEngineEnrollmentRedeemFailure.InvalidRequest, unsupported.Failure);
    }

    [Fact]
    public async Task Enrolled_private_key_is_required_for_later_connector_proof()
    {
        var fixture = new Fixture();
        var issued = await fixture.Service.IssueAsync(Request());
        using var connectorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var attackerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var redeemed = await fixture.Service.RedeemAsync(Redemption(issued, connectorKey));
        var unsigned = Proof(redeemed.Identity!);
        var valid = unsigned with
        {
            Signature = ExternalEngineEnrollmentProtocol.Sign(
                connectorKey,
                ExternalEngineEnrollmentProtocol.CreateConnectorProofPayload(unsigned))
        };
        var attacker = unsigned with
        {
            Signature = ExternalEngineEnrollmentProtocol.Sign(
                attackerKey,
                ExternalEngineEnrollmentProtocol.CreateConnectorProofPayload(unsigned))
        };
        var capturedChallenge = unsigned with { Signature = issued.Challenge };

        Assert.True(await fixture.Service.VerifyConnectorSignatureAsync(valid, valid.Operation, valid.PayloadDigest));
        Assert.False(await fixture.Service.VerifyConnectorSignatureAsync(attacker, attacker.Operation, attacker.PayloadDigest));
        Assert.False(await fixture.Service.VerifyConnectorSignatureAsync(capturedChallenge, capturedChallenge.Operation, capturedChallenge.PayloadDigest));
    }

    [Fact]
    public async Task Request_diagnostics_redact_enrollment_and_proof_material()
    {
        var fixture = new Fixture();
        var issued = await fixture.Service.IssueAsync(Request());
        using var connectorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var redemption = Redemption(issued, connectorKey);
        var identity = (await fixture.Service.RedeemAsync(redemption)).Identity!;
        var proof = SignProof(Proof(identity), connectorKey);

        Assert.DoesNotContain(redemption.Challenge, redemption.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(redemption.PublicKey, redemption.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(redemption.Signature, redemption.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(proof.Nonce, proof.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(proof.Signature, proof.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Connector_proof_is_bound_to_workspace_connection_operation_and_payload()
    {
        var fixture = new Fixture();
        var issued = await fixture.Service.IssueAsync(Request());
        using var connectorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var identity = (await fixture.Service.RedeemAsync(Redemption(issued, connectorKey))).Identity!;
        var proof = Proof(identity);

        var wrongWorkspace = SignProof(proof with { WorkspaceId = Guid.NewGuid() }, connectorKey);
        var wrongConnection = SignProof(proof with { ConnectionId = Guid.NewGuid() }, connectorKey);
        var wrongOperation = SignProof(proof with { Operation = "infrastructure.reconcile" }, connectorKey);
        var wrongPayload = SignProof(proof with { PayloadDigest = Digest("different") }, connectorKey);

        Assert.False(await fixture.Service.VerifyConnectorSignatureAsync(wrongWorkspace, proof.Operation, proof.PayloadDigest));
        Assert.False(await fixture.Service.VerifyConnectorSignatureAsync(wrongConnection, proof.Operation, proof.PayloadDigest));
        Assert.False(await fixture.Service.VerifyConnectorSignatureAsync(wrongOperation, proof.Operation, proof.PayloadDigest));
        Assert.False(await fixture.Service.VerifyConnectorSignatureAsync(wrongPayload, proof.Operation, proof.PayloadDigest));
    }

    [Fact]
    public async Task Connector_proof_is_time_bounded_and_single_use()
    {
        var fixture = new Fixture();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var identity = (await fixture.Service.RedeemAsync(Redemption(await fixture.Service.IssueAsync(Request()), key))).Identity!;
        var valid = SignProof(Proof(identity), key);
        var stale = SignProof(Proof(identity) with
        {
            IssuedAt = Now.Subtract(ExternalEngineEnrollmentDefaults.MaximumProofAge),
            Nonce = Challenge()
        }, key);
        var future = SignProof(Proof(identity) with { IssuedAt = Now.AddSeconds(1), Nonce = Challenge() }, key);

        var first = await fixture.Service.VerifyConnectorProofAsync(valid, valid.Operation, valid.PayloadDigest);
        var replay = await fixture.Service.VerifyConnectorProofAsync(valid, valid.Operation, valid.PayloadDigest);

        Assert.True(first.Succeeded);
        Assert.Equal(ExternalEngineConnectorProofFailure.Replay, replay.Failure);
        Assert.Equal(ExternalEngineConnectorProofFailure.Expired,
            (await fixture.Service.VerifyConnectorProofAsync(stale, stale.Operation, stale.PayloadDigest)).Failure);
        Assert.Equal(ExternalEngineConnectorProofFailure.Future,
            (await fixture.Service.VerifyConnectorProofAsync(future, future.Operation, future.PayloadDigest)).Failure);
    }

    [Fact]
    public async Task Rotation_accepts_the_previous_key_only_inside_the_bounded_overlap()
    {
        var fixture = new Fixture();
        using var currentKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var nextKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var laterKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var identity = (await fixture.Service.RedeemAsync(Redemption(await fixture.Service.IssueAsync(Request()), currentKey))).Identity!;
        var overlap = TimeSpan.FromMinutes(2);
        var nextPublicKey = ExternalEngineEnrollmentProtocol.ExportPublicKey(nextKey);
        var rotation = SignProof(Proof(identity) with
        {
            Operation = ExternalEngineEnrollmentDefaults.RotationOperation,
            PayloadDigest = ExternalEngineEnrollmentProtocol.CreateRotationPayloadDigest(nextPublicKey, overlap)
        }, currentKey);

        var rotated = await fixture.Service.RotateConnectorKeyAsync(
            new ExternalEngineConnectorKeyRotationRequest(rotation, nextPublicKey, overlap));

        Assert.True(rotated.Succeeded);
        Assert.Equal(2, rotated.Identity!.KeyVersion);
        Assert.Equal(1, rotated.Identity.PreviousKeyVersion);

        fixture.Time.Advance(overlap.Subtract(TimeSpan.FromMilliseconds(1)));
        var oldBeforeExpiry = SignProof(Proof(identity) with
        {
            IssuedAt = fixture.Time.GetUtcNow(),
            Nonce = Challenge()
        }, currentKey);
        Assert.True((await fixture.Service.VerifyConnectorProofAsync(
            oldBeforeExpiry, oldBeforeExpiry.Operation, oldBeforeExpiry.PayloadDigest)).Succeeded);

        var laterPublicKey = ExternalEngineEnrollmentProtocol.ExportPublicKey(laterKey);
        var secondRotation = SignProof(Proof(rotated.Identity) with
        {
            IssuedAt = fixture.Time.GetUtcNow(),
            Nonce = Challenge(),
            Operation = ExternalEngineEnrollmentDefaults.RotationOperation,
            PayloadDigest = ExternalEngineEnrollmentProtocol.CreateRotationPayloadDigest(laterPublicKey, overlap)
        }, nextKey);
        Assert.Equal(
            ExternalEngineConnectorProofFailure.InvalidRequest,
            (await fixture.Service.RotateConnectorKeyAsync(
                new ExternalEngineConnectorKeyRotationRequest(secondRotation, laterPublicKey, overlap))).Failure);

        fixture.Time.Advance(TimeSpan.FromMilliseconds(1));
        var oldAtExpiry = SignProof(Proof(identity) with
        {
            IssuedAt = fixture.Time.GetUtcNow(),
            Nonce = Challenge()
        }, currentKey);
        Assert.Equal(
            ExternalEngineConnectorProofFailure.KeyVersionMismatch,
            (await fixture.Service.VerifyConnectorProofAsync(
                oldAtExpiry, oldAtExpiry.Operation, oldAtExpiry.PayloadDigest)).Failure);

        var current = SignProof(Proof(rotated.Identity) with
        {
            IssuedAt = fixture.Time.GetUtcNow(),
            Nonce = Challenge()
        }, nextKey);
        Assert.True((await fixture.Service.VerifyConnectorProofAsync(
            current, current.Operation, current.PayloadDigest)).Succeeded);
    }

    [Fact]
    public async Task Revocation_is_immediate_and_recovery_requires_a_fresh_pairing_challenge()
    {
        var fixture = new Fixture();
        using var lostKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var replacementKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var identity = (await fixture.Service.RedeemAsync(Redemption(await fixture.Service.IssueAsync(Request()), lostKey))).Identity!;
        var preRevocationChallenge = await fixture.Service.IssueAsync(Request());
        fixture.Time.Advance(TimeSpan.FromSeconds(1));
        var revoked = await fixture.Service.RevokeConnectorForRecoveryAsync(
            identity.OrganizationId,
            identity.WorkspaceId,
            identity.ConnectionId,
            identity.Id);

        Assert.NotNull(revoked?.RevokedAt);
        Assert.Equal(
            ExternalEngineEnrollmentRedeemFailure.InvalidRequest,
            (await fixture.Service.RedeemAsync(Redemption(preRevocationChallenge, replacementKey))).Failure);
        var afterRevocation = SignProof(Proof(identity) with { Nonce = Challenge() }, lostKey);
        Assert.Equal(
            ExternalEngineConnectorProofFailure.Revoked,
            (await fixture.Service.VerifyConnectorProofAsync(
                afterRevocation, afterRevocation.Operation, afterRevocation.PayloadDigest)).Failure);

        fixture.Time.Advance(TimeSpan.FromSeconds(1));
        var repaired = await fixture.Service.RedeemAsync(
            Redemption(await fixture.Service.IssueAsync(Request()), replacementKey));

        Assert.True(repaired.Succeeded);
        Assert.Equal(identity.Id, repaired.Identity!.Id);
        Assert.Equal(identity.KeyVersion + 1, repaired.Identity.KeyVersion);
        Assert.Null(repaired.Identity.RevokedAt);
        Assert.Null(repaired.Identity.PreviousPublicKey);
        var oldKeyAfterRepair = SignProof(Proof(identity) with { Nonce = Challenge() }, lostKey);
        Assert.Equal(
            ExternalEngineConnectorProofFailure.KeyVersionMismatch,
            (await fixture.Service.VerifyConnectorProofAsync(
                oldKeyAfterRepair, oldKeyAfterRepair.Operation, oldKeyAfterRepair.PayloadDigest)).Failure);
    }

    [Fact]
    public void Canonical_payload_changes_when_any_security_binding_changes()
    {
        var identity = Identity();
        var proof = Proof(identity);
        var baseline = ExternalEngineEnrollmentProtocol.CreateConnectorProofPayload(proof);

        Assert.NotEqual(baseline, ExternalEngineEnrollmentProtocol.CreateConnectorProofPayload(proof with { Operation = "heartbeat.v2" }));
        Assert.NotEqual(baseline, ExternalEngineEnrollmentProtocol.CreateConnectorProofPayload(proof with { Nonce = Challenge() }));
        Assert.NotEqual(baseline, ExternalEngineEnrollmentProtocol.CreateConnectorProofPayload(proof with { PayloadDigest = Digest("other") }));
    }

    [Fact]
    public void Redemption_canonicalization_matches_the_version_one_golden_vector()
    {
        var payload = ExternalEngineEnrollmentProtocol.CreateRedemptionPayload(
            Guid.Parse("40000000-0000-0000-0000-000000000001"),
            OrganizationId,
            WorkspaceId,
            ConnectionId,
            ExternalEngineEnrollmentDefaults.PairingPurpose,
            ExternalEngineEnrollmentDefaults.AudienceFor(ConnectionId),
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
            "AQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQE");

        Assert.Equal(
            "00000031656C73612D636F6E74726F6C2E65787465726E616C2D656E67696E652D656E726F6C6C6D656E742E72656465656D2E76310000002434303030303030302D303030302D303030302D303030302D3030303030303030303030310000002431303030303030302D303030302D303030302D303030302D3030303030303030303030310000002432303030303030302D303030302D303030302D303030302D3030303030303030303030310000002433303030303030302D303030302D303030302D303030302D3030303030303030303030310000001465787465726E616C2D656E67696E652E706169720000004775726E3A656C73613A65787465726E616C2D656E67696E652D636F6E6E6563746F723A33303030303030302D303030302D303030302D303030302D3030303030303030303030310000002B414141414141414141414141414141414141414141414141414141414141414141414141414141414141410000002B41514542415145424151454241514542415145424151454241514542415145424151454241514542415145",
            Convert.ToHexString(payload));
    }

    private static ExternalEngineEnrollmentIssueRequest Request(TimeSpan? lifetime = null) =>
        new(OrganizationId, WorkspaceId, ConnectionId, lifetime);

    private static ExternalEngineEnrollmentRedeemRequest Redemption(
        ExternalEngineEnrollmentIssueResult issued,
        ECDsa key)
    {
        var publicKey = ExternalEngineEnrollmentProtocol.ExportPublicKey(key);
        var request = new ExternalEngineEnrollmentRedeemRequest(
            issued.ChallengeId,
            issued.OrganizationId,
            issued.WorkspaceId,
            issued.ConnectionId,
            issued.Purpose,
            issued.Audience,
            issued.Challenge,
            publicKey,
            string.Empty);
        return Resign(request, key);
    }

    private static ExternalEngineEnrollmentRedeemRequest Resign(
        ExternalEngineEnrollmentRedeemRequest request,
        ECDsa key) =>
        request with { Signature = ExternalEngineEnrollmentProtocol.Sign(key, RedemptionPayload(request)) };

    private static byte[] RedemptionPayload(ExternalEngineEnrollmentRedeemRequest request) =>
        ExternalEngineEnrollmentProtocol.CreateRedemptionPayload(
            request.ChallengeId,
            request.OrganizationId,
            request.WorkspaceId,
            request.ConnectionId,
            request.Purpose,
            request.Audience,
            ExternalEngineEnrollmentProtocol.HashChallenge(request.Challenge),
            ExternalEngineEnrollmentProtocol.PublicKeyThumbprint(request.PublicKey));

    private static ExternalEngineConnectorProof Proof(ExternalEngineConnectorIdentity identity) =>
        new(
            identity.Id,
            identity.OrganizationId,
            identity.WorkspaceId,
            identity.ConnectionId,
            identity.Audience,
            identity.KeyVersion,
            "heartbeat.submit",
            Digest("payload"),
            Now,
            Challenge(),
            string.Empty);

    private static ExternalEngineConnectorProof SignProof(ExternalEngineConnectorProof proof, ECDsa key) =>
        proof with
        {
            Signature = ExternalEngineEnrollmentProtocol.Sign(
                key,
                ExternalEngineEnrollmentProtocol.CreateConnectorProofPayload(proof))
        };

    private static ExternalEngineConnectorIdentity Identity() =>
        new(
            Guid.NewGuid(),
            OrganizationId,
            WorkspaceId,
            ConnectionId,
            ExternalEngineEnrollmentDefaults.AudienceFor(ConnectionId),
            ExternalEngineEnrollmentDefaults.KeyAlgorithm,
            1,
            "public-key",
            "thumbprint",
            Now);

    private static string Challenge() => ExternalEngineEnrollmentProtocol.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    private static string Digest(string value) =>
        ExternalEngineEnrollmentProtocol.Base64UrlEncode(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));

    private sealed class Fixture
    {
        public AdjustableTimeProvider Time { get; } = new(Now);
        public InMemoryExternalEngineEnrollmentStore Store { get; } = new();
        public ExternalEngineEnrollmentService Service { get; }

        public Fixture()
        {
            Service = new ExternalEngineEnrollmentService(Store, Time);
        }
    }

    private sealed class AdjustableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan value) => _now = _now.Add(value);
    }
}
