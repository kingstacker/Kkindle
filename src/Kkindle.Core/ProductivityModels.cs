using System.Text.Json.Serialization;

namespace Kkindle.Core;

public enum LibraryReadingStatus { Unread = 0, Reading = 1, Finished = 2 }

public enum LibrarySortMode
{
    UpdatedDescending = 0,
    TitleAscending = 1,
    AuthorAscending = 2,
    CreatedDescending = 3,
    ProgressDescending = 4
}

public sealed record AppSettings
{
    public string UiLanguage { get; init; } = UiText.DetectSystemLanguage();
    public bool OnboardingCompleted { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DefaultDeviceModel { get; init; }
    public string PreferredOpenFormat { get; init; } = "epub";
    public string CalibrePath { get; init; } = string.Empty;
    public bool AutoBackupEnabled { get; init; }
    public bool AutoGenerateEpubAndAzw3OnImport { get; init; }
    public bool CollectionsMutuallyExclusive { get; init; } = true;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool AutoGenerateAzw3OnImport { get; init; }
    public int AutoBackupRetention { get; init; } = 5;
    public bool AiEnabled { get; init; } = true;
    public bool NetworkEnabled { get; init; } = true;
    public bool AutoUpdateCheckEnabled { get; init; } = true;

    // Update checks run at most once per calendar day; the timestamp and the
    // discovered update summary persist so the title-bar badge survives restarts
    // without extra network calls.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public DateTimeOffset? LastAutoUpdateCheckAt { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PendingUpdateVersion { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PendingUpdateReleaseNotes { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PendingUpdatePackagePath { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public DateTimeOffset? PendingUpdateDownloadedAt { get; init; }

    public bool AutoDoubanMatchOnImport { get; init; }
    public bool AutoConnectDevice { get; init; } = true;
    public bool CompareKindleLibraryEnabled { get; init; } = true;
    public bool GridGalleryDisplay { get; init; }
    public bool ReadingMaterialsCollapsedByDefault { get; init; } = true;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ReaderVerticalDebugBoxesEnabled { get; init; }
    public ReaderLayoutSettings DefaultReaderLayout { get; init; } = new();

    public static AppSettings Normalize(AppSettings? settings)
    {
        settings ??= new AppSettings();
        var preferred = (settings.PreferredOpenFormat ?? string.Empty).Trim().TrimStart('.').ToLowerInvariant();
        if (preferred is not ("epub" or "pdf" or "azw3" or "mobi")) preferred = "epub";
        return settings with
        {
            UiLanguage = UiText.NormalizeLanguage(settings.UiLanguage),
            DefaultDeviceModel = string.IsNullOrWhiteSpace(settings.DefaultDeviceModel)
                ? null
                : settings.DefaultDeviceModel.Trim(),
            PreferredOpenFormat = preferred,
            CalibrePath = (settings.CalibrePath ?? string.Empty).Trim(),
            AutoGenerateEpubAndAzw3OnImport = settings.AutoGenerateEpubAndAzw3OnImport
                || settings.AutoGenerateAzw3OnImport,
            AutoGenerateAzw3OnImport = false,
            AutoBackupRetention = Math.Clamp(settings.AutoBackupRetention, 1, 30),
            DefaultReaderLayout = ReaderLayoutDefaults.Normalize(settings.DefaultReaderLayout ?? new ReaderLayoutSettings())
        };
    }
}

public sealed record ManagedFont(string Id, string DisplayName, string CssFamily, string RelativePath, DateTimeOffset ImportedAt);
public sealed record DictionaryDefinition(string Id, string Name, string RelativePath, int EntryCount, DateTimeOffset ImportedAt, bool Enabled = true);
public sealed record DictionaryEntry(string Term, string Definition, string DictionaryName);
public sealed record PdfPageText(int PageNumber, string Text);
public sealed record PdfSearchResult(int PageNumber, string Excerpt, int MatchIndex);

public sealed record ReadingDashboard(
    int BooksStarted,
    int BooksFinished,
    long TotalSeconds,
    double AverageProgress,
    int BookmarkCount,
    int AnnotationCount,
    IReadOnlyList<ReadingDashboardBook> RecentBooks,
    IReadOnlyList<ReadingDashboardDay> DailyReading);

public sealed record ReadingDashboardBook(Guid BookId, Guid BookFileId, double ProgressPercent, long CumulativeSeconds, DateTimeOffset UpdatedAt);
public sealed record ReadingDashboardDay(DateOnly Date, long ActiveSeconds);

public enum ReadingMaterialSource { Local, Kindle }

public sealed record ReadingMaterialRecord(
    ReadingMaterialSource Source,
    string BookTitle,
    string Type,
    string Location,
    string Quote,
    string Note,
    DateTimeOffset? UpdatedAt);
