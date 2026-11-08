import SwiftUI
import UIKit

private enum ReaderPanel: String, Identifiable {
    case toc, typography, speech, dictionary, assistant, note, annotation, translation, info
    var id: String { rawValue }
    var title: String {
        switch self {
        case .toc: return "目录"; case .typography: return "阅读设置"
        case .speech: return "朗读设置"; case .dictionary: return "本地词典"; case .assistant: return "AI 助手"
        case .note: return "笔记"; case .annotation: return "批注"; case .translation: return "翻译"; case .info: return "注释"
        }
    }
}

struct NativeReaderPage: View {
    @ObservedObject var store: NativeStore
    @ObservedObject var session: NativeReaderSession
    let book: LibraryBook
    @State private var panel: ReaderPanel?
    @State private var dialog: ReaderDialog?
    @State private var selectedText = ""
    @State private var seekProgress = 0.0
    @State private var dragging = false
    @State private var closing = false
    @State private var searching = false
    @FocusState private var searchFocused: Bool
    @State private var searchQuery = ""
    @State private var searchSubmitted = ""
    @State private var searchHits: [ReaderSearchHit] = []
    @State private var searchBusy = false
    @State private var searchError: String?
    @State private var searchGeneration = 0
    @State private var showingSearchResults = false
    @State private var jumpingToSearch = false
    private var reader: ReaderSnapshot { session.state }

