using ElsaControl.Deployment.Core.Instances;
using ElsaControl.Provisioning.Abstractions;
using ElsaControl.Provisioning.Core;

namespace ElsaControl.Deployment.Azure;

/// <summary>
/// Maps sanitised Pulumi events onto the existing Azure step-ledger checkpoint
/// shape. Stage names and messages are customer-safe and match the seven-stage
/// projector. Raw Pulumi text is never copied.
/// </summary>
public static class PulumiStepLedgerMapper
{
    public const string ControlWorkspaceIdKey = "controlWorkspaceId";
    public const string ControlProviderOperationIdKey = "controlProviderOperationId";
    public const string ApplyCompletedMessage = "Apply completed.";

    public const string FoundationPreparingCode = "foundation.preparing";
    public const string FoundationReadyCode = "foundation.ready";
    public const string RegistryReadyCode = "configuration.registry-ready";
    public const string SecretsReadyCode = "configuration.secrets-ready";
    public const string DatabaseReadyCode = "configuration.database-ready";
    public const string RuntimeDeployingCode = "runtime.deploying";
    public const string RuntimeDeployedCode = "runtime.deployed";
    public const string ApplyCompletedCode = "azure.step.completed";
    public const string HealthVerifiedCode = "health.verified";
    public const string TrafficRoutedCode = "traffic.routed";
    public const string OperationSucceededCode = "azure.operation.succeeded";
    public const string CleanupSucceededCode = "azure.cleanup.succeeded";
    public const string ResourceGroupAbsentMessage = "Owned resource group is absent.";

    public const string FoundationPreparingMessage = "Foundation is preparing.";
    public const string FoundationReadyMessage = "Foundation is ready.";
    public const string RegistryReadyMessage = "Registry access is ready.";
    public const string SecretsReadyMessage = "Protected references are ready.";
    public const string DatabaseReadyMessage = "Database is ready.";
    public const string RuntimeDeployingMessage = "Runtime is deploying.";
    public const string RuntimeDeployedMessage = "Runtime is deployed.";
    public const string HealthVerifiedMessage = "The candidate is healthy.";
    public const string TrafficRoutedMessage = "Traffic is routed.";
    public const string OperationSucceededMessage = "Azure workload reconciliation completed.";
    public const string CleanupSucceededMessage = "Exact owned-resource cleanup was verified.";
    public const string ResourceFailedMessage = "The provider step failed.";
    public const string UpdateCancelledMessage = "The provider update was cancelled.";

    public static bool TryReadCorrelation(
        IReadOnlyDictionary<string, string>? stackConfig,
        out Guid workspaceId,
        out Guid providerOperationId)
    {
        workspaceId = Guid.Empty;
        providerOperationId = Guid.Empty;
        if (stackConfig is null)
            return false;
        return TryReadGuid(stackConfig, ControlWorkspaceIdKey, out workspaceId)
               && TryReadGuid(stackConfig, ControlProviderOperationIdKey, out providerOperationId);
    }

