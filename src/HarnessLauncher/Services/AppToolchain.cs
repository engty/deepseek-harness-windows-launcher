using System.Net.Http;
using System.Text.Json.Serialization;
using HarnessLauncher.Support;

namespace HarnessLauncher.Services;

public sealed record ToolchainRequirement(string Id, string? Version = null);

public enum ToolchainArtifactKind
{
    Raw,
}

/// <summary>
/// Pinned, allow-listed tool artifact. JSON field names are camelCase to stay
/// byte-compatible with manifests written by the macOS launcher.
/// </summary>
public sealed record ToolchainManifest
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("version")] public required string Version { get; init; }
    [JsonPropertyName("architecture")] public required string Architecture { get; init; }
    [JsonPropertyName("executableName")] public required string ExecutableName { get; init; }
    [JsonPropertyName("artifactURL")] public required Uri ArtifactUrl { get; init; }
    [JsonPropertyName("artifactSize")] public required long ArtifactSize { get; init; }
    [JsonPropertyName("sha256")] public required string Sha256 { get; init; }
    [JsonPropertyName("artifactKind")] public string ArtifactKind { get; init; } = "raw";
    [JsonPropertyName("sourceURL")] public required Uri SourceUrl { get; init; }
    [JsonPropertyName("licenseURL")] public required Uri LicenseUrl { get; init; }
    [JsonPropertyName("maxBytes")] public required long MaxBytes { get; init; }

    public ToolchainRequirement Requirement => new(Id, Version);
}

public sealed record ToolchainInstallPlan(ToolchainManifest Manifest, string Destination)
{
    public string Executable => Path.Combine(Destination, "bin", Manifest.ExecutableName);

    public string ConfirmationText => $"""
        • {Manifest.Id} {Manifest.Version}（App 私有依赖）
          来源：{Manifest.SourceUrl}
          下载：{Manifest.ArtifactSize} bytes，SHA-256：{Manifest.Sha256}
          目录：{Destination}
          只对 DeepSeek Harness 子进程生效，不写入系统目录或全局 PATH。
        """;
}

public sealed class ToolchainCatalog
{
    private readonly Dictionary<string, ToolchainManifest> _manifests;

    public ToolchainCatalog(IReadOnlyList<ToolchainManifest> manifests)
    {
        _manifests = manifests.ToDictionary(m => $"{m.Id}:{m.Version}:{m.Architecture}");
    }

