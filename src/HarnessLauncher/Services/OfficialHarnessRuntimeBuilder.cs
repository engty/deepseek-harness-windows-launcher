using System.Security.Cryptography;
using System.Text.Json;
using HarnessLauncher.Models;
using HarnessLauncher.Support;

namespace HarnessLauncher.Services;

public sealed record OfficialHarnessRuntimeArtifact(RuntimeManifest Manifest, string ArtifactPath);

/// <summary>
/// Rebuilds a complete Windows Runtime in the App-owned cache from the exact
/// version reported by the official npm registry. The existing Node/pnpm
/// toolchain is reused, while the Harness package graph is resolved into a
/// clean staging tree to avoid mixing incompatible versions.
/// </summary>
public sealed class OfficialHarnessRuntimeBuilder
{
    public async Task<OfficialHarnessRuntimeArtifact> BuildAsync(
        RuntimeInstallation current,
        OfficialHarnessVersionResult official,
        AppPaths paths,
        IProgress<RuntimeUpdateStage>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (current.NodeExecutable is not { Length: > 0 } node || !File.Exists(node))
            throw new InvalidOperationException("当前 Runtime 没有内置 Node，无法准备官方 Runtime 更新。");

        var pnpm = FindPnpm(current.Root);
        if (pnpm is null) throw new InvalidOperationException("当前 Runtime 没有内置 pnpm，无法准备官方 Runtime 更新。");

        var stagingRoot = Path.Combine(paths.Caches, "updates", "official-staging", Guid.NewGuid().ToString("N"));
        var stagedRuntime = Path.Combine(stagingRoot, "runtime");
        Directory.CreateDirectory(stagingRoot);
        try
        {
            progress?.Report(RuntimeUpdateStage.Preparing);
            await Task.Run(() => DataSlotManager.CopyDirectory(current.Root, stagedRuntime), cancellationToken)
                .ConfigureAwait(false);

            progress?.Report(RuntimeUpdateStage.Downloading);
            var environment = PluginExecutionEnvironment.Create(
                new RuntimeInstallation(
                    Path.Combine(stagedRuntime, "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js"),
                    stagedRuntime,
                    null,
                    Path.Combine(stagedRuntime, "node", "bin", "node.exe")),
                paths,
                Path.Combine(stagedRuntime, "dsh-home"),
                BuildPath(current, stagedRuntime));
            environment["npm_config_registry"] = "https://registry.npmjs.org";
            environment["NPM_CONFIG_REGISTRY"] = "https://registry.npmjs.org";
            environment["npm_config_ignore_scripts"] = "true";
            environment["CI"] = "1";
            environment["PATH"] = BuildPath(current, stagedRuntime);

            var install = await RunPnpmAsync(
                pnpm, node, stagedRuntime,
                ["--dir", stagedRuntime, "add", "--ignore-scripts", "--lockfile=false", "--save-exact", $"@deepseek-ai/dsh@{official.Version}"],
                environment, cancellationToken).ConfigureAwait(false);
            if (install.Status != 0)
                throw new InvalidOperationException($"官方 Harness 依赖安装失败：{SensitiveDataRedactor.Redact(install.Output)}");

            progress?.Report(RuntimeUpdateStage.Verifying);
            var installation = new RuntimeLocator(
                new Dictionary<string, string?> { ["HARNESS_RUNTIME_ROOT"] = stagedRuntime, ["PATH"] = environment["PATH"] },
                paths).Locate();
            if (!string.Equals(installation.Version, official.Version, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"官方 Runtime 版本预检失败：期望 {official.Version}，实际 {installation.Version ?? "unknown"}。");

            var probe = await RunRuntimeProbeAsync(installation, environment, stagedRuntime, cancellationToken)
                .ConfigureAwait(false);
            if (probe.Status != 0)
                throw new InvalidOperationException($"官方 Runtime 启动预检失败：{SensitiveDataRedactor.Redact(probe.Output)}");

            progress?.Report(RuntimeUpdateStage.Packaging);
            var artifactDirectory = Path.Combine(paths.Caches, "updates", "official-artifacts");
            Directory.CreateDirectory(artifactDirectory);
            var artifactPath = Path.Combine(artifactDirectory, $"DeepSeek-Harness-{official.Version}-windows-x64.tar.gz");
            if (File.Exists(artifactPath)) File.Delete(artifactPath);
            var archive = await SubprocessRunner.RunAsync(
                Path.Combine(Environment.SystemDirectory, "tar.exe"),
                ["-a", "-c", "-f", artifactPath, "-C", stagedRuntime, "."],
                currentDirectory: stagedRuntime,
                timeout: TimeSpan.FromMinutes(20),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (archive.Status != 0 || !File.Exists(artifactPath))
                throw new InvalidOperationException($"无法生成 Runtime 更新包：{SensitiveDataRedactor.Redact(archive.Output)}");

            var info = new FileInfo(artifactPath);
            var manifest = new RuntimeManifest
            {
                SchemaVersion = 1,
                RuntimeId = $"official-{official.Version.Replace('.', '-').Replace('+', '-')}",
                Channel = "official-npm",
                Architecture = ToolchainCatalog.CurrentArchitecture,
                Harness = new RuntimeManifest.HarnessVersion
                {
                    Package = "@deepseek-ai/dsh",
                    Version = official.Version,
                    Commit = $"npm-{official.Version}",
                },
                NodeVersion = await ReadNodeVersionAsync(node, cancellationToken).ConfigureAwait(false),
                MinShellVersion = LauncherVersion.Current,
                DataFormat = "dsh-home-v1",
                Artifact = new RuntimeManifest.ArtifactInfo
                {
                    Url = official.PackageUrl,
                    Size = info.Length,
                    Sha256 = await Sha256Async(artifactPath, cancellationToken).ConfigureAwait(false),
                },
                ReleaseNotesUrl = official.PackageUrl,
                PublishedAt = DateTime.UtcNow,
            };
            return new OfficialHarnessRuntimeArtifact(manifest, artifactPath);
        }
        finally
        {
            try { if (Directory.Exists(stagingRoot)) Directory.Delete(stagingRoot, true); }
            catch { }
        }
    }

    private static string? FindPnpm(string root) => new[]
    {
        Path.Combine(root, "node_modules", "pnpm", "bin", "pnpm.cjs"),
        Path.Combine(root, "node_modules", ".bin", "pnpm.cmd"),
        Path.Combine(root, "node_modules", ".bin", "pnpm.exe"),
    }.FirstOrDefault(File.Exists);

    private static async Task<SubprocessResult> RunPnpmAsync(
        string pnpm,
        string node,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken)
    {
        if (pnpm.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
            return await SubprocessRunner.RunAsync("cmd.exe", new[] { "/d", "/c", pnpm }.Concat(arguments).ToList(), environment, workingDirectory, TimeSpan.FromMinutes(15), cancellationToken);
        if (pnpm.EndsWith(".cjs", StringComparison.OrdinalIgnoreCase))
            return await SubprocessRunner.RunAsync(node, new[] { pnpm }.Concat(arguments).ToList(), environment, workingDirectory, TimeSpan.FromMinutes(15), cancellationToken);
        return await SubprocessRunner.RunAsync(pnpm, arguments, environment, workingDirectory, TimeSpan.FromMinutes(15), cancellationToken);
    }

    private static Task<SubprocessResult> RunRuntimeProbeAsync(
        RuntimeInstallation installation,
        IReadOnlyDictionary<string, string> environment,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var node = installation.NodeExecutable ?? "node.exe";
        var entry = installation.Executable;
        return entry.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
            ? SubprocessRunner.RunAsync("cmd.exe", new[] { "/d", "/c", entry, "--version" }, environment, workingDirectory, TimeSpan.FromSeconds(60), cancellationToken)
            : SubprocessRunner.RunAsync(node, new[] { entry, "--version" }, environment, workingDirectory, TimeSpan.FromSeconds(60), cancellationToken);
    }

    private static string BuildPath(RuntimeInstallation current, string stagedRuntime) => string.Join(
        Path.PathSeparator,
        new[]
        {
            Path.Combine(stagedRuntime, "node", "bin"),
            Path.Combine(stagedRuntime, "node_modules", ".bin"),
            Path.GetDirectoryName(current.NodeExecutable ?? "") ?? "",
            Environment.GetEnvironmentVariable("PATH") ?? "",
        }.Where(value => !string.IsNullOrWhiteSpace(value)));

    private static async Task<string> ReadNodeVersionAsync(string node, CancellationToken cancellationToken)
    {
        var result = await SubprocessRunner.RunAsync(node, ["--version"], cancellationToken: cancellationToken);
        return result.Status == 0 ? result.Output.Trim() : "unknown";
    }

    private static async Task<string> Sha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, useAsync: true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }
}
