using Kkindle.Core;
using Kkindle.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Kkindle.Tests;

public sealed class ReaderAnnotationSearchTests
{
    [Fact]
    public async Task SearchesAnnotationsAcrossBooksWithFilters()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(root);
            await new SqliteBookLibraryService(paths, new BookMetadataService()).InitializeAsync();
            var data = new ReaderDataService(paths);
            await data.InitializeAsync();

            var firstBook = Guid.NewGuid();
            var secondBook = Guid.NewGuid();
            var firstFile = Guid.NewGuid();
            var secondFile = Guid.NewGuid();
            var oldTime = DateTimeOffset.UtcNow.AddDays(-20);
            var newTime = DateTimeOffset.UtcNow.AddDays(-2);

            await using (var connection = new SqliteConnection($"Data Source={paths.Database}"))
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO Books (Id, Title, Authors, Tags, Category, IsFavorite, ReadingStatus, CreatedAt, UpdatedAt)
                    VALUES ($firstBook, '山海笔记', '作者甲', '', '', 0, 0, $time, $time);
                    INSERT INTO Books (Id, Title, Authors, Tags, Category, IsFavorite, ReadingStatus, CreatedAt, UpdatedAt)
                    VALUES ($secondBook, '技术手册', '作者乙', '', '', 0, 0, $time, $time);
                    INSERT INTO BookFiles (Id, BookId, Format, RelativePath, Size, Sha256)
                    VALUES ($firstFile, $firstBook, 'epub', 'library/a.epub', 1, $shaA);
                    INSERT INTO BookFiles (Id, BookId, Format, RelativePath, Size, Sha256)
                    VALUES ($secondFile, $secondBook, 'pdf', 'library/b.pdf', 1, $shaB);
                    """;
                command.Parameters.AddWithValue("$firstBook", firstBook.ToString());
                command.Parameters.AddWithValue("$secondBook", secondBook.ToString());
                command.Parameters.AddWithValue("$firstFile", firstFile.ToString());
                command.Parameters.AddWithValue("$secondFile", secondFile.ToString());
                command.Parameters.AddWithValue("$time", oldTime.ToString("O"));
                command.Parameters.AddWithValue("$shaA", new string('a', 64));
                command.Parameters.AddWithValue("$shaB", new string('b', 64));
                await command.ExecuteNonQueryAsync();
            }

            await data.SaveAnnotationAsync(new ReaderAnnotation
            {
                BookId = firstBook,
                BookFileId = firstFile,
                ChapterPath = "chapter-1.xhtml",
                StartOffset = 0,
                EndOffset = 4,
                SelectedText = "山川异域",
                Note = "风月同天",
                Color = "#FFD54F",
                CreatedAt = oldTime,
                UpdatedAt = oldTime
            });
            await data.SaveAnnotationAsync(new ReaderAnnotation
            {
                BookId = secondBook,
                BookFileId = secondFile,
                ChapterPath = "page-1.pdf",
                StartOffset = 10,
                EndOffset = 14,
                SelectedText = "系统设计",
                Note = "需要缓存",
                Color = "#64B5F6",
                CreatedAt = newTime,
                UpdatedAt = newTime
            });

            var all = await data.SearchAnnotationsAsync();
            Assert.Equal(2, all.Count);
            Assert.Equal("系统设计", all[0].Annotation.SelectedText);
            Assert.Equal("技术手册", all[0].BookTitle);
            Assert.Equal("pdf", all[0].BookFormat);

            var byQuote = await data.SearchAnnotationsAsync("山川");
            Assert.Single(byQuote);
            Assert.Equal(firstBook, byQuote[0].Annotation.BookId);

            var byNote = await data.SearchAnnotationsAsync("缓存");
            Assert.Single(byNote);
            Assert.Equal(secondBook, byNote[0].Annotation.BookId);

            var byTitle = await data.SearchAnnotationsAsync("技术");
            Assert.Single(byTitle);
            Assert.Equal(secondBook, byTitle[0].Annotation.BookId);

            var byBook = await data.SearchAnnotationsAsync(bookId: firstBook);
            Assert.Single(byBook);
            Assert.Equal("#FFD54F", byBook[0].Annotation.Color);

            var byColor = await data.SearchAnnotationsAsync(color: "#64B5F6");
            Assert.Single(byColor);
            Assert.Equal("系统设计", byColor[0].Annotation.SelectedText);

            var recent = await data.SearchAnnotationsAsync(updatedAfter: DateTimeOffset.UtcNow.AddDays(-7));
            Assert.Single(recent);
            Assert.Equal(secondBook, recent[0].Annotation.BookId);

            var options = await data.GetAnnotationBookOptionsAsync();
            Assert.Equal(2, options.Count);
            Assert.Contains(options, option => option.BookId == firstBook && option.Title == "山海笔记");
            Assert.Contains(options, option => option.BookId == secondBook && option.Title == "技术手册");
        }
        finally { TestHelpers.TryDelete(root); }
    }
}
