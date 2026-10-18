using Avalonia.Media;
using Kkindle.Core;

namespace Kkindle;

internal sealed record AppPalette(
    Color Paper, Color Sidebar, Color Panel, Color Ink, Color Muted, Color Subtle,
    Color Hairline, Color StrongBorder, Color Hover, Color Pressed, Color Cover,
    Color Accent, Color AccentHover, Color AccentPressed, Color OnAccent, Color Focus)
{
    public static AppPalette For(AppTheme theme) => theme switch
    {
        AppTheme.Beige => Beige,
        AppTheme.Green => Green,
        _ => Classic
    };

    private static readonly AppPalette Classic = Create(
        "#FFFFFF", "#FFFFFF", "#FFFFFF", "#000000", "#5A5A5A", "#777777",
        "#E2E2E2", "#BFBFBF", "#F2F2F2", "#D9D9D9", "#E4E4E4",
        "#000000", "#1F1F1F", "#3F3F3F", "#FFFFFF", "#005FCC");
    private static readonly AppPalette Beige = Create(
        "#F4F2EC", "#ECE9E2", "#F9F7F2", "#303030", "#5B5B58", "#74746F",
        "#DCD9D1", "#B4B0A6", "#E8E5DD", "#DAD6CC", "#E1DED6",
        "#56544E", "#46443F", "#36342F", "#FAF9F5", "#56544E");
    private static readonly AppPalette Green = Create(
        "#EDF3EA", "#E1EBDD", "#F4F7F0", "#27392A", "#4F624E", "#657460",
        "#CCD8C6", "#ADBFA6", "#E0EADB", "#D2E0CA", "#D8E3D0",
        "#48683E", "#3C5934", "#314A2B", "#FAFCF7", "#314A2B");

    // Generic control resources must also be scoped to the reader; otherwise
    // a dark library would leak through Fluent templates in a light reader.
    public static AppPalette FromReader(ReaderPalette palette) => new(
        palette.Page, palette.Sidebar, palette.Chrome, palette.Ink, palette.Muted, palette.Muted,
        palette.Border, palette.Border, palette.Hover, palette.Selected, palette.Selected,
        palette.Accent, palette.Accent, palette.Accent, palette.OnAccent, palette.Accent);

    private static AppPalette Create(params string[] colors)
    {
        var c = colors.Select(Color.Parse).ToArray();
        return new(c[0], c[1], c[2], c[3], c[4], c[5], c[6], c[7], c[8], c[9], c[10], c[11], c[12], c[13], c[14], c[15]);
    }
}
