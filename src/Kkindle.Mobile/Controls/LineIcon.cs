using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Kkindle.Mobile.Controls;

/// <summary>
/// Draws a 24×24 icon geometry at a fixed scale. A stretched Path would resize
/// every glyph to its own bounds, so a chevron and a book would not line up.
/// The stroke inherits Foreground, letting a button's state recolor its icon.
/// </summary>
public sealed class LineIcon : Control
{
    private const double GridSize = 24;

    public static readonly StyledProperty<Geometry?> DataProperty =
        AvaloniaProperty.Register<LineIcon, Geometry?>(nameof(Data));

    public static readonly StyledProperty<double> SizeProperty =
        AvaloniaProperty.Register<LineIcon, double>(nameof(Size), 22);

    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<LineIcon, double>(nameof(StrokeThickness), 1.6);

    public static readonly StyledProperty<bool> FilledProperty =
        AvaloniaProperty.Register<LineIcon, bool>(nameof(Filled));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<LineIcon>();

    static LineIcon()
    {
        AffectsMeasure<LineIcon>(SizeProperty);
        AffectsRender<LineIcon>(DataProperty, StrokeThicknessProperty, FilledProperty, ForegroundProperty);
    }

    public Geometry? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public double Size
    {
        get => GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public bool Filled
    {
        get => GetValue(FilledProperty);
        set => SetValue(FilledProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    public override void Render(DrawingContext context)
    {
        if (Data is not { } geometry || Foreground is not { } brush) return;
        var scale = Size / GridSize;
        var pen = new Pen(brush, StrokeThickness, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        using (context.PushTransform(Matrix.CreateScale(scale, scale)))
            context.DrawGeometry(Filled ? brush : null, pen, geometry);
    }
}
