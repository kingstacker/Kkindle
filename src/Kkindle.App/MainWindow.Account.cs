using Avalonia.Controls;
using Avalonia.Interactivity;
using Kkindle.Core;

namespace Kkindle;

public partial class MainWindow
{
    private AccountCenterView? _accountCenterView;
    private bool _deviceChecksStarted;
    private AccountStatus? _syncAccountStatus;
    private AccountMembership? _accountNavigationMembership;
    private int _accountSummaryRevision;

    private async void AccountNavigationButton_Click(object? sender, RoutedEventArgs e)
    {
        if (BlockNavigationWhileTransferring()) return;
        if (_accountCenterView is null)
        {
            _accountCenterView = new AccountCenterView(_modules.Accounts.Service, _modules.Accounts.Membership);
            _accountCenterView.AccountStateChanged += (_, state) => PresentAccountSummary(state.Account, state.Membership);
            _accountCenterView.AccountSignedIn += async (_, state) => await SelectPlusCloudSyncAsync(state);
            AccountCenterHost.Content = _accountCenterView;
        }
        ShowStage3Page(AccountCenterHost, AccountNavigationButton);
        await _accountCenterView.RefreshAsync();
    }

    private async Task SelectPlusCloudSyncAsync(AccountStateChangedEventArgs state)
    {
        if (AccountVisuals.Tier(state.Membership) != "plus" || !state.Account.IsLoggedIn || state.Account.RequiresDataRestart
            || state.Account.Region != _modules.DataRegion) return;
        try
        {
            while (_s3SyncBusy)
            {
                if (_s3SyncCompletion is not { } completion) return;
                await completion.Task.WaitAsync(_lifetimeCancellation.Token);
            }
            if (_s3SettingsSaveTask is { IsCompleted: false } saving) await saving;
            _s3SettingsSaveTask = PersistPlusCloudSyncSettingsAsync(state);
            await _s3SettingsSaveTask;
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException) { S3SyncStatusText.Text = UiText.Localize(error.Message); }
    }

    private async Task<bool> PersistPlusCloudSyncSettingsAsync(AccountStateChangedEventArgs state)
    {
        _s3SettingsSaving = true;
        SetS3SyncControlsBusy(_s3SyncBusy);
        try
        {
            var current = await _modules.Accounts.Service.GetStatusAsync(cancellationToken: _lifetimeCancellation.Token);
            if (current.User?.Id != state.Account.User?.Id || current.Region != state.Account.Region || current.RequiresDataRestart) return false;
            var editVersion = _s3SettingsEditVersion;
            var draft = ReadS3SyncSettingsFromControls();
            var hadDraft = _s3SettingsDirty;
            var settings = _s3SyncStoredSettings.Settings with
            {
                Provider = SyncProvider.AccountServer, AccountRegion = current.Region,
                AccountServerUrl = current.ServerUrl, AccountUserId = current.User!.Id
            };
            // A missing sync key keeps transfers disabled until the user sets one.
            settings = settings with { Enabled = settings.Enabled && settings.IsConfigured };
            await _s3SyncService.SaveSettingsAsync(_s3SyncStoredSettings.DeviceId, settings, _lifetimeCancellation.Token);
            _s3SyncStoredSettings = new(_s3SyncStoredSettings.DeviceId, settings);
            _s3DeletionConfirmationPending = false;
            _s3SyncCancelledByUser = false;
            _lastS3SyncAt = null;
            _localSyncChanges.MarkChanged();
            if (_s3SettingsEditVersion == editVersion)
            {
                var selectedDraft = draft with { Provider = settings.Provider, AccountRegion = settings.AccountRegion,
                    AccountServerUrl = settings.AccountServerUrl, AccountUserId = settings.AccountUserId };
                selectedDraft = selectedDraft with { Enabled = selectedDraft.Enabled && selectedDraft.IsConfigured };
                PopulateS3SyncControls(hadDraft ? selectedDraft : null);
            }
            else UpdateS3SettingsDraftState();
            RefreshS3SyncIndicatorFromSettings();
            S3SyncStatusText.Text = UiText.Get(settings.IsConfigured ? "已切换到 Kkindle 云同步，可随时选择其他同步方式。"
                : "已选择 Kkindle 云同步，设置同步密钥后即可启用。可随时选择其他同步方式。");
            if (IsAutomaticS3SyncReady()) ScheduleS3LocalChangeSync(S3LocalChangeSyncDebounce);
            else _s3LocalChangeSyncTimer.Stop();
            return true;
        }
        finally
        {
            _s3SettingsSaving = false;
            SetS3SyncControlsBusy(_s3SyncBusy);
        }
    }

