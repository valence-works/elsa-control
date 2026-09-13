using System.Text.Json;
using ElsaControl.RuntimeBuilder.Abstractions;
using ElsaControl.RuntimeBuilder.Abstractions.RuntimeConfigurations;
using ElsaControl.RuntimeBuilder.Core.RuntimeConfigurations;

namespace ElsaControl.RuntimeBuilder.Core.Tests;

public sealed class ManagedRuntimeConfigurationAdapterTests
{
    [Fact]
    public void Projects_builder_defaults_to_managed_safe_intent_and_reports_export_metadata()
    {
        var result = ManagedRuntimeConfigurationAdapter.Project(new RuntimeBuilderIntent(
            new RuntimeImageSelection("elsa-pro-combined", "latest", 8080, new Dictionary<string, string>()),
            [],
            [],
            [],
            new LocalPackagesOptions(false, null),
            "docker-compose"));

        Assert.True(result.CanProvision);
        Assert.NotNull(result.BuilderIntent);
        Assert.Equal("elsa-instance", result.BuilderIntent!.Image.Slug);
        Assert.Null(result.BuilderIntent.Image.Tag);
        Assert.Null(result.BuilderIntent.Image.HostPort);
        Assert.Null(result.BuilderIntent.Image.EnvOverrides);
        Assert.Null(result.BuilderIntent.LocalPackages);
        Assert.Null(result.BuilderIntent.Target);
        Assert.Contains(result.Findings, x => x.Code == "builder.image.slug.ignored" && x.Severity == "info");
        Assert.Contains(result.Findings, x => x.Code == "builder.image.tag.ignored" && x.Severity == "info");
        Assert.Contains(result.Findings, x => x.Code == "builder.image.hostPort.ignored" && x.Severity == "info");
        Assert.Contains(result.Findings, x => x.Code == "builder.target.ignored" && x.Severity == "info");
    }

    [Fact]
    public void Rejects_a_disabled_local_package_path_instead_of_silently_dropping_it()
    {
        var result = ManagedRuntimeConfigurationAdapter.Project(new RuntimeBuilderIntent(
            new RuntimeImageSelection("elsa-pro-server", null, null, null),
            [], [], [], new LocalPackagesOptions(false, "/tmp/packages")));

        Assert.False(result.CanProvision);
        Assert.Null(result.BuilderIntent!.LocalPackages);
        Assert.Contains(result.Findings, x => x.Code == "builder.localPackages.unsupported");
    }

