import Foundation

enum JSONValue: Codable {
    case null, bool(Bool), number(Double), string(String), array([JSONValue]), object([String: JSONValue])
    init(from decoder: Decoder) throws {
        let container = try decoder.singleValueContainer()
        if container.decodeNil() { self = .null }
        else if let value = try? container.decode(Bool.self) { self = .bool(value) }
        else if let value = try? container.decode(String.self) { self = .string(value) }
        else if let value = try? container.decode(Double.self) { self = .number(value) }
        else if let value = try? container.decode([JSONValue].self) { self = .array(value) }
        else { self = .object(try container.decode([String: JSONValue].self)) }
    }
    func encode(to encoder: Encoder) throws {
        var container = encoder.singleValueContainer()
        switch self {
        case .null: try container.encodeNil()
        case .bool(let value): try container.encode(value)
        case .number(let value): try container.encode(value)
        case .string(let value): try container.encode(value)
        case .array(let value): try container.encode(value)
        case .object(let value): try container.encode(value)
        }
    }
    var foundationValue: Any {
        switch self {
        case .null: return NSNull()
        case .bool(let value): return value
        case .number(let value): return value
        case .string(let value): return value
        case .array(let value): return value.map(\.foundationValue)
        case .object(let value): return value.mapValues(\.foundationValue)
        }
    }
}

func requestObject<Value: Encodable>(_ value: Value) throws -> Any {
    try JSONSerialization.jsonObject(with: JSONEncoder().encode(value))
}

struct CreatedFolder: Decodable { let id: String; let name: String }
struct CoverResult: Decodable { let cover: String? }
struct MessageResult: Decodable { let message: String; let warning: String? }
struct TextResult: Decodable { let text: String }
struct ProfileSummary: Decodable { let version: String; let sync: String; let ai: String; let kindle: String; let storage: String }

struct SyncForm: Codable {
    var provider = 0
    var enabled = false
    var automaticSyncEnabled = true
    var downloadBookFilesOnSync = false
    var endpoint = ""
    var accessKey = ""
    var secretKey = ""
    var webDavEndpoint = ""
    var webDavUsername = ""
    var webDavPassword = ""
    var bucket = ""
    var region = "us-east-1"
    var pathStyle = false
    var skipTlsVerify = false
    var prefix = "kkindle"
    var encryptionKey = ""
}
struct SyncFormSnapshot: Decodable { let settings: SyncForm; let summary: String }

struct AiForm: Codable {
    var provider = "deepseek"
    var baseUrl = "https://api.deepseek.com"
    var model = ""
    var apiKey = ""
}
struct AiDefaults: Decodable { let baseUrl: String; let model: String }

struct EmailForm: Codable {
    var kindleEmailAddress = ""
    var senderEmailAddress = ""
    var smtpHost = ""
    var smtpPort = 587
    var smtpUsername = ""
    var smtpPassword = ""
    var enableSsl = true
}

struct MaterialRecord: Decodable, Identifiable {
    let id: String
    let bookId: String
    let bookTitle: String
    let kind: String
    let quote: String
    let note: String
    let locationText: String
    let updatedAt: String
    let location: JSONValue
    var kindLabel: String { kind == "note" ? "笔记" : kind == "bookmark" ? "书签" : "划线" }
}

struct DashboardBook: Decodable, Identifiable {
    let bookId: String
    let title: String
    let progressPercent: Double
    let cumulativeSeconds: Int64
    let isInLibrary: Bool
    var id: String { bookId }
}
struct ReadingDay: Decodable, Identifiable { let date: String; let activeSeconds: Int64; var id: String { date } }
struct ReadingDashboard: Decodable {
    let booksStarted: Int
    let booksFinished: Int
    let totalSeconds: Int64
    let averageProgress: Double
    let bookmarkCount: Int
    let annotationCount: Int
    let recentBooks: [DashboardBook]
    let dailyReading: [ReadingDay]
    let books: [DashboardBook]
    let mostReadBooks: [DashboardBook]
}
struct Medal: Decodable, Identifiable {
    let name: String
    let rule: String
    let isUnlocked: Bool
    let fraction: Double
    var id: String { name }
}
struct DashboardSnapshot: Decodable { let dashboard: ReadingDashboard; let medals: [Medal]; let streak: Int }
struct TrashRecord: Decodable, Identifiable { let id: String; let title: String; let detail: String; let deletedAt: String }

struct ReaderToc: Decodable, Identifiable { let id: Int; let title: String; let level: Int; let selected: Bool }
struct ReaderSearchHit: Decodable, Identifiable { let id: Int; let title: String; let excerpt: String }
struct ReaderDialog: Decodable, Equatable { let kind: String; let title: String; let text: String; let annotationId: String?; let note: String? }
struct SpeechVoice: Decodable, Identifiable { let id: String; let name: String; let culture: String? }
struct SpeechForm: Decodable { var voice: String; var speed: Double; var autoAdvance: Bool }
struct SpeechSnapshot: Decodable { let settings: SpeechForm; let voices: [SpeechVoice] }
struct DictionaryRecord: Decodable, Identifiable { let id: String; let name: String; let entryCount: Int; let enabled: Bool }
struct DictionaryEntry: Decodable { let term: String; let definition: String; let dictionaryName: String }
struct TxtReview: Decodable, Identifiable { let id: Int64; let name: String }
struct TxtRules: Codable, Equatable { var encoding = 0; var headingPreset = 0; var customHeadingPattern = ""; var recognizeVolumes = true }
struct TxtChapter: Decodable, Identifiable { let number: Int; let title: String; let isVolume: Bool; var id: Int { number } }
struct TxtPreview: Decodable { let chapterCount: Int; let detectedEncoding: String; let chapters: [TxtChapter]; let hasPreviousPage: Bool; let hasNextPage: Bool }

func readingDuration(_ seconds: Int64) -> String {
    seconds < 3600 ? String(format: "%.0f 分钟", Double(seconds) / 60) : String(format: "%.1f 小时", Double(seconds) / 3600)
}
