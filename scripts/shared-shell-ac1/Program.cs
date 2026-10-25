using System.Globalization;
using System.Text.Json;
using ElsaControl.SharedShellAc1;

var options = CliOptions.Parse(args);
if (options.ShowHelp)
{
    Console.WriteLine("""
        Elsa Control AC1 Shared-shell density harness (#798)

        Usage:
          ElsaControl.SharedShellAc1 --package <dll> --mode <density|cold-start|unload|all>
            [--groups 1,5,20] [--iterations 10] [--warmup 1] [--output results.json]

        density     One process; activate N tenant-group shells; report settled memory.
        cold-start  Activate N groups and record shell-build + first-workflow wall time.
        unload      Load, run, release the collectible ALC; report WeakReference reclaim.
        all         density + cold-start + unload in this process (density first).
        """);
    return 0;
}

if (string.IsNullOrWhiteSpace(options.PackagePath) || !File.Exists(options.PackagePath))
{
    Console.Error.WriteLine($"Verified package DLL not found: '{options.PackagePath}'.");
    return 2;
}

var report = new RunReport
{
    Issue = "https://github.com/valence-works/elsa-control/issues/798",
    CapturedAtUtc = DateTimeOffset.UtcNow,
    Machine = MachineSpec.Capture(),
    PackagePath = options.PackagePath,
    Iterations = options.Iterations,
    Warmup = options.Warmup,
    Caveats = Caveats.Default,
};

if (options.Mode is "density" or "all")
    report.Density = await RunDensityAsync(options).ConfigureAwait(false);

if (options.Mode is "cold-start" or "all")
    report.ColdStart = await RunColdStartAsync(options).ConfigureAwait(false);

if (options.Mode is "unload" or "all")
    report.Unload = await RunUnloadAsync(options).ConfigureAwait(false);

var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
if (!string.IsNullOrWhiteSpace(options.OutputPath))
{
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.OutputPath))!);
    await File.WriteAllTextAsync(options.OutputPath, json).ConfigureAwait(false);
}

Console.WriteLine(json);
return 0;

static async Task<List<DensityResult>> RunDensityAsync(CliOptions options)
{
    var results = new List<DensityResult>();
    foreach (var groups in options.Groups)
    {
        var workingSets = new List<double>();
        var privates = new List<double>();
        var heaps = new List<double>();
        var gcHeaps = new List<double>();
        MemorySnapshot? last = null;

        for (var iteration = 0; iteration < options.Warmup + options.Iterations; iteration++)
        {
            await using var host = await TenantGroupHost.StartAsync(groups, options.PackagePath).ConfigureAwait(false);
            _ = await host.ActivateAllAsync().ConfigureAwait(false);
            var snapshot = ProcessMemorySampler.CaptureSettled();
            if (iteration >= options.Warmup)
            {
                workingSets.Add(snapshot.WorkingSetMb);
                privates.Add(snapshot.PrivateMb);
                heaps.Add(snapshot.ManagedHeapMb);
                gcHeaps.Add(snapshot.GcHeapMb);
            }

            last = snapshot;
        }

        results.Add(new DensityResult
        {
            Groups = groups,
            WorkingSetMb = MetricSummary.From(workingSets),
            PrivateMb = MetricSummary.From(privates),
            ManagedHeapMb = MetricSummary.From(heaps),
            GcHeapMb = MetricSummary.From(gcHeaps),
            LastAssemblyCount = last?.AssemblyCount ?? 0,
        });
    }

    return results;
}

static async Task<List<ColdStartResult>> RunColdStartAsync(CliOptions options)
{
    var results = new List<ColdStartResult>();
    foreach (var groups in options.Groups)
    {
        var totals = new List<double>();
        var builds = new List<double>();
        var firsts = new List<double>();
        var reloads = new List<double>();
        var firstShellTotals = new List<double>();

        for (var iteration = 0; iteration < options.Warmup + options.Iterations; iteration++)
        {
            await using var host = await TenantGroupHost.StartAsync(groups, options.PackagePath).ConfigureAwait(false);
            var samples = await host.ActivateAllAsync().ConfigureAwait(false);
            var reloadMs = await host.ReloadFirstGroupAsync().ConfigureAwait(false);
            if (iteration < options.Warmup)
                continue;

            totals.Add(samples.Sum(s => s.TotalMs));
            builds.Add(samples.Average(s => s.ShellBuildMs));
            firsts.Add(samples.Average(s => s.FirstWorkflowMs));
            firstShellTotals.Add(samples[0].TotalMs);
            reloads.Add(reloadMs);
        }

        results.Add(new ColdStartResult
        {
            Groups = groups,
            FirstShellTotalMs = MetricSummary.From(firstShellTotals),
            MeanShellBuildMs = MetricSummary.From(builds),
            MeanFirstWorkflowMs = MetricSummary.From(firsts),
            ActivateAllMs = MetricSummary.From(totals),
            ReloadFirstShellMs = MetricSummary.From(reloads),
        });
    }

    return results;
}

