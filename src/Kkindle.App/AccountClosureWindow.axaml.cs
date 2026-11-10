using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Kkindle.Core;

namespace Kkindle;

public partial class AccountClosureWindow : Window
{
    private bool _accepted;
    internal string CurrentPassword => ClosurePasswordBox.Text ?? "";

    public AccountClosureWindow()
    {
        InitializeComponent();
        Closed += (_, _) => { if (!_accepted) ClearPassword(); };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(false); } };
    }

    internal void ClearPassword() => ClosurePasswordBox.Text = "";
    private void OnConfirmationChanged(object? sender, TextChangedEventArgs e)
    {
        if (ClosureConfirmButton is not null)
            ClosureConfirmButton.IsEnabled = ClosureConfirmationBox.Text == "注销账号" && AccountValidation.IsPassword(CurrentPassword);
    }
    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
    private void OnConfirmClick(object? sender, RoutedEventArgs e)
    {
        if (!ClosureConfirmButton.IsEnabled || ClosureConfirmationBox.Text != "注销账号" || !AccountValidation.IsPassword(CurrentPassword)) return;
        _accepted = true;
        Close(true);
    }
}
