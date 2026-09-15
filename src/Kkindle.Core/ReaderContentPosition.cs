namespace Kkindle.Core;

/// <summary>
/// A location in one EPUB chapter, independent of the current pagination.
/// Text offsets use the original body-text stream. ImageIndex is the zero-based
/// ordinal of a rendered image in document order, including repeated images;
/// it never contains a device-local cache path. PageIndex is a last resort.
/// </summary>
public sealed record ReaderContentPosition
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;
    public int TextOffset { get; init; } = -1;
    public int? ImageIndex { get; init; }
    public int PageIndex { get; init; }
    public double RunY { get; init; }
    public double SliceFraction { get; init; }

    public static ReaderContentPosition? Validate(ReaderContentPosition? position) =>
        position is { Version: CurrentVersion, TextOffset: >= -1, PageIndex: >= 0 }
        && position.ImageIndex is not < 0
        && double.IsFinite(position.RunY)
        && double.IsFinite(position.SliceFraction)
        && position.SliceFraction is >= 0 and <= 1
            ? position : null;
}
