using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Kkindle.ApplicationServices;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Kkindle.Mobile.Views.Sheets;

namespace Kkindle.Mobile.Views;

/// <summary>
/// Immersive reader. EPUB uses the shared self-drawn surface in paginated mode
/// and PDF the shared PDFium surface; both retain progress in the existing
/// reader database so desktop and mobile resume the same place. Touch input is
/// classified here: side taps and swipes turn pages, a center tap toggles the
/// chrome. The hosts stay unfocusable because a focused host swallows Android's
/// back key as a reader shortcut, leaving the page impossible to leave.
/// </summary>
public partial class ReaderPage : UserControl
{
    private const double SwipeDistance = 44;
    private const double TapSlop = 12;
    private const double SwipeDominance = 1.3;
    private const string PdfPageChapterPrefix = "pdf:page:";
    private static readonly TimeSpan LongPressDelay = TimeSpan.FromMilliseconds(450);

    private readonly Book _book;
    private readonly ApplicationModules _modules;
    private readonly MobileShell _shell;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<TocEntry> _tocEntries = [];
    private readonly List<ReaderBookmark> _bookmarks = [];
    private readonly List<ReaderAnnotation> _annotations = [];
    private readonly ReaderProgressRow? _jump;
    private readonly DispatcherTimer _longPress = new() { Interval = LongPressDelay };
    private IReaderHost? _readerHost;
    private EpubReaderDocument? _epubDocument;
    private BookFile? _bookFile;
    private string? _epubPath;
    private int _chapterIndex;
    private int _pdfPage = 1;
    private int _pdfPageCount;
    private double _epubPosition;
    private double _overallProgress;
    private ReaderLayoutSettings _layout;
    private DateTimeOffset _lastProgressSave = DateTimeOffset.MinValue;
    private readonly ReadingActivityClock _activityClock = new(DateTimeOffset.UtcNow);
    private long _unsavedSeconds;
    private Point? _pressPoint;
    private bool _pressMoved;
    private bool _selectionArmed;
    private bool _expectSelection;
    private ReaderSelection? _selection;
    private bool _tapConsumed;
    private bool _sliderActive;
    private bool _turning;
    private bool _started;
    private bool _disposed;
    // Set once the book is on screen. Before that (or after a failed open) the
    // position fields hold defaults, and saving them would overwrite the real
    // progress here and, through sync, on every other device.
    private bool _ready;
    private bool _isPdf;

    /// <summary>Design-time only.</summary>
    public ReaderPage()
    {
        _book = null!;
        _modules = null!;
        _shell = null!;
        _layout = null!;
        InitializeComponent();
    }

    /// <param name="jump">Opens at this location instead of the saved progress.</param>
    public ReaderPage(Book book, ApplicationModules modules, MobileShell shell, ReaderProgressRow? jump = null)
    {
        _book = book;
        _jump = jump;
        _modules = modules;
        _shell = shell;
        _layout = BuildLayout(shell.Preferences.Current);
        InitializeComponent();
        TitleText.Text = book.Title;
        LoadingTitleText.Text = book.Title;
        if (shell.Services.CreateTtsEngine is null || shell.Services.CreateTtsPlayer is null)
        {
            ListenButton.IsVisible = false;
            ReaderActions.ColumnDefinitions = ColumnDefinitions.Parse("*,*,*");
            Grid.SetColumn(AiButton, 1);
            Grid.SetColumn(TypographyButton, 2);
        }

        const RoutingStrategies tunnel = RoutingStrategies.Tunnel;
        ReaderHostSlot.AddHandler(PointerPressedEvent, OnSurfacePressed, tunnel, handledEventsToo: true);
        ReaderHostSlot.AddHandler(PointerMovedEvent, OnSurfaceMoved, tunnel, handledEventsToo: true);
        ReaderHostSlot.AddHandler(PointerReleasedEvent, OnSurfaceReleased, tunnel, handledEventsToo: true);
        ReaderHostSlot.AddHandler(PointerCaptureLostEvent, OnSurfaceCaptureLost, RoutingStrategies.Bubble, handledEventsToo: true);
        _longPress.Tick += OnLongPress;
        ProgressSlider.AddHandler(PointerPressedEvent, OnSliderPressed, tunnel, handledEventsToo: true);
        ProgressSlider.AddHandler(PointerReleasedEvent, OnSliderReleased, tunnel, handledEventsToo: true);
        ProgressSliderGroup.SetHostVisible(false);
        ProgressSliderGroup.DragStarted += () => _sliderActive = true;
        ProgressSliderGroup.DragCompleted += canceled =>
        {
            _sliderActive = false;
            if (!canceled) _ = SeekAsync(ProgressSlider.Value);
        };
        Loaded += OnLoaded;
    }

    public bool IsChromeVisible => _usesNativeFrontend ? _nativeChromeVisible : TopChrome.Classes.Contains("visible");

    public string? PositionText => FooterPosition.Text;

