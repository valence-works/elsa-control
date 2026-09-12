using System.Text.Json;
using ElsaControl.Deployment.Azure;

namespace ElsaControl.Deployment.Azure.Tests;

public sealed class AzureProviderRunnerOptionsTests : IDisposable
{
    private readonly string _templateRoot = Path.Combine(Path.GetTempPath(), $"elsa-azure-options-{Guid.NewGuid():N}");

    public AzureProviderRunnerOptionsTests()
    {
        Directory.CreateDirectory(_templateRoot);
        File.WriteAllText(Path.Combine(_templateRoot, "main.bicep"), "targetScope = 'resourceGroup'");
        File.WriteAllText(Path.Combine(_templateRoot, "acr-pull-role.bicep"), "targetScope = 'resourceGroup'");
        File.WriteAllText(Path.Combine(_templateRoot, "sql-bootstrap.sql"), "SELECT 1;");
        File.WriteAllText(Path.Combine(_templateRoot, "az"), "azure-cli");
        File.WriteAllText(Path.Combine(_templateRoot, "sqlcmd"), "sqlcmd");
        File.WriteAllText(Path.Combine(_templateRoot, "curl"), "curl");
    }

    [Fact]
    public void Validates_explicit_governed_runner_options()
    {
        ValidOptions().Validate();
        (ValidOptions() with { TemplateRoot = _templateRoot + Path.DirectorySeparatorChar }).Validate();
        (ValidOptions() with { SqlBootstrapLogin = "operator_example.test#EXT#@tenant.onmicrosoft.com" }).Validate();
    }

    [Fact]
    public void Rejects_template_authority_outside_the_bounded_tree()
    {
        for (var index = 0; index < 33; index++)
            Directory.CreateDirectory(Path.Combine(_templateRoot, $"directory-{index:D2}"));

        Assert.Throws<ArgumentException>(() => ValidOptions().Validate());
    }

    [Fact]
    public void Default_options_fail_closed()
    {
        Assert.Throws<InvalidOperationException>(() => new AzureProviderRunnerOptions().Validate());
    }

    [Fact]
    public void Disposable_mode_requires_expiry_and_is_bound_into_the_authority()
    {
        var production = ValidOptions();
        var expiry = new DateOnly(2026, 9, 30);
        Assert.Throws<ArgumentException>(() => (production with { DisposableProofMode = true }).Validate());
        Assert.Throws<ArgumentException>(() => (production with { DisposableExpiryUtc = expiry }).Validate());
        var proof = production with { DisposableProofMode = true, DisposableExpiryUtc = expiry, AzureCliClientId = null };
        proof.Validate();
        Assert.NotEqual(production.ComputeProviderScopeFingerprint(ValidScope()), proof.ComputeProviderScopeFingerprint(ValidScope()));
        Assert.NotEqual(proof.ComputeProviderScopeFingerprint(ValidScope()),
            (proof with { DisposableExpiryUtc = expiry.AddDays(1) }).ComputeProviderScopeFingerprint(ValidScope()));
    }

