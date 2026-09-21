using System.Text.Json;

namespace HarnessLauncher.Services;

public static class Dsh1024Adapter
{
    public const string Version = "0.5.0";
    private static readonly string[] Files = ["client/client.js", "lib/routes.js", "lib/update.js"];

    public static bool Sync(string profileWeb, string resources)
    {
        var package = Path.Combine(profileWeb, "node_modules", "dsh1024");
        var manifestPath = Path.Combine(package, "package.json");
        if (!File.Exists(manifestPath)) return false;
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = document.RootElement;
        var name = root.TryGetProperty("name", out var nameValue) ? nameValue.GetString() : null;
        var version = root.TryGetProperty("version", out var versionValue) ? versionValue.GetString() : null;
        if (name != "dsh1024" || version != Version)
            throw new InvalidOperationException($"1024 Store {version ?? "unknown"} 尚未通过 Windows 适配验证，请使用内置的 {Version} 版本。");

        var pending = Files.Select(relative =>
        {
            var source = Path.Combine(resources, relative.Replace('/', Path.DirectorySeparatorChar));
            var target = Path.Combine(package, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(source)) throw new FileNotFoundException("缺少 1024 Store 适配资源", source);
            return (source, target, Bytes: File.ReadAllBytes(source));
        }).ToList();

        var changed = false;
        foreach (var item in pending)
        {
            if (File.Exists(item.target) && File.ReadAllBytes(item.target).SequenceEqual(item.Bytes)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(item.target)!);
            var temp = item.target + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllBytes(temp, item.Bytes);
            File.Move(temp, item.target, true);
            changed = true;
        }
        return changed;
    }
}
