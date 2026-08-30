using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kkindle.Layout;

/// <summary>
/// Deterministic JSON projection of a finished layout. This is the snapshot
/// format for cross-platform pagination equality: the same chapter, options,
/// fonts and engine versions must serialize byte-for-byte identically on
/// Windows, Linux and macOS.
/// </summary>
public static class PageModelJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public sealed record RunDto(
        string FontFile,
        float FontSize,
        int[] Glyphs,
        float[] X,
        float[] Y,
        float OriginX,
        float OriginY,
        bool Sideways,
        bool Bold,
        int TextStart,
        int TextLength,
        int[] Clusters);

    public sealed record ImageDto(
        string Path,
        float Left,
        float Top,
        float Right,
        float Bottom,
        float RotationDegrees = 0f);

    public sealed record RectDto(int Kind, float Left, float Top, float Right, float Bottom, int TextStart);

    public sealed record ZoneDto(
        int Kind,
        string Href,
        string? FootnoteText,
        float Left,
        float Top,
        float Right,
        float Bottom);

    public sealed record PageDto(
        int Index,
        string Mode,
        float Width,
        float Height,
        int TextStart,
        int TextEnd,
        List<RunDto> Runs,
        List<ImageDto> Images,
        List<RectDto> Decorations,
        List<ZoneDto> HotZones);

    public sealed record LayoutDto(int PageCount, int BodyTextLength, List<PageDto> Pages);

    public static string Serialize(ChapterLayout layout)
    {
        var dto = new LayoutDto(
            layout.Pages.Count,
            layout.BodyTextLength,
            layout.Pages.Select(ToDto).ToList());
        return JsonSerializer.Serialize(dto, Options);
    }

    private static PageDto ToDto(LayoutPage page) => new(
        page.Index,
        page.WritingMode.ToString(),
        page.Width,
        page.Height,
        page.TextStartOffset,
        page.TextEndOffset,
        page.Runs.Select(r => new RunDto(
            Path.GetFileName(r.FontPath),
            r.FontSize,
            r.Glyphs.Select(g => (int)g).ToArray(),
            r.X,
            r.Y,
            r.OriginX,
            r.OriginY,
            r.Sideways,
            r.SyntheticBold || r.Style.Bold,
            r.TextStart,
            r.TextLength,
            r.Clusters)).ToList(),
        page.Images.Select(i => new ImageDto(
            i.Path,
            i.Rect.Left,
            i.Rect.Top,
            i.Rect.Right,
            i.Rect.Bottom,
            i.RotationDegrees)).ToList(),
        page.Decorations.Select(d => new RectDto(
            (int)d.Kind,
            d.Rect.Left,
            d.Rect.Top,
            d.Rect.Right,
            d.Rect.Bottom,
            d.TextStart)).ToList(),
        page.HotZones.Select(z => new ZoneDto(
            (int)z.Kind,
            z.Href,
            z.FootnoteText,
            z.Rect.Left,
            z.Rect.Top,
            z.Rect.Right,
            z.Rect.Bottom)).ToList());
}
