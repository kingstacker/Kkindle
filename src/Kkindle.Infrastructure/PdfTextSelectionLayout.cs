using System.Text;
using System.Text.RegularExpressions;

namespace Kkindle.Infrastructure;

public readonly record struct PdfTextRange(int Start, int End);

/// <summary>Geometric reading order without changing the persisted PDFium offsets.</summary>
public sealed class PdfTextSelectionLayout
{
    private sealed class Line(IEnumerable<PdfTextGlyph> glyphs)
    {
        public List<PdfTextGlyph> Glyphs { get; } = [.. glyphs];
        public double Top { get; private set; } = glyphs.Select(g => g.Bounds.Y).DefaultIfEmpty(double.PositiveInfinity).Min();
        public double Bottom { get; private set; } = glyphs.Select(g => g.Bounds.Y + g.Bounds.Height).DefaultIfEmpty(double.NegativeInfinity).Max();
        public double Left { get; private set; } = glyphs.Select(g => g.Bounds.X).DefaultIfEmpty(double.PositiveInfinity).Min();
        public double Right { get; private set; } = glyphs.Select(g => g.Bounds.X + g.Bounds.Width).DefaultIfEmpty(double.NegativeInfinity).Max();
        public void Add(PdfTextGlyph glyph)
        {
            Glyphs.Add(glyph);
            Top = Math.Min(Top, glyph.Bounds.Y);
            Bottom = Math.Max(Bottom, glyph.Bounds.Y + glyph.Bounds.Height);
            Left = Math.Min(Left, glyph.Bounds.X);
            Right = Math.Max(Right, glyph.Bounds.X + glyph.Bounds.Width);
        }
    }
    private readonly PdfPageContent _page;
    private readonly bool _usesColumns;
    private readonly bool _hasHorizontalLayout;
    private readonly double _paragraphGap = 1;
    public IReadOnlyList<PdfTextGlyph> Glyphs { get; }
    public PdfTextSelectionLayout(PdfPageContent page)
    {
        _page = page;
        if (page.Glyphs.Count < 2 || page.Glyphs.Count(g => Math.Abs(g.AdvanceY) > Math.Abs(g.AdvanceX)) > page.Glyphs.Count / 4)
        {
            Glyphs = page.Glyphs;
            return;
        }
        var lines = new List<Line>();
        PdfTextGlyph? previousGlyph = null;
        foreach (var glyph in page.Glyphs)
        {
            var last = lines.LastOrDefault();
            // Preserve PDFium's line breaks. Comparing against an expanding
            // union of glyph bounds lets staggered columns bridge successive
            // rows until a whole page becomes one line sorted by X.
            var previousEnd = previousGlyph is { } previous ? previous.Offset + previous.Length : 0;
            var hasLineBreak = glyph.Offset >= previousEnd
                && page.Text.AsSpan(previousEnd, glyph.Offset - previousEnd).IndexOfAny('\r', '\n') >= 0;
            var sameRow = previousGlyph is { } p
                && Math.Abs(p.Bounds.Y + p.Bounds.Height / 2 - glyph.Bounds.Y - glyph.Bounds.Height / 2)
                    < Math.Max(p.Bounds.Height, glyph.Bounds.Height) * 0.8;
            if (last is not null && !hasLineBreak && sameRow)
                last.Add(glyph);
            else lines.Add(new([glyph]));
            previousGlyph = glyph;
        }
        _paragraphGap = ParagraphGap([lines]);
        _hasHorizontalLayout = true;
        var segments = new List<Line>();
        var gutters = new List<(double Left, double Right)>();
        void AddGutter(double left, double right)
        {
            var middle = (left + right) / 2;
            if (middle is > 0.25 and < 0.75) gutters.Add((left, right));
        }
        foreach (var line in lines)
        {
            var sorted = line.Glyphs.OrderBy(g => g.Bounds.X).ToArray();
            var segment = new Line([]);
            for (var i = 0; i < sorted.Length; i++)
            {
                var g = sorted[i];
                if (i > 0)
                {
                    var previous = sorted[i - 1].Bounds;
                    var gap = (g.Bounds.X - previous.X - previous.Width) * page.Width;
                    if (gap > Math.Max(g.Bounds.Height, previous.Height) * page.Height * 2.5)
                    {
                        segments.Add(segment);
                        segment = new([]);
                        AddGutter(previous.X + previous.Width, g.Bounds.X);
                    }
                }
                segment.Add(g);
            }
            segments.Add(segment);
        }
        // PDFium may also put the two columns on separate native text lines,
        // even when their baselines align. Detect those gutters between runs
        // without merging or reordering characters from different rows.
        foreach (var left in segments)
        foreach (var right in segments)
        {
            var height = Math.Max(left.Bottom - left.Top, right.Bottom - right.Top);
            if (right.Left <= left.Right
                || Math.Abs((left.Top + left.Bottom - right.Top - right.Bottom) / 2) >= height * 0.8
                || (right.Left - left.Right) * page.Width <= height * page.Height * 2.5)
                continue;
            AddGutter(left.Right, right.Left);
        }
        // Require repeated evidence of a gutter; a title or spaced equation
        // alone must not turn an ordinary page into columns.
        // Short lines make the middle of a gap move far into the left column.
        // Align evidence by the next column's repeated starting edge instead,
        // then keep the split inside the shared empty interval.
        var alignedGutters = gutters.Select(g => gutters.Where(other => Math.Abs(other.Right - g.Right) < 0.01).ToArray())
            .OrderByDescending(group => group.Length).FirstOrDefault();
        if (alignedGutters is not { Length: >= 2 })
        {
            Glyphs = page.Glyphs;
            return;
        }
        var split = (alignedGutters.Max(g => g.Left) + alignedGutters.Min(g => g.Right)) / 2;
        var ordered = new List<PdfTextGlyph>();
        var band = new List<Line>();
        void Flush()
        {
            foreach (var line in band.OrderBy(l => l.Left >= split ? 1 : 0).ThenBy(l => l.Top).ThenBy(l => l.Left))
                ordered.AddRange(line.Glyphs);
            band.Clear();
        }
        foreach (var line in segments.OrderBy(l => l.Top).ThenBy(l => l.Left))
        {
            if (line.Left < split && line.Right > split)
            {
                Flush();
                ordered.AddRange(line.Glyphs);
            }
            else band.Add(line);
        }
        Flush();
        Glyphs = ordered;
        _usesColumns = true;
        _paragraphGap = ParagraphGap(segments.GroupBy(l => l.Left >= split));
    }

