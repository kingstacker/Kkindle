using Avalonia.Controls;
using Avalonia.Interactivity;
using Kkindle.ApplicationServices;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Kkindle.Mobile.Views;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kkindle.Mobile.Tests;

[Collection("Mobile UI")]
public sealed class BackupUiTests(MobileUiSession session)
{
    [Fact]
    public Task MissingKeyPromptsBeforeOpeningImportOrExport() => session.Session.Dispatch(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "kkindle-backup-ui-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root);
        var picker = new RecordingPicker();
        var share = new RecordingShare();
        var services = new MobileServices(paths, new TestProtector(), new BookMetadataService(),
            new UnavailableBookFormatConverter(), picker, FileShare: share);
        using var modules = MobileComposition.Create(services);
        await modules.Library.Books.InitializeAsync();
        var shell = new MobileShell(modules, services, new MobilePreferencesStore(paths));
        var window = new Window { Width = 411, Height = 880, Content = shell };
        try
        {
            window.Show();
            shell.ShowProfile();
            var page = Assert.IsType<ProfilePage>(shell.CurrentPage);
            var status = page.FindControl<TextBlock>("BackupStatusText")!;
            foreach (var name in new[] { "ExportBackupButton", "RestoreBackupButton" })
            {
                status.Text = "";
                var button = page.FindControl<Button>(name)!;
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (!button.IsEnabled || !status.IsVisible || string.IsNullOrEmpty(status.Text))
                    await Task.Delay(10, timeout.Token);
                Assert.Contains("设置同步密钥", status.Text);
                Assert.Equal(0, picker.Requests);
                Assert.Equal(0, share.Requests);
                Assert.False(MobileDataCoordinator.IsRestartRequired);
            }
            if (Directory.Exists(paths.Backups))
                Assert.Empty(Directory.GetFiles(paths.Backups, "*.kkindle*", SearchOption.AllDirectories));
        }
        finally
        {
            window.Close();
            await Task.Delay(100);
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
        return true;
    }, CancellationToken.None);

    private sealed class TestProtector : ISecretProtector
    {
        public byte[] Protect(byte[] value) => value.ToArray();
        public byte[] Unprotect(byte[] value) => value.ToArray();
    }

    private sealed class RecordingPicker : IFileImportSource
    {
        public int Requests { get; private set; }
        public Task<IReadOnlyList<string>> PickFilesAsync(CancellationToken cancellationToken = default)
        {
            Requests++;
            return Task.FromResult<IReadOnlyList<string>>([]);
        }
    }

    private sealed class RecordingShare : IMobileFileShare
    {
        public int Requests { get; private set; }
        public Task<bool> ShareAsync(string fileName, byte[] content) { Requests++; return Task.FromResult(false); }
        public Task<bool> ShareFileAsync(string path) { Requests++; return Task.FromResult(false); }
    }
}
