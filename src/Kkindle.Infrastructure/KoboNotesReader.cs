using System.Globalization;
using Kkindle.Core;
using Microsoft.Data.Sqlite;

namespace Kkindle.Infrastructure;

/// <summary>Reads Kobo bookmarks without modifying the device's database or journal.</summary>
public static class KoboNotesReader
{
    public static async Task<IReadOnlyList<KindleClipping>> ReadAsync(
        string databasePath, int maxItems = int.MaxValue, CancellationToken cancellationToken = default)
    {
        if (maxItems <= 0 || !File.Exists(databasePath)) return [];
        if ((File.GetAttributes(databasePath) & FileAttributes.ReparsePoint) != 0
            || (File.GetAttributes(Path.GetDirectoryName(databasePath)!) & FileAttributes.ReparsePoint) != 0)
            throw new IOException(UiText.Get("设备笔记目录不能是链接或联接点。"));
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Private, Pooling = false
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        var columns = await ColumnsAsync(connection, "Bookmark", cancellationToken);
        if (!columns.Contains("BookmarkID") || !columns.Contains("VolumeID"))
            throw new NotSupportedException(UiText.Get("当前设备的笔记格式暂不支持。"));

        var titles = await ReadTitlesAsync(connection, cancellationToken);
        var fields = new[] { "BookmarkID", "VolumeID", "Text", "Annotation", "DateCreated", "StartContainerPath", "StartOffset" };
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT " + string.Join(", ", fields.Select(field => columns.Contains(field)
            ? $"COALESCE(\"{field}\", '')" : "''")) + " FROM Bookmark"
            + (columns.Contains("DateCreated") ? " ORDER BY DateCreated DESC" : " ORDER BY BookmarkID")
            + " LIMIT $limit";
        command.Parameters.AddWithValue("$limit", maxItems);
        var result = new List<KindleClipping>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            string Text(int ordinal) => Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture) ?? "";
            var id = Text(0);
            var volumeId = Text(1);
            var quote = Text(2);
            var note = Text(3);
            var added = DateTimeOffset.TryParse(Text(4), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var timestamp) ? timestamp : (DateTimeOffset?)null;
            var title = titles.GetValueOrDefault(volumeId);
            var bookTitle = title.Title ?? Path.GetFileNameWithoutExtension(Uri.UnescapeDataString(volumeId));
            var location = string.Join(" · ", new[] { Text(5), Text(6) }.Where(value => value.Length > 0));
            var clipping = new KindleClipping
            {
                Id = "kobo:" + id, BookTitle = string.IsNullOrWhiteSpace(bookTitle) ? UiText.Get("未知书籍") : bookTitle,
                Author = title.Author ?? "", Metadata = location, AddedAt = added,
                Content = quote.Length > 0 ? quote : note,
                Type = quote.Length > 0 ? KindleClippingType.Highlight
                    : note.Length > 0 ? KindleClippingType.Note : KindleClippingType.Bookmark
            };
            if (quote.Length > 0 && note.Length > 0)
                clipping.PairedNote = new KindleClipping
                {
                    Id = "kobo-note:" + id, BookTitle = clipping.BookTitle, Author = clipping.Author,
                    Metadata = location, AddedAt = added, Type = KindleClippingType.Note, Content = note
                };
            result.Add(clipping);
        }
        return result;
    }

    private static async Task<HashSet<string>> ColumnsAsync(SqliteConnection connection, string table, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table}\")";
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) columns.Add(reader.GetString(1));
        return columns;
    }

    private static async Task<Dictionary<string, (string? Title, string? Author)>> ReadTitlesAsync(
        SqliteConnection connection, CancellationToken cancellationToken)
    {
        var titles = new Dictionary<string, (string?, string?)>(StringComparer.Ordinal);
        var columns = await ColumnsAsync(connection, "content", cancellationToken);
        if (!columns.Contains("ContentID") || !columns.Contains("Title")) return titles;
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT ContentID, Title, " + (columns.Contains("Attribution") ? "Attribution" : "NULL")
            + " FROM content" + (columns.Contains("BookID") ? " WHERE BookID IS NULL" : "");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (reader.IsDBNull(0)) continue;
            titles[reader.GetString(0)] = (reader.IsDBNull(1) ? null : reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2));
        }
        return titles;
    }
}
