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
    private ChapterContent? _content;
    private ChapterLayout? _layout;
    private TypesetLayoutOptions? _options;
    private ReaderLayoutSettings _settings = new();
    private readonly XhtmlChapterLoader _loader = new(paragraphIndent: true);

    private int _pageIndex;
    private bool _composePending;
    private bool _bitmapDirty;
    private WriteableBitmap? _bitmap;
    private int _bitmapPixelWidth;
    private int _bitmapPixelHeight;
    private DispatcherTimer? _relayoutTimer;

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
    private int _selectionStart = -1;
    private int _selectionEnd = -1;

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
    }

    public void Dispose()
    {
        _relayoutTimer?.Stop();
        _engine?.Dispose();
        _engine = null;
        _layout = null;
        _content = null;
        _bitmap = null;
    }

    /// <summary>Loads and lays out a chapter. The URI fragment selects the anchor.</summary>
    public void Navigate(Uri uri)
    {
        Source = uri;
        var path = uri.IsFile ? uri.LocalPath : uri.AbsolutePath;
        var fragment = string.IsNullOrWhiteSpace(uri.Fragment)
            ? null
            : Uri.UnescapeDataString(uri.Fragment.TrimStart('#'));
        NavigateCore(path, fragment);
    }

    // ---- native surface API used by the MainWindow seams ------------------

    public bool IsNative => true;

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
        bool restoreFromProgress)
    {
        var settingsChanged = SettingsChanged(settings);
        _settings = settings;

        if (_content is not null && (settingsChanged || _layout is null || _composePending))
        {
            Recompose();
        }

        if (restoreFromProgress)
        {
            _pendingFragment = fragment;
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

    // ---- chapter pipeline -------------------------------------------------

    private bool SettingsChanged(ReaderLayoutSettings settings) =>
        settings.FontScale != _settings.FontScale
        || Math.Abs(settings.LineHeight - _settings.LineHeight) > 0.001
        || settings.VerticalWriting != _settings.VerticalWriting
        || settings.ParagraphIndent != _settings.ParagraphIndent
        || Math.Abs(settings.BodyPadding - _settings.BodyPadding) > 0.001;

    private void NavigateCore(string chapterPath, string? fragment)
    {
        _pendingFragment = fragment;
        _pendingRestoreOffset = null;
        _pendingRestorePage = null;
        _pendingSeekToEnd = false;
        _selectionStart = _selectionEnd = _selectionAnchor = -1;
        _searchHits = null;
        _pageIndex = 0;

        // Compose off the UI thread; layout and shaping are pure CPU work on
        // engine-owned state, and the result is applied back on the UI thread.
        var settings = _settings;
        var width = Math.Max(1, Bounds.Width);
        var height = Math.Max(1, Bounds.Height);
        NavigationStarting?.Invoke(this, new ReaderNavigationStartingEventArgs(Source));
        _ = Dispatcher.UIThread.InvokeAsync(async () =>
        {
            try
            {
                var composed = await Task.Run(() =>
                {
                    var content = _loader.Load(chapterPath);
                    var options = BuildOptions(settings, width, height);
                    var layout = Engine.Compose(content, options);
                    return (content, options, layout);
                });

                _content = composed.content;
                _options = composed.options;
                _layout = composed.layout;
                _composePending = false;
                _bitmapDirty = true;
                _pageIndex = 0;
                ApplyPendingPosition();
                InvalidateVisual();
                EmitScroll();
                NavigationCompleted?.Invoke(this, new ReaderNavigationCompletedEventArgs(Source, true));
            }
            catch
            {
                NavigationCompleted?.Invoke(this, new ReaderNavigationCompletedEventArgs(Source, false));
            }
        });
    }

    private TypesetEngine Engine
    {
        get
        {
            if (_engine is null)
            {
                _engine = new TypesetFontLibrary(GetBundledFontPath()) is { } fonts
                    ? new TypesetEngine(fonts)
                    : throw new InvalidOperationException("Bundled reader font is missing.");
            }

            return _engine;
        }
    }

    internal static string GetBundledFontPath() =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", BundledFontFileName);

    private TypesetLayoutOptions BuildOptions(ReaderLayoutSettings settings, double width, double height)
    {
        var vertical = settings.VerticalWriting;
        var inset = settings.BodyPadding;
        if (vertical)
        {
            var (horizontalInset, verticalInset) = ReaderPlatformLayoutPolicy.GetVerticalPageInsets(
                width,
                height,
                inset);
            inset = vertical ? verticalInset : horizontalInset;
        }

        return new TypesetLayoutOptions
        {
            WritingMode = vertical ? TypesetWritingMode.VerticalRl : TypesetWritingMode.HorizontalTb,
            BaseFontSize = 16f * (float)settings.FontScale,
            LineHeight = (float)settings.LineHeight,
            ParagraphIndent = settings.ParagraphIndent,
            ViewportWidth = (float)width,
            ViewportHeight = (float)height,
            InsetHorizontal = (float)inset,
            InsetVertical = (float)inset,
        };
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
            var fragmentPage = _layout.GetPageIndexOfFragment(fragment);
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

    // ---- rendering --------------------------------------------------------

    private void ScheduleRelayout()
    {
        if (_content is null)
        {
            return;
        }

        _relayoutTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(70), DispatcherPriority.Background, (_, _) =>
        {
            _relayoutTimer!.Stop();
            if (_content is not null)
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
        if (_content is null)
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
        _layout = Engine.Compose(_content, options);
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
        var highlightBands = AnnotationBandsFor(page);
        var searchBands = SearchBandsFor(page);
        var painter = new TypesetPainter(FontsForPainting, TypesetPaintTheme.Paper, ResolveImage);
        painter.Paint(canvas, page, selectionBands, highlightBands, searchBands);
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
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
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

    private IReadOnlyList<SKRect>? AnnotationBandsFor(LayoutPage page)
    {
        if (_annotations.Count == 0 || _layout is null)
        {
            return null;
        }

        var bands = new List<SKRect>();
        foreach (var annotation in _annotations)
        {
            if (annotation.EndOffset <= annotation.StartOffset)
            {
                continue;
            }

            bands.AddRange(_layout.GetOverlayRects(
                page.Index,
                annotation.StartOffset,
                annotation.EndOffset - annotation.StartOffset));
        }

        return bands.Count > 0 ? bands : null;
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
            top = Vertical ? 0.0 : state.Position,
            left = Vertical ? state.Position : state.Position,
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
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        var position = point.Position;
        if (_layout is not null && e.ClickCount == 1)
        {
            var offset = _layout.HitTest(_pageIndex, new SKPoint((float)position.X, (float)position.Y));
            if (offset >= 0)
            {
                _selecting = true;
                _selectionAnchor = offset;
                _selectionStart = offset;
                _selectionEnd = offset;
                _bitmapDirty = true;
                InvalidateVisual();
                e.Pointer.Capture(this);
                e.Handled = true;
                return;
            }
        }

        // No text under the pointer: treat as a page click zone.
        EmitPageClick(position);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (!_selecting || _layout is null)
        {
            return;
        }

        var position = e.GetPosition(this);
        var offset = _layout.HitTest(_pageIndex, new SKPoint((float)position.X, (float)position.Y));
        if (offset < 0)
        {
            return;
        }

        _selectionStart = Math.Min(_selectionAnchor, offset);
        _selectionEnd = Math.Max(_selectionAnchor, offset);
        _bitmapDirty = true;
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (_selecting)
        {
            _selecting = false;
            e.Pointer.Capture(null);
            EmitSelection();
            e.Handled = true;
            return;
        }

        base.OnPointerReleased(e);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        var delta = (int)Math.Round(e.Delta.Y * 120);
        if (delta != 0)
        {
            Emit(new { type = "wheel", deltaY = (double)delta });
        }

        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
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
            _ => null,
        };

        if (name is not null)
        {
            Emit(new { type = "key", key = name });
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    private void EmitPageClick(Point position)
    {
        var width = Math.Max(1, Bounds.Width);
        var side = position.X < width / 3
            ? "left"
            : position.X > width * 2 / 3
                ? "right"
                : null;
        if (side is not null)
        {
            Emit(new { type = "pageClick", side });
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
        var end = Math.Clamp(_selectionEnd, start + 1, _content.BodyText.Length);
        var text = _content.BodyText[start..end];
        var prefixStart = Math.Max(0, start - 16);
        var prefix = _content.BodyText[prefixStart..start];
        var suffixEnd = Math.Min(_content.BodyText.Length, end + 16);
        var suffix = _content.BodyText[end..suffixEnd];

        Emit(new
        {
            type = "selection",
            text,
            startOffset = start,
            endOffset = end,
            prefix,
            suffix,
        });
    }

    public void ClearSelection()
    {
        _selectionStart = _selectionEnd = _selectionAnchor = -1;
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
