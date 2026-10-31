using System.Text;
using Kkindle.ApplicationServices.Tts;
using Kkindle.Layout;

namespace Kkindle.ApplicationServices;

/// <summary>Builds the readable speech stream without a UI surface.</summary>
public static class ReaderSpeechTextBuilder
{
    public static ReaderTtsTextSnapshot? Load(
        string chapterPath,
        bool paragraphIndent,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var content = new XhtmlChapterLoader(paragraphIndent).Load(chapterPath);
        cancellationToken.ThrowIfCancellationRequested();
        return Build(content, 0);
    }

    public static ReaderTtsTextSnapshot? Build(
        ChapterContent content,
        int speechStartOffset,
        ChapterLayout? layout = null)
    {
        if (content.BodyText.Length == 0) return null;

        var ranges = new List<(int Start, int End)>();
        foreach (var block in content.Blocks)
        {
            foreach (var item in block.Items)
            {
                // Ghost items (ruby phonetics), footnote references and
                // generated markers/images must not be spoken. Keep the
                // reference in BodyText so source offsets and footnote
                // interaction remain unchanged; only omit it from this TTS
                // snapshot.
                if (item.Ghost
                    || item.Kind == InlineKind.FootnoteMarker
                    || item.FootnoteHref is not null
                    || item.TextStart < 0
                    || item.Text.Length == 0)
                    continue;

                var itemStart = Math.Clamp(item.TextStart, 0, content.BodyText.Length);
                var end = Math.Clamp(
                    (long)item.TextStart + item.Text.Length,
                    (long)itemStart,
                    content.BodyText.Length);
                if (end > itemStart)
                    ranges.Add((itemStart, (int)end));
            }
        }

        ranges.Sort(static (left, right) =>
        {
            var result = left.Start.CompareTo(right.Start);
            return result != 0 ? result : left.End.CompareTo(right.End);
        });

        var merged = new List<(int Start, int End)>(ranges.Count);
        foreach (var range in ranges)
        {
            if (merged.Count > 0 && range.Start <= merged[^1].End)
            {
                var previous = merged[^1];
                merged[^1] = (previous.Start, Math.Max(previous.End, range.End));
            }
            else
            {
                merged.Add(range);
            }
        }

        if (merged.Count == 0) return null;

        var text = new StringBuilder();
        var sourceOffsets = new List<int>();
        var previousEnd = -1;
        foreach (var range in merged)
        {
            if (previousEnd >= 0 && range.Start > previousEnd)
                AppendSpeechSeparator(
                    content.BodyText,
                    previousEnd,
                    range.Start,
                    text,
                    sourceOffsets);

            AppendSpeechSourceRange(
                content.BodyText,
                range.Start,
                range.End,
                text,
                sourceOffsets);
            previousEnd = Math.Max(previousEnd, range.End);
        }

        if (text.Length == 0) return null;

        var snapshot = new ReaderTtsTextSnapshot(
            text.ToString(),
            0,
            sourceOffsets,
            layout is null
                ? null
                : GetSpeechPageBreakOffsets(
                    text.ToString(),
                    sourceOffsets,
                    layout));
        var start = snapshot.GetTextOffsetAtOrAfterSource(
            Math.Max(0, speechStartOffset));
        return start == 0
            ? snapshot
            : new ReaderTtsTextSnapshot(
                snapshot.Text,
                start,
                sourceOffsets,
                snapshot.PageBreakOffsets);
    }

    private static IReadOnlyList<int> GetSpeechPageBreakOffsets(
        string speechText,
        IReadOnlyList<int> sourceOffsets,
        ChapterLayout layout)
    {
        if (layout.Pages.Count < 2 || speechText.Length == 0)
            return [];

        var snapshot = new ReaderTtsTextSnapshot(
            speechText,
            0,
            sourceOffsets);
        var pageBreaks = new List<int>();
        for (var pageIndex = 1; pageIndex < layout.Pages.Count; pageIndex++)
        {
            var sourceOffset = layout.Pages[pageIndex].TextStartOffset;
            if (sourceOffset < 0) continue;

            var textOffset = snapshot.GetTextOffsetAtOrAfterSource(sourceOffset);
            if (textOffset > 0 && textOffset < speechText.Length)
                pageBreaks.Add(textOffset);
        }

        return pageBreaks;
    }

    private static void AppendSpeechSourceRange(
        string source,
        int start,
        int end,
        StringBuilder text,
        List<int> sourceOffsets)
    {
        for (var offset = start; offset < end; offset++)
        {
            text.Append(source[offset]);
            sourceOffsets.Add(offset);
        }
    }

    private static void AppendSpeechSeparator(
        string source,
        int start,
        int end,
        StringBuilder text,
        List<int> sourceOffsets)
    {
        var firstWhitespace = -1;
        var hasLineBreak = false;
        for (var offset = start; offset < end; offset++)
        {
            if (!char.IsWhiteSpace(source[offset]))
            {
                continue;
            }

            firstWhitespace = firstWhitespace < 0 ? offset : firstWhitespace;
            hasLineBreak |= source[offset] is '\r' or '\n';
        }

        if (firstWhitespace < 0)
        {
            return;
        }

        text.Append(hasLineBreak ? '\n' : ' ');
        sourceOffsets.Add(firstWhitespace);
    }

}
