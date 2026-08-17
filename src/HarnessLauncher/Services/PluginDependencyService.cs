using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace HarnessLauncher.Services;

public enum PluginDependencySource
{
    Bundled,
    System,
    User,
}

public static class PluginDependencySourceExtensions
{
    public static string DisplayName(this PluginDependencySource source) => source switch
    {
        PluginDependencySource.Bundled => "App 内置",
        PluginDependencySource.System => "Windows 系统",
        PluginDependencySource.User => "用户已有",
        _ => "未知",
    };
}

public sealed record ResolvedPluginDependency(
    string Name,
    string Executable,
    string? Version,
    PluginDependencySource Source)
{
    public string ConfirmationLine =>
        $"• {Name}{(Version is not null ? $" {Version}" : "")}（{Source.DisplayName()}，仅供本 App 使用）";
}

public sealed record PluginDependencyPlan(
    IReadOnlyList<ResolvedPluginDependency> Dependencies,
    string SearchPath,
    IReadOnlyList<ToolchainInstallPlan> ToolchainInstallPlans)
{
    public bool UsesUserTools => Dependencies.Any(d => d.Source == PluginDependencySource.User);

    public string ConfirmationText
    {
        get
        {
            var dependencyLines = string.Join("\n", Dependencies.Select(d => d.ConfirmationLine));
            var installLines = string.Join("\n", ToolchainInstallPlans.Select(p => p.ConfirmationText));
            var installText = installLines.Length == 0
                ? ""
                : $"\n\n需要用户确认后安装的 App 私有依赖：\n{installLines}";
            return $"""
                插件基础依赖：
                {dependencyLines}

                PATH 只会传给 DeepSeek Harness 的插件子进程，不会修改系统 PATH、Shell 配置或全局包。
                插件及其 Node.js 依赖只会写入 App 私有 DSH_HOME。
                {installText}
                """;
        }
    }
}

public class PluginDependencyException : Exception
{
    public PluginDependencyException(IReadOnlyList<string> missing)
        : base($"App 私有插件工具链不完整，缺少：{string.Join("、", missing)}。请更新或重新安装 DeepSeek Harness；Launcher 不会自动修改全局环境。")
    {
    }
}

/// <summary>
/// Resolves the small, allow-listed toolchain required by the official
/// `dsh plugin` command. App-bundled tools always win. Existing user tools
/// are a compatibility fallback and are exposed only to the child process.
/// Windows port: .cmd shims, ';' PATH separator, System32 curl.
/// </summary>
public sealed class PluginDependencyService
{
    private readonly IReadOnlyDictionary<string, string?> _baseEnvironment;
    private readonly string? _privateToolchainRoot;

