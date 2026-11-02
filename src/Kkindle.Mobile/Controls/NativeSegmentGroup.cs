using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Kkindle.Mobile.Controls;

/// <summary>Applies the sheet scrim to a hosted native control.</summary>
public interface INativeSegmentScrim
{
    void SetScrim(Color? color);
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
    private Color _scrimColor;

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
        ApplySheetObscured();
    }

    /// <summary>Lets Avalonia's sheet and scrim cover the hosted native view.</summary>
    public void SetObscured(bool obscured)
    {
        if (_native is null || _fallback is null) return;
        _native.IsVisible = !obscured;
        _fallback.Opacity = obscured ? 1 : 0;
    }

    /// <summary>
    /// Dim uncovered native controls in place; hide only those actually covered
    /// by the Avalonia sheet, which must be drawn above them.
    /// </summary>
    public void SetSheetObscured(bool obscured, bool covered, Color scrimColor)
    {
        _sheetObscured = obscured;
        _sheetCoversControl = covered;
        _scrimColor = scrimColor;
        ApplySheetObscured();
    }

    private void ApplySheetObscured()
    {
        if (_native is null || _fallback is null) return;
        if (_native is not INativeSegmentScrim nativeScrim)
        {
            SetObscured(_sheetObscured);
            return;
        }

        nativeScrim.SetScrim(_sheetObscured ? _scrimColor : null);
        _native.IsVisible = !_sheetObscured || !_sheetCoversControl;
        _fallback.Opacity = _sheetObscured && _sheetCoversControl ? 1 : 0;
    }
}
