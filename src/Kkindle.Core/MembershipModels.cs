namespace Kkindle.Core;

public static class MemberEntitlements
{
    public const int DeviceLimit = 6;
    public const int MaxPlusYears = 5;
    public const int MaxPlusDays = 365 * MaxPlusYears;
    public const string SyncRequirement = "多端同步需要功能解锁（¥88 永久）或有效的 Plus 会员。功能解锁使用自备 S3 / WebDAV，Kkindle 云同步需要 Plus。";
    public const string AccountCloudRequirement = "Kkindle 云同步和下载需要有效的 Plus 会员。Free 不支持多端同步，功能解锁请使用自备 S3 / WebDAV。";
    /// <summary>Wire IDs stay "free" / "lifetime" / "pro"; the plan sells them as Free, 功能解锁 and Plus.</summary>
    public static string TierName(string tier) => tier switch
    { "pro" => "Plus 会员", "lifetime" => "功能解锁", _ => "Free" };

    public static DeviceFeature SyncFeature(SyncProvider provider) => provider switch
    {
        SyncProvider.S3 => DeviceFeature.CustomS3,
        SyncProvider.WebDav => DeviceFeature.CustomWebDav,
        SyncProvider.AccountServer => DeviceFeature.AccountCloudSync,
        _ => throw new ArgumentOutOfRangeException(nameof(provider))
    };

    public static DateTimeOffset CloudRetentionEndsAt(DateTimeOffset expiry) => expiry.ToUniversalTime().AddMonths(1);

    public static bool CanExtendPlus(AccountMembership membership, int days, DateTimeOffset now)
    {
        if (days is < 1 or > MaxPlusDays || membership.PendingPlusDays < 0) return false;
        var start = membership.ProExpiresAt is { } expiry && expiry > now ? expiry : now;
        var limit = membership.PlusRenewalLimitAt ?? now.ToUniversalTime().AddYears(MaxPlusYears);
        try { return start.AddDays((long)days + membership.PendingPlusDays) <= limit; }
        catch (ArgumentOutOfRangeException) { return false; }
    }

    public static string ActivationMessage(ActivationCodeRedemption result) => UiText.Get(result.AlreadyRedeemed
        ? "该激活码已兑换，当前会员状态已刷新。"
        : result.Kind == "pro" ? "Plus 激活成功，已增加 1 年有效期。" : "功能会员激活成功，已永久解锁。");

    public static string PlusExpiryMessage(DateTimeOffset expiry, DateTimeOffset now)
    {
        var retentionEnd = CloudRetentionEndsAt(expiry);
        return now < retentionEnd
            ? UiText.Get("Plus 已于 {0:yyyy/MM/dd} 到期。Kkindle 云同步与下载已停用，云端书籍及其他同步数据保留至 {1:yyyy/MM/dd}；在保留期内续费可恢复访问，保留期结束后自动销毁。", expiry.LocalDateTime, retentionEnd.LocalDateTime)
            : UiText.Get("Plus 已于 {0:yyyy/MM/dd} 到期。Kkindle 云同步与下载已停用，云端书籍及其他同步数据的一个月保留期已结束，数据将自动销毁。", expiry.LocalDateTime);
    }
}

/// <summary>Custom stores need 功能解锁 or active Plus; official cloud sync needs active Plus. Custom AI stays free.</summary>
public enum DeviceFeature { CustomS3, CustomWebDav, AccountCloudSync }

public interface IDeviceFeatureAccess
{
    Task EnsureFeatureAsync(DeviceFeature feature, CancellationToken cancellationToken = default);
}

public sealed class DeviceFeatureRequiredException(string message) : InvalidOperationException(message);

public sealed record AccountDevice(Guid DeviceId, string Name, string Platform, string State,
    DateTimeOffset RegisteredAt, DateTimeOffset LastSeenAt);

// UploadQuota is retained for wire compatibility; zero means no monthly upload limit. Monthly bytes are statistics.
public sealed record AccountMembership(Guid AccountId, AccountRegion Region, string Tier, bool HasLifetimeFeatures,
    DateTimeOffset? ProExpiresAt, int DeviceLimit, int LoggedInDeviceCount, int ActivatedDeviceCount,
    long UploadBytes, long UploadQuota, long DownloadBytes, long DownloadRemainingBytes, long DownloadCreditBytes,
    long StorageBytes, long StorageQuota, DateTimeOffset? StorageAddonNextExpiresAt,
    long? PlusDownloadQuotaBytes = null, long? PlusDownloadUsedBytes = null,
    DateTimeOffset? PlusDownloadStartsAt = null, DateTimeOffset? PlusDownloadExpiresAt = null,
    DateTimeOffset? PlusRenewalLimitAt = null, int PendingPlusDays = 0);

public sealed record DeviceFeatureStatus(AccountMembership Membership, AccountDevice[] Devices, Guid CurrentDeviceId)
{
    public bool IsCurrentDeviceActive => Devices.Any(device => device.DeviceId == CurrentDeviceId && device.State == "active");
    public bool CanUseFeatures => Membership.Tier is "lifetime" or "pro" && Membership.HasLifetimeFeatures && IsCurrentDeviceActive;
    public bool CanUseAccountCloudSync => Membership.Tier == "pro" && CanUseFeatures;
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

public sealed class ActivationCodeRedeemRequest
{
    public string Code { get; init; } = "";
    public Guid RequestId { get; init; }
}

public sealed record ActivationCodeRedemption(Guid CodeId, string Kind, int Years,
    DateTimeOffset RedeemedAt, DeviceFeatureStatus Status, bool AlreadyRedeemed = false);
