using System.Text;
using System.Text.Json;
using Kkindle.Core;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using MailAddress = System.Net.Mail.MailAddress;

namespace Kkindle.Infrastructure;

public sealed class KindleEmailSettings
{
    public string KindleEmailAddress { get; set; } = string.Empty;
    public string SenderEmailAddress { get; set; } = string.Empty;
    public string SmtpHost { get; set; } = string.Empty;
    public int SmtpPort { get; set; } = 587;
    public string SmtpUsername { get; set; } = string.Empty;
    public string SmtpPassword { get; set; } = string.Empty;
    public bool EnableSsl { get; set; } = true;

    public bool IsConfigured => Validate() is null;

    public KindleEmailSettings Clone() => new()
    {
        KindleEmailAddress = KindleEmailAddress,
        SenderEmailAddress = SenderEmailAddress,
        SmtpHost = SmtpHost,
        SmtpPort = SmtpPort,
        SmtpUsername = SmtpUsername,
        SmtpPassword = SmtpPassword,
        EnableSsl = EnableSsl
    };

    public string? Validate()
    {
        if (!TryCreateAddress(KindleEmailAddress)) return UiText.Get("请输入有效的 Kindle 收件邮箱地址。");
        if (!TryCreateAddress(SenderEmailAddress)) return UiText.Get("请输入有效的发件邮箱地址。");
        if (string.IsNullOrWhiteSpace(SmtpHost)) return UiText.Get("请输入 SMTP 服务器地址。");
        if (SmtpPort is < 1 or > 65535) return UiText.Get("SMTP 端口必须在 1 到 65535 之间。");
        if (string.IsNullOrWhiteSpace(SmtpUsername)) return UiText.Get("请输入 SMTP 用户名。");
        if (string.IsNullOrWhiteSpace(SmtpPassword)) return UiText.Get("请输入 SMTP 密码或应用专用密码。");
        return null;
    }

    public static KindleEmailSettings Normalize(KindleEmailSettings settings) => new()
    {
        KindleEmailAddress = settings.KindleEmailAddress.Trim(),
        SenderEmailAddress = settings.SenderEmailAddress.Trim(),
        SmtpHost = settings.SmtpHost.Trim(),
        SmtpPort = settings.SmtpPort is >= 1 and <= 65535 ? settings.SmtpPort : 587,
        SmtpUsername = settings.SmtpUsername.Trim(),
        SmtpPassword = settings.SmtpPassword,
        EnableSsl = settings.EnableSsl
    };

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

public sealed class KindleEmailSettingsStore
{
    private readonly AppPaths _paths;
    private readonly ISecretProtector _protector;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    public KindleEmailSettingsStore(AppPaths paths, ISecretProtector protector)
    {
        _paths = paths;
        _protector = protector;
    }

    private string SettingsPath => Path.Combine(_paths.Data, "kindle-email-settings.json");

    public async Task<KindleEmailSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        _paths.EnsureDirectories();
        if (!File.Exists(SettingsPath)) return new KindleEmailSettings();

        try
        {
            await using var stream = new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920, true);
            var persisted = await JsonSerializer.DeserializeAsync<PersistedKindleEmailSettings>(stream, _jsonOptions, cancellationToken);
            if (persisted is null) return new KindleEmailSettings();

            return NormalizeLoadedSettings(new KindleEmailSettings
            {
                KindleEmailAddress = persisted.KindleEmailAddress ?? string.Empty,
                SenderEmailAddress = persisted.SenderEmailAddress ?? string.Empty,
                SmtpHost = persisted.SmtpHost ?? string.Empty,
                SmtpPort = persisted.SmtpPort,
                SmtpUsername = persisted.SmtpUsername ?? string.Empty,
                SmtpPassword = string.IsNullOrWhiteSpace(persisted.ProtectedPassword)
                    ? string.Empty
                    : Encoding.UTF8.GetString(_protector.Unprotect(Convert.FromBase64String(persisted.ProtectedPassword))),
                EnableSsl = persisted.EnableSsl
            });
        }
        catch (Exception exception) when (exception is IOException
            or JsonException
            or FormatException
            or System.ComponentModel.Win32Exception
            or System.Security.Cryptography.CryptographicException)
        {
            return new KindleEmailSettings();
        }
    }

    private static KindleEmailSettings NormalizeLoadedSettings(KindleEmailSettings settings)
    {
        var normalized = KindleEmailSettings.Normalize(settings);
        if (string.Equals(normalized.SmtpHost, "smtp.163.com", StringComparison.OrdinalIgnoreCase)
            && normalized.SmtpPort == 587
            && normalized.EnableSsl)
        {
            // Older Kkindle builds used 587 for the 163 preset. The endpoint
            // can close the connection before its SMTP greeting, while port
            // 25 advertises STARTTLS and works with SmtpClient.
            normalized.SmtpPort = 25;
        }

        return normalized;
    }

    public async Task SaveAsync(KindleEmailSettings settings, CancellationToken cancellationToken = default)
    {
        using var lease = await SettingsWriteLock.AcquireAsync(_paths, cancellationToken);
        await SaveUnderLockAsync(settings, cancellationToken);
    }

    internal async Task SaveUnderLockAsync(KindleEmailSettings settings, CancellationToken cancellationToken, DateTimeOffset? syncedAt = null)
    {
        _paths.EnsureDirectories();
        var normalized = KindleEmailSettings.Normalize(settings);
        var persisted = new PersistedKindleEmailSettings
        {
            KindleEmailAddress = normalized.KindleEmailAddress,
            SenderEmailAddress = normalized.SenderEmailAddress,
            SmtpHost = normalized.SmtpHost,
            SmtpPort = normalized.SmtpPort,
            SmtpUsername = normalized.SmtpUsername,
            ProtectedPassword = string.IsNullOrWhiteSpace(normalized.SmtpPassword)
                ? string.Empty
                : Convert.ToBase64String(_protector.Protect(Encoding.UTF8.GetBytes(normalized.SmtpPassword))),
            EnableSsl = normalized.EnableSsl
        };

        var temporaryPath = SettingsPath + ".tmp";
        await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            await JsonSerializer.SerializeAsync(stream, persisted, _jsonOptions, cancellationToken);
        if (syncedAt is { } timestamp) File.SetLastWriteTimeUtc(temporaryPath, timestamp.UtcDateTime);
        SettingsFile.Publish(temporaryPath, SettingsPath);
    }

    private sealed class PersistedKindleEmailSettings
    {
        public string? KindleEmailAddress { get; set; }
        public string? SenderEmailAddress { get; set; }
        public string? SmtpHost { get; set; }
        public int SmtpPort { get; set; }
        public string? SmtpUsername { get; set; }
        public string? ProtectedPassword { get; set; }
        public bool EnableSsl { get; set; } = true;
    }
}

