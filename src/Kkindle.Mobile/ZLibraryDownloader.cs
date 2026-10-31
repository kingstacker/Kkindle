using Kkindle.ApplicationServices;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle.Mobile;

/// <summary>
/// Downloads a Z-Library book into the library the way the desktop app does:
/// import the file, then write the details shown on the book's page (title,
/// author, publisher, year, series, ISBN, pages, description, cover) into it.
/// </summary>
public sealed class ZLibraryDownloader(ApplicationModules modules)
{
    private static readonly HttpClient CoverClient = new() { Timeout = TimeSpan.FromSeconds(20) };

    public ZLibraryService Service => modules.Integrations.ZLibrary;

    public async Task<ZLibrarySettings> EnsureLoggedInAsync(CancellationToken cancellationToken)
    {
        var settings = await modules.Integrations.ZLibrarySettings.LoadAsync(cancellationToken);
        if (!settings.IsConfigured) throw new InvalidOperationException("请先在“Z-Library 账号”中填写账号。");
        if (!Service.IsLoggedIn)
            await Service.LoginAsync(settings.Email, settings.Password, settings.BaseUrl, cancellationToken);
        return settings;
    }

    /// <returns>The existing library book when this one is already there.</returns>
    public async Task<LibraryBookMatch?> FindInLibraryAsync(ZLibraryBook book, CancellationToken cancellationToken) =>
        (await modules.Library.Books.GetLibraryMatchRecordsAsync(cancellationToken))
            .FirstOrDefault(local => ZLibraryMetadata.Matches(local, book));

    public async Task<Book> DownloadAsync(
        ZLibraryBook source,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        await EnsureLoggedInAsync(cancellationToken);
        var downloads = Path.Combine(modules.Paths.Data, "downloads");
        var downloaded = await Service.DownloadAsync(source, downloads, progress, cancellationToken);
        try
        {
            var result = await modules.Library.Books.ImportAsync([downloaded], cancellationToken: cancellationToken);
            var imported = result.Items.FirstOrDefault(item => item.Succeeded && item.Book is not null)
                ?? throw new IOException(result.Items.FirstOrDefault()?.Message ?? "导入书库失败。");
            var book = imported.Book!;

            // A fresh import takes the page's details over what the file declared;
            // a file merged into an existing book only fills its gaps.
            ZLibraryMetadata.Apply(book, source, replaceExisting: imported.Added && book.Files.Count <= 1);
            if ((imported.Added || string.IsNullOrWhiteSpace(book.CoverPath))
                && await DownloadCoverAsync(book, source, cancellationToken) is { } cover)
            {
                book.CoverPath = cover;
            }
            await modules.Library.Books.UpdateMetadataAsync(book, cancellationToken);
            return book;
        }
        finally
        {
            try
            {
                File.Delete(downloaded);
            }
            catch (IOException)
            {
                // The library keeps its own copy; a leftover download is harmless.
            }
        }
    }

    private async Task<string?> DownloadCoverAsync(Book book, ZLibraryBook source, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(source.CoverUrl, UriKind.Absolute, out var url)) return null;
        try
        {
            var bytes = await CoverClient.GetByteArrayAsync(url, cancellationToken);
            if (bytes.Length == 0) return null;
            var paths = modules.Paths;
            paths.EnsureDirectories();
            var path = Path.Combine(paths.Covers, $"{book.Id:N}-zlibrary.jpg");
            await File.WriteAllBytesAsync(path, bytes, cancellationToken);
            return Path.GetRelativePath(paths.Data, path);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or TaskCanceledException
            && !cancellationToken.IsCancellationRequested)
        {
            // The book is still imported; it just keeps the cover from its file.
            return null;
        }
    }

    public static async Task<byte[]?> TryGetCoverAsync(string? url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        try
        {
            return await CoverClient.GetByteArrayAsync(uri, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or TaskCanceledException)
        {
            return null;
        }
    }
}
