using Xunit;
using HarnessLauncher.Services;

namespace HarnessLauncher.Tests;

public class PluginCommandParserTests
{
    [Fact]
    public void ParsesOfficialInstallCommand()
    {
        var args = PluginCommandParser.ParseInstallCommand(
            "dsh plugin --profile web add dsh-llm-codex");
        Assert.Equal(new[] { "add", "dsh-llm-codex" }, args);
    }

    [Fact]
    public void ParsesNpxAndPnpmForms()
    {
        Assert.Equal(new[] { "add", "pkg" },
            PluginCommandParser.ParseInstallCommand("npx @deepseek-ai/dsh plugin --profile web add pkg"));
        Assert.Equal(new[] { "add", "pkg" },
            PluginCommandParser.ParseInstallCommand("pnpm dlx @deepseek-ai/dsh plugin --profile web add pkg"));
    }

    [Fact]
    public void NormalizesGitHubShorthand()
    {
        var args = PluginCommandParser.ParseInstallCommand(
            "dsh plugin --profile web add github:owner/repo");
        Assert.Equal(new[] { "add", "https://github.com/owner/repo.git" }, args);
    }

    [Fact]
    public void KeepsRefSuffixWhenNormalizing()
    {
        var args = PluginCommandParser.ParseInstallCommand(
            "dsh plugin --profile web add github:owner/repo#v1");
        Assert.Equal(new[] { "add", "https://github.com/owner/repo.git#v1" }, args);
    }

    [Theory]
    [InlineData("dsh plugin --profile web add pkg; rm -rf /")]
    [InlineData("dsh plugin --profile web add pkg | cat")]
    [InlineData("dsh plugin --profile web add pkg && whoami")]
    [InlineData("dsh plugin --profile web add pkg < input")]
    public void RejectsShellOperators(string input)
    {
        Assert.Throws<PluginCommandParserException>(
            () => PluginCommandParser.ParseInstallCommand(input));
    }

    [Theory]
    [InlineData("dsh plugin --profile web add --force")]   // pnpm option injection
    [InlineData("dsh plugin --profile web add -g")]
    public void RejectsOptions(string input)
    {
        Assert.Throws<PluginCommandParserException>(
            () => PluginCommandParser.ParseInstallCommand(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("dsh plugin --profile beta add pkg")]
    [InlineData("dsh plugin add pkg")]
    [InlineData("npm install pkg")]
    [InlineData("dsh plugin --profile web remove pkg")]
    [InlineData("dsh plugin --profile web add")]           // missing spec
    [InlineData("dsh plugin --profile web add \"unterminated")]
    public void RejectsUnsupportedForms(string input)
    {
        Assert.Throws<PluginCommandParserException>(
            () => PluginCommandParser.ParseInstallCommand(input));
    }

    [Fact]
    public void HandlesQuotedSpecs()
    {
        var args = PluginCommandParser.ParseInstallCommand(
            "dsh plugin --profile web add \"@scope/plugin name\"");
        Assert.Equal(new[] { "add", "@scope/plugin name" }, args);
    }
}