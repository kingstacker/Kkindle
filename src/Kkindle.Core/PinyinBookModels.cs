namespace Kkindle.Core;

public enum PinyinBookOutputStyle
{
    ToneMarked = 0,
    ToneNumber = 1,
    NoTone = 2
}

public sealed record PinyinBookOptions
{
    public PinyinBookOutputStyle OutputStyle { get; init; } = PinyinBookOutputStyle.ToneMarked;

    /// <summary>
    /// Let the book generator send only locally flagged polyphone candidates
    /// to the configured AI service for contextual review.
    /// </summary>
    public bool EnableAiReview { get; init; } = true;

    /// <summary>
    /// Maximum number of distinct review candidates in one AI request.
    /// </summary>
    public int AiReviewBatchSize { get; init; } = 24;

    public static PinyinBookOptions Normalize(PinyinBookOptions? options)
    {
        options ??= new PinyinBookOptions();
        return options with
        {
            OutputStyle = Enum.IsDefined(options.OutputStyle)
                ? options.OutputStyle
                : PinyinBookOutputStyle.ToneMarked,
            AiReviewBatchSize = Math.Clamp(options.AiReviewBatchSize, 4, 48)
        };
    }
}

public sealed record PinyinBookProgress(
    string Stage,
    string CurrentItem,
    int ProcessedPages,
    int TotalPages,
    int AnnotatedCharacters,
    int ReviewCandidates = 0,
    int ReviewedCandidates = 0,
    double? OverallPercentage = null,
    PinyinBookSegmentProgress? Segment = null,
    int ProcessedSegments = 0,
    int TotalSegments = 0)
{
    public double Percentage => OverallPercentage is { } overall
        ? Math.Clamp(overall, 0, 100)
        : TotalSegments > 0
            ? Math.Clamp(ProcessedSegments * 100d / TotalSegments, 0, 100)
            : TotalPages <= 0
            ? Stage.Contains("完成", StringComparison.Ordinal) ? 100 : 0
            : Math.Clamp(ProcessedPages * 100d / TotalPages, 0, 100);
}

public enum PinyinBookSegmentStatus
{
    None = 0,
    Processing,
    Completed,
    Failed,
    Canceled
}

public sealed record PinyinBookSegmentProgress(
    int Index,
    string EntryName,
    string OriginalText,
    string AnnotatedText,
    PinyinBookSegmentStatus Status,
    string ProcessFlow,
    string? ErrorMessage = null);

public enum PinyinBookResumeMode
{
    Restart = 0,
    Resume
}

public sealed record PinyinBookResumeInfo(
    PinyinBookOptions Options,
    int CompletedSegments,
    int FailedSegments,
    int TotalSegments,
    DateTimeOffset UpdatedAt)
{
    public int IncompleteSegments => Math.Max(TotalSegments - CompletedSegments, 0);
}

public sealed record PinyinBookResult(
    string SourcePath,
    string OutputPath,
    int ScannedPageCount,
    int AnnotatedPageCount,
    int AnnotatedCharacterCount,
    int ReviewCandidateCount = 0,
    int AiReviewedCandidateCount = 0,
    string? AiReviewError = null,
    int FailedSegmentCount = 0);

public static class PinyinBookPolicy
{
    public static bool IsGeneratedPinyinVersion(BookFile? file)
    {
        if (file is null) return false;
        var stem = Path.GetFileNameWithoutExtension(file.RelativePath);
        return stem.EndsWith("-拼音版", StringComparison.OrdinalIgnoreCase)
            || stem.EndsWith("_拼音版", StringComparison.OrdinalIgnoreCase);
    }
}
