using System.Globalization;
using System.Text.RegularExpressions;
using Kkindle.Core;

namespace Kkindle.Infrastructure;

/// <summary>Logical destinations detected in a text PDF.</summary>
public enum PdfPaperNavigationKind
{
    Section,
    Figure,
    Table,
    Reference
}

/// <summary>
/// A crop expressed in the PDFium page coordinate system. Coordinates are
/// fractions of the page with a top-left origin and remain stable when the
/// reader zooms or rotates the view.
/// </summary>
public readonly record struct PdfPageCrop(double X, double Y, double Width, double Height)
{
    public static PdfPageCrop Full => new(0, 0, 1, 1);

    public PdfPageCrop Normalize()
    {
        var x = Math.Clamp(double.IsFinite(X) ? X : 0, 0, 0.99);
        var y = Math.Clamp(double.IsFinite(Y) ? Y : 0, 0, 0.99);
        var width = Math.Clamp(double.IsFinite(Width) ? Width : 1, 0.01, 1 - x);
        var height = Math.Clamp(double.IsFinite(Height) ? Height : 1, 0.01, 1 - y);
        return new(x, y, width, height);
    }
}

public sealed record PdfColumnDetection(
    bool IsTwoColumn,
    PdfPageCrop Left,
    PdfPageCrop Right)
{
    public static PdfColumnDetection SingleColumn => new(false, PdfPageCrop.Full, PdfPageCrop.Full);
}

public sealed record PdfPaperNavigationItem(
    PdfPaperNavigationKind Kind,
    string Title,
    int PageNumber,
    int StartOffset,
    int Level = 0,
    string? Label = null);

public sealed record PdfPaperAnalysis(
    bool IsLikelyPaper,
    IReadOnlyList<PdfPaperNavigationItem> Sections,
    IReadOnlyList<PdfPaperNavigationItem> Elements,
    int TextPageCount);

/// <summary>
/// Lightweight, deterministic analysis for text PDFs. It deliberately does
/// not claim to understand the paper's science; it only finds stable visual
/// destinations that can be reviewed and corrected by the user.
/// </summary>
public static class PdfPaperAnalysisService
{
    private static readonly Regex SectionPattern = new(
        "^(?:(?<number>\\d+(?:[.\\-]\\d+)*|[IVX]+)[.)]?\\s+)?(?<title>abstract|introduction|background|related work|method(?:s|ology)?|materials? and methods?|experiment(?:s|al)?|dataset|data|result(?:s)?|discussion|conclusion|limitations?|future work|acknowledg(?:e)?ments?|appendix|references|bibliography|摘要|引言|背景|相关工作|方法|材料与方法|实验|数据集|结果|讨论|结论|局限性|展望|致谢|附录|参考文献|文献综述)(?:\\b|[：:、.\\-]|$)(?<rest>.*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex NumberedSectionPattern = new(
        "^(?<number>\\d+(?:[.\\-]\\d+)*[.)]?)\\s+(?<title>[^.!?。！？]{2,120})$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex FigurePattern = new(
        "^(?<label>(?:figure|fig\\.?|图|图表)\\s*\\d+[a-z]?)\\s*[:：.\\-]?\\s*(?<title>.*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex TablePattern = new(
        "^(?<label>(?:table|表)\\s*\\d+[a-z]?)\\s*[:：.\\-]?\\s*(?<title>.*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ReferencePattern = new(
        "^(?<label>\\[\\d{1,4}\\]|\\d{1,4}[.)])\\s+(?<title>.+)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly HashSet<string> SectionKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "abstract", "introduction", "background", "related work", "method", "methods",
        "methodology", "materials and methods", "experiment", "experiments", "dataset",
        "data", "results", "discussion", "conclusion", "limitations", "future work",
        "acknowledgments", "acknowledgements", "appendix", "references", "bibliography",
        "摘要", "引言", "背景", "相关工作", "方法", "材料与方法", "实验", "数据集",
        "结果", "讨论", "结论", "局限性", "展望", "致谢", "附录", "参考文献", "文献综述"
    };

