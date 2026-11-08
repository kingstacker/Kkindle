using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Kkindle.Mobile.Controls;

/// <summary>Sheet interaction for a hosted native slider.</summary>
public interface INativeSliderInteraction
{
    void SetSheetInteractionBlocked(bool blocked);
    void SetSheetScrim(Color? color, Color backdrop);
    bool SupportsSheetClipping => false;
    void SetSheetClipRect(Rect? visible) { }
}

/// <summary>Uses a native slider while retaining the shared slider as the value source.</summary>
public sealed class NativeSliderGroup : ContentControl
{
    public static Func<Slider, Action, Action<bool>, Control>? CreateNativeControl { get; set; }
    public event Action? DragStarted;
    public event Action<bool>? DragCompleted;

    private Slider? _fallback;
    private Control? _native;
    private bool _inViewport = true;
    private bool _hostVisible = true;
    private bool _sheetObscured;
    private bool _sheetCoversControl;
    private Color? _sheetScrim;
    private Color _sheetBackdrop;
    private int _attachmentVersion;
    private NativeScrollPositionTracker? _scrollTracker;
    private Rect? _sheetClipRect;

    public bool SupportsSheetClipping => _native is INativeSliderInteraction { SupportsSheetClipping: true };

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _scrollTracker ??= new NativeScrollPositionTracker(this,
            () => (_native as INativeScrollPosition)?.UpdateScrollPosition());
        _scrollTracker.Attach();
        _attachmentVersion++;
        EffectiveViewportChanged += OnEffectiveViewportChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attachmentVersion++;
        _scrollTracker?.Detach();
        EffectiveViewportChanged -= OnEffectiveViewportChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnEffectiveViewportChanged(object? sender, EffectiveViewportChangedEventArgs e)
    {
        var viewport = e.EffectiveViewport;
        var visible = viewport.Width > 0 && viewport.Height > 0
            && viewport.X <= 0.5 && viewport.Y <= 0.5
            && viewport.Right >= Bounds.Width - 0.5 && viewport.Bottom >= Bounds.Height - 0.5;
        if (visible == _inViewport) return;
        _inViewport = visible;
        ApplyVisibility();
    }

    protected override async void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        var factory = CreateNativeControl;
        if (_native is not null || factory is null || Content is not Slider slider) return;
        var sheet = this.GetVisualAncestors().OfType<Control>()
            .FirstOrDefault(ancestor => ancestor.Name == "SheetScope");
        if (sheet is not null)
        {
            var version = _attachmentVersion;
            await Task.Delay(300);
            if (_native is not null || version != _attachmentVersion || !sheet.IsEffectivelyVisible
                || TopLevel.GetTopLevel(this) is null || !ReferenceEquals(Content, slider)) return;
        }
        _fallback = slider;
        _native = factory(slider, () => DragStarted?.Invoke(), canceled => DragCompleted?.Invoke(canceled));
        Content = null;
        Content = new Grid { Children = { slider, _native } };
        ApplyVisibility();
    }

    public void SetHostVisible(bool visible) { _hostVisible = visible; ApplyVisibility(); }
    public void SetObscured(bool obscured)
    {
        _sheetObscured = obscured;
        _sheetCoversControl = obscured;
        ApplyVisibility();
    }
    public void SetSheetObscured(bool obscured, bool covered)
    {
        _sheetObscured = obscured;
        _sheetCoversControl = covered;
        ApplyVisibility();
    }
    public void SetSheetScrim(Color? color, Color backdrop) { _sheetScrim = color; _sheetBackdrop = backdrop; ApplyVisibility(); }
    public void SetSheetClipRect(Rect? visible)
    {
        _sheetClipRect = visible;
        if (_native is INativeSliderInteraction interaction)
            interaction.SetSheetClipRect(visible);
    }

    private void ApplyVisibility()
    {
        if (_native is null || _fallback is null) return;
        if (_native is INativeSliderInteraction interaction)
        {
            interaction.SetSheetInteractionBlocked(_sheetObscured || !_hostVisible);
            interaction.SetSheetScrim(_sheetScrim, _sheetBackdrop);
            interaction.SetSheetClipRect(_sheetClipRect);
        }
        var covered = _sheetObscured && _sheetCoversControl;
        var showNative = _hostVisible && _inViewport && (!covered || SupportsSheetClipping);
        _native.IsVisible = showNative;
        _fallback.Opacity = !showNative && _hostVisible && !covered ? 1 : 0;
        _fallback.IsHitTestVisible = !showNative && !_sheetObscured && _hostVisible;
    }
}
