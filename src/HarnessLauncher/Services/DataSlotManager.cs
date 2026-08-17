using System.Text.Json;
using System.Text.Json.Serialization;
using HarnessLauncher.Support;

namespace HarnessLauncher.Services;

public sealed record DataSlotActivation(string PreviousSlot, string CandidateSlot);

public class DataSlotException : Exception
{
    public DataSlotException(string message) : base(message) { }

    public static DataSlotException CloneFailed(string message) => new($"无法复制 Harness 数据 slot：{message}");
    public static DataSlotException ActivationFailed(string message) => new($"无法切换 Harness 数据 slot：{message}");
    public static DataSlotException RollbackFailed(string message) => new($"数据 slot 恢复失败：{message}");
    public static DataSlotException RecoveryFailed(string message) => new($"数据 slot 事务恢复失败：{message}");
}

/// <summary>Journal written before the active slot is moved, so a crash or
/// power loss between the two rename steps can be repaired on next launch.</summary>
public sealed class DataSlotTransactionJournal
{
    public const string FileName = "slot-transaction.json";
    public const string PhaseActiveMoved = "active-moved";

    [JsonPropertyName("phase")] public required string Phase { get; set; }
    [JsonPropertyName("previousSlot")] public required string PreviousSlot { get; set; }
    [JsonPropertyName("candidateSlot")] public required string CandidateSlot { get; set; }
    [JsonPropertyName("recordedAt")] public DateTime RecordedAt { get; set; }
}

/// <summary>
/// Provides recoverable data-slot transactions for plugin and Runtime
/// preflight. Direct port of the macOS DataSlotManager.
/// </summary>
public sealed class DataSlotManager
{
    /// <summary>
    /// Copies the active slot on a background thread: a profile with
    /// installed plugins can be hundreds of megabytes, and the copy is pure
    /// blocking file I/O that would otherwise freeze the window and menus.
    /// </summary>
    public async Task<string> CloneActiveSlotAsync(AppPaths paths)
    {
        return await Task.Run(() =>
        {
            try
            {
                paths.Prepare();
                var root = Path.Combine(paths.Caches, "updates", "data-slots", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);
                var candidate = Path.Combine(root, "candidate");
                CopyDirectory(paths.ActiveDataSlot, candidate);
                return candidate;
            }
            catch (Exception error)
            {
                throw DataSlotException.CloneFailed(error.Message);
            }
        }).ConfigureAwait(false);
    }

    public DataSlotActivation Activate(string candidateSlot, AppPaths paths)
    {
        var previousSlot = Path.Combine(paths.Backups,
            $"data-slot-{Timestamp()}-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(paths.Backups);
            WriteJournal(DataSlotTransactionJournal.PhaseActiveMoved, previousSlot, candidateSlot, paths);
            Directory.Move(paths.ActiveDataSlot, previousSlot);
            try
            {
                Directory.Move(candidateSlot, paths.ActiveDataSlot);
            }
            catch
            {
                var restoreFailed = false;
                try { Directory.Move(previousSlot, paths.ActiveDataSlot); }
                catch { restoreFailed = true; }
                if (restoreFailed)
                {
                    // Keep the journal: the next launch repairs this state.
                    throw DataSlotException.RollbackFailed(
                        "候选 slot 切换失败，且旧 slot 恢复也失败。事务日志已保留，下次启动将自动恢复。");
                }
                RemoveJournal(paths);
                throw;
            }
            RemoveJournal(paths);
            return new DataSlotActivation(previousSlot, paths.ActiveDataSlot);
        }
        catch (DataSlotException)
        {
            throw;
        }
        catch (Exception error)
        {
            try { RemoveJournal(paths); } catch { }
            throw DataSlotException.ActivationFailed(error.Message);
        }
    }

    /// <summary>
    /// Restores the old slot and preserves the failed candidate under the
    /// backups tree for post-update investigation.
    /// </summary>
    public void Rollback(DataSlotActivation activation, AppPaths paths)
    {
        var failedSlot = Path.Combine(paths.Backups,
            $"failed-data-slot-{Timestamp()}-{Guid.NewGuid():N}");
        if (Directory.Exists(activation.CandidateSlot))
        {
            try { Directory.Move(activation.CandidateSlot, failedSlot); } catch { }
        }
        if (!Directory.Exists(activation.PreviousSlot))
        {
            throw DataSlotException.RecoveryFailed(
                $"rollback 时找不到旧 slot：{activation.PreviousSlot}");
        }
        try
        {
            Directory.Move(activation.PreviousSlot, paths.ActiveDataSlot);
        }
        catch (Exception error)
        {
            throw DataSlotException.RollbackFailed(error.Message);
        }
    }

