using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using HarnessLauncher.Models;
using HarnessLauncher.Services;
using HarnessLauncher.Views;
using Microsoft.Web.WebView2.Core;

namespace HarnessLauncher;

public partial class MainWindow : Window
{
    private readonly LauncherModel _model;
    private readonly WpfLauncherDialogs _dialogs;
    private readonly Support.TrayIconService _tray;
    private bool _webViewReady;
    private Uri? _loadedOrigin;
    private bool _shutdownStarted;
    private bool _allowExit;

    public MainWindow()
    {
        InitializeComponent();
        // 主窗口也要走统一样式：背景/前景 + 暗色标题栏
        Support.ThemeManager.StyleWindow(this);
        _tray = new Support.TrayIconService();
        _tray.RestoreRequested += RestoreFromTray;
        _tray.ExitRequested += () =>
        {
            // 托盘右键「退出启动器」：真正退出
            _allowExit = true;
            Close();
        };
        _dialogs = new WpfLauncherDialogs(this);
        _model = new LauncherModel(dialogs: _dialogs);
        _model.PropertyChanged += Model_PropertyChanged;
        Closing += MainWindow_Closing;
        Loaded += async (_, _) =>
        {
            await InitializeWebViewAsync();
            RefreshUi();
            await _model.StartIfNeededAsync();
        };
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(
                userDataFolder: _model.Paths.WebView2UserData);
            await WebView.EnsureCoreWebView2Async(environment);
            WebView.CoreWebView2.NavigationStarting += WebView_NavigationStarting;
            WebView.CoreWebView2.NewWindowRequested += WebView_NewWindowRequested;
            WebView.CoreWebView2.NavigationCompleted += WebView_NavigationCompleted;
            // Let the hosted Harness web UI follow the system light/dark
            // mode via prefers-color-scheme.
            WebView.CoreWebView2.Profile.PreferredColorScheme =
                Support.ThemeManager.WebView2ColorScheme;
            Support.ThemeManager.ThemeChanged += _ =>
            {
                if (_webViewReady)
                {
                    WebView.CoreWebView2.Profile.PreferredColorScheme =
                        Support.ThemeManager.WebView2ColorScheme;
                }
            };
            _webViewReady = true;
        }
        catch (Exception error)
        {
            _model.WebViewDidFail(
                "WebView2 运行时不可用。请安装 Microsoft Edge WebView2 Runtime（大多数 Windows 10/11 已内置）：" +
                error.Message);
        }
    }

    private void Model_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(RefreshUi);
            return;
        }
        RefreshUi();
    }

    private void RefreshUi()
    {
        var phase = _model.Phase;

        // Toolbar
        StatusDot.Fill = phase.IsReady ? Brushes.ForestGreen : Brushes.Firebrick;
        VersionText.Text = _model.RuntimeVersion is { } version
            ? $"DeepSeek Harness {version}" : "DeepSeek Harness";
        BalanceText.Text = _model.BalanceState is DeepSeekBalanceState.Available &&
            _model.BalanceAmountDisplayText is { } amount
                ? $"余额 {amount}"
                : _model.BalanceDisplayText;
        BalanceText.Foreground = _model.BalanceTone switch
        {
            DeepSeekBalanceTone.Healthy => Brushes.ForestGreen,
            DeepSeekBalanceTone.Warning => Brushes.DarkGoldenrod,
            DeepSeekBalanceTone.Critical => Brushes.Firebrick,
            _ => (Brush)Application.Current.Resources["AppSecondaryForegroundBrush"],
        };
        BalanceButton.ToolTip = _model.IsBalanceConfigured
            ? "点击更换 DeepSeek API Key" : "配置 DeepSeek API Key";
        var updateVisible = _model.HasAvailableRuntimeUpdate;
        UpdateButton.Visibility = updateVisible ? Visibility.Visible : Visibility.Collapsed;
        UpdateSeparator.Visibility = updateVisible ? Visibility.Visible : Visibility.Collapsed;
        UpdateButton.IsEnabled = !_model.IsOperationInProgress;

        // Menu state
        InstallPluginMenuItem.IsEnabled = !_model.IsOperationInProgress;
        StopPluginMenuItem.IsEnabled = !_model.IsOperationInProgress;
        RemovePluginMenuItem.IsEnabled = !_model.IsOperationInProgress;
        RestartMenuItem.IsEnabled = !_model.IsOperationInProgress && _model.CanRestart;
        RestartButton.IsEnabled = !_model.IsOperationInProgress;
        RebuildInstalledPluginsMenu();

        // Content
        if (phase is LauncherPhase.Ready(var endpoint))
        {
            StartupPanel.Visibility = Visibility.Collapsed;
            ShowWebView(endpoint);
        }
        else
        {
            WebView.Visibility = Visibility.Collapsed;
            StartupPanel.Visibility = Visibility.Visible;
            PhaseTitleText.Text = phase.Title;
            StartingProgress.Visibility = phase is LauncherPhase.Starting
                ? Visibility.Visible : Visibility.Collapsed;
            DetailText.Text = phase switch
            {
                LauncherPhase.RuntimeMissing =>
                    "请将经过验证的 dsh Runtime 放入 App 旁的 runtime 目录，或在开发时设置 HARNESS_DSH_PATH 指向可执行的 dsh。主界面不会打开系统浏览器。",
                LauncherPhase.Failed(var message) => message,
                LauncherPhase.Stopped =>
                    "DeepSeek Harness 当前已停止。点击「重启 DeepSeek Harness」重新启动专用 App 窗口中的 Harness UI。",
                LauncherPhase.Busy(var operation) => operation,
                _ => "正在准备 Harness 专用窗口。",
            };
        }
    }

    private void ShowWebView(Uri endpoint)
    {
        if (!_webViewReady) return;
        WebView.Visibility = Visibility.Visible;
        // Reload only when the Harness origin itself changes (for example
        // after a process restart on a new port), never on every state
        // change — the WebView may currently be on Harness's Settings route.
        if (_loadedOrigin is null || !SharesOrigin(_loadedOrigin, endpoint))
        {
            _loadedOrigin = endpoint;
            WebView.CoreWebView2.Navigate(endpoint.ToString());
        }
    }

    private static bool SharesOrigin(Uri lhs, Uri rhs) =>
        lhs.Scheme.Equals(rhs.Scheme, StringComparison.OrdinalIgnoreCase) &&
        lhs.Host.Equals(rhs.Host, StringComparison.OrdinalIgnoreCase) &&
        lhs.Port == rhs.Port;

    private void WebView_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var target) || _loadedOrigin is null)
        {
            e.Cancel = true;
            return;
        }
        if (SharesOrigin(target, _loadedOrigin)) return;
        // Only user-clicked external links may leave the dedicated App
        // window. Redirects and script navigations are denied.
        if (target.Scheme == "https" && e.IsUserInitiated)
        {
            OpenExternal(target);
        }
        e.Cancel = true;
    }

    private void WebView_NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var target) && target.Scheme == "https")
        {
            OpenExternal(target);
        }
    }

    private void WebView_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess)
        {
            _model.WebViewDidFail($"导航失败（{e.WebErrorStatus}）");
        }
    }

    private static void OpenExternal(Uri url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url.ToString()) { UseShellExecute = true });
        }
        catch { }
    }

    private void RebuildInstalledPluginsMenu()
    {
        InstalledPluginsMenuItem.Items.Clear();
        if (_model.Plugins.Count == 0)
        {
            var empty = new MenuItem { Header = "没有已安装的 bundle 插件", IsEnabled = false };
            InstalledPluginsMenuItem.Items.Add(empty);
            return;
        }
        foreach (var plugin in _model.Plugins)
        {
            var status = _model.PluginStatusFor(plugin);
            var header = new MenuItem { Header = plugin.Name };
            header.Items.Add(new MenuItem { Header = $"状态：{status}", IsEnabled = false });
            header.Items.Add(new MenuItem { Header = $"版本：{plugin.Version}", IsEnabled = false });
            header.Items.Add(new Separator());
            var start = new MenuItem
            {
                Header = "启动插件",
                IsEnabled = status is PluginRuntimeState.Stopped or PluginRuntimeState.Error
                    && !_model.IsOperationInProgress,
            };
            start.Click += async (_, _) => await _model.StartPluginAsync(plugin);
            var stop = new MenuItem
            {
                Header = "停止插件",
                IsEnabled = status is PluginRuntimeState.Running or PluginRuntimeState.Starting
                    && !_model.IsOperationInProgress,
            };
            stop.Click += async (_, _) => await _model.StopPluginAsync(plugin);
            header.Items.Add(start);
            header.Items.Add(stop);
            InstalledPluginsMenuItem.Items.Add(header);
        }
    }

    // ---- Menu handlers ----

    private void ChangeApiKey_Click(object sender, RoutedEventArgs e) =>
        _model.ConfigureDeepSeekBalance(forcePrompt: true);

    private void Balance_Click(object sender, RoutedEventArgs e) =>
        _model.ConfigureDeepSeekBalance(forcePrompt: _model.IsBalanceConfigured);

    private void CheckUpdates_Click(object sender, RoutedEventArgs e) => _model.CheckForUpdates();

    private void CheckAppUpdates_Click(object sender, RoutedEventArgs e) => _model.CheckForAppUpdates();

    private void DownloadUpdate_Click(object sender, RoutedEventArgs e) => _model.DownloadLatestUpdate();

    private async void Restart_Click(object sender, RoutedEventArgs e) => await _model.RestartAsync();

    private void InstallPlugin_Click(object sender, RoutedEventArgs e) => _model.InstallPluginPrompt();

    private void StopPlugin_Click(object sender, RoutedEventArgs e) => _model.StopPluginPrompt();

    private void RemovePlugin_Click(object sender, RoutedEventArgs e) => _model.RemovePluginPrompt();

    private void ExportDiagnostics_Click(object sender, RoutedEventArgs e) => _model.ExportDiagnostics();

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        // 窗口菜单「退出」视为明确退出意图：直接真正退出
        _allowExit = true;
        Close();
    }

    /// <summary>双击托盘图标 / 托盘菜单「显示主窗口」时还原窗口。</summary>
    private void RestoreFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        // 默认关闭（点 X）不退出，最小化到系统托盘后台运行
        if (!_allowExit)
        {
            e.Cancel = true;
            Hide();
            _tray.NotifyMinimized();
            return;
        }
        if (_shutdownStarted || !_model.IsHarnessRunning)
        {
            _tray.Dispose();
            // ShutdownMode 是 OnExplicitShutdown，窗口关闭不会自动退进程
            Application.Current.Shutdown();
            return;
        }
        // Mirror the macOS terminateLater flow: delay the close until the
        // sidecar has stopped, then close for real.
        e.Cancel = true;
        _shutdownStarted = true;
        try
        {
            await _model.StopAsync();
        }
        catch { }
        _tray.Dispose();
        Application.Current.Shutdown();
    }

    /// <summary>Called from App.OnExit as a last-resort cleanup.</summary>
    public void ShutdownHarness()
    {
        try
        {
            _model.StopAsync().Wait(TimeSpan.FromSeconds(10));
        }
        catch { }
    }
}

