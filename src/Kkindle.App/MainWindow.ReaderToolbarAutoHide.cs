using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Kkindle;

public partial class MainWindow
{
    private const int ReaderToolbarHideDelayMs = 1400;
    private const double ReaderToolbarRevealHeight = 18;
    private bool _readerToolbarAutoHideEnabled;
    private bool _readerToolbarsVisible = true;
    private bool _readerToolbarPointerAtEdge;
    private bool _readerToolbarPointerPressed;
    private bool _readerToolbarKeyboardFocus;
    private DispatcherTimer? _readerToolbarHideTimer;
    private DispatcherTimer? _readerToolbarLayoutTimer;

    private void InitializeReaderToolbarAutoHide()
    {
        // Observe handled events too: text selection and the progress thumb
        // capture the pointer before a normal bubbled handler can see it.
        AddHandler(PointerMovedEvent, ReaderToolbars_PointerMoved,
            RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerPressedEvent, ReaderToolbars_PointerPressed,
            RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, ReaderToolbars_PointerReleased,
            RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerCaptureLostEvent, (_, _) =>
        {
            _readerToolbarPointerPressed = false;
            ScheduleReaderToolbarHide();
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        Deactivated += (_, _) => _readerToolbarPointerPressed = false;
        PointerExited += (_, _) =>
        {
            _readerToolbarPointerAtEdge = false;
            ScheduleReaderToolbarHide();
        };
        foreach (var bar in new[] { ReaderHeaderBar, ReaderFooterBar })
        {
            bar.GotFocus += ReaderToolbars_GotFocus;
            bar.LostFocus += (_, _) => ScheduleReaderToolbarHide();
            bar.PointerExited += (_, _) =>
            {
                _readerToolbarPointerAtEdge = false;
                ScheduleReaderToolbarHide();
            };
        }
        foreach (var flyout in new[] { ReaderFlowButton.Flyout, ReaderMoreButton.Flyout })
        {
            if (flyout is null) continue;
            flyout.Opened += ReaderToolbarPopup_Changed;
            flyout.Closed += ReaderToolbarPopup_Changed;
        }
        foreach (var popup in new[]
        {
            ReaderLayoutSettingsPopup, ReaderTtsPopup, ReaderChapterPreviewPopup, ReaderAnnotationInputPopup
        })
        {
            popup.Opened += ReaderToolbarPopup_Changed;
            popup.Closed += ReaderToolbarPopup_Changed;
        }
        ReaderRoot.PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty) UpdateReaderToolbarAutoHide();
        };
    }

    private void UpdateReaderToolbarAutoHide()
    {
        var enabled = ReaderRoot.IsVisible && !_readerZenMode
            && !_readerTocExpanded
            && Volatile.Read(ref _readerCloseInProgress) == 0;
        var changed = enabled != _readerToolbarAutoHideEnabled;
        _readerToolbarAutoHideEnabled = enabled;

        if (!enabled)
        {
            _readerToolbarHideTimer?.Stop();
            _readerToolbarPointerAtEdge = false;
            _readerToolbarPointerPressed = false;
            _readerToolbarKeyboardFocus = false;
            SetReaderToolbarsVisible(true);
            return;
        }
        if (changed)
        {
            _readerToolbarPointerAtEdge = ReaderHeaderBar.IsPointerOver || ReaderFooterBar.IsPointerOver;
            SetReaderToolbarsVisible(true);
        }
        ScheduleReaderToolbarHide();
    }