    public PluginDependencyService(
        IReadOnlyDictionary<string, string?>? environment = null,
        string? privateToolchainRoot = null)
    {
        _baseEnvironment = environment ?? Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string?)e.Value);
        _privateToolchainRoot = privateToolchainRoot;
    }

    public PluginDependencyPlan Resolve(
        RuntimeInstallation installation,
        IReadOnlyList<string> arguments,
        IReadOnlyList<ToolchainRequirement>? additionalRequirements = null)
    {
        var dependencies = new List<ResolvedPluginDependency>();
        var missing = new List<string>();
        var installPlans = new List<ToolchainInstallPlan>();

        if (ResolvePnpm(installation) is { } pnpm)
        {
            dependencies.Add(pnpm);
        }
        else
        {
            missing.Add("pnpm");
        }

        if (arguments.Any(IsGitHostedSpec))
        {
            if (ResolveSystemTool("git", Path.Combine(Environment.GetFolderPath(
                    Environment.SpecialFolder.ProgramFiles), "Git", "cmd", "git.exe")) is { } git)
            {
                dependencies.Add(git);
            }
            else
            {
                missing.Add("git");
            }
            if (ResolveSystemTool("curl", Path.Combine(Environment.SystemDirectory, "curl.exe")) is { } curl)
            {
                dependencies.Add(curl);
            }
            else
            {
                missing.Add("curl");
            }
        }

        foreach (var requirement in additionalRequirements ?? Array.Empty<ToolchainRequirement>())
        {
            if (ResolvePrivateTool(requirement) is { } tool)
            {
                dependencies.Add(tool);
            }
            else if (ToolchainCatalog.Bundled.ManifestFor(requirement) is { } manifest &&
                     _privateToolchainRoot is not null)
            {
                installPlans.Add(new ToolchainInstallPlan(
                    manifest,
                    Path.Combine(_privateToolchainRoot, manifest.Id, manifest.Version)));
            }
            else
            {
                missing.Add(requirement.Id);
            }
        }

        if (missing.Count > 0) throw new PluginDependencyException(missing);
        return new PluginDependencyPlan(
            dependencies,
            SearchPathFor(installation, dependencies),
            installPlans);
    }

    /// <summary>
    /// The Harness sidecar inherits the same private tool directories so an
    /// installed plugin can reach App-managed basics such as Node and pnpm.
    /// </summary>
    public string RuntimeSearchPath(RuntimeInstallation installation)
    {
        var dependencies = new List<ResolvedPluginDependency>();
        if (ResolvePnpm(installation) is { } pnpm) dependencies.Add(pnpm);
        return SearchPathFor(installation, dependencies);
    }

    public static ToolchainRequirement? InstallableRequirementFrom(string output)
    {
        var normalized = output.ToLowerInvariant();
        foreach (var requirement in new[] { new ToolchainRequirement("jq", "1.7.1") })
        {
            var id = Regex.Escape(requirement.Id);
            var patterns = new[]
            {
                $@"\b{id}:\s*command not found\b",
                $@"\bcommand not found:\s*{id}\b",
                $@"\bspawn\s+{id}\s+enoent\b",
                $@"\benoent\b.{{0,80}}\b{id}\b",
                $@"'{id}'\s+is not recognized\b",
            };
            if (patterns.Any(p => Regex.IsMatch(normalized, p)))
            {
                return requirement;
            }
        }
        return null;
    }

    public Dictionary<string, string> Applying(
        PluginDependencyPlan plan,
        IReadOnlyDictionary<string, string> additions,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var result = new Dictionary<string, string>(
            (environment ?? _baseEnvironment)
                .Where(p => p.Value is not null)
                .Select(p => KeyValuePair.Create(p.Key, p.Value!)),
            StringComparer.OrdinalIgnoreCase);
        foreach (var pair in additions)
        {
            result[pair.Key] = pair.Value;
        }
        result["PATH"] = plan.SearchPath;
        return result;
    }

    private ResolvedPluginDependency? ResolvePnpm(RuntimeInstallation installation)
    {
        var bundledCandidates = new List<string>
        {
            Path.Combine(installation.Root, "node_modules", ".bin", "pnpm.cmd"),
            Path.Combine(installation.Root, "bin", "pnpm.cmd"),
            Path.Combine(Path.GetDirectoryName(installation.Executable)!, "pnpm.cmd"),
            Path.Combine(installation.Root, "node_modules", ".bin", "pnpm"),
            Path.Combine(installation.Root, "bin", "pnpm"),
            Path.Combine(Path.GetDirectoryName(installation.Executable)!, "pnpm"),
        };
        if (installation.NodeExecutable is { } node)
        {
            var nodeDir = Path.GetDirectoryName(node)!;
            bundledCandidates.Add(Path.Combine(nodeDir, "pnpm.cmd"));
            bundledCandidates.Add(Path.Combine(nodeDir, "pnpm"));
        }
        if (_privateToolchainRoot is not null)
        {
            bundledCandidates.Add(Path.Combine(_privateToolchainRoot, "bin", "pnpm.cmd"));
            bundledCandidates.Add(Path.Combine(_privateToolchainRoot, "pnpm", "bin", "pnpm.cmd"));
        }

        if (bundledCandidates.FirstOrDefault(File.Exists) is { } bundled)
        {
            return new ResolvedPluginDependency(
                "pnpm", bundled, PnpmVersionFor(bundled, installation),
                PluginDependencySource.Bundled);
        }

        if (ExecutableOnConfiguredPath("pnpm") is { } onPath)
        {
            return new ResolvedPluginDependency("pnpm", onPath, null, PluginDependencySource.User);
        }

        if (CommonUserPnpmCandidates().FirstOrDefault(File.Exists) is { } common)
        {
            return new ResolvedPluginDependency("pnpm", common, null, PluginDependencySource.User);
        }
        return null;
    }

    private ResolvedPluginDependency? ResolveSystemTool(string name, string preferredPath)
    {
        if (File.Exists(preferredPath))
        {
            return new ResolvedPluginDependency(name, preferredPath, null, PluginDependencySource.System);
        }
        if (ExecutableOnConfiguredPath(name) is { } executable)
        {
            return new ResolvedPluginDependency(name, executable, null, PluginDependencySource.User);
        }
        return null;
    }

    private string SearchPathFor(
        RuntimeInstallation installation,
        IReadOnlyList<ResolvedPluginDependency> dependencies)
    {
        var directories = dependencies
            .Select(d => Path.GetDirectoryName(d.Executable)!)
            .ToList();
        if (installation.NodeExecutable is { } node)
        {
            directories.Add(Path.GetDirectoryName(node)!);
        }
        if (_privateToolchainRoot is not null)
        {
            directories.AddRange(PrivateToolchainBinDirectories(_privateToolchainRoot));
        }
        directories.Add(Environment.SystemDirectory);
        directories.Add(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        if (_baseEnvironment.TryGetValue("PATH", out var configuredPath) && configuredPath is not null)
        {
            directories.AddRange(configuredPath.Split(Path.PathSeparator));
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return string.Join(Path.PathSeparator, directories
            .Where(d => !string.IsNullOrWhiteSpace(d) && seen.Add(d)));
    }

    private string? ExecutableOnConfiguredPath(string name)
    {
        if (!_baseEnvironment.TryGetValue("PATH", out var configuredPath) || configuredPath is null)
        {
            return null;
        }
        foreach (var component in configuredPath.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(component)) continue;
            foreach (var extension in new[] { ".cmd", ".exe", ".bat", "" })
            {
                var candidate = Path.Combine(component.Trim(), name + extension);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    private static IEnumerable<string> CommonUserPnpmCandidates()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        yield return Path.Combine(localAppData, "pnpm", "pnpm.cmd");
        yield return Path.Combine(roaming, "npm", "pnpm.cmd");
        yield return Path.Combine(home, ".local", "share", "pnpm", "pnpm.cmd");
        yield return Path.Combine(localAppData, "Volta", "bin", "pnpm.cmd");
        yield return Path.Combine(home, ".bun", "bin", "pnpm.cmd");
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return Path.Combine(programFiles, "nodejs", "pnpm.cmd");
    }

    private static string? PnpmVersionFor(string executable, RuntimeInstallation installation)
    {
        var candidates = new[]
        {
            Path.Combine(installation.Root, "node_modules", "pnpm", "package.json"),
            Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(executable)!)!, "pnpm", "package.json"),
        };
        foreach (var manifest in candidates)
        {
            if (!File.Exists(manifest)) continue;
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(manifest));
                if (document.RootElement.TryGetProperty("version", out var version))
                {
                    return version.GetString();
                }
            }
            catch { }
        }
        return null;
    }

    private ResolvedPluginDependency? ResolvePrivateTool(ToolchainRequirement requirement)
    {
        if (_privateToolchainRoot is null) return null;
        if (ToolchainCatalog.Bundled.ManifestFor(requirement) is not { } manifest) return null;
        var versionRoot = Path.Combine(_privateToolchainRoot, manifest.Id, manifest.Version);
        var executable = Path.Combine(versionRoot, "bin", manifest.ExecutableName);
        if (!File.Exists(executable)) return null;
        // The toolchain directory lives under user-writable AppData: only
        // trust an installed binary when both its manifest and its SHA-256
        // still match the pinned catalog entry.
        if (!InstalledManifestIsValid(versionRoot, manifest) ||
            !BinarySha256(executable).Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        return new ResolvedPluginDependency(
            manifest.Id, executable, manifest.Version, PluginDependencySource.Bundled);
    }

    private static bool InstalledManifestIsValid(string versionRoot, ToolchainManifest manifest)
    {
        var path = Path.Combine(versionRoot, "manifest.json");
        if (!File.Exists(path)) return false;
        try
        {
            var installed = JsonSerializer.Deserialize<ToolchainManifest>(File.ReadAllText(path));
            return installed == manifest;
        }
        catch
        {
            return false;
        }
    }

    public static string BinarySha256(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static IEnumerable<string> PrivateToolchainBinDirectories(string root)
    {
        yield return Path.Combine(root, "bin");
        if (!Directory.Exists(root)) yield break;
        foreach (var toolDirectory in Directory.GetDirectories(root))
        {
            foreach (var versionDirectory in Directory.GetDirectories(toolDirectory))
            {
                yield return Path.Combine(versionDirectory, "bin");
            }
        }
    }

    private static bool IsGitHostedSpec(string value)
    {
        var normalized = value.ToLowerInvariant();
        return normalized.StartsWith("github:")
            || normalized.StartsWith("git+")
            || normalized.StartsWith("git@")
            || normalized.Contains("github.com/")
            || normalized.Contains(".git#")
            || normalized.EndsWith(".git");
    }
}
