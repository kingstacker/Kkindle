using Microsoft.Data.Sqlite;
using Kkindle.Core;

namespace Kkindle.Infrastructure;

public sealed partial class SqliteBookLibraryService
{
    private readonly Dictionary<Guid, (string Stamp, PinyinSearchIndex Index)> _libraryPinyinSearchIndexes = [];
    private readonly object _libraryPinyinSearchIndexGate = new();

    private bool MatchesLibrarySearch(Book book, string query, string pinyinQuery)
    {
        if (book.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || book.Authors.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || book.Tags.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || (book.Series?.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false))
        {
            return true;
        }

        return pinyinQuery.Length > 0
            && GetLibraryPinyinSearchIndex(book).Matches(pinyinQuery);
    }

    private PinyinSearchIndex GetLibraryPinyinSearchIndex(Book book)
    {
        var stamp = LibraryPinyinSearchStamp(book);
        lock (_libraryPinyinSearchIndexGate)
        {
            if (_libraryPinyinSearchIndexes.TryGetValue(book.Id, out var cached)
                && cached.Stamp == stamp)
            {
                return cached.Index;
            }
        }

        var index = PinyinSearchIndex.Create(book.Title, book.Authors, book.Tags, book.Series);
        lock (_libraryPinyinSearchIndexGate)
        {
            _libraryPinyinSearchIndexes[book.Id] = (stamp, index);
            return index;
        }
    }

    private static string LibraryPinyinSearchStamp(Book book) =>
        string.Join('\u001f', book.Title, book.Authors, book.Tags, book.Series ?? string.Empty);

    private async Task<LibraryPageResult> SearchPageByPinyinAsync(
        SqliteConnection connection,
        string query,
        string pinyinQuery,
        string? author,
        string? tag,
        string? format,
        string? category,
        Guid? collectionId,
        LibraryReadingStatus? readingStatus,
        bool? favorite,
        LibrarySortMode sortMode,
        int pageIndex,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var where = BuildLibrarySearchWhere(
            null,
            author,
            tag,
            format,
            category,
            collectionId,
            readingStatus,
            favorite);
        var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT b.Id, b.Title, b.Authors, b.Series, b.SeriesIndex, b.Description, b.Tags, b.Category, b.IsFavorite, b.ReadingStatus, b.CoverPath, b.CreatedAt, b.UpdatedAt,
                   b.Publisher, b.PublishDate, b.Isbn, b.PageCount, b.Binding, b.DoubanRating, b.DoubanRatingCount
            FROM Books b
            {where}
            ORDER BY {GetLibrarySortSql(sortMode)};
            """;
        AddLibrarySearchParameters(command, null, author, tag, format, category, collectionId, readingStatus);

        var matches = new List<Book>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var book = ReadBook(reader);
                cancellationToken.ThrowIfCancellationRequested();
                if (MatchesLibrarySearch(book, query, pinyinQuery)) matches.Add(book);
            }
        }

        var pageOffset = Math.Min((long)pageIndex * pageSize, matches.Count);
        var pageBooks = matches
            .Skip((int)pageOffset)
            .Take(pageSize)
            .ToArray();
        var filesByBook = await ReadFilesByBookAsync(
            connection,
            pageBooks.Select(book => book.Id),
            cancellationToken);
        var collectionsByBook = await ReadCollectionIdsByBookAsync(
            connection,
            pageBooks.Select(book => book.Id),
            cancellationToken);
        foreach (var book in pageBooks)
        {
            book.Files = filesByBook.GetValueOrDefault(book.Id) ?? [];
            book.CollectionIds = collectionsByBook.GetValueOrDefault(book.Id) ?? [];
        }

        return new LibraryPageResult(pageBooks, matches.Count);
    }
}
