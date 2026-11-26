using Pulumi;

namespace ElsaControl.Provisioning.Providers.Azure.Programs;

/// <summary>
/// The <c>elsa-platform</c> program. One stack per environment.
/// </summary>
public sealed class ElsaPlatformStack : Stack
{
    public const string ProjectName = "elsa-platform";

    [Output] public Output<string> EnvironmentName { get; private set; }

    public ElsaPlatformStack()
    {
        EnvironmentName = Define(ReadInputs());
    }

    public static string ReadInputs()
    {
        var config = new Config();
        return config.Require("environmentName");
    }

    public static Output<string> Define(string environmentName)
    {
        var environment = new SharedEnvironmentResource("environment", new SharedEnvironmentArgs(environmentName));
        return environment.EnvironmentName;
    }
}
