using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Kkindle.Core;

namespace Kkindle;

public partial class MainWindow
{
    private const double ReaderTtsFloatingPanelWidth = 128;
    private const double ReaderTtsFloatingPanelHeight = 128;
    private const double ReaderTtsFloatingPanelRightMargin = 22;
    private const double ReaderTtsFloatingPanelBottomMargin = 20;
    private const double ReaderTtsFloatingCollapsedVisibleWidth = 16;
    private const double ReaderTtsFloatingCollapsedOffset =
        ReaderTtsFloatingPanelWidth
        + ReaderTtsFloatingPanelRightMargin
        - ReaderTtsFloatingCollapsedVisibleWidth;
    private const double ReaderTtsFloatingEdgeActivationWidth = 36;
    private const double ReaderTtsFloatingEdgeActivationTopPadding = 22;
    private const double ReaderTtsFloatingEdgeActivationBottomPadding = 10;
    private const int ReaderTtsFloatingCollapseDelayMs = 700;
    private const int ReaderTtsFloatingAutoHideDelayMs = 2200;
    private const int ReaderTtsFloatingPointerExitCollapseDelayMs = 180;
    private const int ReaderTtsButtonBreathingIntervalMs = 50;
    private const double ReaderTtsButtonBreathingPeriodMs = 1800;
    private const double ReaderTtsSpectrumMinimumHeight = 4;
    private const double ReaderTtsSpectrumMaximumHeight = 22;

    private sealed record ReaderTtsPrefetchRequest(
        int ChapterIndex,
        string ChapterPath,
        string Key,
        string BookKey,
        string ChapterKey,
        bool ParagraphIndent);

    private TtsSettings _readerTtsSettings = new();
    private bool _readerTtsVoicesLoading;
    private int _readerTtsVoicesRequest;
    private bool _readerTtsAutoNavigation;
    private bool _readerTtsFloatingRequested;
    private bool _readerTtsFloatingExpanded;
    private bool _readerTtsFloatingStartupShowing;
    private bool _readerTtsResumeAfterNavigation;
    private long _readerTtsNavigationRequestVersion;
    private bool _readerTtsEnvironmentChecked;
    private bool _readerTtsPreviewInProgress;
    private long _readerTtsHighlightVersion;
    private string? _readerTtsNotice;
    private Task? _readerTtsEnvironmentTask;
    private DispatcherTimer? _readerTtsFloatingCollapseTimer;
    private DispatcherTimer? _readerTtsFloatingAutoHideTimer;
    private DispatcherTimer? _readerTtsButtonBreathingTimer;
    private double _readerTtsButtonBreathingPhase;

    private static bool IsZhCnVoice(TtsVoiceInfo voice)
    {
        var culture = voice.Culture?.Replace('_', '-');
        return string.Equals(culture, "zh-CN", StringComparison.OrdinalIgnoreCase)
            || voice.Id.StartsWith("zh-CN-", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsReaderTtsActiveState(TtsPlaybackState state)
        => state is TtsPlaybackState.Generating
            or TtsPlaybackState.Playing
            or TtsPlaybackState.Paused
            or TtsPlaybackState.AdvancingChapter;

    private long BeginReaderTtsNavigationRequest(bool shouldResume)
    {
        var requestVersion = Interlocked.Increment(
            ref _readerTtsNavigationRequestVersion);
        if (shouldResume)
            _readerTtsResumeAfterNavigation = true;
        return requestVersion;
    }

    private bool IsCurrentReaderTtsNavigationRequest(long requestVersion)
        => requestVersion == Volatile.Read(
            ref _readerTtsNavigationRequestVersion);

    private bool CanResumeReaderTtsNavigation(
        long requestVersion,
        bool shouldResume)
        => shouldResume
            && IsCurrentReaderTtsNavigationRequest(requestVersion)
            && _readerTtsResumeAfterNavigation;

    private void CompleteReaderTtsNavigationRequest(long requestVersion)
    {
        if (IsCurrentReaderTtsNavigationRequest(requestVersion))
            _readerTtsResumeAfterNavigation = false;
    }

    private void CancelReaderTtsContinuation()
    {
        _readerTtsResumeAfterNavigation = false;
        Interlocked.Increment(ref _readerTtsNavigationRequestVersion);
        _readerTtsFloatingRequested = false;
    }

    private ReaderTtsDocument? GetCurrentReaderTtsDocument()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            try
            {
                return Dispatcher.UIThread
                    .InvokeAsync(GetCurrentReaderTtsDocument)
                    .GetAwaiter()
                    .GetResult();
            }
            catch
            {
                return null;
            }
        }

        if (_readerIsPdf
            || _readerDocument is null
            || CurrentReaderHost is not NativeReaderHost host)
        {
            return null;
        }

        var speech = host.GetSpeechText();
        if (speech is null || string.IsNullOrWhiteSpace(speech.Text))
            return null;

        var key = string.Join(
            ":",
            _readerBookFile?.Id.ToString("N") ?? string.Empty,
            _readerChapterIndex,
            host.Source?.AbsoluteUri ?? string.Empty);
        return new ReaderTtsDocument(
            key,
            speech.Text,
            speech.StartOffset,
            (start, length) => ApplyReaderTtsHighlightAsync(host, start, length),
            () => ClearReaderTtsHighlight(host),
            speech.MapToSource,
            _readerBookFile?.Id.ToString("N"),
            $"{_readerChapterIndex}:{host.Source?.AbsoluteUri}",
            speech.PageBreakOffsets);
    }

    private async Task<ReaderTtsDocument?> GetNextReaderTtsDocumentAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var request = Dispatcher.UIThread.CheckAccess()
                ? CaptureNextReaderTtsPrefetchRequest()
                : await Dispatcher.UIThread.InvokeAsync(
                    CaptureNextReaderTtsPrefetchRequest);
            if (request is null) return null;

