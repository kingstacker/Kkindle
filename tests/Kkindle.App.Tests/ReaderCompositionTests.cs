using Avalonia.Controls;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Kkindle.TestFixtures;
using Xunit;

namespace Kkindle.Ui.Tests;

public sealed partial class SettingsTests
{
    [Fact]
    public Task ReaderCreationUsesTheInjectedFormatFactoryAndAppearance() => Run(async () =>
    {
        var factory = new TestFormatFactory();
        await using var scope = await TestWindow.Create(readerHostFactory: factory);
        await scope.Call<Task>("EnsureReaderHostsAsync");
        Assert.Equal(new[] { ReaderHostFormat.Epub, ReaderHostFormat.Epub }, factory.Formats);
        Assert.NotSame(scope.Field<IReaderHost>("_readerActiveHost"), scope.Field<IReaderHost>("_readerPreloadHost"));
        Assert.All(factory.Hosts, host => Assert.NotNull(host.Appearance));

        scope.Call("ApplyReaderAppearance");
        Assert.All(factory.Hosts, host => Assert.Equal(2, host.AppearanceUpdates));
        var oldHosts = factory.Hosts.ToArray();
        scope.Set("_readerIsPdf", true);
        await scope.Call<Task>("EnsureReaderHostsAsync");
        Assert.All(oldHosts, host => Assert.Equal(1, host.DisposeCount));
        Assert.Equal(ReaderHostFormat.Pdf, factory.Formats.Last());
        Assert.Null(scope.Field<IReaderHost?>("_readerPreloadHost"));
        Assert.True(scope.Call<bool>("IsReaderHostTypeCompatible", factory.Hosts.Last()));
    });

