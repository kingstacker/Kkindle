using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Foundation;
using Kkindle.Mobile.Controls;
using UIKit;
using System.Runtime.Versioning;

namespace Kkindle.iOSHead;

/// <summary>Hosts UIKit's system segmented control within Avalonia's layout.</summary>
[SupportedOSPlatform("ios26.0")]
internal sealed class IOSNativeSegmentControl : NativeControlHost, INativeSegmentScrim
{
    private readonly IReadOnlyList<Button> _buttons;
    private UISegmentedControl? _control;
    private UIVisualEffectView? _glass;
    private UIView? _scrim;
    private Color? _scrimColor;

    public IOSNativeSegmentControl(IReadOnlyList<Button> buttons)
    {
        _buttons = buttons;
        foreach (var button in buttons)
            ((INotifyCollectionChanged)button.Classes).CollectionChanged += OnClassesChanged;
        ActualThemeVariantChanged += (_, _) => UpdateTheme();
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        var effect = UIGlassEffect.Create(UIGlassEffectStyle.Regular);
        effect.Interactive = true;
        _glass = new UIVisualEffectView(effect);
        _glass.Layer.CornerRadius = 18;
        _glass.ClipsToBounds = true;
        _control = new UISegmentedControl(_buttons.Select(button => button.Content?.ToString() ?? "").ToArray());
        if (UIFont.FromName("KingHwaOldSong", 14) is { } font)
        {
            var attributes = new UIStringAttributes { Font = font };
            _control.SetTitleTextAttributes(attributes, UIControlState.Normal);
            _control.SetTitleTextAttributes(attributes, UIControlState.Selected);
        }
        _control.Frame = _glass.ContentView.Bounds;
        _control.AutoresizingMask = UIViewAutoresizing.FlexibleDimensions;
        _glass.ContentView.AddSubview(_control);
        _scrim = new UIView(_glass.ContentView.Bounds)
        {
            AutoresizingMask = UIViewAutoresizing.FlexibleDimensions,
            UserInteractionEnabled = false
        };
        _glass.ContentView.AddSubview(_scrim);
        _control.ValueChanged += OnValueChanged;
        UpdateSelection();
        UpdateTheme();
        ApplyScrim();
        return new PlatformHandle(_glass.Handle, "UIView");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        if (_control is null) return;
        _control.ValueChanged -= OnValueChanged;
        _control.Dispose();
        _control = null;
        _scrim?.Dispose();
        _scrim = null;
        _glass?.Dispose();
        _glass = null;
    }

    public void SetScrim(Color? color)
    {
        _scrimColor = color;
        ApplyScrim();
    }

    private void ApplyScrim()
    {
        if (_glass is null || _scrim is null) return;
        _glass.UserInteractionEnabled = _scrimColor is null;
        _scrim.Hidden = _scrimColor is null;
        if (_scrimColor is { } color)
            _scrim.BackgroundColor = UIColor.FromRGBA(color.R, color.G, color.B, color.A);
    }

    private void OnValueChanged(object? sender, EventArgs e)
    {
        if (_control is null) return;
        var index = (int)_control.SelectedSegment;
        if (index >= 0 && index < _buttons.Count)
            _buttons[index].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private void OnClassesChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateSelection();

    private void UpdateSelection()
    {
        if (_control is null) return;
        for (var index = 0; index < _buttons.Count; index++)
            if (_buttons[index].Classes.Contains("on"))
            {
                _control.SelectedSegment = index;
                return;
            }
    }

    private void UpdateTheme()
    {
        if (_control is not null)
        {
            var style = ActualThemeVariant == ThemeVariant.Dark
                ? UIUserInterfaceStyle.Dark : UIUserInterfaceStyle.Light;
            _control.OverrideUserInterfaceStyle = style;
            if (_glass is not null) _glass.OverrideUserInterfaceStyle = style;
        }
    }
}
