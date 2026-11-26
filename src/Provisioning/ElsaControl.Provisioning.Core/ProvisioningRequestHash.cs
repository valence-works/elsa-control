using System.Security.Cryptography;
using System.Text;
using ElsaControl.Provisioning.Abstractions;

namespace ElsaControl.Provisioning.Core;

internal static class ProvisioningRequestHash
{
    public static string Compute(ProvisioningCommand command)
    {
        var spec = command.Spec;
        var target = command.Target;
        var payload = string.Join('\n',
            command.Kind.ToString(),
            target.ProviderId,
            target.Stack.Project,
            target.Stack.Name,
            target.PlatformStack?.Project ?? "",
            target.PlatformStack?.Name ?? "",
            target.Credential.Kind,
            target.Credential.Locator,
            spec.RuntimeImage,
            spec.Database?.Engine ?? "",
            spec.Database?.SkuName ?? "",
            spec.Database?.HighlyAvailable.ToString() ?? "",
            spec.Endpoint?.PublicIngress.ToString() ?? "",
            spec.Scale?.MinReplicas.ToString() ?? "",
            spec.Scale?.MaxReplicas.ToString() ?? "",
            string.Join(',', spec.Secrets.Select(secret => $"{secret.Name}={secret.Locator}")),
            string.Join(',', spec.StackConfig.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}={pair.Value}")));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }
}
