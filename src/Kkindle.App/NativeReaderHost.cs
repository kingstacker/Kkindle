using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Kkindle.Core;
using Kkindle.Layout;
using SkiaSharp;

namespace Kkindle;

/// <summary>
/// The self-drawn EPUB reading surface. The chapter is loaded, shaped, broken,
/// paginated and painted by Kkindle.Layout (HarfBuzz + Skia); this control only
/// blits the finished page into an Avalonia <see cref="WriteableBitmap"/> and
/// translates input into the same JSON bridge protocol the WebKit reader used,
/// so the surrounding reader pipeline keeps working unchanged.
/// </summary>
public sealed class NativeReaderHost : Control, IReaderHost, IReaderPageSnapshotProvider
{
    public const string BundledFontFileName = "KingHwaOldSong-v3.0.ttf";

    private static readonly object ImageCacheLock = new();
    private static readonly Dictionary<string, SKImage> ImageCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Queue<string> ImageCacheOrder = new();

    private readonly TaskCompletionSource _readyTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private TypesetEngine? _engine;
    private readonly object _engineGate = new();
    private ChapterContent? _content;
    private ChapterLayout? _layout;
    private TypesetLayoutOptions? _options;
    private ReaderLayoutSettings _settings = new();
    private CancellationTokenSource? _navigationCts;
    private long _navigationVersion;
    private bool _disposed;

    private int _pageIndex;
    private bool _composePending;
    private bool _showVerticalDebugBoxes;
    private bool _bitmapDirty;
    private WriteableBitmap? _bitmap;
    private int _bitmapPixelWidth;
    private int _bitmapPixelHeight;
    private DispatcherTimer? _relayoutTimer;
    private DispatcherTimer? _selectionAutoPageTurnTimer;
    private bool _selectionAutoPageTurnArmed;
    private int _selectionAutoPageTurnDirection;

    private static readonly TimeSpan SelectionAutoPageTurnDelay = TimeSpan.FromSeconds(1);

    private readonly Dictionary<string, int> _fragmentPagesByRequest = new(StringComparer.Ordinal);
    private int? _pendingRestoreOffset;
    private int? _pendingRestorePage;
    private string? _pendingFragment;
    private bool _pendingSeekToEnd;

    private readonly List<ReaderAnnotation> _annotations = new();
    private List<(int Start, int Length)>? _searchHits;
    private int? _focusSearchHit;

    // Selection state in body-text offsets.
    private bool _selecting;
    private int _selectionAnchor = -1;
    private int _selectionActiveOffset = -1;
    private int _selectionStart = -1;
    private int _selectionEnd = -1;
    private bool _pointerDown;
    private bool _pointerDragStarted;
    private Point _pointerDownPosition;
    private Point _lastPointerPosition;
    private bool _hasLastPointerPosition;
    private int _pointerDownOffset = -1;
    private PlacedHotZone? _pointerHotZone;
    private string? _hoveredFootnoteHref;

    public NativeReaderHost()
    {
        Focusable = true;
        ClipToBounds = true;
        ReadyTask = _readyTcs.Task;
        _readyTcs.TrySetResult();
        PropertyChanged += (_, args) =>
        {
            if (args.Property == BoundsProperty)
            {
                ScheduleRelayout();
            }
        };
        PointerCaptureLost += (_, _) =>
        {
            if (!_pointerDown)
            {
                return;
            }

            _pointerDown = false;
            _pointerDragStarted = false;
            _selecting = false;
            _hasLastPointerPosition = false;
            StopSelectionAutoPageTurn();
        };
    }

    // ---- IReaderHost -----------------------------------------------------

    public object View => this;

    public Uri? Source { get; private set; }

    public Task ReadyTask { get; }

    public event EventHandler<ReaderNavigationStartingEventArgs>? NavigationStarting;

    public event EventHandler<ReaderNavigationCompletedEventArgs>? NavigationCompleted;

    public event EventHandler<ReaderWebMessageReceivedEventArgs>? WebMessageReceived;

    public Task<string?> InvokeScriptAsync(string script)
    {
        // The native surface has no script engine. Leftover calls from shared
        // pipelines (reveal scripts and similar no-ops) are swallowed here;
        // everything meaningful reaches the host through its native methods.
        return Task.FromResult<string?>(null);
    }

    public void Stop()
    {
        _navigationCts?.Cancel();
    }

    public void Dispose()
    {
        _disposed = true;
        _navigationCts?.Cancel();
        _navigationCts?.Dispose();
        _navigationCts = null;
        _relayoutTimer?.Stop();
        StopSelectionAutoPageTurn();
        lock (_engineGate)
        {
            _engine?.Dispose();
            _engine = null;
        }
        _layout = null;
        _content = null;
        _bitmap?.Dispose();
        _bitmap = null;
    }

    /// <summary>Loads and lays out a chapter. The URI fragment selects the anchor.</summary>
    public void Navigate(Uri uri)
    {
        if (_disposed)
        {
            return;
        }

        Source = uri;
        var path = uri.IsFile ? uri.LocalPath : uri.AbsolutePath;
        string? fragment = null;
        if (!string.IsNullOrWhiteSpace(uri.Fragment))
        {
            try
            {
                fragment = Uri.UnescapeDataString(uri.Fragment.TrimStart('#'));
            }
            catch (UriFormatException)
            {
                fragment = uri.Fragment.TrimStart('#');
            }
        }
        NavigateCore(path, fragment);
    }

    // ---- native surface API used by the MainWindow seams ------------------

    public bool IsNative => true;

    /// <summary>The native EPUB surface is always page based.</summary>
    public bool IsPaginated => true;

    public int PageCount => _layout?.Pages.Count ?? 0;

    public int CurrentPage => _pageIndex;

    public bool Vertical => _settings.VerticalWriting;

