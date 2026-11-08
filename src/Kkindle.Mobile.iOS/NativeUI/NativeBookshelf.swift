import SwiftUI

struct NativeLibraryPage: View {
    @ObservedObject var store: NativeStore
    @State private var query = ""
    @State private var searching = false
    @FocusState private var searchFocused: Bool
    @State private var loading = true
    @State private var importing = false
    @State private var filter = 0
    @State private var creatingFolder = false
    @State private var renamingFolder: LibraryFolder?
    @State private var folderBusy = false

    private var books: [LibraryBook] {
        NativeBookOrder.sorted(store.library.filter { book in
            (query.isEmpty || book.title.localizedCaseInsensitiveContains(query) || book.authors.localizedCaseInsensitiveContains(query))
            && (filter == 0 || filter == 1 && (book.progress > 0 && book.progress < 99.5 || book.readingStatus == 1)
                || filter == 2 && book.favorite || filter == 3 && (book.progress >= 99.5 || book.readingStatus == 2))
        }, sort: store.preferences.sort)
    }
    private func summary(_ books: [LibraryBook]) -> String {
        let reading = books.filter { $0.progress > 0 && $0.progress < 99.5 || $0.readingStatus == 1 }.count
        return "\(books.count) 本" + (reading > 0 ? " · \(reading) 本在读" : "")
    }

