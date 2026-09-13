using Avalonia;
using Avalonia.Media;
using GlyphPath = Avalonia.Controls.Shapes.Path;

namespace Kkindle;

/// <summary>
/// A navigation outline that shares the toolbar's pen and rendering behavior.
/// IconScale changes only the artwork inside the 24-DIP layout slot.
/// </summary>
public sealed class SidebarGlyph : GlyphPath
{
    public static readonly StyledProperty<double> IconScaleProperty =
        AvaloniaProperty.Register<SidebarGlyph, double>(nameof(IconScale), 1,
            validate: value => double.IsFinite(value) && value > 0);

    static SidebarGlyph() => AffectsGeometry<SidebarGlyph>(IconScaleProperty);

    public double IconScale
    {
        get => GetValue(IconScaleProperty);
        set => SetValue(IconScaleProperty, value);
    }

    // Use the same stroke thickness, round caps, and antialiasing as the toolbar.
    protected override Type StyleKeyOverride => typeof(GlyphPath);

    protected override Geometry? CreateDefiningGeometry()
    {
        var source = base.CreateDefiningGeometry();
        if (source is null || IconScale == 1)
            return source;

        // Transform the geometry, not the control or pen. Wrap the shared
        // resource so its own optical offset and other users stay untouched.
        var offset = 12 * (1 - IconScale);
        return new GeometryGroup
        {
            Children = { source },
            Transform = new MatrixTransform(new Matrix(IconScale, 0, 0, IconScale, offset, offset))
        };
    }
}
