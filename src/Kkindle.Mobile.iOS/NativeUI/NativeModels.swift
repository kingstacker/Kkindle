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
struct ProfileSummary: Decodable { let version: String; let sync: String; let ai: String; let kindle: String; let storage: String; let account: AccountStatus }
struct AccountUser: Decodable { let id: String; let username: String; let email: String?; let emailVerified: Bool? }
struct AccountStatus: Decodable {
    let serverUrl: String; let user: AccountUser?; let expiresAt: String?; let isLoggedIn: Bool
    let region: Int; let requiresDataRestart: Bool
    var regionName: String { region == 1 ? "国际区" : "中国区" }
}

struct AccountDevice: Decodable, Identifiable {
    let deviceId: String; let name: String; let platform: String; let state: String
    let registeredAt: String; let lastSeenAt: String
    var id: String { deviceId }
}
struct AccountMembership: Decodable {
    let accountId: String; let region: Int; let tier: String; let hasLifetimeFeatures: Bool
    let proExpiresAt: String?; let deviceLimit: Int; let loggedInDeviceCount: Int; let activatedDeviceCount: Int
    let uploadBytes: Int64; let uploadQuota: Int64; let downloadBytes: Int64; let downloadRemainingBytes: Int64
    let downloadCreditBytes: Int64; let storageBytes: Int64; let storageQuota: Int64; let storageAddonNextExpiresAt: String?
    let plusRenewalLimitAt: String?; let pendingPlusDays: Int?
    let plusDownloadQuotaBytes: Int64?; let plusDownloadUsedBytes: Int64?; let plusDownloadExpiresAt: String?
    let plusEarlyDownloadQuotaBytes: Int64?; let plusEarlyDownloadUsedBytes: Int64?
    let plusEarlyDownloadEndsAt: String?
    let metadataDownloadQuotaBytes: Int64?; let metadataDownloadUsedBytes: Int64?
    let metadataDownloadResetsAt: String?
    let trialAvailable: Bool?
    let trialClaimed: Bool?
    let trialClaimedAt: String?
    let trialExpiresAt: String?
    var tierName: String { tier == "pro" ? "Plus 会员" : tier == "lifetime" ? "功能解锁" : "Free" }
    /// The plan as sold, like the desktop: a lapsed Plus no longer counts as Plus.
    var plan: String {
        if tier == "pro", let expiry = Self.date(proExpiresAt), expiry > Date() { return "plus" }
        return tier == "lifetime" ? "lifetime" : "free"
    }
    var planName: String { plan == "plus" ? "Plus 会员" : plan == "lifetime" ? "功能解锁" : "Free" }
    var isPlusExpired: Bool { Self.date(proExpiresAt).map { $0 <= Date() } ?? false }
    var plusRenewalMessage: String? {
        guard plan == "plus", let expiry = Self.date(proExpiresAt), expiry > Date() else { return nil }
        let remaining = expiry.timeIntervalSinceNow
        guard remaining <= 30 * 86_400 else { return nil }
        let days = max(1, Int(ceil(remaining / 86_400)))
        if isTrialPlus {
            return days <= 1
                ? "Plus 免费试用将于 \(Self.day(expiry)) 到期（不足 1 天）。到期后 Kkindle 云同步、下载及自备 S3 / WebDAV 将停用，试用期云端数据保留 7 天；开通正式 Plus 可继续使用。请及时开通。"
                : "Plus 免费试用将于 \(Self.day(expiry)) 到期（剩余 \(days) 天）。到期后 Kkindle 云同步、下载及自备 S3 / WebDAV 将停用，试用期云端数据保留 7 天；开通正式 Plus 可继续使用。请及时开通。"
        }
        return days <= 1
            ? "Plus 将于 \(Self.day(expiry)) 到期（不足 1 天）。到期后 Kkindle 云同步、下载及自备 S3 / WebDAV 将停用，云端数据保留一个月；已购买功能解锁的自备同步不受影响。请及时续费。"
            : "Plus 将于 \(Self.day(expiry)) 到期（剩余 \(days) 天）。到期后 Kkindle 云同步、下载及自备 S3 / WebDAV 将停用，云端数据保留一个月；已购买功能解锁的自备同步不受影响。请及时续费。"
    }
    var isPlusExpiringSoon: Bool { plusRenewalMessage != nil }
    var trialExpiresAtDate: Date? { Self.date(trialExpiresAt) }
    var isTrialActive: Bool { trialClaimed == true && (trialExpiresAtDate.map { $0 > Date() } ?? false) }
    var canClaimTrial: Bool { trialAvailable == true }
    var trialStatusMessage: String {
        guard isTrialActive, let end = trialExpiresAtDate else {
            return "只有主动打开领取开关才会领取；试用到期后，云端数据保留 7 天后自动销毁。"
        }
        return "试用期至 \(Self.day(end))，到期后云端数据保留 7 天后自动销毁。可随时开通正式 Plus 继续使用。"
    }
    /// The active or most recent Plus period was the free trial rather than a paid subscription.
    private var isTrialPlus: Bool {
        guard let trial = trialExpiresAtDate else { return false }
        return Self.date(proExpiresAt) == trial
    }
    var planExpiryText: String {
        if plan == "plus", let expiry = Self.date(proExpiresAt) { return "有效至 " + Self.day(expiry) }
        return plan == "lifetime" ? "永久有效" : isPlusExpired ? "Plus 已到期" : "永久免费"
    }
    var plusDownloadPeriodText: String? { Self.date(plusDownloadExpiresAt).map { "本期有效至 " + Self.day($0) } }
    var plusEarlyDownloadEndText: String? {
        guard let end = Self.date(plusEarlyDownloadEndsAt), end > Date() else { return nil }
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.calendar = Calendar(identifier: .gregorian)
        formatter.dateFormat = "yyyy/MM/dd HH:mm"
        return formatter.string(from: end)
    }
    /// The desktop's expiry notice. Paid cloud data is retained for one UTC calendar month;
    /// a lapsed free trial keeps it for 7 days.
    var plusExpiryMessage: String? {
        guard let expiry = Self.date(proExpiresAt), expiry <= Date() else { return nil }
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(identifier: "UTC")!
        if isTrialPlus {
            guard let retention = calendar.date(byAdding: .day, value: 7, to: expiry) else { return nil }
            return Date() < retention
                ? "Plus 免费试用已于 \(Self.day(expiry)) 到期。Kkindle 云同步、下载及试用期间使用的自备 S3 / WebDAV 已停用；已购买的功能解锁不受影响。试用期云端书籍及其他同步数据保留至 \(Self.day(retention))；在保留期内开通 Plus 可恢复访问，保留期结束后自动销毁。"
                : "Plus 免费试用已于 \(Self.day(expiry)) 到期。Kkindle 云同步、下载及试用期间使用的自备 S3 / WebDAV 已停用；已购买的功能解锁不受影响。试用期云端数据的 7 天保留期已结束，数据已进入自动销毁流程。"
        }
        guard let retention = calendar.date(byAdding: .month, value: 1, to: expiry) else { return nil }
        return Date() < retention
            ? "Plus 已于 \(Self.day(expiry)) 到期。Kkindle 云同步、下载及 Plus 期间使用的自备 S3 / WebDAV 已停用；已购买的功能解锁不受影响。云端书籍及其他同步数据保留至 \(Self.day(retention))；在保留期内续费可恢复访问，保留期结束后自动销毁。"
            : "Plus 已于 \(Self.day(expiry)) 到期。Kkindle 云同步、下载及 Plus 期间使用的自备 S3 / WebDAV 已停用；已购买的功能解锁不受影响。云端书籍及其他同步数据的一个月保留期已结束，数据已进入自动销毁流程。"
    }
    func canExtendPlus(days: Int) -> Bool {
        guard days > 0 && days <= 1825 && (pendingPlusDays ?? 0) >= 0 else { return false }
        let now = Date()
        let start = max(Self.date(proExpiresAt) ?? now, now)
        let limit = Self.date(plusRenewalLimitAt) ?? Calendar(identifier: .gregorian).date(byAdding: .year, value: 5, to: now)!
        return start.addingTimeInterval(Double(days + (pendingPlusDays ?? 0)) * 86_400) <= limit
    }
    private static func date(_ text: String?) -> Date? {
        guard let text else { return nil }
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        if let date = formatter.date(from: text) { return date }
        formatter.formatOptions = [.withInternetDateTime]
        return formatter.date(from: text)
    }
    private static func day(_ date: Date) -> String {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.calendar = Calendar(identifier: .gregorian)
        formatter.dateFormat = "yyyy/MM/dd"
        return formatter.string(from: date)
    }
}
struct DeviceFeatureStatus: Decodable {
    let membership: AccountMembership; let devices: [AccountDevice]; let currentDeviceId: String
    let isCurrentDeviceActive: Bool; let canUseFeatures: Bool
    let canUseAccountCloudSync: Bool?
}
struct ActivationCodeRedemption: Decodable {
    let codeId: String; let kind: String; let years: Int; let redeemedAt: String; let status: DeviceFeatureStatus
    let alreadyRedeemed: Bool?
}
struct MembershipProduct: Decodable, Identifiable {
    let id: String; let name: String; let kind: String; let priceMinor: Int; let currency: String
    let durationDays: Int; let bytes: Int64; let description: String
    /// The desktop's display format: "¥128", "¥88.5", or "12 XYZ" for other currencies.
    var price: String {
        let formatter = NumberFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.numberStyle = .decimal
        formatter.minimumFractionDigits = 0; formatter.maximumFractionDigits = 2
        formatter.usesGroupingSeparator = false
        let amount = formatter.string(from: NSNumber(value: Double(priceMinor) / 100)) ?? String(Double(priceMinor) / 100)
        switch currency {
        case "CNY": return "¥" + amount
        case "USD": return "$" + amount
        case "EUR": return "€" + amount
        default: return amount + " " + currency
        }
    }
}
struct PurchaseOrder: Decodable, Identifiable {
    let id: String; let accountId: String; let productId: String; let productName: String; let kind: String
    let priceMinor: Int; let currency: String; let durationDays: Int; let bytes: Int64; let state: String
    let createdAt: String; let expiresAt: String; let fulfilledAt: String?
    var isExpired: Bool {
        guard state == "pending" else { return false }
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        var expiry = formatter.date(from: expiresAt)
        if expiry == nil { formatter.formatOptions = [.withInternetDateTime]; expiry = formatter.date(from: expiresAt) }
        return expiry.map { $0 <= Date() } ?? false
    }
    var stateName: String { state == "fulfilled" ? "已到账" : state == "cancelled" ? "已取消" : isExpired ? "已过期" : "待确认付款" }
}