    public static PdfPaperAnalysis Analyze(IReadOnlyList<PdfPageText> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        var sections = new List<PdfPaperNavigationItem>();
        var elements = new List<PdfPaperNavigationItem>();
        var seenSections = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenElements = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var inReferences = false;
        var textPageCount = 0;

        foreach (var page in pages.OrderBy(item => item.PageNumber))
        {
            if (string.IsNullOrWhiteSpace(page.Text)) continue;
            textPageCount++;
            foreach (var line in EnumerateLines(page.Text))
            {
                if (line.Text.Length == 0) continue;

                var isReferenceLine = inReferences && ReferencePattern.IsMatch(line.Text);
                if (!isReferenceLine
                    && TryGetSection(line.Text, out var sectionTitle, out var level, out var isReferences))
                {
                    var key = $"{sectionTitle}|{page.PageNumber}";
                    if (seenSections.Add(key))
                        sections.Add(new(PdfPaperNavigationKind.Section, sectionTitle,
                            page.PageNumber, line.StartOffset, level));
                    inReferences |= isReferences;
                    continue;
                }

                if (TryGetElement(line.Text, FigurePattern, PdfPaperNavigationKind.Figure, out var figure))
                {
                    var key = $"{figure.Label}|{page.PageNumber}|{line.StartOffset}";
                    if (seenElements.Add(key))
                        elements.Add(figure with { PageNumber = page.PageNumber, StartOffset = line.StartOffset });
                    continue;
                }

                if (TryGetElement(line.Text, TablePattern, PdfPaperNavigationKind.Table, out var table))
                {
                    var key = $"{table.Label}|{page.PageNumber}|{line.StartOffset}";
                    if (seenElements.Add(key))
                        elements.Add(table with { PageNumber = page.PageNumber, StartOffset = line.StartOffset });
                    continue;
                }

                if (inReferences && ReferencePattern.Match(line.Text) is { Success: true } reference)
                {
                    var label = reference.Groups["label"].Value.Trim();
                    var title = reference.Groups["title"].Value.Trim();
                    var key = $"{label}|{page.PageNumber}|{line.StartOffset}";
                    if (seenElements.Add(key))
                        elements.Add(new(PdfPaperNavigationKind.Reference,
                            $"{label} {Truncate(title, 140)}", page.PageNumber, line.StartOffset, 1, label));
                }
            }
        }

        var isLikelyPaper = sections.Count >= 2
            || elements.Count > 0
            || sections.Any(section => SectionKeywords.Contains(section.Title));
        return new(isLikelyPaper, sections, elements, textPageCount);
    }

    /// <summary>
    /// Detects the characteristic central gutter of a two-column paper. The
    /// result is intentionally conservative; false positives would make the
    /// paper harder to read than leaving the full page visible.
    /// </summary>
    public static PdfColumnDetection DetectColumns(PdfPageContent page)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (page.Glyphs.Count < 24 || page.Width <= 0 || page.Height <= 0)
            return PdfColumnDetection.SingleColumn;

        var glyphs = page.Glyphs
            .Where(glyph => glyph.Bounds.Width > 0 && glyph.Bounds.Height > 0)
            .OrderBy(glyph => glyph.Bounds.X)
            .ToArray();
        if (glyphs.Length < 24) return PdfColumnDetection.SingleColumn;

        var total = glyphs.Length;
        var bestGap = 0d;
        var bestLeft = 0;
        var bestRight = 0;
        for (var index = 0; index + 1 < glyphs.Length; index++)
        {
            var leftGlyph = glyphs[index];
            var rightGlyph = glyphs[index + 1];
            var gap = rightGlyph.Bounds.X - (leftGlyph.Bounds.X + leftGlyph.Bounds.Width);
            var midpoint = (leftGlyph.Bounds.X + rightGlyph.Bounds.X + rightGlyph.Bounds.Width) / 2;
            if (gap <= 0.06 || midpoint < 0.30 || midpoint > 0.70)
                continue;

            var left = index + 1;
            var right = total - left;
            if (left < total * 0.22 || right < total * 0.22 || gap <= bestGap) continue;
            bestGap = gap;
            bestLeft = left;
            bestRight = right;
        }

        if (bestGap <= 0.06 || bestLeft < total * 0.22 || bestRight < total * 0.22)
            return PdfColumnDetection.SingleColumn;

