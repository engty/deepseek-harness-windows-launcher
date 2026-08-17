using System.Text.Json;
using System.Text.Json.Serialization;
using HarnessLauncher.Support;

namespace HarnessLauncher.Services;

public static class LauncherVersion
{
    public static string Current =>
        typeof(LauncherVersion).Assembly.GetName().Version is { } v
            ? $"{v.Major}.{v.Minor}.{v.Build}"
            : "0.0.0";
}

public sealed class RuntimeActivationRecord
{
    [JsonPropertyName("runtimeId")] public required string RuntimeId { get; set; }
    [JsonPropertyName("runtimePath")] public required string RuntimePath { get; set; }
    [JsonPropertyName("architecture")] public required string Architecture { get; set; }
    [JsonPropertyName("harnessVersion")] public required string HarnessVersion { get; set; }
}

public sealed record RuntimeActivation(
    RuntimeInstallation Installation,
    string RuntimePath,
    string? PreviousManifestJson);

public class RuntimeArchiveException : Exception
{
    public RuntimeArchiveException(string message) : base(message) { }

    public static readonly RuntimeArchiveException InvalidRuntimeId =
        new("Runtime ID 不是安全的目录名称。");
    public static readonly RuntimeArchiveException UnsupportedArchive =
        new("Runtime artifact 不是受支持的归档格式。");
    public static RuntimeArchiveException ArchiveListingFailed(string message) =>
        new($"无法读取 Runtime artifact 目录：{message}");
    public static RuntimeArchiveException ArchiveExtractionFailed(string message) =>
        new($"Runtime artifact 解压失败：{message}");
    public static RuntimeArchiveException UnsafeArchiveEntry(string entry) =>
        new($"Runtime artifact 包含不安全路径：{entry}");
    public static RuntimeArchiveException UnsafeArchiveEntryType(string detail) =>
        new($"Runtime artifact 包含不允许的条目类型：{detail}");
    public static RuntimeArchiveException DuplicateArchiveEntry(string entry) =>
        new($"Runtime artifact 包含重复路径条目：{entry}");
    public static readonly RuntimeArchiveException TooManyArchiveEntries =
        new("Runtime artifact 条目数量超过安全上限。");
    public static RuntimeArchiveException SymlinkEscapesRuntime(string path) =>
        new($"Runtime artifact 的符号链接越出 Bundle：{path}");
    public static readonly RuntimeArchiveException RuntimeExecutableMissing =
        new("Runtime Bundle 中没有可执行的 dsh。");
    public static RuntimeArchiveException ActivationFailed(string message) =>
        new($"Runtime 激活失败：{message}");
}

/// <summary>
/// Windows port of RuntimeArchiveInstaller. Archive extraction uses the
/// bsdtar that ships with Windows 10+ (C:\Windows\System32\tar.exe) with the
/// same entry-safety checks as the macOS version.
/// </summary>
public sealed class RuntimeArchiveInstaller
{
    private static readonly string TarExecutable =
        Path.Combine(Environment.SystemDirectory, "tar.exe");

    private const int MaxArchiveEntries = 200_000;
    private static readonly TimeSpan ListingTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan ExtractionTimeout = TimeSpan.FromSeconds(600);

