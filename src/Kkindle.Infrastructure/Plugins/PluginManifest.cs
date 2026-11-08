using System.Text.Json;
using System.Text.RegularExpressions;

namespace Kkindle.Infrastructure.Plugins;

/// <summary>What a plugin may touch in the library.</summary>
public enum PluginLibraryAccess
{
    None = 0,
    Read = 1,
    Write = 2
}

/// <summary>A page that a plugin adds to a book's context menu.</summary>
public sealed record PluginBookAction(
    string Id,
    string Title,
    string Page,
    bool AllowsMultipleBooks,
    int Width,
    int Height);

/// <summary>An optional page shown from the plugin's entry in settings.</summary>
public sealed record PluginSettingsPage(string Page, int Width, int Height);

/// <summary>
/// A standalone page that is not tied to books, opened from the plugin's row
/// in settings (for example an online catalogue browser).
/// </summary>
public sealed record PluginAppPage(string Title, string Page, int Width, int Height);

/// <summary>A standalone command shown in the library toolbar's plugin menu.</summary>
public sealed record PluginCommand(string Id, string Title, string Page, int Width, int Height);

/// <summary>An entry appended to the reader's selection toolbar.</summary>
public sealed record PluginReaderAction(string Id, string Title, string Page, int Width, int Height);

/// <summary>
/// A headless lookup source shown in the reader's dictionary popup. The
/// script runs in the plugin script runtime and answers <c>lookup</c> calls.
/// </summary>
public sealed record PluginDictionaryProvider(string Id, string Title, string Script);

/// <summary>A headless translation engine offered in the reader's translation popup.</summary>
public sealed record PluginTranslatorProvider(string Id, string Title, string Script);

/// <summary>
/// A script run without a window: once after startup and then every
/// <see cref="IntervalMinutes"/> minutes (0 means startup only).
/// </summary>
public sealed record PluginBackgroundTask(string Script, int IntervalMinutes, bool RunOnStartup);

/// <summary>Display strings for one language; missing entries fall back to the manifest defaults.</summary>
public sealed record PluginLocalization(
    string? Name,
    string? Description,
    IReadOnlyDictionary<string, string> Titles);

/// <summary>
/// The validated contents of a plugin's <c>manifest.json</c>. Construction
/// goes through <see cref="Parse"/> so every instance has passed validation.
/// </summary>
public sealed partial record PluginManifest
{
    public const string FileName = "manifest.json";
    public const int SupportedApiVersion = 1;

    /// <summary>
    /// Shortest allowed background interval. Plugins cannot poll faster than
    /// this; the scheduler also enforces it.
    /// </summary>
    public const int MinimumBackgroundIntervalMinutes = 15;
    private const int DefaultPageWidth = 960;
    private const int DefaultPageHeight = 680;

    private PluginManifest() { }

    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Version { get; init; }
    public string Description { get; init; } = string.Empty;
    public string Author { get; init; } = string.Empty;
    public int ApiVersion { get; init; }
    public string? Icon { get; init; }
    public IReadOnlyList<PluginNetworkOrigin> NetworkOrigins { get; init; } = [];
    public bool HasNetworkPermission { get; init; }
    public PluginLibraryAccess LibraryAccess { get; init; }

    /// <summary>
    /// Grants read access to the whole library (search, collections, reading
    /// data) instead of only the books the page was opened with. Collection
    /// changes additionally require <see cref="LibraryAccess"/> write.
    /// </summary>
    public bool HasLibraryScopeAll { get; init; }

    public bool CanImportLibrary { get; init; }
    public bool AllowsTranslation { get; init; }
    public bool AllowsPinyin { get; init; }
    public bool UsesStorage { get; init; }
    public bool UsesSecrets { get; init; }

    /// <summary>Reads chapter text and the table of contents of accessible books.</summary>
    public bool CanReadContent { get; init; }

    /// <summary>Sends prompts to the AI service the user configured in the app.</summary>
    public bool UsesAi { get; init; }

    /// <summary>Adds annotations and bookmarks to accessible books.</summary>
    public bool CanWriteReadingData { get; init; }

    /// <summary>Reads the raw bytes of accessible book files.</summary>
    public bool CanReadBookFiles { get; init; }

