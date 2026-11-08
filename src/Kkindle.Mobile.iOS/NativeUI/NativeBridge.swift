import Foundation
import SwiftUI
import UIKit

public typealias ManagedRequest = @convention(c) (Int64, UnsafePointer<CChar>?) -> Void

struct NativeFailure: LocalizedError {
    let message: String
    var errorDescription: String? { message }
}

private struct Reply<Value: Decodable>: Decodable {
    let ok: Bool
    let result: Value?
    let error: String?
}

struct Done: Decodable {}

struct MobilePreferences: Codable, Equatable {
    var themeMode = 0
    var listView = false
    var collectionsView = false
    var pdfZoom = 1.0
    var sort = 0
    var readerTheme = 0
    var readerFontScale = 1.35
    var readerLineHeight = 1.75
    var readerParagraphSpacing = 0.0
    var readerBodyPadding = 24.0
    var readerParagraphIndent = false
    var readerVerticalWriting = false
    var readerPageAnimation = 1

    var colorScheme: ColorScheme? {
        themeMode == 1 ? .light : themeMode == 2 ? .dark : nil
    }
    /// The page colours from `ReaderPalette`. SwiftUI fills the margins around
    /// the rendered page, so any difference shows as a band above and below it.
    var paper: Color {
        switch readerTheme {
        case 1: return Color(red: 244 / 255, green: 242 / 255, blue: 236 / 255)
        case 2: return Color(red: 238 / 255, green: 243 / 255, blue: 236 / 255)
        case 5: return Color(red: 14 / 255, green: 14 / 255, blue: 14 / 255)
        default: return .white
        }
    }
}

struct LibraryBook: Decodable, Identifiable, Equatable {
    let id: String
    let title: String
    let authors: String
    let formats: String
    let size: String
    let coverPath: String?
    let progress: Double
    let favorite: Bool
    let readingStatus: Int
    let collectionIds: [String]
    let updatedAt: String
    let createdAt: String
    let description: String?
    let series: String?
    let publisher: String?
    let publishDate: String?
    let isbn: String?
    let pageCount: String?
    let binding: String?
    let tags: String
    let category: String
    let onlineRating: Double?
}

struct LibraryFolder: Decodable, Identifiable, Equatable {
    let id: String
    let name: String
    let count: Int
    let automatic: Bool
}

struct LibrarySnapshot: Decodable {
    let books: [LibraryBook]
    let collections: [LibraryFolder]
    let preferences: MobilePreferences
    let continueBookId: String?
}

struct NativePrompt: Decodable, Identifiable {
    let id: Int64
    let title: String
    let message: String
    let confirm: String
}

struct ReaderSnapshot: Decodable, Equatable {
    var ready = false
    var chromeVisible = true
    var isPdf = false
    var title = ""
    var chapter = ""
    var position = ""
    var progress = 0.0
    var bookmark = false
    var error: String?
    var speechState = 0
    var speechStatus = ""
    var selectedText: String?
}

private struct NativeEvent: Decodable {
    let type: String
    let message: String?
    let reader: ReaderSnapshot?
    let prompt: NativePrompt?
    let preferences: MobilePreferences?
    let id: Int64?
    let dialog: ReaderDialog?
    let txtImport: TxtReview?
    let busy: Bool?
    let channel: String?
    let sessionId: String?
    let text: String?
    let enabled: Bool?
}

/// Reader state changes on page turns, speech progress and every AI token.
/// Only the reader observes it, so the library tabs below are not re-rendered.
@MainActor
final class NativeReaderSession: ObservableObject {
    @Published var state = ReaderSnapshot()
    @Published var dialog: ReaderDialog?
    @Published var aiStreams: [String: String] = [:]
}

