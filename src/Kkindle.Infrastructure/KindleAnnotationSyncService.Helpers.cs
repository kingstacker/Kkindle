using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Kkindle.Core;
#if NET10_0_OR_GREATER
using Kkindle.Layout;
#endif

namespace Kkindle.Infrastructure;

public sealed partial class KindleAnnotationSyncService
{
    private async Task<ReaderAnnotation?> CreateLocalAnnotationAsync(
        BookContext context,
        DeviceAnnotationPair device,
        CancellationToken cancellationToken)
    {
        var chunks = await GetReaderChaptersAsync(context, null, cancellationToken);
        if (chunks.Count == 0) return null;
        var location = LocateQuote(chunks, device.Quote)
            ?? LocateQuoteWithDeviceContext(chunks, context.RawText, device);
        if (location is null) return null;
        var annotation = new ReaderAnnotation
        {
            BookId = context.LocalBook.Id,
            BookFileId = location.Chunk.BookFileId,
            ChapterPath = location.Chunk.ChapterPath,
            StartOffset = location.Start,
            EndOffset = location.End,
            SelectedText = location.SelectedText,
            Prefix = location.Prefix,
            Suffix = location.Suffix,
            Color = "#000000",
            UnderlineStyle = "marker",
            Note = device.NoteText,
            CreatedAt = device.CreatedAt,
            UpdatedAt = device.UpdatedAt
        };
        return annotation;
    }

    private async Task<IReadOnlyList<BookContentChunk>> GetReaderChaptersAsync(
        BookContext context, Guid? fileId, CancellationToken cancellationToken)
    {
        var key = fileId ?? Guid.Empty;
        if (context.ReaderChapters.TryGetValue(key, out var cached)) return cached;
        var cacheKey = ReaderChapterKey(context.LocalBook, fileId);
        if (!TryGetCachedReaderChapters(cacheKey, out var chapters))
        {
            chapters = await ReadReaderChaptersAsync(context.LocalBook, cancellationToken, fileId);
            StoreReaderChapters(cacheKey, chapters);
        }
        context.ReaderChapters[key] = chapters;
        if (chapters.Count > 0) context.ReaderChapters[chapters[0].BookFileId] = chapters;
        return chapters;
    }

