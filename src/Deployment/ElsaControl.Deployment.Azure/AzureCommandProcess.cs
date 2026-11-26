using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace ElsaControl.Deployment.Azure;

/// <summary>
/// Runs one command with shell-free argument transport, bounded output capture and a hard
/// process lifetime. This is intentionally only a process boundary; command selection,
/// provider policy, credentials and Azure lifecycle semantics belong to higher layers.
/// </summary>
internal sealed class AzureCommandProcess : IAzureCommandProcess
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan DefaultCaptureDrainTimeout = TimeSpan.FromMilliseconds(250);
    public const int DefaultOutputLimit = 64 * 1024;

    private readonly TimeSpan _timeout;
    private readonly TimeSpan _captureDrainTimeout;
    private readonly int _outputCharacterLimit;
    private readonly Func<Process, bool> _terminateProcessTree;

    public AzureCommandProcess(
        TimeSpan? timeout = null,
        int outputCharacterLimit = DefaultOutputLimit,
        Func<Process, bool>? terminateProcessTree = null,
        TimeSpan? captureDrainTimeout = null)
    {
        _timeout = timeout ?? DefaultTimeout;
        if (_timeout <= TimeSpan.Zero || _timeout == Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout), "The command timeout must be positive and finite.");

        if (outputCharacterLimit < 1)
            throw new ArgumentOutOfRangeException(nameof(outputCharacterLimit), "The command output limit must be positive.");

        _captureDrainTimeout = captureDrainTimeout ?? DefaultCaptureDrainTimeout;
        if (_captureDrainTimeout <= TimeSpan.Zero || _captureDrainTimeout == Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(captureDrainTimeout), "The capture drain timeout must be positive and finite.");

        _outputCharacterLimit = outputCharacterLimit;
        _terminateProcessTree = terminateProcessTree ?? KillProcessTree;
    }

    /// <inheritdoc />
    public async Task<AzureCommandProcessResult<T>> ExecuteAsync<T>(
        AzureCommandProcessRequest request,
        AzureCommandOutputProjector<T> outputProjector,
        CancellationToken cancellationToken = default)
        where T : AzureCommandSafeOutput
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(outputProjector);

        if (cancellationToken.IsCancellationRequested)
            return CancelledResult<T>();

        using var process = new Process
        {
            EnableRaisingEvents = true
        };

        try
        {
            process.StartInfo = CreateStartInfo(request);
            if (!process.Start())
                return StartFailedResult<T>();
        }
        catch (Win32Exception exception)
        {
            return exception.NativeErrorCode is 2 or 3
                ? ExecutableNotFoundResult<T>()
                : StartFailedResult<T>();
        }
        catch (Exception)
        {
            return StartFailedResult<T>();
        }

        using var captureCancellation = new CancellationTokenSource();
        var outputLimitReached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var outputBudget = new OutputBudget(_outputCharacterLimit);
        var standardOutputTask = ReadBoundedAsync(
            process.StandardOutput.BaseStream,
            outputBudget,
            outputLimitReached,
            captureCancellation.Token);
        var standardErrorTask = ReadBoundedAsync(
            process.StandardError.BaseStream,
            outputBudget,
            outputLimitReached,
            captureCancellation.Token);
        var exitTask = process.WaitForExitAsync();
        var timeoutTask = Task.Delay(_timeout);
        var cancellationSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellationRegistration = cancellationToken.Register(
            static state => ((TaskCompletionSource<bool>)state!).TrySetResult(true),
            cancellationSignal);
        var cancellationTask = cancellationSignal.Task;

        var completedTask = await Task.WhenAny(
            exitTask,
            timeoutTask,
            cancellationTask,
            outputLimitReached.Task).ConfigureAwait(false);

        var status = completedTask == cancellationTask
            ? AzureCommandProcessStatus.Cancelled
            : completedTask == timeoutTask
                ? AzureCommandProcessStatus.TimedOut
                : completedTask == outputLimitReached.Task
                    ? AzureCommandProcessStatus.OutputLimitExceeded
                    : AzureCommandProcessStatus.Succeeded;

        // A process that exits at the same time as a reader observes its cap still exceeded the
        // cap. Check the signal after the process has naturally exited as well as in WhenAny.
        if (status == AzureCommandProcessStatus.Succeeded && outputLimitReached.Task.IsCompleted)
            status = AzureCommandProcessStatus.OutputLimitExceeded;

        if (status != AzureCommandProcessStatus.Succeeded)
        {
            var terminationRequested = _terminateProcessTree(process);
            var terminated = await WaitForExitAfterTerminationAsync(exitTask).ConfigureAwait(false);
            await ObserveCaptureTasksAsync(
                standardOutputTask,
                standardErrorTask,
                captureCancellation).ConfigureAwait(false);
            if (!terminationRequested || !terminated)
                return TerminationUncertainResult<T>();
            return status switch
            {
                AzureCommandProcessStatus.Cancelled => CancelledResult<T>(),
                AzureCommandProcessStatus.TimedOut => TimedOutResult<T>(),
                AzureCommandProcessStatus.OutputLimitExceeded => OutputLimitExceededResult<T>(),
                _ => ExecutionFailedResult<T>()
            };
        }

        var captures = await ObserveCaptureTasksAsync(
            standardOutputTask,
            standardErrorTask,
            captureCancellation).ConfigureAwait(false);
        if (!captures.Complete && standardOutputTask.Status != TaskStatus.RanToCompletion)
            return InvalidOutputResult<T>();
        if (outputLimitReached.Task.IsCompleted)
            return OutputLimitExceededResult<T>();

        if (process.ExitCode == 0)
        {
            try
            {
                var value = outputProjector(captures.StandardOutput.AsMemory());
                return new AzureCommandProcessResult<T>(
                    AzureCommandProcessStatus.Succeeded,
                    AzureCommandProcessFailureKind.None,
                    process.ExitCode,
                    value,
                    "azure.command.succeeded",
                    "The Azure command completed successfully.");
            }
            catch (Exception)
            {
                return InvalidOutputResult<T>();
            }
        }

        // Exit code is safe typed metadata. Captured output is intentionally discarded because
        // command providers may write credentials, tokens or other sensitive values to stderr.
        return new AzureCommandProcessResult<T>(
            AzureCommandProcessStatus.Failed,
            AzureCommandProcessFailureKind.NonZeroExitCode,
            process.ExitCode,
            default,
            "azure.command.non-zero-exit",
            "The Azure command exited with a non-zero status.");
    }

    /// <summary>Convenience alias for callers that describe a process invocation as a run.</summary>
    public Task<AzureCommandProcessResult<T>> RunAsync<T>(
        AzureCommandProcessRequest request,
        AzureCommandOutputProjector<T> outputProjector,
        CancellationToken cancellationToken = default)
        where T : AzureCommandSafeOutput => ExecuteAsync(request, outputProjector, cancellationToken);

    private static ProcessStartInfo CreateStartInfo(AzureCommandProcessRequest request)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = request.FileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var argument in request.Arguments)
            startInfo.ArgumentList.Add(argument.Value);

        if (request.WorkingDirectory is not null)
            startInfo.WorkingDirectory = request.WorkingDirectory;

        if (request.EnvironmentVariables is not null)
        {
            foreach (var (name, value) in request.EnvironmentVariables)
                startInfo.Environment[name] = value;
        }

        return startInfo;
    }

    /// <summary>
    /// Drain redirected output on I/O completion threads. Bytes are committed before the next
    /// read is armed, and cancellation completes the task immediately so a descendant-held pipe
    /// cannot hang the caller. A busy worker pool therefore cannot drop a short successful
    /// payload by winning the capture-drain timer.
    /// </summary>
    private static Task<BoundedCapture> ReadBoundedAsync(
        Stream stream,
        OutputBudget outputBudget,
        TaskCompletionSource<bool> outputLimitReached,
        CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<BoundedCapture>(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = new StringBuilder(Math.Min(outputBudget.Capacity, 4096));
        var gate = new object();
        var buffer = new byte[4096];
        var chars = new char[4096];
        var decoder = Encoding.UTF8.GetDecoder();
        var completed = 0;

        void Complete()
        {
            if (Interlocked.Exchange(ref completed, 1) != 0)
                return;
            string value;
            lock (gate)
                value = builder.ToString();
            tcs.TrySetResult(new BoundedCapture(value));
        }

        void ReadCallback(IAsyncResult asyncResult)
        {
            try
            {
                int read;
                try
                {
                    read = stream.EndRead(asyncResult);
                }
                catch (Exception) when (Volatile.Read(ref completed) == 1)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    Complete();
                    return;
                }
                catch (IOException)
                {
                    Complete();
                    return;
                }

                if (Volatile.Read(ref completed) == 1)
                    return;
                if (read == 0)
                {
                    Complete();
                    return;
                }

                var decoded = decoder.GetChars(buffer, 0, read, chars, 0, flush: false);
                var accepted = outputBudget.Take(decoded);
                if (accepted > 0)
                {
                    lock (gate)
                        builder.Append(chars, 0, accepted);
                }
                if (accepted < decoded)
                {
                    outputLimitReached.TrySetResult(true);
                    Complete();
                    return;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    Complete();
                    return;
                }

                stream.BeginRead(buffer, 0, buffer.Length, ReadCallback, null);
            }
            catch (Exception)
            {
                Complete();
            }
        }

        try
        {
            cancellationToken.Register(Complete);
            if (cancellationToken.IsCancellationRequested)
            {
                Complete();
                return tcs.Task;
            }

            stream.BeginRead(buffer, 0, buffer.Length, ReadCallback, null);
        }
        catch (Exception)
        {
            Complete();
        }

        return tcs.Task;
    }

    private async Task<BoundedCaptures> ObserveCaptureTasksAsync(
        Task<BoundedCapture> standardOutputTask,
        Task<BoundedCapture> standardErrorTask,
        CancellationTokenSource captureCancellation)
    {
        var capturesTask = Task.WhenAll(standardOutputTask, standardErrorTask);
        try
        {
            if (await Task.WhenAny(capturesTask, Task.Delay(_captureDrainTimeout)).ConfigureAwait(false) != capturesTask)
            {
                // A descendant can inherit redirected handles after the direct child exits.
                // Cancel the reads so those inherited handles cannot hold this invocation open.
                captureCancellation.Cancel();
            }

            if (await Task.WhenAny(capturesTask, Task.Delay(_captureDrainTimeout)).ConfigureAwait(false) != capturesTask)
            {
                _ = capturesTask.ContinueWith(
                    static task => _ = task.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                // Prefer a stream that already finished over discarding both captures.
            }
            else
                await capturesTask.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A stream can close concurrently with process-tree termination. Failed results do
            // not expose captures, and successful process exit has already drained both streams.
        }

        var standardOutput = standardOutputTask.Status == TaskStatus.RanToCompletion
            ? standardOutputTask.Result
            : new BoundedCapture(string.Empty);
        var standardError = standardErrorTask.Status == TaskStatus.RanToCompletion
            ? standardErrorTask.Result
            : new BoundedCapture(string.Empty);
        var complete = standardOutputTask.IsCompleted && standardErrorTask.IsCompleted;
        return new BoundedCaptures(standardOutput.Value, standardError.Value, complete);
    }

    private static async Task<bool> WaitForExitAfterTerminationAsync(Task exitTask)
    {
        try
        {
            if (await Task.WhenAny(exitTask, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false) != exitTask)
                return false;
            await exitTask.ConfigureAwait(false);
            return true;
        }
        catch (Exception)
        {
            // Process termination is best effort. The result remains a safe classification even
            // when the platform reports an already-closed process handle.
            return false;
        }
    }

    private static bool KillProcessTree(Process process)
    {
        try
        {
            if (process.HasExited)
                return false;
            process.Kill(entireProcessTree: true);
            return true;
        }
        catch (Exception) when (process.HasExited)
        {
            // Once the direct process exits the platform handle cannot prove that descendants
            // were terminated. The durable caller must recover this as an uncertain outcome.
            return false;
        }
        catch (Exception)
        {
            // There is no safe diagnostic value in a platform-specific kill exception.
            return false;
        }
    }

    private static AzureCommandProcessResult<T> CancelledResult<T>() where T : AzureCommandSafeOutput => FailureResult<T>(
        AzureCommandProcessStatus.Cancelled,
        AzureCommandProcessFailureKind.Cancelled,
        "azure.command.cancelled",
        "The Azure command was cancelled.");

    private static AzureCommandProcessResult<T> TimedOutResult<T>() where T : AzureCommandSafeOutput => FailureResult<T>(
        AzureCommandProcessStatus.TimedOut,
        AzureCommandProcessFailureKind.TimedOut,
        "azure.command.timed-out",
        "The Azure command exceeded its execution timeout.");

    private static AzureCommandProcessResult<T> OutputLimitExceededResult<T>() where T : AzureCommandSafeOutput => FailureResult<T>(
        AzureCommandProcessStatus.OutputLimitExceeded,
        AzureCommandProcessFailureKind.OutputLimitExceeded,
        "azure.command.output-limit-exceeded",
        "The Azure command exceeded its output limit.");

    private static AzureCommandProcessResult<T> TerminationUncertainResult<T>() where T : AzureCommandSafeOutput => FailureResult<T>(
        AzureCommandProcessStatus.TerminationUncertain,
        AzureCommandProcessFailureKind.TerminationUncertain,
        "azure.command.termination-uncertain",
        "The Azure command termination outcome is uncertain.");

    private static AzureCommandProcessResult<T> ExecutableNotFoundResult<T>() where T : AzureCommandSafeOutput => FailureResult<T>(
        AzureCommandProcessStatus.Failed,
        AzureCommandProcessFailureKind.ExecutableNotFound,
        "azure.command.executable-not-found",
        "The Azure command executable could not be found.");

    private static AzureCommandProcessResult<T> StartFailedResult<T>() where T : AzureCommandSafeOutput => FailureResult<T>(
        AzureCommandProcessStatus.Failed,
        AzureCommandProcessFailureKind.StartFailed,
        "azure.command.start-failed",
        "The Azure command could not be started.");

    private static AzureCommandProcessResult<T> ExecutionFailedResult<T>() where T : AzureCommandSafeOutput => FailureResult<T>(
        AzureCommandProcessStatus.Failed,
        AzureCommandProcessFailureKind.ExecutionFailed,
        "azure.command.execution-failed",
        "The Azure command failed before a result could be observed.");

    private static AzureCommandProcessResult<T> InvalidOutputResult<T>() where T : AzureCommandSafeOutput => FailureResult<T>(
        AzureCommandProcessStatus.Failed,
        AzureCommandProcessFailureKind.InvalidOutput,
        "azure.command.invalid-output",
        "The Azure command returned an invalid result.");

    private static AzureCommandProcessResult<T> FailureResult<T>(
        AzureCommandProcessStatus status,
        AzureCommandProcessFailureKind failureKind,
        string code,
        string message) where T : AzureCommandSafeOutput => new(
        status,
        failureKind,
        null,
        default,
        code,
        message);

    private readonly record struct BoundedCapture(string Value);

    private readonly record struct BoundedCaptures(string StandardOutput, string StandardError, bool Complete);

    private sealed class OutputBudget(int capacity)
    {
        private int _remaining = capacity;

        public int Capacity { get; } = capacity;

        public int Take(int requested)
        {
            while (true)
            {
                var remaining = Volatile.Read(ref _remaining);
                if (remaining == 0)
                    return 0;
                var accepted = Math.Min(remaining, requested);
                if (Interlocked.CompareExchange(ref _remaining, remaining - accepted, remaining) == remaining)
                    return accepted;
            }
        }
    }
}
