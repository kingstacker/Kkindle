using System.Diagnostics;
using Kkindle.ApplicationServices;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle;

internal static class DesktopApplicationBootstrap
{
    public static ApplicationModules Create(
        AppPaths? paths = null,
        IBookLibraryService? library = null,
        IBookFormatConverter? formatConverter = null,
        DoubanMetadataService? douban = null,
        AppServices? services = null,
        AppSettings? startupSettings = null)
        => new(new ApplicationModuleOptions
        {
            Paths = paths ?? services?.Paths
                ?? new AppPaths(AppRootConfiguration.ResolveRoot(AppContext.BaseDirectory)),
            SecretProtector = services?.SecretProtector ?? new PreviewSecretProtector(),
            Library = library,
            MetadataService = new BookMetadataService(services?.PdfDocumentFactory),
            FormatConverter = formatConverter ?? new BookFormatConversionService(),
            Douban = douban,
            StartupSettings = startupSettings,
            UpdateInstaller = services?.UpdateInstaller,
            SyncService = services?.SyncService,
            TtsCacheDirectory = services?.TtsCacheDirectory,
            RetrievalLog = message => Debug.WriteLine($"[RAG] {message}")
        });

    public static ApplicationModules CreateForStartup(AppServices? services)
    {
        var paths = services?.Paths
            ?? new AppPaths(AppRootConfiguration.ResolveRoot(AppContext.BaseDirectory));
        var settings = new AppSettingsStore(paths).LoadSynchronously();
        return Create(paths: paths, services: services, startupSettings: settings);
    }

    // Platform heads always supply encryption; only the designer/test fallback
    // retains the existing unprotected-settings behavior.
    private sealed class PreviewSecretProtector : ISecretProtector
    {
        public byte[] Protect(byte[] value) => value.ToArray();
        public byte[] Unprotect(byte[] value) => value.ToArray();
    }
}