    [Fact]
    public Task FailedPreloadCreationDoesNotPublishOrLeakAnActiveHost() => Run(async () =>
    {
        var factory = new TestFormatFactory { FailPreload = true };
        await using var scope = await TestWindow.Create(readerHostFactory: factory);
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.Call<Task>("EnsureReaderHostsAsync"));
        Assert.Null(scope.Field<IReaderHost?>("_readerActiveHost"));
        Assert.Null(scope.Field<IReaderHost?>("_readerPreloadHost"));
        Assert.Equal(1, Assert.Single(factory.Hosts).DisposeCount);
    });

    [Fact]
    public Task SingletonFactoryIsRejectedAndDisposedOnlyOnce() => Run(async () =>
    {
        var factory = new TestFormatFactory { ReuseHost = true };
        await using var scope = await TestWindow.Create(readerHostFactory: factory);
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.Call<Task>("EnsureReaderHostsAsync"));
        Assert.Null(scope.Field<IReaderHost?>("_readerActiveHost"));
        Assert.Null(scope.Field<IReaderHost?>("_readerPreloadHost"));
        Assert.Equal(1, Assert.Single(factory.Hosts).DisposeCount);
    });

    [Fact]
    public Task DesktopFactoryKeepsNativeFormatSelection() => Run(() =>
    {
        var factory = new DesktopReaderHostFactory();
        using var epub = factory.Create(ReaderHostFormat.Epub);
        using var second = factory.Create(ReaderHostFormat.Epub);
        using var pdf = factory.Create(ReaderHostFormat.Pdf);
        Assert.IsType<NativeReaderHost>(epub);
        Assert.IsType<NativePdfReaderHost>(pdf);
        Assert.NotSame(epub, second);
        Assert.True(factory.IsCompatible(epub, ReaderHostFormat.Epub));
        Assert.False(factory.IsCompatible(epub, ReaderHostFormat.Pdf));
        Assert.Throws<ArgumentOutOfRangeException>(() => factory.Create((ReaderHostFormat)99));
        return Task.CompletedTask;
    });

    [Fact]
    public Task DesktopPdfHostUsesInjectedDocumentBackend() => Run(async () =>
    {
        var backend = new FakePdfDocumentFactory();
        var factory = new DesktopReaderHostFactory(backend);
        using (var host = Assert.IsType<NativePdfReaderHost>(factory.Create(ReaderHostFormat.Pdf)))
        {
            Assert.Equal(2, await host.PrepareDocumentAsync("fake.pdf", CancellationToken.None));
            Assert.Equal("Injected title", host.CachedDocumentInfo!.Title);
            Assert.Equal(new[] { "Page 1", "Page 2" }, (await host.BuildTextIndexAsync(CancellationToken.None)).Select(page => page.Text));
        }
        Assert.Equal(1, backend.OpenCount);
        Assert.Equal(1, backend.DisposeCount);
    });

    [Fact]
    public Task DesktopWindowUsesInjectedSyncService() => Run(async () =>
    {
        var sync = new PortableSyncService();
        await using var scope = await TestWindow.Create(syncService: sync);
        Assert.Same(sync, scope.Sync);
        Assert.Equal("test-device", scope.Field<S3SyncStoredSettings>("_s3SyncStoredSettings").DeviceId);
        Assert.Equal(0, sync.SyncCount);
    });

    private sealed class PortableSyncService : ISyncService
    {
        public int SyncCount { get; private set; }
        public event EventHandler? RemoteSettingsApplied { add { } remove { } }
        public Task<S3SyncStoredSettings> LoadSettingsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new S3SyncStoredSettings("test-device", new S3SyncSettings()));
        public Task SaveSettingsAsync(string deviceId, S3SyncSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ResetLocalBaselineAsync(string deviceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task TestConnectionAsync(S3SyncSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<S3SyncStatusSummary> GetStatusAsync(string deviceId, S3SyncSettings settings, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<string> EnsureBookFileDownloadedAsync(string deviceId, S3SyncSettings settings, BookFile file, IProgress<string>? progress = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyDictionary<Guid, BookSyncStatus>> GetBookSyncStatusesAsync(string deviceId, S3SyncSettings settings, IReadOnlyCollection<Book> books, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyDictionary<Guid, BookSyncStatus>>(new Dictionary<Guid, BookSyncStatus>());
        public Task<S3SyncResult> SyncAsync(string deviceId, S3SyncSettings settings, IProgress<string>? progress = null, CancellationToken cancellationToken = default, S3SyncOptions? options = null)
        { SyncCount++; return Task.FromResult(new S3SyncResult(1, 0, 0, 0, 0, false)); }
        public Task InitializeDeletionTrackingAsync(CancellationToken cancellationToken = default, string? deviceId = null) => Task.CompletedTask;
    }

    [Fact]
    public Task PdfCanOpenNavigateAndChangeModeThroughAnInjectedHost() => Run(async () =>
    {
        var factory = new PortablePdfFactory();
        await using var scope = await TestWindow.Create(readerHostFactory: factory);
        var path = PdfFixture.Write(scope.Paths.ReaderCache);
        var library = new SqliteBookLibraryService(scope.Paths, new BookMetadataService());
        await library.ImportAsync([path]);
        var book = Assert.Single(await library.SearchAsync());
        using var card = new BookCardViewModel(book, scope.Paths.Data);
        await scope.Call<Task>("OpenPdfReaderAsync", card, book.Files[0], library.GetAbsoluteFilePath(book.Files[0]));
        Assert.True(scope.Get<Control>("ReaderRoot").IsVisible);
        var host = Assert.IsType<PortablePdfHost>(scope.Field<IReaderHost>("_readerActiveHost"));
        Assert.True(host.Prepared);
        Assert.True(scope.Get<Control>("ReaderPdfBackButton").IsVisible);
        Assert.False(scope.Get<Control>("ReaderPdfThumbnailsButton").IsVisible);
        await scope.Call<Task>("SetReaderPdfDisplayModeAsync", PdfReaderDisplayMode.TwoPage);
        Assert.Equal(PdfReaderDisplayMode.TwoPage, host.DisplayMode);
        await scope.Call<Task>("NavigatePdfPageAsync", 3, CancellationToken.None, true);
        Assert.Equal(3, host.PageNumber);
        Assert.Equal(2, scope.Field<int>("_readerChapterIndex"));
        Assert.True(host.AnnotationsApplied);
        await scope.Call<Task>("ApplyReaderPdfSearchAsync", "text", scope.Field<int>("_readerPdfSearchSequence"));
        Assert.Equal(2, scope.Field<int>("_readerSearchCount"));
        await scope.Call<Task>("NavigateReaderSearchAsync", 1, null);
        Assert.Equal(1, host.ScrolledSearchHit);
    });

    [Fact]
    public Task EpubCapabilitiesSupportPositionSearchAndSpeechWithoutNativeControl() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        var chapter = Path.Combine(scope.Paths.ReaderCache, "portable.xhtml");
        var host = new PortableTextHost(new Uri(chapter));
        scope.Set("_readerDocument", new EpubReaderDocument(scope.Paths.ReaderCache, [chapter], [], []));
        scope.Set("_readerActiveHost", host);
        scope.Get<Control>("ReaderRoot").IsVisible = true;
        Assert.Equal("portable page", await scope.Call<Task<string?>>("CaptureCurrentPageQuoteAsync"));
        var scrollTask = scope.Call<Task>("CaptureReaderScrollStateAsync", host);
        await scrollTask;
        Assert.NotNull(scrollTask.GetType().GetProperty("Result")!.GetValue(scrollTask));
        Assert.True(await scope.Call<Task<bool>>("NavigateReaderHostCoreAsync", host, host.Source!, CancellationToken.None));
        Assert.True(host.LayoutNavigationCalled);
        await scope.Call<Task>("ConfigureReaderHostCoreAsync", host, CancellationToken.None);
        Assert.True(host.Configured);
        scope.Set("_readerPendingBookmarkContentPosition", new ReaderContentPosition { TextOffset = 9 });
        await scope.Call<Task>("ApplyReaderLocationAsync", host, host.Source!, CancellationToken.None, ReaderNavigationIntent.Bookmark, false);
        Assert.True(host.RestoredPosition);
        await scope.Call<Task>("TurnReaderPageAsync", 1, false);
        Assert.Equal(1, host.CurrentPage);
        await scope.Call<Task>("ApplyReaderSearchAsync", "page", scope.Field<int>("_readerSearchSequence"), false);
        Assert.Equal(1, scope.Field<int>("_readerSearchCount"));
        Assert.Equal((9, 4), Assert.Single(host.Highlights!));
        await scope.Call<Task>("NavigateReaderSearchAsync", 0, null);
        Assert.Equal(0, host.ScrolledSearchHit);
        var speech = scope.Call<ReaderTtsDocument>("GetCurrentReaderTtsDocument");
        Assert.Equal("portable page", speech.Text);
        await speech.Highlight(1, 3);
        Assert.Equal((1, 3), host.SpeechHighlight);
        speech.ClearHighlight();
        Assert.Null(host.SpeechHighlight);
    });

    private sealed class PortableTextHost(Uri source) : IReaderConfiguredHost, IReaderLocationHost, IReaderLayoutNavigationHost, IReaderPaginationHost, IReaderAnnotationHost, IReaderSpeechHost
    {
        public object View { get; } = new Border();
        public Uri? Source => source;
        public Task ReadyTask => Task.CompletedTask;
        public bool IsPaginated => true;
        public bool Vertical => false;
        public bool LayoutNavigationCalled { get; private set; }
        public bool Configured { get; private set; }
        public bool RestoredPosition { get; private set; }
        public int PageCount => 3;
        public int CurrentPage { get; private set; }
        public string? BodyText => "portable page";
        public IReadOnlyList<(int Start, int Length)> RubyAnnotationRanges => [];
        public List<(int Start, int Length)>? Highlights { get; private set; }
        public int? ScrolledSearchHit { get; private set; }
        public (int, int)? SpeechHighlight { get; private set; }
        public event EventHandler<ReaderNavigationStartingEventArgs>? NavigationStarting { add { } remove { } }
        public event EventHandler<ReaderNavigationCompletedEventArgs>? NavigationCompleted { add { } remove { } }
        public event EventHandler<ReaderWebMessageReceivedEventArgs>? WebMessageReceived { add { } remove { } }
        public void Navigate(Uri uri) { }
        public void Navigate(Uri uri, ReaderLayoutSettings settings, bool showVerticalDebugBoxes) => LayoutNavigationCalled = true;
        public void SetNavigationFragments(IEnumerable<string> fragments) { }
        public Task<string?> InvokeScriptAsync(string script) => Task.FromResult<string?>("null");
        public void Stop() { }
        public void Dispose() { }
        public ReaderContentPosition? CaptureContentPosition() => new() { TextOffset = 9 };
        public bool IsContentPositionVisible(ReaderContentPosition? position) => position?.TextOffset == 9;
        public bool RestoreContentPosition(ReaderContentPosition? position) => RestoredPosition = position is not null;
        public Task Configure(ReaderLayoutSettings settings, double scrollPosition, string? fragment,
            bool restoreFromProgress, bool showVerticalDebugBoxes, ReaderContentPosition? contentPosition = null,
            double? legacyChapterRatio = null) { Configured = true; return Task.CompletedTask; }
        public void PreservePositionForViewportChange() { }
        public bool CanTurn(int direction) => direction > 0 && CurrentPage < PageCount - 1;
        public bool TurnPage(int direction) { if (!CanTurn(direction)) return false; CurrentPage++; return true; }
        public void ScrollByPixel(double delta) { }
        public void SetAnnotations(IReadOnlyList<ReaderAnnotation> annotations) { }
        public bool TryGetAnnotationRange(ReaderAnnotation annotation, out int start, out int end) { start = end = 0; return false; }
        public void SeekToPixelScroll(double pixelScroll) { }
        public void SeekToBoundary(bool toEnd) { }
        public void ScrollToFragment(string fragmentId) { }
        public Task RevealFootnoteAsync(string fragmentId) => Task.CompletedTask;
        public (double Position, double Ratio, double ScrollWidth, double ScrollHeight, double ClientWidth, double ClientHeight) GetScrollState() => (9, 0.5, 100, 50, 50, 50);
        public string? GetCurrentNavigationFragment() => "section";
        public string? GetCurrentPageQuote(int maxLength = 72) => BodyText;
        public IReadOnlyList<ReaderSearchMatch> FindSearchMatches(string query) => [new(9, 4)];
        public void SetSearchHighlights(List<(int Start, int Length)>? hits, int? focusIndex) => Highlights = hits;
        public void ScrollToSearchHit(int index) => ScrolledSearchHit = index;
        public void ScrollToOffset(int offset) { }
        public ReaderTtsTextSnapshot? GetSpeechText() => new("portable page", 0, Enumerable.Range(0, 13).ToArray());
        public void SetSpeechHighlight(int start, int length) => SpeechHighlight = (start, length);
        public void ClearSpeechHighlight() => SpeechHighlight = null;
    }

    private sealed class PortablePdfFactory : IReaderHostFactory
    {
        public IReaderHost Create(ReaderHostFormat format) => new PortablePdfHost();
        public bool IsCompatible(IReaderHost host, ReaderHostFormat format) =>
            format == ReaderHostFormat.Pdf && host is PortablePdfHost;
    }

    private sealed class PortablePdfHost : IPdfReadingHost, IPdfAnnotationsHost, IPdfSearchHost
    {
        public object View { get; } = new Border();
        public Uri? Source { get; private set; }
        public Task ReadyTask => Task.CompletedTask;
        public bool Prepared { get; private set; }
        public bool AnnotationsApplied { get; private set; }
        public int? ScrolledSearchHit { get; private set; }
        public int PageNumber { get; private set; } = 1;
        public int PageCount => 4;
        public PdfReaderDisplayMode DisplayMode { get; private set; }
        public PdfReaderFitMode FitMode { get; private set; }
        public double Zoom => 1;
        public string? LastError => null;
        public double NavigationTop => 0;
        public double NavigationBottom => 1;
        public IReadOnlyList<int> VisiblePageNumbers => [PageNumber];
        public PdfDocumentInfo? CachedDocumentInfo => new(4, "", "", []);
        public bool IsPointAnnotationMode { get; private set; }
        public bool IsHandToolMode { get; private set; }
        public string? SelectionFragment => null;
        public event EventHandler<ReaderNavigationStartingEventArgs>? NavigationStarting;
        public event EventHandler<ReaderNavigationCompletedEventArgs>? NavigationCompleted;
        public event EventHandler<ReaderWebMessageReceivedEventArgs>? WebMessageReceived { add { } remove { } }
        public Task<int> PrepareDocumentAsync(string path, CancellationToken cancellationToken = default)
        { Prepared = true; return Task.FromResult(PageCount); }
        public void Navigate(Uri uri) => _ = NavigateAsync(uri);
        public Task<bool> NavigateAsync(Uri uri, CancellationToken cancellationToken = default,
            bool recordHistory = true, bool preserveVerticalPosition = false)
        {
            var starting = new ReaderNavigationStartingEventArgs(uri);
            NavigationStarting?.Invoke(this, starting);
            if (starting.Cancel) return Task.FromResult(false);
            Source = uri;
            PageNumber = PdfNavigationTarget.ReadPage(uri);
            NavigationCompleted?.Invoke(this, new(uri, true));
            return Task.FromResult(true);
        }
        public Task<string?> InvokeScriptAsync(string script) => Task.FromResult<string?>("null");
        public void Stop() { }
        public void Dispose() { }
        public Task<PdfDocumentInfo> ReadDocumentInfoAsync(CancellationToken token) => Task.FromResult(CachedDocumentInfo!);
        public Task<IReadOnlyList<PdfPageText>> BuildTextIndexAsync(CancellationToken token) =>
            Task.FromResult<IReadOnlyList<PdfPageText>>(Enumerable.Range(1, 4).Select(p => new PdfPageText(p, "")).ToArray());
        public PdfPageContent? GetPageContent(int page) => null;
        public void SetPointAnnotationMode(bool enabled) => IsPointAnnotationMode = enabled;
        public void SetHandToolMode(bool enabled) => IsHandToolMode = enabled;
        public void SetAnnotations(IReadOnlyList<ReaderAnnotation> annotations) => AnnotationsApplied = true;
        public void SelectAnnotation(Guid id) { }
        public void ClearAnnotationSelection() { }
        public string GetSelectedTextForTranslation() => "";
        public string GetSelectedTextForCopy() => "";
        public void ClearSelection() { }
        public void ScrollToOffset(int offset) { }
        public void ClearSearch() { }
        public void SetSpeechHighlight(int start, int length) { }
        public void ClearSpeechHighlight() { }
        public (int Count, int Index) Find(string query, int? offset = null) => (2, 0);
        public void ScrollToSearchHit(int index) => ScrolledSearchHit = index;
        public int GetAdjacentPage(int direction) => Math.Clamp(PageNumber + Math.Sign(direction), 1, PageCount);
        public Task<bool> NavigateHistoryAsync(int direction, CancellationToken token) => Task.FromResult(false);
        public string GetPageLabel(int page) => page.ToString();
        public string GetPageDescription(int page) => page.ToString();
        public int FindLabeledPage(string label) => int.TryParse(label, out var page) ? page : -1;
        public void SetPageNumberOffset(int? offset) { }
        public void ScrollToTop(double top, bool preserveVerticalPosition = false) { }
        public void ScrollToPdfPoint(double x, double y) { }
        public Task RefreshViewportAsync(CancellationToken token) => Task.CompletedTask;
        public Task SetDisplayModeAsync(PdfReaderDisplayMode mode) { DisplayMode = mode; return Task.CompletedTask; }
        public Task SetFitModeAsync(PdfReaderFitMode mode) { FitMode = mode; return Task.CompletedTask; }
        public Task RotateClockwiseAsync() => Task.CompletedTask;
        public string CaptureViewState() => "test-pdf-state";
        public void RestoreViewSettings(string? state) { }
        public Task RestoreViewStateAsync(string? state, int legacyScrollPosition = 0) => Task.CompletedTask;
    }

    private sealed class TestFormatFactory : IReaderHostFactory
    {
        public List<ReaderHostFormat> Formats { get; } = [];
        public List<TestFormatHost> Hosts { get; } = [];
        public bool FailPreload { get; init; }
        public bool ReuseHost { get; init; }
        public IReaderHost Create(ReaderHostFormat format)
        {
            Formats.Add(format);
            if (FailPreload && Formats.Count == 2) throw new InvalidOperationException("Preload unavailable.");
            if (ReuseHost && Hosts.Count != 0) return Hosts[0];
            var host = new TestFormatHost(format);
            Hosts.Add(host);
            return host;
        }
        public bool IsCompatible(IReaderHost host, ReaderHostFormat format)
            => host is TestFormatHost test && test.Format == format;
    }

    private sealed class TestFormatHost(ReaderHostFormat format) : IReaderHost, IReaderAppearanceHost
    {
        public ReaderHostFormat Format { get; } = format;
        public object View { get; } = new Border();
        public Uri? Source => null;
        public Task ReadyTask => Task.CompletedTask;
        public ReaderAppearanceSettings? Appearance { get; private set; }
        public int AppearanceUpdates { get; private set; }
        public int DisposeCount { get; private set; }
        public event EventHandler<ReaderNavigationStartingEventArgs>? NavigationStarting { add { } remove { } }
        public event EventHandler<ReaderNavigationCompletedEventArgs>? NavigationCompleted { add { } remove { } }
        public event EventHandler<ReaderWebMessageReceivedEventArgs>? WebMessageReceived { add { } remove { } }
        public void Navigate(Uri uri) { }
        public Task<string?> InvokeScriptAsync(string script) => Task.FromResult<string?>("null");
        public void Stop() { }
        public void Dispose() => DisposeCount++;
        public void SetAppearance(ReaderAppearanceSettings appearance)
        {
            Appearance = appearance;
            AppearanceUpdates++;
        }
    }
}
