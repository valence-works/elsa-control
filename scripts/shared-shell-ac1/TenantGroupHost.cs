using System.Diagnostics;
using CShells.DependencyInjection;
using CShells.Lifecycle;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ElsaControl.SharedShellAc1;

internal sealed class TenantGroupHost : IAsyncDisposable
{
    private readonly IHost _host;
    private readonly IShellRegistry _registry;
    private PackageRevisionLoadContext? _package;
    private readonly string _dataDirectory;
    private readonly int _groupCount;

    private TenantGroupHost(IHost host, IShellRegistry registry, PackageRevisionLoadContext package, string dataDirectory, int groupCount)
    {
        _host = host;
        _registry = registry;
        _package = package;
        _dataDirectory = dataDirectory;
        _groupCount = groupCount;
    }

    public PackageRevisionLoadContext Package => _package ?? throw new ObjectDisposedException(nameof(TenantGroupHost));
    public IShellRegistry Registry => _registry;

    public static async Task<TenantGroupHost> StartAsync(int groupCount, string packagePath, string? dataDirectory = null, CancellationToken cancellationToken = default)
    {
        dataDirectory ??= Path.Combine(Path.GetTempPath(), "elsa-control-ac1", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dataDirectory);

        var package = PackageRevisionLoader.Load(packagePath, "ac1-verified-echo-v1");
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddSingleton(package);
        builder.Services.AddSingleton(new SharedShellAc1Options
        {
            DataDirectory = dataDirectory,
            PackagePath = packagePath,
        });
        builder.Services.AddCShells(cshells =>
        {
            cshells.ShareSingletonWithShells<PackageRevisionLoadContext>();
            cshells.ShareSingletonWithShells<SharedShellAc1Options>();
            cshells.WithAssemblies(typeof(TenantGroupRuntimeFeature).Assembly);
            for (var i = 0; i < groupCount; i++)
            {
                var name = GroupName(i);
                cshells.AddShell(name, shell => shell.WithFeatures("TenantGroupRuntime"));
            }
        });

        var host = builder.Build();
        await host.StartAsync(cancellationToken).ConfigureAwait(false);
        var registry = host.Services.GetRequiredService<IShellRegistry>();
        return new TenantGroupHost(host, registry, package, dataDirectory, groupCount);
    }

    public static string GroupName(int index) => $"tenant-group-{index + 1:000}";

    public async Task<ColdStartSample> ActivateGroupAsync(int index, CancellationToken cancellationToken = default)
    {
        var name = GroupName(index);
        var total = Stopwatch.StartNew();
        var shellBuild = Stopwatch.StartNew();
        var shell = await _registry.GetOrActivateAsync(name, cancellationToken).ConfigureAwait(false);
        var shellBuildMs = shellBuild.Elapsed.TotalMilliseconds;

        var firstRun = Stopwatch.StartNew();
        await using var scope = shell.BeginScope();
        var catalog = scope.ServiceProvider.GetRequiredService<IVerifiedActivityCatalog>();
        var output = catalog.ExecuteFirstWorkflow(name);
        var firstRunMs = firstRun.Elapsed.TotalMilliseconds;
        total.Stop();

        if (!output.EndsWith(":ready", StringComparison.Ordinal))
            throw new InvalidOperationException($"First workflow for {name} did not become ready: '{output}'.");

        return new ColdStartSample(name, shellBuildMs, firstRunMs, total.Elapsed.TotalMilliseconds, output);
    }

    public async Task<IReadOnlyList<ColdStartSample>> ActivateAllAsync(CancellationToken cancellationToken = default)
    {
        var samples = new List<ColdStartSample>(_groupCount);
        for (var i = 0; i < _groupCount; i++)
            samples.Add(await ActivateGroupAsync(i, cancellationToken).ConfigureAwait(false));
        return samples;
    }

    public async Task<double> ReloadFirstGroupAsync(CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var result = await _registry.ReloadAsync(GroupName(0), cancellationToken).ConfigureAwait(false);
        if (result.Error is not null)
            throw new InvalidOperationException($"Reload of {GroupName(0)} failed.", result.Error);
        if (result.Drain is not null)
            await result.Drain.WaitAsync(cancellationToken).ConfigureAwait(false);
        var reloaded = result.NewShell ?? throw new InvalidOperationException($"Reload of {GroupName(0)} returned no shell.");
        await using var scope = reloaded.BeginScope();
        var catalog = scope.ServiceProvider.GetRequiredService<IVerifiedActivityCatalog>();
        _ = catalog.ExecuteFirstWorkflow(GroupName(0));
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _host.StopAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        }
        finally
        {
            _host.Dispose();
            if (_package is not null)
            {
                while (_package.RefCount > 0)
                    _package.Release();
                _package = null;
            }

            try
            {
                if (Directory.Exists(_dataDirectory))
                    Directory.Delete(_dataDirectory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}

internal sealed record ColdStartSample(
    string ShellName,
    double ShellBuildMs,
    double FirstWorkflowMs,
    double TotalMs,
    string Output);
