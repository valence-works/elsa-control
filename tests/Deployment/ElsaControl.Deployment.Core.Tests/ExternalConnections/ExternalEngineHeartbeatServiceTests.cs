using System.Security.Cryptography;
using System.Text;
using ElsaControl.Deployment.Core.ExternalConnections;
using Xunit;

namespace ElsaControl.Deployment.Core.Tests.ExternalConnections;

public sealed class ExternalEngineHeartbeatServiceTests
{
    private static readonly Guid OrganizationId = Guid.Parse("10000000-0000-0000-0000-000000000482");
    private static readonly Guid WorkspaceId = Guid.Parse("20000000-0000-0000-0000-000000000482");
    private static readonly Guid ConnectionId = Guid.Parse("30000000-0000-0000-0000-000000000482");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-17T10:00:00Z");
    private const string ImageDigest = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task Connector_claimed_valence_runtime_with_matching_catalog_digests_stays_self_reported()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var fixture = await Fixture.CreateAsync(key);
        var report = Report(1, components: [new("runtime", ImageDigest)], capabilities:
            [ExternalEngineHeartbeatService.StatusCapability, ExternalEngineHeartbeatService.StudioCapability]);

        var result = await fixture.Service.SubmitAsync(fixture.Request(report, key));

        Assert.NotNull(result);
        Assert.Equal(ExternalEngineHeartbeatStatus.Accepted, result.Status);
        Assert.Equal(ExternalEngineReleaseEvidenceLevel.SelfReported, result.Connection!.ReleaseEvidenceLevel);
        Assert.Null(result.Connection.ReleaseEvidenceReference);
        Assert.Equal("valence-runtime", result.Connection.ObservedDistribution);
        Assert.Equal("3.8.1", result.Connection.ObservedVersion);
        Assert.Equal("server", result.Connection.ObservedRuntimeKind);
        Assert.Equal(2, result.Connection.Capabilities.Count);
        Assert.Equal("https://studio.example.test/elsa/", result.Connection.StudioDestinationCandidate);
        Assert.NotNull(result.Connection.StudioDestinationCandidateId);
        Assert.Null(result.Connection.StudioDestination);
        Assert.Equal(ExternalEngineHeartbeatFreshness.Fresh,
            ExternalEngineConnectionFreshness.Classify(result.Connection, fixture.Time.GetUtcNow()));
    }

    [Fact]
    public void Protocol_v1_canonical_payload_matches_the_portable_golden_vector()
    {
        var report = Report(
            1,
            components:
            [
                new("worker", "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"),
                new("runtime", ImageDigest)
            ],
            capabilities:
            [
                ExternalEngineHeartbeatService.StudioCapability,
                ExternalEngineHeartbeatService.StatusCapability,
                ExternalEngineHeartbeatService.StudioCapability
            ]);

        var canonical = Encoding.UTF8.GetString(ExternalEngineHeartbeatService.CreateCanonicalPayload(report));

        Assert.Equal(
            "{\"sequence\":1,\"observedAt\":\"2026-09-17T10:00:00.0000000Z\",\"connectorProtocol\":\"1\",\"connectorVersion\":\"1.4.0\",\"runnerId\":\"AAAAAAAAAAAAAAAAAAAAAA\",\"runtimeHealth\":\"healthy\",\"runtimeKind\":\"server\",\"observedDistribution\":\"valence-runtime\",\"observedVersion\":\"3.8.1\",\"studioDestination\":\"https://studio.example.test/elsa/\",\"capabilities\":[\"connection.status\",\"studio.open\"],\"components\":[{\"id\":\"runtime\",\"imageDigest\":\"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"},{\"id\":\"worker\",\"imageDigest\":\"sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\"}]}",
            canonical);
    }

    [Fact]
    public void Optional_display_name_is_omitted_from_canonical_bytes_unless_present()
    {
        var absent = Report(1);
        var present = Report(1) with { DisplayName = "Acme Orders Engine" };

        Assert.Equal(
            Encoding.UTF8.GetString(ExternalEngineHeartbeatService.CreateCanonicalPayload(absent)),
            Encoding.UTF8.GetString(ExternalEngineHeartbeatService.CreateCanonicalPayload(Report(1))));
        Assert.DoesNotContain("displayName", Encoding.UTF8.GetString(ExternalEngineHeartbeatService.CreateCanonicalPayload(absent)), StringComparison.Ordinal);
        Assert.Equal(
            "{\"sequence\":1,\"observedAt\":\"2026-09-17T10:00:00.0000000Z\",\"connectorProtocol\":\"1\",\"connectorVersion\":\"1.4.0\",\"runnerId\":\"AAAAAAAAAAAAAAAAAAAAAA\",\"displayName\":\"Acme Orders Engine\",\"runtimeHealth\":\"healthy\",\"runtimeKind\":\"server\",\"observedDistribution\":\"valence-runtime\",\"observedVersion\":\"3.8.1\",\"studioDestination\":\"https://studio.example.test/elsa/\",\"capabilities\":[\"connection.status\"],\"components\":[]}",
            Encoding.UTF8.GetString(ExternalEngineHeartbeatService.CreateCanonicalPayload(present)));
    }

    [Theory]
    [InlineData("E")]
    [InlineData("Acme Orders Engine")]
    public async Task Host_display_name_replaces_the_pairing_label_and_does_not_change_evidence(string displayName)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var fixture = await Fixture.CreateAsync(key);
        Assert.Equal("Engine", fixture.Store.Connection.DisplayName);
        var report = Report(1, components: [new("runtime", ImageDigest)]) with { DisplayName = displayName };

        var result = await fixture.Service.SubmitAsync(fixture.Request(report, key));

        Assert.Equal(ExternalEngineHeartbeatStatus.Accepted, result!.Status);
        Assert.Equal(displayName, result.Connection!.DisplayName);
        Assert.Equal(ExternalEngineReleaseEvidenceLevel.SelfReported, result.Connection.ReleaseEvidenceLevel);
        Assert.Null(result.Connection.ReleaseEvidenceReference);
    }

    [Fact]
    public async Task Absent_host_display_name_leaves_the_pairing_label_unchanged()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var fixture = await Fixture.CreateAsync(key);
        var result = await fixture.Service.SubmitAsync(fixture.Request(Report(1), key));

        Assert.Equal(ExternalEngineHeartbeatStatus.Accepted, result!.Status);
        Assert.Equal("Engine", result.Connection!.DisplayName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" Acme")]
    [InlineData("Acme ")]
    public void Untrimmed_or_blank_host_display_name_is_rejected(string displayName)
    {
        var report = Report(1) with { DisplayName = displayName };
        Assert.Throws<ArgumentException>(() => ExternalEngineHeartbeatService.CreateCanonicalPayload(report));
    }

    [Fact]
    public void Runner_lease_covers_three_max_interval_beats_inside_the_freshness_window()
    {
        Assert.True(ExternalEngineHeartbeatService.RunnerLeaseTtl
            >= 3 * ExternalEngineHeartbeatService.MaxHeartbeatInterval);
        Assert.True(ExternalEngineHeartbeatService.RunnerLeaseTtl + ExternalEngineHeartbeatService.MaxHeartbeatInterval
            < ExternalEngineHeartbeatService.FreshnessWindow);
        Assert.Equal(TimeSpan.FromSeconds(5), ExternalEngineHeartbeatService.MinimumInterval);
        Assert.Equal(TimeSpan.FromSeconds(15), ExternalEngineHeartbeatService.DefaultHeartbeatInterval);
        Assert.Equal(TimeSpan.FromSeconds(15), ExternalEngineHeartbeatService.MaxHeartbeatInterval);
        Assert.Equal(TimeSpan.FromSeconds(45), ExternalEngineHeartbeatService.RunnerLeaseTtl);
        Assert.Equal(TimeSpan.FromSeconds(90), ExternalEngineHeartbeatService.FreshnessWindow);
    }

    [Theory]
    [InlineData("Acme\nOrders")]
    [InlineData("Acme\tOrders")]
    [InlineData("Acme\u202EOrders")]
    [InlineData("Acme\u2066Orders")]
    [InlineData("Acme\u200BOrders")]
    [InlineData("Acme\uFEFFOrders")]
    [InlineData("Acme\u2028Orders")]
    [InlineData("Acme\u2029Orders")]
    [InlineData("Acme\u061COrders")]
    [InlineData("Acme\u2060Orders")]
    [InlineData("Acme\u2061Orders")]
    [InlineData("Acme\u2062Orders")]
    [InlineData("Acme\u2063Orders")]
    [InlineData("Acme\u2064Orders")]
    [InlineData("Acme\U000E0001Orders")]
    [InlineData("Acme\U000E0020Orders")]
    [InlineData("Acme\U000E0041Orders")]
    [InlineData("Acme\U000E007FOrders")]
    public void Invalid_host_display_name_is_rejected(string displayName)
    {
        var report = Report(1) with { DisplayName = displayName };
        Assert.Throws<ArgumentException>(() => ExternalEngineHeartbeatService.CreateCanonicalPayload(report));
    }

    [Fact]
    public void Display_name_longer_than_eighty_characters_is_rejected()
    {
        var report = Report(1) with { DisplayName = new string('x', 81) };
        Assert.Throws<ArgumentException>(() => ExternalEngineHeartbeatService.CreateCanonicalPayload(report));
    }

    [Fact]
    public async Task Eighty_character_host_display_name_is_accepted()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var fixture = await Fixture.CreateAsync(key);
        var displayName = new string('A', 80);

        var result = await fixture.Service.SubmitAsync(fixture.Request(Report(1) with { DisplayName = displayName }, key));

        Assert.Equal(displayName, result!.Connection!.DisplayName);
    }

    [Fact]
    public void Display_name_length_is_counted_in_unicode_code_points_after_nfc()
    {
        var composed = "é";
        var decomposed = "e\u0301";
        Assert.Equal(
            Encoding.UTF8.GetString(ExternalEngineHeartbeatService.CreateCanonicalPayload(Report(1) with { DisplayName = composed })),
            Encoding.UTF8.GetString(ExternalEngineHeartbeatService.CreateCanonicalPayload(Report(1) with { DisplayName = decomposed })));

        var eightySupplementary = string.Concat(Enumerable.Repeat("😀", 80));
        Assert.Equal(
            eightySupplementary,
            ExternalEngineHeartbeatService.NormalizeHostDisplayName(eightySupplementary));
        Assert.Throws<ArgumentException>(() =>
            ExternalEngineHeartbeatService.NormalizeHostDisplayName(string.Concat(Enumerable.Repeat("😀", 81))));
    }

    [Fact]
    public void Non_ascii_display_name_uses_javascript_encoder_default_escapes()
    {
        var report = Report(1) with { DisplayName = "Café & π" };
        Assert.Contains(
            "\"displayName\":\"Caf\\u00E9 \\u0026 \\u03C0\"",
            Encoding.UTF8.GetString(ExternalEngineHeartbeatService.CreateCanonicalPayload(report)),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Version_only_or_mismatched_component_evidence_stays_self_reported()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var fixture = await Fixture.CreateAsync(key);
        var report = Report(1, components:
            [new("runtime", "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")]);

        var result = await fixture.Service.SubmitAsync(fixture.Request(report, key));

        Assert.Equal(ExternalEngineHeartbeatStatus.Accepted, result!.Status);
        Assert.Equal(ExternalEngineReleaseEvidenceLevel.SelfReported, result.Connection!.ReleaseEvidenceLevel);
        Assert.Null(result.Connection.ReleaseEvidenceReference);
        Assert.Equal("valence-runtime", result.Connection.ObservedDistribution);
        Assert.Equal("3.8.1", result.Connection.ObservedVersion);
    }

    [Fact]
    public async Task Connector_claimed_supported_elsa_oss_release_stays_self_reported()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var fixture = await Fixture.CreateAsync(key);
        var report = Report(1, distribution: "elsa-oss", components: [new("runtime", ImageDigest)]);

        var result = await fixture.Service.SubmitAsync(fixture.Request(report, key));

        Assert.Equal(ExternalEngineHeartbeatStatus.Accepted, result!.Status);
        Assert.Equal(ExternalEngineReleaseEvidenceLevel.SelfReported, result.Connection!.ReleaseEvidenceLevel);
        Assert.Null(result.Connection.ReleaseEvidenceReference);
        Assert.Equal("elsa-oss", result.Connection.ObservedDistribution);
        Assert.Equal("3.8.1", result.Connection.ObservedVersion);
    }

    [Fact]
    public async Task Missing_observed_release_identity_stays_none()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var fixture = await Fixture.CreateAsync(key);
        var report = Report(1) with { ObservedDistribution = null, ObservedVersion = null };

        var result = await fixture.Service.SubmitAsync(fixture.Request(report, key));

        Assert.Equal(ExternalEngineHeartbeatStatus.Accepted, result!.Status);
        Assert.Equal(ExternalEngineReleaseEvidenceLevel.None, result.Connection!.ReleaseEvidenceLevel);
        Assert.Null(result.Connection.ReleaseEvidenceReference);
        Assert.Null(result.Connection.ObservedDistribution);
        Assert.Null(result.Connection.ObservedVersion);
    }

    [Fact]
    public async Task Unsupported_protocol_is_reported_only_after_proof_verification_and_other_invalid_reports_do_not_change_it()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var fixture = await Fixture.CreateAsync(key);
        var unsupportedReport = Report(1) with { ConnectorProtocol = "2" };
        var invalidProofRequest = fixture.Request(unsupportedReport, key);
        var invalidProof = invalidProofRequest with
        {
            Proof = invalidProofRequest.Proof with { Signature = "invalid" }
        };

        var denied = await fixture.Service.SubmitAsync(invalidProof);
        Assert.Equal(ExternalEngineHeartbeatStatus.ProofDenied, denied!.Status);
        Assert.Equal(ExternalEngineConnectorCompatibilityStatus.Unknown, fixture.Store.Connection.ConnectorCompatibilityStatus);

        var unsupported = await fixture.Service.SubmitAsync(fixture.Request(unsupportedReport, key));
        Assert.Equal(ExternalEngineHeartbeatStatus.UnsupportedProtocol, unsupported!.Status);
        Assert.Equal(ExternalEngineConnectorCompatibilityStatus.UnsupportedProtocol,
            unsupported.Connection!.ConnectorCompatibilityStatus);
        Assert.Equal(Now, unsupported.Connection.ConnectorCompatibilityObservedAt);
        Assert.Null(unsupported.Connection.ConnectorProtocol);
        Assert.Equal(Now, unsupported.Connection.LastAuthenticatedAt);
        Assert.Equal(1, unsupported.Connection.LastHeartbeatSequence);

        fixture.Time.Advance(TimeSpan.FromSeconds(1));
        var unknown = await fixture.Service.SubmitAsync(
            fixture.Request(Report(2, capabilities: ["engine.delete"]), key));
        Assert.Equal(ExternalEngineHeartbeatStatus.InvalidReport, unknown!.Status);
        Assert.Empty(fixture.Store.Connection.Capabilities);
        Assert.Equal(ExternalEngineConnectorCompatibilityStatus.UnsupportedProtocol,
            fixture.Store.Connection.ConnectorCompatibilityStatus);

        var unsupportedReplay = await fixture.Service.SubmitAsync(fixture.Request(unsupportedReport, key));
        Assert.Equal(ExternalEngineHeartbeatStatus.OutOfOrder, unsupportedReplay!.Status);
        var sameSequenceRecovery = await fixture.Service.SubmitAsync(fixture.Request(Report(1), key));
        Assert.Equal(ExternalEngineHeartbeatStatus.OutOfOrder, sameSequenceRecovery!.Status);

        fixture.Time.Advance(TimeSpan.FromSeconds(5));
        var recovered = await fixture.Service.SubmitAsync(fixture.Request(Report(2), key));
        Assert.Equal(ExternalEngineHeartbeatStatus.Accepted, recovered!.Status);
        Assert.Equal(ExternalEngineConnectorCompatibilityStatus.Compatible,
            recovered.Connection!.ConnectorCompatibilityStatus);
        Assert.Equal(2, recovered.Connection.LastHeartbeatSequence);
    }

    [Fact]
    public async Task Proof_replay_and_new_proof_with_old_sequence_do_not_refresh_last_seen()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var fixture = await Fixture.CreateAsync(key);
        var firstRequest = fixture.Request(Report(1), key);
        var first = await fixture.Service.SubmitAsync(firstRequest);
        var acceptedAt = first!.Connection!.LastAuthenticatedAt;

        fixture.Time.Advance(TimeSpan.FromSeconds(6));
        var replay = await fixture.Service.SubmitAsync(firstRequest);
        Assert.Equal(ExternalEngineHeartbeatStatus.ProofDenied, replay!.Status);
        Assert.Equal(ExternalEngineConnectorProofFailure.Replay, replay.ProofFailure);

        var oldSequence = await fixture.Service.SubmitAsync(fixture.Request(Report(1), key));
        Assert.Equal(ExternalEngineHeartbeatStatus.OutOfOrder, oldSequence!.Status);
        Assert.Equal(acceptedAt, fixture.Store.Connection.LastAuthenticatedAt);
    }

    [Fact]
    public async Task Fresh_connection_projects_stale_then_recovers_with_a_new_heartbeat()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var fixture = await Fixture.CreateAsync(key);
        Assert.Equal(ExternalEngineHeartbeatFreshness.Waiting,
            ExternalEngineConnectionFreshness.Classify(fixture.Store.Connection, fixture.Time.GetUtcNow()));

        var connected = await fixture.Service.SubmitAsync(fixture.Request(Report(1), key));
        Assert.Equal(ExternalEngineConnectionStatus.Connected, connected!.Connection!.Status);

        fixture.Time.Advance(ExternalEngineHeartbeatService.FreshnessWindow + TimeSpan.FromSeconds(1));
        var stale = ExternalEngineConnectionFreshness.Project(fixture.Store.Connection, fixture.Time.GetUtcNow());
        Assert.Equal(ExternalEngineConnectionStatus.Degraded, stale.Status);
        Assert.Equal(ExternalEngineConnectorReachability.Unreachable, stale.ConnectorReachability);
        Assert.Equal(ExternalEngineHeartbeatFreshness.Stale,
            ExternalEngineConnectionFreshness.Classify(stale, fixture.Time.GetUtcNow()));

        var recovered = await fixture.Service.SubmitAsync(fixture.Request(Report(2), key));
        Assert.Equal(ExternalEngineHeartbeatStatus.Accepted, recovered!.Status);
        Assert.Equal(ExternalEngineConnectionStatus.Connected, recovered.Connection!.Status);
        Assert.Equal(ExternalEngineHeartbeatFreshness.Fresh,
            ExternalEngineConnectionFreshness.Classify(recovered.Connection, fixture.Time.GetUtcNow()));
    }

    [Fact]
    public async Task Concurrent_higher_sequence_wins_and_a_later_sequence_retries_against_the_new_version()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var fixture = await Fixture.CreateAsync(key);
        fixture.Store.InjectConcurrentSequence(3);

        var superseded = await fixture.Service.SubmitAsync(fixture.Request(Report(2), key));

        Assert.Equal(ExternalEngineHeartbeatStatus.OutOfOrder, superseded!.Status);
        Assert.Equal(3, fixture.Store.Connection.LastHeartbeatSequence);

        fixture.Time.Advance(TimeSpan.FromSeconds(6));
        fixture.Store.InjectConcurrentSequence(4);
        var newest = await fixture.Service.SubmitAsync(fixture.Request(Report(5), key));
        Assert.Equal(ExternalEngineHeartbeatStatus.Accepted, newest!.Status);
        Assert.Equal(5, fixture.Store.Connection.LastHeartbeatSequence);
    }

    [Fact]
    public async Task First_heartbeat_takes_the_runner_lease_without_a_change_audit()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var fixture = await Fixture.CreateAsync(key);

        var result = await fixture.Service.SubmitAsync(fixture.Request(Report(1), key));

        Assert.Equal(ExternalEngineHeartbeatStatus.Accepted, result!.Status);
        Assert.Equal(ExternalEngineTestRunners.Alpha, result.Connection!.ActiveRunnerId);
        Assert.Equal(Now.Add(ExternalEngineHeartbeatService.RunnerLeaseTtl), result.Connection.RunnerLeaseExpiresAt);
        Assert.DoesNotContain(ExternalEngineHeartbeatService.RunnerChangedAuditAction, fixture.Store.Audits);
    }

    [Fact]
    public async Task Takeover_is_blocked_while_another_runner_holds_a_live_lease()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var fixture = await Fixture.CreateAsync(key);
        var first = await fixture.Service.SubmitAsync(fixture.Request(Report(1), key));
        Assert.Equal(ExternalEngineHeartbeatStatus.Accepted, first!.Status);
        var acceptedSequence = first.Connection!.LastHeartbeatSequence;
        var acceptedLease = first.Connection.RunnerLeaseExpiresAt;

        fixture.Time.Advance(TimeSpan.FromSeconds(6));
        var blocked = await fixture.Service.SubmitAsync(
            fixture.Request(Report(2, runnerId: ExternalEngineTestRunners.Bravo), key));

        Assert.Equal(ExternalEngineHeartbeatStatus.RunnerConflict, blocked!.Status);
        Assert.Equal(ExternalEngineHeartbeatService.RunnerLeaseTtl - TimeSpan.FromSeconds(6), blocked.RetryAfter);
        Assert.Equal(acceptedSequence, fixture.Store.Connection.LastHeartbeatSequence);
        Assert.Equal(ExternalEngineTestRunners.Alpha, fixture.Store.Connection.ActiveRunnerId);
        Assert.Equal(acceptedLease, fixture.Store.Connection.RunnerLeaseExpiresAt);
        Assert.DoesNotContain(ExternalEngineHeartbeatService.RunnerChangedAuditAction, fixture.Store.Audits);
    }

    [Fact]
    public async Task Takeover_after_expiry_resets_the_sequence_baseline_and_is_audited()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var fixture = await Fixture.CreateAsync(key);
        Assert.True((await fixture.Service.SubmitAsync(fixture.Request(Report(9), key)))!.Accepted);

        fixture.Time.Advance(ExternalEngineHeartbeatService.RunnerLeaseTtl);
        var takeover = await fixture.Service.SubmitAsync(
            fixture.Request(Report(1, runnerId: ExternalEngineTestRunners.Bravo), key));

        Assert.Equal(ExternalEngineHeartbeatStatus.Accepted, takeover!.Status);
        Assert.Equal(ExternalEngineTestRunners.Bravo, takeover.Connection!.ActiveRunnerId);
        Assert.Equal(1, takeover.Connection.LastHeartbeatSequence);
        Assert.Equal(Now.Add(ExternalEngineHeartbeatService.RunnerLeaseTtl * 2), takeover.Connection.RunnerLeaseExpiresAt);
        Assert.Contains(ExternalEngineHeartbeatService.RunnerChangedAuditAction, fixture.Store.Audits);
    }

    [Fact]
    public async Task Two_runners_racing_leave_the_first_holder_and_409_the_second()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var fixture = await Fixture.CreateAsync(key);
        fixture.Store.InjectConcurrentSequence(3, ExternalEngineTestRunners.Bravo);

        var raced = await fixture.Service.SubmitAsync(fixture.Request(Report(2), key));

        Assert.Equal(ExternalEngineHeartbeatStatus.RunnerConflict, raced!.Status);
        Assert.Equal(ExternalEngineTestRunners.Bravo, fixture.Store.Connection.ActiveRunnerId);
        Assert.Equal(3, fixture.Store.Connection.LastHeartbeatSequence);
        Assert.False(fixture.Store.AppliedAfterConcurrent);
    }

    [Fact]
    public async Task Rejected_heartbeat_does_not_renew_or_take_the_runner_lease()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var fixture = await Fixture.CreateAsync(key);
        var first = await fixture.Service.SubmitAsync(fixture.Request(Report(1), key));
        var lease = first!.Connection!.RunnerLeaseExpiresAt;
        Assert.Equal(ExternalEngineTestRunners.Alpha, first.Connection.ActiveRunnerId);

        fixture.Time.Advance(TimeSpan.FromSeconds(6));
        var invalid = await fixture.Service.SubmitAsync(
            fixture.Request(Report(2, capabilities: ["engine.delete"]), key));
        Assert.Equal(ExternalEngineHeartbeatStatus.InvalidReport, invalid!.Status);
        Assert.Equal(lease, fixture.Store.Connection.RunnerLeaseExpiresAt);
        Assert.Equal(1, fixture.Store.Connection.LastHeartbeatSequence);

        var outOfOrder = await fixture.Service.SubmitAsync(fixture.Request(Report(1), key));
        Assert.Equal(ExternalEngineHeartbeatStatus.OutOfOrder, outOfOrder!.Status);
        Assert.Equal(lease, fixture.Store.Connection.RunnerLeaseExpiresAt);

        var otherRunner = await fixture.Service.SubmitAsync(
            fixture.Request(Report(2, runnerId: ExternalEngineTestRunners.Bravo), key));
        Assert.Equal(ExternalEngineHeartbeatStatus.RunnerConflict, otherRunner!.Status);
        Assert.Equal(lease, fixture.Store.Connection.RunnerLeaseExpiresAt);
        Assert.Equal(ExternalEngineTestRunners.Alpha, fixture.Store.Connection.ActiveRunnerId);
    }

    [Fact]
    public async Task Host_label_replacement_is_audited_without_the_label_text()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var fixture = await Fixture.CreateAsync(key);

        var result = await fixture.Service.SubmitAsync(
            fixture.Request(Report(1) with { DisplayName = "Acme Orders Engine" }, key));

        Assert.Equal("Acme Orders Engine", result!.Connection!.DisplayName);
        Assert.Contains(ExternalEngineHeartbeatService.LabelChangedAuditAction, fixture.Store.Audits);
        Assert.All(fixture.Store.Audits, action => Assert.DoesNotContain("Acme", action, StringComparison.Ordinal));
    }

    private static ExternalEngineHeartbeatReport Report(
        long sequence,
        string distribution = "valence-runtime",
        IReadOnlyList<ExternalEngineComponentObservation>? components = null,
        IReadOnlyList<string>? capabilities = null,
        string? runnerId = null) =>
        new(
            sequence,
            Now,
            ExternalEngineHeartbeatService.CurrentProtocol,
            "1.4.0",
            ExternalEngineRuntimeHealth.Healthy,
            "server",
            distribution,
            "3.8.1",
            "https://studio.example.test/elsa/",
            capabilities ?? [ExternalEngineHeartbeatService.StatusCapability],
            components ?? [],
            runnerId ?? ExternalEngineTestRunners.Alpha);

    private sealed class Fixture(
        MutableTimeProvider time,
        RecordingConnectionStore store,
        ExternalEngineHeartbeatService service,
        ExternalEngineConnectorIdentity identity)
    {
        private long _nonce;

        public MutableTimeProvider Time { get; } = time;
        public RecordingConnectionStore Store { get; } = store;
        public ExternalEngineHeartbeatService Service { get; } = service;

        public static async Task<Fixture> CreateAsync(ECDsa key)
        {
            var time = new MutableTimeProvider(Now);
            var enrollmentStore = new InMemoryExternalEngineEnrollmentStore();
            var enrollment = new ExternalEngineEnrollmentService(enrollmentStore, time);
            var issued = await enrollment.IssueAsync(new(OrganizationId, WorkspaceId, ConnectionId));
            var publicKey = ExternalEngineEnrollmentProtocol.ExportPublicKey(key);
            var unsigned = new ExternalEngineEnrollmentRedeemRequest(
                issued.ChallengeId, OrganizationId, WorkspaceId, ConnectionId, issued.Purpose, issued.Audience,
                issued.Challenge, publicKey, "");
            var redemption = unsigned with
            {
                Signature = ExternalEngineEnrollmentProtocol.Sign(key,
                    ExternalEngineEnrollmentProtocol.CreateRedemptionPayload(
                        unsigned.ChallengeId, unsigned.OrganizationId, unsigned.WorkspaceId, unsigned.ConnectionId,
                        unsigned.Purpose, unsigned.Audience,
                        ExternalEngineEnrollmentProtocol.HashChallenge(unsigned.Challenge),
                        ExternalEngineEnrollmentProtocol.PublicKeyThumbprint(publicKey)))
            };
            var redeemed = await enrollment.RedeemAsync(redemption);
            Assert.True(redeemed.Succeeded);
            var connection = new ExternalEngineConnection(
                ConnectionId, OrganizationId, WorkspaceId, "Engine", ExternalEngineConnectionStatus.Pending,
                ExternalEngineRuntimeHealth.Unknown, ExternalEngineConnectorReachability.Unknown,
                null, null, null, null, null, ExternalEngineReleaseEvidenceLevel.None, null, [], null,
                redeemed.Identity!.Id, issued.ChallengeId, Now, Now, null, 1);
            var store = new RecordingConnectionStore(connection);
            var service = new ExternalEngineHeartbeatService(store, enrollment, time);
            return new(time, store, service, redeemed.Identity);
        }

        public ExternalEngineHeartbeatRequest Request(ExternalEngineHeartbeatReport report, ECDsa key)
        {
            var proof = new ExternalEngineConnectorProof(
                identity.Id,
                OrganizationId,
                WorkspaceId,
                ConnectionId,
                identity.Audience,
                identity.KeyVersion,
                ExternalEngineHeartbeatService.HeartbeatOperation,
                ExternalEngineHeartbeatService.CreatePayloadDigest(report),
                Time.GetUtcNow(),
                ExternalEngineEnrollmentProtocol.Base64UrlEncode(BitConverter.GetBytes(++_nonce).Concat(RandomNumberGenerator.GetBytes(8)).ToArray()),
                "");
            return new(proof with
            {
                Signature = ExternalEngineEnrollmentProtocol.Sign(key,
                    ExternalEngineEnrollmentProtocol.CreateConnectorProofPayload(proof))
            }, report);
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan value) => _now = _now.Add(value);
    }

    private sealed class RecordingConnectionStore(ExternalEngineConnection connection) : IExternalEngineConnectionStore
    {
        private long? _concurrentSequence;
        private string? _concurrentRunnerId;
        public ExternalEngineConnection Connection { get; private set; } = connection;
        public List<string> Audits { get; } = [];
        public bool AppliedAfterConcurrent { get; private set; }

        public void InjectConcurrentSequence(long sequence, string? runnerId = null)
        {
            _concurrentSequence = sequence;
            _concurrentRunnerId = runnerId;
        }

        public Task<IReadOnlyList<ExternalEngineConnection>> ListAsync(Guid organizationId, Guid workspaceId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ExternalEngineConnection>>([Connection]);

        public Task<ExternalEngineConnection?> FindAsync(Guid organizationId, Guid workspaceId, Guid connectionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<ExternalEngineConnection?>(organizationId == OrganizationId && workspaceId == WorkspaceId && connectionId == ConnectionId ? Connection : null);

        public Task<ExternalEngineHeartbeatStoreResult> TryApplyHeartbeatAsync(
            ExternalEngineConnection expected,
            ExternalEngineHeartbeatProjection projection,
            Guid identityId,
            DateTimeOffset receivedAt,
            TimeSpan minimumInterval,
            CancellationToken cancellationToken = default)
        {
            if (_concurrentSequence is { } concurrentSequence)
            {
                _concurrentSequence = null;
                var concurrentRunner = _concurrentRunnerId;
                _concurrentRunnerId = null;
                Connection = Connection with
                {
                    Status = ExternalEngineConnectionStatus.Connected,
                    ConnectorReachability = ExternalEngineConnectorReachability.Reachable,
                    LastAuthenticatedAt = receivedAt.Subtract(TimeSpan.FromSeconds(6)),
                    LastHeartbeatSequence = concurrentSequence,
                    LastHeartbeatObservedAt = projection.ObservedAt,
                    ActiveRunnerId = concurrentRunner ?? Connection.ActiveRunnerId,
                    RunnerLeaseExpiresAt = concurrentRunner is null
                        ? Connection.RunnerLeaseExpiresAt
                        : receivedAt.Add(ExternalEngineHeartbeatService.RunnerLeaseTtl),
                    UpdatedAt = receivedAt,
                    Version = Connection.Version + 1
                };
                return Task.FromResult(new ExternalEngineHeartbeatStoreResult(ExternalEngineHeartbeatStoreStatus.Concurrent, Connection));
            }
            if (expected.Version != Connection.Version)
                return Task.FromResult(new ExternalEngineHeartbeatStoreResult(ExternalEngineHeartbeatStoreStatus.Concurrent, Connection));
            if (identityId != Connection.ActiveIdentityId)
                return Task.FromResult(new ExternalEngineHeartbeatStoreResult(ExternalEngineHeartbeatStoreStatus.ScopeMismatch, Connection));
            if (ExternalEngineHeartbeatService.HasLiveRunnerLease(Connection, receivedAt)
                && !string.Equals(Connection.ActiveRunnerId, projection.RunnerId, StringComparison.Ordinal))
            {
                return Task.FromResult(new ExternalEngineHeartbeatStoreResult(
                    ExternalEngineHeartbeatStoreStatus.RunnerConflict,
                    Connection,
                    ExternalEngineHeartbeatService.RemainingLease(Connection, receivedAt)));
            }
            if (!projection.ResetSequenceBaseline
                && Connection.LastHeartbeatSequence is not null
                && projection.Sequence <= Connection.LastHeartbeatSequence)
                return Task.FromResult(new ExternalEngineHeartbeatStoreResult(ExternalEngineHeartbeatStoreStatus.OutOfOrder, Connection));
            if (Connection.LastAuthenticatedAt is { } last && receivedAt - last < minimumInterval)
                return Task.FromResult(new ExternalEngineHeartbeatStoreResult(ExternalEngineHeartbeatStoreStatus.RateLimited, Connection, minimumInterval - (receivedAt - last)));
            var studioCandidate = projection.Capabilities.Contains(
                    ExternalEngineHeartbeatService.StudioCapability, StringComparer.Ordinal)
                ? projection.StudioDestinationCandidate
                : null;
            var candidateChanged = !string.Equals(
                Connection.StudioDestinationCandidate, studioCandidate, StringComparison.Ordinal);
            var previousRunnerId = Connection.ActiveRunnerId;
            var previousDisplayName = Connection.DisplayName;
            Connection = Connection with
            {
                Status = projection.Status,
                RuntimeHealth = projection.RuntimeHealth,
                ConnectorReachability = projection.ConnectorReachability,
                LastAuthenticatedAt = receivedAt,
                ConnectorProtocol = projection.ConnectorProtocol,
                ConnectorVersion = projection.ConnectorVersion,
                DisplayName = projection.DisplayName ?? Connection.DisplayName,
                ObservedDistribution = projection.ObservedDistribution,
                ObservedVersion = projection.ObservedVersion,
                ObservedRuntimeKind = projection.ObservedRuntimeKind,
                ReleaseEvidenceLevel = projection.ReleaseEvidenceLevel,
                ReleaseEvidenceReference = projection.ReleaseEvidenceReference,
                StudioDestinationCandidate = studioCandidate,
                StudioDestinationCandidateId = candidateChanged
                    ? studioCandidate is null ? null : Guid.NewGuid()
                    : Connection.StudioDestinationCandidateId,
                StudioDestination = candidateChanged ? null : Connection.StudioDestination,
                StudioDestinationConfirmedAt = candidateChanged ? null : Connection.StudioDestinationConfirmedAt,
                StudioDestinationConfirmedByAccountId = candidateChanged ? null : Connection.StudioDestinationConfirmedByAccountId,
                Capabilities = projection.Capabilities,
                ConnectorCompatibilityStatus = ExternalEngineConnectorCompatibilityStatus.Compatible,
                ConnectorCompatibilityObservedAt = receivedAt,
                CapabilitiesObservedAt = receivedAt,
                LastHeartbeatSequence = projection.Sequence,
                LastHeartbeatObservedAt = projection.ObservedAt,
                ActiveRunnerId = projection.RunnerId ?? Connection.ActiveRunnerId,
                RunnerLeaseExpiresAt = projection.RunnerId is null
                    ? Connection.RunnerLeaseExpiresAt
                    : receivedAt.Add(ExternalEngineHeartbeatService.RunnerLeaseTtl),
                UpdatedAt = receivedAt,
                Version = Connection.Version + 1
            };
            if (previousRunnerId is not null
                && !string.Equals(previousRunnerId, Connection.ActiveRunnerId, StringComparison.Ordinal))
                Audits.Add(ExternalEngineHeartbeatService.RunnerChangedAuditAction);
            if (projection.DisplayName is not null
                && !string.Equals(previousDisplayName, projection.DisplayName, StringComparison.Ordinal))
                Audits.Add(ExternalEngineHeartbeatService.LabelChangedAuditAction);
            AppliedAfterConcurrent = true;
            return Task.FromResult(new ExternalEngineHeartbeatStoreResult(ExternalEngineHeartbeatStoreStatus.Applied, Connection));
        }

        public Task<ExternalEngineHeartbeatStoreResult> TryRecordUnsupportedProtocolAsync(
            ExternalEngineConnection expected,
            long sequence,
            DateTimeOffset observedAt,
            Guid identityId,
            DateTimeOffset receivedAt,
            TimeSpan minimumInterval,
            CancellationToken cancellationToken = default)
        {
            if (expected.Version != Connection.Version)
                return Task.FromResult(new ExternalEngineHeartbeatStoreResult(ExternalEngineHeartbeatStoreStatus.Concurrent, Connection));
            if (identityId != Connection.ActiveIdentityId)
                return Task.FromResult(new ExternalEngineHeartbeatStoreResult(ExternalEngineHeartbeatStoreStatus.ScopeMismatch, Connection));
            if (Connection.LastHeartbeatSequence is { } lastSequence && sequence <= lastSequence)
                return Task.FromResult(new ExternalEngineHeartbeatStoreResult(ExternalEngineHeartbeatStoreStatus.OutOfOrder, Connection));
            if (Connection.LastAuthenticatedAt is { } last && receivedAt - last < minimumInterval)
                return Task.FromResult(new ExternalEngineHeartbeatStoreResult(
                    ExternalEngineHeartbeatStoreStatus.RateLimited,
                    Connection,
                    minimumInterval - (receivedAt - last)));
            Connection = Connection with
            {
                ConnectorCompatibilityStatus = ExternalEngineConnectorCompatibilityStatus.UnsupportedProtocol,
                ConnectorCompatibilityObservedAt = receivedAt,
                ConnectorReachability = ExternalEngineConnectorReachability.Reachable,
                LastAuthenticatedAt = receivedAt,
                LastHeartbeatSequence = sequence,
                LastHeartbeatObservedAt = observedAt,
                UpdatedAt = receivedAt,
                Version = Connection.Version + 1
            };
            return Task.FromResult(new ExternalEngineHeartbeatStoreResult(ExternalEngineHeartbeatStoreStatus.Applied, Connection));
        }

        public Task<ExternalEngineConnectionCreateResult> TryCreateAsync(ExternalEngineConnection value, string idempotencyKey, string requestDigest, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ExternalEngineConnection?> TrySetPairingChallengeAsync(ExternalEngineConnection expected, Guid challengeId, DateTimeOffset updatedAt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ExternalEngineConnection?> TrySetActiveIdentityAsync(ExternalEngineConnection expected, Guid identityId, DateTimeOffset updatedAt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ExternalEngineConnection?> TryPrepareRepairAsync(ExternalEngineConnection expected, DateTimeOffset updatedAt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ExternalEngineConnection?> TryDisconnectAsync(ExternalEngineConnection expected, DateTimeOffset revokedAt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
