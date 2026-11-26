using ElsaControl.Provisioning.Abstractions;
using ElsaControl.Provisioning.Core;

namespace ElsaControl.Provisioning.Core.Tests;

public sealed class ProvisioningEventSanitizerTests
{
    [Fact]
    public async Task Store_drops_subscription_resource_and_secret_shapes()
    {
        var operationId = Guid.NewGuid();
        var store = new InMemoryProvisioningStore();
        await store.AppendAsync(new ProvisioningEvent(
            operationId,
            ProvisioningEventKind.ResourceFailed,
            "failed /subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-x password=super-secret https://app.azurecontainerapps.io",
            DateTimeOffset.UtcNow,
            "/subscriptions/00000000-0000-0000-0000-000000000000/providers/Microsoft.App/containerApps/app"));
        await store.AppendAsync(new ProvisioningEvent(
            operationId,
            ProvisioningEventKind.ResourceSucceeded,
            ProvisioningReasonCodes.ResourceSucceeded,
            DateTimeOffset.UtcNow,
            ProvisioningCapabilities.Database));

        var events = await store.ListAsync(operationId);
        Assert.Equal(2, events.Count);
        Assert.All(events, e =>
        {
            Assert.DoesNotContain("subscriptions", e.ReasonCode, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("password=", e.ReasonCode, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("azurecontainerapps", e.ReasonCode, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("resourceGroups", e.ReasonCode, StringComparison.OrdinalIgnoreCase);
            Assert.True(e.Capability is null || e.Capability == ProvisioningCapabilities.Database);
        });
        Assert.Equal(ProvisioningReasonCodes.OperationFailed, events[0].ReasonCode);
        Assert.Null(events[0].Capability);
        Assert.Equal(ProvisioningCapabilities.Database, events[1].Capability);
    }

    [Theory]
    [InlineData("/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg")]
    [InlineData("https://example.azurecontainerapps.io")]
    [InlineData("AccountKey=abc")]
    [InlineData("password=secret")]
    public void LooksUnsafe_rejects_cloud_identifiers_and_secret_shapes(string value) =>
        Assert.True(ProvisioningEventSanitizer.LooksUnsafe(value));
}
