using System.Text.RegularExpressions;

namespace ElsaControl.Deployment.Azure.Tests;

/// <summary>
/// The provider's exact Container Apps mapping and the checked-in production template must agree:
/// every size the runner passes is accepted by main.bicep and selectable by the module, and the
/// template accepts nothing the runner would refuse.
/// </summary>
public sealed class AzureContainerAppsCapacityTests
{
    private static readonly string TemplateRoot = Path.Combine(FindRepositoryRoot(), "infra", "azure-production");
    private static readonly string Main = File.ReadAllText(Path.Combine(TemplateRoot, "main.bicep"));
    private static readonly string Module = File.ReadAllText(Path.Combine(TemplateRoot, "modules", "container-app.bicep"));

    [Fact]
    public void Template_accepts_exactly_the_mapped_consumption_sizes()
    {
        var sizes = AzureContainerAppsCapacity.ConsumptionSizes;
        var lookup = Regex.Matches(Module, @"'(?<key>[^']+)': \{\s*cpu: json\('(?<cpu>[^']+)'\)\s*memory: '(?<memory>[^']+)'\s*\}")
            .Select(match => (Key: match.Groups["key"].Value, Cpu: match.Groups["cpu"].Value, Memory: match.Groups["memory"].Value))
            .ToArray();

        Assert.Equal(sizes.Select(size => size.Cpu), AllowedValues("workloadCpu"));
        Assert.Equal(sizes.Select(size => size.Memory), AllowedValues("workloadMemory"));
        Assert.Equal(sizes.Select(size => (Key: $"{size.Cpu}/{size.Memory}", size.Cpu, size.Memory)), lookup);
    }

    [Theory]
    [InlineData("workloadMinReplicas", 0)]
    [InlineData("workloadMaxReplicas", 1)]
    public void Template_replica_bounds_match_the_provider_mapping(string parameter, int minimum)
    {
        var bounds = Regex.Match(Main, $@"@minValue\((?<min>\d+)\)\s*@maxValue\((?<max>\d+)\)\s*param {parameter} int\r?\n");

        Assert.True(bounds.Success, parameter);
        Assert.Equal(minimum, int.Parse(bounds.Groups["min"].Value));
        Assert.Equal(AzureContainerAppsCapacity.MaximumReplicas, int.Parse(bounds.Groups["max"].Value));
    }

    [Fact]
    public void Every_consumption_size_maps_to_itself_without_rounding()
    {
        Assert.All(AzureContainerAppsCapacity.ConsumptionSizes, size =>
            Assert.Same(size, AzureContainerAppsCapacity.Map(new(1, 1, size.CpuMillicores, size.MemoryMiB))));
        Assert.Equal(1, AzureContainerAppsCapacity.ConsumptionSizes.Select(size => size.MemoryMiB * 1000 / size.CpuMillicores).Distinct().Count());
    }

    [Theory]
    [InlineData(0, 1, 500, 1024, true)]
    [InlineData(1, 300, 500, 1024, true)]
    [InlineData(-1, 1, 500, 1024, false)]
    [InlineData(0, 0, 500, 1024, false)]
    [InlineData(1, 301, 500, 1024, false)]
    [InlineData(2, 1, 500, 1024, false)]
    [InlineData(1, 1, 500, 2048, false)]
    [InlineData(1, 1, 499, 1024, false)]
    [InlineData(1, 1, 2250, 4608, false)]
    public void Maps_only_exact_sizes_within_replica_bounds(int minReplicas, int maxReplicas, int cpuMillicores, int memoryMiB, bool mapped)
    {
        Assert.Equal(mapped, AzureContainerAppsCapacity.Map(new(minReplicas, maxReplicas, cpuMillicores, memoryMiB)) is not null);
    }

    private static string[] AllowedValues(string parameter)
    {
        var allowed = Regex.Match(Main, $@"@allowed\(\[(?<values>[^\]]*)\]\)\s*param {parameter} string\r?\n");
        Assert.True(allowed.Success, parameter);
        return allowed.Groups["values"].Value
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => value.Trim('\''))
            .ToArray();
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ElsaControl.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("The repository root could not be found.");
    }
}
