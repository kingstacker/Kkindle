using Avalonia.Media.Imaging;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle;

// Cache encoded thumbnails, never shared Bitmap instances: each visible card
// owns its decoder result and can release it independently on virtualization.
internal static class CoverThumbnailCache
{
    private const int Budget = 8 * 1024 * 1024;
    private static readonly SemaphoreSlim Gate = new(2, 2);
    private static readonly object CacheLock = new();
    private static readonly Dictionary<string, byte[]> Entries = [];
    private static readonly Queue<string> Order = new();
    private static int _bytes;

    internal static async Task<Bitmap?> LoadAsync(Book book, string root, CancellationToken token)
    {
        await Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                var cover = string.IsNullOrWhiteSpace(book.CoverPath) ? null : Path.Combine(root, book.CoverPath);
                var epubs = book.Files.Where(file => file.Format.Equals("epub", StringComparison.OrdinalIgnoreCase))
                    .Select(file => Path.Combine(root, file.RelativePath)).ToArray();
                var key = book.Title + "|" + Stamp(cover) + "|" + string.Join("|", epubs.Select(Stamp));
                byte[]? bytes;
                lock (CacheLock) Entries.TryGetValue(key, out bytes);
                if (bytes is null)
                {
                    var generated = epubs.Any(path => TitleCoverService.IsGeneratedTxtCover(path, cover));
                    Bitmap? image = null;
                    if (!generated && cover is not null)
                    {
                        try
                        {
                            using var input = File.OpenRead(cover);
                            image = Bitmap.DecodeToWidth(input, 320);
                        }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { }
                    }
                    if (image is null)
                    {
                        using var input = new MemoryStream(TitleCoverService.CreatePng(book.Title));
                        image = Bitmap.DecodeToWidth(input, 320);
                    }
                    using (image)
                    using (var output = new MemoryStream())
                    {
                        image.Save(output, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                        bytes = output.ToArray();
                    }
                    token.ThrowIfCancellationRequested();
                    lock (CacheLock)
                    {
                        if (bytes.Length <= Budget && !Entries.ContainsKey(key))
                        {
                            while (_bytes + bytes.Length > Budget && Order.TryDequeue(out var oldest))
                                if (Entries.Remove(oldest, out var old)) _bytes -= old.Length;
                            Entries.Add(key, bytes);
                            Order.Enqueue(key);
                            _bytes += bytes.Length;
                        }
                    }
                }
                token.ThrowIfCancellationRequested();
                using var stream = new MemoryStream(bytes);
                return new Bitmap(stream);
            }, token).ConfigureAwait(false);
        }
        finally { Gate.Release(); }
    }

    private static string Stamp(string? path)
    {
        if (path is null) return "";
        var file = new FileInfo(path);
        return file.Exists ? $"{path}:{file.Length}:{file.LastWriteTimeUtc.Ticks}" : path;
    }
}
