using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Kkindle.Core;

namespace Kkindle;

/// <summary>Small modal text prompt used for naming saved filters.</summary>
internal static class TextPromptWindow
{
    public static async Task<string?> ShowAsync(Window owner, string title, string initial)
    {
        var box = new TextBox
        {
            Text = initial,
            Height = 38,
            Padding = new Thickness(10, 6)
        };
        var ok = new Button { Content = UiText.Get("确定"), Padding = new Thickness(14, 6) };
        var cancel = new Button { Content = UiText.Get("取消"), Padding = new Thickness(14, 6) };
        cancel.Classes.Add("quiet");

        var window = new Window
        {
            Title = title,
            Width = 420,
            Height = 190,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = AppAppearanceResources.GetBrush("PaperBrush"),
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 14,
                Children =
                {
                    new TextBlock
                    {
                        Text = title,
                        FontSize = 16,
                        FontWeight = FontWeight.SemiBold,
                        Foreground = AppAppearanceResources.GetBrush("InkBrush")
                    },
                    box,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { cancel, ok }
                    }
                }
            }
        };

        ok.Click += (_, _) =>
            window.Close(string.IsNullOrWhiteSpace(box.Text) ? null : box.Text.Trim());
        cancel.Click += (_, _) => window.Close(null);
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                window.Close(string.IsNullOrWhiteSpace(box.Text) ? null : box.Text.Trim());
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                window.Close(null);
            }
        };
        window.Opened += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };
        return await window.ShowDialog<string?>(owner);
    }
}
