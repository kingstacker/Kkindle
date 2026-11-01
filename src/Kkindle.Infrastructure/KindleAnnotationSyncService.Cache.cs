using Kkindle.Core;

namespace Kkindle.Infrastructure;

/// <summary>
/// Repeated checks must not copy every shared book from the Kindle again.
/// Identities let books without annotations be skipped; full text is kept in a
/// bounded cache for analysis only, and every write plan reads fresh text.
/// </summary>
public sealed partial class KindleAnnotationSyncService
{
    private const long MaxCachedDeviceTextBytes = 128L * 1024 * 1024;
    private const int MaxCachedReaderBooks = 16;
    private readonly object _cacheGate = new();
    private readonly Dictionary<string, KindleRawBookText> _deviceIdentityCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, KindleRawBookText> _deviceTextCache = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _deviceTextOrder = new();
    private long _deviceTextBytes;
    private readonly Dictionary<string, IReadOnlyList<BookContentChunk>> _readerChapterCache = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _readerChapterOrder = new();

    /// <summary>
    /// A cache key is only produced when the scan supplies a modification time
    /// or hash; size alone cannot prove that a device file is unchanged.
    /// </summary>
    private static string? DeviceBookStamp(KindleDevice device, KindleBook book)
    {
        if (book.ModifiedAt is null && string.IsNullOrWhiteSpace(book.Sha256)) return null;
        return string.Join(
            '|',
            device.Identity,
            NormalizeDevicePath(book.RelativePath).ToUpperInvariant(),
            book.Size.ToString(System.Globalization.CultureInfo.InvariantCulture),
            (book.ModifiedAt?.UtcTicks ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture),
            book.Sha256.ToUpperInvariant());
    }

    private bool TryGetCachedIdentity(string? stamp, out KindleRawBookText identity)
    {
        identity = null!;
        if (stamp is null) return false;
        lock (_cacheGate)
        {
            if (!_deviceIdentityCache.TryGetValue(stamp, out var found)) return false;
            identity = found;
            return true;
        }
    }

    private bool TryGetCachedDeviceText(string? stamp, out KindleRawBookText text)
    {
        text = null!;
        if (stamp is null) return false;
        lock (_cacheGate)
        {
            if (!_deviceTextCache.TryGetValue(stamp, out var found)) return false;
            text = found;
            _deviceTextOrder.Remove(stamp);
            _deviceTextOrder.AddLast(stamp);
            return true;
        }
    }

    private void StoreDeviceText(string? stamp, KindleRawBookText text)
    {
        if (stamp is null) return;
        var size = text.Text.LongLength + (text.UnassembledText?.LongLength ?? 0);
        lock (_cacheGate)
        {
            _deviceIdentityCache[stamp] = text with { Text = [], UnassembledText = null };
            if (_deviceTextCache.Remove(stamp, out var previous))
            {
                _deviceTextOrder.Remove(stamp);
                _deviceTextBytes -= previous.Text.LongLength + (previous.UnassembledText?.LongLength ?? 0);
            }
            if (size > MaxCachedDeviceTextBytes / 4) return;
            _deviceTextCache[stamp] = text;
            _deviceTextOrder.AddLast(stamp);
            _deviceTextBytes += size;
            while (_deviceTextBytes > MaxCachedDeviceTextBytes && _deviceTextOrder.First is { } oldest)
            {
                _deviceTextOrder.RemoveFirst();
                if (_deviceTextCache.Remove(oldest.Value, out var evicted))
                    _deviceTextBytes -= evicted.Text.LongLength + (evicted.UnassembledText?.LongLength ?? 0);
            }
        }
    }

    /// <summary>
    /// Reader chapters depend only on local library files, so they are keyed by
    /// the stored file hashes and shared between analysis and synchronization.
    /// </summary>
    private static string? ReaderChapterKey(Book book, Guid? fileId)
    {
        if (book.Files.Count == 0 || book.Files.Any(file => string.IsNullOrWhiteSpace(file.Sha256))) return null;
        return $"{book.Id:N}|{fileId?.ToString("N")}|" + string.Join(
            ',',
            book.Files.OrderBy(file => file.Id).Select(file => $"{file.Id:N}:{file.Sha256.ToUpperInvariant()}"));
    }

    private bool TryGetCachedReaderChapters(string? key, out IReadOnlyList<BookContentChunk> chapters)
    {
        chapters = [];
        if (key is null) return false;
        lock (_cacheGate)
        {
            if (!_readerChapterCache.TryGetValue(key, out var found)) return false;
            chapters = found;
            _readerChapterOrder.Remove(key);
            _readerChapterOrder.AddLast(key);
            return true;
        }
    }

    private void StoreReaderChapters(string? key, IReadOnlyList<BookContentChunk> chapters)
    {
        if (key is null || chapters.Count == 0) return;
        lock (_cacheGate)
        {
            if (_readerChapterCache.ContainsKey(key)) _readerChapterOrder.Remove(key);
            _readerChapterCache[key] = chapters;
            _readerChapterOrder.AddLast(key);
            while (_readerChapterOrder.Count > MaxCachedReaderBooks && _readerChapterOrder.First is { } oldest)
            {
                _readerChapterOrder.RemoveFirst();
                _readerChapterCache.Remove(oldest.Value);
            }
        }
    }
}
