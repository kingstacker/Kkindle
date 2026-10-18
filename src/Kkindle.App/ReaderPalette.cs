using Avalonia.Media;
using Kkindle.Core;
using Kkindle.Layout;
using SkiaSharp;

namespace Kkindle;

/// <summary>One palette shared by Avalonia chrome and the native typesetter.</summary>
public sealed record ReaderPalette(
    Color Page, Color Chrome, Color Sidebar, Color Ink, Color Muted,
    Color Border, Color Hover, Color Selected, Color Accent, Color OnAccent)
{
    private static ReaderPalette Palette(params string[] colors) => new(
        Color.Parse(colors[0]), Color.Parse(colors[1]), Color.Parse(colors[2]),
        Color.Parse(colors[3]), Color.Parse(colors[4]), Color.Parse(colors[5]),
        Color.Parse(colors[6]), Color.Parse(colors[7]), Color.Parse(colors[8]), Color.Parse(colors[9]));

    private static readonly ReaderPalette Classic = Palette(
        "#FFFFFF", "#FFFFFF", "#FFFFFF", "#111111", "#5A5A5A",
        "#E2E2E2", "#ECECEA", "#E6E6E6", "#242424", "#FFFFFF");
    private static readonly ReaderPalette Beige = Palette(
        "#F4F2EC", "#F4F2EC", "#ECE9E2", "#303030", "#5B5B58",
        "#DCD9D1", "#E8E5DD", "#DAD6CC", "#56544E", "#FAF9F5");
    private static readonly ReaderPalette Green = Palette(
        "#EEF3EC", "#EEF3EC", "#EEF3EC", "#29382D", "#59685D",
        "#C9D5C7", "#DCE6D7", "#D4E0CF", "#3F6B57", "#F5FAF2");

    public static ReaderPalette For(ReaderTheme theme) => theme switch
    {
        ReaderTheme.Beige => Beige,
        ReaderTheme.Green => Green,
        _ => Classic
    };

    public IBrush AccentBrush { get; } = new SolidColorBrush(Accent);
    public IBrush BorderBrush { get; } = new SolidColorBrush(Border);

    public static SKColor ToSkia(Color color) => new(color.R, color.G, color.B, color.A);

    public TypesetPaintTheme PaintTheme => new()
    {
        Background = ToSkia(Page), Text = ToSkia(Ink), Muted = ToSkia(Muted),
        Selection = ToSkia(Ink), SelectionText = ToSkia(Page),
        Highlight = ToSkia(Selected), SearchMark = ToSkia(Hover), Rule = ToSkia(Border)
    };
}
