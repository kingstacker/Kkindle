using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kkindle.Core;

namespace Kkindle.Infrastructure;

/// <summary>
/// Reconciles device annotations as a union. When both sides describe the same
/// quoted range, the newer non-empty note wins; records unique to either side
/// are copied to the other side and are never removed automatically.
/// </summary>
public sealed partial class KindleAnnotationSyncService
{
    private static readonly JsonSerializerOptions CompactJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };
    private readonly AppPaths _paths;
    private readonly IBookLibraryService _library;
    private readonly ReaderDataService _readerData;
    private readonly EpubBookContentService _bookContent;

    public KindleAnnotationSyncService(
        AppPaths paths,
        IBookLibraryService library,
        ReaderDataService readerData,
        EpubBookContentService bookContent)
    {
        _paths = paths;
        _library = library;
        _readerData = readerData;
        _bookContent = bookContent;
    }

    public async Task<KindleAnnotationSyncPreview> AnalyzeAsync(
        KindleDevice device,
        IReadOnlyList<KindleBook> deviceBooks,
        IKindleDeviceService kindle,
        CancellationToken cancellationToken = default,
        IProgress<KindleAnnotationSyncProgress>? progress = null)
    {
        var contexts = await PrepareContextsAsync(
            device,
            deviceBooks,
            kindle,
            buildApplyPlan: false,
            cancellationToken, progress);
        var summaries = contexts
            .Where(context => context.ChangesToComputer.Count > 0
                || context.ChangesToDevice.Count > 0
                || context.LocalRepairs.Count > 0 || context.AnchorRepairs.Count > 0
                || context.UnsupportedCount > 0)
            .Select(context => new KindleAnnotationSyncBookSummary(
                context.LocalBook.Id,
                context.LocalBook.Title,
                context.DeviceBook.Title,
                context.DeviceBook.RelativePath,
                context.LocalAnnotations.Count,
                context.DeviceAnnotations.Count + context.DeviceUnsupportedCount,
                context.ComputerChangeCount,
                context.DeviceChangeCount,
                context.UnsupportedCount))
            .ToArray();
        var summarySignature = string.Join(
            '\n',
            summaries.Select(summary =>
                $"{summary.LocalBookId:N}|{summary.DeviceRelativePath}|{summary.LocalCount}|{summary.DeviceCount}|{summary.ChangesToComputer}|{summary.ChangesToDevice}|{summary.UnsupportedCount}"));
        var signatureSource = summarySignature + "\n" + string.Join('\n', contexts.SelectMany(context =>
            context.LocalAnnotations.OrderBy(annotation => annotation.Id)
                .Select(annotation => $"{annotation.Id:N}|{annotation.UpdatedAt:O}|{annotation.ChapterPath}|{annotation.StartOffset}|{annotation.EndOffset}|{annotation.Note}")
                .Concat(context.DeviceRecords.OrderBy(record => record.AnnotationId, StringComparer.Ordinal)
                    .Select(record => $"{record.Book.BookId}|{record.AnnotationId}|{record.SerializedPayload}"))));
        var signature = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(signatureSource))).ToLowerInvariant();
        progress?.Report(new(KindleAnnotationSyncStage.Ready, 1, 1));
        return new KindleAnnotationSyncPreview(
            summaries.Any(summary => summary.ChangesToComputer + summary.ChangesToDevice > 0),
            signature,
            summaries);
    }

    public async Task<KindleAnnotationSyncResult> SynchronizeAsync(
        KindleDevice device,
        IReadOnlyList<KindleBook> deviceBooks,
        IKindleDeviceService kindle,
        CancellationToken cancellationToken = default,
        IProgress<KindleAnnotationSyncProgress>? progress = null)
    {
        var contexts = await PrepareContextsAsync(
            device,
            deviceBooks,
            kindle,
            buildApplyPlan: true,
            cancellationToken, progress);
        var changedContexts = contexts
            .Where(context => context.ChangesToComputer.Count > 0
                || context.ChangesToDevice.Count > 0 || context.LocalRepairs.Count > 0 || context.AnchorRepairs.Count > 0)
            .ToArray();
        if (changedContexts.Length == 0)
        {
            progress?.Report(new(KindleAnnotationSyncStage.Completed, 1, 1));
            return new KindleAnnotationSyncResult(
                0, 0, 0,
                contexts.Sum(context => context.UnsupportedCount),
                null,
                null);
        }

        var imported = 0;
        var totalImports = changedContexts.Sum(context => context.ComputerChangeCount);
        var processedImports = 0;
        foreach (var context in changedContexts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var repair in context.LocalRepairs.Values.Where(repair =>
                         !context.ChangesToComputer.Any(change => change.ExistingLocal?.Id == repair.Id)))
            {
                progress?.Report(new(KindleAnnotationSyncStage.ImportingToComputer,
                    processedImports++, totalImports, context.LocalBook.Title));
                await _readerData.SaveAnnotationAsync(repair, cancellationToken);
                imported++;
            }
            foreach (var change in context.ChangesToComputer)
            {
                progress?.Report(new(KindleAnnotationSyncStage.ImportingToComputer,
                    processedImports++, totalImports, context.LocalBook.Title));
                if (change.ExistingLocal is { } existing)
                {
                    if (NotesEqual(existing.Note, change.Device.NoteText))
                        continue;
                    var updated = CloneAnnotation(context.LocalRepairs.GetValueOrDefault(existing.Id) ?? existing);
                    updated.Note = change.Device.NoteText;
                    updated.UpdatedAt = change.Device.UpdatedAt;
                    await _readerData.SaveAnnotationAsync(updated, cancellationToken);
                    imported++;
                    continue;
                }

                var annotation = await CreateLocalAnnotationAsync(
                    context,
                    change.Device,
                    cancellationToken);
                if (annotation is null)
                {
                    context.UnsupportedCount++;
                    continue;
                }
                await _readerData.SaveAnnotationAsync(annotation, cancellationToken);
                imported++;
            }
        }

        if (totalImports > 0)
            progress?.Report(new(KindleAnnotationSyncStage.ImportingToComputer, totalImports, totalImports));

        progress?.Report(new(KindleAnnotationSyncStage.PreparingWrite));
        var (mutations, deviceChanges) = BuildDeviceMutations(contexts);
        KindleAnnotationWriteResult? writeResult = null;
        if (mutations.Count > 0)
        {
            var snapshot = contexts[0].Snapshot;
            writeResult = await kindle.ApplyAnnotationMutationsAsync(
                device,
                snapshot,
                mutations,
                cancellationToken, progress);
        }

        progress?.Report(new(KindleAnnotationSyncStage.Completed, 1, 1));
        return new KindleAnnotationSyncResult(
            changedContexts.Length,
            imported,
            deviceChanges,
            contexts.Sum(context => context.UnsupportedCount),
            writeResult?.LocalBackupPath,
            writeResult?.DeviceBackupPath);
    }

    private async Task<IReadOnlyList<BookContext>> PrepareContextsAsync(
        KindleDevice device,
        IReadOnlyList<KindleBook> deviceBooks,
        IKindleDeviceService kindle,
        bool buildApplyPlan,
        CancellationToken cancellationToken,
        IProgress<KindleAnnotationSyncProgress>? progress)
    {
        progress?.Report(new(KindleAnnotationSyncStage.ReadingDevice));
        await Task.Yield();
        var snapshot = await kindle.ReadAnnotationSnapshotAsync(device, cancellationToken);
        if (snapshot is null || snapshot.Records.Count == 0 && deviceBooks.Count == 0) return [];
        progress?.Report(new(KindleAnnotationSyncStage.MatchingBooks));
        var localMatches = await _library.GetLibraryMatchRecordsAsync(cancellationToken);
        var comparison = BookLibraryComparer.Compare(localMatches, deviceBooks);
        var pairs = BuildUnambiguousPairs(comparison.Matches ?? []);
        var deviceByPath = deviceBooks.ToDictionary(
            book => NormalizeDevicePath(book.RelativePath),
            StringComparer.OrdinalIgnoreCase);
        var result = new List<BookContext>();
        var processed = 0;
        foreach (var pair in pairs)
        {
            progress?.Report(new(KindleAnnotationSyncStage.ProcessingBooks, processed++, pairs.Count));
            cancellationToken.ThrowIfCancellationRequested();
            if (!deviceByPath.TryGetValue(NormalizeDevicePath(pair.DeviceRelativePath), out var deviceBook))
                continue;
            var localBook = await _library.GetBookAsync(pair.LocalBookId, cancellationToken);
            if (localBook is null) continue;
            var localAnnotations = await ReadLocalAnnotationsAsync(localBook, cancellationToken);
            var stamp = DeviceBookStamp(device, deviceBook);
            if (localAnnotations.Count == 0)
            {
                // Without local annotations a book only matters when the device
                // database has records for it; skip the copy when that is known.
                if (snapshot.Records.Count == 0) continue;
                if (TryGetCachedIdentity(stamp, out var identity)
                    && !snapshot.Records.Any(record => IsSameDeviceBook(record.Book, identity)))
                    continue;
            }
            progress?.Report(new(KindleAnnotationSyncStage.ProcessingBooks,
                processed - 1, pairs.Count, localBook.Title));
            var rawText = await ReadDeviceBookTextAsync(
                device, deviceBook, kindle, stamp,
                allowCachedText: !buildApplyPlan,
                cancellationToken, progress);
            var records = rawText is null ? [] : snapshot.Records
                .Where(record => IsSameDeviceBook(record.Book, rawText)).ToArray();
            if (records.Length == 0 && localAnnotations.Count == 0) continue;
            // Multiple database identities for a file need explicit resolution;
            // a display title must never select a write target.
            if (records.Select(record => record.Book.BookId).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
                continue;

            var context = new BookContext(
                snapshot,
                localBook,
                deviceBook,
                localAnnotations,
                rawText,
                records);
            PrepareDeviceGroups(context);
            PrepareChanges(context, buildApplyPlan);
            await PrepareAnchorRepairsAsync(context, cancellationToken);
            result.Add(context);
        }
        progress?.Report(new(KindleAnnotationSyncStage.ProcessingBooks, pairs.Count, pairs.Count));
        return result;
    }

    private static IReadOnlyList<BookLibraryMatchPair> BuildUnambiguousPairs(
        IReadOnlyList<BookLibraryMatchPair> pairs)
    {
        var localCounts = pairs
            .GroupBy(pair => pair.LocalBookId)
            .ToDictionary(group => group.Key, group => group.Count());
        var deviceCounts = pairs
            .GroupBy(pair => NormalizeDevicePath(pair.DeviceRelativePath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Count(),
                StringComparer.OrdinalIgnoreCase);
        return pairs
            .Where(pair => localCounts[pair.LocalBookId] == 1
                && deviceCounts[NormalizeDevicePath(pair.DeviceRelativePath)] == 1)
            .ToArray();
    }

    private async Task<IReadOnlyList<ReaderAnnotation>> ReadLocalAnnotationsAsync(
        Book book,
        CancellationToken cancellationToken)
    {
        var annotations = new List<ReaderAnnotation>();
        foreach (var file in book.Files)
            annotations.AddRange(await _readerData.GetAnnotationsAsync(
                file.Id,
                cancellationToken,
                limit: 100_000));
        return annotations;
    }

    private async Task<KindleRawBookText?> ReadDeviceBookTextAsync(
        KindleDevice device,
        KindleBook book,
        IKindleDeviceService kindle,
        string? stamp,
        bool allowCachedText,
        CancellationToken cancellationToken,
        IProgress<KindleAnnotationSyncProgress>? progress = null)
    {
        if (book.Format is not ("azw3" or "mobi" or "azw" or "prc")) return null;
        if (allowCachedText && TryGetCachedDeviceText(stamp, out var cached)) return cached;
        var directory = Path.Combine(
            Path.GetTempPath(),
            "Kkindle",
            "annotation-sync-book",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = await kindle.ExportBookAsync(
                device,
                book,
                directory,
                progress: progress is null ? null : new KindleAnnotationTransferProgress(
                    progress, KindleAnnotationSyncStage.ReadingBook, book.Title),
                cancellationToken: cancellationToken);
            var text = await KindleBookTextService.ReadAsync(path, cancellationToken);
            StoreDeviceText(stamp, text);
            return text;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or NotSupportedException
            or TimeoutException)
        {
            return null;
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void PrepareDeviceGroups(BookContext context)
    {
        var records = context.DeviceRecords;
        var highlights = records.Where(record => record.Dataset == 1).ToArray();
        var notes = records.Where(record => record.Dataset == 3).ToArray();
        var consumedNotes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var highlight in highlights)
        {
            var note = notes
                .Where(candidate =>
                    !consumedNotes.Contains(candidate.AnnotationId)
                    && candidate.Start >= highlight.Start
                    && candidate.End <= highlight.End)
                .OrderByDescending(candidate => candidate.ModifiedTime)
                .FirstOrDefault();
            if (note is not null) consumedNotes.Add(note.AnnotationId);
            var owned = highlight.AnnotationId.StartsWith("kkindle.highlight.", StringComparison.Ordinal);
            var exclusiveEnd = context.RawText is not { } raw ? highlight.End
                : owned ? raw.OwnedDeviceEndToExclusive(highlight.End)
                : raw.DeviceEndToExclusive(highlight.End);
            var quote = context.RawText?.ExtractRange(highlight.Start, exclusiveEnd) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(quote) && !owned)
            {
                context.UnsupportedCount++;
                context.DeviceUnsupportedCount++;
                continue;
            }
            context.DeviceAnnotations.Add(new DeviceAnnotationPair(highlight, note, quote,
                context.RawText?.ExtractLegacyRange(highlight.Start, highlight.End) ?? string.Empty,
                context.RawText?.ExtractRange(highlight.Start, highlight.End) ?? string.Empty,
                exclusiveEnd));
        }
        var orphanNotes = notes.Count(note => !consumedNotes.Contains(note.AnnotationId));
        context.UnsupportedCount += orphanNotes;
        context.DeviceUnsupportedCount += orphanNotes;
    }

    private static void PrepareChanges(BookContext context, bool buildApplyPlan)
    {
        var matchedLocal = new HashSet<Guid>();
        foreach (var device in context.DeviceAnnotations)
        {
            var candidates = context.LocalAnnotations.Where(annotation =>
                !matchedLocal.Contains(annotation.Id)
                && (device.Highlight.AnnotationId == "kkindle.highlight." + annotation.Id.ToString("N")
                    || (TextKey(annotation.SelectedText) == TextKey(device.Quote)
                        && context.RawText is not null
                        && context.RawText.TryFindRange(annotation.SelectedText, annotation.Prefix, annotation.Suffix,
                    out var start, out var end)
                        && Math.Abs(start - device.Highlight.Start) <= 3
                        && Math.Abs(end - device.ExclusiveEnd) <= 3)
                    || (!string.IsNullOrWhiteSpace(device.LegacyQuote)
                        && TextKey(annotation.SelectedText) == TextKey(device.LegacyQuote)
                        && annotation.CreatedAt.ToUnixTimeMilliseconds() == device.Highlight.CreatedTime)
                    // Imported before the closed Kindle end was honored: the quote
                    // lost its last single-byte character. Same record, not a new one.
                    || (!string.IsNullOrWhiteSpace(device.TruncatedQuote)
                        && TextKey(annotation.SelectedText) == TextKey(device.TruncatedQuote)
                        && annotation.CreatedAt.ToUnixTimeMilliseconds() == device.Highlight.CreatedTime))).ToArray();
            if (candidates.Length > 1)
            {
                foreach (var candidate in candidates) matchedLocal.Add(candidate.Id);
                context.UnsupportedCount++;
                continue;
            }
            var existing = candidates.SingleOrDefault();
            if (existing is null)
            {
                context.ChangesToComputer.Add(new ComputerChange(device, null));
                continue;
            }
            matchedLocal.Add(existing.Id);
            context.MatchedAnnotations.Add((existing, device));
            var deviceNote = device.NoteText;
            if (NotesEqual(existing.Note, deviceNote)) continue;
            if (string.IsNullOrWhiteSpace(deviceNote))
            {
                if (!string.IsNullOrWhiteSpace(existing.Note))
                    context.ChangesToDevice.Add(new DeviceChange(existing, device));
            }
            else if (string.IsNullOrWhiteSpace(existing.Note))
            {
                // A note cleared on the computer after the Kindle note was last
                // edited is a deletion. Deletions are never propagated, and the
                // user's clear must not be undone by re-importing the old note.
                if (existing.UpdatedAt > device.UpdatedAt)
                    context.UnsupportedCount++;
                else
                    context.ChangesToComputer.Add(new ComputerChange(device, existing));
            }
            else if (device.UpdatedAt > existing.UpdatedAt)
            {
                // A Kindle clock running ahead would otherwise always win and
                // silently overwrite newer computer edits.
                if (device.UpdatedAt > DateTimeOffset.UtcNow + DeviceClockTolerance)
                    context.UnsupportedCount++;
                else
                    context.ChangesToComputer.Add(new ComputerChange(device, existing));
            }
            else if (existing.UpdatedAt > device.UpdatedAt)
            {
                context.ChangesToDevice.Add(new DeviceChange(existing, device));
            }
            else if (existing.UpdatedAt == device.UpdatedAt)
            {
                context.UnsupportedCount++;
            }
        }

        foreach (var local in context.LocalAnnotations.Where(annotation => !matchedLocal.Contains(annotation.Id)))
        {
            if (string.IsNullOrWhiteSpace(local.SelectedText))
            {
                context.UnsupportedCount++;
                continue;
            }
            if (context.RawText is null)
            {
                context.UnsupportedCount++;
                continue;
            }
            if (!context.RawText.TryFindRange(
                    local.SelectedText,
                    local.Prefix,
                    local.Suffix,
                    out _,
                    out _))
            {
                context.UnsupportedCount++;
                continue;
            }
            context.ChangesToDevice.Add(new DeviceChange(local, null));
        }
    }

    private sealed class BookContext(
        KindleAnnotationSnapshot snapshot,
        Book localBook,
        KindleBook deviceBook,
        IReadOnlyList<ReaderAnnotation> localAnnotations,
        KindleRawBookText? rawText,
        IReadOnlyList<KindleAnnotationRecord> deviceRecords)
    {
        public KindleAnnotationSnapshot Snapshot { get; } = snapshot;
        public Book LocalBook { get; } = localBook;
        public KindleBook DeviceBook { get; } = deviceBook;
        public IReadOnlyList<ReaderAnnotation> LocalAnnotations { get; } = localAnnotations;
        public KindleRawBookText? RawText { get; } = rawText;
        public IReadOnlyList<KindleAnnotationRecord> DeviceRecords { get; } = deviceRecords;
        public List<DeviceAnnotationPair> DeviceAnnotations { get; } = [];
        public List<ComputerChange> ChangesToComputer { get; } = [];
        public List<DeviceChange> ChangesToDevice { get; } = [];
        public Dictionary<Guid, IReadOnlyList<BookContentChunk>> ReaderChapters { get; } = [];
        public Dictionary<Guid, ReaderAnnotation> LocalRepairs { get; } = [];
        public List<(ReaderAnnotation Local, DeviceAnnotationPair Device)> MatchedAnnotations { get; } = [];
        public List<DeviceAnchorRepair> AnchorRepairs { get; } = [];
        public int ComputerChangeCount => ChangesToComputer.Count + LocalRepairs.Keys.Count(id =>
            !ChangesToComputer.Any(change => change.ExistingLocal?.Id == id));
        // One user-visible change per annotation, however many rows it needs.
        public int DeviceChangeCount => ChangesToDevice.Select(change => change.Local.Id)
            .Concat(AnchorRepairs.Select(repair => repair.LocalId)).Distinct().Count();
        public int UnsupportedCount { get; set; }
        // Kindle records that exist but cannot be paired; local-side problems
        // must not inflate the number of annotations shown for the Kindle.
        public int DeviceUnsupportedCount { get; set; }
    }

    private sealed record DeviceAnnotationPair(
        KindleAnnotationRecord Highlight,
        KindleAnnotationRecord? Note,
        string Quote,
        string LegacyQuote = "",
        string TruncatedQuote = "",
        long ExclusiveEnd = 0)
    {
        public string NoteText => Note?.NoteText ?? string.Empty;
        public DateTimeOffset CreatedAt => DateTimeOffset.FromUnixTimeMilliseconds(
            Highlight.CreatedTime);
        public DateTimeOffset UpdatedAt => DateTimeOffset.FromUnixTimeMilliseconds(
            Note?.ModifiedTime ?? Highlight.ModifiedTime);
    }

    private sealed record ComputerChange(
        DeviceAnnotationPair Device,
        ReaderAnnotation? ExistingLocal);

    private sealed record DeviceChange(
        ReaderAnnotation Local,
        DeviceAnnotationPair? ExistingDevice);

    /// <summary>Start and End are the highlight's corrected closed device range.</summary>
    private sealed record DeviceAnchorRepair(DeviceAnnotationPair Device, Guid LocalId, long Start, long End, bool NoteOnly = false);

    private static ReaderAnnotation CloneAnnotation(ReaderAnnotation source) => new()
    {
        Id = source.Id,
        BookId = source.BookId,
        BookFileId = source.BookFileId,
        ChapterPath = source.ChapterPath,
        Fragment = source.Fragment,
        StartOffset = source.StartOffset,
        EndOffset = source.EndOffset,
        SelectedText = source.SelectedText,
        Prefix = source.Prefix,
        Suffix = source.Suffix,
        Color = source.Color,
        UnderlineStyle = source.UnderlineStyle,
        Note = source.Note,
        CreatedAt = source.CreatedAt,
        UpdatedAt = source.UpdatedAt
    };

    private static string NormalizeDevicePath(string path) =>
        path.Replace('/', '\\').TrimStart('\\');

    private static bool IsSameDeviceBook(KindleAnnotationBookIdentity identity, KindleRawBookText raw) =>
        raw.Matches(identity);

    private static readonly TimeSpan DeviceClockTolerance = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The Kindle reader trims note text and may normalize line endings, so a
    /// round-tripped note must not be reported as a new difference.
    /// </summary>
    private static bool NotesEqual(string? left, string? right) => string.Equals(
        (left ?? string.Empty).ReplaceLineEndings("\n").Trim(),
        (right ?? string.Empty).ReplaceLineEndings("\n").Trim(),
        StringComparison.Ordinal);

    private static string NormalizeTitle(string? value) => string.Concat(
        (value ?? string.Empty)
            .Normalize(NormalizationForm.FormKC)
            .Where(character => !char.IsWhiteSpace(character)))
        .ToUpperInvariant();

    private static string TextKey(string? value) =>
        string.Concat((value ?? string.Empty)
            .Normalize(NormalizationForm.FormKC)
            .Where(character => !char.IsWhiteSpace(character)))
            .ToUpperInvariant();
}