    private static double ParagraphGap(IEnumerable<IEnumerable<Line>> columns)
    {
        var spacing = columns.SelectMany(column =>
        {
            var rows = column.OrderBy(l => l.Top).ToArray();
            return rows.Zip(rows.Skip(1), (a, b) =>
                (Gap: b.Top - a.Top, Height: Math.Max(a.Bottom - a.Top, b.Bottom - b.Top)))
                .Where(row => row.Gap > row.Height * 0.8).Select(row => row.Gap);
        }).Order().ToArray();
        return spacing.Length == 0 ? 1 : spacing[(spacing.Length - 1) / 2] * 1.6;
    }

    public int IndexAtOffset(int offset) => Enumerable.Range(0, Glyphs.Count)
        .FirstOrDefault(i => Glyphs[i].Offset <= offset && Glyphs[i].Offset + Glyphs[i].Length > offset, -1);

    public IReadOnlyList<PdfTextRange> GetRanges(int start, int end)
    {
        var result = new List<PdfTextRange>();
        foreach (var glyph in Glyphs.Skip(Math.Max(0, start)).Take(Math.Max(0, end - start)).OrderBy(g => g.Offset))
        {
            if (result.Count > 0 && glyph.Offset >= result[^1].End
                && string.IsNullOrWhiteSpace(_page.Text[result[^1].End..glyph.Offset]))
                result[^1] = result[^1] with { End = glyph.Offset + glyph.Length };
            else result.Add(new(glyph.Offset, glyph.Offset + glyph.Length));
        }
        return result;
    }

    public string GetText(IEnumerable<PdfTextRange> ranges) => GetText(ranges, detectParagraphs: false);

    /// <summary>Join visual line wraps while retaining geometric paragraph breaks.</summary>
    public string GetTranslationText(IEnumerable<PdfTextRange> ranges) =>
        NormalizeCopy(GetText(ranges, detectParagraphs: true));

