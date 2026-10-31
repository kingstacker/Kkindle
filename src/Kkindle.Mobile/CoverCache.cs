using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle.Mobile;

/// <summary>
/// Decodes covers off the UI thread at display size. Search re-renders the grid
/// on every keystroke, so full-size decoding per tile is not affordable on a phone.
/// </summary>
public sealed class CoverCache(AppPaths paths)
{
    private readonly ConcurrentDictionary<(string Path, int Width, bool Gray), Task<Bitmap?>> _covers = new();

    public Task<Bitmap?> GetAsync(Book book, int width, bool grayscale)
    {
        if (string.IsNullOrWhiteSpace(book.CoverPath)) return Task.FromResult<Bitmap?>(null);
        var path = Path.IsPathRooted(book.CoverPath)
            ? book.CoverPath
            : Path.Combine(paths.Data, book.CoverPath);
        return _covers.GetOrAdd((path, width, grayscale),
            key => Task.Run(() => Decode(key.Path, key.Width, key.Gray)));
    }

    /// <summary>Drops cached decodes of a file that was just replaced.</summary>
    public void Forget(string path)
    {
        var full = Path.GetFullPath(path);
        foreach (var key in _covers.Keys.Where(key => Path.GetFullPath(key.Path) == full))
            _covers.TryRemove(key, out _);
    }

    private static Bitmap? Decode(string path, int width, bool grayscale)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = File.OpenRead(path);
            var bitmap = Bitmap.DecodeToWidth(stream, width);
            if (!grayscale) return bitmap;
            using (bitmap) return ToGrayscale(bitmap);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            // A broken cover falls back to the title initial.
            return null;
        }
    }

    private static Bitmap ToGrayscale(Bitmap source)
    {
        var format = source.Format ?? PixelFormat.Bgra8888;
        var target = new WriteableBitmap(source.PixelSize, source.Dpi, format, AlphaFormat.Premul);
        using var buffer = target.Lock();
        var size = buffer.RowBytes * buffer.Size.Height;
        source.CopyPixels(new PixelRect(source.PixelSize), buffer.Address, size, buffer.RowBytes);

        var pixels = new byte[size];
        Marshal.Copy(buffer.Address, pixels, 0, size);
        var redFirst = format == PixelFormat.Rgba8888;
        for (var index = 0; index + 3 < size; index += 4)
        {
            var red = pixels[index + (redFirst ? 0 : 2)];
            var green = pixels[index + 1];
            var blue = pixels[index + (redFirst ? 2 : 0)];
            var gray = (byte)((red * 77 + green * 150 + blue * 29 + 128) >> 8);
            pixels[index] = gray;
            pixels[index + 1] = gray;
            pixels[index + 2] = gray;
        }
        Marshal.Copy(pixels, 0, buffer.Address, size);
        return target;
    }
}
