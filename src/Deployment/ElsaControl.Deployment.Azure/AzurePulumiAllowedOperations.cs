using ElsaControl.Deployment.Abstractions.Instances;

namespace ElsaControl.Deployment.Azure;

/// <summary>
/// Pulumi-owned engines use <c>LatestRevision=true</c> traffic until revision-pinned
/// promotion lands (#1046). Until then only Create, Retry of a never-Ready Create,
/// and Delete may run. Any other lifecycle action fails closed.
/// </summary>
public static class AzurePulumiAllowedOperations
{
    public static bool AllowsLifecycle(AzureProviderOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (operation.Action == AzureProviderOperationAction.Delete)
            return true;
        return operation.LifecycleAction is ElsaInstanceOperationAction.Create
            or ElsaInstanceOperationAction.Retry;
    }

    public static bool Allows(AzureProviderOperation operation, bool instanceReachedReady)
    {
        if (!AllowsLifecycle(operation))
            return false;
        return operation.Action == AzureProviderOperationAction.Delete
            || operation.LifecycleAction != ElsaInstanceOperationAction.Retry
            || !instanceReachedReady;
    }
}