    private async Task<IReadOnlyList<BookContentChunk>> ReadReaderChaptersAsync(
        Book book,
        CancellationToken cancellationToken,
        Guid? fileId = null)
    {
        var file = book.Files
            .Where(candidate => fileId is null || candidate.Id == fileId)
            .OrderBy(candidate => candidate.Format.Equals("epub", StringComparison.OrdinalIgnoreCase) ? 0
                : candidate.Format is "azw3" or "mobi" or "azw" or "prc" ? 1 : 2)
            .FirstOrDefault(candidate => candidate.Format is "epub" or "azw3" or "mobi" or "azw" or "prc");
        if (file is null) return [];

        string? temporary = null;
        try
        {
            var source = await ResolveExistingBookPathAsync(file, cancellationToken);
            if (source is null) return [];
            var readerSource = source;
            var readerHash = file.Sha256;
            if (file.Format.Equals("epub", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(readerHash))
                    readerHash = await Hashing.Sha256Async(source, cancellationToken);
            }
            else
            {
                temporary = Path.Combine(
                    Path.GetTempPath(),
                    "Kkindle",
                    "annotation-sync-index",
                    Guid.NewGuid().ToString("N") + ".epub");
                Directory.CreateDirectory(Path.GetDirectoryName(temporary)!);
                await Azw3ReaderService.PrepareEpubAsync(source, temporary, cancellationToken);
                readerHash = await Hashing.Sha256Async(temporary, cancellationToken);
                readerSource = temporary;
            }

            var document = await new EpubReaderPreparationService(_paths).PrepareAsync(
                readerSource,
                readerHash!,
                cancellationToken);
            // Search chunks normalize text; annotation coordinates must instead
            // use exactly the same stream as the native reader.
            var chapters = new List<BookContentChunk>();
            for (var index = 0; index < document.Chapters.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
#if NET10_0_OR_GREATER
                var body = await Task.Run(() => new XhtmlChapterLoader().Load(
                    document.Chapters[index], cancellationToken).BodyText, cancellationToken);
                chapters.Add(new BookContentChunk(0, book.Id, file.Id, readerHash!, index, 0,
                    document.ChapterTitles.ElementAtOrDefault(index) ?? string.Empty,
                    Path.GetRelativePath(document.RootPath, document.Chapters[index]).Replace('\\', '/'),
                    0, body.Length, body, 0));
#else
                throw new NotSupportedException("当前运行环境不支持精确的阅读器批注定位。");
#endif
            }
            return chapters;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or NotSupportedException)
        {
            return [];
        }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private async Task<string?> ResolveExistingBookPathAsync(
        BookFile file,
        CancellationToken cancellationToken)
    {
        var recorded = _library.GetAbsoluteFilePath(file);
        if (File.Exists(recorded)) return recorded;
        var fileName = Path.GetFileName(file.RelativePath);
        if (string.IsNullOrWhiteSpace(fileName) || !Directory.Exists(_paths.Library)) return null;
        foreach (var candidate in Directory.EnumerateFiles(
                     _paths.Library,
                     fileName,
                     new EnumerationOptions
                     {
                         RecurseSubdirectories = true,
                         IgnoreInaccessible = true,
                         AttributesToSkip = FileAttributes.ReparsePoint
                     }))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(file.Sha256)) return candidate;
            var hash = await Hashing.Sha256Async(candidate, cancellationToken);
            if (hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                return candidate;
        }
        return null;
    }

    private async Task PrepareAnchorRepairsAsync(BookContext context, CancellationToken cancellationToken)
    {
        foreach (var (local, device) in context.MatchedAnnotations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var owned = device.Highlight.AnnotationId == "kkindle.highlight." + local.Id.ToString("N");
            var quote = owned ? local.SelectedText : device.Quote;
            // An unreadable local book only prevents the computer-side repair; the
            // device range is derived from the annotation's own text and context.
            var current = local;
            var chapters = await GetReaderChaptersAsync(context, local.BookFileId, cancellationToken);
            if (chapters.Count > 0)
            {
                var currentChapter = chapters.Where(chapter => string.Equals(chapter.ChapterPath,
                    local.ChapterPath, StringComparison.OrdinalIgnoreCase)).ToArray();
                var location = LocateQuoteWithContext(currentChapter, quote, local.Prefix, local.Suffix)
                    ?? LocateQuote(currentChapter, quote)
                    ?? LocateQuote(chapters, quote);
                if (location is not null)
                {
                    var corrected = CloneAnnotation(local);
                    corrected.ChapterPath = location.Chunk.ChapterPath;
                    corrected.StartOffset = location.Start;
                    corrected.EndOffset = location.End;
                    corrected.SelectedText = location.SelectedText;
                    if (corrected.ChapterPath != local.ChapterPath || corrected.StartOffset != local.StartOffset
                        || corrected.EndOffset != local.EndOffset || corrected.SelectedText != local.SelectedText)
                    {
                        corrected.Fragment = null;
                        corrected.Prefix = location.Prefix;
                        corrected.Suffix = location.Suffix;
                        // Repositioning is not a new edit to the user's note.
                        context.LocalRepairs[local.Id] = corrected;
                    }
                    current = corrected;
                }
            }
            if (context.RawText is not { } raw) continue;
            // Records written by Kkindle follow the computer's text, also when the
            // local annotation was recreated with a new id (for example after it
            // was imported back). Kindle-made records are never moved.
            var writtenByKkindle = device.Highlight.AnnotationId.StartsWith(
                "kkindle.highlight.", StringComparison.Ordinal);
            // Device ranges are closed, so the last byte is end - 1.
            if (writtenByKkindle && raw.TryFindRange(current.SelectedText,
                    current.Prefix, current.Suffix, out var start, out var end)
                && (start != device.Highlight.Start || end - 1 != device.Highlight.End))
            {
                context.AnchorRepairs.Add(new(device, local.Id, start, end - 1));
                continue;
            }
            if (device.Note is { } note && IsOwnedNote(note)
                && (note.End != device.Highlight.End
                    || note.Start != raw.LastCharacterStart(device.Highlight.Start, device.Highlight.End)))
                context.AnchorRepairs.Add(new(device, local.Id, device.Highlight.Start, device.Highlight.End, NoteOnly: true));
        }
    }

