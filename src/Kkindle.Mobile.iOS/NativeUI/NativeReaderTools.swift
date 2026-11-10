import SwiftUI

struct NativeDictionary: View {
    @ObservedObject var store: NativeStore
    @State private var term: String
    @State private var entries: [DictionaryEntry] = []
    @State private var dictionaries: [DictionaryRecord] = []
    @State private var busy = false
    @State private var status = ""
    init(store: NativeStore, initialTerm: String) { self.store = store; _term = State(initialValue: initialTerm) }
    var body: some View {
        NativeForm {
            Section {
                HStack { TextField("查询词语", text: $term).onSubmit { lookup() }; Button("查询") { lookup() }.buttonStyle(.borderless) }
                if busy { ProgressView() }
                if !status.isEmpty { Text(status).font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.secondary) }
            }
            ForEach(Array(entries.enumerated()), id: \.offset) { _, entry in
                Section(entry.term + " · " + entry.dictionaryName) { Text(entry.definition).textSelection(.enabled) }
            }
            Section("词典管理") {
                ForEach(dictionaries) { dictionary in
                    VStack(alignment: .leading, spacing: 8) {
                        Toggle(dictionary.name + " · \(dictionary.entryCount) 条", isOn: Binding(get: { dictionary.enabled }, set: { enabled in action("toggle", ["id": dictionary.id, "enabled": enabled]) })).tint(NativeTheme.systemSwitchTint)
                        Button("移除", role: .destructive) { action("remove", ["id": dictionary.id]) }.buttonStyle(.borderless)
                    }
                }
                if dictionaries.isEmpty { Text("尚未导入词典").foregroundStyle(NativeTheme.secondary) }
                Button("导入词典") { action("import") }
            }
        }.disabled(busy).navigationTitle("本地词典").navigationBarTitleDisplayMode(.inline).navigationBarHidden(false).modifier(NativeKeyboardDone()).modifier(NativeHideTabBar())
            .task { do { dictionaries = try await store.request("dictionary.get"); if !term.isEmpty { entries = try await store.request("dictionary.lookup", ["term": term]) } } catch { if !Task.isCancelled { status = error.localizedDescription } } }
    }
    private func lookup() {
        guard !term.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return }; busy = true
        store.perform { defer { busy = false }; entries = try await store.request("dictionary.lookup", ["term": term]); status = entries.isEmpty ? "没有找到释义，请检查词典是否已导入并启用。" : "找到 \(entries.count) 条释义" }
    }
    private func action(_ command: String, _ args: [String: Any] = [:]) {
        busy = true
        store.perform { defer { busy = false }; try await store.command("dictionary.\(command)", args); dictionaries = try await store.request("dictionary.get"); if !term.isEmpty { entries = try await store.request("dictionary.lookup", ["term": term]) } }
    }
}

private struct ConversationTurn: Codable { let role: String; var content: String }

struct NativeAssistant: View {
    @ObservedObject var store: NativeStore
    @ObservedObject var session: NativeReaderSession
    let book: LibraryBook
    let selection: String
    @State private var question = ""
    @State private var history: [ConversationTurn] = []
    @State private var busy = false
    @State private var sessionId = UUID().uuidString
    @State private var requestTask: Task<Void, Never>?
    @State private var status = ""
    var body: some View {
        VStack(spacing: 0) {
            ScrollViewReader { proxy in
                ScrollView {
                    VStack(alignment: .leading, spacing: 22) {
                        if history.isEmpty { Text("可以概括当前页、解释所选文字，或结合全书关键词检索回答问题。").foregroundStyle(NativeTheme.secondary) }
                        ForEach(Array(history.enumerated()), id: \.offset) { index, turn in
                            VStack(alignment: .leading, spacing: 8) {
                                Text(turn.role == "user" ? "你" : "阅读助手").font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.secondary)
                                Text(busy && index == history.count - 1 ? session.aiStreams[sessionId] ?? turn.content : turn.content).textSelection(.enabled)
                            }.frame(maxWidth: .infinity, alignment: .leading).id(index)
                        }
                        if !status.isEmpty { Text(status).font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.secondary) }
                    }.padding(24)
                }.onChange(of: session.aiStreams[sessionId]) { _ in if !history.isEmpty { proxy.scrollTo(history.count - 1, anchor: .bottom) } }
            }
            Divider()
            HStack {
                Button("概括本页") { ask("用几句话概括这一页的要点。", pageOnly: true) }
                if !selection.isEmpty { Button("解释原文") { ask("解释这段话：「\(selection)」", pageOnly: true) } }
                Spacer()
            }.font(NativeTheme.font(12, relativeTo: .caption)).padding(.horizontal, 20).padding(.top, 14).disabled(busy)
            HStack(spacing: 12) {
                TextField("输入问题", text: $question).textFieldStyle(.roundedBorder).onSubmit { send() }
                if busy { Button("停止") { requestTask?.cancel() } } else { Button("发送") { send() }.disabled(question.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty) }
            }.padding(20)
        }.modifier(NativeKeyboardDone()).onDisappear { requestTask?.cancel(); session.aiStreams.removeValue(forKey: sessionId) }
    }
    private func send() { let value = question.trimmingCharacters(in: .whitespacesAndNewlines); guard !value.isEmpty else { return }; question = ""; ask(value, pageOnly: false) }
    private func ask(_ question: String, pageOnly: Bool) {
        guard !busy else { return }; busy = true; status = pageOnly ? "本次使用当前页。" : "正在进行全书关键词检索…"
        let previous = history; history.append(ConversationTurn(role: "user", content: question)); history.append(ConversationTurn(role: "assistant", content: "正在思考…"))
        session.aiStreams[sessionId] = "正在思考…"
        requestTask = Task {
            defer { busy = false }
            do {
                let answer: TextResult = try await store.request("reader.ai", ["bookId": book.id, "question": question, "history": try requestObject(previous), "pageOnly": pageOnly, "sessionId": sessionId])
                history[history.count - 1].content = answer.text; status = ""
            } catch { if !Task.isCancelled { history[history.count - 1].content = error.localizedDescription } else { history[history.count - 1].content = session.aiStreams[sessionId] ?? "已停止" } }
        }
    }
}

