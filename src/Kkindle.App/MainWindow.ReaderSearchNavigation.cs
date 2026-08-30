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
        // keeps behavior closest to the old DOM walker. The chunk offset
        // itself comes from the search index's normalized extract, a
        // different text stream, so it must never be used directly against
        // the native body text — fall back to the earliest query term.
        var nativeBody = nativeReader.BodyText ?? string.Empty;
        var nativeQuery = (searchQuery ?? string.Empty).Trim();
        var nativeHit = nativeQuery.Length > 0
            ? nativeBody.IndexOf(nativeQuery, StringComparison.OrdinalIgnoreCase)
            : -1;
        if (nativeHit < 0 && nativeQuery.Length > 0)
        {
            foreach (var run in nativeQuery.Split(' ',
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var index = nativeBody.IndexOf(run, StringComparison.OrdinalIgnoreCase);
                if (index >= 0 && (nativeHit < 0 || index < nativeHit)) nativeHit = index;
            }
        }
        nativeReader.ScrollToOffset(nativeHit >= 0 ? nativeHit : Math.Max(0, offset));
    }
}