    /// <summary>
    /// Short or repeated device quotes are disambiguated with the visible text
    /// around the Kindle position. Only a single exact match is accepted.
    /// </summary>
    private static LocatedQuote? LocateQuoteWithDeviceContext(
        IReadOnlyList<BookContentChunk> chunks,
        KindleRawBookText? raw,
        DeviceAnnotationPair device)
    {
        if (raw is null || raw.Text.Length == 0) return null;
        var highlight = device.Highlight;
        var before = NormalizeWithMap(raw.ExtractRange(
            Math.Max(0, highlight.Start - 512), highlight.Start)).Value;
        var after = NormalizeWithMap(raw.ExtractRange(
            device.ExclusiveEnd, Math.Min(raw.Text.LongLength, device.ExclusiveEnd + 512))).Value;
        foreach (var length in new[] { 24, 12, 6 })
        {
            var prefix = before.Length > length ? before[^length..] : before;
            var suffix = after.Length > length ? after[..length] : after;
            if (prefix.Length == 0 && suffix.Length == 0) return null;
            var located = LocateQuoteWithContext(chunks, device.Quote, prefix, suffix)
                ?? (prefix.Length > 0 ? LocateQuoteWithContext(chunks, device.Quote, prefix, null) : null)
                ?? (suffix.Length > 0 ? LocateQuoteWithContext(chunks, device.Quote, null, suffix) : null);
            if (located is not null) return located;
        }
        return null;
    }

    private static LocatedQuote? LocateQuote(
        IReadOnlyList<BookContentChunk> chunks,
        string quote) => LocateQuoteWithContext(chunks, quote, null, null);

