using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle;

/// <summary>Fixed-layout PDF pages with the same selection/annotation bridge as EPUB.</summary>
public sealed class NativePdfReaderHost : Control, IReaderHost, IReaderPageSnapshotProvider
{
    private readonly object _documentGate = new();
    private readonly ScrollBar _vertical = new() { Orientation = Orientation.Vertical };
    private readonly ScrollBar _horizontal = new() { Orientation = Orientation.Horizontal };
    private readonly DispatcherTimer _resizeTimer;
    private PdfDocumentService? _document;
    private string? _documentPath;
    private CancellationTokenSource? _navigation;
    private Bitmap? _bitmap;
    private PdfPageContent? _content;
    private IReadOnlyList<ReaderAnnotation> _annotations = [];
    private ReaderPalette _palette = ReaderPalette.For(ReaderTheme.Classic);
    private int _version;
    private int _rasterVersion;
    private bool _disposed;
    private bool _syncingBars;
    private Vector _pan;
    private Point? _pointerStart;
    private int _anchorOffset = -1;
    private int _selectionStart;
    private int _selectionEnd;
    private bool _dragging;
    private Guid? _hoveredAnnotation;
    private readonly List<(int Start, int Length)> _search = [];
    private int _searchIndex = -1;
    private (int Start, int Length)? _speechHighlight;

    public NativePdfReaderHost()
    {
        Focusable = true;
        ClipToBounds = true;
        VisualChildren.Add(_vertical);
        VisualChildren.Add(_horizontal);
        _vertical.ValueChanged += (_, _) => { if (!_syncingBars) SetPan(new(_pan.X, _vertical.Value)); };
        _horizontal.ValueChanged += (_, _) => { if (!_syncingBars) SetPan(new(_horizontal.Value, _pan.Y)); };
        _resizeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _resizeTimer.Tick += async (_, _) => { _resizeTimer.Stop(); await RefreshRasterAsync(); };
        PropertyChanged += (_, args) =>
        {
            if (args.Property != BoundsProperty) return;
            InvalidateVisual();
            _resizeTimer.Stop();
            if (!_disposed && _content is not null) _resizeTimer.Start();
        };
        PointerCaptureLost += (_, _) => { _pointerStart = null; _dragging = false; };
    }

    public object View => this;
    public Uri? Source { get; private set; }
    public Task ReadyTask => Task.CompletedTask;
    public int PageNumber { get; private set; } = 1;
    public int PageCount { get; private set; }
    public double Zoom { get; private set; } = 1;
    public string? LastError { get; private set; }
    public PdfPageContent? PageContent => _content;
    public double VisibleTop => _content is null ? 0 : Math.Clamp(-PageBounds.Y / PageBounds.Height, 0, 1);
    public event EventHandler<ReaderNavigationStartingEventArgs>? NavigationStarting;
    public event EventHandler<ReaderNavigationCompletedEventArgs>? NavigationCompleted;
    public event EventHandler<ReaderWebMessageReceivedEventArgs>? WebMessageReceived;

    public Task<string?> InvokeScriptAsync(string script) => Task.FromResult<string?>(null);
    public void Navigate(Uri uri) => _ = NavigateAsync(uri);

