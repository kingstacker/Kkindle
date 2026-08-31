using System.Text.RegularExpressions;
using Kkindle.Core;
using Microsoft.Data.Sqlite;

namespace Kkindle.Infrastructure;

public sealed partial class ReaderDataService
{
    private readonly AppPaths _paths;
    private readonly SemaphoreSlim _databaseGate = new(1, 1);
    private bool _ftsAvailable;

    public ReaderDataService(AppPaths paths)
    {
        _paths = paths;
    }

    private string ConnectionString => new SqliteConnectionStringBuilder
    {
        DataSource = _paths.Database,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Shared
    }.ToString();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _paths.EnsureDirectories();
        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS ReaderAnnotations (
                    Id TEXT PRIMARY KEY,
                    BookId TEXT NOT NULL,
                    BookFileId TEXT NOT NULL,
                    ChapterPath TEXT NOT NULL,
                    Fragment TEXT NULL,
                    StartOffset INTEGER NOT NULL,
                    EndOffset INTEGER NOT NULL,
                    SelectedText TEXT NOT NULL,
                    Prefix TEXT NOT NULL DEFAULT '',
                    Suffix TEXT NOT NULL DEFAULT '',
                    Color TEXT NOT NULL DEFAULT '#000000',
                    UnderlineStyle TEXT NOT NULL DEFAULT 'solid',
                    Note TEXT NOT NULL DEFAULT '',
                    CreatedAt TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS IX_ReaderAnnotations_BookFile
                    ON ReaderAnnotations(BookFileId, ChapterPath, StartOffset);

                CREATE TABLE IF NOT EXISTS BookContentChunks (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    BookId TEXT NOT NULL,
                    BookFileId TEXT NOT NULL,
                    SourceHash TEXT NOT NULL,
                    ChapterIndex INTEGER NOT NULL,
                    ChunkIndex INTEGER NOT NULL,
                    ChapterTitle TEXT NOT NULL,
                    ChapterPath TEXT NOT NULL,
                    StartOffset INTEGER NOT NULL,
                    EndOffset INTEGER NOT NULL,
                    Content TEXT NOT NULL
                );
                CREATE UNIQUE INDEX IF NOT EXISTS UX_BookContentChunks_Position
                    ON BookContentChunks(BookFileId, SourceHash, ChapterIndex, ChunkIndex);
                CREATE INDEX IF NOT EXISTS IX_BookContentChunks_Book
                    ON BookContentChunks(BookId, ChapterIndex, ChunkIndex);

                CREATE TABLE IF NOT EXISTS ReaderProgress (
                    BookFileId TEXT PRIMARY KEY,
                    BookId TEXT NOT NULL,
                    ChapterPath TEXT NOT NULL,
                    Fragment TEXT NULL,
                    ChapterIndex INTEGER NOT NULL DEFAULT 0,
                    ScrollPosition INTEGER NOT NULL DEFAULT 0,
                    ProgressPercent REAL NOT NULL DEFAULT 0,
                    FlowMode INTEGER NOT NULL DEFAULT 0,
                    UpdatedAt TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS ReaderBookmarks (
                    Id TEXT PRIMARY KEY,
                    BookId TEXT NOT NULL,
                    BookFileId TEXT NOT NULL,
                    ChapterPath TEXT NOT NULL,
                    Fragment TEXT NULL,
                    ChapterIndex INTEGER NOT NULL DEFAULT 0,
                    ScrollPosition INTEGER NULL,
                    FlowMode INTEGER NOT NULL DEFAULT 0,
                    Title TEXT NOT NULL DEFAULT '',
                    Quote TEXT NOT NULL DEFAULT '',
                    CreatedAt TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS IX_ReaderBookmarks_BookFile
                    ON ReaderBookmarks(BookFileId, ChapterIndex, CreatedAt);

                CREATE TABLE IF NOT EXISTS ReaderLayoutSettings (
                    BookFileId TEXT PRIMARY KEY,
                    BookId TEXT NOT NULL,
                    FontScale REAL NOT NULL DEFAULT 1.0,
                    LineHeight REAL NOT NULL DEFAULT 1.88,
                    MaxWidth REAL NOT NULL DEFAULT 800,
                    BodyPadding REAL NOT NULL DEFAULT 68,
                    FontFamily TEXT NULL,
                    FlowMode INTEGER NOT NULL DEFAULT 0,
                    VerticalWriting INTEGER NOT NULL DEFAULT 0,
                    TwoPageMode INTEGER NOT NULL DEFAULT 0,
                    ParagraphIndent INTEGER NOT NULL DEFAULT 1,
                    UpdatedAt TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS ReaderReadingStats (
                    BookFileId TEXT PRIMARY KEY,
                    BookId TEXT NOT NULL,
                    CumulativeSeconds INTEGER NOT NULL DEFAULT 0,
                    ProgressPercent REAL NOT NULL DEFAULT 0,
                    CompletedChapters INTEGER NOT NULL DEFAULT 0,
                    TotalChapters INTEGER NOT NULL DEFAULT 0,
                    UpdatedAt TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS ReaderReadingSessions (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    BookId TEXT NOT NULL,
                    BookFileId TEXT NOT NULL,
                    ActiveSeconds INTEGER NOT NULL,
                    ProgressPercent REAL NOT NULL DEFAULT 0,
                    RecordedAt TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS IX_ReaderReadingSessions_RecordedAt
                    ON ReaderReadingSessions(RecordedAt);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            await EnsureReaderLayoutTwoPageColumnAsync(connection, cancellationToken);
            await EnsureReaderLayoutParagraphIndentColumnAsync(connection, cancellationToken);
            await EnsureReaderAnnotationStyleColumnAsync(connection, cancellationToken);
            await EnsureReaderBookmarkPositionColumnsAsync(connection, cancellationToken);

            _ftsAvailable = await EnsureFullTextIndexAsync(connection, cancellationToken);
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    public async Task<IReadOnlyList<ReaderAnnotation>> GetAnnotationsAsync(
        Guid bookFileId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, BookId, BookFileId, ChapterPath, Fragment, StartOffset, EndOffset,
                   SelectedText, Prefix, Suffix, Color, UnderlineStyle, Note, CreatedAt, UpdatedAt
            FROM ReaderAnnotations
            WHERE BookFileId = $bookFileId
            ORDER BY ChapterPath COLLATE NOCASE, StartOffset, CreatedAt;
            """;
        command.Parameters.AddWithValue("$bookFileId", bookFileId.ToString());
        var result = new List<ReaderAnnotation>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(ReadAnnotation(reader));
        return result;
    }

    public async Task<IReadOnlyList<ReaderAnnotation>> GetAllAnnotationsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, BookId, BookFileId, ChapterPath, Fragment, StartOffset, EndOffset,
                   SelectedText, Prefix, Suffix, Color, UnderlineStyle, Note, CreatedAt, UpdatedAt
            FROM ReaderAnnotations
            ORDER BY UpdatedAt DESC, BookId, ChapterPath COLLATE NOCASE, StartOffset;
            """;
        var result = new List<ReaderAnnotation>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadAnnotation(reader));
        return result;
    }

    private static ReaderAnnotation ReadAnnotation(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)),
        BookId = Guid.Parse(reader.GetString(1)),
        BookFileId = Guid.Parse(reader.GetString(2)),
        ChapterPath = reader.GetString(3),
        Fragment = reader.IsDBNull(4) ? null : reader.GetString(4),
        StartOffset = reader.GetInt32(5),
        EndOffset = reader.GetInt32(6),
        SelectedText = reader.GetString(7),
        Prefix = reader.GetString(8),
        Suffix = reader.GetString(9),
        Color = reader.GetString(10),
        UnderlineStyle = reader.GetString(11),
        Note = reader.GetString(12),
        CreatedAt = DateTimeOffset.Parse(reader.GetString(13)),
        UpdatedAt = DateTimeOffset.Parse(reader.GetString(14))
    };

    public async Task SaveAnnotationAsync(ReaderAnnotation annotation, CancellationToken cancellationToken = default)
    {
        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO ReaderAnnotations (
                    Id, BookId, BookFileId, ChapterPath, Fragment, StartOffset, EndOffset,
                    SelectedText, Prefix, Suffix, Color, UnderlineStyle, Note, CreatedAt, UpdatedAt)
                VALUES (
                    $id, $bookId, $bookFileId, $chapterPath, $fragment, $startOffset, $endOffset,
                    $selectedText, $prefix, $suffix, $color, $underlineStyle, $note, $createdAt, $updatedAt)
                ON CONFLICT(Id) DO UPDATE SET
                    ChapterPath=$chapterPath, Fragment=$fragment, StartOffset=$startOffset, EndOffset=$endOffset,
                    SelectedText=$selectedText, Prefix=$prefix, Suffix=$suffix, Color=$color,
                    UnderlineStyle=$underlineStyle,
                    Note=$note, UpdatedAt=$updatedAt;
                """;
            command.Parameters.AddWithValue("$id", annotation.Id.ToString());
            command.Parameters.AddWithValue("$bookId", annotation.BookId.ToString());
            command.Parameters.AddWithValue("$bookFileId", annotation.BookFileId.ToString());
            command.Parameters.AddWithValue("$chapterPath", annotation.ChapterPath);
            command.Parameters.AddWithValue("$fragment", (object?)annotation.Fragment ?? DBNull.Value);
            command.Parameters.AddWithValue("$startOffset", annotation.StartOffset);
            command.Parameters.AddWithValue("$endOffset", annotation.EndOffset);
            command.Parameters.AddWithValue("$selectedText", annotation.SelectedText);
            command.Parameters.AddWithValue("$prefix", annotation.Prefix);
            command.Parameters.AddWithValue("$suffix", annotation.Suffix);
            command.Parameters.AddWithValue("$color", annotation.Color);
            command.Parameters.AddWithValue("$underlineStyle", annotation.UnderlineStyle);
            command.Parameters.AddWithValue("$note", annotation.Note);
            command.Parameters.AddWithValue("$createdAt", annotation.CreatedAt.ToString("O"));
            command.Parameters.AddWithValue("$updatedAt", annotation.UpdatedAt.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    public async Task DeleteAnnotationAsync(Guid annotationId, CancellationToken cancellationToken = default)
    {
        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM ReaderAnnotations WHERE Id = $id;";
            command.Parameters.AddWithValue("$id", annotationId.ToString());
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    // ------------------------------------------------------------------
    // Reading progress (breakpoint restore).
    // ------------------------------------------------------------------

    public async Task<ReaderProgressRow?> GetProgressAsync(
        Guid bookFileId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT BookId, BookFileId, ChapterPath, Fragment, ChapterIndex, ScrollPosition,
                   ProgressPercent, FlowMode, UpdatedAt
            FROM ReaderProgress
            WHERE BookFileId = $bookFileId;
            """;
        command.Parameters.AddWithValue("$bookFileId", bookFileId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new ReaderProgressRow(
            Guid.Parse(reader.GetString(0)),
            Guid.Parse(reader.GetString(1)),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetInt32(4),
            reader.GetInt32(5),
            reader.GetDouble(6),
            reader.GetInt32(7),
            DateTimeOffset.Parse(reader.GetString(8)));
    }

    public async Task SaveProgressAsync(
        ReaderProgressRow progress,
        CancellationToken cancellationToken = default)
    {
        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO ReaderProgress (
                    BookFileId, BookId, ChapterPath, Fragment, ChapterIndex, ScrollPosition,
                    ProgressPercent, FlowMode, UpdatedAt)
                VALUES (
                    $bookFileId, $bookId, $chapterPath, $fragment, $chapterIndex, $scrollPosition,
                    $progressPercent, $flowMode, $updatedAt)
                ON CONFLICT(BookFileId) DO UPDATE SET
                    BookId=$bookId, ChapterPath=$chapterPath, Fragment=$fragment,
                    ChapterIndex=$chapterIndex, ScrollPosition=$scrollPosition,
                    ProgressPercent=$progressPercent, FlowMode=$flowMode, UpdatedAt=$updatedAt;
                """;
            command.Parameters.AddWithValue("$bookFileId", progress.BookFileId.ToString());
            command.Parameters.AddWithValue("$bookId", progress.BookId.ToString());
            command.Parameters.AddWithValue("$chapterPath", progress.ChapterPath);
            command.Parameters.AddWithValue("$fragment", (object?)progress.Fragment ?? DBNull.Value);
            command.Parameters.AddWithValue("$chapterIndex", progress.ChapterIndex);
            command.Parameters.AddWithValue("$scrollPosition", progress.ScrollPosition);
            command.Parameters.AddWithValue("$progressPercent", progress.ProgressPercent);
            command.Parameters.AddWithValue("$flowMode", progress.FlowMode);
            command.Parameters.AddWithValue("$updatedAt", progress.UpdatedAt.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    // ------------------------------------------------------------------
    // Bookmarks.
    // ------------------------------------------------------------------

    public async Task<IReadOnlyList<ReaderBookmark>> GetBookmarksAsync(
        Guid bookFileId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, BookId, BookFileId, ChapterPath, Fragment, ChapterIndex,
                   ScrollPosition, FlowMode, Title, Quote, CreatedAt
            FROM ReaderBookmarks
            WHERE BookFileId = $bookFileId
            ORDER BY ChapterIndex, CreatedAt;
            """;
        command.Parameters.AddWithValue("$bookFileId", bookFileId.ToString());
        var result = new List<ReaderBookmark>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new ReaderBookmark
            {
                Id = Guid.Parse(reader.GetString(0)),
                BookId = Guid.Parse(reader.GetString(1)),
                BookFileId = Guid.Parse(reader.GetString(2)),
                ChapterPath = reader.GetString(3),
                Fragment = reader.IsDBNull(4) ? null : reader.GetString(4),
                ChapterIndex = reader.GetInt32(5),
                ScrollPosition = reader.IsDBNull(6) ? null : reader.GetInt32(6),
                FlowMode = reader.GetInt32(7),
                Title = reader.GetString(8),
                Quote = reader.GetString(9),
                CreatedAt = DateTimeOffset.Parse(reader.GetString(10))
            });
        }
        return result;
    }

    public async Task SaveBookmarkAsync(ReaderBookmark bookmark, CancellationToken cancellationToken = default)
    {
        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO ReaderBookmarks (
                    Id, BookId, BookFileId, ChapterPath, Fragment, ChapterIndex,
                    ScrollPosition, FlowMode, Title, Quote, CreatedAt)
                VALUES (
                    $id, $bookId, $bookFileId, $chapterPath, $fragment, $chapterIndex,
                    $scrollPosition, $flowMode, $title, $quote, $createdAt)
                ON CONFLICT(Id) DO UPDATE SET
                    BookId=$bookId, BookFileId=$bookFileId, ChapterPath=$chapterPath, Fragment=$fragment,
                    ChapterIndex=$chapterIndex, ScrollPosition=$scrollPosition, FlowMode=$flowMode,
                    Title=$title, Quote=$quote, CreatedAt=$createdAt;
                """;
            command.Parameters.AddWithValue("$id", bookmark.Id.ToString());
            command.Parameters.AddWithValue("$bookId", bookmark.BookId.ToString());
            command.Parameters.AddWithValue("$bookFileId", bookmark.BookFileId.ToString());
            command.Parameters.AddWithValue("$chapterPath", bookmark.ChapterPath);
            command.Parameters.AddWithValue("$fragment", (object?)bookmark.Fragment ?? DBNull.Value);
            command.Parameters.AddWithValue("$chapterIndex", bookmark.ChapterIndex);
            command.Parameters.AddWithValue("$scrollPosition", (object?)bookmark.ScrollPosition ?? DBNull.Value);
            command.Parameters.AddWithValue("$flowMode", bookmark.FlowMode);
            command.Parameters.AddWithValue("$title", bookmark.Title);
            command.Parameters.AddWithValue("$quote", bookmark.Quote);
            command.Parameters.AddWithValue("$createdAt", bookmark.CreatedAt.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    public async Task DeleteBookmarkAsync(Guid bookmarkId, CancellationToken cancellationToken = default)
    {
        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM ReaderBookmarks WHERE Id = $id;";
            command.Parameters.AddWithValue("$id", bookmarkId.ToString());
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    // ------------------------------------------------------------------
    // Per-book layout settings.
    // ------------------------------------------------------------------

    private static async Task EnsureReaderLayoutTwoPageColumnAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using var inspect = connection.CreateCommand();
        inspect.CommandText = "PRAGMA table_info(ReaderLayoutSettings);";
        var hasTwoPageColumn = false;
        await using (var reader = await inspect.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                if (reader.FieldCount > 1
                    && string.Equals(reader.GetString(1), "TwoPageMode", StringComparison.OrdinalIgnoreCase))
                {
                    hasTwoPageColumn = true;
                    break;
                }
            }
        }

        if (hasTwoPageColumn) return;

        using var alter = connection.CreateCommand();
        alter.CommandText = "ALTER TABLE ReaderLayoutSettings ADD COLUMN TwoPageMode INTEGER NOT NULL DEFAULT 0;";
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureReaderLayoutParagraphIndentColumnAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using var inspect = connection.CreateCommand();
        inspect.CommandText = "PRAGMA table_info(ReaderLayoutSettings);";
        var hasParagraphIndentColumn = false;
        await using (var reader = await inspect.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                if (reader.FieldCount > 1
                    && string.Equals(reader.GetString(1), "ParagraphIndent", StringComparison.OrdinalIgnoreCase))
                {
                    hasParagraphIndentColumn = true;
                    break;
                }
            }
        }

        if (hasParagraphIndentColumn) return;

        // Existing readers have always kept the publisher/default paragraph
        // indent, so migrate old rows to the enabled state.
        using var alter = connection.CreateCommand();
        alter.CommandText = "ALTER TABLE ReaderLayoutSettings ADD COLUMN ParagraphIndent INTEGER NOT NULL DEFAULT 1;";
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureReaderAnnotationStyleColumnAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using var inspect = connection.CreateCommand();
        inspect.CommandText = "PRAGMA table_info(ReaderAnnotations);";
        var hasUnderlineStyleColumn = false;
        await using (var reader = await inspect.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                if (string.Equals(reader.GetString(1), "UnderlineStyle", StringComparison.OrdinalIgnoreCase))
                {
                    hasUnderlineStyleColumn = true;
                    break;
                }
            }
        }

        if (hasUnderlineStyleColumn) return;

        using var alter = connection.CreateCommand();
        alter.CommandText = "ALTER TABLE ReaderAnnotations ADD COLUMN UnderlineStyle TEXT NOT NULL DEFAULT 'solid';";
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureReaderBookmarkPositionColumnsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using var inspect = connection.CreateCommand();
        inspect.CommandText = "PRAGMA table_info(ReaderBookmarks);";
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var reader = await inspect.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                columns.Add(reader.GetString(1));
        }

        if (!columns.Contains("ScrollPosition"))
        {
            using var addPosition = connection.CreateCommand();
            addPosition.CommandText = "ALTER TABLE ReaderBookmarks ADD COLUMN ScrollPosition INTEGER NULL;";
            await addPosition.ExecuteNonQueryAsync(cancellationToken);
        }

        if (!columns.Contains("FlowMode"))
        {
            using var addFlowMode = connection.CreateCommand();
            addFlowMode.CommandText = "ALTER TABLE ReaderBookmarks ADD COLUMN FlowMode INTEGER NOT NULL DEFAULT 0;";
            await addFlowMode.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task<ReaderLayoutSettings?> GetLayoutSettingsAsync(
        Guid bookFileId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT FontScale, LineHeight, MaxWidth, BodyPadding, FontFamily, FlowMode, VerticalWriting, TwoPageMode, ParagraphIndent
            FROM ReaderLayoutSettings
            WHERE BookFileId = $bookFileId;
            """;
        command.Parameters.AddWithValue("$bookFileId", bookFileId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new ReaderLayoutSettings(
            reader.GetDouble(0),
            reader.GetDouble(1),
            reader.GetDouble(2),
            reader.GetDouble(3),
            reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
            reader.GetInt32(5),
            reader.GetInt32(6) != 0,
            reader.GetInt32(7) != 0)
        {
            ParagraphIndent = reader.GetInt32(8) != 0
        };
    }

    public async Task SaveLayoutSettingsAsync(
        Guid bookId,
        Guid bookFileId,
        ReaderLayoutSettings settings,
        CancellationToken cancellationToken = default)
    {
        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO ReaderLayoutSettings (
                    BookFileId, BookId, FontScale, LineHeight, MaxWidth, BodyPadding,
                    FontFamily, FlowMode, VerticalWriting, TwoPageMode, ParagraphIndent, UpdatedAt)
                VALUES (
                    $bookFileId, $bookId, $fontScale, $lineHeight, $maxWidth, $bodyPadding,
                    $fontFamily, $flowMode, $verticalWriting, $twoPageMode, $paragraphIndent, $updatedAt)
                ON CONFLICT(BookFileId) DO UPDATE SET
                    BookId=$bookId, FontScale=$fontScale, LineHeight=$lineHeight,
                    MaxWidth=$maxWidth, BodyPadding=$bodyPadding, FontFamily=$fontFamily,
                    FlowMode=$flowMode, VerticalWriting=$verticalWriting,
                    TwoPageMode=$twoPageMode, ParagraphIndent=$paragraphIndent, UpdatedAt=$updatedAt;
                """;
            command.Parameters.AddWithValue("$bookFileId", bookFileId.ToString());
            command.Parameters.AddWithValue("$bookId", bookId.ToString());
            command.Parameters.AddWithValue("$fontScale", settings.FontScale);
            command.Parameters.AddWithValue("$lineHeight", settings.LineHeight);
            command.Parameters.AddWithValue("$maxWidth", settings.MaxWidth);
            command.Parameters.AddWithValue("$bodyPadding", settings.BodyPadding);
            command.Parameters.AddWithValue("$fontFamily", string.IsNullOrWhiteSpace(settings.FontFamily) ? DBNull.Value : settings.FontFamily);
            command.Parameters.AddWithValue("$flowMode", settings.FlowMode);
            command.Parameters.AddWithValue("$verticalWriting", settings.VerticalWriting ? 1 : 0);
            command.Parameters.AddWithValue("$twoPageMode", settings.TwoPageMode ? 1 : 0);
            command.Parameters.AddWithValue("$paragraphIndent", settings.ParagraphIndent ? 1 : 0);
            command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    // ------------------------------------------------------------------
    // Reading stats (cumulative active reading time + progress snapshot).
    // ------------------------------------------------------------------

    public async Task<ReaderReadingStats?> GetReadingStatsAsync(
        Guid bookFileId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT BookId, BookFileId, CumulativeSeconds, ProgressPercent, CompletedChapters, TotalChapters, UpdatedAt
            FROM ReaderReadingStats
            WHERE BookFileId = $bookFileId;
            """;
        command.Parameters.AddWithValue("$bookFileId", bookFileId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new ReaderReadingStats
        {
            BookId = Guid.Parse(reader.GetString(0)),
            BookFileId = Guid.Parse(reader.GetString(1)),
            CumulativeSeconds = reader.GetInt64(2),
            ProgressPercent = reader.GetDouble(3),
            CompletedChapters = reader.GetInt32(4),
            TotalChapters = reader.GetInt32(5),
            UpdatedAt = DateTimeOffset.Parse(reader.GetString(6))
        };
    }

    public async Task SaveReadingStatsAsync(ReaderReadingStats stats, CancellationToken cancellationToken = default)
    {
        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO ReaderReadingStats (
                    BookFileId, BookId, CumulativeSeconds, ProgressPercent, CompletedChapters, TotalChapters, UpdatedAt)
                VALUES (
                    $bookFileId, $bookId, $cumulativeSeconds, $progressPercent, $completedChapters, $totalChapters, $updatedAt)
                ON CONFLICT(BookFileId) DO UPDATE SET
                    BookId=$bookId, CumulativeSeconds=$cumulativeSeconds, ProgressPercent=$progressPercent,
                    CompletedChapters=$completedChapters, TotalChapters=$totalChapters, UpdatedAt=$updatedAt;
                """;
            command.Parameters.AddWithValue("$bookFileId", stats.BookFileId.ToString());
            command.Parameters.AddWithValue("$bookId", stats.BookId.ToString());
            command.Parameters.AddWithValue("$cumulativeSeconds", stats.CumulativeSeconds);
            command.Parameters.AddWithValue("$progressPercent", stats.ProgressPercent);
            command.Parameters.AddWithValue("$completedChapters", stats.CompletedChapters);
            command.Parameters.AddWithValue("$totalChapters", stats.TotalChapters);
            command.Parameters.AddWithValue("$updatedAt", stats.UpdatedAt.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    public async Task<ReadingDashboard> GetReadingDashboardAsync(
        int recentLimit = 12,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var summary = connection.CreateCommand();
        summary.CommandText = """
            SELECT
                COUNT(*),
                COALESCE(SUM(CASE WHEN ProgressPercent >= 99.5 THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CumulativeSeconds), 0),
                COALESCE(AVG(ProgressPercent), 0)
            FROM ReaderReadingStats;
            """;
        int started;
        int finished;
        long seconds;
        double average;
        await using (var reader = await summary.ExecuteReaderAsync(cancellationToken))
        {
            await reader.ReadAsync(cancellationToken);
            started = reader.GetInt32(0);
            finished = reader.GetInt32(1);
            seconds = reader.GetInt64(2);
            average = reader.GetDouble(3);
        }

        var counts = connection.CreateCommand();
        counts.CommandText = "SELECT (SELECT COUNT(*) FROM ReaderBookmarks), (SELECT COUNT(*) FROM ReaderAnnotations);";
        int bookmarks;
        int annotations;
        await using (var reader = await counts.ExecuteReaderAsync(cancellationToken))
        {
            await reader.ReadAsync(cancellationToken);
            bookmarks = reader.GetInt32(0);
            annotations = reader.GetInt32(1);
        }

        var recent = connection.CreateCommand();
        recent.CommandText = """
            SELECT BookId, BookFileId, ProgressPercent, CumulativeSeconds, UpdatedAt
            FROM ReaderReadingStats
            ORDER BY UpdatedAt DESC
            LIMIT $limit;
            """;
        recent.Parameters.AddWithValue("$limit", Math.Clamp(recentLimit, 1, 100));
        var recentBooks = new List<ReadingDashboardBook>();
        await using (var reader = await recent.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                recentBooks.Add(new ReadingDashboardBook(
                    Guid.Parse(reader.GetString(0)),
                    Guid.Parse(reader.GetString(1)),
                    reader.GetDouble(2),
                    reader.GetInt64(3),
                    DateTimeOffset.Parse(reader.GetString(4))));
        }

        var firstDay = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(-13));
        var dailyCommand = connection.CreateCommand();
        dailyCommand.CommandText = """
            SELECT substr(RecordedAt, 1, 10), COALESCE(SUM(ActiveSeconds), 0)
            FROM ReaderReadingSessions
            WHERE RecordedAt >= $cutoff
            GROUP BY substr(RecordedAt, 1, 10)
            ORDER BY substr(RecordedAt, 1, 10);
            """;
        dailyCommand.Parameters.AddWithValue("$cutoff", firstDay.ToString("yyyy-MM-dd"));
        var dailyValues = new Dictionary<DateOnly, long>();
        await using (var reader = await dailyCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                if (DateOnly.TryParse(reader.GetString(0), out var day))
                    dailyValues[day] = reader.GetInt64(1);
            }
        }
        var dailyReading = Enumerable.Range(0, 14)
            .Select(offset => firstDay.AddDays(offset))
            .Select(day => new ReadingDashboardDay(day, dailyValues.GetValueOrDefault(day)))
            .ToArray();

        return new ReadingDashboard(started, finished, seconds, average, bookmarks, annotations, recentBooks, dailyReading);
    }

    public async Task AddReadingTimeAsync(
        Guid bookId,
        Guid bookFileId,
        long activeSeconds,
        double progressPercent,
        int completedChapters,
        int totalChapters,
        CancellationToken cancellationToken = default)
    {
        if (activeSeconds <= 0) return;
        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO ReaderReadingStats (
                    BookFileId, BookId, CumulativeSeconds, ProgressPercent, CompletedChapters, TotalChapters, UpdatedAt)
                VALUES (
                    $bookFileId, $bookId, $seconds, $progressPercent, $completedChapters, $totalChapters, $updatedAt)
                ON CONFLICT(BookFileId) DO UPDATE SET
                    CumulativeSeconds = CumulativeSeconds + $seconds,
                    ProgressPercent = $progressPercent,
                    CompletedChapters = $completedChapters,
                    TotalChapters = $totalChapters,
                    UpdatedAt = $updatedAt;
                """;
            command.Parameters.AddWithValue("$bookFileId", bookFileId.ToString());
            command.Parameters.AddWithValue("$bookId", bookId.ToString());
            command.Parameters.AddWithValue("$seconds", activeSeconds);
            command.Parameters.AddWithValue("$progressPercent", progressPercent);
            command.Parameters.AddWithValue("$completedChapters", completedChapters);
            command.Parameters.AddWithValue("$totalChapters", totalChapters);
            command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);

            var session = connection.CreateCommand();
            session.CommandText = """
                INSERT INTO ReaderReadingSessions (
                    BookId, BookFileId, ActiveSeconds, ProgressPercent, RecordedAt)
                VALUES ($bookId, $bookFileId, $seconds, $progressPercent, $recordedAt);
                """;
            session.Parameters.AddWithValue("$bookId", bookId.ToString());
            session.Parameters.AddWithValue("$bookFileId", bookFileId.ToString());
            session.Parameters.AddWithValue("$seconds", activeSeconds);
            session.Parameters.AddWithValue("$progressPercent", progressPercent);
            session.Parameters.AddWithValue("$recordedAt", DateTimeOffset.UtcNow.ToString("O"));
            await session.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    public async Task<bool> IsIndexCurrentAsync(
        Guid bookFileId,
        string sourceHash,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM BookContentChunks
                WHERE BookFileId = $bookFileId AND SourceHash = $sourceHash LIMIT 1);
            """;
        command.Parameters.AddWithValue("$bookFileId", bookFileId.ToString());
        command.Parameters.AddWithValue("$sourceHash", sourceHash);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    public async Task ReplaceBookChunksAsync(
        Guid bookId,
        Guid bookFileId,
        string sourceHash,
        IReadOnlyList<BookContentChunkDraft> chunks,
        CancellationToken cancellationToken = default)
    {
        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            var delete = connection.CreateCommand();
            delete.Transaction = (SqliteTransaction)transaction;
            delete.CommandText = "DELETE FROM BookContentChunks WHERE BookFileId = $bookFileId;";
            delete.Parameters.AddWithValue("$bookFileId", bookFileId.ToString());
            await delete.ExecuteNonQueryAsync(cancellationToken);

            var insert = connection.CreateCommand();
            insert.Transaction = (SqliteTransaction)transaction;
            insert.CommandText = """
                INSERT INTO BookContentChunks (
                    BookId, BookFileId, SourceHash, ChapterIndex, ChunkIndex, ChapterTitle,
                    ChapterPath, StartOffset, EndOffset, Content)
                VALUES (
                    $bookId, $bookFileId, $sourceHash, $chapterIndex, $chunkIndex, $chapterTitle,
                    $chapterPath, $startOffset, $endOffset, $content);
                """;
            insert.Parameters.Add("$bookId", SqliteType.Text);
            insert.Parameters.Add("$bookFileId", SqliteType.Text);
            insert.Parameters.Add("$sourceHash", SqliteType.Text);
            insert.Parameters.Add("$chapterIndex", SqliteType.Integer);
            insert.Parameters.Add("$chunkIndex", SqliteType.Integer);
            insert.Parameters.Add("$chapterTitle", SqliteType.Text);
            insert.Parameters.Add("$chapterPath", SqliteType.Text);
            insert.Parameters.Add("$startOffset", SqliteType.Integer);
            insert.Parameters.Add("$endOffset", SqliteType.Integer);
            insert.Parameters.Add("$content", SqliteType.Text);

            foreach (var chunk in chunks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                insert.Parameters["$bookId"].Value = bookId.ToString();
                insert.Parameters["$bookFileId"].Value = bookFileId.ToString();
                insert.Parameters["$sourceHash"].Value = sourceHash;
                insert.Parameters["$chapterIndex"].Value = chunk.ChapterIndex;
                insert.Parameters["$chunkIndex"].Value = chunk.ChunkIndex;
                insert.Parameters["$chapterTitle"].Value = chunk.ChapterTitle;
                insert.Parameters["$chapterPath"].Value = chunk.ChapterPath;
                insert.Parameters["$startOffset"].Value = chunk.StartOffset;
                insert.Parameters["$endOffset"].Value = chunk.EndOffset;
                insert.Parameters["$content"].Value = chunk.Content;
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    public async Task<IReadOnlyList<BookContentChunk>> SearchBookAsync(
        Guid bookId,
        string query,
        int limit = 6,
        CancellationToken cancellationToken = default,
        bool exactPhraseOnly = false)
    {
        var terms = BuildSearchTerms(query);
        if (terms.Count == 0) return [];
        // int.MaxValue is the explicit "whole book" mode used by the reader
        // search UI. Small bounded callers (for example AI context retrieval)
        // keep their existing limits.
        var requestedLimit = limit == int.MaxValue ? int.MaxValue : Math.Clamp(limit, 1, 100);
        // Adjacent index chunks overlap so a phrase crossing a chunk boundary
        // remains searchable. Fetch extra candidates, then collapse candidates
        // whose first matching character resolves to the same chapter offset.
        var candidateLimit = requestedLimit == int.MaxValue
            ? int.MaxValue
            : Math.Clamp(requestedLimit * 3, requestedLimit, 100);

        if (_ftsAvailable && terms.Any(term => term.Length >= 3))
        {
            try
            {
                var ftsResults = await SearchFullTextAsync(bookId, terms, candidateLimit, cancellationToken);
                if (ftsResults.Count > 0)
                {
                    var matches = exactPhraseOnly
                        ? FilterExactPhraseMatches(ftsResults, query)
                        : ftsResults;
                    if (matches.Count > 0)
                        return DeduplicateSearchResults(matches, query, terms, requestedLimit);
                }
            }
            catch (SqliteException)
            {
                // Malformed or unsupported FTS query: the LIKE fallback remains fully local.
            }
        }

        var likeResults = await SearchLikeAsync(bookId, terms, candidateLimit, cancellationToken);
        var filtered = exactPhraseOnly
            ? FilterExactPhraseMatches(likeResults, query)
            : likeResults;
        return DeduplicateSearchResults(filtered, query, terms, requestedLimit);
    }

    private static IReadOnlyList<BookContentChunk> FilterExactPhraseMatches(
        IEnumerable<BookContentChunk> candidates,
        string query)
    {
        var normalizedQuery = WhitespaceRegex().Replace(query.Trim(), " ");
        if (normalizedQuery.Length == 0) return [];
        return candidates
            .Where(candidate =>
                ContainsNormalizedPhrase(candidate.Content, normalizedQuery)
                || ContainsNormalizedPhrase(candidate.ChapterTitle, normalizedQuery))
            .ToArray();
    }

    private static bool ContainsNormalizedPhrase(string? value, string normalizedQuery)
    {
        var normalizedValue = WhitespaceRegex().Replace(value ?? string.Empty, " ");
        return normalizedValue.Contains(
            normalizedQuery,
            StringComparison.CurrentCultureIgnoreCase);
    }

    public async Task<IReadOnlyList<BookContentChunk>> GetBookOverviewChunksAsync(
        Guid bookId,
        int limit = 12,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, BookId, BookFileId, SourceHash, ChapterIndex, ChunkIndex, ChapterTitle,
                   ChapterPath, StartOffset, EndOffset, Content
            FROM BookContentChunks
            WHERE BookId = $bookId AND ChunkIndex = 0
            ORDER BY ChapterIndex;
            """;
        command.Parameters.AddWithValue("$bookId", bookId.ToString());
        var openings = new List<BookContentChunk>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) openings.Add(ReadChunk(reader));
        if (openings.Count <= limit) return openings;

        var sampled = new List<BookContentChunk>(limit);
        for (var index = 0; index < limit; index++)
        {
            var sourceIndex = (int)Math.Round(index * (openings.Count - 1d) / (limit - 1d));
            sampled.Add(openings[sourceIndex]);
        }
        return sampled;
    }

    private async Task<IReadOnlyList<BookContentChunk>> SearchFullTextAsync(
        Guid bookId,
        IReadOnlyList<string> terms,
        int limit,
        CancellationToken cancellationToken)
    {
        var searchable = terms.Where(term => term.Length >= 3).Take(16).ToArray();
        if (searchable.Length == 0) return [];
        // ChapterTitle is stored on every content chunk for navigation. Search
        // it only on the first chunk below; otherwise a title-only query would
        // return the same chapter once for every chunk in that chapter.
        var contentMatch = BuildFtsColumnQuery("Content", searchable);
        var titleMatch = BuildFtsColumnQuery("ChapterTitle", searchable);
        // The reader's whole-book search passes int.MaxValue and presents
        // every hit as a reading list. Relevance ordering is useful for
        // bounded retrieval callers, but it makes that list jump between
        // chapters instead of following the book.
        var orderBy = limit == int.MaxValue
            ? "c.ChapterIndex, c.ChunkIndex, c.StartOffset, c.Id"
            : "best_matches.Rank, c.ChapterIndex, c.ChunkIndex";

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = $"""
            WITH matched AS (
                SELECT c.Id, bm25(BookContentFts, 1.0, 2.8) AS Rank
                FROM BookContentFts
                INNER JOIN BookContentChunks c ON c.Id = BookContentFts.rowid
                WHERE BookContentFts MATCH $contentQuery AND c.BookId = $bookId
                UNION ALL
                SELECT c.Id, bm25(BookContentFts, 1.0, 2.8) AS Rank
                FROM BookContentFts
                INNER JOIN BookContentChunks c ON c.Id = BookContentFts.rowid
                WHERE BookContentFts MATCH $titleQuery
                  AND c.BookId = $bookId
                  AND c.ChunkIndex = 0
            ), best_matches AS (
                SELECT Id, MIN(Rank) AS Rank
                FROM matched
                GROUP BY Id
            )
            SELECT c.Id, c.BookId, c.BookFileId, c.SourceHash, c.ChapterIndex, c.ChunkIndex,
                   c.ChapterTitle, c.ChapterPath, c.StartOffset, c.EndOffset, c.Content,
                   best_matches.Rank
            FROM best_matches
            INNER JOIN BookContentChunks c ON c.Id = best_matches.Id
            ORDER BY {orderBy}
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$contentQuery", contentMatch);
        command.Parameters.AddWithValue("$titleQuery", titleMatch);
        command.Parameters.AddWithValue("$bookId", bookId.ToString());
        command.Parameters.AddWithValue("$limit", limit == int.MaxValue ? -1 : Math.Clamp(limit, 1, 100));
        var result = new List<BookContentChunk>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadChunk(reader, includeRank: true));
        return result;
    }

    private static string BuildFtsColumnQuery(string column, IEnumerable<string> terms) =>
        string.Join(" OR ", terms.Select(term =>
            $"{column} : \"{term.Replace("\"", "\"\"")}\""));

    private async Task<IReadOnlyList<BookContentChunk>> SearchLikeAsync(
        Guid bookId,
        IReadOnlyList<string> terms,
        int limit,
        CancellationToken cancellationToken)
    {
        var selectedTerms = terms.OrderByDescending(term => term.Length).Take(5).ToArray();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        var predicates = new List<string>();
        for (var index = 0; index < selectedTerms.Length; index++)
        {
            // ChapterTitle is duplicated on every chunk. Only the chapter's
            // opening chunk represents a title-only match; content matches
            // still return every relevant chunk.
            predicates.Add($"(Content LIKE $term{index} OR (ChunkIndex = 0 AND ChapterTitle LIKE $term{index}))");
            command.Parameters.AddWithValue($"$term{index}", $"%{selectedTerms[index]}%");
        }
        command.CommandText = $"""
            SELECT Id, BookId, BookFileId, SourceHash, ChapterIndex, ChunkIndex, ChapterTitle,
                   ChapterPath, StartOffset, EndOffset, Content
            FROM BookContentChunks
            WHERE BookId = $bookId AND ({string.Join(" OR ", predicates)})
            ORDER BY ChapterIndex, ChunkIndex
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$bookId", bookId.ToString());
        command.Parameters.AddWithValue("$limit", limit == int.MaxValue ? -1 : Math.Clamp(limit, 1, 100));
        var result = new List<BookContentChunk>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadChunk(reader));
        return result;
    }

    internal static IReadOnlyList<BookContentChunk> DeduplicateSearchResults(
        IEnumerable<BookContentChunk> candidates,
        string query,
        IReadOnlyList<string> terms,
        int limit)
    {
        var results = new List<BookContentChunk>();
        var seenLocations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenContexts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenParagraphRanges = new Dictionary<string, List<(int Start, int End)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            var localOffset = FindPrimarySearchMatch(candidate.Content, query, terms);
            var location = localOffset >= 0
                ? candidate.StartOffset + localOffset
                : candidate.StartOffset;
            var key = $"{candidate.BookFileId:N}\u001f{candidate.ChapterPath}\u001f{location}";
            if (seenLocations.Contains(key)) continue;
            // One indexed chunk can end halfway through a paragraph and the
            // next overlapping chunk can report a later occurrence from that
            // same paragraph as a separate result. Both clicks then land on
            // the same rendered page. Merge overlapping paragraph spans while
            // retaining all hit highlighting inside the surviving result.
            var paragraphRange = GetSearchParagraphRange(candidate, localOffset);
            var paragraphKey = $"{candidate.BookFileId:N}\u001f{candidate.ChapterPath}";
            if (seenParagraphRanges.TryGetValue(paragraphKey, out var ranges)
                && ranges.Any(range => paragraphRange.Start < range.End && range.Start < paragraphRange.End))
            {
                continue;
            }
            // Older indexes can contain overlapping chunks whose recorded
            // offsets were produced before whitespace normalization. Their
            // numeric locations differ even though the result shown to the
            // user is identical, so also collapse equal chapter-local context.
            var context = BuildSearchContext(candidate.Content, query, terms);
            // Duplicate EPUB spine documents may expose the same text under
            // different chapter paths. Equal book-local context is still the
            // same search occurrence from the user's perspective.
            var contextKey = $"{candidate.BookFileId:N}\u001f{context}";
            if (context.Length > 0 && seenContexts.Contains(contextKey)) continue;
            seenLocations.Add(key);
            if (context.Length > 0) seenContexts.Add(contextKey);
            if (!seenParagraphRanges.TryGetValue(paragraphKey, out ranges))
            {
                ranges = [];
                seenParagraphRanges[paragraphKey] = ranges;
            }
            ranges.Add(paragraphRange);
            results.Add(candidate);
            if (results.Count >= (limit == int.MaxValue ? int.MaxValue : Math.Clamp(limit, 1, 100))) break;
        }
        return results;
    }

