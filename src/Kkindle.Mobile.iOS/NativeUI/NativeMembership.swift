import SwiftUI

struct NativeMembershipSections: View {
    @ObservedObject var store: NativeStore
    @State private var details: DeviceFeatureStatus?
    @State private var products: [MembershipProduct] = []
    @State private var orders: [PurchaseOrder] = []
    @State private var status = ""
    @State private var busy = false
    @State private var task: Task<Void, Never>?
    @State private var kickTarget: AccountDevice?
    @State private var activationCode = ""
    @State private var activationAttemptCode = ""
    @State private var activationRequestId = UUID().uuidString

    var body: some View {
        Group {
            Section("会员与设备") {
                if let member = details?.membership {
                    Text(member.tierName).font(NativeTheme.font(19, relativeTo: .title3)).accessibilityIdentifier("membership.tier")
                    Text(member.hasLifetimeFeatures && member.tier != "free" ? "自备 S3 / WebDAV 多端同步已解锁" : "Free 不支持多端同步")
                        .font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.secondary)
                        .accessibilityIdentifier("membership.sync")
                    if member.tier == "pro", let expiry = member.proExpiresAt { Text("Plus 有效至 \(expiry.prefix(10))").font(NativeTheme.font(12)) }
                    if member.tier != "pro" {
                        if let expiry = member.proExpiresAt {
                            Text("Plus 已于 \(expiry.prefix(10)) 到期。官方云同步与下载已停用，云端书籍及其他同步数据保留一个月；在保留期内续费可恢复访问，保留期结束后自动销毁。")
                                .font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.secondary)
                                .accessibilityIdentifier("membership.expired")
                        }
                    }
                }
                Text("Free 不支持多端同步。功能解锁使用自备 S3 / WebDAV；Plus 提供官方云同步、10GB 空间和每年 150GB 下载。Plus 到期后停止官方云同步与下载，云数据保留一个月后自动销毁。每个账号最多登录 6 台设备。")
                    .font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.secondary)
                Button("刷新会员与设备") { perform { await refresh() } }.accessibilityIdentifier("membership.refresh")
                if busy { ProgressView() }
                if !status.isEmpty { Text(status).font(NativeTheme.font(12)).foregroundStyle(NativeTheme.secondary).accessibilityIdentifier("membership.status") }
            }
            if let details { NativeAccountUsage(status: details) }
            Section("已登录设备 \(details?.membership.loggedInDeviceCount ?? 0) / \(details?.membership.deviceLimit ?? 6)") {
                if !deviceCapacity.isEmpty {
                    Text(deviceCapacity).font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.secondary)
                        .accessibilityIdentifier("membership.devices.capacity")
                }
                ForEach(details?.devices.filter { $0.state == "active" } ?? []) { device in
                    VStack(alignment: .leading, spacing: 7) {
                        Text(device.name + (device.deviceId == details?.currentDeviceId ? "（当前设备）" : ""))
                            .font(NativeTheme.font(14)).accessibilityIdentifier("membership.device.\(device.deviceId)")
                        Text("\(device.platform) · 最近访问 \(device.lastSeenAt.prefix(16).replacingOccurrences(of: "T", with: " ")) UTC")
                            .font(NativeTheme.font(11, relativeTo: .caption)).foregroundStyle(NativeTheme.secondary)
                        Button("踢出设备", role: .destructive) { kickTarget = device }.accessibilityIdentifier("membership.kick.\(device.deviceId)")
                    }
                }
            }
            NativeMembershipPlans(membership: details?.membership, products: products, account: store.account,
                                  loading: busy && products.isEmpty) { purchase($0) }
            Section("激活码兑换") {
                Text("激活码绑定当前已登录账户。功能会员永久有效，Plus 激活码每个增加 1 年。")
                    .font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.secondary)
                TextField("输入激活码", text: $activationCode).font(NativeTheme.font(14))
                    .textInputAutocapitalization(.characters).autocorrectionDisabled().privacySensitive()
                    .accessibilityIdentifier("membership.activation.code")
                Button("兑换激活码") { redeem() }
                    .disabled(activationCode.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                    .accessibilityIdentifier("membership.activation.redeem")
            }
            if !orders.isEmpty {
                Section("订单") {
                    ForEach(orders) { order in
                        VStack(alignment: .leading, spacing: 6) {
                            Text(order.productName + " · " + order.stateName).font(NativeTheme.font(13)).accessibilityIdentifier("membership.order.\(order.id)")
                            if order.state == "pending" && !order.isExpired {
                                Text(order.id).font(NativeTheme.font(10, relativeTo: .caption)).foregroundStyle(NativeTheme.secondary)
                                Button("取消订单") { perform {
                                    let _: MessageResult = try await store.request("settings.membership.cancel", ["orderId": order.id])
                                    await refresh()
                                } }.accessibilityIdentifier("membership.cancel.\(order.id)")
                            }
                        }
                    }
                }
            }
        }.disabled(busy)
            .task(id: store.account?.user?.id) { activationCode = ""; activationAttemptCode = ""; busy = true; defer { busy = false }; await refresh() }
            .confirmationDialog("踢出设备？", isPresented: Binding(get: { kickTarget != nil }, set: { if !$0 { kickTarget = nil } }), titleVisibility: .visible) {
                if let device = kickTarget {
                    Button("踢出 \(device.name)", role: .destructive) { kick(device) }
                }
                Button("取消", role: .cancel) { kickTarget = nil }
            } message: { Text("此设备会退出登录并释放名额。") }
            .onDisappear { task?.cancel(); activationCode = ""; activationAttemptCode = "" }
    }

    private var deviceCapacity: String {
        guard let member = details?.membership else { return "" }
        let remaining = max(0, member.deviceLimit - member.loggedInDeviceCount)
        return remaining > 0 ? "还可登录 \(remaining) 台设备" : "设备名额已满，可退出旧设备后登录新设备。"
    }

    private func refresh() async {
        do {
            let value: DeviceFeatureStatus = try await store.request("settings.membership.get")
            let offers: [MembershipProduct] = try await store.request("settings.membership.products")
            let purchases: [PurchaseOrder] = try await store.request("settings.membership.orders")
            guard !Task.isCancelled else { return }
            details = value; products = offers; orders = purchases; status = ""
        } catch { if !Task.isCancelled { status = error.localizedDescription } }
    }

    private func perform(_ operation: @escaping () async throws -> Void) {
        guard !busy else { return }
        busy = true
        task = Task {
            defer { busy = false }
            do { try await operation() }
            catch { if !Task.isCancelled { status = error.localizedDescription } }
        }
    }

    private func purchase(_ product: MembershipProduct) {
        perform {
            let _: PurchaseOrder = try await store.request("settings.membership.purchase", ["productId": product.id, "requestId": UUID().uuidString])
            await refresh(); status = "订单已创建，等待确认付款。权益到账后刷新即可使用。"
        }
    }
    private func redeem() {
        guard store.account?.isLoggedIn == true else { return }
        let code = activationCode.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !code.isEmpty else { return }
        let normalized = code.uppercased().replacingOccurrences(of: "-", with: "").replacingOccurrences(of: " ", with: "")
        if normalized != activationAttemptCode { activationAttemptCode = normalized; activationRequestId = UUID().uuidString }
        let requestId = activationRequestId
        perform {
            let result: ActivationCodeRedemption = try await store.request("settings.membership.redeem", ["code": code, "requestId": requestId])
            guard !Task.isCancelled else { return }
            details = result.status; activationCode = ""; activationAttemptCode = ""
            await refresh()
            status = result.alreadyRedeemed == true ? "该激活码已兑换，当前会员状态已刷新。"
                : result.kind == "pro" ? "Plus 激活成功，已增加 1 年有效期。" : "功能会员激活成功，已永久解锁。"
        }
    }
    private func kick(_ device: AccountDevice) {
        kickTarget = nil
        perform {
            let _: MessageResult = try await store.request("settings.membership.kick", ["deviceId": device.deviceId])
            let account: AccountStatus = try await store.request("settings.account.get")
            store.account = account
            if account.isLoggedIn { await refresh() }
        }
    }
}

