import SwiftUI
import UIKit

struct NativeKeyboardDone: ViewModifier {
    func body(content: Content) -> some View {
        content.toolbar {
            ToolbarItemGroup(placement: .keyboard) {
                Spacer()
                Button("完成") { UIApplication.shared.sendAction(#selector(UIResponder.resignFirstResponder), to: nil, from: nil, for: nil) }
            }
        }
    }
}

struct NativeProfilePage: View {
    @ObservedObject var store: NativeStore
    @State private var summary: ProfileSummary?
    @State private var busy = false
    @State private var developer = false
    @State private var versionTaps = 0
    @State private var firstVersionTap = Date.distantPast

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 0) {
                Text("我的").font(NativeTheme.pageTitleFont).accessibilityIdentifier("page.profile")
                    .padding(.bottom, 16)
                NavigationLink { NativeAccountPage(store: store) } label: {
                    NativeSettingsRow(title: (store.account ?? summary?.account)?.user?.username ?? "登录 / 注册",
                        value: (store.account ?? summary?.account)?.regionName ?? "用户账号")
                }.accessibilityIdentifier("profile.account")
                NavigationLink { NativeSyncSettings(store: store) } label: { NativeSettingsRow(title: "云同步", value: summary?.sync ?? "") }
                    .accessibilityIdentifier("profile.sync")
                NavigationLink { NativeAiSettings(store: store) } label: { NativeSettingsRow(title: "AI 助手", value: summary?.ai ?? "") }
                NavigationLink { NativeEmailSettings(store: store) } label: { NativeSettingsRow(title: "发送到 Kindle", value: summary?.kindle ?? "") }

                NavigationLink { NativeLibrarySettings(store: store) } label: { NativeSettingsRow(title: "书库设置") }
                    .accessibilityIdentifier("profile.librarySettings")

                sectionLabel("外观", top: 28).padding(.bottom, 10)
                Picker("主题", selection: Binding(get: { store.preferences.themeMode }, set: { mode in
                    var value = store.preferences; value.themeMode = mode
                    store.setPreferences(value)
                })) {
                    Text("跟随系统").tag(0); Text("浅色").tag(1); Text("深色").tag(2)
                }.pickerStyle(.segmented).accessibilityIdentifier("profile.theme")

                sectionLabel("数据", top: 32).padding(.bottom, 4)
                NativeSettingsRow(title: "书籍占用", value: summary?.storage ?? "—", chevron: false)
                Button { runBackup(false) } label: { NativeSettingsRow(title: "导出备份") }
                    .disabled(busy).accessibilityIdentifier("profile.backup.export")
                Button { runBackup(true) } label: { NativeSettingsRow(title: "恢复备份") }
                    .disabled(busy).accessibilityIdentifier("profile.backup.restore")
                NavigationLink { NativeTrash(store: store) } label: { NativeSettingsRow(title: "回收站") }
                NavigationLink { NativeDictionary(store: store, initialTerm: "").nativeBackButton() } label: { NativeSettingsRow(title: "本地词典") }

                sectionLabel("关于", top: 24).padding(.bottom, 4)
                Button { UIPasteboard.general.string = "1109898894"; store.showToast("群号已复制到剪贴板") } label: {
                    NativeSettingsRow(title: "QQ 交流群", value: "1109898894", chevron: false)
                }
                Button { tapVersion() } label: { NativeSettingsRow(title: "版本", value: summary?.version ?? "", chevron: false) }
            }.padding(.horizontal, 24).padding(.top, 20).padding(.bottom, 32).buttonStyle(.plain)
        }.background(NativeAmbientBackground()).navigationBarHidden(true)
            .overlay(alignment: .top) { NativeStatusBarGlass() }
            .sheet(isPresented: $developer) {
                NativeNameSheet(store: store, title: "开发者模式", initial: "", button: "进入", isSecure: true) { password in
                    try await store.command("settings.developer.toggle", ["password": password])
                }
            }
            .task {
                do {
                    let result: ProfileSummary = try await store.request("settings.profile")
                    summary = result; store.account = result.account
                }
                catch { if !Task.isCancelled { store.failure = error.localizedDescription } }
            }
    }
    private func sectionLabel(_ title: String, top: CGFloat) -> some View {
        Text(title).font(NativeTheme.font(13, relativeTo: .caption)).foregroundStyle(NativeTheme.muted).padding(.top, top)
    }
    private func runBackup(_ restore: Bool) {
        busy = true
        store.perform { defer { busy = false }; try await store.command(restore ? "backup.restore" : "backup.export") }
    }
    private func tapVersion() {
        let now = Date()
        if versionTaps == 0 || now.timeIntervalSince(firstVersionTap) > 3 { versionTaps = 0; firstVersionTap = now }
        versionTaps += 1
        guard versionTaps >= 5 else { return }; versionTaps = 0
        if store.developerMode { store.perform { try await store.command("settings.developer.toggle") } }
        else { developer = true }
    }
}

