using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HarnessLauncher.Support;

namespace HarnessLauncher.Services;

/// <summary>
/// Sidecar identity stored in the PID file. The process start time and nonce
/// let the next launch prove that a lingering PID actually belongs to this
/// app's previous sidecar instead of a reused PID that now points at an
/// unrelated process.
/// </summary>
public sealed class SidecarPidRecord
{
    public int Pid { get; set; }
    public string Nonce { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public string Executable { get; set; } = "";
}

public class HarnessProcessException : Exception
{
    public HarnessProcessException(string message) : base(message) { }

    public static readonly HarnessProcessException AlreadyRunning = new("Harness 已经在运行。");
    public static HarnessProcessException FailedToLaunch(string message) => new($"无法启动 Harness：{message}");
    public static HarnessProcessException ExitedBeforeReady(string output) => new($"Harness 在 UI 就绪前退出。\n{output}");
    public static readonly HarnessProcessException ReadinessTimeout = new("等待 Harness Web UI 就绪超时。");
}

/// <summary>
/// Windows port of HarnessProcessController: starts the dsh sidecar, waits
/// for the "dsh web: http://127.0.0.1:&lt;port&gt;" readiness line, and owns the
/// PID file used to clean up stale sidecars from a previous crashed launch.
/// Everything runs as the current user; no admin rights are required.
/// </summary>
public sealed class HarnessProcessController
{
    private Process? _process;
    public Action<string>? OnUnexpectedTermination { get; set; }
    private TaskCompletionSource<Uri>? _readiness;
    private Guid _launchToken = Guid.NewGuid();
    private readonly StringBuilder _outputBuffer = new();
    private readonly object _bufferLock = new();
    private string? _sidecarPidPath;

    public bool IsRunning => _process is { HasExited: false };

    public async Task<Uri> StartAsync(
        RuntimeInstallation installation,
        AppPaths paths,
        string? overlayPath,
        string? dshHomeOverride = null,
        string? currentDirectoryOverride = null)
    {
        if (_process is not null) throw HarnessProcessException.AlreadyRunning;
        paths.Prepare();
        CleanupStaleSidecar(installation, paths);
        lock (_bufferLock) { _outputBuffer.Clear(); }

        var dshArguments = new List<string> { "--profile", "web" };
        if (overlayPath is not null)
        {
            dshArguments.Add("--patch");
            dshArguments.Add(overlayPath);
        }
        dshArguments.Add("--host");
        dshArguments.Add("127.0.0.1");
        dshArguments.Add("--port");
        dshArguments.Add("0");

        var startInfo = CreateStartInfo(installation, dshArguments);
        startInfo.WorkingDirectory = currentDirectoryOverride ?? paths.ActiveDataSlot;

        var environment = Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string?)e.Value)!;
        environment["DSH_HOME"] = dshHomeOverride ?? paths.DshHome;
        environment["DSH_LAUNCHER"] = "DeepSeekHarness";
        environment["PATH"] = new PluginDependencyService(environment, paths.Toolchain)
            .RuntimeSearchPath(installation);
        startInfo.Environment.Clear();
        foreach (var pair in environment)
        {
            if (pair.Value is not null) startInfo.Environment[pair.Key] = pair.Value;
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _process = process;
        var token = Guid.NewGuid();
        _launchToken = token;

        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.Exited += (_, _) =>
        {
            exited.TrySetResult();
            HandleTermination(process, token);
        };
        process.OutputDataReceived += (_, e) => Consume(e.Data, isError: false, token);
        process.ErrorDataReceived += (_, e) => Consume(e.Data, isError: true, token);

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _sidecarPidPath = paths.SidecarPid;
            var record = new SidecarPidRecord
            {
                Pid = process.Id,
                Nonce = token.ToString(),
                StartedAt = DateTime.Now,
                Executable = installation.Executable,
            };
            File.WriteAllText(paths.SidecarPid, JsonSerializer.Serialize(record));
        }
        catch (Exception error)
        {
            CleanupProcess();
            throw HarnessProcessException.FailedToLaunch(error.Message);
        }

        var readiness = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        _readiness = readiness;
        // The ready line may already have arrived before _readiness was set.
        lock (_bufferLock)
        {
            ResolveReadiness(_outputBuffer.ToString());
        }

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var registration = timeoutCts.Token.Register(() =>
        {
            if (_launchToken == token)
            {
                _readiness?.TrySetException(HarnessProcessException.ReadinessTimeout);
            }
        }, useSynchronizationContext: false).ConfigureAwait(false);