    private static ReaderLayoutSettings BuildLayout(MobilePreferences preferences)
    {
        var normalized = ReaderLayoutDefaults.Normalize(new ReaderLayoutSettings(
            FontScale: preferences.ReaderFontScale,
            LineHeight: preferences.ReaderLineHeight,
            MaxWidth: 900,
            BodyPadding: ReaderLayoutDefaults.DefaultBodyPadding,
            FontFamily: ReaderFontDefaults.BundledFamily,
            FlowMode: 1,
            VerticalWriting: preferences.ReaderVerticalWriting,
            TwoPageMode: false) { ParagraphSpacing = preferences.ReaderParagraphSpacing });
        // Applied after Normalize: a phone reads better with a narrower column
        // than the desktop's 24-point floor allows.
        return normalized with
        {
            ParagraphIndent = preferences.ReaderParagraphIndent,
            BodyPadding = Math.Clamp(preferences.ReaderBodyPadding, 16, 64)
        };
    }

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (_started) return;
        _started = true;
        ApplyTheme();
        _shell.Services.SystemBars?.SetEdgeGesturesCaptured(true);
        await OpenAsync();
    }

    private async Task OpenAsync()
    {
        using var operation = MobileDataCoordinator.TryBeginOperation();
        if (operation is null) return;
        try
        {
            LoadingPanel.IsVisible = !_usesNativeFrontend;
            ErrorPanel.IsVisible = false;
            LoadingDetailText.Text = "正在读取书籍信息";

            _bookFile = ChooseReadableFile(_book);
            if (_bookFile is null)
            {
                ShowError("这本书没有可读取的 EPUB 或 PDF 文件。");
                return;
            }

            var path = _modules.Library.Books.GetAbsoluteFilePath(_bookFile);
            await _shell.EnsureReaderDataAsync();
            if (!File.Exists(path) && await TryDownloadAsync(_bookFile) is { } downloaded) path = downloaded;
            if (!File.Exists(path))
            {
                ShowError("书籍文件不在本机，请重新导入，或在“我的”里开启同步后重试。");
                return;
            }

            _bookmarks.AddRange(await _modules.Reader.Data.GetBookmarksAsync(_bookFile.Id, _lifetime.Token));
            var format = _bookFile.Format.Trim().TrimStart('.').ToLowerInvariant();
            if (format == "pdf")
            {
                await OpenPdfAsync(path, _lifetime.Token);
                return;
            }

            if (format is "epub" or "azw3" or "mobi")
            {
                await OpenEpubAsync(path, format, _lifetime.Token);
                return;
            }

            ShowError($"暂不支持在安卓阅读 {format.ToUpperInvariant()} 格式。");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ShowError(UiText.Localize(exception.Message));
        }
    }

    /// <summary>Synced books arrive as metadata; their files download when first opened.</summary>
    private async Task<string?> TryDownloadAsync(BookFile file)
    {
        var stored = await _modules.Sync.Service.LoadSettingsAsync(_lifetime.Token);
        if (!stored.Settings.Enabled || !stored.Settings.IsConfigured) return null;
        LoadingDetailText.Text = "正在从云端下载…";
        var progress = new Progress<string>(message => LoadingDetailText.Text = UiText.Localize(message));
        return await _modules.Sync.Service.EnsureBookFileDownloadedAsync(
            stored.DeviceId, stored.Settings, file, progress, _lifetime.Token);
    }

    private async Task OpenEpubAsync(string sourcePath, string format, CancellationToken cancellationToken)
    {
        _isPdf = false;
        LoadingDetailText.Text = "正在整理章节与图片…";

        _epubPath = sourcePath;
        if (format is "azw3" or "mobi")
        {
            var sourceHash = string.IsNullOrWhiteSpace(_bookFile!.Sha256)
                ? await Hashing.Sha256Async(sourcePath, cancellationToken)
                : _bookFile.Sha256;
            var converted = await _modules.Reader.FormatCache.PrepareEpubAsync(
                sourcePath, sourceHash, format, cancellationToken);
            _epubPath = converted.EpubPath;
        }

        var contentHash = string.IsNullOrWhiteSpace(_bookFile!.Sha256)
            ? await Hashing.Sha256Async(_epubPath, cancellationToken)
            : _bookFile.Sha256;
        _epubDocument = await _modules.Reader.Preparation.PrepareAsync(
            _epubPath, contentHash, cancellationToken);
        if (_epubDocument.Chapters.Count == 0)
            throw new InvalidDataException("EPUB 中没有可阅读章节。");

        var savedProgress = _jump ?? await _modules.Reader.Data.GetProgressAsync(_bookFile.Id, cancellationToken);
        var restore = ResolvePosition(savedProgress);
        _chapterIndex = Math.Clamp(restore?.ChapterIndex ?? 0, 0, _epubDocument.Chapters.Count - 1);
        _epubPosition = restore?.ScrollPosition ?? 0;

        var host = new NativeReaderHost { Focusable = false };
        _readerHost = host;
        host.SetAppearance(new ReaderAppearanceSettings { Theme = CurrentTheme });
        host.WebMessageReceived += OnReaderWebMessage;
        ReaderHostSlot.Content = host.View;
        BuildEpubToc();

        var chapterPath = _epubDocument.Chapters[_chapterIndex];
        var target = new Uri(chapterPath);
        if (restore is { ScrollPosition: <= 0 } && !string.IsNullOrWhiteSpace(restore.Fragment))
            target = new Uri(target.AbsoluteUri + "#" + Uri.EscapeDataString(restore.Fragment));

        if (!await NavigateEpubAsync(host, target, cancellationToken))
            throw new InvalidOperationException("阅读器无法加载 EPUB 章节。");

        if (restore is not null)
        {
            await host.Configure(
                _layout,
                restore.ScrollPosition,
                restore.Fragment,
                restoreFromProgress: true,
                showVerticalDebugBoxes: false,
                contentPosition: restore.ContentPosition);
        }

        _annotations.AddRange(await _modules.Reader.Data.GetAnnotationsAsync(_bookFile.Id, cancellationToken));
        host.SetAnnotations(_annotations.ToList());
        UpdateEpubLabels();
        LoadingPanel.IsVisible = false;
        _ready = true;
        NotifyNativeState();
    }

    /// <summary>
    /// Progress travels between devices, so a chapter is stored relative to the
    /// book. Older mobile builds stored a device path; those fall back to the
    /// chapter index, which stays valid everywhere.
    /// </summary>
    private ReaderProgressRow? ResolvePosition(ReaderProgressRow? saved)
    {
        if (_epubDocument is not { } document || saved is null) return null;
        if (ReaderResourcePolicy.RestoreProgress(document, saved) is { } restored) return restored;
        if (saved.ChapterIndex < 0 || saved.ChapterIndex >= document.Chapters.Count) return null;
        return saved with
        {
            ChapterPath = ChapterPathAt(saved.ChapterIndex),
            Fragment = null,
            ContentPosition = ReaderContentPosition.Validate(saved.ContentPosition)
        };
    }

    private string CurrentChapterPath =>
        _isPdf ? $"{PdfPageChapterPrefix}{_pdfPage}" : ChapterPathAt(_chapterIndex);

    private string ChapterPathAt(int index) =>
        _epubDocument is { } document && index >= 0 && index < document.Chapters.Count
            ? Path.GetRelativePath(document.RootPath, document.Chapters[index]).Replace('\\', '/')
            : string.Empty;

    private async Task OpenPdfAsync(string path, CancellationToken cancellationToken)
    {
        _isPdf = true;
        TypographyLabel.Text = "显示";
        LoadingDetailText.Text = "正在渲染 PDF 页面…";

        var savedProgress = _jump ?? await _modules.Reader.Data.GetProgressAsync(_bookFile!.Id, cancellationToken);
        var initialPage = Math.Max(1, (savedProgress?.ChapterIndex ?? 0) + 1);

        var host = new NativePdfReaderHost { Focusable = false };
        _readerHost = host;
        host.SetAppearance(new ReaderAppearanceSettings { Theme = CurrentTheme });
        host.WebMessageReceived += OnReaderWebMessage;
        ReaderHostSlot.Content = host.View;

        _pdfPageCount = await host.PrepareDocumentAsync(path, cancellationToken);
        if (_pdfPageCount <= 0) throw new InvalidOperationException("这个 PDF 没有可显示的页面。");
        _annotations.AddRange(await _modules.Reader.Data.GetAnnotationsAsync(_bookFile!.Id, cancellationToken));
        host.SetAnnotations(_annotations.ToList());
        // One whole page at a time, turned like the EPUB surface.
        await host.SetDisplayModeAsync(PdfReaderDisplayMode.SinglePage);
        await host.SetFitModeAsync(PdfReaderFitMode.Page);
        initialPage = Math.Clamp(initialPage, 1, _pdfPageCount);
        var target = new Uri(new Uri(path).AbsoluteUri + $"#page={initialPage}");
        if (!await host.NavigateAsync(target, cancellationToken))
            throw new InvalidOperationException(host.LastError ?? "阅读器无法加载 PDF。");

        await ApplyPdfZoomAsync(host);
        _pdfPage = host.PageNumber;
        BuildPdfToc(host.CachedDocumentInfo);
        UpdatePdfLabels();
        LoadingPanel.IsVisible = false;
        _ready = true;
        NotifyNativeState();
    }

    /// <summary>
    /// The index changes before navigating because position messages from the
    /// new chapter arrive while it loads; a failed load puts it back so labels
    /// and saved progress keep describing the chapter still on screen.
    /// </summary>
    private async Task<bool> NavigateToChapterAsync(NativeReaderHost host, int chapter, Uri target)
    {
        var previous = _chapterIndex;
        _chapterIndex = chapter;
        var navigated = false;
        try
        {
            navigated = await NavigateEpubAsync(host, target, _lifetime.Token);
            return navigated;
        }
        finally
        {
            if (!navigated) _chapterIndex = previous;
        }
    }

    private async Task<bool> NavigateEpubAsync(
        NativeReaderHost host,
        Uri target,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<ReaderNavigationCompletedEventArgs>? handler = null;
        handler = (_, args) =>
        {
            if (!UriEquals(args.Request, target)) return;
            completion.TrySetResult(args.IsSuccess);
        };
        host.NavigationCompleted += handler;
        try
        {
            host.Navigate(target, _layout, showVerticalDebugBoxes: false);
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        }
        finally
        {
            host.NavigationCompleted -= handler;
        }
    }

    private static bool UriEquals(Uri? left, Uri right) =>
        left is not null
        && string.Equals(left.AbsoluteUri, right.AbsoluteUri, StringComparison.Ordinal);

    private void OnReaderWebMessage(object? sender, ReaderWebMessageReceivedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Body)) return;
        try
        {
            using var json = JsonDocument.Parse(e.Body);
            var root = json.RootElement;
            if (!root.TryGetProperty("type", out var type)) return;
            if (type.GetString() == "scroll")
            {
                var left = root.TryGetProperty("left", out var leftElement) ? leftElement.GetDouble() : 0;
                _epubPosition = Math.Max(0, left);
                UpdateEpubLabels();
                _ = SaveProgressSoonAsync(force: false);
            }
            else if (type.GetString() == "link")
            {
                _tapConsumed = true;
                _ = HandleLinkAsync(
                    root.TryGetProperty("href", out var href) ? href.GetString() : null,
                    root.TryGetProperty("footnoteText", out var note) && note.ValueKind == JsonValueKind.String
                        ? note.GetString()
                        : null);
            }
            // The PDF surface reports a first tap on a highlight as a selection.
            else if (type.GetString() is "annotationClick" or "annotationSelect")
            {
                _tapConsumed = true;
                if (root.TryGetProperty("id", out var id) && id.TryGetGuid(out var annotationId))
                    ShowAnnotation(annotationId);
            }
            else if (type.GetString() == "selection" && _expectSelection)
            {
                _expectSelection = false;
                if (root.TryGetProperty("text", out var text) && text.GetString() is { Length: > 0 } selected)
                {
                    ShowSelection(new ReaderSelection(
                        selected,
                        root.GetProperty("startOffset").GetInt32(),
                        root.GetProperty("endOffset").GetInt32(),
                        root.GetProperty("prefix").GetString() ?? string.Empty,
                        root.GetProperty("suffix").GetString() ?? string.Empty,
                        ReadDouble(root, "x"),
                        ReadDouble(root, "y"),
                        ReadDouble(root, "bottom")));
                }
            }
            else if (type.GetString() == "pdfPage")
            {
                _pdfPage = root.TryGetProperty("page", out var pageElement)
                    ? pageElement.GetInt32()
                    : _pdfPage;
                UpdatePdfLabels();
                _ = SaveProgressSoonAsync(force: false);
            }
        }
        catch (JsonException)
        {
            // Reader bridge messages are advisory; malformed state must not
            // interrupt page rendering.
        }
    }

    private static double? ReadDouble(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Number
            ? element.GetDouble()
            : null;

    private void UpdateEpubLabels()
    {
        if (_epubDocument is null || _readerHost is not NativeReaderHost host) return;
        var pages = Math.Max(1, host.PageCount);
        var page = Math.Clamp(host.CurrentPage, 0, pages - 1);
        var chapterRatio = pages > 1 ? (double)page / (pages - 1) : 0;
        _overallProgress = Math.Clamp((_chapterIndex + chapterRatio) / _epubDocument.Chapters.Count, 0, 1);
        var title = _epubDocument.ChapterTitles.ElementAtOrDefault(_chapterIndex)
            ?? Path.GetFileNameWithoutExtension(_epubDocument.Chapters[_chapterIndex]);
        PresentPosition(title, $"{page + 1} / {pages}");
    }

    private void UpdatePdfLabels()
    {
        if (_pdfPageCount <= 0) return;
        _overallProgress = Math.Clamp((double)_pdfPage / _pdfPageCount, 0, 1);
        PresentPosition($"第 {_pdfPage} 页", $"{_pdfPage} / {_pdfPageCount}");
    }

    private void PresentPosition(string title, string position)
    {
        var percent = $"{_overallProgress * 100:0}%";
        FooterChapter.Text = title;
        FooterPosition.Text = $"{position}  ·  {percent}";
        ChapterText.Text = title;
        if (_sliderActive) return;
        PercentText.Text = percent;
        ProgressSlider.Value = _overallProgress * 100;
        BookmarkIcon.Filled = FindCurrentBookmark() is not null;
        NotifyNativeState();
    }

    private void BuildEpubToc()
    {
        _tocEntries.Clear();
        if (_epubDocument is null) return;
        var items = _epubDocument.Navigation.Count > 0
            ? _epubDocument.Navigation
            : _epubDocument.ChapterTitles.Select((title, index) =>
                new EpubReaderNavigationItem(title, _epubDocument.Chapters[index], index));
        foreach (var item in items)
        {
            var title = string.IsNullOrWhiteSpace(item.Title)
                ? _epubDocument.ChapterTitles.ElementAtOrDefault(item.ChapterIndex)
                : item.Title;
            _tocEntries.Add(new TocEntry(
                string.IsNullOrWhiteSpace(title) ? $"第 {item.ChapterIndex + 1} 章" : title,
                item.ChapterIndex,
                item.Target,
                item.Level));
        }
    }

    private void BuildPdfToc(PdfDocumentInfo? info)
    {
        _tocEntries.Clear();
        if (info?.Outline is { Count: > 0 })
        {
            foreach (var item in info.Outline)
            {
                _tocEntries.Add(new TocEntry(
                    item.Title,
                    Math.Max(0, item.PageNumber - 1),
                    null,
                    item.Level));
            }
            return;
        }

        for (var page = 1; page <= Math.Min(_pdfPageCount, 300); page++)
            _tocEntries.Add(new TocEntry($"第 {page} 页", page - 1, null, 0));
    }

    private async Task JumpToAsync(TocEntry item)
    {
        _tts?.NotifyManualNavigation();
        _shell.HideSheet();
        SetChromeVisible(false);
        try
        {
            if (_isPdf)
            {
                await GoToPdfPageAsync(item.ChapterIndex + 1);
            }
            else if (_readerHost is NativeReaderHost epub && _epubDocument is not null)
            {
                var chapter = Math.Clamp(item.ChapterIndex, 0, _epubDocument.Chapters.Count - 1);
                var targetText = string.IsNullOrWhiteSpace(item.Target)
                    ? _epubDocument.Chapters[chapter]
                    : item.Target;
                if (!Uri.TryCreate(targetText, UriKind.Absolute, out var target))
                    target = new Uri(_epubDocument.Chapters[chapter]);
                if (await NavigateToChapterAsync(epub, chapter, target))
                {
                    UpdateEpubLabels();
                    _ = SaveProgressSoonAsync(force: true);
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _shell.ShowToast(UiText.Localize(exception.Message));
        }
    }

    private async Task GoToPdfPageAsync(int page)
    {
        if (_readerHost is not NativePdfReaderHost pdf) return;
        // A whole-page view shows each page from its start, never a pan carried over.
        if (!IsPdfZoomed && pdf.Zoom > 1) await pdf.SetFitModeAsync(PdfReaderFitMode.Page);
        page = Math.Clamp(page, 1, _pdfPageCount);
        var path = _modules.Library.Books.GetAbsoluteFilePath(_bookFile!);
        if (!await pdf.NavigateAsync(new Uri(new Uri(path).AbsoluteUri + $"#page={page}"), _lifetime.Token))
            return;
        _pdfPage = pdf.PageNumber;
        UpdatePdfLabels();
        _ = SaveProgressSoonAsync(force: true);
    }

    private async Task<bool> SaveProgressSoonAsync(bool force)
    {
        using var operation = MobileDataCoordinator.TryBeginOperation();
        if (operation is null) return false;
        return await SaveProgressCoreAsync(force);
    }

    // The caller owns an operation lease for the entire save.
    private async Task<bool> SaveProgressCoreAsync(bool force)
    {
        if (!_ready || _bookFile is null || _readerHost is null) return true;
        var now = DateTimeOffset.UtcNow;
        CountReadingTime(now);
        if (!force && now - _lastProgressSave < TimeSpan.FromSeconds(1.2)) return true;
        _lastProgressSave = now;

        long seconds = 0;
        try
        {
            var chapterIndex = _isPdf ? Math.Max(0, _pdfPage - 1) : _chapterIndex;
            var chapterPath = _isPdf
                ? $"{PdfPageChapterPrefix}{_pdfPage}"
                : ChapterPathAt(chapterIndex);
            var scrollPosition = _isPdf ? 0 : (int)Math.Clamp(_epubPosition, 0, int.MaxValue);
            var contentPosition = (_readerHost as IReaderPositionHost)?.CaptureContentPosition();
            var token = force ? CancellationToken.None : _lifetime.Token;
            var percent = Math.Clamp(_overallProgress * 100, 0, 100);
            await _modules.Reader.Data.SaveProgressAsync(new ReaderProgressRow(
                _book.Id,
                _bookFile.Id,
                chapterPath,
                null,
                chapterIndex,
                scrollPosition,
                percent,
                1,
                now)
            { ContentPosition = contentPosition }, token);

            // The dashboard, library grid and continue card read reading stats.
            seconds = Interlocked.Exchange(ref _unsavedSeconds, 0);
            var chapters = _isPdf ? _pdfPageCount : _epubDocument?.Chapters.Count ?? 0;
            await _modules.Reader.Data.AddReadingTimeAsync(
                _book.Id, _bookFile.Id, seconds, percent, chapterIndex, chapters, token);
            return true;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            Interlocked.Add(ref _unsavedSeconds, seconds);
            return false;
        }
        catch
        {
            Interlocked.Add(ref _unsavedSeconds, seconds);
            // Progress is best-effort; a transient database error must not
            // close the reading surface.
            return false;
        }
    }

    /// <summary>Time since the last interaction counts, capped so an idle phone adds nothing.</summary>
    private void CountReadingTime(DateTimeOffset now)
    {
        Interlocked.Add(ref _unsavedSeconds, _activityClock.CountUntil(now));
    }

    private void ShowError(string message)
    {
        _nativeError = message;
        LoadingPanel.IsVisible = false;
        ErrorPanel.IsVisible = !_usesNativeFrontend;
        ErrorText.Text = message;
        SetChromeVisible(false);
    }

    public void SetChromeVisible(bool visible)
    {
        if (_usesNativeFrontend)
        {
            _nativeChromeVisible = visible;
            NotifyNativeState();
            return;
        }
        TopChrome.Classes.Set("visible", visible);
        BottomChrome.Classes.Set("visible", visible);
        ProgressSliderGroup.SetHostVisible(visible);
        // The reading-aloud bar stays reachable above the toolbar.
        var speechBottom = visible ? BottomChrome.Bounds.Height + 24 : 40;
        SpeechBar.Margin = new Thickness(12, 0, 12, speechBottom);
        SpeechDock.Margin = new Thickness(0, 0, 0, speechBottom);
    }

    private void OnSurfacePressed(object? sender, PointerPressedEventArgs e)
    {
        TrackTouchPressed(e);
        // The native frontend draws its selection toolbar in SwiftUI, so the
        // Avalonia layer stays hidden while a selection is active.
        if (SelectionLayer.IsVisible || _usesNativeFrontend && _selection is not null)
        {
            // A tap outside the floating toolbar dismisses the selection
            // instead of turning the page or toggling the chrome.
            CancelPress();
            ClearSelection();
            e.Handled = true;
            return;
        }
        if (_touches.Count > 1)
        {
            // A second finger starts a pinch, never a tap or selection.
            CancelPress();
            e.Handled = true;
            return;
        }
        _pressPoint = e.GetPosition(ReaderHostSlot);
        _pressMoved = false;
        _selectionArmed = false;
        _tapConsumed = false;
        if (!IsPdfZoomed) _longPress.Start();
    }

    private void CancelPress()
    {
        _longPress.Stop();
        _pressPoint = null;
        _selectionArmed = false;
        _tapConsumed = true;
    }

    /// <summary>A long press hands the following drag to the host as a text selection.</summary>
    private void OnLongPress(object? sender, EventArgs e)
    {
        _longPress.Stop();
        if (_pressPoint is null || _pressMoved) return;
        _selectionArmed = true;
        _shell.ShowToast("拖动选择文字");
    }

    /// <summary>Stops the host from treating a finger drag as a text selection.</summary>
    private void OnSurfaceMoved(object? sender, PointerEventArgs e)
    {
        if (TrackTouchMoved(e))
        {
            e.Handled = true;
            return;
        }
        if (_pressPoint is not { } start || _selectionArmed || IsPdfZoomed) return;
        e.Handled = true;
        var current = e.GetPosition(ReaderHostSlot);
        if (Math.Abs(current.X - start.X) > TapSlop || Math.Abs(current.Y - start.Y) > TapSlop)
        {
            _pressMoved = true;
            _longPress.Stop();
        }
    }

    private void OnSurfaceReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (TrackTouchReleased(e))
        {
            CancelPress();
            e.Handled = true;
            return;
        }
        _longPress.Stop();
        if (_pressPoint is not { } start) return;
        _pressPoint = null;
        if (_selectionArmed)
        {
            _selectionArmed = false;
            _expectSelection = true;
            return;
        }

        var end = e.GetPosition(ReaderHostSlot);
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var dominant = Math.Abs(dx) >= Math.Abs(dy) * SwipeDominance;
        if (Math.Abs(dx) >= SwipeDistance && dominant)
        {
            if (!IsPdfZoomed) HandleSwipe(dx);
        }
        else if (Math.Abs(dx) <= TapSlop && Math.Abs(dy) <= TapSlop)
        {
            // The host reports link and annotation hits after this tunnel handler.
            var ratio = end.X / Math.Max(1, ReaderHostSlot.Bounds.Width);
            Dispatcher.UIThread.Post(() =>
            {
                if (!_tapConsumed) HandleTap(ratio);
            }, DispatcherPriority.Background);
        }
    }

    private void HandleSwipe(double dx)
    {
        if (IsChromeVisible) SetChromeVisible(false);
        else _ = TurnAsync(dx < 0 ? 1 : -1);
    }

    private void HandleTap(double ratio)
    {
        if (IsChromeVisible) SetChromeVisible(false);
        else if (ratio < 1d / 3) _ = TurnAsync(-1);
        else if (ratio > 2d / 3) _ = TurnAsync(1);
        else SetChromeVisible(true);
    }

    /// <summary>Turns one page, crossing into the neighboring chapter at its edge.</summary>
    public async Task TurnAsync(int direction)
    {
        HideSelectionBar();
        if (_turning) return;
        _tts?.NotifyManualNavigation();
        _turning = true;
        try
        {
            if (_isPdf && (_pdfPage + direction < 1 || _pdfPage + direction > _pdfPageCount))
            {
                ShowEdgeNotice(direction);
                return;
            }
            if (!_isPdf && _readerHost is NativeReaderHost current
                && !current.CanTurn(direction)
                && (_epubDocument is null || _chapterIndex + direction < 0
                    || _chapterIndex + direction >= _epubDocument.Chapters.Count))
            {
                ShowEdgeNotice(direction);
                return;
            }

            var surface = new ReaderTransitionSurface(
                ReaderHostSlot, TransitionSnapshot, TransitionTrail, TransitionFront, TransitionEdge, ReaderPaper);
            var animation = (int)_shell.Preferences.Current.ReaderPageAnimation;
            await ReaderTransitionPlayer.RunAsync(surface, animation, direction, async () =>
            {
                if (_isPdf)
                {
                    await GoToPdfPageAsync(_pdfPage + direction);
                    return true;
                }

                if (_readerHost is not NativeReaderHost host) return false;
                if (!host.TurnPage(direction))
                    await GoToChapterAsync(host, _chapterIndex + direction, fromEnd: direction < 0, direction);
                return true;
            }, _lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _shell.ShowToast(UiText.Localize(exception.Message));
        }
        finally
        {
            _turning = false;
        }
    }

    private async Task GoToChapterAsync(NativeReaderHost host, int chapter, bool fromEnd, int direction)
    {
        HideSelectionBar();
        if (_epubDocument is null) return;
        if (chapter < 0 || chapter >= _epubDocument.Chapters.Count)
        {
            ShowEdgeNotice(direction);
            return;
        }

        if (!await NavigateToChapterAsync(host, chapter, new Uri(_epubDocument.Chapters[chapter]))) return;
        if (fromEnd) host.SeekToBoundary(toEnd: true);
        UpdateEpubLabels();
        _ = SaveProgressSoonAsync(force: true);
    }

    private void ShowEdgeNotice(int direction) =>
        _shell.ShowToast(direction < 0 ? "已经是第一页" : "已经是最后一页");

    private async Task MoveChapterAsync(int direction)
    {
        _tts?.NotifyManualNavigation();
        try
        {
            if (_isPdf) await GoToPdfPageAsync(_pdfPage + direction);
            else if (_readerHost is NativeReaderHost host)
                await GoToChapterAsync(host, _chapterIndex + direction, fromEnd: false, direction);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _shell.ShowToast(UiText.Localize(exception.Message));
        }
    }

    private async Task HandleLinkAsync(string? href, string? footnoteText)
    {
        if (string.IsNullOrWhiteSpace(href)) return;
        if (!string.IsNullOrWhiteSpace(footnoteText))
        {
            if (_usesNativeFrontend)
            {
                NativeDialogRequested?.Invoke(new NativeReaderDialog("info", "注释", footnoteText));
                return;
            }
            _shell.ShowSheet("注释", new InfoSheet(footnoteText), ThemeScope.RequestedThemeVariant);
            return;
        }

        try
        {
            if (Uri.TryCreate(href, UriKind.Absolute, out var external) && external.Scheme is "http" or "https")
            {
                if (TopLevel.GetTopLevel(this)?.Launcher is { } launcher) await launcher.LaunchUriAsync(external);
                return;
            }

            if (_readerHost is not NativeReaderHost host || _epubDocument is not { } document) return;
            _tts?.NotifyManualNavigation();
            var target = new Uri(new Uri(document.Chapters[_chapterIndex]), href);
            var path = Path.GetFullPath(target.LocalPath);
            var chapter = document.Chapters.ToList().FindIndex(item =>
                string.Equals(Path.GetFullPath(item), path, StringComparison.Ordinal));
            if (chapter < 0) return;
            if (await NavigateToChapterAsync(host, chapter, target))
            {
                UpdateEpubLabels();
                _ = SaveProgressSoonAsync(force: true);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is UriFormatException or InvalidOperationException or TimeoutException)
        {
            _shell.ShowToast(UiText.Localize(exception.Message));
        }
    }

    private void ClearSelection()
    {
        HideSelectionBar();
        (_readerHost as IReaderSelectionHost)?.ClearSelection();
    }

    /// <summary>
    /// Shows the icon toolbar beside the selection instead of a sheet, so the
    /// selected text stays visible while choosing an action.
    /// </summary>
    private void ShowSelection(ReaderSelection selection)
    {
        var chapterPath = CurrentChapterPath;
        var overlaps = _annotations.Any(item => item.ChapterPath == chapterPath
            && selection.Start < item.EndOffset && selection.End > item.StartOffset);
        _selection = selection;
        if (_usesNativeFrontend)
        {
            NotifyNativeState();
            return;
        }
        SelectionHighlightButton.IsVisible = !overlaps;
        SelectionNoteButton.IsVisible = !overlaps;
        SelectionLayer.IsVisible = true;
        PositionSelectionBar(selection);
    }

    private void HideSelectionBar()
    {
        _selection = null;
        SelectionLayer.IsVisible = false;
        NotifyNativeState();
    }

    private void PositionSelectionBar(ReaderSelection selection)
    {
        SelectionBar.Measure(Size.Infinity);
        var size = SelectionBar.DesiredSize;
        var width = ReaderPaper.Bounds.Width;
        var height = ReaderPaper.Bounds.Height;
        if (width <= 0 || height <= 0) return;

        // Anchor to the selection's left edge and prefer the space above it;
        // fall below when the first line is too close to the top edge.
        const double inset = 8;
        const double gap = 10;
        var left = selection.X ?? (width - size.Width) / 2;
        left = Math.Clamp(left, inset, Math.Max(inset, width - inset - size.Width));
        var top = (selection.Y ?? height / 2) - size.Height - gap;
        if (top < inset)
            top = (selection.Bottom ?? selection.Y ?? height / 2) + gap;
        top = Math.Clamp(top, inset, Math.Max(inset, height - inset - size.Height));
        Canvas.SetLeft(SelectionBar, left);
        Canvas.SetTop(SelectionBar, top);
    }

    private void OnSelectionHighlightClick(object? sender, RoutedEventArgs e)
    {
        if (_selection is not { } selection) return;
        HideSelectionBar();
        _ = SaveAnnotationAsync(NewAnnotation(selection));
    }

    private void OnSelectionNoteClick(object? sender, RoutedEventArgs e)
    {
        if (_selection is not { } selection) return;
        HideSelectionBar();
        PromptNote(NewAnnotation(selection));
    }

    private void OnSelectionTranslateClick(object? sender, RoutedEventArgs e)
    {
        if (_selection is not { } selection) return;
        ClearSelection();
        _ = TranslateAsync(selection.Text);
    }

    private void OnSelectionAskClick(object? sender, RoutedEventArgs e)
    {
        if (_selection is not { } selection) return;
        ClearSelection();
        ShowAssistant(selection.Text);
    }

    private void OnSelectionCopyClick(object? sender, RoutedEventArgs e)
    {
        if (_selection is not { } selection) return;
        _ = CopyAsync(selection.Text);
    }

    private ReaderAnnotation NewAnnotation(ReaderSelection selection) => new()
    {
        BookId = _book.Id,
        BookFileId = _bookFile!.Id,
        ChapterPath = CurrentChapterPath,
        Fragment = _isPdf ? (_readerHost as IPdfTextHost)?.SelectionFragment : null,
        StartOffset = selection.Start,
        EndOffset = selection.End,
        SelectedText = selection.Text,
        Prefix = selection.Prefix,
        Suffix = selection.Suffix
    };

    private void PromptNote(ReaderAnnotation annotation)
    {
        _shell.ShowSheet("笔记", new PromptSheet(annotation.SelectedText, "保存", async note =>
        {
            annotation.Note = note;
            _shell.HideSheet();
            await SaveAnnotationAsync(annotation);
        }, initialText: annotation.Note, placeholder: "写下你的想法"), ThemeScope.RequestedThemeVariant);
    }

    private async Task SaveAnnotationAsync(ReaderAnnotation annotation)
    {
        using var operation = MobileDataCoordinator.TryBeginOperation();
        if (operation is null) return;
        try
        {
            annotation.UpdatedAt = DateTimeOffset.UtcNow;
            await _modules.Reader.Data.SaveAnnotationAsync(annotation, _lifetime.Token);
            _annotations.RemoveAll(item => item.Id == annotation.Id);
            _annotations.Add(annotation);
            (_readerHost as IReaderAnnotationHost)?.SetAnnotations(_annotations.ToList());
            ClearSelection();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _shell.ShowToast("保存失败：" + UiText.Localize(exception.Message));
        }
    }

    private void ShowAnnotation(Guid id)
    {
        if (_annotations.Find(item => item.Id == id) is not { } annotation) return;
        if (_usesNativeFrontend)
        {
            NativeDialogRequested?.Invoke(new NativeReaderDialog("annotation", "批注", annotation.DisplayQuote,
                annotation.Id, annotation.Note));
            return;
        }
        var title = annotation.DisplayQuote.Length > 24
            ? annotation.DisplayQuote[..24] + "…"
            : annotation.DisplayQuote;
        _shell.ShowSheet(title, new ChoiceSheet(
            [
                new ChoiceItem(annotation.Note.Length > 0 ? "编辑笔记" : "写笔记", Tag: true),
                new ChoiceItem("删除", Tag: false)
            ],
            choice =>
            {
                if (choice.Tag is true)
                {
                    PromptNote(annotation);
                    return;
                }
                _shell.HideSheet();
                _ = DeleteAnnotationAsync(annotation);
            }), ThemeScope.RequestedThemeVariant);
    }

    private async Task DeleteAnnotationAsync(ReaderAnnotation annotation)
    {
        using var operation = MobileDataCoordinator.TryBeginOperation();
        if (operation is null) return;
        try
        {
            await _modules.Reader.Data.DeleteAnnotationAsync(annotation.Id, _lifetime.Token);
            _annotations.Remove(annotation);
            (_readerHost as IReaderAnnotationHost)?.SetAnnotations(_annotations.ToList());
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _shell.ShowToast("删除失败：" + UiText.Localize(exception.Message));
        }
    }

    private async Task CopyAsync(string text)
    {
        ClearSelection();
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        await clipboard.SetTextAsync(text);
        _shell.ShowToast("已复制");
    }

    private void OnSliderPressed(object? sender, PointerPressedEventArgs e) => _sliderActive = true;

    private void OnSliderValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_sliderActive) PercentText.Text = $"{e.NewValue:0}%";
    }

    private void OnSliderReleased(object? sender, PointerReleasedEventArgs e)
    {
        // The slider applies the final thumb position after this tunnel handler.
        Dispatcher.UIThread.Post(() =>
        {
            _sliderActive = false;
            _ = SeekAsync(ProgressSlider.Value);
        });
    }

    private async Task SeekAsync(double percent)
    {
        _tts?.NotifyManualNavigation();
        try
        {
            if (_isPdf)
            {
                await GoToPdfPageAsync((int)Math.Round(percent / 100 * _pdfPageCount));
                return;
            }

            if (_readerHost is not NativeReaderHost host || _epubDocument is null) return;
            var position = percent / 100 * _epubDocument.Chapters.Count;
            var chapter = Math.Min((int)position, _epubDocument.Chapters.Count - 1);
            if (chapter != _chapterIndex)
                await GoToChapterAsync(host, chapter, fromEnd: false, chapter - _chapterIndex);
            host.SeekToRatio(Math.Clamp(position - chapter, 0, 1));
            UpdateEpubLabels();
            _ = SaveProgressSoonAsync(force: true);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _shell.ShowToast(UiText.Localize(exception.Message));
        }
    }

    private void OnBackClick(object? sender, RoutedEventArgs e) => _shell.GoBack();

    private void OnTocClick(object? sender, RoutedEventArgs e) => ShowToc();

    public void ShowToc()
    {
        HideSelectionBar();
        var selected = CurrentTocIndex();
        var items = _tocEntries
            .Select((entry, index) => new ChoiceItem(entry.Title, null, index == selected, entry.Level, entry))
            .ToList();
        _shell.ShowSheet("目录", new ChoiceSheet(items, choice =>
        {
            if (choice.Tag is TocEntry entry) _ = JumpToAsync(entry);
        }), ThemeScope.RequestedThemeVariant);
    }

    /// <summary>
    /// The entry for the current chapter or PDF page. Pages without their own
    /// entry, such as illustrations, belong to the nearest preceding entry.
    /// </summary>
    private int CurrentTocIndex()
    {
        var current = _isPdf ? _pdfPage - 1 : _chapterIndex;
        var selected = _tocEntries.FindIndex(entry => entry.ChapterIndex == current);
        return selected >= 0 ? selected : _tocEntries.FindLastIndex(entry => entry.ChapterIndex < current);
    }

    private async void OnPreviousClick(object? sender, RoutedEventArgs e) => await MoveChapterAsync(-1);

    private async void OnNextClick(object? sender, RoutedEventArgs e) => await MoveChapterAsync(1);

    private void OnTypographyClick(object? sender, RoutedEventArgs e) => ShowTypography();

    public void ShowTypography()
    {
        HideSelectionBar();
        _shell.ShowSheet(_isPdf ? "显示" : "排版", new ReaderSettingsSheet(_shell.Preferences, !_isPdf, OnReaderSettingsChanged),
            ThemeScope.RequestedThemeVariant);
    }

    private ReaderTheme CurrentTheme => _shell.Preferences.Current.ReaderTheme;

    private double PdfZoom => _shell.Preferences.Current.PdfZoom;

    private bool IsPdfZoomed => _isPdf && PdfZoom > 1;

    /// <summary>A zoomed page outgrows the screen, so drags pan it instead of turning.</summary>
    private async Task ApplyPdfZoomAsync(NativePdfReaderHost host)
    {
        host.SetHandToolMode(PdfZoom > 1);
        // Fitting resets zoom and re-centres; zooming back to 1 with an anchor
        // would leave the page panned off to one side.
        if (PdfZoom > 1) await host.SetZoomAsync(PdfZoom);
        else await host.SetFitModeAsync(PdfReaderFitMode.Page);
    }

    private void ApplyTheme()
    {
        var dark = CurrentTheme == ReaderTheme.Ink;
        var paper = ReaderPalette.For(CurrentTheme).Page;
        ThemeScope.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        Surface.Background = new SolidColorBrush(paper);
        _shell.SetPaperSystemBars(paper, dark);
    }

    private async void OnReaderSettingsChanged() => await ApplyReaderSettingsAsync();

    private async Task ApplyReaderSettingsAsync()
    {
        ApplyTheme();
        (_readerHost as IReaderAppearanceHost)?.SetAppearance(new ReaderAppearanceSettings { Theme = CurrentTheme });
        // Preserve the sheet's scroll position while the page updates live.
        _shell.SetSheetTheme(ThemeScope.RequestedThemeVariant);
        if (_readerHost is NativePdfReaderHost pdf && pdf.Zoom != PdfZoom) await ApplyPdfZoomAsync(pdf);

        var layout = BuildLayout(_shell.Preferences.Current);
        if (layout == _layout) return;
        _tts?.NotifyManualNavigation();
        _layout = layout;
        if (_readerHost is not IReaderConfiguredHost configured) return;
        try
        {
            // Reflow keeps the reader on the same sentence, not the same offset.
            await configured.Configure(
                _layout,
                _epubPosition,
                fragment: null,
                restoreFromProgress: true,
                showVerticalDebugBoxes: false,
                contentPosition: configured.CaptureContentPosition());
            UpdateEpubLabels();
        }
        catch (Exception exception)
        {
            _shell.ShowToast(UiText.Localize(exception.Message));
        }
    }

    private ReaderBookmark? FindCurrentBookmark()
    {
        var chapter = _isPdf ? _pdfPage - 1 : _chapterIndex;
        var position = _isPdf ? 0 : (int)Math.Clamp(_epubPosition, 0, int.MaxValue);
        return _bookmarks.Find(mark => mark.ChapterIndex == chapter && mark.ScrollPosition == position);
    }

    private async void OnBookmarkClick(object? sender, RoutedEventArgs e) => await ToggleBookmarkAsync();

    private bool _bookmarkBusy;

    public async Task ToggleBookmarkAsync()
    {
        // A second tap while the first save runs would add the same mark twice.
        if (_bookmarkBusy || !_ready) return;
        using var operation = MobileDataCoordinator.TryBeginOperation();
        if (operation is null || _bookFile is null) return;
        _bookmarkBusy = true;
        try
        {
            if (FindCurrentBookmark() is { } existing)
            {
                await _modules.Reader.Data.DeleteBookmarkAsync(existing.Id, _lifetime.Token);
                _bookmarks.Remove(existing);
                BookmarkIcon.Filled = false;
                NotifyNativeState();
                _shell.ShowToast("已移除书签");
                return;
            }

            var title = _isPdf
                ? $"第 {_pdfPage} 页"
                : _epubDocument?.ChapterTitles.ElementAtOrDefault(_chapterIndex) ?? "当前章节";
            var bookmark = new ReaderBookmark
            {
                BookId = _book.Id,
                BookFileId = _bookFile.Id,
                ChapterPath = _isPdf ? $"{PdfPageChapterPrefix}{_pdfPage}" : ChapterPathAt(_chapterIndex),
                ChapterIndex = _isPdf ? _pdfPage - 1 : _chapterIndex,
                ScrollPosition = _isPdf ? 0 : (int)Math.Clamp(_epubPosition, 0, int.MaxValue),
                FlowMode = 1,
                Title = title,
                Quote = CurrentPageQuote() ?? $"进度 {_overallProgress * 100:0}%"
            };
            await _modules.Reader.Data.SaveBookmarkAsync(bookmark, _lifetime.Token);
            _bookmarks.Add(bookmark);
            BookmarkIcon.Filled = true;
            NotifyNativeState();
            _shell.ShowToast("已添加书签");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _shell.ShowToast(UiText.Localize(exception.Message));
        }        finally
        {
            _bookmarkBusy = false;
        }
    }

    private const int QuoteLength = 72;

    /// <summary>The opening words of the visible page, used to recognize a bookmark.</summary>
    private string? CurrentPageQuote()
    {
        if (_readerHost is IReaderPositionHost host) return host.GetCurrentPageQuote(QuoteLength);
        if (_readerHost is not NativePdfReaderHost pdf) return null;
        var text = pdf.GetPageContent(pdf.PageNumber)?.Text;
        if (string.IsNullOrWhiteSpace(text)) return null;
        var line = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return line.Length > QuoteLength ? line[..QuoteLength] + "…" : line;
    }

    private static BookFile? ChooseReadableFile(Book book)
    {
        foreach (var format in new[] { "epub", "pdf", "azw3", "mobi" })
        {
            var file = book.Files.FirstOrDefault(candidate =>
                candidate.Format.Trim().TrimStart('.').Equals(format, StringComparison.OrdinalIgnoreCase));
            if (file is not null) return file;
        }
        return book.Files.FirstOrDefault();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (!_disposed && _started)
        {
            _disposed = true;
            _longPress.Stop();
            _tts?.Dispose();
            _shell.Services.SystemBars?.SetEdgeGesturesCaptured(false);
            _shell.ClearPaperSystemBars();
            _ = SaveAndSyncOnExitAsync();
            _lifetime.Cancel();
            if (_readerHost is not null)
            {
                _readerHost.WebMessageReceived -= OnReaderWebMessage;
                if (_readerHost is NativeReaderHost epub) epub.SetAnnotations([]);
                _readerHost.Dispose();
                _readerHost = null;
            }
            ReaderHostSlot.Content = null;
            _lifetime.Dispose();
        }
        base.OnDetachedFromVisualTree(e);
    }

    private async Task SaveAndSyncOnExitAsync()
    {
        using var operation = MobileDataCoordinator.TryBeginOperation();
        if (operation is null) return;
        if (await SaveProgressCoreAsync(force: true))
            _shell.RequestAutoSync(MobileShell.AutoSyncGap.AfterReading);
    }

    public Task PauseAsync()
    {
        Interlocked.Add(ref _unsavedSeconds, _activityClock.Pause(DateTimeOffset.UtcNow));
        return SaveProgressSoonAsync(force: true);
    }

    public void Resume()
    {
        ResetTouches();
        _activityClock.Resume(DateTimeOffset.UtcNow);
    }

    private sealed record ReaderSelection(
        string Text,
        int Start,
        int End,
        string Prefix,
        string Suffix,
        double? X,
        double? Y,
        double? Bottom);

    private sealed record TocEntry(string Title, int ChapterIndex, string? Target, int Level);
}