    var body: some View {
        ZStack(alignment: .top) {
            readerContent.allowsHitTesting(!showingSearchResults).accessibilityHidden(showingSearchResults)
            if showingSearchResults {
                Button(action: dismissSearchResults) { Color.black.opacity(0.08).ignoresSafeArea() }
                    .buttonStyle(.plain).accessibilityLabel("收起搜索结果")
                    .accessibilityIdentifier("reader.search.dismiss")
            }
            VStack(spacing: 8) {
                if reader.chromeVisible || !reader.ready { topBar }
                if showingSearchResults {
                    NativeReaderSearchResults(hits: searchHits, busy: searchBusy, error: searchError,
                                              jumping: jumpingToSearch, pick: jumpToSearch, close: dismissSearchResults)
                        .frame(height: searchHits.isEmpty ? 58 : min(300, CGFloat(searchHits.count) * 96 + 38))
                        .modifier(NativeFloatingSurface()).padding(.horizontal, 12)
                }
                Spacer(minLength: 0)
            }
        }
        .ignoresSafeArea(.keyboard)
        .font(NativeTheme.font()).foregroundStyle(NativeTheme.ink).tint(NativeTheme.ink)
        .environment(\.symbolVariants, .none)
        .preferredColorScheme(store.preferences.readerTheme == 5 ? .dark : .light)
        .interactiveDismissDisabled(true)
        .sheet(item: $panel) { panel in
            Group {
                if panel == .typography {
                    NativeTypographyPanel(store: store, isPdf: reader.isPdf) { self.panel = nil }
                } else {
                    NavigationView {
                        panelContent(panel)
                            .navigationTitle(panel.title).navigationBarTitleDisplayMode(.inline).navigationBarHidden(false)
                            .toolbar { ToolbarItem(placement: .cancellationAction) { Button("完成") { self.panel = nil } } }
                    }.navigationViewStyle(.stack)
                }
            }
            .font(NativeTheme.font()).foregroundStyle(NativeTheme.ink).tint(NativeTheme.ink)
            .preferredColorScheme(store.preferences.readerTheme == 5 ? .dark : .light)
        }
        .onAppear { seekProgress = reader.progress }
        .onChange(of: reader.progress) { if !dragging { seekProgress = $0 } }
        .onChange(of: reader.chromeVisible) { if !$0 && searching { closeSearch() } }
        .onChange(of: session.dialog) { value in
            guard let value else { return }
            if searching { closeSearch() }
            dialog = value; panel = value.kind == "annotation" ? .annotation : .info
            session.dialog = nil
        }
        .task(id: searchGeneration) {
            let generation = searchGeneration
            guard !searchSubmitted.isEmpty else { return }
            do {
                let hits: [ReaderSearchHit] = try await store.request("reader.search", ["query": searchSubmitted])
                guard !Task.isCancelled, generation == searchGeneration else { return }
                searchHits = hits
            } catch {
                guard !Task.isCancelled, generation == searchGeneration else { return }
                searchError = error.localizedDescription
            }
            if !Task.isCancelled, generation == searchGeneration { searchBusy = false }
        }
    }
    private var readerContent: some View {
        ZStack {
            store.preferences.paper.ignoresSafeArea()
            ReaderSurface(controller: store.readerController).padding(.bottom, 24)
            if !reader.ready {
                VStack(spacing: 16) {
                    Text(book.title).font(NativeTheme.font(21, relativeTo: .title2)).multilineTextAlignment(.center)
                    if let error = reader.error { Text(error).foregroundStyle(NativeTheme.secondary).multilineTextAlignment(.center) }
                    else { ProgressView("正在打开书籍…") }
                }.padding(32).frame(maxWidth: .infinity, maxHeight: .infinity).background(store.preferences.paper)
            }
            VStack(spacing: 0) {
                Spacer(minLength: 0)
                // Toasts sit above whichever reader toolbars are showing instead of covering them.
                if let toast = store.toast { NativeToast(text: toast).padding(.bottom, 12) }
                if let text = reader.selectedText { selectionBar(text) }
                if reader.speechState != 0 && reader.speechState != 4 { speechBar }
                if reader.chromeVisible && reader.ready { bottomBar }
                HStack {
                    Text(reader.chapter).lineLimit(1); Spacer(); Text(reader.position).monospacedDigit().accessibilityIdentifier("reader.position")
                }.font(NativeTheme.font(11.5, relativeTo: .caption2)).foregroundStyle(NativeTheme.muted).padding(.horizontal, 24).frame(height: 24)
                    .background(store.preferences.paper).allowsHitTesting(false)
            }
        }
    }
    private func closeSearch() {
        searchFocused = false
        dismissSearchResults()
        withAnimation(.easeOut(duration: 0.2)) { searching = false }
    }
    private func dismissSearchResults() {
        showingSearchResults = false; searchSubmitted = ""; searchBusy = false; searchGeneration += 1
    }
    private func submitSearch() {
        guard !jumpingToSearch else { return }
        let query = searchQuery.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !query.isEmpty else { return }
        searchFocused = false; searchSubmitted = query
        searchHits = []; searchError = nil; searchBusy = true; showingSearchResults = true; searchGeneration += 1
    }
    private func jumpToSearch(_ hit: ReaderSearchHit) {
        guard !searchBusy, !jumpingToSearch else { return }
        jumpingToSearch = true
        store.perform {
            defer { jumpingToSearch = false }
            try await store.command("reader.searchJump", ["id": hit.id]); closeSearch()
        }
    }
    private var topBar: some View {
        HStack(spacing: 0) {
            Button {
                if searching { closeSearch(); return }
                closing = true
                Task { await store.closeReader(); closing = false }
            } label: { Group { if closing { ProgressView() } else { Image(systemName: "chevron.left") } }.frame(width: 44, height: 44) }
                .disabled(closing).accessibilityLabel("返回书籍详情").accessibilityIdentifier("reader.close")
            ZStack(alignment: .trailing) {
                if searching {
                    HStack(spacing: 0) {
                        NativeSearchField(text: $searchQuery, prompt: "搜索全书", focused: $searchFocused,
                                          identifier: "reader.search.query", submit: submitSearch)
                        Button(action: closeSearch) { Image(systemName: "xmark").font(.system(size: 15)).frame(width: 32, height: 44) }
                            .accessibilityLabel("关闭搜索").accessibilityIdentifier("reader.search.close")
                    }.transition(.move(edge: .trailing).combined(with: .opacity))
                } else {
                    Text(book.title).font(NativeTheme.font(14)).lineLimit(1).frame(maxWidth: .infinity, alignment: .leading)
                }
            }.frame(maxWidth: .infinity, minHeight: 44).padding(.horizontal, 2).clipped()
            Button {
                if searching { submitSearch() }
                else {
                    withAnimation(.easeOut(duration: 0.2)) { searching = true }
                    searchFocused = true
                }
            } label: {
                Image(systemName: "magnifyingglass").frame(width: 44, height: 44)
            }.disabled(!reader.ready).accessibilityLabel("搜索全书").accessibilityIdentifier("reader.search")
            Button { run("bookmark") } label: { Image(systemName: reader.bookmark ? "bookmark.fill" : "bookmark").frame(width: 44, height: 44) }
                .disabled(!reader.ready).accessibilityLabel("书签").accessibilityIdentifier("reader.bookmark")
        }.font(.system(size: 19, weight: .regular)).buttonStyle(.plain).padding(4)
            .modifier(NativeFloatingSurface()).padding(.horizontal, 12).padding(.top, 8)
    }
    private var bottomBar: some View {
        VStack(spacing: 0) {
            HStack { Text(reader.chapter).lineLimit(1); Spacer(); Text("\(seekProgress, specifier: "%.0f")%") }
                .font(NativeTheme.font(13)).foregroundStyle(NativeTheme.secondary)
            HStack(spacing: 0) {
                Button { run("chapter", ["direction": -1]) } label: { Image(systemName: "backward.end").frame(width: 36, height: 44) }.accessibilityLabel("上一章或上一页")
                Slider(value: $seekProgress, in: 0...100, onEditingChanged: { editing in
                    dragging = editing
                    if !editing { run("seek", ["progress": seekProgress]) }
                }).accessibilityLabel("阅读进度").accessibilityIdentifier("reader.progress")
                Button { run("chapter", ["direction": 1]) } label: { Image(systemName: "forward.end").frame(width: 36, height: 44) }.accessibilityLabel("下一章或下一页")
            }.font(.system(size: 17, weight: .regular)).buttonStyle(.plain).padding(.horizontal, -10).padding(.top, 2)
            HStack(spacing: 0) {
                tool("目录", "list.bullet") { panel = .toc }
                tool(reader.speechState == 0 || reader.speechState == 4 ? "听书" : "停止", "headphones") { run("speech") }
                tool("AI", "sparkles") { selectedText = ""; panel = .assistant }
                tool(reader.isPdf ? "显示" : "排版", "Aa") { panel = .typography }
            }
        }.padding(.horizontal, 18).padding(.top, 14).padding(.bottom, 6)
            .modifier(NativeFloatingSurface()).padding(.horizontal, 12).padding(.bottom, 12)
    }
    private var speechBar: some View {
        HStack(spacing: 18) {
            Image(systemName: "headphones")
            Text(reader.speechStatus).font(NativeTheme.font(12, relativeTo: .caption)).lineLimit(1).frame(maxWidth: .infinity, alignment: .leading)
            Button("设置") { panel = .speech }.font(NativeTheme.font(12, relativeTo: .caption))
            Button { run("speechPause") } label: { Image(systemName: reader.speechState == 3 ? "play.fill" : "pause.fill") }.accessibilityLabel("暂停或继续朗读")
            Button { run("speechStop") } label: { Image(systemName: "xmark") }.accessibilityLabel("停止朗读")
        }.buttonStyle(.plain).padding(12).modifier(NativeFloatingSurface()).padding(.horizontal, 12).padding(.bottom, 8)
    }
    private func selectionBar(_ text: String) -> some View {
        HStack(spacing: 0) {
            tool("划线", "highlighter") { run("annotate") }
            tool("笔记", "square.and.pencil") { selectedText = text; dialog = nil; panel = .note }
            tool("词典", "book.closed") { selectedText = text; panel = .dictionary }
            tool("翻译", "character.bubble") { selectedText = text; panel = .translation }
            tool("AI", "sparkles") { selectedText = text; panel = .assistant }
            tool("复制", "doc.on.doc") { UIPasteboard.general.string = text; run("clearSelection"); store.showToast("已复制") }
        }.padding(4).modifier(NativeFloatingSurface()).padding(.horizontal, 12).padding(.bottom, 8)
    }
    private func tool(_ title: String, _ image: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            VStack(spacing: 2) {
                if image == "Aa" { Text("Aa").font(NativeTheme.font(16)).frame(height: 21) }
                else { Image(systemName: image).font(.system(size: 21, weight: .regular)).frame(height: 21) }
                Text(title).font(NativeTheme.font(11, relativeTo: .caption2))
            }.frame(maxWidth: .infinity).frame(minHeight: 52).contentShape(Rectangle())
        }
            .buttonStyle(.plain).accessibilityIdentifier("reader.\(title)")
    }
    @ViewBuilder private func panelContent(_ panel: ReaderPanel) -> some View {
        switch panel {
        case .toc: NativeReaderTocView(store: store, close: { self.panel = nil })
        case .typography: NativeTypography(store: store, isPdf: reader.isPdf)
        case .speech: NativeSpeechSettings(store: store, close: { self.panel = nil })
        case .dictionary: NativeDictionary(store: store, initialTerm: selectedText)
        case .assistant: NativeAssistant(store: store, session: session, book: book, selection: selectedText)
        case .note, .annotation: NativeNoteEditor(store: store, quote: dialog?.text ?? selectedText, annotationId: dialog?.annotationId, initial: dialog?.note ?? "", close: { self.panel = nil })
        case .translation: NativeTranslation(store: store, text: selectedText)
        case .info: ScrollView { Text(dialog?.text ?? "").textSelection(.enabled).frame(maxWidth: .infinity, alignment: .leading).padding(24) }
        }
    }
    private func run(_ command: String, _ args: [String: Any] = [:]) { store.perform { try await store.command("reader.\(command)", args) } }
}