    [Fact]
    public void Rejects_a_symbolic_link_as_template_authority()
    {
        if (OperatingSystem.IsWindows())
            return;

        var link = _templateRoot + "-link";
        Directory.CreateSymbolicLink(link, _templateRoot);
        try
        {
            Assert.Throws<ArgumentException>(() => (ValidOptions() with { TemplateRoot = link }).Validate());
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("10.0.0.0/24")]
    [InlineData("not-an-ip")]
    [InlineData(" 203.0.113.10 ")]
    public void Rejects_broad_or_invalid_sql_bootstrap_addresses(string address)
    {
        Assert.Throws<ArgumentException>(() => (ValidOptions() with { SqlBootstrapIp = address }).Validate());
    }

    [Fact]
    public void Target_scope_fingerprint_is_canonical_stable_and_scope_sensitive()
    {
        var scope = ValidScope();
        var same = scope with { ResourceGroupName = scope.ResourceGroupName.ToUpperInvariant() };
        var changed = scope with { RegistryResourceGroupName = "other-registry-rg" };

        Assert.Equal(scope.ComputeFingerprint(), same.ComputeFingerprint());
        Assert.NotEqual(scope.ComputeFingerprint(), changed.ComputeFingerprint());
        Assert.Equal(64, scope.ComputeFingerprint().Length);
        Assert.Equal(
            scope.ComputeFingerprint(),
            (scope with { Location = $" {scope.Location.ToUpperInvariant()} " }).ComputeFingerprint());
    }

    [Fact]
    public void Provider_scope_fingerprint_binds_bootstrap_and_template_authority()
    {
        var options = ValidOptions();
        var scope = ValidScope();

        Assert.NotEqual(
            options.ComputeProviderScopeFingerprint(scope),
            (options with { SqlBootstrapIp = "203.0.113.11" }).ComputeProviderScopeFingerprint(scope));
        var original = options.ComputeProviderScopeFingerprint(scope);
        File.AppendAllText(Path.Combine(_templateRoot, "main.bicep"), "\n// changed");
        Assert.NotEqual(original, options.ComputeProviderScopeFingerprint(scope));
        original = options.ComputeProviderScopeFingerprint(scope);
        File.AppendAllText(Path.Combine(_templateRoot, "az"), "\nchanged");
        Assert.NotEqual(original, options.ComputeProviderScopeFingerprint(scope));
    }

    [Fact]
    public void Provider_scope_fingerprint_binds_the_normalized_release_feed_service_index()
    {
        var options = ValidOptions();
        var scope = ValidScope();
        var original = options.ComputeProviderScopeFingerprint(scope);

        Assert.Equal(
            original,
            (options with { ReleaseFeedServiceIndex = " https://api.nuget.org/v3/index.json " })
                .ComputeProviderScopeFingerprint(scope));
        Assert.NotEqual(
            original,
            (options with { ReleaseFeedServiceIndex = "https://pkgs.example.test/v3/index.json" })
                .ComputeProviderScopeFingerprint(scope));
        var context = new AzureProviderExecutionContext(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "operation-identity", "idempotency-key", "target-key", "provider-assignment",
            new string('a', 64), new string('b', 64), original);
        Assert.Throws<InvalidOperationException>(() =>
            (options with { ReleaseFeedServiceIndex = "https://pkgs.example.test/v3/index.json" })
                .ValidateExecutionAuthority(context, scope));
    }

    [Theory]
    [InlineData("http://pkgs.example.test/v3/index.json")]
    [InlineData("https://pkgs.example.test")]
    [InlineData("https://pkgs.example.test/v3/index.json?token=secret")]
    [InlineData("https://user:password@pkgs.example.test/v3/index.json")]
    [InlineData("https://pkgs.example.test:8443/v3/index.json")]
    [InlineData("https://pkgs.example.test/v3\\index.json")]
    [InlineData("https://pkgs.example.test/v3/index.json#fragment")]
    [InlineData("https://pkgs.example.test/v3/index.json?")]
    [InlineData("https://@pkgs.example.test/v3/index.json")]
    [InlineData("https://127.0.0.1/v3/index.json")]
    [InlineData("https://pkgs.example.test/v3/index.json\n")]
    public void Rejects_unsafe_release_feed_service_index(string feed)
    {
        Assert.Throws<ArgumentException>(() => (ValidOptions() with { ReleaseFeedServiceIndex = feed }).Validate());
    }

    [Fact]
    public void Provider_scope_fingerprint_binds_the_managed_handoff_only_when_configured()
    {
        var withoutHandoff = ValidOptions();
        var withHandoff = withoutHandoff with { ManagedHandoff = ValidHandoff() };
        withHandoff.Validate();

        var unbound = withoutHandoff.ComputeProviderScopeFingerprint(ValidScope());
        var bound = withHandoff.ComputeProviderScopeFingerprint(ValidScope());

        Assert.NotEqual(unbound, bound);
        Assert.Equal(bound, (withoutHandoff with { ManagedHandoff = ValidHandoff() }).ComputeProviderScopeFingerprint(ValidScope()));
        Assert.All(
            new[]
            {
                ValidHandoff() with { ControlBaseUrl = "https://other.example.test", ControlContinuationUrl = "https://other.example.test/admin/runtimes" },
                ValidHandoff() with { ControlContinuationUrl = "https://control.example.test/admin/other" },
                ValidHandoff() with { RuntimeMaximumLifetime = TimeSpan.FromHours(1) },
                ValidHandoff() with { RuntimePermissions = ["read:*"] }
            },
            changed => Assert.NotEqual(bound, (withoutHandoff with { ManagedHandoff = changed }).ComputeProviderScopeFingerprint(ValidScope())));
    }

    [Theory]
    [InlineData("http://control.example.test", "http://control.example.test/admin/runtimes", 8, "*")]
    [InlineData("https://control.example.test/", "https://control.example.test/admin/runtimes", 8, "*")]
    [InlineData("https://control.example.test/api", "https://control.example.test/admin/runtimes", 8, "*")]
    [InlineData("https://user@control.example.test", "https://control.example.test/admin/runtimes", 8, "*")]
    [InlineData("https://control.example.test", "https://elsewhere.example.test/admin/runtimes", 8, "*")]
    [InlineData("https://control.example.test", "https://control.example.test/admin/runtimes?next=1", 8, "*")]
    [InlineData("https://control.example.test", "https://control.example.test/", 8, "*")]
    [InlineData("https://control.example.test", "https://control.example.test/admin/runtimes", 9, "*")]
    [InlineData("https://control.example.test", "https://control.example.test/admin/runtimes", 0, "*")]
    [InlineData("https://control.example.test", "https://control.example.test/admin/runtimes", 8, "")]
    [InlineData("https://control.example.test", "https://control.example.test/admin/runtimes", 8, "read workflows")]
    [InlineData("https://control.example.test", "https://control.example.test/admin/runtimes", 8, "read,write")]
    public void Rejects_unsafe_managed_handoff_inputs(string controlBaseUrl, string continuationUrl, int lifetimeHours, string permission)
    {
        var handoff = new AzureManagedHandoffOptions(controlBaseUrl, continuationUrl, TimeSpan.FromHours(lifetimeHours), [permission]);

        Assert.ThrowsAny<ArgumentException>(() => (ValidOptions() with { ManagedHandoff = handoff }).Validate());
    }

    [Fact]
    public void Rejects_a_fractional_session_ceiling_duplicate_grants_and_a_disposable_handoff()
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            (ValidOptions() with { ManagedHandoff = ValidHandoff() with { RuntimeMaximumLifetime = TimeSpan.FromMilliseconds(1500) } }).Validate());
        Assert.ThrowsAny<ArgumentException>(() =>
            (ValidOptions() with { ManagedHandoff = ValidHandoff() with { RuntimePermissions = ["*", "*"] } }).Validate());
        Assert.ThrowsAny<ArgumentException>(() => (ValidOptions() with
        {
            ManagedHandoff = ValidHandoff(),
            DisposableProofMode = true,
            DisposableExpiryUtc = new DateOnly(2026, 9, 30),
            AzureCliClientId = null
        }).Validate());
    }

    [Fact]
    public void Provider_scope_fingerprint_binds_the_managed_identity_client_id()
    {
        var options = ValidOptions();
        var scope = ValidScope();
        var original = options.ComputeProviderScopeFingerprint(scope);

        Assert.NotEqual(
            original,
            (options with { AzureCliClientId = "44444444-4444-4444-4444-444444444444" }).ComputeProviderScopeFingerprint(scope));
    }

    [Theory]
    [InlineData("33333333-3333-3333-3333-33333333333A")]
    [InlineData("not-a-guid")]
    public void Rejects_an_unsafe_managed_identity_client_id(string clientId)
    {
        Assert.Throws<ArgumentException>(() => (ValidOptions() with { AzureCliClientId = clientId }).Validate());
    }

    [Fact]
    public void Concrete_execution_requires_the_exact_durable_authority_fingerprint()
    {
        var options = ValidOptions();
        var scope = ValidScope();
        var context = new AzureProviderExecutionContext(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "operation-identity",
            "idempotency-key",
            "target-key",
            "provider-assignment",
            new string('a', 64),
            new string('b', 64),
            options.ComputeProviderScopeFingerprint(scope));

        options.ValidateExecutionAuthority(context, scope);
        Assert.Throws<InvalidOperationException>(() =>
            options.ValidateExecutionAuthority(context with { ProviderScopeFingerprint = null }, scope));
        Assert.Throws<InvalidOperationException>(() =>
            options.ValidateExecutionAuthority(context with { ProviderScopeFingerprint = new string('c', 64) }, scope));
    }

    [Fact]
    public void Template_authority_fingerprint_binds_nested_sources_and_rejects_nested_symlinks()
    {
        var modules = Path.Combine(_templateRoot, "modules");
        Directory.CreateDirectory(modules);
        var module = Path.Combine(modules, "workload.bicep");
        File.WriteAllText(module, "resource workload 'Microsoft.App/containerApps@2025-02-02-preview' = {};");
        var options = ValidOptions();
        var original = options.ComputeTemplateAuthorityFingerprint();
        File.AppendAllText(module, "\n// changed");
        Assert.NotEqual(original, options.ComputeTemplateAuthorityFingerprint());

        if (OperatingSystem.IsWindows())
            return;

        var external = Path.Combine(Path.GetTempPath(), $"elsa-external-{Guid.NewGuid():N}.bicep");
        var link = Path.Combine(modules, "linked.bicep");
        File.WriteAllText(external, "// external");
        File.CreateSymbolicLink(link, external);
        try
        {
            Assert.Throws<ArgumentException>(options.ComputeTemplateAuthorityFingerprint);
        }
        finally
        {
            File.Delete(link);
            File.Delete(external);
        }
    }

    [Fact]
    public void Secret_lease_is_value_free_in_text_and_json_and_unavailable_after_disposal()
    {
        var lease = new AzureSecretLease("sensitive-value");

        Assert.Equal("sensitive-value", lease.Value.ToString());
        Assert.Equal(nameof(AzureSecretLease), lease.ToString());
        var exception = Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(lease));
        Assert.DoesNotContain("sensitive-value", exception.Message, StringComparison.Ordinal);
        lease.Dispose();
        Assert.Throws<ObjectDisposedException>(() => _ = lease.Value);
    }

    [Theory]
    [InlineData("secret://vault/database", true)]
    [InlineData("secret://user:password@vault/database", false)]
    [InlineData("file:///tmp/secret", false)]
    public void Secret_resolution_request_accepts_only_approved_opaque_locators(string reference, bool accepted)
    {
        var request = SecretRequest("database", reference);
        if (accepted)
            request.Validate();
        else
            Assert.Throws<ArgumentException>(request.Validate);
    }

    [Theory]
    [InlineData(" Database ")]
    [InlineData("Database")]
    [InlineData("database/name")]
    public void Secret_resolution_request_rejects_noncanonical_names(string name)
    {
        Assert.Throws<ArgumentException>(() =>
            SecretRequest(name, "secret://vault/database").Validate());
    }

    [Fact]
    public async Task Unconfigured_secret_resolver_fails_closed()
    {
        var resolver = new UnconfiguredAzureSecretResolver();
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await resolver.ResolveAsync(SecretRequest("database", "secret://vault/database")));
    }

    public void Dispose() => Directory.Delete(_templateRoot, recursive: true);

    private AzureProviderRunnerOptions ValidOptions() => new()
    {
        Enabled = true,
        AzureCliClientId = "33333333-3333-3333-3333-333333333333",
        AzureCliPath = Path.Combine(_templateRoot, "az"),
        SqlCmdPath = Path.Combine(_templateRoot, "sqlcmd"),
        CurlPath = Path.Combine(_templateRoot, "curl"),
        TemplateRoot = _templateRoot,
        SqlBootstrapObjectId = "11111111-1111-1111-1111-111111111111",
        SqlBootstrapLogin = "proof-bootstrap",
        SqlBootstrapIp = "203.0.113.10",
        RuntimeAdminUsername = "runtime-admin"
    };

    private static AzureManagedHandoffOptions ValidHandoff() => new(
        "https://control.example.test", "https://control.example.test/admin/runtimes", TimeSpan.FromHours(8), ["*"]);

    private static AzureProviderTargetScope ValidScope() => new(
        "11111111-1111-1111-1111-111111111111",
        "proof-rg",
        "22222222-2222-2222-2222-222222222222",
        "registry-rg",
        "valenceruntimeimages",
        "westeurope");

    private static AzureSecretResolutionRequest SecretRequest(string name, string reference) => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        Guid.NewGuid(),
        "provider-assignment",
        name,
        reference);
}
