import SwiftUI

struct NativeMembershipSections: View {
    @ObservedObject var store: NativeStore
    var onSignOut: (() -> Void)?
    var signingOut = false
    var accountStatus = ""
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
            Section {
                NativeAccountHeader(account: store.account, membership: details?.membership,
                                    busy: busy, signingOut: signingOut,
                                    status: status, accountStatus: accountStatus,
                                    refresh: { perform { await refresh() } }, signOut: onSignOut)
            }
            if let member = details?.membership, member.isPlusExpired, let notice = member.plusExpiryMessage {
                NativeExpiryNotice(text: notice)
            }
            if let details { NativeAccountUsage(status: details) }
            Section("已登录设备") {
                HStack(alignment: .center, spacing: 12) {
                    Text(deviceCapacity).font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.secondary)
                        .accessibilityIdentifier("membership.devices.capacity")
                    Spacer(minLength: 8)
                    VStack(alignment: .trailing, spacing: 5) {
                        Text("\(details?.membership.loggedInDeviceCount ?? 0) / \(details?.membership.deviceLimit ?? 6) 台")
                            .font(NativeTheme.font(15, relativeTo: .subheadline)).monospacedDigit()
                        HStack(spacing: 4) {
                            ForEach(0..<max(0, details?.membership.deviceLimit ?? 6), id: \.self) { index in
                                RoundedRectangle(cornerRadius: 2)
                                    .fill(index < (details?.membership.loggedInDeviceCount ?? 0) ? NativeTheme.success : NativeTheme.strong)
                                    .frame(width: 16, height: 4)
                            }
                        }.accessibilityHidden(true)
                    }
                }
                ForEach(activeDevices) { device in
                    NativeDeviceRow(device: device, current: device.deviceId == details?.currentDeviceId) {
                        kickTarget = device
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

    /// The desktop lists the current device first, then the most recently seen.
    private var activeDevices: [AccountDevice] {
        (details?.devices ?? []).filter { $0.state == "active" }.sorted {
            let left = $0.deviceId == details?.currentDeviceId
            let right = $1.deviceId == details?.currentDeviceId
            if left != right { return left }
            return $0.lastSeenAt > $1.lastSeenAt
        }
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
    @State private var selection = 2

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
        Section {
            VStack(alignment: .leading, spacing: 6) {
                Text("会员方案").font(NativeTheme.font(17, relativeTo: .headline).weight(.semibold))
                Text("选择适合你的阅读方式，随时升级。").font(NativeTheme.font(13, relativeTo: .caption)).foregroundStyle(NativeTheme.secondary)
                Text("功能解锁、Plus 会员及扩容服务均不支持退款，请在购买前确认方案和期限。")
                    .font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.secondary)
                    .accessibilityIdentifier("membership.no-refund-policy")
            }.id(Self.anchor)
            Picker("会员方案", selection: $selection) {
                Text("Free").tag(0)
                Text("功能解锁").tag(1)
                Text("Plus").tag(2)
            }.pickerStyle(.segmented)
                .accessibilityIdentifier("membership.plans.tabs")
            planCard(selection)
                .id(selection)
                .simultaneousGesture(
                    DragGesture(minimumDistance: 24).onEnded { value in
                        guard abs(value.translation.width) > abs(value.translation.height) else { return }
                        if value.translation.width < -40 { select(selection + 1) }
                        else if value.translation.width > 40 { select(selection - 1) }
                    }
                )
                .accessibilityHint("左右轻扫可切换会员方案")
            Text(hint).font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.secondary)
                .accessibilityIdentifier("membership.plans.hint")
        }.onChange(of: membership?.plan) { newPlan in
            // Open on the paid plan the account already holds; otherwise show
            // the recommended Plus offer first.
            if newPlan == "lifetime" { select(1) }
            else if newPlan != nil { select(2) }
        }
    }

    private func select(_ index: Int) {
        selection = min(2, max(0, index))
    }

    @ViewBuilder private func planCard(_ index: Int) -> some View {
        switch index {
        case 0:
            NativePlanCard(name: "Free", tagline: "让阅读自由开始", price: "¥0", period: "永久免费",
                           accent: NativeTheme.planFree, isCurrent: plan == "free",
                           benefits: ["本地书库管理与阅读", "USB Kindle 书籍与批注双向同步", "插件功能", "自定义 AI 接口", "加密备份"],
                           footer: "仅本地权益，不含多端同步或 Kkindle 云空间。") {
                Button(plan == "free" ? "当前方案" : "永久免费") {}
                    .buttonStyle(NativePlanButtonStyle(accent: NativeTheme.planFree, filled: false)).disabled(true)
            }
        case 1:
            NativePlanCard(name: "功能解锁", tagline: "你的书库，由你掌控", price: price(lifetime), period: "一次购买，永久使用",
                           accent: NativeTheme.planLifetime, isCurrent: plan == "lifetime",
                           benefits: ["包含全部 Free 权益", "自备 S3 / WebDAV 多端同步", "进度与批注完整同步", "最多 6 台设备登录", "一次购买，永久使用"],
                           footer: "需自行配置 S3 / WebDAV；存储与流量费用自行承担。") {
                purchaseButton(plan == "lifetime" ? "已永久解锁" : lifetime == nil ? "暂未开放" : "解锁功能",
                               product: lifetime, enabled: lifetime != nil && plan != "lifetime", accent: NativeTheme.planLifetime, filled: false)
            }
        default:
            NativePlanCard(name: "Plus", tagline: "让每一本书，随你而行", price: price(plus), suffix: "/年", period: "按年付费，随时续费",
                           badge: "推荐", accent: NativeTheme.planPlus, isCurrent: plan == "plus",
                           benefits: ["包含功能解锁的同步权益", "10GB Kkindle 私人云空间", "书籍与阅读数据自动同步", "每会员年度 150GB 下载额度", "最多 6 台设备登录"],
                           footer: "到期后云端数据保留一个月；Plus 不附赠永久功能解锁。") {
                purchaseButton(plus == nil ? "暂未开放" : !canRenewPlus ? "暂不可续费" : plan == "plus" ? "续费 Plus" : "开通 Plus",
                               product: plus, enabled: canRenewPlus, accent: NativeTheme.planPlus, filled: true)
            }
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