    private static (int Start, int End) GetSearchParagraphRange(
        BookContentChunk candidate,
        int localOffset)
    {
        var content = candidate.Content ?? string.Empty;
        if (content.Length == 0) return (candidate.StartOffset, candidate.StartOffset + 1);
        var match = Math.Clamp(localOffset, 0, content.Length - 1);
        var paragraphStart = content.LastIndexOf('\n', match);
        paragraphStart = paragraphStart < 0 ? 0 : paragraphStart + 1;
        var paragraphEnd = content.IndexOf('\n', match);
        if (paragraphEnd < 0) paragraphEnd = content.Length;
        return (
            candidate.StartOffset + paragraphStart,
            candidate.StartOffset + Math.Max(paragraphStart + 1, paragraphEnd));
    }

    private static int FindPrimarySearchMatch(
        string content,
        string query,
        IReadOnlyList<string> terms)
    {
        var exactQuery = query.Trim();
        if (exactQuery.Length > 0)
        {
            var exact = content.IndexOf(exactQuery, StringComparison.CurrentCultureIgnoreCase);
            if (exact >= 0) return exact;
        }

        var earliest = -1;
        foreach (var term in terms)
        {
            var match = content.IndexOf(term, StringComparison.CurrentCultureIgnoreCase);
            if (match >= 0 && (earliest < 0 || match < earliest)) earliest = match;
        }
        return earliest;
    }

