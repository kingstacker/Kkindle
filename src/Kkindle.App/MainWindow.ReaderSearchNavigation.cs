using System.Text.Json;
using Kkindle.Core;

namespace Kkindle;

public partial class MainWindow
{
    private async Task ScrollToPendingReaderChunkAsync(
        IReaderHost host,
        CancellationToken cancellationToken)
    {
        if (_readerPendingChunkOffset is not int offset) return;
        _readerPendingChunkOffset = null;
        var searchQuery = _readerPendingSearchQuery;
        _readerPendingSearchQuery = null;
        var searchContext = _readerPendingSearchContext;
        _readerPendingSearchContext = null;
        cancellationToken.ThrowIfCancellationRequested();
        if (host is not NativeReaderHost nativeReader)
        {
            return;
        }

        // Whole-book jumps locate the query inside the same body-text
        // stream; the chunk offset is only a hint, so matching the query
        // keeps behavior closest to the old DOM walker.
        var nativeBody = nativeReader.BodyText ?? string.Empty;
        var nativeQuery = (searchQuery ?? string.Empty).Trim();
        var nativeHit = nativeQuery.Length > 0
            ? nativeBody.IndexOf(nativeQuery, StringComparison.OrdinalIgnoreCase)
            : -1;
        nativeReader.ScrollToOffset(nativeHit >= 0 ? nativeHit : Math.Max(0, offset));
    }
}
