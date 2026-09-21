using System.Text.RegularExpressions;

namespace HarnessLauncher.Services;

public sealed record PluginStoreRequest(IReadOnlyList<string> Arguments, IReadOnlyList<string> AllowedBuildScripts)
{
    private static readonly Regex Token = new("^[A-Za-z0-9@:/._#+=-]+$", RegexOptions.Compiled);
    private static readonly Regex PackageName = new("^(?:@[A-Za-z0-9._-]+/)?[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.Compiled);

    public bool IsRemoval => Arguments.FirstOrDefault() == "remove";

    public static PluginStoreRequest Parse(IEnumerable<string> input)
    {
        var values = input.ToList();
        if (values.Count < 5 || values.Count > 32 ||
            !values.Take(3).SequenceEqual(["plugin", "--profile", "web"]) ||
            values[3] is not ("add" or "remove") ||
            values.Any(value => value.Length is 0 or > 1024 || !Token.IsMatch(value)))
            throw new PluginCommandParserException("1024 Store 请求不是受支持的官方插件命令。");

        var specs = new List<string>();
        var approvals = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values.Skip(4))
        {
            if (values[3] == "add" && value.StartsWith("--allow-build=", StringComparison.Ordinal))
            {
                var package = value["--allow-build=".Length..];
                if (!PackageName.IsMatch(package)) throw new PluginCommandParserException("构建脚本包名无效。");
                approvals.Add(package);
                continue;
            }
            if (value.StartsWith('-') || (values[3] == "remove" && !PackageName.IsMatch(value)))
                throw new PluginCommandParserException($"插件参数不允许：{value}");
            if (value.Equals("dsh1024", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("dsh1024@", StringComparison.OrdinalIgnoreCase))
                throw new PluginCommandParserException("1024 Store 由启动器维护，不能从商店自身覆盖。");
            specs.Add(value);
        }
        if (specs.Count == 0) throw new PluginCommandParserException("没有插件包名。");
        var arguments = values[3] == "remove"
            ? new[] { "remove" }.Concat(specs).ToList()
            : new[] { "add" }.Concat(specs).ToList();
        return new PluginStoreRequest(arguments, approvals.OrderBy(value => value).ToList());
    }
}