    private static LocatedQuote? LocateQuoteWithContext(
        IReadOnlyList<BookContentChunk> chunks, string quote, string? prefix, string? suffix)
    {
        var normalizedQuote = NormalizeWithMap(quote).Value;
        var normalizedPrefix = NormalizeWithMap(prefix ?? string.Empty).Value;
        var normalizedSuffix = NormalizeWithMap(suffix ?? string.Empty).Value;
        if (normalizedQuote.Length == 0) return null;
        var matches = new List<(BookContentChunk Chunk, NormalizedText Content, int Index)>();
        foreach (var chunk in chunks)
        {
            var normalized = NormalizeWithMap(chunk.Content);
            var offset = 0;
            while (offset <= normalized.Value.Length - normalizedQuote.Length)
            {
                var index = normalized.Value.IndexOf(
                    normalizedQuote,
                    offset,
                    StringComparison.OrdinalIgnoreCase);
                if (index < 0) break;
                offset = index + Math.Max(1, normalizedQuote.Length);
                if (normalizedPrefix.Length > 0 && !normalized.Value[..index].EndsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (normalizedSuffix.Length > 0 && !normalized.Value[(index + normalizedQuote.Length)..].StartsWith(normalizedSuffix, StringComparison.OrdinalIgnoreCase)) continue;
                matches.Add((chunk, normalized, index));
            }
        }
        var distinct = matches
            .GroupBy(match => (
                match.Chunk.BookFileId,
                ChapterPath: match.Chunk.ChapterPath,
                Start: match.Chunk.StartOffset + match.Content.Map[match.Index]))
            .Select(group => group.First())
            .ToArray();
        if (distinct.Length != 1) return null;
        var match = distinct[0];
        var sourceStart = match.Content.Map[match.Index];
        var sourceEnd = match.Content.Map[match.Index + normalizedQuote.Length - 1] + 1;
        if (sourceEnd <= sourceStart) return null;
        var selected = match.Chunk.Content[sourceStart..sourceEnd];
        return new LocatedQuote(
            match.Chunk,
            match.Chunk.StartOffset + sourceStart,
            match.Chunk.StartOffset + sourceEnd,
            selected,
            match.Chunk.Content[Math.Max(0, sourceStart - 40)..sourceStart],
            match.Chunk.Content[sourceEnd..Math.Min(match.Chunk.Content.Length, sourceEnd + 40)]);
    }

    private static (List<KindleAnnotationMutation> Mutations, int ChangedAnnotations) BuildDeviceMutations(
        IReadOnlyList<BookContext> contexts)
    {
        var mutations = new List<KindleAnnotationMutation>();
        var byKey = new Dictionary<(string Id, string BookId), int>();
        var changedAnnotations = 0;
        foreach (var context in contexts)
        {
            if (context.RawText is null) continue;
            var bookData = ResolveBookData(context);
            var bookId = context.DeviceRecords.FirstOrDefault()?.Book.BookId
                ?? context.RawText.CreateBookId();
            if (string.IsNullOrWhiteSpace(bookId))
            {
                context.UnsupportedCount += context.ChangesToDevice.Count;
                continue;
            }

            var changed = new HashSet<Guid>();
            // A note added to a highlight that is also being repositioned must
            // be placed inside the corrected range, not the stale one.
            var repairedRanges = new Dictionary<string, (long Start, long End)>(StringComparer.Ordinal);
            foreach (var repair in context.AnchorRepairs)
                repairedRanges.TryAdd(repair.Device.Highlight.AnnotationId, (repair.Start, repair.End));

            foreach (var change in context.ChangesToDevice)
            {
                var local = change.Local;
                var paired = change.ExistingDevice;
                if (paired is not null)
                {
                    if (NotesEqual(local.Note, paired.NoteText)) continue;
                    if (string.IsNullOrWhiteSpace(local.Note))
                    {
                        // Deletions are intentionally not propagated in this
                        // first safe sync mode.
                        context.UnsupportedCount++;
                        continue;
                    }
                    var modified = Math.Max(
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        local.UpdatedAt.ToUnixTimeMilliseconds());
                    if (paired.Note is { } note)
                    {
                        AddMutation(mutations, byKey, UpdateNoteMutation(note, local.Note, modified));
                    }
                    else
                    {
                        var range = repairedRanges.TryGetValue(paired.Highlight.AnnotationId, out var repaired)
                            ? repaired
                            : (paired.Highlight.Start, paired.Highlight.End);
                        AddMutation(
                            mutations,
                            byKey,
                            NewNoteMutation(context.RawText, range.Item1, range.Item2, local, bookData, bookId, modified));
                    }
                    changed.Add(local.Id);
                    continue;
                }

                if (!context.RawText.TryFindRange(
                        local.SelectedText,
                        local.Prefix,
                        local.Suffix,
                        out var start,
                        out var end))
                {
                    context.UnsupportedCount++;
                    continue;
                }
                var now = Math.Max(
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    local.UpdatedAt.ToUnixTimeMilliseconds());
                var created = local.CreatedAt.ToUnixTimeMilliseconds();
                var highlightId = "kkindle.highlight." + local.Id.ToString("N");
                var closedEnd = end - 1;
                var highlight = NewHighlightMutation(
                    highlightId,
                    bookId,
                    bookData,
                    start,
                    closedEnd,
                    created,
                    now);
                AddMutation(mutations, byKey, highlight);
                if (!string.IsNullOrWhiteSpace(local.Note))
                {
                    var noteStart = context.RawText.LastCharacterStart(start, closedEnd);
                    AddMutation(
                        mutations,
                        byKey,
                        NewPayloadMutation(
                            "kkindle.note." + local.Id.ToString("N"),
                            bookId,
                            3,
                            noteStart,
                            closedEnd,
                            created,
                            now,
                            "NOTE",
                            NoteMetadata(local.Note),
                            bookData) with
                        {
                            NoteText = local.Note
                        });
                }
                changed.Add(local.Id);
            }
            foreach (var repair in context.AnchorRepairs)
            {
                if (!repair.NoteOnly)
                    AddRelocatedMutation(mutations, byKey, repair.Device.Highlight, repair.Start, repair.End);
                if (repair.Device.Note is { } note)
                {
                    long noteStart, noteEnd;
                    if (IsOwnedNote(note))
                    {
                        noteStart = context.RawText.LastCharacterStart(repair.Start, repair.End);
                        noteEnd = repair.End;
                    }
                    else
                    {
                        var delta = repair.Start - repair.Device.Highlight.Start;
                        noteStart = Math.Clamp(note.Start + delta, repair.Start, repair.End);
                        noteEnd = Math.Clamp(note.End + delta, noteStart, repair.End);
                        if (noteEnd <= noteStart) { noteStart = repair.Start; noteEnd = repair.End; }
                    }
                    AddRelocatedMutation(mutations, byKey, note, noteStart, noteEnd);
                }
                changed.Add(repair.LocalId);
            }
            changedAnnotations += changed.Count;
        }
        return (mutations, changedAnnotations);
    }

    private static bool IsOwnedNote(KindleAnnotationRecord note) =>
        note.AnnotationId.StartsWith("kkindle.note.", StringComparison.Ordinal);

    private static void AddRelocatedMutation(List<KindleAnnotationMutation> mutations,
        Dictionary<(string Id, string BookId), int> byKey,
        KindleAnnotationRecord record, long start, long end)
    {
        var key = (record.AnnotationId, record.Book.BookId);
        var original = byKey.TryGetValue(key, out var index) ? mutations[index]
            : new KindleAnnotationMutation(record.AnnotationId, record.Book.BookId, record.Dataset,
                record.Start, record.End, record.CreatedTime, record.ModifiedTime, record.NoteText, record.SerializedPayload);
        var payload = JsonNode.Parse(original.SerializedPayload) as JsonObject ?? new JsonObject();
        var modified = Math.Max(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), original.ModifiedTime + 1);
        payload["start_position"] = PositionNode(start);
        payload["end_position"] = PositionNode(end);
        payload["last_modified"] = modified;
        AddMutation(mutations, byKey, original with
        {
            Start = start, End = end, ModifiedTime = modified, SerializedPayload = payload.ToJsonString(CompactJson)
        });
    }

