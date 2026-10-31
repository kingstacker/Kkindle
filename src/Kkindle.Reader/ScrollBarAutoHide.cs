using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Kkindle;

/// <summary>
/// Gives every ScrollViewer the same transient scrollbar behavior. The
/// ScrollBar's built-in auto-hide follows pointer interaction, but it does
/// not reliably reveal the thumb for wheel and keyboard scrolling, so the
/// owning ScrollViewer also drives a short-lived scrolling class.
/// </summary>
internal sealed class ScrollBarAutoHide
{
    private const string AutoHideClass = "autoHideScroll";
    private const string ScrollingClass = "autoHideScrolling";
    private const string BarAutoHideClass = "autoHideBar";
    private const string BarScrollingClass = "autoHideBarScrolling";
    private const int IdleHideDelayMs = 900;

    private static readonly ConditionalWeakTable<ScrollViewer, Registration> Registrations = new();
    private static readonly ConditionalWeakTable<ScrollBar, BarRegistration> BarRegistrations = new();

    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<ScrollBarAutoHide, Control, bool>(
            "IsEnabled",
            defaultValue: false);

    static ScrollBarAutoHide()
    {
        IsEnabledProperty.Changed.AddClassHandler<ScrollViewer>(static (viewer, args) =>
        {
            if (args.GetNewValue<bool>())
            {
                if (!Registrations.TryGetValue(viewer, out _))
                    Registrations.Add(viewer, new Registration(viewer));
            }
            else if (Registrations.TryGetValue(viewer, out var registration))
            {
                registration.Dispose();
                Registrations.Remove(viewer);
            }
        });

        IsEnabledProperty.Changed.AddClassHandler<ScrollBar>(static (bar, args) =>
        {
            if (args.GetNewValue<bool>())
            {
                if (!BarRegistrations.TryGetValue(bar, out _))
                    BarRegistrations.Add(bar, new BarRegistration(bar));
            }
            else if (BarRegistrations.TryGetValue(bar, out var registration))
            {
                registration.Dispose();
                BarRegistrations.Remove(bar);
            }
        });
    }

    public static bool GetIsEnabled(Control element) => element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(Control element, bool value) =>
        element.SetValue(IsEnabledProperty, value);

    public static void Show(ScrollBar element)
    {
        if (BarRegistrations.TryGetValue(element, out var registration))
            registration.Show();
    }

    private sealed class Registration : IDisposable
    {
        private readonly ScrollViewer _viewer;
        private readonly DispatcherTimer _hideTimer;
        private readonly HashSet<ScrollBar> _bars = [];
        private bool _scanQueued;
        private bool _disposed;

        public Registration(ScrollViewer viewer)
        {
            _viewer = viewer;
            _hideTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(IdleHideDelayMs)
            };
            _hideTimer.Tick += HideTimer_Tick;

            _viewer.Classes.Add(AutoHideClass);
            _viewer.ScrollChanged += Viewer_ScrollChanged;
            _viewer.TemplateApplied += Viewer_TemplateApplied;
            _viewer.AttachedToVisualTree += Viewer_AttachedToVisualTree;
            _viewer.DetachedFromVisualTree += Viewer_DetachedFromVisualTree;
            QueueScan();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _hideTimer.Stop();
            _hideTimer.Tick -= HideTimer_Tick;
            _viewer.ScrollChanged -= Viewer_ScrollChanged;
            _viewer.TemplateApplied -= Viewer_TemplateApplied;
            _viewer.AttachedToVisualTree -= Viewer_AttachedToVisualTree;
            _viewer.DetachedFromVisualTree -= Viewer_DetachedFromVisualTree;
            foreach (var bar in _bars.ToArray()) DetachBar(bar);
            _bars.Clear();
            _viewer.Classes.Remove(AutoHideClass);
            _viewer.Classes.Remove(ScrollingClass);
        }

        private void Viewer_ScrollChanged(object? sender, ScrollChangedEventArgs e)
        {
            QueueScan();
            if (e.OffsetDelta == default) return;

            Show();
            RestartHideTimer();
        }

        private void Viewer_TemplateApplied(object? sender, TemplateAppliedEventArgs e) => QueueScan();

