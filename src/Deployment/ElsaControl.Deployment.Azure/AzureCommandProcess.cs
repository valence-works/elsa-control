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
    public static readonly TimeSpan DefaultStdoutEofTimeout = TimeSpan.FromSeconds(3);
    public const int DefaultOutputLimit = 64 * 1024;

    private readonly TimeSpan _timeout;
    private readonly TimeSpan _captureDrainTimeout;
    private readonly TimeSpan _stdoutEofTimeout;
    private readonly int _outputCharacterLimit;
    private readonly Func<Process, bool> _terminateProcessTree;
    private readonly Func<Stream, Stream>? _wrapStandardOutput; // Test seam for delayed or chunked stdout.
    private readonly Func<Stream, Stream>? _wrapStandardError;

    public AzureCommandProcess(
        TimeSpan? timeout = null,
        int outputCharacterLimit = DefaultOutputLimit,
        Func<Process, bool>? terminateProcessTree = null,
        TimeSpan? captureDrainTimeout = null,
        TimeSpan? stdoutEofTimeout = null,
        Func<Stream, Stream>? wrapStandardOutput = null,
        Func<Stream, Stream>? wrapStandardError = null)
    {
        _timeout = timeout ?? DefaultTimeout;
        if (_timeout <= TimeSpan.Zero || _timeout == Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout), "The command timeout must be positive and finite.");

        if (outputCharacterLimit < 1)
            throw new ArgumentOutOfRangeException(nameof(outputCharacterLimit), "The command output limit must be positive.");

        _captureDrainTimeout = captureDrainTimeout ?? DefaultCaptureDrainTimeout;
        if (_captureDrainTimeout <= TimeSpan.Zero || _captureDrainTimeout == Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(captureDrainTimeout), "The capture drain timeout must be positive and finite.");

        _stdoutEofTimeout = stdoutEofTimeout ?? DefaultStdoutEofTimeout;
        if (_stdoutEofTimeout <= TimeSpan.Zero || _stdoutEofTimeout == Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(stdoutEofTimeout), "The stdout EOF timeout must be positive and finite.");

        _outputCharacterLimit = outputCharacterLimit;
        _terminateProcessTree = terminateProcessTree ?? KillProcessTree;
        _wrapStandardOutput = wrapStandardOutput;
        _wrapStandardError = wrapStandardError;
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

        var lifetime = Stopwatch.StartNew();
        using var abortCapture = new CancellationTokenSource();
        using var stderrDrain = new CancellationTokenSource();
        using var stdoutCapture = CancellationTokenSource.CreateLinkedTokenSource(abortCapture.Token);
        using var stderrCapture = CancellationTokenSource.CreateLinkedTokenSource(abortCapture.Token, stderrDrain.Token);
        var outputLimitReached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var outputBudget = new OutputBudget(_outputCharacterLimit);
        var standardOutputTask = ReadBoundedAsync(
            WrapStream(process.StandardOutput.BaseStream, _wrapStandardOutput),
            outputBudget,
            outputLimitReached,
            stdoutCapture.Token);
        var standardErrorTask = ReadBoundedAsync(
            WrapStream(process.StandardError.BaseStream, _wrapStandardError),
            outputBudget,
            outputLimitReached,
            stderrCapture.Token);
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
            abortCapture.Cancel();
            await SettleCaptureTasksAsync(standardOutputTask, standardErrorTask).ConfigureAwait(false);
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

        var remaining = _timeout - lifetime.Elapsed;
        var stdoutWait = remaining <= TimeSpan.Zero
            ? TimeSpan.Zero
            : remaining < _stdoutEofTimeout ? remaining : _stdoutEofTimeout;
        var captures = await ObserveSuccessCapturesAsync(
            standardOutputTask,
            standardErrorTask,
            abortCapture,
            stderrDrain,
            stdoutWait).ConfigureAwait(false);
        if (!captures.StdoutReachedEof)
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

    private static Stream WrapStream(Stream stream, Func<Stream, Stream>? wrap) =>
        wrap is null ? stream : wrap(stream);

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
    /// Drain redirected stdout and stderr after the child exits. Unix pipes complete
    /// <see cref="Stream.BeginRead"/> on worker-pool threads, so a short drain timer can still
    /// fire before a queued read commits. Stdout is therefore waited independently until EOF or
    /// a bounded wait (the remaining command timeout, capped at a few seconds). Stderr keeps the
    /// short drain so a descendant holding stderr cannot stall the caller. Cancellation still
    /// completes both captures without waiting for EOF so a descendant-held pipe cannot hang the
    /// invocation. A stdout stream that does not reach EOF is classified as invalid output, not
    /// success, so a truncated or dropped payload cannot be projected as a command result.
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
        var chars = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];
        var decoder = Encoding.UTF8.GetDecoder();
        var completed = 0;

        void Complete(bool reachedEof)
        {
            if (Interlocked.Exchange(ref completed, 1) != 0)
                return;
            string value;
            lock (gate)
                value = builder.ToString();
            tcs.TrySetResult(new BoundedCapture(value, reachedEof));
        }

        void AppendDecoded(int decoded)
        {
            var accepted = outputBudget.Take(decoded);
            if (accepted > 0)
            {
                lock (gate)
                    builder.Append(chars, 0, accepted);
            }
            if (accepted < decoded)
            {
                outputLimitReached.TrySetResult(true);
                Complete(reachedEof: false);
            }
        }

        void FlushDecoder()
        {
            var flushed = decoder.GetChars(buffer, 0, 0, chars, 0, flush: true);
            if (flushed > 0)
                AppendDecoded(flushed);
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
                    Complete(reachedEof: false);
                    return;
                }
                catch (IOException)
                {
                    Complete(reachedEof: false);
                    return;
                }

                if (Volatile.Read(ref completed) == 1)
                    return;
                if (read == 0)
                {
                    FlushDecoder();
                    if (Volatile.Read(ref completed) == 1)
                        return;
                    Complete(reachedEof: true);
                    return;
                }

                var decoded = decoder.GetChars(buffer, 0, read, chars, 0, flush: false);
                AppendDecoded(decoded);
                if (Volatile.Read(ref completed) == 1)
                    return;

                if (cancellationToken.IsCancellationRequested)
                {
                    Complete(reachedEof: false);
                    return;
                }

                stream.BeginRead(buffer, 0, buffer.Length, ReadCallback, null);
            }
            catch (Exception)
            {
                Complete(reachedEof: false);
            }
        }

        try
        {
            cancellationToken.Register(() => Complete(reachedEof: false));
            if (cancellationToken.IsCancellationRequested)
            {
                Complete(reachedEof: false);
                return tcs.Task;
            }

            stream.BeginRead(buffer, 0, buffer.Length, ReadCallback, null);
        }
        catch (Exception)
        {
            Complete(reachedEof: false);
        }

        return tcs.Task;
    }

    private async Task<BoundedCaptures> ObserveSuccessCapturesAsync(
        Task<BoundedCapture> standardOutputTask,
        Task<BoundedCapture> standardErrorTask,
        CancellationTokenSource abortCapture,
        CancellationTokenSource stderrDrain,
        TimeSpan stdoutWait)
    {
        if (!await TryWaitAsync(standardOutputTask, stdoutWait).ConfigureAwait(false)
            || standardOutputTask.Status != TaskStatus.RanToCompletion
            || !standardOutputTask.Result.ReachedEof)
        {
            abortCapture.Cancel();
            await SettleCaptureTasksAsync(standardOutputTask, standardErrorTask).ConfigureAwait(false);
            return new BoundedCaptures(string.Empty, string.Empty, StdoutReachedEof: false);
        }

        if (!await TryWaitAsync(standardErrorTask, _captureDrainTimeout).ConfigureAwait(false))
        {
            stderrDrain.Cancel();
            await TryWaitAsync(standardErrorTask, _captureDrainTimeout).ConfigureAwait(false);
        }

        var stdout = standardOutputTask.Result;
        var stderr = standardErrorTask.Status == TaskStatus.RanToCompletion
            ? standardErrorTask.Result
            : new BoundedCapture(string.Empty, ReachedEof: false);
        return new BoundedCaptures(stdout.Value, stderr.Value, StdoutReachedEof: true);
    }

    private async Task SettleCaptureTasksAsync(
        Task<BoundedCapture> standardOutputTask,
        Task<BoundedCapture> standardErrorTask)
    {
        var capturesTask = Task.WhenAll(standardOutputTask, standardErrorTask);
        try
        {
            if (!await TryWaitAsync(capturesTask, _captureDrainTimeout).ConfigureAwait(false)
                && !await TryWaitAsync(capturesTask, _captureDrainTimeout).ConfigureAwait(false))
            {
                _ = capturesTask.ContinueWith(
                    static task => _ = task.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                return;
            }

            await capturesTask.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A stream can close concurrently with process-tree termination. Failed results do
            // not expose captures, and successful process exit has already drained both streams.
        }
    }

    private static async Task<bool> TryWaitAsync(Task task, TimeSpan timeout)
    {
        if (task.IsCompleted)
            return true;
        if (timeout <= TimeSpan.Zero)
            return false;
        return await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false) == task;
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

    private readonly record struct BoundedCapture(string Value, bool ReachedEof);

    private readonly record struct BoundedCaptures(string StandardOutput, string StandardError, bool StdoutReachedEof);

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
