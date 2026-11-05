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
    void SetSheetScrim(Color? color);
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
    private bool _inViewport = true;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        EffectiveViewportChanged += OnEffectiveViewportChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        EffectiveViewportChanged -= OnEffectiveViewportChanged;
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>
    /// A hosted native view is a platform view drawn above the whole Avalonia
    /// scene, so a scrolling ancestor cannot clip it. Whenever any part of the
    /// group leaves the visible viewport — sliding under a page header, mostly
    /// — the Avalonia fallback takes over, because Avalonia does clip it.
    /// </summary>
    private void OnEffectiveViewportChanged(object? sender, EffectiveViewportChangedEventArgs e)
    {
        // The rect is the visible part expressed in this control's own
        // coordinates, so it *contains* the control when nothing is cut off.
        var viewport = e.EffectiveViewport;
        var visible = viewport.Width > 0 && viewport.Height > 0
            && viewport.X <= 0.5 && viewport.Y <= 0.5
            && viewport.Right >= Bounds.Width - 0.5
            && viewport.Bottom >= Bounds.Height - 0.5;
        if (visible == _inViewport) return;
        _inViewport = visible;
        ApplyObscured();
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
            await Task.Delay(300);
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
    }

    /// <summary>Hides the hosted native view; the fallback stands in.</summary>
    public void SetObscured(bool obscured)
    {
        if (_native is null || _fallback is null) return;
        _sheetObscured = obscured;
        _sheetCoversControl = obscured;
        ApplyObscured();
    }

    /// <summary>
    /// A covered native control gives way to the fallback, and a visible one
    /// cannot be tapped while the sheet is open.
    /// </summary>
    public void SetSheetObscured(bool obscured, bool covered)
    {
        _sheetObscured = obscured;
        _sheetCoversControl = covered;
        ApplyObscured();
    }

    /// <summary>Fades the native control with the page's sheet scrim.</summary>
    public void SetSheetScrim(Color? color)
    {
        _sheetScrim = color;
        ApplyObscured();
    }

    private void ApplyObscured()
    {
        if (_native is null || _fallback is null) return;
        if (_native is INativeSegmentInteraction interaction)
        {
            interaction.SetSheetInteractionBlocked(_sheetObscured);
            interaction.SetSheetScrim(_sheetScrim);
        }

        // Hidden while the sheet panel covers it, and while scrolling carries
        // it out of the viewport: the fallback then stands in, drawn and
        // clipped by Avalonia, so it can also take the taps.
        var showNative = _inViewport && !(_sheetObscured && _sheetCoversControl);
        _native.IsVisible = showNative;
        _fallback.Opacity = showNative ? 0 : 1;
        _fallback.IsHitTestVisible = !_inViewport && !_sheetObscured;
    }
}