    var body: some View {
        VStack(spacing: 0) {
            masthead
            GeometryReader { geometry in
                ScrollView {
                    if store.preferences.collectionsView { foldersView(width: geometry.size.width) }
                    else { booksView(width: geometry.size.width) }
                }.refreshable { await refresh() }
            }
        }.background(NativeTheme.page).navigationBarHidden(true)
        .sheet(isPresented: $creatingFolder) {
            NativeNameSheet(store: store, title: "新建收藏夹", initial: "", button: "创建") { name in
                let _: CreatedFolder = try await store.request("collection.create", ["name": name]); try await store.refreshLibrary()
            }
        }
        .sheet(item: $renamingFolder) { folder in
            NativeNameSheet(store: store, title: "重命名收藏夹", initial: folder.name, button: "保存") { name in
                try await store.command("collection.rename", ["collectionId": folder.id, "name": name]); try await store.refreshLibrary()
            }
        }
        .task {
            defer { loading = false }
            await refresh()
        }
    }
    private var masthead: some View {
        HStack(spacing: 0) {
            if searching {
                NativeSearchField(text: $query, prompt: store.preferences.collectionsView ? "搜索收藏夹" : "搜索书名或作者",
                                  focused: $searchFocused, identifier: "library.search") { searchFocused = false }
            } else {
                HStack(spacing: 18) {
                    modeButton("书库", collections: false, identifier: "library.books")
                    modeButton("收藏夹", collections: true, identifier: "library.collections")
                }
                Spacer(minLength: 0)
            }
            Button {
                searching.toggle(); searchFocused = searching
                if !searching { query = "" }
            } label: {
                Image(systemName: searching ? "xmark" : "magnifyingglass").font(.system(size: 20, weight: .regular))
                    .frame(width: 44, height: 44)
            }.buttonStyle(.plain).accessibilityLabel(searching ? "关闭搜索" : "搜索书库")
                .accessibilityIdentifier("library.search.toggle")
            Button {
                if store.preferences.collectionsView { creatingFolder = true }
                else {
                    importing = true
                    store.perform { defer { importing = false }; try await store.command("library.import"); try await store.refreshLibrary() }
                }
            } label: {
                Group { if importing { ProgressView() } else { Image(systemName: "plus").font(.system(size: 21, weight: .regular)) } }
                    .frame(width: 44, height: 44)
            }.buttonStyle(.plain).disabled(importing || folderBusy)
                .accessibilityLabel(store.preferences.collectionsView ? "新建收藏夹" : "导入书籍")
                .accessibilityIdentifier("library.import")
        }.padding(.leading, 24).padding(.trailing, 12).padding(.top, 16).padding(.bottom, 4)
    }
    private func modeButton(_ title: String, collections: Bool, identifier: String) -> some View {
        Button {
            var value = store.preferences; value.collectionsView = collections
            store.setPreferences(value)
        } label: {
            Text(title).font(NativeTheme.font(21, relativeTo: .title2))
                .foregroundStyle(store.preferences.collectionsView == collections ? NativeTheme.ink : NativeTheme.faint)
                .frame(minHeight: 44)
        }.buttonStyle(.plain).accessibilityIdentifier(identifier)
            .accessibilityAddTraits(store.preferences.collectionsView == collections ? .isSelected : [])
    }
    private var filters: some View {
        HStack(spacing: 0) {
            Picker("筛选", selection: $filter) {
                Text("全部").tag(0); Text("在读").tag(1); Text("收藏").tag(2); Text("读完").tag(3)
            }.pickerStyle(.segmented).accessibilityIdentifier("library.filter")
            NativeBookSortMenu(store: store)
            NativeBookLayoutButton(store: store, identifier: "library.layout")
        }
    }
    private func booksView(width pageWidth: CGFloat) -> some View {
        // Filtering and sorting the whole library is the costly part of a render.
        let books = self.books
        return VStack(alignment: .leading, spacing: 0) {
            filters.padding(.horizontal, -12).padding(.bottom, 12)
            continueCard
            Text(summary(books)).font(NativeTheme.font(12, relativeTo: .caption))
                .foregroundStyle(NativeTheme.muted).padding(.bottom, 14)
            if books.isEmpty { emptyView }
            else {
                NativeBookShelf(store: store, books: books, pageWidth: pageWidth) { book in bookActions(book) }
            }
        }.padding(.horizontal, 24).padding(.top, 8).padding(.bottom, 28)
    }
    private var emptyView: some View {
        VStack(spacing: 6) {
            if loading { ProgressView("正在加载书库…") }
            else {
                Image(systemName: "book").font(.system(size: 34, weight: .ultraLight)).foregroundStyle(NativeTheme.muted)
                Text(store.library.isEmpty ? "书库还是空的" : "没有找到相关书籍")
                    .font(NativeTheme.font(17, relativeTo: .headline)).padding(.top, 8)
                Text(store.library.isEmpty ? "点右上角 + 导入 EPUB、PDF 或 TXT" : "换个关键词或筛选条件试试")
                    .font(NativeTheme.font(13, relativeTo: .subheadline)).foregroundStyle(NativeTheme.muted)
                    .multilineTextAlignment(.center)
            }
        }.frame(maxWidth: .infinity).padding(.top, 96).padding(.bottom, 40)
    }
    @ViewBuilder private var continueCard: some View {
        if query.isEmpty, filter <= 1, let id = store.continueBookId, let book = store.library.first(where: { $0.id == id }) {
            Button { store.perform { try await store.openReader(book) } } label: {
                HStack(spacing: 14) {
                    BookCover(book: book, width: 48, height: 68)
                    VStack(alignment: .leading, spacing: 3) {
                        Text("继续阅读").font(NativeTheme.font(13)).foregroundStyle(NativeTheme.muted)
                        Text(book.title).font(NativeTheme.font(16)).lineLimit(1)
                        Spacer(minLength: 4)
                        HStack(spacing: 10) {
                            NativeThinProgress(value: book.progress)
                            Text("\(book.progress, specifier: "%.0f")%")
                                .font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.muted)
                        }
                    }
                }.frame(height: 68).contentShape(Rectangle())
            }.buttonStyle(.plain).disabled(store.openingReader).accessibilityIdentifier("library.continue")
                .padding(.top, 4).padding(.bottom, 20)
        }
    }
    private func foldersView(width: CGFloat) -> some View {
        let cardWidth = max(1, (width - 48 - 14) / 2)
        let folders = store.folders.filter { query.isEmpty || $0.name.localizedCaseInsensitiveContains(query) }
        return VStack(alignment: .leading, spacing: 16) {
            Text("\(folders.count) 个收藏夹").font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.muted)
            LazyVGrid(columns: [GridItem(.flexible(), spacing: 14), GridItem(.flexible(), spacing: 14)], spacing: 14) {
                ForEach(folders) { folder in
                    NavigationLink { NativeCollectionPage(store: store, folderId: folder.id) } label: {
                        NativeFolderCard(folder: folder, books: store.library.filter { $0.collectionIds.contains(folder.id) }, width: cardWidth)
                    }.buttonStyle(.plain).accessibilityIdentifier("collection.\(folder.id)")
                        .contextMenu {
                            if !folder.automatic {
                                Button("重命名") { renamingFolder = folder }
                                Button("清空书籍") { folderAction("clear", folder: folder) }
                                Menu("合并到") {
                                    ForEach(store.folders.filter { !$0.automatic && $0.id != folder.id }) { target in
                                        Button(target.name) { folderAction("merge", folder: folder, target: target.id) }
                                    }
                                }
                                Button("解散收藏夹", role: .destructive) { folderAction("dissolve", folder: folder) }
                            }
                        }
                }
                Button { creatingFolder = true } label: {
                    VStack(spacing: 10) {
                        Image(systemName: "plus").font(.system(size: 20, weight: .regular))
                            .frame(width: 44, height: 44).background(NativeTheme.sunk, in: Circle())
                        Text("新建收藏夹").font(NativeTheme.font(13)).foregroundStyle(NativeTheme.muted)
                    }.frame(maxWidth: .infinity).frame(height: cardWidth * 1.48)
                    .overlay(RoundedRectangle(cornerRadius: 18).stroke(NativeTheme.strong, style: StrokeStyle(lineWidth: 1.4, dash: [3, 3])))
                }.buttonStyle(.plain).accessibilityIdentifier("collection.create")
            }
            Text("长按收藏夹可重命名、清空、合并或解散。")
                .font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.muted)
        }.padding(.horizontal, 24).padding(.top, 14).padding(.bottom, 28).disabled(folderBusy)
    }
    @ViewBuilder private func bookActions(_ book: LibraryBook) -> some View {
        Button("阅读") { store.perform { try await store.openReader(book) } }
        Button(book.favorite ? "取消收藏" : "收藏") {
            store.perform { try await store.command("book.favorite", ["bookId": book.id, "favorite": !book.favorite]); try await store.refreshLibrary() }
        }
    }
    private func folderAction(_ operation: String, folder: LibraryFolder, target: String? = nil) {
        folderBusy = true
        store.perform {
            defer { folderBusy = false }
            var args = ["collectionId": folder.id]; if let target { args["targetId"] = target }
            try await store.command("collection.\(operation)", args); try await store.refreshLibrary()
        }
    }
    private func refresh() async {
        do { try await store.refreshLibrary() }
        catch { if !Task.isCancelled { store.failure = error.localizedDescription } }
    }
}

