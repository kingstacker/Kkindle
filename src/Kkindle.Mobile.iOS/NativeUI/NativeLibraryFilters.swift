import SwiftUI

/// Advanced choices are combined with the shelf's quick filter and search.
struct NativeLibraryFilterSelection: Equatable {
    var author: String?
    var format: String?
    var tag: String?
    var category: String?
    var readingStatus: String?

    var count: Int { [author, format, tag, category, readingStatus].compactMap { $0 }.count }
}

struct NativeLibraryFilterOptions: Decodable {
    let authors: [String]
    let formats: [String]
    let tags: [String]
    let categories: [String]
}

/// Edits a draft so dismissing the sheet leaves the shelf alone. The book
/// count uses the same quick filter and search as the shelf, so it reflects
/// what applying the draft will show.
struct NativeLibraryFilterSheet: View {
    @Environment(\.dismiss) private var dismiss
    let options: NativeLibraryFilterOptions
    let quickFilter: Int
    let books: [LibraryBook]
    let query: String
    let apply: (NativeLibraryFilterSelection) -> Void
    @State private var draft: NativeLibraryFilterSelection

    init(options: NativeLibraryFilterOptions, selection: NativeLibraryFilterSelection, quickFilter: Int,
         books: [LibraryBook], query: String, apply: @escaping (NativeLibraryFilterSelection) -> Void) {
        self.options = options
        self.quickFilter = quickFilter
        self.books = books
        self.query = query
        self.apply = apply
        _draft = State(initialValue: selection)
    }

    private var count: Int {
        books.filter { book in
            matchesQuery(book)
                && matchesQuickFilter(book)
                && (draft.readingStatus.flatMap(Int.init).map { $0 == NativeBookReadingStatus.value(book) } ?? true)
                && matchesFormat(draft.format, in: book.formats)
                && matchesToken(draft.author, in: book.authors)
                && matchesToken(draft.tag, in: book.tags)
                && matchesCategory(draft.category, in: book.category)
        }.count
    }

    var body: some View {
        NavigationView {
            VStack(spacing: 0) {
                ScrollView {
                    VStack(spacing: 14) {
                        card {
                            VStack(alignment: .leading, spacing: 12) {
                                sectionTitle("阅读状态")
                                chipRow {
                                    chip("全部", selected: draft.readingStatus == nil,
                                         identifier: "library.filters.option.status.all") { draft.readingStatus = nil }
                                    chip("待读", selected: draft.readingStatus == "0",
                                         identifier: "library.filters.option.status.0") { draft.readingStatus = "0" }
                                    chip("在读", selected: draft.readingStatus == "1",
                                         identifier: "library.filters.option.status.1") { draft.readingStatus = "1" }
                                    chip("读完", selected: draft.readingStatus == "2",
                                         identifier: "library.filters.option.status.2") { draft.readingStatus = "2" }
                                }
                            }
                        }
                        card {
                            VStack(alignment: .leading, spacing: 12) {
                                sectionTitle("格式")
                                chipRow {
                                    chip("全部", selected: draft.format == nil,
                                         identifier: "library.filters.option.format.all") { draft.format = nil }
                                    ForEach(options.formats, id: \.self) { format in
                                        chip(format, selected: draft.format == format,
                                             identifier: "library.filters.option.format.\(format)") { draft.format = format }
                                    }
                                }
                            }
                        }
                        card {
                            VStack(spacing: 0) {
                                metadataLink(icon: "person", title: "作者", key: "author",
                                             values: options.authors, selection: $draft.author)
                                divider
                                metadataLink(icon: "tag", title: "标签", key: "tag",
                                             values: options.tags, selection: $draft.tag)
                                divider
                                metadataLink(icon: "square.stack.3d.up", title: "分类", key: "category",
                                             values: options.categories, selection: $draft.category)
                            }.padding(.vertical, 2)
                        }
                    }.padding(.horizontal, 16).padding(.top, 4).padding(.bottom, 20)
                }
            }
            .background(NativeAmbientBackground(fallback: NativeTheme.sunk))
            .safeAreaInset(edge: .bottom, spacing: 0) { bottomBar }
            .navigationTitle("筛选书籍").navigationBarTitleDisplayMode(.inline).navigationBarHidden(false)
            .modifier(NativeTransparentNavigationBar())
            .toolbar {
                ToolbarItem(placement: .navigationBarLeading) {
                    Button { dismiss() } label: { Image(systemName: "chevron.left") }
                        .accessibilityLabel("返回")
                        .accessibilityIdentifier("library.filters.cancel")
                }
                ToolbarItem(placement: .navigationBarTrailing) {
                    Button("清除") { draft = NativeLibraryFilterSelection() }
                        .accessibilityIdentifier("library.filters.clear")
                }
            }
        }.navigationViewStyle(.stack).font(NativeTheme.font()).foregroundStyle(NativeTheme.ink).tint(NativeTheme.ink)
    }

