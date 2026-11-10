using System.Security.Cryptography;
using Kkindle.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Kkindle.Tests;

public sealed partial class KindleAnnotationDatabaseReaderTests
{
    [Fact]
    public async Task CompleteSnapshotRejectsTruncationWhileBrowsingCanLimitResults()
    {
        var root = TestHelpers.CreateTempDirectory();
        var database = Path.Combine(root, "annotations.db");
        try
        {
            await CreateDatabaseAsync(database, ("one", 3, "{\"type\":\"NOTE\"}", 1L), ("two", 3, "{\"type\":\"NOTE\"}", 2L));
            var before = await File.ReadAllBytesAsync(database);
            Assert.Single(await KindleAnnotationDatabaseReader.ReadRecordsAsync(database, maxItems: 1));
            await Assert.ThrowsAsync<NotSupportedException>(() =>
                KindleAnnotationDatabaseReader.ReadRecordsAsync(database, maxItems: 1, requireCompleteSnapshot: true));
            Assert.Equal(before, await File.ReadAllBytesAsync(database));
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{\"type\":\"HANDWRITING\"}")]
    [InlineData("{\"type\":\"NOTE\",\"json_metadata\":\"invalid-json\"}")]
    [InlineData("{\"type\":\"HIGHLIGHT\"}")]
    public async Task CompleteSnapshotRejectsMalformedOrUnsupportedRecords(string payload)
    {
        var root = TestHelpers.CreateTempDirectory();
        var database = Path.Combine(root, "annotations.db");
        try
        {
            await CreateDatabaseAsync(database, ("bad", 3, payload, 1L));
            var before = SHA256.HashData(await File.ReadAllBytesAsync(database));
            await Assert.ThrowsAsync<NotSupportedException>(() =>
                KindleAnnotationDatabaseReader.ReadRecordsAsync(database, requireCompleteSnapshot: true));
            Assert.Equal(before, SHA256.HashData(await File.ReadAllBytesAsync(database)));
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task CompleteSnapshotRejectsUnknownPositionsAndDuplicateKeys()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var noPositions = Path.Combine(root, "no-positions.db");
            await CreateDatabaseAsync(noPositions, ("one", 3, "{\"type\":\"NOTE\"}", 1L),
                startPosition: "{}", endPosition: "{}");
            await Assert.ThrowsAsync<NotSupportedException>(() =>
                KindleAnnotationDatabaseReader.ReadRecordsAsync(noPositions, requireCompleteSnapshot: true));
            var duplicates = Path.Combine(root, "duplicates.db");
            await CreateDatabaseAsync(duplicates, ("one", 3, "{\"type\":\"NOTE\"}", 1L), ("one", 3, "{\"type\":\"NOTE\"}", 2L));
            await Assert.ThrowsAsync<NotSupportedException>(() =>
                KindleAnnotationDatabaseReader.ReadRecordsAsync(duplicates, requireCompleteSnapshot: true));
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task CompleteSnapshotDoesNotTreatUnknownSchemaAsAnEmptyDatabase()
    {
        var root = TestHelpers.CreateTempDirectory();
        var database = Path.Combine(root, "annotations.db");
        try
        {
            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString()))
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE future_annotations(id TEXT);";
                await command.ExecuteNonQueryAsync();
            }
            Assert.Empty(await KindleAnnotationDatabaseReader.ReadClippingsAsync(database));
            await Assert.ThrowsAsync<NotSupportedException>(() =>
                KindleAnnotationDatabaseReader.ReadRecordsAsync(database, requireCompleteSnapshot: true));
        }
        finally { TestHelpers.TryDelete(root); }
    }
}
