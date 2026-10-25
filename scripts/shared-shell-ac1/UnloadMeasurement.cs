using System.Runtime.CompilerServices;

namespace ElsaControl.SharedShellAc1;

internal static class UnloadMeasurement
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static UnloadResult Run(string packagePath)
    {
        var baseline = ProcessMemorySampler.CaptureSettled();
        var (weakContext, weakAssembly, refCountAtUnload, loaded) = LoadRunAndUnload(packagePath);
        ProcessMemorySampler.Settle();
        var after = ProcessMemorySampler.CaptureSettled();

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while ((weakContext.IsAlive || weakAssembly.IsAlive) && DateTime.UtcNow < deadline)
            ProcessMemorySampler.Settle();
        after = ProcessMemorySampler.CaptureSettled();

        return new UnloadResult
        {
            ContextCollected = !weakContext.IsAlive,
            AssemblyCollected = !weakAssembly.IsAlive,
            RefCountAtUnload = refCountAtUnload,
            WorkingSetBaselineMb = baseline.WorkingSetMb,
            WorkingSetLoadedMb = loaded.WorkingSetMb,
            WorkingSetAfterMb = after.WorkingSetMb,
            ManagedHeapBaselineMb = baseline.ManagedHeapMb,
            ManagedHeapLoadedMb = loaded.ManagedHeapMb,
            ManagedHeapAfterMb = after.ManagedHeapMb,
            WaitedForCollectionMs = 5000,
        };
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Context, WeakReference Assembly, int RefCountAtUnload, MemorySnapshot Loaded) LoadRunAndUnload(string packagePath)
    {
        var host = TenantGroupHost.StartAsync(1, packagePath).GetAwaiter().GetResult();
        try
        {
            _ = host.ActivateAllAsync().GetAwaiter().GetResult();
            var loaded = ProcessMemorySampler.CaptureSettled();
            var weakContext = host.Package.WeakSelf;
            var weakAssembly = host.Package.WeakAssembly;
            var refCount = host.Package.RefCount;
            host.DisposeAsync().AsTask().GetAwaiter().GetResult();
            host = null!;
            return (weakContext, weakAssembly, refCount, loaded);
        }
        catch
        {
            host.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
    }
}
