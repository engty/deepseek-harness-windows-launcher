using System.Text.RegularExpressions;

namespace HarnessLauncher.Services;

public class PluginCommandParserException : Exception
{
    public PluginCommandParserException(string message) : base(message) { }

    public static readonly PluginCommandParserException EmptyCommand =
        new("没有输入插件安装命令。");
    public static readonly PluginCommandParserException UnterminatedQuote =
        new("插件命令中的引号没有闭合。");
    public static readonly PluginCommandParserException UnsupportedCommand =
        new("只支持官方 dsh plugin --profile web add 命令。");
    public static readonly PluginCommandParserException MissingPackageSpec =
        new("安装命令中没有插件 package spec。");
    public static PluginCommandParserException OptionNotAllowed(string option) =>
        new($"不允许把 pnpm 选项传给插件安装器：{option}");
}

/// <summary>
/// Parses a pasted command into argv without invoking a shell. Only the
/// official web-profile install form is accepted. Direct port of the macOS
/// PluginCommandParser.
/// </summary>
public static class PluginCommandParser
{
    public static IReadOnlyList<string> ParseInstallCommand(string input)
    {
        var tokens = Tokenize(input);
        if (tokens.Count == 0) throw PluginCommandParserException.EmptyCommand;

        // Accept either the official executable form or the app helper form.
        var pluginIndex = tokens.IndexOf("plugin");
        if (pluginIndex >= 0)
        {
            var prefix = tokens.Take(pluginIndex).ToList();
            if (!IsSupportedDshInvocation(prefix)) throw PluginCommandParserException.UnsupportedCommand;
            var cursor = pluginIndex + 1;
            if (cursor >= tokens.Count || tokens[cursor] != "--profile")
                throw PluginCommandParserException.UnsupportedCommand;
            cursor++;
            if (cursor >= tokens.Count || tokens[cursor] != "web")
                throw PluginCommandParserException.UnsupportedCommand;
            cursor++;
            if (cursor >= tokens.Count || tokens[cursor] != "add")
                throw PluginCommandParserException.UnsupportedCommand;
            cursor++;
            return PackageArguments(tokens.Skip(cursor).ToList());
        }

        var executable = tokens[0];
        if (executable == "deepseek-harness-plugin" || executable.EndsWith("/deepseek-harness-plugin") ||
            executable.EndsWith("\\deepseek-harness-plugin"))
        {
            if (tokens.Count < 2 || tokens[1] != "add") throw PluginCommandParserException.UnsupportedCommand;
            return PackageArguments(tokens.Skip(2).ToList());
        }

        throw PluginCommandParserException.UnsupportedCommand;
    }

    private static bool IsSupportedDshInvocation(IReadOnlyList<string> prefix)
    {
        if (prefix.Count == 1)
        {
            return prefix[0] == "dsh" || prefix[0].EndsWith("/dsh") || prefix[0].EndsWith("\\dsh") ||
                   prefix[0].Equals("dsh.cmd", StringComparison.OrdinalIgnoreCase) ||
                   prefix[0].EndsWith("\\dsh.cmd", StringComparison.OrdinalIgnoreCase);
        }
        if (prefix.Count == 2 && prefix[0] == "npx" && prefix[1] == "@deepseek-ai/dsh") return true;
        if (prefix.Count == 3 && prefix[0] == "pnpm" && prefix[1] == "dlx" && prefix[2] == "@deepseek-ai/dsh") return true;
        return false;
    }

    private static IReadOnlyList<string> PackageArguments(IReadOnlyList<string> values)
    {
        if (values.Count == 0) throw PluginCommandParserException.MissingPackageSpec;
        foreach (var value in values)
        {
            if (value.StartsWith('-')) throw PluginCommandParserException.OptionNotAllowed(value);
        }
        // pnpm resolves the shorthand `github:owner/repo` through Git SSH.
        // Public GitHub repositories are safely cloneable over HTTPS, so
        // normalize only the strict public shorthand shape.
        return new[] { "add" }.Concat(values.Select(NormalizeGitHubShorthand)).ToList();
    }

    private static readonly Regex GitHubShorthandPattern = new(
        @"^github:([A-Za-z0-9][A-Za-z0-9._-]*)/([A-Za-z0-9][A-Za-z0-9._-]*)(#[^\s]+)?$",
        RegexOptions.Compiled);

    private static string NormalizeGitHubShorthand(string value)
    {
        var match = GitHubShorthandPattern.Match(value);
        if (!match.Success) return value;
        var owner = match.Groups[1].Value;
        var repository = match.Groups[2].Value;
        var suffix = match.Groups[3].Success ? match.Groups[3].Value : "";
        return $"https://github.com/{owner}/{repository}.git{suffix}";
    }

    private static List<string> Tokenize(string input)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        char? quote = null;
        var escaping = false;

        foreach (var character in input)
        {
            if (escaping)
            {
                current.Append(character);
                escaping = false;
                continue;
            }
            if (character == '\\' && quote != '\'')
            {
                escaping = true;
                continue;
            }
            if (quote is { } activeQuote)
            {
                if (character == activeQuote) quote = null;
                else current.Append(character);
                continue;
            }
            if (character is '\'' or '"')
            {
                quote = character;
            }
            else if (char.IsWhiteSpace(character))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
            }
            else if (character is ';' or '|' or '&' or '<' or '>')
            {
                // These are shell operators, not valid package specs. Reject
                // them even though we never pass the text to a shell.
                throw PluginCommandParserException.UnsupportedCommand;
            }
            else
            {
                current.Append(character);
            }
        }

        if (escaping) current.Append('\\');
        if (quote is not null) throw PluginCommandParserException.UnterminatedQuote;
        if (current.Length > 0) tokens.Add(current.ToString());
        return tokens;
    }
}
