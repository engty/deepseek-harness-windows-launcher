using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using HarnessLauncher.Models;

namespace HarnessLauncher.Services;

public sealed record AppUpdateResult(
    string CurrentVersion,
    string LatestVersion,
    Uri ReleaseUrl,
    string? ReleaseName,
    DateTime? PublishedAt)
{
    public bool IsUpdateAvailable
    {
        get
        {
            if (!StrictSemanticVersion.TryParse(LatestVersion, out var latest)) return false;
            if (!StrictSemanticVersion.TryParse(CurrentVersion, out var current)) return true;
            return latest! > current!;
        }
    }
}

public class AppUpdateException : Exception
{
    public AppUpdateException(string message) : base(message) { }

    public static readonly AppUpdateException InvalidFeedUrl =
        new("DeepSeek Harness App 更新源地址无效。");
    public static readonly AppUpdateException InvalidResponse =
        new("GitHub App 更新服务返回了无效响应。");
    public static readonly AppUpdateException InvalidJSON =
        new("GitHub App 更新信息不是有效 JSON。");
    public static readonly AppUpdateException InvalidReleaseUrl =
        new("GitHub Release 下载地址不是受信任的 HTTPS 地址。");
    public static readonly AppUpdateException InvalidReleaseVersion =
        new("GitHub Release 没有可识别的版本号。");
}

/// <summary>
/// Windows port of AppUpdateService. The default feed points at the Windows
/// launcher's own GitHub releases instead of the macOS repository.
/// </summary>
public sealed class AppUpdateService
{
    private static readonly Uri DefaultFeedUrl = new(
        "https://api.github.com/repos/engty/deepseek-harness-windows-launcher/releases/latest");

    private readonly Uri? _feedUrl;
    private readonly HttpClient _client;

    public AppUpdateService(IReadOnlyDictionary<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string?)e.Value);

        var rawUrl = environment.TryGetValue("HARNESS_APP_UPDATE_URL", out var raw) ? raw : null;
        _feedUrl = !string.IsNullOrEmpty(rawUrl) && Uri.TryCreate(rawUrl, UriKind.Absolute, out var parsed)
            ? parsed
            : DefaultFeedUrl;

        _client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
    }

    public async Task<AppUpdateResult> CheckAsync(string currentVersion)
    {
        if (_feedUrl is null || _feedUrl.Scheme != "https")
        {
            throw AppUpdateException.InvalidFeedUrl;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, _feedUrl);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd("DeepSeek-Harness-Windows-Launcher");

        using var response = await _client.SendAsync(request).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw AppUpdateException.InvalidResponse;
        }

        GitHubRelease? release;
        try
        {
            release = await JsonSerializer.DeserializeAsync<GitHubRelease>(
                await response.Content.ReadAsStreamAsync().ConfigureAwait(false)).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            throw AppUpdateException.InvalidJSON;
        }
        if (release is null) throw AppUpdateException.InvalidJSON;

        if (!StrictSemanticVersion.TryParse(release.TagName, out var latestVersion))
        {
            throw AppUpdateException.InvalidReleaseVersion;
        }
        if (release.HtmlUrl.Scheme != "https" ||
            !release.HtmlUrl.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            throw AppUpdateException.InvalidReleaseUrl;
        }

        return new AppUpdateResult(
            currentVersion,
            latestVersion!.ToString(),
            release.HtmlUrl,
            release.Name,
            release.PublishedAt);
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string TagName { get; set; } = "";
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("html_url")] public Uri HtmlUrl { get; set; } = new("https://github.com/");
        [JsonPropertyName("published_at")] public DateTime? PublishedAt { get; set; }
    }
}
