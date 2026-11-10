import SwiftUI
import UIKit

struct NativeNameSheet: View {
    @Environment(\.dismiss) private var dismiss
    @ObservedObject var store: NativeStore
    let title: String
    let button: String
    let save: (String) async throws -> Void
    let isSecure: Bool
    @State private var value: String
    @State private var busy = false
    @State private var error: String?
    @FocusState private var focused: Bool
    init(store: NativeStore, title: String, initial: String, button: String, isSecure: Bool = false, save: @escaping (String) async throws -> Void) {
        self.store = store; self.title = title; self.button = button; self.isSecure = isSecure; self.save = save; _value = State(initialValue: initial)
    }
    var body: some View {
        NavigationView {
            VStack(alignment: .leading, spacing: 12) {
                Group {
                    if isSecure { SecureField("请输入密码", text: $value) }
                    else { TextField(title, text: $value) }
                }.font(NativeTheme.font()).focused($focused).padding(14)
                    .background(NativeTheme.sunk, in: RoundedRectangle(cornerRadius: 12))
                    .accessibilityIdentifier("name.input")
                if let error { Text(error).font(NativeTheme.font(13)).foregroundStyle(NativeTheme.muted) }
                Spacer()
            }.padding(24).background(NativeAmbientBackground())
                .navigationTitle(title).navigationBarTitleDisplayMode(.inline).navigationBarHidden(false)
                .modifier(NativeKeyboardDone())
                .toolbar {
                    ToolbarItem(placement: .cancellationAction) { Button("取消") { dismiss() }.disabled(busy) }
                    ToolbarItem(placement: .confirmationAction) { Button(button) {
                        busy = true
                        Task { defer { busy = false }; do { try await save(value.trimmingCharacters(in: .whitespacesAndNewlines)); dismiss() } catch { self.error = error.localizedDescription } }
                    }.disabled(busy || value.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty).accessibilityIdentifier("name.save") }
                }
        }.navigationViewStyle(.stack).interactiveDismissDisabled(busy)
            .font(NativeTheme.font()).foregroundStyle(NativeTheme.ink).tint(NativeTheme.ink)
            .onAppear { focused = true }
    }
}

