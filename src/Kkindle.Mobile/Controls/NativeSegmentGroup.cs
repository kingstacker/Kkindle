using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Kkindle.Mobile.Controls;

/// <summary>Controls a hosted native segment while a sheet is open.</summary>
public interface INativeSegmentInteraction
{
    void SetSheetInteractionBlocked(bool blocked);
    void SetSheetScrim(Color? color, Color backdrop);

    /// <summary>Clips the hosted view to the part still inside its scroll viewport.</summary>
    void SetClipRect(Rect? visible) { }
}

/// <summary>Uses a platform segmented control when one is available.</summary>
public sealed class NativeSegmentGroup : ContentControl
{
    public static Func<IReadOnlyList<Button>, Control>? CreateNativeControl { get; set; }
    private bool _initialized;
    private Control? _fallback;
    private Control? _native;
    private bool _sheetObscured;
    private bool _sheetCoversControl;
    private Color? _sheetScrim;
    private Color _sheetBackdrop;
    private Rect? _clipRect;
    private NativeScrollPositionTracker? _scrollTracker;

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

    /// <summary>
    /// A hosted native view sits above the Avalonia scene, so a scrolling
    /// ancestor cannot clip it. Instead of swapping in the differently styled
    /// Avalonia fallback, the native control is told exactly which part of
    /// itself is still visible and masks the rest.
    /// </summary>
    private void OnEffectiveViewportChanged(object? sender, EffectiveViewportChangedEventArgs e)
    {
        var size = Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0) return;
        var own = new Rect(size);
        var viewport = e.EffectiveViewport.Intersect(own);
        var fullyVisible = viewport.Width >= size.Width - 0.5 && viewport.Height >= size.Height - 0.5;
        var next = fullyVisible ? null : (Rect?)viewport;
        if (next == _clipRect) return;
        _clipRect = next;
        if (_native is INativeSegmentInteraction interaction)
            interaction.SetClipRect(next);
    }

    protected override async void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (_initialized || CreateNativeControl is null || Content is not Control fallback) return;

        // NativeControlHost cannot follow Avalonia's render-transform animation.
        // Wait until the bottom sheet has finished sliding before attaching UIKit.
        var sheet = this.GetVisualAncestors().OfType<Control>()
            .FirstOrDefault(ancestor => ancestor.Name == "SheetScope");
        if (sheet is not null)
        {
            await Task.Delay(420);
            if (_initialized || !sheet.IsVisible || TopLevel.GetTopLevel(this) is null) return;
        }

        var buttons = fallback.GetVisualDescendants().OfType<Button>().ToArray();
        if (buttons.Length < 2) return;
        _initialized = true;
        var native = CreateNativeControl(buttons);
        _fallback = fallback;
        _native = native;
        Content = null;
        fallback.Opacity = 0;
        fallback.IsHitTestVisible = false;
        Content = new Grid { Children = { fallback, native } };
        ApplyObscured();
        if (_clipRect is { } clip && native is INativeSegmentInteraction clipped)
            clipped.SetClipRect(clip);
    }

    /// <summary>Hides the native view; the fallback stands in, under an opaque blocker.</summary>
    public void SetObscured(bool obscured)
    {
        if (_native is null || _fallback is null) return;
        _sheetObscured = obscured;
        _sheetCoversControl = obscured;
        ApplyObscured();
    }

    /// <summary>
    /// A covered native control is hidden instead of swapped for the fallback:
    /// the sheet is opaque there anyway, and swapping would visibly change its
    /// glass styling during the sheet animation.
    /// </summary>
    public void SetSheetObscured(bool obscured, bool covered)
    {
        _sheetObscured = obscured;
        _sheetCoversControl = covered;
        ApplyObscured();
    }

    /// <summary>Fades the native control with the page's sheet scrim.</summary>
    public void SetSheetScrim(Color? color, Color backdrop)
    {
        _sheetScrim = color;
        _sheetBackdrop = backdrop;
        ApplyObscured();
    }

    private void ApplyObscured()
    {
        if (_native is null || _fallback is null) return;
        if (_native is INativeSegmentInteraction interaction)
        {
            interaction.SetSheetInteractionBlocked(_sheetObscured);
            interaction.SetSheetScrim(_sheetScrim, _sheetBackdrop);
        }

        var showNative = !(_sheetObscured && _sheetCoversControl);
        _native.IsVisible = showNative;
        _fallback.Opacity = 0;
        _fallback.IsHitTestVisible = false;
    }
}
