#if KKINDLE_SWIFT_UI
using System.Text.Json;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Kkindle.Mobile;
using static Kkindle.iOSHead.IOSNativeJson;

namespace Kkindle.iOSHead;

internal sealed partial class IOSNativeFrontend
{
    private S3SyncSettings? _syncDraft;
    private static readonly SemaphoreSlim DictionaryGate = new(1, 1);

    private async Task<object> DispatchSettingsAsync(string command, JsonElement args, CancellationToken token)
    {
        using var operation = BeginOperation();
        var modules = _shell.Modules;
        if (command.StartsWith("dictionary.", StringComparison.Ordinal))
        {
            await DictionaryGate.WaitAsync(token);
            try { return await DispatchDictionaryAsync(command, args, token); }
            finally { DictionaryGate.Release(); }
        }
        switch (command)
        {
            case "settings.account.get":
                return await modules.Accounts.Service.GetStatusAsync(Flag(args, "validateSession"), token);
            case "settings.account.select":
                return await modules.Accounts.Service.SelectRegionAsync((AccountRegion)args.GetProperty("region").GetInt32(), token);
            case "settings.account.login":
            case "settings.account.register":
                if (args.TryGetProperty("region", out var loginRegion))
                    await modules.Accounts.Service.SelectRegionAsync((AccountRegion)loginRegion.GetInt32(), token);
                return await modules.Accounts.Service.SignInAsync(Text(args, "serverUrl"), Text(args, "username"),
                    Text(args, "password"), command == "settings.account.register", token,
                    command == "settings.account.register" ? Text(args, "email") : null,
                    command == "settings.account.register" ? Text(args, "confirmPassword") : null);
            case "settings.account.logout":
                var revoked = await modules.Accounts.Service.SignOutAsync(token);
                return Data(("message", revoked ? "已退出登录。" : "已在本机退出；服务器暂时不可用，远端会话将在到期后失效。"));
            case "settings.membership.get": return await modules.Accounts.Membership.RefreshAsync(token);
            case "settings.membership.products": return await modules.Accounts.Membership.ProductsAsync(token);
            case "settings.membership.orders": return await modules.Accounts.Membership.OrdersAsync(token);
            case "settings.membership.redeem":
                return await modules.Accounts.Membership.RedeemActivationCodeAsync(Text(args, "code"), Guid.Parse(Text(args, "requestId")), token);
            case "settings.membership.purchase":
                return await modules.Accounts.Membership.PurchaseAsync(Text(args, "productId"), Guid.Parse(Text(args, "requestId")), token);
            case "settings.membership.cancel":
                await modules.Accounts.Membership.CancelOrderAsync(Guid.Parse(Text(args, "orderId")), token);
                return Data(("message", "订单已取消。"));
            case "settings.membership.kick":
                await modules.Accounts.Membership.KickAsync(Guid.Parse(Text(args, "deviceId")), token);
                return Data(("message", "设备已退出登录。"));
            case "settings.developer.toggle":
                if (!_shell.TryToggleDeveloperMode(Text(args, "password"))) throw new InvalidOperationException("密码错误");
                break;
            case "settings.profile":
                await modules.Library.Books.InitializeAsync(token);
                var profileBooks = await modules.Library.Books.SearchAsync(cancellationToken: token);
                var syncProfile = await modules.Sync.Service.LoadSettingsAsync(token);
                var aiProfile = await modules.Ai.Settings.LoadAsync(token);
                var emailProfile = await modules.Integrations.KindleEmailSettings.LoadAsync(token);
                return Data(("version", ApplicationVersion.GetDisplayVersion(typeof(MobileApp).Assembly)),
                    ("sync", syncProfile.Settings is { Enabled: true, IsConfigured: true } ? syncProfile.Settings.ProviderName + " · 已开启" : "未开启"),
                    ("ai", aiProfile.IsConfigured ? aiProfile.ProviderDisplayName : "未设置"),
                    ("kindle", emailProfile.IsConfigured ? "已设置" : "未设置"),
                    ("storage", MobileFormat.Size(profileBooks.Sum(book => book.Files.Sum(file => file.Size)))),
                    ("account", await modules.Accounts.Service.GetStatusAsync(cancellationToken: token)));
            case "settings.sync.get":
                var stored = await modules.Sync.Service.LoadSettingsAsync(token);
                _syncDraft = stored.Settings;
                return Data(("settings", stored.Settings), ("summary", await SyncSummaryAsync(stored, token)));
            case "settings.sync.save":
            case "settings.sync.test":
            case "settings.sync.run":
                var current = await modules.Sync.Service.LoadSettingsAsync(token);
                var settings = ReadSyncSettings(args, _syncDraft ?? current.Settings);
                settings = settings with { AccountRegion = modules.DataRegion };
                if (settings.Provider == SyncProvider.AccountServer)
                    settings = await modules.Accounts.Service.CreateSyncSettingsAsync(settings, token);
                if (command == "settings.sync.run") settings = settings with { Enabled = true };
                if ((settings.Enabled || command != "settings.sync.save") && settings.Validate() is { } invalid)
                    throw new InvalidOperationException(invalid);
                if (command == "settings.sync.test")
                {
                    await modules.Sync.Service.TestConnectionAsync(settings, token);
                    return Data(("message", "连接成功"));
                }
                await modules.Sync.Service.SaveSettingsAsync(current.DeviceId, settings, token);
                _syncDraft = settings;
                if (command == "settings.sync.save") return Data();
                await modules.Library.Books.InitializeAsync(token);
                await _shell.EnsureReaderDataAsync();
                var result = await _shell.Sync.RunAsync(new S3SyncStoredSettings(current.DeviceId, settings),
                    new Progress<string>(message => Publish(Data(("type", "status"), ("channel", "sync"), ("message", UiText.Localize(message))))),
                    (exception, _) => ConfirmAsync("确认同步删除", UiText.Localize(exception.Message)
                        + "\n\n继续后，这些删除会同步到其他设备。", "同步这些删除"), token, operation);
                return Data(("message", result is null ? "已取消，未同步删除。" : result.IsPartial
                    ? $"同步未完成：新增 {result.BooksAdded} 本书，下载 {result.FilesDownloaded} 个文件；请重试。"
                    : result.Changed ? $"同步完成：新增 {result.BooksAdded} 本，下载 {result.FilesDownloaded} 个文件，应用 {result.AnnotationsApplied} 条批注。"
                    : "同步完成，当前已是最新。"), ("warning", result?.Warning));
            case "settings.sync.import":
                var syncCurrent = await modules.Sync.Service.LoadSettingsAsync(token);
                var importDraft = ReadSyncSettings(args, _syncDraft ?? syncCurrent.Settings);
                var files = await _shell.Services.ImportSource.PickFilesAsync(token);
                try
                {
                    if (files.Count == 0) return importDraft;
                    if (files.Count != 1) throw new InvalidOperationException("请选择一个同步参数文件。");
                    _syncDraft = await SyncConnectionProfileService.ImportAsync(files[0], importDraft, token);
                    return _syncDraft;
                }
                finally { _shell.Services.ImportSource.ReleaseFiles(files); }
            case "settings.sync.export":
                var exportCurrent = await modules.Sync.Service.LoadSettingsAsync(token);
                await ExportSyncProfileAsync(ReadSyncSettings(args, _syncDraft ?? exportCurrent.Settings), token);
                break;
            case "settings.ai.get": return await modules.Ai.Settings.LoadAsync(token);
            case "settings.ai.defaults":
                var defaults = AiConnectionSettings.GetDefaults(Text(args, "provider", "deepseek"));
                return Data(("baseUrl", defaults.BaseUrl), ("model", defaults.Model));
            case "settings.ai.save":
            case "settings.ai.test":
                var ai = args.GetProperty("settings").Deserialize(IOSNativeJsonContext.Default.AiConnectionSettings)
                    ?? throw new InvalidDataException("无效的 AI 设置。");
                if (!Uri.TryCreate(ai.BaseUrl, UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("http" or "https"))
                    throw new InvalidOperationException("请输入有效的接口地址。");
                if (command == "settings.ai.test")
                {
                    await modules.Ai.ChatClient.TestConnectionAsync(ai, token);
                    return Data(("message", "连接成功"));
                }
                await modules.Ai.Settings.SaveAsync(ai, token);
                break;
            case "settings.kindle.get": return await modules.Integrations.KindleEmailSettings.LoadAsync(token);
            case "settings.kindle.save":
                var email = args.GetProperty("settings").Deserialize(IOSNativeJsonContext.Default.KindleEmailSettings)
                    ?? throw new InvalidDataException("无效的邮箱设置。");
                if (email.Validate() is { } emailInvalid) throw new InvalidOperationException(emailInvalid);
                await modules.Integrations.KindleEmailSettings.SaveAsync(KindleEmailSettings.Normalize(email), token);
                break;
            case "settings.speech.get":
                using (var engine = _shell.Services.CreateTtsEngine?.Invoke())
                    return Data(("settings", await modules.Reader.TtsSettings.LoadAsync(token)),
                        ("voices", engine is null ? Array.Empty<TtsVoiceInfo>() : (await engine.GetVoicesAsync(token)).ToArray()));
            case "settings.speech.save":
                var speech = await modules.Reader.TtsSettings.LoadAsync(token);
                speech.Voice = Text(args, "voice", speech.Voice);
                speech.Speed = args.GetProperty("speed").GetDouble();
                speech.AutoAdvance = Flag(args, "autoAdvance", speech.AutoAdvance);
                await modules.Reader.TtsSettings.SaveAsync(speech, token);
                break;
            default: throw new InvalidOperationException("未知设置操作：" + command);
        }
        return Data();
    }

    private async Task<string> SyncSummaryAsync(S3SyncStoredSettings stored, CancellationToken token)
    {
        if (!stored.Settings.IsConfigured) return stored.Settings.Provider == SyncProvider.AccountServer
            ? "登录账号并设置同步密钥后可与其他设备同步" : "填写连接信息后可与其他设备同步";
        var status = await _shell.Modules.Sync.Service.GetStatusAsync(stored.DeviceId, stored.Settings, token);
        return status.LastSyncAt is { } last
            ? $"上次同步 {last.LocalDateTime:yyyy/MM/dd HH:mm} · {status.SnapshotBookCount} 本"
                + (status.PendingFileDownloadCount > 0 ? $" · {status.PendingFileDownloadCount} 个文件待下载" : "")
            : "尚未同步";
    }

    private static S3SyncSettings ReadSyncSettings(JsonElement args, S3SyncSettings current)
    {
        var draft = Read(args.GetProperty("settings"), current, IOSNativeJsonContext.Default.S3SyncSettings);
        // Preserve service settings the phone doesn't edit, including fields
        // loaded by an imported connection profile.
        return S3SyncSettings.Normalize(current with
        {
            Provider = draft.Provider, Enabled = draft.Enabled, AutomaticSyncEnabled = draft.AutomaticSyncEnabled,
            DownloadBookFilesOnSync = draft.DownloadBookFilesOnSync, Endpoint = draft.Endpoint,
            AccessKey = draft.AccessKey, SecretKey = draft.SecretKey, WebDavEndpoint = draft.WebDavEndpoint,
            WebDavUsername = draft.WebDavUsername, WebDavPassword = draft.WebDavPassword,
            Bucket = draft.Bucket, Region = draft.Region, PathStyle = draft.PathStyle,
            SkipTlsVerify = draft.SkipTlsVerify, Prefix = draft.Prefix, EncryptionKey = draft.EncryptionKey,
            AccountRegion = current.AccountRegion, AccountServerUrl = current.AccountServerUrl, AccountUserId = current.AccountUserId
        });
    }

    private async Task ExportSyncProfileAsync(S3SyncSettings settings, CancellationToken token)
    {
        if (!await ConfirmAsync("导出连接参数", "参数文件会明文包含当前服务的账号和密码，请只保存或分享给信任的位置。同步加密密钥不会导出。", "继续导出")) return;
        var directory = Path.Combine(Path.GetTempPath(), "kkindle-native-sync-profile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, $"Kkindle-{settings.ProviderName}-{DateTime.Now:yyyyMMdd-HHmmss}"
                + SyncConnectionProfileService.FileExtension(settings.Provider));
            await SyncConnectionProfileService.ExportAsync(path, settings, token);
            if (_shell.Services.FileShare is { } share && await share.ShareFileAsync(path))
                _shell.ShowToast("参数文件已导出。");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private async Task<object> DispatchDictionaryAsync(string command, JsonElement args, CancellationToken token)
    {
        var dictionary = _shell.Modules.Library.Dictionary;
        switch (command)
        {
            case "dictionary.get": return (await dictionary.ListAsync(token)).ToArray();
            case "dictionary.lookup": return (await dictionary.LookupAsync(Text(args, "term").Trim(), token)).ToArray();
            case "dictionary.toggle": await dictionary.SetEnabledAsync(Text(args, "id"), Flag(args, "enabled"), token); break;
            case "dictionary.remove":
                if (await ConfirmAsync("移除词典", "移除后可以重新导入。", "移除")) await dictionary.RemoveAsync(Text(args, "id"), token);
                break;
            case "dictionary.import":
                var files = await _shell.Services.ImportSource.PickFilesAsync(token);
                try
                {
                    foreach (var file in files) await Task.Run(() => dictionary.ImportAsync(file, cancellationToken: token), token);
                }
                finally { _shell.Services.ImportSource.ReleaseFiles(files); }
                break;
            default: throw new InvalidOperationException("未知词典操作：" + command);
        }
        return Data();
    }
}
#endif