    /// <summary>
    /// Applies reader settings, restores a position and repaints. Positions
    /// follow the persisted convention: pixel scroll for horizontal paged
    /// mode, page-start character offset for vertical writing.
    /// </summary>
    public Task Configure(
        ReaderLayoutSettings settings,
        double scrollPosition,
        string? fragment,
        bool restoreFromProgress,
        bool showVerticalDebugBoxes)
    {
        var settingsChanged = SettingsChanged(settings);
        _settings = settings;
        _showVerticalDebugBoxes = showVerticalDebugBoxes;

        if (_content is not null && (settingsChanged || _layout is null || _composePending))
        {
            Recompose();
        }

        // A saved progress position is authoritative when it exists. The
        // fragment is still useful for fresh link navigation, but letting it
        // win here would reopen a chapter at the link's page instead of the
        // user's last reading position.
        var hasSavedPosition = restoreFromProgress && scrollPosition > 0.5;
        _pendingFragment = hasSavedPosition || string.IsNullOrWhiteSpace(fragment)
            ? null
            : NormalizeFragment(fragment);
        if (restoreFromProgress)
        {
            _pendingRestoreOffset = null;
            _pendingRestorePage = null;
            _pendingSeekToEnd = false;
            if (Vertical)
            {
                _pendingRestoreOffset = (int)Math.Round(Math.Max(0, scrollPosition));
            }
            else
            {
                _pendingRestorePage = PageFromPixelScroll(scrollPosition);
            }
        }
        else
        {
            _pendingRestoreOffset = null;
            _pendingRestorePage = null;
            _pendingSeekToEnd = false;
        }

        ApplyPendingPosition();
        _bitmapDirty = true;
        InvalidateVisual();
        EmitScroll();
        return Task.CompletedTask;
    }

    /// <summary>Chapter-boundary positioning: the first page or the last full page.</summary>
    public void SeekToBoundary(bool toEnd)
    {
        if (_layout is null || _layout.Pages.Count == 0)
        {
            _pendingSeekToEnd = toEnd;
            return;
        }

        _pageIndex = toEnd ? Math.Max(0, _layout.Pages.Count - 1) : 0;
        _bitmapDirty = true;
        InvalidateVisual();
        EmitScroll();
    }

    public void ScrollToOffset(int offset)
    {
        if (_layout is null)
        {
            _pendingRestoreOffset = offset;
            return;
        }

        var page = _layout.GetPageIndexOfOffset(offset);
        if (page >= 0 && page != _pageIndex)
        {
            _pageIndex = page;
            _bitmapDirty = true;
            InvalidateVisual();
            EmitScroll();
        }
    }

    public void ScrollToFragment(string fragmentId)
    {
        fragmentId = NormalizeFragment(fragmentId);
        if (_layout is null)
        {
            _pendingFragment = fragmentId;
            return;
        }

        var page = _layout.GetPageIndexOfFragment(fragmentId);
        if (page >= 0 && page != _pageIndex)
        {
            _pageIndex = page;
            _bitmapDirty = true;
            InvalidateVisual();
            EmitScroll();
        }
    }

    /// <summary>
    /// Seeks by a persisted horizontal pixel scroll (page stride × index).
    /// In vertical writing the persisted value is a character offset instead.
    /// </summary>
    public void SeekToPixelScroll(double pixelScroll)
    {
        if (Vertical)
        {
            ScrollToOffset((int)Math.Max(0, pixelScroll));
            return;
        }

        _pendingRestorePage = PageFromPixelScroll(pixelScroll);
        ApplyPendingPosition();
        _bitmapDirty = true;
        InvalidateVisual();
        EmitScroll();
    }

    public void SeekToRatio(double ratio)
    {
        if (_layout is null || _layout.Pages.Count == 0)
        {
            return;
        }

        var page = (int)Math.Clamp(Math.Round(ratio * (_layout.Pages.Count - 1)), 0, _layout.Pages.Count - 1);
        if (page != _pageIndex)
        {
            _pageIndex = page;
            _bitmapDirty = true;
            InvalidateVisual();
            EmitScroll();
        }
    }

    /// <summary>True when the page turn stays inside the chapter.</summary>
    public bool CanTurn(int direction)
    {
        if (_layout is null || _layout.Pages.Count == 0)
        {
            return false;
        }

        var target = _pageIndex + direction;
        return target >= 0 && target < _layout.Pages.Count;
    }

    /// <summary>Performs an in-chapter page turn. Returns false at a chapter edge.</summary>
    public bool TurnPage(int direction)
    {
        if (!CanTurn(direction))
        {
            return false;
        }

        _pageIndex += direction;
        _bitmapDirty = true;
        InvalidateVisual();
        EmitScroll();
        return true;
    }

    public void SetAnnotations(IReadOnlyList<ReaderAnnotation> annotations)
    {
        _annotations.Clear();
        _annotations.AddRange(annotations);
        _bitmapDirty = true;
        InvalidateVisual();
    }

    public void SetSearchHighlights(List<(int Start, int Length)>? hits, int? focusIndex)
    {
        _searchHits = hits;
        _focusSearchHit = focusIndex;
        if (focusIndex is { } index && hits is { Count: > 0 } && index >= 0 && index < hits.Count)
        {
            ScrollToOffset(hits[index].Start);
        }

        _bitmapDirty = true;
        InvalidateVisual();
    }

    public void ScrollToSearchHit(int index)
    {
        if (_searchHits is { Count: > 0 } && index >= 0 && index < _searchHits.Count)
        {
            ScrollToOffset(_searchHits[index].Start);
        }
    }

    public (double Position, double Ratio, double ScrollWidth, double ScrollHeight, double ClientWidth, double ClientHeight) GetScrollState()
    {
        var viewportWidth = Math.Max(1, Bounds.Width);
        var viewportHeight = Math.Max(1, Bounds.Height);
        if (Vertical)
        {
            var start = _layout is not null
                && _pageIndex >= 0
                && _pageIndex < _layout.Pages.Count
                && _layout.Pages[_pageIndex].TextStartOffset >= 0
                ? _layout.Pages[_pageIndex].TextStartOffset
                : 0;
            var total = Math.Max(1, _layout?.BodyTextLength ?? 1);
            return (start, Math.Clamp((double)start / total, 0, 1), total, viewportHeight, viewportWidth, viewportHeight);
        }

        var stride = viewportWidth;
        var scrollWidth = stride * Math.Max(1, PageCount);
        var left = _pageIndex * stride;
        var maxLeft = Math.Max(0, scrollWidth - stride);
        return (left, maxLeft > 0 ? Math.Clamp(left / maxLeft, 0, 1) : 0, scrollWidth, viewportHeight, stride, viewportHeight);
    }

