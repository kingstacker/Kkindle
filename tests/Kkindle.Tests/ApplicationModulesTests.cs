using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle.Tests;

public sealed class ApplicationModulesTests
{
    [Fact]
    public void ApplicationLayerHasNoUiOrDesktopPlatformReference()
    {
        var assembly = typeof(ApplicationModules).Assembly;
        Assert.Same(assembly, typeof(TtsService).Assembly);
        Assert.Same(assembly, typeof(ReaderTtsDocument).Assembly);
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference =>
            reference.Name!.StartsWith("Avalonia", StringComparison.Ordinal)
            || reference.Name.StartsWith("Kkindle.App", StringComparison.Ordinal)
            || reference.Name.StartsWith("Kkindle.Platform", StringComparison.Ordinal));
    }

    [Fact]
    public void CompositionPreservesInjectedServicesAndDoesNotStartWork()
    {
        var paths = NewPaths();
        var library = new SqliteBookLibraryService(paths, new BookMetadataService());
        var converter = new TestConverter();
        var protector = new TestProtector();
        using var modules = new ApplicationModules(new ApplicationModuleOptions
        {
            Paths = paths, SecretProtector = protector,
            Library = library, FormatConverter = converter, MetadataService = new BookMetadataService(),
            TtsCacheDirectory = Path.Combine(paths.Root, "audio"),
            StartupSettings = new AppSettings { UiLanguage = "en-US", OnboardingCompleted = true }
        });

        Assert.Same(paths, modules.Paths);
        Assert.Same(protector, modules.SecretProtector);
        Assert.Same(library, modules.Library.Books);
        Assert.Same(converter, modules.Library.FormatConverter);
        Assert.Equal(Path.Combine(paths.Root, "audio"), modules.Reader.TtsCache.CacheRoot);
        Assert.Equal("en-US", modules.StartupSettings.UiLanguage);
        Assert.True(modules.HasStartupSettings);
        Assert.Null(modules.Integrations.Updates);
        Assert.False(Directory.Exists(paths.Root));
    }

    [Fact]
    public void ModuleGraphsAreNotGlobalSingletonsAndDisposeIdempotently()
    {
        using var first = CreateModules(NewPaths());
        using var second = CreateModules(NewPaths());
        Assert.NotSame(first.Library.Books, second.Library.Books);
        Assert.NotSame(first.Reader.Data, second.Reader.Data);
        Assert.NotSame(first.Ai.ChatClient, second.Ai.ChatClient);
        Assert.NotSame(first.Ai.Embedding, second.Ai.Embedding);
        Assert.NotSame(first.Sync.Service, second.Sync.Service);
        Assert.False(first.HasStartupSettings);
        first.Dispose();
        first.Dispose();
        Assert.True(first.IsDisposed);
        Assert.False(second.IsDisposed);
    }

    [Fact]
    public async Task ModulesUseTheInjectedStorageAndSecretProtection()
    {
        var paths = NewPaths();
        var protector = new TestProtector();
        using var modules = CreateModules(paths, protector);
        try
        {
            await modules.Reader.Data.InitializeAsync();
            await modules.Ai.Settings.SaveAsync(new AiConnectionSettings { ApiKey = "module-test-secret" });
            var loaded = await modules.Ai.Settings.LoadAsync();
            Assert.Equal("module-test-secret", loaded.ApiKey);
            Assert.Equal(1, protector.ProtectCalls);
            Assert.Equal(1, protector.UnprotectCalls);
            Assert.True(File.Exists(paths.Database));
            Assert.DoesNotContain("module-test-secret",
                await File.ReadAllTextAsync(Path.Combine(paths.Data, "ai-settings.json")));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(paths.Root)) Directory.Delete(paths.Root, recursive: true);
        }
    }

    [Fact]
    public void CompositionRequiresAnExplicitSecretProtector()
    {
        Assert.Throws<ArgumentNullException>(() => new ApplicationModules(new ApplicationModuleOptions
        {
            Paths = NewPaths(), SecretProtector = null!, FormatConverter = new TestConverter(), MetadataService = new BookMetadataService()
        }));
    }

    [Fact]
    public async Task PortableCompositionRequiresAnExplicitConverter()
    {
        Assert.Throws<ArgumentNullException>(() => new ApplicationModules(new ApplicationModuleOptions
        {
            Paths = NewPaths(), SecretProtector = new TestProtector(), FormatConverter = null!, MetadataService = new BookMetadataService()
        }));
        var converter = new UnavailableBookFormatConverter();
        using var modules = new ApplicationModules(new ApplicationModuleOptions
        {
            Paths = NewPaths(), SecretProtector = new TestProtector(), FormatConverter = converter, MetadataService = new BookMetadataService()
        });
        Assert.Same(converter, modules.Library.FormatConverter);
        await Assert.ThrowsAsync<NotSupportedException>(() => converter.ConvertAsync("a.epub", "b.pdf"));
    }

    [Fact]
    public async Task CompositionUsesTheInjectedSyncServiceWithoutStartingIt()
    {
        var sync = new TestSyncService();
        using var modules = new ApplicationModules(new ApplicationModuleOptions
        {
            Paths = NewPaths(), SecretProtector = new TestProtector(), FormatConverter = new TestConverter(),
            MetadataService = new BookMetadataService(), SyncService = sync
        });
        Assert.Same(sync, modules.Sync.Service);
        Assert.Equal(0, sync.SyncCount);
        await modules.Sync.Service.SyncAsync("device", new S3SyncSettings());
        Assert.Equal(1, sync.SyncCount);
    }

    private static AppPaths NewPaths() => new(Path.Combine(Path.GetTempPath(),
        "kkindle-module-tests", Guid.NewGuid().ToString("N")));

    private static ApplicationModules CreateModules(AppPaths paths, TestProtector? protector = null)
        => new(new ApplicationModuleOptions { Paths = paths, SecretProtector = protector ?? new TestProtector(), FormatConverter = new TestConverter(), MetadataService = new BookMetadataService() });

    private sealed class TestProtector : ISecretProtector
    {
        public int ProtectCalls { get; private set; }
        public int UnprotectCalls { get; private set; }
        public byte[] Protect(byte[] value) { ProtectCalls++; return Transform(value); }
        public byte[] Unprotect(byte[] value) { UnprotectCalls++; return Transform(value); }
        private static byte[] Transform(byte[] value) => value.Select(item => (byte)(item ^ 0x5A)).ToArray();
    }

    private sealed class TestConverter : IBookFormatConverter
    {
        public Task ConvertAsync(string sourcePath, string destinationPath,
            IProgress<FormatConversionProgress>? progress = null,
            CancellationToken cancellationToken = default, FormatConversionMetadata? metadata = null)
            => throw new InvalidOperationException("Composition must not invoke format conversion.");
    }

    private sealed class TestSyncService : ISyncService
    {
        public int SyncCount { get; private set; }
        public event EventHandler? RemoteSettingsApplied { add { } remove { } }
        public Task<S3SyncStoredSettings> LoadSettingsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new S3SyncStoredSettings("device", new S3SyncSettings()));
        public Task SaveSettingsAsync(string deviceId, S3SyncSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ResetLocalBaselineAsync(string deviceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task TestConnectionAsync(S3SyncSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<S3SyncStatusSummary> GetStatusAsync(string deviceId, S3SyncSettings settings, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<string> EnsureBookFileDownloadedAsync(string deviceId, S3SyncSettings settings, BookFile file, IProgress<string>? progress = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyDictionary<Guid, BookSyncStatus>> GetBookSyncStatusesAsync(string deviceId, S3SyncSettings settings, IReadOnlyCollection<Book> books, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<S3SyncResult> SyncAsync(string deviceId, S3SyncSettings settings, IProgress<string>? progress = null, CancellationToken cancellationToken = default, S3SyncOptions? options = null)
        { SyncCount++; return Task.FromResult(new S3SyncResult(1, 0, 0, 0, 0, false)); }
        public Task InitializeDeletionTrackingAsync(CancellationToken cancellationToken = default, string? deviceId = null) => Task.CompletedTask;
    }
}
