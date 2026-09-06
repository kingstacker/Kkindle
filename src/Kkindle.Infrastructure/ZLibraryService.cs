using System.Globalization;
using System.Net;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using Kkindle.Core;

namespace Kkindle.Infrastructure;

public sealed class ZLibrarySettings
{
    public const string DefaultBaseUrl = "https://z-lib.gd";

    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = DefaultBaseUrl;

    public bool IsConfigured => Validate() is null;

    public ZLibrarySettings Clone() => new()
    {
        Email = Email,
        Password = Password,
        BaseUrl = BaseUrl
    };

    public static ZLibrarySettings Normalize(ZLibrarySettings settings)
    {
        var baseUrl = (settings.BaseUrl ?? string.Empty).Trim().TrimEnd('/');
        if (baseUrl.Length > 0 && !baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            baseUrl = "https://" + baseUrl;
        if (baseUrl.Length == 0) baseUrl = DefaultBaseUrl;
        return new ZLibrarySettings
        {
            Email = (settings.Email ?? string.Empty).Trim(),
            Password = settings.Password ?? string.Empty,
            BaseUrl = baseUrl
        };
    }

    public string? Validate()
    {
        if (!TryCreateAddress(Email)) return UiText.Get("请输入有效的 Z-Library 账号邮箱地址。");
        if (string.IsNullOrWhiteSpace(Password)) return UiText.Get("请输入 Z-Library 账号密码。");
        var baseUrl = Normalize(this).BaseUrl;
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != "http" && uri.Scheme != "https"))
            return UiText.Get("请输入有效的 Z-Library API 服务地址。");
        return null;
    }

    private static bool TryCreateAddress(string value)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(value) && new MailAddress(value).Address.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

public sealed class ZLibrarySettingsStore
{
    private readonly AppPaths _paths;
    private readonly ISecretProtector _protector;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    public ZLibrarySettingsStore(AppPaths paths, ISecretProtector protector)
    {
        _paths = paths;
        _protector = protector;
    }

    private string SettingsPath => Path.Combine(_paths.Data, "zlibrary-settings.json");

    public async Task<ZLibrarySettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        _paths.EnsureDirectories();
        if (!File.Exists(SettingsPath)) return new ZLibrarySettings();

        try
        {
            await using var stream = new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            var persisted = await JsonSerializer.DeserializeAsync<PersistedZLibrarySettings>(stream, _jsonOptions, cancellationToken);
            if (persisted is null) return new ZLibrarySettings();

            return ZLibrarySettings.Normalize(new ZLibrarySettings
            {
                Email = persisted.Email ?? string.Empty,
                Password = string.IsNullOrWhiteSpace(persisted.ProtectedPassword)
                    ? string.Empty
                    : Encoding.UTF8.GetString(_protector.Unprotect(Convert.FromBase64String(persisted.ProtectedPassword))),
                BaseUrl = string.IsNullOrWhiteSpace(persisted.BaseUrl)
                    ? ZLibrarySettings.DefaultBaseUrl
                    : persisted.BaseUrl
            });
        }
        catch (Exception exception) when (exception is IOException
            or JsonException
            or FormatException
            or System.ComponentModel.Win32Exception
            or System.Security.Cryptography.CryptographicException)
        {
            return new ZLibrarySettings();
        }
    }

    public async Task SaveAsync(ZLibrarySettings settings, CancellationToken cancellationToken = default)
    {
        using var lease = await SettingsWriteLock.AcquireAsync(_paths, cancellationToken);
        await SaveUnderLockAsync(settings, cancellationToken);
    }

    internal async Task SaveUnderLockAsync(ZLibrarySettings settings, CancellationToken cancellationToken, DateTimeOffset? syncedAt = null)
    {
        _paths.EnsureDirectories();
        var normalized = ZLibrarySettings.Normalize(settings);
        var persisted = new PersistedZLibrarySettings
        {
            Email = normalized.Email,
            ProtectedPassword = string.IsNullOrWhiteSpace(normalized.Password)
                ? string.Empty
                : Convert.ToBase64String(_protector.Protect(Encoding.UTF8.GetBytes(normalized.Password))),
            BaseUrl = normalized.BaseUrl
        };

        var temporaryPath = SettingsPath + ".tmp";
        await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            await JsonSerializer.SerializeAsync(stream, persisted, _jsonOptions, cancellationToken);
        if (syncedAt is { } timestamp) File.SetLastWriteTimeUtc(temporaryPath, timestamp.UtcDateTime);
        File.Move(temporaryPath, SettingsPath, overwrite: true);
    }

    private sealed class PersistedZLibrarySettings
    {
        public string? Email { get; set; }
        public string? ProtectedPassword { get; set; }
        public string? BaseUrl { get; set; }
    }
}

