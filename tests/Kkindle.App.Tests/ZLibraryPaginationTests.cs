using System.Net;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Avalonia.Headless;
using Avalonia.Input;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Xunit;

namespace Kkindle.Ui.Tests;

public sealed partial class SettingsTests
{
    [Fact]
    public Task ZLibraryRowAndBlankClicksToggleDetails() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        using var item = new ZLibraryBookCardViewModel(new ZLibraryBook { Title = "点击测试", Author = "作者" });
        scope.Window.ZLibraryBooks.Add(item);
        scope.Call("ShowStage3Page", scope.Get<Grid>("ZLibraryPage"), null);
        await Render();
        void Click(Control target, Point point)
        {
            var windowPoint = target.TranslatePoint(point, scope.Window)!.Value;
            scope.Window.MouseDown(windowPoint, MouseButton.Left);
            scope.Window.MouseUp(windowPoint, MouseButton.Left);
        }
        var row = scope.Get<ScrollViewer>("ZLibraryResultsScrollViewer").GetVisualDescendants()
            .OfType<Border>().First(control => control.Classes.Contains("flatSubsection"));
        var panel = scope.Get<Border>("ZLibraryDetailPanel");
        Click(row, new Point(120, 25));
        await Until(() => scope.Get<Grid>("ZLibraryPage").ColumnDefinitions[1].Width.Value >= 343.9);
        await Render();
        Click(panel, new Point(100, 80));
        Assert.True(panel.IsVisible);
        await Task.Delay(200);
        Click(row, new Point(120, 25));
        await Until(() => !panel.IsVisible);
        await Render();
        Click(row, new Point(120, 25));
        await Until(() => scope.Get<Grid>("ZLibraryPage").ColumnDefinitions[1].Width.Value >= 343.9);
        await Render();
        var results = scope.Get<ScrollViewer>("ZLibraryResultsScrollViewer");
        Click(results, new Point(150, results.Bounds.Height - 30));
        await Until(() => !panel.IsVisible);
        Assert.Equal(0, scope.Get<Grid>("ZLibraryPage").ColumnDefinitions[1].Width.Value);
    });

    [Fact]
    public Task ZLibraryDetailsAndImportedMetadataPersist() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        var paths = scope.Field<AppPaths>("_paths");
        var library = scope.Field<IBookLibraryService>("_library");
        await library.InitializeAsync();
        var file = Path.Combine(paths.Data, "online-test.txt");
        await File.WriteAllTextAsync(file, "Test content for imported online book.");
        var result = await library.ImportAsync([file]);
        var imported = Assert.Single(result.Items).Book!;
        var source = new ZLibraryBook
        {
            Title = "全译新注世说新语·全2册", Author = "蒋凡 · 李笑野", Extension = "epub",
            Publisher = "人民文学出版社", Year = 2023, Pages = 1139, Identifier = "9787020096220",
            Series = "人文传统经典", Description = "<p>这是一部记载人物与风貌的经典。 &amp; 阅读测试。</p>",
            Language = "Chinese", Edition = "新版", Volume = "全2册", Size = 2200000,
            OfficialDetailUrl = "https://example.com/book", ReadOnlineAvailable = true,
            ReadOnlineUrl = "https://example.com/read"
        };
        ZLibraryMetadata.Apply(imported, source, true);
        await library.UpdateMetadataAsync(imported);
        var saved = (await library.GetBookAsync(imported.Id))!;
        Assert.Equal(source.Title, saved.Title);
        Assert.Equal(source.Author, saved.Authors);
        Assert.Equal(source.Publisher, saved.Publisher);
        Assert.Equal("2023", saved.PublishDate);
        Assert.Equal("1139", saved.PageCount);
        Assert.Equal(source.Identifier, saved.Isbn);
        Assert.Equal(source.Series, saved.Series);
        Assert.Contains("& 阅读测试", saved.Description);
        Assert.Contains("全2册", saved.Description);
        var matches = await library.GetLibraryMatchRecordsAsync();
        Assert.True(ZLibraryMetadata.Matches(Assert.Single(matches), source));
        using (var duplicate = new ZLibraryBookCardViewModel(source))
        {
            // Network is disabled and no account is configured: duplicate detection
            // must stop the action before either network/account check is reached.
            scope.Call("ZLibraryDownloadButton_Click", new Button { Tag = duplicate }, new RoutedEventArgs());
            await Until(() => scope.Get<Control>("MessageOverlay").IsVisible);
            Assert.Contains("已存在", duplicate.StatusMessage);
            Assert.Equal(1, await library.GetBookCountAsync());
            scope.Call("MessageOkButton_Click", null, new RoutedEventArgs());
            await Until(() => !duplicate.IsDownloading);
        }
        saved.Title = "手动编辑书名";
        saved.Description = "手动简介";
        ZLibraryMetadata.Apply(saved, source, false);
        Assert.Equal("手动编辑书名", saved.Title);
        Assert.Equal("手动简介", saved.Description);
        ZLibraryMetadata.Apply(saved, new ZLibraryBook { Title = "", Author = "" }, true);
        Assert.Equal("手动编辑书名", saved.Title);
        Assert.Equal("手动简介", saved.Description);

        using var item = new ZLibraryBookCardViewModel(source);
        scope.Call("ShowStage3Page", scope.Get<Grid>("ZLibraryPage"), null);
        await Render();
        var originalSearchWidth = scope.Get<TextBox>("ZLibrarySearchBox").Bounds.Width;
        var originalSearchButtonX = scope.Get<Button>("ZLibrarySearchButton").TranslatePoint(default, scope.Window)!.Value.X;
        scope.Call("ZLibraryDetailsButton_Click", new Button { Tag = item }, new RoutedEventArgs());
        Assert.True(scope.Get<Border>("ZLibraryDetailPanel").IsVisible);
        Assert.Same(item, scope.Get<Border>("ZLibraryDetailPanel").DataContext);
        await Until(() => scope.Get<Grid>("ZLibraryPage").ColumnDefinitions[1].Width.Value >= 343.9);
        await Render();
        var panel = scope.Get<Border>("ZLibraryDetailPanel");
        Assert.True(panel.Bounds.Height > scope.Get<ScrollViewer>("ZLibraryResultsScrollViewer").Bounds.Height);
        Assert.True(scope.Get<TextBox>("ZLibrarySearchBox").Bounds.Width < originalSearchWidth - 300);
        Assert.True(scope.Get<Button>("ZLibrarySearchButton").TranslatePoint(default, scope.Window)!.Value.X < originalSearchButtonX - 300);
        Assert.True(panel.TranslatePoint(default, scope.Window)!.Value.Y < scope.Get<TextBox>("ZLibrarySearchBox").TranslatePoint(default, scope.Window)!.Value.Y);
        Capture(scope.Window, "zlibrary-book-details");
        var detailScroller = scope.Get<Border>("ZLibraryDetailPanel").GetVisualDescendants().OfType<ScrollViewer>().First();
        detailScroller.Offset = new Avalonia.Vector(0, 10000);
        await Render();
        Capture(scope.Window, "zlibrary-book-details-actions");
        scope.Call("ZLibraryDetailCloseButton_Click", null, new RoutedEventArgs());
        Assert.True(panel.IsVisible);
        await Until(() => !panel.IsVisible);
        await Render();
        Assert.False(scope.Get<Border>("ZLibraryDetailPanel").IsVisible);
        Assert.Equal(0, scope.Get<Grid>("ZLibraryPage").ColumnDefinitions[1].Width.Value);
        Assert.Equal(originalSearchWidth, scope.Get<TextBox>("ZLibrarySearchBox").Bounds.Width, 1);
        scope.Call("ZLibraryDetailsButton_Click", new Button { Tag = item }, new RoutedEventArgs());
        await Task.Delay(50);
        scope.Call("ZLibraryDetailCloseButton_Click", null, new RoutedEventArgs());
        scope.Call("ZLibraryDetailsButton_Click", new Button { Tag = item }, new RoutedEventArgs());
        await Until(() => scope.Get<Grid>("ZLibraryPage").ColumnDefinitions[1].Width.Value >= 343.9);
        Assert.True(panel.IsVisible);
        Assert.Same(item, panel.DataContext);
        scope.Call("ResetZLibraryDetailPanel");
        Assert.False(panel.IsVisible);
        Assert.Equal(0, scope.Get<Grid>("ZLibraryPage").ColumnDefinitions[1].Width.Value);
    });

    [Fact]
    public Task ZLibraryPaginationKeepsCurrentPageOnFailureAndReusesSearch() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        var forms = new List<string>();
        var fail = false;
        scope.Field<ZLibraryService>("_zLibraryService").Dispose();
        scope.Set("_zLibraryService", new ZLibraryService(new PaginationHandler(async (request, token) =>
        {
            if (request.Method == HttpMethod.Get) return PaginationJson("{\"success\":1}");
            var form = await request.Content!.ReadAsStringAsync(token);
            forms.Add(form);
            if (fail) throw new HttpRequestException("connection interrupted");
            var page = form.Contains("page=2") ? 2 : 1;
            return PaginationJson($"{{\"success\":1,\"books\":[{{\"id\":{page},\"title\":\"Page {page}\"}}],\"pagination\":{{\"current\":{page},\"total_pages\":2}}}}");
        })));
        scope.Set("_zLibrarySettings", new ZLibrarySettings());
        scope.Set("_appSettings", scope.Field<AppSettings>("_appSettings") with { NetworkEnabled = true });
        scope.Call("ShowStage3Page", scope.Get<Grid>("ZLibraryPage"), null);
        scope.Get<TextBox>("ZLibrarySearchBox").Text = "original query";
        scope.Get<ComboBox>("ZLibraryExtensionBox").SelectedIndex = 1;
        await scope.Call<Task>("StartZLibrarySearchAsync");
        var first = Assert.Single(scope.Window.ZLibraryBooks);
        scope.Get<TextBox>("ZLibrarySearchBox").Text = "unsubmitted edit";
        scope.Get<ComboBox>("ZLibraryExtensionBox").SelectedIndex = 2;
        fail = true;
        scope.Call("ZLibraryNextPageButton_Click", null, new RoutedEventArgs());
        await Until(() => scope.Get<Control>("MessageOverlay").IsVisible);
        Assert.Same(first, Assert.Single(scope.Window.ZLibraryBooks));
        Assert.Equal(1, scope.Field<int>("_zLibraryPage"));
        Assert.Contains("1 / 2", scope.Get<TextBlock>("ZLibraryPageText").Text);
        scope.Call("MessageOkButton_Click", null, new RoutedEventArgs());
        await Until(() => scope.Get<Button>("ZLibraryNextPageButton").IsEnabled);
        fail = false;
        scope.Call("ZLibraryNextPageButton_Click", null, new RoutedEventArgs());
        await Until(() => scope.Field<int>("_zLibraryPage") == 2);
        Assert.Equal("Page 2", Assert.Single(scope.Window.ZLibraryBooks).Title);
        Assert.Contains("message=original+query", forms.Last());
        Assert.Contains("extensions%5B0%5D=epub", forms.Last());
        await Render();
        Capture(scope.Window, "zlibrary-page-two-after-retry");
    });

    private static HttpResponseMessage PaginationJson(string body) => new(HttpStatusCode.OK)
    { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private sealed class PaginationHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