struct NativeTypography: View {
    @ObservedObject var store: NativeStore
    let isPdf: Bool
    private func preference<Value>(_ key: WritableKeyPath<MobilePreferences, Value>) -> Binding<Value> {
        Binding(get: { store.preferences[keyPath: key] }, set: { newValue in
            var value = store.preferences; value[keyPath: key] = newValue
            store.setPreferences(value)
        })
    }
    var body: some View {
        ScrollView {
            VStack(spacing: 4) {
                row("纸张") {
                    Picker("纸张", selection: preference(\.readerTheme)) { Text("白").tag(0); Text("米").tag(1); Text("黑").tag(5) }
                        .pickerStyle(.segmented).accessibilityIdentifier("typography.paper")
                }
                row("翻页动画") {
                    Picker("翻页动画", selection: preference(\.readerPageAnimation)) { Text("无").tag(0); Text("淡入").tag(1); Text("滑动").tag(2); Text("墨水").tag(3) }.pickerStyle(.segmented)
                }
                if isPdf {
                    row("缩放") {
                        Picker("缩放", selection: preference(\.pdfZoom)) { Text("整页").tag(1.0); Text("1.5 倍").tag(1.5); Text("2 倍").tag(2.0) }.pickerStyle(.segmented)
                    }
                } else {
                    row("字号") {
                        HStack(spacing: 0) {
                            Button { changeFont(-0.1) } label: { Text("A−").frame(width: 44, height: 44) }
                                .disabled(store.preferences.readerFontScale <= 0.8).accessibilityLabel("减小字号")
                            Text("\(store.preferences.readerFontScale * 100, specifier: "%.0f")%")
                                .monospacedDigit().frame(maxWidth: .infinity).accessibilityIdentifier("typography.fontScale")
                            Button { changeFont(0.1) } label: { Text("A+").frame(width: 44, height: 44) }
                                .disabled(store.preferences.readerFontScale >= 1.8).accessibilityLabel("增大字号")
                        }.buttonStyle(.plain).background(NativeTheme.sunk, in: RoundedRectangle(cornerRadius: 12))
                    }
                    row("行距") {
                        Picker("行距", selection: preference(\.readerLineHeight)) { Text("紧凑").tag(1.5); Text("标准").tag(1.75); Text("宽松").tag(2.1) }.pickerStyle(.segmented)
                    }
                    row("段间距") {
                        Picker("段间距", selection: preference(\.readerParagraphSpacing)) { Text("紧凑").tag(0.0); Text("标准").tag(0.5); Text("宽松").tag(1.0) }
                            .pickerStyle(.segmented).accessibilityIdentifier("typography.paragraphSpacing")
                    }
                    row("页边距") {
                        Picker("页边距", selection: preference(\.readerBodyPadding)) { Text("窄").tag(16.0); Text("标准").tag(24.0); Text("宽").tag(36.0) }.pickerStyle(.segmented)
                    }
                    NativeToggle(title: "首行缩进", isOn: preference(\.readerParagraphIndent), identifier: "typography.indent")
                    row("排版方向") {
                        Picker("排版方向", selection: preference(\.readerVerticalWriting)) { Text("横排").tag(false); Text("竖排").tag(true) }
                            .pickerStyle(.segmented).accessibilityIdentifier("typography.direction")
                    }
                }
            }.padding(.horizontal, 24).padding(.top, 2).padding(.bottom, 16)
        }.accessibilityIdentifier("typography.scroll")
    }
    private func row<Content: View>(_ title: String, @ViewBuilder controls: () -> Content) -> some View {
        HStack(spacing: 12) {
            Text(title).font(NativeTheme.font(13)).foregroundStyle(NativeTheme.secondary).frame(width: 58, alignment: .leading)
            controls().frame(maxWidth: .infinity)
        }.frame(minHeight: 44)
    }
    private func changeFont(_ delta: Double) {
        var value = store.preferences; value.readerFontScale = min(1.8, max(0.8, ((value.readerFontScale + delta) * 100).rounded() / 100))
        store.setPreferences(value)
    }
}