    private static string BuildSearchContext(
        string content,
        string query,
        IReadOnlyList<string> terms)
    {
        var normalized = WhitespaceRegex().Replace(content ?? string.Empty, " ").Trim();
        if (normalized.Length == 0) return string.Empty;
        var match = FindPrimarySearchMatch(normalized, query, terms);
        if (match < 0) match = 0;
        // Keep the context anchored to the match. Rebalancing a short tail by
        // moving the window backward makes two overlapping chunks differ by a
        // few characters even though they represent the same occurrence.
        var start = Math.Max(0, match - 40);
        var end = Math.Min(normalized.Length, match + 60);
        return normalized[start..end];
    }

    internal static IReadOnlyList<string> BuildSearchTerms(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var normalized = WhitespaceRegex().Replace(query.Trim(), " ");
        foreach (var stopPhrase in ChineseStopPhrases)
            normalized = normalized.Replace(stopPhrase, string.Empty, StringComparison.OrdinalIgnoreCase);

        var terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in LatinWordRegex().Matches(normalized))
            if (match.Value.Length >= 3) terms.Add(match.Value);

        foreach (Match match in ChineseSequenceRegex().Matches(normalized))
        {
            var value = match.Value;
            if (value.Length >= 3) terms.Add(value.Length <= 18 ? value : value[..18]);
            for (var size = Math.Min(6, value.Length); size >= 3; size--)
            {
                for (var start = 0; start + size <= value.Length; start += Math.Max(1, size - 2))
                    terms.Add(value.Substring(start, size));
            }
        }

