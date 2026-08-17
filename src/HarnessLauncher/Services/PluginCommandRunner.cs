using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using HarnessLauncher.Support;

namespace HarnessLauncher.Services;

public readonly record struct PluginCommandResult(int Status, string Output);

public class PluginCommandException : Exception
{
    public PluginCommandException(string message) : base(message) { }

    public static PluginCommandException FailedToLaunch(string message) =>
        new($"无法启动 Harness 插件命令：{message}");
    public static PluginCommandException NonZeroExit(string output) =>
        new($"插件命令执行失败。\n{output}");
    public static PluginCommandException CommandTimedOut(string command, string output)
    {
        var tail = SensitiveDataRedactor.Redact(output.Length > 2000 ? output[^2000..] : output);
        var detail = string.IsNullOrEmpty(tail) ? "" : $"\n{tail}";
        return new PluginCommandException($"插件命令执行超时（{command}），已终止其进程树。{detail}");
    }

    public sealed class BuildScriptsRequireApproval : PluginCommandException
    {
        public IReadOnlyList<string> Packages { get; }

        public BuildScriptsRequireApproval(IReadOnlyList<string> packages, string output)
            : base($"pnpm 阻止了插件构建脚本：{string.Join("、", packages)}。\n{output}")
        {
            Packages = packages;
        }
    }
}

/// <summary>
/// Windows port of PluginCommandRunner: runs official `dsh plugin` commands
/// against a cloned staging slot, preflights the candidate profile, and
/// activates it transactionally.
/// </summary>
public sealed class PluginCommandRunner
{
    /// <summary>Default wall-clock limit for one `dsh plugin` command.</summary>
    private static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromSeconds(900);

    private Process? _activeProcess;

    /// <summary>
    /// Terminates the currently running plugin command and its child tree.
    /// Used when the app quits so pnpm/plugin build children do not survive
    /// as orphans.
    /// </summary>
    public void CancelActiveCommand()
    {
        if (_activeProcess is { } process)
        {
            SubprocessRunner.TryKillTree(process);
        }
    }

    public PluginDependencyPlan DependencyPlan(
        RuntimeInstallation installation,
        AppPaths paths,
        IReadOnlyList<string> arguments,
        IReadOnlyList<ToolchainRequirement>? additionalRequirements = null)
    {
        return new PluginDependencyService(privateToolchainRoot: paths.Toolchain)
            .Resolve(installation, arguments, additionalRequirements);
    }

