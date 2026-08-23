namespace Kkindle.Core;

public sealed class ReaderAnnotation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BookId { get; set; }
    public Guid BookFileId { get; set; }
    public string ChapterPath { get; set; } = string.Empty;
    public string? Fragment { get; set; }
    public int StartOffset { get; set; }
    public int EndOffset { get; set; }
    public string SelectedText { get; set; } = string.Empty;
    public string Prefix { get; set; } = string.Empty;
    public string Suffix { get; set; } = string.Empty;
    public string Color { get; set; } = "#000000";
    public string UnderlineStyle { get; set; } = "solid";
    public string Note { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public string DisplayQuote => string.IsNullOrWhiteSpace(SelectedText) ? "未命名批注" : SelectedText;
    public string DisplayNote => string.IsNullOrWhiteSpace(Note) ? "仅划线" : Note;
}

public sealed record BookContentChunk(
    long Id,
    Guid BookId,
    Guid BookFileId,
    string SourceHash,
    int ChapterIndex,
    int ChunkIndex,
    string ChapterTitle,
    string ChapterPath,
    int StartOffset,
    int EndOffset,
    string Content,
    double Rank = 0);

public sealed record BookContentChunkDraft(
    int ChapterIndex,
    int ChunkIndex,
    string ChapterTitle,
    string ChapterPath,
    int StartOffset,
    int EndOffset,
    string Content);

// ------------------------------------------------------------------
// Reader persistence: progress restore, bookmarks, per-book layout
// settings and cumulative reading stats. All rows are keyed by the
// BookFile so every format of the same book keeps its own position.
// ------------------------------------------------------------------

public sealed record ReaderProgressRow(
    Guid BookId,
    Guid BookFileId,
    string ChapterPath,
    string? Fragment,
    int ChapterIndex,
    int ScrollPosition,
    double ProgressPercent,
    int FlowMode,
    DateTimeOffset UpdatedAt);