    /// <summary>Oldest application version the plugin supports, or null.</summary>
    public Version? MinAppVersion { get; init; }

    public string? Homepage { get; init; }

    /// <summary>Per-language display strings keyed by normalized language (<c>en-US</c>, <c>zh-CN</c>).</summary>
    public IReadOnlyDictionary<string, PluginLocalization> Localizations { get; init; } =
        new Dictionary<string, PluginLocalization>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<PluginDictionaryProvider> Dictionaries { get; init; } = [];
    public IReadOnlyList<PluginTranslatorProvider> Translators { get; init; } = [];
    public PluginBackgroundTask? Background { get; init; }
    public IReadOnlyList<PluginBookAction> BookActions { get; init; } = [];
    public IReadOnlyList<PluginCommand> Commands { get; init; } = [];
    public IReadOnlyList<PluginReaderAction> ReaderActions { get; init; } = [];
    public PluginSettingsPage? SettingsPage { get; init; }
    public PluginAppPage? AppPage { get; init; }

    public static PluginManifest Parse(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
        }
        catch (JsonException exception)
        {
            throw new PluginPackageException($"插件清单不是有效的 JSON：{exception.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new PluginPackageException("插件清单必须是 JSON 对象。");

            var id = RequiredString(root, "id");
            if (!IdRegex().IsMatch(id))
                throw new PluginPackageException("插件 id 只能包含小写字母、数字、点和短横线，长度 3–64 个字符。");
            var name = RequiredString(root, "name");
            if (name.Length > 60)
                throw new PluginPackageException("插件名称不能超过 60 个字符。");
            var version = RequiredString(root, "version");
            if (!TryParseVersion(version, out _))
                throw new PluginPackageException("插件版本号格式无效，应类似 1.0.0。");
            var apiVersion = root.TryGetProperty("apiVersion", out var apiElement) && apiElement.TryGetInt32(out var api)
                ? api
                : throw new PluginPackageException("插件清单缺少 apiVersion。");
            if (apiVersion != SupportedApiVersion)
                throw new PluginPackageException($"插件需要接口版本 {apiVersion}，当前应用支持版本 {SupportedApiVersion}。");

            var origins = new List<PluginNetworkOrigin>();
            var hasNetworkPermission = false;
            var libraryAccess = PluginLibraryAccess.None;
            var hasLibraryScopeAll = false;
            var canImportLibrary = false;
            var allowsTranslation = false;
            var allowsPinyin = false;
            var usesStorage = false;
            var usesSecrets = false;
            var canReadContent = false;
            var usesAi = false;
            var canWriteReadingData = false;
            var canReadBookFiles = false;
            if (root.TryGetProperty("permissions", out var permissions))
            {
                if (permissions.ValueKind != JsonValueKind.Object)
                    throw new PluginPackageException("permissions 必须是对象。");
                if (permissions.TryGetProperty("network", out var network))
                {
                    if (network.ValueKind != JsonValueKind.Array)
                        throw new PluginPackageException("permissions.network 必须是数组。");
                    hasNetworkPermission = true;
                    foreach (var origin in network.EnumerateArray())
                    {
                        var value = origin.ValueKind == JsonValueKind.String ? origin.GetString() : null;
                        origins.Add(PluginNetworkOrigin.Parse(value ?? string.Empty));
                    }
                }
                if (permissions.TryGetProperty("library", out var library))
                {
                    libraryAccess = library.GetString() switch
                    {
                        "read" => PluginLibraryAccess.Read,
                        "write" => PluginLibraryAccess.Write,
                        _ => throw new PluginPackageException("permissions.library 只能是 \"read\" 或 \"write\"。")
                    };
                }
                if (permissions.TryGetProperty("libraryAll", out var libraryAll))
                    hasLibraryScopeAll = libraryAll.ValueKind == JsonValueKind.True;
                if (permissions.TryGetProperty("import", out var import))
                    canImportLibrary = import.ValueKind == JsonValueKind.True;
                if (permissions.TryGetProperty("translation", out var translation))
                    allowsTranslation = translation.ValueKind == JsonValueKind.True;
                if (permissions.TryGetProperty("pinyin", out var pinyin))
                    allowsPinyin = pinyin.ValueKind == JsonValueKind.True;
                if (permissions.TryGetProperty("storage", out var storage))
                    usesStorage = storage.ValueKind == JsonValueKind.True;
                if (permissions.TryGetProperty("secrets", out var secrets))
                    usesSecrets = secrets.ValueKind == JsonValueKind.True;
                canReadContent = IsTrue(permissions, "content");
                usesAi = IsTrue(permissions, "ai");
                canWriteReadingData = IsTrue(permissions, "readingWrite");
                canReadBookFiles = IsTrue(permissions, "bookFiles");
            }

            var actions = new List<PluginBookAction>();
            var commands = new List<PluginCommand>();
            var readerActions = new List<PluginReaderAction>();
            PluginSettingsPage? settingsPage = null;
            PluginAppPage? appPage = null;
            var dictionaries = new List<PluginDictionaryProvider>();
            var translators = new List<PluginTranslatorProvider>();
            PluginBackgroundTask? background = null;
            if (root.TryGetProperty("contributes", out var contributes))
            {
                if (contributes.ValueKind != JsonValueKind.Object)
                    throw new PluginPackageException("contributes 必须是对象。");
                if (contributes.TryGetProperty("bookActions", out var bookActions))
                {
                    if (bookActions.ValueKind != JsonValueKind.Array)
                        throw new PluginPackageException("contributes.bookActions 必须是数组。");
                    foreach (var action in bookActions.EnumerateArray())
                    {
                        var actionId = RequiredString(action, "id");
                        if (!ActionIdRegex().IsMatch(actionId))
                            throw new PluginPackageException($"操作 id“{actionId}”无效。");
                        if (actions.Any(existing => existing.Id == actionId))
                            throw new PluginPackageException($"操作 id“{actionId}”重复。");
                        actions.Add(new PluginBookAction(
                            actionId,
                            RequiredString(action, "title"),
                            RequiredPagePath(action, "page"),
                            !action.TryGetProperty("multiple", out var multiple) || multiple.ValueKind != JsonValueKind.False,
                            ReadDimension(action, "width", DefaultPageWidth),
                            ReadDimension(action, "height", DefaultPageHeight)));
                    }
                }
                if (contributes.TryGetProperty("commands", out var commandElements))
                {
                    if (commandElements.ValueKind != JsonValueKind.Array)
                        throw new PluginPackageException("contributes.commands 必须是数组。");
                    foreach (var command in commandElements.EnumerateArray())
                    {
                        var commandId = RequiredString(command, "id");
                        if (!ActionIdRegex().IsMatch(commandId))
                            throw new PluginPackageException($"命令 id“{commandId}”无效。");
                        if (commands.Any(existing => existing.Id == commandId))
                            throw new PluginPackageException($"命令 id“{commandId}”重复。");
                        commands.Add(new PluginCommand(
                            commandId,
                            RequiredString(command, "title"),
                            RequiredPagePath(command, "page"),
                            ReadDimension(command, "width", DefaultPageWidth),
                            ReadDimension(command, "height", DefaultPageHeight)));
                    }
                }
                if (contributes.TryGetProperty("readerActions", out var readerActionElements))
                {
                    if (readerActionElements.ValueKind != JsonValueKind.Array)
                        throw new PluginPackageException("contributes.readerActions 必须是数组。");
                    foreach (var action in readerActionElements.EnumerateArray())
                    {
                        var actionId = RequiredString(action, "id");
                        if (!ActionIdRegex().IsMatch(actionId))
                            throw new PluginPackageException($"阅读操作 id“{actionId}”无效。");
                        if (readerActions.Any(existing => existing.Id == actionId))
                            throw new PluginPackageException($"阅读操作 id“{actionId}”重复。");
                        readerActions.Add(new PluginReaderAction(
                            actionId,
                            RequiredString(action, "title"),
                            RequiredPagePath(action, "page"),
                            ReadDimension(action, "width", DefaultPageWidth),
                            ReadDimension(action, "height", DefaultPageHeight)));
                    }
                }
                if (contributes.TryGetProperty("settings", out var settings))
                {
                    settingsPage = new PluginSettingsPage(
                        RequiredPagePath(settings, "page"),
                        ReadDimension(settings, "width", 640),
                        ReadDimension(settings, "height", 520));
                }
                if (contributes.TryGetProperty("app", out var app))
                {
                    var title = RequiredString(app, "title");
                    if (title.Length > 60)
                        throw new PluginPackageException("应用页面标题不能超过 60 个字符。");
                    appPage = new PluginAppPage(
                        title,
                        RequiredPagePath(app, "page"),
                        ReadDimension(app, "width", DefaultPageWidth),
                        ReadDimension(app, "height", DefaultPageHeight));
                }
                foreach (var (id2, title2, script) in ReadProviders(contributes, "dictionaries", "词典"))
                    dictionaries.Add(new PluginDictionaryProvider(id2, title2, script));
                foreach (var (id2, title2, script) in ReadProviders(contributes, "translators", "翻译"))
                    translators.Add(new PluginTranslatorProvider(id2, title2, script));
                if (contributes.TryGetProperty("background", out var backgroundElement))
                {
                    if (backgroundElement.ValueKind != JsonValueKind.Object)
                        throw new PluginPackageException("contributes.background 必须是对象。");
                    var interval = backgroundElement.TryGetProperty("intervalMinutes", out var intervalElement)
                        && intervalElement.TryGetInt32(out var minutes)
                            ? minutes
                            : 0;
                    if (interval < 0 || interval > 7 * 24 * 60)
                        throw new PluginPackageException("contributes.background.intervalMinutes 超出范围。");
                    if (interval is > 0 and < MinimumBackgroundIntervalMinutes)
                        throw new PluginPackageException(
                            $"后台任务间隔不能少于 {MinimumBackgroundIntervalMinutes} 分钟。");
                    var onStartup = !backgroundElement.TryGetProperty("onStartup", out var startupElement)
                        || startupElement.ValueKind != JsonValueKind.False;
                    if (interval == 0 && !onStartup)
                        throw new PluginPackageException("后台任务需要 intervalMinutes 或 onStartup。");
                    background = new PluginBackgroundTask(
                        RequiredScriptPath(backgroundElement, "script"),
                        interval,
                        onStartup);
                }
            }

            if (actions.Any() && libraryAccess == PluginLibraryAccess.None)
                throw new PluginPackageException("提供书籍操作的插件需要声明 permissions.library。");
            if ((allowsTranslation || allowsPinyin) && libraryAccess == PluginLibraryAccess.None)
                throw new PluginPackageException("翻译或注音插件需要声明 permissions.library。");
            if ((canReadContent || canWriteReadingData || canReadBookFiles) && libraryAccess == PluginLibraryAccess.None)
                throw new PluginPackageException("读取正文、书籍文件或写入阅读数据需要同时声明 permissions.library。");

            Version? minAppVersion = null;
            var minAppText = OptionalString(root, "minAppVersion");
            if (minAppText.Length > 0)
            {
                if (!TryParseVersion(minAppText, out var parsedMinimum))
                    throw new PluginPackageException("minAppVersion 格式无效，应类似 1.2.0。");
                minAppVersion = parsedMinimum;
            }

            var homepage = OptionalString(root, "homepage");
            if (homepage.Length > 0
                && (!Uri.TryCreate(homepage, UriKind.Absolute, out var homepageUri) || homepageUri.Scheme != Uri.UriSchemeHttps))
                throw new PluginPackageException("homepage 必须是 https:// 地址。");

            var localizations = ReadLocalizations(root);

            string? icon = null;
            if (root.TryGetProperty("icon", out var iconElement) && iconElement.ValueKind == JsonValueKind.String)
                icon = PluginPackage.NormalizeEntryPath(iconElement.GetString() ?? string.Empty);

            return new PluginManifest
            {
                Id = id,
                Name = name,
                Version = version,
                Description = OptionalString(root, "description"),
                Author = OptionalString(root, "author"),
                ApiVersion = apiVersion,
                Icon = icon,
                NetworkOrigins = origins,
                HasNetworkPermission = hasNetworkPermission,
                LibraryAccess = libraryAccess,
                HasLibraryScopeAll = hasLibraryScopeAll,
                CanImportLibrary = canImportLibrary,
                AllowsTranslation = allowsTranslation,
                AllowsPinyin = allowsPinyin,
                UsesStorage = usesStorage,
                UsesSecrets = usesSecrets,
                BookActions = actions,
                Commands = commands,
                ReaderActions = readerActions,
                SettingsPage = settingsPage,
                AppPage = appPage,
                CanReadContent = canReadContent,
                UsesAi = usesAi,
                CanWriteReadingData = canWriteReadingData,
                CanReadBookFiles = canReadBookFiles,
                MinAppVersion = minAppVersion,
                Homepage = homepage.Length == 0 ? null : homepage,
                Localizations = localizations,
                Dictionaries = dictionaries,
                Translators = translators,
                Background = background
            };
        }
    }