    private var bottomBar: some View {
        Button { apply(draft); dismiss() } label: {
            Text("查看 \(count) 本书").font(NativeTheme.font(15)).foregroundStyle(NativeTheme.ink)
                .frame(maxWidth: .infinity).frame(height: 50)
                .modifier(NativeFilterGlass(shape: Capsule(), interactive: true))
        }.buttonStyle(.plain).accessibilityIdentifier("library.filters.apply")
            .padding(.horizontal, 20).padding(.top, 10).padding(.bottom, 8)
    }

    private func card<Content: View>(@ViewBuilder _ content: () -> Content) -> some View {
        content().frame(maxWidth: .infinity, alignment: .leading)
            .padding(16)
            .modifier(NativeFilterGlass(shape: RoundedRectangle(cornerRadius: 16)))
    }

    private func sectionTitle(_ title: String) -> some View {
        Text(title).font(NativeTheme.font(13, relativeTo: .caption)).foregroundStyle(NativeTheme.secondary)
    }

    private func chipRow<Content: View>(@ViewBuilder _ content: () -> Content) -> some View {
        ScrollView(.horizontal, showsIndicators: false) {
            HStack(spacing: 8) { content(); Spacer(minLength: 0) }
        }
    }

    private func chip(_ title: String, selected: Bool, identifier: String,
                      action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Text(title).font(NativeTheme.font(14)).lineLimit(1)
                .padding(.horizontal, 16).frame(minHeight: 36)
                .foregroundStyle(selected ? NativeTheme.page : NativeTheme.ink)
                .modifier(NativeFilterGlass(shape: Capsule(), selected: selected, interactive: true))
        }.buttonStyle(.plain).accessibilityAddTraits(selected ? .isSelected : [])
            .accessibilityIdentifier(identifier)
    }

    private func metadataLink(icon: String, title: String, key: String, values: [String],
                              selection: Binding<String?>) -> some View {
        NavigationLink {
            NativeLibraryFilterValuePage(title: title, key: key, values: values, selection: selection)
        } label: {
            HStack(spacing: 12) {
                Image(systemName: icon).font(.system(size: 17))
                    .foregroundStyle(NativeTheme.secondary).frame(width: 24)
                Text(title).font(NativeTheme.font(15))
                Spacer(minLength: 12)
                Text(selection.wrappedValue ?? "不限").font(NativeTheme.font(14))
                    .foregroundStyle(NativeTheme.muted).lineLimit(1)
                Image(systemName: "chevron.right").font(.system(size: 12, weight: .regular))
                    .foregroundStyle(NativeTheme.faint)
            }.frame(minHeight: 50).contentShape(Rectangle())
        }.buttonStyle(.plain)
            .accessibilityIdentifier("library.filters.metadata.\(key)")
    }

    private var divider: some View {
        NativeTheme.divider.frame(height: 0.5).padding(.leading, 36)
    }

    private func matchesQuery(_ book: LibraryBook) -> Bool {
        query.isEmpty || book.title.localizedCaseInsensitiveContains(query)
            || book.authors.localizedCaseInsensitiveContains(query)
    }

    private func matchesQuickFilter(_ book: LibraryBook) -> Bool {
        quickFilter == 0 || quickFilter == 1 && NativeBookReadingStatus.value(book) == 1
            || quickFilter == 2 && book.favorite || quickFilter == 3 && NativeBookReadingStatus.value(book) == 2
    }

    /// The server filters authors and tags by whole token, not substring.
    private func matchesToken(_ value: String?, in field: String) -> Bool {
        guard let value else { return true }
        return field.split(whereSeparator: { ",，;；".contains($0) })
            .map { $0.trimmingCharacters(in: .whitespaces) }
            .contains { $0.localizedCaseInsensitiveCompare(value) == .orderedSame }
    }

    private func matchesCategory(_ value: String?, in field: String) -> Bool {
        guard let value else { return true }
        return field.trimmingCharacters(in: .whitespaces)
            .localizedCaseInsensitiveCompare(value) == .orderedSame
    }

    private func matchesFormat(_ value: String?, in field: String) -> Bool {
        guard let value else { return true }
        return field.split(separator: "·")
            .map { $0.trimmingCharacters(in: .whitespaces) }
            .contains { $0.localizedCaseInsensitiveCompare(value) == .orderedSame }
    }
}

