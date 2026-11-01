using System.Collections.Concurrent;
using Avalonia.Media.Imaging;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle.Mobile;

/// <summary>
/// Decodes covers off the UI thread at display size. Search re-renders the grid
/// on every keystroke, so full-size decoding per tile is not affordable on a phone.
/// </summary>
public sealed class CoverCache(AppPaths paths)
{
    private readonly ConcurrentDictionary<(string Path, int Width), Task<Bitmap?>> _covers = new();

    public Task<Bitmap?> GetAsync(Book book, int width)
    {
        if (string.IsNullOrWhiteSpace(book.CoverPath)) return Task.FromResult<Bitmap?>(null);
        var path = Path.IsPathRooted(book.CoverPath)
            ? book.CoverPath
            : Path.Combine(paths.Data, book.CoverPath);
        return _covers.GetOrAdd((path, width),
            key => Task.Run(() => Decode(key.Path, key.Width)));
    }

    /// <summary>Drops cached decodes of a file that was just replaced.</summary>
    public void Forget(string path)
    {
        var full = Path.GetFullPath(path);
        foreach (var key in _covers.Keys.Where(key => Path.GetFullPath(key.Path) == full))
            _covers.TryRemove(key, out _);
    }

    private static Bitmap? Decode(string path, int width)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = File.OpenRead(path);
            return Bitmap.DecodeToWidth(stream, width);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            // A broken cover falls back to the title initial.
            return null;
        }
    }

}
