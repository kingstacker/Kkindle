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

        var nativeBody = nativeReader.BodyText ?? string.Empty;
        var nativeQuery = (searchQuery ?? string.Empty).Trim();
        // The index extract collapses XHTML whitespace, while the native
        // loader keeps the original text-node stream. Use the result's
        // surrounding context first, then its index offset as a stable
        // fallback, so repeated words in one chapter do not all jump to the
        // first occurrence.
        var nativeHit = ReaderSearchTextPolicy.FindBestMatchOffset(
            nativeBody,
            nativeQuery,
            searchContext,
            offset);
        nativeReader.ScrollToOffset(nativeHit >= 0 ? nativeHit : Math.Max(0, offset));
    }
}
