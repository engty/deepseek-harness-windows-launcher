using System.Windows;
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
            Set(resources, "AppInputBackgroundBrush", 0xFF, 0xFF, 0xFF);
        }
        ThemeChanged?.Invoke(theme);
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
    }
}
