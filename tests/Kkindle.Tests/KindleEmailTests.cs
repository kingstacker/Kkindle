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
}