enum NativeBookOrder {
    static func sorted(_ books: [LibraryBook], sort: Int) -> [LibraryBook] {
        books.sorted { left, right in
            switch sort {
            case 1: return left.title.localizedStandardCompare(right.title) == .orderedAscending
            case 2: return left.authors.localizedStandardCompare(right.authors) == .orderedAscending
            case 3: return left.createdAt > right.createdAt
            default: return left.updatedAt > right.updatedAt
            }
        }
    }
}

/// Both the library and an opened collection use the same cover geometry,
/// text, spacing, navigation and persisted grid/list preference.
struct NativeBookShelf<Actions: View>: View {
    @ObservedObject var store: NativeStore
    let books: [LibraryBook]
    let pageWidth: CGFloat
    private let actions: (LibraryBook) -> Actions

    init(store: NativeStore, books: [LibraryBook], pageWidth: CGFloat,
         @ViewBuilder actions: @escaping (LibraryBook) -> Actions) {
        self.store = store; self.books = books; self.pageWidth = pageWidth; self.actions = actions
    }
    private var columns: Int { max(3, Int((pageWidth - 32) / 136)) }
    private var coverWidth: CGFloat { max(1, (pageWidth - 48 - CGFloat(columns - 1) * 16) / CGFloat(columns)) }

    var body: some View {
        Group {
            if store.preferences.listView {
                LazyVStack(spacing: 0) { ForEach(books) { book in bookLink(book) } }
            } else {
                LazyVGrid(columns: Array(repeating: GridItem(.flexible(), spacing: 16, alignment: .top), count: columns),
                          alignment: .leading, spacing: 26) {
                    ForEach(books) { book in bookLink(book) }
                }
            }
        }.accessibilityElement(children: .contain)
            .accessibilityIdentifier(store.preferences.listView ? "books.list" : "books.grid")
    }
    private func bookLink(_ book: LibraryBook) -> some View {
        NavigationLink { NativeBookDetailPage(store: store, bookId: book.id) } label: {
            if store.preferences.listView { BookRow(book: book) }
            else { NativeBookTile(book: book, width: coverWidth) }
        }.buttonStyle(.plain).contextMenu { actions(book) }.accessibilityIdentifier("book.\(book.id)")
    }
}

