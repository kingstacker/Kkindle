using System.Text;

namespace Kkindle.Core;

public sealed class Book
{
    public Guid Id { get; set; }
    public string Title { get; set; } = "未命名书籍";
    public string Authors { get; set; } = "未知作者";
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
    public string? CoverPath { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<BookFile> Files { get; set; } = [];
    public List<Guid> CollectionIds { get; set; } = [];

    public string FormatSummary => Files.Count == 0
        ? string.Empty
        : string.Join(" · ", Files.Select(x => x.Format.ToUpperInvariant()).Distinct());

    public string ProgressLabel => Files.Count == 0
        ? string.Empty
        : UiText.Get("{0}  ·  {1} 个文件", FormatSummary, Files.Count);
}

public sealed class BookCollection
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "新收藏夹";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class BookFile
{
    public Guid Id { get; set; }
    public Guid BookId { get; set; }
    public string Format { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public long Size { get; set; }
    public string Sha256 { get; set; } = string.Empty;
}

public sealed class KindleDevice
{
    public string RootPath { get; init; } = string.Empty;
    public string VolumeSerial { get; init; } = string.Empty;
    public string Name { get; init; } = "Kindle";
    public long TotalBytes { get; init; }
    public long FreeBytes { get; init; }
    public bool IsReady { get; init; }
    public KindleTransport Transport { get; init; } = KindleTransport.MassStorage;

    public string Identity => string.IsNullOrWhiteSpace(VolumeSerial)
        ? RootPath.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
        : VolumeSerial.Trim();
    public string CapacityLabel => UiText.Get("{0} 可用 / {1}", FormatBytes(FreeBytes), FormatBytes(TotalBytes));
    public string ConnectionLabel => Transport == KindleTransport.Wpd ? "MTP" : UiText.Get("USB 磁盘");

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
            return $"{bytes / 1024d / 1024 / 1024:0.0} GB";
        return $"{bytes / 1024d / 1024:0} MB";
    }
}

public enum KindleTransport
{
    MassStorage,
    Wpd
}

public sealed class KindleBook
{
    public string RelativePath { get; set; } = string.Empty;
    public string FileName => Path.GetFileName(RelativePath);
    public string Title { get; set; } = "未命名书籍";
    public string Authors { get; set; } = "未知作者";
    public string Format { get; set; } = string.Empty;
    public long Size { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public string? CoverPath { get; set; }
    public DateTimeOffset? ModifiedAt { get; set; }
    public bool IsManagedByKkindle { get; set; }
    public string SizeLabel => Size >= 1024L * 1024
        ? $"{Size / 1024d / 1024:0.0} MB"
        : $"{Size / 1024d:0} KB";
}

public enum KindleScanStage
{
    Enumerated,
    Enriched
}

public sealed record KindleScanProgress(
    KindleScanStage Stage,
    IReadOnlyList<KindleBook> Books,
    IReadOnlyList<string> RemovedPaths,
    int Processed,
    int Total);

public enum BookLibraryPresence
{
    ComputerOnly,
    KindleOnly,
    Both
}

public sealed record BookLibraryComparisonResult(
    IReadOnlySet<Guid> BooksOnKindle,
    IReadOnlySet<string> KindleBooksOnComputer);

public static class BookLibraryComparer
{
    public static BookLibraryComparisonResult Compare(
        IEnumerable<Book> computerBooks,
        IEnumerable<KindleBook> kindleBooks)
    {
        ArgumentNullException.ThrowIfNull(computerBooks);
        ArgumentNullException.ThrowIfNull(kindleBooks);

        var localBooks = computerBooks.ToArray();
        var deviceBooks = kindleBooks.ToArray();
        var localHashes = BuildLookup(
            localBooks.SelectMany(book => book.Files
                .Where(file => !string.IsNullOrWhiteSpace(file.Sha256))
                .Select(file => (Key: NormalizeHash(file.Sha256), Book: book))));
        var localMetadata = BuildLookup(localBooks
            .Select(book => (Key: CreateMetadataKey(book.Title, book.Authors), Book: book))
            .Where(item => item.Key is not null)
            .Select(item => (item.Key!, item.Book)));
        var localTitles = BuildLookup(localBooks
            .Select(book => (Key: CreateTitleKey(book.Title), Book: book))
            .Where(item => item.Key is not null)
            .Select(item => (item.Key!, item.Book)));
        var deviceTitleCounts = deviceBooks
            .Select(book => CreateTitleKey(book.Title))
            .Where(key => key is not null)
            .GroupBy(key => key!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        var matchedLocalIds = new HashSet<Guid>();
        var matchedDevicePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kindleBook in deviceBooks)
        {
            var matches = new HashSet<Book>();
            var hash = NormalizeHash(kindleBook.Sha256);
            if (hash.Length > 0 && localHashes.TryGetValue(hash, out var hashMatches))
                matches.UnionWith(hashMatches);

            if (matches.Count == 0
                && CreateMetadataKey(kindleBook.Title, kindleBook.Authors) is { } metadataKey
                && localMetadata.TryGetValue(metadataKey, out var metadataMatches))
                matches.UnionWith(metadataMatches);

            if (matches.Count == 0
                && CreateTitleKey(kindleBook.Title) is { } titleKey
                && deviceTitleCounts.GetValueOrDefault(titleKey) == 1
                && localTitles.TryGetValue(titleKey, out var titleMatches)
                && titleMatches.Count == 1)
                matches.Add(titleMatches[0]);

            if (matches.Count == 0) continue;
            foreach (var match in matches) matchedLocalIds.Add(match.Id);
            matchedDevicePaths.Add(kindleBook.RelativePath);
        }

        return new BookLibraryComparisonResult(matchedLocalIds, matchedDevicePaths);
    }

