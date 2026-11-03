using Kkindle.Core;
using Kkindle.Infrastructure;
using Kkindle.Platform.Common;

namespace Kkindle.McpServer;

internal static class McpPlatformServices
{
    public static IKindleDeviceService? CreateDeviceService(
        AppPaths paths,
        IMetadataService metadata)
    {
#if WINDOWS
        return new Kkindle.Platform.Windows.KindleDeviceService(paths, metadata);
#else
        if (!OperatingSystem.IsMacOS()) return null;
        // macOS needs the same dual transport as the desktop head: mounted
        // storage for older Kindles and libmtp for newer MTP-only models.
        var massStorage = new MassStorageKindleDeviceService(
            paths,
            metadata,
            eject: Kkindle.Platform.MacOS.MacOSKindleEjector.EjectAsync);
        var mtp = new Kkindle.Platform.MacOS.MtpKindleDeviceService(paths, metadata);
        return new Kkindle.Platform.MacOS.MacOSKindleDeviceService(massStorage, mtp);
#endif
    }
}