/// The desktop account center's plan cards on a phone: the same offers, wording
/// and button rules. Prices only ever come from the server's product list.
struct NativeMembershipPlans: View {
    static let anchor = "membership.plans"
    let membership: AccountMembership?
    let products: [MembershipProduct]
    let account: AccountStatus?
    var loading = false
    let purchase: (MembershipProduct) -> Void

    private var signedIn: Bool { account?.isLoggedIn == true }
    private var plan: String { membership?.plan ?? "" }
    private var lifetime: MembershipProduct? { products.first { $0.kind == "lifetime" } }
    private var plus: MembershipProduct? { products.first { $0.kind == "plus" || $0.kind == "pro" } }
    private var canRenewPlus: Bool {
        guard let plus, let membership else { return false }
        return membership.canExtendPlus(days: plus.durationDays)
    }
    private var isLoopbackServer: Bool {
        guard let host = account.flatMap({ URL(string: $0.serverUrl)?.host?.lowercased() }) else { return false }
        return host == "localhost" || host == "::1" || host == "[::1]" || host.hasPrefix("127.")
    }
    private var hint: String {
        let limit = "Plus 最多可持有 5 年有效期，付费和激活码共用上限。"
        guard signedIn else { return "登录后可查看价格并创建订单。" + limit }
        if loading && products.isEmpty { return "正在获取方案价格…" }
        return (products.isEmpty ? "购买服务暂未开放，请稍后刷新。"
            : isLoopbackServer ? "本机演示价：创建订单不会实际扣款。" : "创建订单后，确认付款并到账即可开通权益。") + limit
    }
    private func price(_ product: MembershipProduct?) -> String? { signedIn ? product?.price ?? "—" : nil }

