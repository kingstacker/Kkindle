using System.Runtime.InteropServices;
using System.Text;

namespace Kkindle.Infrastructure;

public sealed record PdfPageLink(PdfTextBounds Bounds, int? PageNumber = null,
    double? Left = null, double? Top = null, double? Zoom = null, string? Uri = null);

public sealed partial class PdfDocumentService
{
    private string[] ReadPageLabels(CancellationToken token)
    {
        var labels = new string[PageCount];
        for (var page = 0; page < PageCount; page++)
        {
            token.ThrowIfCancellationRequested();
            var length = Native.FPDF_GetPageLabel(_document, page, null, new CULong(0)).Value;
            if (length is <= 2 or > 4096) { labels[page] = string.Empty; continue; }
            var buffer = new byte[(int)length];
            Native.FPDF_GetPageLabel(_document, page, buffer, new CULong(length));
            labels[page] = Encoding.Unicode.GetString(buffer).TrimEnd('\0');
        }
        return labels;
    }

    private IReadOnlyList<PdfPageLink> ReadLinks(IntPtr page, CancellationToken token)
    {
        var links = new List<PdfPageLink>();
        var transform = CreatePageTransform(page);
        var targets = new Dictionary<int, PageTransform>();
        var position = 0;
        while (links.Count < 10_000 && position < 20_000 && Native.FPDFLink_Enumerate(page, ref position, out var link) != 0)
        {
            token.ThrowIfCancellationRequested();
            if (Native.FPDFLink_GetAnnotRect(link, out var rect) == 0) continue;
            var bounds = TransformBounds(transform, rect.Left, rect.Right, rect.Bottom, rect.Top);
            var destination = Native.FPDFLink_GetDest(_document, link);
            var action = Native.FPDFLink_GetAction(link);
            var type = action == IntPtr.Zero ? 0 : Native.FPDFAction_GetType(action);
            if (destination == IntPtr.Zero && type == 1) destination = Native.FPDFAction_GetDest(_document, action);
            var index = destination == IntPtr.Zero ? -1 : Native.FPDFDest_GetDestPageIndex(_document, destination);
            if (index >= 0 && index < PageCount)
            {
                double? left = null, top = null, zoom = null;
                if (Native.FPDFDest_GetLocationInPage(destination, out var hasX, out var hasY, out var hasZoom,
                        out var x, out var y, out var z) != 0)
                {
                    if (GetOutlinePageTransform(index, targets) is { } target)
                    {
                        var point = target.ToDevice(hasX != 0 && float.IsFinite(x) ? x : 0,
                            hasY != 0 && float.IsFinite(y) ? y : 0);
                        // A rotated destination can map PDF x to screen y.
                        if (hasX != 0 || hasY != 0)
                        {
                            if (target.XX != 0 ? hasX != 0 : hasY != 0) left = Math.Clamp(point.X / DeviceUnits, 0, 1);
                            if (target.YY != 0 ? hasY != 0 : hasX != 0) top = Math.Clamp(point.Y / DeviceUnits, 0, 1);
                        }
                    }
                    if (hasZoom != 0 && float.IsFinite(z) && z > 0) zoom = z;
                }
                else
                {
                    var parameters = new float[4];
                    var view = Native.FPDFDest_GetView(destination, out var count, parameters).Value;
                    if (view is 3 or 7 && count.Value > 0 && float.IsFinite(parameters[0])
                        && GetOutlinePageTransform(index, targets) is { } target)
                        top = Math.Clamp(target.ToDevice(0, parameters[0]).Y / DeviceUnits, 0, 1);
                }
                links.Add(new(bounds, index + 1, left, top, zoom));
            }
            else if (type == 3)
            {
                var length = Native.FPDFAction_GetURIPath(_document, action, null, new CULong(0)).Value;
                if (length is <= 1 or > 16_384) continue;
                var buffer = new byte[(int)length];
                Native.FPDFAction_GetURIPath(_document, action, buffer, new CULong(length));
                var uri = Encoding.UTF8.GetString(buffer).TrimEnd('\0');
                if (System.Uri.TryCreate(uri, UriKind.Absolute, out var target) && target.Scheme is "http" or "https" or "mailto")
                    links.Add(new(bounds, Uri: target.AbsoluteUri));
            }
        }
        return links;
    }

    private static partial class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct PdfRect { public float Left, Top, Right, Bottom; }
        [DllImport(Library)] internal static extern CULong FPDF_GetPageLabel(IntPtr document, int index, [Out] byte[]? buffer, CULong length);
        [DllImport(Library)] internal static extern int FPDFLink_Enumerate(IntPtr page, ref int position, out IntPtr link);
        [DllImport(Library)] internal static extern int FPDFLink_GetAnnotRect(IntPtr link, out PdfRect rect);
        [DllImport(Library)] internal static extern IntPtr FPDFLink_GetDest(IntPtr document, IntPtr link);
        [DllImport(Library)] internal static extern IntPtr FPDFLink_GetAction(IntPtr link);
        [DllImport(Library)] internal static extern CULong FPDFAction_GetURIPath(IntPtr document, IntPtr action, [Out] byte[]? buffer, CULong length);
    }
}
