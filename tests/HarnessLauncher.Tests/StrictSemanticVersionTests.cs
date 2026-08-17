using Xunit;
using HarnessLauncher.Models;

namespace HarnessLauncher.Tests;

public class StrictSemanticVersionTests
{
    [Theory]
    [InlineData("1.2.3", 1, 2, 3)]
    [InlineData("v1.2.3", 1, 2, 3)]
    [InlineData("0.1.0-rc.6", 0, 1, 0)]
    [InlineData("10.20.30+build.5", 10, 20, 30)]
    public void ParsesValidVersions(string raw, int major, int minor, int patch)
    {
        Assert.True(StrictSemanticVersion.TryParse(raw, out var version));
        Assert.Equal(major, version!.Major);
        Assert.Equal(minor, version.Minor);
        Assert.Equal(patch, version.Patch);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1.2")]
    [InlineData("1.2.3rc")]
    [InlineData("1.2foo")]
    [InlineData("1.2.3-01")]     // numeric identifier with leading zero
    [InlineData("1.2.3-")]       // empty prerelease
    [InlineData("1.2.3+")]       // empty build metadata
    [InlineData("1.2.3.4")]
    public void RejectsInvalidVersions(string raw)
    {
        Assert.False(StrictSemanticVersion.TryParse(raw, out _));
    }

    [Theory]
    [InlineData("1.2.3", "1.2.4")]
    [InlineData("1.2.3", "1.3.0")]
    [InlineData("1.2.3", "2.0.0")]
    [InlineData("1.0.0-rc.1", "1.0.0")]   // prerelease sorts below release
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1")]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.beta")]
    [InlineData("1.0.0-1", "1.0.0-alpha")] // numeric < alphanumeric
    [InlineData("1.0.0-rc.6", "1.0.0-rc.10")]
    public void OrdersVersions(string lower, string higher)
    {
        Assert.True(StrictSemanticVersion.TryParse(lower, out var l));
        Assert.True(StrictSemanticVersion.TryParse(higher, out var h));
        Assert.True(l! < h!, $"{lower} should be lower than {higher}");
        Assert.True(h! > l!);
    }

    [Fact]
    public void BuildMetadataIsIgnoredForOrdering()
    {
        Assert.True(StrictSemanticVersion.TryParse("1.0.0+a", out var a));
        Assert.True(StrictSemanticVersion.TryParse("1.0.0+b", out var b));
        Assert.Equal(a, b);
    }
}