    var body: some View {
        Section("会员方案") {
            VStack(alignment: .leading, spacing: 6) {
                Text("选择适合你的阅读方式，随时升级。").font(NativeTheme.font(13, relativeTo: .caption)).foregroundStyle(NativeTheme.secondary)
                Text("功能解锁、Plus 会员及扩容服务均不支持退款，请在购买前确认方案和期限。")
                    .font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.secondary)
                    .accessibilityIdentifier("membership.no-refund-policy")
            }.id(Self.anchor)
            NativePlanCard(name: "Plus", tagline: "让每一本书，随你而行", price: price(plus), suffix: "/年", period: "按年付费，随时续费",
                           badge: "推荐", accent: NativeTheme.planPlus, isCurrent: plan == "plus",
                           benefits: ["包含功能解锁的同步权益", "10GB Kkindle 私人云空间", "书籍与阅读数据自动同步", "每会员年度 150GB 下载额度", "最多 6 台设备登录"],
                           footer: "到期后云端数据保留一个月；Plus 不附赠永久功能解锁。") {
                purchaseButton(plus == nil ? "暂未开放" : !canRenewPlus ? "暂不可续费" : plan == "plus" ? "续费 Plus" : "开通 Plus",
                               product: plus, enabled: canRenewPlus, accent: NativeTheme.planPlus, filled: true)
            }
            NativePlanCard(name: "功能解锁", tagline: "你的书库，由你掌控", price: price(lifetime), period: "一次购买，永久使用",
                           accent: NativeTheme.planLifetime, isCurrent: plan == "lifetime",
                           benefits: ["包含全部 Free 权益", "自备 S3 / WebDAV 多端同步", "进度与批注完整同步", "最多 6 台设备登录", "一次购买，永久使用"],
                           footer: "需自行配置 S3 / WebDAV；存储与流量费用自行承担。") {
                purchaseButton(plan == "lifetime" ? "已永久解锁" : lifetime == nil ? "暂未开放" : "解锁功能",
                               product: lifetime, enabled: lifetime != nil && plan != "lifetime", accent: NativeTheme.planLifetime, filled: false)
            }
            NativePlanCard(name: "Free", tagline: "让阅读自由开始", price: "¥0", period: "永久免费",
                           accent: NativeTheme.planFree, isCurrent: plan == "free",
                           benefits: ["本地书库管理与阅读", "USB Kindle 书籍与批注双向同步", "插件功能", "自定义 AI 接口", "加密备份"],
                           footer: "仅本地权益，不含多端同步或 Kkindle 云空间。") {
                Button(plan == "free" ? "当前方案" : "永久免费") {}
                    .buttonStyle(NativePlanButtonStyle(accent: NativeTheme.planFree, filled: false)).disabled(true)
            }
            Text(hint).font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.secondary)
                .accessibilityIdentifier("membership.plans.hint")
        }
    }

    @ViewBuilder private func purchaseButton(_ title: String, product: MembershipProduct?, enabled: Bool, accent: Color, filled: Bool) -> some View {
        if signedIn {
            Button(title) { if let product { purchase(product) } }
                .buttonStyle(NativePlanButtonStyle(accent: accent, filled: filled)).disabled(!enabled)
                .accessibilityIdentifier(product.map { "membership.buy.\($0.id)" } ?? "")
        }
    }
}

