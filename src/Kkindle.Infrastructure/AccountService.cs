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
    public static string DefaultServerUrl => AccountRegions.DefaultServerUrl(AccountRegion.China);
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly AccountSessionStore _store;
    private readonly DeviceIdentityStore _identity;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeProvider _clock;
    private readonly AccountRegion? _dataRegion;
    private readonly Func<AccountRegion, string> _defaultServerUrl;
    private bool _loaded;
    private AccountRegion _region;
    private Dictionary<AccountRegion, RegionalAccountConnection> _connections = [];
    private string _serverUrl = "";
    private AccountSessionResponse? _session;

    public AccountService(AppPaths paths, ISecretProtector protector, HttpClient? http = null, TimeProvider? clock = null,
        AccountRegion? dataRegion = null, Func<AccountRegion, string>? defaultServerUrl = null)
    {
        _store = new(paths, protector);
        _identity = new(paths, protector);
        _ownsHttp = http is null;
        // A redirect must never forward a password or a bearer token to another host.
        _http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };
        _clock = clock ?? TimeProvider.System;
        _dataRegion = dataRegion;
        _defaultServerUrl = defaultServerUrl ?? AccountRegions.DefaultServerUrl;
    }

    public event EventHandler<AccountDeviceInvalidatedEventArgs>? DeviceInvalidated;
    public Task<DeviceEnrollmentRequest> GetDeviceAsync(CancellationToken token = default) => _identity.GetAsync(token);

    internal async Task<(AccountStatus Scope, T Value)> MemberRequestAsync<T>(HttpMethod method, string action,
        JsonTypeInfo<T> metadata, JsonContent? content, CancellationToken token, string? signInMessage = null)
    {
        await _gate.WaitAsync(token);
        try
        {
            await LoadAsync(token);
            if (_session is null) throw new DeviceFeatureRequiredException(signInMessage ?? "请先登录当前区域的会员账号后使用多端同步。");
            var scope = Status();
            using var request = Request(method, _serverUrl, action, _session.Token);
            request.Content = content;
            using var response = await SendAsync(request, token);
            await EnsureMemberSuccessAsync(response, token);
            return (scope, await ReadAsync(response, metadata, token, maximumBytes: 64 * 1024));
        }
        finally { _gate.Release(); }
    }

    internal async Task MemberActionAsync(string action, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            await LoadAsync(token);
            if (_session is null) throw new InvalidOperationException("请先登录当前区域的账号。");
            using var request = Request(HttpMethod.Post, _serverUrl, action, _session.Token);
            using var response = await SendAsync(request, token);
            await EnsureMemberSuccessAsync(response, token);
        }
        finally { _gate.Release(); }
    }

    private async Task EnsureMemberSuccessAsync(HttpResponseMessage response, CancellationToken token)
    {
        if (response.IsSuccessStatusCode) return;
        var code = response.Headers.TryGetValues("X-Kkindle-Error", out var values) ? values.FirstOrDefault() : null;
        if (response.StatusCode == HttpStatusCode.Unauthorized || code is "account_banned" or "device_mismatch")
        {
            await InvalidateAsync(token);
            throw new DeviceFeatureRequiredException("此设备已退出登录或账号已失效，请重新登录。");
        }
        var message = ApiMessage(code);
        if (code is "membership_required" or "plus_required") throw new DeviceFeatureRequiredException(message!);
        throw new InvalidOperationException(message ?? "账号服务暂时不可用，请稍后重试。");
    }

    private async Task InvalidateAsync(CancellationToken token)
    {
        var hadSession = _session is not null;
        await SetAsync(_serverUrl, null, token);
        if (hadSession) DeviceInvalidated?.Invoke(this, new("此设备已退出登录或会话已过期，请重新登录。"));
    }

    private static string? ApiMessage(string? code) => code switch
    {
        "device_limit_reached" => UiText.Get("此账号已登录 {0} 台设备，请在已登录设备的账号页踢出旧设备，再登录。", MemberEntitlements.DeviceLimit),
        "device_blocked" => "此设备已被管理员停用，请联系管理员。",
        "device_mismatch" => "本机设备身份已改变，请重新登录。",
        "invalid_device" => "设备信息无效，请检查系统密钥库或更新客户端。",
        "membership_required" => MemberEntitlements.SyncRequirement,
        "plus_required" => MemberEntitlements.AccountCloudRequirement,
        "purchases_disabled" => "购买服务暂未开放，请联系管理员。",
        "invalid_product" => "商品不存在或已下架，请刷新商品列表。",
        "order_request_conflict" => "请为不同商品创建新的订单。",
        "too_many_orders" => "请先处理或取消已有的待支付订单。",
        "order_not_pending" => "该订单已取消、到账或超过付款期限。",
        "membership_limit" => "Plus 最多可持有 5 年有效期，付费和激活码共用此上限。请先处理待支付的 Plus 订单或等待可续费后再试。",
        "invalid_membership" => "Plus 单次有效期需为 1–1825 天，最多 5 年。",
        "invalid_activation_code" => "激活码无效，请检查后重试。",
        "activation_region_mismatch" => "此激活码属于其他区域，请切换到发行区域并登录后兑换。",
        "activation_disabled" => "此激活码已停用，请联系发行方。",
        "activation_expired" => "此激活码已超过兑换截止时间。",
        "activation_already_used" => "此激活码已使用，不能再次兑换。",
        "activation_request_conflict" => "请为其他激活码创建新的兑换请求。",
        "invalid_activation_request" => "兑换请求标识无效。",
        "invalid_activation_kind" => "激活码仅支持永久功能会员或 1 年 Plus。",
        "lifetime_already_active" => "此账号已永久解锁功能，无需再次兑换。激活码未被使用。",
        "rate_limited" => "请求过于频繁，请稍后再试。",
        "registration_limit_reached" => "此网络的注册次数已达上限，请稍后再试或联系管理员。",
        "registration_blocked" => "此网络或账号信息暂不支持注册，请联系管理员。",
        "username_blocked" => "此用户名暂不可用，请选择其他用户名。",
        "cloud_retention_due" => "云数据保留期限已结束，正在清理，请稍后重新开通 Plus。",
        "region_mismatch" => "账号服务区域不匹配，请检查所选区域。",
        "username_exists" => "该用户名已注册，请换一个用户名。",
        "email_exists" => "该邮箱已被其他账号使用。",
        "email_required" => "注册时必须填写邮箱。",
        "email_change_not_allowed" => "注册邮箱不可修改。",
        "invalid_email" => "请输入有效的邮箱地址。",
        "invalid_username" => "用户名需为 3–32 位字母、数字、点、下划线或连字符。",
        "current_password_required" => "请输入当前密码。",
        "current_password_incorrect" => "当前密码不正确。",
        "invalid_password" => "密码需为 8–128 位，且不能全部为空格。",
        "confirmation_password_required" => "请输入确认登录密码。",
        "passwords_do_not_match" => "两次输入的密码不一致。",
        "password_unchanged" => "新密码不能与当前密码相同。",
        "confirmation_required" => "请输入“注销账号”确认文字。",
        _ => null
    };

    public async Task<AccountStatus> SelectRegionAsync(AccountRegion region, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(region)) throw new ArgumentOutOfRangeException(nameof(region));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await LoadAsync(cancellationToken);
            var connection = ResolveConnection(region, _connections.GetValueOrDefault(region));
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
                    await InvalidateAsync(cancellationToken);
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
        bool register = false, CancellationToken cancellationToken = default, string? email = null, string? confirmPassword = null)
    {
        var endpoint = AccountValidation.ServerUri(serverUrl).AbsoluteUri.TrimEnd('/');
        username = username.Trim();
        if (!(register ? AccountValidation.IsUsername(username) : AccountValidation.IsLogin(username)))
            throw new ArgumentException(register ? "用户名需为 3–32 位字母、数字、点、下划线或连字符。" : "请输入有效的用户名或邮箱。");
        email = string.IsNullOrWhiteSpace(email) ? null : email.Trim();
        if (register && email is null) throw new ArgumentException("注册时必须填写邮箱。");
        if (email is not null && !AccountValidation.IsEmail(email)) throw new ArgumentException("请输入有效的邮箱地址。");
        if (!AccountValidation.IsPassword(password))
            throw new ArgumentException("密码需为 8–128 位，且不能全部为空格。");
        if (register && !AccountValidation.IsPassword(confirmPassword)) throw new ArgumentException("请输入确认登录密码。");
        if (register && password != confirmPassword) throw new ArgumentException("两次输入的密码不一致。");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await LoadAsync(cancellationToken);
            var device = await _identity.GetAsync(cancellationToken);
            using var request = Request(HttpMethod.Post, endpoint, register ? "register" : "login");
            request.Content = JsonContent.Create(new AccountCredentials { Username = username, Email = email, Password = password,
                ConfirmPassword = register ? confirmPassword : null, Region = _region,
                DeviceId = device.DeviceId, DeviceName = device.DeviceName, Platform = device.Platform },
                AccountJsonContext.Default.AccountCredentials);
            using var response = await SendAsync(request, cancellationToken);
            EnsureSuccess(response, register);
            var session = await ReadAsync(response, AccountJsonContext.Default.AccountSessionResponse, cancellationToken);
            if (session.Region != _region) throw new InvalidOperationException("账号服务区域不匹配，请检查所选区域和服务地址。");
            if (session.User is null || session.User.Id == Guid.Empty || !AccountValidation.IsUsername(session.User.Username)
                || register && session.User.Email != email
                || session.Token is not { Length: 43 }
                || !session.Token.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
                || session.ExpiresAt <= _clock.GetUtcNow())
                throw new InvalidOperationException("账号服务返回了无效的会话。");
            await SetAsync(endpoint, session, cancellationToken);
            return Status();
        }
        finally { _gate.Release(); }
    }

    public async Task<AccountStatus> UpdateProfileAsync(string username, string currentPassword, CancellationToken token = default)
    {
        username = username.Trim();
        if (!AccountValidation.IsUsername(username)) throw new ArgumentException("用户名需为 3–32 位字母、数字、点、下划线或连字符。");
        if (!AccountValidation.IsPassword(currentPassword)) throw new ArgumentException("请输入当前密码。");
        await _gate.WaitAsync(token);
        try
        {
            await LoadAsync(token);
            var session = _session ?? throw new InvalidOperationException("请先登录当前区域的账号。");
            using var request = Request(HttpMethod.Post, _serverUrl, "profile", session.Token);
            request.Content = JsonContent.Create(new AccountProfileUpdate { Username = username, CurrentPassword = currentPassword },
                AccountJsonContext.Default.AccountProfileUpdate);
            using var response = await SendAsync(request, token);
            await EnsureAccountChangeSuccessAsync(response, token);
            var user = await ReadAsync(response, AccountJsonContext.Default.AccountUser, token);
            if (user.Id != session.User.Id || user.Username != username || user.Email != session.User.Email)
                throw new InvalidOperationException("账号服务返回了无效的账号资料。");
            await SetAsync(_serverUrl, new AccountSessionResponse { Region = _region, User = user, Token = session.Token, ExpiresAt = session.ExpiresAt }, token);
            return Status();
        }
        finally { _gate.Release(); }
    }

    public async Task<AccountStatus> ChangePasswordAsync(string currentPassword, string newPassword, CancellationToken token = default)
    {
        if (!AccountValidation.IsPassword(currentPassword)) throw new ArgumentException("请输入当前密码。");
        if (!AccountValidation.IsPassword(newPassword)) throw new ArgumentException("密码需为 8–128 位，且不能全部为空格。");
        if (currentPassword == newPassword) throw new ArgumentException("新密码不能与当前密码相同。");
        await _gate.WaitAsync(token);
        try
        {
            await LoadAsync(token);
            var current = _session ?? throw new InvalidOperationException("请先登录当前区域的账号。");
            using var request = Request(HttpMethod.Post, _serverUrl, "password", current.Token);
            request.Content = JsonContent.Create(new AccountPasswordChange { CurrentPassword = currentPassword, NewPassword = newPassword },
                AccountJsonContext.Default.AccountPasswordChange);
            using var response = await SendAsync(request, token);
            await EnsureAccountChangeSuccessAsync(response, token);
            var session = await ReadAsync(response, AccountJsonContext.Default.AccountSessionResponse, token);
            if (session.Region != _region || session.User is null || session.User.Id != current.User.Id || !AccountValidation.IsUsername(session.User.Username)
                || session.Token is not { Length: 43 } || !session.Token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
                || session.ExpiresAt <= _clock.GetUtcNow()) throw new InvalidOperationException("账号服务返回了无效的会话。");
            // The server rotated this device's token and revoked the other devices.
            await SetAsync(_serverUrl, session, CancellationToken.None);
            return Status();
        }
        finally { _gate.Release(); }
    }

    public async Task CloseAccountAsync(string currentPassword, string confirmation, CancellationToken token = default)
    {
        if (confirmation != "注销账号") throw new ArgumentException("请输入“注销账号”确认文字。");
        if (!AccountValidation.IsPassword(currentPassword)) throw new ArgumentException("请输入当前密码。");
        await _gate.WaitAsync(token);
        try
        {
            await LoadAsync(token);
            var session = _session ?? throw new InvalidOperationException("请先登录当前区域的账号。");
            using var request = Request(HttpMethod.Post, _serverUrl, "close", session.Token);
            request.Content = JsonContent.Create(new AccountClosureRequest { CurrentPassword = currentPassword, Confirmation = confirmation },
                AccountJsonContext.Default.AccountClosureRequest);
            using var response = await SendAsync(request, token);
            await EnsureAccountChangeSuccessAsync(response, token);
            await SetAsync(_serverUrl, null, CancellationToken.None);
        }
        finally { _gate.Release(); }
    }

    private async Task EnsureAccountChangeSuccessAsync(HttpResponseMessage response, CancellationToken token)
    {
        // Wrong current passwords are 403 and keep this device signed in.
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        { await InvalidateAsync(token); throw new InvalidOperationException("会话已失效，请重新登录。"); }
        EnsureSuccess(response, register: false);
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
            foreach (var region in _connections.Keys.ToArray())
                _connections[region] = ResolveConnection(region, _connections[region]);
            var connection = ResolveConnection(_region, _connections.GetValueOrDefault(_region));
            _connections[_region] = connection;
            _serverUrl = connection.ServerUrl; _session = connection.Session;
            _loaded = true;
        }
        if (_session is { } session && (session.ExpiresAt <= _clock.GetUtcNow() || session.User is null
            || session.Region != _region || session.Token is not { Length: 43 }
            || !session.Token.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
            || session.User.Id == Guid.Empty || !AccountValidation.IsUsername(session.User.Username)))
            await InvalidateAsync(token);
        if (_session is null)
        {
            var connection = ResolveConnection(_region, _connections.GetValueOrDefault(_region));
            _connections[_region] = connection;
            _serverUrl = connection.ServerUrl;
        }
    }

    private string DefaultEndpoint(AccountRegion region) =>
        AccountValidation.ServerUri(_defaultServerUrl(region)).AbsoluteUri.TrimEnd('/');

    private RegionalAccountConnection ResolveConnection(AccountRegion region, RegionalAccountConnection? connection)
    {
        if (connection is null) return new(DefaultEndpoint(region), null, true);
        var followsDefault = connection.UsesDefaultServerUrl
            ?? AccountServerEndpoints.IsLegacyDevelopmentDefault(region, connection.ServerUrl);
        // A stored token is always bound to its stored server, even if a release
        // or environment override changes the configured default endpoint.
        if (connection.Session is null && followsDefault) return new(DefaultEndpoint(region), null, true);
        return new(connection.ServerUrl, connection.Session, followsDefault);
    }

    private async Task SetAsync(string server, AccountSessionResponse? session, CancellationToken token)
    {
        var previous = _connections.GetValueOrDefault(_region);
        var followsDefault = previous is not null && string.Equals(previous.ServerUrl, server, StringComparison.Ordinal)
            ? previous.UsesDefaultServerUrl == true
            : string.Equals(DefaultEndpoint(_region), server, StringComparison.Ordinal);
        // Persist token removal before resolving a new default. Even an invalid
        // deployment override must not prevent local logout; the next status
        // read resolves (and validates) the signed-out default connection.
        var connection = new RegionalAccountConnection(server, session, followsDefault);
        var updated = new Dictionary<AccountRegion, RegionalAccountConnection>(_connections) { [_region] = connection };
        await _store.SaveAsync(_region, updated, token);
        _connections = updated;
        _serverUrl = connection.ServerUrl;
        _session = connection.Session;
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
        if (response.Headers.TryGetValues("X-Kkindle-Error", out values) && ApiMessage(values.FirstOrDefault()) is { } message)
            throw new InvalidOperationException(message);
        throw new InvalidOperationException(response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "用户名、邮箱或密码错误。",
            HttpStatusCode.Forbidden => "账号服务区域不匹配，请检查所选区域和服务地址。",
            HttpStatusCode.Conflict when register => "该用户名已注册，请登录或换一个用户名。",
            HttpStatusCode.TooManyRequests => "尝试次数过多，请稍后再试。",
            HttpStatusCode.BadRequest => "账号信息无效，请检查用户名和密码。",
            _ => "账号服务暂时不可用，请检查地址或稍后重试。"
        });
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, JsonTypeInfo<T> metadata, CancellationToken token,
        int maximumBytes = 16 * 1024)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            // Bound replies, including replies from a mistakenly configured server.
            await response.Content.LoadIntoBufferAsync(maximumBytes).WaitAsync(deadline.Token);
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

public sealed class AccountDeviceInvalidatedEventArgs(string message) : EventArgs
{
    public string Message { get; } = message;
}