    public static PulumiLedgerCheckpoint? TryMap(
        ProvisioningEvent value,
        AzureProviderOperationPhase currentPhase)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Kind == ProvisioningEventKind.OperationStarted)
        {
            var phase = currentPhase == AzureProviderOperationPhase.Planned
                ? AzureProviderOperationPhase.FoundationSubmitted
                : currentPhase;
            return new PulumiLedgerCheckpoint(
                phase,
                AzureProviderRunnerStep.Foundation,
                AzureProviderOperationStatus.Running,
                FoundationPreparingCode,
                FoundationPreparingMessage,
                Finalize: false);
        }

        if (value.Kind == ProvisioningEventKind.OperationSucceeded)
        {
            var phase = Max(currentPhase, AzureProviderOperationPhase.TrafficPromoted);
            return new PulumiLedgerCheckpoint(
                phase,
                AzureProviderRunnerStep.Promotion,
                AzureProviderOperationStatus.Succeeded,
                OperationSucceededCode,
                OperationSucceededMessage,
                Finalize: true,
                AzureProviderHealth.Healthy);
        }

        if (value.Kind == ProvisioningEventKind.OperationCancelled)
        {
            return new PulumiLedgerCheckpoint(
                currentPhase == AzureProviderOperationPhase.Planned
                    ? AzureProviderOperationPhase.FoundationSubmitted
                    : currentPhase,
                AttemptedStepFor(currentPhase),
                AzureProviderOperationStatus.Cancelled,
                ManagedElsaReasonCodeCatalog.AzureDeploymentCanceled,
                UpdateCancelledMessage,
                Finalize: true);
        }

        if (value.Kind is ProvisioningEventKind.ResourceFailed or ProvisioningEventKind.OperationFailed)
        {
            return new PulumiLedgerCheckpoint(
                currentPhase == AzureProviderOperationPhase.Planned
                    ? AzureProviderOperationPhase.FoundationSubmitted
                    : currentPhase,
                AttemptedStepFor(currentPhase),
                AzureProviderOperationStatus.Failed,
                ManagedElsaReasonCodeCatalog.AzureDeploymentFailed,
                ResourceFailedMessage,
                Finalize: true);
        }

        if (value.Kind == ProvisioningEventKind.ResourceStarted)
            return MapStarted(value.ResourceKind, currentPhase);

        if (value.Kind == ProvisioningEventKind.ResourceSucceeded)
            return MapSucceeded(value.ResourceKind, currentPhase, value.Message);

        return null;
    }

    public static bool IsResourceGroupAbsentReceipt(ProvisioningEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Kind == ProvisioningEventKind.ResourceSucceeded
               && value.ResourceKind == "resource-group"
               && string.Equals(value.Message, ResourceGroupAbsentMessage, StringComparison.Ordinal);
    }

    public static PulumiLedgerCheckpoint ResourceGroupAbsent() =>
        new(
            AzureProviderOperationPhase.CleanupVerified,
            AzureProviderRunnerStep.Cleanup,
            AzureProviderOperationStatus.Running,
            CleanupSucceededCode,
            CleanupSucceededMessage,
            Finalize: false,
            ReplaceResources: true,
            Resources: new AzureProviderResourceReferences());

    public static PulumiLedgerCheckpoint DeleteSucceeded() =>
        new(
            AzureProviderOperationPhase.CleanupVerified,
            AzureProviderRunnerStep.Cleanup,
            AzureProviderOperationStatus.Succeeded,
            CleanupSucceededCode,
            CleanupSucceededMessage,
            Finalize: true,
            ReplaceResources: true,
            Resources: new AzureProviderResourceReferences());

    public static PulumiLedgerCheckpoint HealthVerified() =>
        new(
            AzureProviderOperationPhase.HealthVerified,
            AzureProviderRunnerStep.Health,
            AzureProviderOperationStatus.Running,
            HealthVerifiedCode,
            HealthVerifiedMessage,
            Finalize: false,
            AzureProviderHealth.Healthy);

    public static PulumiLedgerCheckpoint TrafficPromoted() =>
        new(
            AzureProviderOperationPhase.TrafficPromoted,
            AzureProviderRunnerStep.Promotion,
            AzureProviderOperationStatus.Running,
            TrafficRoutedCode,
            TrafficRoutedMessage,
            Finalize: false,
            AzureProviderHealth.Healthy);

    public static bool IsCustomerSafe(PulumiLedgerCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        return AzureProviderOperationValidation.IsSafeCode(checkpoint.Code)
               && !string.IsNullOrWhiteSpace(checkpoint.Message)
               && checkpoint.Message.Length <= 2000
               && !checkpoint.Message.Any(char.IsControl)
               && !ContainsSensitiveMarker(checkpoint.Message)
               && !ProvisioningEventSanitizer.ContainsSensitiveToken(checkpoint.Code)
               && !ProvisioningEventSanitizer.ContainsSensitiveToken(checkpoint.Message);
    }

    private static PulumiLedgerCheckpoint? MapStarted(string resourceKind, AzureProviderOperationPhase current)
    {
        if (IsFoundation(resourceKind))
        {
            var phase = current == AzureProviderOperationPhase.Planned
                ? AzureProviderOperationPhase.FoundationSubmitted
                : Max(current, AzureProviderOperationPhase.FoundationSubmitted);
            return new(
                phase,
                AzureProviderRunnerStep.Foundation,
                AzureProviderOperationStatus.Running,
                FoundationPreparingCode,
                FoundationPreparingMessage,
                Finalize: false);
        }

        if (IsRegistry(resourceKind))
            return Started(current, AzureProviderOperationPhase.AcrPullObserved, AzureProviderRunnerStep.AcrPull,
                RegistryReadyCode, RegistryReadyMessage);

        if (IsSecrets(resourceKind))
            return Started(current, AzureProviderOperationPhase.SeedSecretsObserved, AzureProviderRunnerStep.SeedSecrets,
                SecretsReadyCode, SecretsReadyMessage);

        if (IsDatabase(resourceKind))
            return Started(current, AzureProviderOperationPhase.SqlBootstrapReady, AzureProviderRunnerStep.SqlBootstrap,
                DatabaseReadyCode, DatabaseReadyMessage);

        if (IsRuntime(resourceKind))
            return Started(current, AzureProviderOperationPhase.WorkloadSubmitted, AzureProviderRunnerStep.Workload,
                RuntimeDeployingCode, RuntimeDeployingMessage);

        return null;
    }

    private static PulumiLedgerCheckpoint? MapSucceeded(
        string resourceKind,
        AzureProviderOperationPhase current,
        string message)
    {
        if (string.Equals(message, ApplyCompletedMessage, StringComparison.Ordinal))
        {
            var phase = Max(current, AzureProviderOperationPhase.WorkloadReady);
            return new(
                phase,
                AzureProviderRunnerStep.Workload,
                AzureProviderOperationStatus.Running,
                ApplyCompletedCode,
                ApplyCompletedMessage,
                Finalize: false);
        }

        if (IsFoundation(resourceKind))
        {
            var phase = resourceKind == ProvisioningCapability.SharedEnvironment
                ? AzureProviderOperationPhase.FoundationReady
                : AzureProviderOperationPhase.FoundationObserved;
            return Succeeded(current, phase, AzureProviderRunnerStep.Foundation, FoundationReadyCode, FoundationReadyMessage);
        }

        if (IsRegistry(resourceKind))
            return Succeeded(current, AzureProviderOperationPhase.AcrPullObserved, AzureProviderRunnerStep.AcrPull,
                RegistryReadyCode, RegistryReadyMessage);

        if (IsSecrets(resourceKind))
            return Succeeded(current, AzureProviderOperationPhase.SeedSecretsObserved, AzureProviderRunnerStep.SeedSecrets,
                SecretsReadyCode, SecretsReadyMessage);

        if (IsDatabase(resourceKind))
            return Succeeded(current, AzureProviderOperationPhase.SqlBootstrapReady, AzureProviderRunnerStep.SqlBootstrap,
                DatabaseReadyCode, DatabaseReadyMessage);

        if (IsRuntime(resourceKind))
            return Succeeded(current, AzureProviderOperationPhase.WorkloadReady, AzureProviderRunnerStep.Workload,
                RuntimeDeployedCode, RuntimeDeployedMessage);

        return null;
    }

    private static PulumiLedgerCheckpoint? Started(
        AzureProviderOperationPhase current,
        AzureProviderOperationPhase target,
        AzureProviderRunnerStep step,
        string code,
        string message)
    {
        if (AzureProviderOperationPhaseOrdering.Compare(current, Predecessor(target)) < 0)
            return null;
        var phase = current == AzureProviderOperationPhase.Planned
            ? AzureProviderOperationPhase.FoundationSubmitted
            : current;
        return new(phase, step, AzureProviderOperationStatus.Running, code, message, Finalize: false);
    }

    private static PulumiLedgerCheckpoint? Succeeded(
        AzureProviderOperationPhase current,
        AzureProviderOperationPhase target,
        AzureProviderRunnerStep step,
        string code,
        string message)
    {
        if (AzureProviderOperationPhaseOrdering.Compare(target, current) < 0)
            return null;
        return new(target, step, AzureProviderOperationStatus.Running, code, message, Finalize: false);
    }

    private static bool IsFoundation(string kind) =>
        kind is "resource-group" or "identity" or "observability"
            or ProvisioningCapability.SharedEnvironment;

    private static bool IsRegistry(string kind) => kind == "role-assignment";

    private static bool IsSecrets(string kind) => kind == ProvisioningCapability.Secrets;

    private static bool IsDatabase(string kind) =>
        kind is ProvisioningCapability.ManagedSql or "network";

    private static bool IsRuntime(string kind) => kind == ProvisioningCapability.ContainerRuntime;

    private static AzureProviderOperationPhase Predecessor(AzureProviderOperationPhase phase) => phase switch
    {
        AzureProviderOperationPhase.AcrPullObserved => AzureProviderOperationPhase.FoundationSubmitted,
        AzureProviderOperationPhase.SeedSecretsObserved => AzureProviderOperationPhase.FoundationSubmitted,
        AzureProviderOperationPhase.SqlBootstrapReady => AzureProviderOperationPhase.FoundationSubmitted,
        AzureProviderOperationPhase.WorkloadSubmitted => AzureProviderOperationPhase.FoundationObserved,
        _ => AzureProviderOperationPhase.Planned
    };

    private static AzureProviderOperationPhase Max(
        AzureProviderOperationPhase left,
        AzureProviderOperationPhase right) =>
        AzureProviderOperationPhaseOrdering.Compare(left, right) >= 0 ? left : right;

    private static AzureProviderRunnerStep? AttemptedStepFor(AzureProviderOperationPhase phase) => phase switch
    {
        AzureProviderOperationPhase.Planned or AzureProviderOperationPhase.FoundationSubmitted
            or AzureProviderOperationPhase.FoundationObserved => AzureProviderRunnerStep.Foundation,
        AzureProviderOperationPhase.AcrPullObserved => AzureProviderRunnerStep.AcrPull,
        AzureProviderOperationPhase.SeedSecretsObserved => AzureProviderRunnerStep.SeedSecrets,
        AzureProviderOperationPhase.SqlFirewallReady or AzureProviderOperationPhase.SqlBootstrapReady
            or AzureProviderOperationPhase.FoundationReady => AzureProviderRunnerStep.SqlBootstrap,
        AzureProviderOperationPhase.WorkloadSubmitted or AzureProviderOperationPhase.WorkloadReady =>
            AzureProviderRunnerStep.Workload,
        AzureProviderOperationPhase.HealthVerified => AzureProviderRunnerStep.Health,
        AzureProviderOperationPhase.TrafficPromoted => AzureProviderRunnerStep.Promotion,
        _ => AzureProviderRunnerStep.Foundation
    };

    private static bool ContainsSensitiveMarker(string value) =>
        value.Contains("password", StringComparison.OrdinalIgnoreCase)
        || value.Contains("token", StringComparison.OrdinalIgnoreCase)
        || value.Contains("secret", StringComparison.OrdinalIgnoreCase)
        || value.Contains("connectionstring", StringComparison.OrdinalIgnoreCase)
        || value.Contains("stack trace", StringComparison.OrdinalIgnoreCase);

    private static bool TryReadGuid(
        IReadOnlyDictionary<string, string> stackConfig,
        string key,
        out Guid value)
    {
        value = Guid.Empty;
        return stackConfig.TryGetValue(key, out var text)
               && Guid.TryParse(text, out value)
               && value != Guid.Empty;
    }
}

public sealed record PulumiLedgerCheckpoint(
    AzureProviderOperationPhase Phase,
    AzureProviderRunnerStep? AttemptedStep,
    AzureProviderOperationStatus Status,
    string Code,
    string Message,
    bool Finalize,
    AzureProviderHealth Health = AzureProviderHealth.Unknown,
    string? Endpoint = null,
    bool ReplaceResources = false,
    AzureProviderResourceReferences? Resources = null);
