using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.VisualTree;

namespace Kkindle.Mobile.Controls;

public interface INativeActionButton
{
    void SetClipRect(Rect? visible);
}

public sealed class NativeActionButton : ContentControl
{
    public static Func<Button, Control>? CreateNativeControl { get; set; }

    private Button? _fallback;
    private Control? _native;
    private NativeScrollPositionTracker? _scrollTracker;
    private Rect? _clipRect;
    private bool _obscured;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _scrollTracker ??= new NativeScrollPositionTracker(this,
            () => (_native as INativeScrollPosition)?.UpdateScrollPosition());
        _scrollTracker.Attach();
        EffectiveViewportChanged += OnEffectiveViewportChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        EffectiveViewportChanged -= OnEffectiveViewportChanged;
        _scrollTracker?.Detach();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        var factory = CreateNativeControl;
        if (_native is not null || factory is null || Content is not Button fallback) return;
        _fallback = fallback;
        _native = factory(fallback);
        fallback.PropertyChanged += OnFallbackChanged;
        Content = null;
        Content = new Grid { Children = { fallback, _native } };
        if (_native is INativeActionButton native) native.SetClipRect(_clipRect);
        ApplyVisibility();
    }

    private void OnFallbackChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IsVisibleProperty) ApplyVisibility();
    }

    private void OnEffectiveViewportChanged(object? sender, EffectiveViewportChangedEventArgs e)
    {
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        var visible = e.EffectiveViewport.Intersect(bounds);
        _clipRect = visible == bounds ? null : visible;
        if (_native is INativeActionButton native) native.SetClipRect(_clipRect);
    }

    public void SetObscured(bool obscured)
    {
        _obscured = obscured;
        ApplyVisibility();
    }

    private void ApplyVisibility()
    {
        if (_fallback is null || _native is null) return;
        _native.IsVisible = !_obscured && _fallback.IsVisible;
        _fallback.Opacity = _obscured ? 1 : 0;
        _fallback.IsHitTestVisible = false;
    }
}
