using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Kkindle.Core;
using Kkindle.Infrastructure;
using System.Diagnostics;
using System.Text.Json;

namespace Kkindle;

/// <summary>
/// First Avalonia Kreader slice: EPUB preparation, a two-host native webview
/// surface, chapter switching and a local-file navigation guard. The richer
/// TOC, pagination and annotation tools stay behind this boundary for the
/// following reader slices.
/// </summary>
public partial class MainWindow
{
    private EpubReaderDocument? _readerDocument;
    private BookCardViewModel? _readerBookCard;
    private BookFile? _readerBookFile;
    private int _readerChapterIndex;
    private bool _readerShowingPreload;
    private CancellationTokenSource? _readerSessionCancellation;
    private CancellationTokenSource? _readerNavigationCancellation;
    private int _readerCloseInProgress;
    private readonly SemaphoreSlim _readerActiveHostNavigationGate = new(1, 1);
    private readonly SemaphoreSlim _readerPreloadHostNavigationGate = new(1, 1);

    private IReaderHost? CurrentReaderHost =>
        _readerShowingPreload && _readerPreloadHost is not null
            ? _readerPreloadHost
            : _readerActiveHost;

    private IReaderHost? HiddenReaderHost =>
        OperatingSystem.IsLinux()
            ? null
            : _readerShowingPreload ? _readerActiveHost : _readerPreloadHost;

    private static bool IsReaderHostReady(IReaderHost? host)
        => host?.ReadyTask.IsCompletedSuccessfully == true;

    private async Task OpenEpubReaderAsync(
        BookCardViewModel card,
        BookFile file,
        string epubPath,
        bool restoreProgress = true)
    {
        _readerSessionCancellation?.Cancel();
        _readerSessionCancellation?.Dispose();
        _readerSessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token);
        var sessionToken = _readerSessionCancellation.Token;

