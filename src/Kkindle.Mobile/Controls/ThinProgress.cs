using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Kkindle.Mobile.Controls;

/// <summary>Hairline progress with rounded ends: Value is 0–100, fill follows inherited Foreground.</summary>
public sealed class ThinProgress : Control
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<ThinProgress, double>(nameof(Value));

    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<ThinProgress, IBrush?>(nameof(TrackBrush));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<ThinProgress>();

    static ThinProgress()
    {
        AffectsRender<ThinProgress>(ValueProperty, TrackBrushProperty, ForegroundProperty);
    }

    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public IBrush? TrackBrush
    {
        get => GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        var radius = bounds.Height / 2;
        context.DrawRectangle(TrackBrush ?? Brushes.Transparent, null, bounds, radius, radius);
        var fill = bounds.Width * Math.Clamp(Value, 0, 100) / 100;
        // A sliver narrower than the cap would render as a dot; show it at cap width.
        if (Foreground is { } brush && fill > 0)
            context.DrawRectangle(brush, null, bounds.WithWidth(Math.Max(fill, bounds.Height)), radius, radius);
    }
}