public sealed class ReaderBookmark
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BookId { get; set; }
    public Guid BookFileId { get; set; }
    public string ChapterPath { get; set; } = string.Empty;
    public string? Fragment { get; set; }
    public int ChapterIndex { get; set; }
    public int? ScrollPosition { get; set; }
    public int FlowMode { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Quote { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? "未命名书签" : Title;
    public string DisplayQuote => string.IsNullOrWhiteSpace(Quote) ? "当前阅读位置" : Quote;
    public string DisplayTime => CreatedAt.ToLocalTime().ToString("MM-dd HH:mm");
}

public static class ReaderFontDefaults
{
    public const string DefaultFamily = "京华老宋体";
    public const string BundledFamily = "KingHwaOldSong";
    public const string BundledFontFileName = "KingHwaOldSong-v3.0.ttf";
}

public sealed record ReaderLayoutSettings(
    double FontScale = 1.2,
    double LineHeight = 1.8,
    double MaxWidth = 1200,
    double BodyPadding = 24,
    string FontFamily = ReaderFontDefaults.DefaultFamily,
    int FlowMode = 1,
    bool VerticalWriting = false,
    bool TwoPageMode = false);

// ------------------------------------------------------------------
// Layout settings safety net. Persisted per-book settings can carry
// stale or corrupted values (NaN, out-of-range widths, invalid flow
// modes) from older builds. Clamp every field to the supported ranges
// so a bad row can never force an EPUB into an unreadable layout;
// the rest of the user's reading data is never touched.
// ------------------------------------------------------------------

public static class ReaderLayoutDefaults
{
    public const double DefaultFontScale = 1.2;
    public const double DefaultLineHeight = 1.8;
    public const double DefaultMaxWidth = 1200;
    public const double DefaultBodyPadding = 24;
    public const double MinFontScale = 0.8;
    public const double MaxFontScale = 1.8;
    public const double MinLineHeight = 1.3;
    public const double MaxLineHeight = 2.6;
    public const double MinMaxWidth = 480;
    // 3840 covers a full-width column on an unscaled 4K panel; the reader
    // clamps the effective width to the viewport anyway, so larger monitors
    // simply get more headroom.
    public const double MaxMaxWidth = 3840;
    public const double MinBodyPadding = 24;
    public const double MaxBodyPadding = 160;

    public static ReaderLayoutSettings Normalize(ReaderLayoutSettings settings)
    {
        var fontScale = double.IsFinite(settings.FontScale)
            ? Math.Clamp(settings.FontScale, MinFontScale, MaxFontScale)
            : DefaultFontScale;
        var lineHeight = double.IsFinite(settings.LineHeight)
            ? Math.Clamp(settings.LineHeight, MinLineHeight, MaxLineHeight)
            : DefaultLineHeight;
        var maxWidth = double.IsFinite(settings.MaxWidth)
            ? Math.Clamp(settings.MaxWidth, MinMaxWidth, MaxMaxWidth)
            : DefaultMaxWidth;
        var bodyPadding = double.IsFinite(settings.BodyPadding)
            ? Math.Clamp(settings.BodyPadding, MinBodyPadding, MaxBodyPadding)
            : DefaultBodyPadding;
        var fontFamily = string.IsNullOrWhiteSpace(settings.FontFamily)
            ? ReaderFontDefaults.DefaultFamily
            : settings.FontFamily.Trim();
        return settings with
        {
            FontScale = fontScale,
            LineHeight = lineHeight,
            MaxWidth = maxWidth,
            BodyPadding = bodyPadding,
            FontFamily = fontFamily,
            FlowMode = settings.FlowMode == 1 ? 1 : 0,
            VerticalWriting = settings.VerticalWriting,
            TwoPageMode = settings.TwoPageMode
        };
    }
}

public sealed class ReaderReadingStats
{
    public Guid BookId { get; set; }
    public Guid BookFileId { get; set; }
    public long CumulativeSeconds { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public double ProgressPercent { get; set; }
    public int CompletedChapters { get; set; }
    public int TotalChapters { get; set; }

    public string DurationLabel
    {
        get
        {
            var seconds = CumulativeSeconds;
            if (seconds < 60) return $"{seconds} 秒";
            if (seconds < 3600) return $"{seconds / 60} 分钟";
            return $"{seconds / 3600.0:0.0} 小时";
        }
    }
}

public static class ReaderFormatting
{
    public static string FormatPercent(double percent) =>
        $"{Math.Clamp((int)Math.Round(percent), 0, 100)}%";
}

// ------------------------------------------------------------------
// Chapter-navigation intent. Every real chapter switch funnels through
// NavigateReaderSourceAsync with an explicit intent so the reader knows
// WHY it navigated:
//   - None      open-book breakpoint restore, prev/next chapter,
//               continuous scroll edge transitions.
//   - Toc       a TOC click. Plain chapter entries must start at the
//               chapter's first line; entries that explicitly carry a
//               fragment anchor jump to that anchor.
//   - Progress  progress-slider jump: chapter first line.
//   - Link / Footnote / Bookmark / Annotation / Search / AiSource:
//               explicit named locations that scroll to their own target.
// An explicit user target must win over any automatic breakpoint restore,
// and a navigation must never inherit the stale pending location of the
// navigation it superseded (rapid TOC clicks, or a TOC click right after
// a search/bookmark/annotation jump).
// ------------------------------------------------------------------
public enum ReaderNavigationIntent
{
    None = 0,
    Toc = 1,
    Progress = 2,
    Bookmark = 3,
    Annotation = 4,
    Search = 5,
    AiSource = 6,
    Link = 7,
    Footnote = 8
}

public static class ReaderNavigationLocationPolicy
{
    // WebView2 treats a different fragment in the same XHTML as an in-page
    // navigation. Such jumps may not raise NavigationCompleted, so callers
    // compare document identity without the fragment and position directly.
    public static bool TargetsSameDocument(Uri? current, Uri? target)
    {
        if (current is null || target is null || !current.IsAbsoluteUri || !target.IsAbsoluteUri)
            return false;

        return Uri.Compare(
            current,
            target,
            UriComponents.SchemeAndServer | UriComponents.Path | UriComponents.Query,
            UriFormat.SafeUnescaped,
            StringComparison.OrdinalIgnoreCase) == 0;
    }

    // A plain chapter navigation (TOC without an explicit anchor, or the
    // progress slider) always starts at the chapter's first line: the actual
    // scroll container goes back to the content-box start (scrollTop = 0 in
    // scroll mode; first column boundary + scrollTop = 0 in pagination).
    public static bool GoesToChapterStart(ReaderNavigationIntent intent) =>
        intent is ReaderNavigationIntent.Toc or ReaderNavigationIntent.Progress;

    // Automatic breakpoint restore is only used when the user did not ask
    // for a specific target (open-book restore, plain chapter switches).
    public static bool UsesRestorePosition(ReaderNavigationIntent intent) =>
        intent == ReaderNavigationIntent.None;

    public static bool KeepsChunkOffset(ReaderNavigationIntent intent) =>
        intent is ReaderNavigationIntent.Search or ReaderNavigationIntent.AiSource;

    public static bool KeepsBookmarkQuote(ReaderNavigationIntent intent) =>
        intent == ReaderNavigationIntent.Bookmark;

    public static bool KeepsAnnotationScroll(ReaderNavigationIntent intent) =>
        intent == ReaderNavigationIntent.Annotation;

    public static bool KeepsRestorePosition(ReaderNavigationIntent intent) =>
        intent == ReaderNavigationIntent.None;

    // A TOC entry that explicitly carries a fragment anchor goes to that
    // anchor; a plain chapter entry starts at the first line. This keeps
    // genuine heading anchors working while ordinary chapter entries always
    // open at the chapter top. A trailing bare '#' is not an anchor (.NET
    // Uri.Fragment reports "#" for it, but the EPUB navigation parser never
    // emits such targets — it only appends a fragment when the anchor text is
    // non-empty — so it must fall back to the plain chapter entry).
    public static bool TocTargetHasAnchor(Uri target) =>
        target?.Fragment is { Length: > 0 } fragment
        && !string.Equals(fragment, "#", StringComparison.Ordinal);

    public static string TocAnchorId(Uri target)
    {
        var fragment = target?.Fragment.TrimStart('#') ?? string.Empty;
        return string.Equals(fragment, "#", StringComparison.Ordinal) ? string.Empty : fragment;
    }

    // Chapter-start normalization (drop leading blank blocks, zero the first
    // element's top margin) may only run when the navigation is ABOUT to show
    // the chapter's first line:
    //   - a plain TOC entry (no explicit anchor),
    //   - a progress-slider jump,
    //   - a plain chapter switch (None) with no breakpoint restore pending.
    // It must never run for fragment anchors, bookmarks, annotations, search
    // or AI-source targets: those carry their own precise scroll target and
    // their offset math depends on the untouched DOM.
    public static bool ShouldNormalizeChapterStart(ReaderNavigationIntent intent, Uri? target, bool hasPendingRestorePosition) =>
        intent switch
        {
            ReaderNavigationIntent.Toc => target is not null && !TocTargetHasAnchor(target),
            ReaderNavigationIntent.Progress => true,
            ReaderNavigationIntent.None => !hasPendingRestorePosition,
            ReaderNavigationIntent.Link => target is not null && !TocTargetHasAnchor(target),
            _ => false
        };
}
