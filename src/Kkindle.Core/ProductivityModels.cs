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

public sealed record LibraryPageResult(IReadOnlyList<Book> Books, int TotalCount);

public sealed record LibraryFilterOptions(
    IReadOnlyList<string> Authors,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> Formats,
    IReadOnlyList<string> Categories);

public sealed record LibraryCollectionSummary(
    BookCollection Collection,
    int BookCount,
    IReadOnlyList<string> CoverPaths);

public sealed record LibraryFileMatch(string RelativePath, string Sha256);

public sealed record LibraryBookMatch(
    Guid Id,
    string Title,
    string Authors,
    IReadOnlyList<LibraryFileMatch> Files);

public sealed record LibraryBookDisplayInfo(Guid Id, string Title, string? CoverPath);

public sealed record AppSettings
{
    public const string DefaultEmbeddingModelId = "BAAI/bge-small-zh-v1.5";
    public const string DefaultPinyinEngineId = PinyinBookEngineCatalog.DotNetG2PId;

    public string UiLanguage { get; init; } = UiText.DetectSystemLanguage();
    public AppTheme MainTheme { get; init; } = AppTheme.Classic;
    public bool OnboardingCompleted { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DefaultDeviceModel { get; init; }
    public string PreferredOpenFormat { get; init; } = "epub";
    public string CalibrePath { get; init; } = string.Empty;
    public bool AutoBackupEnabled { get; init; }
    public bool AutoGenerateEpubAndAzw3OnImport { get; init; }
    public bool CollectionsMutuallyExclusive { get; init; } = true;
    // The library surface is restored before the first window is shown so a
    // restart does not unexpectedly fall back to the grid view.
    public string LibraryViewMode { get; init; } = "Grid";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool AutoGenerateAzw3OnImport { get; init; }
    public int AutoBackupRetention { get; init; } = 5;
    public bool AiEnabled { get; init; } = true;
    public bool NetworkEnabled { get; init; } = true;
    public bool SendToKindleWebEnabled { get; init; } = true;
    public string EmbeddingModelId { get; init; } = DefaultEmbeddingModelId;
    public string PinyinEngineId { get; init; } = DefaultPinyinEngineId;
    public bool AutoUpdateCheckEnabled { get; init; } = true;
    public bool DevelopmentUpdateCheckEnabled { get; init; }

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
    public string? PendingUpdateReleaseNotesEnglish { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PendingUpdatePackagePath { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public DateTimeOffset? PendingUpdateDownloadedAt { get; init; }

    public bool AutoDoubanMatchOnImport { get; init; }
    public bool AutoConnectDevice { get; init; } = true;
    public bool CompareKindleLibraryEnabled { get; init; } = true;
    public bool GridGalleryDisplay { get; init; }
    public bool ShowSyncStatusIcon { get; init; } = true;
    public bool ShowLibraryPresenceIcon { get; init; } = true;
    public bool ReadingMaterialsCollapsedByDefault { get; init; } = true;
    public bool PinyinContextMenuEnabled { get; init; } = true;
    public bool PinyinLocalOnly { get; init; } = true;
    public BookTranslationSettings Translation { get; init; } = new();
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ReaderVerticalDebugBoxesEnabled { get; init; }
    public ReaderLayoutSettings DefaultReaderLayout { get; init; } = new();
    public ReaderAppearanceSettings ReaderAppearance { get; init; } = new();

    public static AppSettings Normalize(AppSettings? settings)
    {
        settings ??= new AppSettings();
        var preferred = (settings.PreferredOpenFormat ?? string.Empty).Trim().TrimStart('.').ToLowerInvariant();
        if (preferred is not ("epub" or "pdf" or "azw3" or "mobi")) preferred = "epub";
        var libraryViewMode = (settings.LibraryViewMode ?? string.Empty).Trim();
        if (!libraryViewMode.Equals("List", StringComparison.OrdinalIgnoreCase)
            && !libraryViewMode.Equals("Collections", StringComparison.OrdinalIgnoreCase))
            libraryViewMode = "Grid";
        else
            libraryViewMode = libraryViewMode.Equals("List", StringComparison.OrdinalIgnoreCase)
                ? "List"
                : "Collections";
        return settings with
        {
            UiLanguage = UiText.NormalizeLanguage(settings.UiLanguage),
            MainTheme = Enum.IsDefined(settings.MainTheme) ? settings.MainTheme : AppTheme.Classic,
            DefaultDeviceModel = string.IsNullOrWhiteSpace(settings.DefaultDeviceModel)
                ? null
                : settings.DefaultDeviceModel.Trim(),
            PreferredOpenFormat = preferred,
            LibraryViewMode = libraryViewMode,
            CalibrePath = (settings.CalibrePath ?? string.Empty).Trim(),
            EmbeddingModelId = string.IsNullOrWhiteSpace(settings.EmbeddingModelId)
                ? DefaultEmbeddingModelId
                : settings.EmbeddingModelId.Trim(),
            PinyinEngineId = PinyinBookEngineCatalog.NormalizeId(settings.PinyinEngineId),
            AutoGenerateEpubAndAzw3OnImport = settings.AutoGenerateEpubAndAzw3OnImport
                || settings.AutoGenerateAzw3OnImport,
            AutoGenerateAzw3OnImport = false,
            AutoBackupRetention = Math.Clamp(settings.AutoBackupRetention, 1, 30),
            DefaultReaderLayout = ReaderLayoutDefaults.Normalize(settings.DefaultReaderLayout ?? new ReaderLayoutSettings()),
            ReaderAppearance = ReaderAppearanceSettings.Normalize(settings.ReaderAppearance),
            Translation = BookTranslationSettings.Normalize(settings.Translation)
        };
    }
}

public sealed record ManagedFont(string Id, string DisplayName, string CssFamily, string RelativePath, DateTimeOffset ImportedAt);
public sealed record DictionaryDefinition(string Id, string Name, string RelativePath, int EntryCount, DateTimeOffset ImportedAt, bool Enabled = true);
public sealed record DictionaryEntry(string Term, string Definition, string DictionaryName);
public sealed record PdfPageText(int PageNumber, string Text);
public sealed record PdfSearchResult(int PageNumber, string Excerpt, int MatchIndex);

public enum PlatformDiagnosticStatus
{
    Ready,
    Warning,
    Unavailable
}

public enum PlatformDiagnosticRepairKind
{
    None,
    InstallCalibre,
    InstallTts
}

public sealed record PlatformDiagnostic(
    string Name,
    PlatformDiagnosticStatus Status,
    string Detail,
    PlatformDiagnosticRepairKind RepairKind = PlatformDiagnosticRepairKind.None);

public sealed record ReadingDashboard(
    int BooksStarted,
    int BooksFinished,
    long TotalSeconds,
    double AverageProgress,
    int BookmarkCount,
    int AnnotationCount,
    IReadOnlyList<ReadingDashboardBook> RecentBooks,
    IReadOnlyList<ReadingDashboardDay> DailyReading)
{
    public IReadOnlyList<ReadingDashboardBook> Books { get; init; } = RecentBooks;
    public IReadOnlyList<ReadingDashboardBook> MostReadBooks { get; init; } = RecentBooks;
}

public sealed record ReadingDashboardBook(Guid BookId, Guid BookFileId, double ProgressPercent, long CumulativeSeconds, DateTimeOffset UpdatedAt)
{
    public string Title { get; init; } = string.Empty;
    public bool IsInLibrary { get; init; }
}
public sealed record ReadingDashboardDay(DateOnly Date, long ActiveSeconds);

public enum ReadingMaterialSource { Local, Device, Kindle = Device }

public sealed record ReadingMaterialRecord(
    ReadingMaterialSource Source,
    string BookTitle,
    string Type,
    string Location,
    string Quote,
    string Note,
    DateTimeOffset? UpdatedAt,
    string? SourceDeviceId = null,
    string? SourceDeviceName = null)
{
    public string SourceLabel => Source == ReadingMaterialSource.Local ? UiText.Get("本地书籍")
        : string.IsNullOrWhiteSpace(SourceDeviceName) ? UiText.Get("设备") : SourceDeviceName;
}