    /// <summary>Every page path the manifest refers to.</summary>
    public IEnumerable<string> PagePaths =>
        BookActions.Select(action => action.Page)
            .Concat(Commands.Select(command => command.Page))
            .Concat(ReaderActions.Select(action => action.Page))
            .Concat(SettingsPage is null ? [] : [SettingsPage.Page])
            .Concat(AppPage is null ? [] : [AppPage.Page])
            .Distinct(StringComparer.Ordinal);

    /// <summary>Every headless script the manifest refers to.</summary>
    public IEnumerable<string> ScriptPaths =>
        Dictionaries.Select(provider => provider.Script)
            .Concat(Translators.Select(provider => provider.Script))
            .Concat(Background is null ? [] : [Background.Script])
            .Distinct(StringComparer.Ordinal);

    /// <summary>True when the plugin needs the script runtime rather than only pages.</summary>
    public bool HasHeadlessContributions => Dictionaries.Count > 0 || Translators.Count > 0 || Background is not null;

    public string GetName(string? language) => FindLocalization(language)?.Name ?? Name;

    public string GetDescription(string? language) => FindLocalization(language)?.Description ?? Description;

    /// <summary>
    /// Localized title of a contribution. Keys are contribution ids; the app
    /// page uses the key <c>app</c>.
    /// </summary>
    public string GetTitle(string? language, string key, string fallback) =>
        FindLocalization(language)?.Titles.TryGetValue(key, out var title) == true ? title : fallback;

