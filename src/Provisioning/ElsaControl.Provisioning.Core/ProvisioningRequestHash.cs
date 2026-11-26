using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ElsaControl.Provisioning.Abstractions;

namespace ElsaControl.Provisioning.Core;

/// <summary>
/// Stable hash of a provisioning request. Shared by in-memory and durable stores.
/// </summary>
public static class ProvisioningRequestHash
{
    public static string Compute(ProvisioningRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var payload = JsonSerializer.Serialize(new
        {
            request.Command,
            request.Target.ProviderId,
            request.Target.StackKind,
            Project = ProvisioningProjects.For(request.Target.StackKind),
            StackName = ProvisioningStackName.Canonicalize(request.Target.StackName),
            request.Target.PlatformStackName,
            request.Spec.RuntimeImage,
            Capabilities = request.Spec.RequiredCapabilities.Order(StringComparer.Ordinal),
            request.Spec.DatabaseKind,
            request.Spec.SecretsKind,
            request.Spec.EndpointKind,
            request.Spec.ScaleKind,
            Config = request.StackConfig.OrderBy(pair => pair.Key, StringComparer.Ordinal)
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }
}
