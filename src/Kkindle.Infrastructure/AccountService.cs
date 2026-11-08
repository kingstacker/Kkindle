using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Kkindle.Core;

namespace Kkindle.Infrastructure;

/// <summary>Real server authentication, with a protected, device-local session.</summary>
public sealed class AccountService : IDisposable
{
    public const string DefaultServerUrl = "http://127.0.0.1:5088";
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly AccountSessionStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeProvider _clock;
    private readonly AccountRegion? _dataRegion;
    private bool _loaded;
    private AccountRegion _region;
    private Dictionary<AccountRegion, RegionalAccountConnection> _connections = [];
    private string _serverUrl = DefaultServerUrl;
    private AccountSessionResponse? _session;

    public AccountService(AppPaths paths, ISecretProtector protector, HttpClient? http = null, TimeProvider? clock = null,
        AccountRegion? dataRegion = null)
    {
        _store = new(paths, protector);
        _ownsHttp = http is null;
        // A redirect must never forward a password or a bearer token to another host.
        _http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };
        _clock = clock ?? TimeProvider.System;
        _dataRegion = dataRegion;
    }

    public async Task<AccountStatus> SelectRegionAsync(AccountRegion region, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(region)) throw new ArgumentOutOfRangeException(nameof(region));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await LoadAsync(cancellationToken);
            var connection = _connections.GetValueOrDefault(region)
                ?? new RegionalAccountConnection(AccountRegions.DefaultServerUrl(region), null);
            var updated = new Dictionary<AccountRegion, RegionalAccountConnection>(_connections) { [region] = connection };
            await _store.SaveAsync(region, updated, cancellationToken);
            _connections = updated; _region = region;
            _serverUrl = connection.ServerUrl; _session = connection.Session;
            await LoadAsync(cancellationToken);
            return Status();
        }
        finally { _gate.Release(); }
    }

    public async Task<S3SyncSettings> CreateSyncSettingsAsync(S3SyncSettings current, CancellationToken cancellationToken = default)
    {
        var status = await GetStatusAsync(validateSession: true, cancellationToken: cancellationToken);
        if (!status.IsLoggedIn) throw new InvalidOperationException("请先登录当前区域的用户账号。");
        if (status.RequiresDataRestart) throw new InvalidOperationException("区域已切换，请重新打开应用后使用该区域的独立书库和同步。");
        return current with { Provider = SyncProvider.AccountServer, AccountRegion = status.Region,
            AccountServerUrl = status.ServerUrl, AccountUserId = status.User!.Id };
    }

    public async Task EnsureSyncRegionAsync(AccountRegion dataRegion, CancellationToken token = default)
    {
        var status = await GetStatusAsync(cancellationToken: token);
        if (status.Region != dataRegion || status.RequiresDataRestart)
            throw new InvalidOperationException("区域已切换，请重新打开应用后使用该区域的独立书库和同步。");
    }

    internal async Task AuthorizeSyncRequestAsync(HttpRequestMessage request, S3SyncSettings settings, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            await LoadAsync(token);
            if (Status().RequiresDataRestart) throw new InvalidOperationException("区域已切换，请重新打开应用后使用该区域的独立书库和同步。");
            if (_session is null || _session.User.Id != settings.AccountUserId || _region != settings.AccountRegion
                || !string.Equals(_serverUrl, settings.AccountServerUrl, StringComparison.Ordinal)
                || request.RequestUri is null || !AccountValidation.ServerUri(_serverUrl).IsBaseOf(request.RequestUri))
                throw new InvalidOperationException("同步账号或区域已改变，请重新登录并选择当前账号的云同步。");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _session.Token);
            request.Headers.Add("X-Kkindle-Region", AccountRegions.Slug(_region));
        }
        finally { _gate.Release(); }
    }

    public async Task<AccountStatus> GetStatusAsync(bool validateSession = false, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await LoadAsync(cancellationToken);
            if (validateSession && _session is { } session)
            {
                using var request = Request(HttpMethod.Get, _serverUrl, "session", session.Token);
                using var response = await SendAsync(request, cancellationToken);
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                    await SetAsync(_serverUrl, null, cancellationToken);
                else
                {
                    EnsureSuccess(response, false);
                    var info = await ReadAsync(response, AccountJsonContext.Default.AccountSessionInfo, cancellationToken);
                    if (info.Region != _region || info.User is null || info.User.Id != session.User.Id || !AccountValidation.IsUsername(info.User.Username)
                        || info.ExpiresAt <= _clock.GetUtcNow())
                        throw new InvalidOperationException("账号服务返回了无效的会话。");
                    await SetAsync(_serverUrl, new AccountSessionResponse
                        { User = info.User, Token = session.Token, ExpiresAt = info.ExpiresAt, Region = _region }, cancellationToken);
                }
            }
            return Status();
        }
        finally { _gate.Release(); }
    }

    public async Task<AccountStatus> SignInAsync(string serverUrl, string username, string password,
        bool register = false, CancellationToken cancellationToken = default)
    {
        var endpoint = AccountValidation.ServerUri(serverUrl).AbsoluteUri.TrimEnd('/');
        username = username.Trim();
        if (!AccountValidation.IsUsername(username))
            throw new ArgumentException("用户名需为 3–32 位字母、数字、点、下划线或连字符。");
        if (!AccountValidation.IsPassword(password))
            throw new ArgumentException("密码需为 8–128 位，且不能全部为空格。");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await LoadAsync(cancellationToken);
            using var request = Request(HttpMethod.Post, endpoint, register ? "register" : "login");
            request.Content = JsonContent.Create(new AccountCredentials { Username = username, Password = password, Region = _region },
                AccountJsonContext.Default.AccountCredentials);
            using var response = await SendAsync(request, cancellationToken);
            EnsureSuccess(response, register);
            var session = await ReadAsync(response, AccountJsonContext.Default.AccountSessionResponse, cancellationToken);
            if (session.Region != _region) throw new InvalidOperationException("账号服务区域不匹配，请检查所选区域和服务地址。");
            if (session.User is null || session.User.Id == Guid.Empty || !AccountValidation.IsUsername(session.User.Username)
                || session.Token is not { Length: 43 }
                || !session.Token.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
                || session.ExpiresAt <= _clock.GetUtcNow())
                throw new InvalidOperationException("账号服务返回了无效的会话。");
            await SetAsync(endpoint, session, cancellationToken);
            return Status();
        }
        finally { _gate.Release(); }
    }

    /// <returns>False when the device signed out but the server could not revoke the session.</returns>
    public async Task<bool> SignOutAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await LoadAsync(cancellationToken);
            var revoked = true;
            if (_session is { } session)
            {
                try
                {
                    using var request = Request(HttpMethod.Post, _serverUrl, "logout", session.Token);
                    using var response = await SendAsync(request, cancellationToken);
                    revoked = response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Unauthorized;
                }
                catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or InvalidOperationException)
                {
                    revoked = false;
                }
            }
            // Even cancellation or a network outage must not leave this device signed in.
            await SetAsync(_serverUrl, null, CancellationToken.None);
            return revoked;
        }
        finally { _gate.Release(); }
    }

    private async Task LoadAsync(CancellationToken token)
    {
        if (!_loaded)
        {
            (_region, _connections) = await _store.LoadAsync(token);
            var connection = _connections.GetValueOrDefault(_region)
                ?? new RegionalAccountConnection(AccountRegions.DefaultServerUrl(_region), null);
            _serverUrl = connection.ServerUrl; _session = connection.Session;
            _loaded = true;
        }
        if (_session is { } session && (session.ExpiresAt <= _clock.GetUtcNow() || session.User is null
            || session.Region != _region || session.Token is not { Length: 43 }
            || !session.Token.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
            || session.User.Id == Guid.Empty || !AccountValidation.IsUsername(session.User.Username)))
            await SetAsync(_serverUrl, null, token);
    }

    private async Task SetAsync(string server, AccountSessionResponse? session, CancellationToken token)
    {
        var updated = new Dictionary<AccountRegion, RegionalAccountConnection>(_connections) { [_region] = new(server, session) };
        await _store.SaveAsync(_region, updated, token);
        _connections = updated;
        _serverUrl = server;
        _session = session;
    }

    private AccountStatus Status() => new(_serverUrl, _session?.User, _session?.ExpiresAt, _region,
        _dataRegion is { } region && region != _region);

    private HttpRequestMessage Request(HttpMethod method, string server, string action, string? bearer = null)
    {
        var request = new HttpRequestMessage(method, new Uri(AccountValidation.ServerUri(server), "api/accounts/" + action));
        if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Headers.Add("X-Kkindle-Region", AccountRegions.Slug(_region));
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        try { return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token); }
        catch (HttpRequestException) { throw new InvalidOperationException("无法连接账号服务，请检查服务地址和网络。"); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new InvalidOperationException("账号服务连接超时，请稍后重试。"); }
    }

    private static void EnsureSuccess(HttpResponseMessage response, bool register)
    {
        if (response.IsSuccessStatusCode) return;
        if (response.Headers.TryGetValues("X-Kkindle-Error", out var values) && values.Contains("account_banned"))
            throw new InvalidOperationException("此账号已被封禁，请联系管理员。");
        throw new InvalidOperationException(response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "用户名或密码错误。",
            HttpStatusCode.Forbidden => "账号服务区域不匹配，请检查所选区域和服务地址。",
            HttpStatusCode.Conflict when register => "该用户名已注册，请登录或换一个用户名。",
            HttpStatusCode.TooManyRequests => "尝试次数过多，请稍后再试。",
            HttpStatusCode.BadRequest => "账号信息无效，请检查用户名和密码。",
            _ => "账号服务暂时不可用，请检查地址或稍后重试。"
        });
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, JsonTypeInfo<T> metadata, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            // Bound replies, including replies from a mistakenly configured server.
            await response.Content.LoadIntoBufferAsync(16 * 1024).WaitAsync(deadline.Token);
            return await response.Content.ReadFromJsonAsync(metadata, deadline.Token)
                ?? throw new InvalidOperationException("账号服务返回了无效的响应。");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new InvalidOperationException("账号服务连接超时，请稍后重试。"); }
        catch (Exception exception) when (exception is JsonException or HttpRequestException or NotSupportedException)
        { throw new InvalidOperationException("账号服务返回了无效的响应。"); }
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
