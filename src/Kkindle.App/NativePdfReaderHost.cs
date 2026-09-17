using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle;

/// <summary>Virtualized PDF pages, rasterized from the original PDF at device resolution.</summary>
public sealed partial class NativePdfReaderHost : Control, IReaderHost, IReaderPageSnapshotProvider
{
    private sealed class CachedPage(PdfPageContent content) : IDisposable
    {
        public PdfPageContent Content { get; } = content;
        public PdfRasterRegion? Raster { get; set; }
        public WriteableBitmap? Bitmap { get; set; }
        public ReaderPalette? Palette { get; set; }
        public void Dispose() => Bitmap?.Dispose();
    }

    private readonly object _documentGate = new();
    private readonly ScrollBar _vertical = new() { Orientation = Orientation.Vertical };
    private readonly ScrollBar _horizontal = new() { Orientation = Orientation.Horizontal };
    private readonly DispatcherTimer _resizeTimer;
    private readonly Dictionary<int, CachedPage> _pages = [];
    private PdfDocumentService? _document;
    private string? _documentPath;
    private bool _documentReady;
    private CancellationTokenSource? _documentCancellation;
    private CancellationTokenSource? _navigation;
    private CancellationTokenSource? _viewportCancellation;
    private Task _renderTask = Task.CompletedTask;
    private Size[] _sizes = [];
    private PdfReaderLayout? _layout;
    private double _layoutDpi = 1;
    private ReaderPalette _palette = ReaderPalette.For(ReaderTheme.Classic);
    private IReadOnlyList<ReaderAnnotation> _annotations = [];
    private int _version;
    private int _rasterVersion;
    private int _layoutRequest;
    private bool _disposed;
    private bool _syncingBars;
    private Vector _pan;
    private Point? _pointerStart;
    private int _anchorOffset = -1;
    private int _selectionPage = 1;
    private int _selectionStart;
    private int _selectionEnd;
    private bool _dragging;
    private Guid? _hoveredAnnotation;
    private readonly List<(int Start, int Length)> _search = [];
    private int _searchPage = 1;
    private int _searchIndex = -1;
    private string _searchQuery = string.Empty;
    private (int Page, int Start, int Length)? _speechHighlight;
    private int _paperColumnIndex;
    private bool _pointAnnotationMode;
    private bool _regionSelectionMode;
    private Point? _regionPointerStart;
    private int _regionSelectionPage;
    private PdfPageCrop? _regionSelection;