public sealed class ZLibraryService : IZLibraryService, IDisposable
{
    private static readonly string[] SeedBaseUrls =
    [
        "https://z-lib.gd",
        "https://z-lib.fo",
        "https://library-oceania.sk",
        "https://library-latin.sk",
        "https://z-lib.fm",
        "https://library-asia.sk",
        "https://lib-africa.sk",
        "https://z-library.do",
        "https://1lib.sk",
        "https://z-lib.gl",
        "https://z-library.rs",
        "https://z-lib.do",
        "https://z-lib.gs"
    ];

    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _loginLock = new(1, 1);
    private string _email = string.Empty;
    private string _password = string.Empty;
    private string _baseUrl = ZLibrarySettings.DefaultBaseUrl;
    private string _remixUserId = string.Empty;
    private string _remixUserKey = string.Empty;
    private string? _personalDomain;
    private bool _baseUrlVerified;

    public bool IsLoggedIn => _remixUserId.Length > 0 && _remixUserKey.Length > 0;
    public string ActiveBaseUrl => _baseUrl;

    public ZLibraryService(HttpMessageHandler? handler = null)
    {
        handler ??= new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
            CookieContainer = new CookieContainer(),
            UseCookies = true
        };
        _httpClient = new HttpClient(handler);
        _httpClient.Timeout = TimeSpan.FromSeconds(120);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _loginLock.Dispose();
    }

    public async Task LoginAsync(
        string email,
        string password,
        string baseUrl,
        CancellationToken cancellationToken = default)
    {
        await _loginLock.WaitAsync(cancellationToken);
        try
        {
            var normalizedBaseUrl = NormalizeBaseUrl(baseUrl);
            try
            {
                await LoginAtBaseUrlAsync(email, password, normalizedBaseUrl, cancellationToken);
            }
            catch (Exception exception) when (IsEndpointFailure(exception))
            {
                var discovered = await DiscoverBaseUrlAsync(normalizedBaseUrl, cancellationToken);
                if (discovered is null) throw new InvalidOperationException("无法连接 Z-Library API，请稍后重试或手动设置可用服务地址。", exception);
                await LoginAtBaseUrlAsync(email, password, discovered, cancellationToken);
            }
        }
        finally
        {
            _loginLock.Release();
        }
    }

    public async Task<ZLibrarySearchResult> SearchAsync(
        string query,
        int page = 1,
        int limit = 20,
        IReadOnlyList<string>? extensions = null,
        IReadOnlyList<string>? languages = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return new ZLibrarySearchResult([], 0, 1, 0);
        if (!IsLoggedIn) await EnsureReachableBaseUrlAsync(cancellationToken);

        var form = new Dictionary<string, string>
        {
            ["message"] = query.Trim(),
            ["page"] = Math.Max(1, page).ToString(CultureInfo.InvariantCulture),
            ["limit"] = Math.Clamp(limit, 1, 100).ToString(CultureInfo.InvariantCulture),
            ["e"] = "0"
        };
        if (extensions is { Count: > 0 })
        {
            var index = 0;
            foreach (var extension in extensions.Where(value => !string.IsNullOrWhiteSpace(value)))
                form.Add($"extensions[{index++}]", extension.Trim().ToLowerInvariant());
        }
        if (languages is { Count: > 0 })
        {
            var index = 0;
            foreach (var language in languages.Where(value => !string.IsNullOrWhiteSpace(value)))
                form.Add($"languages[{index++}]", language.Trim().ToLowerInvariant());
        }

        using var request = CreateFormRequest(_baseUrl, "/eapi/book/search", form);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = ParseApiResponse(response, body);
        var root = document.RootElement;

        var books = new List<ZLibraryBook>();
        if (root.TryGetProperty("books", out var booksElement) && booksElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in booksElement.EnumerateArray())
            {
                var bookElement = item.TryGetProperty("book", out var nestedBook) && nestedBook.ValueKind == JsonValueKind.Object
                    ? nestedBook
                    : item;
                if (bookElement.ValueKind != JsonValueKind.Object) continue;
                var id = GetInt64(item, "id", bookElement, "id");
                if (id <= 0) continue;
                books.Add(new ZLibraryBook
                {
                    Id = id,
                    Title = ReadString(bookElement, "title") ?? "未命名书籍",
                    Author = ReadString(bookElement, "author") ?? "未知作者",
                    Extension = (ReadString(bookElement, "extension") ?? string.Empty).Trim(),
                    Size = GetInt64(bookElement, "filesize", bookElement, "filesize"),
                    Language = ReadString(bookElement, "language") ?? string.Empty,
                    CoverUrl = ResolveApiUrl(ReadString(bookElement, "cover_url") ?? ReadString(bookElement, "cover")),
                    Hash = ReadString(item, "hash") ?? ReadString(bookElement, "hash") ?? ReadString(bookElement, "md5") ?? string.Empty,
                    Year = GetInt32Nullable(bookElement, "year"),
                    Publisher = ReadString(bookElement, "publisher"),
                    Series = ReadString(bookElement, "series"),
                    Edition = ReadFirstScalar(bookElement, "edition"),
                    Identifier = ReadString(bookElement, "identifier"),
                    Volume = ReadFirstScalar(bookElement, "volume"),
                    Description = ReadString(bookElement, "description"),
                    OfficialDetailUrl = ResolveApiUrl(ReadString(bookElement, "href")),
                    ReadOnlineUrl = ResolveApiUrl(ReadString(bookElement, "readOnlineUrl")),
                    Pages = GetInt32Nullable(bookElement, "pages"),
                    ReadOnlineAvailable = ReadBoolean(bookElement, "readOnlineAvailable"),
                    KindleAvailable = ReadBoolean(bookElement, "kindleAvailable"),
                    SendToEmailAvailable = ReadBoolean(bookElement, "sendToEmailAvailable")
                });
            }
        }

        var pagination = FindObject(root, "pagination");
        var totalValue = GetInt64(root, "total");
        if (totalValue <= 0) totalValue = GetInt64(root, "exactBooksCount");
        if (totalValue <= 0 && pagination is JsonElement paginationElement)
            totalValue = GetInt64(paginationElement, "total_items");
        var total = (int)Math.Clamp(totalValue, 0, int.MaxValue);
        var actualPage = pagination is JsonElement pageElement
            ? (int)Math.Max(1, GetInt64(pageElement, "current"))
            : Math.Max(1, page);
        var pageCountValue = pagination is JsonElement countElement
            ? GetInt64(countElement, "total_pages")
            : 0;
        var pageCount = pageCountValue > 0
            ? (int)Math.Min(pageCountValue, int.MaxValue)
            : limit <= 0 ? 0 : (int)Math.Ceiling(total / (double)limit);
        return new ZLibrarySearchResult(books, total, actualPage, pageCount);
    }

    public async Task<string?> GetDownloadUrlAsync(
        ZLibraryBook book,
        string preferredExtension,
        CancellationToken cancellationToken = default)
    {
        if (book.Id <= 0 || string.IsNullOrWhiteSpace(book.Hash)) return null;
        if (!IsLoggedIn) await EnsureLoggedInAsync(cancellationToken);

        var escapedHash = Uri.EscapeDataString(book.Hash);
        try
        {
            using var fileRequest = CreateRequest(_baseUrl, $"/eapi/book/{book.Id}/{escapedHash}/file");
            using var fileResponse = await _httpClient.SendAsync(fileRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var fileBody = await fileResponse.Content.ReadAsStringAsync(cancellationToken);
            using var fileDocument = ParseApiResponse(fileResponse, fileBody);
            var fileRoot = fileDocument.RootElement;
            if (fileRoot.TryGetProperty("file", out var file) && file.ValueKind == JsonValueKind.Object)
            {
                var downloadLink = ReadString(file, "downloadLink") ?? ReadString(file, "download_url");
                if (!string.IsNullOrWhiteSpace(downloadLink)) return RewriteDownloadHost(downloadLink);
                if (file.TryGetProperty("allowDownload", out var allowed) && allowed.ValueKind == JsonValueKind.False)
                    throw new InvalidOperationException("当前账号已达到下载限额，请稍后重试或检查账号状态。");
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or HttpRequestException)
        {
            // Older mirrors expose only the formats endpoint; retain it as a compatibility fallback.
        }

        using var request = CreateRequest(_baseUrl, $"/eapi/book/{book.Id}/{escapedHash}/formats");
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = ParseApiResponse(response, body);
        var root = document.RootElement;

        if (!root.TryGetProperty("formats", out var formats) || formats.ValueKind != JsonValueKind.Array)
            return null;

        string? fallback = null;
        foreach (var format in formats.EnumerateArray())
        {
            var extension = ReadString(format, "extension") ?? string.Empty;
            var url = ReadString(format, "download_url");
            if (string.IsNullOrWhiteSpace(url)) continue;
            fallback ??= url;
            if (extension.Equals(preferredExtension, StringComparison.OrdinalIgnoreCase))
                return RewriteDownloadHost(url);
        }
        return fallback is null ? null : RewriteDownloadHost(fallback);
    }

    public async Task<string> DownloadAsync(
        ZLibraryBook book,
        string destinationDirectory,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var url = await GetDownloadUrlAsync(book, book.Extension, cancellationToken);
        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException("未找到可用的下载地址，可能是会员额度限制或文件已下架。");

        Directory.CreateDirectory(destinationDirectory);
        var fileName = GetUniqueFileName(destinationDirectory, book);
        var destinationPath = Path.Combine(destinationDirectory, fileName);
        var temporaryPath = destinationPath + ".part";
        try
        {
            using var request = CreateRequest(url, null);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"下载失败（HTTP {(int)response.StatusCode}）。");
            if (response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                using var errorDocument = ParseApiResponse(response, errorBody);
                throw new InvalidOperationException("下载被服务拒绝，请检查账号状态或会员额度。");
            }

            var totalBytes = response.Content.Headers.ContentLength ?? 0;
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            var buffer = new byte[81920];
            long copied = 0;
            await using (var destination = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                while (true)
                {
                    var read = await source.ReadAsync(buffer, cancellationToken);
                    if (read <= 0) break;
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    copied += read;
                    progress?.Report(new TransferProgress(copied, totalBytes, $"正在下载 {fileName}"));
                }
                await destination.FlushAsync(cancellationToken);
            }
            File.Move(temporaryPath, destinationPath, overwrite: true);
            return destinationPath;
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private async Task EnsureLoggedInAsync(CancellationToken cancellationToken)
    {
        if (IsLoggedIn) return;
        if (string.IsNullOrWhiteSpace(_email) || string.IsNullOrWhiteSpace(_password))
            throw new InvalidOperationException("尚未登录 Z-Library，请先在账号设置中配置账号。");
        await LoginAsync(_email, _password, _baseUrl, cancellationToken);
    }

    private async Task LoginAtBaseUrlAsync(string email, string password, string baseUrl, CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>
        {
            ["email"] = (email ?? string.Empty).Trim(),
            ["password"] = password ?? string.Empty
        };
        using var request = CreateFormRequest(baseUrl, "/eapi/user/login", form);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = ParseApiResponse(response, body);
        var root = document.RootElement;
        var session = FindObject(root, "user") ?? FindObject(root, "response");

        var userId = ReadFirstScalar(root, "remix-userid", "remix_userid", "remixUserId", "user_id", "id");
        var userKey = ReadFirstScalar(root, "remix-userkey", "remix_userkey", "remixUserKey", "user_key");
        if (session is JsonElement sessionElement)
        {
            if (userId.Length == 0) userId = ReadFirstScalar(sessionElement, "remix-userid", "remix_userid", "remixUserId", "user_id", "id");
            if (userKey.Length == 0) userKey = ReadFirstScalar(sessionElement, "remix-userkey", "remix_userkey", "remixUserKey", "user_key");
        }
        if (userId.Length == 0 || userKey.Length == 0)
            throw new InvalidDataException("登录响应中缺少用户凭证，请检查账号密码或服务地址。");

        _email = (email ?? string.Empty).Trim();
        _password = password ?? string.Empty;
        _baseUrl = NormalizeBaseUrl(baseUrl);
        _remixUserId = userId;
        _remixUserKey = userKey;
        _personalDomain = FindPersonalDomain(root);
        _baseUrlVerified = true;
    }

    private async Task EnsureReachableBaseUrlAsync(CancellationToken cancellationToken)
    {
        if (_baseUrlVerified) return;
        if (await ProbeBaseUrlAsync(_baseUrl, cancellationToken) is not null)
        {
            _baseUrlVerified = true;
            return;
        }
        var discovered = await DiscoverBaseUrlAsync(_baseUrl, cancellationToken);
        if (discovered is null)
            throw new InvalidOperationException("未找到可用的 Z-Library API 服务，请稍后重试或在账号设置中填写可用地址。");
        _baseUrl = discovered;
        _baseUrlVerified = true;
    }

    private async Task<string?> DiscoverBaseUrlAsync(string excludedBaseUrl, CancellationToken cancellationToken)
    {
        var candidates = SeedBaseUrls
            .Select(NormalizeBaseUrl)
            .Where(value => !value.Equals(NormalizeBaseUrl(excludedBaseUrl), StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var probes = candidates.Select(value => ProbeBaseUrlAsync(value, cancellationToken)).ToList();
        while (probes.Count > 0)
        {
            var completed = await Task.WhenAny(probes);
            probes.Remove(completed);
            var result = await completed;
            if (result is not null) return result;
        }
        return null;
    }

    private async Task<string?> ProbeBaseUrlAsync(string baseUrl, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            using var request = CreateRequest(baseUrl, "/eapi/info/ok", includeSession: false);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode) return null;
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            using var document = JsonDocument.Parse(body);
            return IsSuccess(document.RootElement) ? NormalizeBaseUrl(baseUrl) : null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or IOException) { return null; }
    }

    private static bool IsEndpointFailure(Exception exception) =>
        exception is HttpRequestException or InvalidDataException
        || exception is TaskCanceledException;

    private HttpRequestMessage CreateFormRequest(string baseUrl, string endpoint, IReadOnlyDictionary<string, string> form)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(baseUrl.TrimEnd('/')), endpoint));
        request.Content = new FormUrlEncodedContent(form);
        request.Headers.UserAgent.ParseAdd("Kkindle/1.0");
        request.Headers.Accept.ParseAdd("application/json, text/javascript, */*; q=0.01");
        request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        if (IsLoggedIn)
        {
            request.Headers.Add("remix-userid", _remixUserId);
            request.Headers.Add("remix-userkey", _remixUserKey);
            request.Headers.Add("Cookie", $"remix_userid={_remixUserId}; remix_userkey={_remixUserKey}");
        }
        return request;
    }

    private HttpRequestMessage CreateRequest(string baseUrl, string? endpoint, bool includeSession = true)
    {
        var url = string.IsNullOrEmpty(endpoint) ? baseUrl : new Uri(new Uri(baseUrl.TrimEnd('/')), endpoint).ToString();
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("Kkindle/1.0");
        if (includeSession && IsLoggedIn)
        {
            request.Headers.Add("remix-userid", _remixUserId);
            request.Headers.Add("remix-userkey", _remixUserKey);
            request.Headers.Add("Cookie", $"remix_userid={_remixUserId}; remix_userkey={_remixUserKey}");
        }
        return request;
    }

    private static JsonDocument ParseApiResponse(HttpResponseMessage response, string body)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw new InvalidDataException($"Z-Library 服务返回了无法识别的响应（HTTP {(int)response.StatusCode}）。");
        }

        if (!response.IsSuccessStatusCode)
        {
            document.Dispose();
            throw new HttpRequestException($"Z-Library 服务返回 HTTP {(int)response.StatusCode}。", null, response.StatusCode);
        }

        if (document.RootElement.TryGetProperty("success", out var success)
            && (success.ValueKind == JsonValueKind.False
                || (success.ValueKind == JsonValueKind.Number && success.TryGetInt32(out var successNumber) && successNumber == 0)))
        {
            var error = ReadString(document.RootElement, "error")
                ?? ReadString(document.RootElement, "message")
                ?? "未知错误";
            throw new InvalidOperationException($"Z-Library 请求失败：{error}");
        }
        return document;
    }

    private static string NormalizeBaseUrl(string baseUrl)
    {
        var normalized = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
        if (normalized.Length == 0) return ZLibrarySettings.DefaultBaseUrl;
        if (!normalized.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            normalized = "https://" + normalized;
        return normalized;
    }

    private string? FindPersonalDomain(JsonElement root)
    {
        if (root.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object)
        {
            var fromUser = ReadString(user, "personal_domain");
            if (!string.IsNullOrWhiteSpace(fromUser)) return fromUser.Trim();
        }
        var fromRoot = ReadString(root, "personal_domain");
        return string.IsNullOrWhiteSpace(fromRoot) ? null : fromRoot.Trim();
    }

    private static JsonElement? FindObject(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Object ? value : null;

    private static bool IsSuccess(JsonElement root)
    {
        if (!root.TryGetProperty("success", out var success)) return false;
        return success.ValueKind == JsonValueKind.True
            || (success.ValueKind == JsonValueKind.Number && success.TryGetInt32(out var number) && number == 1);
    }

    private string RewriteDownloadHost(string url)
    {
        if (string.IsNullOrWhiteSpace(_personalDomain)) return url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;
        if (!Uri.TryCreate(_baseUrl, UriKind.Absolute, out var baseUri)) return url;
        if (!uri.Host.Equals(baseUri.Host, StringComparison.OrdinalIgnoreCase)) return url;

        var personalDomain = _personalDomain!.Trim().TrimEnd('/');
        return personalDomain.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || personalDomain.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? $"{personalDomain}{uri.PathAndQuery}"
                : $"https://{personalDomain}{uri.PathAndQuery}";
    }

    private string? ResolveApiUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absolute)
            && absolute.Scheme is "http" or "https")
            return absolute.ToString();
        if (trimmed.StartsWith("//", StringComparison.Ordinal)) return "https:" + trimmed;
        return Uri.TryCreate(new Uri(_baseUrl.TrimEnd('/') + "/"), trimmed, out var resolved)
            ? resolved.ToString()
            : null;
    }

    private static string GetUniqueFileName(string directory, ZLibraryBook book)
    {
        var extension = string.IsNullOrWhiteSpace(book.Extension) ? "epub" : book.Extension.Trim().TrimStart('.');
        var baseName = SanitizeFileName(book.Title);
        if (baseName.Length == 0) baseName = $"book-{book.Id}";
        var candidate = $"{baseName}.{extension}";
        var index = 2;
        while (File.Exists(Path.Combine(directory, candidate)))
        {
            candidate = $"{baseName}-{index}.{extension}";
            index++;
        }
        return candidate;
    }

    private static string SanitizeFileName(string title)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(title.Trim()
            .Select(character => invalid.Contains(character) ? ' ' : character)
            .ToArray())
            .Trim();
        return cleaned.Length > 80 ? cleaned[..80].Trim() : cleaned;
    }

    private static string? ReadString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
            return null;
        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static string ReadFirstString(JsonElement element, params string[] properties)
    {
        foreach (var property in properties)
        {
            var value = ReadString(element, property);
            if (value is not null) return value;
        }
        return string.Empty;
    }

    private static string ReadFirstScalar(JsonElement element, params string[] properties)
    {
        foreach (var property in properties)
        {
            if (!element.TryGetProperty(property, out var value)) continue;
            if (value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
                return value.GetString()!.Trim();
            if (value.ValueKind == JsonValueKind.Number) return value.GetRawText();
        }
        return string.Empty;
    }

    private static long GetInt64(JsonElement element, string property, JsonElement fallbackElement, string fallbackProperty)
    {
        var value = GetInt64Core(element, property);
        if (value is not null) return value.Value;
        value = GetInt64Core(fallbackElement, fallbackProperty);
        return value ?? 0;
    }

    private static long GetInt64(JsonElement element, string property)
    {
        var value = GetInt64Core(element, property);
        return value ?? 0;
    }

    private static long? GetInt64Core(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)) return number;
        return null;
    }

    private static int? GetInt32Nullable(JsonElement element, string property)
    {
        var value = GetInt64Core(element, property);
        return value is >= int.MinValue and <= int.MaxValue ? (int)value.Value : null;
    }

    private static bool ReadBoolean(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return false;
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False) return value.GetBoolean();
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number != 0;
        return value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var result) && result;
    }
}
