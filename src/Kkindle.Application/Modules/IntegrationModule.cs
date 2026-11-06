using Kkindle.Infrastructure;

namespace Kkindle.ApplicationServices;

public sealed class IntegrationModule
{
    internal IntegrationModule(ApplicationModuleOptions options)
    {
        Diagnostics = new PlatformDiagnosticsService(options.Paths);
        DeviceModels = new DeviceModelStore(options.Paths);
        DeviceCache = new KindleDeviceAuxiliaryCacheStore(options.Paths);
        KindleEmailSettings = new KindleEmailSettingsStore(options.Paths, options.SecretProtector);
        KindleEmail = new KindleEmailSender();
        Updates = options.UpdateInstaller is { } installer ? new UpdateService(installer) : null;
    }

    public PlatformDiagnosticsService Diagnostics { get; }
    public DeviceModelStore DeviceModels { get; }
    public KindleDeviceAuxiliaryCacheStore DeviceCache { get; }
    public KindleEmailSettingsStore KindleEmailSettings { get; }
    public KindleEmailSender KindleEmail { get; }
    public UpdateService? Updates { get; }
}
