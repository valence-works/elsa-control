namespace ElsaControl.Deployment.Azure;

/// <summary>
/// Reads the pinned realization backend for an engine. Missing rows are Bicep.
/// </summary>
public interface IElsaInstanceProvisioningBackendStore
{
    Task<ElsaInstanceProvisioningBackend> GetBackendAsync(
        Guid workspaceId,
        Guid instanceId,
        CancellationToken cancellationToken = default);
}