    private static Dictionary<string, List<Book>> BuildLookup(IEnumerable<(string Key, Book Book)> entries)
    {
        var lookup = new Dictionary<string, List<Book>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, book) in entries)
        {
            if (!lookup.TryGetValue(key, out var books))
            {
                books = [];
                lookup[key] = books;
            }
            books.Add(book);
        }
        return lookup;
    }

    private static string NormalizeHash(string? hash) => (hash ?? string.Empty).Trim();

    private static string? CreateMetadataKey(string? title, string? authors)
    {
        var normalizedTitle = NormalizeMetadata(title);
        var normalizedAuthors = NormalizeAuthors(authors);
        if (normalizedTitle.Length == 0 || normalizedAuthors.Length == 0) return null;
        if (normalizedAuthors is "未知作者" or "UNKNOWN AUTHOR") return null;
        return $"{normalizedTitle}\n{normalizedAuthors}";
    }

    private static string? CreateTitleKey(string? title)
    {
        var normalizedTitle = NormalizeMetadata(title);
        if (normalizedTitle.Length == 0 || normalizedTitle is "未命名书籍" or "UNTITLED") return null;
        return normalizedTitle;
    }

    private static string NormalizeMetadata(string? value) => string.Concat(
        (value ?? string.Empty)
            .Normalize(NormalizationForm.FormKC)
            .Trim()
            .Where(character => !char.IsWhiteSpace(character)))
        .ToUpperInvariant();

    private static string NormalizeAuthors(string? authors) => string.Join("|",
        (authors ?? string.Empty)
            .Split(new[] { ',', '，', ';', '；', '&' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeMetadata)
            .Where(author => author.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase));
}

public enum KindleResourceKind
{
    Font,
    Dictionary
}

public sealed class KindleDeviceResource
{
    public KindleResourceKind Kind { get; set; }
    public string RelativePath { get; set; } = string.Empty;
    public string FileName => Path.GetFileName(RelativePath);
    public string Format => Path.GetExtension(RelativePath).TrimStart('.').ToUpperInvariant();
    public long Size { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public DateTimeOffset? ModifiedAt { get; set; }
    public string SizeLabel => Size >= 1024L * 1024
        ? $"{Size / 1024d / 1024:0.0} MB"
        : $"{Math.Max(0, Size) / 1024d:0} KB";
    public string Display => $"{FileName}  ·  {Format}  ·  {SizeLabel}";
}

public static class KindleResourcePolicy
{
    private static readonly HashSet<string> FontExtensions = new(StringComparer.OrdinalIgnoreCase) { ".ttf", ".otf" };
    private static readonly HashSet<string> DictionaryExtensions = new(StringComparer.OrdinalIgnoreCase) { ".azw", ".azw3", ".mobi", ".prc", ".kfx" };

