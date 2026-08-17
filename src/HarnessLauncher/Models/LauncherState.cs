using System.Text.Json.Serialization;

namespace HarnessLauncher.Models;

public abstract record LauncherPhase
{
    public sealed record Starting : LauncherPhase;
    public sealed record Ready(Uri Endpoint) : LauncherPhase;
    public sealed record Stopped : LauncherPhase;
    public sealed record Busy(string Operation) : LauncherPhase;
    public sealed record RuntimeMissing(string Message) : LauncherPhase;
    public sealed record Failed(string Message) : LauncherPhase;

    public string Title => this switch
    {
        Starting => "Starting DeepSeek Harness",
        Ready => "DeepSeek Harness Running",
        Stopped => "DeepSeek Harness Stopped",
        Busy(var operation) => operation,
        RuntimeMissing => "DeepSeek Harness Runtime Not Found",
        Failed => "DeepSeek Harness Failed to Start",
        _ => "DeepSeek Harness",
    };

    public bool IsReady => this is Ready;
}

public enum PluginRuntimeState
{
    Running,
    Stopped,
    Starting,
    Stopping,
    Error,
}

public abstract record RuntimeUpdateState
{
    public sealed record Idle : RuntimeUpdateState;
    public sealed record Checking : RuntimeUpdateState;
    public sealed record Available(string RuntimeId) : RuntimeUpdateState;
    public sealed record Downloaded(string Path) : RuntimeUpdateState;
    public sealed record UpToDate : RuntimeUpdateState;
    public sealed record Failed(string Message) : RuntimeUpdateState;
}

public abstract record AppUpdateState
{
    public sealed record Idle : AppUpdateState;
    public sealed record Checking : AppUpdateState;
    public sealed record Available(string Version, Uri Url) : AppUpdateState;
    public sealed record UpToDate : AppUpdateState;
    public sealed record Failed(string Message) : AppUpdateState;
}

public sealed class DeepSeekBalanceInfo
{
    [JsonPropertyName("currency")] public string Currency { get; set; } = "";
    [JsonPropertyName("total_balance")] public object? TotalBalanceRaw { get; set; }
    [JsonPropertyName("granted_balance")] public object? GrantedBalanceRaw { get; set; }
    [JsonPropertyName("topped_up_balance")] public object? ToppedUpBalanceRaw { get; set; }

    private static string LossyString(object? value) => value switch
    {
        null => "-",
        string s => s,
        System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.String } e => e.GetString() ?? "-",
        System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.Number } e => e.GetRawText(),
        _ => value.ToString() ?? "-",
    };

    public string TotalBalance => LossyString(TotalBalanceRaw);
    public string GrantedBalance => LossyString(GrantedBalanceRaw);
    public string ToppedUpBalance => LossyString(ToppedUpBalanceRaw);
}

public sealed class DeepSeekBalanceResponse
{
    [JsonPropertyName("is_available")] public bool IsAvailable { get; set; }
    [JsonPropertyName("balance_infos")] public List<DeepSeekBalanceInfo> BalanceInfos { get; set; } = new();
}

public enum DeepSeekBalanceTone
{
    Unknown,
    Healthy,
    Warning,
    Critical,
}

public static class DeepSeekBalanceToneEvaluator
{
    public static DeepSeekBalanceTone Evaluate(IReadOnlyList<DeepSeekBalanceInfo> balanceInfos)
    {
        var cny = balanceInfos.FirstOrDefault(info => info.Currency.Equals("CNY", StringComparison.OrdinalIgnoreCase));
        if (cny is null || !decimal.TryParse(cny.TotalBalance, System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out var amount))
        {
            return DeepSeekBalanceTone.Unknown;
        }
        if (amount >= 100m) return DeepSeekBalanceTone.Healthy;
        if (amount >= 50m) return DeepSeekBalanceTone.Warning;
        return DeepSeekBalanceTone.Critical;
    }
}

public abstract record DeepSeekBalanceState
{
    public sealed record NotConfigured : DeepSeekBalanceState;
    public sealed record Loading : DeepSeekBalanceState;
    public sealed record Available(List<DeepSeekBalanceInfo> Infos) : DeepSeekBalanceState;
    public sealed record Failed(string Message) : DeepSeekBalanceState;
}

public sealed class HarnessPlugin
{
    public required string Id { get; init; }
    public required string Version { get; init; }
    public required IReadOnlyList<string> BundleRowIds { get; init; }
    public required bool IsDisabled { get; init; }

    public string Name => Id;
    public PluginRuntimeState State => IsDisabled ? PluginRuntimeState.Stopped : PluginRuntimeState.Running;
}