        private void Viewer_AttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e) => QueueScan();

        private void Viewer_DetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
        {
            _hideTimer.Stop();
            _viewer.Classes.Remove(ScrollingClass);
            foreach (var bar in _bars.ToArray()) DetachBar(bar);
            _bars.Clear();
        }

        private void QueueScan()
        {
            if (_disposed || _scanQueued) return;
            _scanQueued = true;
            Dispatcher.UIThread.Post(Scan, DispatcherPriority.Loaded);
        }

        private void Scan()
        {
            _scanQueued = false;
            if (_disposed) return;

            var currentBars = _viewer.GetVisualDescendants()
                .OfType<ScrollBar>()
                .Where(IsOwnedByViewer)
                .ToHashSet();

            foreach (var bar in _bars.Where(bar => !currentBars.Contains(bar)).ToArray())
                DetachBar(bar);

            foreach (var bar in currentBars)
            {
                if (_bars.Add(bar))
                {
                    bar.PointerEntered += Bar_PointerEntered;
                    bar.PointerExited += Bar_PointerExited;
                }
            }
        }

        private bool IsOwnedByViewer(ScrollBar bar)
        {
            for (var current = bar.GetVisualParent(); current is not null; current = current.GetVisualParent())
            {
                if (ReferenceEquals(current, _viewer)) return true;
                if (current is ScrollViewer) return false;
            }

            return false;
        }

        private void DetachBar(ScrollBar bar)
        {
            bar.PointerEntered -= Bar_PointerEntered;
            bar.PointerExited -= Bar_PointerExited;
            _bars.Remove(bar);
        }

        private void Bar_PointerEntered(object? sender, PointerEventArgs e)
        {
            Show();
            _hideTimer.Stop();
        }

        private void Bar_PointerExited(object? sender, PointerEventArgs e)
        {
            if (_bars.Any(bar => bar.IsPointerOver)) return;
            RestartHideTimer();
        }

        private void Show() => _viewer.Classes.Add(ScrollingClass);

        private void RestartHideTimer()
        {
            _hideTimer.Stop();
            _hideTimer.Start();
        }

        private void HideTimer_Tick(object? sender, EventArgs e)
        {
            _hideTimer.Stop();
            if (_bars.Any(bar => bar.IsPointerOver)) return;
            _viewer.Classes.Remove(ScrollingClass);
        }
    }

    private sealed class BarRegistration : IDisposable
    {
        private readonly ScrollBar _bar;
        private readonly DispatcherTimer _hideTimer;
        private bool _disposed;

        public BarRegistration(ScrollBar bar)
        {
            _bar = bar;
            _hideTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(IdleHideDelayMs)
            };
            _hideTimer.Tick += HideTimer_Tick;
            _bar.Classes.Add(BarAutoHideClass);
            _bar.PointerEntered += Bar_PointerEntered;
            _bar.PointerExited += Bar_PointerExited;
            _bar.DetachedFromVisualTree += Bar_DetachedFromVisualTree;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _hideTimer.Stop();
            _hideTimer.Tick -= HideTimer_Tick;
            _bar.PointerEntered -= Bar_PointerEntered;
            _bar.PointerExited -= Bar_PointerExited;
            _bar.DetachedFromVisualTree -= Bar_DetachedFromVisualTree;
            _bar.Classes.Remove(BarAutoHideClass);
            _bar.Classes.Remove(BarScrollingClass);
        }

        public void Show()
        {
            if (_disposed) return;
            _bar.Classes.Add(BarScrollingClass);
            RestartHideTimer();
        }

        private void Bar_PointerEntered(object? sender, PointerEventArgs e)
        {
            if (_disposed) return;
            _bar.Classes.Add(BarScrollingClass);
            _hideTimer.Stop();
        }

        private void Bar_PointerExited(object? sender, PointerEventArgs e) => RestartHideTimer();

        private void Bar_DetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
        {
            _hideTimer.Stop();
            _bar.Classes.Remove(BarScrollingClass);
        }

        private void RestartHideTimer()
        {
            if (_disposed) return;
            _hideTimer.Stop();
            _hideTimer.Start();
        }

        private void HideTimer_Tick(object? sender, EventArgs e)
        {
            _hideTimer.Stop();
            if (_bar.IsPointerOver) return;
            _bar.Classes.Remove(BarScrollingClass);
        }
    }
}
