using System.Text.Json;
using HarnessLauncher.Support;

namespace HarnessLauncher.Services;

public sealed record LegacySessionRepairSummary(
    int ScannedFiles,
    int RepairedSessions,
    int RepairedMessages,
    int FailedFiles);

/// <summary>
/// Runs the reviewed legacy session repair helper against a staging tree. The
/// original session files are copied to a timestamped backup before the
/// repaired output is published, so a failed repair cannot destroy history.
/// </summary>
public sealed class LegacySessionRepairService
{
    public async Task<LegacySessionRepairSummary> RepairAsync(
        RuntimeInstallation installation,
        AppPaths paths,
        CancellationToken cancellationToken = default)
    {
        var sessionsRoot = Path.Combine(paths.DshHome, "sessions");
        var helper = Path.Combine(AppContext.BaseDirectory, "Resources", "session-repair", "repair_legacy_sessions.mjs");
        if (!Directory.Exists(sessionsRoot) || !File.Exists(helper) || installation.NodeExecutable is not { Length: > 0 } node)
            return new LegacySessionRepairSummary(0, 0, 0, 0);

        var sessionFiles = Directory.EnumerateFiles(sessionsRoot, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path) is "session.jsonl" or "session.jsonl.zstd")
            .ToList();
        if (sessionFiles.Count == 0) return new LegacySessionRepairSummary(0, 0, 0, 0);

        var staging = Path.Combine(paths.Caches, "legacy-session-repair", Guid.NewGuid().ToString("N"));
        var backup = Path.Combine(paths.Backups, $"legacy-session-repair-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            var result = await SubprocessRunner.RunAsync(
                node,
                [helper, "--root", sessionsRoot, "--output-root", staging],
                currentDirectory: sessionsRoot,
                timeout: TimeSpan.FromMinutes(10),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (result.Status != 0)
            {
                AppLogger.Log(AppLogger.Level.Error, "sessions",
                    $"Legacy session repair failed: {SensitiveDataRedactor.Redact(result.Output)}");
                return new LegacySessionRepairSummary(sessionFiles.Count, 0, 0, sessionFiles.Count);
            }

            using var document = JsonDocument.Parse(result.Output.Trim().Split('\n').Last());
            var root = document.RootElement;
            var repairedFiles = root.TryGetProperty("repairedFiles", out var files) && files.ValueKind == JsonValueKind.Array
                ? files.EnumerateArray().ToList()
                : new List<JsonElement>();
            var failed = root.TryGetProperty("failedFiles", out var failures) && failures.ValueKind == JsonValueKind.Array
                ? failures.GetArrayLength()
                : 0;
            var repairedMessages = repairedFiles.Sum(file =>
                file.TryGetProperty("repairedMessages", out var count) ? count.GetInt32() : 0);

            foreach (var file in repairedFiles)
            {
                if (!file.TryGetProperty("relativePath", out var relativeValue)) continue;
                var relative = relativeValue.GetString();
                if (string.IsNullOrWhiteSpace(relative)) continue;
                var source = Path.GetFullPath(Path.Combine(sessionsRoot, relative));
                var staged = Path.GetFullPath(Path.Combine(staging, relative));
                if (!IsChildOf(source, sessionsRoot) || !IsChildOf(staged, staging) || !File.Exists(staged)) continue;
                var backupPath = Path.Combine(backup, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                File.Copy(source, backupPath, overwrite: false);
                File.Move(staged, source, overwrite: true);
            }
            if (repairedFiles.Count > 0)
                AppLogger.Log(AppLogger.Level.Info, "sessions",
                    $"Repaired {repairedFiles.Count} legacy sessions ({repairedMessages} messages).");
            return new LegacySessionRepairSummary(sessionFiles.Count, repairedFiles.Count, repairedMessages, failed);
        }
        catch (Exception error)
        {
            AppLogger.Log(AppLogger.Level.Error, "sessions", $"Legacy session repair failed: {error.Message}");
            return new LegacySessionRepairSummary(sessionFiles.Count, 0, 0, sessionFiles.Count);
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { }
        }
    }

    private static bool IsChildOf(string path, string root)
    {
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
