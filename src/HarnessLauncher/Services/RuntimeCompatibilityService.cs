using HarnessLauncher.Models;
using HarnessLauncher.Support;

namespace HarnessLauncher.Services;

/// <summary>
/// Applies the narrow compatibility bridges that the current macOS launcher
/// keeps around for profiles created by older Harness/plugin combinations.
/// All changes are reversible: replaced packages are copied to an App-owned
/// backup directory and user profiles are never edited unless the package is
/// already installed there.
/// </summary>
public sealed class RuntimeCompatibilityService
{
    public void Apply(AppPaths paths, RuntimeInstallation installation)
    {
        ApplyToProfile(paths.ProfileWeb, installation.Root, installation.Version, paths.Backups);
    }

    public void ApplyToProfile(
        string profileWeb,
        string runtimeRoot,
        string? runtimeVersion,
        string backupRoot)
    {
        if (!Directory.Exists(profileWeb)) return;
        SyncRuntimeCoreModule(profileWeb, runtimeRoot, backupRoot);
        SyncDshLlmCodex(profileWeb, runtimeRoot);
        SyncDshMnemonSession(profileWeb);
        SyncDshMnemonProjection(profileWeb, runtimeVersion);
    }

    private static void SyncRuntimeCoreModule(string profileWeb, string runtimeRoot, string backupRoot)
    {
        var active = Path.Combine(profileWeb, "node_modules", "@deepseek-ai", "dsh-llm");
        if (!Directory.Exists(active) && !File.Exists(active)) return;
        var runtimePackage = FindPackage(runtimeRoot, "@deepseek-ai/dsh-llm");
        if (runtimePackage is null || SameDirectory(active, runtimePackage)) return;

        var backup = Path.Combine(backupRoot, "runtime-compatibility",
            $"dsh-llm-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        MovePath(active, backup);
        try
        {
            CopyDirectory(runtimePackage, active);
            AppLogger.Log(AppLogger.Level.Info, "profile",
                "Aligned profile @deepseek-ai/dsh-llm with the active Runtime.");
        }
        catch
        {
            try { MovePath(backup, active); } catch { }
            throw;
        }
    }

    private static void SyncDshLlmCodex(string profileWeb, string runtimeRoot)
    {
        var sourcePath = Path.Combine(profileWeb, "node_modules", "dsh-llm-codex", "lib", "translate.js");
        var runtimePath = FindPackage(runtimeRoot, "@deepseek-ai/dsh-llm");
        var exportPath = runtimePath is null ? null : Path.Combine(runtimePath, "lib", "index.js");
        if (!File.Exists(sourcePath) || exportPath is null || !File.Exists(exportPath)) return;
        var source = File.ReadAllText(sourcePath);
        var exports = File.ReadAllText(exportPath);
        var modern = exports.Contains("ToolCallId", StringComparison.Ordinal);
        var adapted = source;
        if (modern && source.Contains("import { CallId, LlmError, EMPTY_RESPONSE_CODE }", StringComparison.Ordinal))
        {
            adapted = adapted.Replace(
                "import { CallId, LlmError, EMPTY_RESPONSE_CODE }",
                "import { ToolCallId, LlmError, EMPTY_RESPONSE_CODE }",
                StringComparison.Ordinal).Replace("CallId(", "ToolCallId(", StringComparison.Ordinal);
        }
        else if (!modern && exports.Contains("CallId", StringComparison.Ordinal) &&
                 source.Contains("import { ToolCallId, LlmError, EMPTY_RESPONSE_CODE }", StringComparison.Ordinal))
        {
            adapted = adapted.Replace(
                "import { ToolCallId, LlmError, EMPTY_RESPONSE_CODE }",
                "import { CallId, LlmError, EMPTY_RESPONSE_CODE }",
                StringComparison.Ordinal).Replace("ToolCallId(", "CallId(", StringComparison.Ordinal);
        }
        if (adapted != source) WriteAtomic(sourcePath, adapted);
    }

    private static void SyncDshMnemonSession(string profileWeb)
    {
        var sourcePath = Path.Combine(profileWeb, "node_modules", "dsh-mnemon", "lib", "index.js");
        if (!File.Exists(sourcePath)) return;
        var source = File.ReadAllText(sourcePath);
        if (source.Contains("function dshMnemonSessionEvents", StringComparison.Ordinal) ||
            (!source.Contains("this.agent.session.events", StringComparison.Ordinal) &&
             !source.Contains("run.localAgent?.session.events", StringComparison.Ordinal))) return;

        const string anchor = "const MNEMON_READ_CHANNEL = \"/dsh-mnemon-read\";";
        if (!source.Contains(anchor, StringComparison.Ordinal)) return;
        const string helper = """
        function dshMnemonSessionEvents(session) {
          const events = typeof session?.snapshotEvents === "function" ? session.snapshotEvents() : session?.events;
          return Array.isArray(events) ? events : [];
        }

        """;
        var adapted = source
            .Replace("run.localAgent?.session.events ?? []", "dshMnemonSessionEvents(run.localAgent?.session)", StringComparison.Ordinal)
            .Replace("this.agent.session.events", "dshMnemonSessionEvents(this.agent.session)", StringComparison.Ordinal)
            .Replace(anchor, helper + anchor, StringComparison.Ordinal);
        WriteAtomic(sourcePath, adapted);
    }

    private static void SyncDshMnemonProjection(string profileWeb, string? runtimeVersion)
    {
        var sourcePath = Path.Combine(profileWeb, "node_modules", "dsh-mnemon", "lib", "index.js");
        if (!File.Exists(sourcePath) || !StrictSemanticVersion.TryParse(runtimeVersion ?? "", out var parsed)) return;
        var source = File.ReadAllText(sourcePath);
        var modernRuntime = parsed! >= ParseVersion("0.1.1-rc.1");
        var adapted = source;
        if (modernRuntime && source.Contains("schema: tokenUsageSchema.nullable(),", StringComparison.Ordinal))
        {
            adapted = adapted.Replace("schema: tokenUsageSchema.nullable(),", "stateSchema: tokenUsageStateSchema,", StringComparison.Ordinal);
            adapted = adapted.Replace(
                "view: (state) => state.descriptorSeen ? state.totals : null",
                "wire: { viewSchema: tokenUsageSchema.nullable(), view: (state) => state.descriptorSeen ? state.totals : null }",
                StringComparison.Ordinal);
        }
        else if (!modernRuntime && source.Contains("stateSchema: tokenUsageStateSchema,", StringComparison.Ordinal))
        {
            adapted = adapted.Replace("stateSchema: tokenUsageStateSchema,", "schema: tokenUsageSchema.nullable(),", StringComparison.Ordinal);
            adapted = adapted.Replace(
                "wire: { viewSchema: tokenUsageSchema.nullable(), view: (state) => state.descriptorSeen ? state.totals : null }",
                "view: (state) => state.descriptorSeen ? state.totals : null",
                StringComparison.Ordinal);
        }
        if (adapted != source) WriteAtomic(sourcePath, adapted);
    }

    private static StrictSemanticVersion ParseVersion(string value) =>
        StrictSemanticVersion.TryParse(value, out var version)
            ? version!
            : throw new InvalidOperationException($"Invalid compatibility version: {value}");

    private static string? FindPackage(string runtimeRoot, string packageName)
    {
        var direct = Path.Combine(runtimeRoot, "node_modules", packageName.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(Path.Combine(direct, "package.json"))) return direct;
        var pnpm = Path.Combine(runtimeRoot, "node_modules", ".pnpm");
        if (!Directory.Exists(pnpm)) return null;
        var packageDirectory = packageName.Split('/').Last();
        var prefix = packageName.StartsWith('@')
            ? "@" + packageName[1..].Replace('/', '+') + "@"
            : packageName + "@";
        foreach (var entry in Directory.EnumerateDirectories(pnpm))
        {
            if (!Path.GetFileName(entry).StartsWith(prefix, StringComparison.Ordinal)) continue;
            var candidate = Path.Combine(entry, "node_modules", packageName.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(Path.Combine(candidate, "package.json"))) return candidate;
            candidate = Path.Combine(entry, "node_modules", packageDirectory);
            if (File.Exists(Path.Combine(candidate, "package.json"))) return candidate;
        }
        return null;
    }

    private static bool SameDirectory(string lhs, string rhs) =>
        string.Equals(Path.GetFullPath(lhs).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(rhs).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static void MovePath(string source, string destination)
    {
        if (Directory.Exists(source)) Directory.Move(source, destination);
        else File.Move(source, destination);
    }

    private static void CopyDirectory(string source, string destination) =>
        DataSlotManager.CopyDirectory(source, destination);

    private static void WriteAtomic(string path, string content)
    {
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temporary, content);
        File.Move(temporary, path, true);
    }
}
