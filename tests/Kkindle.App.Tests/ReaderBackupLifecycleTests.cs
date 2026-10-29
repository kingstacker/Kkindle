using System.Reflection;
using Avalonia.Controls;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kkindle.Ui.Tests;

[Collection("Settings UI")]
public sealed class ReaderBackupLifecycleTests(SettingsUiSession session)
{
    [Fact]
    public Task FailedAnnotationEditKeepsTheOriginalInMemory() => session.Session.Dispatch(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        var reader = scope.Field<ReaderDataService>("_readerData");
        var bookId = Guid.NewGuid();
        var file = new BookFile { Id = Guid.NewGuid(), BookId = bookId };
        using var card = new BookCardViewModel(new Book { Id = bookId }, scope.Paths.Data);
        var annotation = new ReaderAnnotation { BookId = bookId, BookFileId = file.Id, ChapterPath = "pdf:page:1",
            SelectedText = "quote", StartOffset = 1, EndOffset = 6, Note = "original" };
        await reader.SaveAnnotationAsync(annotation);
        scope.Window.ReaderAnnotations.Add(annotation);
        scope.Set("_readerBookCard", card);
        scope.Set("_readerBookFile", file);
        scope.Set("_readerIsPdf", true);
        scope.Set("_readerPdfPage", 1);
        scope.Set("_selectedReaderAnnotation", annotation);
        scope.Set("_readerPendingSelection", "quote");
        scope.Set("_readerPendingSelectionStartOffset", 1);
        scope.Set("_readerPendingSelectionEndOffset", 6);
        await using var db = new SqliteConnection($"Data Source={scope.Paths.Database}");
        await db.OpenAsync();
        using var command = db.CreateCommand();
        command.CommandText = "CREATE TRIGGER BlockAnnotation BEFORE INSERT ON ReaderAnnotations BEGIN SELECT RAISE(FAIL, 'blocked'); END;";
        await command.ExecuteNonQueryAsync();
        await scope.Call<Task>("SaveReaderAnnotationAsync", "changed", null, null);
        Assert.Equal("original", annotation.Note);
        Assert.Equal("original", Assert.Single(await reader.GetAnnotationsAsync(file.Id)).Note);
        scope.Set("_readerBookCard", null);
        scope.Set("_readerBookFile", null);
        return true;
    }, CancellationToken.None);

    [Theory]
    [InlineData("import")]
    [InlineData("close")]
    [InlineData("open")]
    public Task OpeningIsBlockedDuringConflictingOperations(string operation) => session.Session.Dispatch(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        using var card = new BookCardViewModel(new Book { Id = Guid.NewGuid() }, scope.Paths.Data);
        if (operation == "import") scope.Set("_backupImportInProgress", true);
        if (operation == "close") scope.Set("_readerCloseInProgress", 1);
        if (operation == "open") scope.Set("_bookOpenInProgress", 1);
        await scope.Call<Task>("OpenBookAsync", card, null, true);
        Assert.False(scope.Get<Control>("ReaderRoot").IsVisible);
        scope.Set("_backupImportInProgress", false);
        scope.Set("_readerCloseInProgress", 0);
        scope.Set("_bookOpenInProgress", 0);
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task NativeExitWaitsForBackupImport() => session.Session.Dispatch(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        scope.Set("_backupImportInProgress", true);
        scope.Window.Close();
        Assert.True(scope.Window.IsVisible);
        scope.Set("_backupImportInProgress", false);
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task LateReadingTimeFailureDoesNotCreditTheNewBook() => session.Session.Dispatch(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        var reader = scope.Field<ReaderDataService>("_readerData");
        var a = new BookFile { Id = Guid.NewGuid(), BookId = Guid.NewGuid() };
        var b = new BookFile { Id = Guid.NewGuid(), BookId = Guid.NewGuid() };
        using var cardA = new BookCardViewModel(new Book { Id = a.BookId }, scope.Paths.Data);
        using var cardB = new BookCardViewModel(new Book { Id = b.BookId }, scope.Paths.Data);
        await using var db = new SqliteConnection($"Data Source={scope.Paths.Database}");
        await db.OpenAsync();
        using var command = db.CreateCommand();
        command.CommandText = $"CREATE TRIGGER BlockTime BEFORE INSERT ON ReaderReadingStats WHEN NEW.BookFileId='{a.Id}' BEGIN SELECT RAISE(FAIL, 'blocked'); END;";
        await command.ExecuteNonQueryAsync();
        var gate = (SemaphoreSlim)typeof(ReaderDataService).GetField("_databaseGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(reader)!;
        await gate.WaitAsync();
        Task flush;
        try
        {
            scope.Set("_readerBookCard", cardA);
            scope.Set("_readerBookFile", a);
            scope.Set("_readerActiveSeconds", 30L);
            flush = scope.Call<Task>("FlushReaderActiveSecondsAsync");
            for (var i = 0; i < 100 && !File.Exists(Path.Combine(scope.Paths.Data, "reader-reading-time-recovery.json")); i++) await Task.Delay(10);
            scope.Set("_readerBookCard", cardB);
            scope.Set("_readerBookFile", b);
        }
        finally { gate.Release(); }
        await flush;
        await scope.Call<Task>("FlushReaderActiveSecondsAsync");
        Assert.Null(await reader.GetReadingStatsAsync(b.Id));
        command.CommandText = "DROP TRIGGER BlockTime;";
        await command.ExecuteNonQueryAsync();
        await scope.Field<ReaderReadingTimeRecoveryService>("_readerReadingTimeRecovery").InitializeAsync();
        Assert.Equal(30, (await reader.GetReadingStatsAsync(a.Id))!.CumulativeSeconds);
        scope.Set("_readerBookCard", null);
        scope.Set("_readerBookFile", null);
        return true;
    }, CancellationToken.None);
}
