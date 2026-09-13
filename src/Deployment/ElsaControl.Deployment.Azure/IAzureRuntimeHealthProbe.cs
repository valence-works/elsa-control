using ElsaControl.Deployment.Core.Instances;

namespace ElsaControl.Deployment.Azure;

/// <summary>
/// Read-only probe of a workload's runtime health route on its verified managed origin. It never
/// exercises Azure authority or mutates resources, and it returns only a classification and a
/// fixed safe code, never the endpoint, the response body or process output.
/// </summary>
public interface IAzureRuntimeHealthProbe
{
    Task<ElsaInstanceHealthProbeResult> ProbeAsync(
        string workloadName,
        string endpointOrigin,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}
