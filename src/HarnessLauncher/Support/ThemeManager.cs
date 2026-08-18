using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace HarnessLauncher.Support;

/// <summary>
/// Follows the Windows system light/dark mode ("AppsUseLightTheme" under
/// HKCU — reading it needs no admin rights) and swaps the app's dynamic
/// brushes live when the user changes the system theme.
/// </summary>
public static class ThemeManager
{
    public enum AppTheme { Light, Dark }

    public static AppTheme Current { get; private set; } = AppTheme.Light;

    public static event Action<AppTheme>? ThemeChanged;

    private static bool _initialized;

    public static void Initialize(Application application)
    {
        if (_initialized) return;
        _initialized = true;
        ApplyTheme(application, ReadSystemTheme());
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color)
            {
                application.Dispatcher.Invoke(() => ApplyTheme(application, ReadSystemTheme()));
            }
        };
    }

    private static AppTheme ReadSystemTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            return value is int light && light == 0 ? AppTheme.Dark : AppTheme.Light;
        }
        catch
        {
            return AppTheme.Light;
        }
    }

    private static void ApplyTheme(Application application, AppTheme theme)
    {
        Current = theme;
        var resources = application.Resources;
        if (theme == AppTheme.Dark)
        {
            Set(resources, "AppBackgroundBrush", 0x20, 0x20, 0x20);
            Set(resources, "AppPanelBackgroundBrush", 0x2B, 0x2B, 0x2B);
            Set(resources, "AppForegroundBrush", 0xE8, 0xE8, 0xE8);
            Set(resources, "AppSecondaryForegroundBrush", 0xAD, 0xAD, 0xAD);
            Set(resources, "AppBorderBrush", 0x44, 0x44, 0x44);
            Set(resources, "AppSeparatorBrush", 0x44, 0x44, 0x44);
            Set(resources, "AppButtonBackgroundBrush", 0x3A, 0x3A, 0x3A);
            Set(resources, "AppHoverBackgroundBrush", 0x44, 0x44, 0x44);
            Set(resources, "AppInputBackgroundBrush", 0x33, 0x33, 0x33);
        }
        else
        {
            Set(resources, "AppBackgroundBrush", 0xFF, 0xFF, 0xFF);
            Set(resources, "AppPanelBackgroundBrush", 0xFA, 0xFA, 0xFA);
            Set(resources, "AppForegroundBrush", 0x1A, 0x1A, 0x1A);
            Set(resources, "AppSecondaryForegroundBrush", 0x66, 0x66, 0x66);
            Set(resources, "AppBorderBrush", 0x22, 0x22, 0x22, alpha: 0x22);
            Set(resources, "AppSeparatorBrush", 0x00, 0x00, 0x00, alpha: 0x33);
            Set(resources, "AppButtonBackgroundBrush", 0xF5, 0xF5, 0xF5);
            Set(resources, "AppHoverBackgroundBrush", 0xE5, 0xE5, 0xE5);
            Set(resources, "AppInputBackgroundBrush", 0xFF, 0xFF, 0xFF);
        }
        ApplySystemColorOverrides(resources, theme);
        ThemeChanged?.Invoke(theme);
    }

    /// <summary>
    /// WPF 原生控件（菜单弹出层、按钮悬停态等）的默认模板直接引用
    /// SystemColors 键；在应用资源里按主题覆盖这些键，下拉菜单、
    /// 禁用文字、选中高亮才会跟随亮/暗色，而不是固定使用系统浅色值。
    /// </summary>
    private static void ApplySystemColorOverrides(ResourceDictionary resources, AppTheme theme)
    {
        if (theme == AppTheme.Dark)
        {
            resources[SystemColors.MenuBrushKey] = new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2B));
            resources[SystemColors.MenuBarBrushKey] = new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2B));
            resources[SystemColors.MenuTextBrushKey] = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8));
            resources[SystemColors.ControlBrushKey] = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A));
            resources[SystemColors.ControlTextBrushKey] = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8));
            resources[SystemColors.WindowBrushKey] = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20));
            resources[SystemColors.WindowTextBrushKey] = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8));
            resources[SystemColors.HighlightBrushKey] = new SolidColorBrush(Color.FromRgb(0x3E, 0x5A, 0x88));
            resources[SystemColors.HighlightTextBrushKey] = new SolidColorBrush(Colors.White);
            resources[SystemColors.GrayTextBrushKey] = new SolidColorBrush(Color.FromRgb(0x7A, 0x7A, 0x7A));
            resources[SystemColors.InactiveBorderBrushKey] = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44));
        }
        else
        {
            resources[SystemColors.MenuBrushKey] = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0));
            resources[SystemColors.MenuBarBrushKey] = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0));
            resources[SystemColors.MenuTextBrushKey] = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A));
            resources[SystemColors.ControlBrushKey] = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0));
            resources[SystemColors.ControlTextBrushKey] = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A));
            resources[SystemColors.WindowBrushKey] = new SolidColorBrush(Colors.White);
            resources[SystemColors.WindowTextBrushKey] = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A));
            resources[SystemColors.HighlightBrushKey] = new SolidColorBrush(Color.FromRgb(0x2D, 0x62, 0xC4));
            resources[SystemColors.HighlightTextBrushKey] = new SolidColorBrush(Colors.White);
            resources[SystemColors.GrayTextBrushKey] = new SolidColorBrush(Color.FromRgb(0x6D, 0x6D, 0x6D));
            resources[SystemColors.InactiveBorderBrushKey] = new SolidColorBrush(Color.FromRgb(0xD9, 0xD9, 0xD9));
        }
    }

    private static void Set(ResourceDictionary resources, string key, byte r, byte g, byte b, byte alpha = 0xFF)
    {
        resources[key] = new SolidColorBrush(Color.FromArgb(alpha, r, g, b));
    }

    /// <summary>
    /// Maps the app theme to WebView2's preferred color scheme so the hosted
    /// Harness web UI sees a matching prefers-color-scheme.
    /// </summary>
    public static Microsoft.Web.WebView2.Core.CoreWebView2PreferredColorScheme WebView2ColorScheme =>
        Current == AppTheme.Dark
            ? Microsoft.Web.WebView2.Core.CoreWebView2PreferredColorScheme.Dark
            : Microsoft.Web.WebView2.Core.CoreWebView2PreferredColorScheme.Light;

    /// <summary>Applies the themed window chrome to any app dialog window.</summary>
    public static void StyleWindow(Window window)
    {
        window.Background = (Brush)Application.Current.Resources["AppBackgroundBrush"];
        window.Foreground = (Brush)Application.Current.Resources["AppForegroundBrush"];
        window.SetResourceReference(Window.BackgroundProperty, "AppBackgroundBrush");
        window.SetResourceReference(Window.ForegroundProperty, "AppForegroundBrush");

        // 标题栏跟随主题：DWMWA_USE_IMMERSIVE_DARK_MODE（Win10 1809+）。
        // 窗口句柄可能在 StyleWindow 调用时还没创建，挂到 SourceInitialized 上。
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
        {
            ApplyDarkTitleBar(window);
        }
        else
        {
            window.SourceInitialized += (_, _) => ApplyDarkTitleBar(window);
        }
        ThemeChanged += _ => window.Dispatcher.Invoke(() => ApplyDarkTitleBar(window));
    }

    private static void ApplyDarkTitleBar(Window window)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        var dark = Current == AppTheme.Dark ? 1 : 0;
        // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE（2004+），旧系统退回未公开的 19
        if (DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int)) != 0)
        {
            DwmSetWindowAttribute(hwnd, 19, ref dark, sizeof(int));
        }
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd, int attribute, ref int value, int size);
}
