using System.Diagnostics;
using System.Runtime.CompilerServices;
using ElsaControl.Deployment.Azure;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

/// <summary>
/// Owns the one fake Azure CLI used by delete-recovery seeds. The executable is
/// materialized once at assembly load, before any test in this host can spawn, and
/// this process never holds a writable descriptor on that inode.
/// </summary>
public sealed class DeleteRecoveryFakeAzureCliFixture : IDisposable
{
    public DeleteRecoveryFakeAzureCliFixture() => _ = DeleteRecoveryFakeAzureCli.ExecutablePath;

    public void Dispose() => DeleteRecoveryFakeAzureCli.TryDelete();
}

internal static class DeleteRecoveryFakeAzureCli
{
    internal static string ExecutablePath { get; } = OperatingSystem.IsWindows()
        ? Path.Combine(Path.GetTempPath(), "azure-command-unused")
        : Materialize();

    [ModuleInitializer]
    internal static void EnsureMaterialized() => _ = ExecutablePath;

    internal static void TryDelete()
    {
        if (OperatingSystem.IsWindows())
            return;
        var root = Path.GetDirectoryName(ExecutablePath);
        if (root is not null && Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private static string Materialize()
    {
        var root = Path.Combine(Path.GetTempPath(), $"elsa-delete-recovery-az-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "azure-command");
        WriteClosedExecutable(path, Script);
        return path;
    }

    private static void WriteClosedExecutable(string path, string script)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "/bin/sh",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("umask 077; cat > \"$1\" && chmod 755 \"$1\"");
        startInfo.ArgumentList.Add("write-closed-executable");
        startInfo.ArgumentList.Add(path);

        using var writer = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The closed executable writer did not start.");
        writer.StandardInput.Write(script);
        writer.StandardInput.Close();
        if (!writer.WaitForExit(30_000))
        {
            try
            {
                writer.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // Best-effort stop of a stuck writer.
            }

            throw new TimeoutException("The closed executable writer did not exit.");
        }

        if (writer.ExitCode != 0)
            throw new InvalidOperationException($"The closed executable writer exited {writer.ExitCode}.");
        if (!File.Exists(path))
            throw new InvalidOperationException("The closed executable writer did not create the file.");
    }

    private const string Script = """
#!/bin/sh
set -eu
delete_requested="$PWD/delete-requested"
absence_observed="$PWD/absence-observed"
log="$PWD/commands.log"
subscription=$(cat "$PWD/expected-subscription")
resource_group=$(cat "$PWD/expected-resource-group")
workload=$(cat "$PWD/workload-name")

printf '%s\n' "$*" >> "$log"
if [ "$#" -eq 9 ] && [ "$*" = "group exists --subscription $subscription --name $resource_group --output tsv --only-show-errors" ]; then
    if [ -f "$delete_requested" ] && [ ! -f "$absence_observed" ]; then
        : > "$absence_observed"
        printf '%s' 'true'
    elif [ -f "$delete_requested" ]; then
        printf '%s' 'false'
    else
        printf '%s' 'true'
    fi
    exit 0
fi
if [ "$#" -eq 11 ] && [ "$*" = "group show --subscription $subscription --name $resource_group --query tags --output json --only-show-errors" ]; then
    printf '%s' "{\"managed-by\":\"elsa-control\",\"owner\":\"elsa-control\",\"workload-name\":\"$workload\",\"sqlBootstrapObjectId\":\"11111111-1111-1111-1111-111111111111\"}"
    exit 0
fi
if [ "$#" -eq 9 ] && [ "$*" = "resource list --subscription $subscription --resource-group $resource_group --output json --only-show-errors" ]; then
    printf '%s' '[]'
    exit 0
fi
if [ "$1" = "monitor" ] && [ "$2" = "log-analytics" ] && [ "$3" = "workspace" ] && [ "$4" = "show" ]; then
    exit 3
fi
if [ "$#" -eq 11 ] && [ "$*" = "group delete --subscription $subscription --name $resource_group --yes --no-wait --output none --only-show-errors" ]; then
    : > "$delete_requested"
    exit 0
fi
if [ "$#" -eq 9 ] && [ "$*" = "keyvault list-deleted --subscription $subscription --resource-type vault --output json --only-show-errors" ]; then
    printf '%s' '[]'
    exit 0
fi
if [ "$#" -eq 8 ] && [ "$1" = "rest" ] && [ "$2" = "--method" ] && [ "$3" = "get" ] && [ "$4" = "--url" ] &&
    printf '%s' "$5" | grep -q 'deletedWorkspaces' && [ "$6" = "--output" ] && [ "$7" = "json" ] && [ "$8" = "--only-show-errors" ]; then
    printf '%s' '{"value":[]}'
    exit 0
fi
exit 97
""";
}

/// <summary>
/// Test-only start retry. ETXTBSY is classified as <see cref="AzureCommandProcessFailureKind.StartFailed"/>.
/// Production <see cref="AzureCommandProcess"/> keeps a single attempt.
/// </summary>
internal sealed class EtxtbsyRetryAzureCommandProcess : IAzureCommandProcess
{
    private const int MaxAttempts = 8;
    private static readonly TimeSpan InitialBackoff = TimeSpan.FromMilliseconds(5);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMilliseconds(80);

    private readonly AzureCommandProcess _inner;

    public EtxtbsyRetryAzureCommandProcess(TimeSpan? timeout, int outputCharacterLimit) =>
        _inner = new AzureCommandProcess(timeout, outputCharacterLimit);

    public async Task<AzureCommandProcessResult<T>> ExecuteAsync<T>(
        AzureCommandProcessRequest request,
        AzureCommandOutputProjector<T> outputProjector,
        CancellationToken cancellationToken = default)
        where T : AzureCommandSafeOutput
    {
        var delay = InitialBackoff;
        for (var attempt = 1; ; attempt++)
        {
            var result = await _inner.ExecuteAsync(request, outputProjector, cancellationToken);
            if (result.FailureKind != AzureCommandProcessFailureKind.StartFailed ||
                attempt >= MaxAttempts)
                return result;

            try
            {
                await Task.Delay(delay, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return result;
            }

            var next = delay.TotalMilliseconds * 2;
            delay = TimeSpan.FromMilliseconds(Math.Min(next, MaxBackoff.TotalMilliseconds));
        }
    }
}
