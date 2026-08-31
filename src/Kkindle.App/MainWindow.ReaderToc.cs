using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle;

/// <summary>
/// Port of the WinUI reference's minimal TOC rail (极简目录): a narrow marker
/// rail that keeps the chapter map visible without taking the reading column
/// away from the body. Markers wave toward the pointer, a floating label shows
/// the hovered chapter title, the wheel scrolls with a short eased animation
/// and the up/down indicators reflect the scroll extent.
/// </summary>
public sealed record ReaderTocMarker(EpubReaderNavigationItem Item, bool IsCurrent)
{
    private static readonly IBrush CurrentBrush = new SolidColorBrush(
        Color.FromArgb(255, 91, 98, 104));
    private static readonly IBrush InactiveBrush = new SolidColorBrush(
        Color.FromArgb(255, 211, 213, 209));
    public static readonly IBrush HoverBrush = new SolidColorBrush(
        Color.FromArgb(255, 96, 96, 96));

    public string Title => Item.Title;
    public IBrush Fill => GetFill(IsCurrent);
    public static IBrush GetFill(bool isCurrent) => isCurrent ? CurrentBrush : InactiveBrush;
}

public partial class MainWindow
{
    private const double ReaderTocMinimalWidth = 52d;
    private const double ReaderCompactMarkerMinimumWidth = 8d;
    // A right-only wave starts at the centered 8 px resting marker. Keep its
    // longest stroke inside the 52 px rail (including the right divider).
    private const double ReaderCompactMarkerMaximumWidth = 28d;
    private const double ReaderCompactMarkerWaveRadius = 96d;
    private const double ReaderCompactScrollAnimationDurationMs = 160d;
    private bool _readerTocMinimal;
    private bool _readerTocExpanded = true;
    private IReadOnlyList<EpubReaderNavigationItem> _readerCompactNavigationItems = [];
    private string? _readerCompactSelectedTarget;
    private DispatcherTimer? _readerCompactScrollTimer;
    private bool _readerCompactScrollAnimating;
    private double _readerCompactScrollStart;
    private double _readerCompactScrollTarget;
    private DateTimeOffset _readerCompactScrollStartedAt;
    private bool _readerCompactPointerActive;
    private double _readerCompactPointerY;
    private Control? _readerChapterPreviewTarget;
    private bool _readerChapterPreviewAbove;
    private readonly Dictionary<string, string> _readerChapterPreviewTextCache = new(StringComparer.OrdinalIgnoreCase);
    private int _readerChapterPreviewRequestVersion;

    // 目录列表的滚动条自动隐藏：滚动时淡入，静止约一秒后淡出，悬停条上
    // 期间保持可见。滚动条属于 ListBox 的模板，需要在模板完成后把状态类
    // 加到内部 ScrollViewer 上；直接设置 ScrollBar 的 Opacity 会在模板重建
    // 或主题刷新时失效。
    private const int ReaderTocScrollBarIdleHideMs = 900;
    private const string ReaderTocScrollClass = "readerTocScroll";
    private const string ReaderTocScrollingClass = "readerTocScrolling";
    private ScrollViewer? _readerTocScrollViewer;
    private ScrollBar? _readerTocScrollBar;
    private DispatcherTimer? _readerTocScrollBarHideTimer;
    private bool _readerTocScrollBarAttachPending;

