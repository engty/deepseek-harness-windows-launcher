using System.Windows;

namespace HarnessLauncher;

public partial class App : Application
{
    private MainWindow? _mainWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Support.ThemeManager.Initialize(this);
        _mainWindow = new MainWindow();
        _mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mainWindow?.ShutdownHarness();
        base.OnExit(e);
    }
}
