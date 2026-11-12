using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using CShells.Lifecycle;
using Elsa.Persistence.EFCore;
using Elsa.Persistence.EFCore.Modules.Management;
using Elsa.Workflows.Management.Entities;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ElsaControl.SharedEngine.Spike.Tests;

public sealed class MeasurementsTests
{
    [Fact]
    public async Task Records_ac3_numbers()
    {
        var evidencePath = Path.Combine(FindSpikeRoot(), "evidence", "ac3-measurements.json");
        Directory.CreateDirectory(Path.GetDirectoryName(evidencePath)!);

        using var shard = SpikeWebApplicationFactory.CreateShard();
        var activation = new Dictionary<string, double>();
        foreach (var name in new[] { SpikeConstants.DefaultShell, SpikeConstants.TenantA, SpikeConstants.TenantB })
        {
            var started = Stopwatch.StartNew();
            await shard.ActivateNamedShellsAsync(name);
            activation[name] = started.Elapsed.TotalMilliseconds;
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var idleWorkingSetBytes = Process.GetCurrentProcess().WorkingSet64;

        var shellA = await TenantClient.ActivateAsync(shard, SpikeConstants.TenantA);
        var shellB = await TenantClient.ActivateAsync(shard, SpikeConstants.TenantB);
        var semaphoreIsStatic = ProveStoreSemaphoreIsStatic();

        var sequential = await TimeSequentialWritesAsync(shellA, 20);
        var concurrent = await TimeConcurrentWritesAsync(shellA, shellB, 10);
        var sameId = await TimeSameDefinitionIdPublishAsync(shellA, shellB);

        using var lookup = SpikeWebApplicationFactory.CreateLookup();
        await lookup.ActivateNamedShellsAsync(SpikeConstants.DefaultShell);
        for (var i = 0; i < 64; i++)
            await lookup.ActivateNamedShellsAsync($"probe-{i}");

        using var probeClient = TenantClient.Create(lookup, "probe-32.local");
        var warmup = await probeClient.GetAsync("/spike/whoami");
        warmup.EnsureSuccessStatusCode();

        var samples = new List<double>(10_000);
        var lookupWatch = Stopwatch.StartNew();
        for (var i = 0; i < 10_000; i++)
        {
            var requestWatch = Stopwatch.StartNew();
            using var response = await probeClient.GetAsync("/spike/whoami");
            response.EnsureSuccessStatusCode();
            samples.Add(requestWatch.Elapsed.TotalMilliseconds);
        }

        samples.Sort();
        var measurements = new
        {
            pin = SpikeConstants.ElsaCorePin,
            cshells = SpikeConstants.CShellsVersion,
            persistence = string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPIKE_SQL_CONNECTION")) ? "sqlite" : "sqlserver",
            idleWorkingSetBytes,
            activationMilliseconds = activation,
            storeSemaphoreIsStatic = semaphoreIsStatic,
            sequentialTwentyWritesMs = sequential,
            concurrentTenWritesPerTenantMs = concurrent,
            sameDefinitionIdCrossTenantPublishMs = sameId,
            hostLookup = new
            {
                probeShells = 64,
                samples = samples.Count,
                p50Ms = Percentile(samples, 0.50),
                p95Ms = Percentile(samples, 0.95),
                p99Ms = Percentile(samples, 0.99),
                totalMs = lookupWatch.Elapsed.TotalMilliseconds
            }
        };

        await File.WriteAllTextAsync(evidencePath, JsonSerializer.Serialize(measurements, new JsonSerializerOptions { WriteIndented = true }));
        Assert.True(idleWorkingSetBytes > 0);
        Assert.True(activation[SpikeConstants.TenantA] > 0);
        Assert.True(semaphoreIsStatic);
        Assert.True(Percentile(samples, 0.50) >= 0);
    }

    private static bool ProveStoreSemaphoreIsStatic()
    {
        var storeType = typeof(Store<ManagementElsaDbContext, WorkflowDefinition>);
        var field = storeType.GetField("Semaphore", BindingFlags.NonPublic | BindingFlags.Static);
        return field is not null && field.IsStatic;
    }

    private static async Task<double> TimeSequentialWritesAsync(IShell shell, int count)
    {
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < count; i++)
            await TenantClient.PublishNamedAsync(shell, $"seq-{i}", $"seq-{i}");
        return watch.Elapsed.TotalMilliseconds;
    }

    private static async Task<double> TimeConcurrentWritesAsync(IShell shellA, IShell shellB, int countEach)
    {
        var watch = Stopwatch.StartNew();
        var writesA = Enumerable.Range(0, countEach).Select(i => TenantClient.PublishNamedAsync(shellA, $"burst-a-{i}", $"burst-a-{i}"));
        var writesB = Enumerable.Range(0, countEach).Select(i => TenantClient.PublishNamedAsync(shellB, $"burst-b-{i}", $"burst-b-{i}"));
        await Task.WhenAll(writesA.Concat(writesB));
        return watch.Elapsed.TotalMilliseconds;
    }

    private static async Task<double> TimeSameDefinitionIdPublishAsync(IShell shellA, IShell shellB)
    {
        var watch = Stopwatch.StartNew();
        await Task.WhenAll(
            TenantClient.PublishNamedAsync(shellA, "g19-shared", "g19-a"),
            TenantClient.PublishNamedAsync(shellB, "g19-shared", "g19-b"));
        return watch.Elapsed.TotalMilliseconds;
    }

    private static double Percentile(IReadOnlyList<double> sorted, double percentile)
    {
        if (sorted.Count == 0)
            return 0;
        var index = (int)Math.Clamp(Math.Ceiling(percentile * sorted.Count) - 1, 0, sorted.Count - 1);
        return sorted[index];
    }

    private static string FindSpikeRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "restore-elsa-core.sh")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Spike root not found.");
    }
}