struct SyncForm: Codable {
    var provider = 0
    var providerChosenByUser = false
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
    var accountRegion = 0
    var accountServerUrl = ""
    var accountUserId = "00000000-0000-0000-0000-000000000000"
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
    let icon: String
    let isUnlocked: Bool
    let fraction: Double
    var id: String { name }
}
struct ReadingPeriodBucket: Decodable, Identifiable {
    let startDate: String; let endDate: String; let activeSeconds: Int64
    var id: String { startDate }
    func contains(_ date: String) -> Bool { date >= startDate && date <= endDate }
}
struct DashboardSnapshot: Decodable {
    let dashboard: ReadingDashboard; let medals: [Medal]; let streak: Int
    let period: Int; let today: String; let buckets: [ReadingPeriodBucket]
}
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

/// Account storage and transfer sizes in the desktop's format, such as "1.5 GB".
func memberSize(_ bytes: Int64) -> String {
    let value = max(0, bytes)
    let (amount, unit): (Double, String) = value >= 1_073_741_824 ? (Double(value) / 1_073_741_824, "GB")
        : value >= 1_048_576 ? (Double(value) / 1_048_576, "MB")
        : value >= 1024 ? (Double(value) / 1024, "KB") : (Double(value), "B")
    let formatter = NumberFormatter()
    formatter.locale = Locale(identifier: "en_US_POSIX")
    formatter.numberStyle = .decimal
    formatter.minimumFractionDigits = 0; formatter.maximumFractionDigits = 2
    formatter.usesGroupingSeparator = false
    return (formatter.string(from: NSNumber(value: amount)) ?? String(amount)) + " " + unit
}

/// The desktop usage ring's percentage, including its "<0.1%" floor.
func memberPercent(_ ratio: Double) -> String {
    let percent = max(0, ratio) * 100
    if percent > 0 && percent < 0.1 { return "<0.1%" }
    let text = String(format: "%.1f", percent)
    return (text.hasSuffix(".0") ? String(text.dropLast(2)) : text) + "%"
}

func readingDuration(_ seconds: Int64) -> String {
    seconds < 3600 ? String(format: "%.0f 分钟", Double(seconds) / 60) : String(format: "%.1f 小时", Double(seconds) / 3600)
}
