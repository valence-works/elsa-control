namespace ElsaControl.Deployment.Core.Provisioning;

/// <summary>
/// Reads deployment environments that are safe candidates for a reviewed
/// managed-engine provisioning request.
/// </summary>
public interface IEngineProvisioningTargetStore
{
    Task<IReadOnlyList<EngineProvisioningTarget>> GetAvailableTargetsAsync(
        Guid workspaceId,
        CancellationToken cancellationToken = default);
}

public sealed record EngineProvisioningTarget(
    Guid ApplicationId,
    Guid EnvironmentId);
