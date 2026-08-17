using System.Diagnostics;
using System.Text;

namespace HarnessLauncher.Support;

public sealed class SubprocessRunnerException : Exception
{
    public SubprocessRunnerException(string message) : base(message) { }

    public static SubprocessRunnerException LaunchFailed(string message) =>
        new($"无法启动子进程：{message}");

    public static SubprocessRunnerException TimedOut(string command, string output)
    {
        var tail = SensitiveDataRedactor.Redact(output.Length > 2000 ? output[^2000..] : output);
        var detail = string.IsNullOrEmpty(tail) ? "" : $"\n{tail}";
        return new SubprocessRunnerException($"子进程执行超时（{command}），已终止。{detail}");
    }
}

public readonly record struct SubprocessResult(int Status, string Output);

/// <summary>
/// Runs a short-lived helper process and streams its stdout/stderr into a
/// bounded buffer while it runs (reading only after termination would let
/// the child block forever once the pipe buffer fills).
/// The caller is responsible for redacting output before display, and for
/// passing absolute tool paths (the runner never searches PATH implicitly).
/// </summary>
public static class SubprocessRunner
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);

    public static async Task<SubprocessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment = null,
        string? currentDirectory = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        if (environment is not null)
        {
            startInfo.Environment.Clear();
            foreach (var pair in environment)
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }
        }
        if (currentDirectory is not null)
        {
            startInfo.WorkingDirectory = currentDirectory;
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var buffer = new BoundedSubprocessOutputBuffer(4 * 1024 * 1024);
        var commandDescription = Path.GetFileName(executable);

        process.OutputDataReceived += (_, e) => buffer.AppendLine(e.Data);
        process.ErrorDataReceived += (_, e) => buffer.AppendLine(e.Data);

        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.Exited += (_, _) => completion.TrySetResult(process.ExitCode);

        try
        {
            process.Start();
        }
        catch (Exception error)
        {
            throw SubprocessRunnerException.LaunchFailed(error.Message);
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = new CancellationTokenSource(timeout ?? DefaultTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);
        await using var registration = linked.Token.Register(() =>
        {
            TryKillTree(process);
            if (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                completion.TrySetException(SubprocessRunnerException.TimedOut(commandDescription, buffer.StringValue));
            }
            else
            {
                completion.TrySetCanceled(cancellationToken);
            }
        }, useSynchronizationContext: false).ConfigureAwait(false);

        var status = await completion.Task.ConfigureAwait(false);
        // Let async pipe readers flush the final chunks.
        try { process.WaitForExit(2000); } catch { }
        return new SubprocessResult(status, buffer.StringValue);
    }

    public static void TryKillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch { }
    }
}

/// <summary>Thread-safe bounded buffer that keeps the newest bytes.</summary>
public sealed class BoundedSubprocessOutputBuffer
{
    private readonly object _lock = new();
    private readonly int _limit;
    private readonly StringBuilder _data = new();

    public BoundedSubprocessOutputBuffer(int limit)
    {
        _limit = limit;
    }

    public void AppendLine(string? line)
    {
        if (line is null) return;
        lock (_lock)
        {
            _data.AppendLine(line);
            if (_data.Length > _limit)
            {
                _data.Remove(0, _data.Length - _limit);
            }
        }
    }

    public string StringValue
    {
        get { lock (_lock) { return _data.ToString(); } }
    }
}