struct NativeBookDetailPage: View {
    @ObservedObject var store: NativeStore
    @Environment(\.dismiss) private var dismiss
    let bookId: String
    @State private var editor = false
    @State private var folders = false
    @State private var rename = false
    @State private var busy = false
    @State private var expanded = false
    private var book: LibraryBook? { store.library.first { $0.id == bookId } }

    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: 0) {
                Button { dismiss() } label: { Image(systemName: "chevron.left").frame(width: 44, height: 44) }
                    .buttonStyle(.plain).accessibilityLabel("返回书库").accessibilityIdentifier("book.back")
                Spacer()
                if let book {
                    Button { action("favorite", ["favorite": !book.favorite]) } label: {
                        Image(systemName: book.favorite ? "star.fill" : "star").frame(width: 44, height: 44)
                    }.buttonStyle(.plain).disabled(busy).accessibilityLabel(book.favorite ? "取消收藏" : "收藏")
                    Menu {
                        Button("编辑书籍信息") { editor = true }; Button("加入收藏夹") { folders = true }
                        Button("重命名") { rename = true }; Button("发送到 Kindle") { action("kindle") }
                        Button("移到回收站", role: .destructive) { action("delete") }
                    } label: { Image(systemName: "ellipsis").frame(width: 44, height: 44) }
                        .buttonStyle(.plain).disabled(busy).accessibilityLabel("书籍选项")
                }
            }.font(.system(size: 20, weight: .regular)).padding(.horizontal, 8).padding(.top, 6)
            ScrollView {
                if let book {
                    VStack(alignment: .leading, spacing: 0) {
                        BookCover(book: book, width: 148, height: 214).frame(maxWidth: .infinity)
                        Text(book.title).font(NativeTheme.font(21, relativeTo: .title2)).lineLimit(3).lineSpacing(5)
                            .multilineTextAlignment(.center).frame(maxWidth: .infinity).padding(.top, 28)
                        Text(book.authors).font(NativeTheme.font(14)).foregroundStyle(NativeTheme.secondary)
                            .multilineTextAlignment(.center).frame(maxWidth: .infinity).padding(.top, 6)
                        Text("\(book.formats) · \(book.size) · 已读 \(book.progress, specifier: "%.0f")%")
                            .font(NativeTheme.font(12.5, relativeTo: .caption)).foregroundStyle(NativeTheme.muted)
                            .frame(maxWidth: .infinity).padding(.top, 8)
                        if let description = book.description, !description.isEmpty {
                            VStack(alignment: .leading, spacing: 8) {
                                Text(description).font(NativeTheme.font(14)).foregroundStyle(NativeTheme.secondary)
                                    .lineLimit(expanded ? nil : 5).lineSpacing(7).textSelection(.enabled)
                                Button(expanded ? "收起" : "展开全部") { expanded.toggle() }
                                    .font(NativeTheme.font(14)).foregroundStyle(NativeTheme.muted).buttonStyle(.plain)
                            }.padding(.top, 32)
                        }
                        HStack {
                            Text("书籍信息").font(NativeTheme.font(13)).foregroundStyle(NativeTheme.muted)
                            Spacer()
                            Button("编辑") { editor = true }.font(NativeTheme.font(14)).foregroundStyle(NativeTheme.muted).buttonStyle(.plain)
                        }.padding(.top, 24).padding(.bottom, 4)
                        VStack(spacing: 14) {
                            fact("评分", book.onlineRating.map { String(format: "%.1f", $0) })
                            fact("丛书", book.series); fact("出版社", book.publisher); fact("出版日期", book.publishDate)
                            fact("ISBN", book.isbn); fact("页数", book.pageCount); fact("装帧", book.binding)
                            fact("标签", book.tags); fact("分类", book.category); fact("文件", book.formats)
                            fact("添加时间", String(book.createdAt.prefix(10)).replacingOccurrences(of: "-", with: "/"))
                        }.font(NativeTheme.font(14)).padding(.top, 7)
                    }.padding(.horizontal, 28).padding(.top, 12).padding(.bottom, 24)
                }
            }
            if let book {
                Button { store.perform { try await store.openReader(book) } } label: {
                    Text(book.progress > 0 ? "继续阅读 · \(book.progress, specifier: "%.0f")%" : "开始阅读")
                }.buttonStyle(NativePrimaryButtonStyle()).disabled(store.openingReader).accessibilityIdentifier("book.read")
                    .padding(.horizontal, 28).padding(.top, 8).padding(.bottom, 14)
            }
        }.background(NativeAmbientBackground()).navigationBarHidden(true).modifier(NativeHideTabBar())
            .sheet(isPresented: $editor) { if let book { NativeBookEditor(store: store, book: book) } }
            .sheet(isPresented: $folders) { NativeBookCollections(store: store, bookId: bookId) }
            .sheet(isPresented: $rename) {
                NativeNameSheet(store: store, title: "修改书名", initial: book?.title ?? "", button: "保存") { title in
                    try await store.command("book.rename", ["bookId": bookId, "title": title]); try await store.refreshLibrary()
                }
            }
    }
    @ViewBuilder private func fact(_ name: String, _ value: String?) -> some View {
        if let value, !value.isEmpty {
            HStack(alignment: .top, spacing: 8) {
                Text(name).foregroundStyle(NativeTheme.muted).frame(width: 88, alignment: .leading)
                Text(value).foregroundStyle(NativeTheme.secondary).frame(maxWidth: .infinity, alignment: .trailing).multilineTextAlignment(.trailing)
            }
        }
    }
    private func action(_ operation: String, _ args: [String: Any] = [:]) {
        busy = true; var payload = args; payload["bookId"] = bookId
        store.perform {
            defer { busy = false }; try await store.command("book.\(operation)", payload)
            try await store.refreshLibrary(); if book == nil { dismiss() }
        }
    }
}

