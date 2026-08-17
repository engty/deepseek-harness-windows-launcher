using Xunit;
using HarnessLauncher.Services;

namespace HarnessLauncher.Tests;

public class PnpmWorkspaceConfigTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "pnpm-ws-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void CreatesWorkspaceFileWhenMissing()
    {
        Directory.CreateDirectory(_root);
        PnpmWorkspaceConfig.ApproveBuildScripts(new[] { "pkg-a" }, _root);
        var text = File.ReadAllText(Path.Combine(_root, "pnpm-workspace.yaml"));
        Assert.Contains("allowBuilds:", text);
        Assert.Contains("  pkg-a: true", text);
    }

    [Fact]
    public void AppendsToExistingAllowBuilds()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "pnpm-workspace.yaml"),
            "packages:\n  - .\n\nallowBuilds:\n  existing-pkg: true\n");
        PnpmWorkspaceConfig.ApproveBuildScripts(new[] { "new-pkg" }, _root);
        var text = File.ReadAllText(Path.Combine(_root, "pnpm-workspace.yaml"));
        Assert.Contains("  existing-pkg: true", text);
        Assert.Contains("  new-pkg: true", text);
    }

    [Fact]
    public void DoesNotDuplicateEntries()
    {
        Directory.CreateDirectory(_root);
        PnpmWorkspaceConfig.ApproveBuildScripts(new[] { "pkg-a" }, _root);
        var before = File.ReadAllText(Path.Combine(_root, "pnpm-workspace.yaml"));
        PnpmWorkspaceConfig.ApproveBuildScripts(new[] { "pkg-a" }, _root);
        var after = File.ReadAllText(Path.Combine(_root, "pnpm-workspace.yaml"));
        Assert.Equal(before, after);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("pkg; rm")]
    [InlineData("pkg name with spaces and !")]
    public void RejectsUnsafeNames(string name)
    {
        Directory.CreateDirectory(_root);
        Assert.Throws<PnpmWorkspaceConfigException>(
            () => PnpmWorkspaceConfig.ApproveBuildScripts(new[] { name }, _root));
    }

    [Fact]
    public void AcceptsScopedNames()
    {
        Directory.CreateDirectory(_root);
        PnpmWorkspaceConfig.ApproveBuildScripts(new[] { "@scope/pkg" }, _root);
        Assert.Contains("  @scope/pkg: true",
            File.ReadAllText(Path.Combine(_root, "pnpm-workspace.yaml")));
    }
}

public class BuildApprovalParserTests
{
    [Fact]
    public void ParsesIgnoredBuildScriptsLine()
    {
        const string output = """
            Progress: resolved 42
            Ignored build scripts: esbuild@0.21.5, @scope/native-pkg@1.0.0
            Done
            """;
        var packages = PluginCommandRunner.ParseBuildApprovalPackages(output);
        Assert.NotNull(packages);
        // Same tokenization as the macOS original: scoped names survive,
        // bare names and versions are split into package tokens.
        Assert.Contains("@scope/native-pkg", packages!);
        Assert.Contains("esbuild", packages!);
    }

    [Fact]
    public void ReturnsNullWithoutMarker()
    {
        Assert.Null(PluginCommandRunner.ParseBuildApprovalPackages("all good"));
    }
}