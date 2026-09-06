using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Kkindle.Core;

namespace Kkindle.Infrastructure;

public sealed class SqliteBookLibraryService : IBookLibraryService
{
    // Import results are only used for the small post-import workflows (format
    // generation and optional metadata matching). Do not retain a full Book
    // object for every item in a very large folder import.
    private const int DetailedImportResultLimit = 1_000;

    private static readonly JsonSerializerOptions TrashJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".epub", ".pdf", ".mobi", ".azw3"
    };

    private readonly AppPaths _paths;
    private readonly IMetadataService _metadata;
    private readonly SemaphoreSlim _databaseGate = new(1, 1);

    public event EventHandler<LocalDataChangedEventArgs>? DataChanged;

    public SqliteBookLibraryService(AppPaths paths, IMetadataService metadata)
    {
        _paths = paths;
        _metadata = metadata;
    }

    private void NotifyDataChanged()
    {
        try
        {
            DataChanged?.Invoke(
                this,
                new LocalDataChangedEventArgs(LocalDataChangeKind.Library));
        }
        catch
        {
            // A notification must never turn a completed local write into a
            // failed library operation while the UI is shutting down.
        }
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
        await using var connection = await OpenConnectionAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS Books (
                Id TEXT PRIMARY KEY,
                Title TEXT NOT NULL,
                Authors TEXT NOT NULL,
                Series TEXT NULL,
                SeriesIndex REAL NULL,
                Description TEXT NULL,
                Publisher TEXT NULL,
                PublishDate TEXT NULL,
                Isbn TEXT NULL,
                PageCount TEXT NULL,
                Binding TEXT NULL,
                DoubanRating REAL NULL,
                DoubanRatingCount INTEGER NULL,
                Tags TEXT NOT NULL DEFAULT '',
                Category TEXT NOT NULL DEFAULT '',
                IsFavorite INTEGER NOT NULL DEFAULT 0,
                ReadingStatus INTEGER NOT NULL DEFAULT 0,
                CoverPath TEXT NULL,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS BookFiles (
                Id TEXT PRIMARY KEY,
                BookId TEXT NOT NULL REFERENCES Books(Id) ON DELETE CASCADE,
                Format TEXT NOT NULL,
                RelativePath TEXT NOT NULL,
                Size INTEGER NOT NULL,
                Sha256 TEXT NOT NULL UNIQUE
            );
            CREATE INDEX IF NOT EXISTS IX_Books_TitleAuthors ON Books(Title, Authors);
            CREATE INDEX IF NOT EXISTS IX_BookFiles_BookId ON BookFiles(BookId);
            CREATE TABLE IF NOT EXISTS BookCollections (
                Id TEXT PRIMARY KEY,
                Name TEXT NOT NULL COLLATE NOCASE UNIQUE,
                CreatedAt TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS BookCollectionItems (
                CollectionId TEXT NOT NULL REFERENCES BookCollections(Id) ON DELETE CASCADE,
                BookId TEXT NOT NULL REFERENCES Books(Id) ON DELETE CASCADE,
                AddedAt TEXT NOT NULL,
                PRIMARY KEY (CollectionId, BookId)
            );
            CREATE INDEX IF NOT EXISTS IX_BookCollectionItems_BookId ON BookCollectionItems(BookId);
            CREATE TABLE IF NOT EXISTS LibraryTrash (
                Id TEXT PRIMARY KEY,
                Kind INTEGER NOT NULL,
                BookId TEXT NOT NULL,
                Title TEXT NOT NULL,
                Format TEXT NULL,
                Size INTEGER NOT NULL DEFAULT 0,
                DeletedAt TEXT NOT NULL,
                BookJson TEXT NULL,
                FileJson TEXT NULL,
                TrashPath TEXT NOT NULL,
                OriginalPath TEXT NOT NULL,
                TrashCoverPath TEXT NULL,
                OriginalCoverPath TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_LibraryTrash_DeletedAt ON LibraryTrash(DeletedAt DESC);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await EnsureBookProductivityColumnsAsync(connection, cancellationToken);
        await EnsureDefaultCollectionAsync(connection, cancellationToken);
    }

    // The "未收藏" collection is created automatically so every imported book
    // has a default home. On first creation the currently uncollected books are
    // backfilled into it; later removals are respected (no re-backfill).
    private async Task EnsureDefaultCollectionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var collectionId = await GetCollectionIdByNameAsync(
            connection,
            BookLibraryDefaults.UncollectedCollectionName,
            cancellationToken);
        if (collectionId is null)
        {
            collectionId = Guid.NewGuid();
            var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO BookCollections (Id, Name, CreatedAt)
                VALUES ($id, $name, $createdAt);
                """;
            insert.Parameters.AddWithValue("$id", collectionId.Value.ToString());
            insert.Parameters.AddWithValue("$name", BookLibraryDefaults.UncollectedCollectionName);
            insert.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToString("O"));
            await insert.ExecuteNonQueryAsync(cancellationToken);

            var backfill = connection.CreateCommand();
            backfill.CommandText = """
                INSERT INTO BookCollectionItems (CollectionId, BookId, AddedAt)
                SELECT $collectionId, Id, $addedAt
                FROM Books
                WHERE NOT EXISTS (
                    SELECT 1 FROM BookCollectionItems
                    WHERE BookCollectionItems.BookId = Books.Id
                );
                """;
            backfill.Parameters.AddWithValue("$collectionId", collectionId.Value.ToString());
            backfill.Parameters.AddWithValue("$addedAt", DateTimeOffset.UtcNow.ToString("O"));
            await backfill.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<Guid?> GetCollectionIdByNameAsync(
        SqliteConnection connection,
        string name,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = "SELECT Id FROM BookCollections WHERE Name = $name COLLATE NOCASE LIMIT 1;";
        command.Parameters.AddWithValue("$name", name);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is string id && Guid.TryParse(id, out var parsed) ? parsed : null;
    }

    public async Task<IReadOnlyList<Book>> SearchAsync(string? query = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Title, Authors, Series, SeriesIndex, Description, Tags, Category, IsFavorite, ReadingStatus, CoverPath, CreatedAt, UpdatedAt,
                   Publisher, PublishDate, Isbn, PageCount, Binding, DoubanRating, DoubanRatingCount
            FROM Books
            WHERE $query = '' OR Title LIKE $like OR Authors LIKE $like OR Tags LIKE $like OR Series LIKE $like
            ORDER BY UpdatedAt DESC, Title COLLATE NOCASE;
            """;
        var text = query?.Trim() ?? string.Empty;
        command.Parameters.AddWithValue("$query", text);
        command.Parameters.AddWithValue("$like", $"%{text}%");

        var books = new List<Book>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                books.Add(ReadBook(reader));
        }

        var filesByBook = await ReadFilesByBookAsync(connection, books.Select(book => book.Id), cancellationToken);
        var collectionsByBook = await ReadCollectionIdsByBookAsync(
            connection,
            books.Select(book => book.Id),
            cancellationToken);
        foreach (var book in books)
        {
            book.Files = filesByBook.GetValueOrDefault(book.Id) ?? [];
            book.CollectionIds = collectionsByBook.GetValueOrDefault(book.Id) ?? [];
        }

        return books;
    }

    public async Task<ImportBatchResult> ImportAsync(
        IEnumerable<string> paths,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default,
        Func<ImportBookConflict, Task<ImportConflictResolution>>? conflictResolver = null)
    {
        var files = ExpandInputFiles(paths).ToList();
        var result = new ImportBatchResult();
        result.BookDetailsAvailable = files.Count <= DetailedImportResultLimit;
        var totalBytes = files.Sum(GetFileLengthSafe);
        long completedBytes = 0;

        foreach (var sourcePath in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var file = new FileInfo(sourcePath);
                if (!file.Exists)
                    throw new FileNotFoundException("文件不存在", sourcePath);

                var hash = await Hashing.Sha256Async(sourcePath, cancellationToken);
                await using var connection = await OpenConnectionAsync(cancellationToken);
                var duplicate = await FindBookByHashAsync(connection, hash, cancellationToken);
                if (duplicate is not null)
                {
                    result.Items.Add(new ImportItemResult(
                        sourcePath,
                        true,
                        "已存在，跳过重复文件",
                        result.BookDetailsAvailable ? duplicate : null,
                        BookId: duplicate.Id));
                    completedBytes += file.Length;
                    progress?.Report(new TransferProgress(completedBytes, totalBytes, $"已检查 {file.Name}"));
                    continue;
                }

                var metadata = await _metadata.ReadMetadataAsync(sourcePath, cancellationToken);
                var title = string.IsNullOrWhiteSpace(metadata.Title) ? Path.GetFileNameWithoutExtension(sourcePath) : metadata.Title.Trim();
                var authors = string.IsNullOrWhiteSpace(metadata.Authors) ? "未知作者" : metadata.Authors.Trim();
                Book? book = await FindBookByTitleAuthorsAsync(connection, title, authors, cancellationToken);
                if (book is not null && conflictResolver is not null)
                {
                    book.Files = await ReadFilesAsync(connection, book.Id, cancellationToken);
                    var resolution = await conflictResolver(new ImportBookConflict(
                        sourcePath,
                        book,
                        title,
                        authors,
                        Path.GetExtension(sourcePath).TrimStart('.').ToLowerInvariant()));
                    if (resolution == ImportConflictResolution.Skip)
                    {
                        result.Items.Add(new ImportItemResult(
                            sourcePath,
                            true,
                            "已跳过同名版本",
                            result.BookDetailsAvailable ? book : null,
                            Added: false,
                            BookId: book.Id));
                        completedBytes += file.Length;
                        progress?.Report(new TransferProgress(completedBytes, totalBytes, $"已跳过 {file.Name}"));
                        continue;
                    }

                    if (resolution == ImportConflictResolution.KeepSeparate)
                        book = null;
                }
                var newBook = book is null;
                book ??= new Book
                {
                    Id = Guid.NewGuid(),
                    Title = title,
                    Authors = authors,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                };

                book.Series ??= metadata.Series;
                book.SeriesIndex ??= metadata.SeriesIndex;
                book.Description ??= metadata.Description;
                book.UpdatedAt = DateTimeOffset.UtcNow;

                var bookDirectory = Path.Combine(_paths.Library, book.Id.ToString("N"));
                Directory.CreateDirectory(bookDirectory);
                var targetName = GetUniqueFileName(bookDirectory, Path.GetFileName(sourcePath));
                var targetPath = Path.Combine(bookDirectory, targetName);
                var temporaryPath = targetPath + ".part";
                try
                {
                    await CopyFileAsync(sourcePath, temporaryPath, file.Length, completedBytes, totalBytes, progress, cancellationToken);
                    File.Move(temporaryPath, targetPath, true);
                }
                finally
                {
                    if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                }

                if (newBook)
                {
                    await InsertBookAsync(connection, book, cancellationToken);
                }
                else
                {
                    await UpdateBookRowAsync(connection, book, cancellationToken);
                }

                if (newBook)
                {
                    var defaultCollectionId = await GetCollectionIdByNameAsync(
                        connection,
                        BookLibraryDefaults.UncollectedCollectionName,
                        cancellationToken);
                    if (defaultCollectionId is not null)
                    {
                        var membership = connection.CreateCommand();
                        membership.CommandText = """
                            INSERT OR IGNORE INTO BookCollectionItems (CollectionId, BookId, AddedAt)
                            VALUES ($collectionId, $bookId, $addedAt);
                            """;
                        membership.Parameters.AddWithValue("$collectionId", defaultCollectionId.Value.ToString());
                        membership.Parameters.AddWithValue("$bookId", book.Id.ToString());
                        membership.Parameters.AddWithValue("$addedAt", DateTimeOffset.UtcNow.ToString("O"));
                        await membership.ExecuteNonQueryAsync(cancellationToken);
                        book.CollectionIds.Add(defaultCollectionId.Value);
                    }
                }

                var relativePath = Path.GetRelativePath(_paths.Data, targetPath);
                var bookFile = new BookFile
                {
                    Id = Guid.NewGuid(),
                    BookId = book.Id,
                    Format = Path.GetExtension(sourcePath).TrimStart('.').ToLowerInvariant(),
                    RelativePath = relativePath,
                    Size = file.Length,
                    Sha256 = hash
                };
                await InsertFileAsync(connection, bookFile, cancellationToken);
                book.Files.Add(bookFile);

                if (metadata.CoverBytes is { Length: > 0 } && string.IsNullOrWhiteSpace(book.CoverPath))
                {
                    var coverName = $"{book.Id:N}{NormalizeCoverExtension(metadata.CoverExtension)}";
                    var coverPath = Path.Combine(_paths.Covers, coverName);
                    await File.WriteAllBytesAsync(coverPath, metadata.CoverBytes, cancellationToken);
                    book.CoverPath = Path.GetRelativePath(_paths.Data, coverPath);
                    await UpdateBookRowAsync(connection, book, cancellationToken);
                }

                result.Items.Add(new ImportItemResult(
                    sourcePath,
                    true,
                    newBook ? "已导入" : "已添加新格式",
                    result.BookDetailsAvailable ? book : null,
                    Added: true,
                    BookId: book.Id));
                NotifyDataChanged();
                completedBytes += file.Length;
                progress?.Report(new TransferProgress(completedBytes, totalBytes, $"已导入 {file.Name}"));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                result.Items.Add(new ImportItemResult(sourcePath, false, ex.Message, null));
            }
        }

        return result;
    }

    public async Task<BookFile> AddFileToBookAsync(
        Guid bookId,
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        var source = Path.GetFullPath(sourcePath);
        if (!File.Exists(source))
            throw new FileNotFoundException("待添加的书籍文件不存在。", source);

        var format = BookFormatConversionPolicy.Normalize(Path.GetExtension(source));
        if (!BookFormatConversionPolicy.IsConvertibleFormat(format))
            throw new NotSupportedException("书库只允许添加 EPUB、AZW3、PDF 或 MOBI 格式。");

        var fileInfo = new FileInfo(source);
        var hash = await Hashing.Sha256Async(source, cancellationToken);
        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            if (!await BookExistsAsync(connection, bookId, cancellationToken))
                throw new InvalidOperationException("目标书籍已不存在，请刷新书库后重试。 ");

            if (await FindFileByHashAsync(connection, hash, cancellationToken) is not null)
                throw new InvalidOperationException("相同文件已经在书库中。 ");

            var bookDirectory = Path.Combine(_paths.Library, bookId.ToString("N"));
            Directory.CreateDirectory(bookDirectory);
            var targetName = GetUniqueFileName(bookDirectory, Path.GetFileName(source));
            var targetPath = Path.Combine(bookDirectory, targetName);
            var temporaryPath = targetPath + ".part";
            var targetCreated = false;
            var fileRowCreated = false;
            try
            {
                await CopyFileAsync(
                    source,
                    temporaryPath,
                    fileInfo.Length,
                    completed: 0,
                    total: fileInfo.Length,
                    progress: null,
                    cancellationToken);
                File.Move(temporaryPath, targetPath, true);
                targetCreated = true;

                var touch = connection.CreateCommand();
                touch.CommandText = "UPDATE Books SET UpdatedAt = $updatedAt WHERE Id = $bookId;";
                touch.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
                touch.Parameters.AddWithValue("$bookId", bookId.ToString());
                await touch.ExecuteNonQueryAsync(cancellationToken);

                var bookFile = new BookFile
                {
                    Id = Guid.NewGuid(),
                    BookId = bookId,
                    Format = format,
                    RelativePath = Path.GetRelativePath(_paths.Data, targetPath),
                    Size = fileInfo.Length,
                    Sha256 = hash
                };
                await InsertFileAsync(connection, bookFile, cancellationToken);
                fileRowCreated = true;
                NotifyDataChanged();
                return bookFile;
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    TryDeleteFile(temporaryPath);
                if (targetCreated && !fileRowCreated)
                    TryDeleteFile(targetPath);
            }
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    public async Task UpdateMetadataAsync(Book book, CancellationToken cancellationToken = default)
    {
        book.UpdatedAt = DateTimeOffset.UtcNow;
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await UpdateBookRowAsync(connection, book, cancellationToken);
        NotifyDataChanged();
    }

    public async Task DeleteFileAsync(
        Guid bookId,
        Guid bookFileId,
        CancellationToken cancellationToken = default)
    {
        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            var book = await ReadBookByIdAsync(connection, bookId, cancellationToken)
                ?? throw new FileNotFoundException("指定书籍不存在。");
            var file = book.Files.FirstOrDefault(item => item.Id == bookFileId)
                ?? throw new FileNotFoundException("指定书籍格式不存在。");

            if (book.Files.Count <= 1)
                await MoveBookToTrashAsync(connection, book, cancellationToken);
            else
                await MoveFileToTrashAsync(connection, book, file, cancellationToken);
            NotifyDataChanged();
        }
        finally { _databaseGate.Release(); }
    }

    public async Task DeleteAsync(Guid bookId, CancellationToken cancellationToken = default)
    {
        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            var book = await ReadBookByIdAsync(connection, bookId, cancellationToken);
            if (book is null)
                return;

            await MoveBookToTrashAsync(connection, book, cancellationToken);
            NotifyDataChanged();
        }
        finally { _databaseGate.Release(); }
    }

    public async Task<IReadOnlyList<LibraryTrashItem>> GetTrashItemsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Kind, BookId, Title, Format, Size, DeletedAt
            FROM LibraryTrash
            ORDER BY DeletedAt DESC;
            """;
        var items = new List<LibraryTrashItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!Guid.TryParse(reader.GetString(0), out var id)
                || !Guid.TryParse(reader.GetString(2), out var bookId)
                || !Enum.IsDefined(typeof(LibraryTrashItemKind), reader.GetInt32(1)))
                continue;

            items.Add(new LibraryTrashItem(
                id,
                (LibraryTrashItemKind)reader.GetInt32(1),
                bookId,
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetInt64(5),
                DateTimeOffset.Parse(reader.GetString(6))));
        }
        return items;
    }

    public async Task RestoreTrashItemAsync(
        Guid trashItemId,
        CancellationToken cancellationToken = default)
    {
        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            var entry = await ReadTrashEntryAsync(connection, trashItemId, cancellationToken)
                ?? throw new FileNotFoundException("回收站项目不存在，可能已被其他设备处理。");

            if (entry.Kind == LibraryTrashItemKind.Book)
                await RestoreBookFromTrashAsync(connection, entry, cancellationToken);
            else
                await RestoreFileFromTrashAsync(connection, entry, cancellationToken);
            NotifyDataChanged();
        }
        finally { _databaseGate.Release(); }
    }

    public async Task PurgeTrashItemAsync(
        Guid trashItemId,
        CancellationToken cancellationToken = default)
    {
        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            var entry = await ReadTrashEntryAsync(connection, trashItemId, cancellationToken)
                ?? throw new FileNotFoundException("回收站项目不存在，可能已被其他设备处理。");
            DeletePath(ResolveDataPath(entry.TrashPath));
            if (!string.IsNullOrWhiteSpace(entry.TrashCoverPath))
                DeletePath(ResolveDataPath(entry.TrashCoverPath));

            var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM LibraryTrash WHERE Id = $id;";
            command.Parameters.AddWithValue("$id", trashItemId.ToString());
            await command.ExecuteNonQueryAsync(cancellationToken);
            NotifyDataChanged();
        }
        finally { _databaseGate.Release(); }
    }

    private async Task MoveBookToTrashAsync(
        SqliteConnection connection,
        Book book,
        CancellationToken cancellationToken)
    {
        var trashId = Guid.NewGuid();
        var trashDirectory = Path.Combine(_paths.Trash, "books", trashId.ToString("N"));
        var originalDirectory = Path.Combine(_paths.Library, book.Id.ToString("N"));
        var trashPath = Path.GetRelativePath(_paths.Data, trashDirectory);
        var originalPath = Path.GetRelativePath(_paths.Data, originalDirectory);
        var trashCoverPath = string.IsNullOrWhiteSpace(book.CoverPath)
            ? null
            : Path.GetRelativePath(
                _paths.Data,
                Path.Combine(_paths.Trash, "covers", trashId.ToString("N"), "cover" + Path.GetExtension(book.CoverPath)));
        var originalCoverPath = book.CoverPath;

        try
        {
            MovePath(originalDirectory, trashDirectory);
            if (!string.IsNullOrWhiteSpace(originalCoverPath))
            {
                var originalCover = ResolveDataPath(originalCoverPath);
                var movedCover = ResolveDataPath(trashCoverPath!);
                MovePath(originalCover, movedCover);
            }
        }
        catch
        {
            MovePathBack(trashDirectory, originalDirectory);
            if (!string.IsNullOrWhiteSpace(trashCoverPath))
                MovePathBack(ResolveDataPath(trashCoverPath), ResolveDataPath(originalCoverPath!));
            throw;
        }

        var entry = new TrashEntry
        {
            Id = trashId,
            Kind = LibraryTrashItemKind.Book,
            BookId = book.Id,
            Title = book.Title,
            Format = null,
            Size = book.Files.Sum(file => Math.Max(0, file.Size)),
            DeletedAt = DateTimeOffset.UtcNow,
            BookJson = JsonSerializer.Serialize(book, TrashJsonOptions),
            TrashPath = trashPath,
            OriginalPath = originalPath,
            TrashCoverPath = trashCoverPath,
            OriginalCoverPath = originalCoverPath
        };

        try
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            try
            {
                await InsertTrashEntryAsync(connection, transaction, entry, cancellationToken);
                var delete = connection.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM Books WHERE Id = $id;";
                delete.Parameters.AddWithValue("$id", book.Id.ToString());
                await delete.ExecuteNonQueryAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                throw;
            }
        }
        catch
        {
            MovePathBack(trashDirectory, originalDirectory);
            if (!string.IsNullOrWhiteSpace(trashCoverPath))
                MovePathBack(ResolveDataPath(trashCoverPath), ResolveDataPath(originalCoverPath!));
            throw;
        }
    }

    private async Task MoveFileToTrashAsync(
        SqliteConnection connection,
        Book book,
        BookFile file,
        CancellationToken cancellationToken)
    {
        var trashId = Guid.NewGuid();
        var originalPath = file.RelativePath;
        var sourcePath = ResolveDataPath(originalPath);
        var trashDirectory = Path.Combine(_paths.Trash, "files", trashId.ToString("N"));
        var trashFilePath = Path.Combine(trashDirectory, SanitizeFileName(Path.GetFileName(originalPath)));
        var trashPath = Path.GetRelativePath(_paths.Data, trashFilePath);
        MovePath(sourcePath, trashFilePath);

        var entry = new TrashEntry
        {
            Id = trashId,
            Kind = LibraryTrashItemKind.File,
            BookId = book.Id,
            Title = book.Title,
            Format = file.Format,
            Size = file.Size,
            DeletedAt = DateTimeOffset.UtcNow,
            FileJson = JsonSerializer.Serialize(file, TrashJsonOptions),
            TrashPath = trashPath,
            OriginalPath = originalPath
        };

        try
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            try
            {
                await InsertTrashEntryAsync(connection, transaction, entry, cancellationToken);
                var delete = connection.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM BookFiles WHERE Id = $id AND BookId = $bookId;";
                delete.Parameters.AddWithValue("$id", file.Id.ToString());
                delete.Parameters.AddWithValue("$bookId", book.Id.ToString());
                await delete.ExecuteNonQueryAsync(cancellationToken);

                var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = "UPDATE Books SET UpdatedAt = $updatedAt WHERE Id = $bookId;";
                update.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
                update.Parameters.AddWithValue("$bookId", book.Id.ToString());
                await update.ExecuteNonQueryAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                throw;
            }
        }
        catch
        {
            MovePathBack(trashFilePath, sourcePath);
            throw;
        }
    }

    private async Task RestoreBookFromTrashAsync(
        SqliteConnection connection,
        TrashEntry entry,
        CancellationToken cancellationToken)
    {
        var book = JsonSerializer.Deserialize<Book>(entry.BookJson ?? string.Empty, TrashJsonOptions)
            ?? throw new InvalidDataException("回收站中的书籍记录无法读取。");
        book.Files ??= [];
        book.CollectionIds ??= [];
        if (await BookExistsAsync(connection, book.Id, cancellationToken))
            throw new InvalidOperationException("同 ID 的书籍已经存在，无法恢复。");
        foreach (var file in book.Files)
        {
            if (await FindFileByHashAsync(connection, file.Sha256, cancellationToken) is not null)
                throw new InvalidOperationException($"恢复失败：文件 {file.Format.ToUpperInvariant()} 已存在于书库中。");
        }

        var sourceDirectory = ResolveDataPath(entry.TrashPath);
        var targetDirectory = ResolveDataPath(entry.OriginalPath);
        var targetCover = string.IsNullOrWhiteSpace(entry.OriginalCoverPath)
            ? null
            : ResolveDataPath(entry.OriginalCoverPath);
        var sourceCover = string.IsNullOrWhiteSpace(entry.TrashCoverPath)
            ? null
            : ResolveDataPath(entry.TrashCoverPath);
        EnsureMoveTargetAvailable(targetDirectory);
        if (targetCover is not null) EnsureMoveTargetAvailable(targetCover);

        MovePath(sourceDirectory, targetDirectory);
        if (sourceCover is not null && targetCover is not null)
            MovePath(sourceCover, targetCover);
        book.UpdatedAt = DateTimeOffset.UtcNow;

        try
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            try
            {
                await InsertBookAsync(connection, book, cancellationToken, transaction);
                foreach (var file in book.Files)
                    await InsertFileAsync(connection, file, cancellationToken, transaction);
                foreach (var collectionId in book.CollectionIds)
                    await RestoreCollectionMembershipAsync(connection, transaction, collectionId, book.Id, cancellationToken);
                await DeleteTrashEntryAsync(connection, transaction, entry.Id, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                throw;
            }
        }
        catch
        {
            MovePathBack(targetDirectory, sourceDirectory);
            if (sourceCover is not null && targetCover is not null)
                MovePathBack(targetCover, sourceCover);
            throw;
        }
    }

    private async Task RestoreFileFromTrashAsync(
        SqliteConnection connection,
        TrashEntry entry,
        CancellationToken cancellationToken)
    {
        var file = JsonSerializer.Deserialize<BookFile>(entry.FileJson ?? string.Empty, TrashJsonOptions)
            ?? throw new InvalidDataException("回收站中的文件记录无法读取。");
        if (!await BookExistsAsync(connection, file.BookId, cancellationToken))
            throw new InvalidOperationException("原书籍已经不存在，无法恢复此格式。");
        if (await FindFileByHashAsync(connection, file.Sha256, cancellationToken) is not null)
            throw new InvalidOperationException("相同文件已经在书库中，无法重复恢复。");

        var sourcePath = ResolveDataPath(entry.TrashPath);
        var targetPath = ResolveDataPath(entry.OriginalPath);
        EnsureMoveTargetAvailable(targetPath);
        MovePath(sourcePath, targetPath);

        try
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            try
            {
                await InsertFileAsync(connection, file, cancellationToken, transaction);
                var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = "UPDATE Books SET UpdatedAt = $updatedAt WHERE Id = $bookId;";
                update.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
                update.Parameters.AddWithValue("$bookId", file.BookId.ToString());
                await update.ExecuteNonQueryAsync(cancellationToken);
                await DeleteTrashEntryAsync(connection, transaction, entry.Id, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                throw;
            }
        }
        catch
        {
            MovePathBack(targetPath, sourcePath);
            throw;
        }
    }

    private static async Task RestoreCollectionMembershipAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid collectionId,
        Guid bookId,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO BookCollectionItems (CollectionId, BookId, AddedAt)
            SELECT $collectionId, $bookId, $addedAt
            WHERE EXISTS(SELECT 1 FROM BookCollections WHERE Id = $collectionId);
            """;
        command.Parameters.AddWithValue("$collectionId", collectionId.ToString());
        command.Parameters.AddWithValue("$bookId", bookId.ToString());
        command.Parameters.AddWithValue("$addedAt", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<BookCollection>> GetCollectionsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Name, CreatedAt FROM BookCollections ORDER BY Name COLLATE NOCASE;";
        var collections = new List<BookCollection>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            collections.Add(new BookCollection
            {
                Id = Guid.Parse(reader.GetString(0)),
                Name = reader.GetString(1),
                CreatedAt = DateTimeOffset.Parse(reader.GetString(2))
            });
        }
        return collections;
    }

    public async Task<BookCollection> CreateCollectionAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        var normalizedName = (name ?? string.Empty).Trim();
        if (normalizedName.Length == 0)
            throw new ArgumentException("收藏夹名称不能为空。", nameof(name));
        if (normalizedName.Length > 60)
            throw new ArgumentException("收藏夹名称不能超过 60 个字符。", nameof(name));

        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            var existing = connection.CreateCommand();
            existing.CommandText = "SELECT EXISTS(SELECT 1 FROM BookCollections WHERE Name = $name COLLATE NOCASE);";
            existing.Parameters.AddWithValue("$name", normalizedName);
            if (Convert.ToInt64(await existing.ExecuteScalarAsync(cancellationToken)) != 0)
                throw new InvalidOperationException("同名收藏夹已经存在。");

            var collection = new BookCollection
            {
                Id = Guid.NewGuid(),
                Name = normalizedName,
                CreatedAt = DateTimeOffset.UtcNow
            };
            var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO BookCollections (Id, Name, CreatedAt) VALUES ($id, $name, $createdAt);";
            insert.Parameters.AddWithValue("$id", collection.Id.ToString());
            insert.Parameters.AddWithValue("$name", collection.Name);
            insert.Parameters.AddWithValue("$createdAt", collection.CreatedAt.ToString("O"));
            await insert.ExecuteNonQueryAsync(cancellationToken);
            NotifyDataChanged();
            return collection;
        }
        finally { _databaseGate.Release(); }
    }

    public async Task DeleteCollectionAsync(Guid collectionId, CancellationToken cancellationToken = default)
    {
        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            try
            {
                var deleteItems = connection.CreateCommand();
                deleteItems.Transaction = transaction;
                deleteItems.CommandText = "DELETE FROM BookCollectionItems WHERE CollectionId = $id;";
                deleteItems.Parameters.AddWithValue("$id", collectionId.ToString());
                await deleteItems.ExecuteNonQueryAsync(cancellationToken);

                var deleteCollection = connection.CreateCommand();
                deleteCollection.Transaction = transaction;
                deleteCollection.CommandText = "DELETE FROM BookCollections WHERE Id = $id;";
                deleteCollection.Parameters.AddWithValue("$id", collectionId.ToString());
                await deleteCollection.ExecuteNonQueryAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                NotifyDataChanged();
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                throw;
            }
        }
        finally { _databaseGate.Release(); }
    }

    public Task AddBookToCollectionAsync(
        Guid bookId,
        Guid collectionId,
        CancellationToken cancellationToken = default) =>
        SetBookCollectionMembershipAsync(bookId, collectionId, add: true, cancellationToken);

    public Task RemoveBookFromCollectionAsync(
        Guid bookId,
        Guid collectionId,
        CancellationToken cancellationToken = default) =>
        SetBookCollectionMembershipAsync(bookId, collectionId, add: false, cancellationToken);

    private async Task SetBookCollectionMembershipAsync(
        Guid bookId,
        Guid collectionId,
        bool add,
        CancellationToken cancellationToken)
    {
        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            var command = connection.CreateCommand();
            command.CommandText = add
                ? """
                  INSERT OR IGNORE INTO BookCollectionItems (CollectionId, BookId, AddedAt)
                  SELECT $collectionId, $bookId, $addedAt
                  WHERE EXISTS(SELECT 1 FROM BookCollections WHERE Id = $collectionId)
                    AND EXISTS(SELECT 1 FROM Books WHERE Id = $bookId);
                  """
                : "DELETE FROM BookCollectionItems WHERE CollectionId = $collectionId AND BookId = $bookId;";
            command.Parameters.AddWithValue("$collectionId", collectionId.ToString());
            command.Parameters.AddWithValue("$bookId", bookId.ToString());
            if (add) command.Parameters.AddWithValue("$addedAt", DateTimeOffset.UtcNow.ToString("O"));
            var changed = await command.ExecuteNonQueryAsync(cancellationToken);
            if (changed > 0)
                NotifyDataChanged();
            if (add && changed == 0)
            {
                var verify = connection.CreateCommand();
                verify.CommandText = "SELECT EXISTS(SELECT 1 FROM BookCollectionItems WHERE CollectionId = $collectionId AND BookId = $bookId);";
                verify.Parameters.AddWithValue("$collectionId", collectionId.ToString());
                verify.Parameters.AddWithValue("$bookId", bookId.ToString());
                if (Convert.ToInt64(await verify.ExecuteScalarAsync(cancellationToken)) == 0)
                    throw new InvalidOperationException("书籍或收藏夹已不存在。");
            }
        }
        finally { _databaseGate.Release(); }
    }

    public string GetAbsoluteFilePath(BookFile file) => ResolveDataPath(file.RelativePath);

    private string ResolveDataPath(string relativePath)
    {
        var full = Path.GetFullPath(Path.Combine(_paths.Data, relativePath));
        var dataRoot = Path.GetFullPath(_paths.Data + Path.DirectorySeparatorChar);
        if (!full.StartsWith(dataRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("书籍路径不在应用数据目录内。");
        return full;
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var foreignKeys = connection.CreateCommand();
        foreignKeys.CommandText = "PRAGMA foreign_keys = ON;";
        await foreignKeys.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private static IEnumerable<string> ExpandInputFiles(IEnumerable<string> paths)
    {
        foreach (var input in paths.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            if (File.Exists(input) && SupportedExtensions.Contains(Path.GetExtension(input)))
            {
                yield return Path.GetFullPath(input);
                continue;
            }

            if (Directory.Exists(input))
            {
                foreach (var file in Directory.EnumerateFiles(input, "*.*", SearchOption.AllDirectories)
                    .Where(x => SupportedExtensions.Contains(Path.GetExtension(x))))
                    yield return Path.GetFullPath(file);
            }
        }
    }

    private static long GetFileLengthSafe(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return 0; }
    }

    private static string GetUniqueFileName(string directory, string originalName)
    {
        var safe = SanitizeFileName(originalName);
        var candidate = Path.Combine(directory, safe);
        if (!File.Exists(candidate)) return safe;
        var stem = Path.GetFileNameWithoutExtension(safe);
        var extension = Path.GetExtension(safe);
        for (var index = 2; ; index++)
        {
            var name = $"{stem} ({index}){extension}";
            if (!File.Exists(Path.Combine(directory, name))) return name;
        }
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
            builder.Append(invalid.Contains(character) ? '_' : character);
        var result = builder.ToString().Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(result) ? "book.bin" : result;
    }

    private static string NormalizeCoverExtension(string? extension)
    {
        var value = extension?.Trim().ToLowerInvariant() ?? ".jpg";
        return value is ".jpg" or ".jpeg" or ".png" or ".webp" ? value : ".jpg";
    }

    private async Task CopyFileAsync(string source, string target, long fileLength, long completed, long total, IProgress<TransferProgress>? progress, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[128 * 1024];
        long copied = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            copied += read;
            progress?.Report(new TransferProgress(completed + copied, total, $"正在复制 {Path.GetFileName(source)}"));
        }
        await output.FlushAsync(cancellationToken);
        if (copied != fileLength) throw new IOException("复制后的文件大小不一致。");
    }

    private static Book ReadBook(SqliteDataReader reader)
    {
        return new Book
        {
            Id = Guid.Parse(reader.GetString(0)),
            Title = reader.GetString(1),
            Authors = reader.GetString(2),
            Series = reader.IsDBNull(3) ? null : reader.GetString(3),
            SeriesIndex = reader.IsDBNull(4) ? null : reader.GetDouble(4),
            Description = reader.IsDBNull(5) ? null : reader.GetString(5),
            Tags = reader.GetString(6),
            Category = reader.GetString(7),
            IsFavorite = reader.GetInt64(8) != 0,
            ReadingStatus = Enum.IsDefined(typeof(LibraryReadingStatus), reader.GetInt32(9))
                ? (LibraryReadingStatus)reader.GetInt32(9)
                : LibraryReadingStatus.Unread,
            CoverPath = reader.IsDBNull(10) ? null : reader.GetString(10),
            CreatedAt = DateTimeOffset.Parse(reader.GetString(11)),
            UpdatedAt = DateTimeOffset.Parse(reader.GetString(12)),
            Publisher = reader.IsDBNull(13) ? null : reader.GetString(13),
            PublishDate = reader.IsDBNull(14) ? null : reader.GetString(14),
            Isbn = reader.IsDBNull(15) ? null : reader.GetString(15),
            PageCount = reader.IsDBNull(16) ? null : reader.GetString(16),
            Binding = reader.IsDBNull(17) ? null : reader.GetString(17),
            DoubanRating = reader.IsDBNull(18) ? null : reader.GetDouble(18),
            DoubanRatingCount = reader.IsDBNull(19) ? null : reader.GetInt32(19)
        };
    }

    private static async Task<List<BookFile>> ReadFilesAsync(SqliteConnection connection, Guid bookId, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, BookId, Format, RelativePath, Size, Sha256 FROM BookFiles WHERE BookId = $bookId ORDER BY Format;";
        command.Parameters.AddWithValue("$bookId", bookId.ToString());
        var files = new List<BookFile>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            files.Add(new BookFile
            {
                Id = Guid.Parse(reader.GetString(0)),
                BookId = Guid.Parse(reader.GetString(1)),
                Format = reader.GetString(2),
                RelativePath = reader.GetString(3),
                Size = reader.GetInt64(4),
                Sha256 = reader.GetString(5)
            });
        }
        return files;
    }

    private static async Task<Dictionary<Guid, List<BookFile>>> ReadFilesByBookAsync(
        SqliteConnection connection,
        IEnumerable<Guid> bookIds,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, List<BookFile>>();
        foreach (var batch in bookIds.Chunk(500))
        {
            var command = connection.CreateCommand();
            var parameters = batch.Select((_, index) => $"$book{index}").ToArray();
            command.CommandText = $"""
                SELECT Id, BookId, Format, RelativePath, Size, Sha256
                FROM BookFiles
                WHERE BookId IN ({string.Join(", ", parameters)})
                ORDER BY BookId, Format;
                """;
            for (var index = 0; index < batch.Length; index++)
                command.Parameters.AddWithValue(parameters[index], batch[index].ToString());

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var bookId = Guid.Parse(reader.GetString(1));
                if (!result.TryGetValue(bookId, out var files))
                {
                    files = [];
                    result[bookId] = files;
                }
                files.Add(new BookFile
                {
                    Id = Guid.Parse(reader.GetString(0)),
                    BookId = bookId,
                    Format = reader.GetString(2),
                    RelativePath = reader.GetString(3),
                    Size = reader.GetInt64(4),
                    Sha256 = reader.GetString(5)
                });
            }
        }
        return result;
    }

    private static async Task<Dictionary<Guid, List<Guid>>> ReadCollectionIdsByBookAsync(
        SqliteConnection connection,
        IEnumerable<Guid> bookIds,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, List<Guid>>();
        foreach (var batch in bookIds.Chunk(500))
        {
            var command = connection.CreateCommand();
            var parameters = batch.Select((_, index) => $"$book{index}").ToArray();
            command.CommandText = $"""
                SELECT BookId, CollectionId
                FROM BookCollectionItems
                WHERE BookId IN ({string.Join(", ", parameters)});
                """;
            for (var index = 0; index < batch.Length; index++)
                command.Parameters.AddWithValue(parameters[index], batch[index].ToString());

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var bookId = Guid.Parse(reader.GetString(0));
                if (!result.TryGetValue(bookId, out var collectionIds))
                {
                    collectionIds = [];
                    result[bookId] = collectionIds;
                }
                collectionIds.Add(Guid.Parse(reader.GetString(1)));
            }
        }
        return result;
    }

    private static async Task<Book?> FindBookByHashAsync(SqliteConnection connection, string hash, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT b.Id, b.Title, b.Authors, b.Series, b.SeriesIndex, b.Description, b.Tags, b.Category, b.IsFavorite, b.ReadingStatus, b.CoverPath, b.CreatedAt, b.UpdatedAt,
                   b.Publisher, b.PublishDate, b.Isbn, b.PageCount, b.Binding, b.DoubanRating, b.DoubanRatingCount
            FROM Books b INNER JOIN BookFiles f ON f.BookId = b.Id WHERE f.Sha256 = $hash LIMIT 1;
            """;
        command.Parameters.AddWithValue("$hash", hash);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadBook(reader) : null;
    }

    private static async Task<Book?> FindBookByTitleAuthorsAsync(SqliteConnection connection, string title, string authors, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Title, Authors, Series, SeriesIndex, Description, Tags, Category, IsFavorite, ReadingStatus, CoverPath, CreatedAt, UpdatedAt,
                   Publisher, PublishDate, Isbn, PageCount, Binding, DoubanRating, DoubanRatingCount
            FROM Books WHERE lower(Title) = lower($title) AND lower(Authors) = lower($authors) LIMIT 1;
            """;
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$authors", authors);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadBook(reader) : null;
    }

    private static async Task<Book?> ReadBookByIdAsync(
        SqliteConnection connection,
        Guid bookId,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Title, Authors, Series, SeriesIndex, Description, Tags, Category, IsFavorite, ReadingStatus, CoverPath, CreatedAt, UpdatedAt,
                   Publisher, PublishDate, Isbn, PageCount, Binding, DoubanRating, DoubanRatingCount
            FROM Books WHERE Id = $id LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", bookId.ToString());
        Book? book;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken)) return null;
            book = ReadBook(reader);
        }
        book.Files = await ReadFilesAsync(connection, book.Id, cancellationToken);
        book.CollectionIds = (await ReadCollectionIdsByBookAsync(connection, [book.Id], cancellationToken))
            .GetValueOrDefault(book.Id) ?? [];
        return book;
    }

    private static async Task<bool> BookExistsAsync(
        SqliteConnection connection,
        Guid bookId,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM Books WHERE Id = $bookId);";
        command.Parameters.AddWithValue("$bookId", bookId.ToString());
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) != 0;
    }

    private static async Task<BookFile?> FindFileByHashAsync(
        SqliteConnection connection,
        string hash,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, BookId, Format, RelativePath, Size, Sha256 FROM BookFiles WHERE Sha256 = $hash LIMIT 1;";
        command.Parameters.AddWithValue("$hash", hash);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new BookFile
        {
            Id = Guid.Parse(reader.GetString(0)),
            BookId = Guid.Parse(reader.GetString(1)),
            Format = reader.GetString(2),
            RelativePath = reader.GetString(3),
            Size = reader.GetInt64(4),
            Sha256 = reader.GetString(5)
        };
    }

    private static async Task InsertBookAsync(
        SqliteConnection connection,
        Book book,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO Books (Id, Title, Authors, Series, SeriesIndex, Description, Tags, Category, IsFavorite, ReadingStatus, CoverPath, CreatedAt, UpdatedAt,
                               Publisher, PublishDate, Isbn, PageCount, Binding, DoubanRating, DoubanRatingCount)
            VALUES ($id, $title, $authors, $series, $seriesIndex, $description, $tags, $category, $isFavorite, $readingStatus, $coverPath, $createdAt, $updatedAt,
                    $publisher, $publishDate, $isbn, $pageCount, $binding, $doubanRating, $doubanRatingCount);
            """;
        AddBookParameters(command, book);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpdateBookRowAsync(
        SqliteConnection connection,
        Book book,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE Books SET Title=$title, Authors=$authors, Series=$series, SeriesIndex=$seriesIndex, Description=$description,
                Tags=$tags, Category=$category, IsFavorite=$isFavorite, ReadingStatus=$readingStatus,
                CoverPath=$coverPath, UpdatedAt=$updatedAt, Publisher=$publisher, PublishDate=$publishDate,
                Isbn=$isbn, PageCount=$pageCount, Binding=$binding, DoubanRating=$doubanRating,
                DoubanRatingCount=$doubanRatingCount WHERE Id=$id;
            """;
        AddBookParameters(command, book);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddBookParameters(SqliteCommand command, Book book)
    {
        command.Parameters.AddWithValue("$id", book.Id.ToString());
        command.Parameters.AddWithValue("$title", book.Title);
        command.Parameters.AddWithValue("$authors", book.Authors);
        command.Parameters.AddWithValue("$series", (object?)book.Series ?? DBNull.Value);
        command.Parameters.AddWithValue("$seriesIndex", (object?)book.SeriesIndex ?? DBNull.Value);
        command.Parameters.AddWithValue("$description", (object?)book.Description ?? DBNull.Value);
        command.Parameters.AddWithValue("$publisher", (object?)book.Publisher ?? DBNull.Value);
        command.Parameters.AddWithValue("$publishDate", (object?)book.PublishDate ?? DBNull.Value);
        command.Parameters.AddWithValue("$isbn", (object?)book.Isbn ?? DBNull.Value);
        command.Parameters.AddWithValue("$pageCount", (object?)book.PageCount ?? DBNull.Value);
        command.Parameters.AddWithValue("$binding", (object?)book.Binding ?? DBNull.Value);
        command.Parameters.AddWithValue("$doubanRating", (object?)book.DoubanRating ?? DBNull.Value);
        command.Parameters.AddWithValue("$doubanRatingCount", (object?)book.DoubanRatingCount ?? DBNull.Value);
        command.Parameters.AddWithValue("$tags", book.Tags ?? string.Empty);
        command.Parameters.AddWithValue("$category", book.Category ?? string.Empty);
        command.Parameters.AddWithValue("$isFavorite", book.IsFavorite ? 1 : 0);
        command.Parameters.AddWithValue("$readingStatus", (int)book.ReadingStatus);
        command.Parameters.AddWithValue("$coverPath", (object?)book.CoverPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", book.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", book.UpdatedAt.ToString("O"));
    }

    private static async Task EnsureBookProductivityColumnsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var inspect = connection.CreateCommand();
        inspect.CommandText = "PRAGMA table_info(Books);";
        await using (var reader = await inspect.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) existing.Add(reader.GetString(1));

        foreach (var definition in new[]
        {
            (Name: "Category", Sql: "ALTER TABLE Books ADD COLUMN Category TEXT NOT NULL DEFAULT '';"),
            (Name: "IsFavorite", Sql: "ALTER TABLE Books ADD COLUMN IsFavorite INTEGER NOT NULL DEFAULT 0;"),
            (Name: "ReadingStatus", Sql: "ALTER TABLE Books ADD COLUMN ReadingStatus INTEGER NOT NULL DEFAULT 0;"),
            (Name: "Publisher", Sql: "ALTER TABLE Books ADD COLUMN Publisher TEXT NULL;"),
            (Name: "PublishDate", Sql: "ALTER TABLE Books ADD COLUMN PublishDate TEXT NULL;"),
            (Name: "Isbn", Sql: "ALTER TABLE Books ADD COLUMN Isbn TEXT NULL;"),
            (Name: "PageCount", Sql: "ALTER TABLE Books ADD COLUMN PageCount TEXT NULL;"),
            (Name: "Binding", Sql: "ALTER TABLE Books ADD COLUMN Binding TEXT NULL;"),
            (Name: "DoubanRating", Sql: "ALTER TABLE Books ADD COLUMN DoubanRating REAL NULL;"),
            (Name: "DoubanRatingCount", Sql: "ALTER TABLE Books ADD COLUMN DoubanRatingCount INTEGER NULL;")
        })
        {
            if (existing.Contains(definition.Name)) continue;
            var alter = connection.CreateCommand();
            alter.CommandText = definition.Sql;
            await alter.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task InsertFileAsync(
        SqliteConnection connection,
        BookFile file,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO BookFiles (Id, BookId, Format, RelativePath, Size, Sha256) VALUES ($id, $bookId, $format, $relativePath, $size, $sha256);";
        command.Parameters.AddWithValue("$id", file.Id.ToString());
        command.Parameters.AddWithValue("$bookId", file.BookId.ToString());
        command.Parameters.AddWithValue("$format", file.Format);
        command.Parameters.AddWithValue("$relativePath", file.RelativePath);
        command.Parameters.AddWithValue("$size", file.Size);
        command.Parameters.AddWithValue("$sha256", file.Sha256);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertTrashEntryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TrashEntry entry,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO LibraryTrash
                (Id, Kind, BookId, Title, Format, Size, DeletedAt, BookJson, FileJson,
                 TrashPath, OriginalPath, TrashCoverPath, OriginalCoverPath)
            VALUES
                ($id, $kind, $bookId, $title, $format, $size, $deletedAt, $bookJson, $fileJson,
                 $trashPath, $originalPath, $trashCoverPath, $originalCoverPath);
            """;
        command.Parameters.AddWithValue("$id", entry.Id.ToString());
        command.Parameters.AddWithValue("$kind", (int)entry.Kind);
        command.Parameters.AddWithValue("$bookId", entry.BookId.ToString());
        command.Parameters.AddWithValue("$title", entry.Title);
        command.Parameters.AddWithValue("$format", (object?)entry.Format ?? DBNull.Value);
        command.Parameters.AddWithValue("$size", entry.Size);
        command.Parameters.AddWithValue("$deletedAt", entry.DeletedAt.ToString("O"));
        command.Parameters.AddWithValue("$bookJson", (object?)entry.BookJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$fileJson", (object?)entry.FileJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$trashPath", entry.TrashPath);
        command.Parameters.AddWithValue("$originalPath", entry.OriginalPath);
        command.Parameters.AddWithValue("$trashCoverPath", (object?)entry.TrashCoverPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$originalCoverPath", (object?)entry.OriginalCoverPath ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DeleteTrashEntryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid id,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM LibraryTrash WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<TrashEntry?> ReadTrashEntryAsync(
        SqliteConnection connection,
        Guid id,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Kind, BookId, Title, Format, Size, DeletedAt, BookJson, FileJson,
                   TrashPath, OriginalPath, TrashCoverPath, OriginalCoverPath
            FROM LibraryTrash WHERE Id = $id LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", id.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        if (!Guid.TryParse(reader.GetString(0), out var parsedId)
            || !Guid.TryParse(reader.GetString(2), out var bookId)
            || !Enum.IsDefined(typeof(LibraryTrashItemKind), reader.GetInt32(1)))
            return null;
        return new TrashEntry
        {
            Id = parsedId,
            Kind = (LibraryTrashItemKind)reader.GetInt32(1),
            BookId = bookId,
            Title = reader.GetString(3),
            Format = reader.IsDBNull(4) ? null : reader.GetString(4),
            Size = reader.GetInt64(5),
            DeletedAt = DateTimeOffset.Parse(reader.GetString(6)),
            BookJson = reader.IsDBNull(7) ? null : reader.GetString(7),
            FileJson = reader.IsDBNull(8) ? null : reader.GetString(8),
            TrashPath = reader.GetString(9),
            OriginalPath = reader.GetString(10),
            TrashCoverPath = reader.IsDBNull(11) ? null : reader.GetString(11),
            OriginalCoverPath = reader.IsDBNull(12) ? null : reader.GetString(12)
        };
    }

    private static void EnsureMoveTargetAvailable(string path)
    {
        if (File.Exists(path) || Directory.Exists(path))
            throw new IOException($"恢复目标已存在：{path}");
    }

    private static void MovePath(string source, string destination)
    {
        if (!File.Exists(source) && !Directory.Exists(source)) return;
        EnsureMoveTargetAvailable(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (File.Exists(source))
            File.Move(source, destination);
        else
            Directory.Move(source, destination);
    }

    private static void MovePathBack(string source, string destination)
    {
        try
        {
            if (File.Exists(source) || Directory.Exists(source))
                MovePath(source, destination);
        }
        catch
        {
            // Keep the original exception from the database operation. A
            // subsequent startup can still expose the manifest for recovery.
        }
    }

    private static void DeletePath(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
        else if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }

    private sealed class TrashEntry
    {
        public Guid Id { get; init; }
        public LibraryTrashItemKind Kind { get; init; }
        public Guid BookId { get; init; }
        public string Title { get; init; } = string.Empty;
        public string? Format { get; init; }
        public long Size { get; init; }
        public DateTimeOffset DeletedAt { get; init; }
        public string? BookJson { get; init; }
        public string? FileJson { get; init; }
        public string TrashPath { get; init; } = string.Empty;
        public string OriginalPath { get; init; } = string.Empty;
        public string? TrashCoverPath { get; init; }
        public string? OriginalCoverPath { get; init; }
    }
}
