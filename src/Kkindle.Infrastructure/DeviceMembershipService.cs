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
        var officialCloud = feature == DeviceFeature.AccountCloudSync;
        var reply = await accounts.MemberRequestAsync(officialCloud ? HttpMethod.Get : HttpMethod.Post,
            "devices/" + device.DeviceId + (officialCloud ? "" : "/features"),
            AccountJsonContext.Default.DeviceFeatureStatus, null, cancellationToken);
        Validate(reply.Value, reply.Scope, device.DeviceId);
        if (reply.Scope.RequiresDataRestart) throw new DeviceFeatureRequiredException("区域已切换，请重新打开应用后使用该区域的账号和功能权益。");
        if (officialCloud ? !reply.Value.CanUseAccountCloudSync : !reply.Value.CanUseFeatures)
            throw new DeviceFeatureRequiredException(officialCloud ? MemberEntitlements.AccountCloudRequirement : MemberEntitlements.SyncRequirement);
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

    /// <summary>Claims the one-time free trial. Only called after the user turns on the claim switch.</summary>
    public async Task<AccountMembership> ClaimTrialAsync(CancellationToken token = default)
    {
        var reply = await accounts.MemberRequestAsync(HttpMethod.Post, "trial",
            AccountJsonContext.Default.AccountMembership, null, token,
            UiText.Get("请先登录当前区域的账号后领取免费试用。"));
        if (reply.Value.AccountId != reply.Scope.User?.Id || reply.Value.Region != reply.Scope.Region
            || !reply.Value.TrialClaimed || reply.Value.TrialClaimedAt is null || reply.Value.TrialExpiresAt is null)
            throw new InvalidOperationException("账号服务返回了无效的试用状态。");
        return reply.Value;
    }

    public async Task<ActivationCodeRedemption> RedeemActivationCodeAsync(string code, Guid requestId, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > 80) throw new InvalidOperationException(UiText.Get("请输入有效的激活码。"));
        if (requestId == Guid.Empty) throw new ArgumentException("兑换请求标识无效。", nameof(requestId));
        var device = await accounts.GetDeviceAsync(token);
        var reply = await accounts.MemberRequestAsync(HttpMethod.Post, "activation-codes/redeem", AccountJsonContext.Default.ActivationCodeRedemption,
            JsonContent.Create(new ActivationCodeRedeemRequest { Code = code.Trim(), RequestId = requestId }, AccountJsonContext.Default.ActivationCodeRedeemRequest),
            token, UiText.Get("请先登录当前区域的账号后兑换激活码。"));
        if (reply.Value.CodeId == Guid.Empty || reply.Value.Kind is not ("lifetime" or "pro")
            || (reply.Value.Kind == "pro" ? reply.Value.Years != 1 : reply.Value.Years != 0) || reply.Value.Status is null)
            throw new InvalidOperationException(UiText.Get("服务器返回了无效的激活结果。"));
        Validate(reply.Value.Status, reply.Scope, device.DeviceId);
        return reply.Value;
    }

    private static void Validate(DeviceFeatureStatus status, AccountStatus scope, Guid device)
    {
        if (status.Membership is null || status.Devices is null || status.Membership.AccountId != scope.User?.Id
            || status.Membership.Region != scope.Region || status.CurrentDeviceId != device
            || status.Membership.DeviceLimit != MemberEntitlements.DeviceLimit || status.Membership.Tier is not ("free" or "lifetime" or "pro")
            || status.Membership.LoggedInDeviceCount is < 0 or > MemberEntitlements.DeviceLimit
            || status.Membership.PendingPlusDays < 0
            || status.Devices.Length > 100 || status.Devices.Any(item => item.DeviceId == Guid.Empty)
            || status.Devices.Select(item => item.DeviceId).Distinct().Count() != status.Devices.Length
            || status.Devices.Any(item => item.State is not ("active" or "signed_out" or "blocked" or "expired"))
            || status.Devices.Count(item => item.State == "active") != status.Membership.LoggedInDeviceCount
            || status.Membership.ActivatedDeviceCount != (status.Membership.HasLifetimeFeatures ? status.Membership.LoggedInDeviceCount : 0)
            || (status.Membership.TrialAvailable && (status.Membership.TrialClaimed || status.Membership.ProExpiresAt is not null))
            || status.Membership.TrialClaimed != (status.Membership.TrialClaimedAt is not null)
            || (status.Membership.TrialExpiresAt is not null && !status.Membership.TrialClaimed)
            || (status.Membership.TrialExpiresAt is { } trialExpiry && status.Membership.TrialClaimedAt is { } trialClaimed
                && trialExpiry <= trialClaimed))
            throw new InvalidOperationException("账号服务返回了无效的会员或设备状态。");
    }
}

internal sealed class DeniedDeviceFeatureAccess : IDeviceFeatureAccess
{
    internal static DeniedDeviceFeatureAccess Instance { get; } = new();
    public Task EnsureFeatureAsync(DeviceFeature feature, CancellationToken cancellationToken = default) =>
        Task.FromException(new DeviceFeatureRequiredException(feature == DeviceFeature.AccountCloudSync
            ? MemberEntitlements.AccountCloudRequirement : MemberEntitlements.SyncRequirement));
}