        try
        {
            return await readiness.Task.ConfigureAwait(false);
        }
        catch
        {
            await StopAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static ProcessStartInfo CreateStartInfo(RuntimeInstallation installation, List<string> dshArguments)
    {
        ProcessStartInfo startInfo;
        if (installation.NodeExecutable is { } node && !installation.IsShellShim)
        {
            // The extensionless dsh entry is a JavaScript file: run it with the
            // bundled Node, exactly like the macOS launcher does.
            startInfo = new ProcessStartInfo(node);
            startInfo.ArgumentList.Add(installation.Executable);
        }
        else if (installation.IsShellShim)
        {
            // Windows npm shims are .cmd scripts and must go through cmd.exe.
            // The bundled Node directory is already first on PATH, so the
            // shim finds node.exe without any system install.
            startInfo = new ProcessStartInfo("cmd.exe", "/d /s /c \"" + installation.Executable + "\"");
        }
        else
        {
            startInfo = new ProcessStartInfo(installation.Executable);
        }
        foreach (var argument in dshArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        startInfo.UseShellExecute = false;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.CreateNoWindow = true;
        startInfo.StandardOutputEncoding = Encoding.UTF8;
        startInfo.StandardErrorEncoding = Encoding.UTF8;
        return startInfo;
    }

    public async Task StopAsync()
    {
        _launchToken = Guid.NewGuid();
        _readiness?.TrySetException(HarnessProcessException.ExitedBeforeReady("Harness 被请求停止。"));
        _readiness = null;
        var process = _process;
        if (process is null)
        {
            CleanupProcess();
            return;
        }

        SubprocessRunner.TryKillTree(process);
        for (var i = 0; i < 50 && !process.HasExited; i++)
        {
            await Task.Delay(100).ConfigureAwait(false);
        }
        CleanupProcess();
    }

    private void Consume(string? line, bool isError, Guid token)
    {
        if (line is null) return;
        string snapshot;
        lock (_bufferLock)
        {
            if (token != _launchToken) return;
            _outputBuffer.AppendLine(line);
            if (_outputBuffer.Length > 160_000)
            {
                _outputBuffer.Remove(0, _outputBuffer.Length - 160_000);
            }
            snapshot = _outputBuffer.ToString();
        }
        var redacted = SensitiveDataRedactor.Redact(line);
        AppLogger.Log(isError ? AppLogger.Level.Error : AppLogger.Level.Info, "runtime",
            $"Harness {(isError ? "stderr" : "stdout")}: {redacted}");
        ResolveReadiness(snapshot);
    }

    private static readonly Regex ReadyPattern =
        new(@"dsh web:\s+(http://127\.0\.0\.1:\d+)", RegexOptions.Compiled);

    private void ResolveReadiness(string text)
    {
        var readiness = _readiness;
        if (readiness is null) return;
        var match = ReadyPattern.Match(text);
        if (!match.Success || !Uri.TryCreate(match.Groups[1].Value, UriKind.Absolute, out var url)) return;
        if (_readiness == readiness)
        {
            _readiness = null;
        }
        readiness.TrySetResult(url);
    }

    private void HandleTermination(Process process, Guid token)
    {
        if (_launchToken != token) return;
        string message;
        lock (_bufferLock)
        {
            message = _outputBuffer.ToString().Trim();
        }
        var tail = SensitiveDataRedactor.Redact(message.Length > 4000 ? message[^4000..] : message);
        var readiness = _readiness;
        if (readiness is not null)
        {
            _readiness = null;
            readiness.TrySetException(HarnessProcessException.ExitedBeforeReady(tail));
        }
        else
        {
            OnUnexpectedTermination?.Invoke(tail);
        }
        if (ReferenceEquals(_process, process))
        {
            CleanupProcess();
        }
    }

    private void CleanupProcess()
    {
        if (_process is { } process && _sidecarPidPath is { } pidPath && File.Exists(pidPath))
        {
            try
            {
                var record = JsonSerializer.Deserialize<SidecarPidRecord>(File.ReadAllText(pidPath));
                if (record?.Pid == process.Id)
                {
                    File.Delete(pidPath);
                }
            }
            catch { }
        }
        _sidecarPidPath = null;
        _process = null;
    }

    private void CleanupStaleSidecar(RuntimeInstallation installation, AppPaths paths)
    {
        if (!File.Exists(paths.SidecarPid)) return;

        int pid;
        DateTime? recordedStartTime = null;
        try
        {
            var text = File.ReadAllText(paths.SidecarPid).Trim();
            if (text.StartsWith('{'))
            {
                var record = JsonSerializer.Deserialize<SidecarPidRecord>(text);
                if (record is null) { File.Delete(paths.SidecarPid); return; }
                pid = record.Pid;
                recordedStartTime = record.StartedAt;
            }
            else if (int.TryParse(text, out var legacyPid))
            {
                // Legacy plain-integer PID files keep the previous path-only
                // behaviour for one upgrade cycle.
                pid = legacyPid;
            }
            else
            {
                File.Delete(paths.SidecarPid);
                return;
            }
        }
        catch
        {
            try { File.Delete(paths.SidecarPid); } catch { }
            return;
        }

        if (pid <= 0 || pid == Environment.ProcessId)
        {
            try { File.Delete(paths.SidecarPid); } catch { }
            return;
        }

        Process live;
        try
        {
            live = Process.GetProcessById(pid);
        }
        catch
        {
            // No such process anymore: the PID file is stale.
            try { File.Delete(paths.SidecarPid); } catch { }
            return;
        }

        using (live)
        {
            var allowedPaths = new[] { installation.NodeExecutable, installation.Executable }
                .Where(p => p is not null)
                .Select(p => Path.GetFullPath(p!))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            string? runningPath = null;
            try { runningPath = live.MainModule?.FileName; } catch { }
            // cmd.exe wraps .cmd shims, so the shim itself is also allowed.
            var isCmdShim = installation.IsShellShim &&
                string.Equals(runningPath, Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                    StringComparison.OrdinalIgnoreCase);

            if (runningPath is null || (!allowedPaths.Contains(runningPath) && !isCmdShim))
            {
                // Do not kill a process that no longer provably belongs to
                // this app's Runtime; the PID may have been reused.
                try { File.Delete(paths.SidecarPid); } catch { }
                return;
            }

            if (recordedStartTime is { } recorded)
            {
                // The executable path matches, but a reused PID can still
                // point at a different instance of the same binary. Only kill
                // when the recorded process start time matches the live one.
                DateTime liveStart;
                try { liveStart = live.StartTime; }
                catch
                {
                    try { File.Delete(paths.SidecarPid); } catch { }
                    return;
                }
                if (Math.Abs((liveStart - recorded).TotalSeconds) >= 5)
                {
                    try { File.Delete(paths.SidecarPid); } catch { }
                    return;
                }
            }

            SubprocessRunner.TryKillTree(live);
            try { File.Delete(paths.SidecarPid); } catch { }
        }
    }
}
