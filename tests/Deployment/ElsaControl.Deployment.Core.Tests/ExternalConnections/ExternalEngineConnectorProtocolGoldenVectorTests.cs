using ElsaControl.Deployment.Core.ExternalConnections;
using Xunit;

namespace ElsaControl.Deployment.Core.Tests.ExternalConnections;

public sealed class ExternalEngineConnectorProtocolGoldenVectorTests
{
    [Fact]
    public void Committed_vectors_are_generated_from_the_live_protocol_and_still_verify()
    {
        var file = ExternalEngineConnectorProtocolGoldenVectors.CommittedFile(FindRepoRoot());
        var regenerate = IsTruthy(Environment.GetEnvironmentVariable(
            ExternalEngineConnectorProtocolGoldenVectors.RegenerateEnvironmentVariable));
        if (regenerate && (IsTruthy(Environment.GetEnvironmentVariable("CI")) || IsTruthy(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"))))
        {
            Assert.Fail(
                "Golden-vector regeneration is disabled when CI or GITHUB_ACTIONS is set. " +
                $"Unset {ExternalEngineConnectorProtocolGoldenVectors.RegenerateEnvironmentVariable}.");
        }

        Assert.True(
            file.Exists || regenerate,
            $"Committed golden vectors are missing at {ExternalEngineConnectorProtocolGoldenVectors.RelativePath}. Regenerate them first.");

        var committed = file.Exists ? File.ReadAllText(file.FullName) : null;
        var generated = ExternalEngineConnectorProtocolGoldenVectors.GenerateJson(
            committed is null ? null : ExternalEngineConnectorProtocolGoldenVectors.ReadSignatures(committed));

        if (regenerate)
            File.WriteAllText(file.FullName, generated);

        Assert.True(
            string.Equals(regenerate || committed is null ? generated : committed, generated, StringComparison.Ordinal),
            $"Committed golden vectors at {ExternalEngineConnectorProtocolGoldenVectors.RelativePath} are stale. " +
            $"Regenerate with {ExternalEngineConnectorProtocolGoldenVectors.RegenerateEnvironmentVariable}=1 and commit the updated file.");
        ExternalEngineConnectorProtocolGoldenVectors.VerifySignatures(generated);
        Assert.Contains("\"kind\": \"enroll-redeem\"", generated, StringComparison.Ordinal);
        Assert.Contains("\"kind\": \"heartbeat\"", generated, StringComparison.Ordinal);
        Assert.Contains("\"kind\": \"rotate\"", generated, StringComparison.Ordinal);
        Assert.Contains("\"kind\": \"revoke\"", generated, StringComparison.Ordinal);
        Assert.Contains(
            $"\"maximumProofFutureSkewSeconds\": {(int)ExternalEngineEnrollmentDefaults.MaximumProofFutureSkew.TotalSeconds}",
            generated,
            StringComparison.Ordinal);
        Assert.Contains("\"testOnly\": true", generated, StringComparison.Ordinal);
        Assert.Contains("TEST ONLY", generated, StringComparison.Ordinal);
    }

    [Fact]
    public void Heartbeat_report_vector_matches_the_existing_protocol_v1_portable_payload()
    {
        using var document = System.Text.Json.JsonDocument.Parse(
            ExternalEngineConnectorProtocolGoldenVectors.GenerateJson());
        var heartbeat = document.RootElement.GetProperty("vectors").EnumerateArray()
            .Single(vector => vector.GetProperty("id").GetString() == "heartbeat.valid");

        Assert.Equal(
            "lv-I-PKdau9NK9KBH6y_lxB0lXkpUFiZRNkXxeLS6pA",
            heartbeat.GetProperty("payloadDigest").GetString());
        Assert.Equal(
            "{\"sequence\":1,\"observedAt\":\"2026-09-17T10:00:00.0000000Z\",\"connectorProtocol\":\"1\",\"connectorVersion\":\"1.4.0\",\"runtimeHealth\":\"healthy\",\"runtimeKind\":\"server\",\"observedDistribution\":\"valence-runtime\",\"observedVersion\":\"3.8.1\",\"studioDestination\":\"https://studio.example.test/elsa/\",\"capabilities\":[\"connection.status\",\"studio.open\"],\"components\":[{\"id\":\"runtime\",\"imageDigest\":\"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"},{\"id\":\"worker\",\"imageDigest\":\"sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\"}]}",
            heartbeat.GetProperty("reportCanonicalPayloadUtf8").GetString());
        Assert.DoesNotContain("displayName", heartbeat.GetProperty("reportCanonicalPayloadUtf8").GetString(), StringComparison.Ordinal);

        var named = document.RootElement.GetProperty("vectors").EnumerateArray()
            .Single(vector => vector.GetProperty("id").GetString() == "heartbeat.valid-display-name");
        Assert.Contains("\"displayName\":\"Acme Orders Engine\"", named.GetProperty("reportCanonicalPayloadUtf8").GetString(), StringComparison.Ordinal);
        Assert.StartsWith(
            "{\"sequence\":1,\"observedAt\":\"2026-09-17T10:00:00.0000000Z\",\"connectorProtocol\":\"1\",\"connectorVersion\":\"1.4.0\",\"displayName\":\"Acme Orders Engine\",\"runtimeHealth\":\"healthy\"",
            named.GetProperty("reportCanonicalPayloadUtf8").GetString(),
            StringComparison.Ordinal);
    }

    private static DirectoryInfo FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ElsaControl.sln")))
            directory = directory.Parent;

        return directory ?? throw new InvalidOperationException("Could not locate repository root.");
    }

    private static bool IsTruthy(string? value) =>
        string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
}