    private PluginLocalization? FindLocalization(string? language)
    {
        if (string.IsNullOrWhiteSpace(language) || Localizations.Count == 0) return null;
        if (Localizations.TryGetValue(language, out var exact)) return exact;
        var prefix = language.Split('-')[0];
        return Localizations
            .Where(pair => pair.Key.Split('-')[0].Equals(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Value)
            .FirstOrDefault();
    }

    public static bool TryParseVersion(string value, out Version version)
    {
        var core = value.Split('-', '+')[0];
        if (core.Count(character => character == '.') == 0) core += ".0";
        if (System.Version.TryParse(core, out var parsed))
        {
            version = parsed;
            return true;
        }
        version = null!;
        return false;
    }

    private static string RequiredString(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString()))
            return value.GetString()!.Trim();
        throw new PluginPackageException($"插件清单缺少字段“{name}”。");
    }

    private static string OptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? string.Empty
            : string.Empty;

    private static string RequiredPagePath(JsonElement element, string name)
    {
        var path = PluginPackage.NormalizeEntryPath(RequiredString(element, name));
        if (!path.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
            throw new PluginPackageException($"页面“{path}”必须是 .html 文件。");
        return path;
    }

    private static string RequiredScriptPath(JsonElement element, string name)
    {
        var path = PluginPackage.NormalizeEntryPath(RequiredString(element, name));
        if (!path.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
            throw new PluginPackageException($"脚本“{path}”必须是 .js 文件。");
        return path;
    }

    private static bool IsTrue(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static IEnumerable<(string Id, string Title, string Script)> ReadProviders(
        JsonElement contributes,
        string property,
        string label)
    {
        if (!contributes.TryGetProperty(property, out var elements)) yield break;
        if (elements.ValueKind != JsonValueKind.Array)
            throw new PluginPackageException($"contributes.{property} 必须是数组。");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in elements.EnumerateArray())
        {
            var id = RequiredString(element, "id");
            if (!ActionIdRegex().IsMatch(id))
                throw new PluginPackageException($"{label} id“{id}”无效。");
            if (!seen.Add(id))
                throw new PluginPackageException($"{label} id“{id}”重复。");
            var title = RequiredString(element, "title");
            if (title.Length > 40)
                throw new PluginPackageException($"{label}名称不能超过 40 个字符。");
            yield return (id, title, RequiredScriptPath(element, "script"));
        }
    }

    private static Dictionary<string, PluginLocalization> ReadLocalizations(JsonElement root)
    {
        var result = new Dictionary<string, PluginLocalization>(StringComparer.OrdinalIgnoreCase);
        if (!root.TryGetProperty("i18n", out var i18n)) return result;
        if (i18n.ValueKind != JsonValueKind.Object)
            throw new PluginPackageException("i18n 必须是对象。");
        foreach (var language in i18n.EnumerateObject())
        {
            if (!LanguageRegex().IsMatch(language.Name))
                throw new PluginPackageException($"i18n 语言代码“{language.Name}”无效，应类似 en-US。");
            if (language.Value.ValueKind != JsonValueKind.Object)
                throw new PluginPackageException($"i18n.{language.Name} 必须是对象。");
            var titles = new Dictionary<string, string>(StringComparer.Ordinal);
            if (language.Value.TryGetProperty("titles", out var titleElements))
            {
                if (titleElements.ValueKind != JsonValueKind.Object)
                    throw new PluginPackageException($"i18n.{language.Name}.titles 必须是对象。");
                foreach (var title in titleElements.EnumerateObject())
                {
                    if (title.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(title.Value.GetString()))
                        titles[title.Name] = Limit(title.Value.GetString()!.Trim(), 60);
                }
            }
            var name = OptionalString(language.Value, "name");
            var description = OptionalString(language.Value, "description");
            result[language.Name] = new PluginLocalization(
                name.Length == 0 ? null : Limit(name, 60),
                description.Length == 0 ? null : Limit(description, 2000),
                titles);
        }
        return result;
    }

    private static string Limit(string value, int length) => value.Length <= length ? value : value[..length];

    private static int ReadDimension(JsonElement element, string name, int fallback) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number)
            ? Math.Clamp(number, 320, 1600)
            : fallback;

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9.-]{1,62})[a-z0-9]$")]
    private static partial Regex IdRegex();

    [GeneratedRegex("^[A-Za-z0-9_-]{1,40}$")]
    private static partial Regex ActionIdRegex();

    [GeneratedRegex("^[a-z]{2,3}(?:-[A-Za-z0-9]{2,8})*$")]
    private static partial Regex LanguageRegex();
}