            var snapshot = await Task.Run(
                () => NativeReaderHost.LoadSpeechTextSnapshot(
                    request.ChapterPath,
                    request.ParagraphIndent,
                    cancellationToken),
                cancellationToken);
            if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.Text))
                return null;

            // This document is used only for cache warming. The visible host
            // supplies the real highlight callback after the chapter swap.
            return new ReaderTtsDocument(
                request.Key,
                snapshot.Text,
                snapshot.StartOffset,
                static (_, _) => Task.CompletedTask,
                static () => { },
                snapshot.MapToSource,
                request.BookKey,
                request.ChapterKey,
                snapshot.PageBreakOffsets);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch
        {
            // Next-chapter preparation is an optimization. The visible
            // chapter and the normal on-demand request remain authoritative.
            return null;
        }
    }

    private ReaderTtsPrefetchRequest? CaptureNextReaderTtsPrefetchRequest()
    {
        if (_readerIsPdf
            || _readerDocument is null
            || _readerBookFile is null)
        {
            return null;
        }

        var chapterIndex = _readerChapterIndex + 1;
        if (chapterIndex < 0 || chapterIndex >= _readerDocument.Chapters.Count)
            return null;

        var chapterPath = Path.GetFullPath(_readerDocument.Chapters[chapterIndex]);
        if (!File.Exists(chapterPath)) return null;

        var target = new Uri(chapterPath, UriKind.Absolute);
        var bookKey = _readerBookFile.Id.ToString("N");
        return new ReaderTtsPrefetchRequest(
            chapterIndex,
            chapterPath,
            string.Join(":", bookKey, chapterIndex, target.AbsoluteUri),
            bookKey,
            $"{chapterIndex}:{target.AbsoluteUri}",
            _readerLayout.ParagraphIndent);
    }

    private Task ApplyReaderTtsHighlightAsync(
        NativeReaderHost host,
        int start,
        int length)
    {
        var version = Interlocked.Increment(ref _readerTtsHighlightVersion);
        return RunOnReaderUiAsync(() =>
        {
            if (version != Volatile.Read(ref _readerTtsHighlightVersion)
                || !ReaderRoot.IsVisible
                || !ReferenceEquals(CurrentReaderHost, host))
            {
                return;
            }

            host.SetSpeechHighlight(start, length);
        });
    }

    private void ClearReaderTtsHighlight(NativeReaderHost host)
    {
        var version = Interlocked.Increment(ref _readerTtsHighlightVersion);
        try
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                if (version == Volatile.Read(ref _readerTtsHighlightVersion)
                    && ReferenceEquals(CurrentReaderHost, host))
                {
                    host.ClearSpeechHighlight();
                }

                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (version == Volatile.Read(ref _readerTtsHighlightVersion)
                    && ReferenceEquals(CurrentReaderHost, host))
                {
                    host.ClearSpeechHighlight();
                }
            });
        }
        catch
        {
            // The reader is closing; there is no visual state left to clear.
        }
    }

    private static Task RunOnReaderUiAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    action();
                    completion.TrySetResult(null);
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            });
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }

        return completion.Task;
    }

    private async Task<bool> AdvanceReaderForTtsAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Dispatcher.UIThread.CheckAccess())
        {
            var completion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                Dispatcher.UIThread.Post(async () =>
                {
                    try
                    {
                        completion.TrySetResult(
                            await AdvanceReaderForTtsAsync(cancellationToken));
                    }
                    catch (OperationCanceledException)
                        when (cancellationToken.IsCancellationRequested)
                    {
                        completion.TrySetCanceled(cancellationToken);
                    }
                    catch (Exception exception)
                    {
                        completion.TrySetException(exception);
                    }
                });
            }
            catch
            {
                return false;
            }

            return await completion.Task.WaitAsync(cancellationToken);
        }

        if (_readerIsPdf
            || _readerDocument is null
            || CurrentReaderHost is null)
        {
            return false;
        }

        var previousChapter = _readerChapterIndex;
        _readerTtsAutoNavigation = true;
        try
        {
            await MoveReaderChapterAsync(1);
            return _readerChapterIndex > previousChapter;
        }
        finally
        {
            _readerTtsAutoNavigation = false;
        }
    }

    private void ReaderTts_StateChanged(
        object? sender,
        TtsStateChangedEventArgs e)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => ReaderTts_StateChanged(sender, e));
            return;
        }

        if (e.State is TtsPlaybackState.Generating
            or TtsPlaybackState.Playing
            or TtsPlaybackState.Paused
            or TtsPlaybackState.AdvancingChapter)
        {
            _readerTtsFloatingRequested = true;
        }
        if (e.State is TtsPlaybackState.Stopped
            or TtsPlaybackState.Error)
        {
            _readerTtsFloatingRequested = false;
            _readerTtsFloatingStartupShowing = false;
            _readerTtsFloatingExpanded = false;
            _readerTtsFloatingCollapseTimer?.Stop();
            _readerTtsFloatingAutoHideTimer?.Stop();
        }
        if (e.State == TtsPlaybackState.Error
            && !string.IsNullOrWhiteSpace(e.Message))
        {
            ReaderStatusText.Text = T("朗读失败：{0}", UiText.Localize(e.Message));
        }

        UpdateReaderTtsUi();
    }

    private void ReaderTts_EnvironmentChanged(
        object? sender,
        EventArgs e)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => ReaderTts_EnvironmentChanged(sender, e));
            return;
        }

        UpdateReaderTtsUi();
    }

    /// <summary>
    /// Starts the one-per-window dependency bootstrap. App startup calls this
    /// in the background; opening a reader awaits the same task if it is still
    /// running.
    /// </summary>
    public Task InitializeTtsEnvironmentAsync()
    {
        if (_readerTtsEnvironmentTask is { } existing)
            return existing;

        _readerTtsEnvironmentTask = InitializeTtsEnvironmentCoreAsync();
        return _readerTtsEnvironmentTask;
    }

    private async Task InitializeTtsEnvironmentCoreAsync()
    {
        try
        {
            _readerTtsNotice = T("正在自动准备听书环境…");
            UpdateReaderTtsUi();
            var progress = new Progress<TtsSetupProgress>(update =>
            {
                _readerTtsNotice = update.Message;
                UpdateReaderTtsUi();
            });
            var availability = await _readerTts.EnsureEnvironmentReadyAsync(
                    progress,
                    _lifetimeCancellation.Token)
                .ConfigureAwait(false);
            _readerTtsEnvironmentChecked = true;
            _readerTtsNotice = availability.IsAvailable
                ? null
                : availability.Message;
        }
        catch (OperationCanceledException)
            when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _readerTtsEnvironmentChecked = false;
            _readerTtsNotice = T(
                "自动准备听书环境失败：{0}",
                UiText.Localize(exception.Message));
        }
        finally
        {
            UpdateReaderTtsUi();
        }
    }

    private async Task InitializeReaderTtsAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            _readerTtsSettings = await _ttsSettingsStore.LoadAsync(cancellationToken);
            ApplyReaderTtsSettingsToControls();
            if (!_readerTtsEnvironmentChecked)
            {
                await InitializeTtsEnvironmentAsync()
                    .WaitAsync(cancellationToken);
            }

            UpdateReaderTtsUi();
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _readerTtsNotice = T(
                "读取听书设置失败：{0}",
                UiText.Localize(exception.Message));
            UpdateReaderTtsUi();
        }
    }

    private void ApplyReaderTtsSettingsToControls()
    {
        if (ReaderTtsVoiceBox is null) return;

        _readerTtsSettings = TtsSettings.Normalize(_readerTtsSettings);
        ReaderTtsVoiceBox.Text = _readerTtsSettings.Voice;
        ReaderTtsSpeedSlider.Value = _readerTtsSettings.Speed;
        ReaderTtsVolumeSlider.Value = _readerTtsSettings.Volume;
        ReaderTtsPitchSlider.Value = _readerTtsSettings.Pitch;
        ReaderTtsAutoAdvanceCheck.IsChecked = _readerTtsSettings.AutoAdvance;
        UpdateReaderTtsValueLabels();
    }

    private void CaptureReaderTtsSettingsFromControls()
    {
        _readerTtsSettings.Provider = TtsSettings.DefaultProvider;
        var selectedVoice = ReaderTtsVoiceBox.SelectedItem as ComboBoxItem;
        _readerTtsSettings.Voice = selectedVoice?.Tag as string
            ?? ReaderTtsVoiceBox.Text?.Trim()
            ?? TtsOptions.DefaultVoice;
        _readerTtsSettings.Speed = ReaderTtsSpeedSlider.Value;
        _readerTtsSettings.Volume = (int)Math.Round(ReaderTtsVolumeSlider.Value);
        _readerTtsSettings.Pitch = (int)Math.Round(ReaderTtsPitchSlider.Value);
        _readerTtsSettings.AutoAdvance = ReaderTtsAutoAdvanceCheck.IsChecked == true;
        _readerTtsSettings = TtsSettings.Normalize(_readerTtsSettings);
    }

    private void UpdateReaderTtsValueLabels()
    {
        if (ReaderTtsSpeedValueText is not null && ReaderTtsSpeedSlider is not null)
        {
            ReaderTtsSpeedValueText.Text = ReaderTtsSpeedSlider.Value
                .ToString("0.0×", System.Globalization.CultureInfo.InvariantCulture);
        }

        if (ReaderTtsVolumeValueText is not null && ReaderTtsVolumeSlider is not null)
            ReaderTtsVolumeValueText.Text = $"{ReaderTtsVolumeSlider.Value:0}%";
        if (ReaderTtsPitchValueText is not null && ReaderTtsPitchSlider is not null)
            ReaderTtsPitchValueText.Text = $"{ReaderTtsPitchSlider.Value:+0;-0;0} Hz";
    }

    private void ReaderTtsSpeedSlider_ValueChanged(
        object? sender,
        RangeBaseValueChangedEventArgs e)
        => UpdateReaderTtsValueLabels();

    private void ReaderTtsVolumeSlider_ValueChanged(
        object? sender,
        RangeBaseValueChangedEventArgs e)
        => UpdateReaderTtsValueLabels();

    private void ReaderTtsPitchSlider_ValueChanged(
        object? sender,
        RangeBaseValueChangedEventArgs e)
        => UpdateReaderTtsValueLabels();

    private void UpdateReaderTtsUi()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(UpdateReaderTtsUi);
            return;
        }
        if (ReaderTtsButton is null) return;

        // An image-only page/chapter has no current speech document, but it is
        // still a valid starting point: TtsService will advance to the next
        // chapter containing visible text when playback starts.
        var documentAvailable = !_readerIsPdf
            && _readerDocument is not null
            && CurrentReaderHost is not null;
        var state = _readerTts.State;
        var isActive = IsReaderTtsActiveState(state);
        var buttonText = _readerTts.EnvironmentSetupInProgress
            ? T("准备中")
            : state switch
            {
                TtsPlaybackState.Generating => T("生成中"),
                TtsPlaybackState.Playing => T("朗读中"),
                TtsPlaybackState.Paused => T("已暂停"),
                TtsPlaybackState.AdvancingChapter => T("跳转中"),
                _ => T("听书")
            };
        ReaderTtsButtonText.Text = buttonText;
        ToolTip.SetTip(
            ReaderTtsButton,
            _readerTts.EnvironmentSetupInProgress
                ? T("准备听书环境")
                : isActive ? T("停止听书") : T("开始听书"));
        UpdateReaderTtsButtonVisualState(state);
        ReaderTtsButton.IsEnabled = _readerTts.CanOpen
            && documentAvailable
            && !_readerTts.EnvironmentSetupInProgress;
        ReaderTtsTitleText.Text = T("听书");
        ReaderTtsDescriptionText.Text = T(
            "首次启动会自动准备 edge-tts；生成语音需要联网。Windows 和 Linux 均支持。");
        ReaderTtsProviderLabelText.Text = T("语音引擎");
        ReaderTtsEngineText.Text = "edge-tts";
        ReaderTtsVoiceLabelText.Text = T("声音");
        ReaderTtsVoiceBox.PlaceholderText = T("选择 edge-tts 语音");
        ReaderTtsPreviewButton.Content = _readerTtsPreviewInProgress
            ? T("试听中…")
            : T("试听");
        ReaderTtsPreviewSampleText.Text = T(
            "试听内容：{0}",
            TtsService.PreviewText);
        ReaderTtsRefreshVoicesButton.Content = T("刷新语音");
        ReaderTtsSpeedLabelText.Text = T("语速");
        ReaderTtsVolumeLabelText.Text = T("音量");
        ReaderTtsPitchLabelText.Text = T("音调");
        ReaderTtsAutoAdvanceCheck.Content = T("自动朗读下一章");
        ReaderTtsApplyButton.Content = T("应用设置");
        ReaderTtsPreviousSegmentButton.Content = T("上一段");
        ReaderTtsNextSegmentButton.Content = T("下一段");
        ReaderTtsStopButton.Content = T("停止");

        ReaderTtsPlayButton.Content = state switch
        {
            TtsPlaybackState.Playing => T("暂停"),
            TtsPlaybackState.Paused => T("继续"),
            TtsPlaybackState.Generating
                or TtsPlaybackState.AdvancingChapter => T("处理中…"),
            _ => T("播放")
        };
        ReaderTtsPlayButton.IsEnabled = _readerTts.CanOpen
            && documentAvailable
            && !_readerTtsPreviewInProgress
            && !_readerTts.EnvironmentSetupInProgress
            && state is not TtsPlaybackState.Generating
                and not TtsPlaybackState.AdvancingChapter;
        ReaderTtsPreviewButton.IsEnabled = _readerTts.CanOpen
            && !_readerTtsPreviewInProgress
            && !_readerTts.EnvironmentSetupInProgress
            && state is TtsPlaybackState.Stopped
                or TtsPlaybackState.Error;
        ReaderTtsStopButton.IsEnabled = state is not TtsPlaybackState.Stopped;
        ReaderTtsPreviousSegmentButton.IsEnabled = _readerTts.CanSkipPrevious;
        ReaderTtsNextSegmentButton.IsEnabled = _readerTts.CanSkipNext;
        ReaderTtsStatusText.Text = _readerTtsPreviewInProgress
            ? T("正在试听：{0}", TtsService.PreviewText)
            : state switch
            {
                TtsPlaybackState.Generating => T(
                    "正在生成语音… {0}/{1}",
                    _readerTts.SegmentIndex,
                    _readerTts.SegmentCount),
                TtsPlaybackState.Playing => T(
                    "正在朗读… {0}/{1}",
                    _readerTts.SegmentIndex,
                    _readerTts.SegmentCount),
                TtsPlaybackState.Paused => T("听书已暂停。"),
                TtsPlaybackState.AdvancingChapter => T("正在切换章节…"),
                TtsPlaybackState.Error => T(
                    "听书失败：{0}",
                    UiText.Localize(_readerTts.Message ?? T("未知错误"))),
                _ => _readerTts.EnvironmentSetupInProgress
                    ? _readerTts.EnvironmentMessage ?? T("正在自动准备听书环境…")
                    : _readerTtsNotice
                    ?? _readerTts.EnvironmentMessage
                    ?? (_readerTts.Availability is { IsAvailable: false } unavailable
                        ? unavailable.Message
                        : string.Empty)
            };

        UpdateReaderTtsFloatingUi(state);
    }

    private void UpdateReaderTtsButtonVisualState(TtsPlaybackState state)
    {
        if (ReaderTtsButton is null
            || ReaderTtsButtonText is null
            || ReaderTtsSpectrum is null)
        {
            return;
        }

        var active = IsReaderTtsActiveState(state);
        var playing = state == TtsPlaybackState.Playing;
        ReaderTtsButton.Classes.Set("ttsActive", active);
        ReaderTtsButtonText.IsVisible = !playing;
        ReaderTtsSpectrum.IsVisible = playing;
        if (!playing)
        {
            _readerTtsButtonBreathingTimer?.Stop();
            _readerTtsButtonBreathingPhase = 0;
            ResetReaderTtsSpectrum();
            return;
        }

        _readerTtsButtonBreathingTimer ??= CreateReaderTtsButtonBreathingTimer();
        if (_readerTtsButtonBreathingTimer.IsEnabled) return;

        _readerTtsButtonBreathingPhase = 0;
        UpdateReaderTtsSpectrum();
        _readerTtsButtonBreathingTimer.Start();
    }

    private DispatcherTimer CreateReaderTtsButtonBreathingTimer()
    {
        var timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(ReaderTtsButtonBreathingIntervalMs)
        };
        timer.Tick += ReaderTtsButtonBreathingTimer_Tick;
        return timer;
    }

    private void ReaderTtsButtonBreathingTimer_Tick(object? sender, EventArgs e)
    {
        var active = _readerTts.State == TtsPlaybackState.Playing;
        if (ReaderTtsButton is null
            || ReaderTtsButtonText is null
            || ReaderTtsSpectrum is null
            || !active)
        {
            _readerTtsButtonBreathingTimer?.Stop();
            if (ReaderTtsButton is not null)
                ReaderTtsButton.Classes.Set("ttsActive", false);
            if (ReaderTtsButtonText is not null)
                ReaderTtsButtonText.IsVisible = true;
            if (ReaderTtsSpectrum is not null)
                ReaderTtsSpectrum.IsVisible = false;
            ResetReaderTtsSpectrum();
            return;
        }

        UpdateReaderTtsSpectrum();
        _readerTtsButtonBreathingPhase +=
            2 * Math.PI * ReaderTtsButtonBreathingIntervalMs
            / ReaderTtsButtonBreathingPeriodMs;
        if (_readerTtsButtonBreathingPhase >= 2 * Math.PI)
            _readerTtsButtonBreathingPhase -= 2 * Math.PI;
    }

    private void UpdateReaderTtsSpectrum()
    {
        var bars = new[]
        {
            ReaderTtsSpectrumBar1,
            ReaderTtsSpectrumBar2,
            ReaderTtsSpectrumBar3,
            ReaderTtsSpectrumBar4
        };
        var offsets = new[] { 0.1, 1.3, 2.6, 3.8 };
        var ranges = new[] { 0.55, 0.82, 1.00, 0.68 };
        for (var index = 0; index < bars.Length; index++)
        {
            var wave = (1 - Math.Cos(
                _readerTtsButtonBreathingPhase + offsets[index])) / 2;
            bars[index].Height = ReaderTtsSpectrumMinimumHeight
                + (wave * ranges[index]
                    * (ReaderTtsSpectrumMaximumHeight
                        - ReaderTtsSpectrumMinimumHeight));
        }
    }

    private void ResetReaderTtsSpectrum()
    {
        if (ReaderTtsSpectrumBar1 is null)
            return;

        ReaderTtsSpectrumBar1.Height = 6;
        ReaderTtsSpectrumBar2.Height = 11;
        ReaderTtsSpectrumBar3.Height = 17;
        ReaderTtsSpectrumBar4.Height = 22;
    }

    private void UpdateReaderTtsFloatingUi(TtsPlaybackState state)
    {
        if (ReaderTtsFloatingPanel is null
            || ReaderTtsFloatingEdgeActivationRegion is null
            || ReaderTtsFloatingToggleButton is null
            || ReaderTtsFloatingStopButton is null)
        {
            return;
        }

        var active = IsReaderTtsFloatingActive();
        if (!active)
        {
            _readerTtsFloatingStartupShowing = false;
            _readerTtsFloatingExpanded = false;
            _readerTtsFloatingCollapseTimer?.Stop();
            _readerTtsFloatingAutoHideTimer?.Stop();
        }

        ReaderTtsFloatingPanel.IsVisible = active;
        ReaderTtsFloatingEdgeActivationRegion.IsVisible = active;
        // The edge strip is a wake-up target only while the wheel is hidden.
        // Leaving it hit-testable while expanded can re-enter the strip as
        // the pointer leaves the wheel and cancel the collapse timer.
        ReaderTtsFloatingEdgeActivationRegion.IsHitTestVisible = active
            && !_readerTtsFloatingExpanded;
        if (ReaderTtsFloatingPanel.RenderTransform is TranslateTransform transform)
        {
            transform.X = active && _readerTtsFloatingExpanded
                ? 0
                : ReaderTtsFloatingCollapsedOffset;
        }

        UpdateReaderTtsFloatingPointerWatch();
        if (!active) return;

        var canToggle = state is not TtsPlaybackState.Generating
            and not TtsPlaybackState.AdvancingChapter;
        ReaderTtsFloatingToggleButton.Content = state switch
        {
            TtsPlaybackState.Playing => "Ⅱ",
            TtsPlaybackState.Generating
                or TtsPlaybackState.AdvancingChapter => "…",
            _ => "▶"
        };
        ReaderTtsFloatingToggleButton.IsEnabled = canToggle;
        ReaderTtsFloatingStopButton.IsEnabled = state is not TtsPlaybackState.Stopped;
        ReaderTtsFloatingPreviousButton.IsEnabled = _readerChapterIndex > 0;
        ReaderTtsFloatingNextButton.IsEnabled = _readerDocument is not null
            && _readerChapterIndex >= 0
            && _readerChapterIndex + 1 < _readerDocument.Chapters.Count;
        ToolTip.SetTip(ReaderTtsFloatingMenuButton, T("听书菜单"));
        ToolTip.SetTip(ReaderTtsFloatingPreviousButton, T("上一章"));
        ToolTip.SetTip(ReaderTtsFloatingNextButton, T("下一章"));
        ToolTip.SetTip(
            ReaderTtsFloatingToggleButton,
            state == TtsPlaybackState.Playing ? T("暂停") : T("继续"));
        ToolTip.SetTip(ReaderTtsFloatingStopButton, T("停止听书"));
    }

    private bool IsReaderTtsFloatingActive()
        => _readerTtsFloatingRequested
            && (IsReaderTtsActiveState(_readerTts.State)
                || _readerTtsFloatingStartupShowing)
            && ReaderRoot is not null
            && ReaderRoot.IsVisible
            && !_readerIsPdf
            && _readerDocument is not null;

    private void UpdateReaderTtsFloatingPointerWatch()
    {
        if (!OperatingSystem.IsWindows()) return;

        if (_readerZenMode || IsReaderTtsFloatingActive())
            StartReaderZenPointerWatch();
        else
            _readerZenPointerWatchTimer?.Stop();
    }

    private void ExpandReaderTtsFloatingPanel()
    {
        if (!IsReaderTtsFloatingActive()) return;

        _readerTtsFloatingCollapseTimer?.Stop();
        _readerTtsFloatingAutoHideTimer?.Stop();
        _readerTtsFloatingStartupShowing = false;
        if (_readerTtsFloatingExpanded) return;

        _readerTtsFloatingExpanded = true;
        UpdateReaderTtsFloatingUi(_readerTts.State);
    }

    private void CollapseReaderTtsFloatingPanel()
    {
        _readerTtsFloatingCollapseTimer?.Stop();
        _readerTtsFloatingAutoHideTimer?.Stop();
        _readerTtsFloatingStartupShowing = false;
        if (!_readerTtsFloatingExpanded) return;

        _readerTtsFloatingExpanded = false;
        UpdateReaderTtsFloatingUi(_readerTts.State);
    }

    private void ShowReaderTtsFloatingPanelTemporarily()
    {
        // The header/popup start action is away from the wheel. Give the
        // user a short visual confirmation without making the wheel stay on
        // screen for the whole reading session.
        _readerTtsFloatingStartupShowing = true;
        if (!IsReaderTtsFloatingActive())
        {
            _readerTtsFloatingStartupShowing = false;
            return;
        }

        _readerTtsFloatingExpanded = true;
        _readerTtsFloatingCollapseTimer?.Stop();
        UpdateReaderTtsFloatingUi(_readerTts.State);
        StartReaderTtsFloatingAutoHideTimer();
    }

    private void StartReaderTtsFloatingAutoHideTimer()
    {
        if (!IsReaderTtsFloatingActive()
            || !_readerTtsFloatingExpanded
            || ReaderTtsPopup.IsOpen)
        {
            return;
        }

        _readerTtsFloatingAutoHideTimer ??= new DispatcherTimer();
        _readerTtsFloatingAutoHideTimer.Stop();
        _readerTtsFloatingAutoHideTimer.Interval =
            TimeSpan.FromMilliseconds(ReaderTtsFloatingAutoHideDelayMs);
        _readerTtsFloatingAutoHideTimer.Tick -=
            ReaderTtsFloatingAutoHideTimer_Tick;
        _readerTtsFloatingAutoHideTimer.Tick +=
            ReaderTtsFloatingAutoHideTimer_Tick;
        _readerTtsFloatingAutoHideTimer.Start();
    }

    private void ReaderTtsFloatingAutoHideTimer_Tick(
        object? sender,
        EventArgs e)
    {
        _readerTtsFloatingAutoHideTimer?.Stop();
        if (!ReaderTtsPopup.IsOpen && _readerTtsFloatingStartupShowing)
            CollapseReaderTtsFloatingPanel();
    }

    private void ScheduleReaderTtsFloatingCollapse(
        int delayMs = ReaderTtsFloatingCollapseDelayMs)
    {
        if (!IsReaderTtsFloatingActive()
            || !_readerTtsFloatingExpanded
            || ReaderTtsPopup.IsOpen)
        {
            return;
        }

        _readerTtsFloatingCollapseTimer ??= new DispatcherTimer();
        var interval = TimeSpan.FromMilliseconds(Math.Max(1, delayMs));
        // The Windows pointer watcher samples every 80ms. Do not restart an
        // already-running countdown on every sample, or it can never tick.
        // A newly requested shorter delay is still allowed to win.
        if (_readerTtsFloatingCollapseTimer.IsEnabled
            && _readerTtsFloatingCollapseTimer.Interval <= interval)
        {
            return;
        }

        _readerTtsFloatingCollapseTimer.Stop();
        _readerTtsFloatingCollapseTimer.Interval = interval;
        _readerTtsFloatingCollapseTimer.Tick -=
            ReaderTtsFloatingCollapseTimer_Tick;
        _readerTtsFloatingCollapseTimer.Tick +=
            ReaderTtsFloatingCollapseTimer_Tick;
        _readerTtsFloatingCollapseTimer.Start();
    }

    private void ReaderTtsFloatingCollapseTimer_Tick(object? sender, EventArgs e)
    {
        _readerTtsFloatingCollapseTimer?.Stop();
        if (!ReaderTtsPopup.IsOpen)
            CollapseReaderTtsFloatingPanel();
    }

    private void UpdateReaderTtsFloatingForPointer(
        double x,
        double y,
        double surfaceWidth,
        double surfaceHeight)
    {
        if (!IsReaderTtsFloatingActive()
            || surfaceWidth <= 0
            || surfaceHeight <= 0)
        {
            return;
        }

        if (ReaderTtsPopup.IsOpen)
        {
            _readerTtsFloatingCollapseTimer?.Stop();
            return;
        }

        var panelLeft = surfaceWidth
            - ReaderTtsFloatingPanelRightMargin
            - ReaderTtsFloatingPanelWidth;
        var panelTop = surfaceHeight
            - ReaderTtsFloatingPanelBottomMargin
            - ReaderTtsFloatingPanelHeight;
        var insidePanel = x >= panelLeft
            && x <= panelLeft + ReaderTtsFloatingPanelWidth
            && y >= panelTop
            && y <= panelTop + ReaderTtsFloatingPanelHeight;
        var insideEdge = x >= surfaceWidth - ReaderTtsFloatingEdgeActivationWidth
            && y >= panelTop - ReaderTtsFloatingEdgeActivationTopPadding
            && y <= surfaceHeight - ReaderTtsFloatingEdgeActivationBottomPadding;

        // The edge strip is only an activation target while the wheel is
        // collapsed. Once the wheel is open, leaving the wheel for that strip
        // must start the collapse timer instead of keeping it open forever.
        var keepExpanded = _readerTtsFloatingExpanded
            ? insidePanel
            : insideEdge;
        if (keepExpanded)
        {
            if (!_readerTtsFloatingExpanded)
                ExpandReaderTtsFloatingPanel();
            else
            {
                _readerTtsFloatingStartupShowing = false;
                _readerTtsFloatingAutoHideTimer?.Stop();
                _readerTtsFloatingCollapseTimer?.Stop();
            }
        }
        else
        {
            // Do not let the global pointer watcher cancel the startup grace
            // period: the start button is normally far from the wheel.
            if (!_readerTtsFloatingStartupShowing)
            {
                ScheduleReaderTtsFloatingCollapse(
                    ReaderTtsFloatingPointerExitCollapseDelayMs);
            }
        }
    }

    private void UpdateReaderTtsFloatingForScreenPointer(PixelPoint cursor)
    {
        if (!IsReaderTtsFloatingActive()
            || ReaderTtsFloatingPanel is null
            || ReaderTtsFloatingEdgeActivationRegion is null)
        {
            return;
        }

        if (ReaderTtsPopup.IsOpen)
        {
            _readerTtsFloatingCollapseTimer?.Stop();
            return;
        }

        // Use the controls' real screen rectangles. This includes the row
        // offset, display scaling and the current slide transform, so native
        // WebView mouse movement cannot leave the wheel permanently expanded.
        var insidePanel = _readerTtsFloatingExpanded
            && ContainsReaderScreenPoint(
                GetReaderScreenRect(ReaderTtsFloatingPanel),
                cursor);
        var insideEdge = !_readerTtsFloatingExpanded
            && ContainsReaderScreenPoint(
                GetReaderScreenRect(ReaderTtsFloatingEdgeActivationRegion),
                cursor);

        if (insidePanel)
        {
            _readerTtsFloatingStartupShowing = false;
            _readerTtsFloatingAutoHideTimer?.Stop();
            _readerTtsFloatingCollapseTimer?.Stop();
        }
        else if (insideEdge)
        {
            ExpandReaderTtsFloatingPanel();
        }
        else if (!_readerTtsFloatingStartupShowing)
        {
            ScheduleReaderTtsFloatingCollapse(
                ReaderTtsFloatingPointerExitCollapseDelayMs);
        }
    }

    private void ReaderTtsFloatingEdgeActivationRegion_PointerEntered(
        object? sender,
        PointerEventArgs e)
    {
        if (_readerTtsFloatingExpanded)
            ScheduleReaderTtsFloatingCollapse(
                ReaderTtsFloatingPointerExitCollapseDelayMs);
        else
            ExpandReaderTtsFloatingPanel();
    }

    private void ReaderTtsFloatingEdgeActivationRegion_PointerMoved(
        object? sender,
        PointerEventArgs e)
    {
        if (_readerTtsFloatingExpanded)
            ScheduleReaderTtsFloatingCollapse(
                ReaderTtsFloatingPointerExitCollapseDelayMs);
        else
            ExpandReaderTtsFloatingPanel();
    }

    private void ReaderTtsFloatingEdgeActivationRegion_PointerExited(
        object? sender,
        PointerEventArgs e)
        => ScheduleReaderTtsFloatingCollapse(
            ReaderTtsFloatingPointerExitCollapseDelayMs);

    private void ReaderTtsFloatingPanel_PointerEntered(
        object? sender,
        PointerEventArgs e)
        => ExpandReaderTtsFloatingPanel();

    private void ReaderTtsFloatingPanel_PointerMoved(
        object? sender,
        PointerEventArgs e)
        => ExpandReaderTtsFloatingPanel();

    private void ReaderTtsFloatingPanel_PointerExited(
        object? sender,
        PointerEventArgs e)
        => ScheduleReaderTtsFloatingCollapse(
            ReaderTtsFloatingPointerExitCollapseDelayMs);

    private void ReaderTtsPopup_Closed(object? sender, EventArgs e)
    {
        if (_readerTtsFloatingStartupShowing)
            StartReaderTtsFloatingAutoHideTimer();
        else
            ScheduleReaderTtsFloatingCollapse();
    }

    private void OpenReaderTtsPopup(
        Control placementTarget,
        PlacementMode placement)
    {
        if (_readerIsPdf || !_readerTts.CanOpen) return;
        ReaderTtsPopup.PlacementTarget = placementTarget;
        ReaderTtsPopup.Placement = placement;
        ReaderTtsPopup.IsOpen = true;
        UpdateReaderTtsUi();
        _ = RefreshReaderTtsVoicesAsync();
    }

    private async void ReaderTtsButton_Click(
        object? sender,
        RoutedEventArgs e)
    {
        try
        {
            if (IsReaderTtsActiveState(_readerTts.State)
                || _readerTtsFloatingRequested
                || _readerTtsResumeAfterNavigation)
            {
                // The header button is deliberately a start/stop toggle. The
                // floating wheel remains available so its MENU is the only
                // place where voice and playback settings are configured.
                CancelReaderTtsContinuation();
                await _readerTts.StopAsync();
                return;
            }

            await StartReaderTtsWithSavedSettingsAsync("启动听书失败：{0}");
        }
        catch (OperationCanceledException)
            when (ReaderToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _readerTtsNotice = T(
                "启动听书失败：{0}",
                UiText.Localize(exception.Message));
            UpdateReaderTtsUi();
        }
    }

    private void ReaderTtsFloatingMenuButton_Click(
        object? sender,
        RoutedEventArgs e)
        => OpenReaderTtsPopup(ReaderTtsFloatingMenuButton, PlacementMode.Top);

    private async void ReaderTtsRefreshVoicesButton_Click(
        object? sender,
        RoutedEventArgs e)
        => await RefreshReaderTtsVoicesAsync();

    private async void ReaderTtsPreviewButton_Click(
        object? sender,
        RoutedEventArgs e)
    {
        if (_readerTtsPreviewInProgress) return;

        try
        {
            CaptureReaderTtsSettingsFromControls();
            _readerTtsPreviewInProgress = true;
            _readerTtsNotice = T(
                "正在试听：{0}",
                TtsService.PreviewText);
            UpdateReaderTtsUi();

            await _readerTts.PreviewAsync(
                _readerTtsSettings,
                ReaderToken);
            _readerTtsNotice = T("试听完成。");
        }
        catch (OperationCanceledException)
            when (ReaderToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _readerTtsNotice = T(
                "声音试听失败：{0}",
                UiText.Localize(exception.Message));
        }
        finally
        {
            _readerTtsPreviewInProgress = false;
            UpdateReaderTtsUi();
        }
    }

    private async Task RefreshReaderTtsVoicesAsync()
    {
        if (_readerTtsVoicesLoading) return;
        _readerTtsVoicesLoading = true;
        var request = ++_readerTtsVoicesRequest;
        _readerTtsNotice = T("正在加载语音…");
        UpdateReaderTtsUi();
        try
        {
            var voices = await _readerTts.GetVoicesAsync(ReaderToken);
            if (request != _readerTtsVoicesRequest) return;
            // The reader is currently configured for Simplified Chinese.
            // Keep the engine result unfiltered so other providers can expose
            // their full catalog later, but only show zh-CN voices here.
            var zhCnVoices = voices
                .Where(IsZhCnVoice)
                .ToArray();
            var items = zhCnVoices
                .Select(voice => new ComboBoxItem
                {
                    Content = string.IsNullOrWhiteSpace(voice.Culture)
                        ? voice.Name
                        : $"{voice.Name} · {voice.Culture}",
                    Tag = voice.Id
                })
                .ToArray();
            ReaderTtsVoiceBox.ItemsSource = items;
            var selected = items.FirstOrDefault(item =>
                string.Equals(
                    item.Tag as string,
                    _readerTtsSettings.Voice,
                    StringComparison.OrdinalIgnoreCase));
            if (selected is null)
            {
                selected = items.FirstOrDefault(item =>
                    string.Equals(
                        item.Tag as string,
                        TtsOptions.DefaultVoice,
                        StringComparison.OrdinalIgnoreCase))
                    ?? items.FirstOrDefault();
                if (selected?.Tag is string fallbackVoice)
                    _readerTtsSettings.Voice = fallbackVoice;
            }
            ReaderTtsVoiceBox.SelectedItem = selected;
            ReaderTtsVoiceBox.Text = _readerTtsSettings.Voice;
            _readerTtsNotice = items.Length == 0
                ? _readerTts.Availability is { IsAvailable: false } unavailable
                    ? unavailable.Message
                    : T("未找到 edge-tts 语音。")
                : null;
            UpdateReaderTtsUi();
        }
        catch (OperationCanceledException)
            when (ReaderToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _readerTtsNotice = T(
                "加载语音失败：{0}",
                UiText.Localize(exception.Message));
            UpdateReaderTtsUi();
        }
        finally
        {
            _readerTtsVoicesLoading = false;
        }
    }

    private async void ReaderTtsApplyButton_Click(
        object? sender,
        RoutedEventArgs e)
    {
        try
        {
            CaptureReaderTtsSettingsFromControls();
            if (_readerTts.State is not TtsPlaybackState.Stopped
                and not TtsPlaybackState.Error)
            {
                CancelReaderTtsContinuation();
                await _readerTts.StopAsync();
            }

            await _ttsSettingsStore.SaveAsync(_readerTtsSettings, ReaderToken);
            _readerTtsNotice = T("设置已保存。");
            UpdateReaderTtsUi();
        }
        catch (OperationCanceledException)
            when (ReaderToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _readerTtsNotice = T(
                "保存听书设置失败：{0}",
                UiText.Localize(exception.Message));
            UpdateReaderTtsUi();
        }
    }

    private async void ReaderTtsPlayButton_Click(
        object? sender,
        RoutedEventArgs e)
    {
        try
        {
            if (_readerTts.State is TtsPlaybackState.Playing
                or TtsPlaybackState.Paused)
            {
                _readerTts.PauseOrResume();
                return;
            }

            CaptureReaderTtsSettingsFromControls();
            await _ttsSettingsStore.SaveAsync(_readerTtsSettings, ReaderToken);
            await StartReaderTtsWithSavedSettingsAsync("启动听书失败：{0}");
        }
        catch (OperationCanceledException)
            when (ReaderToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _readerTtsNotice = T(
                "启动听书失败：{0}",
                UiText.Localize(exception.Message));
            UpdateReaderTtsUi();
        }
    }

    private async void ReaderTtsStopButton_Click(
        object? sender,
        RoutedEventArgs e)
    {
        CancelReaderTtsContinuation();
        await _readerTts.StopAsync();
    }

    private void ReaderTtsFloatingToggleButton_Click(
        object? sender,
        RoutedEventArgs e)
    {
        if (_readerTts.State is TtsPlaybackState.Playing
            or TtsPlaybackState.Paused)
        {
            _readerTts.PauseOrResume();
        }
        else if (_readerTts.State is TtsPlaybackState.Stopped
            or TtsPlaybackState.Error)
        {
            ReaderTtsPlayButton_Click(sender, e);
        }
    }

    private async void ReaderTtsFloatingStopButton_Click(
        object? sender,
        RoutedEventArgs e)
    {
        CancelReaderTtsContinuation();
        await _readerTts.StopAsync();
    }

    private async Task<bool> StartReaderTtsWithSavedSettingsAsync(
        string failureFormat,
        bool isNavigationResume = false)
    {
        try
        {
            if (!isNavigationResume)
                CancelReaderTtsContinuation();
            _readerTtsNotice = null;
            _readerTtsFloatingRequested = true;
            ShowReaderTtsFloatingPanelTemporarily();
            await _readerTts.StartAsync(_readerTtsSettings, ReaderToken);
            UpdateReaderTtsUi();
            return true;
        }
        catch (OperationCanceledException)
            when (ReaderToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception exception)
        {
            _readerTtsNotice = T(
                failureFormat,
                UiText.Localize(exception.Message));
            UpdateReaderTtsUi();
            return false;
        }
    }

    private async Task ResumeReaderTtsAfterNavigationAsync(bool shouldResume)
    {
        if (!shouldResume
            || _readerIsPdf
            || !ReaderRoot.IsVisible
            || GetCurrentReaderTtsDocument() is null)
        {
            return;
        }

        await StartReaderTtsWithSavedSettingsAsync(
            "切换章节后启动听书失败：{0}",
            isNavigationResume: true);
    }

    private async void ReaderTtsPreviousSegmentButton_Click(
        object? sender,
        RoutedEventArgs e)
        => await _readerTts.SkipSegmentAsync(-1);

    private async void ReaderTtsNextSegmentButton_Click(
        object? sender,
        RoutedEventArgs e)
        => await _readerTts.SkipSegmentAsync(1);

    private async void ReaderTtsFloatingPreviousButton_Click(
        object? sender,
        RoutedEventArgs e)
        => await MoveReaderTtsChapterAsync(-1, positionAtStart: true);

    private async void ReaderTtsFloatingNextButton_Click(
        object? sender,
        RoutedEventArgs e)
        => await MoveReaderTtsChapterAsync(1);

    private async Task MoveReaderTtsChapterAsync(
        int direction,
        bool positionAtStart = false)
    {
        var state = _readerTts.State;
        var shouldContinue = _readerTtsResumeAfterNavigation
            || _readerTtsFloatingRequested
            || IsReaderTtsActiveState(state);
        var navigationRequestVersion = BeginReaderTtsNavigationRequest(
            shouldContinue);
        var previousChapter = _readerChapterIndex;

        try
        {
            await _readerTts.StopAsync();
            if (!IsCurrentReaderTtsNavigationRequest(navigationRequestVersion))
                return;

            await MoveReaderChapterAsync(
                direction,
                positionAtStart: positionAtStart);

            if (!CanResumeReaderTtsNavigation(
                    navigationRequestVersion,
                    shouldContinue)
                || previousChapter == _readerChapterIndex
                || !ReaderRoot.IsVisible
                || _readerIsPdf
                || GetCurrentReaderTtsDocument() is null)
            {
                return;
            }

            await ResumeReaderTtsAfterNavigationAsync(true);
        }
        finally
        {
            CompleteReaderTtsNavigationRequest(navigationRequestVersion);
        }
    }
}
