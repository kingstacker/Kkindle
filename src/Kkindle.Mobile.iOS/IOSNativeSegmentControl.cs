using System.Collections.Specialized;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Kkindle.Mobile.Controls;
using UIKit;

namespace Kkindle.iOSHead;

/// <summary>Uses the system segmented control and its native iOS appearance and gestures.</summary>
[SupportedOSPlatform("ios26.0")]
internal sealed class IOSNativeSegmentControl : NativeControlHost, INativeSegmentInteraction, INativeScrollPosition
{
    private readonly IReadOnlyList<Button> _buttons;
    private UIView? _container;
    private UIView? _backdrop;
    private UISegmentedControl? _segments;
    private UIView? _scrim;
    private UIView? _clipMask;
    private Rect? _clipRect;
    private bool _sheetInteractionBlocked;
    private Color? _sheetScrim;
    private Color _sheetBackdrop;

    public IOSNativeSegmentControl(IReadOnlyList<Button> buttons)
    {
        _buttons = buttons;
        foreach (var button in buttons)
            ((INotifyCollectionChanged)button.Classes).CollectionChanged += OnClassesChanged;
        ActualThemeVariantChanged += (_, _) => UpdateTheme();
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        _container = new SegmentContainer { ClipsToBounds = true };
        _backdrop = new UIView(_container.Bounds)
        {
            AutoresizingMask = UIViewAutoresizing.FlexibleDimensions,
            UserInteractionEnabled = false
        };
        _container.AddSubview(_backdrop);
        _segments = new UISegmentedControl(_buttons.Select(button => button.Content?.ToString() ?? "").ToArray())
        {
            Frame = _container.Bounds,
            AutoresizingMask = UIViewAutoresizing.FlexibleDimensions
        };
        _segments.ValueChanged += OnValueChanged;
        _container.AddSubview(_segments);
        _scrim = new UIView(_container.Bounds)
        {
            AutoresizingMask = UIViewAutoresizing.FlexibleDimensions,
            UserInteractionEnabled = false,
            Alpha = 0
        };
        _container.AddSubview(_scrim);
        UpdateSelection();
        UpdateTheme();
        if (_clipRect is { } clip) SetClipRect(clip);
        _container.UserInteractionEnabled = !_sheetInteractionBlocked;
        ApplyScrim(false);
        return new PlatformHandle(_container.Handle, "UIView");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        if (_segments is not null)
        {
            _segments.ValueChanged -= OnValueChanged;
            _segments.Dispose();
            _segments = null;
        }
        _scrim?.Dispose();
        _scrim = null;
        _backdrop?.Dispose();
        _backdrop = null;
        if (_container is not null) _container.Layer.Mask = null;
        _clipMask?.Dispose();
        _clipMask = null;
        _container?.Dispose();
        _container = null;
    }

    public void SetSheetInteractionBlocked(bool blocked)
    {
        _sheetInteractionBlocked = blocked;
        if (_container is not null) _container.UserInteractionEnabled = !blocked;
    }

    public void UpdateScrollPosition() => TryUpdateNativeControlPosition();

    /// <summary>Masks the part of the hosted view that has scrolled out of its viewport.</summary>
    public void SetClipRect(Rect? visible)
    {
        _clipRect = visible;
        if (_container is null) return;
        if (visible is not { } rect)
        {
            _container.Layer.Mask = null;
            _clipMask?.Dispose();
            _clipMask = null;
            return;
        }

        _clipMask ??= new UIView { BackgroundColor = UIColor.Black };
        _clipMask.Frame = new CGRect(
            rect.X, rect.Y, Math.Max(0, rect.Width), Math.Max(0, rect.Height));
        _container.Layer.Mask = _clipMask.Layer;
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
        // The glass samples what is behind it. The page scrim is painted below
        // the hosted view, so an opaque page-coloured backdrop keeps that
        // sample undimmed and lets the overlay dim the control exactly once,
        // including its opaque selected pill.
        if (_backdrop is not null && _sheetScrim is not null)
            _backdrop.BackgroundColor = UIColor.FromRGB(_sheetBackdrop.R, _sheetBackdrop.G, _sheetBackdrop.B);
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
                    if (_sheetScrim is null && _backdrop is not null)
                        _backdrop.BackgroundColor = UIColor.Clear;
                });
        else
        {
            _scrim.Alpha = (nfloat)opacity;
            if (_sheetScrim is null && _backdrop is not null)
                _backdrop.BackgroundColor = UIColor.Clear;
        }
    }

    private sealed class SegmentContainer : UIView
    {
        public override void LayoutSubviews()
        {
            base.LayoutSubviews();
            if (Subviews.Length < 3) return;
            Subviews[0].Layer.CornerRadius = Bounds.Height / 2;
            Subviews[2].Layer.CornerRadius = Bounds.Height / 2;
        }
    }

    private void OnValueChanged(object? sender, EventArgs e)
    {
        var index = _segments?.SelectedSegment ?? -1;
        if (index < 0 || index >= _buttons.Count) return;
        _buttons[(int)index].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        UpdateSelection();
    }

    private void OnClassesChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateSelection();

    private void UpdateSelection()
    {
        if (_segments is null) return;
        var selected = -1;
        for (var index = 0; index < _buttons.Count; index++)
            if (_buttons[index].Classes.Contains("on"))
            {
                selected = index;
                break;
            }
        _segments.SelectedSegment = selected;
    }

    private void UpdateTheme()
    {
        if (_container is not null)
            _container.OverrideUserInterfaceStyle = ActualThemeVariant == ThemeVariant.Dark
                ? UIUserInterfaceStyle.Dark
                : UIUserInterfaceStyle.Light;
    }
}
