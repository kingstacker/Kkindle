import SwiftUI

struct NativeAccountPage: View {
    @ObservedObject var store: NativeStore
    @State private var serverUrl = ""
    @State private var username = ""
    @State private var email = ""
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
                    if let expiry = account.expiresAt { Text("会话有效至 \(expiry.prefix(10))").font(NativeTheme.font(12, relativeTo: .caption)) }
                }
                NativeMembershipSections(store: store)
                actionSection
            } else {
                Section {
                    Picker("账号操作", selection: $register) { Text("登录").tag(false); Text("注册").tag(true) }
                        .pickerStyle(.segmented).accessibilityIdentifier("account.mode")
                    Text(register ? "注册后可在多台设备同步书籍和阅读进度。" : "使用用户名或邮箱登录同一账号。")
                        .font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.secondary)
                }
                Section(register ? "注册信息" : "登录信息") {
                    if register {
                        NativeTextField(title: "邮箱", text: $email).textContentType(.emailAddress).keyboardType(.emailAddress)
                            .accessibilityIdentifier("account.emailField")
                        Text("注册邮箱不可修改。").font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.secondary)
                    }
                    NativeTextField(title: register ? "用户名" : "用户名或邮箱", text: $username).textContentType(.username)
                        .accessibilityIdentifier("account.usernameField")
                    if register {
                        Text("3–32 位字母、数字、点、下划线或连字符").font(NativeTheme.font(12, relativeTo: .caption))
                            .foregroundStyle(NativeTheme.secondary)
                    }
                    if register {
                        NativeTextField(title: "登录密码（8–128 位）", text: $password, secure: true)
                            .textContentType(.newPassword).accessibilityIdentifier("account.password")
                        NativeTextField(title: "确认登录密码", text: $confirmation, secure: true)
                            .textContentType(.newPassword).accessibilityIdentifier("account.confirmation")
                    } else {
                        NativeTextField(title: "登录密码（8–128 位）", text: $password, secure: true)
                            .textContentType(.password).accessibilityIdentifier("account.password")
                    }
                }
                actionSection
            }
        }.textInputAutocapitalization(.never).disableAutocorrection(true).disabled(loading || busy)
            .id(store.account?.isLoggedIn == true ? "account.signed-in" : "account.signed-out")
            .navigationTitle("用户账号").navigationBarTitleDisplayMode(.inline).navigationBarHidden(false)
            .nativeBackButton()
            .modifier(NativeKeyboardDone())
            .modifier(NativeHideTabBar())
            .task { await load() }
            .onChange(of: register) { _ in clearPasswords(); status = "" }
            .onDisappear { actionTask?.cancel(); clearPasswords() }
    }

    /// Form actions stay in the scrolling list so they do not jump above the
    /// keyboard or hover over the fields while the page moves.
    @ViewBuilder private var actionSection: some View {
        Section {
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
            }
        }
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
        var args: [String: Any] = ["serverUrl": serverUrl, "username": username, "password": password,
                                   "region": store.account?.region ?? 0]
        if register { args["email"] = email; args["confirmPassword"] = confirmation }
        let command = register ? "settings.account.register" : "settings.account.login"
        actionTask = Task {
            defer { busy = false; clearPasswords() }
            do {
                let account: AccountStatus = try await store.request(command, args)
                guard !Task.isCancelled else { return }
                store.account = account; status = ""
            } catch { if !Task.isCancelled { status = error.localizedDescription } }
        }
    }

    private func selectRegion(_ region: Int) {
        guard !loading && !busy && region != store.account?.region else { return }
        busy = true; clearPasswords(); username = ""; email = ""; status = ""
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