    public async Task<PluginCommandResult> MutateProfileAsync(
        RuntimeInstallation installation,
        AppPaths paths,
        IReadOnlyList<string> arguments,
        PluginDependencyPlan? dependencyPlan = null,
        IReadOnlyList<string>? allowedBuildScripts = null)
    {
        dependencyPlan ??= DependencyPlan(installation, paths, arguments);
        var dataSlotManager = new DataSlotManager();
        var stagingSlot = await dataSlotManager.CloneActiveSlotAsync(paths).ConfigureAwait(false);
        var stagingRoot = Path.GetDirectoryName(stagingSlot)!;
        // The private staging copy is removed on every exit path after this
        // point; only the slot that was actually activated survives.
        try
        {
            var stagingHome = Path.Combine(stagingSlot, "dsh-home");
            var stagingProfile = Path.Combine(stagingHome, "profiles", "web");
            var metadataStore = new PluginMetadataStore();
            Directory.CreateDirectory(stagingProfile);
            PluginOperationLog.Append(
                "DEPENDENCY PLAN " + string.Join(", ", dependencyPlan.Dependencies.Select(
                    d => $"{d.Name}={d.Version ?? "unknown"} [{d.Source.DisplayName()}]")),
                paths.PluginOperationsLog);
            if (allowedBuildScripts is { Count: > 0 })
            {
                PnpmWorkspaceConfig.ApproveBuildScripts(allowedBuildScripts, stagingProfile);
            }

            var dependencyService = new PluginDependencyService(privateToolchainRoot: paths.Toolchain);
            var result = await RunAsync(
                installation,
                new[] { "plugin", "--profile", "web" }.Concat(arguments).ToList(),
                dependencyService.Applying(dependencyPlan, new Dictionary<string, string>
                {
                    ["DSH_HOME"] = stagingHome,
                    ["DSH_LAUNCHER"] = "DeepSeekHarness",
                }),
                stagingProfile,
                paths.PluginOperationsLog).ConfigureAwait(false);

            if (result.Status != 0)
            {
                if (ParseBuildApprovalPackages(result.Output) is { } packages)
                {
                    throw new PluginCommandException.BuildScriptsRequireApproval(
                        packages, Redact(result.Output));
                }
                throw PluginCommandException.NonZeroExit(Redact(result.Output));
            }

            var preflight = await RunAsync(
                installation,
                new[] { "--profile", "web", "--dump-config" },
                dependencyService.Applying(dependencyPlan, new Dictionary<string, string>
                {
                    ["DSH_HOME"] = stagingHome,
                    ["DSH_LAUNCHER"] = "DeepSeekHarness",
                }),
                stagingProfile,
                paths.PluginOperationsLog).ConfigureAwait(false);
            if (preflight.Status != 0)
            {
                PluginOperationLog.Append($"PREFLIGHT FAILED {Redact(preflight.Output)}",
                    paths.PluginOperationsLog);
                throw PluginCommandException.NonZeroExit("插件配置预检失败，当前 profile 未改变。");
            }

            var candidateController = new HarnessProcessController();
            try
            {
                await candidateController.StartAsync(
                    installation,
                    paths,
                    File.Exists(paths.Overlay) ? paths.Overlay : null,
                    dshHomeOverride: stagingHome,
                    currentDirectoryOverride: stagingSlot).ConfigureAwait(false);
                await candidateController.StopAsync().ConfigureAwait(false);
            }
            catch (Exception error)
            {
                PluginOperationLog.Append(
                    $"CANDIDATE START FAILED {SensitiveDataRedactor.Redact(error.Message)}",
                    paths.PluginOperationsLog);
                await candidateController.StopAsync().ConfigureAwait(false);
                throw PluginCommandException.NonZeroExit("插件候选启动预检失败，当前 profile 未改变。");
            }

            if (!File.Exists(Path.Combine(stagingProfile, "package.json")))
            {
                PluginOperationLog.Append("STAGING PROFILE INVALID: package.json missing",
                    paths.PluginOperationsLog);
                throw PluginCommandException.NonZeroExit("临时 profile 没有生成 package.json");
            }

            var metadata = metadataStore.Collect(stagingProfile, arguments);
            metadataStore.Write(metadata,
                Path.Combine(stagingHome, "launcher", "plugin-metadata.json"));

            try
            {
                dataSlotManager.Activate(stagingSlot, paths);
            }
            catch (Exception error)
            {
                PluginOperationLog.Append(
                    $"ACTIVATE FAILED {SensitiveDataRedactor.Redact(error.Message)}",
                    paths.PluginOperationsLog);
                throw;
            }
            PluginOperationLog.Append(
                $"ACTIVATE SUCCEEDED {Redact(string.Join(' ', arguments))}",
                paths.PluginOperationsLog);
            AppLogger.Log(AppLogger.Level.Info, "plugins",
                $"Plugin profile activated: {Redact(string.Join(' ', arguments))}");
            return result;
        }
        finally
        {
            try { if (Directory.Exists(stagingRoot)) Directory.Delete(stagingRoot, recursive: true); }
            catch { }
        }
    }

    private async Task<PluginCommandResult> RunAsync(
        RuntimeInstallation installation,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        string currentDirectory,
        string logPath)
    {
        ProcessStartInfo startInfo;
        if (installation.NodeExecutable is { } node && !installation.IsShellShim)
        {
            startInfo = new ProcessStartInfo(node);
            startInfo.ArgumentList.Add(installation.Executable);
        }
        else if (installation.IsShellShim)
        {
            startInfo = new ProcessStartInfo("cmd.exe", "/d /s /c \"" + installation.Executable + "\"");
        }
        else
        {
            startInfo = new ProcessStartInfo(installation.Executable);
        }
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        startInfo.UseShellExecute = false;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.CreateNoWindow = true;
        startInfo.StandardOutputEncoding = Encoding.UTF8;
        startInfo.StandardErrorEncoding = Encoding.UTF8;
        startInfo.WorkingDirectory = currentDirectory;
        startInfo.Environment.Clear();
        foreach (var pair in environment)
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }

        var outputBuffer = new BoundedSubprocessOutputBuffer(4 * 1024 * 1024);
        var commandDescription = Redact(string.Join(' ', arguments));
        AppLogger.Log(AppLogger.Level.Info, "plugins", $"Plugin command started: {commandDescription}");
        PluginOperationLog.Append($"START {commandDescription}", logPath);

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => outputBuffer.AppendLine(e.Data);
        process.ErrorDataReceived += (_, e) => outputBuffer.AppendLine(e.Data);