private struct NativeTypographyPanel: View {
    @ObservedObject var store: NativeStore
    let isPdf: Bool
    let close: () -> Void
    var body: some View {
        VStack(spacing: 0) {
            HStack {
                Text(isPdf ? "显示设置" : "阅读设置").font(NativeTheme.font(17, relativeTo: .headline))
                Spacer()
                Button("完成", action: close).font(NativeTheme.font(14)).frame(minWidth: 44, minHeight: 44)
                    .accessibilityIdentifier("typography.done")
            }.buttonStyle(.plain).padding(.horizontal, 24).padding(.top, 8)
            NativeTypography(store: store, isPdf: isPdf)
        }.background(NativeTheme.page).accessibilityElement(children: .contain)
            .accessibilityIdentifier("typography.panel").modifier(NativeTypographyPresentation(isPdf: isPdf))
    }
}

@available(iOS 16.0, *)
private struct NativeTypographyDetent: CustomPresentationDetent {
    static func height(in context: Context) -> CGFloat? { min(340, context.maxDetentValue * 0.42) }
}

@available(iOS 16.0, *)
private struct NativePdfTypographyDetent: CustomPresentationDetent {
    static func height(in context: Context) -> CGFloat? { min(220, context.maxDetentValue * 0.42) }
}

private struct NativeTypographyPresentation: ViewModifier {
    let isPdf: Bool
    @ViewBuilder func body(content: Content) -> some View {
        if #available(iOS 16.0, *) {
            content.presentationDetents([isPdf ? .custom(NativePdfTypographyDetent.self) : .custom(NativeTypographyDetent.self)])
                .presentationDragIndicator(.visible)
        } else {
            content.background(NativeLegacyTypographyPresentation())
        }
    }
}

