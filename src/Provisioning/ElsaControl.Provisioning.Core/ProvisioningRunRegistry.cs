using System.Collections.Concurrent;
using ElsaControl.Provisioning.Abstractions;

namespace ElsaControl.Provisioning.Core;

/// <summary>
/// In-process registry of in-flight provider runs. Cancel looks here; it never
/// Destroy. Keys are <c>project/stack</c>.
/// </summary>
public sealed class ProvisioningRunRegistry
{
    private readonly ConcurrentDictionary<string, InFlightRun> _runs = new(StringComparer.Ordinal);

    public IDisposable Register(ProvisioningOperation operation, CancellationTokenSource cancellation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(cancellation);
        var key = ProvisioningStackLockKey.Format(operation.StackKind, operation.StackName);
        var run = new InFlightRun(operation.Id, cancellation);
        if (!_runs.TryAdd(key, run))
            throw new InvalidOperationException("A run is already registered for this stack.");
        return new Registration(this, key, operation.Id);
    }

    public bool TryCancel(ProvisioningStackKind kind, string stackName)
    {
        var key = ProvisioningStackLockKey.Format(kind, stackName);
        if (!_runs.TryGetValue(key, out var run))
            return false;
        try
        {
            run.Cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            return false;
        }

        return true;
    }

    public bool IsRunning(ProvisioningStackKind kind, string stackName) =>
        _runs.ContainsKey(ProvisioningStackLockKey.Format(kind, stackName));

    private void Complete(string key, Guid operationId)
    {
        if (_runs.TryGetValue(key, out var run) && run.OperationId == operationId)
            _runs.TryRemove(key, out _);
    }

    private sealed record InFlightRun(Guid OperationId, CancellationTokenSource Cancellation);

    private sealed class Registration(ProvisioningRunRegistry registry, string key, Guid operationId) : IDisposable
    {
        public void Dispose() => registry.Complete(key, operationId);
    }
}
