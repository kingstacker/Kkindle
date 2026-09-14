using Kkindle.Core;

namespace Kkindle.Tests;

public sealed class KindleWebSendTests
{
    [Theory]
    [InlineData("EPUB", true)]
    [InlineData(".pdf", true)]
    [InlineData("docx", true)]
    [InlineData("jpg", true)]
    [InlineData("mobi", false)]
    [InlineData("azw3", false)]
    [InlineData("kfx", false)]
    [InlineData("exe", false)]
    [InlineData(null, false)]
    public void UsesOfficialWebFormats(string? format, bool accepted) =>
        Assert.Equal(accepted, KindleWebFilePolicy.IsSupportedFormat(format));

    [Fact]
    public void WebLimitIsIndependentOfEmailLimit()
    {
        Assert.True(KindleWebFilePolicy.IsWithinLimit(KindleWebFilePolicy.MaximumFileBytes));
        Assert.False(KindleWebFilePolicy.IsWithinLimit(KindleWebFilePolicy.MaximumFileBytes + 1));
        Assert.False(KindleWebFilePolicy.IsWithinLimit(0));
        Assert.False(KindleWebFilePolicy.IsWithinLimit(-1));
        Assert.True(KindleWebFilePolicy.IsWithinLimit(100 * 1024 * 1024));
        Assert.False(KindleEmailSelectionPolicy.IsWithinAttachmentLimit(100 * 1024 * 1024));
    }

    [Theory]
    [InlineData("https://www.amazon.com/sendtokindle", true)]
    [InlineData("https://www.amazon.com/sendtokindle/?ref=test", true)]
    [InlineData("https://www.amazon.com/ap/signin", false)]
    [InlineData("https://www.amazon.com/-/zh/ap/signin", false)]
    [InlineData("https://www.amazon.com/other/sendtokindle", false)]
    [InlineData("https://www.amazon.de/sendtokindle", false)]
    [InlineData("https://www.amazon.com.evil.example/sendtokindle", false)]
    [InlineData("http://www.amazon.com/sendtokindle", false)]
    public void UploadBoundaryIsNarrowerThanLoginNavigation(string url, bool accepted) =>
        Assert.Equal(accepted, KindleWebNavigationPolicy.IsUploadPage(new Uri(url)));

    [Fact]
    public async Task WaitsForLoginThenSubmitsExactlyOnce()
    {
        var page = new FakePage { LoginReads = 2 };
        var workflow = Workflow(page);
        var phases = new List<string>();
        var results = await workflow.SendAsync(Batch, (phase, _) => phases.Add(phase), CancellationToken.None);
        Assert.Contains("signin", phases);
        Assert.Equal(1, page.StageCount);
        Assert.Equal(1, page.SubmitCount);
        Assert.True(workflow.SubmissionAttempted);
        Assert.All(results, file => Assert.Equal("submitted", file.Status));
    }

    [Fact]
    public async Task RefusesMismatchedPageFileList()
    {
        var page = new FakePage { ReplaceReadyNames = true };
        var workflow = Workflow(page);
        await Assert.ThrowsAsync<TimeoutException>(() => workflow.SendAsync(Batch, (_, _) => { }, CancellationToken.None));
        Assert.Equal(0, page.SubmitCount);
        Assert.False(workflow.SubmissionAttempted);
    }

