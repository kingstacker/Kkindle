using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle.ApplicationServices;

public sealed class SyncModule
{
    internal SyncModule(ApplicationModuleOptions options, AccountService accounts, AccountRegion dataRegion)
    {
        Service = options.SyncService ?? new S3SyncService(options.Paths, options.SecretProtector, accounts, dataRegion);
    }

    public ISyncService Service { get; }
}