    private string GetText(IEnumerable<PdfTextRange> ranges, bool detectParagraphs)
    {
        var selected = ranges.ToArray();
        if (!_usesColumns && (!detectParagraphs || !_hasHorizontalLayout))
            return string.Join("\n", selected.Select(r => _page.Text[Math.Clamp(r.Start, 0, _page.Text.Length)..Math.Clamp(r.End, r.Start, _page.Text.Length)]));
        var builder = new StringBuilder();
        var sorted = selected.OrderBy(r => r.Start).ToArray();
        var starts = sorted.Select(r => r.Start).ToArray();
        bool Includes(PdfTextGlyph glyph)
        {
            var index = Array.BinarySearch(starts, glyph.Offset);
            if (index < 0) index = ~index - 1;
            return index >= 0 && glyph.Offset < sorted[index].End;
        }
        PdfTextGlyph? previous = null;
        foreach (var glyph in Glyphs.Where(Includes))
        {
            if (previous is { } p)
            {
                var sameLine = Math.Abs(p.Bounds.Y + p.Bounds.Height / 2 - glyph.Bounds.Y - glyph.Bounds.Height / 2)
                    < Math.Max(p.Bounds.Height, glyph.Bounds.Height) * 0.8;
                var gapStart = p.Offset + p.Length;
                var gap = glyph.Offset >= gapStart ? _page.Text[gapStart..glyph.Offset] : string.Empty;
                if (sameLine && glyph.Bounds.X >= p.Bounds.X)
                {
                    if (gap.Length > 0 && string.IsNullOrWhiteSpace(gap)
                        || glyph.Offset != gapStart && (glyph.Bounds.X - p.Bounds.X - p.Bounds.Width) * _page.Width > Math.Max(p.Bounds.Height, glyph.Bounds.Height) * _page.Height * 0.3)
                        builder.Append(' ');
                }
                else
                {
                    var paragraph = glyph.Bounds.Y < p.Bounds.Y
                        || glyph.Bounds.Y - p.Bounds.Y > _paragraphGap
                        || detectParagraphs && (gap.Contains("\n\n", StringComparison.Ordinal)
                            || gap.Contains("\r\n\r\n", StringComparison.Ordinal));
                    builder.Append(paragraph ? "\n\n" : "\n");
                }
            }
            builder.Append(_page.Text.AsSpan(glyph.Offset, glyph.Length));
            previous = glyph;
        }
        return builder.ToString();
    }

    public static string NormalizeCopy(string text)
    {
        text = text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\u00ad", "");
        text = Regex.Replace(text, @"(?<=[A-Za-z])-[ \t]*\n[ \t]*(?=[a-z])", "");
        return Regex.Replace(text, @"(?<=[^\n])\n(?=[^\n])", m =>
            IsCjk(text[m.Index - 1]) && IsCjk(text[m.Index + 1]) ? "" : " ").Trim();
    }
    private static bool IsCjk(char c) => c is >= '\u2e80' and <= '\u9fff' or >= '\uf900' and <= '\ufaff';
}

public static class PdfTextRanges
{
    private const string Prefix = "pdf-ranges:v1:";
    public static string? Encode(IReadOnlyList<PdfTextRange>? ranges) => ranges is { Count: > 1 }
        ? Prefix + string.Join(';', ranges.Select(r => $"{r.Start},{r.End}")) : null;
    public static IReadOnlyList<PdfTextRange> Read(string? fragment, int start, int end)
    {
        if (fragment?.StartsWith(Prefix, StringComparison.Ordinal) != true || fragment.Length > 200_000) return [new(start, end)];
        var ranges = new List<PdfTextRange>();
        foreach (var part in fragment[Prefix.Length..].Split(';'))
        {
            var pair = part.Split(',');
            if (pair.Length != 2 || !int.TryParse(pair[0], out var a) || !int.TryParse(pair[1], out var b)
                || a < start || b > end || b <= a || ranges.Count >= 10_000) return [new(start, end)];
            ranges.Add(new(a, b));
        }
        return ranges.Count > 0 ? ranges : [new(start, end)];
    }
}
