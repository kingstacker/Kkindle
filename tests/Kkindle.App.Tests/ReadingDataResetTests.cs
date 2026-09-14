using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kkindle.Ui.Tests;

public sealed partial class SettingsTests
{
    [Theory]
    [InlineData("zh-CN")]
    [InlineData("en-US")]
    public Task ReadingDataResetSettings_RequireConfirmationAndKeepBookmarksAndNotes(string language) => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        ((Kkindle.App)Application.Current!).ApplyLanguage(language);
        var (_, file) = await SeedSettingsReadingResetAsync(scope);
        var reader = scope.Field<ReaderDataService>("_readerData");
        await scope.Call<Task>("RefreshReadingDashboardAsync");
        Assert.Single(scope.Window.DashboardRecentItems);
        scope.Window.Width = 1024;
        scope.Window.Height = 768;
        scope.Call("OpenSettingsExpander", "Data", scope.Get<Expander>("SettingsReadingDataExpander"));
        await Render();
        var resetButton = scope.Get<Button>("ResetReadingDataButton");
        Assert.Equal(language == "en-US" ? "Reset reading data" : "重置阅读数据", resetButton.Content);
        AssertWithinWindow(resetButton, scope.Window);
        Capture(scope.Window, $"{language}-reading-data-reset-settings");

        var cancel = scope.Call<Task>("ResetReadingDataFromSettingsAsync");
        await Until(() => scope.Get<Control>("ConfirmationOverlay").IsVisible);
        await Render();
        Assert.False(cancel.IsCompleted);
        Assert.False(resetButton.IsEnabled);
        Assert.True(scope.Get<Button>("ConfirmationCancelButton").IsFocused);
        AssertWithinWindow(scope.Get<Button>("ConfirmationOkButton"), scope.Window);
        AssertWithinWindow(scope.Get<Button>("ConfirmationCancelButton"), scope.Window);
        Assert.Contains(language == "en-US" ? "bookmarks" : "书签", scope.Get<TextBlock>("ConfirmationMessageText").Text!);
        Capture(scope.Window, $"{language}-reading-data-reset-confirmation");
        await scope.Call<Task>("ResetReadingDataFromSettingsAsync");
        Assert.False(cancel.IsCompleted);
        Assert.Equal(120, (await reader.GetReadingStatsAsync(file))!.CumulativeSeconds);
        scope.Get<Button>("ConfirmationCancelButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await cancel.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(resetButton.IsEnabled);
        Assert.Equal(60, (await reader.GetProgressAsync(file))!.ProgressPercent);
        Assert.Equal(120, (await reader.GetReadingStatsAsync(file))!.CumulativeSeconds);

        var changeVersion = scope.Field<long>("_s3LocalChangeVersion");
        var confirm = scope.Call<Task>("ResetReadingDataFromSettingsAsync");
        await Until(() => scope.Get<Control>("ConfirmationOverlay").IsVisible);
        scope.Get<Button>("ConfirmationOkButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await confirm.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(await reader.GetProgressAsync(file));
        Assert.Null(await reader.GetReadingStatsAsync(file));
        Assert.Single(await reader.GetBookmarksAsync(file));
        var note = Assert.Single(await reader.GetAnnotationsAsync(file));
        Assert.Equal("Retained highlight", note.SelectedText);
        Assert.Equal("Retained note", note.Note);
        Assert.NotNull(await reader.GetLayoutSettingsAsync(file));
        Assert.Equal(1, await CountSettingsResetRowsAsync(scope, "Books"));
        Assert.Equal(1, await CountSettingsResetRowsAsync(scope, "BookFiles"));
        Assert.Equal(0, await CountSettingsResetRowsAsync(scope, "ReaderReadingSessions"));
        Assert.Empty(scope.Window.DashboardRecentItems);
        Assert.Empty(scope.Window.DashboardBookTimes);
        Assert.True(scope.Get<TextBlock>("DashboardRecentEmptyText").IsVisible);
        Assert.Equal("1 / 1", scope.Get<TextBlock>("DashboardBookmarksText").Text);
        Assert.True(scope.Field<long>("_s3LocalChangeVersion") > changeVersion);
        Assert.Equal(UiText.Get("阅读统计和进度已重置。书籍、书签、划线与批注已保留。"),
            scope.Get<TextBlock>("SettingsReadingDataStatusText").Text);
        Assert.False(scope.Get<Control>("ConfirmationOverlay").IsVisible);
        Assert.True(resetButton.IsEnabled);
    });

