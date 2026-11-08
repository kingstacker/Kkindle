using System.Runtime.Versioning;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Styling;
using UIKit;

namespace Kkindle.iOSHead;

[SupportedOSPlatform("ios26.0")]
internal sealed class IOSNativeMenuButton(IReadOnlyList<MenuItem> items) : NativeControlHost
{
    private UIButton? _button;

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        var actions = items.Select(item =>
        {
            var image = item.Tag is string symbol ? UIImage.GetSystemImage(symbol) : null;
            var action = UIAction.Create(item.Header?.ToString() ?? string.Empty, image, null,
                _ => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)));
            if (item.Classes.Contains("destructive"))
                action.Attributes = UIMenuElementAttributes.Destructive;
            if (!item.IsEnabled)
                action.Attributes |= UIMenuElementAttributes.Disabled;
            return action;
        }).ToArray();
        _button = UIButton.FromType(UIButtonType.System);
        _button.SetImage(UIImage.GetSystemImage("ellipsis"), UIControlState.Normal);
        _button.TintColor = UIColor.Label;
        _button.AccessibilityLabel = "更多操作";
        _button.Menu = UIMenu.Create(actions);
        _button.ShowsMenuAsPrimaryAction = true;
        UpdateTheme();
        ActualThemeVariantChanged += OnThemeChanged;
        return new PlatformHandle(_button.Handle, "UIView");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        ActualThemeVariantChanged -= OnThemeChanged;
        _button?.Dispose();
        _button = null;
    }

    private void OnThemeChanged(object? sender, EventArgs e) => UpdateTheme();

    private void UpdateTheme()
    {
        if (_button is not null)
            _button.OverrideUserInterfaceStyle = ActualThemeVariant == ThemeVariant.Dark
                ? UIUserInterfaceStyle.Dark : UIUserInterfaceStyle.Light;
    }
}