struct NativeLibrarySettings: View {
    @ObservedObject var store: NativeStore

    var body: some View {
        NativeForm {
            settingToggle("画廊视图", identifier: "library.settings.gallery", isOn: Binding(get: { store.preferences.galleryMode }, set: { enabled in
                var value = store.preferences; value.galleryMode = enabled
                store.setPreferences(value)
            }))
            settingToggle("炫彩模式", identifier: "library.settings.colorful", isOn: Binding(get: { store.preferences.colorfulMode }, set: { enabled in
                var value = store.preferences; value.colorfulMode = enabled
                store.setPreferences(value)
            }))
            settingToggle("显示书籍同步状态", identifier: "library.settings.syncStatus", isOn: Binding(get: { store.preferences.showBookSyncStatus }, set: { enabled in
                var value = store.preferences; value.showBookSyncStatus = enabled
                store.setPreferences(value)
            }))
        }
        .navigationTitle("书库设置").navigationBarTitleDisplayMode(.inline).navigationBarHidden(false)
        .nativeBackButton()
        .modifier(NativeHideTabBar())
    }

    private func settingToggle(_ title: String, identifier: String, isOn: Binding<Bool>) -> some View {
        HStack(spacing: 12) {
            Text(title).font(NativeTheme.font()).accessibilityHidden(true)
            Spacer(minLength: 12)
            NativeSwitch(title: title, isOn: isOn, identifier: identifier).fixedSize()
        }.frame(minHeight: 44).accessibilityElement(children: .contain)
    }
}

struct NativeSyncSettings: View {
    @ObservedObject var store: NativeStore
    @State private var form = SyncForm()
    @State private var formLoaded = false
    @State private var savedProvider = 0
    @State private var providerTouched = false
    @State private var summary = ""
    @State private var status = ""
    @State private var busy = false
    @State private var loading = true
    @State private var advanced = false
    @State private var actionTask: Task<Void, Never>?
    @State private var member: DeviceFeatureStatus?
    @State private var memberMessage = ""

    private var accountMode: Bool { form.provider == 2 }
    private var signedIn: Bool { store.account?.isLoggedIn == true }
    private var memberKey: String { accountMode ? (store.account?.user?.id ?? "signed-out") : "" }

    /// A deliberate picker change outranks the automatic Plus default until saved.
    private var providerSelection: Binding<Int> {
        Binding(get: { form.provider }, set: { value in
            form.provider = value
            if !loading { providerTouched = true }
        })
    }

    var body: some View {
        NativeForm {
            Section {
                Picker("同步方式", selection: providerSelection) { Text("账号云同步").tag(2); Text("S3").tag(0); Text("WebDAV").tag(1) }
                    .pickerStyle(.segmented).accessibilityIdentifier("sync.provider")
                if !accountMode {
                    Toggle("启用同步", isOn: $form.enabled).tint(NativeTheme.systemSwitchTint)
                    Toggle("自动同步", isOn: $form.automaticSyncEnabled).tint(NativeTheme.systemSwitchTint)
                }
            }
            if accountMode { accountSections } else { customStorageSections }
            Section {
                VStack(alignment: .leading, spacing: 10) {
                    if !status.isEmpty {
                        Text(status).font(NativeTheme.font(13)).foregroundStyle(NativeTheme.muted)
                            .textSelection(.enabled).accessibilityIdentifier("sync.status")
                    }
                    if busy { ProgressView() }
                    HStack(spacing: 12) {
                        if accountMode { Button("立即同步") { run("run") }.accessibilityIdentifier("sync.run") }
                        else { Button("测试连接") { run("test") } }
                        Button("保存") { run("save") }.accessibilityIdentifier("sync.save")
                    }.buttonStyle(NativePrimaryButtonStyle()).disabled(busy || loading)
                }
            }
        }.disabled(busy || loading)
            .navigationTitle("同步").navigationBarTitleDisplayMode(.inline).navigationBarHidden(false)
            .nativeBackButton()
            .modifier(NativeKeyboardDone())
            .modifier(NativeHideTabBar())
            .task { await load() }
            .task(id: memberKey) { await loadMembership() }
            .onChange(of: store.syncStatus) { status = $0 }
            .onDisappear { actionTask?.cancel() }
    }

