using ElsaControl.Deployment.Core.Instances;
using ElsaControl.Provisioning.Abstractions;
using ElsaControl.Provisioning.Core;
using Microsoft.Extensions.Options;

namespace ElsaControl.Deployment.Azure;

/// <summary>
/// Enqueues a Pulumi engine run for a pinned Pulumi instance. Fail-closed:
/// never returns a Bicep-fallback signal. Incomplete options, a missing
/// instance id, or a dispatch exception fail the Azure operation.
/// </summary>
public interface IAzurePulumiProvisioningDispatcher
{
    bool CanDispatch { get; }

    Task<AzurePulumiDispatchResult> DispatchAsync(
        AzureProviderOperation operation,
        AzureWorkloadPlan plan,
        CancellationToken cancellationToken = default);
}

public sealed record AzurePulumiDispatchResult(
    bool Dispatched,
    Guid? ProvisioningOperationId = null);

public sealed class AzurePulumiProvisioningDispatcher(
    IProvisioningService provisioning,
    IOptions<AzurePulumiDispatchOptions> options,
    IOptions<ProvisioningOptions> provisioningOptions,
    IInfrastructureProvider? provider = null) : IAzurePulumiProvisioningDispatcher
{
    public bool CanDispatch =>
        provisioningOptions.Value.WorkerEnabled
        && string.Equals(provider?.ProviderId, AzurePulumiProvisioningBackendSelector.AzureProviderId, StringComparison.Ordinal)
        && options.Value.IsComplete;

    public async Task<AzurePulumiDispatchResult> DispatchAsync(
        AzureProviderOperation operation,
        AzureWorkloadPlan plan,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(plan);
        if (!CanDispatch
            || operation.InstanceId is not { } instanceId
            || instanceId == Guid.Empty)
        {
            return new AzurePulumiDispatchResult(false);
        }

        try
        {
            var configured = options.Value;
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
                operation.Action == AzureProviderOperationAction.Delete
                    ? ProvisioningCommand.Destroy
                    : ProvisioningCommand.Provision,
                AzureWorkloadPlanProvisioningMapper.Spec(plan),
                new ProvisioningTarget(
                    AzurePulumiProvisioningBackendSelector.AzureProviderId,
                    ProvisioningStackKind.Engine,
                    AzureWorkloadPlanProvisioningMapper.EngineStackName(instanceId),
                    new CredentialReference(CredentialReference.AzureWorkloadIdentityKind, configured.CredentialLocator!.Trim()),
                    new StateBackendReference(StateBackendReference.AzureBlobKind, configured.StateBackendLocator!.Trim()),
                    new SecretsProviderReference(SecretsProviderReference.AzureKeyVaultKind, configured.SecretsProviderLocator!.Trim()),
                    configured.EnvironmentName.Trim()),
                operation.IdempotencyKey,
                config);
            var result = operation.Action == AzureProviderOperationAction.Delete
                ? await provisioning.DestroyAsync(request, cancellationToken).ConfigureAwait(false)
                : await provisioning.ProvisionAsync(request, cancellationToken).ConfigureAwait(false);
            return result.State is ProvisioningOperationState.Queued
                or ProvisioningOperationState.Accepted
                or ProvisioningOperationState.Running
                or ProvisioningOperationState.Succeeded
                ? new AzurePulumiDispatchResult(true, result.OperationId)
                : new AzurePulumiDispatchResult(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new AzurePulumiDispatchResult(false);
        }
    }
}
