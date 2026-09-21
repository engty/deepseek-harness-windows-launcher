namespace HarnessLauncher.Services;

/// <summary>
/// Builds the environment inherited by Harness and plugin child processes.
/// Every writable package/cache location is redirected to App-owned paths;
/// the user's machine-wide PATH and npm/pnpm configuration are untouched.
/// </summary>
public static class PluginExecutionEnvironment
{
    public static Dictionary<string, string> Create(
        RuntimeInstallation installation,
        AppPaths paths,
        string dshHome,
        string? searchPath = null,
        IReadOnlyDictionary<string, string>? additions = null)
    {
        paths.Prepare();
        var npmPrefix = Path.Combine(paths.Toolchain, "npm-global");
        var pnpmHome = Path.Combine(paths.Toolchain, "pnpm-global");
        var pnpmStore = Path.Combine(paths.Caches, "pnpm", "store");
        var npmCache = Path.Combine(paths.Caches, "npm");
        foreach (var directory in new[] { npmPrefix, pnpmHome, pnpmStore, npmCache })
        {
            Directory.CreateDirectory(directory);
        }

        var environment = Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .Where(entry => entry.Value is not null)
            .ToDictionary(entry => (string)entry.Key, entry => (string)entry.Value!,
                StringComparer.OrdinalIgnoreCase);
        if (additions is not null)
        {
            foreach (var pair in additions) environment[pair.Key] = pair.Value;
        }

        var directories = (searchPath ?? new PluginDependencyService(
                environment.ToDictionary(pair => pair.Key, pair => (string?)pair.Value), paths.Toolchain)
            .RuntimeSearchPath(installation))
            .Split(Path.PathSeparator)
            .Where(directory => !string.IsNullOrWhiteSpace(directory))
            .ToList();
        var insertion = directories.FindIndex(directory =>
            directory.Equals(Environment.SystemDirectory, StringComparison.OrdinalIgnoreCase));
        if (insertion < 0) insertion = directories.Count;
        directories.InsertRange(insertion, new[]
        {
            Path.Combine(dshHome, "profiles", "web", "node_modules", ".bin"),
            Path.Combine(npmPrefix, "bin"),
            pnpmHome,
        });

        environment["PATH"] = string.Join(Path.PathSeparator,
            directories.Distinct(StringComparer.OrdinalIgnoreCase));
        environment["DSH_HOME"] = dshHome;
        environment["DSH_LAUNCHER"] = "DeepSeekHarness";
        environment["HARNESS_NODE_PATH"] = installation.NodeExecutable ?? "";
        environment["HARNESS_DSH_PATH"] = installation.Executable;
        environment["MNEMON_DATA_DIR"] = Path.Combine(dshHome, "mnemon");
        environment["PNPM_HOME"] = pnpmHome;
        environment["PNPM_STORE_DIR"] = pnpmStore;
        environment["npm_config_prefix"] = npmPrefix;
        environment["NPM_CONFIG_PREFIX"] = npmPrefix;
        environment["npm_config_cache"] = npmCache;
        environment["NPM_CONFIG_CACHE"] = npmCache;
        environment["npm_config_store_dir"] = pnpmStore;
        environment["NPM_CONFIG_STORE_DIR"] = pnpmStore;
        environment["pnpm_config_store_dir"] = pnpmStore;
        environment["PNPM_CONFIG_STORE_DIR"] = pnpmStore;
        return environment;
    }
}