    [Fact]
    public Task ReadingDataResetSettings_DiagnosticModeCannotSkipConfirmation() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        var (_, file) = await SeedSettingsReadingResetAsync(scope);
        var previous = Environment.GetEnvironmentVariable("KKINDLE_SEND_DIAG");
        try
        {
            Environment.SetEnvironmentVariable("KKINDLE_SEND_DIAG", "1");
            var reset = scope.Call<Task>("ResetReadingDataFromSettingsAsync");
            await Until(() => scope.Get<Control>("ConfirmationOverlay").IsVisible);
            Assert.False(reset.IsCompleted);
            scope.Call("ConfirmationCancelButton_Click", null, new RoutedEventArgs());
            await reset.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(120, (await scope.Field<ReaderDataService>("_readerData").GetReadingStatsAsync(file))!.CumulativeSeconds);
        }
        finally { Environment.SetEnvironmentVariable("KKINDLE_SEND_DIAG", previous); }
    });

    [Theory]
    [InlineData("_readerIsPdf", true)]
    [InlineData("_bookOpenInProgress", 1)]
    [InlineData("_readerCloseInProgress", 1)]
    [InlineData("_s3SyncBusy", true)]
    [InlineData("_backupBusy", true)]
    [InlineData("_stage3Ready", false)]
    public Task ReadingDataResetSettings_BlocksWhileDataIsInUse(string field, object value) => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        var (_, file) = await SeedSettingsReadingResetAsync(scope);
        var previous = scope.Field<object>(field);
        try
        {
            scope.Set(field, value);
            await scope.Call<Task>("ResetReadingDataFromSettingsAsync");
            Assert.False(scope.Get<Control>("ConfirmationOverlay").IsVisible);
            Assert.False(string.IsNullOrWhiteSpace(scope.Get<TextBlock>("SettingsReadingDataStatusText").Text));
            Assert.Equal(120, (await scope.Field<ReaderDataService>("_readerData").GetReadingStatsAsync(file))!.CumulativeSeconds);
            Assert.True(scope.Get<Button>("ResetReadingDataButton").IsEnabled);
        }
        finally { scope.Set(field, previous); }
    });

    [Fact]
    public Task ReadingDataResetSettings_RechecksReaderStateAfterConfirmation() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        var (_, file) = await SeedSettingsReadingResetAsync(scope);
        var reset = scope.Call<Task>("ResetReadingDataFromSettingsAsync");
        await Until(() => scope.Get<Control>("ConfirmationOverlay").IsVisible);
        try
        {
            scope.Set("_readerIsPdf", true);
            scope.Call("ConfirmationOkButton_Click", null, new RoutedEventArgs());
            await reset.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(120, (await scope.Field<ReaderDataService>("_readerData").GetReadingStatsAsync(file))!.CumulativeSeconds);
            Assert.Equal(UiText.Get("请先关闭阅读器并返回书库，再重置阅读数据。"), scope.Get<TextBlock>("SettingsReadingDataStatusText").Text);
        }
        finally { scope.Set("_readerIsPdf", false); }
    });

    [Fact]
    public Task ReadingDataResetSettings_BookOpeningWaitsForSyncAndRechecksResetState() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        scope.Set("_s3SyncBusy", true);
        scope.Set("_s3SyncCompletion", completion);
        try
        {
            var card = new BookCardViewModel(new Book { Id = Guid.NewGuid(), Title = "Wait for reset" }, scope.Paths.Data);
            var opening = scope.Call<Task>("OpenBookAsync", card, null, true);
            await Task.Yield();
            Assert.False(opening.IsCompleted);
            Assert.Equal(0, scope.Field<int>("_bookOpenInProgress"));
            scope.Set("_readingDataResetBusy", true);
            scope.Set("_s3SyncBusy", false);
            completion.TrySetResult(true);
            await opening.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, scope.Field<int>("_bookOpenInProgress"));
            Assert.False(scope.Get<Control>("MessageOverlay").IsVisible);
        }
        finally
        {
            scope.Set("_s3SyncBusy", false);
            scope.Set("_readingDataResetBusy", false);
            completion.TrySetResult(true);
        }
    });

    [Theory]
    [InlineData("_bookOpenInProgress")]
    [InlineData("_readerCloseInProgress")]
    public Task ReadingDataResetSettings_SyncCannotStartDuringReaderPersistence(string field) => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        scope.Set("_appSettings", scope.Field<AppSettings>("_appSettings") with { NetworkEnabled = true });
        scope.Set(field, 1);
        try
        {
            Assert.False(await scope.Call<Task<bool>>("RunS3SyncAsync", true, CancellationToken.None));
            Assert.False(scope.Field<bool>("_s3SyncBusy"));
        }
        finally { scope.Set(field, 0); }
    });

    private static async Task<(Guid BookId, Guid FileId)> SeedSettingsReadingResetAsync(TestWindow scope)
    {
        var book = Guid.NewGuid();
        var file = Guid.NewGuid();
        await using (var connection = new SqliteConnection($"Data Source={scope.Paths.Database}"))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO Books (Id, Title, Authors, CreatedAt, UpdatedAt)
                    VALUES ($book, 'Reading reset test', 'Author', $time, $time);
                INSERT INTO BookFiles (Id, BookId, Format, RelativePath, Size, Sha256)
                    VALUES ($file, $book, 'pdf', 'library/reading-reset.pdf', 1, $hash);
                """;
            command.Parameters.AddWithValue("$book", book.ToString());
            command.Parameters.AddWithValue("$file", file.ToString());
            command.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$hash", new string('c', 64));
            await command.ExecuteNonQueryAsync();
        }
        var reader = scope.Field<ReaderDataService>("_readerData");
        await reader.AddReadingTimeAsync(book, file, 120, 60, 6, 10);
        await reader.SaveProgressAsync(new ReaderProgressRow(book, file, "page:6", null, 6, 0, 60, 0, DateTimeOffset.UtcNow));
        await reader.SaveBookmarkAsync(new ReaderBookmark { BookId = book, BookFileId = file, ChapterPath = "page:6", Title = "Retained bookmark" });
        await reader.SaveAnnotationAsync(new ReaderAnnotation
        {
            BookId = book, BookFileId = file, ChapterPath = "page:6", SelectedText = "Retained highlight",
            StartOffset = 0, EndOffset = 18, Note = "Retained note"
        });
        await reader.SaveLayoutSettingsAsync(book, file, new ReaderLayoutSettings { FontScale = 1.2 });
        return (book, file);
    }

    private static async Task<long> CountSettingsResetRowsAsync(TestWindow scope, string table)
    {
        await using var connection = new SqliteConnection($"Data Source={scope.Paths.Database}");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
