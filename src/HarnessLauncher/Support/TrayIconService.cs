using NotifyIcon = System.Windows.Forms.NotifyIcon;
using ContextMenuStrip = System.Windows.Forms.ContextMenuStrip;
using ToolStripMenuItem = System.Windows.Forms.ToolStripMenuItem;
using ToolStripProfessionalRenderer = System.Windows.Forms.ToolStripProfessionalRenderer;
using ProfessionalColorTable = System.Windows.Forms.ProfessionalColorTable;

namespace HarnessLauncher.Support;

/// <summary>
/// 系统托盘图标：关闭主窗口时最小化到托盘（后台运行），双击托盘图标
/// 还原窗口，右键菜单可「显示主窗口」或「退出启动器」。
/// 菜单配色跟随系统亮/暗主题。全部为用户态 API，无需管理员权限。
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private bool _balloonShown;

    public event Action? RestoreRequested;
    public event Action? ExitRequested;

    public TrayIconService()
    {
        _menu = new ContextMenuStrip();
        var showItem = new ToolStripMenuItem("显示主窗口");
        showItem.Click += (_, _) => RestoreRequested?.Invoke();
        var exitItem = new ToolStripMenuItem("退出启动器");
        exitItem.Click += (_, _) => ExitRequested?.Invoke();
        _menu.Items.Add(showItem);
        _menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        _menu.Items.Add(exitItem);

        _notifyIcon = new NotifyIcon
        {
            Text = "DeepSeek Harness",
            Icon = LoadIcon(),
            Visible = true,
            ContextMenuStrip = _menu,
        };
        _notifyIcon.DoubleClick += (_, _) => RestoreRequested?.Invoke();

        ApplyTheme();
        ThemeManager.ThemeChanged += _ => ApplyTheme();
    }

    private static System.Drawing.Icon LoadIcon()
    {
        // 优先使用嵌入 exe 的应用图标（与任务栏/标题栏一致）
        var processPath = Environment.ProcessPath;
        if (processPath is not null)
        {
            try { return System.Drawing.Icon.ExtractAssociatedIcon(processPath); }
            catch { }
        }
        return System.Drawing.SystemIcons.Application;
    }

    /// <summary>首次最小化到托盘时给用户一个气泡提示。</summary>
    public void NotifyMinimized()
    {
        if (_balloonShown) return;
        _balloonShown = true;
        try
        {
            _notifyIcon.ShowBalloonTip(
                3000,
                "DeepSeek Harness",
                "已最小化到系统托盘，双击图标可恢复窗口。",
                System.Windows.Forms.ToolTipIcon.Info);
        }
        catch { }
    }

    private void ApplyTheme()
    {
        var dark = ThemeManager.Current == ThemeManager.AppTheme.Dark;
        _menu.Renderer = new ToolStripProfessionalRenderer(new TrayColorTable(dark));
        _menu.ForeColor = dark
            ? System.Drawing.Color.FromArgb(0xE8, 0xE8, 0xE8)
            : System.Drawing.Color.FromArgb(0x1A, 0x1A, 0x1A);
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
    }

    private sealed class TrayColorTable : ProfessionalColorTable
    {
        private readonly bool _dark;
        public TrayColorTable(bool dark) { _dark = dark; }

        private System.Drawing.Color Back =>
            _dark ? System.Drawing.Color.FromArgb(0x2B, 0x2B, 0x2B) : System.Drawing.Color.FromArgb(0xF0, 0xF0, 0xF0);
        private System.Drawing.Color Hover =>
            _dark ? System.Drawing.Color.FromArgb(0x44, 0x44, 0x44) : System.Drawing.Color.FromArgb(0xE5, 0xE5, 0xE5);
        private System.Drawing.Color Border =>
            _dark ? System.Drawing.Color.FromArgb(0x44, 0x44, 0x44) : System.Drawing.Color.FromArgb(0xCC, 0xCC, 0xCC);

        public override System.Drawing.Color ToolStripDropDownBackground => Back;
        public override System.Drawing.Color MenuItemSelected => Hover;
        public override System.Drawing.Color MenuItemSelectedGradientBegin => Hover;
        public override System.Drawing.Color MenuItemSelectedGradientEnd => Hover;
        public override System.Drawing.Color MenuItemBorder => Hover;
        public override System.Drawing.Color MenuBorder => Border;
        public override System.Drawing.Color SeparatorDark => Border;
        public override System.Drawing.Color SeparatorLight => Border;
        public override System.Drawing.Color ImageMarginGradientBegin => Back;
        public override System.Drawing.Color ImageMarginGradientMiddle => Back;
        public override System.Drawing.Color ImageMarginGradientEnd => Back;
        public override System.Drawing.Color MenuItemPressedGradientBegin => Back;
        public override System.Drawing.Color MenuItemPressedGradientEnd => Back;
    }
}
