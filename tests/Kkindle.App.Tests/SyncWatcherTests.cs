using Avalonia.Threading;
using Kkindle.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kkindle.Ui.Tests;

public sealed partial class SettingsTests
{
    [Fact]
    public Task ExternalChangeNearLocalChangeStillRefreshesTheShelf() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        await SeedLayoutLibrary(scope);
        scope.Call("InitializeCrossProcessDatabaseWatcher");
        var bookId = scope.Window.ViewModel.Books[0].Book.Id;
        await using var db = new SqliteConnection($"Data Source={scope.Paths.Database}");
        await db.OpenAsync();
        using var command = db.CreateCommand();
        command.CommandText = "UPDATE Books SET Title='External title' WHERE Id=$id;";
        command.Parameters.AddWithValue("$id", bookId.ToString());
        await command.ExecuteNonQueryAsync();
        scope.Set("_lastInProcessDataChangeUtc", DateTime.UtcNow);
        scope.Call("ScheduleCrossProcessDatabaseRefresh");
        Dispatcher.UIThread.RunJobs();
        Assert.True(scope.Field<DispatcherTimer>("_crossProcessDataRefreshTimer").IsEnabled);
        scope.Call("CrossProcessDataRefreshTimer_Tick", null, EventArgs.Empty);
        await Until(() => scope.Window.ViewModel.Books.Any(card => card.Book.Id == bookId && card.Book.Title == "External title"));
    });

    [Fact]
    public Task SyncDatabaseNotificationsDoNotQueueAnotherSync() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        scope.Call("InitializeCrossProcessDatabaseWatcher");
        scope.Set("_lastInProcessDataChangeUtc", DateTime.MinValue);
        scope.Set("_s3SyncBusy", true);
        scope.Call("ScheduleCrossProcessDatabaseRefresh");
        scope.Set("_s3SyncBusy", false);
        Dispatcher.UIThread.RunJobs();
        Assert.False(scope.Field<DispatcherTimer>("_crossProcessDataRefreshTimer").IsEnabled);

        // Genuine service writes during sync must remain pending.
        scope.Set("_s3SyncBusy", true);
        var version = scope.Field<long>("_s3LocalChangeVersion");
        scope.Call("HandleLocalDataChanged", LocalDataChangeKind.Bookmark);
        Assert.Equal(version + 1, scope.Field<long>("_s3LocalChangeVersion"));
        scope.Call("CrossProcessDataRefreshTimer_Tick", null, EventArgs.Empty);
        Assert.Equal(version + 1, scope.Field<long>("_s3LocalChangeVersion"));

        scope.Set("_s3SyncBusy", false);
        scope.Set("_lastInProcessDataChangeUtc", DateTime.UtcNow);
        scope.Call("ScheduleCrossProcessDatabaseRefresh");
        Dispatcher.UIThread.RunJobs();
        Assert.True(scope.Field<DispatcherTimer>("_crossProcessDataRefreshTimer").IsEnabled);
        Assert.False(scope.Field<bool>("_crossProcessRefreshNeedsSync"));
        scope.Field<DispatcherTimer>("_crossProcessDataRefreshTimer").Stop();

        scope.Set("_lastInProcessDataChangeUtc", DateTime.MinValue);
        scope.Call("ScheduleCrossProcessDatabaseRefresh");
        Dispatcher.UIThread.RunJobs();
        Assert.True(scope.Field<DispatcherTimer>("_crossProcessDataRefreshTimer").IsEnabled);
        Assert.True(scope.Field<bool>("_crossProcessRefreshNeedsSync"));
        scope.Field<DispatcherTimer>("_crossProcessDataRefreshTimer").Stop();
    });
}
