using Kkindle.Core;

namespace Kkindle.Infrastructure;

// These types are deliberately separate from the live SQLite rows. Relative
// paths and device-local secrets never cross the sync boundary; book files are
// identified by their content hash and reader rows refer to portable IDs.
internal sealed class S3SyncSnapshot
{
    public int Version { get; set; } = 1;
    public string DeviceId { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public S3SyncSettingsSnapshot? Settings { get; set; }
    public List<S3SyncBook> Books { get; set; } = [];
    public List<S3SyncBookFile> Files { get; set; } = [];
    public List<S3SyncCollection> Collections { get; set; } = [];
    public List<S3SyncCollectionItem> CollectionItems { get; set; } = [];
    public List<S3SyncAnnotation> Annotations { get; set; } = [];
    public List<S3SyncProgress> Progress { get; set; } = [];
    public List<S3SyncBookmark> Bookmarks { get; set; } = [];
    public List<S3SyncLayout> Layouts { get; set; } = [];
    public List<S3SyncReadingStats> ReadingStats { get; set; } = [];
    public List<S3SyncTombstone> Tombstones { get; set; } = [];
}

internal sealed class S3SyncBook
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Authors { get; set; } = string.Empty;
    public string? Series { get; set; }
    public double? SeriesIndex { get; set; }
    public string? Description { get; set; }
    public string? Publisher { get; set; }
    public string? PublishDate { get; set; }
    public string? Isbn { get; set; }
    public string? PageCount { get; set; }
    public string? Binding { get; set; }
    public double? DoubanRating { get; set; }
    public int? DoubanRatingCount { get; set; }
    public string Tags { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public bool IsFavorite { get; set; }
    public LibraryReadingStatus ReadingStatus { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? CoverHash { get; set; }
    public string? CoverFileName { get; set; }
}

internal sealed class S3SyncBookFile
{
    public Guid Id { get; set; }
    public Guid BookId { get; set; }
    public string Format { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long Size { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public DateTimeOffset ModifiedAt { get; set; }
}

internal sealed class S3SyncCollection
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class S3SyncCollectionItem
{
    public Guid CollectionId { get; set; }
    public Guid BookId { get; set; }
    public DateTimeOffset AddedAt { get; set; }
}

internal sealed class S3SyncAnnotation
{
    public Guid Id { get; set; }
    public Guid BookId { get; set; }
    public Guid BookFileId { get; set; }
    public string ChapterPath { get; set; } = string.Empty;
    public string? Fragment { get; set; }
    public int StartOffset { get; set; }
    public int EndOffset { get; set; }
    public string SelectedText { get; set; } = string.Empty;
    public string Prefix { get; set; } = string.Empty;
    public string Suffix { get; set; } = string.Empty;
    public string Color { get; set; } = string.Empty;
    public string UnderlineStyle { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class S3SyncProgress
{
    public Guid BookId { get; set; }
    public Guid BookFileId { get; set; }
    public string ChapterPath { get; set; } = string.Empty;
    public string? Fragment { get; set; }
    public int ChapterIndex { get; set; }
    public int ScrollPosition { get; set; }
    public double ProgressPercent { get; set; }
    public int FlowMode { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class S3SyncBookmark
{
    public Guid Id { get; set; }
    public Guid BookId { get; set; }
    public Guid BookFileId { get; set; }
    public string ChapterPath { get; set; } = string.Empty;
    public string? Fragment { get; set; }
    public int ChapterIndex { get; set; }
    public int? ScrollPosition { get; set; }
    public int FlowMode { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Quote { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class S3SyncLayout
{
    public Guid BookId { get; set; }
    public Guid BookFileId { get; set; }
    public double FontScale { get; set; }
    public double LineHeight { get; set; }
    public double MaxWidth { get; set; }
    public double BodyPadding { get; set; }
    public string? FontFamily { get; set; }
    public int FlowMode { get; set; }
    public bool VerticalWriting { get; set; }
    public bool TwoPageMode { get; set; }
    public bool ParagraphIndent { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class S3SyncReadingStats
{
    public Guid BookId { get; set; }
    public Guid BookFileId { get; set; }
    public long CumulativeSeconds { get; set; }
    public double ProgressPercent { get; set; }
    public int CompletedChapters { get; set; }
    public int TotalChapters { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class S3SyncTombstone
{
    public string EntityType { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public DateTimeOffset DeletedAt { get; set; }
}

internal sealed class S3SyncSettingsSnapshot
{
    public DateTimeOffset UpdatedAt { get; set; }
    public S3SyncAppSettings App { get; set; } = new();
    public S3SyncAiSettings Ai { get; set; } = new();
    public S3SyncKindleEmailSettings KindleEmail { get; set; } = new();
    public S3SyncZLibrarySettings ZLibrary { get; set; } = new();
}

internal sealed class S3SyncAppSettings
{
    public string UiLanguage { get; set; } = string.Empty;
    public string PreferredOpenFormat { get; set; } = "epub";
    public bool AutoBackupEnabled { get; set; }
    public bool AutoGenerateEpubAndAzw3OnImport { get; set; }
    public bool CollectionsMutuallyExclusive { get; set; } = true;
    public int AutoBackupRetention { get; set; } = 5;
    public bool AiEnabled { get; set; } = true;
    public bool NetworkEnabled { get; set; } = true;
    public bool AutoUpdateCheckEnabled { get; set; } = true;
    public bool AutoDoubanMatchOnImport { get; set; }
    public bool CompareKindleLibraryEnabled { get; set; } = true;
    public bool GridGalleryDisplay { get; set; }
    public bool ReadingMaterialsCollapsedByDefault { get; set; } = true;
    public ReaderLayoutSettings DefaultReaderLayout { get; set; } = new();
}

internal sealed class S3SyncAiSettings
{
    public string Provider { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
}

internal sealed class S3SyncKindleEmailSettings
{
    public string KindleEmailAddress { get; set; } = string.Empty;
    public string SenderEmailAddress { get; set; } = string.Empty;
    public string SmtpHost { get; set; } = string.Empty;
    public int SmtpPort { get; set; } = 587;
    public string SmtpUsername { get; set; } = string.Empty;
    public bool EnableSsl { get; set; } = true;
}

internal sealed class S3SyncZLibrarySettings
{
    public string Email { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
}

internal sealed class S3SyncState
{
    public string DeviceId { get; set; } = string.Empty;
    public string StorageIdentity { get; set; } = string.Empty;
    public DateTimeOffset? LastSyncAt { get; set; }
    public S3SyncSnapshot? LastUploadedSnapshot { get; set; }
    public List<S3SyncTombstone> Tombstones { get; set; } = [];
}
