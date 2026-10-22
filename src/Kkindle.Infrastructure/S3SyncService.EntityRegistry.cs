namespace Kkindle.Infrastructure;

public sealed partial class S3SyncService
{
    // All portable row types must be registered here. Capture/apply remain typed
    // because reader generations and identity remapping have domain-specific rules.
    private sealed record SyncEntityRow(string Key, DateTimeOffset Version, object Value,
        string[] DeletionKeys, string? BlobHash);
    private sealed record SyncEntityRegistration(string Property, string Type, bool TrackDeletion,
        Func<S3SyncSnapshot, IEnumerable<SyncEntityRow>> Rows);

    private static SyncEntityRegistration Register<T>(string property, string type,
        Func<S3SyncSnapshot, IEnumerable<T>> rows, Func<T, string> key,
        Func<T, DateTimeOffset> version, Func<T, string[]> deletionKeys,
        Func<T, string?> blobHash, bool trackDeletion = true) =>
        new(property, type, trackDeletion, snapshot => rows(snapshot).Select(row =>
            new SyncEntityRow(key(row), version(row), row!, deletionKeys(row), blobHash(row))));

    private static readonly SyncEntityRegistration[] SyncEntities =
    [
        Register<S3SyncBook>(nameof(S3SyncSnapshot.Books), "book", s => s.Books,
            r => r.Id.ToString("N"), r => r.UpdatedAt, r => [VersionKey("book", r.Id)], r => r.CoverHash),
        Register<S3SyncBook>(nameof(S3SyncSnapshot.ReadingMaterialBooks), "reading-material", s => s.ReadingMaterialBooks,
            r => r.Id.ToString("N"), r => r.UpdatedAt, r => [], r => r.CoverHash, trackDeletion: false),
        Register<S3SyncBookFile>(nameof(S3SyncSnapshot.Files), "file", s => s.Files,
            r => r.Id.ToString("N"), r => r.ModifiedAt, r => [VersionKey("file", r.Id), VersionKey("book", r.BookId)], r => r.Sha256),
        Register<S3SyncCollection>(nameof(S3SyncSnapshot.Collections), "collection", s => s.Collections,
            r => r.Id.ToString("N"), r => CollectionUpdatedAt(r), r => [VersionKey("collection", r.Id)], r => null),
        Register<S3SyncCollectionItem>(nameof(S3SyncSnapshot.CollectionItems), "collection-item", s => s.CollectionItems,
            r => CompositeKey(r.CollectionId, r.BookId), r => r.AddedAt, r => [VersionKey("collection-item", CompositeKey(r.CollectionId, r.BookId)), VersionKey("collection", r.CollectionId), VersionKey("book", r.BookId)], r => null),
        Register<S3SyncAnnotation>(nameof(S3SyncSnapshot.Annotations), "annotation", s => s.Annotations,
            r => r.Id.ToString("N"), r => r.UpdatedAt, r => [VersionKey("annotation", r.Id)], r => null),
        Register<S3SyncBookReflection>(nameof(S3SyncSnapshot.BookReflections), "reflection", s => s.BookReflections,
            r => r.BookId.ToString("N"), r => r.UpdatedAt, r => [VersionKey("reflection", r.BookId), VersionKey("book", r.BookId)], r => null),
        Register<S3SyncProgress>(nameof(S3SyncSnapshot.Progress), "progress", s => s.Progress,
            r => r.BookFileId.ToString("N"), r => r.UpdatedAt, r => [VersionKey("progress", r.BookFileId), VersionKey("book", r.BookId), VersionKey("file", r.BookFileId)], r => null),
        Register<S3SyncBookmark>(nameof(S3SyncSnapshot.Bookmarks), "bookmark", s => s.Bookmarks,
            r => r.Id.ToString("N"), r => r.CreatedAt, r => [VersionKey("bookmark", r.Id), VersionKey("book", r.BookId), VersionKey("file", r.BookFileId)], r => null),
        Register<S3SyncLayout>(nameof(S3SyncSnapshot.Layouts), "layout", s => s.Layouts,
            r => r.BookFileId.ToString("N"), r => LayoutSyncVersion(r), r => [VersionKey("layout", r.BookFileId), VersionKey("book", r.BookId), VersionKey("file", r.BookFileId)], r => null),
        Register<S3SyncReadingStats>(nameof(S3SyncSnapshot.ReadingStats), "stats", s => s.ReadingStats,
            r => r.BookFileId.ToString("N"), r => r.UpdatedAt, r => [VersionKey("stats", r.BookFileId), VersionKey("book", r.BookId), VersionKey("file", r.BookFileId)], r => null),
    ];
}
