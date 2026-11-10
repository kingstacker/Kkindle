using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Kkindle.Core;
using Microsoft.Data.Sqlite;

namespace Kkindle.Infrastructure;

internal sealed record KindleAnnotationBookKey(string BookId, string? Asin, string? Guid);

internal sealed record KindleAnnotationDatabaseRecord(
    string Id,
    KindleAnnotationBookKey Book,
    int Dataset,
    KindleClippingType Type,
    long Start,
    long End,
    string Note,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? UpdatedAt,
    string SerializedPayload);

/// <summary>
/// Reads personal highlights and notes from a copied Kindle account database.
/// The caller must pass a local snapshot; this reader never opens a WPD object
/// and never writes to the database.
/// </summary>
internal static class KindleAnnotationDatabaseReader
{
    private const string AnnotationTable = "nonsyncable_annotations";

    public static async Task<IReadOnlyList<KindleAnnotationDatabaseRecord>> ReadRecordsAsync(
        string databasePath,
        int maxItems = int.MaxValue,
        CancellationToken cancellationToken = default,
        bool requireCompleteSnapshot = false)
    {
        if (maxItems <= 0 || !File.Exists(databasePath)) return [];
        RejectLinkedPath(databasePath);
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        using (var queryOnly = connection.CreateCommand())
        {
            queryOnly.CommandText = "PRAGMA query_only = ON;";
            await queryOnly.ExecuteNonQueryAsync(cancellationToken);
        }

        if (!await TableExistsAsync(connection, cancellationToken))
        {
            if (requireCompleteSnapshot)
                throw new NotSupportedException(UiText.Get("当前 Kindle 批注数据库版本暂不支持。"));
            return [];
        }
        var columns = await ColumnsAsync(connection, cancellationToken);
        string[] required = requireCompleteSnapshot
            ? ["annotation_id", "book_id", "dataset", "serialized_payload", "start_position", "end_position", "created_time", "modified_time"]
            : ["annotation_id", "book_id", "dataset", "serialized_payload"];
        if (required.Any(column => !columns.Contains(column)))
            throw new NotSupportedException(UiText.Get("当前 Kindle 批注数据库版本暂不支持。"));
        if (requireCompleteSnapshot)
        {
            using var count = connection.CreateCommand();
            count.CommandText = $"SELECT COUNT(*) FROM {AnnotationTable} WHERE dataset IN (1, 3);";
            if (Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken)) > Math.Clamp(maxItems, 1, 100_000))
                throw new NotSupportedException(UiText.Get("Kindle 批注超过安全同步上限，不能只同步部分数据库。笔记仍可查看和导出。"));
        }

        var modifiedExpression = columns.Contains("modified_time") ? "modified_time" : "0";
        var orderExpression = columns.Contains("modified_time")
            ? "modified_time DESC, annotation_id"
            : "annotation_id";
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT annotation_id, book_id, dataset, serialized_payload,
                   {(columns.Contains("start_position") ? "start_position" : "''")},
                   {(columns.Contains("end_position") ? "end_position" : "''")},
                   {(columns.Contains("created_time") ? "created_time" : "0")},
                   {modifiedExpression}
            FROM {AnnotationTable}
            WHERE dataset IN (1, 3)
            ORDER BY {orderExpression}
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(maxItems, 1, 100_000));

        var records = new List<KindleAnnotationDatabaseRecord>();
        var keys = new HashSet<(string Id, string Book)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var record = ParseRecord(reader, requireCompleteSnapshot);
            if (requireCompleteSnapshot && (record is null || !keys.Add((record.Id, record.Book.BookId))))
                throw new NotSupportedException(UiText.Get("Kindle 批注数据库含有无法安全解析的记录，已停止同步。笔记仍可查看和导出。"));
            if (record is not null) records.Add(record);
        }
        return records;
    }

    public static async Task<IReadOnlyList<KindleClipping>> ReadClippingsAsync(
        string databasePath,
        Func<KindleAnnotationBookKey, string?>? resolveBookTitle = null,
        int maxItems = int.MaxValue,
        CancellationToken cancellationToken = default)
    {
        var records = await ReadRecordsAsync(databasePath, maxItems, cancellationToken);
        return records.Select(record =>
        {
            var bookTitle = resolveBookTitle?.Invoke(record.Book);
            if (string.IsNullOrWhiteSpace(bookTitle)) bookTitle = DisplayBookTitle(record.Book);
            var location = FormatLocation(record.Start, record.End);
            var content = record.Type == KindleClippingType.Note
                ? record.Note
                : UiText.Get("划线原文待定位");
            return new KindleClipping
            {
                Id = $"kindle-db:{record.Book.BookId}:{record.Id}",
                BookTitle = string.IsNullOrWhiteSpace(bookTitle) ? UiText.Get("未知书籍") : bookTitle,
                Author = string.Empty,
                Type = record.Type,
                Metadata = location,
                Content = content,
                RawBlock = string.Empty,
                AddedAt = record.CreatedAt ?? record.UpdatedAt,
                IsReadOnly = true,
                SourceBook = new(record.Book.BookId, record.Book.Asin, record.Book.Guid),
                StartPosition = record.Start,
                EndPosition = record.End
            };
        }).ToArray();
    }

    private static KindleAnnotationDatabaseRecord? ParseRecord(SqliteDataReader row, bool strict)
    {
        var id = row.GetString(0);
        var bookId = row.GetString(1);
        var dataset = row.GetInt32(2);
        var serializedPayload = row.GetString(3);
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(bookId)
            || string.IsNullOrWhiteSpace(serializedPayload)) return null;

        try
        {
            using var document = JsonDocument.Parse(serializedPayload);
            var payload = document.RootElement;
            if (payload.ValueKind != JsonValueKind.Object) return null;
            var type = ParseType(GetString(payload, "type"), dataset);
            if (type is not (KindleClippingType.Highlight or KindleClippingType.Note)) return null;
            if (strict && (dataset == 1 && type != KindleClippingType.Highlight
                || dataset == 3 && type != KindleClippingType.Note)) return null;

            var bookData = GetElement(payload, "book_data") ?? GetElement(payload, "bookData");
            var asin = bookData is { } asinElement ? GetString(asinElement, "asin") : null;
            var guid = bookData is { } guidElement ? GetString(guidElement, "guid") : null;
            if (string.IsNullOrWhiteSpace(guid))
            {
                var marker = bookId.IndexOf("-EBOK-", StringComparison.OrdinalIgnoreCase);
                if (marker >= 0)
                {
                    var guidStart = marker + "-EBOK-".Length;
                    var guidEnd = bookId.IndexOf(':', guidStart);
                    guid = bookId[guidStart..(guidEnd < 0 ? bookId.Length : guidEnd)];
                }
            }

            var start = ReadPosition(payload, "start_position");
            if (start is null) start = ReadPositionText(row.IsDBNull(4) ? null : row.GetString(4));
            var end = ReadPosition(payload, "end_position");
            if (end is null) end = ReadPositionText(row.IsDBNull(5) ? null : row.GetString(5));
            if (strict && (start is null || end is null || start < 0 || end < start)) return null;
            var startValue = Math.Max(0, start ?? 0);
            var endValue = Math.Max(startValue, end ?? startValue);
            var created = ReadUnixMilliseconds(payload, "created_time")
                ?? ReadUnixMilliseconds(row, 6);
            var updated = ReadUnixMilliseconds(payload, "last_modified")
                ?? ReadUnixMilliseconds(row, 7)
                ?? created;

            return new KindleAnnotationDatabaseRecord(
                id,
                new KindleAnnotationBookKey(bookId, EmptyToNull(asin), EmptyToNull(guid)),
                dataset,
                type,
                startValue,
                endValue,
                ReadNoteText(payload, strict),
                created,
                updated,
                serializedPayload);
        }
        catch (Exception exception) when (exception is JsonException
            or FormatException
            or OverflowException
            or InvalidCastException)
        {
            return null;
        }
    }

    private static string ReadNoteText(JsonElement payload, bool strict)
    {
        var metadata = GetElement(payload, "json_metadata") ?? GetElement(payload, "jsonMetadata");
        if (metadata is not { } value) return string.Empty;
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            try
            {
                using var nested = JsonDocument.Parse(text);
                return (GetString(nested.RootElement, "note_text")
                    ?? GetString(nested.RootElement, "noteText")
                    ?? string.Empty).Trim();
            }
            catch (JsonException) when (!strict)
            {
                return string.Empty;
            }
        }
        return value.ValueKind == JsonValueKind.Object
            ? (GetString(value, "note_text") ?? GetString(value, "noteText") ?? string.Empty).Trim()
            : string.Empty;
    }

    private static KindleClippingType ParseType(string? value, int dataset) =>
        value?.Trim().ToUpperInvariant() switch
        {
            "HIGHLIGHT" => KindleClippingType.Highlight,
            "NOTE" => KindleClippingType.Note,
            _ when string.IsNullOrWhiteSpace(value) => dataset switch
            {
                1 => KindleClippingType.Highlight,
                3 => KindleClippingType.Note,
                _ => KindleClippingType.Unknown
            },
            _ => KindleClippingType.Unknown
        };

    private static long? ReadPosition(JsonElement payload, string propertyName)
    {
        if (GetElement(payload, propertyName) is not { } position
            || position.ValueKind != JsonValueKind.Object) return null;
        return ReadPositionValue(GetElement(position, "longPosition"))
            ?? ReadPositionValue(GetElement(position, "shortPosition"));
    }

    private static long? ReadPositionText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            using var document = JsonDocument.Parse(value);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                ? ReadPositionValue(GetElement(root, "longPosition"))
                    ?? ReadPositionValue(GetElement(root, "shortPosition"))
                : ReadPositionValue(root);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static long? ReadPositionValue(JsonElement? value)
    {
        if (value is not { } element) return null;
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var numeric)) return numeric;
        if (element.ValueKind == JsonValueKind.String
            && long.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out numeric))
            return numeric;
        return null;
    }

    private static DateTimeOffset? ReadUnixMilliseconds(JsonElement payload, string propertyName) =>
        ReadUnixMilliseconds(GetElement(payload, propertyName));

    private static DateTimeOffset? ReadUnixMilliseconds(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : FromUnixMilliseconds(Convert.ToInt64(reader.GetValue(ordinal), CultureInfo.InvariantCulture));

    private static DateTimeOffset? ReadUnixMilliseconds(JsonElement? value)
    {
        if (value is not { } element) return null;
        long timestamp;
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var numeric))
            timestamp = numeric;
        else if (element.ValueKind != JsonValueKind.String
            || !long.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out timestamp))
            return null;
        return FromUnixMilliseconds(timestamp);
    }

    private static DateTimeOffset? FromUnixMilliseconds(long value)
    {
        try { return DateTimeOffset.FromUnixTimeMilliseconds(value); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static string FormatLocation(long start, long end)
    {
        if (start == 0 && end == 0) return string.Empty;
        return start == end
            ? UiText.Get("位置 {0}", start)
            : UiText.Get("位置 {0}–{1}", start, end);
    }

    private static string DisplayBookTitle(KindleAnnotationBookKey book)
    {
        var candidate = book.Guid ?? book.Asin;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            candidate = book.BookId;
            var marker = candidate.IndexOf("-EBOK-", StringComparison.OrdinalIgnoreCase);
            if (marker >= 0) candidate = candidate[..marker];
        }
        var display = Regex.Replace(candidate.Replace('_', ' '), @"\s+", " ").Trim();
        return string.IsNullOrWhiteSpace(display) ? UiText.Get("未知书籍") : display;
    }

    private static JsonElement? GetElement(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (element.TryGetProperty(propertyName, out var exact)) return exact;
        foreach (var property in element.EnumerateObject())
            if (property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
                return property.Value;
        return null;
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        var value = GetElement(element, propertyName);
        if (value is not { } actual) return null;
        return actual.ValueKind switch
        {
            JsonValueKind.String => actual.GetString(),
            JsonValueKind.Number => actual.GetRawText(),
            _ => null
        };
    }

    private static string? EmptyToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    internal static void RejectLinkedPath(string databasePath)
    {
        if ((File.GetAttributes(databasePath) & FileAttributes.ReparsePoint) != 0)
            throw new IOException(UiText.Get("设备笔记目录不能是链接或联接点。"));
        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (directory is not null
            && Directory.Exists(directory)
            && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException(UiText.Get("设备笔记目录不能是链接或联接点。"));
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name='{AnnotationTable}');";
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) != 0;
    }

    private static async Task<HashSet<string>> ColumnsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({AnnotationTable});";
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) columns.Add(reader.GetString(1));
        return columns;
    }
}

