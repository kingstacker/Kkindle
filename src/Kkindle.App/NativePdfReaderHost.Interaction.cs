using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle;

public sealed partial class NativePdfReaderHost
{
    private Guid? _selectedAnnotationId;

    public void SelectAnnotation(Guid id)
    {
        var selected = _annotations.Any(item => item.Id == id) ? id : (Guid?)null;
        if (_selectedAnnotationId == selected)
        {
            Focus();
            return;
        }
        _selectedAnnotationId = selected;
        if (selected is null)
        {
            _selectionDashTimer.Stop();
        }
        else if (!_selectionDashTimer.IsEnabled)
        {
            _selectionDashOffset = 0;
            _selectionDashTimer.Start();
        }
        InvalidateVisual();
        Focus();
    }

    /// <summary>The highlight/underline/sticky-note currently picked with the left button.</summary>
    public Guid? SelectedAnnotationId => _selectedAnnotationId;

    /// <summary>Drops the highlight/underline selection without touching the text selection.</summary>
    public void ClearAnnotationSelection()
    {
        if (_selectedAnnotationId is null) return;
        _selectedAnnotationId = null;
        _selectionDashTimer.Stop();
        InvalidateVisual();
    }

    public void SetAnnotations(IReadOnlyList<ReaderAnnotation> annotations)
    {
        _annotations = annotations.ToArray();
        if (!_annotations.Any(item => item.Id == _selectedAnnotationId)) _selectedAnnotationId = null;
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
            ClearSelection();
        }
        Cursor = enabled
            ? new Cursor(StandardCursorType.Cross)
            : new Cursor(StandardCursorType.Arrow);
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
        var size = GetUnrotatedPageSize(bounds);
        var local = new Point(
            x * size.Width,
            y * size.Height);
        return GetPageTransform(bounds).Transform(local);
    }

    public bool TryGetAnnotationAnchor(ReaderAnnotation annotation, out int pageNumber, out Point anchor)
    {
        pageNumber = 0;
        anchor = default;
        if (!annotation.ChapterPath.StartsWith("pdf:", StringComparison.OrdinalIgnoreCase)
            || !int.TryParse(annotation.ChapterPath[(annotation.ChapterPath.LastIndexOf(':') + 1)..], out pageNumber)
            || pageNumber < 1
            || pageNumber > PageCount)
            return false;

        if (ReaderPdfPointAnchor.TryParse(annotation.Fragment, out _, out _)
            && GetPdfPointPosition(annotation) is { } point)
        {
            anchor = point;
            return true;
        }

        if (GetTextAnnotationAnchor(pageNumber, annotation.StartOffset, annotation.EndOffset) is not { } textAnchor)
            return false;
        anchor = textAnchor;
        return true;
    }

    public Point? GetTextAnnotationAnchor(int pageNumber, int start, int end)
    {
        var bounds = GetLocalRangeBounds(pageNumber, start, end);
        if (bounds.Count == 0) return null;
        var last = bounds[^1];
        var point = last.Height > last.Width * 2
            ? new Point(last.Right + 2, last.Bottom)
            : new Point(last.Right, last.Bottom + 2);
        return GetPageTransform(GetPageBounds(pageNumber)).Transform(point);
    }

    public bool TryGetAnnotationCalloutBounds(int pageNumber, Point anchor, Size preferredSize, out Rect bounds)
    {
        bounds = default;
        if (_layout is null || pageNumber < 1 || pageNumber > PageCount
            || preferredSize.Width <= 0 || preferredSize.Height <= 0)
            return false;

        const double inset = 8;
        const double gap = 18;
        var viewport = new Rect(Bounds.Size).Deflate(inset);
        if (viewport.Height < 64) return false;
        var page = GetPageBounds(pageNumber);
        // A clamped card must never outlive its anchor in the visible page.
        if (!page.Contains(anchor) || !new Rect(Viewport).Contains(anchor)) return false;
        var left = Math.Max(viewport.Left, page.Right + gap);
        var width = Math.Min(preferredSize.Width, viewport.Right - left);
        if (width < 144) return false;
        var height = Math.Min(preferredSize.Height, viewport.Height);
        bounds = new Rect(left,
            Math.Clamp(anchor.Y - height / 2, viewport.Top, viewport.Bottom - height),
            width, height);
        var candidate = bounds;
        if (VisiblePageNumbers.Where(number => number != pageNumber)
            .Any(number => GetPageBounds(number).Intersects(candidate))) return false;
        return true;
    }

    private readonly List<(ReaderAnnotation Annotation, Action<Rect> Moved)> _additionalAnnotationCallouts = [];
    private readonly List<(Point Anchor, Rect Bounds)> _additionalAnnotationConnectors = [];

    public void SetAdditionalAnnotationCallouts(IEnumerable<(ReaderAnnotation Annotation, Action<Rect> Moved)> callouts)
    {
        _additionalAnnotationCallouts.Clear();
        _additionalAnnotationCallouts.AddRange(callouts);
        UpdateAnnotationCallout();
        InvalidateVisual();
    }

    public void DismissAnnotationCallout(Guid? additionalId = null)
    {
        if (additionalId is { } id)
            _additionalAnnotationCallouts.RemoveAll(item => item.Annotation.Id == id);
        else _annotationCalloutDismissed = true;
        UpdateAnnotationCallout();
        InvalidateVisual();
    }

    private bool _annotationCalloutVisible;
    private bool _annotationCalloutDismissed;
    private Size _annotationCalloutSize;
    private Action<Rect>? _annotationCalloutMoved;
    private int _annotationCalloutPage;
    private Point _annotationCalloutPagePoint;
    private Size _annotationCalloutPageSize;
    private Size _annotationCalloutViewportSize;
    private int _annotationCalloutRotation;

    public void SetAnnotationCallout(Point anchor, Rect bounds, Action<Rect>? moved = null, int pageNumber = 0, Size? preferredSize = null)
    {
        _annotationCalloutPage = pageNumber > 0 ? pageNumber : PageNumber;
        var page = GetPageBounds(_annotationCalloutPage);
        var size = GetUnrotatedPageSize(page);
        var local = GetPageTransform(page).Invert().Transform(anchor);
        // Keep the anchor in document space, never in stale screen coordinates.
        _annotationCalloutPagePoint = new(local.X / size.Width, local.Y / size.Height);
        _annotationCalloutPageSize = page.Size;
        _annotationCalloutViewportSize = Bounds.Size;
        _annotationCalloutRotation = Rotation;
        _annotationCallout = (anchor, bounds);
        _annotationCalloutSize = preferredSize ?? bounds.Size;
        _annotationCalloutDismissed = false;
        _annotationCalloutMoved = moved;
        UpdateAnnotationCallout();
        InvalidateVisual();
    }

    private void UpdateAnnotationCallout()
    {
        if (_annotationCallout is not { } callout || _layout is null
            || _annotationCalloutPage < 1 || _annotationCalloutPage > PageCount) return;
        var page = GetPageBounds(_annotationCalloutPage);
        var size = GetUnrotatedPageSize(page);
        var anchor = GetPageTransform(page).Transform(new Point(
            _annotationCalloutPagePoint.X * size.Width,
            _annotationCalloutPagePoint.Y * size.Height));
        _annotationCalloutVisible = TryGetAnnotationCalloutBounds(
            _annotationCalloutPage, anchor, _annotationCalloutSize, out var bounds);
        _annotationCalloutVisible &= !_annotationCalloutDismissed;
        if (!_annotationCalloutVisible) bounds = callout.Bounds;
        _annotationCalloutPageSize = page.Size;
        _annotationCalloutViewportSize = Bounds.Size;
        _annotationCalloutRotation = Rotation;
        _annotationCallout = (anchor, bounds);
        _additionalAnnotationConnectors.Clear();
        var cards = new List<(Point Anchor, Rect Bounds, Action<Rect>? Moved, bool Primary)>();
        if (_annotationCalloutVisible) cards.Add((anchor, bounds, _annotationCalloutMoved, true));
        else _annotationCalloutMoved?.Invoke(default);
        foreach (var item in _additionalAnnotationCallouts)
        {
            if (TryGetAnnotationAnchor(item.Annotation, out var number, out var point)
                && TryGetAnnotationCalloutBounds(number, point, new Size(220, 88), out var cardBounds))
                cards.Add((point, cardBounds, item.Moved, false));
            else item.Moved(default);
        }
        cards.Sort((a, b) => a.Anchor.Y.CompareTo(b.Anchor.Y));
        var available = new Rect(Viewport).Deflate(8);
        var height = cards.Count == 0 ? 88 : Math.Min(88, Math.Max(1,
            (available.Height - 8 * (cards.Count - 1)) / cards.Count));
        var nextTop = available.Top;
        for (var index = 0; index < cards.Count; index++)
        {
            var card = cards[index];
            var remaining = cards.Count - index;
            var top = Math.Clamp(card.Anchor.Y - height / 2, nextTop,
                Math.Max(nextTop, available.Bottom - remaining * height - (remaining - 1) * 8));
            var positioned = cards.Count == 1 ? card.Bounds
                : new Rect(card.Bounds.X, top, card.Bounds.Width, height);
            nextTop = positioned.Bottom + 8;
            if (card.Primary) _annotationCallout = (card.Anchor, positioned);
            else _additionalAnnotationConnectors.Add((card.Anchor, positioned));
            card.Moved?.Invoke(positioned);
        }
    }

    public void ClearAnnotationCallout()
    {
        foreach (var item in _additionalAnnotationCallouts) item.Moved(default);
        _additionalAnnotationCallouts.Clear();
        _additionalAnnotationConnectors.Clear();
        if (_annotationCallout is null) return;
        _annotationCallout = null;
        _annotationCalloutMoved = null;
        InvalidateVisual();
    }

    public void ScrollToPdfPoint(double x, double y)
    {
        if (_layout is null || PageNumber < 1 || PageNumber > _layout.Pages.Length)
            return;
        var page = GetPageBounds(PageNumber);
        var size = GetUnrotatedPageSize(page);
        var local = new Point(
            Math.Clamp(x, 0, 1) * size.Width,
            Math.Clamp(y, 0, 1) * size.Height);
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
        return new(
            box.X * size.Width,
            box.Y * size.Height,
            box.Width * size.Width,
            box.Height * size.Height);
    }

    private Point GetNormalizedPagePoint(int pageNumber, Point point)
    {
        var page = GetPageBounds(pageNumber);
        var local = GetPageTransform(page).Invert().Transform(point);
        var size = GetUnrotatedPageSize(page);
        return new(
            Math.Clamp(local.X / Math.Max(1, size.Width), 0, 1),
            Math.Clamp(local.Y / Math.Max(1, size.Height), 0, 1));
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
            DrawPageShadow(context, page);
            context.DrawRectangle(new SolidColorBrush(_palette.Page), new Pen(new SolidColorBrush(_palette.Border), 0.5), page);
            if (_pages.TryGetValue(pageNumber, out var cached) && cached.Bitmap is { } bitmap && cached.Raster is { } raster
                && raster.Rotation == Rotation && cached.Palette == _palette)
            {
                var target = new Rect(
                    page.X + (double)raster.Left / raster.PagePixelWidth * page.Width,
                    page.Y + (double)raster.Top / raster.PagePixelHeight * page.Height,
                    (double)raster.Width / raster.PagePixelWidth * page.Width,
                    (double)raster.Height / raster.PagePixelHeight * page.Height);
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
                    var size = GetUnrotatedPageSize(page);
                    var point = new Point(pointX * size.Width, pointY * size.Height);
                    DrawPointAnnotation(context, point, color);
                    if (_selectedAnnotationId == annotation.Id)
                        context.DrawEllipse(
                            new SolidColorBrush(Color.FromArgb(28, 66, 133, 210)),
                            SelectionPen(),
                            point, 8.5, 8.5);
                    continue;
                }
                if (annotation.EndOffset <= annotation.StartOffset) continue;
                var isSelected = _selectedAnnotationId == annotation.Id;
                foreach (var rect in GetLocalRangeBounds(pageNumber, annotation.StartOffset, annotation.EndOffset))
                {
                    DrawAnnotation(context, rect, annotation.UnderlineStyle, color);
                    if (isSelected)
                        context.DrawRectangle(
                            new SolidColorBrush(Color.FromArgb(24, 66, 133, 210)),
                            SelectionPen(),
                            rect.Inflate(2));
                }
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

        if (_annotationCalloutVisible && _annotationCallout is { } callout)
            DrawAnnotationCalloutConnector(context, callout.Anchor, callout.Bounds, _palette.Ink);
        foreach (var additional in _additionalAnnotationConnectors)
            DrawAnnotationCalloutConnector(context, additional.Anchor, additional.Bounds, _palette.Ink);
    }

    private static void DrawAnnotationCalloutConnector(DrawingContext context, Point anchor, Rect bounds, Color color)
    {
        var connector = new Pen(
            new SolidColorBrush(Color.FromArgb(190, color.R, color.G, color.B)),
            1.35,
            DashStyle.Dash);
        Point endpoint;
        Point firstBend;
        Point secondBend;
        if (anchor.X <= bounds.Left)
        {
            endpoint = new(bounds.Left, FindCalloutPort(anchor.Y, bounds.Top, bounds.Bottom));
            // Turn only in the page gutter, never vertically through body text.
            var elbowX = endpoint.X - 9;
            firstBend = new(elbowX, anchor.Y);
            secondBend = new(elbowX, endpoint.Y);
        }
        else if (anchor.X >= bounds.Right)
        {
            endpoint = new(bounds.Right, FindCalloutPort(anchor.Y, bounds.Top, bounds.Bottom));
            var elbowX = anchor.X + (endpoint.X - anchor.X) * 0.68;
            firstBend = new(elbowX, anchor.Y);
            secondBend = new(elbowX, endpoint.Y);
        }
        else if (anchor.Y <= bounds.Top)
        {
            endpoint = new(FindCalloutPort(anchor.X, bounds.Left, bounds.Right), bounds.Top);
            var elbowY = anchor.Y + (endpoint.Y - anchor.Y) * 0.68;
            firstBend = new(anchor.X, elbowY);
            secondBend = new(endpoint.X, elbowY);
        }
        else
        {
            endpoint = new(FindCalloutPort(anchor.X, bounds.Left, bounds.Right), bounds.Bottom);
            var elbowY = anchor.Y + (endpoint.Y - anchor.Y) * 0.68;
            firstBend = new(anchor.X, elbowY);
            secondBend = new(endpoint.X, elbowY);
        }

        context.DrawLine(connector, anchor, firstBend);
        context.DrawLine(connector, firstBend, secondBend);
        context.DrawLine(connector, secondBend, endpoint);
        context.DrawEllipse(Brushes.White, new Pen(new SolidColorBrush(color), 1), anchor, 2, 2);
    }

    private static double FindCalloutPort(double anchor, double start, double end)
    {
        var margin = Math.Min(22, (end - start) / 3);
        var minimum = start + margin;
        var maximum = end - margin;
        var port = Math.Clamp(anchor, minimum, maximum);
        if (Math.Abs(port - anchor) >= 18) return port;

        var upper = Math.Clamp((start + end) / 2 - 24, minimum, maximum);
        var lower = Math.Clamp((start + end) / 2 + 24, minimum, maximum);
        return Math.Abs(upper - anchor) >= Math.Abs(lower - anchor) ? upper : lower;
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

    // Marching ants: the dash offset is advanced by _selectionDashTimer.
    private Pen SelectionPen() => new(
        new SolidColorBrush(Color.FromArgb(235, 66, 133, 210)),
        1.2,
        new DashStyle([4, 3], _selectionDashOffset));

    // A drawing context has no blur, so the paper shadow is built from a few
    // expanding translucent rings: tight and dark near the page edge, wide and
    // faint further out. Pages read as sheets lying on the surface.
    private void DrawPageShadow(DrawingContext context, Rect page)
    {
        for (var layer = 6; layer >= 1; layer--)
        {
            var spread = layer * 2.4;
            var alpha = (byte)Math.Clamp(54 - layer * 7, 8, 54);
            var shadow = new Rect(
                page.X - spread,
                page.Y - spread + spread * 0.55,
                page.Width + spread * 2,
                page.Height + spread * 2);
            context.DrawRectangle(new SolidColorBrush(Color.FromArgb(alpha, 0, 0, 0)), null, shadow);
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
                if (PdfAnnotTrace.Enabled && page > 0)
        {
            var dump = string.Join("; ", AnnotationsForPage(page).Select(a =>
                a.StartOffset < a.EndOffset
                    ? $"{a.Id.ToString()[..8]} {a.StartOffset}-{a.EndOffset} [{(GetRangeBounds(page, a.StartOffset, a.EndOffset).FirstOrDefault())}]"
                    : $"{a.Id.ToString()[..8]} point {a.Fragment} [{GetPdfPointPosition(a)}]"));
            PdfAnnotTrace.Write($"page {page} annotations: {dump}");
        }
        if (PdfAnnotTrace.Enabled)
            PdfAnnotTrace.Write($"press right={properties.IsRightButtonPressed} left={properties.IsLeftButtonPressed} source={e.Source?.GetType().Name} page={page} point={point.X:F1},{point.Y:F1} sourceIsHost={e.Source == this}");
        if (e.Source != this) return;
        if (properties.IsRightButtonPressed)
        {
            if (page > 0 && AnnotationAt(point) is { } annotation)
            {
                // Right-clicking a highlight, underline or sticky note opens a
                // small delete menu instead of a full-window confirmation.
                // Right-clicking empty page space only clears the selection.
                PdfAnnotTrace.Write($"right-click hit annotation {annotation.Id} ({annotation.SelectedText})");
                SelectAnnotation(annotation.Id);
                Emit(new { type = "annotationContextMenu", id = annotation.Id, x = point.X, y = point.Y });
            }
            else
            {
                ClearAnnotationSelection();
                Emit(new { type = "annotationBlankClick" });
            }
            e.Handled = true;
            return;
        }
        if (!properties.IsLeftButtonPressed) return;
        Focus();
        if (page == 0)
        {
            ClearSelection();
            Emit(new { type = "annotationBlankClick" });
            return;
        }
        ActivatePage(page);
        EmitPage();
        ClearHover();
        if (_pointAnnotationMode)
        {
            if (AnnotationAt(point) is { } annotation)
                Emit(new { type = "annotationClick", id = annotation.Id });
            else
                EmitPointAnnotation(page, point);
            e.Handled = true;
            return;
        }
        // A press that lands on a highlight/underline still starts a normal
        // drag so text can be re-selected over existing markings. The press is
        // resolved as an annotation pick on release when no drag happened.
        _pointerAnnotationHit = AnnotationAt(point)?.Id;
        _pointerClickCount = e.ClickCount;
        if (_pointerAnnotationHit is { } doubleHit && e.ClickCount >= 2)
        {
            Emit(new { type = "annotationClick", id = doubleHit });
            _pointerStart = null;
            _pointerAnnotationHit = null;
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
        Cursor = new Cursor(_pointAnnotationMode
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
        if (_pointerStart is null) return;
        var dragged = _dragging;
        // Read the pending pick before releasing the capture: CaptureLost
        // clears it, since losing capture cancels the click.
        var annotationHit = _pointerAnnotationHit;
        if (PdfAnnotTrace.Enabled) PdfAnnotTrace.Write($"release dragged={dragged} hit={annotationHit}");
        _pointerStart = null;
        _dragging = false;
        _pointerAnnotationHit = null;
        e.Pointer.Capture(null);
        if (dragged) SelectRange(_selectionStart, _selectionEnd);
        else if (annotationHit is { } hitId)
        {
            // Single click picks the highlight/underline so Delete (see
            // HandleReaderKey) or a right click can remove it.
            SelectAnnotation(hitId);
            Emit(new { type = "annotationSelect", id = hitId });
        }
        else
        {
            _selectedAnnotationId = null;
            ClearSelection();
            Emit(new { type = "annotationBlankClick" });
        }
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
        if (e.Key == Key.Delete && e.KeyModifiers == KeyModifiers.None && _selectedAnnotationId is { } id)
        {
            Emit(new { type = "pdfAnnotationDelete", id });
            e.Handled = true;
            return;
        }
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta))
        {
            if (e.Key == Key.A) { SelectRange(0, PageContent?.Text.Length ?? 0); e.Handled = true; }
            else if (e.Key == Key.C && _selectionEnd > _selectionStart) { Emit(new { type = "selectionAction", action = "copy" }); e.Handled = true; }
            else if (e.Key is Key.D0 or Key.NumPad0) { _ = SetFitModeAsync(PdfReaderFitMode.Width); e.Handled = true; }
        }
        else if (e.Key == Key.Escape)
        {
            if (_pointAnnotationMode)
            {
                _pointAnnotationMode = false;
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
