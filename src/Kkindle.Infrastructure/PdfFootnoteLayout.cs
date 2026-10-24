using System.Text.RegularExpressions;

namespace Kkindle.Infrastructure;

public sealed record PdfFootnoteReference(int Offset, string Marker, string Text, PdfTextBounds Bounds);

/// <summary>Matches raised references to numbered notes on the same text-layer page.</summary>
public static partial class PdfFootnoteLayout
{
    private sealed record Line(int Start, string Text, PdfTextGlyph[] Glyphs, PdfTextBounds Bounds);

    public static IReadOnlyList<PdfFootnoteReference> Read(PdfPageContent page)
    {
        if (page.Glyphs.Count == 0 || page.Text.Length == 0) return [];
        var lines = TextLines().Matches(page.Text).Select(match =>
        {
            var glyphs = page.Glyphs.Where(g => g.Offset >= match.Index && g.Offset < match.Index + match.Length).ToArray();
            return new Line(match.Index, match.Value, glyphs, Bounds(glyphs));
        }).Where(line => line.Glyphs.Length > 0).ToArray();
        var definitions = new Dictionary<string, List<(int Offset, string Text, PdfTextBounds Bounds)>>();
        foreach (var (line, index) in lines.Select((line, index) => (line, index)))
        {
            var definition = Definition().Match(line.Text);
            if (!definition.Success) continue;
            var body = line.Text[definition.Length..].Trim();
            var previous = line;
            for (var next = index + 1; next < lines.Length; next++)
            {
                var continuation = lines[next];
                var gap = continuation.Bounds.Y - previous.Bounds.Y;
                if (Definition().IsMatch(continuation.Text) || gap <= 0 || gap > line.Bounds.Height * 2.5
                    || continuation.Bounds.Height > line.Bounds.Height * 1.4
                    || continuation.Bounds.X < line.Bounds.X - 0.015
                    || continuation.Bounds.X > line.Bounds.X + 0.12) break;
                body += " " + continuation.Text.Trim();
                previous = continuation;
            }
            if (body.Length == 0) continue;
            var key = definition.Groups["number"].Value;
            if (!definitions.TryGetValue(key, out var notes)) definitions[key] = notes = [];
            notes.Add((line.Start, body, line.Bounds));
        }
        if (definitions.Count == 0) return [];
        var references = new List<PdfFootnoteReference>();
        foreach (Match marker in Marker().Matches(page.Text))
        {
            if (!definitions.TryGetValue(marker.Groups["number"].Value, out var notes)) continue;
            var glyphs = page.Glyphs.Where(g => g.Offset >= marker.Index && g.Offset < marker.Index + marker.Length).ToArray();
            if (glyphs.Length == 0) continue;
            var bounds = Bounds(glyphs);
            // References must be raised above nearby full-sized text. Ordinary
            // parenthesized values and the definition labels are not hover targets.
            var neighbors = page.Glyphs.Where(g => g.Offset < marker.Index && g.Offset >= marker.Index - 40
                && g.Bounds.X < bounds.X && bounds.X - (g.Bounds.X + g.Bounds.Width) < 0.15
                && Math.Abs(g.Bounds.Y - bounds.Y) < Math.Max(bounds.Height, g.Bounds.Height) * 1.5
                && g.Bounds.Height > bounds.Height * 0.8).ToArray();
            if (!neighbors.Any(g => g.Bounds.Y + g.Bounds.Height > bounds.Y + bounds.Height + g.Bounds.Height * 0.2)) continue;
            var note = notes.Where(n => n.Bounds.Y > bounds.Y + bounds.Height && n.Offset != marker.Index)
                .OrderBy(n => n.Bounds.Y).FirstOrDefault();
            if (note.Text is null) continue;
            references.Add(new(marker.Index, marker.Value, PdfTextSelectionLayout.NormalizeCopy(note.Text), bounds));
        }
        return references;
    }

    private static PdfTextBounds Bounds(IReadOnlyList<PdfTextGlyph> glyphs)
    {
        if (glyphs.Count == 0) return new(0, 0, 0, 0);
        var x = glyphs.Min(g => g.Bounds.X);
        var y = glyphs.Min(g => g.Bounds.Y);
        return new(x, y, glyphs.Max(g => g.Bounds.X + g.Bounds.Width) - x,
            glyphs.Max(g => g.Bounds.Y + g.Bounds.Height) - y);
    }

    [GeneratedRegex(@"[^\r\n]+")]
    private static partial Regex TextLines();
    [GeneratedRegex(@"(?:\((?<number>[1-9]\d{0,2})\)|\[(?<number>[1-9]\d{0,2})\])")]
    private static partial Regex Marker();
    [GeneratedRegex(@"^\s*(?:\((?<number>[1-9]\d{0,2})\)|\[(?<number>[1-9]\d{0,2})\])\s+")]
    private static partial Regex Definition();
}
