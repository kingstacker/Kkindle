using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Kkindle;

// Animate layout height, so the sections below follow the unfolding content.
public sealed class SettingsExpanderContent : Border
{
    public static readonly StyledProperty<bool> IsExpandedProperty =
        AvaloniaProperty.Register<SettingsExpanderContent, bool>(nameof(IsExpanded));
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly Stopwatch _clock = new();
    private double _from;
    private double _to;

    public bool IsExpanded
    {
        get => GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    public SettingsExpanderContent()
    {
        ClipToBounds = true;
        IsVisible = false;
        _timer.Tick += (_, _) =>
        {
            var progress = Math.Clamp(_clock.Elapsed.TotalMilliseconds / 220, 0, 1);
            var eased = 1 - Math.Pow(1 - progress, 3);
            Height = _from + (_to - _from) * eased;
            if (progress < 1) return;
            _timer.Stop();
            IsVisible = IsExpanded;
            Height = IsExpanded ? double.NaN : 0;
        };
        DetachedFromVisualTree += (_, _) => _timer.Stop();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != IsExpandedProperty) return;
        if (TopLevel.GetTopLevel(this) is null)
        {
            IsVisible = IsExpanded;
            Height = IsExpanded ? double.NaN : 0;
            return;
        }
        _timer.Stop();
        _from = IsVisible ? Bounds.Height : 0;
        IsHitTestVisible = IsExpanded;
        IsEnabled = IsExpanded;
        if (IsExpanded)
        {
            var width = Math.Max(0, Bounds.Width > 0 ? Bounds.Width : (Parent as Control)?.Bounds.Width ?? 0);
            Child?.Measure(new Size(Math.Max(0, width - Padding.Left - Padding.Right), double.PositiveInfinity));
            _to = (Child?.DesiredSize.Height ?? 0) + Padding.Top + Padding.Bottom;
            IsVisible = true;
        }
        else _to = 0;
        Height = _from;
        _clock.Restart();
        _timer.Start();
    }
}
