using System.Security.Cryptography;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Kkindle.Tests;

public sealed class KoboNotesReaderTests
{
    [Fact]
    public async Task ReadsHighlightsWithNotesAndExportsDeviceSourceWithoutChangingDatabase()
    {
        var root = Path.Combine(Path.GetTempPath(), "KkindleDeviceTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".kobo"));
        var database = Path.Combine(root, ".kobo", "KoboReader.sqlite");
        try
        {
            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString()))
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE content (ContentID TEXT, BookID TEXT, Title TEXT, Attribution TEXT);
                    INSERT INTO content VALUES ('file:///mnt/onboard/book.epub', NULL, '城南旧事', '林海音');
                    CREATE TABLE Bookmark (BookmarkID TEXT, VolumeID TEXT, Text TEXT, Annotation TEXT, DateCreated TEXT, StartContainerPath TEXT, StartOffset INTEGER);
                    INSERT INTO Bookmark VALUES ('one', 'file:///mnt/onboard/book.epub', '摘录正文', '我的批注', '2026-09-12T02:00:00Z', 'chapter-1.xhtml', 12);
                    INSERT INTO Bookmark VALUES ('two', 'file:///mnt/onboard/book.epub', NULL, '独立笔记', '2026-09-11T02:00:00Z', 'chapter-2.xhtml', 8);
                    """;
                await command.ExecuteNonQueryAsync();
            }
            var before = SHA256.HashData(await File.ReadAllBytesAsync(database));
            var notes = await KoboNotesReader.ReadAsync(database);
            Assert.Equal(2, notes.Count);
            var first = notes[0];
            Assert.Equal("城南旧事", first.BookTitle);
            Assert.Equal("林海音", first.Author);
            Assert.Equal("摘录正文", first.Content);
            Assert.Equal("我的批注", first.PairedNote!.Content);
            Assert.Equal(KindleClippingType.Note, notes[1].Type);
            Assert.Single(await KoboNotesReader.ReadAsync(database, 1));
            Assert.Equal(before, SHA256.HashData(await File.ReadAllBytesAsync(database)));
            var record = new ReadingMaterialRecord(ReadingMaterialSource.Device, first.BookTitle, "划线与笔记",
                first.Metadata, first.Content, first.PairedNote.Content, first.AddedAt, "KOBO-123", "Kobo Libra 2");
            var markdown = ReadingMaterialsExport.BuildMarkdown([record]);
            Assert.Contains("Kobo Libra 2", markdown);
            Assert.DoesNotContain("Kindle ·", markdown);
            await using var stream = new MemoryStream();
            await ReadingMaterialsExport.WriteMarkdownAsync(stream, [record]);
            Assert.Contains("Kobo Libra 2", System.Text.Encoding.UTF8.GetString(stream.ToArray()));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task MissingOptionalColumnsRemainReadable()
    {
        var root = Path.Combine(Path.GetTempPath(), "KkindleDeviceTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var database = Path.Combine(root, "KoboReader.sqlite");
        try
        {
            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString()))
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE Bookmark (BookmarkID TEXT, VolumeID TEXT, Text TEXT); INSERT INTO Bookmark VALUES ('id', 'file:///mnt/onboard/book.epub', 'quote');";
                await command.ExecuteNonQueryAsync();
            }
            var note = Assert.Single(await KoboNotesReader.ReadAsync(database));
            Assert.Equal("book", note.BookTitle);
            Assert.Equal("quote", note.Content);
            Assert.Null(note.AddedAt);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