struct NativeBookEditor: View {
    @ObservedObject var store: NativeStore
    @Environment(\.dismiss) private var dismiss
    let book: LibraryBook
    @State private var fields: [String: String]
    @State private var busy = false
    @State private var error: String?
    init(store: NativeStore, book: LibraryBook) {
        self.store = store; self.book = book
        _fields = State(initialValue: ["title": book.title, "authors": book.authors, "series": book.series ?? "",
            "publisher": book.publisher ?? "", "publishDate": book.publishDate ?? "", "isbn": book.isbn ?? "",
            "pageCount": book.pageCount ?? "", "binding": book.binding ?? "", "tags": book.tags, "category": book.category,
            "description": book.description ?? "", "onlineRating": book.onlineRating.map { String($0) } ?? ""])
    }
    private func field(_ key: String) -> Binding<String> { Binding(get: { fields[key] ?? "" }, set: { fields[key] = $0 }) }
    var body: some View {
        NavigationView {
            NativeForm {
                Section("封面") {
                    if let encoded = fields["cover"], let bytes = Data(base64Encoded: encoded), let image = UIImage(data: bytes) {
                        Image(uiImage: image).resizable().scaledToFit().frame(height: 160)
                    } else { BookCover(book: book, width: 90) }
                    Button("更换封面") {
                        busy = true
                        Task { defer { busy = false }; do { let value: CoverResult = try await store.request("book.cover"); if let cover = value.cover { fields["cover"] = cover } } catch { self.error = error.localizedDescription } }
                    }
                }
                Section("书籍信息") {
                    NativeTextField(title: "书名", text: field("title")); NativeTextField(title: "作者", text: field("authors")); NativeTextField(title: "丛书", text: field("series"))
                    NativeTextField(title: "出版社", text: field("publisher")); NativeTextField(title: "出版日期", text: field("publishDate")); NativeTextField(title: "ISBN", text: field("isbn"))
                    NativeTextField(title: "页数", text: field("pageCount")); NativeTextField(title: "装帧", text: field("binding")); NativeTextField(title: "标签", text: field("tags"))
                    NativeTextField(title: "分类", text: field("category")); NativeTextField(title: "评分（0–10）", text: field("onlineRating")).keyboardType(.decimalPad)
                }
                Section("简介") { TextEditor(text: field("description")).frame(minHeight: 150) }
                if let error { Section { Text(error).foregroundStyle(NativeTheme.secondary) } }
            }.disabled(busy).navigationTitle("编辑书籍").navigationBarTitleDisplayMode(.inline).navigationBarHidden(false).modifier(NativeKeyboardDone())
                .toolbar {
                    ToolbarItem(placement: .cancellationAction) { Button("取消") { dismiss() }.disabled(busy) }
                    ToolbarItem(placement: .confirmationAction) { Button("保存") {
                        busy = true
                        Task { defer { busy = false }; do { try await store.command("book.save", ["bookId": book.id, "fields": fields]); try await store.refreshLibrary(); dismiss() } catch { self.error = error.localizedDescription } }
                    }.disabled(busy || (fields["title"] ?? "").isEmpty) }
                }
        }.navigationViewStyle(.stack).interactiveDismissDisabled(busy)
    }
}

