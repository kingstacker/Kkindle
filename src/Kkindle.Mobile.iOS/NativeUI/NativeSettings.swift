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
                Text("我的").font(NativeTheme.font(26, relativeTo: .largeTitle)).accessibilityIdentifier("page.profile")
                    .padding(.bottom, 16)
                NavigationLink { NativeSyncSettings(store: store) } label: { NativeSettingsRow(title: "云同步", value: summary?.sync ?? "") }
                    .accessibilityIdentifier("profile.sync")
                NavigationLink { NativeAiSettings(store: store) } label: { NativeSettingsRow(title: "AI 助手", value: summary?.ai ?? "") }
                NavigationLink { NativeEmailSettings(store: store) } label: { NativeSettingsRow(title: "发送到 Kindle", value: summary?.kindle ?? "") }

                sectionLabel("外观", top: 28).padding(.bottom, 10)
                Picker("主题", selection: Binding(get: { store.preferences.themeMode }, set: { mode in
                    var value = store.preferences; value.themeMode = mode
                    store.setPreferences(value)
                })) {
                    Text("跟随系统").tag(0); Text("浅色").tag(1); Text("深色").tag(2)
                }.pickerStyle(.segmented).accessibilityIdentifier("profile.theme")

                sectionLabel("数据", top: 32).padding(.bottom, 4)
                NativeSettingsRow(title: "书籍占用", value: summary?.storage ?? "—", chevron: false)
                Button { runBackup(false) } label: { NativeSettingsRow(title: "导出备份") }.disabled(busy)
                Button { runBackup(true) } label: { NativeSettingsRow(title: "恢复备份") }.disabled(busy)
                NavigationLink { NativeTrash(store: store) } label: { NativeSettingsRow(title: "回收站") }
                NavigationLink { NativeDictionary(store: store, initialTerm: "") } label: { NativeSettingsRow(title: "本地词典") }

                sectionLabel("关于", top: 24).padding(.bottom, 4)
                Link(destination: URL(string: "https://github.com/kingstacker")!) { NativeSettingsRow(title: "开发者", value: "kingstacker") }
                Button { UIPasteboard.general.string = "1109898894"; store.showToast("群号已复制到剪贴板") } label: {
                    NativeSettingsRow(title: "QQ 交流群", value: "1109898894", chevron: false)
                }
                Link(destination: URL(string: "https://github.com/kingstacker/Kkindle/issues")!) { NativeSettingsRow(title: "反馈问题") }
                Button { tapVersion() } label: { NativeSettingsRow(title: "版本", value: summary?.version ?? "", chevron: false) }
            }.padding(.horizontal, 24).padding(.top, 20).padding(.bottom, 32).buttonStyle(.plain)
        }.background(NativeTheme.page).navigationBarHidden(true)
            .sheet(isPresented: $developer) {
                NativeNameSheet(store: store, title: "开发者模式", initial: "", button: "进入", isSecure: true) { password in
                    try await store.command("settings.developer.toggle", ["password": password])
                }
            }
            .task {
                do { summary = try await store.request("settings.profile") }
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

struct NativeSyncSettings: View {
    @ObservedObject var store: NativeStore
    @State private var form = SyncForm()
    @State private var summary = ""
    @State private var status = ""
    @State private var busy = false
    @State private var loading = true
    @State private var advanced = false
    @State private var actionTask: Task<Void, Never>?

    var body: some View {
        NativeForm {
            Section {
                Picker("同步方式", selection: $form.provider) { Text("S3").tag(0); Text("WebDAV").tag(1) }.pickerStyle(.segmented)
                Toggle("启用同步", isOn: $form.enabled)
                Toggle("自动同步", isOn: $form.automaticSyncEnabled)
            }
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
                    if form.provider == 0 { Toggle("Path Style", isOn: $form.pathStyle) }
                    Toggle("同步时下载书籍文件", isOn: $form.downloadBookFilesOnSync)
                    Toggle("跳过 TLS 验证", isOn: $form.skipTlsVerify)
                    secret("同步加密密钥", text: $form.encryptionKey)
                }
                Button("导入连接参数") { run("import") }
                Button("导出连接参数") { run("export") }
            }
            Section {
                Text(summary).font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.secondary)
                Button("立即同步") { run("run") }.accessibilityIdentifier("sync.run")
            }
        }.disabled(busy || loading)
            .navigationTitle("同步").navigationBarTitleDisplayMode(.inline).navigationBarHidden(false)
            .modifier(NativeKeyboardDone())
            .safeAreaInset(edge: .bottom, spacing: 0) {
                VStack(alignment: .leading, spacing: 10) {
                    if !status.isEmpty {
                        Text(status).font(NativeTheme.font(13)).foregroundStyle(NativeTheme.muted)
                            .textSelection(.enabled).accessibilityIdentifier("sync.status")
                    }
                    if busy { ProgressView() }
                    HStack(spacing: 12) {
                        Button("测试连接") { run("test") }
                        Button("保存") { run("save") }.accessibilityIdentifier("sync.save")
                    }.buttonStyle(NativePrimaryButtonStyle()).disabled(busy || loading)
                }.padding(.horizontal, 24).padding(.top, 8).padding(.bottom, 14).background(NativeTheme.page)
            }
            .modifier(NativeHideTabBar())
            .task { await load() }
            .onChange(of: store.syncStatus) { status = $0 }
            .onDisappear { actionTask?.cancel() }
    }
    private func field(_ title: String, text: Binding<String>) -> some View {
        NativeTextField(title: title, text: text).textInputAutocapitalization(.never).disableAutocorrection(true)
    }
    private func secret(_ title: String, text: Binding<String>) -> some View {
        NativeTextField(title: title, text: text, secure: true).textInputAutocapitalization(.never).disableAutocorrection(true)
    }
    private func load() async {
        defer { loading = false }
        do { let value: SyncFormSnapshot = try await store.request("settings.sync.get"); form = value.settings; summary = value.summary }
        catch { if !Task.isCancelled { status = error.localizedDescription } }
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
                    if operation == "run" { form.enabled = true; try await store.refreshLibrary() }
                } else {
                    try await store.command("settings.sync.\(operation)", args)
                    if operation == "save" { status = "已保存" }
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
                Toggle("使用 SSL / TLS", isOn: $form.enableSsl)
            }
            if !status.isEmpty { Section { Text(status).foregroundStyle(NativeTheme.secondary) } }
        }.textInputAutocapitalization(.never).disableAutocorrection(true).disabled(loading || busy)
            .navigationTitle("发送到 Kindle").navigationBarTitleDisplayMode(.inline).navigationBarHidden(false).modifier(NativeKeyboardDone()).modifier(NativeHideTabBar())
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
        }.disabled(busy).navigationTitle("回收站").navigationBarTitleDisplayMode(.inline).navigationBarHidden(false)
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
