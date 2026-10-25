using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.RuntimeBuilder.Abstractions.ReleaseManifests;

namespace ElsaControl.Api.Tests;

/// <summary>
/// Pins each Studio grant set to its <c>contracts/managed-elsa-studio-grants-v*.json</c> file, which the runtime
/// image carries unchanged.
/// </summary>
public sealed class ManagedElsaStudioGrantsContractTests
{
    /// <summary>
    /// SHA-256 of the capability and permissions, one per line, in file order. valence-works/elsa-production-image pins
    /// the same value; a change here without the same change there is contract drift.
    /// </summary>
    private const string V1Digest = "dd5b6af3425f7fbc1893cfb325468f3a75c9db054e6438f9c3452035f39ff330";
    private const string V2Digest = "8a0d56c26f8e6a44315d718aaf2b99feed614ceb8101e3f1d24c94301f472467";

    [Theory]
    [InlineData("managed-elsa-studio-grants-v1.json", V1Digest)]
    [InlineData("managed-elsa-studio-grants-v2.json", V2Digest)]
    public void Contract_is_the_version_both_repositories_pin(string fileName, string digest)
    {
        var contract = Read(fileName);
        var canonical = string.Join('\n', [contract.Capability, .. contract.Permissions]);

        Assert.Equal(digest, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))));
    }

    [Fact]
    public void Control_issues_exactly_the_v1_contract_permissions_under_the_v1_capability()
    {
        var contract = Read("managed-elsa-studio-grants-v1.json");
        Assert.Equal(ManagedElsaRuntimePermissions.StudioGrantsV1, contract.Permissions);
        Assert.Equal(ReleaseManifestRuntimeIntegrationCapabilities.ManagedElsaStudioGrantsV1, contract.Capability);
        Assert.Equal(ManagedElsaRuntimePermissions.StudioGrantsV1Capability, contract.Capability);
    }

    [Fact]
    public void Control_issues_exactly_the_v2_contract_permissions_under_the_v2_capability()
    {
        var contract = Read("managed-elsa-studio-grants-v2.json");
        Assert.Equal(ManagedElsaRuntimePermissions.StudioGrantsV2, contract.Permissions);
        Assert.Equal(ReleaseManifestRuntimeIntegrationCapabilities.ManagedElsaStudioGrantsV2, contract.Capability);
        Assert.Equal(ManagedElsaRuntimePermissions.StudioGrantsV2Capability, contract.Capability);
        Assert.DoesNotContain(contract.Permissions, permission =>
            permission.StartsWith("read:", StringComparison.Ordinal) ||
            permission.StartsWith("write:", StringComparison.Ordinal) ||
            permission.StartsWith("publish:", StringComparison.Ordinal));
        Assert.DoesNotContain("system/features:view", contract.Permissions);
        Assert.DoesNotContain(contract.Permissions, permission =>
            permission.StartsWith("workflows/descriptors/", StringComparison.Ordinal) &&
            permission != "workflows/descriptors/activities:view");
    }

    private static StudioGrantsContract Read(string fileName) =>
        JsonSerializer.Deserialize<StudioGrantsContract>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName)),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private sealed record StudioGrantsContract(string Capability, string[] Permissions);
}
