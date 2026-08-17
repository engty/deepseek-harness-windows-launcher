using System.Text.RegularExpressions;

namespace HarnessLauncher.Support;

/// <summary>
/// Port of the macOS SensitiveDataRedactor: literal-secret replacement plus
/// shape-based patterns, applied to every log line and user-facing output.
/// </summary>
public static class SensitiveDataRedactor
{
    private static readonly object LiteralLock = new();
    private static readonly List<string> LiteralSecrets = new();

    /// <summary>
    /// Registers currently known secret values (for example the user's real
    /// DeepSeek API key) so redaction no longer depends on recognizing a
    /// key's shape. Literal replacement is exact, so JSON, YAML, header,
    /// query and URL-encoded contexts are all covered.
    /// </summary>
    public static void RegisterLiteralSecret(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length < 8) return;
        lock (LiteralLock)
        {
            if (LiteralSecrets.Contains(trimmed)) return;
            LiteralSecrets.Add(trimmed);
            if (LiteralSecrets.Count > 8)
            {
                LiteralSecrets.RemoveRange(0, LiteralSecrets.Count - 8);
            }
        }
    }

    private static string[] LiteralSnapshot()
    {
        lock (LiteralLock)
        {
            return LiteralSecrets.OrderByDescending(s => s.Length).ToArray();
        }
    }

    private static readonly (Regex Pattern, string Replacement)[] Patterns =
    {
        (new Regex(@"(?i)(authorization\s*:\s*bearer\s+)[^\s]+", RegexOptions.Compiled), "$1[REDACTED]"),
        // Field-name forms: key = value, key: value, "key":"value" and quoted
        // values with spaces. Quoted strings win so embedded spaces survive.
        (new Regex(@"(?i)(\b(?:api[-_ ]?key|access[-_ ]?token|refresh[-_ ]?token|password|secret|cookie|authorization)\b\s*[""']?\s*[:=]\s*)((?:""[^""]*""|'[^']*'|[^\s,;=""'&]+))", RegexOptions.Compiled), "$1[REDACTED]"),
        (new Regex(@"\bsk-[A-Za-z0-9_-]{8,}\b", RegexOptions.Compiled), "[REDACTED_API_KEY]"),
    };

    public static string Redact(string value)
    {
        var result = value;
        foreach (var secret in LiteralSnapshot())
        {
            result = result.Replace(secret, "[REDACTED_API_KEY]", StringComparison.Ordinal);
        }
        foreach (var (pattern, replacement) in Patterns)
        {
            result = pattern.Replace(result, replacement);
        }
        return result;
    }
}
