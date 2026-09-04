namespace Kkindle;

/// <summary>
/// The text stream used by the native reader and a UTF-16 mapping back to the
/// chapter's original BodyText. Native layout can omit ruby annotations,
/// footnote definitions and collapsed source whitespace.
/// </summary>
public sealed class ReaderTtsTextSnapshot
{
    private readonly int[] _sourceOffsets;

    public ReaderTtsTextSnapshot(
        string text,
        int startOffset,
        IReadOnlyList<int> sourceOffsets,
        IReadOnlyList<int>? pageBreakOffsets = null)
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
        StartOffset = Math.Clamp(startOffset, 0, text.Length);
        PageBreakOffsets = NormalizePageBreakOffsets(pageBreakOffsets, text.Length);
    }

    public string Text { get; }

    public int StartOffset { get; }

    /// <summary>
    /// Text offsets where the composed reader moves to the next page. They are
    /// kept in the same coordinate space as <see cref="Text"/> so TTS can split
    /// a spoken sentence without losing the source-text mapping.
    /// </summary>
    public IReadOnlyList<int> PageBreakOffsets { get; }

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
            var sourceOffset = _sourceOffsets[index];
            sourceStart = Math.Min(sourceStart, sourceOffset);
            sourceEnd = Math.Max(sourceEnd, sourceOffset + 1);
        }

        return sourceStart == int.MaxValue || sourceEnd <= sourceStart
            ? (0, 0)
            : (sourceStart, sourceEnd - sourceStart);
    }

    public int GetTextOffsetAtOrAfterSource(int sourceOffset)
    {
        var low = 0;
        var high = _sourceOffsets.Length;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (_sourceOffsets[middle] < sourceOffset)
                low = middle + 1;
            else
                high = middle;
        }

        return low;
    }

    private static IReadOnlyList<int> NormalizePageBreakOffsets(
        IReadOnlyList<int>? offsets,
        int textLength)
    {
        if (offsets is null || offsets.Count == 0) return [];

        return offsets
            .Where(offset => offset > 0 && offset < textLength)
            .Distinct()
            .OrderBy(offset => offset)
            .ToArray();
    }
}

/// <summary>
/// Reader-specific callbacks consumed by <see cref="TtsService"/>.
/// </summary>
public sealed record ReaderTtsDocument(
    string Key,
    string Text,
    int StartOffset,
    Func<int, int, Task> Highlight,
    Action ClearHighlight,
    Func<int, int, (int Start, int Length)>? MapHighlight = null,
    string? BookKey = null,
    string? ChapterKey = null,
    /// <summary>
    /// Plain-text offsets at which a composed reader page begins. They are
    /// retained for reader synchronization; TTS must not split a sentence at
    /// these visual boundaries.
    /// </summary>
    IReadOnlyList<int>? PageBreakOffsets = null);