    private void ReaderTocList_AttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        QueueReaderTocScrollBarAttach();
    }

    private void ReaderTocList_TemplateApplied(object? sender, TemplateAppliedEventArgs e)
    {
        QueueReaderTocScrollBarAttach();
    }

    private void QueueReaderTocScrollBarAttach()
    {
        if (_readerTocScrollBarAttachPending) return;
        _readerTocScrollBarAttachPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _readerTocScrollBarAttachPending = false;
            AttachReaderTocScrollBar();
        }, DispatcherPriority.Loaded);
    }

    private void AttachReaderTocScrollBar()
    {
        var scrollViewer = FindDescendants<ScrollViewer>(ReaderTocList).FirstOrDefault();
        var scrollBar = FindDescendants<ScrollBar>(ReaderTocList)
            .FirstOrDefault(candidate => candidate.Orientation == Orientation.Vertical);
        if (scrollViewer is null || scrollBar is null) return;

        if (ReferenceEquals(_readerTocScrollViewer, scrollViewer)
            && ReferenceEquals(_readerTocScrollBar, scrollBar))
        {
            return;
        }

        DetachReaderTocScrollBar();
        _readerTocScrollViewer = scrollViewer;
        _readerTocScrollBar = scrollBar;
        scrollViewer.Classes.Add(ReaderTocScrollClass);
        scrollViewer.Classes.Remove(ReaderTocScrollingClass);
        scrollViewer.ScrollChanged += ReaderTocScrollViewer_ScrollChangedForBar;
        scrollBar.PointerEntered += ReaderTocScrollBar_PointerEntered;
        scrollBar.PointerExited += ReaderTocScrollBar_PointerExited;
    }

    private void DetachReaderTocScrollBar()
    {
        if (_readerTocScrollViewer is { } scrollViewer)
        {
            scrollViewer.ScrollChanged -= ReaderTocScrollViewer_ScrollChangedForBar;
            scrollViewer.Classes.Remove(ReaderTocScrollClass);
            scrollViewer.Classes.Remove(ReaderTocScrollingClass);
        }

        if (_readerTocScrollBar is { } scrollBar)
        {
            scrollBar.PointerEntered -= ReaderTocScrollBar_PointerEntered;
            scrollBar.PointerExited -= ReaderTocScrollBar_PointerExited;
        }

        _readerTocScrollViewer = null;
        _readerTocScrollBar = null;
        _readerTocScrollBarHideTimer?.Stop();
    }

    private void ReaderTocScrollViewer_ScrollChangedForBar(object? sender, ScrollChangedEventArgs e)
    {
        if (e.OffsetDelta.Y != 0) ShowReaderTocScrollBar();
    }

    private void ReaderTocScrollBar_PointerEntered(object? sender, PointerEventArgs e)
    {
        ShowReaderTocScrollBar();
    }

    private void ReaderTocScrollBar_PointerExited(object? sender, PointerEventArgs e)
    {
        ScheduleReaderTocScrollBarHide();
    }

    private void ShowReaderTocScrollBar()
    {
        if (_readerTocScrollViewer is not { } scrollViewer) return;
        scrollViewer.Classes.Add(ReaderTocScrollingClass);
        EnsureReaderTocScrollBarHideTimer().Stop();
        EnsureReaderTocScrollBarHideTimer().Start();
    }

    private void ScheduleReaderTocScrollBarHide()
    {
        if (_readerTocScrollBar is not { IsPointerOver: false }
            || _readerTocScrollViewer is null)
        {
            return;
        }

        EnsureReaderTocScrollBarHideTimer().Stop();
        EnsureReaderTocScrollBarHideTimer().Start();
    }

    private DispatcherTimer EnsureReaderTocScrollBarHideTimer()
    {
        if (_readerTocScrollBarHideTimer is not null) return _readerTocScrollBarHideTimer;
        _readerTocScrollBarHideTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(ReaderTocScrollBarIdleHideMs)
        };
        _readerTocScrollBarHideTimer.Tick += (_, _) =>
        {
            _readerTocScrollBarHideTimer!.Stop();
            if (_readerTocScrollViewer is not { } scrollViewer) return;
            if (_readerTocScrollBar is { IsPointerOver: true })
                scrollViewer.Classes.Add(ReaderTocScrollingClass);
            else
                scrollViewer.Classes.Remove(ReaderTocScrollingClass);
        };
        return _readerTocScrollBarHideTimer;
    }

    private void ReaderTocMinimalToggleButton_Click(object? sender, RoutedEventArgs e)
    {
        SetReaderTocMinimal(!_readerTocMinimal);
    }

    private void ReaderTocCompactExpandButton_Click(object? sender, RoutedEventArgs e)
    {
        SetReaderTocMinimal(false);
    }

    private bool _readerCompactPointerPressedHandlerRegistered;

    private void ReaderTocCompactPanel_Loaded(object? sender, RoutedEventArgs e)
    {
        if (_readerCompactPointerPressedHandlerRegistered) return;
        // The ScrollViewer can mark the pointer press as handled while starting
        // its own scroll gesture, so subscribe with handledEventsToo like the
        // WinUI reference's AddHandler call.
        ReaderTocCompactScrollViewer.AddHandler(
            InputElement.PointerPressedEvent,
            ReaderTocCompactScrollViewer_PointerPressed,
            RoutingStrategies.Bubble,
            handledEventsToo: true);
        _readerCompactPointerPressedHandlerRegistered = true;
        UpdateReaderCompactMarkerWave();
    }

    private void ReaderTocCompactScrollViewer_ScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        UpdateReaderCompactScrollIndicators();
        UpdateReaderCompactMarkerWave();
    }

    private void ReaderTocCompactScrollViewer_PointerMoved(object? sender, PointerEventArgs e)
    {
        _readerCompactPointerActive = true;
        _readerCompactPointerY = Math.Clamp(
            e.GetPosition(ReaderTocCompactScrollViewer).Y,
            0,
            ReaderTocCompactScrollViewer.Bounds.Height);
        UpdateReaderCompactMarkerWave();
    }

    private void ReaderTocCompactScrollViewer_PointerExited(object? sender, PointerEventArgs e)
    {
        _readerCompactPointerActive = false;
        UpdateReaderCompactMarkerWave();
    }

    private void ReaderTocCompactScrollViewer_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(ReaderTocCompactScrollViewer).Properties.IsLeftButtonPressed) return;
        var pointerY = e.GetPosition(ReaderTocCompactScrollViewer).Y;
        ReaderTocMarker? closestMarker = null;
        var closestDistance = double.MaxValue;
        foreach (var button in FindDescendants<Button>(ReaderTocCompactList))
        {
            if (button.DataContext is not ReaderTocMarker marker || button.Bounds.Height <= 0) continue;
            try
            {
                var markerCenter = button
                    .TranslatePoint(new Point(0, button.Bounds.Height / 2), ReaderTocCompactScrollViewer)?
                    .Y ?? 0;
                var distance = Math.Abs(markerCenter - pointerY);
                if (distance >= closestDistance) continue;
                closestDistance = distance;
                closestMarker = marker;
            }
            catch (InvalidOperationException)
            {
                // The item may be between visual trees during a layout pass.
            }
        }

        if (closestMarker is null) return;
        NavigateToReaderTocItem(closestMarker.Item);
        e.Handled = true;
    }

    private void ReaderTocCompactScrollViewer_PointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        var delta = e.Delta.Y;
        if (Math.Abs(delta) < 0.01) return;
        var scrollViewer = ReaderTocCompactScrollViewer;
        var maximum = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
        var baseOffset = _readerCompactScrollAnimating
            ? _readerCompactScrollTarget
            : scrollViewer.Offset.Y;
        _readerCompactScrollStart = scrollViewer.Offset.Y;
        _readerCompactScrollTarget = Math.Clamp(
            baseOffset - delta * 120 * 0.45,
            0,
            maximum);
        _readerCompactScrollStartedAt = DateTimeOffset.UtcNow;
        _readerCompactScrollAnimating = true;
        EnsureReaderCompactScrollTimer();
        _readerCompactScrollTimer!.Start();
        e.Handled = true;
    }

    private void EnsureReaderCompactScrollTimer()
    {
        if (_readerCompactScrollTimer is not null) return;
        _readerCompactScrollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _readerCompactScrollTimer.Tick += ReaderCompactScrollTimer_Tick;
    }

    private void ReaderCompactScrollTimer_Tick(object? sender, EventArgs e)
    {
        if (!_readerCompactScrollAnimating || !ReaderTocCompactPanel.IsVisible)
        {
            StopReaderCompactScrollAnimation();
            return;
        }

        var progress = Math.Clamp(
            (DateTimeOffset.UtcNow - _readerCompactScrollStartedAt).TotalMilliseconds
                / ReaderCompactScrollAnimationDurationMs,
            0,
            1);
        var eased = 1 - Math.Pow(1 - progress, 3);
        var offset = _readerCompactScrollStart
            + (_readerCompactScrollTarget - _readerCompactScrollStart) * eased;
        ReaderTocCompactScrollViewer.Offset = new Vector(0, offset);
        UpdateReaderCompactScrollIndicators();
        UpdateReaderCompactMarkerWave();

        if (progress >= 1) StopReaderCompactScrollAnimation();
    }

    private void StopReaderCompactScrollAnimation()
    {
        _readerCompactScrollAnimating = false;
        _readerCompactScrollTimer?.Stop();
    }

    private void UpdateReaderCompactScrollIndicators()
    {
        if (ReaderTocCompactUpIndicator is null || ReaderTocCompactDownIndicator is null) return;

        const double edgeTolerance = 0.5d;
        var offset = ReaderTocCompactScrollViewer.Offset.Y;
        var maximum = Math.Max(0, ReaderTocCompactScrollViewer.Extent.Height - ReaderTocCompactScrollViewer.Viewport.Height);
        ReaderTocCompactUpIndicator.Text = offset <= edgeTolerance ? "\u25B3" : "\u25B2";
        ReaderTocCompactDownIndicator.Text = offset >= maximum - edgeTolerance ? "\u25BD" : "\u25BC";
    }

    private void UpdateReaderCompactMarkerWave()
    {
        if (ReaderTocCompactScrollViewer is null
            || ReaderTocCompactList is null
            || ReaderTocCompactScrollViewer.Bounds.Height <= 0)
        {
            return;
        }

        Border? hoveredMarker = null;
        EpubReaderNavigationItem? hoveredItem = null;
        var hoveredDistance = double.MaxValue;
        foreach (var button in FindDescendants<Button>(ReaderTocCompactList))
        {
            if (button.Content is not Border marker || button.Bounds.Height <= 0) continue;
            var markerData = button.DataContext as ReaderTocMarker;
            if (markerData is not null)
            {
                var isCurrent = _readerCompactSelectedTarget is not null
                    && markerData.Item.Target.Equals(
                        _readerCompactSelectedTarget,
                        StringComparison.OrdinalIgnoreCase);
                marker.Background = ReaderTocMarker.GetFill(isCurrent);
            }

            try
            {
                if (!_readerCompactPointerActive)
                {
                    SetReaderCompactMarkerWidth(marker, ReaderCompactMarkerMinimumWidth);
                    continue;
                }

                var markerCenter = button
                    .TranslatePoint(new Point(0, button.Bounds.Height / 2), ReaderTocCompactScrollViewer)?
                    .Y ?? 0;
                var normalizedDistance = Math.Clamp(
                    Math.Abs(markerCenter - _readerCompactPointerY) / ReaderCompactMarkerWaveRadius,
                    0,
                    1);
                var distance = Math.Abs(markerCenter - _readerCompactPointerY);
                var wave = Math.Sin((1 - normalizedDistance) * Math.PI / 2);
                SetReaderCompactMarkerWidth(
                    marker,
                    ReaderCompactMarkerMinimumWidth
                        + (ReaderCompactMarkerMaximumWidth - ReaderCompactMarkerMinimumWidth) * wave);
                if (distance < hoveredDistance)
                {
                    hoveredDistance = distance;
                    hoveredMarker = marker;
                    hoveredItem = markerData?.Item;
                }
            }
            catch (InvalidOperationException)
            {
                // The item may be between visual trees during a layout pass.
            }
        }

        if (hoveredMarker is not null)
            hoveredMarker.Background = ReaderTocMarker.HoverBrush;
        if (!_readerSliderPreviewVisible)
        {
            if (_readerCompactPointerActive)
                UpdateReaderCompactHoverLabel(hoveredMarker, hoveredItem);
            else
                HideReaderCompactHoverLabel();
        }
    }

    private void UpdateReaderCompactHoverLabel(
        Control? target,
        EpubReaderNavigationItem? item)
    {
        if (target is null || item is null)
        {
            HideReaderCompactHoverLabel();
            return;
        }

        try
        {
            var index = GetReaderNavigationItemIndex(item);
            ShowReaderChapterPreview(target, index, placeAbove: false, includeBodyPreview: true);
        }
        catch (InvalidOperationException)
        {
            HideReaderCompactHoverLabel();
        }
    }

    private void HideReaderCompactHoverLabel()
    {
        _readerChapterPreviewRequestVersion++;
        if (ReaderChapterPreviewPopup is not null)
            ReaderChapterPreviewPopup.IsOpen = false;
        if (ReaderChapterPreviewBodyText is not null)
        {
            ReaderChapterPreviewBodyText.Text = string.Empty;
            ReaderChapterPreviewBodyText.IsVisible = false;
        }
        _readerChapterPreviewTarget = null;
    }

    private int GetReaderNavigationItemIndex(EpubReaderNavigationItem item)
    {
        for (var index = 0; index < _readerTocItems.Count; index++)
        {
            if (ReferenceEquals(_readerTocItems[index], item)
                || _readerTocItems[index].Target.Equals(item.Target, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return Math.Clamp(item.ChapterIndex, 0, Math.Max(0, _readerTocItems.Count - 1));
    }

    private void ShowReaderChapterPreview(
        Control target,
        int index,
        bool placeAbove,
        bool includeBodyPreview = false)
    {
        if (ReaderChapterPreviewPopup is null || target.Bounds.Width <= 0 || target.Bounds.Height <= 0)
            return;

        var requestVersion = ++_readerChapterPreviewRequestVersion;
        ReaderChapterPreviewBodyText.Text = string.Empty;
        ReaderChapterPreviewBodyText.IsVisible = false;
        if (_readerIsPdf)
        {
            var total = Math.Max(1, _readerPdfPages.Count);
            var page = Math.Clamp(index + 1, 1, total);
            ReaderChapterPreviewTitleText.Text = T("第 {0} 页", page);
        }
        else
        {
            if (_readerTocItems.Count == 0) return;
            index = Math.Clamp(index, 0, _readerTocItems.Count - 1);
            ReaderChapterPreviewTitleText.Text = _readerTocItems[index].Title;
            if (includeBodyPreview)
            {
                ReaderChapterPreviewBodyText.Text = T("正在读取正文…");
                ReaderChapterPreviewBodyText.IsVisible = true;
            }
        }

        var targetChanged = !ReferenceEquals(target, _readerChapterPreviewTarget)
            || placeAbove != _readerChapterPreviewAbove;
        if (targetChanged && ReaderChapterPreviewPopup.IsOpen)
            ReaderChapterPreviewPopup.IsOpen = false;

        _readerChapterPreviewTarget = target;
        _readerChapterPreviewAbove = placeAbove;
        ReaderChapterPreviewPopup.PlacementTarget = target;
        ReaderChapterPreviewPopup.Placement = placeAbove
            ? PlacementMode.Top
            : PlacementMode.Right;
        ReaderChapterPreviewPopup.HorizontalOffset = placeAbove ? 0 : 6;
        ReaderChapterPreviewPopup.VerticalOffset = placeAbove ? -8 : 0;
        ReaderChapterPreviewPopup.IsOpen = true;

        if (includeBodyPreview && !_readerIsPdf)
        {
            _ = LoadReaderChapterPreviewBodyAsync(
                target,
                _readerTocItems[index],
                requestVersion);
        }
    }

    private async Task LoadReaderChapterPreviewBodyAsync(
        Control target,
        EpubReaderNavigationItem item,
        int requestVersion)
    {
        if (_readerDocument is null
            || item.ChapterIndex < 0
            || item.ChapterIndex >= _readerDocument.Chapters.Count
            || !Uri.TryCreate(item.Target, UriKind.Absolute, out var targetUri)
            || !targetUri.IsFile
            || !IsPathInside(_readerDocument.RootPath, targetUri.LocalPath))
        {
            return;
        }

        var chapterPath = Path.GetFullPath(targetUri.LocalPath);
        var fragment = GetReaderTargetFragment(targetUri);
        try
        {
            var cacheKey = $"{chapterPath}\0{fragment}\0{item.Title}";
            if (!_readerChapterPreviewTextCache.TryGetValue(cacheKey, out var preview))
            {
                var plainText = await Task.Run(() => ExtractReaderPlainText(chapterPath, fragment, item.Title));
                preview = BuildReaderChapterPreviewText(plainText, item.Title);
                _readerChapterPreviewTextCache[cacheKey] = preview;
            }

            if (requestVersion != _readerChapterPreviewRequestVersion
                || !ReferenceEquals(target, _readerChapterPreviewTarget)
                || ReaderChapterPreviewPopup is null
                || !ReaderChapterPreviewPopup.IsOpen)
            {
                return;
            }

            ReaderChapterPreviewBodyText.Text = preview;
            ReaderChapterPreviewBodyText.IsVisible = !string.IsNullOrWhiteSpace(preview);
        }
        catch (Exception)
        {
            // A hover preview is optional; keep the title visible when the
            // chapter cannot be decoded or is removed during cleanup.
        }
    }

    private static string BuildReaderChapterPreviewText(string plainText, string title)
    {
        plainText = EnsureReaderPlainTextStartsWithTitle(plainText, title);
        return string.Join(
            Environment.NewLine,
            plainText
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .Take(4));
    }

    private static void SetReaderCompactMarkerWidth(Border marker, double width)
    {
        marker.Width = width;
        if (marker.RenderTransform is not TranslateTransform translation)
        {
            translation = new TranslateTransform();
            marker.RenderTransform = translation;
        }

        // The resting marker stays centered. Render translation does not take
        // part in layout, so shifting by half of the added width precisely
        // cancels the centered marker's leftward growth. Its left edge remains
        // fixed and only the right half-wave extends into the reading area.
        translation.X = (width - ReaderCompactMarkerMinimumWidth) / 2;
    }

    private static IEnumerable<T> FindDescendants<T>(Visual root) where T : Visual
    {
        foreach (var child in root.GetVisualChildren())
        {
            if (child is T match) yield return match;

            foreach (var descendant in FindDescendants<T>(child))
                yield return descendant;
        }
    }

    private void SetReaderTocMinimal(bool minimal)
    {
        _readerTocMinimal = minimal;
        _readerTocExpanded = !minimal;
        _readerCompactPointerActive = false;
        HideReaderCompactHoverLabel();
        ApplyReaderPanelLayout();
        UpdateReaderZenTocToggle();
        QueueReaderCompactScrollIndicatorUpdate();
    }

    private void SetReaderCompactNavigationItems(IReadOnlyList<EpubReaderNavigationItem> items)
    {
        StopReaderCompactScrollAnimation();
        _readerChapterPreviewTextCache.Clear();
        _readerCompactNavigationItems = items;
        RefreshReaderCompactMarkers();
        QueueReaderCompactScrollIndicatorUpdate();
    }

    private void ClearReaderCompactNavigationItems()
    {
        StopReaderCompactScrollAnimation();
        _readerCompactNavigationItems = [];
        _readerCompactSelectedTarget = null;
        HideReaderCompactHoverLabel();
        ReaderTocCompactList.ItemsSource = null;
        QueueReaderCompactScrollIndicatorUpdate();
    }

    private void SetReaderCompactSelectedItem(EpubReaderNavigationItem? item)
    {
        _readerCompactSelectedTarget = item?.Target;
        RefreshReaderCompactMarkers();
        QueueReaderCompactScrollIndicatorUpdate();
    }

    private void RefreshReaderCompactMarkers()
    {
        HideReaderCompactHoverLabel();
        ReaderTocCompactList.ItemsSource = _readerCompactNavigationItems
            .Select(item => new ReaderTocMarker(
                item,
                _readerCompactSelectedTarget is not null
                    && item.Target.Equals(
                        _readerCompactSelectedTarget,
                        StringComparison.OrdinalIgnoreCase)))
            .ToArray();
    }

    private void QueueReaderCompactScrollIndicatorUpdate()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (ReaderTocCompactPanel.IsVisible)
            {
                UpdateReaderCompactScrollIndicators();
                UpdateReaderCompactMarkerWave();
            }
        }, DispatcherPriority.Background);
    }

    private void NavigateToReaderTocItem(EpubReaderNavigationItem item)
    {
        // A TOC click is an explicit user target: it must start at the target
        // chapter's first line (or its own anchor), never inherit a leftover
        // "move to chapter end" intent from a superseded previous-chapter turn.
        SetReaderCompactSelectedItem(item);
        // Selecting the item in the full TOC list triggers the selection
        // handler for real user clicks. This is a programmatic selection,
        // therefore use the guarded sync helper and start exactly one jump.
        SetReaderTocSelection(item);
        _ = ObserveReaderTaskAsync(
            NavigateToReaderItemAsync(
                item,
                _readerSessionCancellation?.Token ?? CancellationToken.None,
                ReaderNavigationIntent.Toc));
    }

    private void ApplyReaderPanelLayout()
    {
        if (!ReaderRoot.IsVisible) return;

        // Mirror the WinUI reference's TOC column sizing: the first column is
        // 286 when expanded, 52 when the minimal rail is shown, and 0 when the
        // TOC is hidden entirely.
        ReaderRoot.ColumnDefinitions[0].Width = new GridLength(
            _readerTocExpanded ? 286d : _readerTocMinimal ? ReaderTocMinimalWidth : 0d);
        ReaderTocPanel.IsVisible = _readerTocExpanded;
        ReaderTocCompactPanel.IsVisible = _readerTocMinimal;
        ReaderTocToggleButton.Opacity = _readerTocExpanded ? 0.58 : 1;
        ScheduleLinuxReaderTextFallbackReflow();
        ScheduleReaderRelayout();
    }

    private void UpdateReaderZenTocToggle()
    {
        var label = _readerTocMinimal ? T("关闭极简目录") : T("显示极简目录");
        if (ReaderZenTocButton is not null)
            ReaderZenTocButton.Content = label;
        if (ReaderZenTitleTocText is not null)
            ReaderZenTitleTocText.Text = label;
        if (ReaderZenPopupTocText is not null)
            ReaderZenPopupTocText.Text = label;
    }
}
