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
internal sealed class IOSNativeSegmentControl : NativeControlHost, INativeSegmentInteraction
{
    private readonly IReadOnlyList<Button> _buttons;
    private UISegmentedControl? _control;
    private UIView? _container;
    private UIVisualEffectView? _glass;
    private UIView? _scrim;
    private bool _sheetInteractionBlocked;
    private Color? _sheetScrim;

    public IOSNativeSegmentControl(IReadOnlyList<Button> buttons)
    {
        _buttons = buttons;
        foreach (var button in buttons)
            ((INotifyCollectionChanged)button.Classes).CollectionChanged += OnClassesChanged;
        ActualThemeVariantChanged += (_, _) => UpdateTheme();
        SizeChanged += (_, _) => UpdateCornerRadius();
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        var effect = UIGlassEffect.Create(UIGlassEffectStyle.Regular);
        // Keep the track's glass appearance stable while a segment is pressed.
        effect.Interactive = false;
        _container = new UIView { ClipsToBounds = true };
        _glass = new UIVisualEffectView(effect)
        {
            Frame = _container.Bounds,
            AutoresizingMask = UIViewAutoresizing.FlexibleDimensions
        };
        _glass.ClipsToBounds = true;
        _container.AddSubview(_glass);
        UpdateCornerRadius();
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
        _scrim = new UIView(_container.Bounds)
        {
            AutoresizingMask = UIViewAutoresizing.FlexibleDimensions,
            UserInteractionEnabled = false,
            Alpha = 0
        };
        _container.AddSubview(_scrim);
        _control.ValueChanged += OnValueChanged;
        UpdateSelection();
        UpdateTheme();
        _container.UserInteractionEnabled = !_sheetInteractionBlocked;
        ApplyScrim(animated: false);
        return new PlatformHandle(_container.Handle, "UIView");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        if (_control is null) return;
        _control.ValueChanged -= OnValueChanged;
        _control.Dispose();
        _control = null;
        _glass?.Dispose();
        _glass = null;
        _scrim?.Dispose();
        _scrim = null;
        _container?.Dispose();
        _container = null;
    }

    public void SetSheetInteractionBlocked(bool blocked)
    {
        _sheetInteractionBlocked = blocked;
        if (_container is not null) _container.UserInteractionEnabled = !blocked;
    }

    public void SetSheetScrim(Color? color)
    {
        if (_sheetScrim == color) return;
        _sheetScrim = color;
        ApplyScrim(animated: true);
    }

    private void ApplyScrim(bool animated)
    {
        if (_scrim is null) return;
        if (_sheetScrim is { } color)
            _scrim.BackgroundColor = UIColor.FromRGB(color.R, color.G, color.B);
        var opacity = _sheetScrim is { } scrim ? scrim.A / 255.0 : 0;
        if (animated)
            UIView.Animate(0.2, 0, UIViewAnimationOptions.BeginFromCurrentState | UIViewAnimationOptions.CurveLinear,
                () => { if (_scrim is not null) _scrim.Alpha = (nfloat)opacity; }, null);
        else
            _scrim.Alpha = (nfloat)opacity;
    }

    private void UpdateCornerRadius()
    {
        if (_glass is not null)
            _glass.Layer.CornerRadius = (nfloat)(Math.Min(Bounds.Width, Bounds.Height) / 2);
        if (_container is not null)
            _container.Layer.CornerRadius = (nfloat)(Math.Min(Bounds.Width, Bounds.Height) / 2);
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
