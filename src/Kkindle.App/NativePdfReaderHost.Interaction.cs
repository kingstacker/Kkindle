using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle;

public sealed partial class NativePdfReaderHost
{
    public void SetAnnotations(IReadOnlyList<ReaderAnnotation> annotations)
    {
        _annotations = annotations.ToArray();
        ClearHover();
        InvalidateVisual();
    }

    private IEnumerable<ReaderAnnotation> AnnotationsForPage(int page) => _annotations.Where(annotation =>
        annotation.ChapterPath.StartsWith("pdf:", StringComparison.Ordinal)
        && int.TryParse(annotation.ChapterPath.AsSpan(annotation.ChapterPath.LastIndexOf(':') + 1), out var number) && number == page);

    public void ClearSelection()
    {
        _selectionStart = _selectionEnd = 0;
        _selectionPage = PageNumber;
        InvalidateVisual();
        EmitSelection();
    }

    public void SetPointAnnotationMode(bool enabled)
    {
        _pointAnnotationMode = enabled;
        if (enabled)
        {
            _regionSelectionMode = false;
            _regionPointerStart = null;
            _regionSelection = null;
            ClearSelection();
        }
        Cursor = enabled
            ? new Cursor(StandardCursorType.Cross)
            : new Cursor(StandardCursorType.Arrow);
        InvalidateVisual();
    }

    public void SetRegionSelectionMode(bool enabled)
    {
        _regionSelectionMode = enabled;
        _pointAnnotationMode = false;
        _regionPointerStart = null;
        _regionSelection = null;
        if (enabled) ClearSelection();
        Cursor = enabled
            ? new Cursor(StandardCursorType.Cross)
            : new Cursor(StandardCursorType.Arrow);
        InvalidateVisual();
    }

    public void ClearRegionSelection()
    {
        _regionSelectionMode = false;
        _regionPointerStart = null;
        _regionSelection = null;
        InvalidateVisual();
    }

    public Point? GetPdfPointPosition(ReaderAnnotation annotation)
    {
        if (!ReaderPdfPointAnchor.TryParse(annotation.Fragment, out var x, out var y)
            || !annotation.ChapterPath.StartsWith("pdf:", StringComparison.OrdinalIgnoreCase)
            || !int.TryParse(annotation.ChapterPath[(annotation.ChapterPath.LastIndexOf(':') + 1)..], out var page)
            || page < 1
            || page > PageCount)
            return null;
        var bounds = GetPageBounds(page);
        var crop = GetPageCrop(page).Normalize();
        var size = GetUnrotatedPageSize(bounds);
        var local = new Point(
            (x - crop.X) / crop.Width * size.Width,
            (y - crop.Y) / crop.Height * size.Height);
        return GetPageTransform(bounds).Transform(local);
    }

    public async Task EnsurePaperColumnForPointAsync(
        double x,
        CancellationToken cancellationToken = default)
    {
        if (DisplayMode != PdfReaderDisplayMode.PaperColumns
            || GetPageContent(PageNumber) is not { } content)
            return;
        var columns = PdfPaperAnalysisService.DetectColumns(content);
        if (!columns.IsTwoColumn) return;
        var targetColumn = x >= columns.Right.X ? 1 : 0;
        if (targetColumn == _paperColumnIndex) return;
        _paperColumnIndex = targetColumn;
        ClearSelection();
        RebuildLayout(preservePosition: false);
        await RefreshViewportAsync(cancellationToken);
        Emit(new { type = "pdfLayout" });
    }

    public void ScrollToPdfPoint(double x, double y)
    {
        if (_layout is null || PageNumber < 1 || PageNumber > _layout.Pages.Length)
            return;
        var page = GetPageBounds(PageNumber);
        var crop = GetPageCrop(PageNumber).Normalize();
        var size = GetUnrotatedPageSize(page);
        var local = new Point(
            (Math.Clamp(x, 0, 1) - crop.X) / crop.Width * size.Width,
            (Math.Clamp(y, 0, 1) - crop.Y) / crop.Height * size.Height);
        var position = GetPageTransform(page).Transform(local);
        SetPan(new(
            position.X - Viewport.Width * 0.2,
            position.Y - Viewport.Height * 0.25),
            trackPage: false);
    }

