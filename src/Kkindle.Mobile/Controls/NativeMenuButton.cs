using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Kkindle.Mobile.Controls;

public sealed class NativeMenuButton : ContentControl
{
    public static Func<IReadOnlyList<MenuItem>, Control>? CreateNativeControl { get; set; }

    private Button? _fallback;
    private Control? _native;
    private bool _obscured;

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        var factory = CreateNativeControl;
        if (_native is not null || factory is null
            || Content is not Button { Flyout: MenuFlyout flyout } fallback) return;
        var items = flyout.Items.OfType<MenuItem>().ToArray();
        if (items.Length == 0) return;

        _fallback = fallback;
        _native = factory(items);
        Content = null;
        Content = new Grid { Children = { fallback, _native } };
        ApplyVisibility();
    }

    public void SetObscured(bool obscured)
    {
        _obscured = obscured;
        ApplyVisibility();
    }

    private void ApplyVisibility()
    {
        if (_fallback is null || _native is null) return;
        _native.IsVisible = !_obscured;
        _fallback.Opacity = _obscured ? 1 : 0;
        _fallback.IsHitTestVisible = false;
    }
}
