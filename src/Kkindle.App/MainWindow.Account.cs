using Avalonia.Interactivity;
using Kkindle.Core;

namespace Kkindle;

public partial class MainWindow
{
    private bool _accountWindowOpen;
    private AccountStatus? _syncAccountStatus;

    private async void AccountNavigationButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_accountWindowOpen || BlockNavigationWhileTransferring()) return;
        _accountWindowOpen = true;
        try { await new AccountWindow(_modules.Accounts.Service).ShowDialog(this); }
        finally { _accountWindowOpen = false; await RefreshAccountSummaryAsync(); }
    }

    private async Task RefreshAccountSummaryAsync()
    {
        try
        {
            var status = await _modules.Accounts.Service.GetStatusAsync(cancellationToken: _lifetimeCancellation.Token);
            _syncAccountStatus = status;
            AccountNavigationText.Text = status.User?.Username ?? UiText.Get("登录 / 注册");
            AccountNavigationButton.SetValue(Avalonia.Controls.ToolTip.TipProperty, UiText.Get(AccountRegions.DisplayName(status.Region)));
            AccountSyncSummaryText.Text = UiText.Get(AccountRegions.DisplayName(status.Region)) + " · " + (status.User?.Username ?? UiText.Get("登录 / 注册"))
                + (status.RequiresDataRestart ? "\n" + UiText.Get("区域已切换，请重新打开应用后使用该区域的独立书库和同步。") : "");
            UpdateS3SettingsDraftState();
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { AccountNavigationText.Text = UiText.Get("用户账号"); }
    }

    private Task<S3SyncSettings> BindAccountSyncSettingsAsync(S3SyncSettings settings, CancellationToken token) =>
        settings.Provider == SyncProvider.AccountServer
            ? _modules.Accounts.Service.CreateSyncSettingsAsync(settings, token)
            : Task.FromResult(settings with { AccountRegion = _modules.DataRegion });
}