    private void StartDeviceChecks()
    {
        if (_deviceChecksStarted) return;
        _deviceChecksStarted = true;
        _modules.Accounts.Service.DeviceInvalidated += OnDeviceInvalidated;
        _modules.Accounts.Monitor.MembershipExpired += OnMembershipExpired;
        _modules.Accounts.Monitor.Start();
    }

    private void OnMembershipExpired(object? sender, Kkindle.Infrastructure.AccountMembershipExpiredEventArgs e) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            if (_lifetimeCancellation.IsCancellationRequested) return;
            await RefreshAccountSummaryAsync();
            await ShowMessageAsync(UiText.Get("会员已过期"), e.Message);
        });

    private void OnDeviceInvalidated(object? sender, Kkindle.Infrastructure.AccountDeviceInvalidatedEventArgs e) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            if (_lifetimeCancellation.IsCancellationRequested) return;
            await RefreshAccountSummaryAsync();
            await ShowMessageAsync(UiText.Get("设备已失效"), UiText.Localize(e.Message));
        });

    private async Task RefreshAccountSummaryAsync()
    {
        var revision = ++_accountSummaryRevision;
        try
        {
            var status = await _modules.Accounts.Service.GetStatusAsync(cancellationToken: _lifetimeCancellation.Token);
            if (revision != _accountSummaryRevision || _lifetimeCancellation.IsCancellationRequested) return;
            PresentAccountSummary(status, null);
            revision = _accountSummaryRevision;
            if (status.IsLoggedIn)
            {
                try
                {
                    var membership = await _modules.Accounts.Membership.RefreshAsync(_lifetimeCancellation.Token);
                    if (revision == _accountSummaryRevision && !_lifetimeCancellation.IsCancellationRequested)
                        PresentAccountSummary(status, membership.Membership);
                }
                catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested) { }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    // A failed membership refresh keeps the known account
                    // label; login state and feature authorization are separate.
                }
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (_syncAccountStatus is { } known) PresentAccountSummary(known, null);
        }
    }

    private void PresentAccountSummary(AccountStatus status, AccountMembership? membership)
    {
        ++_accountSummaryRevision;
        if (_syncAccountStatus?.User?.Id != status.User?.Id || _syncAccountStatus?.Region != status.Region || !status.IsLoggedIn)
            _accountNavigationMembership = null;
        if (membership is not null && membership.AccountId == status.User?.Id && membership.Region == status.Region)
            _accountNavigationMembership = membership;
        _syncAccountStatus = status;
        var tier = AccountVisuals.Tier(_accountNavigationMembership);
        AccountNavigationText.Text = UiText.Get(status.IsLoggedIn ? "已登录" : "登录");
        AccountNavigationIcon.Data = AccountVisuals.MembershipIcon(tier, status.IsLoggedIn);
        AccountVisuals.SetTier(AccountNavigationIcon, tier);
        var region = UiText.Get(AccountRegions.DisplayName(status.Region));
        var tooltip = status.IsLoggedIn ? status.User!.Username + "\n" + region
            + (_accountNavigationMembership is null ? "" : " · " + UiText.Get(tier switch { "plus" => "Plus 会员", "lifetime" => "功能解锁", _ => "Free" })) : region;
        AccountNavigationButton.SetValue(Avalonia.Controls.ToolTip.TipProperty, tooltip);
        UpdateAccountSyncPresentation();
        UpdateS3SettingsDraftState();
    }

    private bool IsAccountSyncKnownUnavailable() => _syncAccountStatus is not { IsLoggedIn: true }
        || _syncAccountStatus.RequiresDataRestart || _syncAccountStatus.Region != _modules.DataRegion
        || (_accountNavigationMembership is not null && AccountVisuals.Tier(_accountNavigationMembership) != "plus");

    private void UpdateAccountSyncPresentation()
    {
        var status = _syncAccountStatus;
        var account = SyncProviderBox.SelectedIndex == (int)SyncProvider.AccountServer;
        var signedIn = status is { IsLoggedIn: true };
        SyncProviderBox.Classes.Set("accountSignedIn", account && signedIn);
        AccountSyncProviderItem.Classes.Set("accountSignedIn", signedIn);
        Avalonia.Automation.AutomationProperties.SetHelpText(SyncProviderBox, account ? UiText.Get(signedIn ? "已登录" : "登录 / 注册") : "");
        var region = UiText.Get(AccountRegions.DisplayName(status?.Region ?? _modules.DataRegion));
        var tier = AccountVisuals.Tier(_accountNavigationMembership);
        var tooltip = UiText.Get(signedIn ? "已登录" : "登录 / 注册") + "\n" + region
            + (_accountNavigationMembership is null ? "" : " · "
            + UiText.Get(tier switch { "plus" => "Plus 会员", "lifetime" => "功能解锁", _ => "Free" }))
            + "\n" + (Avalonia.Application.Current?.FindResource("Sync.AccountHint")?.ToString() ?? "仅同步当前区域账号的数据，两区不互通。");
        SyncProviderBox.SetValue(Avalonia.Controls.ToolTip.TipProperty, account ? tooltip : null);
        AccountSyncProviderItem.SetValue(Avalonia.Controls.ToolTip.TipProperty, tooltip);

        string? notice = null;
        string? details = null;
        if (status is { RequiresDataRestart: true } || status is not null && status.Region != _modules.DataRegion)
            notice = UiText.Get("区域已切换，请重新打开应用后同步。");
        else if (status is not { IsLoggedIn: true })
            notice = UiText.Get("登录 Plus 账户后使用云同步。");
        else if (_accountNavigationMembership is not null && tier != "plus")
        {
            if (_accountNavigationMembership.ProExpiresAt is { } expiry && expiry <= DateTimeOffset.UtcNow)
            {
                var retentionEnd = MemberEntitlements.CloudRetentionEndsAt(expiry);
                notice = DateTimeOffset.UtcNow < retentionEnd
                    ? UiText.Get("Plus 已到期，同步与下载已暂停。云端数据保留至 {0:yyyy/MM/dd}，届时自动销毁。", retentionEnd.LocalDateTime)
                    : UiText.Get("Plus 已到期，同步与下载已暂停。云端数据保留期已结束。");
                details = MemberEntitlements.PlusExpiryMessage(expiry, DateTimeOffset.UtcNow);
            }
            else notice = UiText.Get("Kkindle 云同步需要 Plus 会员。");
        }
        AccountSyncNoticeText.Text = notice;
        AccountSyncNoticeText.IsVisible = account && notice is not null;
        AccountSyncNoticeText.SetValue(Avalonia.Controls.ToolTip.TipProperty, details);
    }

    private Task<S3SyncSettings> BindAccountSyncSettingsAsync(S3SyncSettings settings, CancellationToken token) =>
        settings.Provider == SyncProvider.AccountServer
            ? _modules.Accounts.Service.CreateSyncSettingsAsync(settings, token)
            : Task.FromResult(settings with { AccountRegion = _modules.DataRegion });
}
