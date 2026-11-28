
namespace ElsaControl.Provisioning.Core.Tests;

public sealed class ProvisioningRevisionSuffixTests
{
    private static readonly Guid MaxLengthInstanceId =
        Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");

    [Fact]
    public void A_max_length_app_and_generated_suffix_fit_the_azure_revision_name_limit()
    {
        var appName = ProvisioningEngineContainerApp.ForInstance(MaxLengthInstanceId);
        var suffix = ProvisioningRevisionSuffix.FromFingerprint(new string('f', 64));

        Assert.Equal(ProvisioningEngineContainerApp.MaximumLength, appName.Length);
        Assert.Equal(ProvisioningRevisionSuffix.MaximumLength, suffix.Length);
        Assert.Equal(19, ProvisioningRevisionSuffix.FingerprintLength);
        var revisionName = ProvisioningRevisionSuffix.RevisionName(appName, suffix);
        Assert.Equal(ProvisioningRevisionSuffix.MaximumRevisionNameLength, revisionName.Length);
        Assert.Equal($"{appName}--{suffix}", revisionName);
        Assert.StartsWith("r", suffix, StringComparison.Ordinal);
        Assert.Equal("r" + new string('f', 19), suffix);
    }

    [Fact]
    public void The_same_fingerprint_always_yields_the_same_suffix()
    {
        var fingerprint = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        Assert.Equal(
            ProvisioningRevisionSuffix.FromFingerprint(fingerprint),
            ProvisioningRevisionSuffix.FromFingerprint(fingerprint));
        Assert.Equal("r0123456789abcdef012", ProvisioningRevisionSuffix.FromFingerprint(fingerprint));
    }

    [Fact]
    public void A_suffix_longer_than_the_bound_is_rejected()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ProvisioningRevisionSuffix.Validate("r" + new string('a', 20)));
        var overflow = "r" + new string('1', 23);
        Assert.Equal(24, overflow.Length);
        Assert.True(
            ProvisioningEngineContainerApp.MaximumLength
            + ProvisioningRevisionSuffix.Separator.Length
            + overflow.Length
            > ProvisioningRevisionSuffix.MaximumRevisionNameLength);
        Assert.Throws<InvalidOperationException>(() => ProvisioningRevisionSuffix.Validate(overflow));
    }

    [Fact]
    public void The_bicep_workload_revision_name_already_fits_at_maximum_inputs()
    {
        // Bicep names the app `{workloadName}-app`. Workload names are capped at 16.
        // The suffix is take(planFingerprint, 24), and a collision retry may append `-r` plus
        // at most three digits. The longest revision name is therefore under Azure's 54-character
        // limit, so the Bicep path does not need a suffix change for parity.
        const int maximumBicepWorkloadNameLength = 16;
        const string bicepAppSuffix = "-app";
        const int bicepFingerprintSuffixLength = 24;
        const string bicepRetryOrdinal = "-r999";
        var appNameLength = maximumBicepWorkloadNameLength + bicepAppSuffix.Length;
        var suffixLength = bicepFingerprintSuffixLength + bicepRetryOrdinal.Length;
        var revisionNameLength =
            appNameLength + ProvisioningRevisionSuffix.Separator.Length + suffixLength;

        Assert.True(IsSafeWorkloadName(new string('w', maximumBicepWorkloadNameLength)));
        Assert.Equal(20, appNameLength);
        Assert.Equal(51, revisionNameLength);
        Assert.True(revisionNameLength <= ProvisioningRevisionSuffix.MaximumRevisionNameLength);
    }

    [Theory]
    [InlineData("aca-eng-", 32, 32)]  // Container Apps environment
    [InlineData("log-eng-", 63, 40)]  // Log Analytics workspace ("log-eng-" + 32-char guid)
    [InlineData("kv", 24, 22)]        // Key Vault ("kv" + 20 hex chars)
    [InlineData("sql-engine-", 63, 27)] // SQL server ("sql-engine-" + 16 hex chars)
    [InlineData("id-engine-", 128, 42)] // User-assigned identity (guid-N)
    [InlineData("elsa-engine-", 90, 44)] // Resource group (guid-N)
    public void Other_engine_azure_names_stay_within_their_limits(
        string prefix,
        int azureLimit,
        int expectedLength)
    {
        var instanceId = MaxLengthInstanceId.ToString("N");
        var name = prefix switch
        {
            "aca-eng-" => Truncate(prefix + instanceId, 32),
            "log-eng-" => Truncate(prefix + instanceId, 63),
            "kv" => prefix + instanceId[..20],
            "sql-engine-" => prefix + instanceId[..16],
            "id-engine-" => prefix + instanceId,
            "elsa-engine-" => ProvisioningEngineResourceGroup.ForInstance(MaxLengthInstanceId),
            _ => throw new ArgumentOutOfRangeException(nameof(prefix))
        };
        Assert.Equal(expectedLength, name.Length);
        Assert.True(name.Length <= azureLimit);
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private static bool IsSafeWorkloadName(string value) =>
        value.Length is >= 3 and <= 16
        && char.IsAsciiLetterOrDigit(value[0])
        && char.IsAsciiLetterOrDigit(value[^1])
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');
}