public sealed class KindleEmailSender
{
    public static string DescribeFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var root = exception.GetBaseException();
        if (ReferenceEquals(root, exception)
            || string.Equals(root.Message, exception.Message, StringComparison.Ordinal))
            return exception.Message;

        // Keep the root socket/TLS reason visible alongside the protocol
        // error so provider configuration failures are actionable.
        return $"{exception.Message} ({root.Message})";
    }

    public async Task SendTestAsync(
        KindleEmailSettings settings,
        CancellationToken cancellationToken = default)
    {
        ValidateSettings(settings);

        var message = CreateMessage(
            settings,
            "Kkindle 测试邮件",
            "这是一封来自 Kkindle 的测试邮件，用于验证 Kindle 邮箱发信配置。");
        await SendMessageAsync(settings, message, cancellationToken);
    }

    public async Task SendAsync(
        KindleEmailSettings settings,
        string filePath,
        string subject,
        CancellationToken cancellationToken = default)
    {
        ValidateSettings(settings);
        if (!File.Exists(filePath)) throw new FileNotFoundException("找不到要发送的书籍文件。", filePath);
        var fileSizeBytes = new FileInfo(filePath).Length;
        if (!KindleEmailSelectionPolicy.IsWithinAttachmentLimit(fileSizeBytes))
            throw new InvalidOperationException(
                $"书籍文件大小为 {fileSizeBytes / (1024d * 1024d):0.#} MB，超过 Send to Kindle 邮箱单本 50 MB 的限制。");

        var message = CreateMessage(
            settings,
            string.IsNullOrWhiteSpace(subject) ? "Send to Kindle" : subject.Trim(),
            "Sent from Kkindle.");
        await using var attachmentStream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            useAsync: true);
        var attachment = new MimePart("application", "octet-stream")
        {
            Content = new MimeContent(attachmentStream, ContentEncoding.Default),
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
            ContentTransferEncoding = ContentEncoding.Base64,
            FileName = Path.GetFileName(filePath)
        };
        message.Body = new Multipart("mixed")
        {
            new TextPart("plain") { Text = "Sent from Kkindle." },
            attachment
        };
        await SendMessageAsync(settings, message, cancellationToken);
    }

    private static void ValidateSettings(KindleEmailSettings settings)
    {
        var validationError = settings.Validate();
        if (validationError is not null) throw new InvalidOperationException(validationError);
    }

    private static MimeMessage CreateMessage(KindleEmailSettings settings, string subject, string body)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(settings.SenderEmailAddress));
        message.To.Add(MailboxAddress.Parse(settings.KindleEmailAddress));
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = body };
        return message;
    }

    private static async Task SendMessageAsync(
        KindleEmailSettings settings,
        MimeMessage message,
        CancellationToken cancellationToken)
    {
        using var client = new SmtpClient
        {
            Timeout = 180_000
        };
        var socketOptions = !settings.EnableSsl
            ? SecureSocketOptions.None
            : settings.SmtpPort == 465
                ? SecureSocketOptions.SslOnConnect
                : SecureSocketOptions.StartTls;
        await client.ConnectAsync(settings.SmtpHost, settings.SmtpPort, socketOptions, cancellationToken);
        await client.AuthenticateAsync(settings.SmtpUsername, settings.SmtpPassword, cancellationToken);
        await client.SendAsync(message, cancellationToken);
        await client.DisconnectAsync(quit: true, cancellationToken);
    }
}