    public async Task<RuntimeActivation> ActivateAsync(
        Models.RuntimeManifest manifest,
        string artifactPath,
        AppPaths paths,
        RuntimeInstallation? previousInstallation = null)
    {
        if (!manifest.HasSafeRuntimeId) throw RuntimeArchiveException.InvalidRuntimeId;
        if (!File.Exists(artifactPath))
        {
            throw RuntimeArchiveException.ActivationFailed("artifact 不存在。");
        }

        paths.Prepare();
        var staging = Path.Combine(paths.Caches, "updates", "staging", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            await ExtractAsync(artifactPath, staging).ConfigureAwait(false);
            ValidateLinks(staging);
            var runtimeRoot = LocateRuntimeRoot(staging);

            var runtimePath = Path.Combine(paths.Runtimes, $"{manifest.RuntimeId}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(paths.Runtimes);
            Directory.Move(runtimeRoot, runtimePath);

            var previousJson = File.Exists(paths.ActiveRuntimeManifest)
                ? File.ReadAllText(paths.ActiveRuntimeManifest) : null;
            var rollbackJson = previousJson;
            var record = new RuntimeActivationRecord
            {
                RuntimeId = manifest.RuntimeId,
                RuntimePath = runtimePath,
                Architecture = manifest.Architecture,
                HarnessVersion = manifest.Harness.Version,
            };
            try
            {
                if (previousJson is not null)
                {
                    WriteAtomically(previousJson, paths.LastKnownGoodRuntimeManifest);
                }
                else if (previousInstallation is not null)
                {
                    var previousRecord = new RuntimeActivationRecord
                    {
                        RuntimeId = "last-known-good",
                        RuntimePath = previousInstallation.Root,
                        Architecture = ToolchainCatalog.CurrentArchitecture,
                        HarnessVersion = previousInstallation.Version ?? "unknown",
                    };
                    rollbackJson = JsonSerializer.Serialize(previousRecord);
                    WriteAtomically(rollbackJson, paths.LastKnownGoodRuntimeManifest);
                }
                WriteAtomically(JsonSerializer.Serialize(record), paths.ActiveRuntimeManifest);
            }
            catch (Exception error)
            {
                try { Directory.Delete(runtimePath, recursive: true); } catch { }
                throw RuntimeArchiveException.ActivationFailed(error.Message);
            }

            try
            {
                var locator = new RuntimeLocator(
                    new Dictionary<string, string?>
                    {
                        ["HARNESS_RUNTIME_ROOT"] = runtimePath,
                        ["PATH"] = "",
                    },
                    paths);
                var installation = locator.Locate();
                return new RuntimeActivation(installation, runtimePath, rollbackJson);
            }
            catch
            {
                try
                {
                    Rollback(new RuntimeActivation(
                        new RuntimeInstallation(runtimePath, runtimePath, null, null),
                        runtimePath, rollbackJson), paths);
                }
                catch { }
                throw RuntimeArchiveException.RuntimeExecutableMissing;
            }
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
            catch { }
        }
    }

    public void Rollback(RuntimeActivation activation, AppPaths paths)
    {
        try { if (Directory.Exists(activation.RuntimePath)) Directory.Delete(activation.RuntimePath, true); }
        catch { }
        if (activation.PreviousManifestJson is { } previous)
        {
            WriteAtomically(previous, paths.ActiveRuntimeManifest);
        }
        else if (File.Exists(paths.ActiveRuntimeManifest))
        {
            File.Delete(paths.ActiveRuntimeManifest);
        }
    }

    public void RestoreLastKnownGood(AppPaths paths)
    {
        var json = File.ReadAllText(paths.LastKnownGoodRuntimeManifest);
        WriteAtomically(json, paths.ActiveRuntimeManifest);
    }

    /// <summary>
    /// Removes Runtime trees under runtimes/ that are no longer referenced by
    /// the active or last-known-good manifests.
    /// </summary>
    public void CleanupOrphanedRuntimes(AppPaths paths)
    {
        var referenced = ReferencedRuntimePaths(paths);
        if (!Directory.Exists(paths.Runtimes)) return;
        foreach (var entry in Directory.GetDirectories(paths.Runtimes))
        {
            var standardized = Path.GetFullPath(entry);
            if (referenced.Contains(standardized)) continue;
            try
            {
                Directory.Delete(entry, recursive: true);
                AppLogger.Log(AppLogger.Level.Info, "launcher",
                    $"Removed orphaned Runtime: {Path.GetFileName(entry)}");
            }
            catch (Exception error)
            {
                AppLogger.Log(AppLogger.Level.Error, "launcher",
                    $"Could not remove orphaned Runtime {Path.GetFileName(entry)}: {error.Message}");
            }
        }
    }

    private static HashSet<string> ReferencedRuntimePaths(AppPaths paths)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var manifest in new[] { paths.ActiveRuntimeManifest, paths.LastKnownGoodRuntimeManifest })
        {
            if (!File.Exists(manifest)) continue;
            try
            {
                var record = JsonSerializer.Deserialize<RuntimeActivationRecord>(File.ReadAllText(manifest));
                if (record is not null)
                {
                    result.Add(Path.GetFullPath(record.RuntimePath));
                }
            }
            catch { }
        }
        return result;
    }

    private async Task ExtractAsync(string artifact, string destination)
    {
        // 1. Enumerate every entry path first: reject absolute paths, `..`
        //    components, duplicates and oversized archives before anything
        //    touches the filesystem.
        var listing = await SubprocessRunner.RunAsync(
            TarExecutable, new[] { "-tf", artifact }, timeout: ListingTimeout).ConfigureAwait(false);
        if (listing.Status != 0)
        {
            throw RuntimeArchiveException.ArchiveListingFailed(
                SensitiveDataRedactor.Redact(listing.Output));
        }
        var entries = listing.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(e => e.TrimEnd('\r')).ToList();
        ValidateArchiveEntries(entries);

        // 2. Reject hardlink/device/FIFO/socket entries up front. Regular
        //    files, directories and symlinks are the only types the Runtime
        //    bundle legitimately contains.
        var verbose = await SubprocessRunner.RunAsync(
            TarExecutable, new[] { "-tvf", artifact }, timeout: ListingTimeout).ConfigureAwait(false);
        if (verbose.Status != 0)
        {
            throw RuntimeArchiveException.ArchiveListingFailed(
                SensitiveDataRedactor.Redact(verbose.Output));
        }
        ValidateEntryTypes(verbose.Output);

        // 3. Extract into the private staging directory.
        var extraction = await SubprocessRunner.RunAsync(
            TarExecutable, new[] { "-xf", artifact, "-C", destination },
            timeout: ExtractionTimeout).ConfigureAwait(false);
        if (extraction.Status != 0)
        {
            throw RuntimeArchiveException.ArchiveExtractionFailed(
                SensitiveDataRedactor.Redact(extraction.Output));
        }
    }

    private static string LocateRuntimeRoot(string staging)
    {
        string[] DirectCandidates(string root) => new[]
        {
            Path.Combine(root, "bin", "dsh.cmd"),
            Path.Combine(root, "bin", "dsh"),
            Path.Combine(root, "dsh.cmd"),
            Path.Combine(root, "dsh"),
            Path.Combine(root, "node_modules", ".bin", "dsh.cmd"),
            Path.Combine(root, "node_modules", ".bin", "dsh"),
        };

        string? RootFor(string executable, string baseDirectory)
        {
            var normalized = executable.Replace('/', Path.DirectorySeparatorChar);
            if (normalized.EndsWith(Path.Combine("bin", "dsh.cmd")) ||
                normalized.EndsWith(Path.Combine("bin", "dsh")))
            {
                return baseDirectory;
            }
            var marker = Path.Combine("node_modules", ".bin", "dsh");
            if (normalized.Contains(marker))
            {
                // node_modules/.bin/dsh(.cmd) → up three levels.
                return Path.GetFullPath(Path.Combine(
                    Path.GetDirectoryName(executable)!, "..", "..", ".."));
            }
            return Path.GetDirectoryName(executable)!;
        }

        var direct = DirectCandidates(staging).FirstOrDefault(File.Exists);
        if (direct is not null) return RootFor(direct, staging);

        var directories = Directory.GetDirectories(staging);
        if (directories.Length != 1) throw RuntimeArchiveException.RuntimeExecutableMissing;
        var nested = directories[0];
        var nestedExecutable = DirectCandidates(nested).FirstOrDefault(File.Exists);
        if (nestedExecutable is null) throw RuntimeArchiveException.RuntimeExecutableMissing;
        return RootFor(nestedExecutable, nested);
    }

    private static void ValidateArchiveEntries(IReadOnlyList<string> entries)
    {
        if (entries.Count > MaxArchiveEntries) throw RuntimeArchiveException.TooManyArchiveEntries;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            ValidateArchiveEntry(entry);
            if (!seen.Add(entry)) throw RuntimeArchiveException.DuplicateArchiveEntry(entry);
        }
    }

    private static void ValidateArchiveEntry(string entry)
    {
        var normalized = entry.Trim();
        if (normalized.Length == 0 ||
            normalized.StartsWith('/') ||
            normalized.Contains('\0') ||
            // Windows drive-letter absolute paths are equally unsafe.
            (normalized.Length >= 2 && normalized[1] == ':'))
        {
            throw RuntimeArchiveException.UnsafeArchiveEntry(entry);
        }
        if (normalized.Split('/').Contains(".."))
        {
            throw RuntimeArchiveException.UnsafeArchiveEntry(entry);
        }
        if (System.Text.Encoding.UTF8.GetByteCount(normalized) > 1024)
        {
            throw RuntimeArchiveException.UnsafeArchiveEntry(entry);
        }
    }

    /// <summary>
    /// `tar -tvf` starts every line with the entry type character:
    /// `-` regular file, `d` directory, `l` symlink, `h` hardlink,
    /// `c`/`b` device, `p` FIFO, `s` socket. Everything except files,
    /// directories and symlinks is rejected.
    /// </summary>
    private static void ValidateEntryTypes(string verboseListing)
    {
        foreach (var rawLine in verboseListing.Split('\n'))
        {
            if (rawLine.Length == 0) continue;
            if ("-dl".Contains(rawLine[0])) continue;
            throw RuntimeArchiveException.UnsafeArchiveEntryType(
                rawLine.Length > 160 ? rawLine[..160] : rawLine);
        }
    }

    /// <summary>
    /// Post-extraction containment check: no symlink/junction inside the
    /// Runtime tree may resolve to a location outside it. On Windows,
    /// extracting symlinks usually requires developer mode, so links are
    /// rare — but junctions are checked the same way.
    /// </summary>
    private static void ValidateLinks(string root)
    {
        var rootFull = Path.GetFullPath(root);
        var rootPrefix = rootFull.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var entry in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
        {
            var info = new FileInfo(entry);
            if (!info.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            var resolved = Path.GetFullPath(entry);
            if (!resolved.Equals(rootFull, StringComparison.OrdinalIgnoreCase) &&
                !resolved.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw RuntimeArchiveException.SymlinkEscapesRuntime(entry);
            }
        }
    }

    private static void WriteAtomically(string content, string path)
    {
        var temporary = Path.Combine(
            Path.GetDirectoryName(path)!,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                using var writer = new StreamWriter(stream);
                writer.Write(content);
                writer.Flush();
                // fsync the payload so a power loss cannot leave a
                // zero-length manifest paired with the new Runtime pointer.
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }
}