    public ToolchainManifest? ManifestFor(ToolchainRequirement requirement)
    {
        if (requirement.Version is { } version &&
            _manifests.TryGetValue($"{requirement.Id}:{version}:{CurrentArchitecture}", out var exact))
        {
            return exact;
        }
        return _manifests.Values
            .Where(m => m.Id == requirement.Id && m.Architecture == CurrentArchitecture)
            .OrderByDescending(m => m.Version, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    public static string CurrentArchitecture =>
        System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture ==
            System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64";

    public static readonly ToolchainCatalog Bundled = new(new ToolchainManifest[]
    {
        new()
        {
            Id = "jq",
            Version = "1.7.1",
            Architecture = "x64",
            ExecutableName = "jq.exe",
            ArtifactUrl = new Uri("https://github.com/jqlang/jq/releases/download/jq-1.7.1/jq-windows-amd64.exe"),
            ArtifactSize = 985_088,
            Sha256 = "7451fbbf37feffb9bf262bd97c54f0da558c63f0748e64152dd87b0a07b6d6ab",
            SourceUrl = new Uri("https://github.com/jqlang/jq"),
            LicenseUrl = new Uri("https://github.com/jqlang/jq/blob/jq-1.7.1/COPYING"),
            MaxBytes = 2_000_000,
        },
    });
}

public class ToolchainInstallerException : Exception
{
    public ToolchainInstallerException(string message) : base(message) { }

    public static ToolchainInstallerException UnsupportedRequirement(string id) =>
        new($"没有受控清单允许自动安装依赖：{id}。");
    public static readonly ToolchainInstallerException InvalidUrl =
        new("依赖下载地址不是 HTTPS。");
    public static readonly ToolchainInstallerException InvalidResponse =
        new("依赖下载服务器返回了无效响应。");
    public static readonly ToolchainInstallerException DownloadTooLarge =
        new("依赖下载超过允许的大小上限。");
    public static ToolchainInstallerException ArtifactSizeMismatch(long expected, long actual) =>
        new($"依赖大小校验失败（预期 {expected}，实际 {actual}）。");
    public static readonly ToolchainInstallerException ArtifactHashMismatch =
        new("依赖 SHA-256 校验失败，未安装任何文件。");
    public static readonly ToolchainInstallerException UnsupportedArtifact =
        new("依赖归档格式不在受控清单范围内。");
    public static readonly ToolchainInstallerException UnsafeExecutableName =
        new("依赖可执行文件名不安全。");
    public static ToolchainInstallerException InstallationFailed(string message) =>
        new($"依赖安装失败：{message}");
}

/// <summary>
/// Downloads a pinned tool into the app-private toolchain directory after
/// HTTPS, size and SHA-256 validation. Runs entirely under %LOCALAPPDATA% —
/// no admin rights required.
/// </summary>
public sealed class ToolchainInstaller
{
    private readonly ToolchainCatalog _catalog;
    private readonly HttpClient _client;

    public ToolchainInstaller(ToolchainCatalog? catalog = null, HttpClient? client = null)
    {
        _catalog = catalog ?? ToolchainCatalog.Bundled;
        _client = client ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
    }

    public async Task<ToolchainInstallPlan> InstallAsync(
        ToolchainRequirement requirement,
        AppPaths paths,
        Action<long, long>? progress = null)
    {
        if (_catalog.ManifestFor(requirement) is not { } manifest)
        {
            throw ToolchainInstallerException.UnsupportedRequirement(requirement.Id);
        }
        if (manifest.ArtifactUrl.Scheme != "https" ||
            manifest.SourceUrl.Scheme != "https" ||
            manifest.LicenseUrl.Scheme != "https")
        {
            throw ToolchainInstallerException.InvalidUrl;
        }
        if (!IsSafeExecutableName(manifest.ExecutableName))
        {
            throw ToolchainInstallerException.UnsafeExecutableName;
        }

        paths.Prepare();
        SweepStaleStaging(paths);

        var destination = Path.Combine(paths.Toolchain, manifest.Id, manifest.Version);
        var plan = new ToolchainInstallPlan(manifest, destination);
        if (File.Exists(plan.Executable) && IsInstalledManifestValid(plan))
        {
            return plan;
        }
        if (Directory.Exists(destination))
        {
            // The directory exists but is not trustworthy (corrupt manifest
            // or tampered binary). Quarantine it and install fresh.
            var quarantined = Path.Combine(paths.Toolchain, $".invalid-{Guid.NewGuid():N}");
            try { Directory.Move(destination, quarantined); } catch { }
            AppLogger.Log(AppLogger.Level.Error, "plugins",
                $"Quarantined inconsistent private toolchain directory: {manifest.Id} {manifest.Version}");
        }

        var data = await _client.GetByteArrayAsync(manifest.ArtifactUrl).ConfigureAwait(false);
        progress?.Invoke(0, manifest.ArtifactSize);
        if (data.LongLength > manifest.MaxBytes) throw ToolchainInstallerException.DownloadTooLarge;
        if (data.LongLength != manifest.ArtifactSize)
        {
            throw ToolchainInstallerException.ArtifactSizeMismatch(manifest.ArtifactSize, data.LongLength);
        }
        var digest = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(data)).ToLowerInvariant();
        if (!digest.Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw ToolchainInstallerException.ArtifactHashMismatch;
        }
        progress?.Invoke(data.LongLength, manifest.ArtifactSize);

        var staging = Path.Combine(paths.Toolchain, ".staging", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(staging, "bin"));
            if (manifest.ArtifactKind != "raw")
            {
                throw ToolchainInstallerException.UnsupportedArtifact;
            }
            var executable = Path.Combine(staging, "bin", manifest.ExecutableName);
            await File.WriteAllBytesAsync(executable, data).ConfigureAwait(false);
            var manifestJson = System.Text.Json.JsonSerializer.Serialize(manifest);
            await File.WriteAllTextAsync(
                Path.Combine(staging, "manifest.json"), manifestJson).ConfigureAwait(false);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (Directory.Exists(destination))
            {
                throw ToolchainInstallerException.InstallationFailed("目标版本目录在准备期间被占用，请重试。");
            }
            Directory.Move(staging, destination);
        }
        catch
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch { }
            throw;
        }
        return plan;
    }

    private static bool IsInstalledManifestValid(ToolchainInstallPlan plan)
    {
        var path = Path.Combine(plan.Destination, "manifest.json");
        if (!File.Exists(path)) return false;
        try
        {
            var installed = System.Text.Json.JsonSerializer.Deserialize<ToolchainManifest>(
                File.ReadAllText(path));
            return installed == plan.Manifest;
        }
        catch
        {
            return false;
        }
    }

    private static void SweepStaleStaging(AppPaths paths)
    {
        var stagingRoot = Path.Combine(paths.Toolchain, ".staging");
        if (!Directory.Exists(stagingRoot)) return;
        foreach (var entry in Directory.GetDirectories(stagingRoot))
        {
            try { Directory.Delete(entry, recursive: true); } catch { }
        }
    }

    private static bool IsSafeExecutableName(string name) =>
        !string.IsNullOrEmpty(name) &&
        name == Path.GetFileName(name) &&
        !name.Contains('/') && !name.Contains('\\');
}
