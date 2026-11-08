import SwiftUI

struct NativeMaterials: View {
    @ObservedObject var store: NativeStore
    @State private var entries: [MaterialRecord] = []
    @State private var filter = "all"
    @State private var loading = true
    @State private var exporting = false
    @State private var expanded: Set<String> = []
    private struct BookGroup: Identifiable {
        let id: String
        var records: [MaterialRecord]
    }
    private func matches(_ item: MaterialRecord) -> Bool { filter == "all" || item.kind == filter }
    /// One pass over the entries. They arrive newest first, so the order in
    /// which books first appear also sorts the books by their latest entry.
    private var groups: [BookGroup] {
        var groups: [BookGroup] = []
        var index: [String: Int] = [:]
        for item in entries where matches(item) {
            if let position = index[item.bookId] { groups[position].records.append(item) }
            else { index[item.bookId] = groups.count; groups.append(BookGroup(id: item.bookId, records: [item])) }
        }
        return groups
    }

    var body: some View {
        let groups = self.groups
        let count = groups.reduce(0) { $0 + $1.records.count }
        let books = Dictionary(store.library.map { ($0.id, $0) }, uniquingKeysWith: { first, _ in first })
        return VStack(spacing: 0) {
            HStack {
                Text("阅读资料").font(NativeTheme.font(26, relativeTo: .largeTitle)).accessibilityIdentifier("page.materials")
                Spacer()
                Text("\(count) 条").font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.muted)
                Menu {
                    Button("导出 Markdown") { export(true) }; Button("导出纯文本") { export(false) }
                } label: { Text("导出").font(NativeTheme.font(14)).frame(minHeight: 44) }
                    .disabled(exporting || count == 0).accessibilityLabel("导出阅读资料")
            }.padding(.horizontal, 24).padding(.top, 20).padding(.bottom, 10)
            Picker("资料筛选", selection: $filter) { Text("全部").tag("all"); Text("划线").tag("highlight"); Text("笔记").tag("note"); Text("书签").tag("bookmark") }
                .pickerStyle(.segmented).padding(.horizontal, 12).padding(.bottom, 8)
            ScrollView {
                LazyVStack(alignment: .leading, spacing: 0) {
                    if loading { ProgressView("正在读取阅读资料…").frame(maxWidth: .infinity).padding(.top, 96) }
                    if !loading && count == 0 {
                        VStack(spacing: 6) {
                            Image(systemName: "note.text").font(.system(size: 34, weight: .ultraLight)).foregroundStyle(NativeTheme.muted)
                            Text("还没有阅读资料").font(NativeTheme.font(17)).padding(.top, 8)
                            Text("书签、划线和笔记会汇集在这里").font(NativeTheme.font(13)).foregroundStyle(NativeTheme.muted)
                        }.frame(maxWidth: .infinity).padding(.top, 96)
                    }
                    ForEach(groups) { group in
                        let id = group.id
                        let records = group.records
                        Button {
                            if expanded.contains(id) { expanded.remove(id) } else { expanded.insert(id) }
                        } label: {
                            HStack(spacing: 14) {
                                if let book = books[id] { BookCover(book: book, width: 48, height: 68) }
                                else { Image(systemName: "book").font(.system(size: 28, weight: .ultraLight)).frame(width: 48, height: 68).foregroundStyle(NativeTheme.muted) }
                                VStack(alignment: .leading, spacing: 6) {
                                    Text(records.first?.bookTitle ?? "未知书名").font(NativeTheme.font(15)).lineLimit(2)
                                    Text("\(records.count) 条资料").font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.muted)
                                }.frame(maxWidth: .infinity, alignment: .leading)
                                Image(systemName: expanded.contains(id) ? "chevron.down" : "chevron.right")
                                    .font(.system(size: 14, weight: .regular)).foregroundStyle(NativeTheme.muted)
                            }.padding(.vertical, 14).contentShape(Rectangle())
                        }.buttonStyle(.plain).accessibilityIdentifier("materials.book.\(id)")
                        if expanded.contains(id) {
                            ForEach(records) { item in materialEntry(item).padding(.bottom, 6) }
                            Color.clear.frame(height: 6)
                        }
                        NativeTheme.divider.frame(height: 1)
                    }
                }.padding(.horizontal, 24).padding(.top, 4).padding(.bottom, 28)
            }.refreshable { await load() }
        }.background(NativeTheme.page).navigationBarHidden(true).task { await load() }
    }
    private func materialEntry(_ item: MaterialRecord) -> some View {
        Button {
            if let book = store.library.first(where: { $0.id == item.bookId }) {
                store.perform { try await store.openReader(book, location: item.location.foundationValue as? [String: Any]) }
            } else { store.showToast("这本书已不在书库中") }
        } label: {
            HStack(alignment: .top, spacing: 10) {
                Image(systemName: item.kind == "bookmark" ? "bookmark" : "note.text")
                    .font(.system(size: 17, weight: .regular)).foregroundStyle(NativeTheme.muted).frame(width: 18).padding(.top, 1)
                VStack(alignment: .leading, spacing: 5) {
                    Text("\(item.kindLabel) · \(item.locationText)").font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.muted).lineLimit(1)
                    Text(item.quote).font(NativeTheme.font(14)).lineSpacing(4)
                    if !item.note.isEmpty { Text(item.note).font(NativeTheme.font(13)).foregroundStyle(NativeTheme.secondary).lineSpacing(4) }
                }.frame(maxWidth: .infinity, alignment: .leading)
            }
            .padding(.horizontal, 12).padding(.vertical, 11).background(NativeTheme.sunk, in: RoundedRectangle(cornerRadius: 10))
        }.buttonStyle(.plain).disabled(store.openingReader).accessibilityIdentifier("materials.entry.\(item.id)")
    }
    private func load() async {
        defer { loading = false }
        do { entries = try await store.request("materials.get") }
        catch { if !Task.isCancelled { store.failure = error.localizedDescription } }
    }
    private func export(_ markdown: Bool) {
        exporting = true; let ids = entries.filter(matches).map(\.id)
        store.perform { defer { exporting = false }; try await store.command("materials.export", ["ids": ids, "markdown": markdown]) }
    }
}