    /// <summary>
    /// Finishes or rolls back an interrupted slot swap. Called at launch and
    /// before every Harness start; a no-op when no transaction is pending.
    /// </summary>
    public void RecoverPendingTransaction(AppPaths paths)
    {
        var journalPath = JournalPath(paths);
        DataSlotTransactionJournal? journal = null;
        try
        {
            if (File.Exists(journalPath))
            {
                journal = JsonSerializer.Deserialize<DataSlotTransactionJournal>(
                    File.ReadAllText(journalPath));
            }
        }
        catch { }
        if (journal is null) return;

        var previousSlot = Path.GetFullPath(journal.PreviousSlot);
        var candidateSlot = Path.GetFullPath(journal.CandidateSlot);

        // The journal only ever holds paths inside App-owned directories.
        // Ignore anything else instead of moving arbitrary user files.
        if (!IsAppOwnedSlotPath(previousSlot, paths) || !IsAppOwnedSlotPath(candidateSlot, paths))
        {
            AppLogger.Log(AppLogger.Level.Error, "launcher",
                "Ignored data-slot journal with unexpected paths.");
            try { RemoveJournal(paths); } catch { }
            return;
        }

        var activeExists = Directory.Exists(paths.ActiveDataSlot);
        var previousExists = Directory.Exists(previousSlot);
        var candidateExists = Directory.Exists(candidateSlot);

        try
        {
            if (activeExists)
            {
                // The swap completed right before the crash: only the journal
                // deletion was lost.
                RemoveJournal(paths);
            }
            else if (candidateExists)
            {
                // Active was moved away but the candidate never landed:
                // finish the transaction forward.
                Directory.Move(candidateSlot, paths.ActiveDataSlot);
                RemoveJournal(paths);
                AppLogger.Log(AppLogger.Level.Info, "launcher",
                    "Recovered data-slot transaction by activating the candidate slot.");
            }
            else if (previousExists)
            {
                // The candidate is gone: roll back to the previous slot.
                Directory.Move(previousSlot, paths.ActiveDataSlot);
                RemoveJournal(paths);
                AppLogger.Log(AppLogger.Level.Info, "launcher",
                    "Recovered data-slot transaction by restoring the previous slot.");
            }
            else
            {
                AppLogger.Log(AppLogger.Level.Error, "launcher",
                    "Data-slot journal exists but neither slot is available; starting with a fresh profile.");
                RemoveJournal(paths);
            }
        }
        catch (Exception error)
        {
            AppLogger.Log(AppLogger.Level.Error, "launcher",
                $"Data-slot transaction recovery failed: {error.Message}");
        }
    }

    public static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }
        foreach (var directory in Directory.GetDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }

    private static string JournalPath(AppPaths paths) =>
        Path.Combine(paths.State, DataSlotTransactionJournal.FileName);

    private static void WriteJournal(string phase, string previousSlot, string candidateSlot, AppPaths paths)
    {
        var journal = new DataSlotTransactionJournal
        {
            Phase = phase,
            PreviousSlot = previousSlot,
            CandidateSlot = candidateSlot,
            RecordedAt = DateTime.Now,
        };
        var json = JsonSerializer.Serialize(journal);
        var path = JournalPath(paths);
        // Write-then-replace plus a flush-to-disk so a power loss cannot
        // leave a zero-length journal.
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                using var writer = new StreamWriter(stream);
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    private static void RemoveJournal(AppPaths paths)
    {
        var path = JournalPath(paths);
        if (File.Exists(path)) File.Delete(path);
    }

    private static bool IsAppOwnedSlotPath(string path, AppPaths paths)
    {
        var value = Path.GetFullPath(path);
        var supportPrefix = Path.GetFullPath(paths.ApplicationSupport) + Path.DirectorySeparatorChar;
        var cachesPrefix = Path.GetFullPath(paths.Caches) + Path.DirectorySeparatorChar;
        if (!value.StartsWith(supportPrefix, StringComparison.OrdinalIgnoreCase) &&
            !value.StartsWith(cachesPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var backupsPrefix = Path.GetFullPath(paths.Backups) + Path.DirectorySeparatorChar;
        var dataSlotsPrefix = Path.GetFullPath(
            Path.Combine(paths.Caches, "updates", "data-slots")) + Path.DirectorySeparatorChar;
        return value.StartsWith(backupsPrefix, StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith(dataSlotsPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string Timestamp() =>
        DateTime.Now.ToString("yyyy-MM-ddTHH-mm-ss");
}
