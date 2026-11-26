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

    [Theory]
    [InlineData("https://example.blob.core.windows.net/file?sig=not-a-real-signature")]
    [InlineData("https://user:not-a-password@registry.example/path")]
    public void Url_credentials_are_rejected(string value) =>
        Assert.True(ProvisioningEventSanitizer.ContainsSecretOrResourceIdentifier(value));

    [Fact]
    public void Image_repository_hosts_are_admitted() =>
        Assert.False(ProvisioningEventSanitizer.ContainsSecretOrResourceIdentifier(
            "valenceruntimeimages.azurecr.io/runtime-combined"));

    [Fact]
    public void Secret_locators_are_admitted() =>
        Assert.False(ProvisioningEventSanitizer.ContainsSecretOrResourceIdentifier(
            "secret://vault/database-connection"));

    [Fact]
    public void Admin_password_override_key_is_allowlisted()
    {
        Assert.True(ProvisioningEventSanitizer.IsAdmittedSecretConfigKey(
            ProvisioningEventSanitizer.AdminPasswordOverrideKey));
        Assert.True(ProvisioningEventSanitizer.ContainsSecretOrResourceIdentifier(
            ProvisioningEventSanitizer.AdminPasswordOverrideKey));
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
