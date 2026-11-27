namespace ElsaControl.Deployment.Azure;

/// <summary>
/// Pulumi-owned Create/Retry continuation after apply: HTTP health of the
/// stack endpoint, then a read-only check that ingress still points at the
/// latest revision. Never writes traffic weights and never calls Bicep.
/// </summary>
public interface IAzurePulumiReadinessVerifier
{
    Task VerifyAsync(AzureProviderOperation operation, CancellationToken cancellationToken = default);
}
