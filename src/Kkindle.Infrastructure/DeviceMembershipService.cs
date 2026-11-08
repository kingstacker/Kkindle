using System.Net.Http.Json;
using Kkindle.Core;

namespace Kkindle.Infrastructure;

public sealed class DeviceMembershipService(AccountService accounts) : IDeviceFeatureAccess
{
    public async Task<DeviceFeatureStatus> RefreshAsync(CancellationToken token = default)
    {
        var device = await accounts.GetDeviceAsync(token);
        var reply = await accounts.MemberRequestAsync(HttpMethod.Get, "devices/" + device.DeviceId,
            AccountJsonContext.Default.DeviceFeatureStatus, null, token);
        Validate(reply.Value, reply.Scope, device.DeviceId);
        return reply.Value;
    }

    public async Task EnsureFeatureAsync(DeviceFeature feature, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(feature)) throw new ArgumentOutOfRangeException(nameof(feature));
        var device = await accounts.GetDeviceAsync(cancellationToken);
        var reply = await accounts.MemberRequestAsync(HttpMethod.Post, "devices/" + device.DeviceId + "/features",
            AccountJsonContext.Default.DeviceFeatureStatus, null, cancellationToken);
        Validate(reply.Value, reply.Scope, device.DeviceId);
        if (reply.Scope.RequiresDataRestart) throw new DeviceFeatureRequiredException("区域已切换，请重新打开应用后使用该区域的账号和功能权益。");
        if (!reply.Value.CanUseFeatures) throw new DeviceFeatureRequiredException("请登录拥有终生功能会员的账号，并确认本设备仍在 5 台已登录设备内。其他功能免费。");
    }

    public async Task KickAsync(Guid device, CancellationToken token = default)
    {
        await accounts.MemberActionAsync("devices/" + device + "/kick", token);
        var current = await accounts.GetDeviceAsync(token);
        if (current.DeviceId == device) await accounts.GetStatusAsync(validateSession: true, cancellationToken: token);
    }

    public async Task<MembershipProduct[]> ProductsAsync(CancellationToken token = default) =>
        (await accounts.MemberRequestAsync(HttpMethod.Get, "products", AccountJsonContext.Default.MembershipProductArray, null, token)).Value;

    public async Task<PurchaseOrder[]> OrdersAsync(CancellationToken token = default) =>
        (await accounts.MemberRequestAsync(HttpMethod.Get, "orders", AccountJsonContext.Default.PurchaseOrderArray, null, token)).Value;

    public async Task<PurchaseOrder> PurchaseAsync(string productId, Guid requestId, CancellationToken token = default)
    {
        var reply = await accounts.MemberRequestAsync(HttpMethod.Post, "orders", AccountJsonContext.Default.PurchaseOrder,
            JsonContent.Create(new PurchaseRequest { ProductId = productId, RequestId = requestId }, AccountJsonContext.Default.PurchaseRequest), token);
        if (reply.Value.AccountId != reply.Scope.User?.Id || reply.Value.Id == Guid.Empty || reply.Value.ProductId != productId)
            throw new InvalidOperationException("服务器返回了无效的订单。");
        return reply.Value;
    }

    public Task CancelOrderAsync(Guid order, CancellationToken token = default) => accounts.MemberActionAsync("orders/" + order + "/cancel", token);

    private static void Validate(DeviceFeatureStatus status, AccountStatus scope, Guid device)
    {
        if (status.Membership is null || status.Devices is null || status.Membership.AccountId != scope.User?.Id
            || status.Membership.Region != scope.Region || status.CurrentDeviceId != device
            || status.Membership.DeviceLimit != MemberEntitlements.DeviceLimit || status.Membership.Tier is not ("free" or "lifetime" or "pro")
            || status.Membership.LoggedInDeviceCount is < 0 or > MemberEntitlements.DeviceLimit
            || status.Devices.Length > 100 || status.Devices.Any(item => item.DeviceId == Guid.Empty)
            || status.Devices.Select(item => item.DeviceId).Distinct().Count() != status.Devices.Length
            || status.Devices.Any(item => item.State is not ("active" or "signed_out" or "blocked" or "expired"))
            || status.Devices.Count(item => item.State == "active") != status.Membership.LoggedInDeviceCount
            || status.Membership.ActivatedDeviceCount != (status.Membership.HasLifetimeFeatures ? status.Membership.LoggedInDeviceCount : 0))
            throw new InvalidOperationException("账号服务返回了无效的会员或设备状态。");
    }
}

internal sealed class DeniedDeviceFeatureAccess : IDeviceFeatureAccess
{
    internal static DeniedDeviceFeatureAccess Instance { get; } = new();
    public Task EnsureFeatureAsync(DeviceFeature feature, CancellationToken cancellationToken = default) =>
        Task.FromException(new DeviceFeatureRequiredException("自定义 S3 和自定义 AI 需要终生功能会员，请在账号页登录会员账号。其他功能免费。"));
}
