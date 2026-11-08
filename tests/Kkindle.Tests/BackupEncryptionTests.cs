using System.IO.Compression;
using System.Security.Cryptography;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle.Tests;

public sealed partial class BackupTests
{
    internal const string BackupTestKey = "fictional-backup-test-encryption-key";

    internal static async Task SetBackupKeyAsync(AppPaths paths, string key = BackupTestKey)
    {
        var store = new S3SyncSettingsStore(paths, new TestHelpers.PlaintextSecretProtector());
        var current = await store.LoadAsync();
        await store.SaveAsync(current.DeviceId, current.Settings with { EncryptionKey = key });
    }

    internal static async Task<ZipArchive> OpenBackupArchiveAsync(string path, ZipArchiveMode mode = ZipArchiveMode.Read, string key = BackupTestKey)
    {
        var clear = new MemoryStream();
        await using (var input = File.OpenRead(path)) await BackupEncryption.DecryptAsync(input, clear, key, CancellationToken.None);
        clear.Position = 0;
        return new FixtureArchive(clear, mode, path, key);
    }

    private sealed class FixtureArchive : ZipArchive
    {
        private readonly MemoryStream clear;
        private readonly ZipArchiveMode mode;
        private readonly string path;
        private readonly string key;
        private bool _disposed;
        public FixtureArchive(MemoryStream clear, ZipArchiveMode mode, string path, string key) : base(clear, mode, leaveOpen: true)
        { this.clear = clear; this.mode = mode; this.path = path; this.key = key; }
        protected override void Dispose(bool disposing)
        {
            if (_disposed) return;
            base.Dispose(disposing);
            if (!disposing) return;
            _disposed = true;
            try
            {
                if (mode == ZipArchiveMode.Update)
                {
                    clear.Position = 0;
                    using var output = File.Create(path);
                    using var encrypted = BackupEncryption.CreateWriter(output, key);
                    clear.CopyTo(encrypted);
                }
            }
            finally { clear.Dispose(); }
        }
    }

