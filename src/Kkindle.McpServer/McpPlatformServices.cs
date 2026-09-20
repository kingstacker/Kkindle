using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle.McpServer;

internal static class McpPlatformServices
{
    public static IKindleDeviceService? CreateEjectService(
        AppPaths paths,
        IMetadataService metadata)
    {
#if NET10_0_WINDOWS
        return new Kkindle.Platform.Windows.KindleDeviceService(paths, metadata);
#else
        _ = paths;
        _ = metadata;
        return null;
#endif
    }
}
