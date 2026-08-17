using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using HarnessLauncher.Models;

namespace HarnessLauncher.Services;

public sealed record RuntimeUpdateResult(
    RuntimeManifest Manifest,
    string? CurrentRuntimeId,
    string? CurrentHarnessVersion)
{
    public bool IsUpdateAvailable
    {
        get
        {
            if (CurrentRuntimeId is not null) return CurrentRuntimeId != Manifest.RuntimeId;
            if (CurrentHarnessVersion is not null) return CurrentHarnessVersion != Manifest.Harness.Version;
            return true;
        }
    }
}

/// <summary>
/// Direct port of RuntimeUpdateService: HTTPS-only manifest feed, redirects
/// disabled, bounded retries, SHA-256 + size verification streamed in 1 MiB
/// chunks.
/// </summary>
public sealed class RuntimeUpdateService
{
    private readonly IReadOnlyDictionary<string, string?> _environment;
    private readonly HttpClient _client;
    private readonly AppPaths _paths;

    public RuntimeUpdateService(
        IReadOnlyDictionary<string, string?>? environment = null,
        AppPaths? paths = null)
    {
        _environment = environment ?? Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string?)e.Value);
        _paths = paths ?? new AppPaths();
        _client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            // Whole-transfer deadline.
            Timeout = TimeSpan.FromSeconds(600),
        };
    }

    public static string CurrentArchitecture => ToolchainCatalog.CurrentArchitecture;

    public async Task<RuntimeUpdateResult> CheckAsync(string? currentHarnessVersion = null)
    {
        if (!_environment.TryGetValue("HARNESS_UPDATE_MANIFEST_URL", out var rawFeed) ||
            string.IsNullOrEmpty(rawFeed) ||
            !Uri.TryCreate(rawFeed, UriKind.Absolute, out var feedUrl) ||
            feedUrl.Scheme != "https")
        {
            throw RuntimeManifestException.FeedNotConfigured;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, feedUrl);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var response = await _client.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw RuntimeManifestException.InvalidResponse;
        }

        RuntimeManifest? manifest;
        try
        {
            manifest = await JsonSerializer.DeserializeAsync<RuntimeManifest>(
                await response.Content.ReadAsStreamAsync(timeoutCts.Token).ConfigureAwait(false),
                cancellationToken: timeoutCts.Token).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            throw RuntimeManifestException.InvalidJSON;
        }
        if (manifest is null) throw RuntimeManifestException.InvalidJSON;

        var shellVersion = _environment.TryGetValue("HARNESS_SHELL_VERSION", out var shell) &&
            !string.IsNullOrEmpty(shell)
                ? shell!
                : LauncherVersion.Current;
        RuntimeManifestVerifier.Validate(manifest, CurrentArchitecture, shellVersion);

        return new RuntimeUpdateResult(manifest, ReadCurrentRuntimeId(), currentHarnessVersion);
    }

    public async Task<string> DownloadAsync(RuntimeManifest manifest, string destination)
    {
        if (manifest.Artifact.Url.Scheme != "https") throw RuntimeManifestException.InvalidURL;
        Directory.CreateDirectory(destination);

        // Unique per-attempt staging name: two overlapping downloads (or a
        // stale one) can never remove or overwrite each other's file.
        var target = Path.Combine(destination, $"{manifest.RuntimeId}-{Guid.NewGuid():N}.artifact");
        Exception lastError = RuntimeManifestException.InvalidResponse;

        // Transient network failures get a short bounded retry with backoff;
        // hash/size mismatches do not.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using (var response = await _client.GetAsync(manifest.Artifact.Url,
                    HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        throw RuntimeManifestException.InvalidResponse;
                    }
                    await using (var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    await using (var sink = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        await source.CopyToAsync(sink).ConfigureAwait(false);
                    }
                }

                var actualSize = new FileInfo(target).Length;
                if (actualSize != manifest.Artifact.Size)
                {
                    throw RuntimeManifestException.ArtifactSizeMismatch;
                }
                var digest = await Sha256HexAsync(target).ConfigureAwait(false);
                if (!digest.Equals(manifest.Artifact.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw RuntimeManifestException.ArtifactHashMismatch;
                }

                // Transfer ownership to the caller: a validated artifact must
                // not be deleted by the cleanup below.
                var finalPath = Path.Combine(destination,
                    $"{manifest.RuntimeId}-{Guid.NewGuid():N}.verified.artifact");
                File.Move(target, finalPath, overwrite: true);
                return finalPath;
            }
            catch (Exception error)
            {
                lastError = error;
                try { if (File.Exists(target)) File.Delete(target); } catch { }
                var permanent = error.Message == RuntimeManifestException.ArtifactHashMismatch.Message ||
                                error.Message == RuntimeManifestException.ArtifactSizeMismatch.Message;
                if (permanent) break;
                if (attempt < 2)
                {
                    await Task.Delay(TimeSpan.FromSeconds((attempt + 1) * 2)).ConfigureAwait(false);
                }
            }
        }
        try { if (File.Exists(target)) File.Delete(target); } catch { }
        throw lastError;
    }

    /// <summary>Streams the file in 1 MiB chunks instead of loading the whole
    /// artifact into memory.</summary>
    private static async Task<string> Sha256HexAsync(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1024 * 1024, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private string? ReadCurrentRuntimeId()
    {
        var manifest = _paths.ActiveRuntimeManifest;
        if (!File.Exists(manifest)) return null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifest));
            return document.RootElement.TryGetProperty("runtimeId", out var id)
                ? id.GetString() : null;
        }
        catch { return null; }
    }
}
