using Avalonia.Controls;
using Avalonia.VisualTree;
using Kkindle.ApplicationServices;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Kkindle.Mobile.Views;
using Xunit;

namespace Kkindle.Mobile.Tests;

[Collection("Mobile UI")]
public sealed class NativeReaderSurfaceTests(MobileUiSession session)
{
    [Fact]
    public Task FailedNativeOpenShowsAnEventWithoutControlsOrOverwritingProgress() => session.Session.Dispatch(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "kkindle-native-reader-tests", Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root);
        var services = new MobileServices(paths, new TestProtector(), new BookMetadataService(),
            new UnavailableBookFormatConverter(), new NoFileImportSource());
        using var modules = MobileComposition.Create(services);
        var shell = new MobileShell(modules, services, new MobilePreferencesStore(paths));
        var book = new Book { Id = Guid.NewGuid(), Title = "文件暂不可用" };
        var fileId = Guid.NewGuid();
        var previous = new ReaderProgressRow(book.Id, fileId, "chapter.xhtml", null, 2, 219, 43.5, 1, DateTimeOffset.UtcNow);
        await modules.Reader.Data.InitializeAsync();
        await modules.Reader.Data.SaveProgressAsync(previous);
        var reader = new ReaderPage(book, modules, shell);
        var states = new List<NativeReaderState>();
        reader.NativeStateChanged += states.Add;
        reader.UseNativeFrontend();
        var window = new Window { Width = 390, Height = 844, Content = reader };
        try
        {
            window.Show();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!states.Any(state => state.Error is not null)) await Task.Delay(10, timeout.Token);
            var failed = reader.GetNativeState();
            Assert.False(failed.Ready);
            Assert.Equal(book.Title, failed.Title);
            Assert.Contains("没有可读取", failed.Error);
            Assert.DoesNotContain(reader.GetVisualDescendants().OfType<Button>(), button => button.IsEffectivelyVisible);
            Assert.False(reader.FindControl<Border>("ErrorPanel")!.IsVisible);
            Assert.False(reader.FindControl<Border>("LoadingPanel")!.IsVisible);
            await reader.PauseAsync();
            window.Close();
            await Task.Delay(50);
            Assert.Equal(previous, await modules.Reader.Data.GetProgressAsync(fileId));
        }
        finally
        {
            window.Close();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }, CancellationToken.None);

    private sealed class TestProtector : ISecretProtector
    {
        public byte[] Protect(byte[] value) => value.ToArray();
        public byte[] Unprotect(byte[] value) => value.ToArray();
    }
}