        var split = (glyphs[bestLeft - 1].Bounds.X + glyphs[bestLeft - 1].Bounds.Width) / page.Width;
        var rightStart = glyphs[bestLeft].Bounds.X / page.Width;
        var gutter = Math.Max(0.012, (rightStart - split) * 0.18);
        var leftWidth = Math.Clamp(split + gutter, 0.25, 0.49);
        var rightX = Math.Clamp(rightStart - gutter, 0.51, 0.75);
        if (rightX - leftWidth < 0.02) return PdfColumnDetection.SingleColumn;
        return new(
            true,
            new PdfPageCrop(0, 0, leftWidth, 1).Normalize(),
            new PdfPageCrop(rightX, 0, 1 - rightX, 1).Normalize());
    }

    private static bool TryGetSection(
        string line,
        out string title,
        out int level,
        out bool isReferences)
    {
        title = string.Empty;
        level = 0;
        isReferences = false;
        var match = SectionPattern.Match(line);
        if (match.Success)
        {
            var keyword = match.Groups["title"].Value.Trim();
            var rest = match.Groups["rest"].Value.Trim(' ', '\t', ':', '：', '-', '–', '.');
            title = string.IsNullOrWhiteSpace(rest) ? keyword : $"{keyword}: {rest}";
            level = GetLevel(match.Groups["number"].Value);
            isReferences = keyword.Equals("references", StringComparison.OrdinalIgnoreCase)
                || keyword.Equals("bibliography", StringComparison.OrdinalIgnoreCase)
                || keyword is "参考文献";
            return title.Length > 0;
        }

        match = NumberedSectionPattern.Match(line);
        if (!match.Success) return false;
        title = line.Trim();
        level = GetLevel(match.Groups["number"].Value);
        return level > 0;
    }

    private static bool TryGetElement(
        string line,
        Regex pattern,
        PdfPaperNavigationKind kind,
        out PdfPaperNavigationItem item)
    {
        var match = pattern.Match(line);
        if (!match.Success)
        {
            item = default!;
            return false;
        }

        var label = match.Groups["label"].Value.Trim();
        var title = match.Groups["title"].Value.Trim();
        item = new(kind,
            string.IsNullOrWhiteSpace(title) ? label : $"{label}: {Truncate(title, 140)}",
            0,
            0,
            1,
            label);
        return true;
    }

    private static int GetLevel(string number)
    {
        if (string.IsNullOrWhiteSpace(number)) return 0;
        return Math.Clamp(number.Count(character => character is '.' or '-'), 0, 4);
    }

    private static IEnumerable<(string Text, int StartOffset)> EnumerateLines(string value)
    {
        var start = 0;
        while (start < value.Length)
        {
            var end = value.IndexOfAny(['\r', '\n'], start);
            if (end < 0) end = value.Length;
            var raw = value[start..end];
            var leading = raw.Length - raw.TrimStart().Length;
            var text = raw.Trim();
            if (text.Length > 0) yield return (text, start + leading);
            start = end;
            while (start < value.Length && value[start] is '\r' or '\n') start++;
        }
    }

    private static string Truncate(string value, int maximum) =>
        value.Length <= maximum ? value : value[..maximum].TrimEnd() + "…";
}

/// <summary>Portable anchor encoding used by PDF point annotations.</summary>
public static class ReaderPdfPointAnchor
{
    private const string Prefix = "pdf-point:";

    public static string Encode(double x, double y)
    {
        x = Math.Clamp(double.IsFinite(x) ? x : 0.5, 0, 1);
        y = Math.Clamp(double.IsFinite(y) ? y : 0.5, 0, 1);
        return Prefix
            + x.ToString("R", CultureInfo.InvariantCulture)
            + ","
            + y.ToString("R", CultureInfo.InvariantCulture);
    }

    public static bool TryParse(string? value, out double x, out double y)
    {
        x = y = 0;
        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            return false;
        var parts = value[Prefix.Length..].Split(',', 2);
        return parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y)
            && double.IsFinite(x) && double.IsFinite(y)
            && x is >= 0 and <= 1 && y is >= 0 and <= 1;
    }
}
