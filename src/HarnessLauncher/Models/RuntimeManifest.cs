using System.Text.Json.Serialization;

namespace HarnessLauncher.Models;

public sealed class RuntimeManifest
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; }
    [JsonPropertyName("runtimeId")] public string RuntimeId { get; set; } = "";
    [JsonPropertyName("channel")] public string Channel { get; set; } = "";
    [JsonPropertyName("architecture")] public string Architecture { get; set; } = "";
    [JsonPropertyName("harness")] public HarnessVersion Harness { get; set; } = new();
    [JsonPropertyName("nodeVersion")] public string NodeVersion { get; set; } = "";
    [JsonPropertyName("testedPlugins")] public Dictionary<string, TestedPlugin>? TestedPlugins { get; set; }
    [JsonPropertyName("minShellVersion")] public string MinShellVersion { get; set; } = "";
    [JsonPropertyName("dataFormat")] public string DataFormat { get; set; } = "";
    [JsonPropertyName("artifact")] public ArtifactInfo Artifact { get; set; } = new();
    [JsonPropertyName("releaseNotesUrl")] public Uri? ReleaseNotesUrl { get; set; }
    [JsonPropertyName("publishedAt")] public DateTime? PublishedAt { get; set; }

    public sealed class HarnessVersion
    {
        [JsonPropertyName("package")] public string Package { get; set; } = "";
        [JsonPropertyName("version")] public string Version { get; set; } = "";
        [JsonPropertyName("commit")] public string Commit { get; set; } = "";
    }

    public sealed class TestedPlugin
    {
        [JsonPropertyName("versions")] public List<string> Versions { get; set; } = new();
        [JsonPropertyName("status")] public string Status { get; set; } = "";
    }

    public sealed class ArtifactInfo
    {
        [JsonPropertyName("url")] public Uri Url { get; set; } = new("https://localhost/");
        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    }

    public bool HasSafeRuntimeId =>
        !string.IsNullOrEmpty(RuntimeId) &&
        System.Text.RegularExpressions.Regex.IsMatch(RuntimeId, @"^[A-Za-z0-9._-]+$");
}

public class RuntimeManifestException : Exception
{
    public RuntimeManifestException(string message) : base(message) { }

    public static readonly RuntimeManifestException FeedNotConfigured =
        new("当前 App 未配置 Harness Runtime 更新源；这只影响底层 Runtime 更新，不影响 App 使用。");
    public static readonly RuntimeManifestException InvalidURL =
        new("更新 feed 或 artifact URL 必须是 HTTPS。");
    public static readonly RuntimeManifestException InvalidJSON =
        new("Runtime manifest 不是有效 JSON。");
    public static RuntimeManifestException UnsupportedSchema(int version) =>
        new($"不支持的 Runtime manifest schema：{version}");
    public static RuntimeManifestException UnsupportedArchitecture(string architecture) =>
        new($"Runtime 架构不匹配：{architecture}");
    public static RuntimeManifestException IncompatibleShellVersion(string required, string current) =>
        new($"当前 App Shell 版本 {current} 低于 Runtime 要求的最低版本 {required}。");
    public static RuntimeManifestException InvalidMinShellVersion(string value) =>
        new($"Runtime manifest 的 minShellVersion 不是有效的 SemVer 版本号：{value}");
    public static readonly RuntimeManifestException InvalidRuntimeId =
        new("Runtime ID 不是安全的目录名称。");
    public static readonly RuntimeManifestException InvalidArtifactHash =
        new("Runtime artifact 必须提供 64 位 SHA-256。");
    public static readonly RuntimeManifestException InvalidArtifactSize =
        new("Runtime artifact 大小无效。");
    public static readonly RuntimeManifestException InvalidResponse =
        new("更新服务返回了无效 HTTP 响应。");
    public static readonly RuntimeManifestException ArtifactHashMismatch =
        new("Runtime artifact SHA-256 校验失败。");
    public static readonly RuntimeManifestException ArtifactSizeMismatch =
        new("Runtime artifact 大小校验失败。");

    public bool IsFeedNotConfigured => ReferenceEquals(this, FeedNotConfigured) || Message == FeedNotConfigured.Message;
}
