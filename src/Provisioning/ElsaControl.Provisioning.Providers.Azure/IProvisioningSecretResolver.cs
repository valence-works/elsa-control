using ElsaControl.Provisioning.Core;

namespace ElsaControl.Provisioning.Providers.Azure;

/// <summary>
/// Resolves engine vault seed values at run time. Locators may sit in stack
/// config; values never do. Tests inject a static source.
/// </summary>
public interface IProvisioningSecretResolver
{
    bool IsReady { get; }

    Task EnsureReadyAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, string>> ResolveSecretConfigAsync(
        IReadOnlyDictionary<string, string> stackConfig,
        CancellationToken cancellationToken = default);
}

public sealed class EmptyProvisioningSecretResolver : IProvisioningSecretResolver
{
    public bool IsReady => true;

    public Task EnsureReadyAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyDictionary<string, string>> ResolveSecretConfigAsync(
        IReadOnlyDictionary<string, string> stackConfig,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(stackConfig);
        AzureEngineProgramPolicy.RejectSharedSeedValues(stackConfig);
        if (stackConfig.Values.Any(ProvisioningEventSanitizer.IsSecretLocator))
            throw new InvalidOperationException("Provisioning seed vault is not configured.");
        return Task.FromResult<IReadOnlyDictionary<string, string>>(
            new Dictionary<string, string>(StringComparer.Ordinal));
    }
}

public sealed class StaticProvisioningSecretResolver(IReadOnlyDictionary<string, string> values)
    : IProvisioningSecretResolver
{
    private readonly IReadOnlyDictionary<string, string> _values =
        new Dictionary<string, string>(values ?? throw new ArgumentNullException(nameof(values)), StringComparer.Ordinal);

    public bool IsReady => true;

    public Task EnsureReadyAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyDictionary<string, string>> ResolveSecretConfigAsync(
        IReadOnlyDictionary<string, string> stackConfig,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(stackConfig);
        AzureEngineProgramPolicy.RejectSharedSeedValues(stackConfig);
        AzureEngineProgramPolicy.RejectSharedSeedValues(_values);
        return Task.FromResult(_values);
    }
}
