using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Styling;
using Kkindle.Mobile.Controls;
using UIKit;

namespace Kkindle.iOSHead;

[SupportedOSPlatform("ios26.0")]
internal sealed class IOSNativeActionButton(Button source) : NativeControlHost, INativeActionButton, INativeScrollPosition
{
    private UIButton? _button;
    private UIView? _mask;
    private Rect? _clipRect;
    private LineIcon? _icon;

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        _button = UIButton.FromType(UIButtonType.System);
        _button.TintColor = UIColor.Label;
        _button.TouchUpInside += OnClick;
        source.PropertyChanged += OnSourceChanged;
        _icon = source.Content as LineIcon;
        if (_icon is not null) _icon.PropertyChanged += OnSourceChanged;
        ActualThemeVariantChanged += OnThemeChanged;
        UpdateButton();
        UpdateTheme();
        SetClipRect(_clipRect);
        return new PlatformHandle(_button.Handle, "UIView");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        source.PropertyChanged -= OnSourceChanged;
        if (_icon is not null) _icon.PropertyChanged -= OnSourceChanged;
        _icon = null;
        ActualThemeVariantChanged -= OnThemeChanged;
        if (_button is not null)
        {
            _button.TouchUpInside -= OnClick;
            _button.Layer.Mask = null;
            _button.Dispose();
            _button = null;
        }
        _mask?.Dispose();
        _mask = null;
    }

    private void OnClick(object? sender, EventArgs e)
    {
        if (source.IsEffectivelyEnabled)
            source.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private void OnSourceChanged(object? sender, AvaloniaPropertyChangedEventArgs e) => UpdateButton();
    private void OnThemeChanged(object? sender, EventArgs e) => UpdateTheme();

    private void UpdateTheme()
    {
        if (_button is not null)
            _button.OverrideUserInterfaceStyle = ActualThemeVariant == ThemeVariant.Dark
                ? UIUserInterfaceStyle.Dark : UIUserInterfaceStyle.Light;
    }

    private void UpdateButton()
    {
        if (_button is null) return;
        var symbol = source.Tag as string;
        if (_icon?.Filled == true && symbol == "star") symbol = "star.fill";
        _button.SetImage(symbol is null ? null : UIImage.GetSystemImage(symbol), UIControlState.Normal);
        _button.SetTitle(source.Content as string, UIControlState.Normal);
        _button.AccessibilityLabel = source.Content as string ?? (symbol switch
        {
            "chevron.left" => "返回",
            "star" => "收藏",
            "star.fill" => "取消收藏",
            _ => null
        });
        _button.Enabled = source.IsEffectivelyEnabled;
        if (source.Classes.Contains("primary"))
        {
            _button.Configuration = UIButtonConfiguration.FilledButtonConfiguration;
            _button.TintColor = UIColor.Label;
        }
    }

    public void UpdateScrollPosition() => TryUpdateNativeControlPosition();

    public void SetClipRect(Rect? visible)
    {
        _clipRect = visible;
        if (_button is null) return;
        if (visible is not { } rect)
        {
            _button.Layer.Mask = null;
            return;
        }
        _mask ??= new UIView { BackgroundColor = UIColor.Black };
        _mask.Frame = new CGRect(rect.X, rect.Y, Math.Max(0, rect.Width), Math.Max(0, rect.Height));
        _button.Layer.Mask = _mask.Layer;
    }
}
