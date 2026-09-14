using Avalonia;
using Avalonia.Controls;
using Kkindle.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kkindle.Ui.Tests;

public sealed partial class SettingsTests
{
    [Theory]
    [InlineData("zh-CN", "Archived reading book（已移出书库）")]
    [InlineData("en-US", "Archived reading book (removed from library)")]
    public Task ReadingDashboard_RetainsArchivedTitlesWithoutDuplicateRows(string language, string archivedLabel) => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        ((Kkindle.App)Application.Current!).ApplyLanguage(language);
        var archivedBook = Guid.NewGuid();
        var archivedFile = Guid.NewGuid();
        var currentBook = Guid.NewGuid();
        var currentFile = Guid.NewGuid();
        await using (var connection = new SqliteConnection($"Data Source={scope.Paths.Database}"))
        {
            await connection.OpenAsync();
            using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO Books (Id, Title, Authors, CreatedAt, UpdatedAt) VALUES
                    ($archivedBook, 'Archived reading book', 'Author', $time, $time),
                    ($currentBook, 'Current reading book', 'Author', $time, $time);
                INSERT INTO BookFiles (Id, BookId, Format, RelativePath, Size, Sha256) VALUES
                    ($archivedFile, $archivedBook, 'epub', 'library/archived/book.epub', 1, $archivedHash),
                    ($currentFile, $currentBook, 'pdf', 'library/current/book.pdf', 1, $currentHash);
                """;
            insert.Parameters.AddWithValue("$archivedBook", archivedBook.ToString());
            insert.Parameters.AddWithValue("$archivedFile", archivedFile.ToString());
            insert.Parameters.AddWithValue("$currentBook", currentBook.ToString());
            insert.Parameters.AddWithValue("$currentFile", currentFile.ToString());
            insert.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToString("O"));
            insert.Parameters.AddWithValue("$archivedHash", new string('a', 64));
            insert.Parameters.AddWithValue("$currentHash", new string('b', 64));
            await insert.ExecuteNonQueryAsync();

            var reader = scope.Field<ReaderDataService>("_readerData");
            await reader.AddReadingTimeAsync(archivedBook, archivedFile, 120, 50, 5, 10);
            await reader.AddReadingTimeAsync(currentBook, currentFile, 60, 20, 2, 10);
            using var remove = connection.CreateCommand();
            remove.CommandText = "DELETE FROM BookFiles WHERE Id = $file; DELETE FROM Books WHERE Id = $book;";
            remove.Parameters.AddWithValue("$file", archivedFile.ToString());
            remove.Parameters.AddWithValue("$book", archivedBook.ToString());
            await remove.ExecuteNonQueryAsync();
        }

        await scope.Call<Task>("RefreshReadingDashboardAsync");
        Assert.False(scope.Get<TextBlock>("DashboardStatusText").IsVisible);
        Assert.Equal(2, scope.Window.DashboardRecentItems.Count);
        Assert.Contains(scope.Window.DashboardRecentItems, item => item.Title == archivedLabel && item.Seconds == 120);
        Assert.Contains(scope.Window.DashboardRecentItems, item => item.Title == "Current reading book");
        Assert.Equal(archivedLabel, scope.Window.DashboardBookTimes[0].Label);

        await scope.Call<Task>("RefreshReadingDashboardAsync");
        Assert.Equal(2, scope.Window.DashboardRecentItems.Count);
        Assert.Equal(2, scope.Window.DashboardBookTimes.Count);
    });
}
