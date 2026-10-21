namespace Kkindle.Core;

public enum BackgroundTaskKind
{
    Import,
    FormatConversion,
    BookDownload,
    KindleSend,
    BookTranslation,
    Pinyin,
    Other
}

public enum BackgroundTaskStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Canceled,
    Interrupted
}

/// <summary>
/// A safe, UI-facing snapshot of a tracked operation. It contains no file paths,
/// credentials, or executable retry data.
/// </summary>
public sealed record BackgroundTaskSnapshot(
    Guid Id,
    string Title,
    BackgroundTaskKind Kind,
    BackgroundTaskStatus Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string? Stage,
    int? ProgressCurrent,
    int? ProgressTotal,
    string? Error,
    bool CanCancel,
    bool CanRetry,
    bool IsFromPreviousRun,
    IReadOnlyList<string>? BookTitles = null);

public sealed class BackgroundTaskChangedEventArgs(Guid taskId) : EventArgs
{
    public Guid TaskId { get; } = taskId;
}

/// <summary>Reports progress for one task. Progress values are optional and clamped by the task center.</summary>
public sealed class BackgroundTaskContext(Action<string?, int?, int?> report, Action<IReadOnlyList<string>>? reportBooks = null)
{
    public void ReportBooks(IReadOnlyList<string> titles) => reportBooks?.Invoke(titles);

    public void ReportProgress(string? stage, int? current = null, int? total = null) =>
        report(stage, current, total);
}

public delegate Task BackgroundTaskOperation(BackgroundTaskContext context, CancellationToken cancellationToken);

/// <summary>A task identity plus its in-session completion signal.</summary>
public sealed record BackgroundTaskHandle(Guid Id, Task Completion);

public enum TxtTextEncoding
{
    Auto,
    Utf8,
    Utf16LittleEndian,
    Utf16BigEndian,
    Gb18030
}

public enum TxtHeadingRulePreset
{
    Automatic,
    ChineseNumbered,
    EnglishChapter,
    SpecialHeadings,
    NoHeadings,
    CustomRegex
}

public sealed record TxtImportRules
{
    public static TxtImportRules Default { get; } = new();

    public TxtTextEncoding Encoding { get; init; } = TxtTextEncoding.Auto;
    public TxtHeadingRulePreset HeadingPreset { get; init; } = TxtHeadingRulePreset.Automatic;
    public string? CustomHeadingPattern { get; init; }
    public bool RecognizeVolumes { get; init; } = true;
    public IReadOnlyList<TxtChapterCorrection> Corrections { get; init; } = Array.Empty<TxtChapterCorrection>();
}

/// <summary>
/// A 1-based source line correction. IsHeading=null keeps the selected rule's result;
/// true adds/keeps a heading and false removes it. Title and IsVolume can also be
/// applied to a rule-detected heading.
/// </summary>
public sealed record TxtChapterCorrection(
    int LineNumber,
    bool? IsHeading = null,
    string? Title = null,
    bool? IsVolume = null);

public sealed record TxtChapterPreviewItem(
    int Number,
    int StartLine,
    string Title,
    bool IsVolume,
    int CharacterCount);

/// <summary>Only the requested page of chapter summaries is returned to keep the UI bounded for large books.</summary>
public sealed record TxtImportPreview(
    string Title,
    TxtTextEncoding DetectedEncoding,
    int PageIndex,
    int PageSize,
    int ChapterCount,
    IReadOnlyList<TxtChapterPreviewItem> Chapters)
{
    public bool HasPreviousPage => PageIndex > 0;
    public bool HasNextPage => (long)(PageIndex + 1) * PageSize < ChapterCount;
}