    private static JsonObject ResolveBookData(BookContext context)
    {
        var existing = context.DeviceRecords.FirstOrDefault();
        if (existing is not null)
        {
            try
            {
                var root = JsonNode.Parse(existing.SerializedPayload) as JsonObject;
                if (root?["book_data"] is JsonObject bookData)
                    return (JsonObject)bookData.DeepClone();
                if (root?["bookData"] is JsonObject camelBookData)
                {
                    var cloned = (JsonObject)camelBookData.DeepClone();
                    return cloned;
                }
            }
            catch (System.Text.Json.JsonException) { }
        }
        return new JsonObject
        {
            ["asin"] = context.RawText?.Asin ?? string.Empty,
            ["contentType"] = "EBOK",
            ["guid"] = context.RawText?.CreateGuid() ?? string.Empty,
            ["isOwnedByCustomer"] = 0,
            ["isSample"] = 0
        };
    }

    private static KindleAnnotationMutation UpdateNoteMutation(
        KindleAnnotationRecord note,
        string noteText,
        long modified)
    {
        JsonObject payload;
        try
        {
            payload = JsonNode.Parse(note.SerializedPayload) as JsonObject ?? [];
        }
        catch (System.Text.Json.JsonException)
        {
            payload = [];
        }
        payload["last_modified"] = modified;
        payload["json_metadata"] = NoteMetadata(noteText);
        return new KindleAnnotationMutation(
            note.AnnotationId,
            note.Book.BookId,
            note.Dataset,
            note.Start,
            note.End,
            note.CreatedTime,
            modified,
            noteText,
            payload.ToJsonString(CompactJson));
    }

