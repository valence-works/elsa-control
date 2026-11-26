using ElsaControl.Provisioning.Abstractions;
using ElsaControl.Provisioning.Core;
using Microsoft.Extensions.Options;

namespace ElsaControl.Deployment.Azure;

/// <summary>
/// Enqueues a Pulumi engine run for an admitted Azure reconcile when the
/// provisioning worker is on and dispatch options are complete. Returns false
/// so the existing Bicep executor remains the path otherwise.
/// </summary>
public interface IAzurePulumiProvisioningDispatcher
{
    Task<bool> TryDispatchAsync(
        AzureProviderOperation operation,
        AzureWorkloadPlan plan,
        CancellationToken cancellationToken = default);
}

public sealed class AzurePulumiDispatchOptions
{
    public const string ConfigurationSection = "Provisioning";

    public string? EnvironmentName { get; set; }
    public string? StateBackendLocator { get; set; }
    public string? SecretsProviderLocator { get; set; }
    public string? CredentialLocator { get; set; }
    public string? SqlBootstrapObjectId { get; set; }
    public string? SqlBootstrapLogin { get; set; }

    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(EnvironmentName)
        && !string.IsNullOrWhiteSpace(StateBackendLocator)
        && !string.IsNullOrWhiteSpace(SecretsProviderLocator)
        && !string.IsNullOrWhiteSpace(CredentialLocator)
        && Guid.TryParse(SqlBootstrapObjectId, out var bootstrap)
        && bootstrap != Guid.Empty
        && !string.IsNullOrWhiteSpace(SqlBootstrapLogin);
}

public sealed class AzurePulumiProvisioningDispatcher(
    IProvisioningService provisioning,
    IOptions<AzurePulumiDispatchOptions> options) : IAzurePulumiProvisioningDispatcher
{
    public async Task<bool> TryDispatchAsync(
        AzureProviderOperation operation,
        AzureWorkloadPlan plan,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(plan);
        var configured = options.Value;
        if (!configured.IsComplete
            || operation.Action != AzureProviderOperationAction.Reconcile
            || operation.InstanceId is not { } instanceId
            || instanceId == Guid.Empty)
        {
            return false;
        }

        try
        {
            var config = new Dictionary<string, string>(
                AzureWorkloadPlanProvisioningMapper.StackConfig(
                    plan,
                    instanceId,
                    AzureWorkloadPlanProvisioningMapper.PlatformStackReference(configured.EnvironmentName!.Trim())),
                StringComparer.Ordinal)
            {
                [PulumiStepLedgerMapper.ControlWorkspaceIdKey] = operation.WorkspaceId.ToString("D"),
                [PulumiStepLedgerMapper.ControlProviderOperationIdKey] = operation.Id.ToString("D"),
                ["sqlBootstrapObjectId"] = configured.SqlBootstrapObjectId!.Trim(),
                ["sqlBootstrapLogin"] = configured.SqlBootstrapLogin!.Trim()
            };
            var request = new ProvisioningRequest(
                ProvisioningCommand.Provision,
                AzureWorkloadPlanProvisioningMapper.Spec(plan),
                new ProvisioningTarget(
                    "azure",
                    ProvisioningStackKind.Engine,
                    AzureWorkloadPlanProvisioningMapper.EngineStackName(instanceId),
                    new CredentialReference(CredentialReference.AzureWorkloadIdentityKind, configured.CredentialLocator!.Trim()),
                    new StateBackendReference(StateBackendReference.AzureBlobKind, configured.StateBackendLocator!.Trim()),
                    new SecretsProviderReference(SecretsProviderReference.AzureKeyVaultKind, configured.SecretsProviderLocator!.Trim()),
                    configured.EnvironmentName.Trim()),
                operation.IdempotencyKey,
                config);
            var result = await provisioning.ProvisionAsync(request, cancellationToken).ConfigureAwait(false);
            return result.State is ProvisioningOperationState.Queued
                or ProvisioningOperationState.Accepted
                or ProvisioningOperationState.Running
                or ProvisioningOperationState.Succeeded;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