/// SwiftUI gained detents in iOS 16. iOS 15 still uses UIKit's medium sheet so
/// opening reader settings leaves the text above it available for preview.
private struct NativeLegacyTypographyPresentation: UIViewControllerRepresentable {
    func makeUIViewController(context: Context) -> Controller { Controller() }
    func updateUIViewController(_ controller: Controller, context: Context) {}
    final class Controller: UIViewController {
        override func viewWillAppear(_ animated: Bool) {
            super.viewWillAppear(animated)
            var current: UIViewController? = self
            while let controller = current {
                if let sheet = controller.sheetPresentationController {
                    sheet.detents = [.medium()]
                    sheet.prefersGrabberVisible = true
                    sheet.prefersScrollingExpandsWhenScrolledToEdge = false
                    break
                }
                current = controller.parent
            }
        }
    }
}

struct NativeReaderTocView: View {
    @ObservedObject var store: NativeStore
    let close: () -> Void
    @State private var items: [ReaderToc] = []
    var body: some View {
        ScrollViewReader { proxy in
            List(items) { item in
                Button {
                    store.perform { try await store.command("reader.jump", ["id": item.id]); close() }
                } label: {
                    HStack {
                        Text(item.title).padding(.leading, CGFloat(item.level * 12))
                        Spacer()
                        if item.selected { Image(systemName: "checkmark") }
                    }.contentShape(Rectangle())
                }
                .accessibilityIdentifier("reader.toc.\(item.id)")
                .accessibilityAddTraits(item.selected ? .isSelected : [])
            }
            .onChange(of: items.count) { _ in
                // Open at the current chapter. The rows exist after this update.
                guard let current = items.first(where: \.selected) else { return }
                DispatchQueue.main.async { proxy.scrollTo(current.id, anchor: .center) }
            }
        }
        .task { do { items = try await store.request("reader.toc") } catch { if !Task.isCancelled { store.failure = error.localizedDescription } } }
    }
}

