import SwiftUI

/// The desktop account center's signed-in summary: avatar, username, region,
/// membership badge and expiry, with refresh and sign-out in the same card.
struct NativeAccountHeader: View {
    let account: AccountStatus?
    let membership: AccountMembership?
    let busy: Bool
    let signingOut: Bool
    let status: String
    let accountStatus: String
    let refresh: () -> Void
    let signOut: (() -> Void)?

    private var plan: String { membership?.plan ?? "free" }
    private var accent: Color { NativeTheme.planAccent(plan) }
    private var summary: String {
        guard let membership else { return "正在读取会员信息…" }
        switch membership.plan {
        case "plus": return "Plus 会员 · Kkindle 云同步权益已开通"
        case "lifetime": return "功能解锁 · 自备存储同步已解锁"
        default: return "Free · 畅享本地阅读"
        }
    }
    private var symbol: String {
        switch plan {
        case "plus": return "person.crop.circle.badge.plus"
        case "lifetime": return "person.crop.circle.badge.checkmark"
        default: return "person.crop.circle"
        }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack(alignment: .center, spacing: 14) {
                ZStack {
                    Circle().fill(accent.opacity(0.12))
                    Image(systemName: symbol).font(.system(size: 21, weight: .medium))
                        .foregroundStyle(accent)
                }.frame(width: 44, height: 44).accessibilityHidden(true)
                VStack(alignment: .leading, spacing: 5) {
                    Text(account?.user?.username ?? "").font(NativeTheme.font(20, relativeTo: .title3).weight(.semibold))
                        .lineLimit(1).minimumScaleFactor(0.75).truncationMode(.tail)
                        .accessibilityIdentifier("account.username")
                    HStack(spacing: 6) {
                        Circle().fill(NativeTheme.success).frame(width: 6, height: 6)
                        Text("已登录").font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.success)
                        Text("·").font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.muted)
                        Text(account?.regionName ?? "").font(NativeTheme.font(12, relativeTo: .caption))
                            .foregroundStyle(NativeTheme.muted)
                    }
                    Text(summary).font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.muted)
                        .lineLimit(2).accessibilityIdentifier("membership.sync")
                }
                Spacer(minLength: 8)
                VStack(alignment: .trailing, spacing: 6) {
                    if let membership {
                        Text(membership.planName)
                            .font(NativeTheme.font(12, relativeTo: .caption).weight(.semibold))
                            .foregroundStyle(accent)
                            .padding(.horizontal, 8).padding(.vertical, 3)
                            .background(accent.opacity(0.12), in: RoundedRectangle(cornerRadius: 6))
                            .accessibilityIdentifier("membership.tier")
                        Text(membership.planExpiryText).font(NativeTheme.font(11, relativeTo: .caption2))
                            .foregroundStyle(NativeTheme.muted).multilineTextAlignment(.trailing)
                    } else {
                        ProgressView().controlSize(.small)
                    }
                }
            }
            NativeTheme.divider.frame(height: 1)
            HStack(spacing: 14) {
                Button {
                    refresh()
                } label: {
                    HStack(spacing: 5) {
                        Image(systemName: "arrow.clockwise").font(.system(size: 12, weight: .medium))
                        Text("刷新")
                    }.font(NativeTheme.font(13)).foregroundStyle(NativeTheme.secondary)
                }.buttonStyle(.plain).disabled(busy || signingOut)
                    .accessibilityIdentifier("membership.refresh")
                if busy || signingOut { ProgressView().controlSize(.small) }
                Spacer(minLength: 0)
                if let signOut {
                    Button("退出登录") { signOut() }.buttonStyle(.plain)
                        .font(NativeTheme.font(13)).foregroundStyle(NativeTheme.secondary)
                        .disabled(busy || signingOut)
                        .accessibilityIdentifier("account.logout")
                }
            }
            if !status.isEmpty {
                Text(status).font(NativeTheme.font(12)).foregroundStyle(NativeTheme.secondary)
                    .accessibilityIdentifier("membership.status")
            }
            if !accountStatus.isEmpty {
                Text(accountStatus).font(NativeTheme.font(12)).foregroundStyle(NativeTheme.secondary)
                    .accessibilityIdentifier("account.status")
            }
        }.padding(16).background(NativeTheme.sunk, in: RoundedRectangle(cornerRadius: 18))
    }
}

