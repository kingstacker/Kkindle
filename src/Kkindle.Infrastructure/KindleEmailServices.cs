using System.Net;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using Kkindle.Core;

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
        if (!TryCreateAddress(KindleEmailAddress)) return "请输入有效的 Kindle 收件邮箱地址。";
        if (!TryCreateAddress(SenderEmailAddress)) return "请输入有效的发件邮箱地址。";
        if (string.IsNullOrWhiteSpace(SmtpHost)) return "请输入 SMTP 服务器地址。";
        if (SmtpPort is < 1 or > 65535) return "SMTP 端口必须在 1 到 65535 之间。";
        if (string.IsNullOrWhiteSpace(SmtpUsername)) return "请输入 SMTP 用户名。";
        if (string.IsNullOrWhiteSpace(SmtpPassword)) return "请输入 SMTP 密码或应用专用密码。";
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
            await using var stream = new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            var persisted = await JsonSerializer.DeserializeAsync<PersistedKindleEmailSettings>(stream, _jsonOptions, cancellationToken);
            if (persisted is null) return new KindleEmailSettings();

            return KindleEmailSettings.Normalize(new KindleEmailSettings
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

    public async Task SaveAsync(KindleEmailSettings settings, CancellationToken cancellationToken = default)
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
        File.Move(temporaryPath, SettingsPath, overwrite: true);
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
    public async Task SendAsync(
        KindleEmailSettings settings,
        string filePath,
        string subject,
        CancellationToken cancellationToken = default)
    {
        var validationError = settings.Validate();
        if (validationError is not null) throw new InvalidOperationException(validationError);
        if (!File.Exists(filePath)) throw new FileNotFoundException("找不到要发送的书籍文件。", filePath);
        var fileSizeBytes = new FileInfo(filePath).Length;
        if (!KindleEmailSelectionPolicy.IsWithinAttachmentLimit(fileSizeBytes))
            throw new InvalidOperationException(
                $"书籍文件大小为 {fileSizeBytes / (1024d * 1024d):0.#} MB，超过 Send to Kindle 邮箱单本 50 MB 的限制。");

        using var message = new MailMessage
        {
            From = new MailAddress(settings.SenderEmailAddress),
            Subject = string.IsNullOrWhiteSpace(subject) ? "Send to Kindle" : subject.Trim(),
            Body = "Sent from Kkindle.",
            IsBodyHtml = false,
            SubjectEncoding = Encoding.UTF8,
            BodyEncoding = Encoding.UTF8
        };
        message.To.Add(new MailAddress(settings.KindleEmailAddress));
        message.Attachments.Add(new Attachment(filePath));

        using var client = new SmtpClient(settings.SmtpHost, settings.SmtpPort)
        {
            EnableSsl = settings.EnableSsl,
            UseDefaultCredentials = false,
            Credentials = new NetworkCredential(settings.SmtpUsername, settings.SmtpPassword),
            DeliveryMethod = SmtpDeliveryMethod.Network
        };
        await client.SendMailAsync(message, cancellationToken);
    }
}