struct NativePlanBadge: View {
    let text: String
    let accent: Color
    var body: some View {
        Text(text).font(NativeTheme.font(11, relativeTo: .caption2)).foregroundStyle(accent)
            .padding(.horizontal, 8).padding(.vertical, 3).background(accent.opacity(0.12), in: Capsule())
    }
}

private struct NativePlanButtonStyle: ButtonStyle {
    let accent: Color
    let filled: Bool
    @Environment(\.isEnabled) private var enabled
    func makeBody(configuration: Configuration) -> some View {
        configuration.label.font(NativeTheme.font())
            .foregroundStyle(!enabled ? NativeTheme.muted : filled ? NativeTheme.page : accent)
            .frame(maxWidth: .infinity).padding(.vertical, 12)
            .background(!enabled ? NativeTheme.sunk : filled ? accent : accent.opacity(0.12), in: RoundedRectangle(cornerRadius: 12))
            .opacity(configuration.isPressed ? 0.75 : 1)
    }
}

private struct NativePlanCard<Action: View>: View {
    let name: String
    let tagline: String
    /// nil means the price needs a signed-in session.
    let price: String?
    var suffix = ""
    let period: String
    var badge = ""
    let accent: Color
    let isCurrent: Bool
    let benefits: [String]
    let footer: String
    @ViewBuilder let action: Action

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            VStack(alignment: .leading, spacing: 4) {
                HStack(spacing: 8) {
                    Text(name).font(NativeTheme.font(19, relativeTo: .title3)).foregroundStyle(accent)
                    if !badge.isEmpty { NativePlanBadge(text: badge, accent: accent) }
                }
                caption(tagline)
            }
            VStack(alignment: .leading, spacing: 2) {
                if let price {
                    HStack(alignment: .firstTextBaseline, spacing: 3) {
                        Text(price).font(NativeTheme.font(28, relativeTo: .title))
                        if !suffix.isEmpty { caption(suffix) }
                    }
                } else {
                    Text("登录后查看价格").font(NativeTheme.font(15)).foregroundStyle(NativeTheme.muted)
                }
                caption(period)
            }
            NativeTheme.divider.frame(height: 1)
            VStack(alignment: .leading, spacing: 8) {
                ForEach(benefits, id: \.self) { benefit in
                    HStack(alignment: .top, spacing: 8) {
                        Image(systemName: "checkmark").font(.system(size: 11, weight: .semibold)).foregroundStyle(accent).padding(.top, 4)
                        Text(benefit).font(NativeTheme.font(14))
                    }
                }
            }
            NativeTheme.divider.frame(height: 1)
            caption(footer)
            action
        }.padding(16).frame(maxWidth: .infinity, alignment: .leading)
            .background(NativeTheme.surface, in: RoundedRectangle(cornerRadius: 14))
            .overlay(RoundedRectangle(cornerRadius: 14).stroke(isCurrent ? accent : NativeTheme.strong, lineWidth: isCurrent ? 1.5 : 1))
    }

    private func caption(_ text: String) -> some View {
        Text(text).font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.secondary)
    }
}