/// The desktop expiry notice: a warning tinted card above the locked usage panel.
struct NativeExpiryNotice: View {
    let text: String
    var body: some View {
        HStack(alignment: .top, spacing: 12) {
            Image(systemName: "clock.badge.exclamationmark").font(.system(size: 16, weight: .medium))
                .foregroundStyle(NativeTheme.warning).padding(.top, 2).accessibilityHidden(true)
            Text(text).font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.secondary)
        }.padding(16).frame(maxWidth: .infinity, alignment: .leading)
            .background(NativeTheme.warning.opacity(0.10), in: RoundedRectangle(cornerRadius: 14))
            .overlay(RoundedRectangle(cornerRadius: 14).stroke(NativeTheme.warning.opacity(0.25), lineWidth: 1))
            .accessibilityIdentifier("membership.expired")
    }
}

/// One desktop-style device row: platform avatar, name, current-device badge,
/// relative activity and a quiet sign-out action.
struct NativeDeviceRow: View {
    let device: AccountDevice
    let current: Bool
    let onKick: () -> Void

    private var symbol: String {
        switch device.platform.lowercased() {
        case "ios", "android": return "iphone"
        case "ipados", "ipad": return "ipad"
        default: return "desktopcomputer"
        }
    }
    private var activity: String {
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        var seen = formatter.date(from: device.lastSeenAt)
        if seen == nil {
            formatter.formatOptions = [.withInternetDateTime]
            seen = formatter.date(from: device.lastSeenAt)
        }
        guard let seen else { return device.lastSeenAt }
        let elapsed = max(0, Date().timeIntervalSince(seen))
        if elapsed < 60 { return "刚刚" }
        if elapsed < 3600 { return "\(Int(elapsed / 60)) 分钟前" }
        if elapsed < 86_400 { return "\(Int(elapsed / 3600)) 小时前" }
        if elapsed < 604_800 { return "\(Int(elapsed / 86_400)) 天前" }
        let display = DateFormatter()
        display.dateFormat = "MM/dd HH:mm"
        return display.string(from: seen)
    }

    var body: some View {
        HStack(alignment: .center, spacing: 12) {
            Image(systemName: symbol).font(.system(size: 16, weight: .medium))
                .foregroundStyle(current ? NativeTheme.success : NativeTheme.secondary)
                .frame(width: 36, height: 36)
                .background(current ? NativeTheme.success.opacity(0.12) : NativeTheme.strong,
                            in: RoundedRectangle(cornerRadius: 10))
                .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 4) {
                Text(device.name).font(NativeTheme.font(13, relativeTo: .subheadline)).lineLimit(1)
                    .truncationMode(.tail)
                    .accessibilityIdentifier("membership.device.\(device.deviceId)")
                    .accessibilityLabel(device.name + (current ? "（当前设备）" : ""))
                Text("\(device.platform) · 最近活跃 \(activity)")
                    .font(NativeTheme.font(11, relativeTo: .caption2)).foregroundStyle(NativeTheme.secondary)
            }
            Spacer(minLength: 8)
            if current {
                Text("当前设备").font(NativeTheme.font(10, relativeTo: .caption2)).foregroundStyle(NativeTheme.success)
                    .padding(.horizontal, 6).padding(.vertical, 2)
                    .background(NativeTheme.success.opacity(0.12), in: RoundedRectangle(cornerRadius: 4))
            }
            Button(current ? "退出此设备" : "退出设备") { onKick() }.buttonStyle(.plain)
                .font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.secondary)
                .accessibilityIdentifier("membership.kick.\(device.deviceId)")
        }.padding(.vertical, 4)
    }
}

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
                        usedLabel: "已用空间", percentLabel: "已用",
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
        NativeUsageCard(symbol: "arrow.down.circle", title: "同步流量", period: "会员年度",
                        usedLabel: "年度下载已用", percentLabel: "年度下载占比",
                        used: used, quota: quota, available: hasAnnualUsage,
                        usedIdentifier: "membership.download.used",
                        quotaIdentifier: "membership.download.quota",
                        percentIdentifier: "membership.download.percent") {
            NativeUsageRow(name: "年度下载剩余",
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
    let usedLabel: String
    let percentLabel: String
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
                    Text(usedLabel).font(NativeTheme.font(11, relativeTo: .caption2)).foregroundStyle(NativeTheme.muted)
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
                    Text(percentLabel).font(NativeTheme.font(10, relativeTo: .caption2)).foregroundStyle(NativeTheme.muted)
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
