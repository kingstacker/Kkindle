using System.Net;
using System.Text;

namespace Kkindle.Core;

/// <summary>
/// Text prepared for speech. SourceOffsets contains one source offset per
/// UTF-16 code unit and is useful to callers that need to paint a source
/// highlight after removing markup or decoding an entity.
/// </summary>
public sealed class TtsPreparedText
{
    private readonly int[] _sourceOffsets;

    public TtsPreparedText(string text, IReadOnlyList<int> sourceOffsets)
    {
        Text = text ?? throw new ArgumentNullException(nameof(text));
        ArgumentNullException.ThrowIfNull(sourceOffsets);
        if (sourceOffsets.Count != text.Length)
        {
            throw new ArgumentException(
                "The source-offset map must contain one entry per UTF-16 code unit.",
                nameof(sourceOffsets));
        }

        _sourceOffsets = sourceOffsets.ToArray();
    }

    public string Text { get; }

    public (int Start, int Length) MapToSource(int start, int length)
    {
        if (length <= 0 || _sourceOffsets.Length == 0) return (0, 0);

        var from = Math.Clamp(start, 0, Text.Length);
        var to = Math.Clamp((long)start + length, from, Text.Length);
        if (to <= from) return (0, 0);

        var sourceStart = int.MaxValue;
        var sourceEnd = int.MinValue;
        for (var index = from; index < to; index++)
        {
            sourceStart = Math.Min(sourceStart, _sourceOffsets[index]);
            sourceEnd = Math.Max(sourceEnd, _sourceOffsets[index] + 1);
        }

        return sourceStart == int.MaxValue || sourceEnd <= sourceStart
            ? (0, 0)
            : (sourceStart, sourceEnd - sourceStart);
    }
}

/// <summary>
/// Removes obvious HTML/XML markup and splits speech into chunks at the most
/// natural boundary available. The default maximum is deliberately in the
/// recommended 200–500 Chinese-character range.
/// </summary>
public static class TtsTextSegmenter
{
    public const int RecommendedMinimumCharacters = 200;
    public const int RecommendedMaximumCharacters = 500;
    public const int DefaultMaximumCharacters = 420;

    private static readonly HashSet<string> BlockTags = new(
        [
            "article", "blockquote", "br", "dd", "div", "dl", "dt", "figcaption",
            "figure", "h1", "h2", "h3", "h4", "h5", "h6", "header", "hr", "li",
            "main", "ol", "p", "pre", "section", "table", "td", "th", "tr", "ul"
        ],
        StringComparer.OrdinalIgnoreCase);

    public static TtsPreparedText Prepare(string? source)
    {
        if (string.IsNullOrEmpty(source))
            return new TtsPreparedText(string.Empty, []);

        var text = new StringBuilder(source.Length);
        var sourceOffsets = new List<int>(source.Length);
        var index = 0;
        while (index < source.Length)
        {
            if (source.AsSpan(index).StartsWith("<!--", StringComparison.Ordinal))
            {
                var commentEnd = source.IndexOf("-->", index + 4, StringComparison.Ordinal);
                index = commentEnd < 0 ? source.Length : commentEnd + 3;
                continue;
            }

            if (source[index] == '<'
                && TryReadMarkup(source, index, out var markupEnd, out var tagName))
            {
                if (BlockTags.Contains(tagName)
                    && text.Length > 0
                    && !char.IsWhiteSpace(text[^1]))
                {
                    text.Append('\n');
                    sourceOffsets.Add(index);
                }

                index = markupEnd + 1;
                continue;
            }

            if (source[index] == '&'
                && TryReadEntity(source, index, out var entityEnd, out var decoded))
            {
                foreach (var value in decoded)
                {
                    text.Append(value);
                    sourceOffsets.Add(index);
                }

                index = entityEnd + 1;
                continue;
            }

            if (source[index] != '\0')
            {
                text.Append(source[index]);
                sourceOffsets.Add(index);
            }

            index++;
        }

        return new TtsPreparedText(text.ToString(), sourceOffsets);
    }

