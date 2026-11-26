using System.Text.RegularExpressions;

namespace ElsaControl.Api.Tests;

/// <summary>
/// The Control API image must install exactly one pinned, checksum-verified
/// Pulumi CLI (#1014 / #1034). A missing pin or checksum fails CI through this
/// existing test project; there is no new workflow.
/// </summary>
public sealed class ApiImagePulumiCliPinTests
{
    private static readonly Regex Sha256 = new(@"^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
    private static readonly Regex VersionAssignment = new(
        @"^ARG PULUMI_VERSION=([0-9]+\.[0-9]+\.[0-9]+)\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Multiline);
    private static readonly Regex Amd64Checksum = new(
        @"^ARG PULUMI_AMD64_SHA256=([0-9a-f]{64})\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Multiline);
    private static readonly Regex Arm64Checksum = new(
        @"^ARG PULUMI_ARM64_SHA256=([0-9a-f]{64})\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Multiline);

    [Fact]
    public void Dockerfile_pins_one_checksum_verified_pulumi_cli()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "Hosting", "ElsaControl.Api", "Dockerfile"));

        var version = Assert.Single(VersionAssignment.Matches(source));
        Assert.Equal("3.278.0", version.Groups[1].Value);

        var amd64 = Assert.Single(Amd64Checksum.Matches(source));
        var arm64 = Assert.Single(Arm64Checksum.Matches(source));
        Assert.Matches(Sha256, amd64.Groups[1].Value);
        Assert.Matches(Sha256, arm64.Groups[1].Value);
        Assert.NotEqual(amd64.Groups[1].Value, arm64.Groups[1].Value);

        Assert.Contains("pulumi-v${PULUMI_VERSION}-linux-${pulumi_arch}.tar.gz", source, StringComparison.Ordinal);
        Assert.Contains("echo \"${pulumi_sha256}  /tmp/pulumi.tar.gz\" | sha256sum --check --status", source, StringComparison.Ordinal);
        Assert.Contains("install -m 0755 /tmp/pulumi/pulumi /tmp/pulumi/pulumi-language-dotnet /usr/local/bin/", source, StringComparison.Ordinal);
        Assert.Contains("/usr/local/bin/pulumi version >/dev/null", source, StringComparison.Ordinal);
        Assert.DoesNotContain("PULUMI_VERSION=${", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Provisioning__WorkerEnabled=true", source, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "src", "Hosting", "ElsaControl.Api", "Dockerfile")))
                return directory.FullName;
        }

        throw new InvalidOperationException("The repository root with src/Hosting/ElsaControl.Api/Dockerfile was not found.");
    }
}