        var completion = new TaskCompletionSource<PluginCommandResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        process.Exited += (_, _) =>
        {
            var output = outputBuffer.StringValue;
            var redactedOutput = Redact(output);
            if (process.ExitCode == 0)
            {
                AppLogger.Log(AppLogger.Level.Info, "plugins",
                    $"Plugin command completed: {commandDescription}");
                PluginOperationLog.Append($"EXIT 0 {commandDescription}\n{redactedOutput}", logPath);
            }
            else
            {
                AppLogger.Log(AppLogger.Level.Error, "plugins",
                    $"Plugin command failed ({process.ExitCode}): {redactedOutput}");
                PluginOperationLog.Append($"EXIT {process.ExitCode} {commandDescription}\n{redactedOutput}", logPath);
            }
            completion.TrySetResult(new PluginCommandResult(process.ExitCode, output));
        };

        _activeProcess = process;
        try
        {
            try
            {
                process.Start();
            }
            catch (Exception error)
            {
                AppLogger.Log(AppLogger.Level.Error, "plugins",
                    $"Plugin command launch failed: {error.Message}");
                PluginOperationLog.Append(
                    $"LAUNCH FAILED {commandDescription}: {SensitiveDataRedactor.Redact(error.Message)}",
                    logPath);
                throw PluginCommandException.FailedToLaunch(error.Message);
            }
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeoutCts = new CancellationTokenSource(DefaultCommandTimeout);
            await using var registration = timeoutCts.Token.Register(() =>
            {
                AppLogger.Log(AppLogger.Level.Error, "plugins",
                    $"Plugin command timed out: {commandDescription}");
                SubprocessRunner.TryKillTree(process);
                completion.TrySetException(PluginCommandException.CommandTimedOut(
                    commandDescription, outputBuffer.StringValue));
            }, useSynchronizationContext: false).ConfigureAwait(false);

            var result = await completion.Task.ConfigureAwait(false);
            try { process.WaitForExit(2000); } catch { }
            return result;
        }
        finally
        {
            _activeProcess = null;
        }
    }

    private static string Redact(string output) =>
        string.Join('\n', SensitiveDataRedactor.Redact(output)
            .Split('\n')
            .TakeLast(80));

    private static readonly Regex BuildApprovalMarker = new(
        @"(?im)ignored build scripts:\s*([^\r\n]+)", RegexOptions.Compiled);
    private static readonly Regex PackageNameToken = new(
        @"(?:(?:@[\w._-]+/)?[\w._-]+)", RegexOptions.Compiled);

    public static IReadOnlyList<string>? ParseBuildApprovalPackages(string output)
    {
        var marker = BuildApprovalMarker.Match(output);
        if (!marker.Success) return null;
        var line = marker.Groups[1].Value;
        var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "allowBuilds", "ignored", "build", "scripts", "run", "pnpm" };
        var candidates = PackageNameToken.Matches(line)
            .Select(m => m.Value)
            .Where(v => !ignored.Contains(v))
            .ToList();
        var unique = candidates
            .Distinct(StringComparer.Ordinal)
            .Where(v => v.Contains('/') || Regex.IsMatch(v, @"^[a-z0-9._-]+$"))
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToList();
        return unique.Count == 0 ? null : unique;
    }
}

public static class PluginOperationLog
{
    /// <summary>Cap on how many rotated plugin-operation logs are kept.</summary>
    private const int MaxRotatedLogs = 5;
    private static readonly object WriteLock = new();

    public static void Append(string eventText, string path)
    {
        var redacted = SensitiveDataRedactor.Redact(eventText);
        var line = $"[{DateTime.Now:O}] {redacted}\n";
        lock (WriteLock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path) && new FileInfo(path).Length >= 1_000_000)
                {
                    var rotated = Path.Combine(
                        Path.GetDirectoryName(path)!,
                        $"plugin-operations-{DateTimeOffset.Now.ToUnixTimeSeconds()}-{Guid.NewGuid().ToString("N")[..8]}.log");
                    try { File.Move(path, rotated, overwrite: true); } catch { }
                    PruneRotatedLogs(Path.GetDirectoryName(path)!);
                }
                File.AppendAllText(path, line, Encoding.UTF8);
            }
            catch (Exception error)
            {
                AppLogger.Log(AppLogger.Level.Error, "plugins",
                    $"Could not persist plugin operation log: {error.Message}");
            }
        }
    }

    private static void PruneRotatedLogs(string directory)
    {
        try
        {
            var rotated = Directory.GetFiles(directory, "plugin-operations-*.log")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Skip(MaxRotatedLogs);
            foreach (var old in rotated)
            {
                try { File.Delete(old); } catch { }
            }
        }
        catch { }
    }
}
