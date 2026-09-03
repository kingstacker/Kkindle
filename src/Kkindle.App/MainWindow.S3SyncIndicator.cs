using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Kkindle.Core;

namespace Kkindle;

public partial class MainWindow
{
    private enum S3SyncIndicatorState
    {
        NotConfigured,
        Ready,
        Pending,
        Syncing,
        Succeeded,
        Failed
    }

    private readonly DispatcherTimer _s3SyncIndicatorAnimationTimer =
        new() { Interval = TimeSpan.FromMilliseconds(50) };
    private RotateTransform? _s3SyncSpinnerTransform;
    private S3SyncIndicatorState _s3SyncIndicatorState = S3SyncIndicatorState.NotConfigured;
    private string? _s3SyncIndicatorError;

    private void InitializeS3SyncIndicator()
    {
        _s3SyncIndicatorAnimationTimer.Tick += (_, _) => TickS3SyncIndicatorAnimation();
        UpdateS3SyncIndicator(S3SyncIndicatorState.NotConfigured);
    }

    private void RefreshS3SyncIndicatorFromSettings()
    {
        var settings = _s3SyncStoredSettings.Settings;
        UpdateS3SyncIndicator(
            settings.Enabled && settings.IsConfigured && HasPendingS3LocalChanges
                ? S3SyncIndicatorState.Pending
                : settings.Enabled && settings.IsConfigured
                ? S3SyncIndicatorState.Ready
                : S3SyncIndicatorState.NotConfigured);
    }

    private void UpdateS3SyncIndicator(S3SyncIndicatorState state, string? error = null)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => UpdateS3SyncIndicator(state, error));
            return;
        }

        _s3SyncIndicatorState = state;
        _s3SyncIndicatorError = error;
        var syncing = state == S3SyncIndicatorState.Syncing;

        S3SyncIndicatorButton.Classes.Set("syncing", syncing);
        S3SyncIndicatorButton.Classes.Set("pending", state == S3SyncIndicatorState.Pending);
        S3SyncIndicatorButton.Classes.Set("success", state == S3SyncIndicatorState.Succeeded);
        S3SyncIndicatorButton.Classes.Set("failed", state == S3SyncIndicatorState.Failed);
        S3SyncIndicatorButton.Classes.Set("unconfigured", state == S3SyncIndicatorState.NotConfigured);
        S3SyncCloudIcon.IsVisible = true;
        S3SyncSpinnerIcon.IsVisible = syncing;
        S3SyncSuccessGlyph.IsVisible = state == S3SyncIndicatorState.Succeeded;
        S3SyncFailureGlyph.IsVisible = state == S3SyncIndicatorState.Failed;
        S3SyncCloudIcon.Stroke = state == S3SyncIndicatorState.NotConfigured
            ? Brushes.Gray
            : Brushes.Black;
        S3SyncCloudIcon.StrokeThickness = state == S3SyncIndicatorState.Failed ? 1.9 : 1.6;

        var tooltip = BuildS3SyncIndicatorTooltip();
        ToolTip.SetTip(S3SyncIndicatorButton, tooltip);
        AutomationProperties.SetName(S3SyncIndicatorButton, tooltip);

        if (syncing)
            StartS3SyncIndicatorAnimation();
        else
            StopS3SyncIndicatorAnimation();
    }

    private string BuildS3SyncIndicatorTooltip() => _s3SyncIndicatorState switch
    {
        S3SyncIndicatorState.Ready => T("S3 同步已就绪；点击立即同步"),
        S3SyncIndicatorState.Pending => T("有本地变更待同步；即将自动同步"),
        S3SyncIndicatorState.Syncing => T("正在同步到 S3…"),
        S3SyncIndicatorState.Succeeded => T("S3 同步完成；点击再次同步"),
        S3SyncIndicatorState.Failed when !string.IsNullOrWhiteSpace(_s3SyncIndicatorError)
            => T("S3 同步失败：{0}；点击重试", _s3SyncIndicatorError),
        S3SyncIndicatorState.Failed => T("S3 同步失败；点击重试"),
        _ => T("S3 同步未启用或未配置；点击打开设置")
    };

    private void StartS3SyncIndicatorAnimation()
    {
        _s3SyncSpinnerTransform ??= new RotateTransform(0);
        _s3SyncSpinnerTransform.Angle = 0;
        S3SyncSpinnerIcon.RenderTransform = _s3SyncSpinnerTransform;
        _s3SyncIndicatorAnimationTimer.Start();
    }

    private void StopS3SyncIndicatorAnimation()
    {
        _s3SyncIndicatorAnimationTimer.Stop();
        if (_s3SyncSpinnerTransform is { } transform)
        {
            transform.Angle = 0;
            S3SyncSpinnerIcon.RenderTransform = transform;
        }
    }

    private void TickS3SyncIndicatorAnimation()
    {
        if (!_s3SyncBusy || _s3SyncIndicatorState != S3SyncIndicatorState.Syncing)
        {
            StopS3SyncIndicatorAnimation();
            return;
        }

        _s3SyncSpinnerTransform ??= new RotateTransform(0);
        _s3SyncSpinnerTransform.Angle = (_s3SyncSpinnerTransform.Angle + 18) % 360;
    }

    private async void S3SyncIndicatorButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_s3SyncBusy) return;

        var settings = _s3SyncStoredSettings.Settings;
        if (!settings.Enabled || settings.Validate() is not null)
        {
            ShowStage3Page(SettingsPage, SystemS3SyncNavigationButton);
            ShowSettingsSection("Library");
            ShowSystemSettingsSection("Sync");
            ShowSettingsPanel(SystemSettingsPane);
            S3SyncStatusText.Text = settings.Enabled
                ? T("请先完善 S3 同步设置。")
                : T("请先开启 S3 同步。 ");
            return;
        }

        await RunS3SyncAsync(silent: false);
    }
}