struct NativeReadingDashboard: View {
    @ObservedObject var store: NativeStore
    @State private var value: DashboardSnapshot?
    @State private var days = 14
    @State private var picked: String?
    @State private var allMedals = false
    private var dashboard: ReadingDashboard? { value?.dashboard }

    var body: some View {
        VStack(spacing: 0) {
            Text("阅读数据").font(NativeTheme.font(26, relativeTo: .largeTitle)).frame(maxWidth: .infinity, alignment: .leading)
                .padding(.horizontal, 24).padding(.top, 20).padding(.bottom, 10).accessibilityIdentifier("page.dashboard")
            ScrollView {
            VStack(alignment: .leading, spacing: 28) {
                if let value {
                    VStack(alignment: .leading, spacing: 4) {
                        Text("累计阅读").font(NativeTheme.font(13)).foregroundStyle(NativeTheme.muted)
                        HStack(alignment: .firstTextBaseline, spacing: 6) {
                            Text(durationValue(value.dashboard.totalSeconds)).font(NativeTheme.font(48, relativeTo: .largeTitle))
                            Text(value.dashboard.totalSeconds < 3600 ? "分钟" : "小时").font(NativeTheme.font(16)).foregroundStyle(NativeTheme.secondary)
                        }
                        Text("连续阅读 \(value.streak) 天").font(NativeTheme.font(13)).foregroundStyle(NativeTheme.muted)
                    }
                    LazyVGrid(columns: [GridItem(.flexible(), spacing: 10), GridItem(.flexible(), spacing: 10)], spacing: 10) {
                        statistic("已开始", "\(value.dashboard.booksStarted) 本", detail: String(format: "平均进度 %.1f%%", value.dashboard.averageProgress))
                        statistic("已读完", "\(value.dashboard.booksFinished) 本", detail: "共 \(value.dashboard.books.count) 本书")
                        statistic("书签", "\(value.dashboard.bookmarkCount)")
                        statistic("划线与笔记", "\(value.dashboard.annotationCount)")
                    }
                    dailyChart(value.dashboard.dailyReading)
                    Text("最近在读").font(NativeTheme.font(16, relativeTo: .headline))
                    if value.dashboard.recentBooks.isEmpty { Text("还没有阅读记录").foregroundStyle(NativeTheme.secondary) }
                    ScrollView(.horizontal, showsIndicators: false) {
                        HStack(alignment: .top, spacing: 14) {
                            ForEach(value.dashboard.recentBooks.prefix(10)) { item in
                                if let book = store.library.first(where: { $0.id == item.bookId }) {
                                    NavigationLink { NativeBookDetailPage(store: store, bookId: book.id) } label: {
                                        VStack(alignment: .leading, spacing: 6) {
                                            BookCover(book: book, width: 96, height: 138)
                                            Text(book.title).font(NativeTheme.font(13)).lineLimit(1)
                                            HStack(spacing: 6) {
                                                NativeThinProgress(value: item.progressPercent)
                                                Text("\(item.progressPercent, specifier: "%.0f")%").font(NativeTheme.font(11, relativeTo: .caption2)).foregroundStyle(NativeTheme.muted)
                                            }
                                        }.frame(width: 96)
                                    }.buttonStyle(.plain)
                                }
                            }
                        }.padding(.vertical, 4)
                    }
                    Text("读得最久").font(NativeTheme.font(16, relativeTo: .headline))
                    VStack(spacing: 16) {
                        ForEach(value.dashboard.mostReadBooks.filter { $0.cumulativeSeconds > 0 }.prefix(5)) { item in dashboardBook(item, detail: readingDuration(item.cumulativeSeconds)) }
                    }
                    progressDistribution(value.dashboard.books)
                    HStack { Text("阅读勋章").font(NativeTheme.font(17, relativeTo: .headline)); Spacer(); Button(allMedals ? "已解锁" : "全部") { allMedals.toggle() }.font(NativeTheme.font(12, relativeTo: .caption)) }
                    Text("已解锁 \(value.medals.filter(\.isUnlocked).count) / \(value.medals.count)").font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.secondary)
                    LazyVGrid(columns: [GridItem(.adaptive(minimum: 100))], spacing: 18) {
                        ForEach(value.medals.filter { allMedals || $0.isUnlocked }) { medal in
                            Button { store.showToast(medal.rule + (medal.isUnlocked ? "" : String(format: "（%.0f%%）", medal.fraction * 100))) } label: {
                                VStack(spacing: 9) {
                                    ZStack {
                                        Circle().stroke(.primary.opacity(0.12), lineWidth: 2)
                                        Circle().trim(from: 0, to: min(1, max(0, medal.fraction))).stroke(.primary, lineWidth: 2).rotationEffect(.degrees(-90))
                                        Image(systemName: "seal").font(NativeTheme.font(21, relativeTo: .title2)).opacity(medal.isUnlocked ? 1 : 0.35)
                                    }.frame(width: 50, height: 50)
                                    Text(medal.name).font(NativeTheme.font(12, relativeTo: .caption)).multilineTextAlignment(.center)
                                }.frame(maxWidth: .infinity)
                            }.buttonStyle(.plain)
                        }
                    }
                } else { ProgressView("正在读取阅读数据…").frame(maxWidth: .infinity).padding(.top, 70) }
            }.padding(.horizontal, 24).padding(.top, 16).padding(.bottom, 36)
            }.refreshable { await load() }
        }.background(NativeTheme.page).navigationBarHidden(true).task(id: days) { await load() }
    }
    private func durationValue(_ seconds: Int64) -> String {
        String(format: "%.1f", Double(seconds) / (seconds < 3600 ? 60 : 3600))
    }
    private func statistic(_ name: String, _ value: String, detail: String = "") -> some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(name).font(NativeTheme.font(12.5, relativeTo: .caption)).foregroundStyle(NativeTheme.muted)
            Text(value).font(NativeTheme.font(24, relativeTo: .title2))
            if !detail.isEmpty { Text(detail).font(NativeTheme.font(11.5, relativeTo: .caption2)).foregroundStyle(NativeTheme.muted) }
        }.frame(maxWidth: .infinity, minHeight: 70, alignment: .topLeading).padding(16)
            .background(NativeTheme.sunk, in: RoundedRectangle(cornerRadius: 18))
    }
    private func dailyChart(_ values: [ReadingDay]) -> some View {
        let maximum = max(1, values.map(\.activeSeconds).max() ?? 1)
        let selected = values.first { $0.date == picked } ?? values.last
        return VStack(alignment: .leading, spacing: 16) {
            HStack(spacing: 10) {
                Text("近 \(days) 天阅读时长").font(NativeTheme.font(16, relativeTo: .headline))
                Spacer(minLength: 0)
                Picker("阅读天数", selection: $days) { Text("7 天").tag(7); Text("14 天").tag(14); Text("30 天").tag(30) }.pickerStyle(.segmented).frame(width: 140)
            }
            Text("\(selected?.date ?? "") · \(readingDuration(selected?.activeSeconds ?? 0))").font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.secondary)
            HStack(alignment: .bottom, spacing: 5) {
                ForEach(values) { day in
                    Button { picked = day.date } label: {
                        VStack { Spacer(minLength: 0); RoundedRectangle(cornerRadius: 4).fill(day.activeSeconds > 0 ? NativeTheme.ink : NativeTheme.strong)
                            .frame(maxWidth: 16).frame(height: max(3, 124 * Double(day.activeSeconds) / Double(maximum))) }
                            .frame(maxWidth: .infinity).frame(height: 124).contentShape(Rectangle())
                    }.buttonStyle(.plain).accessibilityLabel("\(day.date)，\(readingDuration(day.activeSeconds))")
                }
            }
            HStack { Text(values.first?.date ?? ""); Spacer(); Text(values.last?.date ?? "") }.font(NativeTheme.font(11, relativeTo: .caption2)).foregroundStyle(NativeTheme.secondary)
        }.padding(16).background(NativeTheme.sunk, in: RoundedRectangle(cornerRadius: 18))
    }
    @ViewBuilder private func dashboardBook(_ item: DashboardBook, detail: String) -> some View {
        if let book = store.library.first(where: { $0.id == item.bookId }) {
            NavigationLink { NativeBookDetailPage(store: store, bookId: book.id) } label: {
                HStack(spacing: 14) {
                    BookCover(book: book, width: 38, height: 54)
                    VStack(alignment: .leading, spacing: 8) {
                        HStack {
                            Text(book.title).font(NativeTheme.font(14)).lineLimit(1); Spacer()
                            Text(detail).font(NativeTheme.font(12.5, relativeTo: .caption)).foregroundStyle(NativeTheme.muted)
                        }
                        NativeThinProgress(value: Double(item.cumulativeSeconds), total: Double(dashboard?.mostReadBooks.map(\.cumulativeSeconds).max() ?? 1), height: 4)
                    }
                }
            }.buttonStyle(.plain)
        } else { VStack(alignment: .leading, spacing: 8) { Text(item.title + "（已移出书库）"); Text(detail).font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.secondary) } }
    }
    private func progressDistribution(_ books: [DashboardBook]) -> some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("进度分布").font(NativeTheme.font(16, relativeTo: .headline))
            Text("平均 \(dashboard?.averageProgress ?? 0, specifier: "%.0f")% · 共 \(books.count) 本").font(NativeTheme.font(12, relativeTo: .caption)).foregroundStyle(NativeTheme.secondary)
            ForEach(0..<5) { index in
                let count = books.filter { ($0.progressPercent >= 99.5 ? 4 : min(3, Int($0.progressPercent / 25))) == index }.count
                HStack { Text(index == 4 ? "已读完" : "\(index * 25)–\(index * 25 + 24)%").frame(width: 70, alignment: .leading)
                    NativeThinProgress(value: Double(count), total: Double(max(1, books.count)), height: 4); Text("\(count) 本").frame(width: 40, alignment: .trailing) }.font(NativeTheme.font(12, relativeTo: .caption))
            }
        }.padding(16).background(NativeTheme.sunk, in: RoundedRectangle(cornerRadius: 18))
    }
    private func load() async {
        do { let result: DashboardSnapshot = try await store.request("dashboard.get", ["days": days]); if !Task.isCancelled { value = result } }
        catch { if !Task.isCancelled { store.failure = error.localizedDescription } }
    }
}