/// <summary>
/// Resolves Calibre-style SDR names such as "规模_02b1....sdr" back to a
/// human-readable book title when the account database only stores an ASIN.
/// </summary>
internal sealed class KindleAnnotationBookTitleResolver
{
    private static readonly Regex GuidSuffix = new(
        @"(?i)(?<key>[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})$",
        RegexOptions.CultureInvariant);
    private static readonly Regex HexSuffix = new(
        @"(?i)(?<key>[0-9a-f]{32})$",
        RegexOptions.CultureInvariant);
    private readonly Dictionary<string, string> _titles;

    private KindleAnnotationBookTitleResolver(Dictionary<string, string> titles) => _titles = titles;

    public static KindleAnnotationBookTitleResolver Create(IEnumerable<string> sdrFolderNames)
    {
        var titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folderName in sdrFolderNames)
        {
            if (string.IsNullOrWhiteSpace(folderName)) continue;
            var stem = folderName.Trim();
            if (stem.EndsWith(".sdr", StringComparison.OrdinalIgnoreCase))
                stem = stem[..^4];
            var suffix = GuidSuffix.Match(stem);
            if (!suffix.Success) suffix = HexSuffix.Match(stem);
            var title = stem;
            if (suffix.Success)
            {
                var key = suffix.Groups["key"].Value;
                var keyStart = suffix.Index;
                if (keyStart > 0 && stem[keyStart - 1] == '_') keyStart--;
                var candidateTitle = stem[..keyStart].TrimEnd('_', ' ', '-');
                if (!string.IsNullOrWhiteSpace(candidateTitle)) title = candidateTitle;
                titles[key] = title;
            }
            titles.TryAdd(stem, title);
        }
        return new KindleAnnotationBookTitleResolver(titles);
    }

    public string? Resolve(KindleAnnotationBookKey book)
    {
        foreach (var key in Candidates(book))
            if (_titles.TryGetValue(key, out var title)) return title;
        return null;
    }

    private static IEnumerable<string> Candidates(KindleAnnotationBookKey book)
    {
        if (!string.IsNullOrWhiteSpace(book.BookId)) yield return book.BookId;
        if (!string.IsNullOrWhiteSpace(book.Asin)) yield return book.Asin;
        if (!string.IsNullOrWhiteSpace(book.Guid))
        {
            var guid = book.Guid;
            yield return guid;
            var colon = guid.IndexOf(':');
            if (colon > 0) yield return guid[..colon];
        }
        if (book.BookId.Length >= 36
            && Guid.TryParse(book.BookId[..36], out var id))
            yield return id.ToString("D");
    }
}

