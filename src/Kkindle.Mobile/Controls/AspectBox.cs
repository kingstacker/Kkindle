using Avalonia;
using Avalonia.Controls;

namespace Kkindle.Mobile.Controls;

/// <summary>Keeps a book-cover proportion whatever column width the screen gives it.</summary>
public sealed class AspectBox : Decorator
{
    public static readonly StyledProperty<double> RatioProperty =
        AvaloniaProperty.Register<AspectBox, double>(nameof(Ratio), 1.45);

    public double Ratio
    {
        get => GetValue(RatioProperty);
        set => SetValue(RatioProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var size = new Size(availableSize.Width, availableSize.Width * Ratio);
        Child?.Measure(size);
        return size;
    }
}
