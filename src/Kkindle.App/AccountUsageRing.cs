using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Kkindle;

/// <summary>A vector usage ring; the adjacent text provides the values and units.</summary>
public sealed class AccountUsageRing : Control
{
    public static readonly StyledProperty<double> RatioProperty =
        AvaloniaProperty.Register<AccountUsageRing, double>(nameof(Ratio));
    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<AccountUsageRing, IBrush?>(nameof(TrackBrush));
    public static readonly StyledProperty<IBrush?> ValueBrushProperty =
        AvaloniaProperty.Register<AccountUsageRing, IBrush?>(nameof(ValueBrush));

    static AccountUsageRing() => AffectsRender<AccountUsageRing>(RatioProperty, TrackBrushProperty, ValueBrushProperty);

    public double Ratio { get => GetValue(RatioProperty); set => SetValue(RatioProperty, value); }
    public IBrush? TrackBrush { get => GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public IBrush? ValueBrush { get => GetValue(ValueBrushProperty); set => SetValue(ValueBrushProperty, value); }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        const double thickness = 7;
        var radius = (Math.Min(Bounds.Width, Bounds.Height) - thickness) / 2;
        if (radius <= 0) return;
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        context.DrawEllipse(null, new Pen(TrackBrush, thickness), center, radius, radius);
        var ratio = double.IsFinite(Ratio) ? Math.Clamp(Ratio, 0, 1) : 0;
        if (ratio <= 0) return;
        var pen = new Pen(ValueBrush, thickness, lineCap: PenLineCap.Round);
        if (ratio >= 1)
        {
            context.DrawEllipse(null, pen, center, radius, radius);
            return;
        }
        var angle = ratio * 2 * Math.PI - Math.PI / 2;
        var end = new Point(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle));
        var arc = new StreamGeometry();
        using (var geometry = arc.Open())
        {
            geometry.BeginFigure(new Point(center.X, center.Y - radius), false);
            geometry.ArcTo(end, new Size(radius, radius), 0, ratio > 0.5, SweepDirection.Clockwise);
            geometry.EndFigure(false);
        }
        context.DrawGeometry(null, pen, arc);
    }
}
