using System.Net;
using System.Net.Http;
using HarnessLauncher.Models;
using HarnessLauncher.Services;
using Xunit;

namespace HarnessLauncher.Tests;

public sealed class LauncherFeatureParityTests
{
    [Fact]
    public void StoreRequestAcceptsOnlyWebProfilePluginOperations()
    {
        var request = PluginStoreRequest.Parse([
            "plugin", "--profile", "web", "add", "@scope/example@1.2.3",
            "--allow-build=@scope/example"]);

        Assert.False(request.IsRemoval);
        Assert.Equal(["add", "@scope/example@1.2.3"], request.Arguments);
        Assert.Equal(["@scope/example"], request.AllowedBuildScripts);
    }

    [Theory]
    [InlineData("plugin", "--profile", "default", "add", "example")]
    [InlineData("plugin", "--profile", "web", "add", "dsh1024@0.5.0")]
    [InlineData("plugin", "--profile", "web", "remove", "--force")]
    public void StoreRequestRejectsUnsafeOrUnsupportedOperations(params string[] command)
    {
        Assert.Throws<PluginCommandParserException>(() => PluginStoreRequest.Parse(command));
    }

    [Fact]
    public void DiscountPeriodUsesChinaTimeAndExpectedMultipliers()
    {
        var weekdayPeakUtc = new DateTimeOffset(2026, 9, 21, 2, 30, 0, TimeSpan.Zero);
        var weekendUtc = new DateTimeOffset(2026, 9, 20, 2, 30, 0, TimeSpan.Zero);

        var peak = DeepSeekDiscountPeriodExtensions.Current(weekdayPeakUtc);
        var discount = DeepSeekDiscountPeriodExtensions.Current(weekendUtc);

        Assert.Equal(DeepSeekDiscountPeriod.Peak, peak);
        Assert.Equal("1.0x", peak.MultiplierText());
        Assert.Equal(DeepSeekDiscountPeriod.Discount, discount);
        Assert.Equal("0.5x", discount.MultiplierText());
    }

    [Fact]
    public async Task OfficialVersionServiceSelectsLatestValidVersion()
    {
        var handler = new StubHandler("""
        {
          "versions": {
            "0.1.0-rc.6": {},
            "0.1.0-rc.7": {},
            "0.1.0": {},
            "not-a-version": {}
          }
        }
        """);
        var service = new OfficialHarnessVersionService(
            new Dictionary<string, string?>
            {
                ["HARNESS_OFFICIAL_VERSION_URL"] = "https://registry.example.test/dsh"
            },
            handler);

        var result = await service.CheckAsync(includePrereleases: false);

        Assert.Equal("0.1.0", result.Version);
        Assert.True(result.IsUpdateAvailable("0.0.9"));
        Assert.False(result.IsUpdateAvailable("0.1.0"));
    }

    [Fact]
    public void PluginExecutionEnvironmentRedirectsWritablePackageState()
    {
        var root = Path.Combine(Path.GetTempPath(), "dsh-env-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(
            Path.Combine(root, "app"),
            Path.Combine(root, "cache"),
            Path.Combine(root, "logs"));
        var installation = new RuntimeInstallation(
            Path.Combine(root, "runtime", "dsh.js"),
            Path.Combine(root, "runtime"),
            "0.1.0-rc.6",
            Path.Combine(root, "runtime", "node.exe"));

        try
        {
            var environment = PluginExecutionEnvironment.Create(
                installation, paths, Path.Combine(root, "profile"));

            Assert.Equal(Path.Combine(paths.Caches, "npm"), environment["NPM_CONFIG_CACHE"]);
            Assert.Equal(Path.Combine(paths.Caches, "pnpm", "store"), environment["PNPM_STORE_DIR"]);
            Assert.Equal(Path.Combine(root, "profile"), environment["DSH_HOME"]);
            Assert.Contains(Path.Combine(paths.Toolchain, "pnpm-global"), environment["PATH"]);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class StubHandler(string payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload),
                RequestMessage = request,
            });
        }
    }
}