    public static IReadOnlyList<TtsTextSegment> Split(
        string? source,
        int maxCharacters = DefaultMaximumCharacters)
        => Split(Prepare(source), maxCharacters);

    public static IReadOnlyList<TtsTextSegment> Split(
        TtsPreparedText prepared,
        int maxCharacters = DefaultMaximumCharacters)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        if (string.IsNullOrWhiteSpace(prepared.Text)) return [];

        maxCharacters = Math.Clamp(
            maxCharacters,
            120,
            RecommendedMaximumCharacters * 2 + 200);

        var text = prepared.Text;
        var segments = new List<TtsTextSegment>();
        var cursor = 0;
        while (cursor < text.Length)
        {
            while (cursor < text.Length && char.IsWhiteSpace(text[cursor])) cursor++;
            if (cursor >= text.Length) break;

            var start = cursor;
            var limit = Math.Min(text.Length, start + maxCharacters);
            var end = FindLastBoundary(text, start, limit, IsParagraphBoundary);
            end = end < 0
                ? FindLastBoundary(text, start, limit, IsSentenceBoundary)
                : end;
            end = end < 0
                ? FindLastBoundary(text, start, limit, IsSecondaryBoundary)
                : end;
            end = end < 0
                ? FindLastBoundary(text, start, limit, IsTertiaryBoundary)
                : end;

            if (end < 0)
                end = FindSafeFallbackEnd(text, start, limit);

            if (end <= start)
                end = Math.Min(text.Length, start + 1);

            while (end > start && char.IsWhiteSpace(text[end - 1])) end--;
            if (end > start)
            {
                segments.Add(new TtsTextSegment(
                    start,
                    end - start,
                    text.Substring(start, end - start)));
            }

            cursor = Math.Max(start + 1, end);
            while (cursor < text.Length && char.IsWhiteSpace(text[cursor])) cursor++;
        }

