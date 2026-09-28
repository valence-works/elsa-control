using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.RuntimeBuilder.Abstractions.ReleaseManifests;

namespace ElsaControl.Api.Tests;

/// <summary>
/// Pins the Studio grant set to <c>contracts/managed-elsa-studio-grants-v1.json</c>, which the runtime image carries
/// unchanged.
/// </summary>
public sealed class ManagedElsaStudioGrantsContractTests
{
    /// <summary>
    /// SHA-256 of the capability and permissions, one per line, in file order. valence-works/elsa-production-image pins
    /// the same value; a change here without the same change there is contract drift.
    /// </summary>
    private const string ContractDigest = "dd5b6af3425f7fbc1893cfb325468f3a75c9db054e6438f9c3452035f39ff330";

    private readonly StudioGrantsContract contract = JsonSerializer.Deserialize<StudioGrantsContract>(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "managed-elsa-studio-grants-v1.json")),
        new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    [Fact]
    public void Contract_is_the_version_both_repositories_pin()
    {
        var canonical = string.Join('\n', [contract.Capability, .. contract.Permissions]);

        Assert.Equal(ContractDigest, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))));
    }

    [Fact]
    public void Control_issues_exactly_the_contract_permissions_under_the_contract_capability()
    {
        Assert.Equal(contract.Permissions, ManagedElsaRuntimePermissions.StudioGrantsV1);
        Assert.Equal(contract.Capability, ReleaseManifestRuntimeIntegrationCapabilities.ManagedElsaStudioGrantsV1);
    }

    private sealed record StudioGrantsContract(string Capability, string[] Permissions);
}
