using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using HarnessLauncher.Models;

namespace HarnessLauncher.Services;

public class DeepSeekBalanceException : Exception
{
    public DeepSeekBalanceException(string message) : base(message) { }

    public static readonly DeepSeekBalanceException InvalidBaseUrl =
        new("DeepSeek API 地址无效，必须使用 HTTPS。");
    public static readonly DeepSeekBalanceException InvalidResponse =
        new("DeepSeek 余额接口返回了无效响应。");
    public static readonly DeepSeekBalanceException Unauthorized =
        new("DeepSeek API Key 无效或已失效。");
    public static DeepSeekBalanceException Server(int statusCode) =>
        new($"DeepSeek 余额接口请求失败（HTTP {statusCode}）。");
    public static readonly DeepSeekBalanceException EmptyResponse =
        new("DeepSeek 余额接口没有返回余额数据。");
}

/// <summary>
/// Direct port of DeepSeekBalanceService: GET {base}/user/balance with a
/// Bearer token, redirects disabled, bounded response body.
/// </summary>
public sealed class DeepSeekBalanceService
{
    private const int MaxBodyBytes = 1024 * 1024;

    private readonly Uri _baseUrl;
    private readonly HttpClient _client;

    public DeepSeekBalanceService(IReadOnlyDictionary<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string?)e.Value);

        // DEEPSEEK_BASE_URL is a local test/代理 hook; only structurally valid
        // HTTPS URLs without credentials/query/fragment are accepted.
        var configured = environment.TryGetValue("DEEPSEEK_BASE_URL", out var raw)
            ? raw : null;
        if (configured is not null &&
            Uri.TryCreate(configured, UriKind.Absolute, out var parsed) &&
            parsed.Scheme == "https" &&
            !string.IsNullOrEmpty(parsed.Host) &&
            string.IsNullOrEmpty(parsed.UserInfo) &&
            string.IsNullOrEmpty(parsed.Query) &&
            string.IsNullOrEmpty(parsed.Fragment))
        {
            _baseUrl = parsed;
        }
        else
        {
            _baseUrl = new Uri("https://api.deepseek.com");
        }

        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        _client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
    }

    public async Task<DeepSeekBalanceResponse> FetchAsync(string apiKey)
    {
        if (_baseUrl.Scheme != "https") throw DeepSeekBalanceException.InvalidBaseUrl;
        var endpoint = new Uri(_baseUrl, _baseUrl.AbsolutePath.TrimEnd('/') + "/user/balance");
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await _client.SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw DeepSeekBalanceException.Unauthorized;
        }
        if (!response.IsSuccessStatusCode)
        {
            throw DeepSeekBalanceException.Server((int)response.StatusCode);
        }
        if (response.Content.Headers.ContentLength > MaxBodyBytes)
        {
            throw DeepSeekBalanceException.InvalidResponse;
        }
        var data = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        if (data.Length > MaxBodyBytes)
        {
            throw DeepSeekBalanceException.InvalidResponse;
        }

        DeepSeekBalanceResponse? decoded;
        try
        {
            decoded = JsonSerializer.Deserialize<DeepSeekBalanceResponse>(data);
        }
        catch (JsonException)
        {
            throw DeepSeekBalanceException.InvalidResponse;
        }
        if (decoded is null || decoded.BalanceInfos.Count == 0)
        {
            throw DeepSeekBalanceException.EmptyResponse;
        }
        return decoded;
    }
}
