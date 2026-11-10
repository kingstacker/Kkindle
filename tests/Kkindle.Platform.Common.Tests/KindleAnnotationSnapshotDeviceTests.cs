using System.Security.Cryptography;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Kkindle.Platform.Common;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kkindle.Platform.Common.Tests;

public sealed partial class KindleAnnotationDatabaseDeviceTests
{
    [Fact]
    public async Task MountedSnapshotRejectsMultipleAccountsAndPreservesBrowsing()
    {
        var root = CreateTempDirectory();
        try
        {
            var kindle = Path.Combine(root, "Kindle");
            var databases = new List<string>();
            foreach (var account in new[] { "ONE", "TWO" })
            {
                var database = Path.Combine(kindle, "system", "ksdk", ".annotations", "amzn1.account." + account, "ksdk_annotation_v1.db");
                Directory.CreateDirectory(Path.GetDirectoryName(database)!);
                await CreateDatabaseAsync(database, "{\"type\":\"NOTE\",\"json_metadata\":{\"note_text\":\"测试批注\"}}", "note-" + account);
                databases.Add(database);
            }
            var before = databases.Select(File.ReadAllBytes).ToArray();
            var (service, device) = MountedSnapshotService(root, kindle);
            await Assert.ThrowsAsync<NotSupportedException>(() => service.ReadAnnotationSnapshotAsync(device));
            Assert.Equal(2, (await service.ReadClippingsAsync(device)).Count);
            for (var index = 0; index < databases.Count; index++)
                Assert.Equal(before[index], await File.ReadAllBytesAsync(databases[index]));
        }
        finally { TryDelete(root); }
    }

    [Theory]
    [InlineData("-wal")]
    [InlineData("-shm")]
    [InlineData("-journal")]
    [InlineData("wal-header")]
    public async Task MountedSnapshotRejectsActiveJournalBeforeAnalysis(string mode)
    {
        var root = CreateTempDirectory();
        try
        {
            var kindle = Path.Combine(root, "Kindle");
            var database = Path.Combine(kindle, "system", "ksdk", ".annotations", "amzn1.account.TEST", "ksdk_annotation_v1.db");
            Directory.CreateDirectory(Path.GetDirectoryName(database)!);
            await CreateDatabaseAsync(database, "{\"type\":\"NOTE\"}");
            if (mode == "wal-header")
            {
                await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString());
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA journal_mode=WAL;";
                await command.ExecuteScalarAsync();
            }
            else await File.WriteAllBytesAsync(database + mode, []);
            var before = await File.ReadAllBytesAsync(database);
            var (service, device) = MountedSnapshotService(root, kindle);
            await Assert.ThrowsAsync<IOException>(() => service.ReadAnnotationSnapshotAsync(device));
            Assert.Equal(before, await File.ReadAllBytesAsync(database));
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task MountedSnapshotUsesMatchingRecordsAndHashAndDoesNotWriteToSource()
    {
        var root = CreateTempDirectory();
        try
        {
            var kindle = Path.Combine(root, "Kindle");
            var database = Path.Combine(kindle, "system", "ksdk", ".annotations", "amzn1.account.TEST", "ksdk_annotation_v1.db");
            Directory.CreateDirectory(Path.GetDirectoryName(database)!);
            await CreateDatabaseAsync(database, "{\"type\":\"NOTE\",\"json_metadata\":{\"note_text\":\"快照测试批注\"}}");
            var before = await File.ReadAllBytesAsync(database);
            var (service, device) = MountedSnapshotService(root, kindle);
            var snapshot = await service.ReadAnnotationSnapshotAsync(device);
            Assert.NotNull(snapshot);
            Assert.Equal("快照测试批注", Assert.Single(snapshot.Records).NoteText);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(before)).ToLowerInvariant(), snapshot.Sha256);
            Assert.Equal(before, await File.ReadAllBytesAsync(database));
            Assert.Equal(Path.GetRelativePath(kindle, database).Replace('\\', '/'), snapshot.DatabaseRelativePath);
        }
        finally { TryDelete(root); }
    }

    private static (MassStorageKindleDeviceService Service, KindleDevice Device) MountedSnapshotService(string root, string kindle) =>
        (new MassStorageKindleDeviceService(new AppPaths(Path.Combine(root, "app")), new FakeMetadataService(), [root]),
            new KindleDevice { RootPath = kindle, VolumeSerial = "KINDLE-SNAPSHOT-TEST", Transport = KindleTransport.MassStorage });
}