    private double PixelScrollPosition
    {
        get
        {
            if (Vertical)
            {
                return GetScrollState().Position;
            }

            return CurrentPage * Math.Max(1, Bounds.Width);
        }
    }

    private int PageFromPixelScroll(double scrollPosition)
    {
        var stride = Math.Max(1, Bounds.Width);
        return (int)Math.Clamp(Math.Round(scrollPosition / stride), 0, Math.Max(0, PageCount - 1));
    }

    public string? BodyText => _content?.BodyText;

    /// <summary>Returns a short quote from the currently visible native page.</summary>
    public string? GetCurrentPageQuote(int maxLength = 72)
    {
        if (_content is null
            || _layout is null
            || _pageIndex < 0
            || _pageIndex >= _layout.Pages.Count
            || maxLength <= 0)
        {
            return null;
        }

        var page = _layout.Pages[_pageIndex];
        if (page.TextStartOffset < 0 || page.TextEndOffset <= page.TextStartOffset)
        {
            return null;
        }

        var start = Math.Clamp(page.TextStartOffset, 0, _content.BodyText.Length);
        var end = Math.Clamp(page.TextEndOffset, start, _content.BodyText.Length);
        var normalized = string.Join(
            " ",
            _content.BodyText[start..end].Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length <= maxLength)
        {
            return normalized;
        }

        var length = maxLength;
        if (length > 0 && length < normalized.Length && char.IsHighSurrogate(normalized[length - 1]))
        {
            length--;
        }

        return normalized[..Math.Max(1, length)];
    }

    // ---- chapter pipeline -------------------------------------------------

    private bool SettingsChanged(ReaderLayoutSettings settings) =>
        settings.FontScale != _settings.FontScale
        || Math.Abs(settings.LineHeight - _settings.LineHeight) > 0.001
        || Math.Abs(settings.MaxWidth - _settings.MaxWidth) > 0.001
        || settings.VerticalWriting != _settings.VerticalWriting
        || settings.ParagraphIndent != _settings.ParagraphIndent
        || Math.Abs(settings.BodyPadding - _settings.BodyPadding) > 0.001;

    private void NavigateCore(string chapterPath, string? fragment)
    {
        _composePending = true;
        _navigationCts?.Cancel();
        _navigationCts?.Dispose();
        var navigationCts = new CancellationTokenSource();
        _navigationCts = navigationCts;
        var navigationToken = navigationCts.Token;
        var version = Interlocked.Increment(ref _navigationVersion);
        var navigationSource = Source;

        _pendingFragment = string.IsNullOrWhiteSpace(fragment) ? null : NormalizeFragment(fragment);
        _pendingRestoreOffset = null;
        _pendingRestorePage = null;
        _pendingSeekToEnd = false;
        StopSelectionAutoPageTurn();
        _selectionActiveOffset = _selectionStart = _selectionEnd = _selectionAnchor = -1;
        _hasLastPointerPosition = false;
        _searchHits = null;
        _pageIndex = 0;

        // Compose off the UI thread; layout and shaping are pure CPU work on
        // engine-owned state, and the result is applied back on the UI thread.
        var settings = _settings;
        var width = Math.Max(1, Bounds.Width);
        var height = Math.Max(1, Bounds.Height);
        var startingArgs = new ReaderNavigationStartingEventArgs(navigationSource);
        NavigationStarting?.Invoke(this, startingArgs);
        if (startingArgs.Cancel)
        {
            navigationCts.Cancel();
            NavigationCompleted?.Invoke(this, new ReaderNavigationCompletedEventArgs(navigationSource, false));
            return;
        }

        _ = Dispatcher.UIThread.InvokeAsync(async () =>
        {
            try
            {
                var composed = await Task.Run(() =>
                {
                    navigationToken.ThrowIfCancellationRequested();
                    var loader = new XhtmlChapterLoader(settings.ParagraphIndent);
                    var content = loader.Load(chapterPath);
                    var options = BuildOptions(settings, width, height);
                    var layout = Compose(content, options);
                    return (content, options, layout);
                }, navigationToken);

                navigationToken.ThrowIfCancellationRequested();
                if (_disposed || version != Volatile.Read(ref _navigationVersion))
                {
                    NavigationCompleted?.Invoke(this, new ReaderNavigationCompletedEventArgs(navigationSource, false));
                    return;
                }

                _content = composed.content;
                _options = composed.options;
                _layout = composed.layout;
                _composePending = false;
                _bitmapDirty = true;
                _pageIndex = 0;
                ApplyPendingPosition();
                InvalidateVisual();
                EmitScroll();
                NavigationCompleted?.Invoke(this, new ReaderNavigationCompletedEventArgs(navigationSource, true));
            }
            catch (OperationCanceledException)
            {
                NavigationCompleted?.Invoke(this, new ReaderNavigationCompletedEventArgs(navigationSource, false));
            }
            catch
            {
                NavigationCompleted?.Invoke(this, new ReaderNavigationCompletedEventArgs(navigationSource, false));
            }
        });
    }

    private TypesetEngine Engine
    {
        get
        {
            lock (_engineGate)
            {
                if (_engine is null)
                {
                    _engine = new TypesetEngine(CreateFontLibrary());
                }

                return _engine;
            }
        }
    }

    private ChapterLayout Compose(ChapterContent content, TypesetLayoutOptions options)
    {
        lock (_engineGate)
        {
            return Engine.Compose(content, options);
        }
    }

    internal static string GetBundledFontPath() =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", BundledFontFileName);

