namespace ElsaControl.Deployment.Core.Instances;

/// <summary>
/// Reads the current runtime health of one Ready managed instance from its current deployment
/// endpoint. Implementations are read-only: they never apply, retry or mutate provider resources,
/// and they return only a provider-neutral classification with a bounded safe code, never an
/// endpoint, host, response body, token or exception text.
/// </summary>
public interface IElsaInstanceProviderHealthProbePort
{
    Task<ElsaInstanceHealthProbeResult> ProbeAsync(
        ElsaInstanceHealthProbeRequest request,
        CancellationToken cancellationToken = default);
}
