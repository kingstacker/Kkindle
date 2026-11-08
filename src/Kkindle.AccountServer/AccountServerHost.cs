using System.Net;
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
        if (string.IsNullOrWhiteSpace(builder.Configuration["urls"])) builder.WebHost.UseUrls(AccountRegions.DefaultServerUrl(region));
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
            var authenticatedApi = context.Request.Path.StartsWithSegments("/api/accounts") || context.Request.Path.StartsWithSegments("/api/sync");
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
        app.MapMethods("/api/sync/objects/{**key}", ["GET", "HEAD"], async (string key, HttpContext context, CancellationToken token) =>
        {
            var session = await store.GetSessionAsync(Bearer(context), token);
            var path = objects.ExistingPath(session.User.Id, key);
            return Results.File(path, "application/octet-stream", enableRangeProcessing: true);
        });
        app.MapPut("/api/sync/objects/{**key}", async (string key, HttpContext context, CancellationToken token) =>
        {
            var session = await store.GetSessionAsync(Bearer(context), token);
            if (context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                limit.MaxRequestBodySize = AccountSyncStorage.MaxObjectBytes;
            await objects.PutAsync(session.User.Id, key, context.Request.Body, token);
            return Results.NoContent();
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
