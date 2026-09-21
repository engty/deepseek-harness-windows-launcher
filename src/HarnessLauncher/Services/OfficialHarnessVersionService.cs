using System.Net.Http.Headers;
using System.Text.Json;
using HarnessLauncher.Models;

namespace HarnessLauncher.Services;

public sealed class OfficialHarnessVersionException : Exception
{
    public OfficialHarnessVersionException(string message) : base(message) { }
}

/// <summary>
/// Uses the official npm registry as the default Runtime version signal. A
/// controlled manifest remains available through HARNESS_UPDATE_MANIFEST_URL
/// for reproducible release channels.
/// </summary>
public sealed class OfficialHarnessVersionService
{
    private static readonly Uri DefaultEndpoint = new(
        "https://registry.npmjs.org/@deepseek-ai%2Fdsh");
    private static readonly Uri PackagePage = new(
        "https://www.npmjs.com/package/@deepseek-ai/dsh");

    private readonly Uri _endpoint;
    private readonly HttpClient _client;

    public OfficialHarnessVersionService(
        IReadOnlyDictionary<string, string?>? environment = null,
        HttpMessageHandler? handler = null)
    {
        environment ??= Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string?)e.Value);
        _endpoint = environment.TryGetValue("HARNESS_OFFICIAL_VERSION_URL", out var raw) &&
                    Uri.TryCreate(raw, UriKind.Absolute, out var configured)
            ? configured!
            : DefaultEndpoint;
        _client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
    }

    public async Task<OfficialHarnessVersionResult> CheckAsync(
        bool includePrereleases = true,
        CancellationToken cancellationToken = default)
    {
        if (_endpoint.Scheme != Uri.UriSchemeHttps)
            throw new OfficialHarnessVersionException("官方 Harness 版本查询地址必须使用 HTTPS。");

        using var request = new HttpRequestMessage(HttpMethod.Get, _endpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("DeepSeek-Harness-Windows-Launcher");
        using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new OfficialHarnessVersionException("官方 Harness 版本服务返回了无效响应。");

        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var versions = new List<StrictSemanticVersion>();
        if (document.RootElement.TryGetProperty("versions", out var published) &&
            published.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in published.EnumerateObject())
            {
                if (StrictSemanticVersion.TryParse(property.Name, out var parsed))
                    versions.Add(parsed!);
            }
        }
        if (document.RootElement.TryGetProperty("version", out var single) &&
            single.ValueKind == JsonValueKind.String &&
            StrictSemanticVersion.TryParse(single.GetString()!, out var one))
        {
            versions.Add(one!);
        }

        if (!includePrereleases)
            versions = versions.Where(v => v.Prerelease.Count == 0).ToList();
        if (versions.Count == 0)
            throw new OfficialHarnessVersionException("官方 Harness 暂无可识别版本。");
        var latest = versions.OrderByDescending(version => version).FirstOrDefault()
            ?? throw new OfficialHarnessVersionException("官方 Harness 暂无可识别版本。");
        return new OfficialHarnessVersionResult(latest.ToString(), PackagePage);
    }
}