    public NativePdfReaderHost()
    {
        Focusable = true;
        ClipToBounds = true;
        ScrollBarAutoHide.SetIsEnabled(_vertical, true);
        ScrollBarAutoHide.SetIsEnabled(_horizontal, true);
        VisualChildren.Add(_vertical);
        VisualChildren.Add(_horizontal);
        _vertical.ValueChanged += (_, _) => { if (!_syncingBars) SetPan(new(_pan.X, _vertical.Value)); };
        _horizontal.ValueChanged += (_, _) => { if (!_syncingBars) SetPan(new(_horizontal.Value, _pan.Y)); };
        _resizeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(45) };
        _resizeTimer.Tick += async (_, _) =>
        {
            _resizeTimer.Stop();
            RebuildLayout(preservePosition: true);
            await RefreshViewportAsync();
        };
        PropertyChanged += (_, args) =>
        {
            if (args.Property != BoundsProperty || _disposed || _sizes.Length == 0) return;
            RebuildLayout(preservePosition: true);
            ScheduleRender();
        };
        PointerCaptureLost += (_, _) => { _pointerStart = null; _dragging = false; };
    }

    public object View => this;
    public Uri? Source { get; private set; }
    public Task ReadyTask => Task.CompletedTask;
    public Task RenderingTask => _renderTask;
    public int PageNumber { get; private set; } = 1;
    public int PageCount { get; private set; }
    public double Zoom { get; private set; } = 1;
    public PdfReaderDisplayMode DisplayMode { get; private set; } = PdfReaderDisplayMode.Continuous;
    public PdfReaderFitMode FitMode { get; private set; } = PdfReaderFitMode.Width;
    public int Rotation { get; private set; }
    public int PaperColumnIndex => _paperColumnIndex;
    public bool IsPointAnnotationMode => _pointAnnotationMode;
    public bool IsRegionSelectionMode => _regionSelectionMode;
    public bool HasDetectedPaperColumns =>
        GetPageContent(PageNumber) is { } content
        && PdfPaperAnalysisService.DetectColumns(content).IsTwoColumn;
    public string? LastError { get; private set; }
    public PdfPageContent? PageContent => GetPageContent(PageNumber);
    public double VisibleTop => GetVisibleTop(new Rect(Viewport));
    // ScrollToTop aligns destinations inside this margin. Sample the same
    // edge when following the outline, including after zooming or rotating.
    internal double NavigationTop => GetVisibleTop(new Rect(Viewport).Deflate(PdfReaderLayout.Margin));
    public Rect PageBounds => GetPageBounds(PageNumber);
    public bool CanGoPrevious => DisplayMode == PdfReaderDisplayMode.PaperColumns
        ? _paperColumnIndex > 0 || PageNumber > 1
        : GetAdjacentPage(-1) != PageNumber;
    public bool CanGoNext => DisplayMode == PdfReaderDisplayMode.PaperColumns
        ? (_paperColumnIndex == 0 && HasDetectedPaperColumns) || PageNumber < PageCount
        : GetAdjacentPage(1) != PageNumber;
    internal int CachedPageCount => _pages.Count;
    internal long CachedRasterBytes => _pages.Values.Sum(page => (long)(page.Raster?.Width ?? 0) * (page.Raster?.Height ?? 0) * 4);
    public IReadOnlyList<int> VisiblePageNumbers => _layout?.VisiblePages(DocumentViewport).ToArray() ?? [];
    private double DeviceScaling => TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
    private Size Viewport => new(Math.Max(1, Bounds.Width), Math.Max(1, Bounds.Height));
    private Rect DocumentViewport => new(_pan.X, _pan.Y, Viewport.Width, Viewport.Height);

    private double GetVisibleTop(Rect viewport) => Math.Clamp(PageBounds.Intersect(viewport)
        .TransformToAABB(GetPageTransform(PageBounds).Invert()).Top / Math.Max(1, GetUnrotatedPageSize(PageBounds).Height), 0, 1);

    public event EventHandler<ReaderNavigationStartingEventArgs>? NavigationStarting;
    public event EventHandler<ReaderNavigationCompletedEventArgs>? NavigationCompleted;
    public event EventHandler<ReaderWebMessageReceivedEventArgs>? WebMessageReceived;

    public Task<string?> InvokeScriptAsync(string script) => Task.FromResult<string?>(null);
    public void Navigate(Uri uri) => _ = NavigateAsync(uri);

    public async Task<int> PrepareDocumentAsync(string path, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        path = Path.GetFullPath(path);
        if (_documentReady && _documentCancellation is { IsCancellationRequested: false }
            && string.Equals(_documentPath, path, StringComparison.Ordinal)) return PageCount;
        Stop();
        _documentCancellation?.Dispose();
        _documentCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _documentCancellation.Token;
        var version = ++_version;
        var count = await Task.Run(() =>
        {
            lock (_documentGate)
            {
                token.ThrowIfCancellationRequested();
                _document?.Dispose();
                _document = null;
                _documentPath = null;
                _document = new PdfDocumentService(path);
                _documentPath = path;
                return _document.PageCount;
            }
        }, token);
        token.ThrowIfCancellationRequested();
        if (_disposed || version != _version) throw new OperationCanceledException(token);
        foreach (var page in _pages.Values) page.Dispose();
        _pages.Clear();
        PageCount = count;
        PageNumber = 1;
        _paperColumnIndex = 0;
        _pointAnnotationMode = false;
        _regionSelectionMode = false;
        _regionPointerStart = null;
        _regionSelection = null;
        _sizes = Enumerable.Repeat(new Size(595, 842), count).ToArray();
        _layout = null;
        _pan = default;
        _annotations = [];
        // Publish readiness only after the page count and caches belong to
        // this load. Stop/cancellation must not leave a reusable half-session.
        _documentReady = true;
        return count;
    }

    public async Task<bool> NavigateAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var starting = new ReaderNavigationStartingEventArgs(uri);
        NavigationStarting?.Invoke(this, starting);
        if (starting.Cancel || !uri.IsFile) return false;
        try
        {
            await PrepareDocumentAsync(uri.LocalPath, cancellationToken);
            _navigation?.Cancel();
            _navigation?.Dispose();
            _navigation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _documentCancellation!.Token);
            var token = _navigation.Token;
            var version = ++_version;
            _viewportCancellation?.Cancel();
            ++_rasterVersion;
            var pageNumber = Math.Clamp(ReadTargetPage(uri), 1, PageCount);
            if (DisplayMode == PdfReaderDisplayMode.PaperColumns)
                _paperColumnIndex = 0;
            _pointAnnotationMode = false;
            _regionSelectionMode = false;
            _regionPointerStart = null;
            _regionSelection = null;
            LastError = null;
            var initialPages = DisplayMode == PdfReaderDisplayMode.TwoPage
                ? Enumerable.Range((pageNumber - 1) / 2 * 2 + 1, Math.Min(2, PageCount - (pageNumber - 1) / 2 * 2)).ToArray()
                : [pageNumber];
            await LoadPageContentsAsync(initialPages, version, token);
            token.ThrowIfCancellationRequested();
            if (_disposed || version != _version) return false;
            Source = uri;
            ActivatePage(pageNumber);
            RebuildLayout(preservePosition: false);
            ClearSearch();
            ClearSelection();
            _speechHighlight = null;
            ScrollToTop(ReadTargetTop(uri));
            await RefreshViewportAsync(token);
            token.ThrowIfCancellationRequested();
            if (_disposed || version != _version) return false;
            if (LastError is not null) throw new InvalidOperationException(LastError);
            EmitPage();
            NavigationCompleted?.Invoke(this, new(uri, true));
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception exception)
        {
            if (!_disposed)
            {
                LastError = exception.Message;
                NavigationCompleted?.Invoke(this, new(uri, false));
            }
            return false;
        }
    }

    public Task<PdfDocumentInfo> ReadDocumentInfoAsync(CancellationToken token) => Task.Run(() =>
    {
        lock (_documentGate)
        {
            token.ThrowIfCancellationRequested();
            return (_document ?? throw new ObjectDisposedException(nameof(NativePdfReaderHost))).ReadInfo(token);
        }
    }, token);

    public Task<IReadOnlyList<PdfPageText>> BuildTextIndexAsync(CancellationToken token)
    {
        var count = PageCount;
        var path = _documentPath;
        return Task.Run<IReadOnlyList<PdfPageText>>(async () =>
        {
            var result = new PdfPageText[count];
            long characters = 0;
            for (var page = 1; page <= count; page++)
            {
                token.ThrowIfCancellationRequested();
                var text = string.Empty;
                if (page <= 10_000 && characters < 20_000_000)
                {
                    lock (_documentGate)
                    {
                        token.ThrowIfCancellationRequested();
                        if (_disposed || _documentPath != path || _document is null) throw new OperationCanceledException(token);
                        text = _document.ReadPageText(page, token);
                    }
                }
                result[page - 1] = new(page, text);
                characters += text.Length;
                if (page % 16 != 0) continue;
                // Visible-page work takes priority without marshaling every
                // indexed page onto the UI thread or introducing timer delays.
                try { await _renderTask.WaitAsync(token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
                await Task.Yield();
            }
            return result;
        }, token);
    }

    public static int ReadTargetPage(Uri uri) => int.TryParse(ReadTargetValue(uri, "page"), out var page) ? Math.Max(1, page) : 1;
    public static double ReadTargetTop(Uri uri) => double.TryParse(ReadTargetValue(uri, "top"), NumberStyles.Float, CultureInfo.InvariantCulture, out var top)
        && double.IsFinite(top) ? Math.Clamp(top, 0, 1) : 0;
    public static int ReadTargetOffset(Uri uri) => int.TryParse(
        ReadTargetValue(uri, "offset"),
        NumberStyles.Integer,
        CultureInfo.InvariantCulture,
        out var offset)
            ? Math.Max(0, offset)
            : -1;
    private static string? ReadTargetValue(Uri uri, string key) => uri.Fragment.TrimStart('#').Split('&')
        .Select(part => part.Split('=', 2)).FirstOrDefault(parts => parts.Length == 2 && parts[0] == key)?[1];

    public PdfPageContent? GetPageContent(int page) => _pages.GetValueOrDefault(page)?.Content;
    public Rect GetPageBounds(int page) => _layout is not null && page >= 1 && page <= _layout.Pages.Length
        ? _layout.Pages[page - 1].Translate(-_pan) : new Rect(Viewport);
    internal PdfRasterRegion? GetRenderedRegion(int page) => _pages.GetValueOrDefault(page)?.Raster;

    internal PdfPageCrop GetPageCrop(int page)
    {
        if (DisplayMode != PdfReaderDisplayMode.PaperColumns)
            return PdfPageCrop.Full;
        var content = GetPageContent(page);
        if (content is null) return PdfPageCrop.Full;
        var columns = PdfPaperAnalysisService.DetectColumns(content);
        if (!columns.IsTwoColumn) return PdfPageCrop.Full;
        return _paperColumnIndex == 0 ? columns.Left : columns.Right;
    }

    internal PdfPageCrop GetRenderedPageCrop(int page)
    {
        var crop = GetPageCrop(page).Normalize();
        return Rotation switch
        {
            90 => new PdfPageCrop(
                1 - crop.Y - crop.Height,
                crop.X,
                crop.Height,
                crop.Width).Normalize(),
            180 => new PdfPageCrop(
                1 - crop.X - crop.Width,
                1 - crop.Y - crop.Height,
                crop.Width,
                crop.Height).Normalize(),
            270 => new PdfPageCrop(
                crop.Y,
                1 - crop.X - crop.Width,
                crop.Height,
                crop.Width).Normalize(),
            _ => crop
        };
    }

    public int GetAdjacentPage(int direction)
    {
        if (PageCount == 0 || direction == 0) return PageNumber;
        var first = DisplayMode == PdfReaderDisplayMode.TwoPage ? (PageNumber - 1) / 2 * 2 + 1 : PageNumber;
        var target = first + Math.Sign(direction) * (DisplayMode == PdfReaderDisplayMode.TwoPage ? 2 : 1);
        return target < 1 || target > PageCount ? PageNumber : target;
    }

    public async Task<bool> TurnPaperColumnAsync(int direction, CancellationToken cancellationToken = default)
    {
        if (DisplayMode != PdfReaderDisplayMode.PaperColumns || PageCount == 0)
            return false;
        direction = Math.Sign(direction);
        if (direction == 0) return false;

        if (direction > 0 && _paperColumnIndex == 0 && HasDetectedPaperColumns)
        {
            _paperColumnIndex = 1;
            ClearSelection();
            RebuildLayout(preservePosition: false);
            await RefreshViewportAsync(cancellationToken);
            Emit(new { type = "pdfLayout" });
            return true;
        }

        if (direction < 0 && _paperColumnIndex == 1)
        {
            _paperColumnIndex = 0;
            ClearSelection();
            RebuildLayout(preservePosition: false);
            await RefreshViewportAsync(cancellationToken);
            Emit(new { type = "pdfLayout" });
            return true;
        }

        var target = PageNumber + direction;
        if (target < 1 || target > PageCount) return false;
        _paperColumnIndex = 0;
        var source = new Uri(_documentPath!).AbsoluteUri + $"#page={target}";
        return await NavigateAsync(new Uri(source), cancellationToken);
    }

    public async Task EnsurePaperColumnForOffsetAsync(
        int offset,
        CancellationToken cancellationToken = default)
    {
        if (DisplayMode != PdfReaderDisplayMode.PaperColumns
            || GetPageContent(PageNumber) is not { } content)
            return;
        var glyph = content.Glyphs.FirstOrDefault(item => item.Offset + item.Length > offset);
        var columns = PdfPaperAnalysisService.DetectColumns(content);
        if (glyph is null || !columns.IsTwoColumn) return;
        var targetColumn = glyph.Bounds.X + glyph.Bounds.Width / 2
            >= columns.Right.X ? 1 : 0;
        if (targetColumn == _paperColumnIndex) return;
        _paperColumnIndex = targetColumn;
        ClearSelection();
        RebuildLayout(preservePosition: false);
        await RefreshViewportAsync(cancellationToken);
        Emit(new { type = "pdfLayout" });
    }

    public void SetAppearance(ReaderAppearanceSettings appearance)
    {
        var palette = ReaderPalette.For(appearance.Theme);
        if (_palette != palette)
        {
            _palette = palette;
            ++_rasterVersion;
            ScheduleRender();
        }
        ReaderAppearanceResources.PopulateControls(_vertical.Resources, appearance);
        ReaderAppearanceResources.PopulateControls(_horizontal.Resources, appearance);
        InvalidateVisual();
    }

    public async Task SetDisplayModeAsync(PdfReaderDisplayMode mode)
    {
        if (!Enum.IsDefined(mode)) return;
        var request = ++_layoutRequest;
        if ((mode is PdfReaderDisplayMode.TwoPage or PdfReaderDisplayMode.PaperColumns) && PageCount > 0)
        {
            var first = mode == PdfReaderDisplayMode.TwoPage
                ? (PageNumber - 1) / 2 * 2 + 1
                : PageNumber;
            var count = mode == PdfReaderDisplayMode.TwoPage
                ? Math.Min(2, PageCount - first + 1)
                : 1;
            await LoadPageContentsAsync(Enumerable.Range(first, count), _version, _documentCancellation!.Token);
            if (_disposed || request != _layoutRequest) return;
        }
        if (mode != PdfReaderDisplayMode.PaperColumns)
            _paperColumnIndex = 0;
        ClearSelection();
        _pointAnnotationMode = false;
        _regionSelectionMode = false;
        _regionPointerStart = null;
        _regionSelection = null;
        DisplayMode = mode;
        RebuildLayout(preservePosition: true);
        await RefreshViewportAsync();
        Emit(new { type = "pdfLayout" });
    }

    public async Task SetFitModeAsync(PdfReaderFitMode mode)
    {
        if (!Enum.IsDefined(mode)) return;
        ++_layoutRequest;
        FitMode = mode;
        Zoom = 1;
        RebuildLayout(preservePosition: true);
        await RefreshViewportAsync();
        Emit(new { type = "pdfLayout" });
    }

    public async Task SetZoomAsync(double zoom)
    {
        if (!double.IsFinite(zoom)) return;
        ++_layoutRequest;
        Zoom = Math.Clamp(zoom, 0.25, 8);
        RebuildLayout(preservePosition: true);
        Emit(new { type = "pdfZoom", zoom = Zoom });
        await RefreshViewportAsync();
    }

    public async Task RotateClockwiseAsync()
    {
        if (_disposed || _layout is null) return;
        ++_layoutRequest;
        var page = PageBounds;
        var size = GetUnrotatedPageSize(page);
        var center = GetPageTransform(page).Invert().Transform(new Point(Viewport.Width / 2, Viewport.Height / 2));
        var fraction = new Point(Math.Clamp(center.X / size.Width, 0, 1), Math.Clamp(center.Y / size.Height, 0, 1));
        Rotation = (Rotation + 90) % 360;
        ClearSelection();
        RebuildLayout(preservePosition: false);
        page = _layout.Pages[PageNumber - 1];
        size = GetUnrotatedPageSize(page);
        center = GetPageTransform(page).Transform(new Point(fraction.X * size.Width, fraction.Y * size.Height));
        SetPan(new(center.X - Viewport.Width / 2, center.Y - Viewport.Height / 2), trackPage: false, render: false);
        await RefreshViewportAsync();
        Emit(new { type = "pdfLayout" });
    }

    private double PagePanY => _layout is null ? 0 : Math.Max(0, _pan.Y - _layout.Pages[PageNumber - 1].Y + PdfReaderLayout.Margin);
    public string CaptureViewState() => string.Create(CultureInfo.InvariantCulture,
        $"pdf-view:{Zoom:R};{_pan.X / Math.Max(1, PageBounds.Width):R};{PagePanY / Math.Max(1, PageBounds.Height):R};{(int)DisplayMode};{(int)FitMode};{Rotation};{_paperColumnIndex}");

    public void RestoreViewSettings(string? state)
    {
        var values = ParseViewState(state);
        if (values is null) return;
        Zoom = Math.Clamp(values[0], 0.25, 8);
        DisplayMode = values.Length == 3 ? PdfReaderDisplayMode.SinglePage : (PdfReaderDisplayMode)(int)values[3];
        FitMode = values.Length == 3 ? PdfReaderFitMode.Width : (PdfReaderFitMode)(int)values[4];
        Rotation = values.Length > 5 ? (int)values[5] : 0;
        _paperColumnIndex = values.Length > 6 ? Math.Clamp((int)values[6], 0, 1) : 0;
    }

    public async Task RestoreViewStateAsync(string? state, int legacyScrollPosition = 0)
    {
        var values = ParseViewState(state);
        RestoreViewSettings(state);
        RebuildLayout(preservePosition: false);
        if (_layout is not null)
        {
            var page = _layout.Pages[PageNumber - 1];
            SetPan(values is null ? new(0, page.Y - PdfReaderLayout.Margin + Math.Max(0, legacyScrollPosition))
                : new(Math.Clamp(values[1], 0, 1) * page.Width,
                    page.Y - PdfReaderLayout.Margin + Math.Clamp(values[2], 0, 1) * page.Height), trackPage: false, render: false);
        }
        await RefreshViewportAsync();
    }

    private static double[]? ParseViewState(string? state)
    {
        var values = state?.StartsWith("pdf-view:", StringComparison.Ordinal) == true ? state[9..].Split(';') : [];
        if (values.Length is not (3 or 5 or 6 or 7)) return null;
        var parsed = new double[values.Length];
        for (var index = 0; index < values.Length; index++)
            if (!double.TryParse(values[index], NumberStyles.Float, CultureInfo.InvariantCulture, out parsed[index]) || !double.IsFinite(parsed[index])) return null;
        if (values.Length >= 5 && (parsed[3] != Math.Truncate(parsed[3]) || parsed[4] != Math.Truncate(parsed[4])
            || !Enum.IsDefined((PdfReaderDisplayMode)(int)parsed[3]) || !Enum.IsDefined((PdfReaderFitMode)(int)parsed[4]))) return null;
        if (values.Length == 6 && parsed[5] is not (0 or 90 or 180 or 270)) return null;
        if (values.Length == 7
            && (parsed[5] is not (0 or 90 or 180 or 270)
                || parsed[6] != Math.Truncate(parsed[6])
                || parsed[6] is < 0 or > 1)) return null;
        return parsed;
    }

    private void RebuildLayout(bool preservePosition)
    {
        if (_sizes.Length == 0 || _disposed) return;
        var fractionX = _pan.X / Math.Max(1, PageBounds.Width);
        var fractionY = PagePanY / Math.Max(1, PageBounds.Height);
        _layoutDpi = DeviceScaling;
        _layout = new(
            _sizes,
            Viewport,
            PageNumber,
            DisplayMode,
            FitMode,
            Zoom,
            _layoutDpi,
            Rotation,
            GetPageCrop(PageNumber));
        if (preservePosition)
        {
            var page = _layout.Pages[PageNumber - 1];
            _pan = new(fractionX * page.Width, page.Y - PdfReaderLayout.Margin + fractionY * page.Height);
        }
        SyncScrollBars();
        InvalidateArrange();
        InvalidateVisual();
    }

    private async Task LoadPageContentsAsync(IEnumerable<int> pages, int version, CancellationToken token)
    {
        var missing = pages.Where(page => page >= 1 && page <= PageCount && !_pages.ContainsKey(page)).Distinct().ToArray();
        if (missing.Length == 0) return;
        var loaded = await Task.Run(() =>
        {
            var result = new List<(int Page, PdfPageContent Content)>();
            foreach (var page in missing)
                lock (_documentGate)
                {
                    token.ThrowIfCancellationRequested();
                    if (_disposed || version != _version || _document is null) throw new OperationCanceledException(token);
                    result.Add((page, _document.ReadPage(page, token)));
                }
            return result;
        }, token);
        token.ThrowIfCancellationRequested();
        if (_disposed || version != _version) return;
        foreach (var (page, content) in loaded)
        {
            if (!_pages.ContainsKey(page)) _pages.Add(page, new(content));
            _sizes[page - 1] = new(content.Width, content.Height);
        }
    }

    private void ScheduleRender()
    {
        if (_disposed || _sizes.Length == 0 || _resizeTimer.IsEnabled) return;
        // Throttle, rather than debounce: a continuous trackpad gesture must
        // keep producing sharp frames while input is still arriving.
        _resizeTimer.Start();
    }

    public Task RefreshViewportAsync(CancellationToken token = default)
    {
        if (_disposed || _document is null || _layout is null) return Task.CompletedTask;
        _resizeTimer.Stop();
        _viewportCancellation?.Cancel();
        _viewportCancellation?.Dispose();
        _viewportCancellation = CancellationTokenSource.CreateLinkedTokenSource(token, _documentCancellation!.Token);
        _renderTask = RefreshViewportCoreAsync(_version, ++_rasterVersion, _viewportCancellation.Token);
        return _renderTask;
    }

    private async Task RefreshViewportCoreAsync(int version, int rasterVersion, CancellationToken token)
    {
        var rotation = Rotation;
        var palette = _palette;
        try
        {
            // Unknown page dimensions are estimated until that page enters the viewport.
            // Reflow keeps the current page and its local reading position anchored.
            for (var pass = 0; pass < 4; pass++)
            {
                var needed = VisiblePageNumbers.Append(PageNumber).Distinct().ToArray();
                if (needed.All(_pages.ContainsKey)) break;
                await LoadPageContentsAsync(needed, version, token);
                token.ThrowIfCancellationRequested();
                if (version != _version || rasterVersion != _rasterVersion) return;
                RebuildLayout(preservePosition: true);
            }
            var requests = new List<(int Page, int Width, int Height, int X, int Y, int W, int H)>();
            var visible = VisiblePageNumbers;
            foreach (var page in visible)
            {
                if (!_pages.TryGetValue(page, out var cached)) continue;
                var bounds = GetPageBounds(page);
                var crop = GetRenderedPageCrop(page);
                var fullSize = _sizes[page - 1];
                if (Rotation % 180 != 0) fullSize = new(fullSize.Height, fullSize.Width);
                var cropWidth = Math.Max(1, fullSize.Width * crop.Width);
                var cropHeight = Math.Max(1, fullSize.Height * crop.Height);
                var scale = Math.Max(
                    bounds.Width / cropWidth,
                    bounds.Height / cropHeight);
                var width = Math.Max(1, (int)Math.Round(fullSize.Width * scale * _layoutDpi));
                var height = Math.Max(1, (int)Math.Round(fullSize.Height * scale * _layoutDpi));
                var intersection = bounds.Intersect(new Rect(Viewport));
                if (intersection.Width <= 0 || intersection.Height <= 0) continue;
                var left = Math.Clamp(
                    (int)Math.Floor(crop.X * width + (intersection.X - bounds.X) * _layoutDpi),
                    0,
                    width - 1);
                var top = Math.Clamp(
                    (int)Math.Floor(crop.Y * height + (intersection.Y - bounds.Y) * _layoutDpi),
                    0,
                    height - 1);
                var right = Math.Clamp(
                    (int)Math.Ceiling(crop.X * width + (intersection.Right - bounds.X) * _layoutDpi),
                    left + 1,
                    Math.Max(left + 1, (int)Math.Ceiling((crop.X + crop.Width) * width)));
                var bottom = Math.Clamp(
                    (int)Math.Ceiling(crop.Y * height + (intersection.Bottom - bounds.Y) * _layoutDpi),
                    top + 1,
                    Math.Max(top + 1, (int)Math.Ceiling((crop.Y + crop.Height) * height)));
                if (cached.Raster is { } raster && raster.Rotation == rotation && cached.Palette == palette
                    && raster.PagePixelWidth == width && raster.PagePixelHeight == height
                    && raster.Left <= left && raster.Top <= top && raster.Left + raster.Width >= right && raster.Top + raster.Height >= bottom) continue;
                // A small overscan region absorbs several wheel ticks without repainting.
                var padding = Math.Min(160, (int)(96 * _layoutDpi));
                left = Math.Max(0, left - padding); top = Math.Max(0, top - padding);
                right = Math.Min(width, right + padding); bottom = Math.Min(height, bottom + padding);
                requests.Add((page, width, height, left, top, right - left, bottom - top));
            }
            foreach (var request in requests)
            {
                var raster = await Task.Run(() =>
                {
                    lock (_documentGate)
                    {
                        token.ThrowIfCancellationRequested();
                        if (_disposed || version != _version || rasterVersion != _rasterVersion || _document is null) throw new OperationCanceledException(token);
                        return _document.RenderRegion(request.Page, request.Width, request.Height, request.X, request.Y, request.W, request.H, token, rotation);
                    }
                }, token);
                token.ThrowIfCancellationRequested();
                if (_disposed || version != _version || rasterVersion != _rasterVersion) return;
                ReplaceBitmap(_pages[request.Page], raster, palette);
            }
            var keep = visible.Append(PageNumber).ToHashSet();
            foreach (var page in _pages.Keys.Where(page => !keep.Contains(page)).OrderByDescending(page => Math.Abs(page - PageNumber)).ToArray())
            {
                if (_pages.Count <= Math.Max(6, keep.Count) && CachedRasterBytes < 96_000_000) break;
                _pages[page].Dispose();
                _pages.Remove(page);
            }
            if (!string.IsNullOrEmpty(_searchQuery)) RebuildSearch(_searchQuery);
            EmitPage();
            InvalidateVisual();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (!_disposed && version == _version && rasterVersion == _rasterVersion)
            {
                LastError = exception.Message;
                Emit(new { type = "pdfError", message = LastError });
            }
        }
    }

    private static void ReplaceBitmap(CachedPage page, PdfRasterRegion raster, ReaderPalette palette)
    {
        ApplyPageColors(raster.Pixels, palette);
        var bitmap = new WriteableBitmap(new PixelSize(raster.Width, raster.Height), new Vector(96, 96), PixelFormats.Bgra8888, AlphaFormat.Opaque);
        using (var frame = bitmap.Lock())
        {
            if (frame.RowBytes == raster.Width * 4) Marshal.Copy(raster.Pixels, 0, frame.Address, raster.Pixels.Length);
            else for (var row = 0; row < raster.Height; row++)
                Marshal.Copy(raster.Pixels, row * raster.Width * 4, IntPtr.Add(frame.Address, row * frame.RowBytes), raster.Width * 4);
        }
        page.Bitmap?.Dispose();
        page.Bitmap = bitmap;
        page.Palette = palette;
        // Keep geometry for cache reuse, not a second copy of the pixel buffer.
        page.Raster = raster with { Pixels = [] };
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _vertical.Measure(new Size(12, Math.Max(0, availableSize.Height)));
        _horizontal.Measure(new Size(Math.Max(0, availableSize.Width), 12));
        return base.MeasureOverride(availableSize);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        SyncScrollBars();
        _vertical.Arrange(new Rect(Math.Max(0, finalSize.Width - 12), 0, 12, Math.Max(0, finalSize.Height - 12)));
        _horizontal.Arrange(new Rect(0, Math.Max(0, finalSize.Height - 12), Math.Max(0, finalSize.Width - 12), 12));
        return finalSize;
    }

    private void SyncScrollBars()
    {
        _syncingBars = true;
        try
        {
            _vertical.Maximum = Math.Max(0, (_layout?.Extent.Height ?? 0) - Viewport.Height);
            _horizontal.Maximum = Math.Max(0, (_layout?.Extent.Width ?? 0) - Viewport.Width);
            _vertical.ViewportSize = Viewport.Height;
            _horizontal.ViewportSize = Viewport.Width;
            var dpi = DeviceScaling;
            _pan = new(Math.Round(Math.Clamp(_pan.X, 0, _horizontal.Maximum) * dpi) / dpi,
                Math.Round(Math.Clamp(_pan.Y, 0, _vertical.Maximum) * dpi) / dpi);
            _vertical.Value = _pan.Y;
            _horizontal.Value = _pan.X;
            _vertical.IsVisible = _vertical.Maximum > 1;
            _horizontal.IsVisible = _horizontal.Maximum > 1;
        }
        finally { _syncingBars = false; }
    }

    private void SetPan(Vector pan, bool trackPage = true, bool render = true)
    {
        _pan = pan;
        SyncScrollBars();
        if (trackPage && DisplayMode == PdfReaderDisplayMode.Continuous && _layout is not null)
            ActivatePage(_layout.PageAt(_pan.Y + PdfReaderLayout.Margin + 1));
        InvalidateArrange();
        ClearHover();
        InvalidateVisual();
        EmitPage();
        if (render) ScheduleRender();
    }

    private void ActivatePage(int page)
    {
        page = Math.Clamp(page, 1, Math.Max(1, PageCount));
        if (PageNumber == page) return;
        PageNumber = page;
        if (_documentPath is not null) Source = new Uri(new Uri(_documentPath).AbsoluteUri + $"#page={page}");
        ClearSelection();
        _speechHighlight = null;
        RebuildSearch(_searchQuery);
        EmitPage();
    }

    public void ScrollBy(double pixels)
    {
        ScrollBarAutoHide.Show(_vertical);
        SetPan(new(_pan.X, _pan.Y + pixels));
    }
    public void ScrollToTop(double top)
    {
        if (_layout is null) return;
        var page = _layout.Pages[PageNumber - 1];
        top = Math.Clamp(top, 0, 1);
        var margin = PdfReaderLayout.Margin;
        SetPan(Rotation switch
        {
            90 => new(page.Right + margin - top * page.Width - Viewport.Width, page.Y - margin),
            180 => new(_pan.X, page.Bottom + margin - top * page.Height - Viewport.Height),
            270 => new(page.X - margin + top * page.Width, page.Y - margin),
            _ => new(_pan.X, page.Y - margin + top * page.Height)
        }, trackPage: false);
    }

    private void EmitPage()
    {
        if (_layout is null) return;
        Emit(new { type = "pdfPage", page = PageNumber, top = PagePanY, left = _pan.X,
            scrollWidth = _layout.Extent.Width, scrollHeight = PageBounds.Height + PdfReaderLayout.Margin * 2,
            clientWidth = Viewport.Width, clientHeight = Viewport.Height });
    }

    private void Emit(object message)
    {
        if (!_disposed) WebMessageReceived?.Invoke(this, new ReaderWebMessageReceivedEventArgs(JsonSerializer.Serialize(message)));
    }

    public Task<byte[]?> CaptureVisiblePageAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_pages.Count == 0 || Bounds.Width < 1 || Bounds.Height < 1) return Task.FromResult<byte[]?>(null);
        var dpi = DeviceScaling;
        using var capture = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(Bounds.Width * dpi), (int)Math.Ceiling(Bounds.Height * dpi)), new Vector(96 * dpi, 96 * dpi));
        capture.Render(this);
        using var stream = new MemoryStream();
        capture.Save(stream, PngBitmapEncoderOptions.Default);
        return Task.FromResult<byte[]?>(stream.ToArray());
    }

    public async Task<byte[]?> CaptureRegionPngAsync(
        int pageNumber,
        PdfPageCrop region,
        CancellationToken cancellationToken = default)
    {
        if (pageNumber < 1 || pageNumber > PageCount)
            return null;
        if (!_pages.ContainsKey(pageNumber))
            await LoadPageContentsAsync([pageNumber], _version, _documentCancellation?.Token ?? cancellationToken);
        if (GetPageContent(pageNumber) is null || _document is null)
            return null;

        region = TransformRegionForView(region.Normalize());
        var fullSize = _sizes[pageNumber - 1];
        if (Rotation % 180 != 0) fullSize = new(fullSize.Height, fullSize.Width);
        var scale = Math.Min(
            3d,
            Math.Sqrt(12_000_000d / Math.Max(1, fullSize.Width * fullSize.Height)));
        scale = Math.Max(1, scale);
        var fullWidth = Math.Max(1, (int)Math.Ceiling(fullSize.Width * scale));
        var fullHeight = Math.Max(1, (int)Math.Ceiling(fullSize.Height * scale));
        var left = Math.Clamp((int)Math.Floor(region.X * fullWidth), 0, fullWidth - 1);
        var top = Math.Clamp((int)Math.Floor(region.Y * fullHeight), 0, fullHeight - 1);
        var right = Math.Clamp((int)Math.Ceiling((region.X + region.Width) * fullWidth), left + 1, fullWidth);
        var bottom = Math.Clamp((int)Math.Ceiling((region.Y + region.Height) * fullHeight), top + 1, fullHeight);
        var width = right - left;
        var height = bottom - top;

        var raster = await Task.Run(() =>
        {
            lock (_documentGate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_disposed || _document is null) return null;
                return _document.RenderRegion(
                    pageNumber,
                    fullWidth,
                    fullHeight,
                    left,
                    top,
                    width,
                    height,
                    cancellationToken,
                    Rotation);
            }
        }, cancellationToken);
        if (raster is null) return null;

        var bitmap = new WriteableBitmap(
            new PixelSize(raster.Width, raster.Height),
            new Vector(96, 96),
            PixelFormats.Bgra8888,
            AlphaFormat.Opaque);
        using (var frame = bitmap.Lock())
        {
            if (frame.RowBytes == raster.Width * 4)
                Marshal.Copy(raster.Pixels, 0, frame.Address, raster.Pixels.Length);
            else
                for (var row = 0; row < raster.Height; row++)
                    Marshal.Copy(
                        raster.Pixels,
                        row * raster.Width * 4,
                        IntPtr.Add(frame.Address, row * frame.RowBytes),
                        raster.Width * 4);
        }
        await using var stream = new MemoryStream();
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        bitmap.Dispose();
        return stream.ToArray();
    }

    private PdfPageCrop TransformRegionForView(PdfPageCrop region) => Rotation switch
    {
        90 => new PdfPageCrop(
            1 - region.Y - region.Height,
            region.X,
            region.Height,
            region.Width).Normalize(),
        180 => new PdfPageCrop(
            1 - region.X - region.Width,
            1 - region.Y - region.Height,
            region.Width,
            region.Height).Normalize(),
        270 => new PdfPageCrop(
            region.Y,
            1 - region.X - region.Width,
            region.Height,
            region.Width).Normalize(),
        _ => region
    };

    public void Stop()
    {
        _documentReady = false;
        _navigation?.Cancel();
        _viewportCancellation?.Cancel();
        _documentCancellation?.Cancel();
        ++_version; ++_rasterVersion;
        _resizeTimer.Stop();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _navigation?.Dispose();
        _viewportCancellation?.Dispose();
        _documentCancellation?.Dispose();
        foreach (var page in _pages.Values) page.Dispose();
        _pages.Clear();
        lock (_documentGate) { _document?.Dispose(); _document = null; }
    }
}