@MainActor
final class NativeStore: ObservableObject {
    let handler: ManagedRequest
    let readerController: UIViewController
    @Published var library: [LibraryBook] = []
    @Published var folders: [LibraryFolder] = []
    @Published var preferences = MobilePreferences()
    @Published var continueBookId: String?
    @Published var readerBook: LibraryBook?
    @Published var openingReader = false
    @Published var developerMode = false
    @Published var account: AccountStatus?
    let session = NativeReaderSession()
    @Published var prompt: NativePrompt?
    @Published var toast: String?
    @Published var failure: String? {
        didSet { if let failure { NativeAlerts.error(failure) { [weak self] in self?.failure = nil } } }
    }
    @Published var restartRequired = false
    @Published var dataBusy = false
    @Published var busyMessage = ""
    @Published var txtReview: TxtReview?
    @Published var syncStatus = ""
    private var nextId: Int64 = 0
    private var pending: [Int64: (Data) -> Void] = [:]
    private var toastTask: Task<Void, Never>?
    private var preferenceRevision = 0
    private var preferenceWrites = 0

    init(handler: @escaping ManagedRequest, readerController: UIViewController) {
        self.handler = handler
        self.readerController = readerController
    }

    func request<Value: Decodable>(_ command: String, _ args: [String: Any] = [:]) async throws -> Value {
        nextId += 1
        let id = nextId
        let data = try JSONSerialization.data(withJSONObject: ["command": command, "args": args])
        let json = String(decoding: data, as: UTF8.self)
        return try await withTaskCancellationHandler(operation: {
            try await withCheckedThrowingContinuation { continuation in
                pending[id] = { data in
                    do {
                        let reply = try JSONDecoder().decode(Reply<Value>.self, from: data)
                        if reply.ok, let value = reply.result { continuation.resume(returning: value) }
                        else { continuation.resume(throwing: NativeFailure(message: reply.error ?? "操作失败")) }
                    } catch { continuation.resume(throwing: error) }
                }
                if Task.isCancelled { cancel(id); return }
                json.withCString { handler(id, $0) }
            }
        }, onCancel: {
            Task { @MainActor in self.cancel(id) }
        })
    }

    private func cancel(_ id: Int64) {
        guard let completion = pending.removeValue(forKey: id) else { return }
        let data = Data("{\"ok\":false,\"error\":\"操作已取消\"}".utf8)
        completion(data)
        "{\"command\":\"cancel\"}".withCString { handler(id, $0) }
    }

    func resolve(_ id: Int64, json: String) {
        pending.removeValue(forKey: id)?(Data(json.utf8))
    }

    func receive(_ json: String) {
        guard let event = try? JSONDecoder().decode(NativeEvent.self, from: Data(json.utf8)) else { return }
        switch event.type {
        case "reader": if let reader = event.reader, reader != session.state { session.state = reader }
        case "preferences": if let value = event.preferences, value != preferences { preferences = value }
        case "developer": developerMode = event.enabled ?? false
        case "libraryChanged": perform { try await self.refreshLibrary() }
        case "confirm": if let prompt = event.prompt { self.prompt = prompt; NativeAlerts.confirm(prompt, store: self) }
        case "confirmDismissed": if let id = event.id { NativeAlerts.dismissPrompt(id); if prompt?.id == id { prompt = nil } }
        case "readerDialog": session.dialog = event.dialog
        case "txtImport": txtReview = event.txtImport
        case "dataBusy": dataBusy = event.busy ?? false; busyMessage = event.message ?? ""
        case "status": if event.channel == "sync" { syncStatus = event.message ?? "" }
        case "aiText": if let id = event.sessionId { session.aiStreams[id] = event.text ?? "" }
        case "restart": restartRequired = true
        case "toast": showToast(event.message ?? "")
        case "accountInvalidated": account = nil
        default: break
        }
    }

    func refreshLibrary() async throws {
        let revision = preferenceRevision
        let value: LibrarySnapshot = try await request("library.get")
        // Every assignment re-renders all tabs, even with an equal value.
        if library != value.books { library = value.books }
        if folders != value.collections { folders = value.collections }
        if revision == preferenceRevision && preferenceWrites == 0 && preferences != value.preferences {
            preferences = value.preferences
        }
        if continueBookId != value.continueBookId { continueBookId = value.continueBookId }
    }

