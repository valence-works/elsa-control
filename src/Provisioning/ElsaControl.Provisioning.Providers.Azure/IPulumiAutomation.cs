using ElsaControl.Provisioning.Abstractions;

namespace ElsaControl.Provisioning.Providers.Azure;

/// <summary>
/// Seam over the Pulumi Automation API so tests can run without a Pulumi CLI
/// and so the runner can later move to a separate service or BYO agent.
/// </summary>
public interface IPulumiAutomation
{
    Task RunAsync(PulumiAutomationRequest request, IProviderEventSink events, CancellationToken cancellationToken = default);

    Task CancelAsync(Guid operationId, CancellationToken cancellationToken = default);
}

public sealed record PulumiAutomationRequest
{
    public PulumiAutomationRequest(
        Guid operationId,
        ProvisioningOperationKind kind,
        string project,
        string stack,
        IReadOnlyDictionary<string, string> config,
        IStateBackend stateBackend,
        ISecretsProvider secretsProvider,
        ResolvedCredential credential)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(stack);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(stateBackend);
        ArgumentNullException.ThrowIfNull(secretsProvider);
        ArgumentNullException.ThrowIfNull(credential);
        OperationId = operationId;
        Kind = kind;
        Project = project;
        Stack = stack;
        Config = config;
        StateBackend = stateBackend;
        SecretsProvider = secretsProvider;
        Credential = credential;
    }

    public Guid OperationId { get; }

    public ProvisioningOperationKind Kind { get; }

    public string Project { get; }

    public string Stack { get; }

    public IReadOnlyDictionary<string, string> Config { get; }

    public IStateBackend StateBackend { get; }

    public ISecretsProvider SecretsProvider { get; }

    public ResolvedCredential Credential { get; }
}
