using System.Text.Json;
using HarnessLauncher.Support;

namespace HarnessLauncher.Services;

/// <summary>
/// Seeds the first web profile from the verified Runtime bundle. Existing
/// user profiles are never overwritten; the macOS launcher follows the same
/// rule so removing a bundled plugin remains a durable user choice.
/// </summary>
public sealed class DefaultProfileInstaller
{
    private readonly string _resourceRoot;

    public DefaultProfileInstaller(string? resourceRoot = null)
    {
        _resourceRoot = resourceRoot ?? Path.Combine(AppContext.BaseDirectory, "Resources");
    }

    public bool SeedIfNeeded(AppPaths paths, string runtimeRoot)
    {
        var profileManifest = Path.Combine(paths.ProfileWeb, "package.json");
        if (File.Exists(profileManifest)) return false;

        var bundled = Path.Combine(runtimeRoot, "default-profile", "profiles", "web");
        if (!Directory.Exists(bundled)) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(paths.ProfileWeb)!);
        DataSlotManager.CopyDirectory(bundled, paths.ProfileWeb);
        TryApplyDsh1024Adapter(paths.ProfileWeb);
        AppLogger.Log(AppLogger.Level.Info, "profile", "Seeded the bundled default web profile.");
        return true;
    }

    public bool TryApplyDsh1024Adapter(string profileWeb)
    {
        try
        {
            return Dsh1024Adapter.Sync(profileWeb,
                Path.Combine(_resourceRoot, "dsh1024-launcher"));
        }
        catch (Exception error)
        {
            AppLogger.Log(AppLogger.Level.Error, "profile",
                $"Could not apply the reviewed 1024 Store adapter: {error.Message}");
            return false;
        }
    }

    public static bool HasProfile(string profileWeb) =>
        File.Exists(Path.Combine(profileWeb, "package.json"));
}
