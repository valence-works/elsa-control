namespace ElsaControl.Deployment.Azure;

/// <summary>
/// Resolves the provider-safe plan retained with a durable operation. Implementations must only
/// return an admitted plan; they must never fetch or deserialize a customer payload while a
/// worker is recovering an operation.
/// </summary>
public interface IAzureProviderPlanSource
{
    AzureWorkloadPlan? Resolve(AzureProviderOperation operation);
}

/// <summary>
/// Reconstructs a plan from the immutable, validated operation columns. Missing or unsafe
/// admission evidence produces no plan, causing the worker to leave the operation for explicit
/// recovery rather than guessing at provider inputs.
/// </summary>
public sealed class PersistedAzureProviderPlanSource(AzureProviderTargetScope? providerScope = null) : IAzureProviderPlanSource
{
    public AzureWorkloadPlan? Resolve(AzureProviderOperation operation) =>
        AzureProviderOperationService.TryRestorePlan(operation, providerScope);
}
