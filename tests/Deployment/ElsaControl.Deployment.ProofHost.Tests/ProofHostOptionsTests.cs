using System.Globalization;
using System.Text.Json;
using ElsaControl.Deployment.Azure;
using ElsaControl.Deployment.ProofHost;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace ElsaControl.Deployment.ProofHost.Tests;

public sealed class ProofHostOptionsTests
{
    [Fact]
    public void Parses_a_complete_validate_configuration_from_known_environment_values()
    {
        using var fixture = new ProofHostFixture();

        var result = ProofHostOptionsParser.Parse([], fixture.Environment);

        Assert.True(result.Succeeded, string.Join(", ", result.Errors));
        Assert.Equal(ProofHostMode.Validate, result.Options!.Mode);
        Assert.Equal("3.8.0-preview.5413", result.Options.ElsaVersion);
        Assert.Equal("sha256:" + new string('a', 64), result.Options.ImageDigest);
        Assert.Equal(ProofHostOptions.SupportedFeatures, result.Options.Features);
        Assert.Equal("secret://proof/sql-connection", result.Options.SecretReferences["sql-connection"]);
    }

    [Theory]
    [InlineData("northeurope")]
    [InlineData("swedencentral")]
    public void Accepts_governed_capacity_fallback(string location)
    {
        using var fixture = new ProofHostFixture();
        fixture.Environment["DISPOSABLE_PROOF_LOCATION"] = location;

        var result = ProofHostOptionsParser.Parse([], fixture.Environment);

        Assert.True(result.Succeeded, string.Join(", ", result.Errors));
        Assert.Equal(location, result.Options!.Location);
    }

    [Fact]
    public void Requires_the_exact_mutation_gate_for_run_and_cleanup()
    {
        using var fixture = new ProofHostFixture();

        var run = ProofHostOptionsParser.Parse(["run"], fixture.Environment);
        var cleanup = ProofHostOptionsParser.Parse(["cleanup"], fixture.Environment);

        Assert.Contains("mutationGate.required", run.Errors);
        Assert.Contains("mutationGate.required", cleanup.Errors);
        Assert.True(run.MutationGateFailed);
        Assert.Equal(ProofHostApplication.MutationGateExitCode, run.Errors.Count == 1 ? 3 : 2);
    }

    [Fact]
    public void Accepts_run_only_when_gate_value_is_exactly_uppercase_yes()
    {
        using var fixture = new ProofHostFixture();

        fixture.Environment["DISPOSABLE_PROOF_APPLY"] = "yes";
        Assert.Contains("mutationGate.required", ProofHostOptionsParser.Parse(["run"], fixture.Environment).Errors);

        fixture.Environment["DISPOSABLE_PROOF_APPLY"] = " YES";
        Assert.Contains("mutationGate.required", ProofHostOptionsParser.Parse(["run"], fixture.Environment).Errors);

        fixture.Environment["DISPOSABLE_PROOF_APPLY"] = "YES";
        var parsed = ProofHostOptionsParser.Parse(["run"], fixture.Environment);
        Assert.DoesNotContain("mutationGate.required", parsed.Errors);
    }

    [Fact]
    public void Rejects_unknown_prefixed_environment_values()
    {
        using var fixture = new ProofHostFixture();
        fixture.Environment["DISPOSABLE_PROOF_TYPO"] = "value";

        var result = ProofHostOptionsParser.Parse([], fixture.Environment);

        Assert.Contains("environment.unknown", result.Errors);
    }

    [Fact]
    public void Rejects_empty_feature_segments_instead_of_silently_dropping_them()
    {
        using var fixture = new ProofHostFixture();
        fixture.Environment["DISPOSABLE_PROOF_FEATURES"] = "Liquid,,OpenTelemetry";

        var result = ProofHostOptionsParser.Parse([], fixture.Environment);

        Assert.Contains("features.invalid", result.Errors);
    }

    [Fact]
    public void Rejects_null_features_without_throwing()
    {
        var options = new ProofHostOptions { Features = null! };

        var errors = options.Validate();

        Assert.Contains("features.invalid", errors);
        Assert.Contains("features.unsupported", errors);
    }

