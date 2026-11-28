namespace ElsaControl.Provisioning.Core.Tests;

public sealed class ProvisioningExceptionLogTests
{
    [Fact]
    public void Redacts_secrets_and_keeps_the_stderr_tail()
    {
        var exception = new CommandLikeException(
            "could not create secrets manager for new stack",
            "password=super-secret\nparse azurekeyvault://ecseedkvtud53zotij43k.vault.azure.net/keys/pulumi-secrets: invalid port\n?sig=not-a-real-signature");
        Assert.Equal("could not create secrets manager for new stack", ProvisioningExceptionLog.Message(exception));
        var tail = ProvisioningExceptionLog.StderrTail(exception);
        Assert.NotNull(tail);
        Assert.Contains("invalid port", tail, StringComparison.Ordinal);
        Assert.Contains("password=redacted", tail, StringComparison.Ordinal);
        Assert.Contains("sig=redacted", tail, StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret", tail, StringComparison.Ordinal);
        Assert.DoesNotContain("not-a-real-signature", tail, StringComparison.Ordinal);
    }

    private sealed class CommandLikeException : Exception
    {
        public CommandLikeException(string message, string standardError)
            : base(message)
        {
            StandardError = standardError;
        }

        public string StandardError { get; }
    }
}
