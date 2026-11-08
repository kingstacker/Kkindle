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

/// <summary>System slider backed by the shared Avalonia slider's range and value.</summary>
[SupportedOSPlatform("ios26.0")]
internal sealed class IOSNativeSliderControl : NativeControlHost, INativeSliderInteraction, INativeScrollPosition
{
    private readonly Slider _source;
    private readonly Action _started;
    private readonly Action<bool> _completed;
    private UIView? _container;
    private UISlider? _slider;
    private UIView? _scrim;
    private UIView? _sheetMask;
    private Rect? _sheetClipRect;
    private bool _blocked;
    private Color? _sheetScrim;
    private Color _sheetBackdrop;

    public IOSNativeSliderControl(Slider source, Action started, Action<bool> completed)
    {
        _source = source;
        _started = started;
        _completed = completed;
        ActualThemeVariantChanged += (_, _) => UpdateTheme();
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        _container = new UIView { ClipsToBounds = true };
        _slider = new UISlider
        {
            Frame = _container.Bounds,
            AutoresizingMask = UIViewAutoresizing.FlexibleDimensions,
            MinimumTrackTintColor = UIColor.Label,
            Continuous = true
        };
        _slider.ValueChanged += OnValueChanged;
        _slider.TouchDown += OnTouchDown;
        _slider.TouchUpInside += OnTouchUp;
        _slider.TouchUpOutside += OnTouchUp;
        _slider.TouchCancel += OnTouchCancel;
        _source.PropertyChanged += OnSourceChanged;
        _container.AddSubview(_slider);
        _scrim = new UIView(_container.Bounds)
        {
            AutoresizingMask = UIViewAutoresizing.FlexibleDimensions,
            UserInteractionEnabled = false,
            Alpha = 0
        };
        _container.AddSubview(_scrim);
        UpdateValue();
        UpdateTheme();
        _container.UserInteractionEnabled = !_blocked;
        ApplyScrim(false);
        SetSheetClipRect(_sheetClipRect);
        return new PlatformHandle(_container.Handle, "UIView");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        _source.PropertyChanged -= OnSourceChanged;
        if (_slider is not null)
        {
            _slider.ValueChanged -= OnValueChanged;
            _slider.TouchDown -= OnTouchDown;
            _slider.TouchUpInside -= OnTouchUp;
            _slider.TouchUpOutside -= OnTouchUp;
            _slider.TouchCancel -= OnTouchCancel;
            _slider.Dispose();
            _slider = null;
        }
        _scrim?.Dispose();
        _scrim = null;
        if (_container is not null) _container.Layer.Mask = null;
        _sheetMask?.Dispose();
        _sheetMask = null;
        _container?.Dispose();
        _container = null;
    }

    private void OnTouchDown(object? sender, EventArgs e) => _started();
    private void OnTouchUp(object? sender, EventArgs e) => _completed(false);
    private void OnTouchCancel(object? sender, EventArgs e) => _completed(true);

    private void OnValueChanged(object? sender, EventArgs e)
    {
        if (_slider is null || _blocked) return;
        var value = (double)_slider.Value;
        if (_source.IsSnapToTickEnabled && _source.TickFrequency > 0)
            value = _source.Minimum + Math.Round((value - _source.Minimum) / _source.TickFrequency) * _source.TickFrequency;
        _source.SetCurrentValue(RangeBase.ValueProperty, Math.Clamp(value, _source.Minimum, _source.Maximum));
        UpdateValue();
    }

    private void OnSourceChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == RangeBase.ValueProperty || e.Property == RangeBase.MinimumProperty
            || e.Property == RangeBase.MaximumProperty || e.Property == IsEnabledProperty)
            UpdateValue();
    }

    private void UpdateValue()
    {
        if (_slider is null) return;
        _slider.MinValue = (float)_source.Minimum;
        _slider.MaxValue = (float)_source.Maximum;
        _slider.Value = (float)_source.Value;
        _slider.Enabled = _source.IsEffectivelyEnabled;
    }

    private void UpdateTheme()
    {
        if (_container is not null)
            _container.OverrideUserInterfaceStyle = ActualThemeVariant == ThemeVariant.Dark
                ? UIUserInterfaceStyle.Dark : UIUserInterfaceStyle.Light;
    }

    public void SetSheetInteractionBlocked(bool blocked)
    {
        _blocked = blocked;
        if (_container is not null) _container.UserInteractionEnabled = !blocked;
    }

    public void UpdateScrollPosition() => TryUpdateNativeControlPosition();

    public bool SupportsSheetClipping => true;

    public void SetSheetClipRect(Rect? visible)
    {
        if (_sheetClipRect == visible && _container?.Layer.Mask is not null) return;
        _sheetClipRect = visible;
        if (_container is null) return;
        if (visible is not { } rect)
        {
            _container.Layer.Mask = null;
            return;
        }
        _sheetMask ??= new UIView { BackgroundColor = UIColor.Black };
        _sheetMask.Frame = new CGRect(rect.X, rect.Y, Math.Max(0, rect.Width), Math.Max(0, rect.Height));
        _container.Layer.Mask = _sheetMask.Layer;
    }

    public void SetSheetScrim(Color? color, Color backdrop)
    {
        if (_sheetScrim == color && (color is null || _sheetBackdrop == backdrop)) return;
        _sheetScrim = color;
        _sheetBackdrop = backdrop;
        ApplyScrim(true);
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