    [Fact]
    public async Task BackupRequiresSyncKeyBeforeTouchingImportOrExport()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(Path.Combine(root, "app"));
            await new SqliteBookLibraryService(paths, new BookMetadataService()).InitializeAsync();
            var backup = new AppBackupService(paths, new TestHelpers.PlaintextSecretProtector());
            var destination = Path.Combine(root, "retained.kkindle");
            await File.WriteAllTextAsync(destination, "previous backup fixture");
            var export = await Assert.ThrowsAsync<SyncEncryptionKeyRequiredException>(() => backup.ExportAsync(destination));
            Assert.Contains("请先", export.Message); Assert.Contains("同步密钥", export.Message);
            Assert.Equal("previous backup fixture", await File.ReadAllTextAsync(destination));
            var import = await Assert.ThrowsAsync<SyncEncryptionKeyRequiredException>(() => backup.ImportAsync(Path.Combine(root, "missing.kkindle")));
            Assert.Contains("不会上传", import.Message);
            Assert.Empty(Directory.GetDirectories(paths.Data, ".kkindle-import-*"));
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task ExportEncryptsEntirePackageAndRandomizesEachBackup()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(Path.Combine(root, "app")); await SetBackupKeyAsync(paths);
            var library = new SqliteBookLibraryService(paths, new BookMetadataService()); await library.InitializeAsync();
            var book = Path.Combine(root, "source.epub"); CreateEpub(book); await library.ImportAsync([book]);
            var service = new AppBackupService(paths, new TestHelpers.PlaintextSecretProtector());
            var a = Path.Combine(root, "a.kkindle"); var b = Path.Combine(root, "b.kkindle");
            await service.ExportAsync(a); await service.ExportAsync(b);
            var bytes = await File.ReadAllBytesAsync(a);
            Assert.True(bytes.AsSpan().StartsWith("KKINDLE-BACKUP2"u8));
            Assert.NotEqual(bytes, await File.ReadAllBytesAsync(b));
            var text = System.Text.Encoding.UTF8.GetString(bytes);
            Assert.DoesNotContain(BackupTestKey, text); Assert.DoesNotContain("database/kkindle.db", text); Assert.DoesNotContain("测试书", text);
            using var archive = await OpenBackupArchiveAsync(a);
            using var reader = new StreamReader(archive.GetEntry("settings/settings.json")!.Open());
            Assert.DoesNotContain(BackupTestKey, await reader.ReadToEndAsync());
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Theory]
    [InlineData("wrong-key")]
    [InlineData("tampered")]
    [InlineData("truncated")]
    [InlineData("appended")]
    public async Task InvalidEncryptedBackupNeverReplacesExistingLibrary(string failure)
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var (paths, library, previous, archive, _) = await PrepareImportFailureAsync(root);
            var bytes = await File.ReadAllBytesAsync(archive);
            switch (failure)
            {
                case "wrong-key": await SetBackupKeyAsync(paths, "fictional-incorrect-backup-key"); break;
                case "tampered": bytes[bytes.Length / 2] ^= 1; break;
                case "truncated": bytes = bytes[..^32]; break;
                case "appended": bytes = [.. bytes, 1]; break;
            }
            await File.WriteAllBytesAsync(archive, bytes);
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => new AppBackupService(paths, new TestHelpers.PlaintextSecretProtector()).ImportAsync(archive));
            Assert.DoesNotContain(BackupTestKey, error.Message);
            Assert.Equal(previous.Id, Assert.Single(await library.SearchAsync()).Id);
            Assert.True(File.Exists(library.GetAbsoluteFilePath(previous.Files[0])));
            Assert.Empty(Directory.GetDirectories(paths.Data, ".kkindle-import-*"));
            Assert.Empty(Directory.GetDirectories(paths.Data, ".kkindle-rollback-*"));
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task LargeEncryptedBookRoundTripsAcrossChunks()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var protector = new TestHelpers.PlaintextSecretProtector();
            var source = new AppPaths(Path.Combine(root, "source")); await SetBackupKeyAsync(source);
            var sourceLibrary = new SqliteBookLibraryService(source, new BookMetadataService()); await sourceLibrary.InitializeAsync();
            var epub = Path.Combine(root, "large.epub"); CreateEpub(epub);
            using (var archive = ZipFile.Open(epub, ZipArchiveMode.Update))
            using (var entry = archive.CreateEntry("OEBPS/padding.bin", CompressionLevel.NoCompression).Open())
                await entry.WriteAsync(RandomNumberGenerator.GetBytes(3 * 1024 * 1024 + 17));
            await sourceLibrary.ImportAsync([epub]);
            var path = Path.Combine(root, "large.kkindle"); await new AppBackupService(source, protector).ExportAsync(path);
            var target = new AppPaths(Path.Combine(root, "target")); await SetBackupKeyAsync(target);
            var targetLibrary = new SqliteBookLibraryService(target, new BookMetadataService()); await targetLibrary.InitializeAsync();
            await new AppBackupService(target, protector).ImportAsync(path); await targetLibrary.InitializeAsync();
            var restored = Assert.Single(await targetLibrary.SearchAsync());
            Assert.Equal(await File.ReadAllBytesAsync(epub), await File.ReadAllBytesAsync(targetLibrary.GetAbsoluteFilePath(restored.Files[0])));
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task ReorderedEncryptedChunksAreRejected()
    {
        var value = RandomNumberGenerator.GetBytes(2 * 1024 * 1024 + 100);
        using var encoded = new MemoryStream();
        await using (var writer = BackupEncryption.CreateWriter(encoded, BackupTestKey)) await writer.WriteAsync(value);
        var bytes = encoded.ToArray();
        var headerLength = "KKINDLE-BACKUP2"u8.Length + 16 + 4;
        var recordLength = 4 + 12 + 16 + 1024 * 1024;
        var first = bytes.AsSpan(headerLength, recordLength).ToArray();
        bytes.AsSpan(headerLength + recordLength, recordLength).CopyTo(bytes.AsSpan(headerLength));
        first.CopyTo(bytes, headerLength + recordLength);
        using var input = new MemoryStream(bytes); using var plain = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() => BackupEncryption.DecryptAsync(input, plain, BackupTestKey, CancellationToken.None));
        Assert.Equal(0, plain.Length);
    }

    [Fact]
    public async Task PlaintextLegacyBackupsAreRejectedEvenWhenKeyConfigured()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(Path.Combine(root, "app")); await SetBackupKeyAsync(paths);
            var library = new SqliteBookLibraryService(paths, new BookMetadataService()); await library.InitializeAsync();
            var path = Path.Combine(root, "plain.kkindle");
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create)) TestHelpers.AddZipEntry(archive, "manifest.json", "{\"format\":\"KkindleBackup\",\"version\":1}");
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => new AppBackupService(paths, new TestHelpers.PlaintextSecretProtector()).ImportAsync(path));
            Assert.Contains("未加密", error.Message);
            Assert.Empty(Directory.GetDirectories(paths.Data, ".kkindle-import-*"));
        }
        finally { TestHelpers.TryDelete(root); }
    }
}
