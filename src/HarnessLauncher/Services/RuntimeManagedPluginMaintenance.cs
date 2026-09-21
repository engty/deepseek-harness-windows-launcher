using System.Text.Json;

namespace HarnessLauncher.Services;

public static class RuntimeManagedPluginMaintenance
{
    private static readonly (string Id, string Spec)[] Managed =
    [
        ("dsh-mnemon", "dsh-mnemon@latest"),
    ];

    public static IReadOnlyList<string> UpdateArguments(string profileWeb)
    {
        var manifest = Path.Combine(profileWeb, "package.json");
        if (!File.Exists(manifest)) return Array.Empty<string>();
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifest));
            var root = document.RootElement;
            var installed = root.TryGetProperty("dependencies", out var dependencies) &&
                            dependencies.ValueKind == JsonValueKind.Object
                ? dependencies.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
            var bundles = root.TryGetProperty("dsh", out var dsh) &&
                          dsh.TryGetProperty("profile", out var profile) &&
                          profile.TryGetProperty("bundles", out var bundleArray) &&
                          bundleArray.ValueKind == JsonValueKind.Array
                ? bundleArray.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String).Select(value => value.GetString()!).ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
            return Managed.Where(item => installed.Contains(item.Id) && bundles.Contains(item.Id))
                .Select(item => item.Spec).ToList();
        }
        catch { return Array.Empty<string>(); }
    }
}