    public static string RootRelativePath(KindleResourceKind kind) => kind switch
    {
        KindleResourceKind.Font => "fonts",
        KindleResourceKind.Dictionary => Path.Combine("documents", "dictionaries"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public static bool IsSupportedFile(KindleResourceKind kind, string? path)
    {
        var extension = Path.GetExtension(path ?? string.Empty);
        return kind == KindleResourceKind.Font
            ? FontExtensions.Contains(extension)
            : DictionaryExtensions.Contains(extension);
    }

    public static bool TryGetPathWithinRoot(KindleResourceKind kind, string? relativePath, out string pathWithinRoot)
    {
        pathWithinRoot = string.Empty;
        if (string.IsNullOrWhiteSpace(relativePath)) return false;
        var segments = relativePath.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment is "." or "..")) return false;
        if (segments.Any(segment => segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)) return false;
        var rootSegments = RootRelativePath(kind).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length <= rootSegments.Length) return false;
        for (var index = 0; index < rootSegments.Length; index++)
            if (!segments[index].Equals(rootSegments[index], StringComparison.OrdinalIgnoreCase)) return false;
        pathWithinRoot = Path.Combine(segments[rootSegments.Length..]);
        return IsSupportedFile(kind, pathWithinRoot);
    }
}

public enum KindleClippingType
{
    Highlight,
    Note,
    Bookmark,
    Unknown
}

public sealed class KindleClipping
{
    public string Id { get; set; } = string.Empty;
    public string BookTitle { get; set; } = "未知书籍";
    public string Author { get; set; } = string.Empty;
    public KindleClippingType Type { get; set; }
    public string Metadata { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string RawBlock { get; set; } = string.Empty;
    public DateTimeOffset? AddedAt { get; set; }
    /// <summary>Used by the reading-materials view when a Kindle note belongs to this highlight.</summary>
    public KindleClipping? PairedNote { get; set; }
    public string TypeLabel => Type switch
    {
        KindleClippingType.Highlight => UiText.Get("划线"),
        KindleClippingType.Note => UiText.Get("笔记"),
        KindleClippingType.Bookmark => UiText.Get("书签"),
        _ => UiText.Get("记录")
    };
}

public sealed class BookMetadata
{
    public string? Title { get; init; }
    public string? Authors { get; init; }
    public string? Series { get; init; }
    public double? SeriesIndex { get; init; }
    public string? Description { get; init; }
    public byte[]? CoverBytes { get; init; }
    public string CoverExtension { get; init; } = ".jpg";
}

public sealed record ImportItemResult(
    string SourcePath,
    bool Succeeded,
    string? Message,
    Book? Book,
    bool Added = false);

public sealed class ImportBatchResult
{
    public List<ImportItemResult> Items { get; } = [];
    public int SuccessCount => Items.Count(x => x.Succeeded);
    public int FailureCount => Items.Count(x => !x.Succeeded);
}

public sealed record TransferProgress(long BytesCopied, long TotalBytes, string Message)
{
    public double Percentage => TotalBytes <= 0 ? 0 : BytesCopied * 100d / TotalBytes;
}

public sealed record FormatConversionProgress(double Percentage, string Message)
{
    public int RoundedPercentage => Math.Clamp((int)Math.Round(Percentage), 0, 100);
}

public sealed record FormatConversionMetadata(string Title, string Authors, string? CoverPath = null);

public sealed class ZLibraryBook
{
    public long Id { get; set; }
    public string Title { get; set; } = "未命名书籍";
    public string Author { get; set; } = "未知作者";
    public string Extension { get; set; } = string.Empty;
    public long Size { get; set; }
    public string Language { get; set; } = string.Empty;
    public string? CoverUrl { get; set; }
    public string Hash { get; set; } = string.Empty;
    public int? Year { get; set; }
    public string? Publisher { get; set; }
    public string? Series { get; set; }
    public string? Edition { get; set; }
    public string? Identifier { get; set; }
    public string? Volume { get; set; }
    public string? Description { get; set; }
    public string? OfficialDetailUrl { get; set; }
    public string? ReadOnlineUrl { get; set; }
    public int? Pages { get; set; }
    public bool ReadOnlineAvailable { get; set; }
    public bool KindleAvailable { get; set; }
    public bool SendToEmailAvailable { get; set; }

    public string FormatLabel => string.IsNullOrWhiteSpace(Extension) ? string.Empty : Extension.ToUpperInvariant();
    public string SizeLabel => Size >= 1024L * 1024
        ? $"{Size / 1024d / 1024:0.0} MB"
        : $"{Math.Max(0, Size) / 1024d:0} KB";
    public string LanguageLabel => string.IsNullOrWhiteSpace(Language) ? UiText.Get("未知语言") : Language;
    public string InfoLabel => string.Join(" · ", new[]
    {
        FormatLabel,
        SizeLabel,
        LanguageLabel,
        Pages is > 0 ? UiText.Get("{0} 页", Pages) : string.Empty
    }.Where(label => label.Length > 0));
}

public sealed record ZLibrarySearchResult(IReadOnlyList<ZLibraryBook> Books, int Total, int Page, int PageCount);
