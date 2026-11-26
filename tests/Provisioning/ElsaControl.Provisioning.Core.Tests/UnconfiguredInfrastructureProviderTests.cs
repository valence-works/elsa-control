using ElsaControl.Provisioning.Abstractions;

namespace ElsaControl.Provisioning.Core.Tests;

public sealed class UnconfiguredInfrastructureProviderTests
{
    [Fact]
    public async Task Every_operation_fails_closed()
    {
        var provider = new UnconfiguredInfrastructureProvider();
        var operation = new ProvisioningOperation(
            Guid.NewGuid(),
            ProvisioningCommand.Provision,
            ProvisioningOperationState.Running,
            UnconfiguredInfrastructureProvider.Id,
            ProvisioningStackKind.Platform,
            "staging",
            "unconfigured",
            "unconfigured-hash",
            DateTimeOffset.UtcNow);
        var request = ProvisioningFixtures.Request(
            ProvisioningCommand.Provision,
            ProvisioningFixtures.PlatformTarget(),
            ProvisioningFixtures.PlatformSpec(),
            "unconfigured");
        foreach (var result in new[]
                 {
                     await provider.PreviewAsync(operation, request),
                     await provider.ProvisionAsync(operation, request),
                     await provider.UpdateAsync(operation, request),
                     await provider.RefreshAsync(operation, request),
                     await provider.CancelAsync(operation, request),
                     await provider.DestroyAsync(operation, request)
                 })
        {
            Assert.False(result.Succeeded);
            Assert.Equal(ProvisioningFailureCodes.NotImplemented, result.FailureCode);
        }
    }
}
