using System.Windows;
using System.Windows.Controls;
using HarnessLauncher.Models;

namespace HarnessLauncher.Views;

/// <summary>
/// Modal plugin multi-selection dialog, mirroring the macOS
/// PluginSelectionAccessory (单选、多选、全选).
/// </summary>
public sealed class PluginSelectionDialog : Window
{
    private readonly List<(CheckBox Box, HarnessPlugin Plugin)> _items = new();

    private PluginSelectionDialog(string title, string operation, IReadOnlyList<HarnessPlugin> plugins)
    {
        Title = title;
        Width = 420;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Support.ThemeManager.StyleWindow(this);

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock
        {
            Text = $"可单选、多选或点击“全选”。{operation}不会删除 Harness 会话或其他用户数据。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });

        var listPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        foreach (var plugin in plugins)
        {
            var box = new CheckBox
            {
                Content = $"{plugin.Name}（{plugin.Version}）{(plugin.IsDisabled ? "（已停用）" : "")}",
                Margin = new Thickness(0, 0, 0, 6),
            };
            _items.Add((box, plugin));
            listPanel.Children.Add(box);
        }
        panel.Children.Add(new ScrollViewer
        {
            Content = listPanel,
            MaxHeight = 240,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var selectAll = new Button { Content = "全选", Width = 70, Margin = new Thickness(0, 0, 8, 0) };
        selectAll.Click += (_, _) =>
        {
            foreach (var (box, _) in _items) box.IsChecked = true;
        };
        var ok = new Button { Content = operation, Width = 80, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        ok.Click += (_, _) => { DialogResult = true; };
        var cancel = new Button { Content = "取消", Width = 80, IsCancel = true };
        buttons.Children.Add(selectAll);
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        Content = panel;
    }

    public static IReadOnlyList<HarnessPlugin> Show(
        Window owner, string title, string operation, IReadOnlyList<HarnessPlugin> plugins)
    {
        var dialog = new PluginSelectionDialog(title, operation, plugins) { Owner = owner };
        if (dialog.ShowDialog() != true) return Array.Empty<HarnessPlugin>();
        var selected = dialog._items
            .Where(item => item.Box.IsChecked == true)
            .Select(item => item.Plugin)
            .ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show(owner, $"请至少选择一个插件后再执行{operation}。",
                "未选择插件", MessageBoxButton.OK, MessageBoxImage.Information);
            return Array.Empty<HarnessPlugin>();
        }
        return selected;
    }
}
