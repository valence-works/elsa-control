using ElsaControl.Deployment.Azure;

namespace ElsaControl.Deployment.Azure.Tests;

public sealed class AzureContainerAppReplicaCrashLoopTests
{
    [Fact]
    public void Repeated_restarts_are_a_crash_loop()
    {
        Assert.True(AzureContainerAppReplicaCrashLoop.Detected(
            """[{"runningState":"Running","restartCounts":[2]}]""".AsMemory()));
    }

    [Fact]
    public void Exit_code_139_is_a_crash_loop()
    {
        Assert.True(AzureContainerAppReplicaCrashLoop.Detected(
            """[{"runningState":"Running","runningStateDetails":"Container terminated with exit code 139"}]""".AsMemory()));
    }

    [Fact]
    public void CrashLoopBackOff_details_are_a_crash_loop()
    {
        Assert.True(AzureContainerAppReplicaCrashLoop.Detected(
            """[{"runningState":"Running","runningStateDetails":"Back-off restarting failed container: CrashLoopBackOff"}]""".AsMemory()));
    }

    [Fact]
    public void Nested_container_restart_count_is_a_crash_loop()
    {
        Assert.True(AzureContainerAppReplicaCrashLoop.Detected(
            """[{"properties":{"runningState":"Running","containers":[{"restartCount":3}]}}]""".AsMemory()));
    }

    [Fact]
    public void A_single_restart_is_not_a_crash_loop()
    {
        Assert.False(AzureContainerAppReplicaCrashLoop.Detected(
            """[{"runningState":"Running","restartCounts":[1]}]""".AsMemory()));
    }

    [Fact]
    public void A_terminated_replica_without_restarts_or_a_non_zero_exit_is_not_a_crash_loop()
    {
        Assert.False(AzureContainerAppReplicaCrashLoop.Detected(
            """[{"runningState":"Terminated","restartCounts":[0]}]""".AsMemory()));
    }

    [Fact]
    public void An_empty_or_unparseable_observation_is_not_a_crash_loop()
    {
        Assert.False(AzureContainerAppReplicaCrashLoop.Detected("[]".AsMemory()));
        Assert.False(AzureContainerAppReplicaCrashLoop.Detected("not-json".AsMemory()));
        Assert.False(AzureContainerAppReplicaCrashLoop.Detected(ReadOnlyMemory<char>.Empty));
    }

    [Fact]
    public void Detection_does_not_echo_provider_details()
    {
        const string json =
            """[{"name":"/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-customer/providers/Microsoft.App/containerApps/app","runningState":"Failed","runningStateDetails":"Container terminated with exit code 139"}]""";
        Assert.True(AzureContainerAppReplicaCrashLoop.Detected(json.AsMemory()));
        Assert.True(AzureContainerAppReplicaCrashLoop.Detected(json.AsMemory()));
    }
}
