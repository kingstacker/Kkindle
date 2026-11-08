namespace Kkindle.Core;

public static class MemberEntitlements
{
    public const int DeviceLimit = 5;
    public static string TierName(string tier) => tier switch
    { "pro" => "Pro 会员", "lifetime" => "终生功能会员", _ => "Free 会员" };
}

public enum DeviceFeature { CustomS3, CustomAi }

public interface IDeviceFeatureAccess
{
    Task EnsureFeatureAsync(DeviceFeature feature, CancellationToken cancellationToken = default);
}

public sealed class DeviceFeatureRequiredException(string message) : InvalidOperationException(message);

public sealed record AccountDevice(Guid DeviceId, string Name, string Platform, string State,
    DateTimeOffset RegisteredAt, DateTimeOffset LastSeenAt);

public sealed record AccountMembership(Guid AccountId, AccountRegion Region, string Tier, bool HasLifetimeFeatures,
    DateTimeOffset? ProExpiresAt, int DeviceLimit, int LoggedInDeviceCount, int ActivatedDeviceCount,
    long UploadBytes, long UploadQuota, long DownloadBytes, long DownloadRemainingBytes, long DownloadCreditBytes,
    long StorageBytes, long StorageQuota, DateTimeOffset? StorageAddonNextExpiresAt);

public sealed record DeviceFeatureStatus(AccountMembership Membership, AccountDevice[] Devices, Guid CurrentDeviceId)
{
    public bool IsCurrentDeviceActive => Devices.Any(device => device.DeviceId == CurrentDeviceId && device.State == "active");
    public bool CanUseFeatures => Membership.HasLifetimeFeatures && IsCurrentDeviceActive;
}

public sealed class DeviceEnrollmentRequest
{
    public Guid DeviceId { get; init; }
    public string DeviceName { get; init; } = "";
    public string Platform { get; init; } = "";
}

public sealed record MembershipProduct(string Id, string Name, string Kind, int PriceMinor, string Currency,
    int DurationDays, long Bytes, string Description);

public sealed class PurchaseRequest
{
    public string ProductId { get; init; } = "";
    public Guid RequestId { get; init; }
}

public sealed record PurchaseOrder(Guid Id, Guid AccountId, string ProductId, string ProductName, string Kind,
    int PriceMinor, string Currency, int DurationDays, long Bytes, string State,
    DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, DateTimeOffset? FulfilledAt);