    [Fact]
    public void Blocks_export_overrides_and_provider_specific_infrastructure_without_leaking_them()
    {
        var result = ManagedRuntimeConfigurationAdapter.Project(new RuntimeBuilderIntent(
            new RuntimeImageSelection(
                "elsa-pro-combined",
                "custom-tag",
                9999,
                new Dictionary<string, string> { ["Admin__Password"] = "raw-secret" }),
            [],
            [new(Guid.NewGuid(), "custom", "https://packages.example.test/index.json", "nuget")],
            [new("database", "postgres-compose", "compose-sidecar", new Dictionary<string, JsonElement>
            {
                ["connectionString"] = JsonSerializer.SerializeToElement("raw-secret")
            })],
            new LocalPackagesOptions(true, "/tmp/packages"),
            "kubernetes"));

        Assert.False(result.CanProvision);
        Assert.NotNull(result.BuilderIntent);
        Assert.Empty(result.BuilderIntent!.PackageSources);
        Assert.Empty(result.BuilderIntent.Infrastructure);
        Assert.Null(result.BuilderIntent.LocalPackages);
        Assert.Null(result.BuilderIntent.Target);
        Assert.Null(result.BuilderIntent.Image.EnvOverrides);
        Assert.Contains(result.Findings, x => x.Code == "builder.image.environment.unsupported");
        Assert.Contains(result.Findings, x => x.Code == "builder.packageSources.unsupported");
        Assert.Contains(result.Findings, x => x.Code == "builder.infrastructure.provider.unsupported");
        Assert.Contains(result.Findings, x => x.Code == "builder.localPackages.unsupported");

        var serialized = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("raw-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("postgres-compose", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("/tmp/packages", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("packages.example.test", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void Preserves_exact_packages_safe_settings_and_kind_only_infrastructure()
    {
        var sourceId = Guid.NewGuid();
        var result = ManagedRuntimeConfigurationAdapter.Project(new RuntimeBuilderIntent(
            new RuntimeImageSelection("elsa-pro-server", null, null, null),
            [new(
                sourceId,
                "Elsa.Email",
                "2.0.0",
                ["email"],
                new Dictionary<string, IReadOnlyDictionary<string, JsonElement>>
                {
                    ["email"] = new Dictionary<string, JsonElement>
                    {
                        ["Endpoint"] = JsonSerializer.SerializeToElement("https://mail.example.test"),
                        ["ApiSecret"] = JsonSerializer.SerializeToElement("secret://vault/email")
                    }
                })],
            [],
            [new("database", null!, null!, null)],
            null));

        Assert.True(result.CanProvision, string.Join("; ", result.Findings.Select(x => x.Code)));
        var package = Assert.Single(result.BuilderIntent!.Packages);
        Assert.Equal(sourceId, package.SourceId);
        Assert.Equal("Elsa.Email", package.PackageId);
        Assert.Equal("2.0.0", package.Version);
        Assert.Equal("email", Assert.Single(package.SelectedFeatures));
        Assert.Equal("https://mail.example.test", package.Settings!["email"]["Endpoint"].GetString());
        Assert.Equal("secret://vault/email", package.Settings["email"]["ApiSecret"].GetString());
        var infrastructure = Assert.Single(result.BuilderIntent.Infrastructure);
        Assert.Equal("database", infrastructure.Kind);
        Assert.Null(infrastructure.ProviderId);
        Assert.Null(infrastructure.Strategy);
        Assert.Null(infrastructure.Settings);
    }

    [Fact]
    public void Rejects_invalid_packages_and_raw_secret_values_without_returning_them()
    {
        var result = ManagedRuntimeConfigurationAdapter.Project(new RuntimeBuilderIntent(
            new RuntimeImageSelection("elsa-pro-server", null, null, null),
            [
                new(Guid.NewGuid(), "Elsa.Email", "1.*", ["bad feature"], null),
                new(Guid.NewGuid(), "Elsa.Email", "2.0.0", [], new Dictionary<string, IReadOnlyDictionary<string, JsonElement>>
                {
                    ["email"] = new Dictionary<string, JsonElement>
                    {
                        ["Password"] = JsonSerializer.SerializeToElement("plaintext")
                    }
                })
            ],
            [], [], null));

        Assert.False(result.CanProvision);
        Assert.Empty(result.BuilderIntent!.Packages);
        Assert.Contains(result.Findings, x => x.Code == "package.selection.invalid");
        Assert.Contains(result.Findings, x => x.Code == "configuration.secretValue.forbidden");
        Assert.DoesNotContain("plaintext", JsonSerializer.Serialize(result.BuilderIntent), StringComparison.Ordinal);
    }

    [Fact]
    public void Null_intent_fails_closed()
    {
        var result = ManagedRuntimeConfigurationAdapter.Project(null);

        Assert.False(result.CanProvision);
        Assert.Null(result.BuilderIntent);
        Assert.Contains(result.Findings, x => x.Code == "builder.intent.required");
    }

    [Fact]
    public void Rejects_an_undefined_nested_setting_value_without_echoing_input()
    {
        var result = ManagedRuntimeConfigurationAdapter.Project(new RuntimeBuilderIntent(
            new RuntimeImageSelection("elsa-pro-server", null, null, null),
            [new(
                Guid.NewGuid(),
                "Elsa.Email",
                "2.0.0",
                [],
                new Dictionary<string, IReadOnlyDictionary<string, JsonElement>>
                {
                    ["email"] = new Dictionary<string, JsonElement>
                    {
                        ["Endpoint"] = default
                    }
                })],
            [], [], null));

        Assert.False(result.CanProvision);
        Assert.Empty(result.BuilderIntent!.Packages);
        Assert.Contains(result.Findings, x => x.Code == "configuration.setting.invalid");
        Assert.All(result.Findings, finding => Assert.DoesNotContain("Undefined", finding.Message, StringComparison.OrdinalIgnoreCase));
    }
}