static Task<UnloadResult> RunUnloadAsync(CliOptions options) =>
    Task.FromResult(UnloadMeasurement.Run(options.PackagePath));

internal sealed class CliOptions
{
    public string Mode { get; init; } = "all";
    public string PackagePath { get; init; } = "";
    public string OutputPath { get; init; } = "";
    public int[] Groups { get; init; } = [1, 5, 20];
    public int Iterations { get; init; } = 10;
    public int Warmup { get; init; } = 1;
    public bool ShowHelp { get; init; }

    public static CliOptions Parse(string[] args)
    {
        var mode = "all";
        var package = "";
        var output = "";
        var groups = new[] { 1, 5, 20 };
        var iterations = 10;
        var warmup = 1;
        var help = args.Length == 0 || args.Contains("-h") || args.Contains("--help");

        for (var i = 0; i < args.Length; i++)
        {
            var key = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new InvalidOperationException($"Missing value for {key}.");
            switch (key)
            {
                case "--mode": mode = Next(); break;
                case "--package": package = Next(); break;
                case "--output": output = Next(); break;
                case "--groups":
                    groups = Next().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(v => int.Parse(v, CultureInfo.InvariantCulture))
                        .ToArray();
                    break;
                case "--iterations": iterations = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--warmup": warmup = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "-h":
                case "--help":
                    help = true;
                    break;
            }
        }

        return new CliOptions
        {
            Mode = mode,
            PackagePath = package,
            OutputPath = output,
            Groups = groups,
            Iterations = iterations,
            Warmup = warmup,
            ShowHelp = help && string.IsNullOrWhiteSpace(package),
        };
    }
}

internal sealed class RunReport
{
    public required string Issue { get; init; }
    public required DateTimeOffset CapturedAtUtc { get; init; }
    public required MachineSpec Machine { get; init; }
    public required string PackagePath { get; init; }
    public required int Iterations { get; init; }
    public required int Warmup { get; init; }
    public required string[] Caveats { get; init; }
    public List<DensityResult>? Density { get; set; }
    public List<ColdStartResult>? ColdStart { get; set; }
    public UnloadResult? Unload { get; set; }
}

internal sealed class DensityResult
{
    public required int Groups { get; init; }
    public required MetricSummary WorkingSetMb { get; init; }
    public required MetricSummary PrivateMb { get; init; }
    public required MetricSummary ManagedHeapMb { get; init; }
    public required MetricSummary GcHeapMb { get; init; }
    public required int LastAssemblyCount { get; init; }
}

internal sealed class ColdStartResult
{
    public required int Groups { get; init; }
    public required MetricSummary FirstShellTotalMs { get; init; }
    public required MetricSummary MeanShellBuildMs { get; init; }
    public required MetricSummary MeanFirstWorkflowMs { get; init; }
    public required MetricSummary ActivateAllMs { get; init; }
    public required MetricSummary ReloadFirstShellMs { get; init; }
}

internal sealed class UnloadResult
{
    public required bool ContextCollected { get; init; }
    public required bool AssemblyCollected { get; init; }
    public required int RefCountAtUnload { get; init; }
    public required double WorkingSetBaselineMb { get; init; }
    public required double WorkingSetLoadedMb { get; init; }
    public required double WorkingSetAfterMb { get; init; }
    public required double ManagedHeapBaselineMb { get; init; }
    public required double ManagedHeapLoadedMb { get; init; }
    public required double ManagedHeapAfterMb { get; init; }
    public required int WaitedForCollectionMs { get; init; }
}

internal static class Caveats
{
    public static readonly string[] Default =
    [
        "elsa-control does not host Elsa 4 shells. Managed launch is Dedicated Elsa 3.8 Combined only; Shared is refused at plan admission.",
        "This harness uses CShells 0.0.30 (the Elsa 4 shell host) plus a representative verified activity package in a collectible ALC. It does not boot Elsa.Foundation.Host / Workbench, Nuplane, Elsa.Activities.Runtime, workflow persistence, or Studio.",
        "Per-shell cost therefore understates a production Shared shard that also holds the Elsa 4 workflow runtime, EF persistence model, catalog projection, and HTTP pipeline.",
        "Numbers were taken on a Cloud Agent CI-style VM (burstable, noisy neighbour, page cache already warm). Dedicated Shared hardware, container memory limits, and Server GC under load will differ.",
        "Cold-start is measured from GetOrActivateAsync to first ALC-backed activity Execute, not from process start to an HTTP WriteLine workflow via the Elsa API.",
        "The Architect's 4 GiB replica / 1.5 GiB headroom figure is an unmeasured Shared SKU estimate. This repo's implemented Dedicated default is standard-small (0.5 vCPU / 1 GiB).",
    ];
}