    private static TypesetFontLibrary CreateFontLibrary()
    {
        var main = GetBundledFontPath();
        if (!File.Exists(main))
        {
            throw new InvalidOperationException($"Bundled reader font is missing: {main}");
        }

        var fonts = new List<string>();
        var systemFontDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        var candidates = new[]
        {
            Path.Combine(systemFontDirectory, "segoeui.ttf"),
            Path.Combine(systemFontDirectory, "msyh.ttc"),
            Path.Combine(systemFontDirectory, "simsun.ttc"),
            "/usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc",
            "/usr/share/fonts/truetype/noto/NotoSansCJK-Regular.ttc",
            "/usr/share/fonts/truetype/noto/NotoSans-Regular.ttf",
            "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
            "/System/Library/Fonts/PingFang.ttc",
            "/System/Library/Fonts/Hiragino Sans GB.ttc",
        };
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate)
                && !string.Equals(candidate, main, StringComparison.OrdinalIgnoreCase)
                && !fonts.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                fonts.Add(candidate);
            }
        }

        return new TypesetFontLibrary(main, fonts);
    }

    private static TypesetLayoutOptions BuildOptions(ReaderLayoutSettings settings, double width, double height)
    {
        var vertical = settings.VerticalWriting;
        var horizontalInset = Math.Max(0, settings.BodyPadding);
        var verticalInset = settings.BodyPadding;
        if (vertical)
        {
            (horizontalInset, verticalInset) = ReaderPlatformLayoutPolicy.GetVerticalPageInsets(
                width,
                height,
                settings.BodyPadding);
        }
        else
        {
            // MaxWidth is the readable body width, not the bitmap width. Keep
            // it centered in the viewport while retaining the requested
            // padding on narrow windows.
            var available = Math.Max(1, width - horizontalInset * 2);
            var contentWidth = Math.Min(available, Math.Max(1, settings.MaxWidth));
            horizontalInset = Math.Max(horizontalInset, (width - contentWidth) / 2);
        }

        return new TypesetLayoutOptions
        {
            WritingMode = vertical ? TypesetWritingMode.VerticalRl : TypesetWritingMode.HorizontalTb,
            BaseFontSize = 16f * (float)settings.FontScale,
            LineHeight = (float)settings.LineHeight,
            ParagraphIndent = settings.ParagraphIndent,
            ViewportWidth = (float)width,
            ViewportHeight = (float)height,
            InsetHorizontal = (float)horizontalInset,
            InsetVertical = (float)verticalInset,
        };
    }

    /// <summary>
    /// Measures every EPUB chapter with the same loader, font library and
    /// viewport geometry as the visible native reader. The toolbar must not
    /// use the old plain-text estimate: images, footnotes, headings and
    /// vertical Latin runs all affect the real page count.
    /// </summary>
    internal static int[] EstimatePageCounts(
        IReadOnlyList<string> chapterPaths,
        ReaderLayoutSettings settings,
        double width,
        double height,
        CancellationToken cancellationToken)
    {
        using var engine = new TypesetEngine(CreateFontLibrary());
        var options = BuildOptions(settings, width, height);
        var counts = new int[chapterPaths.Count];
        for (var index = 0; index < chapterPaths.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var content = new XhtmlChapterLoader(settings.ParagraphIndent).Load(chapterPaths[index]);
                counts[index] = Math.Max(1, engine.Compose(content, options).Pages.Count);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // A malformed spine item should not make the progress label
                // disappear for the rest of a valid book.
                counts[index] = 1;
            }
        }

        return counts;
    }

    private bool SameChapter(ReaderAnnotation annotation)
    {
        if (_content is null)
        {
            return false;
        }

        return annotation.EndOffset > annotation.StartOffset
            && annotation.EndOffset <= Math.Max(1, _content.BodyText.Length);
    }

    private void ApplyPendingPosition()
    {
        if (_layout is null || _layout.Pages.Count == 0)
        {
            return;
        }

        if (_pendingSeekToEnd)
        {
            _pageIndex = _layout.Pages.Count - 1;
            _pendingSeekToEnd = false;
            return;
        }

        if (_pendingFragment is { } fragment)
        {
            var fragmentPage = _layout.GetPageIndexOfFragment(NormalizeFragment(fragment));
            if (fragmentPage >= 0)
            {
                _pageIndex = fragmentPage;
                _pendingFragment = null;
                return;
            }
        }

        if (_pendingRestoreOffset is { } offset)
        {
            var offsetPage = _layout.GetPageIndexOfOffset(Math.Clamp(offset, 0, Math.Max(0, _layout.BodyTextLength - 1)));
            if (offsetPage >= 0)
            {
                _pageIndex = offsetPage;
            }

            _pendingRestoreOffset = null;
            return;
        }

        if (_pendingRestorePage is { } restorePage)
        {
            _pageIndex = Math.Clamp(restorePage, 0, _layout.Pages.Count - 1);
            _pendingRestorePage = null;
        }
    }

    private static string NormalizeFragment(string fragment)
    {
        fragment = fragment.Trim().TrimStart('#');
        try
        {
            return Uri.UnescapeDataString(fragment);
        }
        catch (UriFormatException)
        {
            return fragment;
        }
    }

    // ---- rendering --------------------------------------------------------

    private void ScheduleRelayout()
    {
        if (_disposed || _content is null)
        {
            return;
        }

        _relayoutTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(70), DispatcherPriority.Background, (_, _) =>
        {
            _relayoutTimer!.Stop();
            if (!_disposed && _content is not null)
            {
                Recompose();
                ApplyPendingPosition();
                _pageIndex = Math.Clamp(_pageIndex, 0, Math.Max(0, PageCount - 1));
                _bitmapDirty = true;
                InvalidateVisual();
                EmitScroll();
            }
        });
        _relayoutTimer.Stop();
        _relayoutTimer.Start();
    }

    private void Recompose()
    {
        if (_disposed || _content is null)
        {
            return;
        }

        var width = Math.Max(1, Bounds.Width);
        var height = Math.Max(1, Bounds.Height);
        var startOffset = _layout is not null
            && _pageIndex < _layout.Pages.Count
            ? _layout.Pages[_pageIndex].TextStartOffset
            : -1;

        var options = BuildOptions(_settings, width, height);
        _options = options;
        _layout = Compose(_content, options);
        _composePending = false;

        if (startOffset >= 0)
        {
            var page = _layout.GetPageIndexOfOffset(startOffset);
            _pageIndex = page >= 0 ? page : 0;
        }
    }

    private double CurrentScaling => TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;

    public override void Render(DrawingContext context)
    {
        var width = Math.Max(1, Bounds.Width);
        var height = Math.Max(1, Bounds.Height);
        var scaling = CurrentScaling;
        var pixelWidth = Math.Max(1, (int)Math.Ceiling(width * scaling));
        var pixelHeight = Math.Max(1, (int)Math.Ceiling(height * scaling));

        if (_bitmap is null
            || _bitmapPixelWidth != pixelWidth
            || _bitmapPixelHeight != pixelHeight)
        {
            _bitmap?.Dispose();
            _bitmap = new WriteableBitmap(
                new PixelSize(pixelWidth, pixelHeight),
                new Vector(96, 96),
                PixelFormats.Bgra8888,
                AlphaFormat.Opaque);
            _bitmapPixelWidth = pixelWidth;
            _bitmapPixelHeight = pixelHeight;
            _bitmapDirty = true;
        }

        if (_bitmapDirty && _layout is not null && _options is not null)
        {
            PaintPageIntoBitmap(pixelWidth, pixelHeight, scaling);
            _bitmapDirty = false;
        }

        if (_bitmap is not null)
        {
            context.DrawImage(_bitmap, new Rect(0, 0, width, height));
        }
        else
        {
            context.FillRectangle(Brushes.White, new Rect(0, 0, width, height));
        }
    }

    private void PaintPageIntoBitmap(int pixelWidth, int pixelHeight, double scaling)
    {
        if (_bitmap is null || _layout is null || _options is null)
        {
            return;
        }

        var page = _pageIndex >= 0 && _pageIndex < _layout.Pages.Count
            ? _layout.Pages[_pageIndex]
            : null;

        using var frame = _bitmap.Lock();
        var info = new SKImageInfo(pixelWidth, pixelHeight, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var surface = SKSurface.Create(info, frame.Address, frame.RowBytes);
        if (surface is null)
        {
            return;
        }

        var canvas = surface.Canvas;
        if (page is null)
        {
            canvas.Clear(SKColors.White);
            return;
        }

        canvas.Save();
        canvas.Scale((float)scaling);
        var selectionBands = SelectionBandsFor(page);
        var annotationOverlays = AnnotationOverlaysFor(page);
        var searchBands = SearchBandsFor(page);
        lock (_engineGate)
        {
            var painter = new TypesetPainter(Engine.Fonts, TypesetPaintTheme.Paper, ResolveImage);
            painter.Paint(
                canvas,
                page,
                selectionBands,
                highlightBands: null,
                searchBands: searchBands,
                annotationOverlays: annotationOverlays,
                showVerticalDebugBoxes: _showVerticalDebugBoxes);
        }
        canvas.Restore();
        canvas.Flush();
    }

    private TypesetFontLibrary FontsForPainting => Engine.Fonts;

    private static SKImage? ResolveImage(string path)
    {
        lock (ImageCacheLock)
        {
            if (ImageCache.TryGetValue(path, out var cached))
            {
                return cached;
            }

            try
            {
                using var bitmap = SKBitmap.Decode(path);
                if (bitmap is null)
                {
                    return null;
                }

                var image = SKImage.FromBitmap(bitmap);
                ImageCache[path] = image;
                ImageCacheOrder.Enqueue(path);
                while (ImageCacheOrder.Count > 12)
                {
                    var oldest = ImageCacheOrder.Dequeue();
                    if (ImageCache.TryGetValue(oldest, out var stale)
                        && !ReferenceEquals(stale, image))
                    {
                        ImageCache.Remove(oldest);
                        stale.Dispose();
                    }
                }

                return image;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                return null;
            }
        }
    }

    private IReadOnlyList<SKRect>? SelectionBandsFor(LayoutPage page)
    {
        if (_selectionStart < 0 || _selectionEnd <= _selectionStart || _layout is null)
        {
            return null;
        }

        return _layout.GetOverlayRects(page.Index, _selectionStart, _selectionEnd - _selectionStart);
    }

    private IReadOnlyList<TypesetAnnotationOverlay>? AnnotationOverlaysFor(LayoutPage page)
    {
        if (_annotations.Count == 0 || _layout is null)
        {
            return null;
        }

        var overlays = new List<TypesetAnnotationOverlay>();
        foreach (var annotation in _annotations)
        {
            if (annotation.EndOffset <= annotation.StartOffset)
            {
                continue;
            }

            var bands = _layout.GetOverlayRects(
                page.Index,
                annotation.StartOffset,
                annotation.EndOffset - annotation.StartOffset);
            if (bands.Count == 0)
            {
                continue;
            }

            overlays.Add(new TypesetAnnotationOverlay
            {
                Bands = bands,
                Style = annotation.UnderlineStyle,
                Color = ParseAnnotationColor(annotation.Color),
            });
        }

        return overlays.Count > 0 ? overlays : null;
    }

    private static SKColor ParseAnnotationColor(string? value)
    {
        if (value is { Length: 7 }
            && value[0] == '#'
            && uint.TryParse(
                value.AsSpan(1),
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out var rgb))
        {
            return new SKColor(
                (byte)(rgb >> 16),
                (byte)(rgb >> 8),
                (byte)rgb);
        }

        return SKColors.Black;
    }

    private IReadOnlyList<SKRect>? SearchBandsFor(LayoutPage page)
    {
        if (_searchHits is not { Count: > 0 } || _layout is null)
        {
            return null;
        }

        var bands = new List<SKRect>();
        foreach (var (start, length) in _searchHits)
        {
            bands.AddRange(_layout.GetOverlayRects(page.Index, start, length));
        }

        return bands.Count > 0 ? bands : null;
    }

    // ---- input and bridge protocol ----------------------------------------

    private void Emit(object message)
    {
        WebMessageReceived?.Invoke(
            this,
            new ReaderWebMessageReceivedEventArgs(JsonSerializer.Serialize(message)));
    }

    private void EmitScroll()
    {
        var state = GetScrollState();
        Emit(new
        {
            type = "scroll",
            top = 0.0,
            left = state.Position,
            scrollWidth = state.ScrollWidth,
            scrollHeight = state.ScrollHeight,
            clientWidth = state.ClientWidth,
            clientHeight = state.ClientHeight,
            fragment = (string?)null,
        });
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        Focus();
        StopSelectionAutoPageTurn();
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        var position = point.Position;
        _pointerDown = true;
        _selecting = false;
        _pointerDragStarted = false;
        _pointerDownPosition = position;
        _lastPointerPosition = position;
        _hasLastPointerPosition = true;
        _pointerHotZone = _layout?.GetHotZoneAt(
            _pageIndex,
            new SKPoint((float)position.X, (float)position.Y));
        _pointerDownOffset = _layout?.HitTest(
            _pageIndex,
            new SKPoint((float)position.X, (float)position.Y)) ?? -1;

        if (_pointerHotZone is { Kind: HotZoneKind.FootnoteMarker } footnote)
        {
            UpdateFootnoteHover(position, footnote);
        }

        if (_pointerDownOffset >= 0 && _pointerHotZone is null)
        {
            _selectionAnchor = _pointerDownOffset;
            SetActiveSelectionOffset(_pointerDownOffset);
        }

        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var position = e.GetPosition(this);
        var previousPosition = _lastPointerPosition;
        var hadPreviousPosition = _hasLastPointerPosition;
        _lastPointerPosition = position;
        _hasLastPointerPosition = true;
        if (!_pointerDown)
        {
            UpdateFootnoteHover(position);
            return;
        }

        if (_pointerHotZone is not null)
        {
            StopSelectionAutoPageTurn();
            return;
        }

        if (!_pointerDragStarted && _pointerDownOffset >= 0)
        {
            var dx = position.X - _pointerDownPosition.X;
            var dy = position.Y - _pointerDownPosition.Y;
            if (dx * dx + dy * dy >= 16)
            {
                _pointerDragStarted = true;
                _selecting = true;
            }
        }

        if (!_selecting || _layout is null)
        {
            StopSelectionAutoPageTurn();
            return;
        }

        var offset = GetSelectionHitOffset(position);
        if (offset < 0)
        {
            StopSelectionAutoPageTurn();
            return;
        }

        SetActiveSelectionOffset(offset);
        var pageTurnDirection = GetSelectionPageTurnDirection(
            offset,
            position,
            hadPreviousPosition ? previousPosition : null);
        if (pageTurnDirection != 0)
        {
            ArmSelectionAutoPageTurn(pageTurnDirection);
        }
        else
        {
            StopSelectionAutoPageTurn();
        }
        _bitmapDirty = true;
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (!_pointerDown)
        {
            base.OnPointerReleased(e);
            return;
        }

        var position = e.GetPosition(this);
        var hotZone = _pointerHotZone;
        var wasDrag = _pointerDragStarted;
        StopSelectionAutoPageTurn();
        var insideSurface = IsInsideSurface(position);
        var releasedHotZone = insideSurface
            ? _layout?.GetHotZoneAt(
                _pageIndex,
                new SKPoint((float)position.X, (float)position.Y))
            : null;
        var clickSide = insideSurface ? GetPageClickSide(position) : null;
        _pointerDown = false;
        _pointerHotZone = null;
        _hasLastPointerPosition = false;
        e.Pointer.Capture(null);

        if (hotZone is not null)
        {
            // Pointer capture keeps receiving the release after the pointer
            // leaves the page. A link is activated only when the release is
            // still over the same hot-zone; dragging off a marker must not
            // accidentally open a chapter or footnote.
            if (!wasDrag && ReferenceEquals(hotZone, releasedHotZone))
            {
                Emit(new
                {
                    type = "link",
                    href = hotZone.Href,
                    footnote = hotZone.Kind == HotZoneKind.FootnoteMarker,
                    footnoteText = hotZone.FootnoteText,
                    x = position.X,
                    y = position.Y,
                });
            }
            else if (hotZone.Kind == HotZoneKind.FootnoteMarker)
            {
                UpdateFootnoteHover(null);
            }

            _selecting = false;
            e.Handled = true;
            return;
        }

        if (_selecting && wasDrag)
        {
            _selecting = false;
            EmitSelection();
            e.Handled = true;
            return;
        }

        _selecting = false;
        if (clickSide is not null)
        {
            Emit(new { type = "pageClick", side = clickSide });
        }
        else if (_pointerDownOffset >= 0)
        {
            EmitSelection();
        }

        _selectionAnchor = -1;
        _selectionActiveOffset = -1;
        _selectionStart = -1;
        _selectionEnd = -1;
        _pointerDownOffset = -1;
        _bitmapDirty = true;
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        if (!_pointerDown)
        {
            UpdateFootnoteHover(null);
        }

        base.OnPointerExited(e);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        // Avalonia's positive Y wheel delta means wheel-up; the reader bridge
        // uses the browser convention where positive deltaY advances forward.
        var delta = (int)Math.Round(-e.Delta.Y * 120);
        if (delta != 0)
        {
            Emit(new { type = "wheel", deltaY = (double)delta });
        }

        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var ctrl = (e.KeyModifiers & KeyModifiers.Control) != 0;
        if (ctrl && e.Key is Key.F or Key.B)
        {
            Emit(new { type = "shortcut", key = e.Key == Key.F ? "f" : "b", ctrlKey = true });
            e.Handled = true;
            return;
        }

        var name = e.Key switch
        {
            Key.Left => "ArrowLeft",
            Key.Right => "ArrowRight",
            Key.Up => "ArrowUp",
            Key.Down => "ArrowDown",
            Key.PageUp => "PageUp",
            Key.PageDown => "PageDown",
            Key.Home => "Home",
            Key.End => "End",
            Key.Space => "PageDown",
            Key.Escape => "escape",
            Key.F11 => "f11",
            _ => null,
        };

        if (name is not null)
        {
            if (name is "escape" or "f11")
            {
                Emit(new { type = "shortcut", key = name, ctrlKey = false });
            }
            else
            {
                Emit(new { type = "key", key = name });
            }
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    private string? GetPageClickSide(Point position)
    {
        var width = Math.Max(1, Bounds.Width);
        return position.X < width / 3
            ? "left"
            : position.X > width * 2 / 3
                ? "right"
                : null;
    }

    private bool IsInsideSurface(Point position) =>
        position.X >= 0
        && position.Y >= 0
        && position.X <= Bounds.Width
        && position.Y <= Bounds.Height;

    private void UpdateFootnoteHover(Point? position, PlacedHotZone? knownZone = null)
    {
        PlacedHotZone? zone = knownZone;
        if (zone is null && position is { } point && _layout is not null)
        {
            zone = _layout.GetHotZoneAt(
                _pageIndex,
                new SKPoint((float)point.X, (float)point.Y));
        }

        if (zone is { Kind: HotZoneKind.FootnoteMarker } footnote
            && position is { } hoverPoint)
        {
            if (!string.Equals(_hoveredFootnoteHref, footnote.Href, StringComparison.Ordinal))
            {
                _hoveredFootnoteHref = footnote.Href;
                Emit(new
                {
                    type = "footnoteHover",
                    href = footnote.Href,
                    footnoteText = footnote.FootnoteText,
                    x = hoverPoint.X,
                    y = hoverPoint.Y,
                });
            }

            return;
        }

        if (_hoveredFootnoteHref is not null)
        {
            _hoveredFootnoteHref = null;
            Emit(new { type = "footnoteLeave" });
        }
    }

    private void EmitSelection()
    {
        if (_content is null
            || _selectionStart < 0
            || _selectionEnd <= _selectionStart)
        {
            Emit(new { type = "selection", text = (string?)null, startOffset = 0, endOffset = 0, prefix = string.Empty, suffix = string.Empty });
            return;
        }

        var start = Math.Clamp(_selectionStart, 0, _content.BodyText.Length);
        var end = Math.Clamp(_selectionEnd, start, _content.BodyText.Length);
        if (end <= start)
        {
            Emit(new { type = "selection", text = (string?)null, startOffset = 0, endOffset = 0, prefix = string.Empty, suffix = string.Empty });
            return;
        }
        var text = _content.BodyText[start..end];
        var prefixStart = Math.Max(0, start - 16);
        var prefix = _content.BodyText[prefixStart..start];
        var suffixEnd = Math.Min(_content.BodyText.Length, end + 16);
        var suffix = _content.BodyText[end..suffixEnd];
        var placement = GetSelectionPlacement(start, end);

        Emit(new
        {
            type = "selection",
            text,
            startOffset = start,
            endOffset = end,
            prefix,
            suffix,
            x = placement?.Left,
            y = placement?.Top,
            bottom = placement?.Bottom,
        });
    }

    private int GetSelectionHitOffset(Point position)
    {
        if (_layout is null
            || _pageIndex < 0
            || _pageIndex >= _layout.Pages.Count)
        {
            return -1;
        }

        var offset = _layout.HitTest(
            _pageIndex,
            new SKPoint((float)position.X, (float)position.Y));
        if (offset >= 0)
        {
            return offset;
        }

        // Pointer capture continues after the pointer leaves the page. Keep
        // the selection alive at the corresponding text boundary instead of
        // cancelling the auto-turn as soon as HitTest has no glyph to use.
        var page = _layout.Pages[_pageIndex];
        if (page.TextStartOffset < 0 || page.TextEndOffset <= page.TextStartOffset)
        {
            return -1;
        }

        var boundaryDirection = GetPointerBoundaryDirection(position);
        if (boundaryDirection == 0)
        {
            return -1;
        }

        // Once a page has turned, the captured pointer may still be outside
        // the surface at the same side. Preserve the newly established first
        // or last-character endpoint instead of converting it into the whole
        // target page on the next pointer event.
        if (_selectionAutoPageTurnArmed
            && _selectionActiveOffset >= 0
            && ((boundaryDirection > 0 && _selectionActiveOffset == page.TextStartOffset)
                || (boundaryDirection < 0 && _selectionActiveOffset == page.TextEndOffset)))
        {
            return _selectionActiveOffset;
        }

        return boundaryDirection > 0 ? page.TextEndOffset : page.TextStartOffset;
    }

    private int GetPointerBoundaryDirection(Point position)
    {
        if (Vertical)
        {
            // The selection gesture for vertical pages follows the physical
            // horizontal direction requested by the reader UI: right enters
            // the next page and left returns to the previous page.
            if (position.X > Bounds.Width)
            {
                return 1;
            }

            if (position.X < 0)
            {
                return -1;
            }
        }
        else
        {
            if (position.X > Bounds.Width || position.Y > Bounds.Height)
            {
                return 1;
            }

            if (position.X < 0 || position.Y < 0)
            {
                return -1;
            }
        }

        return position.Y > Bounds.Height ? 1 : position.Y < 0 ? -1 : 0;
    }

    private int GetSelectionPageTurnDirection(
        int offset,
        Point position,
        Point? previousPosition)
    {
        if (_layout is null
            || _selectionAnchor < 0
            || _pageIndex < 0
            || _pageIndex >= _layout.Pages.Count)
        {
            return 0;
        }

        var page = _layout.Pages[_pageIndex];
        var atPageStart = ReaderSelectionPagingPolicy.IsAtPageStart(
            offset,
            page.TextStartOffset,
            page.TextEndOffset);
        var atPageEnd = ReaderSelectionPagingPolicy.IsAtPageEnd(
            offset,
            page.TextStartOffset,
            page.TextEndOffset);
        if (!atPageStart && !atPageEnd)
        {
            return 0;
        }

        var logicalDirection = Math.Sign(offset - _selectionAnchor);
        if (Vertical && previousPosition is { } previous)
        {
            var physicalDirection = Math.Sign(position.X - previous.X);
            if (atPageEnd && physicalDirection > 0)
            {
                return 1;
            }

            if (atPageStart && physicalDirection < 0)
            {
                return -1;
            }
        }

        // The offset direction is authoritative when the pointer moves along
        // the text flow, or when a platform sends a boundary event without a
        // horizontal delta. It also covers the first event after a press.
        if (atPageEnd && logicalDirection > 0)
        {
            return 1;
        }

        if (atPageStart && logicalDirection < 0)
        {
            return -1;
        }

        return 0;
    }

    private void ArmSelectionAutoPageTurn(int direction)
    {
        direction = Math.Sign(direction);
        if (direction == 0)
        {
            StopSelectionAutoPageTurn();
            return;
        }

        if (_selectionAutoPageTurnArmed
            && _selectionAutoPageTurnDirection == direction)
        {
            return;
        }

        _selectionAutoPageTurnArmed = true;
        _selectionAutoPageTurnDirection = direction;
        _selectionAutoPageTurnTimer ??= new DispatcherTimer
        {
            Interval = SelectionAutoPageTurnDelay,
        };
        _selectionAutoPageTurnTimer.Stop();
        _selectionAutoPageTurnTimer.Tick -= SelectionAutoPageTurnTimer_Tick;
        _selectionAutoPageTurnTimer.Tick += SelectionAutoPageTurnTimer_Tick;
        _selectionAutoPageTurnTimer.Start();
    }

    private void StopSelectionAutoPageTurn()
    {
        _selectionAutoPageTurnArmed = false;
        _selectionAutoPageTurnDirection = 0;
        _selectionAutoPageTurnTimer?.Stop();
    }

    private void SelectionAutoPageTurnTimer_Tick(object? sender, EventArgs e)
    {
        _selectionAutoPageTurnTimer?.Stop();
        var direction = _selectionAutoPageTurnDirection;
        if (_disposed
            || !_selectionAutoPageTurnArmed
            || !_pointerDown
            || !_selecting
            || _selectionAnchor < 0
            || _layout is null
            || direction == 0
            || !CanTurn(direction))
        {
            StopSelectionAutoPageTurn();
            return;
        }

        if (!ContinueSelectionAcrossPage(direction))
        {
            StopSelectionAutoPageTurn();
            return;
        }

        // Keep advancing once per second while the pointer remains held at
        // the page edge. Every turn resets the endpoint to the target page's
        // first/last character boundary, so a page is never implicitly added
        // in its entirety merely because it became visible.
        _selectionAutoPageTurnTimer!.Start();
    }

    private bool ContinueSelectionAcrossPage(int direction)
    {
        direction = Math.Sign(direction);
        if (_layout is null || direction == 0 || !TurnPage(direction))
        {
            return false;
        }

        var page = _layout.Pages[_pageIndex];
        var endpoint = ReaderSelectionPagingPolicy.GetCrossPageEndpoint(
            direction,
            page.TextStartOffset,
            page.TextEndOffset);
        if (endpoint >= 0)
        {
            SetActiveSelectionOffset(endpoint);
        }

        _bitmapDirty = true;
        InvalidateVisual();
        Emit(new { type = "selectionPageTurn", direction });
        return true;
    }

    private void SetActiveSelectionOffset(int offset)
    {
        if (_selectionAnchor < 0)
        {
            return;
        }

        var maximum = _layout?.BodyTextLength
            ?? _content?.BodyText.Length
            ?? Math.Max(_selectionAnchor, offset);
        _selectionActiveOffset = Math.Clamp(offset, 0, maximum);
        _selectionStart = Math.Min(_selectionAnchor, _selectionActiveOffset);
        _selectionEnd = Math.Max(_selectionAnchor, _selectionActiveOffset);
    }

    private (float Left, float Top, float Bottom)? GetSelectionPlacement(int start, int end)
    {
        if (_layout is null
            || _pageIndex < 0
            || _pageIndex >= _layout.Pages.Count)
        {
            return null;
        }

        var rects = _layout.GetOverlayRects(_pageIndex, start, end - start);
        if (rects.Count == 0)
        {
            // A forward cross-page selection ends immediately before the new
            // page's first character, so it has no painted selection band on
            // that page yet. Anchor the popup to that first character. The
            // reverse case is the matching position immediately after the
            // previous page's last character.
            var boundary = _selectionActiveOffset >= 0
                ? _selectionActiveOffset
                : end;
            var boundaryCharacter = _selectionActiveOffset >= _selectionAnchor
                ? boundary
                : boundary - 1;
            var page = _layout.Pages[_pageIndex];
            var candidates = new[]
            {
                boundaryCharacter,
                page.TextStartOffset,
                page.TextEndOffset - 1,
            };
            foreach (var candidate in candidates.Distinct())
            {
                if (candidate < 0
                    || candidate < page.TextStartOffset
                    || candidate >= page.TextEndOffset
                    || _layout.GetCharRect(_pageIndex, candidate) is not { } rect)
                {
                    continue;
                }

                return (rect.Left, rect.Top, rect.Bottom);
            }

            return null;
        }

        return (
            rects.Min(rect => rect.Left),
            rects.Min(rect => rect.Top),
            rects.Max(rect => rect.Bottom));
    }

    public void ClearSelection()
    {
        _selecting = false;
        StopSelectionAutoPageTurn();
        _selectionActiveOffset = _selectionStart = _selectionEnd = _selectionAnchor = -1;
        _hasLastPointerPosition = false;
        _bitmapDirty = true;
        InvalidateVisual();
    }

    // ---- snapshot provider (page transition effects) -----------------------

    public Task<byte[]?> CaptureVisiblePageAsync(CancellationToken cancellationToken)
    {
        var bitmap = _bitmap;
        if (bitmap is null)
        {
            return Task.FromResult<byte[]?>(null);
        }

        try
        {
            using var frame = bitmap.Lock();
            var info = new SKImageInfo(
                _bitmapPixelWidth,
                _bitmapPixelHeight,
                SKColorType.Bgra8888,
                SKAlphaType.Premul);
            using var image = SKImage.FromPixelCopy(info, frame.Address, frame.RowBytes);
            using var data = image.Encode(SKEncodedImageFormat.Png, 90);
            return Task.FromResult<byte[]?>(data.ToArray());
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            return Task.FromResult<byte[]?>(null);
        }
    }
}
