namespace HarnessLauncher.Services;

/// <summary>
/// Finds the x64 Fixed Version WebView2 runtime shipped beside the portable
/// launcher. Development builds may omit it and use the user's installed
/// Evergreen runtime instead.
/// </summary>
public static class WebView2RuntimeLocator
{
    public static string? FindBundled(string? baseDirectory = null)
    {
        var root = baseDirectory ?? AppContext.BaseDirectory;
        var candidate = Path.Combine(root, "Resources", "webview2");
        return File.Exists(Path.Combine(candidate, "msedgewebview2.exe"))
            ? candidate
            : null;
    }
}
