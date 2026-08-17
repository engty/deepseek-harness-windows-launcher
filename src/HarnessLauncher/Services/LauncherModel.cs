using System.ComponentModel;
using System.Runtime.CompilerServices;
using HarnessLauncher.Models;
using HarnessLauncher.Support;

namespace HarnessLauncher.Services;

/// <summary>
/// UI interaction surface used by LauncherModel. Implemented by the WPF
/// layer (dialogs, message boxes, opening URLs). Every member is called on
/// the UI thread.
/// </summary>
public interface ILauncherDialogs
{
    string? PromptForText(string title, string message, string placeholder);
    string? PromptForSecret(string title, string message);
    bool Confirm(string title, string message);
    void Info(string title, string message);
    IReadOnlyList<HarnessPlugin> PromptForPluginSelection(
        string title, string operation, IReadOnlyList<HarnessPlugin> plugins);
    void OpenUrl(Uri url);
    void RevealFile(string path);
}

/// <summary>
/// Windows port of LauncherModel: owns the Harness lifecycle, plugin
/// mutations, balance, Runtime/App updates and crash recovery. All public
/// members must be called on the UI thread.
/// </summary>
public sealed class LauncherModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private LauncherPhase _phase = new LauncherPhase.Stopped();
    public LauncherPhase Phase { get => _phase; private set => Set(ref _phase, value); }

    private List<HarnessPlugin> _plugins = new();
    public List<HarnessPlugin> Plugins { get => _plugins; private set => Set(ref _plugins, value); }

    private readonly Dictionary<string, PluginRuntimeState> _pluginOperationStates = new();

    private string? _runtimePath;
    public string? RuntimePath { get => _runtimePath; private set => Set(ref _runtimePath, value); }

    private string? _runtimeVersion;
    public string? RuntimeVersion { get => _runtimeVersion; private set => Set(ref _runtimeVersion, value); }

    private string? _lastError;
    public string? LastError { get => _lastError; private set => Set(ref _lastError, value); }

    private RuntimeUpdateState _updateState = new RuntimeUpdateState.Idle();
    public RuntimeUpdateState UpdateState { get => _updateState; private set { Set(ref _updateState, value); Notify(nameof(HasAvailableRuntimeUpdate)); } }

    private AppUpdateState _appUpdateState = new AppUpdateState.Idle();
    public AppUpdateState AppUpdateState { get => _appUpdateState; private set => Set(ref _appUpdateState, value); }

    private DeepSeekBalanceState _balanceState = new DeepSeekBalanceState.NotConfigured();
    public DeepSeekBalanceState BalanceState
    {
        get => _balanceState;
        private set
        {
            Set(ref _balanceState, value);
            Notify(nameof(BalanceDisplayText));
            Notify(nameof(BalanceAmountDisplayText));
            Notify(nameof(BalanceTone));
        }
    }

    private bool _isBalanceConfigured;
    public bool IsBalanceConfigured { get => _isBalanceConfigured; private set => Set(ref _isBalanceConfigured, value); }

    /// <summary>
    /// True while a slot-mutating operation (plugin install/remove/start/stop,
    /// restart, Runtime update activation) is running. New such operations are
    /// rejected instead of interleaving.
    /// </summary>
    private bool _isOperationInProgress;
    public bool IsOperationInProgress { get => _isOperationInProgress; private set => Set(ref _isOperationInProgress, value); }

    public AppPaths Paths { get; }
    private readonly RuntimeLocator _locator;
    private readonly ProfileManager _profileManager;
    private readonly HarnessProcessController _processController;
    private readonly PluginCommandRunner _pluginRunner;
    private readonly RuntimeUpdateService _updateService;
    private readonly AppUpdateService _appUpdateService;
    private readonly RuntimeArchiveInstaller _runtimeInstaller;
    private readonly ToolchainInstaller _toolchainInstaller;
    private readonly DataSlotManager _dataSlotManager;
    private readonly RuntimePreflightService _runtimePreflight;
    private readonly DeepSeekBalanceService _balanceService;
    private readonly DeepSeekCredentialStore _deepSeekCredentialStore = new();
    private readonly CredentialStore _balanceCredentialStore;
    private readonly ILauncherDialogs _dialogs;

    private RuntimeManifest? _latestManifest;
    private CancellationTokenSource? _balanceRefreshCts;
    private CancellationTokenSource? _updateCheckCts;
    private int _consecutiveCrashCount;
    /// <summary>
    /// Bumped whenever a user-facing operation changes the desired state. The
    /// delayed crash-recovery task only restarts Harness when the generation
    /// is unchanged, so a manual stop/restart/mutation in the meantime
    /// cancels the automatic recovery instead of "ghost restarting".
    /// </summary>
    private Guid _operationGeneration = Guid.NewGuid();
    private CancellationTokenSource? _crashRecoveryCts;
    private Guid _balanceRequestId = Guid.NewGuid();
    private Guid _updateRequestId = Guid.NewGuid();

    public LauncherModel(AppPaths? paths = null, ILauncherDialogs? dialogs = null)
    {
        Paths = paths ?? new AppPaths();
        _dialogs = dialogs ?? new NullDialogs();
        _locator = new RuntimeLocator(paths: Paths);
        _profileManager = new ProfileManager(Paths);
        _processController = new HarnessProcessController();
        _pluginRunner = new PluginCommandRunner();
        _updateService = new RuntimeUpdateService(paths: Paths);
        _appUpdateService = new AppUpdateService();
        _runtimeInstaller = new RuntimeArchiveInstaller();
        _toolchainInstaller = new ToolchainInstaller();
        _dataSlotManager = new DataSlotManager();
        _runtimePreflight = new RuntimePreflightService();
        _balanceService = new DeepSeekBalanceService();
        _balanceCredentialStore = new CredentialStore(
            Paths.ProtectedCredentialStore,
            AppPaths.BundleIdentifier + ".credentials.v2");
        _processController.OnUnexpectedTermination = HandleUnexpectedTermination;
        try
        {
            Paths.Prepare();
            AppLogger.Configure(Paths.Logs);
            _dataSlotManager.RecoverPendingTransaction(Paths);
            Plugins = _profileManager.Refresh();
            // Restore the binding state from the DPAPI store. A non-
            // interactive read is safe at launch and keeps the user's API key
            // bound across relaunches.
            try
            {
                IsBalanceConfigured = SynchronizeDeepSeekCredential() is not null;
            }
            catch (Exception error)
            {
                // Keep launch available if a malformed credential document is
                // present; the next explicit key replacement repairs it.
                IsBalanceConfigured = _balanceCredentialStore.Read() is { Length: > 0 };
                LastError = error.Message;
            }
        }
        catch (Exception error)
        {
            LastError = error.Message;
            Phase = new LauncherPhase.Failed(error.Message);
        }
    }

    public Uri? EndpointUrl => Phase is LauncherPhase.Ready ready ? ready.Endpoint : null;

    public string CurrentAppVersion => LauncherVersion.Current;

    public bool IsHarnessRunning => _processController.IsRunning;

    public PluginRuntimeState PluginStatusFor(HarnessPlugin plugin) =>
        _pluginOperationStates.TryGetValue(plugin.Id, out var state) ? state : plugin.State;

    public bool CanRestart => Phase is not (LauncherPhase.Starting or LauncherPhase.Busy);

    public void WebViewDidFail(string message)
    {
        LastError = $"Harness Web UI: {message}";
        AppLogger.Log(AppLogger.Level.Error, "launcher", $"Harness Web UI failed: {message}");
    }

    public async Task StartIfNeededAsync()
    {
        if (Phase.IsReady || Phase is LauncherPhase.Starting || _processController.IsRunning) return;
        await StartAsync();
    }

    public async Task StartAsync()
    {
        if (_processController.IsRunning) return;
        Phase = new LauncherPhase.Starting();
        LastError = null;
        try
        {
            Paths.Prepare();
            _dataSlotManager.RecoverPendingTransaction(Paths);
            var installation = _locator.Locate();
            RuntimePath = installation.Executable;
            RuntimeVersion = installation.Version;
            var url = await _processController.StartAsync(
                installation, Paths, _profileManager.OverlayPathIfNeeded());
            Plugins = _profileManager.Refresh();
            Phase = new LauncherPhase.Ready(url);
            _consecutiveCrashCount = 0;
            AppLogger.Log(AppLogger.Level.Info, "launcher", $"Harness ready at {url}");
            ScheduleAutomaticUpdateCheck();
            ScheduleBalanceRefresh();
        }
        catch (Exception error)
        {
            LastError = error.Message;
            Phase = error is RuntimeLocatorException
                ? new LauncherPhase.RuntimeMissing(error.Message)
                : new LauncherPhase.Failed(error.Message);
            AppLogger.Log(AppLogger.Level.Error, "launcher", $"Harness start failed: {error.Message}");
        }
    }

    public async Task StopAsync()
    {
        BumpOperationGeneration();
        _crashRecoveryCts?.Cancel();
        _crashRecoveryCts = null;
        // A plugin command must not outlive the launcher: pnpm and lifecycle
        // scripts would otherwise keep running as orphans.
        _pluginRunner.CancelActiveCommand();
        if (!_processController.IsRunning)
        {
            Phase = new LauncherPhase.Stopped();
            return;
        }
        Phase = new LauncherPhase.Busy("Stopping Harness");
        await _processController.StopAsync();
        Phase = new LauncherPhase.Stopped();
    }

    public async Task RestartAsync()
    {
        if (!BeginExclusiveOperation()) return;
        try
        {
            await StopAsync();
            await StartAsync();
        }
        finally
        {
            EndExclusiveOperation();
        }
    }

    private bool BeginExclusiveOperation(bool notifyBusy = true)
    {
        if (IsOperationInProgress)
        {
            if (notifyBusy)
            {
                _dialogs.Info("已有操作正在进行", "请等待当前 DeepSeek Harness 操作完成后再试。");
            }
            return false;
        }
        IsOperationInProgress = true;
        BumpOperationGeneration();
        return true;
    }

    private void EndExclusiveOperation() => IsOperationInProgress = false;

    private void BumpOperationGeneration() => _operationGeneration = Guid.NewGuid();

    public async Task SetPluginsEnabledAsync(IReadOnlyList<HarnessPlugin> selectedPlugins, bool enabled)
    {
        if (selectedPlugins.Count == 0) return;
        if (!BeginExclusiveOperation()) return;
        try
        {
            var wasRunning = _processController.IsRunning;
            foreach (var plugin in selectedPlugins)
            {
                _pluginOperationStates[plugin.Id] = enabled
                    ? PluginRuntimeState.Starting : PluginRuntimeState.Stopping;
            }
            var names = string.Join(", ", selectedPlugins.Select(p => p.Name));
            Phase = new LauncherPhase.Busy(enabled ? $"正在启用 {names}" : $"正在停用 {names}");
            if (wasRunning) await _processController.StopAsync();

            try
            {
                _profileManager.SetEnabled(selectedPlugins, enabled);
                Plugins = _profileManager.Refresh();
                foreach (var plugin in selectedPlugins)
                {
                    _pluginOperationStates[plugin.Id] = enabled
                        ? PluginRuntimeState.Running : PluginRuntimeState.Stopped;
                }
                if (wasRunning) await StartAsync();
                else Phase = new LauncherPhase.Stopped();
            }
            catch (Exception error)
            {
                LastError = error.Message;
                foreach (var plugin in selectedPlugins)
                {
                    _pluginOperationStates[plugin.Id] = PluginRuntimeState.Error;
                }
                Phase = new LauncherPhase.Failed(error.Message);
                if (wasRunning) await StartAsync();
            }
        }
        finally
        {
            EndExclusiveOperation();
        }
    }

    public void InstallPluginPrompt()
    {
        var command = _dialogs.PromptForText(
            "安装 Harness 插件",
            "粘贴官方安装命令。只接受 dsh plugin --profile web add <plugin-spec>，不会通过 shell 执行。",
            "例如 dsh plugin --profile web add dsh-llm-codex");
        if (command is null) return;
        try
        {
            var arguments = PluginCommandParser.ParseInstallCommand(command);
            var specs = string.Join(' ', arguments.Skip(1));
            var installation = _locator.Locate();
            var dependencyPlan = _pluginRunner.DependencyPlan(installation, Paths, arguments);
            if (!ConfirmPluginMutation("安装", specs, dependencyPlan)) return;
            _ = MutatePluginAsync(arguments, "正在安装插件", "安装", dependencyPlan);
        }
        catch (Exception error)
        {
            _dialogs.Info("无法准备插件安装", error.Message);
        }
    }

    public void RemovePluginPrompt()
    {
        var selected = PromptForPluginSelection("卸载 Harness 插件", "卸载");
        if (selected.Count == 0) return;
        var names = string.Join(", ", selected.Select(p => p.Name));
        var arguments = new[] { "remove" }.Concat(selected.Select(p => p.Id)).ToList();
        try
        {
            var installation = _locator.Locate();
            var dependencyPlan = _pluginRunner.DependencyPlan(installation, Paths, arguments);
            if (!ConfirmPluginMutation("卸载", names, dependencyPlan)) return;
            _ = MutatePluginAsync(arguments, "正在卸载插件", "卸载", dependencyPlan);
        }
        catch (Exception error)
        {
            _dialogs.Info("无法准备插件卸载", error.Message);
        }
    }

    public void StopPluginPrompt()
    {
        var selected = PromptForPluginSelection("停用 Harness 插件", "停用");
        if (selected.Count == 0) return;
        var names = string.Join(", ", selected.Select(p => p.Name));
        if (!ConfirmPluginMutation("停用", names)) return;
        _ = SetPluginsEnabledAsync(selected, enabled: false);
    }

    public async Task StartPluginAsync(HarnessPlugin plugin) =>
        await SetPluginsEnabledAsync(new[] { plugin }, enabled: true);

    public async Task StopPluginAsync(HarnessPlugin plugin) =>
        await SetPluginsEnabledAsync(new[] { plugin }, enabled: false);

    public void CheckForUpdates() => _ = CheckForUpdatesAsync(presentResult: true);

    public void CheckForAppUpdates() => _ = CheckForAppUpdatesAsync(presentResult: true);

    public void DownloadLatestUpdate() => _ = DownloadLatestUpdateIfAvailableAsync();

    public void ConfigureDeepSeekBalance(bool forcePrompt = false)
    {
        if (IsBalanceConfigured && !forcePrompt)
        {
            _ = RefreshBalanceAsync();
            return;
        }

        var apiKey = _dialogs.PromptForSecret(
            IsBalanceConfigured ? "更换 DeepSeek API Key" : "配置 DeepSeek API Key",
            "同一个 API Key 会同时绑定 DeepSeek 模型和余额查询：一份用 Windows DPAPI 加密保存（仅当前 Windows 用户可解密），另一份同步到 Harness 标准凭据文件。不会写入日志或诊断文件。");
        if (apiKey is null) return;

        try
        {
            // The credential FILE is Harness's source of truth, so write and
            // verify it first. A DPAPI failure afterwards must not leave the
            // file silently un-updated while the UI claims success.
            _deepSeekCredentialStore.Write(apiKey, Paths.DshHome);
            SensitiveDataRedactor.RegisterLiteralSecret(apiKey);
            try
            {
                _balanceCredentialStore.Save(apiKey);
            }
            catch (Exception error)
            {
                AppLogger.Log(AppLogger.Level.Error, "launcher",
                    $"DeepSeek API Key saved to the credential file but not to the DPAPI store: {error.Message}");
            }
            IsBalanceConfigured = true;
            ScheduleBalanceRefresh();
        }
        catch (Exception error)
        {
            BalanceState = new DeepSeekBalanceState.Failed(error.Message);
            _dialogs.Info("无法保存 DeepSeek API Key", error.Message);
        }
    }

    public async Task RefreshBalanceAsync()
    {
        var requestId = Guid.NewGuid();
        _balanceRequestId = requestId;
        string? apiKey;
        try
        {
            apiKey = SynchronizeDeepSeekCredential();
        }
        catch (Exception error)
        {
            BalanceState = new DeepSeekBalanceState.Failed(error.Message);
            return;
        }

        if (string.IsNullOrEmpty(apiKey))
        {
            // Do not forget a valid binding just because a transient read
            // failed. Only an explicit replacement changes the binding.
            IsBalanceConfigured = false;
            BalanceState = new DeepSeekBalanceState.NotConfigured();
            return;
        }

        BalanceState = new DeepSeekBalanceState.Loading();
        try
        {
            var response = await _balanceService.FetchAsync(apiKey);
            // A slow earlier request must not overwrite the result of a newer
            // one (for example right after the user replaced the key).
            if (requestId != _balanceRequestId) return;
            BalanceState = new DeepSeekBalanceState.Available(response.BalanceInfos);
        }
        catch (Exception error)
        {
            if (requestId != _balanceRequestId) return;
            BalanceState = new DeepSeekBalanceState.Failed(error.Message);
        }
    }

    public string BalanceDisplayText => BalanceState switch
    {
        DeepSeekBalanceState.NotConfigured => "余额未设置",
        DeepSeekBalanceState.Loading => "余额查询中…",
        DeepSeekBalanceState.Available(var infos) =>
            infos.Count == 0 ? "余额不可用" : $"余额 {string.Join(" / ", infos.Select(BalanceAmountFor))}",
        DeepSeekBalanceState.Failed => "余额查询失败",
        _ => "余额不可用",
    };

    public string? BalanceAmountDisplayText => BalanceState is DeepSeekBalanceState.Available(var infos) &&
        infos.Count > 0
            ? string.Join(" / ", infos.Select(BalanceAmountFor))
            : null;

    public DeepSeekBalanceTone BalanceTone => BalanceState is DeepSeekBalanceState.Available(var infos)
        ? DeepSeekBalanceToneEvaluator.Evaluate(infos)
        : DeepSeekBalanceTone.Unknown;

    public bool HasAvailableRuntimeUpdate => UpdateState is RuntimeUpdateState.Available;

    public void ExportDiagnostics()
    {
        var text = SensitiveDataRedactor.Redact($"""
            DeepSeek Harness
            Phase: {Phase.Title}
            Runtime: {RuntimePath ?? "not found"}
            Runtime version: {RuntimeVersion ?? "unknown"}
            DSH_HOME: {Paths.DshHome}
            Plugins: {string.Join(", ", Plugins.Select(p => p.Id))}
            Balance: {BalanceDisplayText}
            Error: {LastError ?? "none"}
            """);
        try
        {
            File.WriteAllText(Paths.DiagnosticsFile, text);
            _dialogs.RevealFile(Paths.DiagnosticsFile);
        }
        catch (Exception error)
        {
            LastError = error.Message;
        }
    }

    private async Task MutatePluginAsync(
        IReadOnlyList<string> arguments,
        string operation,
        string userOperation,
        PluginDependencyPlan dependencyPlan,
        IReadOnlyList<ToolchainRequirement>? additionalToolRequirements = null,
        IReadOnlyList<string>? allowedBuildScripts = null,
        bool? restartAfterMutation = null,
        bool attemptDependencyRecovery = true,
        bool attemptBuildScriptApproval = true,
        bool holdsExclusiveLock = false)
    {
        additionalToolRequirements ??= Array.Empty<ToolchainRequirement>();
        allowedBuildScripts ??= Array.Empty<string>();
        if (!holdsExclusiveLock && !BeginExclusiveOperation()) return;
        try
        {
            RuntimeInstallation installation;
            try
            {
                installation = _locator.Locate();
            }
            catch
            {
                const string message = "插件管理需要可执行的 Harness Runtime。";
                Phase = new LauncherPhase.RuntimeMissing(message);
                LastError = message;
                _dialogs.Info($"插件{userOperation}失败", message);
                return;
            }

            var wasRunning = restartAfterMutation ?? _processController.IsRunning;
            Phase = new LauncherPhase.Busy(operation);
            if (restartAfterMutation is null && wasRunning)
            {
                await _processController.StopAsync();
            }

            try
            {
                await _pluginRunner.MutateProfileAsync(
                    installation, Paths, arguments, dependencyPlan, allowedBuildScripts);
                Plugins = _profileManager.Refresh();
                if (wasRunning)
                {
                    await StartAsync();
                    if (!Phase.IsReady)
                    {
                        var restartError = LastError ?? "Harness 重启失败。";
                        LastError = $"插件{userOperation}已完成，但 Harness 重启失败：{restartError}";
                        _dialogs.Info($"插件{userOperation}完成，但 Harness 未启动", LastError);
                        return;
                    }
                }
                else
                {
                    Phase = new LauncherPhase.Stopped();
                }
                LastError = null;
                AppLogger.Log(AppLogger.Level.Info, "plugins", $"Plugin {userOperation} succeeded");
                _dialogs.Info($"插件{userOperation}完成", "DeepSeek Harness 的 web profile 配置已更新。");
            }
            catch (Exception error)
            {
                var message = error.Message;
                if (attemptBuildScriptApproval &&
                    error is PluginCommandException.BuildScriptsRequireApproval approval)
                {
                    if (!ConfirmBuildScriptApproval(approval.Packages))
                    {
                        await FinishPluginFailureAsync(
                            RedactOutput(approval.Message), userOperation, wasRunning);
                        return;
                    }
                    Phase = new LauncherPhase.Busy("正在准备 pnpm 构建权限");
                    try
                    {
                        var retryPlan = _pluginRunner.DependencyPlan(
                            installation, Paths, arguments, additionalToolRequirements);
                        await MutatePluginAsync(
                            arguments, operation, userOperation, retryPlan,
                            additionalToolRequirements, approval.Packages,
                            wasRunning, attemptDependencyRecovery: true,
                            attemptBuildScriptApproval: false, holdsExclusiveLock: true);
                        return;
                    }
                    catch (Exception retryError)
                    {
                        await FinishPluginFailureAsync(retryError.Message, userOperation, wasRunning);
                        return;
                    }
                }
                if (attemptDependencyRecovery &&
                    PluginDependencyService.InstallableRequirementFrom(message) is { } requirement &&
                    ToolchainCatalog.Bundled.ManifestFor(requirement) is { } manifest)
                {
                    var installPlan = new ToolchainInstallPlan(manifest,
                        Path.Combine(Paths.Toolchain, manifest.Id, manifest.Version));
                    if (!ConfirmToolchainInstallation(installPlan))
                    {
                        await FinishPluginFailureAsync(message, userOperation, wasRunning);
                        return;
                    }
                    Phase = new LauncherPhase.Busy($"正在准备 {manifest.Id}");
                    try
                    {
                        await _toolchainInstaller.InstallAsync(requirement, Paths,
                            progress: (completed, total) =>
                                AppLogger.Log(AppLogger.Level.Info, "plugins",
                                    $"Private dependency download {completed}/{total}"));
                        var newRequirements = additionalToolRequirements.Concat(new[] { requirement }).ToList();
                        var retryPlan = _pluginRunner.DependencyPlan(
                            installation, Paths, arguments, newRequirements);
                        await MutatePluginAsync(
                            arguments, operation, userOperation, retryPlan,
                            newRequirements, allowedBuildScripts,
                            wasRunning, attemptDependencyRecovery: false,
                            holdsExclusiveLock: true);
                        return;
                    }
                    catch (Exception retryError)
                    {
                        await FinishPluginFailureAsync(retryError.Message, userOperation, wasRunning);
                        return;
                    }
                }
                await FinishPluginFailureAsync(message, userOperation, wasRunning);
            }
        }
        finally
        {
            if (!holdsExclusiveLock) EndExclusiveOperation();
        }
    }

    private static string RedactOutput(string output) =>
        string.Join('\n', SensitiveDataRedactor.Redact(output).Split('\n').TakeLast(80));

    private async Task FinishPluginFailureAsync(string message, string userOperation, bool restartAfterMutation)
    {
        LastError = message;
        Phase = new LauncherPhase.Failed(message);
        if (restartAfterMutation)
        {
            await StartAsync();
        }
        // StartAsync clears transient state on a successful restart; retain
        // the mutation error so it remains visible in the launcher.
        LastError = message;
        AppLogger.Log(AppLogger.Level.Error, "plugins", $"Plugin {userOperation} failed: {message}");
        _dialogs.Info($"插件{userOperation}失败", message);
    }

    private void HandleUnexpectedTermination(string output)
    {
        if (!(Phase.IsReady || Phase is LauncherPhase.Busy(var op) && op == "Starting DeepSeek Harness"))
        {
            return;
        }
        _consecutiveCrashCount++;
        var message = string.IsNullOrEmpty(output) ? "Harness sidecar unexpectedly exited." : output;
        LastError = message;
        Phase = new LauncherPhase.Failed($"Harness sidecar unexpectedly exited.\n{message}");
        var generation = _operationGeneration;
        if (_consecutiveCrashCount > 3)
        {
            _crashRecoveryCts?.Cancel();
            _ = RecoverLastKnownGoodRuntimeAsync();
            return;
        }
        var retryDelay = TimeSpan.FromSeconds(_consecutiveCrashCount);
        _crashRecoveryCts?.Cancel();
        var cts = _crashRecoveryCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(retryDelay, cts.Token);
                // Only restart when nothing else changed the desired state
                // while we slept; otherwise the automatic recovery would
                // override a manual stop/restart/mutation ("ghost restart").
                if (_operationGeneration != generation) return;
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(StartAsync);
            }
            catch (TaskCanceledException) { }
        });
    }

    private async Task RecoverLastKnownGoodRuntimeAsync()
    {
        if (!BeginExclusiveOperation(notifyBusy: false))
        {
            AppLogger.Log(AppLogger.Level.Error, "launcher",
                "Skipped last-known-good Runtime recovery because another operation is in progress.");
            return;
        }
        try
        {
            RuntimeInstallation fallback;
            try { fallback = _locator.LocateLastKnownGood(); }
            catch { return; }
            try
            {
                _runtimeInstaller.RestoreLastKnownGood(Paths);
                var url = await _processController.StartAsync(
                    fallback, Paths, _profileManager.OverlayPathIfNeeded());
                RuntimePath = fallback.Executable;
                RuntimeVersion = fallback.Version;
                Phase = new LauncherPhase.Ready(url);
                LastError = "已回退到 last-known-good DeepSeek Harness Runtime。";
                _consecutiveCrashCount = 0;
            }
            catch (Exception error)
            {
                LastError = $"Runtime 回退失败：{error.Message}";
            }
        }
        finally
        {
            EndExclusiveOperation();
        }
    }

    private void ScheduleAutomaticUpdateCheck()
    {
        _updateCheckCts?.Cancel();
        var cts = _updateCheckCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), cts.Token);
                while (!cts.IsCancellationRequested)
                {
                    await System.Windows.Application.Current.Dispatcher.InvokeAsync(
                        () => CheckForUpdatesAsync(presentResult: false));
                    await Task.Delay(TimeSpan.FromHours(6), cts.Token);
                }
            }
            catch (TaskCanceledException) { }
        });
    }

    private void ScheduleBalanceRefresh()
    {
        if (!IsBalanceConfigured)
        {
            _balanceRefreshCts?.Cancel();
            _balanceRefreshCts = null;
            return;
        }
        _balanceRefreshCts?.Cancel();
        var cts = _balanceRefreshCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(RefreshBalanceAsync);
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(1), cts.Token);
                }
                catch (TaskCanceledException) { return; }
            }
        });
    }

    /// <summary>
    /// Keep the native balance lookup and Harness's Web Models page on one
    /// credential. The standard Harness file is the source of truth when it
    /// already contains a key; the DPAPI value is used only as a fallback
    /// when the file is missing.
    /// </summary>
    private string? SynchronizeDeepSeekCredential()
    {
        string? fileValue;
        try
        {
            fileValue = _deepSeekCredentialStore.Read(Paths.DshHome);
        }
        catch (Exception error)
        {
            // A malformed credential document must not brick the balance
            // feature while a valid DPAPI copy exists: fall back and repair
            // the file below.
            AppLogger.Log(AppLogger.Level.Error, "launcher",
                $"Credential file is unreadable, falling back to DPAPI store: {error.Message}");
            fileValue = null;
        }
        if (!string.IsNullOrEmpty(fileValue))
        {
            SensitiveDataRedactor.RegisterLiteralSecret(fileValue);
            IsBalanceConfigured = true;
            return fileValue;
        }

        var storeValue = _balanceCredentialStore.Read();
        if (string.IsNullOrEmpty(storeValue))
        {
            IsBalanceConfigured = false;
            return null;
        }
        _deepSeekCredentialStore.Write(storeValue, Paths.DshHome);
        SensitiveDataRedactor.RegisterLiteralSecret(storeValue);
        IsBalanceConfigured = true;
        return storeValue;
    }

    private async Task CheckForUpdatesAsync(bool presentResult)
    {
        var requestId = Guid.NewGuid();
        _updateRequestId = requestId;
        UpdateState = new RuntimeUpdateState.Checking();
        try
        {
            var result = await _updateService.CheckAsync(RuntimeVersion);
            if (requestId != _updateRequestId) return;
            if (result.IsUpdateAvailable)
            {
                _latestManifest = result.Manifest;
                UpdateState = new RuntimeUpdateState.Available(result.Manifest.RuntimeId);
                if (presentResult) PresentUpdateAlert(result);
            }
            else
            {
                _latestManifest = null;
                UpdateState = new RuntimeUpdateState.UpToDate();
                if (presentResult)
                {
                    _dialogs.Info("Harness Runtime 已是最新", result.Manifest.RuntimeId);
                }
            }
        }
        catch (Exception error)
        {
            if (requestId != _updateRequestId) return;
            _latestManifest = null;
            UpdateState = new RuntimeUpdateState.Failed(error.Message);
            if (presentResult)
            {
                var title = error is RuntimeManifestException manifestError &&
                    manifestError.IsFeedNotConfigured
                        ? "Harness Runtime 更新源未配置"
                        : "无法检查 Harness Runtime 更新";
                _dialogs.Info(title, error.Message);
            }
        }
    }

    private async Task CheckForAppUpdatesAsync(bool presentResult)
    {
        AppUpdateState = new AppUpdateState.Checking();
        try
        {
            var result = await _appUpdateService.CheckAsync(CurrentAppVersion);
            if (result.IsUpdateAvailable)
            {
                AppUpdateState = new AppUpdateState.Available(result.LatestVersion, result.ReleaseUrl);
                if (presentResult) PresentAppUpdateAlert(result);
            }
            else
            {
                AppUpdateState = new AppUpdateState.UpToDate();
                if (presentResult)
                {
                    _dialogs.Info("DeepSeek Harness App 已是最新", $"当前版本：{result.CurrentVersion}");
                }
            }
        }
        catch (Exception error)
        {
            AppUpdateState = new AppUpdateState.Failed(error.Message);
            if (presentResult)
            {
                _dialogs.Info("无法检查 DeepSeek Harness App 更新", error.Message);
            }
        }
    }

    private async Task DownloadLatestUpdateIfAvailableAsync()
    {
        if (_latestManifest is null)
        {
            await CheckForUpdatesAsync(presentResult: false);
            if (_latestManifest is null) return;
        }
        await DownloadLatestUpdateAsync(_latestManifest);
    }

    private async Task DownloadLatestUpdateAsync(RuntimeManifest manifest)
    {
        if (!BeginExclusiveOperation()) return;
        try
        {
            UpdateState = new RuntimeUpdateState.Checking();
            try
            {
                var destination = Path.Combine(Paths.Caches, "updates", "staging");
                var artifactPath = await _updateService.DownloadAsync(manifest, destination);
                if (!PresentRuntimeActivationConfirmation(manifest, artifactPath))
                {
                    UpdateState = new RuntimeUpdateState.Available(manifest.RuntimeId);
                    return;
                }
                await ActivateRuntimeUpdateAsync(manifest, artifactPath);
            }
            catch (Exception error)
            {
                UpdateState = new RuntimeUpdateState.Failed(error.Message);
                _dialogs.Info("Harness 更新下载失败", error.Message);
            }
        }
        finally
        {
            EndExclusiveOperation();
        }
    }

    private async Task ActivateRuntimeUpdateAsync(RuntimeManifest manifest, string artifactPath)
    {
        var wasRunning = _processController.IsRunning;
        RuntimeInstallation? previousInstallation = null;
        try { previousInstallation = _locator.Locate(); } catch { }
        RuntimeActivation? activation = null;
        DataSlotActivation? dataActivation = null;
        Phase = new LauncherPhase.Busy("Updating DeepSeek Harness");
        if (wasRunning) await _processController.StopAsync();

        try
        {
            var candidateSlot = await _dataSlotManager.CloneActiveSlotAsync(Paths);
            var newActivation = await _runtimeInstaller.ActivateAsync(
                manifest, artifactPath, Paths, previousInstallation);
            activation = newActivation;
            RuntimePath = newActivation.Installation.Executable;
            RuntimeVersion = newActivation.Installation.Version ?? manifest.Harness.Version;

            var basePreflightRoot = Path.Combine(
                Paths.Caches, "updates", "base-preflight", Guid.NewGuid().ToString("N"));
            try
            {
                await _runtimePreflight.RunAsync(
                    newActivation.Installation, Paths,
                    Path.Combine(basePreflightRoot, "dsh-home"),
                    basePreflightRoot);
            }
            finally
            {
                try { if (Directory.Exists(basePreflightRoot)) Directory.Delete(basePreflightRoot, true); }
                catch { }
            }

            // Always boot the new Runtime against a clone of the user's real
            // profile, even when the App was stopped before the update.
            var candidateController = new HarnessProcessController();
            try
            {
                await candidateController.StartAsync(
                    newActivation.Installation, Paths, _profileManager.OverlayPathIfNeeded(),
                    dshHomeOverride: Path.Combine(candidateSlot, "dsh-home"),
                    currentDirectoryOverride: candidateSlot);
                await candidateController.StopAsync();
            }
            catch (Exception error)
            {
                await candidateController.StopAsync();
                throw HarnessProcessException.FailedToLaunch(
                    $"Runtime 用户 profile 预检失败：{error.Message}");
            }

            dataActivation = _dataSlotManager.Activate(candidateSlot, Paths);
            if (wasRunning)
            {
                var url = await _processController.StartAsync(
                    newActivation.Installation, Paths, _profileManager.OverlayPathIfNeeded());
                Phase = new LauncherPhase.Ready(url);
            }
            else
            {
                Phase = new LauncherPhase.Stopped();
            }
            // Remove versions that are no longer referenced by any pointer.
            _runtimeInstaller.CleanupOrphanedRuntimes(Paths);
            UpdateState = new RuntimeUpdateState.Downloaded(artifactPath);
            _dialogs.Info(
                "DeepSeek Harness 已更新",
                $"Runtime {manifest.Harness.Version} 已完成激活，并通过启动检查。");
        }
        catch (Exception error)
        {
            if (dataActivation is not null)
            {
                try { _dataSlotManager.Rollback(dataActivation, Paths); } catch { }
            }
            if (activation is not null)
            {
                try { _runtimeInstaller.Rollback(activation, Paths); } catch { }
            }
            LastError = error.Message;
            UpdateState = new RuntimeUpdateState.Failed(error.Message);
            Phase = new LauncherPhase.Failed($"DeepSeek Harness 更新失败。\n{error.Message}");

            if (wasRunning && previousInstallation is not null)
            {
                try
                {
                    var url = await _processController.StartAsync(
                        previousInstallation, Paths, _profileManager.OverlayPathIfNeeded());
                    RuntimePath = previousInstallation.Executable;
                    RuntimeVersion = previousInstallation.Version;
                    Phase = new LauncherPhase.Ready(url);
                }
                catch (Exception restartError)
                {
                    LastError = $"更新失败，旧 Runtime 也无法恢复：{restartError.Message}";
                }
            }
        }
    }

    private bool PresentRuntimeActivationConfirmation(RuntimeManifest manifest, string artifactPath) =>
        _dialogs.Confirm(
            "确认更新 DeepSeek Harness？",
            $"版本：{manifest.Harness.Version}\n\nartifact 已通过 HTTPS、大小和 SHA-256 校验，并暂存于：\n{artifactPath}\n\n当前更新源不使用公钥签名，请确认该 feed 属于你信任的发布方。确认后将优雅停止当前 Harness，激活新 Runtime，并用当前插件 profile 做启动检查。");

    private void PresentUpdateAlert(RuntimeUpdateResult result) =>
        _dialogs.Info(
            "发现 Harness Runtime 更新",
            $"候选版本：{result.Manifest.RuntimeId}\n\n当前版本：{result.CurrentRuntimeId ?? RuntimeVersion ?? "unknown"}\n\n请点击顶栏的圆形下载按钮下载并校验更新 artifact。");

    private void PresentAppUpdateAlert(AppUpdateResult result)
    {
        if (_dialogs.Confirm(
            "发现 DeepSeek Harness App 更新",
            $"当前版本：{result.CurrentVersion}\n最新版本：{result.LatestVersion}\n\n此更新会替换外层 Windows App；底层 Harness Runtime 由版本号旁的下载按钮单独管理。\n\n是否打开下载页？"))
        {
            _dialogs.OpenUrl(result.ReleaseUrl);
        }
    }

    private bool ConfirmPluginMutation(string operation, string spec, PluginDependencyPlan? dependencyPlan = null)
    {
        var dependencyText = dependencyPlan is not null ? $"\n\n{dependencyPlan.ConfirmationText}" : "";
        return _dialogs.Confirm(
            $"确认{operation} Harness 插件？",
            $"目标：{spec}\n\n应用会把当前 web profile 复制到临时目录，执行官方 dsh plugin 命令或配置补丁，并执行候选启动预检。插件可能包含本地代码和生命周期/构建脚本；请确认来源可信。{dependencyText}");
    }

    private bool ConfirmToolchainInstallation(ToolchainInstallPlan plan) =>
        _dialogs.Confirm(
            "插件需要额外依赖",
            $"插件安装报告缺少一个受控的基础工具。Launcher 只会安装下面列出的固定版本，不会执行插件提供的任意命令。\n\n{plan.ConfirmationText}\n\n下载完成后会进行 HTTPS、大小、SHA-256 和可执行文件校验；依赖只对本 App 的 Harness 子进程生效。");

    private bool ConfirmBuildScriptApproval(IReadOnlyList<string> packages) =>
        _dialogs.Confirm(
            "插件需要执行安装构建脚本",
            $"pnpm 为安全起见阻止了以下插件的 prepare/build 构建脚本：\n{string.Join("\n", packages.Select(p => $"• {p}"))}\n\n继续后，应用只会把这些精确的包名写入临时 profile 的 pnpm-workspace.yaml allowBuilds 配置，然后重新执行官方安装命令。不会执行 README 中的任意命令，也不会修改用户全局 pnpm 配置。");

    private IReadOnlyList<HarnessPlugin> PromptForPluginSelection(string title, string operation)
    {
        if (Plugins.Count == 0)
        {
            _dialogs.Info("没有已安装插件", "请先通过“插件 > 安装插件…”安装标准 Harness 插件。");
            return Array.Empty<HarnessPlugin>();
        }
        return _dialogs.PromptForPluginSelection(title, operation, Plugins);
    }

    private static string BalanceAmountFor(DeepSeekBalanceInfo info) =>
        info.Currency.ToUpperInvariant() switch
        {
            "CNY" => $"¥{info.TotalBalance}",
            "USD" => $"${info.TotalBalance}",
            var other => $"{info.TotalBalance} {other}",
        };

    private sealed class NullDialogs : ILauncherDialogs
    {
        public string? PromptForText(string title, string message, string placeholder) => null;
        public string? PromptForSecret(string title, string message) => null;
        public bool Confirm(string title, string message) => false;
        public void Info(string title, string message) { }
        public IReadOnlyList<HarnessPlugin> PromptForPluginSelection(
            string title, string operation, IReadOnlyList<HarnessPlugin> plugins) =>
            Array.Empty<HarnessPlugin>();
        public void OpenUrl(Uri url) { }
        public void RevealFile(string path) { }
    }
}
