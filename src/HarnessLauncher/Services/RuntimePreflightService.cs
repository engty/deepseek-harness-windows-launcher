using HarnessLauncher.Support;

namespace HarnessLauncher.Services;

public class RuntimePreflightException : Exception
{
    public RuntimePreflightException(string message) : base(message) { }

    public static RuntimePreflightException VersionFailed(string output) =>
        new($"Runtime --version 预检失败：{SensitiveDataRedactor.Redact(output)}");
    public static RuntimePreflightException ConfigFailed(string output) =>
        new($"Runtime --dump-config 预检失败：{SensitiveDataRedactor.Redact(output)}");
    public static readonly RuntimePreflightException EmptyVersion =
        new("Runtime --version 没有返回版本号。");
}

/// <summary>Direct port of RuntimePreflightService.</summary>
public sealed class RuntimePreflightService
{
    public async Task RunAsync(
        RuntimeInstallation installation,
        AppPaths paths,
        string dshHome,
        string currentDirectory)
    {
        Directory.CreateDirectory(dshHome);
        Directory.CreateDirectory(currentDirectory);

        var version = await ExecuteAsync(installation,
            new[] { "--version" }, dshHome, paths, currentDirectory).ConfigureAwait(false);
        if (version.Status != 0) throw RuntimePreflightException.VersionFailed(version.Output);
        if (string.IsNullOrWhiteSpace(version.Output)) throw RuntimePreflightException.EmptyVersion;

        var config = await ExecuteAsync(installation,
            new[] { "--profile", "web", "--dump-config" }, dshHome, paths, currentDirectory)
            .ConfigureAwait(false);
        if (config.Status != 0) throw RuntimePreflightException.ConfigFailed(config.Output);
    }

    private static async Task<SubprocessResult> ExecuteAsync(
        RuntimeInstallation installation,
        IReadOnlyList<string> arguments,
        string dshHome,
        AppPaths paths,
        string currentDirectory)
    {
        var environment = Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string?)e.Value)!;
        environment["DSH_HOME"] = dshHome;
        environment["DSH_LAUNCHER"] = "DeepSeekHarness";
        environment["PATH"] = new PluginDependencyService(environment, paths.Toolchain)
            .RuntimeSearchPath(installation);

        string executable;
        List<string> fullArguments;
        if (installation.NodeExecutable is { } node && !installation.IsShellShim)
        {
            executable = node;
            fullArguments = new List<string> { installation.Executable };
        }
        else if (installation.IsShellShim)
        {
            executable = "cmd.exe";
            fullArguments = new List<string> { "/d", "/s", "/c", installation.Executable };
        }
        else
        {
            executable = installation.Executable;
            fullArguments = new List<string>();
        }
        fullArguments.AddRange(arguments);

        // Streams output while the child runs and enforces a hard timeout, so
        // a broken candidate Runtime can neither fill the pipe (deadlock) nor
        // hang the update flow forever.
        return await SubprocessRunner.RunAsync(
            executable,
            fullArguments,
            environment.Where(p => p.Value is not null).ToDictionary(p => p.Key, p => p.Value!),
            currentDirectory,
            timeout: TimeSpan.FromSeconds(60)).ConfigureAwait(false);
    }
}