struct NativeBookCollections: View {
    @ObservedObject var store: NativeStore
    @Environment(\.dismiss) private var dismiss
    let bookId: String
    @State private var name = ""
    @State private var busy = false
    var body: some View {
        NavigationView {
            NativeForm {
                ForEach(store.folders.filter { !$0.automatic }) { folder in
                    Toggle(folder.name, isOn: Binding(get: { store.library.first { $0.id == bookId }?.collectionIds.contains(folder.id) ?? false }, set: { member in
                        busy = true
                        store.perform { defer { busy = false }; try await store.command("collection.membership", ["bookId": bookId, "collectionId": folder.id, "member": member]); try await store.refreshLibrary() }
                    })).tint(NativeTheme.systemSwitchTint)
                }
                Section("新建收藏夹") {
                    TextField("名称", text: $name)
                    Button("创建并加入") {
                        busy = true
                        store.perform {
                            defer { busy = false }
                            let folder: CreatedFolder = try await store.request("collection.create", ["name": name])
                            try await store.command("collection.membership", ["bookId": bookId, "collectionId": folder.id, "member": true])
                            name = ""; try await store.refreshLibrary()
                        }
                    }.disabled(name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                }
            }.disabled(busy).navigationTitle("加入收藏夹").navigationBarTitleDisplayMode(.inline).navigationBarHidden(false)
                .toolbar { Button("完成") { dismiss() }.disabled(busy) }
        }.navigationViewStyle(.stack).interactiveDismissDisabled(busy)
    }
}

struct NativeCollectionPage: View {
    @ObservedObject var store: NativeStore
    @Environment(\.dismiss) private var dismiss
    let folderId: String
    @State private var rename = false
    @State private var addBooks = false
    @State private var busy = false
    private var folder: LibraryFolder? { store.folders.first { $0.id == folderId } }
    private var books: [LibraryBook] {
        NativeBookOrder.sorted(store.library.filter { $0.collectionIds.contains(folderId) }, sort: store.preferences.sort)
    }
    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: 0) {
                Button { dismiss() } label: { Image(systemName: "chevron.left").frame(width: 44, height: 44) }
                    .accessibilityLabel("返回收藏夹").accessibilityIdentifier("collection.back")
                Text(folder?.name ?? "收藏夹").font(NativeTheme.font(21, relativeTo: .title2)).lineLimit(1)
                    .frame(maxWidth: .infinity, alignment: .leading).padding(.leading, 4)
                if folder?.automatic == false {
                    Button { addBooks = true } label: { Image(systemName: "plus").frame(width: 44, height: 44) }
                        .accessibilityLabel("添加书籍").accessibilityIdentifier("collection.add")
                    Menu {
                        Button("重命名") { rename = true }; Button("清空书籍") { run("clear") }
                        Menu("合并到") { ForEach(store.folders.filter { !$0.automatic && $0.id != folderId }) { target in Button(target.name) { run("merge", ["targetId": target.id]) } } }
                        Button("解散收藏夹", role: .destructive) { run("dissolve") }
                    } label: { Image(systemName: "ellipsis").frame(width: 44, height: 44) }
                        .accessibilityLabel("收藏夹选项")
                }
            }.font(.system(size: 20, weight: .regular)).buttonStyle(.plain).padding(.horizontal, 8).padding(.top, 16)
            GeometryReader { geometry in
                ScrollView {
                    VStack(alignment: .leading, spacing: 0) {
                        HStack(spacing: 0) {
                            Text("\(books.count) 本").font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.muted)
                            Spacer()
                            NativeBookSortMenu(store: store)
                            NativeBookLayoutButton(store: store, identifier: "collection.layout")
                        }.padding(.trailing, -12).padding(.bottom, 14)
                        if books.isEmpty {
                            VStack(spacing: 12) {
                                Image(systemName: "book").font(.system(size: 34, weight: .ultraLight)).foregroundStyle(NativeTheme.muted)
                                Text("收藏夹还是空的").font(NativeTheme.font(17, relativeTo: .headline))
                                if folder?.automatic == false {
                                    Text("点右上角 + 添加书籍").font(NativeTheme.font(13)).foregroundStyle(NativeTheme.muted)
                                }
                            }.frame(maxWidth: .infinity).padding(.top, 96)
                        } else {
                            NativeBookShelf(store: store, books: books, pageWidth: geometry.size.width) { book in
                                Button("阅读") { store.perform { try await store.openReader(book) } }
                                Button(book.favorite ? "取消收藏" : "收藏") {
                                    store.perform { try await store.command("book.favorite", ["bookId": book.id, "favorite": !book.favorite]); try await store.refreshLibrary() }
                                }
                                if folder?.automatic == false {
                                    Button("移出收藏夹", role: .destructive) { run("membership", ["bookId": book.id, "member": false]) }
                                }
                            }
                        }
                    }.padding(.horizontal, 24).padding(.top, 8).padding(.bottom, 28)
                }.refreshable {
                    do { try await store.refreshLibrary() }
                    catch { if !Task.isCancelled { store.failure = error.localizedDescription } }
                }
            }
        }.background(NativeAmbientBackground()).disabled(busy).navigationBarHidden(true)
            .modifier(NativeHideTabBar())
            .sheet(isPresented: $rename) {
                NativeNameSheet(store: store, title: "重命名收藏夹", initial: folder?.name ?? "", button: "保存") { name in
                    try await store.command("collection.rename", ["collectionId": folderId, "name": name]); try await store.refreshLibrary()
                }
            }
            .sheet(isPresented: $addBooks) {
                NavigationView {
                    List(store.library.filter { !$0.collectionIds.contains(folderId) }) { book in
                        Button { run("membership", ["bookId": book.id, "member": true]) } label: { BookRow(book: book) }
                            .accessibilityIdentifier("collection.pick.\(book.id)")
                    }.disabled(busy).navigationTitle("添加书籍").navigationBarTitleDisplayMode(.inline).navigationBarHidden(false).toolbar { Button("完成") { addBooks = false }.disabled(busy) }
                }.navigationViewStyle(.stack)
            }
    }
    private func run(_ operation: String, _ args: [String: Any] = [:]) {
        var payload = args; payload["collectionId"] = folderId; busy = true
        store.perform { defer { busy = false }; try await store.command("collection.\(operation)", payload); try await store.refreshLibrary(); if folder == nil { dismiss() } }
    }
}