    /// Mirrors the desktop's account mode: connection details, the directory
    /// prefix, advanced options and profile import/export don't apply here.
    @ViewBuilder private var accountSections: some View {
        Section {
            NavigationLink { NativeAccountPage(store: store) } label: { accountSummary }
                .accessibilityIdentifier("sync.account")
            if store.account?.requiresDataRestart == true {
                caption("区域已切换，请重新打开应用后同步。", color: NativeTheme.warning)
            }
            if let member { cloudStatus(member) }
            if !memberMessage.isEmpty { caption(memberMessage).accessibilityIdentifier("sync.membership.status") }
        }
        Section("同步设置") {
            Toggle("启用同步", isOn: $form.enabled).tint(NativeTheme.systemSwitchTint)
            Toggle("自动同步", isOn: $form.automaticSyncEnabled).tint(NativeTheme.systemSwitchTint)
            Toggle("同步时下载书籍文件", isOn: $form.downloadBookFilesOnSync).tint(NativeTheme.systemSwitchTint)
            caption("关闭时仅同步书籍元数据和封面；打开书籍或点击下载图标时再下载书籍文件")
            syncKey
            // The summary describes the saved settings, not an unsaved provider switch.
            if savedProvider == 2 && !summary.isEmpty { caption(summary).accessibilityIdentifier("sync.summary") }
        }
        // Plans, activation codes and orders live in the account page; this row
        // keeps the upgrade path next to the feature that needs it.
        Section("会员") {
            NavigationLink { NativeAccountPage(store: store) } label: {
                NativeSettingsRow(title: "会员方案",
                                  value: member?.membership.tierName ?? (signedIn ? "—" : "登录后查看"),
                                  chevron: false)
            }.accessibilityIdentifier("sync.membership.manage")
        }
    }

    private var accountSummary: some View {
        VStack(alignment: .leading, spacing: 5) {
            HStack(spacing: 8) {
                Text(signedIn ? store.account?.user?.username ?? "" : "登录 / 注册")
                    .font(NativeTheme.font(17, relativeTo: .headline)).lineLimit(1)
                if signedIn, let membership = member?.membership {
                    NativePlanBadge(text: membership.planName, accent: NativeTheme.planAccent(membership.plan))
                }
            }
            caption(accountDetail)
            caption("仅同步当前区域账号的数据，两区不互通。")
        }
    }

    private var accountDetail: String {
        let region = store.account?.regionName ?? "中国区"
        guard signedIn else { return region + " · 登录 Plus 账户后使用云同步" }
        return member.map { region + " · " + $0.membership.planExpiryText } ?? region
    }

