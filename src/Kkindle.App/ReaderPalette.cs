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
    private static readonly ReaderPalette Ivory = Palette(
        "#F8F5EC", "#EEE9DC", "#F2EEE3", "#37342D", "#6D675B",
        "#D2CBBC", "#E8E1D3", "#DED4C2", "#756049", "#FFFAF1");
    private static readonly ReaderPalette Night = Palette(
        "#242623", "#1D201D", "#21241F", "#DDDACE", "#A8AD9F",
        "#42483F", "#343B31", "#414C3C", "#B8C8A5", "#20271E");
    private static readonly ReaderPalette Green = Palette(
        "#EEF3EC", "#DFE8DD", "#E5ECE2", "#29382D", "#59685D",
        "#C9D5C7", "#DCE6D7", "#D4E0CF", "#3F6B57", "#F5FAF2");
    private static readonly ReaderPalette WarmBrown = Palette(
        "#F7F0E4", "#EDE1CF", "#F0E6D6", "#3E352A", "#73624F",
        "#DACBB5", "#EBDEC9", "#E5D4B8", "#8A6546", "#FFFAF2");

    public static ReaderPalette For(ReaderTheme theme) => theme switch
    {
        ReaderTheme.Night => Night,
        ReaderTheme.Green => Green,
        ReaderTheme.WarmBrown => WarmBrown,
        ReaderTheme.Ivory => Ivory,
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