internal static class KindleAnnotationDeviceFiles
{
    private const string DatabaseFileName = "ksdk_annotation_v1.db";

    public static IReadOnlyList<string> FindMountedDatabases(
        string deviceRoot,
        CancellationToken cancellationToken = default)
    {
        var annotationRoot = ReaderDevicePaths.ResolveDirectory(
            deviceRoot,
            Path.Combine("system", "ksdk", ".annotations"));
        if (!Directory.Exists(annotationRoot)) return [];

        var databases = Directory.EnumerateFiles(annotationRoot, DatabaseFileName, new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            MatchCasing = MatchCasing.CaseInsensitive
        })
            .Take(32)
            .Where(path => Path.GetFileName(path).Equals(DatabaseFileName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => IsAccountDatabase(path) ? 0 : 1)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var accounts = databases.Where(IsAccountDatabase).ToArray();
        return accounts.Length > 0 ? accounts : databases;
    }

    public static IReadOnlyList<string> FindSdrFolderNames(
        string documentsRoot,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(documentsRoot)) return [];
        var names = new List<string>();
        foreach (var path in Directory.EnumerateDirectories(documentsRoot, "*.sdr", new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            MatchCasing = MatchCasing.CaseInsensitive
        }))
        {
            cancellationToken.ThrowIfCancellationRequested();
            names.Add(Path.GetFileName(path));
        }
        return names;
    }

    private static bool IsAccountDatabase(string path) =>
        path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => segment.StartsWith("amzn1.account.", StringComparison.OrdinalIgnoreCase));
}
