using Kkindle.Core;
using Kkindle.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Kkindle.Tests;

public sealed class DuplicateBookMergeTests
{
    [Fact]
    public async Task FindsAndMergesBooksWithCaseInsensitiveMatchingHashes()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(root);
            var library = new SqliteBookLibraryService(paths, new BookMetadataService());
            await library.InitializeAsync();
            var data = new ReaderDataService(paths);
            await data.InitializeAsync();

            var canonicalId = Guid.NewGuid();
            var duplicateId = Guid.NewGuid();
            var canonicalFile = Guid.NewGuid();
            var duplicateFile = Guid.NewGuid();
            var earlier = DateTimeOffset.UtcNow.AddMinutes(-10);
            var later = DateTimeOffset.UtcNow;

            await using (var connection = new SqliteConnection($"Data Source={paths.Database}"))
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO Books (Id, Title, Authors, Tags, Category, IsFavorite, ReadingStatus, CreatedAt, UpdatedAt)
                    VALUES ($canonical, '重复书', '作者', '', '', 0, 0, $earlier, $later);
                    INSERT INTO Books (Id, Title, Authors, Tags, Category, IsFavorite, ReadingStatus, CreatedAt, UpdatedAt)
                    VALUES ($duplicate, '重复书', '作者', '', '', 0, 0, $earlier, $earlier);
                    INSERT INTO BookFiles (Id, BookId, Format, RelativePath, Size, Sha256)
                    VALUES ($canonicalFile, $canonical, 'epub', 'library/canonical.epub', 8, $lower);
                    INSERT INTO BookFiles (Id, BookId, Format, RelativePath, Size, Sha256)
                    VALUES ($duplicateFile, $duplicate, 'epub', 'library/duplicate.epub', 8, $upper);
                    """;
                command.Parameters.AddWithValue("$canonical", canonicalId.ToString());
                command.Parameters.AddWithValue("$duplicate", duplicateId.ToString());
                command.Parameters.AddWithValue("$canonicalFile", canonicalFile.ToString());
                command.Parameters.AddWithValue("$duplicateFile", duplicateFile.ToString());
                command.Parameters.AddWithValue("$earlier", earlier.ToString("O"));
                command.Parameters.AddWithValue("$later", later.ToString("O"));
                command.Parameters.AddWithValue("$lower", new string('a', 64));
                command.Parameters.AddWithValue("$upper", new string('A', 64));
                await command.ExecuteNonQueryAsync();
            }

            await data.SaveAnnotationAsync(new ReaderAnnotation
            {
                BookId = canonicalId,
                BookFileId = canonicalFile,
                ChapterPath = "chapter.xhtml",
                StartOffset = 0,
                EndOffset = 2,
                SelectedText = "一",
                CreatedAt = earlier,
                UpdatedAt = earlier
            });
            await data.SaveAnnotationAsync(new ReaderAnnotation
            {
                BookId = duplicateId,
                BookFileId = duplicateFile,
                ChapterPath = "chapter.xhtml",
                StartOffset = 2,
                EndOffset = 4,
                SelectedText = "二",
                CreatedAt = earlier,
                UpdatedAt = earlier
            });

            var service = new S3SyncService(paths, new TestHelpers.PlaintextSecretProtector());
            var groups = await service.FindLocalDuplicateBooksAsync();
            var group = Assert.Single(groups);
            Assert.Equal(2, group.Books.Count);
            Assert.Equal(canonicalId, group.Books[0].Id);

            var merged = await service.MergeLocalDuplicateBooksAsync();
            Assert.Equal(1, merged);

            var remaining = Assert.Single(await library.SearchAsync());
            Assert.Equal(canonicalId, remaining.Id);
            var annotations = await data.SearchAnnotationsAsync();
            Assert.Equal(2, annotations.Count);
            Assert.All(annotations, annotation => Assert.Equal(canonicalId, annotation.Annotation.BookId));
            Assert.All(annotations, annotation => Assert.Equal(canonicalFile, annotation.Annotation.BookFileId));
        }
        finally { TestHelpers.TryDelete(root); }
    }
}
