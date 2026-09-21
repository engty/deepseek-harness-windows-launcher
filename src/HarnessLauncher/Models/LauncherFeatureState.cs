using System.Globalization;

namespace HarnessLauncher.Models;

public enum DeepSeekDiscountPeriod
{
    Peak,
    Discount,
}

public static class DeepSeekDiscountPeriodExtensions
{
    public static DeepSeekDiscountPeriod Current(DateTimeOffset? now = null)
    {
        var time = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(
            now ?? DateTimeOffset.UtcNow, "China Standard Time");
        var weekday = time.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday;
        var peak = weekday && ((time.Hour >= 9 && time.Hour < 12) ||
                               (time.Hour >= 14 && time.Hour < 18));
        return peak ? DeepSeekDiscountPeriod.Peak : DeepSeekDiscountPeriod.Discount;
    }

    public static string MultiplierText(this DeepSeekDiscountPeriod period) =>
        period == DeepSeekDiscountPeriod.Peak ? "1.0x" : "0.5x";

    public static string Label(this DeepSeekDiscountPeriod period) =>
        period == DeepSeekDiscountPeriod.Peak ? "高峰时段" : "折扣时段";
}

public enum RuntimeUpdateStage
{
    Preparing,
    Downloading,
    Verifying,
    Packaging,
    Activating,
}

public sealed class PluginCacheEntry
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public long SizeBytes { get; init; }
    public required string Kind { get; init; }
}

public sealed record PluginCacheCleanupReport(
    long RemovedBytes,
    long PrunedBytes,
    bool PnpmPruneSucceeded)
{
    public string Summary
    {
        get
        {
            var total = RemovedBytes + PrunedBytes;
            var megabytes = total / 1024d / 1024d;
            var size = megabytes >= 1 ? $"{megabytes:0.0} MB" : $"{total / 1024d:0} KB";
            return PnpmPruneSucceeded
                ? $"已清理 {size} 缓存。"
                : $"已清理 {size} App 缓存；共享 pnpm 缓存未完成回收。";
        }
    }
}

public sealed record OfficialHarnessVersionResult(string Version, Uri PackageUrl)
{
    public bool IsUpdateAvailable(string? current)
    {
        if (!StrictSemanticVersion.TryParse(Version, out var latest)) return false;
        if (string.IsNullOrWhiteSpace(current) || !StrictSemanticVersion.TryParse(current, out var old))
            return true;
        return latest! > old!;
    }
}
