import SwiftUI

struct NativeAccountPage: View {
    @ObservedObject var store: NativeStore
    @State private var serverUrl = ""
    @State private var username = ""
    @State private var email = ""
    @State private var password = ""
    @State private var confirmation = ""
    @State private var register = false
    @State private var resetMode = false
    @State private var resetCode = ""
    @State private var resetPassword = ""
    @State private var resetConfirmation = ""
    @State private var emailCode = ""
    @State private var emailCodeSent = false
    @State private var loading = true
    @State private var busy = false
    @State private var status = ""
    @State private var actionTask: Task<Void, Never>?

    var body: some View {
        NativeForm {
            if let account = store.account, account.isLoggedIn {
                restartNotice
                emailVerificationSection
                NativeMembershipSections(store: store, onSignOut: { signOut() },
                                         signingOut: busy, accountStatus: status)
            } else {
                Section {
                    Picker("账号操作", selection: $register) { Text("登录").tag(false); Text("注册").tag(true) }
                        .pickerStyle(.segmented).accessibilityIdentifier("account.mode")
                        .disabled(resetMode)
                    Text(resetMode ? "输入发送到注册邮箱的验证码，然后设置新密码。"
                        : register ? "注册后可在多台设备同步书籍和阅读进度。" : "使用用户名或邮箱登录同一账号。")
                        .font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.secondary)
                }
                Section(resetMode ? "重置密码" : register ? "注册信息" : "登录信息") {
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
                    if resetMode {
                        NativeTextField(title: "邮箱验证码（8 位）", text: $resetCode)
                            .textContentType(.oneTimeCode).accessibilityIdentifier("account.reset.code")
                        Text("验证码 15 分钟内有效。").font(NativeTheme.font(12, relativeTo: .caption))
                            .foregroundStyle(NativeTheme.secondary)
                        NativeTextField(title: "新密码（8–128 位）", text: $resetPassword, secure: true)
                            .textContentType(.newPassword).accessibilityIdentifier("account.reset.password")
                        NativeTextField(title: "确认新密码", text: $resetConfirmation, secure: true)
                            .textContentType(.newPassword).accessibilityIdentifier("account.reset.confirmation")
                    } else if register {
                        NativeTextField(title: "登录密码（8–128 位）", text: $password, secure: true)
                            .textContentType(.newPassword).accessibilityIdentifier("account.password")
                        NativeTextField(title: "确认登录密码", text: $confirmation, secure: true)
                            .textContentType(.newPassword).accessibilityIdentifier("account.confirmation")
                    } else {
                        NativeTextField(title: "登录密码（8–128 位）", text: $password, secure: true)
                            .textContentType(.password).accessibilityIdentifier("account.password")
                    }
                }
                regionSection
                actionSection
            }
        }.textInputAutocapitalization(.never).disableAutocorrection(true).disabled(loading || busy)
            .id(store.account?.isLoggedIn == true ? "account.signed-in" : "account.signed-out")
            .navigationTitle("用户账号").navigationBarTitleDisplayMode(.inline).navigationBarHidden(false)
            .nativeBackButton()
            .modifier(NativeKeyboardDone())
            .modifier(NativeHideTabBar())
            .task { await load() }
            .onChange(of: register) { _ in resetMode = false; clearPasswords(); status = "" }
            .onDisappear { actionTask?.cancel(); clearPasswords() }
    }

    /// A region switch reloads the region's account automatically, so the
    /// restart warning must also stay visible on the signed-in page.
    @ViewBuilder private var restartNotice: some View {
        if store.account?.requiresDataRestart == true {
            Text("区域已切换，请重新打开应用后使用该区域的独立书库和同步。")
                .font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.warning)
                .accessibilityIdentifier("account.restart")
        }
    }

    /// Region belongs to the signed-out form only, right before the sign-in
    /// action; a signed-in page keeps the region chosen at login.
    @ViewBuilder private var regionSection: some View {
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
                if resetMode {
                    Button("重置密码") { submitReset() }.buttonStyle(NativePrimaryButtonStyle())
                        .accessibilityIdentifier("account.reset.submit")
                    Button("返回登录") { cancelReset() }.buttonStyle(.plain)
                        .accessibilityIdentifier("account.reset.cancel")
                } else {
                    Button(register ? "注册并登录" : "登录") { signIn() }.buttonStyle(NativePrimaryButtonStyle())
                        .accessibilityIdentifier("account.submit")
                    if !register {
                        Button("忘记密码？") { beginReset() }.buttonStyle(.plain)
                            .accessibilityIdentifier("account.forgot")
                    }
                }
            }
        }
    }

    /// The registered email can only be verified while signed in; recovery and
    /// verification share the same 8-digit, 15-minute code.
    @ViewBuilder private var emailVerificationSection: some View {
        Section("邮箱验证") {
            if let user = store.account?.user {
                if user.emailVerified == true {
                    Text("注册邮箱已验证。").font(NativeTheme.font(13)).foregroundStyle(NativeTheme.secondary)
                        .accessibilityIdentifier("account.email.verified")
                } else if user.email == nil {
                    Text("此账号未设置注册邮箱，无法验证或找回密码。")
                        .font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.secondary)
                        .accessibilityIdentifier("account.email.missing")
                } else if emailCodeSent {
                    Text("验证码已发送至注册邮箱（15 分钟内有效）。")
                        .font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.secondary)
                    NativeTextField(title: "邮箱验证码（8 位）", text: $emailCode)
                        .textContentType(.oneTimeCode).accessibilityIdentifier("account.email.code")
                    Button("确认验证") { verifyEmail() }.buttonStyle(.plain)
                        .accessibilityIdentifier("account.email.verify")
                } else {
                    Text("注册邮箱尚未验证。验证后可找回密码。")
                        .font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.secondary)
                    Button("发送验证码") { sendEmailCode() }.buttonStyle(.plain)
                        .accessibilityIdentifier("account.email.send")
                }
            }
        }
    }

    private func beginReset() {
        guard !busy, !register else { return }
        let identifier = username.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !identifier.isEmpty else { status = "请输入有效的用户名或邮箱。"; return }
        busy = true; status = "正在发送验证码…"
        actionTask = Task {
            defer { busy = false }
            do {
                let result: MessageResult = try await store.request("settings.account.reset.request",
                    ["serverUrl": serverUrl, "username": identifier, "region": store.account?.region ?? 0])
                guard !Task.isCancelled else { return }
                resetMode = true; resetCode = ""; resetPassword = ""; resetConfirmation = ""
                status = result.message
            } catch { if !Task.isCancelled { status = error.localizedDescription } }
        }
    }

    private func submitReset() {
        guard !busy else { return }
        if resetPassword != resetConfirmation { status = "两次输入的密码不一致。"; return }
        busy = true; status = "正在重置密码…"
        actionTask = Task {
            defer { busy = false }
            do {
                let result: MessageResult = try await store.request("settings.account.reset.confirm",
                    ["serverUrl": serverUrl, "username": username, "code": resetCode,
                     "newPassword": resetPassword, "confirmPassword": resetConfirmation])
                guard !Task.isCancelled else { return }
                resetMode = false; resetCode = ""; resetPassword = ""; resetConfirmation = ""
                password = ""; confirmation = ""; status = result.message
            } catch { if !Task.isCancelled { status = error.localizedDescription } }
        }
    }

    private func cancelReset() {
        resetMode = false; resetCode = ""; resetPassword = ""; resetConfirmation = ""
        status = "已返回登录。"
    }

    private func sendEmailCode() {
        guard !busy else { return }
        busy = true; status = "正在发送邮箱验证码…"
        actionTask = Task {
            defer { busy = false }
            do {
                let result: MessageResult = try await store.request("settings.account.email.request")
                guard !Task.isCancelled else { return }
                emailCodeSent = true; emailCode = ""; status = result.message
            } catch { if !Task.isCancelled { status = error.localizedDescription } }
        }
    }

    private func verifyEmail() {
        guard !busy else { return }
        busy = true; status = "正在验证邮箱…"
        actionTask = Task {
            defer { busy = false }
            do {
                let account: AccountStatus = try await store.request("settings.account.email.verify", ["code": emailCode])
                guard !Task.isCancelled else { return }
                store.account = account; emailCodeSent = false; emailCode = ""; status = "邮箱验证成功。"
            } catch { if !Task.isCancelled { status = error.localizedDescription } }
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
