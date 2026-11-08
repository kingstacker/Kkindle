using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle.ApplicationServices;

public sealed class AccountModule
{
    internal AccountModule(ApplicationModuleOptions options, AccountRegion dataRegion) =>
        Service = new AccountService(options.Paths, options.SecretProtector, dataRegion: dataRegion);

    public AccountService Service { get; }
}