    public void SelectRange(int start, int end)
    {
        var text = PageContent?.Text ?? string.Empty;
        start = Math.Clamp(start, 0, text.Length);
        end = Math.Clamp(end, start, text.Length);
        while (start < end && char.IsWhiteSpace(text[start])) start++;
        while (end > start && char.IsWhiteSpace(text[end - 1])) end--;
        _selectionPage = PageNumber;
        _selectionStart = start;
        _selectionEnd = end;
        InvalidateVisual();
        EmitSelection();
    }

    private void RebuildSearch(string query)
    {
        var previousIndex = _searchPage == PageNumber ? _searchIndex : 0;
        _searchQuery = query;
        _searchPage = PageNumber;
        _search.Clear();
        _searchIndex = -1;
        var text = PageContent?.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(query)) return;
        for (var start = 0; start < text.Length;)
        {
            var match = text.IndexOf(query, start, StringComparison.CurrentCultureIgnoreCase);
            if (match < 0) break;
            _search.Add((match, query.Length));
            start = match + query.Length;
        }
        if (_search.Count > 0) _searchIndex = Math.Clamp(previousIndex, 0, _search.Count - 1);
    }

    public (int Count, int Index) Find(string query, int? offset = null)
    {
        _searchIndex = 0;
        RebuildSearch(query);
        if (_search.Count > 0)
        {
            _searchIndex = offset is { } target ? Math.Max(0, _search.FindIndex(hit => hit.Start >= target)) : 0;
            ScrollToOffset(_search[_searchIndex].Start);
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

    public void ClearSearch() { _searchQuery = string.Empty; _search.Clear(); _searchIndex = -1; InvalidateVisual(); }
    public void SetSpeechHighlight(int start, int length)
    {
        _speechHighlight = (PageNumber, start, length);
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
        var glyph = PageContent?.Glyphs.FirstOrDefault(item => item.Offset + item.Length > offset);
        if (glyph is null || _layout is null) return;
        var page = _layout.Pages[PageNumber - 1];
        var bounds = GetLocalGlyphBounds(PageNumber, glyph.Bounds, page).TransformToAABB(GetPageTransform(page));
        SetPan(new(bounds.X - Viewport.Width * 0.2, bounds.Y - Viewport.Height * 0.25), trackPage: false);
    }

    private Size GetUnrotatedPageSize(Rect page) => Rotation % 180 == 0 ? page.Size : new(page.Height, page.Width);
    private Matrix GetPageTransform(Rect page) => Rotation switch
    {
        90 => new(0, 1, -1, 0, page.Right, page.Top),
        180 => new(-1, 0, 0, -1, page.Right, page.Bottom),
        270 => new(0, -1, 1, 0, page.Left, page.Bottom),
        _ => new(1, 0, 0, 1, page.Left, page.Top)
    };

    private Rect GetLocalGlyphBounds(int pageNumber, PdfTextBounds box, Rect page)
    {
        var size = GetUnrotatedPageSize(page);
        var crop = GetPageCrop(pageNumber).Normalize();
        return new(
            (box.X - crop.X) / crop.Width * size.Width,
            (box.Y - crop.Y) / crop.Height * size.Height,
            box.Width / crop.Width * size.Width,
            box.Height / crop.Height * size.Height);
    }

    private Point GetNormalizedPagePoint(int pageNumber, Point point)
    {
        var page = GetPageBounds(pageNumber);
        var local = GetPageTransform(page).Invert().Transform(point);
        var size = GetUnrotatedPageSize(page);
        var crop = GetPageCrop(pageNumber).Normalize();
        return new(
            Math.Clamp(crop.X + local.X / Math.Max(1, size.Width) * crop.Width, 0, 1),
            Math.Clamp(crop.Y + local.Y / Math.Max(1, size.Height) * crop.Height, 0, 1));
    }

    private Rect GetLocalRegionBounds(int pageNumber, PdfPageCrop region, Rect page)
    {
        var crop = GetPageCrop(pageNumber).Normalize();
        var size = GetUnrotatedPageSize(page);
        region = region.Normalize();
        return new(
            (region.X - crop.X) / crop.Width * size.Width,
            (region.Y - crop.Y) / crop.Height * size.Height,
            region.Width / crop.Width * size.Width,
            region.Height / crop.Height * size.Height);
    }

    private PdfPageCrop GetRegionSelection(Point first, Point second, int pageNumber)
    {
        var start = GetNormalizedPagePoint(pageNumber, first);
        var end = GetNormalizedPagePoint(pageNumber, second);
        return new PdfPageCrop(
            Math.Min(start.X, end.X),
            Math.Min(start.Y, end.Y),
            Math.Abs(start.X - end.X),
            Math.Abs(start.Y - end.Y)).Normalize();
    }

    private void EmitPointAnnotation(int pageNumber, Point point)
    {
        var normalized = GetNormalizedPagePoint(pageNumber, point);
        _pointAnnotationMode = false;
        Cursor = new Cursor(StandardCursorType.Arrow);
        ClearSelection();
        Emit(new
        {
            type = "pdfPointAnnotation",
            page = pageNumber,
            pageX = normalized.X,
            pageY = normalized.Y,
            x = Math.Clamp(point.X, 0, Bounds.Width),
            y = Math.Clamp(point.Y, 0, Bounds.Height)
        });
    }

    private void EmitRegionSelection(int pageNumber, PdfPageCrop region, Point point)
    {
        _regionSelectionMode = false;
        _regionPointerStart = null;
        _regionSelectionPage = pageNumber;
        _regionSelection = region;
        Cursor = new Cursor(StandardCursorType.Arrow);
        InvalidateVisual();
        Emit(new
        {
            type = "pdfRegionSelection",
            page = pageNumber,
            x = region.X,
            y = region.Y,
            width = region.Width,
            height = region.Height,
            anchorX = Math.Clamp(point.X, 0, Bounds.Width),
            anchorY = Math.Clamp(point.Y, 0, Bounds.Height)
        });
    }

    public IReadOnlyList<Rect> GetRangeBounds(int start, int end) => GetRangeBounds(PageNumber, start, end);
    public IReadOnlyList<Rect> GetRangeBounds(int pageNumber, int start, int end)
    {
        var transform = GetPageTransform(GetPageBounds(pageNumber));
        return GetLocalRangeBounds(pageNumber, start, end).Select(rect => rect.TransformToAABB(transform)).ToArray();
    }

    private IReadOnlyList<Rect> GetLocalRangeBounds(int pageNumber, int start, int end)
    {
        var content = GetPageContent(pageNumber);
        if (content is null || end <= start) return [];
        var page = GetPageBounds(pageNumber);
        var rectangles = new List<Rect>();
        foreach (var glyph in content.Glyphs)
        {
            if (glyph.Offset >= end) break;
            if (glyph.Offset + glyph.Length <= start) continue;
            var rect = GetLocalGlyphBounds(pageNumber, glyph.Bounds, page);
            if (rectangles.Count > 0)
            {
                var previous = rectangles[^1];
                var sameLine = Math.Abs(previous.Center.Y - rect.Center.Y) < Math.Max(previous.Height, rect.Height) * 0.45
                    && rect.Left - previous.Right < Math.Max(previous.Height, rect.Height) * 2 && rect.Left >= previous.Left - 1;
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
        if (_layout is null) return;
        if (Math.Abs(DeviceScaling - _layoutDpi) > 0.001) ScheduleRender();
        foreach (var pageNumber in VisiblePageNumbers)
        {
            var page = GetPageBounds(pageNumber);
            var shadow = page.Translate(new Vector(0, 2));
            context.FillRectangle(new SolidColorBrush(Color.FromArgb(22, 0, 0, 0)), shadow.Inflate(1));
            context.DrawRectangle(new SolidColorBrush(_palette.Page), new Pen(new SolidColorBrush(_palette.Border), 0.5), page);
            if (_pages.TryGetValue(pageNumber, out var cached) && cached.Bitmap is { } bitmap && cached.Raster is { } raster
                && raster.Rotation == Rotation && cached.Palette == _palette)
            {
                var crop = GetRenderedPageCrop(pageNumber);
                var target = new Rect(
                    page.X + ((double)raster.Left / raster.PagePixelWidth - crop.X)
                        / Math.Max(0.01, crop.Width) * page.Width,
                    page.Y + ((double)raster.Top / raster.PagePixelHeight - crop.Y)
                        / Math.Max(0.01, crop.Height) * page.Height,
                    (double)raster.Width / raster.PagePixelWidth
                        / Math.Max(0.01, crop.Width) * page.Width,
                    (double)raster.Height / raster.PagePixelHeight
                        / Math.Max(0.01, crop.Height) * page.Height);
                context.DrawImage(bitmap, new Rect(bitmap.Size), target);
            }
            using var clip = context.PushClip(page);
            // Rotate the original annotation geometry as a whole, including
            // underline direction, so a 180° turn does not move lines across text.
            using var transform = context.PushTransform(GetPageTransform(page));
            foreach (var annotation in AnnotationsForPage(pageNumber))
            {
                var color = Color.TryParse(annotation.Color, out var parsed) ? parsed : Colors.Black;
                if (color == Colors.Black && _palette != ReaderPalette.For(ReaderTheme.Classic)) color = _palette.Ink;
                if (ReaderPdfPointAnchor.TryParse(annotation.Fragment, out var pointX, out var pointY))
                {
                    var crop = GetPageCrop(pageNumber).Normalize();
                    var size = GetUnrotatedPageSize(page);
                    var point = new Point(
                        (pointX - crop.X) / crop.Width * size.Width,
                        (pointY - crop.Y) / crop.Height * size.Height);
                    DrawPointAnnotation(context, point, color);
                    continue;
                }
                if (annotation.EndOffset <= annotation.StartOffset) continue;
                foreach (var rect in GetLocalRangeBounds(pageNumber, annotation.StartOffset, annotation.EndOffset))
                    DrawAnnotation(context, rect, annotation.UnderlineStyle, color);
            }
            if (_regionSelectionPage == pageNumber && _regionSelection is { } region)
            {
                var selectionBounds = GetLocalRegionBounds(pageNumber, region, page);
                context.FillRectangle(
                    new SolidColorBrush(Color.FromArgb(48, 66, 133, 210)),
                    selectionBounds);
                context.DrawRectangle(
                    new Pen(new SolidColorBrush(Color.FromArgb(180, 66, 133, 210)), 1.2),
                    selectionBounds);
            }
            if (_searchPage == pageNumber)
                for (var index = 0; index < _search.Count; index++)
                    foreach (var rect in GetLocalRangeBounds(pageNumber, _search[index].Start, _search[index].Start + _search[index].Length))
                        context.FillRectangle(new SolidColorBrush(Color.FromArgb(index == _searchIndex ? (byte)140 : (byte)70, 255, 200, 50)), rect.Inflate(1));
            if (_selectionPage == pageNumber)
                foreach (var rect in GetLocalRangeBounds(pageNumber, _selectionStart, _selectionEnd))
                    context.FillRectangle(new SolidColorBrush(Color.FromArgb(85, 66, 133, 210)), rect.Inflate(1));
            if (_speechHighlight is { } speech && speech.Page == pageNumber)
                foreach (var rect in GetLocalRangeBounds(pageNumber, speech.Start, speech.Start + speech.Length))
                    context.FillRectangle(new SolidColorBrush(Color.FromArgb(65, 100, 160, 95)), rect.Inflate(1));
        }
    }

    private static void DrawAnnotation(DrawingContext context, Rect rect, string style, Color color)
    {
        if (style == "marker")
        {
            context.FillRectangle(new SolidColorBrush(Color.FromArgb(80, color.R, color.G, color.B)), rect.Inflate(1));
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

    private static void DrawPointAnnotation(DrawingContext context, Point point, Color color)
    {
        var brush = new SolidColorBrush(color == Colors.Black
            ? Color.FromArgb(235, 45, 45, 45)
            : Color.FromArgb(235, color.R, color.G, color.B));
        context.DrawEllipse(brush, null, point, 5, 5);
        context.DrawEllipse(null, new Pen(new SolidColorBrush(Colors.White), 1), point, 3.2, 3.2);
    }

    private int PageAtPoint(Point point) => VisiblePageNumbers.FirstOrDefault(page => GetPageBounds(page).Contains(point));
    private int HitOffset(int pageNumber, Point point, bool nearest)
    {
        var content = GetPageContent(pageNumber);
        if (content is null || !nearest && !GetPageBounds(pageNumber).Contains(point)) return -1;
        var page = GetPageBounds(pageNumber);
        point = GetPageTransform(page).Invert().Transform(point);
        PdfTextGlyph? best = null;
        var bestDistance = double.MaxValue;
        foreach (var glyph in content.Glyphs)
        {
            var rect = GetLocalGlyphBounds(pageNumber, glyph.Bounds, page);
            var dx = Math.Max(0, Math.Max(rect.Left - point.X, point.X - rect.Right));
            var dy = Math.Max(0, Math.Max(rect.Top - point.Y, point.Y - rect.Bottom));
            var distance = dx * dx + dy * dy;
            if (distance >= bestDistance) continue;
            best = glyph;
            bestDistance = distance;
        }
        if (best is null || !nearest && bestDistance > 64) return -1;
        var bestBounds = GetLocalGlyphBounds(pageNumber, best.Bounds, page);
        var after = (point.X - bestBounds.Center.X) * best.AdvanceX
            + (point.Y - bestBounds.Center.Y) * best.AdvanceY > 0;
        return best.Offset + (after ? best.Length : 0);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetPosition(this);
        var page = PageAtPoint(point);
        var properties = e.GetCurrentPoint(this).Properties;
        if (e.Source != this) return;
        if (properties.IsRightButtonPressed)
        {
            if (page > 0)
            {
                if (AnnotationAt(point) is { } annotation)
                    Emit(new { type = "annotationClick", id = annotation.Id });
                else
                    EmitPointAnnotation(page, point);
            }
            e.Handled = true;
            return;
        }
        if (!properties.IsLeftButtonPressed) return;
        Focus();
        if (page == 0)
        {
            ClearSelection();
            return;
        }
        ActivatePage(page);
        EmitPage();
        ClearHover();
        if (_regionSelectionMode)
        {
            _regionSelectionPage = page;
            _regionPointerStart = point;
            _regionSelection = null;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }
        if (_pointAnnotationMode)
        {
            if (AnnotationAt(point) is { } annotation)
                Emit(new { type = "annotationClick", id = annotation.Id });
            else
                EmitPointAnnotation(page, point);
            e.Handled = true;
            return;
        }
        _selectionPage = page;
        _anchorOffset = HitOffset(page, point, false);
        _pointerStart = point;
        _dragging = false;
        if (_anchorOffset >= 0 && e.ClickCount >= 2 && PageContent is { } content)
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
        if (_regionPointerStart is { } regionStart)
        {
            var regionPage = _regionSelectionPage;
            if (regionPage > 0)
            {
                _regionSelection = GetRegionSelection(regionStart, point, regionPage);
                InvalidateVisual();
            }
            return;
        }
        if (_pointerStart is { } start && _anchorOffset >= 0)
        {
            _dragging |= Math.Abs(point.X - start.X) + Math.Abs(point.Y - start.Y) > 3;
            if (_dragging)
            {
                var hit = HitOffset(_selectionPage, point, true);
                if (hit >= 0) { _selectionStart = Math.Min(_anchorOffset, hit); _selectionEnd = Math.Max(_anchorOffset, hit); InvalidateVisual(); }
            }
            return;
        }
        var annotation = AnnotationAt(point);
        var page = PageAtPoint(point);
        Cursor = new Cursor(_regionSelectionMode || _pointAnnotationMode
            ? StandardCursorType.Cross
            : annotation is not null
                ? StandardCursorType.Hand
                : page > 0 && HitOffset(page, point, false) >= 0
                    ? StandardCursorType.Ibeam
                    : StandardCursorType.Arrow);
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
        if (_regionPointerStart is { } regionStart)
        {
            var point = e.GetPosition(this);
            var page = _regionSelectionPage;
            _regionPointerStart = null;
            e.Pointer.Capture(null);
            if (page > 0)
            {
                var region = GetRegionSelection(regionStart, point, page);
                if (region.Width >= 0.01 && region.Height >= 0.01)
                    EmitRegionSelection(page, region, point);
                else
                {
                    _regionSelectionMode = false;
                    _regionSelection = null;
                    Cursor = new Cursor(StandardCursorType.Arrow);
                    InvalidateVisual();
                }
            }
            e.Handled = true;
            return;
        }
        if (_pointerStart is null) return;
        var dragged = _dragging;
        _pointerStart = null;
        _dragging = false;
        e.Pointer.Capture(null);
        if (dragged) SelectRange(_selectionStart, _selectionEnd);
        else if (AnnotationAt(e.GetPosition(this)) is { } annotation) Emit(new { type = "annotationClick", id = annotation.Id });
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
            ScrollBarAutoHide.Show(_horizontal);
            SetPan(new(_pan.X - (e.Delta.X != 0 ? e.Delta.X : e.Delta.Y) * 60, _pan.Y));
            return;
        }
        var direction = -Math.Sign(e.Delta.Y);
        if (direction == 0) return;
        if (DisplayMode == PdfReaderDisplayMode.Continuous
            || direction > 0 && _pan.Y < _vertical.Maximum - 1 || direction < 0 && _pan.Y > 1)
            ScrollBy(-e.Delta.Y * 60);
        else Emit(new { type = "page", direction });
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        HandleReaderKey(e);
        base.OnKeyDown(e);
    }

    internal void HandleReaderKey(KeyEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta))
        {
            if (e.Key == Key.A) { SelectRange(0, PageContent?.Text.Length ?? 0); e.Handled = true; }
            else if (e.Key == Key.C && _selectionEnd > _selectionStart) { Emit(new { type = "selectionAction", action = "copy" }); e.Handled = true; }
            else if (e.Key is Key.D0 or Key.NumPad0) { _ = SetFitModeAsync(PdfReaderFitMode.Width); e.Handled = true; }
        }
        else if (e.Key == Key.Escape)
        {
            if (_pointAnnotationMode || _regionSelectionMode || _regionPointerStart is not null)
            {
                _pointAnnotationMode = false;
                _regionSelectionMode = false;
                _regionPointerStart = null;
                _regionSelection = null;
                Cursor = new Cursor(StandardCursorType.Arrow);
                InvalidateVisual();
            }
            ClearSelection();
            e.Handled = true;
        }
        else if (e.Key is Key.PageDown or Key.Space or Key.PageUp && DisplayMode == PdfReaderDisplayMode.Continuous)
        {
            ScrollBy((e.Key == Key.PageUp ? -1 : 1) * Math.Max(60, Viewport.Height - 48));
            e.Handled = true;
        }
        else if (e.Key is Key.PageDown or Key.Right or Key.Space or Key.PageUp or Key.Left)
        {
            Emit(new { type = "page", direction = e.Key is Key.PageUp or Key.Left ? -1 : 1 });
            e.Handled = true;
        }
        else if (e.Key is Key.Down or Key.Up)
        {
            ScrollBy(e.Key == Key.Down ? 60 : -60);
            e.Handled = true;
        }
    }

    private ReaderAnnotation? AnnotationAt(Point point)
    {
        var page = PageAtPoint(point);
        if (page == 0) return null;
        foreach (var annotation in AnnotationsForPage(page))
        {
            if (ReaderPdfPointAnchor.TryParse(annotation.Fragment, out _, out _)
                && GetPdfPointPosition(annotation) is { } marker
                && (marker.X - point.X) * (marker.X - point.X)
                    + (marker.Y - point.Y) * (marker.Y - point.Y) <= 100)
                return annotation;
            if (annotation.EndOffset > annotation.StartOffset
                && GetRangeBounds(page, annotation.StartOffset, annotation.EndOffset)
                    .Any(rect => rect.Inflate(4).Contains(point)))
                return annotation;
        }
        return null;
    }

    private void ClearHover()
    {
        if (_hoveredAnnotation is null) return;
        _hoveredAnnotation = null;
        Emit(new { type = "annotationLeave" });
    }

    private void EmitSelection()
    {
        var text = GetPageContent(_selectionPage)?.Text ?? string.Empty;
        var start = Math.Clamp(_selectionStart, 0, text.Length);
        var end = Math.Clamp(_selectionEnd, start, text.Length);
        var rects = GetRangeBounds(_selectionPage, start, end);
        var placement = rects.Count > 0 ? rects[0] : PageBounds;
        Emit(new { type = "selection", text = text[start..end], startOffset = start, endOffset = end,
            prefix = text[Math.Max(0, start - 16)..start], suffix = text[end..Math.Min(text.Length, end + 16)],
            x = Math.Clamp(placement.Left, 0, Bounds.Width), y = Math.Clamp(placement.Top, 0, Bounds.Height),
            bottom = Math.Clamp(placement.Bottom, 0, Bounds.Height) });
    }
}
