using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Kkindle.Core;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;

namespace Kkindle.AccountServer;

public static class AccountServerHost
{
    public static async Task<WebApplication> BuildAsync(string[] args, TimeProvider? clock = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        if (!Enum.TryParse<AccountRegion>(builder.Configuration["AccountServer:Region"] ?? "China", true, out var region)
            || !Enum.IsDefined(region)) throw new InvalidOperationException("请选择 China 或 International 服务器区域。");
        if (string.IsNullOrWhiteSpace(builder.Configuration["urls"])) builder.WebHost.UseUrls(AccountServerEndpoints.LocalDevelopmentServerUrl(region));
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 16 * 1024);
        builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.TypeInfoResolverChain.Insert(0, AccountJsonContext.Default));
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            foreach (var proxy in builder.Configuration.GetSection("AccountServer:KnownProxies").Get<string[]>() ?? [])
                options.KnownProxies.Add(IPAddress.Parse(proxy));
        });
        builder.Services.AddRateLimiter(options =>
        {
            options.AddPolicy("credentials", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
                { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));
            options.OnRejected = async (context, token) =>
            {
                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                context.HttpContext.Response.Headers.RetryAfter = "60";
                await context.HttpContext.Response.WriteAsJsonAsync(new AccountApiError("尝试次数过多，请稍后再试。"),
                    AccountJsonContext.Default.AccountApiError, cancellationToken: token);
            };
        });
        var directory = builder.Configuration["AccountServer:DataDirectory"]
            ?? Path.Combine(AppContext.BaseDirectory, "data", "accounts");
        var regionDirectory = Path.Combine(directory, AccountRegions.Slug(region));
        var store = new AccountDatabase(regionDirectory, clock ?? TimeProvider.System, region);
        var objects = new AccountSyncStorage(Path.Combine(regionDirectory, "sync"));
        await store.InitializeAsync();
        builder.Services.AddSingleton(store);
        var app = builder.Build();
        app.UseForwardedHeaders();
        // Reject insecure remote authentication; local development and a trusted
        // HTTPS reverse proxy work without shipping a private signing key.
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var authenticatedApi = context.Request.Path.StartsWithSegments("/api/accounts") || context.Request.Path.StartsWithSegments("/api/sync")
                || context.Request.Path.StartsWithSegments("/sync-blobs");
            if (authenticatedApi && !context.Request.IsHttps
                && (context.Connection.RemoteIpAddress is not { } address || !IPAddress.IsLoopback(address)))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(new AccountApiError("账号服务须通过 HTTPS 访问。"),
                    AccountJsonContext.Default.AccountApiError);
                return;
            }
            try { await next(context); }
            catch (AccountRequestException exception)
            {
                context.Response.StatusCode = exception.StatusCode;
                await context.Response.WriteAsJsonAsync(new AccountApiError(exception.Message), AccountJsonContext.Default.AccountApiError);
            }
        });
        app.Use(async (context, next) =>
        {
            if ((context.Request.Path.StartsWithSegments("/api/accounts") || context.Request.Path.StartsWithSegments("/api/sync"))
                && context.Request.Headers["X-Kkindle-Region"] != AccountRegions.Slug(region))
                throw new AccountRequestException(StatusCodes.Status403Forbidden, "账号服务区域不匹配。");
            await next(context);
        });
        app.UseRateLimiter();
        app.MapGet("/health", () => Results.Ok(new { service = "kkindle-accounts", region }));
        app.MapPost("/api/accounts/register", async (AccountCredentials credentials, CancellationToken token) =>
            Results.Json(await store.RegisterAsync(credentials, token), AccountJsonContext.Default.AccountSessionResponse,
                statusCode: StatusCodes.Status201Created)).RequireRateLimiting("credentials");
        app.MapPost("/api/accounts/login", async (AccountCredentials credentials, CancellationToken token) =>
            Results.Json(await store.SignInAsync(credentials, token), AccountJsonContext.Default.AccountSessionResponse))
            .RequireRateLimiting("credentials");
        app.MapGet("/api/accounts/session", async (HttpContext context, CancellationToken token) =>
            Results.Json(await store.GetSessionAsync(Bearer(context), token), AccountJsonContext.Default.AccountSessionInfo));
        app.MapPost("/api/accounts/logout", async (HttpContext context, CancellationToken token) =>
        {
            await store.RevokeAsync(Bearer(context), token);
            return Results.NoContent();
        });
        app.MapGet("/api/sync/objects", async (HttpContext context, CancellationToken token) =>
        {
            var session = await store.GetSessionAsync(Bearer(context), token);
            return Results.Json(objects.List(session.User.Id, context.Request.Query["prefix"].ToString()),
                AccountJsonContext.Default.AccountSyncObjectMetadataArray);
        });
        // Bytes travel through short-lived signed links, mirroring the production server's object storage.
        var links = new SignedLinks(TimeSpan.FromMinutes(15));
        var uploads = new ConcurrentDictionary<Guid, (Guid Account, string Key, long Length)>();
        app.MapMethods("/api/sync/objects/{**key}", ["HEAD"], async (string key, HttpContext context, CancellationToken token) =>
        {
            var session = await store.GetSessionAsync(Bearer(context), token);
            var file = new FileInfo(objects.ExistingPath(session.User.Id, key));
            context.Response.ContentType = "application/octet-stream";
            context.Response.ContentLength = file.Length;
            context.Response.Headers.LastModified = file.LastWriteTimeUtc.ToString("R");
        });
        app.MapPost("/api/sync/uploads", async (SyncUploadRequest request, HttpContext context, CancellationToken token) =>
        {
            var session = await store.GetSessionAsync(Bearer(context), token);
            objects.ValidateUploadKey(session.User.Id, request.Key ?? "");
            if (request.Length is < 0 or > AccountSyncStorage.MaxObjectBytes) throw new AccountRequestException(413, "同步文件超过大小限制。");
            var id = Guid.NewGuid();
            uploads[id] = (session.User.Id, request.Key!, request.Length);
            var (url, expires) = links.Sign(context.Request, $"PUT|{session.User.Id:N}|{id:N}|{request.Length}");
            return Results.Json(new SyncUploadGrant(id, url, expires), AccountJsonContext.Default.SyncUploadGrant);
        });
        app.MapPost("/api/sync/uploads/{upload:guid}/complete", async (Guid upload, HttpContext context, CancellationToken token) =>
        {
            var session = await store.GetSessionAsync(Bearer(context), token);
            if (!uploads.TryGetValue(upload, out var pending) || pending.Account != session.User.Id)
                throw new AccountRequestException(404, "上传授权不存在或已过期。");
            objects.CommitPending(session.User.Id, upload, pending.Key, pending.Length);
            uploads.TryRemove(upload, out _);
            return Results.NoContent();
        });
        app.MapDelete("/api/sync/uploads/{upload:guid}", async (Guid upload, HttpContext context, CancellationToken token) =>
        {
            var session = await store.GetSessionAsync(Bearer(context), token);
            if (uploads.TryGetValue(upload, out var pending) && pending.Account == session.User.Id && uploads.TryRemove(upload, out _))
                objects.DiscardPending(session.User.Id, upload);
            return Results.NoContent();
        });
        app.MapPost("/api/sync/downloads", async (SyncDownloadRequest request, HttpContext context, CancellationToken token) =>
        {
            var session = await store.GetSessionAsync(Bearer(context), token);
            var file = new FileInfo(objects.ExistingPath(session.User.Id, request.Key ?? ""));
            if (request.Offset < 0 || request.Length is <= 0 || (request.Offset > 0 && request.Offset >= file.Length))
                throw new AccountRequestException(416, "请求的同步数据范围无效。");
            var end = request.Length is { } length ? Math.Min(file.Length - 1, request.Offset + length - 1) : file.Length - 1;
            var range = request.Offset == 0 && end == file.Length - 1 ? null : $"bytes={request.Offset}-{end}";
            var (url, expires) = links.Sign(context.Request, $"GET|{session.User.Id:N}|{request.Key}|{request.Offset}|{end}");
            return Results.Json(new SyncDownloadGrant(url, range, request.Offset, Math.Max(0, end - request.Offset + 1), file.Length,
                new DateTimeOffset(file.LastWriteTimeUtc), expires), AccountJsonContext.Default.SyncDownloadGrant);
        });
        app.MapPut("/sync-blobs/{token}", async (string token, HttpContext context, CancellationToken cancellation) =>
        {
            var parts = links.Verify(token, "PUT", 4);
            var length = long.Parse(parts[3]);
            if (context.Request.ContentLength != length) throw new AccountRequestException(411, "上传必须声明与授权一致的数据长度。");
            if (context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                limit.MaxRequestBodySize = Math.Max(1, length);
            await objects.WritePendingAsync(Guid.ParseExact(parts[1], "N"), Guid.ParseExact(parts[2], "N"), context.Request.Body, length, cancellation);
            return Results.NoContent();
        });
        app.MapGet("/sync-blobs/{token}", async (string token, HttpContext context, CancellationToken cancellation) =>
        {
            var parts = links.Verify(token, "GET", 5);
            var path = objects.ExistingPath(Guid.ParseExact(parts[1], "N"), parts[2]);
            long start = long.Parse(parts[3]), end = long.Parse(parts[4]);
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
            if (end >= input.Length) throw new AccountRequestException(409, "同步数据已更新，请重新同步。");
            if (!(start == 0 && end == input.Length - 1))
            {
                if (context.Request.Headers.Range.ToString() != $"bytes={start}-{end}") throw new AccountRequestException(403, "同步链接与请求范围不一致。");
                context.Response.StatusCode = StatusCodes.Status206PartialContent;
                context.Response.Headers.ContentRange = $"bytes {start}-{end}/{input.Length}";
            }
            context.Response.ContentType = "application/octet-stream";
            context.Response.ContentLength = Math.Max(0, end - start + 1);
            input.Seek(start, SeekOrigin.Begin);
            var remaining = end - start + 1;
            var buffer = new byte[81920];
            while (remaining > 0)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(remaining, buffer.Length)), cancellation);
                if (read == 0) break;
                await context.Response.Body.WriteAsync(buffer.AsMemory(0, read), cancellation);
                remaining -= read;
            }
        });
        app.MapDelete("/api/sync/objects/{**key}", async (string key, HttpContext context, CancellationToken token) =>
        {
            var session = await store.GetSessionAsync(Bearer(context), token);
            objects.Delete(session.User.Id, key);
            return Results.NoContent();
        });
        return app;
    }

    private static string Bearer(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            throw new AccountRequestException(StatusCodes.Status401Unauthorized, "请先登录。");
        var token = header[7..];
        if (token.Length != 43 || !token.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
            throw new AccountRequestException(StatusCodes.Status401Unauthorized, "会话无效，请重新登录。");
        return token;
    }
}

