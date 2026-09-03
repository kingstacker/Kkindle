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
    private const double ReaderTtsFloatingCollapsedOffset = 134;
    private const double ReaderTtsFloatingEdgeActivationWidth = 36;
    private const double ReaderTtsFloatingEdgeActivationTopPadding = 22;
    private const double ReaderTtsFloatingEdgeActivationBottomPadding = 10;
    private const int ReaderTtsFloatingCollapseDelayMs = 700;

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
    private bool _readerTtsEnvironmentChecked;
    private bool _readerTtsPreviewInProgress;
    private long _readerTtsHighlightVersion;
    private string? _readerTtsNotice;
    private Task? _readerTtsEnvironmentTask;
    private DispatcherTimer? _readerTtsFloatingCollapseTimer;

    private static bool IsZhCnVoice(TtsVoiceInfo voice)
    {
        var culture = voice.Culture?.Replace('_', '-');
        return string.Equals(culture, "zh-CN", StringComparison.OrdinalIgnoreCase)
            || voice.Id.StartsWith("zh-CN-", StringComparison.OrdinalIgnoreCase);
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
        if (e.State is TtsPlaybackState.Generating
            or TtsPlaybackState.Playing
            or TtsPlaybackState.AdvancingChapter)
        {
            // Every new/resumed playback starts unobtrusively. The edge
            // handle remains available when the controls are needed.
            _readerTtsFloatingExpanded = false;
            _readerTtsFloatingCollapseTimer?.Stop();
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

        var documentAvailable = !_readerIsPdf
            && GetCurrentReaderTtsDocument() is not null;
        ReaderTtsButton.Content = T("听书");
        ReaderTtsButton.IsEnabled = _readerTts.CanOpen && documentAvailable;
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

        var state = _readerTts.State;
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
            _readerTtsFloatingExpanded = false;
            _readerTtsFloatingCollapseTimer?.Stop();
        }

        ReaderTtsFloatingPanel.IsVisible = active;
        ReaderTtsFloatingEdgeActivationRegion.IsVisible = active;
        ReaderTtsFloatingEdgeActivationRegion.IsHitTestVisible = active;
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
        if (_readerTtsFloatingExpanded) return;

        _readerTtsFloatingExpanded = true;
        UpdateReaderTtsFloatingUi(_readerTts.State);
    }

    private void CollapseReaderTtsFloatingPanel()
    {
        _readerTtsFloatingCollapseTimer?.Stop();
        if (!_readerTtsFloatingExpanded) return;

        _readerTtsFloatingExpanded = false;
        UpdateReaderTtsFloatingUi(_readerTts.State);
    }

    private void ScheduleReaderTtsFloatingCollapse()
    {
        if (!IsReaderTtsFloatingActive()
            || !_readerTtsFloatingExpanded
            || ReaderTtsPopup.IsOpen)
        {
            return;
        }

        _readerTtsFloatingCollapseTimer ??= new DispatcherTimer();
        _readerTtsFloatingCollapseTimer.Stop();
        _readerTtsFloatingCollapseTimer.Interval =
            TimeSpan.FromMilliseconds(ReaderTtsFloatingCollapseDelayMs);
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

        if (insidePanel || insideEdge)
        {
            if (!_readerTtsFloatingExpanded)
                ExpandReaderTtsFloatingPanel();
            else
                _readerTtsFloatingCollapseTimer?.Stop();
        }
        else
        {
            ScheduleReaderTtsFloatingCollapse();
        }
    }

    private void ReaderTtsFloatingEdgeActivationRegion_PointerEntered(
        object? sender,
        PointerEventArgs e)
        => ExpandReaderTtsFloatingPanel();

    private void ReaderTtsFloatingEdgeActivationRegion_PointerMoved(
        object? sender,
        PointerEventArgs e)
        => ExpandReaderTtsFloatingPanel();

    private void ReaderTtsFloatingEdgeActivationRegion_PointerExited(
        object? sender,
        PointerEventArgs e)
        => ScheduleReaderTtsFloatingCollapse();

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
        => ScheduleReaderTtsFloatingCollapse();

    private void ReaderTtsPopup_Closed(object? sender, EventArgs e)
        => ScheduleReaderTtsFloatingCollapse();

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

    private void ReaderTtsButton_Click(object? sender, RoutedEventArgs e)
        => OpenReaderTtsPopup(ReaderTtsButton, PlacementMode.Bottom);

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
            _readerTtsNotice = null;

            await _readerTts.StartAsync(_readerTtsSettings, ReaderToken);
            _readerTtsFloatingRequested = true;
            UpdateReaderTtsUi();
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
        _readerTtsFloatingRequested = false;
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
        _readerTtsFloatingRequested = false;
        await _readerTts.StopAsync();
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
        var shouldContinue = _readerTtsFloatingRequested
            && state is not TtsPlaybackState.Paused
            and not TtsPlaybackState.Error;
        var previousChapter = _readerChapterIndex;

        await _readerTts.StopAsync();
        await MoveReaderChapterAsync(
            direction,
            positionAtStart: positionAtStart);

        if (!shouldContinue
            || previousChapter == _readerChapterIndex
            || !ReaderRoot.IsVisible
            || _readerIsPdf
            || GetCurrentReaderTtsDocument() is null)
        {
            return;
        }

        try
        {
            _readerTtsNotice = null;
            await _readerTts.StartAsync(_readerTtsSettings, ReaderToken);
            UpdateReaderTtsUi();
        }
        catch (Exception exception)
        {
            _readerTtsNotice = T(
                "切换章节后启动听书失败：{0}",
                UiText.Localize(exception.Message));
            UpdateReaderTtsUi();
        }
    }
}
