using System.Globalization;
using System.Runtime;
using System.Runtime.InteropServices;

namespace ElsaControl.SharedShellAc1;

internal sealed record MachineSpec(
    string Cpu,
    int Processors,
    long MemoryTotalBytes,
    string Os,
    string Kernel,
    string DotnetRuntime,
    string DotnetSdk,
    bool ServerGc,
    string GcMode)
{
    public static MachineSpec Capture()
    {
        return new MachineSpec(
            Cpu: ReadCpuModel(),
            Processors: Environment.ProcessorCount,
            MemoryTotalBytes: ReadMemTotalBytes(),
            Os: RuntimeInformation.OSDescription,
            Kernel: ReadKernel(),
            DotnetRuntime: RuntimeInformation.FrameworkDescription,
            DotnetSdk: Environment.Version.ToString(),
            ServerGc: GCSettings.IsServerGC,
            GcMode: GCSettings.LatencyMode.ToString());
    }

    private static string ReadCpuModel()
    {
        try
        {
            foreach (var line in File.ReadLines("/proc/cpuinfo"))
            {
                if (line.StartsWith("model name", StringComparison.Ordinal))
                    return line.Split(':', 2)[1].Trim();
            }
        }
        catch (IOException)
        {
        }

        return RuntimeInformation.ProcessArchitecture.ToString();
    }

    private static long ReadMemTotalBytes()
    {
        try
        {
            foreach (var line in File.ReadLines("/proc/meminfo"))
            {
                if (!line.StartsWith("MemTotal:", StringComparison.Ordinal))
                    continue;
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var kb))
                    return kb * 1024;
            }
        }
        catch (IOException)
        {
        }

        return 0;
    }

    private static string ReadKernel()
    {
        try
        {
            return File.ReadAllText("/proc/version").Trim();
        }
        catch (IOException)
        {
            return Environment.OSVersion.VersionString;
        }
    }
}
