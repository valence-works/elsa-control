namespace ElsaControl.Provisioning.Abstractions;

/// <summary>
/// Where to realize a spec: which provider, which credential, which stack.
/// Engine stacks optionally name the platform stack they read through a
/// <c>StackReference</c>.
/// </summary>
public sealed record ProvisioningTarget
{
    public ProvisioningTarget(
        string providerId,
        CredentialReference credential,
        StackIdentity stack,
        StackIdentity? platformStack = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(stack);
        ProviderId = providerId;
        Credential = credential;
        Stack = stack;
        PlatformStack = platformStack;
    }

    public string ProviderId { get; init; }

    public CredentialReference Credential { get; init; }

    public StackIdentity Stack { get; init; }

    public StackIdentity? PlatformStack { get; init; }
}
