using Kkindle.Core;

namespace Kkindle.Infrastructure;

/// <summary>Enriches the device list from the exact device book, without writing annotations.</summary>
internal static class KindleAnnotationClippingTextResolver
{
    private const int ResolutionVersion = 1;
    public static bool NeedsResolution(KindleClipping clip) => clip.SourceBook is not null
        && clip.Type == KindleClippingType.Highlight
        && (!clip.IsTextResolved || clip.TextResolutionVersion != ResolutionVersion);

    public static async Task ResolveAsync(
        KindleDevice device, IReadOnlyList<KindleBook> deviceBooks,
        IReadOnlyList<KindleClipping> clippings, IKindleDeviceService kindle,
        CancellationToken cancellationToken = default,
        IProgress<KindleAnnotationSyncProgress>? progress = null)
    {
        var pending = clippings.Where(NeedsResolution).ToArray();
        if (pending.Length == 0) return;
        var unresolved = pending.Select(clip => clip.SourceBook!.BookId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = deviceBooks.Where(book => book.Format.ToLowerInvariant() is "azw3" or "mobi" or "azw" or "prc")
            .Where(book => book.Size <= 512L * 1024 * 1024)
            .OrderByDescending(book => pending.Any(clip => string.Equals(clip.BookTitle, book.Title, StringComparison.OrdinalIgnoreCase)))
            .ThenBy(book => book.Size).ToArray();
        var root = Path.Combine(Path.GetTempPath(), "Kkindle", "annotation-display", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            for (var index = 0; index < candidates.Length && unresolved.Count > 0; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var book = candidates[index];
                progress?.Report(new(KindleAnnotationSyncStage.ProcessingBooks, index, candidates.Length, book.Title));
                var directory = Path.Combine(root, index.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Directory.CreateDirectory(directory);
                try
                {
                    var file = await kindle.ExportBookAsync(device, book, directory,
                        progress is null ? null : new KindleAnnotationTransferProgress(progress, KindleAnnotationSyncStage.ReadingBook, book.Title),
                        cancellationToken);
                    var raw = await KindleBookTextService.ReadAsync(file, cancellationToken);
                    foreach (var identity in pending.Select(clip => clip.SourceBook!).DistinctBy(identity => identity.BookId))
                    {
                        if (!unresolved.Contains(identity.BookId) || !raw.Matches(identity)) continue;
                        foreach (var clip in clippings.Where(clip => string.Equals(clip.SourceBook?.BookId, identity.BookId, StringComparison.OrdinalIgnoreCase)))
                        {
                            clip.BookTitle = raw.Title;
                            clip.Author = raw.Author;
                            if (clip.Type != KindleClippingType.Highlight || clip.StartPosition is not { } start || clip.EndPosition is not { } end) continue;
                            var quote = raw.ExtractRange(start, end);
                            if (string.IsNullOrWhiteSpace(quote)) continue;
                            clip.Content = quote;
                            clip.IsTextResolved = true;
                            clip.TextResolutionVersion = ResolutionVersion;
                        }
                        unresolved.Remove(identity.BookId);
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                    or InvalidDataException or NotSupportedException or TimeoutException) { }
                finally
                {
                    try { Directory.Delete(directory, recursive: true); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            foreach (var clip in pending.Where(NeedsResolution))
            {
                clip.IsTextResolved = false;
                clip.Content = UiText.IsEnglish
                    ? "Highlight text unavailable (book missing or unsupported format)"
                    : "无法读取划线原文（书籍不可用或格式不支持）";
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
