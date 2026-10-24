using Avalonia.Controls;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Kkindle.TestFixtures;
using Xunit;

namespace Kkindle.Ui.Tests;

[Collection("Settings UI")]
public sealed class PdfOpenDiagnosticTests(SettingsUiSession session)
{
    [Fact]
    public Task OpensPdfFromLongLibraryPath() => session.Session.Dispatch(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        var source = Environment.GetEnvironmentVariable("KKINDLE_TEST_PDF")
            ?? PdfFixture.Write(scope.Paths.ReaderCache);
        // Keep the full path above MAX_PATH even with a short Unix temp root.
        var directory = Path.Combine(scope.Paths.ReaderCache,
            new string('a', 80), new string('b', 80), new string('c', 80));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "中文 Investments.pdf");
        File.Copy(source, path);
        Assert.True(path.Length > 260);
        using (var document = new PdfDocumentService(path))
        {
            Assert.True(document.PageCount > 0);
            Assert.NotEmpty(document.RenderPage(1, 200, 300).Png);
        }
        using (var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.True(exclusive.Length > 0);
        var library = new SqliteBookLibraryService(scope.Paths, new BookMetadataService());
        await library.ImportAsync([path]);
        var book = Assert.Single(await library.SearchAsync());
        using var card = new BookCardViewModel(book, scope.Paths.Data);
        await scope.Call<Task>("OpenPdfReaderAsync", card, book.Files[0], path);
        Assert.True(scope.Get<Control>("ReaderRoot").IsVisible, scope.Get<TextBlock>("TaskStatusText").Text);
        return true;
    }, CancellationToken.None);
}
