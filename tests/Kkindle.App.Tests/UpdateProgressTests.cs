using System.Net;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Xunit;

namespace Kkindle.Ui.Tests;

[Collection("Settings UI")]
public sealed class UpdateProgressTests(SettingsUiSession session)
{
    [Fact]
    public Task UpdateClickOpensProgressAndRepeatedClickReopensWithoutAnotherCheck() => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        using var handler = new PendingRequestHandler();
        using var client = new HttpClient(handler);
        ConfigureUpdater(scope, client);
        scope.Call("SettingsButton_Click", null, new RoutedEventArgs());
        scope.Call("ShowSettingsSection", "About");
        await ReaderTests.Render();

        var button = scope.Get<Button>("CheckForUpdatesButton");
        button.Focus();
        var checking = scope.Call<Task>("CheckForUpdatesAsync", true);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await ReaderTests.Render();

        Assert.True(button.IsEnabled);
        Assert.False(button.IsFocused);
        Assert.Contains("updating", button.Classes);
        Assert.True(scope.Field<Flyout>("_backgroundTaskFlyout").IsOpen);
        var center = scope.Field<BackgroundTaskCenterService>("_backgroundTasks");
        var task = Assert.Single(center.GetSnapshot());
        Assert.Equal(BackgroundTaskStatus.Running, task.Status);

        scope.Call("DismissBackgroundTaskFlyout");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await ReaderTests.Render();
        Assert.True(scope.Field<Flyout>("_backgroundTaskFlyout").IsOpen);
        Assert.Single(center.GetSnapshot());
        Assert.Equal(1, handler.RequestCount);

        Assert.True(center.Cancel(task.Id));
        await checking.WaitAsync(TimeSpan.FromSeconds(5));
        await ReaderTests.Render();
        Assert.Equal(BackgroundTaskStatus.Canceled, Assert.Single(center.GetSnapshot()).Status);
        Assert.DoesNotContain("updating", button.Classes);
        Assert.True(button.IsEnabled);
        Assert.False(scope.Field<bool>("_updateCheckInProgress"));
        Assert.True(scope.Field<Flyout>("_backgroundTaskFlyout").IsOpen);
    });

    [Fact]
    public Task DownloadOpensProgressAndRecordsFailure() => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        using var handler = new PendingRequestHandler();
        using var client = new HttpClient(handler);
        ConfigureUpdater(scope, client);
        var update = new AppUpdateInfo("9.9.9", "v9.9.9", "", new Uri("https://example.test/release"),
            new AppUpdateAsset("Kkindle-9.9.9.zip", new Uri("https://example.test/package"), 100),
            new AppUpdateAsset("SHA256SUMS.txt", new Uri("https://example.test/checksums"), 100));

        var downloading = scope.Call<Task>("DownloadAndInstallAsync", update);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await ReaderTests.Render();
        Assert.True(scope.Field<Flyout>("_backgroundTaskFlyout").IsOpen);
        var center = scope.Field<BackgroundTaskCenterService>("_backgroundTasks");
        var task = Assert.Single(center.GetSnapshot());
        Assert.Equal(BackgroundTaskStatus.Running, task.Status);
        Assert.Contains("9.9.9", task.Title);

        handler.Response.SetResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        await Assert.ThrowsAsync<HttpRequestException>(() => downloading);
        await ReaderTests.Render();
        Assert.Equal(BackgroundTaskStatus.Failed, Assert.Single(center.GetSnapshot()).Status);
        Assert.True(scope.Field<Flyout>("_backgroundTaskFlyout").IsOpen);
    });

    private Task Run(Func<Task> action) => session.Session.Dispatch(async () =>
    {
        await action();
        return true;
    }, CancellationToken.None);

    private static void ConfigureUpdater(ReaderTestWindow scope, HttpClient client)
    {
        var constructor = typeof(UpdateService).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single();
        var updater = constructor.Invoke([new TestInstaller(), client, Path.Combine(scope.Paths.Root, "updates"),
            false, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1)]);
        scope.Set("_updateService", updater);
        scope.Set("_appSettings", scope.Field<AppSettings>("_appSettings") with { NetworkEnabled = true });
        scope.Get<Button>("CheckForUpdatesButton").IsEnabled = true;
    }

    private sealed class TestInstaller : IAppUpdateInstaller
    {
        public bool CanInstall => true;
        public string UnavailableReason => "";
        public string GetPackageAssetName(string version) => $"Kkindle-{version}.zip";
        public void LaunchInstaller(string packagePath) => throw new InvalidOperationException("Tests must not install updates.");
    }

    private sealed class PendingRequestHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<HttpResponseMessage> Response { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int RequestCount { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            Started.TrySetResult();
            return await Response.Task.WaitAsync(cancellationToken);
        }
    }
}