    private void SetReaderToolbarsVisible(bool visible)
    {
        if (_readerToolbarsVisible == visible) return;
        _readerToolbarLayoutTimer?.Stop();
        _readerToolbarsVisible = visible;
        // Restore the controls' space before they appear. Reclaim it only
        // after their fade-out, so neither transition covers live text.
        if (visible) SetReaderToolbarReadingAreaExpanded(false);
        foreach (var bar in new[] { ReaderHeaderBar, ReaderFooterBar })
        {
            bar.Opacity = visible ? 1 : 0;
            bar.IsHitTestVisible = visible;
        }
        if (!visible)
        {
            _readerToolbarLayoutTimer ??= new DispatcherTimer(
                TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) =>
                {
                    _readerToolbarLayoutTimer!.Stop();
                    if (!_readerToolbarAutoHideEnabled || _readerToolbarsVisible) return;
                    if (ReaderToolbarInteractionActive())
                    {
                        SetReaderToolbarsVisible(true);
                        ScheduleReaderToolbarHide();
                        return;
                    }
                    SetReaderToolbarReadingAreaExpanded(true);
                });
            _readerToolbarLayoutTimer.Start();
        }
    }

    private void SetReaderToolbarReadingAreaExpanded(bool expanded)
    {
        if (ReaderRoot.IsVisible && Volatile.Read(ref _readerCloseInProgress) != 0) return;
        var row = expanded ? 0 : 1;
        var span = expanded ? 3 : 1;
        if (Grid.GetRow(ReaderReadingArea) == row && Grid.GetRowSpan(ReaderReadingArea) == span) return;
        (_readerActiveHost as NativeReaderHost)?.PreservePositionForViewportChange();
        (_readerPreloadHost as NativeReaderHost)?.PreservePositionForViewportChange();
        Grid.SetRow(ReaderReadingArea, row);
        Grid.SetRowSpan(ReaderReadingArea, span);
    }

    private void ScheduleReaderToolbarHide()
    {
        if (!_readerToolbarAutoHideEnabled || !_readerToolbarsVisible) return;
        _readerToolbarHideTimer ??= CreateReaderToolbarHideTimer();
        if (!_readerToolbarHideTimer.IsEnabled)
            _readerToolbarHideTimer.Start();
    }

    private DispatcherTimer CreateReaderToolbarHideTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ReaderToolbarHideDelayMs) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!_readerToolbarAutoHideEnabled) return;
            if (_readerToolbarPointerAtEdge || ReaderToolbarInteractionActive())
                ScheduleReaderToolbarHide();
            else
                SetReaderToolbarsVisible(false);
        };
        return timer;
    }

    private bool ReaderToolbarInteractionActive() => _readerToolbarPointerPressed || _readerSliderDragging
        || _readerPageTurnGate.CurrentCount == 0
        || ReaderFlowButton.Flyout?.IsOpen == true || ReaderMoreButton.Flyout?.IsOpen == true
        || ReaderLayoutSettingsPopup.IsOpen || ReaderTtsPopup.IsOpen || ReaderChapterPreviewPopup.IsOpen
        || (_readerIsPdf && ReaderAnnotationInputPopup.IsOpen)
        || (_readerToolbarKeyboardFocus
            && (ReaderHeaderBar.IsKeyboardFocusWithin || ReaderFooterBar.IsKeyboardFocusWithin));

    private void ReaderToolbarPopup_Changed(object? sender, EventArgs e)
    {
        if (!_readerToolbarAutoHideEnabled) return;
        if (ReaderToolbarInteractionActive()) SetReaderToolbarsVisible(true);
        ScheduleReaderToolbarHide();
    }

    private void ReaderToolbars_GotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (!_readerToolbarAutoHideEnabled || e.NavigationMethod is not (NavigationMethod.Tab or NavigationMethod.Directional))
            return;
        _readerToolbarKeyboardFocus = true;
        SetReaderToolbarsVisible(true);
        ScheduleReaderToolbarHide();
    }

    private void ReaderToolbars_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_readerToolbarAutoHideEnabled) return;
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && !_readerSliderDragging) return;
        UpdateReaderToolbarsForPointer(e.GetPosition(ReaderContentPanel));
    }

    private void ReaderToolbars_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!_readerToolbarAutoHideEnabled) return;
        _readerToolbarKeyboardFocus = false;
        var wasHidden = !_readerToolbarsVisible;
        UpdateReaderToolbarsForPointer(e.GetPosition(ReaderContentPanel));
        _readerToolbarPointerPressed = e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            && (_readerToolbarPointerAtEdge
                || new Rect(ReaderContentPanel.Bounds.Size).Contains(e.GetPosition(ReaderContentPanel)));
        // A touch at a hidden edge reveals controls without also turning the
        // page underneath the newly visible toolbar.
        if (wasHidden && _readerToolbarPointerAtEdge) e.Handled = true;
    }

    private void ReaderToolbars_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _readerToolbarPointerPressed = false;
        if (_readerToolbarAutoHideEnabled)
            UpdateReaderToolbarsForPointer(e.GetPosition(ReaderContentPanel));
    }

    private void UpdateReaderToolbarsForPointer(Point point)
    {
        if (!_readerToolbarAutoHideEnabled) return;
        var width = ReaderContentPanel.Bounds.Width;
        var height = ReaderContentPanel.Bounds.Height;
        var top = _readerToolbarsVisible ? ReaderHeaderBar.Bounds.Height : ReaderToolbarRevealHeight;
        var bottom = _readerToolbarsVisible ? ReaderFooterBar.Bounds.Height : ReaderToolbarRevealHeight;
        _readerToolbarPointerAtEdge = point.X >= 0 && point.X <= width
            && point.Y >= -ReaderContentPanel.Margin.Top && point.Y <= height
            && (point.Y <= top || point.Y >= height - bottom);
        if (_readerToolbarPointerAtEdge)
        {
            _readerToolbarHideTimer?.Stop();
            SetReaderToolbarsVisible(true);
        }
        else
        {
            ScheduleReaderToolbarHide();
        }
    }
}
