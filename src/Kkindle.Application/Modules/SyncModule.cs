using Kkindle.Infrastructure;

namespace Kkindle.ApplicationServices;

public sealed class SyncModule
{
    internal SyncModule(ApplicationModuleOptions options)
    {
        Service = options.SyncService ?? new S3SyncService(options.Paths, options.SecretProtector);
    }

    public ISyncService Service { get; }
}
