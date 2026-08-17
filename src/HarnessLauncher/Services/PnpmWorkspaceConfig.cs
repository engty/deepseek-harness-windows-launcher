using System.Text.RegularExpressions;

namespace HarnessLauncher.Services;

public class PnpmWorkspaceConfigException : Exception
{
    public PnpmWorkspaceConfigException(string message) : base(message) { }
}

/// <summary>Direct port of the macOS PnpmWorkspaceConfig.</summary>
public static class PnpmWorkspaceConfig
{
    public static void ApproveBuildScripts(IReadOnlyList<string> packageNames, string profilePath)
    {
        var validNames = packageNames
            .Select(n => n.Trim())
            .Select(name => IsSafePackageName(name)
                ? name
                : throw new PnpmWorkspaceConfigException($"pnpm build 脚本依赖名称不安全：{name}"))
            .ToList();
        var uniqueNames = validNames.Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList();
        if (uniqueNames.Count == 0) return;

        var workspacePath = Path.Combine(profilePath, "pnpm-workspace.yaml");
        string existing;
        if (File.Exists(workspacePath))
        {
            existing = File.ReadAllText(workspacePath);
        }
        else
        {
            // Same minimal profile configuration that Harness creates on
            // first use. Written only into staging, so the active profile is
            // untouched until the transaction passes preflight.
            existing = """
                packages:
                  - .

                nodeLinker: hoisted
                autoInstallPeers: false
                """;
        }

        var existingNames = existing.Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.StartsWith(' ') && line.Contains(':'))
            .Select(line => line.Trim().Split(':', 2)[0])
            .Where(IsSafePackageName)
            .ToHashSet(StringComparer.Ordinal);
        var missingNames = uniqueNames.Where(n => !existingNames.Contains(n)).ToList();
        if (missingNames.Count == 0) return;

        var entries = missingNames.Select(n => $"  {n}: true").ToList();
        var lines = existing.Split('\n').ToList();
        var allowBuildsIndex = lines.FindIndex(l => l.Trim() == "allowBuilds:");
        if (allowBuildsIndex >= 0)
        {
            var insertionIndex = allowBuildsIndex + 1;
            while (insertionIndex < lines.Count)
            {
                var line = lines[insertionIndex];
                if (line.Length == 0 || (line[0] != ' ' && line[0] != '\t')) break;
                if (line.Length == 0) break;
                insertionIndex++;
            }
            lines.InsertRange(insertionIndex, entries);
        }
        else
        {
            lines.Add("");
            lines.Add("allowBuilds:");
            lines.AddRange(entries);
        }
        File.WriteAllText(workspacePath, string.Join('\n', lines));
    }

    private static bool IsSafePackageName(string name) =>
        Regex.IsMatch(name, @"^(?:@[A-Za-z0-9._-]+/)?[A-Za-z0-9._-]+$");
}