    private static KindleAnnotationMutation NewNoteMutation(
        KindleRawBookText raw,
        long highlightStart,
        long highlightEnd,
        ReaderAnnotation local,
        JsonObject bookData,
        string bookId,
        long modified)
    {
        // Anchored on the last selected character, like the Kindle's own notes.
        var start = raw.LastCharacterStart(highlightStart, highlightEnd);
        // The note itself is created now, even when its highlight is older.
        return NewPayloadMutation(
            "kkindle.note." + local.Id.ToString("N"),
            bookId,
            3,
            start,
            highlightEnd,
            modified,
            modified,
            "NOTE",
            NoteMetadata(local.Note),
            bookData) with
        {
            NoteText = local.Note
        };
    }

    private static KindleAnnotationMutation NewHighlightMutation(
        string annotationId,
        string bookId,
        JsonObject bookData,
        long start,
        long end,
        long created,
        long modified) =>
        NewPayloadMutation(
            annotationId,
            bookId,
            1,
            start,
            end,
            created,
            modified,
            "HIGHLIGHT",
            "{}",
            bookData);

    private static KindleAnnotationMutation NewPayloadMutation(
        string annotationId,
        string bookId,
        int dataset,
        long start,
        long end,
        long created,
        long modified,
        string type,
        string metadata,
        JsonObject bookData)
    {
        // Keep the computer's original creation time so the Kindle lists the
        // annotation in its real order; it can never be after the edit time.
        created = Math.Clamp(created, 0, modified);
        var payload = new JsonObject
        {
            ["book_data"] = bookData.DeepClone(),
            ["created_time"] = created,
            ["end_position"] = PositionNode(end),
            ["json_metadata"] = metadata,
            ["last_modified"] = modified,
            ["position_type"] = 0,
            ["start_position"] = PositionNode(start),
            ["type"] = type
        };
        return new KindleAnnotationMutation(
            annotationId,
            bookId,
            dataset,
            start,
            end,
            created,
            modified,
            string.Empty,
            payload.ToJsonString(CompactJson));
    }

    private static JsonObject PositionNode(long value) => new()
    {
        ["longPosition"] = value.ToString(CultureInfo.InvariantCulture),
        ["shortPosition"] = value
    };

    private static string NoteMetadata(string noteText) =>
        new JsonObject { ["note_text"] = noteText }.ToJsonString(CompactJson);

    private static void AddMutation(
        List<KindleAnnotationMutation> mutations,
        Dictionary<(string Id, string BookId), int> byKey,
        KindleAnnotationMutation mutation)
    {
        var key = (mutation.AnnotationId, mutation.BookId);
        if (byKey.TryGetValue(key, out var index))
            mutations[index] = mutation;
        else
        {
            byKey[key] = mutations.Count;
            mutations.Add(mutation);
        }
    }

    private static NormalizedText NormalizeWithMap(string value)
    {
        var text = new StringBuilder(value.Length);
        var map = new List<int>(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsWhiteSpace(character))
            {
                continue;
            }
            text.Append(character);
            map.Add(index);
        }
        return new NormalizedText(text.ToString(), map.ToArray());
    }

    private sealed record NormalizedText(string Value, int[] Map);

    private sealed record LocatedQuote(
        BookContentChunk Chunk,
        int Start,
        int End,
        string SelectedText,
        string Prefix,
        string Suffix);
}
