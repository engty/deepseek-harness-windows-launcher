using System.Text.Json;
using System.Text.Json.Serialization;
using HarnessLauncher.Support;

namespace HarnessLauncher.Services;

public sealed class PluginPackageMetadata
{
    [JsonPropertyName("name")] public required string Name { get; set; }
    [JsonPropertyName("version")] public required string Version { get; set; }
    [JsonPropertyName("requestedSpec")] public string? RequestedSpec { get; set; }
    [JsonPropertyName("license")] public string? License { get; set; }
    [JsonPropertyName("repository")] public string? Repository { get; set; }
    [JsonPropertyName("distributionURL")] public string? DistributionUrl { get; set; }
    [JsonPropertyName("lifecycleScripts")] public List<string> LifecycleScripts { get; set; } = new();
}

public sealed class PluginMetadata
{
    [JsonPropertyName("capturedAt")] public DateTime CapturedAt { get; set; }
    [JsonPropertyName("requestedArguments")] public List<string> RequestedArguments { get; set; } = new();
    [JsonPropertyName("packages")] public List<PluginPackageMetadata> Packages { get; set; } = new();
}

public class PluginMetadataException : Exception
{
    public PluginMetadataException(string message) : base(message) { }

    public static readonly PluginMetadataException InvalidProfile =
        new("无法从 staging profile 读取插件元数据。");
    public static PluginMetadataException WriteFailed(string message) =>
        new($"无法保存插件元数据：{message}");
}

/// <summary>Direct port of the macOS PluginMetadataStore.</summary>
public sealed class PluginMetadataStore
{
    public PluginMetadata Collect(string profilePath, IReadOnlyList<string> arguments)
    {
        var manifestPath = Path.Combine(profilePath, "package.json");
        JsonElement manifest;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            manifest = document.RootElement.Clone();
        }
        catch
        {
            throw PluginMetadataException.InvalidProfile;
        }

        var bundles = NestedStringArray(manifest, "dsh", "profile", "bundles");
        var packages = new List<PluginPackageMetadata>();
        foreach (var packageName in bundles)
        {
            if (!TryGetProperty(manifest, "dependencies", out var dependencies) ||
                !TryGetProperty(dependencies, packageName, out var specElement) ||
                specElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }
            var packagePath = Path.GetFullPath(
                Path.Combine(profilePath, "node_modules", packageName));
            var packageManifestPath = Path.Combine(packagePath, "package.json");
            if (!File.Exists(packageManifestPath)) continue;
            JsonElement packageManifest;
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(packageManifestPath));
                packageManifest = document.RootElement.Clone();
            }
            catch { continue; }
            if (!TryGetProperty(packageManifest, "version", out var versionElement) ||
                versionElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var lifecycleNames = new List<string>();
            if (TryGetProperty(packageManifest, "scripts", out var scripts))
            {
                foreach (var name in new[]
                    { "preinstall", "install", "postinstall", "prepare", "prepublish", "postpublish" })
                {
                    if (TryGetProperty(scripts, name, out _)) lifecycleNames.Add(name);
                }
            }
            string? distributionUrl = null;
            if (TryGetProperty(packageManifest, "dist", out var dist) &&
                TryGetProperty(dist, "tarball", out var tarball) &&
                tarball.ValueKind == JsonValueKind.String)
            {
                distributionUrl = SensitiveDataRedactor.Redact(tarball.GetString()!);
            }

            packages.Add(new PluginPackageMetadata
            {
                Name = TryGetProperty(packageManifest, "name", out var nameElement) &&
                    nameElement.ValueKind == JsonValueKind.String
                        ? nameElement.GetString()! : packageName,
                Version = versionElement.GetString()!,
                RequestedSpec = SensitiveDataRedactor.Redact(specElement.GetString()!),
                License = LicenseValue(packageManifest),
                Repository = RepositoryValue(packageManifest),
                DistributionUrl = distributionUrl,
                LifecycleScripts = lifecycleNames,
            });
        }

        return new PluginMetadata
        {
            CapturedAt = DateTime.Now,
            RequestedArguments = arguments.Select(SensitiveDataRedactor.Redact).ToList(),
            Packages = packages,
        };
    }

    public void Write(PluginMetadata metadata, string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
        catch (Exception error)
        {
            throw PluginMetadataException.WriteFailed(error.Message);
        }
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value);
    }

    private static IReadOnlyList<string> NestedStringArray(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var key in path)
        {
            if (!TryGetProperty(current, key, out current)) return Array.Empty<string>();
        }
        if (current.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
        return current.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()!)
            .ToList();
    }

    private static string? LicenseValue(JsonElement manifest)
    {
        if (!TryGetProperty(manifest, "license", out var license)) return null;
        if (license.ValueKind == JsonValueKind.String) return license.GetString();
        if (license.ValueKind == JsonValueKind.Object &&
            TryGetProperty(license, "type", out var type) &&
            type.ValueKind == JsonValueKind.String)
        {
            return type.GetString();
        }
        return null;
    }

    private static string? RepositoryValue(JsonElement manifest)
    {
        if (!TryGetProperty(manifest, "repository", out var repository)) return null;
        if (repository.ValueKind == JsonValueKind.String)
        {
            return SensitiveDataRedactor.Redact(repository.GetString()!);
        }
        if (repository.ValueKind == JsonValueKind.Object)
        {
            var type = TryGetProperty(repository, "type", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString()! : "";
            var url = TryGetProperty(repository, "url", out var u) && u.ValueKind == JsonValueKind.String
                ? u.GetString()! : "";
            var result = string.Join(' ', new[] { type, url }.Where(s => s.Length > 0));
            return result.Length == 0 ? null : SensitiveDataRedactor.Redact(result);
        }
        return null;
    }
}