    @ViewBuilder private func cloudStatus(_ entitlement: DeviceFeatureStatus) -> some View {
        let membership = entitlement.membership
        if membership.plan == "plus" && entitlement.canUseAccountCloudSync == true {
            VStack(alignment: .leading, spacing: 12) {
                VStack(alignment: .leading, spacing: 4) {
                    Text("Kkindle 云同步权益已开通").font(NativeTheme.font(15)).foregroundStyle(NativeTheme.success)
                    caption("书籍、阅读进度、书签与批注，可通过 Kkindle 云同步在设备之间同步。")
                }
                usage("云端空间", used: membership.storageBytes, quota: membership.storageQuota)
                if let quota = membership.plusDownloadQuotaBytes, let used = membership.plusDownloadUsedBytes {
                    usage("年度下载", used: used, quota: quota, footnote: membership.plusDownloadPeriodText)
                } else { caption("年度下载用量暂不可用") }
            }.accessibilityElement(children: .contain).accessibilityIdentifier("sync.membership.cloud")
        } else {
            // A one-line notice like the desktop's, so the sync key stays on the first screen.
            VStack(alignment: .leading, spacing: 6) {
                HStack(alignment: .firstTextBaseline, spacing: 8) {
                    Text(membership.plan == "plus" ? "当前设备暂不能使用 Kkindle 云同步"
                        : membership.isPlusExpired ? "Kkindle 云同步已停用" : "Kkindle 云同步需要 Plus 会员")
                        .font(NativeTheme.font(14)).foregroundStyle(NativeTheme.warning)
                    Spacer(minLength: 8)
                    if membership.plan != "plus" {
                        NavigationLink { NativeAccountPage(store: store) } label: {
                            Text(membership.isPlusExpired ? "续费 Plus" : "了解 Plus")
                                .font(NativeTheme.font(14)).foregroundStyle(NativeTheme.planPlus)
                        }.buttonStyle(.plain).accessibilityIdentifier("sync.plans.view")
                    }
                }
                caption(membership.plan == "plus" ? "请在账号页面检查当前设备是否仍在已登录设备中。"
                    : membership.plusExpiryMessage ?? (membership.plan == "lifetime"
                        ? "功能解锁使用自备 S3 / WebDAV；Kkindle 云空间与下载需要 Plus。"
                        : "开通 Plus 即可同步书籍与阅读数据，无需配置自备存储。"))
                    .accessibilityIdentifier("sync.membership.notice")
            }.padding(12).frame(maxWidth: .infinity, alignment: .leading)
                .background(NativeTheme.sunk, in: RoundedRectangle(cornerRadius: 12))
        }
    }

    private func usage(_ title: String, used: Int64, quota: Int64, footnote: String? = nil) -> some View {
        VStack(alignment: .leading, spacing: 5) {
            HStack {
                Text(title).font(NativeTheme.font(13, relativeTo: .caption))
                Spacer(minLength: 8)
                caption(memberSize(used) + " / " + memberSize(quota))
            }
            NativeThinProgress(value: Double(max(0, used)), total: Double(max(0, quota)), height: 4)
            if let footnote { caption(footnote) }
        }
    }

    @ViewBuilder private var customStorageSections: some View {
        Section("连接信息") {
            if form.provider == 1 {
                field("服务地址", text: $form.webDavEndpoint)
                field("用户名", text: $form.webDavUsername)
                secret("密码", text: $form.webDavPassword)
            } else {
                field("Endpoint（AWS 可留空）", text: $form.endpoint)
                field("Bucket", text: $form.bucket)
                field("Region", text: $form.region)
                field("Access Key", text: $form.accessKey)
                secret("Secret Key", text: $form.secretKey)
            }
            field("目录前缀", text: $form.prefix)
        }
        Section {
            DisclosureGroup("高级选项", isExpanded: $advanced) {
                if form.provider == 0 { Toggle("Path Style", isOn: $form.pathStyle).tint(NativeTheme.systemSwitchTint) }
                Toggle("同步时下载书籍文件", isOn: $form.downloadBookFilesOnSync).tint(NativeTheme.systemSwitchTint)
                Toggle("跳过 TLS 验证", isOn: $form.skipTlsVerify).tint(NativeTheme.systemSwitchTint)
            }
            syncKey
            Button("导入连接参数") { run("import") }
            Button("导出连接参数") { run("export") }
        }
        Section {
            caption(summary)
            Button("立即同步") { run("run") }.accessibilityIdentifier("sync.run")
        }
    }

    @ViewBuilder private var syncKey: some View {
        // This user-owned key must not be offered to password AutoFill.
        NativeTextField(title: "同步密钥（账号云同步与备份必填）", text: $form.encryptionKey, secure: true)
            .textContentType(.oneTimeCode).textInputAutocapitalization(.never).disableAutocorrection(true)
        caption("密钥由你自行牢记，不会上传或写入备份。遗忘后无法解密已有数据，设置新密钥也无法恢复旧数据。")
    }