        return segments;
    }

    /// <summary>
    /// Splits text into playback-sized sentences. Unlike <see cref="Split"/>,
    /// this method stops at the first complete sentence so the text sent to
    /// the provider and the text highlighted by the reader are the same
    /// sentence. A very long sentence is only cut when it exceeds the hard
    /// safety limit; ordinary sentences are never split just to reach the
    /// configured request size.
    /// </summary>
    public static IReadOnlyList<TtsTextSegment> SplitSentences(
        string? source,
        int maxCharacters = DefaultMaximumCharacters)
        => SplitSentences(Prepare(source), maxCharacters);

    /// <inheritdoc cref="SplitSentences(string?, int)"/>
    public static IReadOnlyList<TtsTextSegment> SplitSentences(
        TtsPreparedText prepared,
        int maxCharacters = DefaultMaximumCharacters)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        if (string.IsNullOrWhiteSpace(prepared.Text)) return [];

        maxCharacters = Math.Clamp(
            maxCharacters,
            120,
            RecommendedMaximumCharacters * 2 + 200);

        var text = prepared.Text;
        var segments = new List<TtsTextSegment>();
        var cursor = 0;
        while (cursor < text.Length)
        {
            while (cursor < text.Length && char.IsWhiteSpace(text[cursor])) cursor++;
            if (cursor >= text.Length) break;

            var start = cursor;
            var naturalEnd = FindNextSentenceEnd(text, start);
            var hardLimit = Math.Min(text.Length, start + maxCharacters);
            var end = naturalEnd switch
            {
                // Keep a complete sentence even when it is a little longer
                // than the normal request size. This is what makes one
                // highlighted range correspond to one spoken sentence.
                > 0 when naturalEnd - start <= RecommendedMaximumCharacters * 2 + 200
                    => naturalEnd,
                > 0 => FindSafeFallbackEnd(text, start, hardLimit),
                _ => FindSafeFallbackEnd(text, start, hardLimit),
            };

            if (end <= start)
                end = Math.Min(text.Length, start + 1);

            while (end > start && char.IsWhiteSpace(text[end - 1])) end--;
            if (end > start)
            {
                segments.Add(new TtsTextSegment(
                    start,
                    end - start,
                    text.Substring(start, end - start)));
            }

            cursor = Math.Max(start + 1, end);
            while (cursor < text.Length && char.IsWhiteSpace(text[cursor])) cursor++;
        }

        return segments;
    }

    /// <summary>
    /// Splits spoken sentences again at reader page boundaries. A sentence may
    /// continue across pages, but the two page fragments are played in order so
    /// the reader can move to the next page before its audio starts. Offsets
    /// must be measured in the same plain-text string passed as <paramref name="source"/>.
    /// </summary>
    public static IReadOnlyList<TtsTextSegment> SplitSentencesAtPageBreaks(
        string? source,
        IReadOnlyList<int>? pageBreakOffsets,
        int maxCharacters = DefaultMaximumCharacters)
    {
        var sentences = SplitSentences(source, maxCharacters);
        if (sentences.Count == 0
            || pageBreakOffsets is null
            || pageBreakOffsets.Count == 0
            || string.IsNullOrEmpty(source))
        {
            return sentences;
        }

        var boundaries = pageBreakOffsets
            .Where(offset => offset > 0 && offset < source.Length)
            .Distinct()
            .OrderBy(offset => offset)
            .ToArray();
        if (boundaries.Length == 0) return sentences;

        var result = new List<TtsTextSegment>(sentences.Count + boundaries.Length);
        foreach (var sentence in sentences)
        {
            var partStart = sentence.Start;
            foreach (var boundary in boundaries)
            {
                if (boundary <= partStart) continue;
                if (boundary >= sentence.End) break;

                AddTrimmedSegment(source, partStart, boundary, result);
                partStart = boundary;
            }

            AddTrimmedSegment(source, partStart, sentence.End, result);
        }

        return result;
    }

    private static void AddTrimmedSegment(
        string source,
        int start,
        int end,
        List<TtsTextSegment> segments)
    {
        while (start < end && char.IsWhiteSpace(source[start])) start++;
        while (end > start && char.IsWhiteSpace(source[end - 1])) end--;
        if (end <= start) return;

        segments.Add(new TtsTextSegment(
            start,
            end - start,
            source.Substring(start, end - start)));
    }

    private static int FindNextSentenceEnd(string text, int start)
    {
        for (var index = start; index < text.Length; index++)
        {
            if (IsParagraphBoundary(text, index))
            {
                var end = index + 1;
                while (end < text.Length && IsParagraphBoundary(text, end)) end++;
                return end;
            }

            if (!IsSentenceBoundary(text, index)) continue;

            var sentenceEnd = index + 1;
            // Keep repeated ellipses and closing quotation/bracket marks in
            // the same spoken unit. Otherwise the highlight would stop just
            // before the closing quote while the audio continues through it.
            while (sentenceEnd < text.Length
                   && (IsSentenceBoundary(text, sentenceEnd)
                       || IsClosingPunctuation(text[sentenceEnd])))
            {
                sentenceEnd++;
            }

            while (sentenceEnd < text.Length && IsParagraphBoundary(text, sentenceEnd))
                sentenceEnd++;
            return sentenceEnd;
        }

        return -1;
    }

    private static int FindLastBoundary(
        string text,
        int start,
        int limit,
        Func<string, int, bool> predicate)
    {
        var result = -1;
        for (var index = start; index < limit; index++)
        {
            if (!predicate(text, index)) continue;

            var end = index + 1;
            while (end < text.Length && IsClosingPunctuation(text[end])) end++;
            while (end < text.Length && IsParagraphBoundary(text, end)) end++;
            result = Math.Min(end, text.Length);
        }

        return result;
    }

    private static int FindSafeFallbackEnd(string text, int start, int limit)
    {
        if (limit >= text.Length) return text.Length;

        // Prefer whitespace or punctuation. This avoids breaking ordinary
        // English words, URLs and numbers when the CJK fallback is not needed.
        for (var index = limit; index > start; index--)
        {
            var previous = text[index - 1];
            if (char.IsWhiteSpace(previous)
                || IsSecondaryPunctuation(previous)
                || IsTertiaryPunctuation(previous))
            {
                return index;
            }

            if (!IsAsciiWord(previous)
                || (index < text.Length && !IsAsciiWord(text[index])))
            {
                return index;
            }
        }

        // A single unbroken token longer than the request limit cannot be
        // preserved forever. Keep progress guaranteed in that rare case.
        return limit;
    }

    private static bool IsParagraphBoundary(string text, int index)
        => text[index] is '\r' or '\n';

    private static bool IsSentenceBoundary(string text, int index)
    {
        var value = text[index];
        if (value is '。' or '！' or '!' or '？' or '?' or '…') return true;
        if (value != '.') return false;

        var previous = index > 0 ? text[index - 1] : '\0';
        var next = index + 1 < text.Length ? text[index + 1] : '\0';
        if (char.IsDigit(previous) && char.IsDigit(next)) return false;
        if (char.IsLetter(previous) && char.IsLetter(next)) return false;
        return true;
    }

    private static bool IsSecondaryBoundary(string text, int index)
        => IsSecondaryPunctuation(text[index]);

    private static bool IsTertiaryBoundary(string text, int index)
        => IsTertiaryPunctuation(text[index]);

    private static bool IsSecondaryPunctuation(char value)
        => value is '；' or ';' or '：' or ':';

    private static bool IsTertiaryPunctuation(char value)
        => value is '，' or ',' or '、';

    private static bool IsClosingPunctuation(char value)
        => value is '”' or '’' or '」' or '』' or '》' or '）' or ')' or
            ']' or '}' or '〉' or '〕' or '】' or '"' or '\'';

    private static bool IsAsciiWord(char value)
        => value is >= 'A' and <= 'Z'
            or >= 'a' and <= 'z'
            or >= '0' and <= '9'
            or '_';

    private static bool TryReadMarkup(
        string source,
        int start,
        out int end,
        out string tagName)
    {
        end = -1;
        tagName = string.Empty;
        if (start + 1 >= source.Length) return false;

        var candidateEnd = source.IndexOf('>', start + 1);
        if (candidateEnd < 0 || candidateEnd - start > 4096) return false;

        var content = source.AsSpan(start + 1, candidateEnd - start - 1).Trim();
        if (content.IsEmpty) return false;
        if (content[0] is '!' or '?')
        {
            end = candidateEnd;
            return true;
        }
        if (content[0] == '/') content = content[1..].TrimStart();
        if (content.IsEmpty || !char.IsLetter(content[0])) return false;

        var nameLength = 0;
        while (nameLength < content.Length
               && (char.IsLetterOrDigit(content[nameLength])
                   || content[nameLength] is ':' or '-' or '_'))
        {
            nameLength++;
        }

        if (nameLength == 0) return false;
        tagName = content[..nameLength].ToString();
        end = candidateEnd;
        return true;
    }

    private static bool TryReadEntity(
        string source,
        int start,
        out int end,
        out string decoded)
    {
        end = -1;
        decoded = string.Empty;
        var candidateEnd = source.IndexOf(';', start + 1);
        if (candidateEnd < 0 || candidateEnd - start > 32) return false;

        var entity = source.Substring(start, candidateEnd - start + 1);
        var value = WebUtility.HtmlDecode(entity);
        if (string.Equals(entity, value, StringComparison.Ordinal)) return false;

        end = candidateEnd;
        decoded = value;
        return true;
    }
}