    /// A Binding setter updates the native control immediately. Persistence and
    /// reader reflow complete asynchronously; older replies cannot undo a tap.
    func setPreferences(_ value: MobilePreferences) {
        guard value != preferences else { return }
        let previous = preferences
        preferenceRevision += 1
        let revision = preferenceRevision
        preferences = value
        preferenceWrites += 1
        perform {
            defer { self.preferenceWrites -= 1 }
            do {
                let object = try JSONSerialization.jsonObject(with: JSONEncoder().encode(value))
                let saved: MobilePreferences = try await self.request("preferences.save", ["preferences": object])
                if self.preferenceRevision == revision { self.preferences = saved }
            } catch {
                if self.preferenceRevision == revision { self.preferences = previous }
                throw error
            }
        }
    }

    func openReader(_ book: LibraryBook, location: [String: Any]? = nil) async throws {
        guard !openingReader, readerBook == nil else { return }
        openingReader = true
        defer { openingReader = false }
        session.state = ReaderSnapshot()
        session.dialog = nil
        var args: [String: Any] = ["bookId": book.id]
        if let location { args["location"] = location }
        let _: Done = try await request("reader.open", args)
        readerBook = book
    }

    func closeReader() async {
        do { let _: Done = try await request("reader.close") }
        catch { if !Task.isCancelled { failure = error.localizedDescription }; return }
        readerBook = nil
        do { try await refreshLibrary() }
        catch { if !Task.isCancelled { failure = error.localizedDescription } }
    }

    func perform(_ work: @escaping @MainActor () async throws -> Void) {
        Task {
            do { try await work() }
            catch { if !Task.isCancelled { failure = error.localizedDescription } }
        }
    }

    func command(_ command: String, _ args: [String: Any] = [:], message: String? = nil) async throws {
        let _: Done = try await request(command, args)
        if let message { showToast(message) }
    }

    func showToast(_ text: String) {
        toastTask?.cancel()
        toast = text
        toastTask = Task {
            do { try await Task.sleep(nanoseconds: 3_000_000_000) }
            catch { return }
            toast = nil
        }
    }

    func answerPrompt(_ value: NativePrompt, accepted: Bool) {
        prompt = nil
        perform { try await self.command("ui.confirm", ["id": value.id, "accepted": accepted]) }
    }
}

@MainActor
private enum NativeRuntime {
    static var store: NativeStore?
}

@_cdecl("kk_ios_create_shell")
@MainActor
public func createNativeShell(_ handler: ManagedRequest?, _ readerPointer: UnsafeMutableRawPointer?) -> UnsafeMutableRawPointer? {
    guard let handler, let readerPointer else { return nil }
    NativeTheme.configureControls()
    let readerController = Unmanaged<UIViewController>.fromOpaque(readerPointer).takeUnretainedValue()
    let store = NativeStore(handler: handler, readerController: readerController)
    NativeRuntime.store = store
    let controller = UIHostingController(rootView: NativeShell(store: store))
    controller.view.backgroundColor = .systemBackground
    controller.view.tintColor = .label
    return Unmanaged.passRetained(controller).toOpaque()
}

@_cdecl("kk_ios_release_shell")
@MainActor
public func releaseNativeShell(_ pointer: UnsafeMutableRawPointer?) {
    if let pointer { Unmanaged<UIViewController>.fromOpaque(pointer).release() }
}

@_cdecl("kk_ios_resolve_request")
@MainActor
public func resolveNativeRequest(_ id: Int64, _ json: UnsafePointer<CChar>?) {
    if let json { NativeRuntime.store?.resolve(id, json: String(cString: json)) }
}

@_cdecl("kk_ios_publish_event")
@MainActor
public func publishNativeEvent(_ json: UnsafePointer<CChar>?) {
    if let json { NativeRuntime.store?.receive(String(cString: json)) }
}
