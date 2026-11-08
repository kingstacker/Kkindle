using Kkindle.Core;
using Kkindle.Infrastructure;
using Kkindle.Infrastructure.Plugins;

namespace Kkindle.ApplicationServices;

/// <summary>
/// A per-shell composition root. It owns the modules' disposable services;
/// the library and platform adapters remain caller-owned. It neither creates controls nor starts background work.
/// </summary>
public sealed class ApplicationModules : IDisposable
{
    private int _disposed;

    public ApplicationModules(ApplicationModuleOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Paths);
        ArgumentNullException.ThrowIfNull(options.SecretProtector);
        ArgumentNullException.ThrowIfNull(options.FormatConverter);
        ArgumentNullException.ThrowIfNull(options.MetadataService);
        if (!string.IsNullOrWhiteSpace(options.TtsCacheDirectory))
            _ = Path.GetFullPath(options.TtsCacheDirectory);
        DataRegion = AccountRegionPaths.ReadActiveRegion(options.Paths);
        Paths = AccountRegionPaths.ForRegion(options.Paths, DataRegion);
        if (DataRegion != AccountRegion.China && (options.Library is not null || options.SyncService is not null))
            throw new ArgumentException("切换区域后不能复用其他区域的书库或同步服务。", nameof(options));
        SecretProtector = options.SecretProtector;
        HasStartupSettings = options.StartupSettings is not null;
        StartupSettings = AppSettings.Normalize(options.StartupSettings);
        Accounts = new AccountModule(options, DataRegion);
        var regionalOptions = options with { Paths = Paths };
        Library = new LibraryModule(regionalOptions);
        Reader = new ReaderModule(regionalOptions, Library);
        Ai = new AiModule(regionalOptions, StartupSettings, Reader);
        Sync = new SyncModule(regionalOptions, Accounts.Service, DataRegion);
        Integrations = new IntegrationModule(regionalOptions);
        Plugins = new PluginManager(Paths);
    }

    public AppPaths Paths { get; }
    public AccountRegion DataRegion { get; }
    public ISecretProtector SecretProtector { get; }
    public AppSettings StartupSettings { get; }
    public bool HasStartupSettings { get; }
    public LibraryModule Library { get; }
    public ReaderModule Reader { get; }
    public AiModule Ai { get; }
    public SyncModule Sync { get; }
    public IntegrationModule Integrations { get; }
    public AccountModule Accounts { get; }
    public PluginManager Plugins { get; }
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        List<Exception>? errors = null;
        // Cancel/await shell work before disposing the shared clients it uses.
        IDisposable?[] resources =
        [
            Integrations.Updates,
            Accounts.Service,
            Ai.EpubTranslation, Ai.Translation, Ai.ChatClient, Ai.WebSearch,
            Ai.EmbeddingModels, Ai.PinyinModels, Ai.Embedding
        ];
        foreach (var resource in resources)
        {
            try { resource?.Dispose(); }
            catch (Exception exception) { (errors ??= []).Add(exception); }
        }
        if (errors is not null) throw new AggregateException(errors);
    }
}
