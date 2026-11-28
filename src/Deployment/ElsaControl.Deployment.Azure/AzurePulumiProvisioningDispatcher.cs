using System.Collections.Concurrent;
using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Core.Instances;
using ElsaControl.Provisioning.Abstractions;
using ElsaControl.Provisioning.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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

    /// <summary>
    /// Process-local skip for Azure operations recently enqueued in this process.
    /// The durable guard is the store lease taken around enqueue; this set is
    /// only an optimisation and must expire so a row whose run ended without a
    /// ledger write can self-heal.
    /// </summary>
    bool HasDispatched(Guid operationId) => false;
}

public sealed record AzurePulumiDispatchResult(
    bool Dispatched,
    Guid? ProvisioningOperationId = null,
    ProvisioningOperationState? State = null,
    string? FailureCode = null,
    IReadOnlyDictionary<string, string>? Outputs = null);

public sealed class AzurePulumiProvisioningDispatcher(
    IProvisioningService provisioning,
    IOptions<AzurePulumiDispatchOptions> options,
    IOptions<ProvisioningOptions> provisioningOptions,
    IInfrastructureProvider? provider = null,
    ILogger<AzurePulumiProvisioningDispatcher>? logger = null,
    TimeProvider? timeProvider = null,
    AzureManagedHandoffOptions? managedHandoff = null) : IAzurePulumiProvisioningDispatcher
{
    internal static readonly TimeSpan DispatchSkipTtl = TimeSpan.FromMinutes(2);

    /// <summary>Column bound of <c>provisioning.Operations.IdempotencyKey</c>.</summary>
    internal const int MaximumProvisioningIdempotencyKeyLength = 256;

    private readonly ILogger<AzurePulumiProvisioningDispatcher> _logger =
        logger ?? NullLogger<AzurePulumiProvisioningDispatcher>.Instance;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<Guid, long> _dispatchedAt = new();

    public bool CanDispatch =>
        provisioningOptions.Value.WorkerEnabled
        && string.Equals(provider?.ProviderId, AzurePulumiProvisioningBackendSelector.AzureProviderId, StringComparison.Ordinal)
        && options.Value.IsComplete;

    public bool HasDispatched(Guid operationId)
    {
        var now = _timeProvider.GetUtcNow();
        Prune(now);
        if (!_dispatchedAt.TryGetValue(operationId, out var ticks))
            return false;
        if (now - FromUtcTicks(ticks) <= DispatchSkipTtl)
            return true;
        _dispatchedAt.TryRemove(operationId, out _);
        return false;
    }

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
            || instanceId == Guid.Empty
            || !AzurePulumiAllowedOperations.AllowsLifecycle(operation))
        {
            return new AzurePulumiDispatchResult(false);
        }

        ProvisioningRequest? request = null;
        try
        {
            var configured = options.Value;
            // Same handoff inputs as the Bicep runner: Control's own origin and session ceiling,
            // with the runtime permissions selected for this plan's release line.
            var handoff = plan.ManagedHandoff && managedHandoff is not null
                ? managedHandoff with
                {
                    AllowedRuntimePermissions = ManagedElsaRuntimePermissions.SelectedFor(
                        plan.ManagedHandoffStudioGrants,
                        plan.ReleaseLine,
                        plan.ElsaVersion)
                }
                : null;
            if (plan.ManagedHandoff && handoff is null && operation.Action != AzureProviderOperationAction.Delete)
            {
                _logger.LogError(
                    "Pulumi dispatch refused. OperationId={OperationId} Reason={Reason}",
                    operation.Id,
                    "The release declares the managed handoff but Control's handoff inputs (ControlPlane:Origin) are not configured.");
                return new AzurePulumiDispatchResult(false);
            }

            var config = new Dictionary<string, string>(
                AzureWorkloadPlanProvisioningMapper.StackConfig(
                    plan,
                    instanceId,
                    AzureWorkloadPlanProvisioningMapper.PlatformStackReference(configured.EnvironmentName!.Trim()),
                    handoffConfigured: handoff is not null,
                    handoff: handoff),
                StringComparer.Ordinal)
            {
                [PulumiStepLedgerMapper.ControlWorkspaceIdKey] = operation.WorkspaceId.ToString("D"),
                [PulumiStepLedgerMapper.ControlProviderOperationIdKey] = operation.Id.ToString("D"),
                ["sqlBootstrapObjectId"] = configured.SqlBootstrapObjectId!.Trim(),
                ["sqlBootstrapLogin"] = configured.SqlBootstrapLogin!.Trim()
            };
            request = new ProvisioningRequest(
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
                ProvisioningIdempotencyKey(operation.IdempotencyKey),
                config);
            return await EnqueueAsync(operation, request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(
                exception,
                "Pulumi dispatch failed. ExceptionType={ExceptionType} ExceptionMessage={ExceptionMessage} PulumiStderr={PulumiStderr}",
                exception.GetType().FullName,
                ProvisioningExceptionLog.Message(exception),
                ProvisioningExceptionLog.StderrTail(exception));
            if (request is null)
                return new AzurePulumiDispatchResult(false);
            try
            {
                return await EnqueueAsync(operation, request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception retry) when (retry is not OperationCanceledException)
            {
                _logger.LogError(
                    retry,
                    "Pulumi dispatch retry failed after commit. ExceptionType={ExceptionType} ExceptionMessage={ExceptionMessage} PulumiStderr={PulumiStderr}",
                    retry.GetType().FullName,
                    ProvisioningExceptionLog.Message(retry),
                    ProvisioningExceptionLog.StderrTail(retry));
                return new AzurePulumiDispatchResult(false);
            }
        }
    }

    private async Task<AzurePulumiDispatchResult> EnqueueAsync(
        AzureProviderOperation operation,
        ProvisioningRequest request,
        CancellationToken cancellationToken)
    {
        var result = operation.Action == AzureProviderOperationAction.Delete
            ? await provisioning.DestroyAsync(request, cancellationToken).ConfigureAwait(false)
            : await provisioning.ProvisionAsync(request, cancellationToken).ConfigureAwait(false);
        var run = await provisioning.GetOperationAsync(result.OperationId, cancellationToken)
            .ConfigureAwait(false);
        var state = run?.State ?? result.State;
        if (state is ProvisioningOperationState.Queued
            or ProvisioningOperationState.Accepted
            or ProvisioningOperationState.Running
            or ProvisioningOperationState.Succeeded
            or ProvisioningOperationState.Failed
            or ProvisioningOperationState.Cancelled
            or ProvisioningOperationState.LockConflict)
        {
            var now = _timeProvider.GetUtcNow();
            Prune(now);
            _dispatchedAt[operation.Id] = now.UtcTicks;
            return new AzurePulumiDispatchResult(
                true,
                result.OperationId,
                state,
                run?.FailureCode ?? result.Code);
        }

        return new AzurePulumiDispatchResult(false);
    }

    /// <summary>
    /// Provider keys may grow to 512 characters through the bounded Delete retry lineage, but the
    /// provisioning ledger column holds 256. Keys that fit are used verbatim so existing
    /// provisioning rows still replay; longer keys map to a stable digest of the full key.
    /// </summary>
    internal static string ProvisioningIdempotencyKey(string providerIdempotencyKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerIdempotencyKey);
        if (providerIdempotencyKey.Length <= MaximumProvisioningIdempotencyKeyLength)
            return providerIdempotencyKey;
        var digest = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(providerIdempotencyKey)))
            .ToLowerInvariant();
        return $"azure-provider-operation:sha256:{digest}";
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var pair in _dispatchedAt)
        {
            if (now - FromUtcTicks(pair.Value) > DispatchSkipTtl)
                _dispatchedAt.TryRemove(pair.Key, out _);
        }
    }

    private static DateTimeOffset FromUtcTicks(long ticks) => new(ticks, TimeSpan.Zero);
}
