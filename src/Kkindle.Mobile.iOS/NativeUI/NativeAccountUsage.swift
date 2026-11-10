import SwiftUI

/// The desktop account center's cloud usage cards, stacked for a phone. Both
/// surfaces read the same figures: used/quota/remaining, this month's download,
/// the annual Plus allowance and any older purchased credit.
struct NativeAccountUsage: View {
    let status: DeviceFeatureStatus

    var body: some View {
        Section("云空间与流量") {
            if status.canUseAccountCloudSync == true {
                NativeStorageUsage(member: status.membership)
                NativeDownloadUsage(member: status.membership)
            } else {
                NativeCloudLocked(member: status.membership)
            }
        }
    }
}

private struct NativeStorageUsage: View {
    let member: AccountMembership

    private var used: Int64 { max(0, member.storageBytes) }
    private var quota: Int64 { max(0, member.storageQuota) }

    var body: some View {
        NativeUsageCard(symbol: "icloud", title: "云端空间",
                        used: used, quota: quota, available: true,
                        usedIdentifier: "membership.storage.used",
                        quotaIdentifier: "membership.storage.quota",
                        percentIdentifier: "membership.storage.percent") {
            NativeUsageRow(name: "剩余空间",
                           value: used >= quota ? "空间已满" : memberSize(quota - used),
                           identifier: "membership.storage.remaining")
        }
    }
}

private struct NativeDownloadUsage: View {
    let member: AccountMembership

    private var hasAnnualUsage: Bool {
        member.plusDownloadQuotaBytes != nil && member.plusDownloadUsedBytes != nil
    }
    private var quota: Int64 { max(0, member.plusDownloadQuotaBytes ?? 0) }
    private var used: Int64 { max(0, member.plusDownloadUsedBytes ?? 0) }
    private var annualRemaining: Int64 { max(0, quota - used) }
    /// Old credit sits outside the annual allowance; the desktop adds it back
    /// into the total remaining figure instead of counting it twice.
    private var additional: Int64 {
        hasAnnualUsage ? max(0, member.downloadRemainingBytes - annualRemaining)
            : max(0, member.downloadCreditBytes)
    }

    var body: some View {
        NativeUsageCard(symbol: "arrow.down.circle", title: "下载流量", period: "会员年度",
                        used: used, quota: quota, available: hasAnnualUsage,
                        usedIdentifier: "membership.download.used",
                        quotaIdentifier: "membership.download.quota",
                        percentIdentifier: "membership.download.percent") {
            NativeUsageRow(name: "年度剩余流量",
                           value: hasAnnualUsage ? memberSize(annualRemaining) : "—",
                           identifier: "membership.download.remaining")
            NativeUsageRow(name: "本月下载", value: memberSize(member.downloadBytes),
                           identifier: "membership.download.month")
            if additional > 0 {
                Text(creditText)
                    .font(NativeTheme.font(11, relativeTo: .caption2)).foregroundStyle(NativeTheme.secondary)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .accessibilityIdentifier("membership.download.credit")
            }
            if hasAnnualUsage, let period = member.plusDownloadPeriodText {
                Text(period)
                    .font(NativeTheme.font(11, relativeTo: .caption2)).foregroundStyle(NativeTheme.muted)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .accessibilityIdentifier("membership.download.period")
            }
        }
    }

    private var creditText: String {
        member.downloadCreditBytes > 0
            ? "额外可用流量 \(memberSize(additional))，含旧版购买余额 \(memberSize(member.downloadCreditBytes))；不计入年度限额。"
            : "额外可用流量 \(memberSize(additional))，不计入年度限额。"
    }
}

/// One large number and one thin bar instead of the desktop's ring: a phone has
/// less width, but the same used figure, percentage and remaining value appear.
private struct NativeUsageCard<Footer: View>: View {
    let symbol: String
    let title: String
    var period = ""
    let used: Int64
    let quota: Int64
    let available: Bool
    let usedIdentifier: String
    let quotaIdentifier: String
    let percentIdentifier: String
    @ViewBuilder let footer: Footer

    private var ratio: Double { quota > 0 ? Double(used) / Double(quota) : used > 0 ? 1 : 0 }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack(spacing: 8) {
                Image(systemName: symbol).font(.system(size: 14, weight: .medium))
                    .foregroundStyle(NativeTheme.ink).accessibilityHidden(true)
                Text(title).font(NativeTheme.font(15, relativeTo: .headline))
                Spacer(minLength: 8)
                if !period.isEmpty {
                    Text(period).font(NativeTheme.font(11, relativeTo: .caption2)).foregroundStyle(NativeTheme.muted)
                }
            }
            HStack(alignment: .center, spacing: 12) {
                VStack(alignment: .leading, spacing: 3) {
                    Text(available ? memberSize(used) : "—")
                        .font(NativeTheme.font(24, relativeTo: .title2)).monospacedDigit()
                        .accessibilityIdentifier(usedIdentifier)
                    Text(available ? "共 " + memberSize(quota) : "年度用量暂不可用")
                        .font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.secondary)
                        .accessibilityIdentifier(quotaIdentifier)
                }
                Spacer(minLength: 0)
                VStack(alignment: .trailing, spacing: 2) {
                    Text(available ? memberPercent(ratio) : "—")
                        .font(NativeTheme.font(17, relativeTo: .body)).monospacedDigit()
                        .accessibilityIdentifier(percentIdentifier)
                    Text("已用").font(NativeTheme.font(10, relativeTo: .caption2)).foregroundStyle(NativeTheme.muted)
                }
            }
            NativeThinProgress(value: available ? Double(used) : 0, total: Double(max(1, quota)), height: 6)
            NativeTheme.divider.frame(height: 1)
            VStack(spacing: 8) { footer }
        }.padding(16).background(NativeTheme.sunk, in: RoundedRectangle(cornerRadius: 18))
    }
}

private struct NativeUsageRow: View {
    let name: String
    let value: String
    var identifier = ""
    var body: some View {
        HStack(spacing: 8) {
            Text(name).font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.muted)
            Spacer(minLength: 8)
            Text(value).font(NativeTheme.font(14, relativeTo: .subheadline)).monospacedDigit()
                .accessibilityIdentifier(identifier)
        }
    }
}

private struct NativeCloudLocked: View {
    let member: AccountMembership

    private var title: String { member.isPlusExpired ? "Kkindle 云同步已停用" : "Kkindle 云同步未开通" }
    private var hint: String {
        member.plan == "lifetime"
            ? "功能解锁使用自备 S3 / WebDAV；Kkindle 云空间与下载需要 Plus。"
            : "开通 Plus 即可同步书籍与阅读数据，无需配置自备存储。"
    }

    var body: some View {
        HStack(alignment: .center, spacing: 12) {
            Image(systemName: "lock.fill").font(.system(size: 15, weight: .medium))
                .foregroundStyle(NativeTheme.secondary)
                .frame(width: 36, height: 36)
                .background(NativeTheme.strong, in: RoundedRectangle(cornerRadius: 10))
                .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 6) {
                Text(title).font(NativeTheme.font(15, relativeTo: .headline))
                Text(hint).font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.secondary)
            }
        }.padding(16).frame(maxWidth: .infinity, alignment: .leading)
            .background(NativeTheme.sunk, in: RoundedRectangle(cornerRadius: 18))
            .accessibilityIdentifier("membership.usage.locked")
    }
}
