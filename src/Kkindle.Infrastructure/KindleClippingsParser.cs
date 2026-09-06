using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Kkindle.Core;

namespace Kkindle.Infrastructure;

public static partial class KindleClippingsParser
{
    public sealed record DisplayPair(KindleClipping Clipping, KindleClipping? PairedNote);

    public static IReadOnlyList<KindleClipping> Parse(string? text, int maxItems = int.MaxValue)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        maxItems = Math.Max(1, maxItems);
        var normalized = text.ReplaceLineEndings("\n");
        var result = new List<KindleClipping>();
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var value in DelimiterPattern().Split(normalized))
        {
            var raw = value.Trim('\n', '\r', ' ', '\t');
            if (raw.Length == 0) continue;
            if (ParseBlock(raw, occurrences) is { } clipping) result.Add(clipping);
            if (result.Count >= maxItems) break;
        }
        return result;
    }

    public static async Task<IReadOnlyList<KindleClipping>> ParseAsync(
        TextReader reader,
        int maxItems = int.MaxValue,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        maxItems = Math.Max(1, maxItems);
        var result = new List<KindleClipping>();
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        var block = new StringBuilder();
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (line.Trim() is "==========")
            {
                if (ParseBlock(block.ToString().Trim(), occurrences) is { } clipping)
                    result.Add(clipping);
                block.Clear();
                if (result.Count >= maxItems) break;
                continue;
            }
            if (block.Length > 0) block.Append('\n');
            block.Append(line);
        }
        if (result.Count < maxItems
            && ParseBlock(block.ToString().Trim(), occurrences) is { } finalClipping)
            result.Add(finalClipping);
        return result;
    }

    private static KindleClipping? ParseBlock(
        string raw,
        IDictionary<string, int> occurrences)
    {
        if (raw.Length == 0) return null;
        var lines = raw.Split('\n');
        var heading = lines[0].Trim().TrimStart('\uFEFF');
        if (heading.Length == 0) return null;
        var metadataIndex = Array.FindIndex(lines, 1, line => line.TrimStart().StartsWith('-'));
        var metadata = metadataIndex >= 0 ? lines[metadataIndex].Trim() : string.Empty;
        var content = metadataIndex >= 0
            ? string.Join("\n", lines.Skip(metadataIndex + 1)).Trim()
            : string.Join("\n", lines.Skip(1)).Trim();
        var (title, author) = ParseHeading(heading);
        var occurrence = occurrences.TryGetValue(raw, out var count) ? count + 1 : 1;
        occurrences[raw] = occurrence;
        return new KindleClipping
        {
            Id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{raw}\n#{occurrence}"))).ToLowerInvariant(),
            BookTitle = title,
            Author = author,
            Type = ParseType(metadata),
            Metadata = metadata,
            Content = content,
            RawBlock = raw,
            AddedAt = ParseAddedAt(metadata)
        };
    }

    /// <summary>
    /// Kindle writes a highlight and its note as separate blocks. Pair notes
    /// with the closest highlight for the same book and location so clients
    /// can render one complete annotation while retaining both source IDs.
    /// </summary>
    public static IReadOnlyList<DisplayPair> PairForDisplay(IEnumerable<KindleClipping> clippings)
    {
        var items = clippings.Where(item => item.Type != KindleClippingType.Bookmark).ToArray();
        var pairedNotes = new HashSet<string>(StringComparer.Ordinal);
        var highlightNotes = new Dictionary<string, KindleClipping>(StringComparer.Ordinal);
        foreach (var clipping in items.Where(item => item.Type == KindleClippingType.Highlight))
        {
            var note = items
                .Where(candidate => candidate.Type == KindleClippingType.Note
                    && !pairedNotes.Contains(candidate.Id)
                    && IsSameBook(clipping, candidate)
                    && LocationsAreRelated(clipping.Metadata, candidate.Metadata))
                .OrderBy(candidate => Math.Abs(Array.IndexOf(items, candidate) - Array.IndexOf(items, clipping)))
                .FirstOrDefault();
            if (note is not null)
            {
                pairedNotes.Add(note.Id);
                highlightNotes[clipping.Id] = note;
            }
        }
        var result = new List<DisplayPair>(items.Length);
        foreach (var clipping in items)
        {
            if (clipping.Type == KindleClippingType.Note && pairedNotes.Contains(clipping.Id)) continue;
            result.Add(new DisplayPair(clipping, highlightNotes.GetValueOrDefault(clipping.Id)));
        }
        return result;
    }

    public static string BuildDocument(IEnumerable<KindleClipping> clippings)
    {
        var blocks = clippings.Select(item => item.RawBlock.Trim().ReplaceLineEndings("\r\n")).Where(value => value.Length > 0).ToArray();
        return blocks.Length == 0 ? string.Empty : string.Join("\r\n==========\r\n", blocks) + "\r\n==========\r\n";
    }

    private static (string Title, string Author) ParseHeading(string heading)
    {
        if (heading.EndsWith('）'))
        {
            var fullWidthOpening = heading.LastIndexOf('（');
            if (fullWidthOpening > 0)
                return (heading[..fullWidthOpening].Trim(), heading[(fullWidthOpening + 1)..^1].Trim());
        }
        if (!heading.EndsWith(')')) return (heading, string.Empty);
        var opening = heading.LastIndexOf(" (", StringComparison.Ordinal);
        if (opening <= 0) return (heading, string.Empty);
        return (heading[..opening].Trim(), heading[(opening + 2)..^1].Trim());
    }

    private static KindleClippingType ParseType(string metadata)
    {
        if (ContainsAny(metadata, "highlight", "划线", "劃線", "标注", "標註", "ハイライト", "하이라이트"))
            return KindleClippingType.Highlight;
        if (ContainsAny(metadata, "note", "笔记", "筆記", "メモ", "노트"))
            return KindleClippingType.Note;
        if (ContainsAny(metadata, "bookmark", "书签", "書籤", "ブックマーク", "책갈피"))
            return KindleClippingType.Bookmark;
        return KindleClippingType.Unknown;
    }

    private static DateTimeOffset? ParseAddedAt(string metadata)
    {
        string[] markers = ["Added on", "添加于", "添加於", "新增於", "作成日", "작성일"];
        var cultures = new[]
        {
            CultureInfo.CurrentCulture,
            CultureInfo.GetCultureInfo("en-US"),
            CultureInfo.GetCultureInfo("zh-CN"),
            CultureInfo.GetCultureInfo("zh-TW"),
            CultureInfo.GetCultureInfo("ja-JP"),
            CultureInfo.GetCultureInfo("ko-KR")
        };

        foreach (var section in Enumerable.Reverse(metadata.Split('|', StringSplitOptions.TrimEntries)))
        {
            var marker = markers.FirstOrDefault(value => section.Contains(value, StringComparison.OrdinalIgnoreCase));
            if (marker is null) continue;
            var markerIndex = section.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            var dateText = section[(markerIndex + marker.Length)..].Trim(' ', '-', ':', '：');
            dateText = CjkWeekdayPattern().Replace(dateText, string.Empty).Trim();
            foreach (var culture in cultures.Distinct())
            {
                if (DateTimeOffset.TryParse(
                        dateText,
                        culture,
                        DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal,
                        out var value))
                    return value;
            }
        }
        return null;
    }

    private static bool ContainsAny(string value, params string[] terms) =>
        terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static bool IsSameBook(KindleClipping left, KindleClipping right) =>
        string.Equals(left.BookTitle, right.BookTitle, StringComparison.CurrentCultureIgnoreCase)
        && string.Equals(left.Author, right.Author, StringComparison.CurrentCultureIgnoreCase);

    private static bool LocationsAreRelated(string leftMetadata, string rightMetadata)
    {
        var left = ExtractLocationRange(leftMetadata);
        var right = ExtractLocationRange(rightMetadata);
        if (left is null || right is null) return false;
        return left.Value.Start <= right.Value.End + 1 && right.Value.Start <= left.Value.End + 1;
    }

    private static (int Start, int End)? ExtractLocationRange(string metadata)
    {
        var location = metadata.Split('|', 2)[0];
        var numbers = LocationNumberPattern().Matches(location)
            .Select(match => int.TryParse(match.Value, out var value) ? value : -1)
            .Where(value => value >= 0)
            .Take(2)
            .ToArray();
        return numbers.Length switch
        {
            0 => null,
            1 => (numbers[0], numbers[0]),
            _ => (Math.Min(numbers[0], numbers[1]), Math.Max(numbers[0], numbers[1]))
        };
    }

    [GeneratedRegex(@"(?m)^\s*={10}\s*$")]
    private static partial Regex DelimiterPattern();

    [GeneratedRegex(@"(?:星期|週|周)[一二三四五六日天]|[月火水木金土日]曜日|[월화수목금토일]요일")]
    private static partial Regex CjkWeekdayPattern();

    [GeneratedRegex(@"\d+")]
    private static partial Regex LocationNumberPattern();
}
