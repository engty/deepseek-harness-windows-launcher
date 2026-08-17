using System.Text.Json;

namespace HarnessLauncher.Services;

public sealed record RuntimeInstallation(
    string Executable,
    string Root,
    string? Version,
    string? NodeExecutable)
{
    /// <summary>
    /// True when the dsh entry point is a .cmd/.bat shim that must be run via
    /// cmd.exe instead of being handed to node.exe as a JavaScript file.
    /// </summary>
    public bool IsShellShim =>
        Executable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
        Executable.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
}

public class RuntimeLocatorException : Exception
{
    public RuntimeLocatorException(string message) : base(message) { }

    public static RuntimeLocatorException NotFound() =>
        new("没有找到 DeepSeek Harness Runtime。");
    public static RuntimeLocatorException NotExecutable(string path) =>
        new($"Harness Runtime 不可执行：{path}");
}

/// <summary>
/// Windows port of RuntimeLocator. Search order: HARNESS_DSH_PATH,
/// HARNESS_RUNTIME_ROOT, runtime bundled next to the app, installed runtimes
/// under %LOCALAPPDATA%, then PATH. Windows dsh entry points are .cmd shims;
/// the bundled Node is node.exe under node/bin.
/// </summary>
public sealed class RuntimeLocator
{
    private readonly IReadOnlyDictionary<string, string?> _environment;
    private readonly AppPaths _paths;

    public RuntimeLocator(
        IReadOnlyDictionary<string, string?>? environment = null,
        AppPaths? paths = null)
    {
        _environment = environment ?? Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string?)e.Value);
        _paths = paths ?? new AppPaths();
    }

    public RuntimeInstallation Locate()
    {
        var candidates = new List<(string Executable, string Root)>();

        if (GetEnv("HARNESS_DSH_PATH") is { Length: > 0 } explicitPath)
        {
            var executable = Path.GetFullPath(explicitPath);
            candidates.Add((executable, Path.GetDirectoryName(Path.GetDirectoryName(executable)!)!));
        }

        if (GetEnv("HARNESS_RUNTIME_ROOT") is { Length: > 0 } runtimeRoot)
        {
            var root = Path.GetFullPath(runtimeRoot);
            candidates.AddRange(ExecutableCandidates(root).Select(e => (e, root)));
        }

        var bundled = Path.Combine(AppContext.BaseDirectory, "runtime");
        candidates.AddRange(ExecutableCandidates(bundled).Select(e => (e, bundled)));

        foreach (var root in ApplicationSupportCandidates())
        {
            candidates.AddRange(ExecutableCandidates(root).Select(e => (e, root)));
        }

        if (ExecutableOnPath("dsh") is { } pathExecutable)
        {
            candidates.Add((pathExecutable, Path.GetDirectoryName(pathExecutable)!));
        }

        foreach (var (executable, root) in candidates)
        {
            if (!File.Exists(executable)) continue;
            return new RuntimeInstallation(
                executable,
                root,
                VersionFor(executable, root),
                NodeExecutableFor(executable, root));
        }

        throw RuntimeLocatorException.NotFound();
    }

    public RuntimeInstallation LocateLastKnownGood()
    {
        var manifest = _paths.LastKnownGoodRuntimeManifest;
        if (!File.Exists(manifest)) throw RuntimeLocatorException.NotFound();
        using var document = JsonDocument.Parse(File.ReadAllText(manifest));
        if (!document.RootElement.TryGetProperty("runtimePath", out var runtimePathElement))
        {
            throw RuntimeLocatorException.NotFound();
        }
        var root = Path.GetFullPath(runtimePathElement.GetString()!);
        foreach (var executable in ExecutableCandidates(root))
        {
            if (!File.Exists(executable)) continue;
            return new RuntimeInstallation(
                executable,
                root,
                VersionFor(executable, root),
                NodeExecutableFor(executable, root));
        }
        throw RuntimeLocatorException.NotFound();
    }

    private string? GetEnv(string name) =>
        _environment.TryGetValue(name, out var value) ? value : null;

    private IEnumerable<string> ApplicationSupportCandidates()
    {
        var roots = new List<string>();
        var activeManifest = _paths.ActiveRuntimeManifest;
        if (File.Exists(activeManifest))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(activeManifest));
                if (document.RootElement.TryGetProperty("runtimePath", out var runtimePath) &&
                    runtimePath.GetString() is { Length: > 0 } path)
                {
                    roots.Add(Path.GetFullPath(path));
                }
                if (document.RootElement.TryGetProperty("runtimeId", out var runtimeId) &&
                    runtimeId.GetString() is { Length: > 0 } id)
                {
                    roots.Add(Path.Combine(_paths.Runtimes, id));
                }
            }
            catch { }
        }
        if (Directory.Exists(_paths.Runtimes))
        {
            roots.AddRange(Directory.GetDirectories(_paths.Runtimes));
        }
        return roots;
    }

    private static IEnumerable<string> ExecutableCandidates(string root)
    {
        // 优先直接定位真实 JS 入口：有内置 Node 时可完全绕过 cmd.exe，
        // 避免 cmd /c 的引号剥离怪癖在含空格路径下炸掉。
        yield return Path.Combine(root, "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");
        yield return Path.Combine(root, "bin", "dsh.cmd");
        yield return Path.Combine(root, "dsh.cmd");
        yield return Path.Combine(root, "node_modules", ".bin", "dsh.cmd");
        yield return Path.Combine(root, "bin", "dsh");
        yield return Path.Combine(root, "dsh");
        yield return Path.Combine(root, "node_modules", ".bin", "dsh");
    }

    private string? ExecutableOnPath(string name)
    {
        if (GetEnv("PATH") is not { } path) return null;
        var extensions = new[] { ".cmd", ".exe", ".bat", "" };
        foreach (var component in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(component)) continue;
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(component.Trim(), name + extension);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    private static string? VersionFor(string executable, string root)
    {
        var directories = new List<string>();
        var current = Path.GetDirectoryName(Path.GetFullPath(executable));
        for (var i = 0; i < 8 && current is not null; i++)
        {
            directories.Add(current);
            current = Path.GetDirectoryName(current);
        }
        directories.Add(Path.GetFullPath(root));

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in directories)
        {
            foreach (var manifestPath in new[]
            {
                Path.Combine(directory, "package.json"),
                Path.Combine(directory, "node_modules", "@deepseek-ai", "dsh", "package.json"),
            })
            {
                if (!seen.Add(manifestPath) || !File.Exists(manifestPath)) continue;
                try
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
                    if (document.RootElement.TryGetProperty("name", out var name) &&
                        name.GetString() == "@deepseek-ai/dsh" &&
                        document.RootElement.TryGetProperty("version", out var version) &&
                        version.GetString() is { Length: > 0 } value)
                    {
                        return value;
                    }
                }
                catch { }
            }
        }
        return null;
    }

    private static string? NodeExecutableFor(string executable, string root)
    {
        var directories = new List<string> { Path.GetFullPath(root) };
        var current = Path.GetDirectoryName(Path.GetFullPath(executable));
        for (var i = 0; i < 8 && current is not null; i++)
        {
            directories.Add(current);
            current = Path.GetDirectoryName(current);
        }

        foreach (var directory in directories)
        {
            foreach (var candidate in new[]
            {
                Path.Combine(directory, "node", "bin", "node.exe"),
                Path.Combine(directory, "bin", "node.exe"),
                Path.Combine(directory, "node.exe"),
                Path.Combine(directory, "node", "bin", "node"),
                Path.Combine(directory, "bin", "node"),
            })
            {
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }
}
