using ElsaControl.Provisioning.Abstractions;

namespace ElsaControl.Provisioning.Core.Tests;

public sealed class ProvisioningEventSanitizerTests
{
    [Theory]
    [InlineData("Apply started.")]
    [InlineData("Platform stack is absent.")]
    [InlineData("Operation succeeded.")]
    public void Safe_messages_pass_through(string message)
    {
        var sanitised = ProvisioningEventSanitizer.Sanitize(Event(message, ProvisioningCapability.ContainerRuntime));
        Assert.Equal(message, sanitised.Message);
        Assert.Equal(ProvisioningCapability.ContainerRuntime, sanitised.ResourceKind);
    }

    [Theory]
    [InlineData("subscriptions/not-a-customer-id")]
    [InlineData("resource id leaked")]
    [InlineData("/resourcegroups/example")]
    [InlineData("/providers/Microsoft.App")]
    [InlineData("https://example.azurewebsites.net")]
    [InlineData("password=not-a-real-secret")]
    [InlineData("accountkey=not-a-real-key")]
    public void Sensitive_messages_are_replaced(string message)
    {
        var sanitised = ProvisioningEventSanitizer.Sanitize(Event(message, ProvisioningCapability.Secrets));
        Assert.Equal(ProvisioningEventSanitizer.DefaultMessage, sanitised.Message);
        Assert.False(ProvisioningEventSanitizer.ContainsSensitiveToken(sanitised.Message));
        Assert.False(ProvisioningEventSanitizer.ContainsSensitiveToken(sanitised.ResourceKind));
    }

    [Fact]
    public void Capability_kind_secrets_is_not_treated_as_a_secret_value()
    {
        var sanitised = ProvisioningEventSanitizer.Sanitize(
            Event("Apply completed.", ProvisioningCapability.Secrets));
        Assert.Equal(ProvisioningCapability.Secrets, sanitised.ResourceKind);
    }

    private static ProvisioningEvent Event(string message, string resourceKind) =>
        new(Guid.NewGuid(), ProvisioningEventKind.ResourceStarted, resourceKind, message, DateTimeOffset.UtcNow);
}
