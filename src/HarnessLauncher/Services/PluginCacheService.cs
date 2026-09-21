using HarnessLauncher.Models;
using HarnessLauncher.Support;

namespace HarnessLauncher.Services;

public sealed class PluginCacheService
{
    public IReadOnlyList<PluginCacheEntry> Entries(IReadOnlyList<HarnessPlugin> plugins, AppPaths paths)
    {
        var entries = plugins.Select(plugin => new PluginCacheEntry
        {
            Id = $"plugin:{plugin.Id}",
            Title = plugin.Name,
            Kind = "plugin",
            SizeBytes = SizeOf(new[]
            {
                Path.Combine(paths.Caches, "plugin-cache", plugin.Id),
                Path.Combine(paths.DshHome, "launcher", "plugin-cache", plugin.Id),
            }),
        }).Where(entry => entry.SizeBytes > 0).ToList();

        var pnpm = PnpmStoreCandidates(paths).ToList();
        var pnpmSize = SizeOf(pnpm);
        if (pnpmSize > 0)
            entries.Add(new PluginCacheEntry { Id = "shared-pnpm", Title = "共享 pnpm 缓存", Kind = "shared-pnpm", SizeBytes = pnpmSize });
        var stagingSize = SizeOf(new[] { paths.PluginStaging });
        if (stagingSize > 0)
            entries.Add(new PluginCacheEntry { Id = "staging", Title = "安装暂存缓存", Kind = "staging", SizeBytes = stagingSize });
        return entries;
    }

    public async Task<PluginCacheCleanupReport> CleanupAsync(
        IReadOnlyList<PluginCacheEntry> entries,
        AppPaths paths,
        RuntimeInstallation? installation)
    {
        long removed = 0;
        foreach (var entry in entries)
        {
            if (entry.Kind == "plugin")
            {
                var id = entry.Id["plugin:".Length..];
                var pathsToRemove = new[]
                {
                    Path.Combine(paths.Caches, "plugin-cache", id),
                    Path.Combine(paths.DshHome, "launcher", "plugin-cache", id),
                };
                removed += SizeOf(pathsToRemove);
                Remove(pathsToRemove);
            }
            else if (entry.Kind == "staging")
            {
                removed += SizeOf(new[] { paths.PluginStaging });
                RemoveContents(paths.PluginStaging);
            }
        }

        if (!entries.Any(entry => entry.Kind == "shared-pnpm") || installation is null)
            return new PluginCacheCleanupReport(removed, 0, !entries.Any(entry => entry.Kind == "shared-pnpm"));

        var before = SizeOf(PnpmStoreCandidates(paths));
        var succeeded = await PrunePnpmStoreAsync(installation, paths).ConfigureAwait(false);
        var after = SizeOf(PnpmStoreCandidates(paths));
        return new PluginCacheCleanupReport(removed, Math.Max(0, before - after), succeeded);
    }

    private static async Task<bool> PrunePnpmStoreAsync(RuntimeInstallation installation, AppPaths paths)
    {
        var pnpm = new[]
        {
            Path.Combine(installation.Root, "node_modules", ".bin", "pnpm.cmd"),
            Path.Combine(installation.Root, "node_modules", "pnpm", "bin", "pnpm.cjs"),
        }.FirstOrDefault(File.Exists);
        if (pnpm is null) return false;
        var executable = pnpm.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
            ? "cmd.exe" : installation.NodeExecutable ?? "node.exe";
        var args = pnpm.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
            ? new[] { "/d", "/c", pnpm, "store", "prune" }
            : new[] { pnpm, "store", "prune" };
        var environment = PluginExecutionEnvironment.Create(installation, paths, paths.DshHome);
        try
        {
            var result = await SubprocessRunner.RunAsync(executable, args, environment, paths.ProfileWeb, TimeSpan.FromMinutes(3));
            return result.Status == 0;
        }
        catch (Exception error)
        {
            AppLogger.Log(AppLogger.Level.Error, "plugins", $"pnpm store prune failed: {error.Message}");
            return false;
        }
    }

    private static IEnumerable<string> PnpmStoreCandidates(AppPaths paths)
    {
        return new[]
        {
            Path.Combine(paths.Caches, "pnpm", "store"),
            Path.Combine(paths.Caches, "pnpm", "metadata"),
        }.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static long SizeOf(IEnumerable<string> values) => values.Sum(SizeOf);

    private static long SizeOf(string path)
    {
        if (File.Exists(path)) return new FileInfo(path).Length;
        if (!Directory.Exists(path)) return 0;
        try { return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length); }
        catch { return 0; }
    }

    private static void Remove(IEnumerable<string> values)
    {
        foreach (var value in values)
        {
            try { if (Directory.Exists(value)) Directory.Delete(value, true); else if (File.Exists(value)) File.Delete(value); }
            catch { }
        }
    }

    private static void RemoveContents(string directory)
    {
        if (!Directory.Exists(directory)) return;
        Remove(Directory.EnumerateFileSystemEntries(directory));
    }
}
