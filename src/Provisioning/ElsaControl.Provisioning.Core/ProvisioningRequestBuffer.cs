using System.Collections.Concurrent;
using ElsaControl.Provisioning.Abstractions;

namespace ElsaControl.Provisioning.Core;

/// <summary>
/// Process-local pending-request map for the skeleton. Durable command
/// payloads stay out of the operation table; a later persistence issue can
/// replace this with a module-owned request table.
/// </summary>
internal static class ProvisioningRequestBuffer
{
    private static readonly ConcurrentDictionary<Guid, ProvisioningRequest> Pending = new();

    public static void Put(Guid operationId, ProvisioningRequest request) =>
        Pending[operationId] = request;

    public static bool TryTake(Guid operationId, out ProvisioningRequest request) =>
        Pending.TryRemove(operationId, out request!);
}