/// The filter sheet draws its own ambient page; a system bar background would
/// add a differently tinted band above the cards.
private struct NativeTransparentNavigationBar: ViewModifier {
    @ViewBuilder func body(content: Content) -> some View {
        if #available(iOS 16.0, *) { content.toolbarBackground(.hidden, for: .navigationBar) }
        else { content }
    }
}

/// Uses the system glass material where available, with a solid fallback on older iOS.
private struct NativeFilterGlass<S: Shape>: ViewModifier {
    let shape: S
    var selected = false
    var interactive = false

    @ViewBuilder func body(content: Content) -> some View {
        if #available(iOS 26.0, *) {
            if selected {
                // A tinted regular glass surface lightens black. Keep the
                // accent opaque while retaining the interactive glass underneath.
                content.background {
                    shape.fill(NativeTheme.ink)
                        .glassEffect(.clear.interactive(), in: shape)
                }
            } else if interactive {
                content.glassEffect(.regular.interactive(), in: shape)
            } else {
                content.glassEffect(.regular, in: shape)
            }
        } else {
            content.background(selected ? NativeTheme.ink : interactive ? NativeTheme.sunk : NativeTheme.surface, in: shape)
        }
    }
}

/// A pushed selector for the long author, tag and category lists.
private struct NativeLibraryFilterValuePage: View {
    let title: String
    let key: String
    let values: [String]
    @Binding var selection: String?
    @Environment(\.dismiss) private var dismiss
    @State private var searching = false
    @State private var search = ""
    @FocusState private var searchFocused: Bool

    private var matching: [String] {
        search.isEmpty ? values : values.filter { $0.localizedCaseInsensitiveContains(search) }
    }

    var body: some View {
        VStack(spacing: 0) {
            if searching {
                TextField("搜索\(title)", text: $search).font(NativeTheme.font(14)).focused($searchFocused)
                    .textInputAutocapitalization(.never).disableAutocorrection(true).submitLabel(.done)
                    .onSubmit { searchFocused = false }
                    .padding(.horizontal, 12).frame(height: 40)
                    .background(NativeTheme.sunk, in: RoundedRectangle(cornerRadius: 10))
                    .padding(.horizontal, 20).padding(.top, 8)
                    .accessibilityIdentifier("library.filters.search.input.\(key)")
            }
            ScrollView {
                LazyVStack(spacing: 0) {
                    option("不限", value: nil, id: "all")
                    ForEach(matching, id: \.self) { value in
                        option(value, value: value, id: value)
                    }
                    if values.isEmpty {
                        Text("暂无可选项").font(NativeTheme.font(13)).foregroundStyle(NativeTheme.muted)
                            .padding(.top, 24)
                    } else if matching.isEmpty {
                        Text("没有找到匹配项").font(NativeTheme.font(13)).foregroundStyle(NativeTheme.muted)
                            .padding(.top, 24)
                    }
                }.padding(.horizontal, 20).padding(.top, 8).padding(.bottom, 24)
            }
        }
        .background(NativeAmbientBackground())
        .navigationTitle(title).navigationBarTitleDisplayMode(.inline).navigationBarHidden(false)
        .nativeBackButton()
        .toolbar {
            ToolbarItem(placement: .navigationBarTrailing) {
                Button {
                    searching.toggle()
                    if searching { searchFocused = true } else { search = ""; searchFocused = false }
                } label: {
                    Image(systemName: searching ? "xmark" : "magnifyingglass")
                        .font(.system(size: 15))
                }.accessibilityLabel(searching ? "关闭搜索\(title)" : "搜索\(title)")
                    .accessibilityIdentifier("library.filters.search.\(key)")
            }
        }
    }

    private func option(_ label: String, value: String?, id: String) -> some View {
        let selected = selection == value
        return Button {
            selection = value
            dismiss()
        } label: {
            HStack(spacing: 12) {
                Text(label).font(NativeTheme.font(15)).lineLimit(1)
                Spacer(minLength: 12)
                if selected {
                    Image(systemName: "checkmark").font(.system(size: 14, weight: .semibold))
                        .foregroundStyle(NativeTheme.ink)
                }
            }.frame(minHeight: 50).contentShape(Rectangle())
        }.buttonStyle(.plain).accessibilityAddTraits(selected ? .isSelected : [])
            .accessibilityIdentifier("library.filters.option.\(key).\(id)")
    }
}

