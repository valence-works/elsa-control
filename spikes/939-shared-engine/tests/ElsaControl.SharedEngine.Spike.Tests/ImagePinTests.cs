using Xunit;

namespace ElsaControl.SharedEngine.Spike.Tests;

public sealed class ImagePinTests
{
    [Fact]
    public void Pin_is_elsa_core_main_at_or_after_59b14969()
    {
        Assert.Equal("59b14969027b5cff1273f54970d90da189f4ff0f", SpikeConstants.ElsaCorePin);
        var headFile = Path.Combine(FindElsaCoreRoot(), ".git", "HEAD");
        Assert.True(File.Exists(headFile) || Directory.Exists(Path.Combine(FindElsaCoreRoot(), "core", "src", "modules", "Elsa")));
        var tenantsFeature = File.ReadAllText(Path.Combine(
            FindElsaCoreRoot(),
            "core/src/modules/Elsa.Tenants/ShellFeatures/TenantsFeature.cs"));
        Assert.Contains("options.IsEnabled = true", tenantsFeature);
        Assert.Contains("RejectUnresolvedTenants", tenantsFeature);
    }

    [Fact]
    public void Public_image_analogue_enables_diagnostics_and_does_not_reference_proto_actor()
    {
        var analogue = Path.Combine(FindElsaCoreRoot(), "core/src/apps/Elsa.ModularServer.Web/Elsa.ModularServer.Web.csproj");
        var project = File.ReadAllText(analogue);
        Assert.Contains("Elsa.Diagnostics.", project);
        Assert.DoesNotContain("Elsa.Actors.ProtoActor", project);
        Assert.DoesNotContain("Proto.Actor", project);

        var program = File.ReadAllText(Path.Combine(FindElsaCoreRoot(), "core/src/apps/Elsa.ModularServer.Web/Program.cs"));
        Assert.Contains("ConsoleLogsDashboardFeature", program);
        Assert.Contains("StructuredLogsDashboardFeature", program);
    }

    [Fact]
    public void Spike_host_does_not_enable_diagnostics_or_proto_actor()
    {
        var hostProject = Path.Combine(FindSpikeRoot(), "src/ElsaControl.SharedEngine.Spike/ElsaControl.SharedEngine.Spike.csproj");
        var project = File.ReadAllText(hostProject);
        Assert.DoesNotContain("Elsa.Diagnostics.", project);
        Assert.DoesNotContain("Elsa.Actors.ProtoActor", project);
        Assert.DoesNotContain("Proto.Actor", project);

        var shells = File.ReadAllText(Path.Combine(FindSpikeRoot(), "src/ElsaControl.SharedEngine.Spike/SpikeShells.cs"));
        Assert.Contains("Diagnostics modules stay disabled on shared engines", shells);
        Assert.DoesNotContain("ConsoleLogs", shells);
        Assert.DoesNotContain("OpenTelemetry", shells);
        Assert.Contains("FileDistributedSynchronizationProvider", shells);
    }

    [Fact]
    public void Default_runtime_lock_provider_is_file_based()
    {
        var runtimeFeature = File.ReadAllText(Path.Combine(
            FindElsaCoreRoot(),
            "core/src/modules/Elsa.Workflows.Runtime/ShellFeatures/WorkflowRuntimeFeature.cs"));
        Assert.Contains("FileDistributedSynchronizationProvider", runtimeFeature);
        Assert.Contains("App_Data", runtimeFeature);
        Assert.Contains("locks", runtimeFeature);
    }

    private static string FindElsaCoreRoot()
    {
        var env = Environment.GetEnvironmentVariable("ElsaCoreRoot");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env))
            return env;
        return Path.Combine(FindSpikeRoot(), ".deps", "elsa-core");
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
