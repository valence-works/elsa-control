using ElsaControl.Billing.Stripe;
using ElsaControl.Deployment.Core.Instances;
using Microsoft.Extensions.Options;

namespace ElsaControl.Api.OrganizationBilling;

public enum StagingRecoveryLifecycleLeverOutcome
{
    Fired,
    Disabled,
    InstanceNotAllowed,
    InstanceNotFound
}

public sealed record StagingRecoveryLifecycleLeverResult(
    StagingRecoveryLifecycleLeverOutcome Outcome,
    StagingRecoveryLifecycleLeverCommit? Commit = null);

/// <summary>
/// Staging-only operator action: accept a normal Reconcile on an allowlisted
/// instance with no active operation and move it to RecoveryRequired through
/// the existing transition methods. It never writes operation state directly.
/// </summary>
public sealed class StagingRecoveryLifecycleLever(
    IStagingRecoveryLifecycleLeverStore store,
    IOptions<StagingRecoveryLifecycleLeverOptions> options,
    IOptions<StripeBillingOptions> stripeOptions,
    ILogger<StagingRecoveryLifecycleLever> logger)
{
    public async Task<StagingRecoveryLifecycleLeverResult> FireAsync(
        Guid instanceId,
        string? operatorSubject,
        CancellationToken cancellationToken = default)
    {
        if (instanceId == Guid.Empty)
            throw new ArgumentException("Instance ID is required.", nameof(instanceId));

        if (!StagingLifecycleLeverGate.IsArmed(options.Value.Enabled, stripeOptions.Value))
        {
            if (options.Value.Enabled)
            {
                logger.LogWarning(
                    "The staging recovery lifecycle lever is disabled because a required staging signal is missing.");
            }

            return new StagingRecoveryLifecycleLeverResult(StagingRecoveryLifecycleLeverOutcome.Disabled);
        }

        if (!options.Value.AllowsInstance(instanceId))
            return new StagingRecoveryLifecycleLeverResult(StagingRecoveryLifecycleLeverOutcome.InstanceNotAllowed);

        try
        {
            var commit = await store.AcceptReconcileAndRequireRecoveryAsync(
                instanceId,
                operatorSubject,
                cancellationToken);
            return new StagingRecoveryLifecycleLeverResult(StagingRecoveryLifecycleLeverOutcome.Fired, commit);
        }
        catch (KeyNotFoundException)
        {
            return new StagingRecoveryLifecycleLeverResult(StagingRecoveryLifecycleLeverOutcome.InstanceNotFound);
        }
    }
}
