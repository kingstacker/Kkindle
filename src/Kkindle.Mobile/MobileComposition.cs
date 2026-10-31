using Kkindle.ApplicationServices;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle.Mobile;

/// <summary>
/// Builds the same service graph the desktop shell uses, from mobile platform
/// adapters. Construction still opens no library, starts no synchronization and
/// downloads no models; pages trigger that work when they become visible.
/// </summary>
public static class MobileComposition
{
    public static ApplicationModules Create(MobileServices services)
    {
        services.Paths.EnsureDirectories();
        // The mobile views are written in Chinese only; shared text (medals, sync
        // messages) must not switch to English with the system language.
        UiText.SetLanguage("zh-CN");
        var settings = new AppSettingsStore(services.Paths).LoadSynchronously();
        return new ApplicationModules(new ApplicationModuleOptions
        {
            Paths = services.Paths,
            SecretProtector = services.SecretProtector,
            MetadataService = services.MetadataService,
            FormatConverter = services.FormatConverter,
            StartupSettings = settings
        });
    }
}