    [Fact]
    public void Cli_values_override_known_environment_values_but_duplicate_cli_options_fail()
    {
        using var fixture = new ProofHostFixture();
        fixture.Environment["DISPOSABLE_PROOF_ELSA_VERSION"] = "3.8.0";

        var parsed = ProofHostOptionsParser.Parse(["--elsa-version", "3.8.0-preview.5413"], fixture.Environment);
        Assert.True(parsed.Succeeded, string.Join(", ", parsed.Errors));
        Assert.Equal("3.8.0-preview.5413", parsed.Options!.ElsaVersion);

        var duplicate = ProofHostOptionsParser.Parse(["--elsa-version", "3.8.0", "--elsa-version", "3.8.1"], fixture.Environment);
        Assert.Contains("argument.duplicate", duplicate.Errors);
    }

    [Theory]
    [InlineData("sha256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")]
    public void Requires_canonical_lowercase_sha256_digests(string digest)
    {
        using var fixture = new ProofHostFixture();
        fixture.Environment["DISPOSABLE_PROOF_IMAGE_DIGEST"] = digest;

        var result = ProofHostOptionsParser.Parse([], fixture.Environment);

        if (digest.Contains('A'))
            Assert.Contains("imageDigest.invalid", result.Errors);
        else
            Assert.DoesNotContain("imageDigest.invalid", result.Errors);
    }

    [Fact]
    public void Rejects_unsafe_or_non_immutable_evidence_locators()
    {
        using var fixture = new ProofHostFixture();
        fixture.Environment["DISPOSABLE_PROOF_RELEASE_MANIFEST_REFERENCE"] = "https://user:password@example.test/manifest?token=secret";

        var result = ProofHostOptionsParser.Parse([], fixture.Environment);

        Assert.Contains("releaseManifestEvidence.invalid", result.Errors);
    }

    [Fact]
    public void Rejects_unsafe_paths_and_noncanonical_identity_values()
    {
        using var fixture = new ProofHostFixture();
        fixture.Environment["DISPOSABLE_PROOF_TEMPLATE_ROOT"] = "relative/templates";
        fixture.Environment["DISPOSABLE_PROOF_SUBSCRIPTION"] = "AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA";
        fixture.Environment["DISPOSABLE_PROOF_SQL_BOOTSTRAP_IP"] = "0.0.0.0";

        var result = ProofHostOptionsParser.Parse([], fixture.Environment);

        Assert.Contains("templateRoot.invalid", result.Errors);
        Assert.Contains("subscriptionId.invalid", result.Errors);
        Assert.Contains("sqlBootstrapIp.invalid", result.Errors);
    }

