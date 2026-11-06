using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using CoreGraphics;
using Foundation;
using Kkindle.Mobile.Controls;
using UIKit;
using System.Runtime.Versioning;

namespace Kkindle.iOSHead;

/// <summary>Hosts a Liquid Glass segmented control within Avalonia's layout.</summary>
[SupportedOSPlatform("ios26.0")]
internal sealed class IOSNativeSegmentControl : NativeControlHost, INativeSegmentInteraction
{
    private static readonly nfloat TrackInset = 3;
    private readonly IReadOnlyList<Button> _buttons;
    private readonly List<UIButton> _segmentButtons = [];
    private SegmentContainer? _container;
    private UIVisualEffectView? _glassContainer;
    private UIVisualEffectView? _trackGlass;
    private UIView? _selectionPill;
    private UIView? _scrim;
    private int _selectedIndex = -1;
    private bool _sheetInteractionBlocked;
    private Color? _sheetScrim;

    public IOSNativeSegmentControl(IReadOnlyList<Button> buttons)
    {
        _buttons = buttons;
        foreach (var button in buttons)
            ((INotifyCollectionChanged)button.Classes).CollectionChanged += OnClassesChanged;
        ActualThemeVariantChanged += (_, _) => UpdateTheme();
        SizeChanged += (_, _) => LayoutSegments(animated: false);
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        _container = new SegmentContainer(() => LayoutSegments(animated: false))
        {
            ClipsToBounds = true
        };

        _glassContainer = new UIVisualEffectView(new UIGlassContainerEffect())
        {
            Frame = _container.Bounds,
            AutoresizingMask = UIViewAutoresizing.FlexibleDimensions,
            ClipsToBounds = true
        };
        _container.AddSubview(_glassContainer);

        var trackEffect = UIGlassEffect.Create(UIGlassEffectStyle.Regular);
        trackEffect.Interactive = false;
        _trackGlass = new UIVisualEffectView(trackEffect)
        {
            Frame = _glassContainer.ContentView.Bounds,
            AutoresizingMask = UIViewAutoresizing.FlexibleDimensions,
            ClipsToBounds = true
        };
        _glassContainer.ContentView.AddSubview(_trackGlass);

        _selectionPill = new UIView
        {
            ClipsToBounds = true,
            UserInteractionEnabled = false
        };
        _selectionPill.Layer.ShadowColor = UIColor.Black.CGColor;
        _selectionPill.Layer.ShadowOpacity = 0.12f;
        _selectionPill.Layer.ShadowRadius = 5;
        _selectionPill.Layer.ShadowOffset = new CGSize(0, 1);
        _glassContainer.ContentView.AddSubview(_selectionPill);

        var font = UIFont.FromName("KingHwaOldSong", 14)
            ?? UIFont.SystemFontOfSize(14, UIFontWeight.Medium);
        for (var index = 0; index < _buttons.Count; index++)
        {
            var segment = new UIButton(UIButtonType.Custom)
            {
                Tag = index,
                BackgroundColor = UIColor.Clear
            };
            segment.SetTitle(_buttons[index].Content?.ToString() ?? "", UIControlState.Normal);
            segment.TitleLabel.Font = font;
            segment.TouchUpInside += OnSegmentTouchUpInside;
            _segmentButtons.Add(segment);
            _container.AddSubview(segment);
        }

        _scrim = new UIView(_container.Bounds)
        {
            AutoresizingMask = UIViewAutoresizing.FlexibleDimensions,
            UserInteractionEnabled = false,
            Alpha = 0
        };
        _container.AddSubview(_scrim);

        UpdateSelection();
        UpdateTheme();
        LayoutSegments(animated: false);
        _container.UserInteractionEnabled = !_sheetInteractionBlocked;
        ApplyScrim(animated: false);
        return new PlatformHandle(_container.Handle, "UIView");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        foreach (var button in _segmentButtons)
            button.TouchUpInside -= OnSegmentTouchUpInside;
        _segmentButtons.Clear();
        _selectionPill?.Dispose();
        _selectionPill = null;
        _trackGlass?.Dispose();
        _trackGlass = null;
        _glassContainer?.Dispose();
        _glassContainer = null;
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

    private void OnSegmentTouchUpInside(object? sender, EventArgs e)
    {
        if (sender is not UIButton { Tag: var tag }
            || tag < 0
            || tag >= _buttons.Count)
        {
            return;
        }

        _buttons[(int)tag].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        UpdateSelection();
        LayoutSegments(animated: true);
    }

    private void OnClassesChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateSelection();

    private void UpdateSelection()
    {
        var selected = -1;
        for (var index = 0; index < _buttons.Count; index++)
            if (_buttons[index].Classes.Contains("on"))
            {
                selected = index;
                break;
            }
        _selectedIndex = selected;
        for (var index = 0; index < _segmentButtons.Count; index++)
            _segmentButtons[index].Selected = index == selected;
    }

    private void LayoutSegments(bool animated)
    {
        if (_container is null
            || _glassContainer is null
            || _trackGlass is null
            || _selectionPill is null)
        {
            return;
        }

        var bounds = _container.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        var radius = bounds.Height / 2;
        _container.Layer.CornerRadius = radius;
        _glassContainer.Frame = bounds;
        _glassContainer.Layer.CornerRadius = radius;
        _trackGlass.Frame = _glassContainer.ContentView.Bounds;
        _trackGlass.Layer.CornerRadius = radius;

        var segmentWidth = _segmentButtons.Count == 0
            ? 0
            : (bounds.Width - TrackInset * 2) / _segmentButtons.Count;
        for (var index = 0; index < _segmentButtons.Count; index++)
        {
            _segmentButtons[index].Frame = new CGRect(
                TrackInset + index * segmentWidth,
                TrackInset,
                segmentWidth,
                Math.Max(0, bounds.Height - TrackInset * 2));
        }

        if (_selectedIndex < 0 || segmentWidth <= 0)
        {
            _selectionPill.Hidden = true;
            return;
        }

        _selectionPill.Hidden = false;
        var target = new CGRect(
            TrackInset + _selectedIndex * segmentWidth,
            TrackInset,
            segmentWidth,
            Math.Max(0, bounds.Height - TrackInset * 2));
        _selectionPill.Layer.CornerRadius = target.Height / 2;
        if (!animated)
        {
            _selectionPill.Frame = target;
            return;
        }

        UIView.Animate(
            0.22,
            0,
            UIViewAnimationOptions.BeginFromCurrentState | UIViewAnimationOptions.CurveEaseOut,
            () => { if (_selectionPill is not null) _selectionPill.Frame = target; },
            null);
    }

    private void UpdateTheme()
    {
        if (_container is null) return;
        var style = ActualThemeVariant == ThemeVariant.Dark
            ? UIUserInterfaceStyle.Dark
            : UIUserInterfaceStyle.Light;
        _container.OverrideUserInterfaceStyle = style;
        _glassContainer?.OverrideUserInterfaceStyle = style;

        var primary = style == UIUserInterfaceStyle.Dark
            ? UIColor.White
            : UIColor.FromWhiteAlpha(0.08f, 1);
        var muted = style == UIUserInterfaceStyle.Dark
            ? UIColor.FromWhiteAlpha(1, 0.64f)
            : UIColor.FromWhiteAlpha(0.35f, 1);
        if (_selectionPill is not null)
        {
            _selectionPill.BackgroundColor = style == UIUserInterfaceStyle.Dark
                ? UIColor.FromWhiteAlpha(1, 0.18f)
                : UIColor.FromWhiteAlpha(1, 0.66f);
        }
        foreach (var button in _segmentButtons)
        {
            button.SetTitleColor(muted, UIControlState.Normal);
            button.SetTitleColor(primary, UIControlState.Selected);
            button.SetTitleColor(primary, UIControlState.Highlighted);
        }
    }

    private sealed class SegmentContainer(Action layout) : UIView
    {
        public override void LayoutSubviews()
        {
            base.LayoutSubviews();
            layout();
        }
    }
}
