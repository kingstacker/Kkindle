using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle.ApplicationServices;

/// <summary>Platform-neutral inputs for a single application's service graph.</summary>
public sealed record ApplicationModuleOptions
{
    public required AppPaths Paths { get; init; }
    public required ISecretProtector SecretProtector { get; init; }
    public AppSettings? StartupSettings { get; init; }
    public IBookLibraryService? Library { get; init; }
    public required IMetadataService MetadataService { get; init; }
    /// <summary>Platform-supplied converter, or an explicit unsupported adapter.</summary>
    public required IBookFormatConverter FormatConverter { get; init; }
    public DoubanMetadataService? Douban { get; init; }
    public IAppUpdateInstaller? UpdateInstaller { get; init; }
    public ISyncService? SyncService { get; init; }
    public string? TtsCacheDirectory { get; init; }
    public Action<string>? RetrievalLog { get; init; }
}
