import SwiftUI

struct NativeAccountPage: View {
    @ObservedObject var store: NativeStore
    @State private var serverUrl = ""
    @State private var username = ""
    @State private var password = ""
    @State private var confirmation = ""
    @State private var register = false
    @State private var loading = true
    @State private var busy = false
    @State private var status = ""
    @State private var actionTask: Task<Void, Never>?

    var body: some View {
        NativeForm {
            Section("服务器区域") {
                Picker("服务器区域", selection: Binding(get: { store.account?.region ?? 0 }, set: { selectRegion($0) })) {
                    Text("中国区").tag(0); Text("国际区").tag(1)
                }.pickerStyle(.segmented).accessibilityIdentifier("account.region")
                Text("两区账号和同步数据独立，不互通。")
                    .font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.secondary)
                if store.account?.requiresDataRestart == true {
                    Text("区域已切换，请重新打开应用后使用该区域的独立书库和同步。")
                        .font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.secondary)
                        .accessibilityIdentifier("account.restart")
                }
            }
            if let account = store.account, account.isLoggedIn {
                Section("已登录") {
                    Text(account.user?.username ?? "").font(NativeTheme.font(21, relativeTo: .title2))
                        .accessibilityIdentifier("account.username")
                    Text(account.serverUrl).font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.secondary)
                    if let expiry = account.expiresAt { Text("会话有效至 \(expiry.prefix(10))").font(NativeTheme.font(12, relativeTo: .caption)) }
                }
            } else {
                Section {
                    Picker("账号操作", selection: $register) { Text("登录").tag(false); Text("注册").tag(true) }
                        .pickerStyle(.segmented).accessibilityIdentifier("account.mode")
                }
                Section("账号服务") {
                    NativeTextField(title: "服务地址", text: $serverUrl).keyboardType(.URL)
                        .accessibilityIdentifier("account.server")
                }
                Section {
                    NativeTextField(title: "用户名", text: $username).textContentType(.username)
                        .accessibilityIdentifier("account.usernameField")
                    Text("3–32 位字母、数字、点、下划线或连字符").font(NativeTheme.font(12, relativeTo: .caption))
                        .foregroundStyle(NativeTheme.secondary)
                    if register {
                        NativeTextField(title: "密码（8–128 位）", text: $password, secure: true)
                            .textContentType(.newPassword).accessibilityIdentifier("account.password")
                        NativeTextField(title: "确认密码", text: $confirmation, secure: true)
                            .textContentType(.newPassword).accessibilityIdentifier("account.confirmation")
                    } else {
                        NativeTextField(title: "密码（8–128 位）", text: $password, secure: true)
                            .textContentType(.password).accessibilityIdentifier("account.password")
                    }
                }
            }
        }.textInputAutocapitalization(.never).disableAutocorrection(true).disabled(loading || busy)
            .navigationTitle("用户账号").navigationBarTitleDisplayMode(.inline).navigationBarHidden(false)
            .modifier(NativeKeyboardDone())
            .safeAreaInset(edge: .bottom, spacing: 0) {
                VStack(alignment: .leading, spacing: 10) {
                    if !status.isEmpty {
                        Text(status).font(NativeTheme.font(13)).foregroundStyle(NativeTheme.secondary)
                            .accessibilityIdentifier("account.status")
                    }
                    if loading || busy { ProgressView() }
                    if store.account?.isLoggedIn == true {
                        Button("退出登录") { signOut() }.buttonStyle(NativePrimaryButtonStyle()).accessibilityIdentifier("account.logout")
                    } else {
                        Button(register ? "注册并登录" : "登录") { signIn() }.buttonStyle(NativePrimaryButtonStyle())
                            .accessibilityIdentifier("account.submit")
                    }
                }.disabled(loading || busy).padding(.horizontal, 24).padding(.top, 8).padding(.bottom, 14)
                    .background(NativeTheme.page)
            }
            .modifier(NativeHideTabBar())
            .task { await load() }
            .onChange(of: register) { _ in clearPasswords(); status = "" }
            .onDisappear { actionTask?.cancel(); clearPasswords() }
    }

    private func load() async {
        defer { loading = false }
        do {
            let account: AccountStatus = try await store.request("settings.account.get", ["validateSession": true])
            guard !Task.isCancelled else { return }
            store.account = account; serverUrl = account.serverUrl
        } catch {
            guard !Task.isCancelled else { return }
            if let account: AccountStatus = try? await store.request("settings.account.get") {
                store.account = account; serverUrl = account.serverUrl
            }
            status = error.localizedDescription
        }
    }

    private func signIn() {
        guard !busy else { return }
        if register && password != confirmation { status = "两次输入的密码不一致。"; return }
        busy = true; status = register ? "正在注册…" : "正在登录…"
        let args: [String: Any] = ["serverUrl": serverUrl, "username": username, "password": password,
                                   "region": store.account?.region ?? 0]
        let command = register ? "settings.account.register" : "settings.account.login"
        actionTask = Task {
            defer { busy = false; clearPasswords() }
            do {
                let account: AccountStatus = try await store.request(command, args)
                guard !Task.isCancelled else { return }
                store.account = account; status = "登录成功。"
            } catch { if !Task.isCancelled { status = error.localizedDescription } }
        }
    }

    private func selectRegion(_ region: Int) {
        guard !loading && !busy && region != store.account?.region else { return }
        busy = true; clearPasswords(); username = ""; status = ""
        actionTask = Task {
            defer { busy = false }
            do {
                let account: AccountStatus = try await store.request("settings.account.select", ["region": region])
                guard !Task.isCancelled else { return }
                store.account = account; serverUrl = account.serverUrl
                let validated: AccountStatus = try await store.request("settings.account.get", ["validateSession": true])
                guard !Task.isCancelled else { return }
                store.account = validated
            } catch { if !Task.isCancelled { status = error.localizedDescription } }
        }
    }

    private func signOut() {
        guard !busy else { return }
        busy = true; status = "正在退出…"
        actionTask = Task {
            defer { busy = false }
            do {
                let result: MessageResult = try await store.request("settings.account.logout")
                let account: AccountStatus = try await store.request("settings.account.get")
                guard !Task.isCancelled else { return }
                store.account = account; serverUrl = account.serverUrl; status = result.message
            } catch { if !Task.isCancelled { status = error.localizedDescription } }
        }
    }

    private func clearPasswords() { password = ""; confirmation = "" }
}
