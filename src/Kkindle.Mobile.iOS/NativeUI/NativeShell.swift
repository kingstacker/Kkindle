import SwiftUI
import UIKit
import ImageIO

struct NativeShell: View {
    @ObservedObject var store: NativeStore
    var body: some View {
        TabView {
            NavigationView { NativeLibraryPage(store: store) }.navigationViewStyle(.stack)
                .tabItem { NativeTabLabel(title: "书库", symbol: "book") }
            NavigationView { NativeMaterials(store: store) }.navigationViewStyle(.stack)
                .tabItem { NativeTabLabel(title: "阅读资料", symbol: "note.text") }
            NavigationView { NativeReadingDashboard(store: store) }.navigationViewStyle(.stack)
                .tabItem { NativeTabLabel(title: "阅读数据", symbol: "chart.bar") }
            NavigationView { NativeProfilePage(store: store) }.navigationViewStyle(.stack)
                .tabItem { NativeTabLabel(title: "我的", symbol: "person") }
        }
        .font(NativeTheme.font()).foregroundStyle(NativeTheme.ink).tint(NativeTheme.ink)
        .environment(\.symbolVariants, .none)
        .background(NativeTheme.page).preferredColorScheme(store.preferences.colorScheme)
        .fullScreenCover(item: $store.readerBook) { book in NativeReaderPage(store: store, session: store.session, book: book) }
        .sheet(item: $store.txtReview) { review in NativeTxtReview(store: store, review: review) }
        .modifier(NativeFeedback(store: store, active: store.readerBook == nil))
        .disabled(store.restartRequired || store.dataBusy)
        .overlay {
            if store.restartRequired {
                VStack(spacing: 16) {
                    Image(systemName: "arrow.clockwise").font(.largeTitle)
                    Text("请重新打开 Kkindle").font(NativeTheme.font(21, relativeTo: .title2))
                    Text("请完全关闭并重新打开应用后继续使用，以载入恢复的数据。")
                        .multilineTextAlignment(.center).foregroundStyle(.secondary)
                }.padding(32).frame(maxWidth: .infinity, maxHeight: .infinity).background(NativeTheme.page)
            } else if store.dataBusy {
                VStack(spacing: 18) { ProgressView(); Text(store.busyMessage).foregroundStyle(.secondary) }
                    .frame(maxWidth: .infinity, maxHeight: .infinity).background(.regularMaterial)
            }
        }
    }
}

struct NativeTabLabel: View {
    let title: String
    let symbol: String
    var body: some View {
        // A concrete UIKit image retains the original outline symbol. TabView
        // otherwise substitutes filled SF Symbol variants for the selection.
        Label {
            Text(title)
        } icon: {
            Image(uiImage: UIImage(systemName: symbol)!)
        }
    }
}

struct NativeToast: View {
    let text: String
    var body: some View {
        Text(text).font(NativeTheme.font(14, relativeTo: .subheadline)).padding(.horizontal, 18).padding(.vertical, 12)
            .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 12))
            .padding(.horizontal, 20).allowsHitTesting(false)
    }
}

struct NativeFeedback: ViewModifier {
    @ObservedObject var store: NativeStore
    let active: Bool
    func body(content: Content) -> some View {
        content.overlay(alignment: .bottom) {
            if active, let toast = store.toast { NativeToast(text: toast).padding(.bottom, 80) }
        }
    }
}

@MainActor
private enum NativeCoverCache {
    static let images = NSCache<NSString, UIImage>()
    static func image(path: String, version: String, pixels: Int) async -> UIImage? {
        images.totalCostLimit = 24 * 1024 * 1024
        let key = "\(path):\(version):\(pixels)" as NSString
        if let image = images.object(forKey: key) { return image }
        let image = await Task.detached(priority: .userInitiated) { () -> UIImage? in
            guard let source = CGImageSourceCreateWithURL(URL(fileURLWithPath: path) as CFURL, nil),
                  let thumbnail = CGImageSourceCreateThumbnailAtIndex(source, 0, [
                    kCGImageSourceCreateThumbnailFromImageAlways: true,
                    kCGImageSourceCreateThumbnailWithTransform: true,
                    kCGImageSourceThumbnailMaxPixelSize: pixels,
                    kCGImageSourceShouldCacheImmediately: true
                  ] as CFDictionary) else { return nil }
            return UIImage(cgImage: thumbnail)
        }.value
        if let image { images.setObject(image, forKey: key, cost: Int(image.size.width * image.size.height) * 4) }
        return image
    }
}

struct BookCover: View {
    let book: LibraryBook
    var width: CGFloat = 62
    var height: CGFloat? = nil
    @Environment(\.colorScheme) private var scheme
    @State private var image: UIImage?
    var body: some View {
        Group {
            if let image { Image(uiImage: image).resizable().scaledToFill() }
            else { ZStack { NativeTheme.sunk; Text(String(book.title.prefix(1))).font(NativeTheme.font(width * 0.27)).foregroundStyle(NativeTheme.muted) } }
        }
        .frame(width: width, height: height ?? width * 1.45)
        .clipShape(RoundedRectangle(cornerRadius: 6))
        .shadow(color: .black.opacity(scheme == .dark ? 0 : 0.12), radius: 5, y: 2)
        .accessibilityHidden(true)
        .task(id: "\(book.coverPath ?? ""):\(book.updatedAt):\(width)") {
            guard let path = book.coverPath else { image = nil; return }
            let result = await NativeCoverCache.image(path: path, version: book.updatedAt, pixels: Int(width * 3))
            if !Task.isCancelled { image = result }
        }
    }
}

struct ReaderSurface: UIViewControllerRepresentable {
    let controller: UIViewController
    func makeUIViewController(context: Context) -> UIViewController { controller }
    func updateUIViewController(_ uiViewController: UIViewController, context: Context) {}
}
