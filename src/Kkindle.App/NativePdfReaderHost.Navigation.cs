using System.Globalization;
using Avalonia;
using Avalonia.Media.Imaging;
using Kkindle.Infrastructure;

namespace Kkindle;

public sealed partial class NativePdfReaderHost
{
    private sealed record NavigationPoint(int Page, string State);
    private readonly List<NavigationPoint> _backHistory = [];
    private readonly List<NavigationPoint> _forwardHistory = [];
    private bool _historyBusy;
    public bool CanNavigateBack => !_historyBusy && _backHistory.Count > 0;
    public bool CanNavigateForward => !_historyBusy && _forwardHistory.Count > 0;
    public int? PageNumberOffset { get; private set; }

    private NavigationPoint CaptureNavigationPoint() => new(PageNumber, CaptureViewState());
    public void RememberNavigationPoint()
    {
        if (Source is not null) AddNavigationPoint(CaptureNavigationPoint());
    }
    private void AddNavigationPoint(NavigationPoint point)
    {
        if (_backHistory.LastOrDefault() != point) _backHistory.Add(point);
        if (_backHistory.Count > 100) _backHistory.RemoveAt(0);
        _forwardHistory.Clear();
        Emit(new { type = "pdfHistory" });
    }
    public async Task<bool> NavigateHistoryAsync(int direction, CancellationToken token = default)
    {
        var from = direction < 0 ? _backHistory : _forwardHistory;
        var to = direction < 0 ? _forwardHistory : _backHistory;
        if (_historyBusy || from.Count == 0 || Source is null) return false;
        var point = from[^1];
        var current = CaptureNavigationPoint();
        _historyBusy = true;
        Emit(new { type = "pdfHistory" });
        try
        {
            // Navigate with the destination's layout so a spread also loads
            // its second page before the saved pan is restored.
            var offset = PageNumberOffset;
            RestoreViewSettings(point.State);
            PageNumberOffset = offset;
            if (!await NavigateAsync(new Uri(new Uri(_documentPath!).AbsoluteUri + $"#page={point.Page}"), token, recordHistory: false)) return false;
            var version = _version;
            await RestoreViewStateAsync(point.State);
            PageNumberOffset = offset;
            if (_disposed || token.IsCancellationRequested || version != _version) return false;
            from.RemoveAt(from.Count - 1);
            to.Add(current);
            EmitPage();
            Emit(new { type = "pdfLayout" });
            return true;
        }
        finally
        {
            _historyBusy = false;
            Emit(new { type = "pdfHistory" });
        }
    }

    public string GetPageLabel(int page)
    {
        if (PageNumberOffset is { } offset)
            return page + offset > 0 ? (page + offset).ToString(CultureInfo.InvariantCulture) : "—";
        var labels = CachedDocumentInfo?.PageLabels;
        return labels is not null && page >= 1 && page <= labels.Count && labels[page - 1].Length > 0
            ? labels[page - 1] : page.ToString(CultureInfo.InvariantCulture);
    }
    public string GetPageDescription(int page) => GetPageLabel(page) == page.ToString(CultureInfo.InvariantCulture)
        ? $"文件第 {page} 页" : $"书页 {GetPageLabel(page)} · 文件第 {page} 页";
    public int FindLabeledPage(string label)
    {
        label = label.Trim();
        if (label.Length == 0 || label == "—") return -1;
        for (var page = 1; page <= PageCount; page++)
            if (string.Equals(GetPageLabel(page), label, StringComparison.OrdinalIgnoreCase)) return page;
        return -1;
    }
    public void SetPageNumberOffset(int? offset)
    {
        if (offset is < -1_000_000 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(offset));
        PageNumberOffset = offset;
        Emit(new { type = "pdfLabels" });
    }

    public async Task<Bitmap?> RenderThumbnailAsync(int page, CancellationToken token, int maxWidth = 144, int maxHeight = 180)
    {
        var documentToken = _documentCancellation?.Token ?? new CancellationToken(true);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, documentToken);
        var path = _documentPath;
        var png = await Task.Run(() =>
        {
            lock (_documentGate)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                if (_disposed || path != _documentPath || _document is null) return null;
                return _document.RenderPage(page, maxWidth, maxHeight, cancellation.Token).Png;
            }
        }, cancellation.Token);
        cancellation.Token.ThrowIfCancellationRequested();
        return png is null ? null : new Bitmap(new MemoryStream(png));
    }

    private PdfPageLink? LinkAt(Point point)
    {
        var page = PageAtPoint(point);
        if (page == 0 || GetPageContent(page) is not { } content) return null;
        var normalized = GetNormalizedPagePoint(page, point);
        return content.Links.LastOrDefault(link => new Rect(link.Bounds.X, link.Bounds.Y, link.Bounds.Width, link.Bounds.Height).Contains(normalized));
    }
    private void FollowLink(PdfPageLink link) => Emit(new
    {
        type = "pdfLink", page = link.PageNumber, left = link.Left, top = link.Top, zoom = link.Zoom, uri = link.Uri
    });
}
