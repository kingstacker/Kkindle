using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle.ApplicationServices;

public sealed class AccountModule
{
    internal AccountModule(ApplicationModuleOptions options, AccountRegion dataRegion)
    {
        Service = new AccountService(options.Paths, options.SecretProtector, dataRegion: dataRegion);
        Membership = new DeviceMembershipService(Service);
        Monitor = new AccountDeviceMonitor(Service, options.Paths);
    }

    public AccountService Service { get; }
    public DeviceMembershipService Membership { get; }
    public AccountDeviceMonitor Monitor { get; }
}
