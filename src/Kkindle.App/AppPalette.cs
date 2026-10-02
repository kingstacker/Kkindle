using Avalonia.Media;
using Kkindle.Core;

namespace Kkindle;

internal sealed record AppPalette(
    Color Paper, Color Sidebar, Color Panel, Color Ink, Color Muted, Color Subtle,
    Color Hairline, Color StrongBorder, Color Hover, Color Pressed, Color Cover,
    Color Accent, Color AccentHover, Color AccentPressed, Color OnAccent)
{
    public static AppPalette For(AppTheme theme) => theme switch
    {
        AppTheme.Night => Night,
        AppTheme.Green => Green,
        AppTheme.MistBlue => MistBlue,
        AppTheme.Ivory => Ivory,
        _ => Classic
    };

    private static readonly AppPalette Classic = Create(
        "#FFFFFF", "#FFFFFF", "#FFFFFF", "#000000", "#5A5A5A", "#777777",
        "#E2E2E2", "#BFBFBF", "#F2F2F2", "#D9D9D9", "#E4E4E4",
        "#000000", "#1F1F1F", "#3F3F3F", "#FFFFFF");
    private static readonly AppPalette Night = Create(
        "#20231F", "#191D19", "#282D26", "#E5E5DB", "#B6BEB0", "#A0AA99",
        "#414A3E", "#687560", "#303A2C", "#404E37", "#333C2F",
        "#BDCCA8", "#CDDBBE", "#AAB99B", "#202719");
    private static readonly AppPalette Green = Create(
        "#EDF3EA", "#E1EBDD", "#F4F7F0", "#27392A", "#4F624E", "#657460",
        "#CCD8C6", "#ADBFA6", "#E0EADB", "#D2E0CA", "#D8E3D0",
        "#48683E", "#3C5934", "#314A2B", "#FAFCF7");
    private static readonly AppPalette MistBlue = Create(
        "#E8EFF5", "#DEE7EF", "#F1F5F9", "#283746", "#4B5D6D", "#6C7D8D",
        "#CAD5DF", "#A6B8C8", "#DBE5EE", "#CBD9E5", "#D4E0EA",
        "#486B89", "#3D5D79", "#314D66", "#F7FAFD");
    private static readonly AppPalette Ivory = Create(
        "#F8F5EC", "#EFEADD", "#FCFAF4", "#37342D", "#5E584D", "#797162",
        "#DCD5C6", "#BFB5A0", "#ECE6D8", "#DFD5C1", "#E7DFCD",
        "#756049", "#63503D", "#514131", "#FFFAF1");

    // Generic control resources must also be scoped to the reader; otherwise
    // a dark library would leak through Fluent templates in a light reader.
    public static AppPalette FromReader(ReaderPalette palette) => new(
        palette.Page, palette.Sidebar, palette.Chrome, palette.Ink, palette.Muted, palette.Muted,
        palette.Border, palette.Border, palette.Hover, palette.Selected, palette.Selected,
        palette.Accent, palette.Accent, palette.Accent, palette.OnAccent);

    private static AppPalette Create(params string[] colors)
    {
        var c = colors.Select(Color.Parse).ToArray();
        return new(c[0], c[1], c[2], c[3], c[4], c[5], c[6], c[7], c[8], c[9], c[10], c[11], c[12], c[13], c[14]);
    }
}
