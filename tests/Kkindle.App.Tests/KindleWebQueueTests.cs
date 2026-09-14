using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Xunit;

namespace Kkindle.Ui.Tests;

public sealed partial class SettingsTests
{
    [Theory]
    [InlineData("zh-CN")]
    [InlineData("en-US")]
    public Task KindleWebQueueRequiresExplicitSendAndFitsMinimumWindow(string language) => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        ((Kkindle.App)Avalonia.Application.Current!).ApplyLanguage(language);
        var file = Path.Combine(scope.Paths.Data, "书籍 example.txt");
        await File.WriteAllTextAsync(file, "Synthetic queue test.");
        var page = new QueuePage();
        var window = new SendToKindleWindow(scope.Paths, page: page) { Width = 760, Height = 540 };
        try
        {
            window.AddFiles([file, file]);
            window.Show();
            var send = window.FindControl<Button>("SendFilesButton")!;
            await Until(() => send.IsEnabled);
            await Render();
            var list = window.FindControl<ItemsControl>("FileList")!;
            Assert.Single(list.Items);
            Assert.Equal(0, page.StageCount);
            Assert.Equal(0, page.SubmitCount);
            Assert.False(window.FindControl<Grid>("BrowserPanel")!.IsVisible);
            foreach (var name in new[] { "SendFilesButton", "ChooseFilesButton", "ClearFilesButton", "QueueSummaryText" })
                AssertWithinWindow(window.FindControl<Control>(name)!, window);

            var artifactDirectory = Environment.GetEnvironmentVariable("KKINDLE_SETTINGS_ARTIFACTS");
            if (!string.IsNullOrWhiteSpace(artifactDirectory))
            {
                Directory.CreateDirectory(artifactDirectory);
                using var image = window.CaptureRenderedFrame();
                image!.Save(Path.Combine(artifactDirectory, $"kindle-web-queue-{language}.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }
            send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => list.Items.Cast<KindleWebQueueItem>().All(item => item.State == "submitted"));
            send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(1, page.StageCount);
            Assert.Equal(1, page.SubmitCount);
            Assert.False(send.IsEnabled);
        }
        finally { window.CloseForShutdown(); }
    });

    [Fact]
    public Task KindleWebQueueRejectsUnsupportedAndEmptyFiles() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        var unsupported = Path.Combine(scope.Paths.Data, "unsupported.mobi");
        var empty = Path.Combine(scope.Paths.Data, "empty.epub");
        await File.WriteAllTextAsync(unsupported, "Synthetic test.");
        await File.WriteAllBytesAsync(empty, []);
        var window = new SendToKindleWindow(scope.Paths, page: new QueuePage());
        try
        {
            window.AddFiles([unsupported, empty, Path.Combine(scope.Paths.Data, "missing.pdf")]);
            Assert.Empty(window.FindControl<ItemsControl>("FileList")!.Items);
            Assert.True(window.FindControl<TextBlock>("QueueNoticeText")!.IsVisible);
            Assert.False(window.FindControl<Button>("SendFilesButton")!.IsEnabled);
        }
        finally { window.CloseForShutdown(); }
    });

    [Fact]
    public Task KindleWebQueueDisplaysAndRefreshesRecentAmazonStatus() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        var page = new QueuePage
        {
            RecentFiles =
            [
                new KindleWebRecentFile
                {
                    Sent = "Just now", Title = "First.epub",
                    From = "Send-to-Kindle for Web", Status = "In library"
                }
            ]
        };
        var window = new SendToKindleWindow(scope.Paths, page: page);
        try
        {
            window.Show();
            var panel = window.FindControl<Border>("RecentStatusPanel")!;
            await Until(() => panel.IsVisible);
            Assert.Equal("reader@example.invalid", window.FindControl<Button>("AccountButton")!.Content);
            var list = window.FindControl<ItemsControl>("RecentStatusList")!;
            var first = Assert.Single(list.Items.Cast<KindleWebRecentItem>());
            Assert.Equal("First.epub", first.Title);
            Assert.Equal(UiText.IsEnglish ? "In library" : "在资料库", first.StatusText);

            page.RecentFiles =
            [
                new KindleWebRecentFile
                {
                    Sent = "Just now", Title = "First.epub",
                    From = "Send-to-Kindle for Web", Status = "Processing"
                }
            ];
            await Until(() => list.Items.Cast<KindleWebRecentItem>().Single().StatusText
                == (UiText.IsEnglish ? "Processing" : "处理中"));
        }
        finally { window.CloseForShutdown(); }
    });

    [Fact]
    public Task KindleWebQueueReturnsFromLoginWithoutSending() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        var page = new QueuePage { SignedIn = false };
        var window = new SendToKindleWindow(scope.Paths, page: page);
        try
        {
            window.Show();
            await Until(() => window.FindControl<Grid>("BrowserPanel")!.IsVisible);
            Assert.Equal(UiText.Get("未登录"), window.FindControl<Button>("AccountButton")!.Content);
            page.SignedIn = true;
            await Until(() => window.FindControl<Grid>("QueuePanel")!.IsVisible);
            Assert.Equal("reader@example.invalid", window.FindControl<Button>("AccountButton")!.Content);
            await Until(() => window.FindControl<Border>("RecentStatusPanel")!.IsVisible);
            Assert.True(window.FindControl<TextBlock>("RecentStatusEmptyText")!.IsVisible);
            Assert.Equal(0, page.StageCount);
            Assert.Equal(0, page.SubmitCount);
        }
        finally { window.CloseForShutdown(); }
    });

    private sealed class QueuePage : IKindleWebPage
    {
        public bool SignedIn { get; set; } = true;
        public string Account { get; set; } = "reader@example.invalid";
        public int StageCount { get; private set; }
        public int SubmitCount { get; private set; }
        private string[] _names = [];
        public IReadOnlyList<KindleWebRecentFile> RecentFiles { get; set; } = [];
        public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<KindleWebPageSnapshot> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(new KindleWebPageSnapshot
        {
            Page = SignedIn ? "ready" : "signin", Account = SignedIn ? Account : "", ReadyNames = _names,
            Files = SubmitCount == 0 ? [] : _names.Select(name => new KindleWebPageFile(name, "submitted")).ToArray(),
            RecentFiles = RecentFiles.ToArray()
        });
        public Task StageAsync(IReadOnlyList<KindleWebFile> files, CancellationToken cancellationToken)
        {
            StageCount++;
            _names = files.Select(file => file.Name).ToArray();
            return Task.CompletedTask;
        }
        public Task<bool> SubmitAsync(CancellationToken cancellationToken)
        {
            SubmitCount++;
            return Task.FromResult(true);
        }
    }
}