/// <summary>An HTTPS origin a plugin may call, optionally with a subdomain wildcard.</summary>
public sealed record PluginNetworkOrigin(string Host, bool IncludesSubdomains)
{
    public static PluginNetworkOrigin Parse(string value)
    {
        var text = value.Trim();
        if (!text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new PluginPackageException($"网络权限“{value}”必须以 https:// 开头。");
        var host = text["https://".Length..].TrimEnd('/');
        var wildcard = host.StartsWith("*.", StringComparison.Ordinal);
        if (wildcard) host = host[2..];
        if (host.Length == 0
            || host.Contains('/') || host.Contains(':') || host.Contains('*')
            || Uri.CheckHostName(host) != UriHostNameType.Dns
            || !host.Contains('.'))
            throw new PluginPackageException($"网络权限“{value}”不是有效的域名。");
        return new PluginNetworkOrigin(host.ToLowerInvariant(), wildcard);
    }

    public bool Allows(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps
        && uri.IsDefaultPort
        && (string.Equals(uri.IdnHost, Host, StringComparison.OrdinalIgnoreCase)
            || (IncludesSubdomains && uri.IdnHost.EndsWith("." + Host, StringComparison.OrdinalIgnoreCase)));

    public override string ToString() => IncludesSubdomains ? $"https://*.{Host}" : $"https://{Host}";
}

public sealed class PluginPackageException(string message) : Exception(message);