        if (terms.Count == 0)
        {
            var fallback = normalized.Trim();
            if (fallback.Length > 0) terms.Add(fallback);
        }
        return terms.OrderByDescending(term => term.Length).Take(24).ToArray();
    }

    private async Task<bool> EnsureFullTextIndexAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        using var existence = connection.CreateCommand();
        existence.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'BookContentFts');";
        var existed = Convert.ToInt32(await existence.ExecuteScalarAsync(cancellationToken)) == 1;
        try
        {
            await CreateFtsTableAsync(connection, "trigram", cancellationToken);
        }
        catch (SqliteException)
        {
            try
            {
                await CreateFtsTableAsync(connection, "unicode61 remove_diacritics 2", cancellationToken);
            }
            catch (SqliteException)
            {
                return false;
            }
        }

        using var triggers = connection.CreateCommand();
        triggers.CommandText = """
            CREATE TRIGGER IF NOT EXISTS BookContentChunks_ai AFTER INSERT ON BookContentChunks BEGIN
                INSERT INTO BookContentFts(rowid, Content, ChapterTitle)
                VALUES (new.Id, new.Content, new.ChapterTitle);
            END;
            CREATE TRIGGER IF NOT EXISTS BookContentChunks_ad AFTER DELETE ON BookContentChunks BEGIN
                INSERT INTO BookContentFts(BookContentFts, rowid, Content, ChapterTitle)
                VALUES ('delete', old.Id, old.Content, old.ChapterTitle);
            END;
            CREATE TRIGGER IF NOT EXISTS BookContentChunks_au AFTER UPDATE ON BookContentChunks BEGIN
                INSERT INTO BookContentFts(BookContentFts, rowid, Content, ChapterTitle)
                VALUES ('delete', old.Id, old.Content, old.ChapterTitle);
                INSERT INTO BookContentFts(rowid, Content, ChapterTitle)
                VALUES (new.Id, new.Content, new.ChapterTitle);
            END;
            """;
        await triggers.ExecuteNonQueryAsync(cancellationToken);
        if (!existed)
        {
            using var rebuild = connection.CreateCommand();
            rebuild.CommandText = "INSERT INTO BookContentFts(BookContentFts) VALUES('rebuild');";
            await rebuild.ExecuteNonQueryAsync(cancellationToken);
        }
        return true;
    }

    private static async Task CreateFtsTableAsync(
        SqliteConnection connection,
        string tokenizer,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            CREATE VIRTUAL TABLE IF NOT EXISTS BookContentFts USING fts5(
                Content,
                ChapterTitle,
                content='BookContentChunks',
                content_rowid='Id',
                tokenize='{tokenizer}'
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        await pragma.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private static BookContentChunk ReadChunk(SqliteDataReader reader, bool includeRank = false)
    {
        return new BookContentChunk(
            reader.GetInt64(0),
            Guid.Parse(reader.GetString(1)),
            Guid.Parse(reader.GetString(2)),
            reader.GetString(3),
            reader.GetInt32(4),
            reader.GetInt32(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetInt32(8),
            reader.GetInt32(9),
            reader.GetString(10),
            includeRank && !reader.IsDBNull(11) ? reader.GetDouble(11) : 0);
    }

    private static readonly string[] ChineseStopPhrases =
    [
        "请根据", "请帮我", "这本书", "本书", "这一章", "本章", "当前章节", "当前",
        "如何", "怎么", "什么是", "为什么", "哪些", "是否", "请", "帮我", "一下",
        "总结", "概括", "解释", "分析", "介绍", "关于", "根据"
    ];

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"[A-Za-z0-9_\-]{3,}")]
    private static partial Regex LatinWordRegex();

    [GeneratedRegex(@"[\u3400-\u9FFF]{2,}")]
    private static partial Regex ChineseSequenceRegex();
}