    private func caption(_ text: String, color: Color = NativeTheme.secondary) -> some View {
        Text(text).font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(color)
    }
    private func field(_ title: String, text: Binding<String>) -> some View {
        NativeTextField(title: title, text: text).textInputAutocapitalization(.never).disableAutocorrection(true)
    }
    private func secret(_ title: String, text: Binding<String>) -> some View {
        NativeTextField(title: title, text: text, secure: true).textInputAutocapitalization(.never).disableAutocorrection(true)
    }
    private func loadSnapshot() async throws -> SyncForm {
        let value: SyncFormSnapshot = try await store.request("settings.sync.get")
        summary = value.summary; savedProvider = value.settings.provider
        return value.settings
    }
    private func load() async {
        defer { loading = false }
        do {
            let saved = try await loadSnapshot()
            // This reruns on return from the account page; keep unsaved edits.
            if !formLoaded { form = saved; formLoaded = true }
            let account: AccountStatus = try await store.request("settings.account.get"); store.account = account
            await applyAccountCloudSyncDefault(saved: saved)
        }
        catch { if !Task.isCancelled { status = error.localizedDescription } }
    }
    /// A Plus account opens on Kkindle cloud sync until the user saves a
    /// provider of their own; that saved choice always keeps its interface.
    private func applyAccountCloudSyncDefault(saved: SyncForm) async {
        guard !providerTouched, form.provider != 2, !saved.providerChosenByUser,
              let account = store.account, account.isLoggedIn, !account.requiresDataRestart else { return }
        guard let member: DeviceFeatureStatus = try? await store.request("settings.membership.get"),
              member.canUseAccountCloudSync == true, !Task.isCancelled else { return }
        form.provider = 2
    }
    private func loadMembership() async {
        let key = memberKey
        member = nil; memberMessage = ""
        guard accountMode && signedIn else { return }
        do {
            let value: DeviceFeatureStatus = try await store.request("settings.membership.get")
            guard !Task.isCancelled && key == memberKey else { return }
            member = value
        } catch { if !Task.isCancelled && key == memberKey { memberMessage = error.localizedDescription } }
    }
    private func run(_ operation: String) {
        busy = true; status = operation == "run" ? "正在同步…" : operation == "test" ? "正在测试连接…" : ""
        actionTask = Task {
            defer { busy = false }
            do {
                let args = ["settings": try requestObject(form)]
                if operation == "import" {
                    let imported: SyncForm = try await store.request("settings.sync.import", args)
                    form = imported; status = "参数已导入，请检查后点击保存。同步加密密钥保持本机原值。"
                } else if operation == "test" || operation == "run" {
                    let result: MessageResult = try await store.request("settings.sync.\(operation)", args)
                    status = result.message + (result.warning.map { " " + $0 } ?? "")
                    if operation == "run" { form.enabled = true; try await store.refreshLibrary(); _ = try? await loadSnapshot() }
                } else {
                    try await store.command("settings.sync.\(operation)", args)
                    if operation == "save" {
                        status = "已保存"; _ = try? await loadSnapshot()
                        if store.preferences.showBookSyncStatus { try await store.refreshLibrary() }
                    }
                }
            } catch { if !Task.isCancelled { status = error.localizedDescription } }
        }
    }
}

struct NativeAiSettings: View {
    @ObservedObject var store: NativeStore
    @State private var form = AiForm()
    @State private var busy = false
    @State private var loading = true
    @State private var status = ""
    @State private var providerTask: Task<Void, Never>?

    var body: some View {
        NativeForm {
            Section {
                Picker("服务", selection: $form.provider) { Text("DeepSeek").tag("deepseek"); Text("OpenAI").tag("openai"); Text("自定义").tag("custom") }
                    .pickerStyle(.segmented)
                NativeTextField(title: "接口地址", text: $form.baseUrl).textInputAutocapitalization(.never).disableAutocorrection(true)
                NativeTextField(title: "模型", text: $form.model).textInputAutocapitalization(.never).disableAutocorrection(true)
                NativeTextField(title: "API Key", text: $form.apiKey, secure: true).textInputAutocapitalization(.never).disableAutocorrection(true)
            }
            Section {
                Button("测试连接") { run(test: true) }
                if busy { ProgressView() }
                if !status.isEmpty { Text(status).foregroundStyle(NativeTheme.secondary).textSelection(.enabled) }
            }
        }.disabled(loading || busy)
            .navigationTitle("AI 接口").navigationBarTitleDisplayMode(.inline).navigationBarHidden(false).modifier(NativeKeyboardDone()).modifier(NativeHideTabBar())
            .nativeBackButton()
            .toolbar { Button("保存") { run(test: false) }.disabled(busy || loading) }
            .task {
                defer { loading = false }
                do { form = try await store.request("settings.ai.get") }
                catch { if !Task.isCancelled { status = error.localizedDescription } }
            }
            .onChange(of: form.provider) { provider in
                guard !loading else { return }
                providerTask?.cancel()
                providerTask = Task {
                    do {
                        let defaults: AiDefaults = try await store.request("settings.ai.defaults", ["provider": provider])
                        guard !Task.isCancelled, form.provider == provider else { return }
                        form.baseUrl = defaults.baseUrl; form.model = defaults.model
                    } catch { }
                }
            }
            .onDisappear { providerTask?.cancel() }
    }
    private func run(test: Bool) {
        busy = true; status = test ? "正在测试连接…" : ""
        store.perform {
            defer { busy = false }
            let args = ["settings": try requestObject(form)]
            if test { let value: MessageResult = try await store.request("settings.ai.test", args); status = value.message }
            else { try await store.command("settings.ai.save", args); status = "已保存" }
        }
    }
}

