using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace ElsaControl.SharedShellAc1;

internal sealed record MemorySnapshot(
    long WorkingSetBytes,
    long PrivateBytes,
    long ManagedHeapBytes,
    long GcHeapSizeBytes,
    long TotalCommittedBytes,
    int Gen0,
    int Gen1,
    int Gen2,
    long LohBytes,
    int AssemblyCount)
{
    public double WorkingSetMb => WorkingSetBytes / (1024d * 1024d);
    public double PrivateMb => PrivateBytes / (1024d * 1024d);
    public double ManagedHeapMb => ManagedHeapBytes / (1024d * 1024d);
    public double GcHeapMb => GcHeapSizeBytes / (1024d * 1024d);
}

internal static class ProcessMemorySampler
{
    public static MemorySnapshot CaptureSettled()
    {
        Settle();
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var gcInfo = GC.GetGCMemoryInfo();
        var privateBytes = TryReadVmRssBytes() ?? process.PrivateMemorySize64;
        return new MemorySnapshot(
            WorkingSetBytes: process.WorkingSet64,
            PrivateBytes: privateBytes,
            ManagedHeapBytes: GC.GetTotalMemory(forceFullCollection: false),
            GcHeapSizeBytes: gcInfo.HeapSizeBytes,
            TotalCommittedBytes: gcInfo.TotalCommittedBytes,
            Gen0: GC.CollectionCount(0),
            Gen1: GC.CollectionCount(1),
            Gen2: GC.CollectionCount(2),
            LohBytes: gcInfo.GenerationInfo.Length > 3 ? gcInfo.GenerationInfo[3].SizeAfterBytes : 0,
            AssemblyCount: AppDomain.CurrentDomain.GetAssemblies().Length);
    }

    public static void Settle()
    {
        for (var i = 0; i < 3; i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }

        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    private static long? TryReadVmRssBytes()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return null;

        try
        {
            foreach (var line in File.ReadLines("/proc/self/status"))
            {
                if (!line.StartsWith("VmRSS:", StringComparison.Ordinal))
                    continue;
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var kb))
                    return kb * 1024;
            }
        }
        catch (IOException)
        {
        }

        return null;
    }
}