struct NativeBookSortMenu: View {
    @ObservedObject var store: NativeStore
    var body: some View {
        Menu {
            Picker("排序", selection: Binding(get: { store.preferences.sort }, set: { sort in
                var value = store.preferences; value.sort = sort
                store.setPreferences(value)
            })) {
                Text("最近更新").tag(0); Text("书名").tag(1); Text("作者").tag(2); Text("添加时间").tag(3)
            }
        } label: {
            Image(systemName: "line.3.horizontal.decrease").font(.system(size: 18, weight: .regular)).frame(width: 38, height: 44)
        }.buttonStyle(.plain).foregroundStyle(NativeTheme.muted).accessibilityLabel("书籍排序")
    }
}

struct NativeBookLayoutButton: View {
    @ObservedObject var store: NativeStore
    let identifier: String
    var body: some View {
        Button {
            var value = store.preferences; value.listView.toggle()
            store.setPreferences(value)
        } label: {
            Image(systemName: store.preferences.listView ? "square.grid.3x3" : "list.bullet")
                .font(.system(size: 18, weight: .regular)).frame(width: 38, height: 44)
        }.buttonStyle(.plain).foregroundStyle(NativeTheme.muted)
            .accessibilityLabel(store.preferences.listView ? "网格视图" : "列表视图")
            .accessibilityIdentifier(identifier)
    }
}

struct NativeBookTile: View {
    let book: LibraryBook
    let width: CGFloat
    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            BookCover(book: book, width: width)
            Group {
                if book.progress > 0 && book.progress < 99.5 { NativeThinProgress(value: book.progress) }
                else { Color.clear.frame(height: 2) }
            }.padding(.top, 8)
            Text(book.title).font(NativeTheme.font(13.5)).lineLimit(2).lineSpacing(3).padding(.top, 8)
            Text(book.authors).font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.muted)
                .lineLimit(1).padding(.top, 2)
        }.frame(maxWidth: .infinity, alignment: .topLeading).contentShape(Rectangle())
    }
}

struct NativeFolderCard: View {
    let folder: LibraryFolder
    let books: [LibraryBook]
    let width: CGFloat
    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            ZStack {
                let covers = Array(books.sorted { $0.updatedAt > $1.updatedAt }.prefix(3))
                if covers.isEmpty {
                    RoundedRectangle(cornerRadius: 6).fill(NativeTheme.strong)
                        .frame(width: width * 0.46, height: width * 0.67)
                        .overlay(Image(systemName: "book").font(.system(size: 26, weight: .ultraLight)).foregroundStyle(NativeTheme.muted))
                } else {
                    ForEach(Array(covers.enumerated()), id: \.element.id) { index, book in
                        BookCover(book: book, width: width * 0.46)
                            .rotationEffect(.degrees(covers.count == 1 ? 0 : Double(index - 1) * 12))
                            .offset(x: covers.count == 1 ? 0 : CGFloat(index - 1) * width * 0.12,
                                    y: index == 1 ? -4 : 4)
                            .zIndex(index == 1 ? 3 : Double(index))
                    }
                }
            }.frame(maxWidth: .infinity).frame(height: width * 1.06)
            VStack(alignment: .leading, spacing: 3) {
                Text(folder.name).font(NativeTheme.font(15)).lineLimit(1)
                Text("\(folder.count) 本").font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.muted)
            }.padding(.horizontal, 16).padding(.bottom, 15)
        }.frame(maxWidth: .infinity).frame(height: width * 1.48, alignment: .top)
            .background(NativeTheme.sunk, in: RoundedRectangle(cornerRadius: 18))
    }
}

struct BookRow: View {
    let book: LibraryBook
    var body: some View {
        HStack(spacing: 16) {
            BookCover(book: book, width: 46, height: 66)
            VStack(alignment: .leading, spacing: 4) {
                Text(book.title).font(NativeTheme.font(15)).lineLimit(1)
                HStack(spacing: 10) {
                    Text(book.authors).font(NativeTheme.font(12.5, relativeTo: .caption)).lineLimit(1)
                    Spacer(minLength: 0)
                    Text("\(book.progress, specifier: "%.0f")%").font(NativeTheme.font(12, relativeTo: .caption))
                }.foregroundStyle(NativeTheme.muted)
            }
        }.padding(.vertical, 10).contentShape(Rectangle())
    }
}