struct NativeEmailSettings: View {
    @ObservedObject var store: NativeStore
    @State private var form = EmailForm()
    @State private var busy = false
    @State private var loading = true
    @State private var status = ""
    var body: some View {
        NativeForm {
            Section("收件人") { NativeTextField(title: "Kindle 邮箱", text: $form.kindleEmailAddress).keyboardType(.emailAddress) }
            Section("发件邮箱") {
                NativeTextField(title: "发件人邮箱", text: $form.senderEmailAddress).keyboardType(.emailAddress)
                NativeTextField(title: "SMTP 服务器", text: $form.smtpHost)
                HStack { Text("端口"); TextField("587", value: $form.smtpPort, formatter: NumberFormatter()).keyboardType(.numberPad).multilineTextAlignment(.trailing) }
                NativeTextField(title: "SMTP 用户名", text: $form.smtpUsername)
                NativeTextField(title: "密码或应用专用密码", text: $form.smtpPassword, secure: true)
                Toggle("使用 SSL / TLS", isOn: $form.enableSsl).tint(NativeTheme.systemSwitchTint)
            }
            if !status.isEmpty { Section { Text(status).foregroundStyle(NativeTheme.secondary) } }
        }.textInputAutocapitalization(.never).disableAutocorrection(true).disabled(loading || busy)
            .navigationTitle("发送到 Kindle").navigationBarTitleDisplayMode(.inline).navigationBarHidden(false).modifier(NativeKeyboardDone()).modifier(NativeHideTabBar())
            .nativeBackButton()
            .toolbar { Button("保存") {
                busy = true
                store.perform { defer { busy = false }; try await store.command("settings.kindle.save", ["settings": try requestObject(form)]); status = "已保存" }
            }.disabled(loading || busy) }
            .task {
                defer { loading = false }
                do { form = try await store.request("settings.kindle.get") }
                catch { if !Task.isCancelled { status = error.localizedDescription } }
            }
    }
}

struct NativeTrash: View {
    @ObservedObject var store: NativeStore
    @State private var items: [TrashRecord] = []
    @State private var busy = false
    var body: some View {
        List {
            Group {
                if items.isEmpty { Text("回收站是空的").foregroundStyle(NativeTheme.secondary) }
                ForEach(items) { item in
                    VStack(alignment: .leading, spacing: 10) {
                        Text(item.title).font(NativeTheme.font(17, relativeTo: .headline))
                        Text(item.detail).font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.secondary)
                        HStack {
                            Button("恢复") { action("restore", id: item.id) }.buttonStyle(.bordered)
                            Button("永久删除", role: .destructive) { action("purge", id: item.id) }.buttonStyle(.bordered)
                        }
                    }.padding(.vertical, 8)
                }
            }.listRowBackground(store.preferences.colorfulMode ? Color.clear : NativeTheme.page)
        }.modifier(NativeListBackground()).disabled(busy).navigationTitle("回收站").navigationBarTitleDisplayMode(.inline).navigationBarHidden(false)
            .nativeBackButton()
            .task { do { items = try await store.request("trash.get") } catch { if !Task.isCancelled { store.failure = error.localizedDescription } } }
    }
    private func action(_ operation: String, id: String) {
        busy = true
        store.perform {
            defer { busy = false }
            try await store.command("trash.\(operation)", ["id": id])
            items = try await store.request("trash.get"); try await store.refreshLibrary()
        }
    }
}
