using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle.ApplicationServices;

public sealed class LibraryModule
{
    internal LibraryModule(ApplicationModuleOptions options)
    {
        Books = options.Library ?? new SqliteBookLibraryService(options.Paths, options.MetadataService);
        FormatConverter = options.FormatConverter;
        Settings = new AppSettingsStore(options.Paths);
        Backups = new AppBackupService(options.Paths, options.SecretProtector);
        Fonts = new FontLibraryService(options.Paths);
        Dictionary = new DictionaryService(options.Paths, FormatConverter);
    }

    public IBookLibraryService Books { get; }
    public IBookFormatConverter FormatConverter { get; }
    public AppSettingsStore Settings { get; }
    public AppBackupService Backups { get; }
    public FontLibraryService Fonts { get; }
    public DictionaryService Dictionary { get; }
}