internal sealed class AccountRequestException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>HMAC-signed, time-limited transfer links; the key lives only in this process.</summary>
internal sealed class SignedLinks(TimeSpan lifetime)
{
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);

    public (Uri Url, DateTimeOffset ExpiresAt) Sign(HttpRequest request, string payload)
    {
        var expires = DateTimeOffset.UtcNow + lifetime;
        var body = Encoding.UTF8.GetBytes(payload + "|" + expires.ToUnixTimeSeconds());
        var token = Base64Url(body) + "." + Base64Url(HMACSHA256.HashData(_key, body));
        return (new Uri($"{request.Scheme}://{request.Host}{request.PathBase}/sync-blobs/{token}"), expires);
    }

    public string[] Verify(string token, string method, int fields)
    {
        var parts = token.Split('.');
        try
        {
            if (parts.Length == 2)
            {
                var body = Convert.FromBase64String(Pad(parts[0]));
                if (CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(_key, body), Convert.FromBase64String(Pad(parts[1]))))
                {
                    var values = Encoding.UTF8.GetString(body).Split('|');
                    if (values.Length == fields + 1 && values[0] == method
                        && DateTimeOffset.FromUnixTimeSeconds(long.Parse(values[^1])) > DateTimeOffset.UtcNow)
                        return values[..^1];
                }
            }
        }
        catch (FormatException) { }
        throw new AccountRequestException(403, "同步链接无效或已过期。");
    }

    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string Pad(string value)
    {
        value = value.Replace('-', '+').Replace('_', '/');
        return value + new string('=', (4 - value.Length % 4) % 4);
    }
}