    [Fact]
    public async Task CancellationBeforeSendDoesNotSubmit()
    {
        using var cancellation = new CancellationTokenSource();
        var page = new FakePage { OnStage = cancellation.Cancel };
        var workflow = Workflow(page);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            workflow.SendAsync(Batch, (_, _) => { }, cancellation.Token));
        Assert.Equal(0, page.SubmitCount);
        Assert.False(workflow.SubmissionAttempted);
    }

    [Fact]
    public async Task AmbiguousSubmitFailureIsNeverRetried()
    {
        var page = new FakePage { ThrowOnSubmit = true };
        var workflow = Workflow(page);
        await Assert.ThrowsAsync<IOException>(() => workflow.SendAsync(Batch, (_, _) => { }, CancellationToken.None));
        Assert.Equal(1, page.SubmitCount);
        Assert.True(workflow.SubmissionAttempted);
    }

    [Fact]
    public async Task RejectedSubmissionCanBeRetriedByUser()
    {
        var page = new FakePage { AcceptSubmit = false };
        var workflow = Workflow(page);
        await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.SendAsync(Batch, (_, _) => { }, CancellationToken.None));
        Assert.Equal(1, page.SubmitCount);
        Assert.False(workflow.SubmissionAttempted);
    }

    [Fact]
    public async Task UploadPercentageAndUnrelatedHistoryDoNotProveSuccess()
    {
        var page = new FakePage
        {
            Result = new()
            {
                Page = "ready", Percentage = 100, HasError = true,
                Files = [new("unrelated.epub", "submitted"), new("One.epub", "submitted")]
            }
        };
        var results = await Workflow(page).SendAsync(Batch, (_, _) => { }, CancellationToken.None);
        Assert.Equal("submitted", results[0].Status);
        Assert.Equal("unknown", results[1].Status);
        Assert.Equal(1, page.SubmitCount);
    }

    [Fact]
    public async Task PreservesPerFileFailureWithoutResendingBatch()
    {
        var page = new FakePage
        {
            Result = new() { Page = "ready", Files = [new("Two.pdf", "failed"), new("One.epub", "submitted")] }
        };
        var results = await Workflow(page).SendAsync(Batch, (_, _) => { }, CancellationToken.None);
        Assert.Equal("One.epub", results[0].Name);
        Assert.Equal("submitted", results[0].Status);
        Assert.Equal("failed", results[1].Status);
        Assert.Equal(1, page.SubmitCount);
    }

    private static readonly KindleWebFile[] Batch =
        [new("/one.epub", "One.epub", 12), new("/two.pdf", "Two.pdf", 24)];

    private static KindleWebSendWorkflow Workflow(FakePage page) => new(page)
    {
        PollInterval = TimeSpan.FromMilliseconds(1), PageTimeout = TimeSpan.FromMilliseconds(100),
        SignInTimeout = TimeSpan.FromSeconds(1), UploadTimeout = TimeSpan.FromSeconds(1)
    };

    private sealed class FakePage : IKindleWebPage
    {
        public int LoginReads { get; set; }
        public int StageCount { get; private set; }
        public int SubmitCount { get; private set; }
        public bool ReplaceReadyNames { get; init; }
        public bool ThrowOnSubmit { get; init; }
        public bool AcceptSubmit { get; init; } = true;
        public Action? OnStage { get; init; }
        public KindleWebPageSnapshot? Result { get; init; }
        private string[] _names = [];

        public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<KindleWebPageSnapshot> ReadAsync(CancellationToken cancellationToken)
        {
            if (LoginReads-- > 0) return Task.FromResult(new KindleWebPageSnapshot { Page = "signin" });
            if (SubmitCount > 0) return Task.FromResult(Result ?? new KindleWebPageSnapshot
            {
                Page = "ready", Files = _names.Select(name => new KindleWebPageFile(name, "submitted")).ToArray()
            });
            return Task.FromResult(new KindleWebPageSnapshot
            {
                Page = "ready", ReadyNames = ReplaceReadyNames && StageCount > 0 ? ["unexpected.epub"] : _names
            });
        }
        public Task StageAsync(IReadOnlyList<KindleWebFile> files, CancellationToken cancellationToken)
        {
            StageCount++;
            _names = files.Select(file => file.Name).ToArray();
            OnStage?.Invoke();
            return Task.CompletedTask;
        }
        public Task<bool> SubmitAsync(CancellationToken cancellationToken)
        {
            SubmitCount++;
            if (ThrowOnSubmit) throw new IOException("Connection lost after click.");
            return Task.FromResult(AcceptSubmit);
        }
    }
}