struct NativeTxtReview: View {
    @ObservedObject var store: NativeStore
    @Environment(\.dismiss) private var dismiss
    let review: TxtReview
    @State private var rules = TxtRules()
    @State private var page = 0
    @State private var preview: TxtPreview?
    @State private var error: String?
    @State private var busy = false
    @State private var finished = false
    private var revision: String { "\(rules.encoding):\(rules.headingPreset):\(rules.customHeadingPattern):\(rules.recognizeVolumes):\(page)" }
    var body: some View {
        NavigationView {
            NativeForm {
                Section { Text(review.name).font(NativeTheme.font(17, relativeTo: .headline))
                    Picker("编码", selection: $rules.encoding) { Text("自动识别").tag(0); Text("UTF-8").tag(1); Text("UTF-16 LE").tag(2); Text("UTF-16 BE").tag(3); Text("GB18030").tag(4) }
                    Picker("章节规则", selection: $rules.headingPreset) { Text("自动识别").tag(0); Text("中文章节").tag(1); Text("英文章节").tag(2); Text("序言和尾声").tag(3); Text("不识别章节").tag(4); Text("自定义正则").tag(5) }
                    if rules.headingPreset == 5 { TextField("章节正则", text: $rules.customHeadingPattern).textInputAutocapitalization(.never).disableAutocorrection(true) }
                    Toggle("识别分卷", isOn: $rules.recognizeVolumes).tint(NativeTheme.systemSwitchTint)
                }
                Section("目录预览") {
                    if let preview {
                        Text("\(preview.chapterCount) 个目录项 · \(preview.detectedEncoding)").font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.secondary)
                        ForEach(preview.chapters) { chapter in Text((chapter.isVolume ? "卷 · " : "") + chapter.title) }
                        HStack { Button("上一页") { page -= 1 }.disabled(!preview.hasPreviousPage); Spacer(); Button("下一页") { page += 1 }.disabled(!preview.hasNextPage) }.buttonStyle(.borderless)
                    } else if let error { Text(error).foregroundStyle(NativeTheme.secondary) } else { ProgressView("正在识别章节…") }
                }
            }.navigationTitle("TXT 导入").navigationBarTitleDisplayMode(.inline).navigationBarHidden(false).disabled(busy)
                .toolbar {
                    ToolbarItem(placement: .cancellationAction) { Button("取消") { dismiss() }.disabled(busy) }
                    ToolbarItem(placement: .confirmationAction) { Button("导入") {
                        busy = true
                        store.perform { defer { busy = false }; try await store.command("txt.complete", ["id": review.id, "rules": try requestObject(rules)]); finished = true; store.txtReview = nil; dismiss() }
                    }.disabled(busy || preview == nil) }
                }
                .task(id: revision) {
                    preview = nil; error = nil
                    do { let value: TxtPreview = try await store.request("txt.preview", ["id": review.id, "rules": try requestObject(rules), "page": page]); if !Task.isCancelled { preview = value } }
                    catch { if !Task.isCancelled { self.error = error.localizedDescription } }
                }
                .onChange(of: rules) { _ in page = 0 }
                .onDisappear { if !finished { store.perform { try await store.command("txt.cancel", ["id": review.id]) } } }
        }.navigationViewStyle(.stack).interactiveDismissDisabled(busy)
    }
}