struct NativeReaderSearchResults: View {
    let hits: [ReaderSearchHit]
    let busy: Bool
    let error: String?
    let jumping: Bool
    let pick: (ReaderSearchHit) -> Void
    let close: () -> Void
    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            Group {
                if busy { ProgressView("正在搜索全书…") }
                else if let error { Text(error) }
                else { Text(hits.isEmpty ? "没有找到相关文字" : "找到 \(hits.count) 处结果") }
            }.font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.muted)
                .padding(.horizontal, 20).padding(.top, 14).padding(.bottom, 6).accessibilityIdentifier("reader.search.status")
            ScrollView {
                LazyVStack(spacing: 0) {
                    ForEach(hits) { hit in
                        Button { pick(hit) } label: {
                            VStack(alignment: .leading, spacing: 5) {
                                Text(hit.title).font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.muted).lineLimit(1)
                                Text(hit.excerpt).font(NativeTheme.font(14)).lineLimit(3).lineSpacing(5)
                                    .frame(maxWidth: .infinity, alignment: .leading)
                            }.padding(.vertical, 14).contentShape(Rectangle())
                        }.disabled(busy || jumping).accessibilityIdentifier("reader.search.hit.\(hit.id)")
                        NativeTheme.strong.frame(height: 0.5)
                    }
                }.padding(.horizontal, 20).padding(.bottom, 20)
            }
        }.buttonStyle(.plain).accessibilityElement(children: .contain)
            .accessibilityIdentifier("reader.search.results").accessibilityAction(.escape, close)
    }
}

struct NativeSpeechSettings: View {
    @ObservedObject var store: NativeStore
    let close: () -> Void
    @State private var voices: [SpeechVoice] = []
    @State private var voice = "zh-CN-XiaoxiaoNeural"
    @State private var speed = 1.0
    @State private var autoAdvance = true
    @State private var ready = false
    @State private var busy = false
    var body: some View {
        NativeForm {
            Section("系统语音") {
                Picker("语音", selection: $voice) {
                    Text("系统默认（中文）").tag("zh-CN-XiaoxiaoNeural")
                    ForEach(voices) { item in Text(item.name + (item.culture.map { " · " + $0 } ?? "")).tag(item.id) }
                }
                Text("语音由设备提供，可以在系统设置中下载更多语音。").font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.secondary)
            }
            Section("语速") { Text("\(speed, specifier: "%.1f")×"); Slider(value: $speed, in: 0.5...2, step: 0.1).accessibilityLabel("朗读语速") }
            Section { Toggle("自动翻到下一章或下一页", isOn: $autoAdvance); Text("设置在下次开始朗读时生效。").font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.secondary) }
            Button("保存") {
                busy = true
                store.perform { defer { busy = false }; try await store.command("settings.speech.save", ["voice": voice, "speed": speed, "autoAdvance": autoAdvance]); close() }
            }
        }.disabled(!ready || busy).task {
            do { let value: SpeechSnapshot = try await store.request("settings.speech.get"); voices = value.voices; voice = value.settings.voice; speed = value.settings.speed; autoAdvance = value.settings.autoAdvance; ready = true }
            catch { if !Task.isCancelled { store.failure = error.localizedDescription } }
        }
    }
}

struct NativeNoteEditor: View {
    @ObservedObject var store: NativeStore
    let quote: String
    let annotationId: String?
    let close: () -> Void
    @State private var note: String
    @State private var busy = false
    init(store: NativeStore, quote: String, annotationId: String?, initial: String, close: @escaping () -> Void) {
        self.store = store; self.quote = quote; self.annotationId = annotationId; self.close = close; _note = State(initialValue: initial)
    }
    var body: some View {
        NativeForm {
            Section("原文") { Text(quote).foregroundStyle(NativeTheme.secondary).textSelection(.enabled) }
            Section("笔记") { TextEditor(text: $note).frame(minHeight: 150) }
            Button("保存") {
                busy = true
                store.perform { defer { busy = false }; var args: [String: Any] = ["note": note]; if let annotationId { args["id"] = annotationId }
                    try await store.command(annotationId == nil ? "reader.annotate" : "reader.annotationEdit", args); close() }
            }
            if let annotationId {
                Button("删除批注", role: .destructive) { busy = true; store.perform { defer { busy = false }; try await store.command("reader.annotationDelete", ["id": annotationId]); close() } }
            }
        }.disabled(busy).modifier(NativeKeyboardDone())
    }
}

struct NativeTranslation: View {
    @ObservedObject var store: NativeStore
    let text: String
    @State private var translated: String?
    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 22) {
                Text(text).foregroundStyle(NativeTheme.secondary).textSelection(.enabled)
                Divider()
                if let translated { Text(translated).textSelection(.enabled) } else { ProgressView("正在翻译…") }
            }.frame(maxWidth: .infinity, alignment: .leading).padding(24)
        }.task { do { let value: TextResult = try await store.request("reader.translate", ["text": text]); translated = value.text } catch { if !Task.isCancelled { translated = error.localizedDescription } } }
    }
}
