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

    var body: some View {
        Group {
            Section("会员与设备") {
                if let member = details?.membership {
                    Text(member.tierName).font(NativeTheme.font(19, relativeTo: .title3)).accessibilityIdentifier("membership.tier")
                    Text(member.hasLifetimeFeatures ? "已永久拥有自定义 S3 和自定义 AI 功能" : "自定义 S3 和自定义 AI 需要功能会员；其他功能免费")
                        .font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.secondary)
                    if let expiry = member.proExpiresAt { Text("Pro 有效至 \(expiry.prefix(10))").font(NativeTheme.font(12)) }
                    Text("空间 \(size(member.storageBytes)) / \(size(member.storageQuota))").font(NativeTheme.font(13))
                    Text("上传 \(size(member.uploadBytes)) / \(size(member.uploadQuota))").font(NativeTheme.font(13))
                    Text("下载剩余 \(size(member.downloadRemainingBytes)) · 购买余额 \(size(member.downloadCreditBytes))").font(NativeTheme.font(13))
                }
                Text("功能会员绑定账号，不使用激活码。每个账号最多登录 5 台设备；客户端每天检查一次设备状态。")
                    .font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.secondary)
                Button("刷新会员与设备") { perform { await refresh() } }.accessibilityIdentifier("membership.refresh")
                if busy { ProgressView() }
                if !status.isEmpty { Text(status).font(NativeTheme.font(12)).foregroundStyle(NativeTheme.secondary).accessibilityIdentifier("membership.status") }
            }
            Section("已登录设备 \(details?.membership.loggedInDeviceCount ?? 0) / 5") {
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
            Section("购买会员、空间或下载流量") {
                ForEach(products) { product in
                    VStack(alignment: .leading, spacing: 8) {
                        Text(product.name).font(NativeTheme.font(14)).bold()
                        Text(product.description).font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.secondary)
                        Button("创建订单 · \(product.price)") { purchase(product) }.accessibilityIdentifier("membership.buy.\(product.id)")
                    }
                }
                if products.isEmpty && !busy { Text("购买服务暂未开放，请联系管理员。").font(NativeTheme.font(12)).foregroundStyle(NativeTheme.secondary) }
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
            .task(id: store.account?.user?.id) { busy = true; defer { busy = false }; await refresh() }
            .confirmationDialog("踢出设备？", isPresented: Binding(get: { kickTarget != nil }, set: { if !$0 { kickTarget = nil } }), titleVisibility: .visible) {
                if let device = kickTarget {
                    Button("踢出 \(device.name)", role: .destructive) { kick(device) }
                }
                Button("取消", role: .cancel) { kickTarget = nil }
            } message: { Text("此设备会退出登录并释放名额。") }
            .onDisappear { task?.cancel() }
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
    private func kick(_ device: AccountDevice) {
        kickTarget = nil
        perform {
            let _: MessageResult = try await store.request("settings.membership.kick", ["deviceId": device.deviceId])
            let account: AccountStatus = try await store.request("settings.account.get")
            store.account = account
            if account.isLoggedIn { await refresh() }
        }
    }
    private func size(_ bytes: Int64) -> String {
        if bytes >= 1_073_741_824 { return String(format: "%.2f GiB", Double(bytes) / 1_073_741_824) }
        if bytes >= 1_048_576 { return String(format: "%.2f MiB", Double(bytes) / 1_048_576) }
        return "\(bytes) B"
    }
}
