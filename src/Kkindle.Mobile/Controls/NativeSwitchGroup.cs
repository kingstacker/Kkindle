using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Kkindle.Mobile.Controls;

/// <summary>Platform interaction needed by a hosted switch above the Avalonia scene.</summary>
public interface INativeSwitchInteraction
{
    void SetInteractionBlocked(bool blocked);
    void SetEnabled(bool enabled);
    void SetSheetScrim(Color? color, Color backdrop);
}

/// <summary>Hosts a platform switch while keeping the ToggleSwitch as the state source.</summary>
public sealed class NativeSwitchGroup : ContentControl
{
    public static Func<ToggleSwitch, string?, Control>? CreateNativeControl { get; set; }
    public static readonly StyledProperty<string?> AccessibilityLabelProperty =
        AvaloniaProperty.Register<NativeSwitchGroup, string?>(nameof(AccessibilityLabel));

    public string? AccessibilityLabel
    {
        get => GetValue(AccessibilityLabelProperty);
        set => SetValue(AccessibilityLabelProperty, value);
    }

    private ToggleSwitch? _fallback;
    private Control? _native;
    private readonly List<Visual> _observedAncestors = [];
    private bool _sheetObscured;
    private bool _sheetCoversControl;
    private Color? _sheetScrim;
    private Color _sheetBackdrop;
    private bool _inViewport = true;
    private int _attachmentVersion;
    private NativeScrollPositionTracker? _scrollTracker;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _scrollTracker ??= new NativeScrollPositionTracker(this,
            () => (_native as INativeScrollPosition)?.UpdateScrollPosition());
        _scrollTracker.Attach();
        _attachmentVersion++;
        EffectiveViewportChanged += OnEffectiveViewportChanged;
        foreach (var ancestor in this.GetVisualAncestors().Prepend(this))
        {
            _observedAncestors.Add(ancestor);
            ancestor.PropertyChanged += OnAncestorPropertyChanged;
        }
        ApplyObscured();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attachmentVersion++;
        _scrollTracker?.Detach();
        EffectiveViewportChanged -= OnEffectiveViewportChanged;
        foreach (var ancestor in _observedAncestors)
            ancestor.PropertyChanged -= OnAncestorPropertyChanged;
        _observedAncestors.Clear();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnAncestorPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IsHitTestVisibleProperty || e.Property == IsEnabledProperty)
            ApplyObscured();
    }

    private void OnEffectiveViewportChanged(object? sender, EffectiveViewportChangedEventArgs e)
    {
        var viewport = e.EffectiveViewport;
        var visible = viewport.Width > 0 && viewport.Height > 0
            && viewport.X <= 0.5 && viewport.Y <= 0.5
            && viewport.Right >= Bounds.Width - 0.5
            && viewport.Bottom >= Bounds.Height - 0.5;
        if (visible == _inViewport) return;
        _inViewport = visible;
        ApplyObscured();
    }

    protected override async void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        var factory = CreateNativeControl;
        if (_native is not null || factory is null || Content is not ToggleSwitch fallback) return;

        // UIKit cannot follow the sheet's Avalonia render-transform animation.
        var sheet = this.GetVisualAncestors().OfType<Control>()
            .FirstOrDefault(ancestor => ancestor.Name == "SheetScope");
        if (sheet is not null)
        {
            var version = _attachmentVersion;
            await Task.Delay(300);
            if (_native is not null || version != _attachmentVersion || !sheet.IsEffectivelyVisible
                || TopLevel.GetTopLevel(this) is null || !ReferenceEquals(Content, fallback)) return;
        }

        _fallback = fallback;
        _native = factory(fallback, AccessibilityLabel);
        fallback.PropertyChanged += OnFallbackPropertyChanged;
        Content = null;
        // iOS 26+ draws a wider glass switch than the historical 51-point track.
        Width = 70;
        Height = 36;
        VerticalAlignment = VerticalAlignment.Center;
        Content = new Grid { Children = { fallback, _native } };
        ApplyObscured();
    }

    private void OnFallbackPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IsEnabledProperty || e.Property == InputElement.IsEffectivelyEnabledProperty)
            ApplyObscured();
    }

    public void SetObscured(bool obscured)
    {
        _sheetObscured = obscured;
        _sheetCoversControl = obscured;
        ApplyObscured();
    }

    public void SetSheetObscured(bool obscured, bool covered)
    {
        _sheetObscured = obscured;
        _sheetCoversControl = covered;
        ApplyObscured();
    }

    public void SetSheetScrim(Color? color, Color backdrop)
    {
        _sheetScrim = color;
        _sheetBackdrop = backdrop;
        ApplyObscured();
    }

    private void ApplyObscured()
    {
        if (_native is null || _fallback is null) return;
        if (_native is INativeSwitchInteraction interaction)
        {
            var hitTestable = _observedAncestors.OfType<InputElement>().All(ancestor => ancestor.IsHitTestVisible);
            interaction.SetEnabled(_fallback.IsEffectivelyEnabled);
            interaction.SetInteractionBlocked(_sheetObscured || !hitTestable || !_fallback.IsEffectivelyEnabled);
            interaction.SetSheetScrim(_sheetScrim, _sheetBackdrop);
        }
        var showNative = _inViewport && !(_sheetObscured && _sheetCoversControl);
        _native.IsVisible = showNative;
        _fallback.Opacity = showNative ? 0 : 1;
        _fallback.IsHitTestVisible = !showNative && !_sheetObscured;
    }
}
