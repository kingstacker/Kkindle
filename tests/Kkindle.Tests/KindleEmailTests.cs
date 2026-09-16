using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle.Tests;

public sealed class KindleEmailTests
{
    [Fact]
    public void SelectsEpubBeforePdfAndRejectsUnsupportedFormats()
    {
        var bookId = Guid.NewGuid();
        var files = new[]
        {
            new BookFile { BookId = bookId, Format = "mobi" },
            new BookFile { BookId = bookId, Format = "PDF" },
            new BookFile { BookId = bookId, Format = "EPUB" }
        };

        var selected = KindleEmailSelectionPolicy.SelectPreferred(files);

        Assert.NotNull(selected);
        Assert.Equal("EPUB", selected!.Format);
        Assert.False(KindleEmailSelectionPolicy.IsSupportedFormat("mobi"));
    }

    [Fact]
    public void ExposesAllSupportedFilesForExplicitEmailSelection()
    {
        var original = new BookFile { Format = "epub", RelativePath = "book.epub" };
        var bilingual = new BookFile { Format = "epub", RelativePath = "book-双语.epub" };
        var pdf = new BookFile { Format = "pdf", RelativePath = "book.pdf" };
        var mobi = new BookFile { Format = "mobi", RelativePath = "book.mobi" };

        var candidates = KindleEmailSelectionPolicy.GetCandidates([original, bilingual, pdf, mobi]);

        Assert.Equal([original, bilingual, pdf], candidates);
    }

    [Fact]
    public void AllowsAttachmentsUpToFiftyMegabytes()
    {
        Assert.True(KindleEmailSelectionPolicy.IsWithinAttachmentLimit(KindleEmailSelectionPolicy.MaximumAttachmentBytes));
        Assert.False(KindleEmailSelectionPolicy.IsWithinAttachmentLimit(KindleEmailSelectionPolicy.MaximumAttachmentBytes + 1));
    }

    [Fact]
    public async Task SenderRejectsOversizedAttachmentBeforeConnecting()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var filePath = Path.Combine(root, "oversized.epub");
            await using (var stream = File.Create(filePath))
                stream.SetLength(KindleEmailSelectionPolicy.MaximumAttachmentBytes + 1);

            var settings = new KindleEmailSettings
            {
                KindleEmailAddress = "kindle@example.com",
                SenderEmailAddress = "sender@example.com",
                SmtpHost = "smtp.example.com",
                SmtpPort = 587,
                SmtpUsername = "sender@example.com",
                SmtpPassword = "app-password"
            };

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new KindleEmailSender().SendAsync(settings, filePath, "Send to Kindle"));

            Assert.Contains("超过 Send to Kindle 邮箱单本 50 MB 的限制", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            TestHelpers.TryDelete(root);
        }
    }

    [Fact]
    public async Task TestSenderRejectsInvalidSettingsBeforeConnecting()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new KindleEmailSender().SendTestAsync(new KindleEmailSettings()));

        Assert.Contains("请输入有效的 Kindle 收件邮箱地址", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribesWrappedSmtpFailureWithItsRootCause()
    {
        var exception = new System.Net.Mail.SmtpException(
            "Failure sending mail.",
            new IOException("The connection was closed."));

        var description = KindleEmailSender.DescribeFailure(exception);

        Assert.Contains("Failure sending mail.", description, StringComparison.Ordinal);
        Assert.Contains("The connection was closed.", description, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidatesSmtpSettingsAndNormalizesWhitespace()
    {
        var settings = new KindleEmailSettings
        {
            KindleEmailAddress = " kindle@example.com ",
            SenderEmailAddress = " sender@example.com ",
            SmtpHost = " smtp.example.com ",
            SmtpPort = 587,
            SmtpUsername = " sender@example.com ",
            SmtpPassword = "app-password"
        };

        Assert.Null(settings.Validate());
        var normalized = KindleEmailSettings.Normalize(settings);
        Assert.Equal("kindle@example.com", normalized.KindleEmailAddress);
        Assert.Equal("smtp.example.com", normalized.SmtpHost);
        Assert.Equal("app-password", normalized.SmtpPassword);
    }

    [Fact]
    public async Task EncryptsSmtpPasswordAtRest()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(Path.Combine(root, "app"));
            var store = new KindleEmailSettingsStore(paths, new TestHelpers.PlaintextSecretProtector());
            const string secret = "smtp-app-password";
            await store.SaveAsync(new KindleEmailSettings
            {
                KindleEmailAddress = "kindle@example.com",
                SenderEmailAddress = "sender@example.com",
                SmtpHost = "smtp.example.com",
                SmtpPort = 587,
                SmtpUsername = "sender@example.com",
                SmtpPassword = secret
            });

            var json = await File.ReadAllTextAsync(Path.Combine(paths.Data, "kindle-email-settings.json"));
            var loaded = await store.LoadAsync();

            Assert.DoesNotContain(secret, json, StringComparison.Ordinal);
            Assert.Equal(secret, loaded.SmtpPassword);
        }
        finally
        {
            TestHelpers.TryDelete(root);
        }
    }

    [Fact]
    public async Task LoadsLegacy163PortWithCorrectedStartTlsPort()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(Path.Combine(root, "app"));
            paths.EnsureDirectories();
            var protector = new TestHelpers.PlaintextSecretProtector();
            var store = new KindleEmailSettingsStore(paths, protector);
            var settingsPath = Path.Combine(paths.Data, "kindle-email-settings.json");
            await File.WriteAllTextAsync(settingsPath, """
                {
                  "KindleEmailAddress": "kindle@example.com",
                  "SenderEmailAddress": "sender@163.com",
                  "SmtpHost": "smtp.163.com",
                  "SmtpPort": 587,
                  "SmtpUsername": "sender@163.com",
                  "ProtectedPassword": "",
                  "EnableSsl": true
                }
                """);

            var loaded = await store.LoadAsync();

            Assert.Equal(25, loaded.SmtpPort);
            Assert.True(loaded.EnableSsl);
        }
        finally
        {
            TestHelpers.TryDelete(root);
        }
    }
}
