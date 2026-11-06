using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Kkindle.Mobile.Views.Sheets;

/// <summary>Confirmation, optionally with one text field (rename, new shelf).</summary>
public partial class PromptSheet : UserControl
{
    private readonly Func<string, Task>? _onConfirm;

    /// <summary>Design-time only.</summary>
    public PromptSheet() => InitializeComponent();

    public PromptSheet(
        string? message,
        string confirmText,
        Func<string, Task> onConfirm,
        string? initialText = null,
        string? placeholder = null,
        bool isPassword = false)
    {
        _onConfirm = onConfirm;
        InitializeComponent();
        MessageText.Text = message;
        MessageText.IsVisible = !string.IsNullOrEmpty(message);
        ConfirmButton.Content = confirmText;
        if (initialText is null && placeholder is null) return;

        InputBox.IsVisible = true;
        if (isPassword) InputBox.PasswordChar = '●';
        InputBox.Text = initialText;
        InputBox.PlaceholderText = placeholder;
        Loaded += (_, _) => InputBox.Focus();
    }

    private async void OnConfirmClick(object? sender, RoutedEventArgs e)
    {
        if (_onConfirm is null) return;
        var text = InputBox.Text?.Trim() ?? string.Empty;
        if (InputBox.IsVisible && text.Length == 0) return;
        ConfirmButton.IsEnabled = false;
        try
        {
            await _onConfirm(text);
        }
        finally
        {
            ConfirmButton.IsEnabled = true;
        }
    }
}
