using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Kkindle.Mobile.Controls;

/// <summary>A touch-only, damped edge stretch for the reader settings sheet.</summary>
public sealed class ElasticScrollViewer : ScrollViewer
{
    public event EventHandler? StretchChanged;

    private readonly TranslateTransform _stretch = new();
    private IPointer? _pointer;
    private double _lastY;
    private double _drag;
    private int _animationVersion;

    protected override Type StyleKeyOverride => typeof(ScrollViewer);

    public ElasticScrollViewer()
    {
        AddHandler(PointerPressedEvent, OnTouchPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerMovedEvent, OnTouchMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnTouchReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private void OnTouchPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Pointer.Type != PointerType.Touch) return;
        _pointer = e.Pointer;
        _lastY = e.GetPosition(this).Y;
        // Recover the uncompressed drag distance if a touch interrupts the return animation.
        _drag = Math.Sign(_stretch.Y) * -65 * Math.Log(1 - Math.Min(Math.Abs(_stretch.Y), 47.9) / 48);
        _animationVersion++;
    }

    private void OnTouchMoved(object? sender, PointerEventArgs e)
    {
        if (!ReferenceEquals(e.Pointer, _pointer)) return;
        var y = e.GetPosition(this).Y;
        var delta = y - _lastY;
        _lastY = y;
        var maxOffset = Math.Max(0, Extent.Height - Viewport.Height);
        var atTop = Offset.Y <= 0.5;
        var atBottom = Offset.Y >= maxOffset - 0.5;
        if (_drag > 0 || (atTop && delta > 0))
            _drag = Math.Clamp(_drag + delta, 0, 120);
        else if (_drag < 0 || (atBottom && delta < 0))
            _drag = Math.Clamp(_drag + delta, -120, 0);
        else
            return;
        ApplyStretch(Math.Sign(_drag) * 48 * (1 - Math.Exp(-Math.Abs(_drag) / 65)));
    }

    private void OnTouchReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!ReferenceEquals(e.Pointer, _pointer)) return;
        _pointer = null;
        _drag = 0;
        ReturnToRest();
    }

    private async void ReturnToRest()
    {
        var version = ++_animationVersion;
        var start = _stretch.Y;
        if (Math.Abs(start) < 0.01)
        {
            ApplyStretch(0);
            return;
        }
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < 260 && version == _animationVersion)
        {
            var remaining = 1 - clock.Elapsed.TotalMilliseconds / 260;
            ApplyStretch(start * remaining * remaining * remaining);
            await Task.Delay(16);
        }
        if (version == _animationVersion) ApplyStretch(0);
    }

    private void ApplyStretch(double value)
    {
        if (Content is not Control content) return;
        if (!ReferenceEquals(content.RenderTransform, _stretch)) content.RenderTransform = _stretch;
        _stretch.Y = value;
        StretchChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _animationVersion++;
        _pointer = null;
        _drag = 0;
        ApplyStretch(0);
        base.OnDetachedFromVisualTree(e);
    }
}
