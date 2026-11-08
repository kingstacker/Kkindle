using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Kkindle.Mobile.Controls;
using UIKit;

namespace Kkindle.iOSHead;

/// <summary>Displays and operates the iOS system switch for an Avalonia ToggleSwitch.</summary>
[SupportedOSPlatform("ios26.0")]
internal sealed class IOSNativeSwitchControl : NativeControlHost, INativeSwitchInteraction, INativeScrollPosition
{
    private readonly ToggleSwitch _toggle;
    private readonly string? _accessibilityLabel;
    private UIView? _container;
    private UISwitch? _switch;
    private UIView? _scrim;
    private bool _interactionBlocked;
    private bool _enabled = true;
    private Color? _sheetScrim;
    private Color _sheetBackdrop;

    public IOSNativeSwitchControl(ToggleSwitch toggle, string? accessibilityLabel)
    {
        _toggle = toggle;
        _accessibilityLabel = accessibilityLabel;
        ActualThemeVariantChanged += OnThemeChanged;
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        _container = new UIView { ClipsToBounds = false };
        _switch = new UISwitch
        {
            Frame = _container.Bounds,
            AutoresizingMask = UIViewAutoresizing.FlexibleDimensions,
            AccessibilityLabel = _accessibilityLabel,
            Enabled = _enabled
        };
        _switch.ValueChanged += OnValueChanged;
        _toggle.PropertyChanged += OnTogglePropertyChanged;
        _container.AddSubview(_switch);
        _scrim = new UIView(_container.Bounds)
        {
            AutoresizingMask = UIViewAutoresizing.FlexibleDimensions,
            UserInteractionEnabled = false,
            Alpha = 0
        };
        _container.AddSubview(_scrim);
        UpdateValue();
        UpdateTheme();
        _container.UserInteractionEnabled = !_interactionBlocked;
        ApplyScrim(false);
        return new PlatformHandle(_container.Handle, "UIView");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        _toggle.PropertyChanged -= OnTogglePropertyChanged;
        if (_switch is not null)
        {
            _switch.ValueChanged -= OnValueChanged;
            _switch.Dispose();
            _switch = null;
        }
        _scrim?.Dispose();
        _scrim = null;
        _container?.Dispose();
        _container = null;
    }

    public void SetInteractionBlocked(bool blocked)
    {
        _interactionBlocked = blocked;
        if (_container is not null) _container.UserInteractionEnabled = !blocked;
    }

    public void UpdateScrollPosition() => TryUpdateNativeControlPosition();

    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        if (_switch is not null) _switch.Enabled = enabled;
    }

    public void SetSheetScrim(Color? color, Color backdrop)
    {
        if (_sheetScrim == color && (color is null || _sheetBackdrop == backdrop)) return;
        _sheetScrim = color;
        _sheetBackdrop = backdrop;
        ApplyScrim(true);
    }

    private void OnValueChanged(object? sender, EventArgs e)
    {
        if (_switch is null || _interactionBlocked) return;
        _toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, _switch.On);
        UpdateValue();
    }

    private void OnTogglePropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == ToggleButton.IsCheckedProperty) UpdateValue();
    }

    private void UpdateValue()
    {
        if (_switch is not null) _switch.On = _toggle.IsChecked == true;
    }

    private void OnThemeChanged(object? sender, EventArgs e) => UpdateTheme();

    private void UpdateTheme()
    {
        if (_container is not null)
            _container.OverrideUserInterfaceStyle = ActualThemeVariant == ThemeVariant.Dark
                ? UIUserInterfaceStyle.Dark
                : UIUserInterfaceStyle.Light;
    }

    private void ApplyScrim(bool animated)
    {
        if (_scrim is null) return;
        if (_container is not null && _sheetScrim is not null)
            _container.BackgroundColor = UIColor.FromRGB(_sheetBackdrop.R, _sheetBackdrop.G, _sheetBackdrop.B);
        if (_sheetScrim is { } color)
            _scrim.BackgroundColor = UIColor.FromRGB(color.R, color.G, color.B);
        var opacity = _sheetScrim is { } scrim ? scrim.A / 255.0 : 0;
        if (animated)
            UIView.Animate(0.3, 0, UIViewAnimationOptions.BeginFromCurrentState | UIViewAnimationOptions.CurveEaseOut,
                () => { if (_scrim is not null) _scrim.Alpha = (nfloat)opacity; },
                () =>
                {
                    // The overlay has faded out; only now let glass sample the
                    // page again, otherwise the track would darken mid-fade.
                    if (_sheetScrim is null && _container is not null)
                        _container.BackgroundColor = UIColor.Clear;
                });
        else
        {
            _scrim.Alpha = (nfloat)opacity;
            if (_sheetScrim is null && _container is not null)
                _container.BackgroundColor = UIColor.Clear;
        }
    }
}
