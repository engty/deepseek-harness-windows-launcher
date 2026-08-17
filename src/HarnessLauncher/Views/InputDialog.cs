using System.Windows;
using System.Windows.Controls;

namespace HarnessLauncher.Views;

/// <summary>Simple modal text/password input dialog.</summary>
public partial class InputDialog : Window
{
    private readonly bool _isSecret;

    private InputDialog(string title, string message, string placeholder, bool isSecret)
    {
        _isSecret = isSecret;
        Title = title;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Support.ThemeManager.StyleWindow(this);

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });

        if (isSecret)
        {
            var box = new PasswordBox { Margin = new Thickness(0, 0, 0, 12) };
            ToolTipService.SetToolTip(box, placeholder);
            panel.Children.Add(box);
        }
        else
        {
            var box = new TextBox { Margin = new Thickness(0, 0, 0, 12) };
            ToolTipService.SetToolTip(box, placeholder);
            panel.Children.Add(box);
        }

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var ok = new Button { Content = "继续", Width = 80, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        ok.Click += (_, _) => { DialogResult = true; };
        var cancel = new Button { Content = "取消", Width = 80, IsCancel = true };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        Content = panel;
    }

    private string? Value => _isSecret
        ? ((PasswordBox)((StackPanel)Content).Children[1]).Password.Trim()
        : ((TextBox)((StackPanel)Content).Children[1]).Text.Trim();

    public static string? Show(Window owner, string title, string message, string placeholder, bool isSecret)
    {
        var dialog = new InputDialog(title, message, placeholder, isSecret) { Owner = owner };
        if (dialog.ShowDialog() != true) return null;
        var value = dialog.Value;
        return string.IsNullOrEmpty(value) ? null : value;
    }
}