/// <summary>WPF implementation of the dialog surface used by LauncherModel.</summary>
public sealed class WpfLauncherDialogs : ILauncherDialogs
{
    private readonly Window _owner;

    public WpfLauncherDialogs(Window owner)
    {
        _owner = owner;
    }

    public string? PromptForText(string title, string message, string placeholder) =>
        InputDialog.Show(_owner, title, message, placeholder, isSecret: false);

    public string? PromptForSecret(string title, string message) =>
        InputDialog.Show(_owner, title, message, "sk-…", isSecret: true);

    public bool Confirm(string title, string message) =>
        MessageBox.Show(_owner, message, title,
            MessageBoxButton.OKCancel, MessageBoxImage.Information) == MessageBoxResult.OK;

    public void Info(string title, string message) =>
        MessageBox.Show(_owner, message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public IReadOnlyList<Models.HarnessPlugin> PromptForPluginSelection(
        string title, string operation, IReadOnlyList<Models.HarnessPlugin> plugins) =>
        PluginSelectionDialog.Show(_owner, title, operation, plugins);

    public void OpenUrl(Uri url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url.ToString()) { UseShellExecute = true });
        }
        catch { }
    }

    public void RevealFile(string path)
    {
        try
        {
            Process.Start("explorer.exe", $"/select,\"{path}\"");
        }
        catch { }
    }
}