    [Fact]
    public void Safe_summary_contains_locators_but_never_secret_values_or_apply_value()
    {
        using var fixture = new ProofHostFixture();
        fixture.Environment["DISPOSABLE_PROOF_APPLY"] = "YES";
        var parsed = ProofHostOptionsParser.Parse(["run"], fixture.Environment);

        Assert.True(parsed.Succeeded, string.Join(", ", parsed.Errors));
        var json = parsed.Options!.ToSafeJson();
        using var document = JsonDocument.Parse(json);
        Assert.Equal("run", document.RootElement.GetProperty("mode").GetString());
        Assert.True(document.RootElement.GetProperty("mutationAuthorized").GetBoolean());
        Assert.Contains("release-manifest", json, StringComparison.Ordinal);
        Assert.DoesNotContain("YES", json, StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validate_mode_is_offline_and_does_not_require_an_executor()
    {
        using var fixture = new ProofHostFixture();
        await using var output = new StringWriter();
        await using var error = new StringWriter();

        var code = await ProofHostApplication.RunAsync([], fixture.Environment, output: output, error: error);

        Assert.Equal(0, code);
        Assert.Empty(error.ToString());
        Assert.Contains("\"mode\": \"validate\"", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mutating_mode_fails_closed_before_composition_when_gate_is_missing()
    {
        using var fixture = new ProofHostFixture();
        await using var error = new StringWriter();

        var code = await ProofHostApplication.RunAsync(["run"], fixture.Environment, new RecordingExecutor(), error: error);

        Assert.Equal(ProofHostApplication.MutationGateExitCode, code);
        Assert.Contains("proof-host.mutationGate.required", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Concrete_host_persists_state_and_emits_only_a_safe_failed_report_when_runner_fails()
    {
        using var fixture = new ProofHostFixture();
        fixture.Environment["DISPOSABLE_PROOF_APPLY"] = "YES";
        // A regular file, not /usr/bin/false: some runner images ship it as a symlink, and the
        // host refuses a symlinked tool path.
        var failingTool = fixture.CreateFailingExecutable();
        fixture.Environment["DISPOSABLE_PROOF_AZURE_CLI_PATH"] = failingTool;
        fixture.Environment["DISPOSABLE_PROOF_SQLCMD_PATH"] = failingTool;
        fixture.Environment["DISPOSABLE_PROOF_CURL_PATH"] = failingTool;
        await using var output = new StringWriter();
        await using var error = new StringWriter();

        var parsed = ProofHostOptionsParser.Parse(["run"], fixture.Environment);
        Assert.True(parsed.Succeeded, string.Join(", ", parsed.Errors));
        var code = await new AzureProofHostExecutor(output, error).ExecuteAsync(parsed.Options!);

        Assert.Equal(5, code);
        Assert.Empty(error.ToString());
        Assert.True(File.Exists(parsed.Options!.StatePath));
        using var report = JsonDocument.Parse(output.ToString());
        Assert.Equal("failed", report.RootElement.GetProperty("outcome").GetString());
        Assert.DoesNotContain("super-secret", output.ToString(), StringComparison.Ordinal);

        await using (var db = new CatalogDbContext(new DbContextOptionsBuilder<CatalogDbContext>()
                         .UseSqlite($"Data Source={parsed.Options.StatePath}").Options))
        {
            var store = new AzureProviderOperationStore(db);
            var scope = parsed.Options.CreateRunnerOptions().ComputeProviderScopeFingerprint(parsed.Options.CreateTargetScope());
            var operation = await store.GetLatestReconcileAsync(parsed.Options.WorkspaceId, parsed.Options.ProofName, scope);
            Assert.NotNull(operation);
            Assert.NotNull(operation.ProviderAssignmentId);
            var assignment = await ((IAzureProviderResourceAssignmentStore)store).GetAsync(operation.WorkspaceId, operation.ProviderAssignmentId.Value);
            Assert.NotNull(assignment);
            Assert.Equal(parsed.Options.ResourceGroupName, assignment.ResourceGroupName);
            var transitions = await store.ListTransitionsAsync(operation.WorkspaceId, operation.Id);
            Assert.Contains(transitions, transition => transition.Code == "azure.step.failed");
        }

        await using var repeatOutput = new StringWriter();
        await using var repeatError = new StringWriter();
        var repeatCode = await new AzureProofHostExecutor(repeatOutput, repeatError).ExecuteAsync(parsed.Options!);
        Assert.Equal(5, repeatCode);
        Assert.Empty(repeatOutput.ToString());
        Assert.Equal("proof-host.execution.failed" + Environment.NewLine, repeatError.ToString());
    }

    [Fact]
    public void Rejects_unbound_features_registry_and_workflow_username()
    {
        using var fixture = new ProofHostFixture();
        fixture.Environment["DISPOSABLE_PROOF_FEATURES"] = "DefaultAuthentication,Liquid";
        fixture.Environment["DISPOSABLE_PROOF_REGISTRY_NAME"] = "otherregistry";
        fixture.Environment["DISPOSABLE_PROOF_WORKFLOW_USERNAME"] = "other-admin";

        var result = ProofHostOptionsParser.Parse([], fixture.Environment);

        Assert.Contains("features.unsupported", result.Errors);
        Assert.Contains("registryName.invalid", result.Errors);
        Assert.Contains("workflowUsername.invalid", result.Errors);
    }

    [Fact]
    public void Accepts_a_bounded_entra_guest_principal_login()
    {
        using var fixture = new ProofHostFixture();
        fixture.Environment["DISPOSABLE_PROOF_SQL_BOOTSTRAP_LOGIN"] = "operator_example.test#EXT#@tenant.onmicrosoft.com";

        var result = ProofHostOptionsParser.Parse([], fixture.Environment);

        Assert.DoesNotContain("sqlBootstrapLogin.invalid", result.Errors);
    }

    [Fact]
    public void Rejects_template_authority_outside_the_bounded_tree()
    {
        using var fixture = new ProofHostFixture();
        var templateRoot = fixture.Environment["DISPOSABLE_PROOF_TEMPLATE_ROOT"]!;
        for (var index = 0; index < 33; index++)
            Directory.CreateDirectory(Path.Combine(templateRoot, $"directory-{index:D2}"));

        var result = ProofHostOptionsParser.Parse([], fixture.Environment);

        Assert.Contains("templateRoot.invalid", result.Errors);
    }

    private sealed class ProofHostFixture : IDisposable
    {
        private readonly string root;

        public ProofHostFixture()
        {
            root = Path.Combine(Path.GetTempPath(), "elsa-proof-host-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            foreach (var file in new[] { "main.bicep", "acr-pull-role.bicep", "sql-bootstrap.sql" })
                File.WriteAllText(Path.Combine(root, file), "authority");

            Environment = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["DISPOSABLE_PROOF_MODE"] = "validate",
                ["DISPOSABLE_PROOF_WORKSPACE_ID"] = "11111111-1111-1111-1111-111111111111",
                ["DISPOSABLE_PROOF_PROOF_NAME"] = "proof195",
                ["DISPOSABLE_PROOF_RESOURCE_GROUP"] = "rg-proof195",
                ["DISPOSABLE_PROOF_SUBSCRIPTION"] = "22222222-2222-2222-2222-222222222222",
                ["DISPOSABLE_PROOF_REGISTRY_SUBSCRIPTION"] = "22222222-2222-2222-2222-222222222222",
                ["DISPOSABLE_PROOF_REGISTRY_RESOURCE_GROUP"] = "rg-registry",
                ["DISPOSABLE_PROOF_REGISTRY_NAME"] = "valenceruntimeimages",
                ["DISPOSABLE_PROOF_LOCATION"] = "westeurope",
                ["DISPOSABLE_PROOF_ELSA_VERSION"] = "3.8.0-preview.5413",
                ["DISPOSABLE_PROOF_TOPOLOGY"] = "combined",
                ["DISPOSABLE_PROOF_FEATURES"] = string.Join(',', ProofHostOptions.SupportedFeatures),
                ["DISPOSABLE_PROOF_IMAGE_REPOSITORY"] = "valenceruntimeimages.azurecr.io/runtime-combined",
                ["DISPOSABLE_PROOF_IMAGE_DIGEST"] = "sha256:" + new string('a', 64),
                ["DISPOSABLE_PROOF_RELEASE_MANIFEST_REFERENCE"] = "oci://valenceruntimeimages.azurecr.io/release-manifest@sha256:" + new string('b', 64),
                ["DISPOSABLE_PROOF_RELEASE_MANIFEST_DIGEST"] = "sha256:" + new string('b', 64),
                ["DISPOSABLE_PROOF_RELEASE_MANIFEST_SIGNATURE_REFERENCE"] = "https://evidence.example.test/signature@sha256:" + new string('c', 64),
                ["DISPOSABLE_PROOF_RELEASE_MANIFEST_SIGNATURE_DIGEST"] = "sha256:" + new string('c', 64),
                ["DISPOSABLE_PROOF_SOURCE_COMMIT"] = "dddddddddddddddddddddddddddddddddddddddd",
                ["DISPOSABLE_PROOF_STATE_PATH"] = Path.Combine(root, "proof-state.db"),
                ["DISPOSABLE_PROOF_TEMPLATE_ROOT"] = root,
                ["DISPOSABLE_PROOF_AZURE_CLI_PATH"] = System.Environment.ProcessPath,
                ["DISPOSABLE_PROOF_SQLCMD_PATH"] = System.Environment.ProcessPath,
                ["DISPOSABLE_PROOF_CURL_PATH"] = System.Environment.ProcessPath,
                ["DISPOSABLE_PROOF_SQL_BOOTSTRAP_OBJECT_ID"] = "33333333-3333-3333-3333-333333333333",
                ["DISPOSABLE_PROOF_SQL_BOOTSTRAP_LOGIN"] = "proof-admin",
                ["DISPOSABLE_PROOF_SQL_BOOTSTRAP_IP"] = "192.0.2.10",
                ["DISPOSABLE_PROOF_OWNER"] = "elsa-control",
                ["DISPOSABLE_PROOF_EXPIRY_UTC"] = DateTime.UtcNow.AddDays(2).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["DISPOSABLE_PROOF_SQL_CONNECTION_REFERENCE"] = "secret://proof/sql-connection",
                ["DISPOSABLE_PROOF_IDENTITY_SIGNING_KEY_REFERENCE"] = "secret://proof/identity-signing-key",
                ["DISPOSABLE_PROOF_ADMIN_PASSWORD_REFERENCE"] = "secret://proof/admin-password"
            };
        }

        public Dictionary<string, string?> Environment { get; }

        public string CreateFailingExecutable()
        {
            var path = Path.Combine(root, "failing-tool");
            File.WriteAllText(path, "#!/bin/sh\nexit 1\n");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private sealed class RecordingExecutor : IProofHostExecutor
    {
        public Task<int> ExecuteAsync(ProofHostOptions options, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }
}
