using HarnessLauncher.Models;

namespace HarnessLauncher.Services;

/// <summary>
/// Validates a decoded Runtime manifest without performing network or file
/// I/O. Direct port of the macOS RuntimeManifestVerifier.
/// </summary>
public static class RuntimeManifestVerifier
{
    public static void Validate(RuntimeManifest manifest, string architecture, string shellVersion)
    {
        if (manifest.SchemaVersion != 1)
        {
            throw RuntimeManifestException.UnsupportedSchema(manifest.SchemaVersion);
        }
        if (!manifest.HasSafeRuntimeId)
        {
            throw RuntimeManifestException.InvalidRuntimeId;
        }
        if (manifest.Artifact.Url.Scheme != "https")
        {
            throw RuntimeManifestException.InvalidURL;
        }
        if (manifest.Architecture != architecture)
        {
            throw RuntimeManifestException.UnsupportedArchitecture(manifest.Architecture);
        }
        if (!StrictSemanticVersion.TryParse(manifest.MinShellVersion, out _))
        {
            throw RuntimeManifestException.InvalidMinShellVersion(manifest.MinShellVersion);
        }
        if (!SatisfiesMinimumShellVersion(shellVersion, manifest.MinShellVersion))
        {
            throw RuntimeManifestException.IncompatibleShellVersion(
                manifest.MinShellVersion, shellVersion);
        }
        // A manifest must always declare a positive size so tar bombs cannot
        // dodge the resource limit.
        if (manifest.Artifact.Size <= 0)
        {
            throw RuntimeManifestException.InvalidArtifactSize;
        }
        var hash = manifest.Artifact.Sha256;
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit))
        {
            throw RuntimeManifestException.InvalidArtifactHash;
        }
    }

    private static bool SatisfiesMinimumShellVersion(string current, string minimum)
    {
        if (!StrictSemanticVersion.TryParse(current, out var currentVersion) ||
            !StrictSemanticVersion.TryParse(minimum, out var minimumVersion))
        {
            return false;
        }
        return currentVersion! >= minimumVersion!;
    }
}