    public async Task<bool> NavigateAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var starting = new ReaderNavigationStartingEventArgs(uri);
        NavigationStarting?.Invoke(this, starting);
        if (starting.Cancel || !uri.IsFile) return false;
        _navigation?.Cancel();
        _navigation?.Dispose();
        _navigation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _navigation.Token;
        var version = ++_version;
        ++_rasterVersion;
        var requestedPage = ReadTargetPage(uri);
        var resolution = RasterResolution();
        LastError = null;
        try
        {
            var rendered = await Task.Run(() =>
            {
                lock (_documentGate)
                {
                    token.ThrowIfCancellationRequested();
                    if (_document is null || !string.Equals(_documentPath, uri.LocalPath, StringComparison.Ordinal))
                    {
                        _document?.Dispose();
                        _document = null;
                        _documentPath = null;
                        _document = new PdfDocumentService(uri.LocalPath);
                        _documentPath = uri.LocalPath;
                    }
                    return (_document.RenderPage(Math.Clamp(requestedPage, 1, _document.PageCount),
                        resolution.Width, resolution.Height, token), _document.PageCount);
                }
            }, token);
            if (_disposed || version != _version || token.IsCancellationRequested) return false;
            Source = uri;
            PageNumber = rendered.Item1.PageNumber;
            PageCount = rendered.PageCount;
            _content = rendered.Item1.Content;
            ReplaceBitmap(rendered.Item1.Png);
            _pan = default;
            _annotations = [];
            _speechHighlight = null;
            ClearSearch();
            ClearSelection();
            ScrollToTop(ReadTargetTop(uri));
            InvalidateArrange();
            EmitPage();
            NavigationCompleted?.Invoke(this, new(uri, true));
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return false; }
        catch (Exception exception)
        {
            if (!_disposed && version == _version)
            {
                LastError = exception.Message;
                NavigationCompleted?.Invoke(this, new(uri, false));
            }
            return false;
        }
    }

    public static int ReadTargetPage(Uri uri) =>
        int.TryParse(ReadTargetValue(uri, "page"), out var page) ? Math.Max(1, page) : 1;

    public static double ReadTargetTop(Uri uri) =>
        double.TryParse(ReadTargetValue(uri, "top"), NumberStyles.Float, CultureInfo.InvariantCulture, out var top)
            && double.IsFinite(top) ? Math.Clamp(top, 0, 1) : 0;

    private static string? ReadTargetValue(Uri uri, string key) => uri.Fragment.TrimStart('#').Split('&')
        .Select(part => part.Split('=', 2)).FirstOrDefault(parts => parts.Length == 2 && parts[0] == key)?[1];

    public void SetAppearance(ReaderAppearanceSettings appearance)
    {
        _palette = ReaderPalette.For(appearance.Theme);
        InvalidateVisual();
    }

    public async Task SetZoomAsync(double zoom)
    {
        if (!double.IsFinite(zoom)) return;
        var previousBounds = PageBounds;
        var center = new Point((Bounds.Width / 2 - previousBounds.X) / Math.Max(1, previousBounds.Width),
            (Bounds.Height / 2 - previousBounds.Y) / Math.Max(1, previousBounds.Height));
        Zoom = Math.Clamp(zoom, 0.5, 4);
        var nextBounds = PageBounds;
        SetPan(new(center.X * nextBounds.Width - Bounds.Width / 2 + 20,
            center.Y * nextBounds.Height - Bounds.Height / 2 + 16));
        Emit(new { type = "pdfZoom", zoom = Zoom });
        await RefreshRasterAsync();
    }

    public void SetAnnotations(IReadOnlyList<ReaderAnnotation> annotations)
    {
        _annotations = annotations.ToArray();
        ClearHover();
        InvalidateVisual();
    }

    public string CaptureViewState() => string.Create(CultureInfo.InvariantCulture,
        $"pdf-view:{Zoom:R};{_pan.X / Math.Max(1, PageBounds.Width):R};{_pan.Y / Math.Max(1, PageBounds.Height):R}");

    public async Task RestoreViewStateAsync(string? state, int legacyScrollPosition = 0)
    {
        var values = state?.StartsWith("pdf-view:", StringComparison.Ordinal) == true ? state[9..].Split(';') : [];
        var parsed = new double[3];
        if (values.Length == 3 && values.Select((value, index) =>
                double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed[index]) && double.IsFinite(parsed[index])).All(valid => valid))
        {
            await SetZoomAsync(parsed[0]);
            SetPan(new(Math.Clamp(parsed[1], 0, 1) * PageBounds.Width, Math.Clamp(parsed[2], 0, 1) * PageBounds.Height));
        }
        else SetPan(new(0, Math.Max(0, legacyScrollPosition)));
    }

    public void ClearSelection()
    {
        _selectionStart = _selectionEnd = 0;
        InvalidateVisual();
        EmitSelection();
    }

    public void SelectRange(int start, int end)
    {
        var text = _content?.Text ?? string.Empty;
        start = Math.Clamp(start, 0, text.Length);
        end = Math.Clamp(end, start, text.Length);
        while (start < end && char.IsWhiteSpace(text[start])) start++;
        while (end > start && char.IsWhiteSpace(text[end - 1])) end--;
        _selectionStart = start;
        _selectionEnd = end;
        InvalidateVisual();
        EmitSelection();
    }

    public (int Count, int Index) Find(string query, int? offset = null)
    {
        _search.Clear();
        _searchIndex = -1;
        var text = _content?.Text ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(query))
        {
            for (var start = 0; start < text.Length;)
            {
                var match = text.IndexOf(query, start, StringComparison.CurrentCultureIgnoreCase);
                if (match < 0) break;
                _search.Add((match, query.Length));
                start = match + query.Length;
            }
            if (_search.Count > 0)
            {
                _searchIndex = offset is { } target ? Math.Max(0, _search.FindIndex(hit => hit.Start >= target)) : 0;
                ScrollToOffset(_search[_searchIndex].Start);
            }
        }
        InvalidateVisual();
        return (_search.Count, _searchIndex);
    }

    public (int Count, int Index) NextSearch(int direction)
    {
        if (_search.Count > 0)
        {
            _searchIndex = (_searchIndex + Math.Sign(direction) + _search.Count) % _search.Count;
            ScrollToOffset(_search[_searchIndex].Start);
            InvalidateVisual();
        }
        return (_search.Count, _searchIndex);
    }

    public void ClearSearch() { _search.Clear(); _searchIndex = -1; InvalidateVisual(); }

    public void SetSpeechHighlight(int start, int length)
    {
        _speechHighlight = (start, length);
        ScrollToOffset(start);
        InvalidateVisual();
    }

    public void ClearSpeechHighlight() { _speechHighlight = null; InvalidateVisual(); }

    public void ScrollToSearchHit(int index)
    {
        if (_search.Count == 0) return;
        _searchIndex = (index % _search.Count + _search.Count) % _search.Count;
        ScrollToOffset(_search[_searchIndex].Start);
        InvalidateVisual();
    }

    public void ScrollToOffset(int offset)
    {
        var glyph = _content?.Glyphs.FirstOrDefault(item => item.Offset + item.Length > offset);
        if (glyph is null) return;
        var bounds = PageBounds;
        SetPan(new(glyph.Bounds.X * bounds.Width - Bounds.Width * 0.2,
            glyph.Bounds.Y * bounds.Height - Bounds.Height * 0.25));
    }

    public void ScrollToTop(double top) => SetPan(new(_pan.X, Math.Clamp(top, 0, 1) * PageBounds.Height));

    public Rect PageBounds
    {
        get
        {
            if (_content is null) return new Rect(0, 0, Math.Max(1, Bounds.Width), Math.Max(1, Bounds.Height));
            var fit = Math.Min(Math.Max(1, Bounds.Width - 40) / _content.Width,
                Math.Max(1, Bounds.Height - 32) / _content.Height);
            var width = _content.Width * fit * Zoom;
            var height = _content.Height * fit * Zoom;
            return new Rect(Math.Max(20, (Bounds.Width - width) / 2) - _pan.X,
                Math.Max(16, (Bounds.Height - height) / 2) - _pan.Y, width, height);
        }
    }

    public IReadOnlyList<Rect> GetRangeBounds(int start, int end)
    {
        if (_content is null || end <= start) return [];
        var page = PageBounds;
        var rectangles = new List<Rect>();
        foreach (var glyph in _content.Glyphs)
        {
            if (glyph.Offset >= end) break;
            if (glyph.Offset + glyph.Length <= start) continue;
            var box = glyph.Bounds;
            var rect = new Rect(page.X + box.X * page.Width, page.Y + box.Y * page.Height,
                box.Width * page.Width, box.Height * page.Height);
            if (rectangles.Count > 0)
            {
                var previous = rectangles[^1];
                var sameLine = Math.Abs(previous.Center.Y - rect.Center.Y) < Math.Max(previous.Height, rect.Height) * 0.45
                    && rect.Left - previous.Right < Math.Max(previous.Height, rect.Height) * 2
                    && rect.Left >= previous.Left - 1;
                var sameVerticalLine = Math.Abs(previous.Center.X - rect.Center.X) < Math.Max(previous.Width, rect.Width) * 0.4
                    && rect.Top - previous.Bottom < Math.Max(previous.Width, rect.Width) * 2 && rect.Top >= previous.Top;
                if (sameLine || sameVerticalLine) { rectangles[^1] = previous.Union(rect); continue; }
            }
            rectangles.Add(rect);
        }
        return rectangles;
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(new SolidColorBrush(_palette.Sidebar), new Rect(Bounds.Size));
        if (_bitmap is null) return;
        var page = PageBounds;
        context.DrawRectangle(Brushes.White, new Pen(new SolidColorBrush(_palette.Border), 1), page);
        context.DrawImage(_bitmap, new Rect(_bitmap.Size), page);
        foreach (var annotation in _annotations)
        {
            if (annotation.EndOffset <= annotation.StartOffset) continue;
            var color = Color.TryParse(annotation.Color, out var parsed) ? parsed : Colors.Black;
            foreach (var rect in GetRangeBounds(annotation.StartOffset, annotation.EndOffset))
                DrawAnnotation(context, rect, annotation.UnderlineStyle, color);
        }
        for (var index = 0; index < _search.Count; index++)
            foreach (var rect in GetRangeBounds(_search[index].Start, _search[index].Start + _search[index].Length))
                context.FillRectangle(new SolidColorBrush(Color.FromArgb(index == _searchIndex ? (byte)140 : (byte)70, 255, 200, 50)), rect.Inflate(1));
        foreach (var rect in GetRangeBounds(_selectionStart, _selectionEnd))
            context.FillRectangle(new SolidColorBrush(Color.FromArgb(85, 66, 133, 210)), rect.Inflate(1));
        if (_speechHighlight is { } speech)
            foreach (var rect in GetRangeBounds(speech.Start, speech.Start + speech.Length))
                context.FillRectangle(new SolidColorBrush(Color.FromArgb(65, 100, 160, 95)), rect.Inflate(1));
    }

    private static void DrawAnnotation(DrawingContext context, Rect rect, string style, Color color)
    {
        if (style == "marker")
        {
            context.FillRectangle(new SolidColorBrush(Color.FromArgb(55, color.R, color.G, color.B)), rect.Inflate(1));
            return;
        }
        var brush = new SolidColorBrush(color);
        var vertical = rect.Height > rect.Width * 2;
        var first = vertical ? new Point(rect.Right + 2, rect.Top) : new Point(rect.Left, rect.Bottom + 2);
        var last = vertical ? new Point(first.X, rect.Bottom) : new Point(rect.Right, first.Y);
        var pen = new Pen(brush, 1.4, style == "dashed" ? DashStyle.Dash : style == "dotted" ? DashStyle.Dot : null);
        if (style == "wavy")
        {
            var length = vertical ? rect.Height : rect.Width;
            var previous = first;
            for (double along = 2; along <= length + 2; along += 2)
            {
                var distance = Math.Min(along, length);
                var wave = Math.Sin(distance * Math.PI / 4) * 1.5;
                var next = vertical ? new Point(first.X + wave, first.Y + distance) : new Point(first.X + distance, first.Y + wave);
                context.DrawLine(pen, previous, next);
                previous = next;
            }
        }
        else
        {
            context.DrawLine(pen, first, last);
            if (style == "double")
            {
                var shift = vertical ? new Vector(3, 0) : new Vector(0, 3);
                context.DrawLine(pen, first + shift, last + shift);
            }
        }
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
        var page = PageBounds;
        _syncingBars = true;
        try
        {
            _vertical.Maximum = Math.Max(0, page.Height + 32 - Bounds.Height);
            _horizontal.Maximum = Math.Max(0, page.Width + 40 - Bounds.Width);
            _vertical.ViewportSize = Bounds.Height;
            _horizontal.ViewportSize = Bounds.Width;
            _pan = new(Math.Clamp(_pan.X, 0, _horizontal.Maximum), Math.Clamp(_pan.Y, 0, _vertical.Maximum));
            _vertical.Value = _pan.Y;
            _horizontal.Value = _pan.X;
            _vertical.IsVisible = _vertical.Maximum > 1;
            _horizontal.IsVisible = _horizontal.Maximum > 1;
        }
        finally { _syncingBars = false; }
    }

    private void SetPan(Vector pan)
    {
        _pan = pan;
        SyncScrollBars();
        InvalidateArrange();
        ClearHover();
        InvalidateVisual();
        EmitPage();
    }

    private int HitOffset(Point point, bool nearest)
    {
        if (_content is null || !nearest && !PageBounds.Contains(point)) return -1;
        var page = PageBounds;
        PdfTextGlyph? best = null;
        var bestDistance = double.MaxValue;
        foreach (var glyph in _content.Glyphs)
        {
            var bounds = glyph.Bounds;
            var rect = new Rect(page.X + bounds.X * page.Width, page.Y + bounds.Y * page.Height,
                bounds.Width * page.Width, bounds.Height * page.Height);
            var dx = Math.Max(0, Math.Max(rect.Left - point.X, point.X - rect.Right));
            var dy = Math.Max(0, Math.Max(rect.Top - point.Y, point.Y - rect.Bottom));
            var distance = dx * dx + dy * dy;
            if (distance >= bestDistance) continue;
            best = glyph;
            bestDistance = distance;
        }
        if (best is null || !nearest && bestDistance > 64) return -1;
        var centerX = page.X + (best.Bounds.X + best.Bounds.Width / 2) * page.Width;
        var centerY = page.Y + (best.Bounds.Y + best.Bounds.Height / 2) * page.Height;
        var after = (point.X - centerX) * best.AdvanceX * page.Width + (point.Y - centerY) * best.AdvanceY * page.Height > 0;
        return best.Offset + (after ? best.Length : 0);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.Source != this || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        Focus();
        var point = e.GetPosition(this);
        ClearHover();
        _anchorOffset = HitOffset(point, false);
        _pointerStart = point;
        _dragging = false;
        if (_anchorOffset >= 0 && e.ClickCount >= 2 && _content is { } content)
        {
            var start = Math.Min(_anchorOffset, Math.Max(0, content.Text.Length - 1));
            var end = start;
            while (start > 0 && !char.IsWhiteSpace(content.Text[start - 1])) start--;
            while (end < content.Text.Length && !char.IsWhiteSpace(content.Text[end])) end++;
            _pointerStart = null;
            SelectRange(start, end);
        }
        else e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetPosition(this);
        if (_pointerStart is { } start && _anchorOffset >= 0)
        {
            _dragging |= Math.Abs(point.X - start.X) + Math.Abs(point.Y - start.Y) > 3;
            if (_dragging)
            {
                var hit = HitOffset(point, true);
                if (hit >= 0) { _selectionStart = Math.Min(_anchorOffset, hit); _selectionEnd = Math.Max(_anchorOffset, hit); InvalidateVisual(); }
            }
            return;
        }
        var annotation = AnnotationAt(point);
        Cursor = new Cursor(annotation is not null ? StandardCursorType.Hand : HitOffset(point, false) >= 0 ? StandardCursorType.Ibeam : StandardCursorType.Arrow);
        if (annotation is not null && !string.IsNullOrWhiteSpace(annotation.Note))
        {
            if (_hoveredAnnotation != annotation.Id)
            {
                _hoveredAnnotation = annotation.Id;
                Emit(new { type = "annotationHover", id = annotation.Id, note = annotation.Note, x = point.X, y = point.Y });
            }
        }
        else ClearHover();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_pointerStart is null) return;
        var dragged = _dragging;
        _pointerStart = null;
        _dragging = false;
        e.Pointer.Capture(null);
        if (dragged) SelectRange(_selectionStart, _selectionEnd);
        else if (AnnotationAt(e.GetPosition(this)) is { } annotation)
            Emit(new { type = "annotationClick", id = annotation.Id });
        else ClearSelection();
        e.Handled = true;
    }

    protected override void OnPointerExited(PointerEventArgs e) { base.OnPointerExited(e); ClearHover(); }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        e.Handled = true;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta))
        {
            _ = SetZoomAsync(Zoom + Math.Sign(e.Delta.Y) * 0.1);
            return;
        }
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) || Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y))
        {
            SetPan(new(_pan.X - (e.Delta.X != 0 ? e.Delta.X : e.Delta.Y) * 60, _pan.Y));
            return;
        }
        var direction = -Math.Sign(e.Delta.Y);
        if (direction == 0) return;
        if (direction > 0 && _pan.Y < _vertical.Maximum - 1 || direction < 0 && _pan.Y > 1)
            SetPan(new(_pan.X, _pan.Y - e.Delta.Y * 60));
        else Emit(new { type = "page", direction });
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta))
        {
            if (e.Key == Key.A) { SelectRange(0, _content?.Text.Length ?? 0); e.Handled = true; }
            else if (e.Key == Key.C && _selectionEnd > _selectionStart) { Emit(new { type = "selectionAction", action = "copy" }); e.Handled = true; }
            else if (e.Key == Key.D0 || e.Key == Key.NumPad0) { _ = SetZoomAsync(1); e.Handled = true; }
        }
        else if (e.Key == Key.Escape) { ClearSelection(); e.Handled = true; }
        else if (e.Key is Key.PageDown or Key.Right or Key.Space or Key.PageUp or Key.Left)
        {
            Emit(new { type = "page", direction = e.Key is Key.PageUp or Key.Left ? -1 : 1 });
            e.Handled = true;
        }
        else if (e.Key is Key.Down or Key.Up)
        {
            SetPan(new(_pan.X, _pan.Y + (e.Key == Key.Down ? 60 : -60)));
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    private ReaderAnnotation? AnnotationAt(Point point) => _annotations.FirstOrDefault(annotation =>
        GetRangeBounds(annotation.StartOffset, annotation.EndOffset).Any(rect => rect.Inflate(4).Contains(point)));

    private void ClearHover()
    {
        if (_hoveredAnnotation is null) return;
        _hoveredAnnotation = null;
        Emit(new { type = "annotationLeave" });
    }

    private void EmitSelection()
    {
        var text = _content?.Text ?? string.Empty;
        var start = Math.Clamp(_selectionStart, 0, text.Length);
        var end = Math.Clamp(_selectionEnd, start, text.Length);
        var rects = GetRangeBounds(start, end);
        var placement = rects.Count > 0 ? rects[0] : PageBounds;
        Emit(new { type = "selection", text = text[start..end], startOffset = start, endOffset = end,
            prefix = text[Math.Max(0, start - 16)..start], suffix = text[end..Math.Min(text.Length, end + 16)],
            x = Math.Clamp(placement.Left, 0, Bounds.Width), y = Math.Clamp(placement.Top, 0, Bounds.Height),
            bottom = Math.Clamp(placement.Bottom, 0, Bounds.Height) });
    }

    private void EmitPage()
    {
        if (_content is null) return;
        Emit(new { type = "pdfPage", page = PageNumber, top = _pan.Y, left = _pan.X,
            scrollWidth = PageBounds.Width + 40, scrollHeight = PageBounds.Height + 32,
            clientWidth = Bounds.Width, clientHeight = Bounds.Height });
    }

    private void Emit(object message)
    {
        if (!_disposed) WebMessageReceived?.Invoke(this, new ReaderWebMessageReceivedEventArgs(JsonSerializer.Serialize(message)));
    }

    private (int Width, int Height) RasterResolution()
    {
        var dpi = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        return ((int)Math.Clamp(Math.Max(800, Bounds.Width) * Zoom * dpi, 1, 8192),
            (int)Math.Clamp(Math.Max(1000, Bounds.Height) * Zoom * dpi, 1, 8192));
    }

    private async Task RefreshRasterAsync()
    {
        if (_disposed || _content is null || Source is null) return;
        var version = _version;
        var rasterVersion = ++_rasterVersion;
        var page = PageNumber;
        var resolution = RasterResolution();
        try
        {
            var png = await Task.Run(() =>
            {
                lock (_documentGate)
                {
                    if (_disposed || version != _version || rasterVersion != _rasterVersion) return null;
                    return _document?.RenderPage(page, resolution.Width, resolution.Height).Png;
                }
            });
            if (png is not null && !_disposed && version == _version && rasterVersion == _rasterVersion)
                ReplaceBitmap(png);
        }
        catch (Exception exception) { if (!_disposed) LastError = exception.Message; }
    }

    private void ReplaceBitmap(byte[] png)
    {
        using var stream = new MemoryStream(png, false);
        var bitmap = new Bitmap(stream);
        _bitmap?.Dispose();
        _bitmap = bitmap;
        InvalidateVisual();
    }

    public Task<byte[]?> CaptureVisiblePageAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_bitmap is null || Bounds.Width < 1 || Bounds.Height < 1) return Task.FromResult<byte[]?>(null);
        using var capture = new RenderTargetBitmap(new PixelSize((int)Bounds.Width, (int)Bounds.Height), new Vector(96, 96));
        capture.Render(this);
        using var stream = new MemoryStream();
        capture.Save(stream, PngBitmapEncoderOptions.Default);
        return Task.FromResult<byte[]?>(stream.ToArray());
    }

    public void Stop() { _navigation?.Cancel(); ++_version; ++_rasterVersion; _resizeTimer.Stop(); }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _navigation?.Dispose();
        _bitmap?.Dispose();
        _bitmap = null;
        _content = null;
        lock (_documentGate) { _document?.Dispose(); _document = null; }
    }
}