        try
        {
            SetTaskStatus($"正在准备《{card.Title}》的阅读缓存…");
            var contentHash = file.Sha256;
            if (contentHash.Length != 64)
                contentHash = await Hashing.Sha256Async(epubPath, sessionToken);

            var document = await _epubReader.PrepareAsync(
                epubPath,
                contentHash,
                sessionToken);

            _readerDocument = document;
            _readerBookCard = card;
            _readerBookFile = file;
            _readerChapterIndex = 0;
            _readerShowingPreload = false;
            await InitializeReaderInteractionAsync(document, file, sessionToken);

            var savedProgress = restoreProgress
                ? await _readerData.GetProgressAsync(file.Id, sessionToken)
                : null;
            _readerRestoredProgress = ValidateReaderProgress(document, savedProgress);
            if (restoreProgress && _readerRestoredProgress is { } progress)
            {
                _readerChapterIndex = progress.ChapterIndex;
                _readerScrollPosition = progress.ScrollPosition;
                _readerCurrentFragment = DecodeReaderFragment(progress.Fragment);
            }

            ReaderBookInfoText.Text = $"{card.Title} · {file.Format.ToUpperInvariant()}";
            ReaderChapterText.Text = GetReaderChapterPositionLabel();
            ReaderStatusText.Text = string.Empty;
            ReaderRoot.IsVisible = true;
            LibraryRoot.IsVisible = false;
            WindowBrandText.IsVisible = true;
            ApplyReaderPanelLayout();

            await EnsureReaderHostsAsync();
            // On Windows, keep the first document behind the opaque reader
            // surface until its Kreader CSS and bundled font have been applied:
            // WebView2 paints the navigated XHTML before the first script
            // injection. Linux native webviews can skip completion events for
            // hidden controls, so keep the visible host on-screen there.
            SetReaderHostLayer(revealActiveHost: !OperatingSystem.IsWindows());

            var target = new Uri(document.Chapters[_readerChapterIndex]);
            // A fragment identifies a section, not the precise page inside
            // that section. When a pixel breakpoint exists, navigating the
            // URL with its old chapter anchor can asynchronously pull the
            // WebView back to the chapter start after the breakpoint restore.
            if ((_readerRestoredProgress?.ScrollPosition ?? 0) <= 0
                && !string.IsNullOrWhiteSpace(_readerCurrentFragment))
            {
                target = new Uri(
                    target.AbsoluteUri
                    + "#"
                    + Uri.EscapeDataString(_readerCurrentFragment));
            }
            var loaded = await NavigateReaderHostAndWaitAsync(
                CurrentReaderHost!,
                target,
                sessionToken);
            if (!loaded)
                throw new InvalidOperationException("阅读器无法加载 EPUB 章节。");

            SetReaderHostLayer();
            FocusCurrentReaderHost();
            SetReaderTocSelectionForLocation(_readerChapterIndex, _readerCurrentFragment);
            await UpdateReaderScrollStateAsync(CurrentReaderHost!);
            ScheduleReaderBookPageCountRefresh();
            UpdateReaderToolbar();
            PrimeReaderContinuousEdgeTracking();

            await UpdateReaderBookmarkIndicatorAsync();
            // Do not replace a breakpoint with the chapter origin when its
            // first restore attempt reported that the DOM was not ready.
            if (_readerRestoredProgress is null)
                await SaveReaderProgressAsync(sessionToken);
            _ = PreloadNextReaderChapterAsync(sessionToken);
        }
        catch (OperationCanceledException) when (sessionToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await CloseReaderAsync();
            SetTaskStatus($"打开 EPUB 阅读器失败：{exception.Message}");
            await ShowMessageAsync("无法打开书籍", exception.Message);
        }
    }

    private static ReaderProgressRow? ValidateReaderProgress(
        EpubReaderDocument document,
        ReaderProgressRow? progress)
    {
        if (progress is null || string.IsNullOrWhiteSpace(progress.ChapterPath)) return null;

        try
        {
            var savedPath = Path.GetFullPath(Path.Combine(
                document.RootPath,
                progress.ChapterPath.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsPathInside(document.RootPath, savedPath)) return null;

            var chapterIndex = document.Chapters
                .Select((chapter, index) => (chapter, index))
                .Where(item => string.Equals(
                    Path.GetFullPath(item.chapter),
                    savedPath,
                    StringComparison.OrdinalIgnoreCase))
                .Select(item => item.index)
                .DefaultIfEmpty(-1)
                .First();
            if (chapterIndex < 0) return null;

            return progress with
            {
                ChapterIndex = chapterIndex,
                ScrollPosition = Math.Max(0, progress.ScrollPosition),
                Fragment = DecodeReaderFragment(progress.Fragment)
            };
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or IOException
                                          or NotSupportedException
                                          or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task EnsureReaderHostsAsync()
    {
        if (_readerActiveHost is null)
        {
            _readerActiveHost = _readerHostFactory();
            _readerActiveHost.NavigationStarting += ReaderHost_NavigationStarting;
            _readerActiveHost.NavigationCompleted += ReaderHost_NavigationCompleted;
            _readerActiveHost.WebMessageReceived += ReaderHost_WebMessageReceived;
            ReaderActiveHostSlot.Content = _readerActiveHost.View;

            if (!OperatingSystem.IsLinux())
            {
                _readerPreloadHost = _readerHostFactory();
                if (ReferenceEquals(_readerActiveHost, _readerPreloadHost))
                    throw new InvalidOperationException("阅读器宿主工厂必须返回两个不同实例。");

                _readerPreloadHost.NavigationStarting += ReaderHost_NavigationStarting;
                _readerPreloadHost.NavigationCompleted += ReaderHost_NavigationCompleted;
                _readerPreloadHost.WebMessageReceived += ReaderHost_WebMessageReceived;
                ReaderPreloadHostSlot.Content = _readerPreloadHost.View;
            }
            else
            {
                _readerPreloadHost = null;
                ReaderPreloadHostSlot.Content = null;
            }
        }

        if (OperatingSystem.IsLinux())
            _readerShowingPreload = false;

        ReaderActiveHostSlot.IsVisible = true;
        ReaderActiveHostSlot.Opacity = 1;
        ReaderActiveHostSlot.IsHitTestVisible = true;
        ReaderActiveHostSlot.ZIndex = 1;
        ReaderPreloadHostSlot.IsVisible = _readerPreloadHost is not null;
        ReaderPreloadHostSlot.Opacity = 0;
        ReaderPreloadHostSlot.IsHitTestVisible = false;
        ReaderPreloadHostSlot.ZIndex = 0;

        if (!await WaitForReaderHostReadyAsync(
                _readerActiveHost,
                TimeSpan.FromSeconds(10),
                _readerSessionCancellation?.Token ?? _lifetimeCancellation.Token))
        {
            throw new InvalidOperationException(
                "阅读器 WebView 初始化超时。请确认 Linux 已安装 WPE WebKit 运行库后重试。");
        }

        if (_readerPreloadHost is not null)
        {
            _ = WarmUpReaderPreloadHostAsync(
                _readerPreloadHost,
                _readerSessionCancellation?.Token ?? _lifetimeCancellation.Token);
        }
    }

    private static async Task<bool> WaitForReaderHostReadyAsync(
        IReaderHost host,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (host.ReadyTask.IsCompletedSuccessfully) return true;

        try
        {
            await host.ReadyTask.WaitAsync(timeout, cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private static async Task WarmUpReaderPreloadHostAsync(
        IReaderHost host,
        CancellationToken cancellationToken)
    {
        try
        {
            await WaitForReaderHostReadyAsync(
                host,
                TimeSpan.FromSeconds(3),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
        }
    }

    private async Task<bool> NavigateReaderHostAndWaitAsync(
        IReaderHost host,
        Uri target,
        CancellationToken cancellationToken)
    {
        var timing = Stopwatch.StartNew();
        var gate = ReferenceEquals(host, _readerPreloadHost)
            ? _readerPreloadHostNavigationGate
            : _readerActiveHostNavigationGate;
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (OperatingSystem.IsLinux() && !_readerIsPdf)
            {
                if (!ReaderLinuxTextFallbackOverlay.IsVisible)
                    SetReaderHostLayer(revealActiveHost: true);
            }

            if (!await WaitForReaderHostReadyAsync(
                    host,
                    TimeSpan.FromSeconds(10),
                    cancellationToken))
            {
                return false;
            }
            LogReaderChapterTiming("nav.ready", timing);

            // A TOC click can request a fragment in the chapter currently
            // prepared by the hidden host. Same-document WebView navigation
            // is not guaranteed to raise NavigationCompleted, so reuse the
            // loaded document and let ApplyReaderLocationAsync perform the
            // exact anchor/offset jump after the host swap.
            if (ReaderNavigationLocationPolicy.TargetsSameDocument(host.Source, target))
            {
                await ConfigureReaderHostAsync(host, cancellationToken);
                return true;
            }

            var completion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<ReaderNavigationCompletedEventArgs>? handler = null;
            handler = (_, args) =>
            {
                if (!ReaderNavigationLocationPolicy.TargetsSameDocument(args.Request, target)
                    && !UriEquals(args.Request, target)) return;
                completion.TrySetResult(args.IsSuccess);
            };
            host.NavigationCompleted += handler;
            try
            {
                if (!await NavigateReaderHostCoreAsync(host, target, cancellationToken))
                    return false;
                var loaded = await completion.Task.WaitAsync(TimeSpan.FromSeconds(12), cancellationToken);
                if (loaded)
                {
                    // The Linux WebView can finish navigation while Avalonia
                    // is still assigning the final reader width. Configuring
                    // and restoring against that transient narrow viewport
                    // maps a correct saved pixel to the wrong vertical page,
                    // then visibly jumps when the host expands. Keep the
                    // document hidden until the native and DOM viewports agree.
                    if (ReferenceEquals(host, CurrentReaderHost))
                        _ = await WaitForReaderViewportToMatchHostAsync(host, cancellationToken);
                    LogReaderChapterTiming("nav.viewportSettled", timing);
                    await ConfigureReaderHostAsync(host, cancellationToken);
                    LogReaderChapterTiming("nav.configured", timing);
                }
                return loaded;
            }
            finally
            {
                host.NavigationCompleted -= handler;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<bool> NavigateReaderHostCoreAsync(
        IReaderHost host,
        Uri target,
        CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsLinux()
            && Environment.GetEnvironmentVariable("KKINDLE_LINUX_HTML_STRING") == "1"
            && !_readerIsPdf
            && target.IsFile
            && host is IReaderHtmlHost htmlHost)
        {
            var path = target.LocalPath;
            if (!File.Exists(path)) return false;
            var html = await File.ReadAllTextAsync(path, cancellationToken);
            var baseUri = new UriBuilder(target)
            {
                Fragment = string.Empty
            }.Uri;
            htmlHost.NavigateToString(html, baseUri);
            return true;
        }

        host.Navigate(target);
        return true;
    }

    private async Task PreloadNextReaderChapterAsync(CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsLinux()
            || _readerDocument is null
            || HiddenReaderHost is not { } host
            || !IsReaderHostReady(host)
            || _readerChapterIndex >= _readerDocument.Chapters.Count - 1) return;

        var target = new Uri(_readerDocument.Chapters[_readerChapterIndex + 1]);
        try
        {
            await NavigateReaderHostAndWaitAsync(
                host,
                target,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            // Preloading is an optimization. The visible host remains usable
            // and will load the chapter on demand if this fails.
        }
    }

    /// <summary>
    /// DEBUG chapter-switch profiler: appends one line per stage to
    /// reader-timing.log so a slow vertical chapter turn can be attributed to
    /// navigation, font waiting, vertical cell preparation or the reveal.
    /// </summary>
    private void LogReaderChapterTiming(string stage, Stopwatch clock)
    {
        try
        {
            var entry = JsonSerializer.Serialize(new
            {
                timestamp = DateTimeOffset.Now,
                stage,
                elapsedMs = clock.ElapsedMilliseconds,
                chapter = _readerChapterIndex,
                vertical = _readerLayout.VerticalWriting,
                flowMode = _readerLayout.FlowMode
            });
            Directory.CreateDirectory(_paths.Logs);
            File.AppendAllText(
                Path.Combine(_paths.Logs, "reader-timing.log"),
                entry + Environment.NewLine);
        }
        catch
        {
            // Timing is diagnostic only and must never break a chapter turn.
        }
    }

    private async Task MoveReaderChapterAsync(int offset, bool startAtChapterTitle = false)
    {
        var chapterTiming = Stopwatch.StartNew();
        await ResetReaderInPageSearchForNavigationAsync();
        _readerPendingBookmarkQuote = null;
        _readerPendingBookmarkPosition = null;
        _readerPendingBookmarkFlowMode = 0;
        _readerPendingAnnotation = null;
        if (_readerIsPdf)
        {
            await NavigatePdfPageAsync(_readerPdfPage + offset, ReaderToken);
            return;
        }
        if (_readerDocument is null || CurrentReaderHost is null) return;
        if (startAtChapterTitle
            && FindAdjacentReaderSubchapter(offset) is { } subchapter)
        {
            await NavigateToReaderItemAsync(
                subchapter,
                ReaderToken,
                ReaderNavigationIntent.Toc,
                transitionDirection: Math.Sign(offset));
            return;
        }

        var moveLinuxFallbackToEnd = UseLinuxPlainTextRecoveryFallback
            && OperatingSystem.IsLinux()
            && !_readerIsPdf
            && offset < 0
            && !startAtChapterTitle;
        _readerLinuxTextFallbackTargetTitle = null;
        if (UseLinuxPlainTextRecoveryFallback
            && OperatingSystem.IsLinux()
            && !_readerIsPdf)
        {
            _readerScrollPosition = offset < 0 ? -1 : 0;
            _readerLinuxTextFallbackMoveToChapterEnd = moveLinuxFallbackToEnd;
            _readerLinuxTextFallbackEndFragment = null;
        }
        _readerCurrentFragment = null;
        var targetIndex = _readerChapterIndex + offset;
        if (targetIndex < 0 || targetIndex >= _readerDocument.Chapters.Count)
        {
            _readerLinuxTextFallbackMoveToChapterEnd = false;
            ReaderStatusText.Text = offset < 0 ? "已经是第一章。" : "已经是最后一章。";
            return;
        }
        ResetReaderContinuousEdgeTracking();

        var sessionToken = _readerSessionCancellation?.Token ?? _lifetimeCancellation.Token;
        _readerNavigationCancellation?.Cancel();
        var navigationCancellation = CancellationTokenSource.CreateLinkedTokenSource(sessionToken);
        _readerNavigationCancellation = navigationCancellation;
        var token = navigationCancellation.Token;
        var target = new Uri(_readerDocument.Chapters[targetIndex]);
        var hiddenHost = HiddenReaderHost;
        var host = OperatingSystem.IsLinux()
            ? CurrentReaderHost
            : IsReaderHostReady(hiddenHost) ? hiddenHost! : CurrentReaderHost;
        try
        {
            ReaderStatusText.Text = string.Empty;
            var holdOverlay = await TryShowReaderChapterHoldOverlayAsync(token);
            var loaded = await NavigateReaderHostAndWaitAsync(host, target, token);
            LogReaderChapterTiming("move.navigated", chapterTiming);
            if (!loaded) throw new InvalidOperationException("章节加载失败。");

            await ApplySavedAnnotationsAsync(host, token);
            await PositionReaderChapterBoundaryAsync(
                host,
                moveToEnd: offset < 0 && !startAtChapterTitle,
                token);
            LogReaderChapterTiming("move.boundary", chapterTiming);
            var outgoingHost = CurrentReaderHost;
            await RunReaderContentTransitionAsync(
                outgoingHost,
                host,
                offset,
                async () =>
                {
                    _readerChapterIndex = targetIndex;
                    // host was picked as the hidden host, so the layer must flip
                    // unconditionally; deriving it from CurrentReaderHost would read
                    // the stale pre-swap flag and freeze the visible chapter after the
                    // first jump (TOC / next-chapter worked only once).
                    _readerShowingPreload = ReferenceEquals(host, _readerPreloadHost);
                    SetReaderHostLayer();
                    await UpdateReaderScrollStateAsync(host);
                    await UpdateLinuxReaderTextFallbackAsync(token);
                    PositionLinuxReaderTextFallbackAtChapterEnd(
                        moveLinuxFallbackToEnd);
                    return true;
                },
                token,
                animate: !holdOverlay);
            LogReaderChapterTiming("move.transition", chapterTiming);
            FocusCurrentReaderHost();
            PrimeReaderContinuousEdgeTracking();
            ReaderChapterText.Text = GetReaderChapterPositionLabel();
            UpdateReaderToolbar();
            ReaderStatusText.Text = string.Empty;
            SetReaderTocSelectionForChapter(targetIndex);
            await UpdateReaderBookmarkIndicatorAsync();
            await SaveReaderProgressAsync(sessionToken);
            _ = PreloadNextReaderChapterAsync(sessionToken);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ReaderStatusText.Text = $"章节加载失败：{exception.Message}";
        }
        finally
        {
            await HideReaderChapterHoldOverlayAsync();
            if (ReferenceEquals(_readerNavigationCancellation, navigationCancellation))
                _readerNavigationCancellation = null;
            navigationCancellation.Dispose();
        }
    }

    private async Task PositionReaderChapterBoundaryAsync(
        IReaderHost host,
        bool moveToEnd,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Vertical writing paginates along the X axis in both flow modes, but
        // its scroll range is negative; only horizontal pagination uses the
        // positive X axis, and everything else scrolls vertically.
        var vertical = _readerLayout.VerticalWriting;
        var horizontal = !vertical && _readerLayout.FlowMode == 1;
        if (!moveToEnd)
            await host.InvokeScriptAsync(ReaderNavigationScripts.NormalizeChapterStart);
        await host.InvokeScriptAsync(
            ReaderPaginationScripts.CreateChapterBoundaryScript(moveToEnd, horizontal, vertical));
        if (_readerLayout.FlowMode == 1)
            await host.InvokeScriptAsync(ReaderPaginationScripts.Snap(_readerLayout.VerticalWriting));
        await UpdateReaderScrollStateAsync(host);
    }

    private string GetReaderChapterLabel() => _readerIsPdf
        ? $"{_readerPdfPage} / {Math.Max(1, _readerPdfPages.Count)} · 第 {_readerPdfPage} 页"
        : _readerDocument is null
            ? string.Empty
            : GetCurrentReaderTocIndex() is var tocIndex && tocIndex >= 0
                ? $"{tocIndex + 1} / {_readerTocItems.Count} · {_readerTocItems[tocIndex].Title}"
                : $"{_readerChapterIndex + 1} / {_readerDocument.Chapters.Count} · {GetReaderChapterDisplayName(_readerChapterIndex)}";

    private string GetReaderChapterPositionLabel() => _readerIsPdf
        ? $"{_readerPdfPage} / {Math.Max(1, _readerPdfPages.Count)}"
        : _readerDocument is null
            ? string.Empty
            : GetCurrentReaderTocIndex() is var tocIndex && tocIndex >= 0
                ? $"{tocIndex + 1} / {_readerTocItems.Count}"
                : $"{_readerChapterIndex + 1} / {_readerDocument.Chapters.Count}";

    private async Task MoveReaderFooterTocAsync(int direction)
    {
        direction = Math.Sign(direction);
        if (direction == 0) return;
        if (_readerIsPdf)
        {
            await NavigatePdfPageAsync(_readerPdfPage + direction, ReaderToken);
            return;
        }

        var currentIndex = GetCurrentReaderTocIndex();
        var targetIndex = currentIndex + direction;
        if (currentIndex < 0 || targetIndex < 0 || targetIndex >= _readerTocItems.Count)
        {
            ReaderStatusText.Text = direction < 0 ? "已经是目录第一项。" : "已经是目录最后一项。";
            return;
        }

        await NavigateToReaderItemAsync(
            _readerTocItems[targetIndex],
            ReaderToken,
            ReaderNavigationIntent.Toc,
            transitionDirection: direction);
    }

    private string GetReaderChapterDisplayName(int chapterIndex)
    {
        var currentFragment = chapterIndex == _readerChapterIndex
            ? DecodeReaderFragment(_readerCurrentFragment)
            : null;
        var item = string.IsNullOrWhiteSpace(currentFragment)
            ? null
            : _readerTocItems.FirstOrDefault(candidate =>
                candidate.ChapterIndex == chapterIndex
                && Uri.TryCreate(candidate.Target, UriKind.Absolute, out var target)
                && string.Equals(
                    GetReaderTargetFragment(target),
                    currentFragment,
                    StringComparison.Ordinal));
        item ??= _readerTocItems.FirstOrDefault(candidate => candidate.ChapterIndex == chapterIndex)
            ?? _readerTocItems
                .Where(candidate => candidate.ChapterIndex <= chapterIndex)
                .OrderByDescending(candidate => candidate.ChapterIndex)
                .FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(item?.Title)) return item.Title.Trim();
        if (_readerDocument is not null
            && chapterIndex >= 0
            && chapterIndex < _readerDocument.ChapterTitles.Count
            && !string.IsNullOrWhiteSpace(_readerDocument.ChapterTitles[chapterIndex]))
        {
            return _readerDocument.ChapterTitles[chapterIndex].Trim();
        }
        if (_readerDocument is not null
            && chapterIndex >= 0
            && chapterIndex < _readerDocument.Chapters.Count)
        {
            var fileName = Path.GetFileNameWithoutExtension(_readerDocument.Chapters[chapterIndex]);
            if (!string.IsNullOrWhiteSpace(fileName)) return fileName;
        }
        return $"第 {chapterIndex + 1} 章";
    }

    private void SetReaderHostLayer(bool revealActiveHost = true)
    {
        // On Linux the plain-text reading surface replaces the native webview
        // instead of layering above it: an embedded native webview paints over
        // Avalonia visuals whatever their ZIndex. Revealing the host while that
        // overlay is the live surface covers the chapter with a blank page, so
        // the guard lives here rather than at each call site — the first open
        // used to reveal the host after the overlay had already been built.
        if (revealActiveHost && IsLinuxReaderTextFallbackActive())
            revealActiveHost = false;

        var activeSlot = _readerShowingPreload ? ReaderPreloadHostSlot : ReaderActiveHostSlot;
        var hiddenSlot = _readerShowingPreload ? ReaderActiveHostSlot : ReaderPreloadHostSlot;
        // Native webviews are HWND-backed. Opacity and ZIndex alone only affect
        // the Avalonia wrapper; the hidden child window can still cover the
        // visible chapter and consume input. Toggle actual visibility as well.
        activeSlot.IsVisible = revealActiveHost;
        hiddenSlot.IsVisible = false;
        activeSlot.Opacity = revealActiveHost ? 1 : 0;
        activeSlot.IsHitTestVisible = revealActiveHost;
        hiddenSlot.Opacity = 0;
        hiddenSlot.IsHitTestVisible = false;
        activeSlot.ZIndex = 1;
        hiddenSlot.ZIndex = 0;
    }

    private void FocusCurrentReaderHost()
    {
        if (IsLinuxReaderTextFallbackActive())
        {
            Dispatcher.UIThread.Post(
                () =>
                {
                    if (ReaderRoot.IsVisible && IsLinuxReaderTextFallbackActive())
                        ReaderLinuxTextFallbackOverlay.Focus();
                },
                DispatcherPriority.Input);
            return;
        }

        if (CurrentReaderHost?.View is not Control readerControl) return;
        Dispatcher.UIThread.Post(
            () =>
            {
                if (ReaderRoot.IsVisible && ReferenceEquals(CurrentReaderHost?.View, readerControl))
                    readerControl.Focus();
            },
            DispatcherPriority.Input);
    }

    private void ReaderHost_NavigationStarting(
        object? sender,
        ReaderNavigationStartingEventArgs e)
    {
        if (e.Request is null) return;
        if (string.Equals(e.Request.Scheme, "about", StringComparison.OrdinalIgnoreCase)) return;
        if (!e.Request.IsFile)
        {
            e.Cancel = true;
            return;
        }

        try
        {
            var target = Path.GetFullPath(e.Request.LocalPath);
            var allowed = _readerIsPdf && !string.IsNullOrWhiteSpace(_readerPdfSourcePath)
                ? target.Equals(Path.GetFullPath(_readerPdfSourcePath), StringComparison.OrdinalIgnoreCase)
                : _readerDocument is not null && IsPathInside(_readerDocument.RootPath, target);
            e.Cancel = !allowed;
        }
        catch (Exception) when (e.Request.IsFile)
        {
            // Malformed file URIs fail closed instead of escaping the active
            // EPUB cache or the single PDF file selected for this session.
            e.Cancel = true;
        }
    }

    private void ReaderHost_NavigationCompleted(
        object? sender,
        ReaderNavigationCompletedEventArgs e)
    {
        if (sender is not IReaderHost host || !ReferenceEquals(host, CurrentReaderHost)) return;
        if (!e.IsSuccess)
        {
            ReaderStatusText.Text = "当前章节加载失败。";
            return;
        }
        ResetReaderStatusText();
    }

    private void ReaderHost_WebMessageReceived(
        object? sender,
        ReaderWebMessageReceivedEventArgs e)
    {
        if (sender is not IReaderHost host || !ReferenceEquals(host, CurrentReaderHost)) return;
        HandleReaderBridgeMessage(e.Body);
    }

    private async Task CloseReaderAsync()
    {
        if (Interlocked.Exchange(ref _readerCloseInProgress, 1) != 0) return;
        try
        {
        // Return-to-bookshelf is the authoritative checkpoint. Wait for a
        // page turn and any side-panel reflow to settle, then read the native
        // WebView position directly instead of trusting the last asynchronous
        // bridge scroll event (which can still contain the chapter origin).
        await _readerPageTurnGate.WaitAsync();
        try
        {
            await _readerLayoutGate.WaitAsync();
            try
            {
                if (!_readerIsPdf && CurrentReaderHost is { } currentHost)
                    await UpdateReaderScrollStateAsync(currentHost);
            }
            finally
            {
                _readerLayoutGate.Release();
            }
            await SaveReaderProgressAsync(CancellationToken.None);
        }
        finally
        {
            _readerPageTurnGate.Release();
        }
        await SaveReaderLayoutAsync(CancellationToken.None);
        StopReaderStatsTimer();
        // Reading time is accounted by the active-seconds flush (the stats
        // timer), matching the WinUI reference: time only accrues while the
        // window is active and the reader is visible.
        await FlushReaderActiveSecondsAsync();
        ExitReaderZenMode();
        Interlocked.Exchange(ref _readerPendingKeyboardNavigation, 0);
        _readerNavigationCancellation?.Cancel();
        _readerNavigationCancellation?.Dispose();
        _readerNavigationCancellation = null;
        _readerSessionCancellation?.Cancel();
        _readerSessionCancellation?.Dispose();
        _readerSessionCancellation = null;
        _readerLayoutApplyCancellation?.Cancel();
        _readerLayoutApplyCancellation?.Dispose();
        _readerLayoutApplyCancellation = null;
        _readerRelayoutCancellation?.Cancel();
        _readerRelayoutCancellation?.Dispose();
        _readerRelayoutCancellation = null;
        _readerPendingRelayoutHost = null;
        _readerPendingRelayoutState = null;
        _readerAiCancellation?.Cancel();
        _readerAiCancellation?.Dispose();
        _readerAiCancellation = null;
        _readerActiveHost?.Stop();
        _readerPreloadHost?.Stop();
        ReaderRoot.IsVisible = false;
        LibraryRoot.IsVisible = true;
        WindowBrandText.IsVisible = false;
        ReaderLayoutSettingsPopup.IsOpen = false;
        ReaderInPageSearchBar.IsVisible = false;
        ReaderLinuxTextFallbackOverlay.IsVisible = false;
        ReaderLinuxTextFallbackText.Text = string.Empty;
        StopReaderFootnoteHoverPoll();
        _readerFootnoteHoverSequence++;
        HideReaderFootnotePopup();
        _readerBookmarkIndicatorSequence++;
        ReaderBookmarkCornerMarker.IsVisible = false;
        ReaderTocCompactPanel.IsVisible = false;
        _readerSliderDragging = false;
        _readerSliderPreviewVisible = false;
        ReaderChapterPreviewPopup.IsOpen = false;
        ClearReaderCompactNavigationItems();
        ReaderAssistantPanel.IsVisible = false;
        ReaderRoot.ColumnDefinitions[2].Width = new GridLength(0);
        ReaderContentPanel.RowDefinitions[0].Height = new GridLength(52);
        ReaderHeaderBar.IsVisible = true;
        ReaderContentPanel.RowDefinitions[2].Height = new GridLength(50);
        ReaderFooterBar.IsVisible = true;
        ReaderTransitionCover.Opacity = 0;
        _readerAssistantVisibleBeforeZen = false;
        _readerIsPdf = false;
        _readerPdfPages = [];
        _readerPdfSourcePath = null;
        ReaderBookmarks.Clear();
        ReaderAnnotations.Clear();
        ReaderSearchResults.Clear();
        _readerPendingChunkOffset = null;
        _readerPendingSearchQuery = null;
        _readerPendingSearchContext = null;
        _readerPendingBookmarkQuote = null;
        _readerPendingBookmarkPosition = null;
        _readerPendingBookmarkFlowMode = 0;
        _readerPendingAnnotation = null;
        _readerCurrentFragment = null;
        _readerSearchSequence++;
        _readerWholeSearchSequence++;
        ReaderAiMessages.Clear();
        ReaderAiSources.Clear();
        _readerPendingSelection = null;
        _readerPendingSelectionStartOffset = 0;
        _readerPendingSelectionEndOffset = 0;
        _readerPendingSelectionPrefix = string.Empty;
        _readerPendingSelectionSuffix = string.Empty;
        await RefreshReadingMaterialsIfDirtyAsync();
        _readerDocument = null;
        _readerBookCard = null;
        _readerBookFile = null;
        await Task.CompletedTask;
        }
        finally
        {
            Interlocked.Exchange(ref _readerCloseInProgress, 0);
        }
    }

    private async Task SaveReaderProgressAsync(CancellationToken cancellationToken)
    {
        if (_readerBookCard is null || _readerBookFile is null) return;

        if (_readerIsPdf)
        {
            if (_readerPdfPages.Count == 0) return;
            var pdfProgress = new ReaderProgressRow(
                _readerBookCard.Book.Id,
                _readerBookFile.Id,
                $"pdf:page:{_readerPdfPage}",
                null,
                _readerPdfPage - 1,
                (int)Math.Round(_readerScrollPosition),
                CalculateReaderProgressPercent(),
                0,
                DateTimeOffset.UtcNow);
            try { await _readerData.SaveProgressAsync(pdfProgress, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch { }
            return;
        }

        if (_readerDocument is null
            || _readerChapterIndex < 0
            || _readerChapterIndex >= _readerDocument.Chapters.Count) return;

        TryApplyLinuxReaderTextFallbackState();

        var chapterPath = Path.GetRelativePath(
                _readerDocument.RootPath,
                _readerDocument.Chapters[_readerChapterIndex])
            .Replace('\\', '/');
        var progress = new ReaderProgressRow(
            _readerBookCard.Book.Id,
            _readerBookFile.Id,
            chapterPath,
            _readerCurrentFragment,
            _readerChapterIndex,
            (int)Math.Round(_readerScrollPosition),
            CalculateReaderProgressPercent(),
            _readerLayout.FlowMode,
            DateTimeOffset.UtcNow);

        try
        {
            await _readerData.SaveProgressAsync(progress, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            // Progress is best-effort and must not make a readable chapter fail.
        }
    }

    private static bool UriEquals(Uri? left, Uri right) =>
        left is not null
        && string.Equals(left.AbsoluteUri, right.AbsoluteUri, StringComparison.OrdinalIgnoreCase);

    private static bool IsPathInside(string root, string path)
    {
        var boundary = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(boundary, StringComparison.OrdinalIgnoreCase);
    }

    private async void CloseReaderButton_Click(object? sender, RoutedEventArgs e)
        => await CloseReaderAsync();

    private async void ReaderPreviousButton_Click(object? sender, RoutedEventArgs e)
        => await MoveReaderFooterTocAsync(-1);

    private async void ReaderNextButton_Click(object? sender, RoutedEventArgs e)
        => await MoveReaderFooterTocAsync(1);
}
