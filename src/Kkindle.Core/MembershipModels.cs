namespace Kkindle.Core;

public static class MemberEntitlements
{
    public const int DeviceLimit = 6;
    public const int MaxPlusYears = 5;
    public const int MaxPlusDays = 365 * MaxPlusYears;
    /// <summary>A lapsed free trial keeps its cloud data for 7 days before deletion.</summary>
    public const int TrialRetentionDays = 7;
    public const string SyncRequirement = "多端同步需要功能解锁（¥88 永久）或有效的 Plus 会员。功能解锁使用自备 S3 / WebDAV；Plus 订阅期内也可使用自备 S3 / WebDAV，到期后收回。Kkindle 云同步需要 Plus。";
    public const string AccountCloudRequirement = "Kkindle 云同步和下载需要有效的 Plus 会员。Free 不支持多端同步；功能解锁可使用自备 S3 / WebDAV 永久同步。";
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

    /// <summary>
    /// Paid Plus keeps cloud data for one UTC calendar month after expiry. A membership that
    /// lapsed at the end of the one-time free trial keeps it for 7 days instead.
    /// </summary>
    public static DateTimeOffset CloudRetentionEndsAt(DateTimeOffset expiry, DateTimeOffset? trialExpiresAt = null) =>
        trialExpiresAt is { } trial && trial == expiry
            ? expiry.ToUniversalTime().AddDays(TrialRetentionDays)
            : expiry.ToUniversalTime().AddMonths(1);

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

    /// <summary>The most urgent 30 / 7 / 1-day reminder that applies, or null when Plus is not expiring soon.</summary>
    public static int? PlusRenewalReminder(DateTimeOffset expiry, DateTimeOffset now)
    {
        var remaining = expiry.ToUniversalTime() - now.ToUniversalTime();
        if (remaining <= TimeSpan.Zero) return null;
        if (remaining <= TimeSpan.FromDays(1)) return 1;
        if (remaining <= TimeSpan.FromDays(7)) return 7;
        if (remaining <= TimeSpan.FromDays(30)) return 30;
        return null;
    }

    public static string PlusRenewalMessage(DateTimeOffset expiry, DateTimeOffset now, DateTimeOffset? trialExpiresAt = null)
    {
        var days = Math.Max(1, (int)Math.Ceiling((expiry.ToUniversalTime() - now.ToUniversalTime()).TotalDays));
        if (trialExpiresAt is { } trial && trial == expiry)
            return days <= 1
                ? UiText.Get("Plus 免费试用将于 {0:yyyy/MM/dd} 到期（不足 1 天）。到期后 Kkindle 云同步、下载及自备 S3 / WebDAV 将停用，试用期云端数据保留 7 天；开通正式 Plus 可继续使用。请及时开通。", expiry.LocalDateTime)
                : UiText.Get("Plus 免费试用将于 {0:yyyy/MM/dd} 到期（剩余 {1} 天）。到期后 Kkindle 云同步、下载及自备 S3 / WebDAV 将停用，试用期云端数据保留 7 天；开通正式 Plus 可继续使用。请及时开通。", expiry.LocalDateTime, days);
        return days <= 1
            ? UiText.Get("Plus 将于 {0:yyyy/MM/dd} 到期（不足 1 天）。到期后 Kkindle 云同步、下载及自备 S3 / WebDAV 将停用，云端数据保留一个月；已购买功能解锁的自备同步不受影响。请及时续费。", expiry.LocalDateTime)
            : UiText.Get("Plus 将于 {0:yyyy/MM/dd} 到期（剩余 {1} 天）。到期后 Kkindle 云同步、下载及自备 S3 / WebDAV 将停用，云端数据保留一个月；已购买功能解锁的自备同步不受影响。请及时续费。", expiry.LocalDateTime, days);
    }

    public static string PlusExpiryMessage(DateTimeOffset expiry, DateTimeOffset now, DateTimeOffset? trialExpiresAt = null)
    {
        var retentionEnd = CloudRetentionEndsAt(expiry, trialExpiresAt);
        var trial = trialExpiresAt is { } trialEnd && trialEnd == expiry;
        if (trial)
            return now < retentionEnd
                ? UiText.Get("Plus 免费试用已于 {0:yyyy/MM/dd} 到期。Kkindle 云同步、下载及试用期间使用的自备 S3 / WebDAV 已停用；已购买的功能解锁不受影响。试用期云端书籍及其他同步数据保留至 {1:yyyy/MM/dd}；在保留期内开通 Plus 可恢复访问，保留期结束后自动销毁。", expiry.LocalDateTime, retentionEnd.LocalDateTime)
                : UiText.Get("Plus 免费试用已于 {0:yyyy/MM/dd} 到期。Kkindle 云同步、下载及试用期间使用的自备 S3 / WebDAV 已停用；已购买的功能解锁不受影响。试用期云端数据的 7 天保留期已结束，数据已进入自动销毁流程。", expiry.LocalDateTime);
        return now < retentionEnd
            ? UiText.Get("Plus 已于 {0:yyyy/MM/dd} 到期。Kkindle 云同步、下载及 Plus 期间使用的自备 S3 / WebDAV 已停用；已购买的功能解锁不受影响。云端书籍及其他同步数据保留至 {1:yyyy/MM/dd}；在保留期内续费可恢复访问，保留期结束后自动销毁。", expiry.LocalDateTime, retentionEnd.LocalDateTime)
            : UiText.Get("Plus 已于 {0:yyyy/MM/dd} 到期。Kkindle 云同步、下载及 Plus 期间使用的自备 S3 / WebDAV 已停用；已购买的功能解锁不受影响。云端书籍及其他同步数据的一个月保留期已结束，数据已进入自动销毁流程。", expiry.LocalDateTime);
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
    DateTimeOffset? PlusRenewalLimitAt = null, int PendingPlusDays = 0,
    long? PlusEarlyDownloadQuotaBytes = null, long? PlusEarlyDownloadUsedBytes = null,
    DateTimeOffset? PlusEarlyDownloadEndsAt = null,
    long? MetadataDownloadQuotaBytes = null, long? MetadataDownloadUsedBytes = null,
    DateTimeOffset? MetadataDownloadResetsAt = null,
    // One-time free Plus trial. TrialAvailable is true only while the account has never
    // held Plus; TrialClaimed stays true after the trial ends.
    bool TrialAvailable = false, bool TrialClaimed = false,
    DateTimeOffset? TrialClaimedAt = null, DateTimeOffset? TrialExpiresAt = null)
{
    /// <summary>The active or most recent Plus period was the free trial, not a paid subscription.</summary>
    public bool IsTrialPlus => TrialExpiresAt is { } trial && trial == ProExpiresAt;
}

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
