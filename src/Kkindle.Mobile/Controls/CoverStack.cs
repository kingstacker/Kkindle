using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Kkindle.Mobile.Controls;

/// <summary>One cover in a <see cref="CoverStack"/>; a missing image falls back to the title's initial.</summary>
public sealed record CoverStackItem(Bitmap? Image, string Initial);

/// <summary>
/// A collection's face: up to three covers fanned out, the newest in front.
/// Empty slots are drawn as faint ghosts so every folder keeps the same shape.
/// </summary>
public sealed class CoverStack : Control
{
    private const double CoverRatio = 1.45;
    private const double BackScale = 0.84;
    private const double BackVisible = 0.46;
    private const double BackAngle = 8;
    private const double Radius = 5;

    private static readonly BoxShadows Shadow = BoxShadows.Parse("0 3 10 0 #29000000");
    private static readonly Typeface InitialFace = new(FontFamily.Default, FontStyle.Normal, FontWeight.Medium);

    public static readonly StyledProperty<IReadOnlyList<CoverStackItem>?> ItemsProperty =
        AvaloniaProperty.Register<CoverStack, IReadOnlyList<CoverStackItem>?>(nameof(Items));

    public static readonly StyledProperty<IBrush?> PlaceholderProperty =
        AvaloniaProperty.Register<CoverStack, IBrush?>(nameof(Placeholder));

    public static readonly StyledProperty<IBrush?> BackdropProperty =
        AvaloniaProperty.Register<CoverStack, IBrush?>(nameof(Backdrop));

    public static readonly StyledProperty<IBrush?> InkProperty =
        AvaloniaProperty.Register<CoverStack, IBrush?>(nameof(Ink));

    static CoverStack()
    {
        AffectsRender<CoverStack>(ItemsProperty, PlaceholderProperty, BackdropProperty, InkProperty);
    }

    public IReadOnlyList<CoverStackItem>? Items
    {
        get => GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    /// <summary>Fill for covers without an image and for empty slots.</summary>
    public IBrush? Placeholder
    {
        get => GetValue(PlaceholderProperty);
        set => SetValue(PlaceholderProperty, value);
    }

    /// <summary>The card color; back covers are washed toward it so they recede.</summary>
    public IBrush? Backdrop
    {
        get => GetValue(BackdropProperty);
        set => SetValue(BackdropProperty, value);
    }

    /// <summary>Color of a fallback initial.</summary>
    public IBrush? Ink
    {
        get => GetValue(InkProperty);
        set => SetValue(InkProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0) return;

        // The fan needs the front cover plus a visible slice of each back cover.
        var frontHeight = Math.Min(size.Height, size.Width / ((1 + 2 * BackVisible * BackScale) / CoverRatio));
        var front = new Size(frontHeight / CoverRatio, frontHeight);
        var back = new Size(front.Width * BackScale, front.Height * BackScale);
        var centerX = size.Width / 2;
        var bottom = size.Height - (size.Height - frontHeight) / 2;

        var items = Items ?? [];
        CoverStackItem? Slot(int index) => index < items.Count ? items[index] : null;

        // Lifted a little so the tilted outer corners stay level with the front cover.
        var backTop = bottom - back.Height * 1.05;
        var left = new Rect(centerX - front.Width / 2 - back.Width * BackVisible, backTop, back.Width, back.Height);
        var right = new Rect(centerX + front.Width / 2 - back.Width * (1 - BackVisible), backTop, back.Width, back.Height);
        DrawCover(context, Slot(2), left, -BackAngle, recede: true);
        DrawCover(context, Slot(1), right, BackAngle, recede: true);
        DrawCover(context, Slot(0), new Rect(centerX - front.Width / 2, bottom - front.Height, front.Width, front.Height), 0, recede: false);
    }

    private void DrawCover(DrawingContext context, CoverStackItem? item, Rect rect, double angle, bool recede)
    {
        // Back covers lean outward around the middle of their bottom edge.
        var pivot = new Point(rect.Center.X, rect.Bottom);
        var transform = Matrix.CreateTranslation(-pivot.X, -pivot.Y)
            * Matrix.CreateRotation(angle * Math.PI / 180)
            * Matrix.CreateTranslation(pivot.X, pivot.Y);
        using var _ = context.PushTransform(transform);

        var shape = new RoundedRect(rect, Radius);
        if (item is null)
        {
            // Ghost slot: no shadow, so it reads as space waiting for a book.
            using (context.PushOpacity(0.55))
                context.DrawRectangle(Placeholder, null, shape);
            return;
        }

        context.DrawRectangle(Placeholder ?? Brushes.Gray, null, shape, Shadow);
        using (context.PushClip(shape))
        {
            if (item.Image is { } image) DrawFilled(context, image, rect);
            else DrawInitial(context, item.Initial, rect);

            if (recede && Backdrop is ISolidColorBrush backdrop)
                context.DrawRectangle(new SolidColorBrush(backdrop.Color, 0.28), null, rect);
        }
    }

    private static void DrawFilled(DrawingContext context, Bitmap image, Rect rect)
    {
        var source = image.Size;
        if (source.Width <= 0 || source.Height <= 0) return;
        var scale = Math.Max(rect.Width / source.Width, rect.Height / source.Height);
        var crop = new Size(rect.Width / scale, rect.Height / scale);
        var sourceRect = new Rect((source.Width - crop.Width) / 2, (source.Height - crop.Height) / 2, crop.Width, crop.Height);
        context.DrawImage(image, sourceRect, rect);
    }

    private void DrawInitial(DrawingContext context, string initial, Rect rect)
    {
        if (string.IsNullOrEmpty(initial)) return;
        var text = new FormattedText(
            initial,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            InitialFace,
            rect.Width * 0.32,
            Ink ?? Brushes.Gray);
        context.DrawText(text, new Point(rect.X + (rect.Width - text.Width) / 2, rect.Y + (rect.Height - text.Height) / 2));
    }
}
