using System.Runtime.InteropServices;
using System.Text;
using SkiaSharp;

namespace Kkindle.Infrastructure;

// Coordinates are fractions of the displayed page, with a top-left origin.
// PDFium performs the crop/rotation transform for both text and rendering.
public readonly record struct PdfTextBounds(double X, double Y, double Width, double Height);
public sealed record PdfTextGlyph(int Offset, int Length, PdfTextBounds Bounds, double AdvanceX = 1, double AdvanceY = 0);
public sealed record PdfPageContent(double Width, double Height, string Text, IReadOnlyList<PdfTextGlyph> Glyphs);
public sealed record PdfOutlineItem(string Title, int PageNumber, int Level, double? Top = null);
public sealed record PdfDocumentInfo(int PageCount, string Title, string Author, IReadOnlyList<PdfOutlineItem> Outline);
public sealed record PdfRenderedPage(int PageNumber, PdfPageContent Content, byte[] Png);
public sealed record PdfRasterRegion(int PageNumber, int PagePixelWidth, int PagePixelHeight,
    int Left, int Top, int Width, int Height, byte[] Pixels, int Rotation = 0);

/// <summary>
/// Owns a PDFium document. All native calls share one gate: PDFium is not
/// thread-safe, including calls made for different books or library covers.
/// Rendering and text extraction use the same engine and UTF-16 offset stream.
/// No PDF JavaScript, external actions or embedded attachments are executed.
/// </summary>
public sealed class PdfDocumentService : IDisposable
{
    private static readonly object NativeGate = new();
    private static bool _initialized;
    private IntPtr _document;

    public PdfDocumentService(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        lock (NativeGate)
        {
            VerifyRuntime();
            _document = Native.FPDF_LoadDocument(Path.GetFullPath(path), null);
            if (_document == IntPtr.Zero)
            {
                var error = Native.FPDF_GetLastError();
                throw new InvalidDataException(error == 4
                    ? "PDF 已加密，请先使用密码解锁文件。"
                    : $"无法读取 PDF 文件（错误 {error}）。");
            }
            PageCount = Native.FPDF_GetPageCount(_document);
            if (PageCount <= 0)
            {
                Dispose();
                throw new InvalidDataException("PDF 没有可读取的页面。");
            }
        }
    }

    public int PageCount { get; }

    public static void VerifyRuntime()
    {
        lock (NativeGate)
        {
            if (_initialized) return;
            Native.FPDF_InitLibrary();
            _initialized = true;
        }
    }

    public PdfDocumentInfo ReadInfo(CancellationToken cancellationToken = default)
    {
        lock (NativeGate)
        {
            CheckOpen(cancellationToken);
            var outline = new List<PdfOutlineItem>();
            var visited = new HashSet<IntPtr>();
            ReadOutline(IntPtr.Zero, 0, outline, visited, cancellationToken);
            return new PdfDocumentInfo(PageCount, ReadMetadata("Title"), ReadMetadata("Author"), outline);
        }
    }

    public PdfPageContent ReadPage(int pageNumber, CancellationToken cancellationToken = default)
    {
        lock (NativeGate)
        {
            CheckOpen(cancellationToken);
            var page = LoadPage(pageNumber);
            try { return ReadPageContent(page, cancellationToken); }
            finally { Native.FPDF_ClosePage(page); }
        }
    }

    public string ReadPageText(int pageNumber, CancellationToken cancellationToken = default)
    {
        lock (NativeGate)
        {
            CheckOpen(cancellationToken);
            var page = LoadPage(pageNumber);
            try { return ReadPageContent(page, cancellationToken, includeGeometry: false).Text; }
            finally { Native.FPDF_ClosePage(page); }
        }
    }

    // Render directly from the PDF display list at the requested device scale.
    // Clipping keeps memory proportional to the viewport, even at high zoom.
    // The reader consumes BGRA pixels directly, without a PNG encode/decode.
    public PdfRasterRegion RenderRegion(int pageNumber, int pagePixelWidth, int pagePixelHeight,
        int left, int top, int width, int height, CancellationToken cancellationToken = default, int rotation = 0)
    {
        if (rotation is not (0 or 90 or 180 or 270)) throw new ArgumentOutOfRangeException(nameof(rotation));
        if (pagePixelWidth is < 1 or > 1_000_000 || pagePixelHeight is < 1 or > 1_000_000
            || left < 0 || top < 0 || width < 1 || height < 1
            || (long)left + width > pagePixelWidth || (long)top + height > pagePixelHeight
            || (long)width * height > 16_000_000)
            throw new ArgumentOutOfRangeException(nameof(width), "PDF render region is too large or outside the page.");
        lock (NativeGate)
        {
            CheckOpen(cancellationToken);
            var page = LoadPage(pageNumber);
            try
            {
                var pixels = GC.AllocateUninitializedArray<byte>(checked(width * height * 4));
                var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
                try
                {
                    var bitmap = Native.FPDFBitmap_CreateEx(width, height, 4, handle.AddrOfPinnedObject(), width * 4);
                    if (bitmap == IntPtr.Zero) throw new InvalidOperationException("无法创建 PDF 页面图像。");
                    try
                    {
                        Native.FPDFBitmap_FillRect(bitmap, 0, 0, width, height, 0xffffffff);
                        // Annotations + LCD text hinting on the opaque, pixel-aligned surface.
                        Native.FPDF_RenderPageBitmap(bitmap, page, -left, -top, pagePixelWidth, pagePixelHeight, rotation / 90, 3);
                        cancellationToken.ThrowIfCancellationRequested();
                        return new(pageNumber, pagePixelWidth, pagePixelHeight, left, top, width, height, pixels, rotation);
                    }
                    finally { Native.FPDFBitmap_Destroy(bitmap); }
                }
                finally { handle.Free(); }
            }
            finally { Native.FPDF_ClosePage(page); }
        }
    }

    public PdfRenderedPage RenderPage(
        int pageNumber,
        int maxWidth = 1600,
        int maxHeight = 2000,
        CancellationToken cancellationToken = default)
    {
        lock (NativeGate)
        {
            CheckOpen(cancellationToken);
            var page = LoadPage(pageNumber);
            try
            {
                var content = ReadPageContent(page, cancellationToken);
                var scale = Math.Min(Math.Clamp(maxWidth, 1, 8192) / content.Width,
                    Math.Clamp(maxHeight, 1, 8192) / content.Height);
                // Bound raster memory even for extreme zoom or malformed sizes.
                scale = Math.Min(scale, Math.Sqrt(16_000_000 / (content.Width * content.Height)));
                var width = Math.Max(1, (int)Math.Ceiling(content.Width * scale));
                var height = Math.Max(1, (int)Math.Ceiling(content.Height * scale));
                using var pixels = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
                var bitmap = Native.FPDFBitmap_CreateEx(width, height, 4, pixels.GetPixels(), pixels.RowBytes);
                if (bitmap == IntPtr.Zero) throw new InvalidOperationException("无法创建 PDF 页面图像。");
                try
                {
                    Native.FPDFBitmap_FillRect(bitmap, 0, 0, width, height, 0xffffffff);
                    // Include existing PDF annotations, but never interactive form actions.
                    Native.FPDF_RenderPageBitmap(bitmap, page, 0, 0, width, height, 0, 1);
                    cancellationToken.ThrowIfCancellationRequested();
                    using var image = SKImage.FromBitmap(pixels);
                    using var png = image.Encode(SKEncodedImageFormat.Png, 100);
                    return new PdfRenderedPage(pageNumber, content, png.ToArray());
                }
                finally { Native.FPDFBitmap_Destroy(bitmap); }
            }
            finally { Native.FPDF_ClosePage(page); }
        }
    }

    private IntPtr LoadPage(int pageNumber)
    {
        if (pageNumber < 1 || pageNumber > PageCount) throw new ArgumentOutOfRangeException(nameof(pageNumber));
        var page = Native.FPDF_LoadPage(_document, pageNumber - 1);
        return page != IntPtr.Zero ? page : throw new InvalidDataException($"无法读取 PDF 第 {pageNumber} 页。");
    }

    private static PdfPageContent ReadPageContent(IntPtr page, CancellationToken cancellationToken, bool includeGeometry = true)
    {
        var width = Native.FPDF_GetPageWidth(page);
        var height = Native.FPDF_GetPageHeight(page);
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
            throw new InvalidDataException("PDF 页面尺寸无效。");
        var textPage = Native.FPDFText_LoadPage(page);
        if (textPage == IntPtr.Zero) return new(width, height, string.Empty, []);
        try
        {
            var text = new StringBuilder();
            var glyphs = new List<PdfTextGlyph>();
            var count = Native.FPDFText_CountChars(textPage);
            // A corrupt page must not exhaust the process with billions of characters.
            if (count > 2_000_000) throw new InvalidDataException("PDF 单页文本过大。");
            for (var index = 0; index < count; index++)
            {
                if (index % 256 == 0) cancellationToken.ThrowIfCancellationRequested();
                var codePoint = Native.FPDFText_GetUnicode(textPage, index);
                if (codePoint == 0 || codePoint > 0x10ffff || codePoint is >= 0xd800 and <= 0xdfff) continue;
                var value = char.ConvertFromUtf32((int)codePoint);
                var offset = text.Length;
                text.Append(value);
                if (!includeGeometry || string.IsNullOrWhiteSpace(value)) continue;
                if (Native.FPDFText_GetCharBox(textPage, index, out var left, out var right, out var bottom, out var top) == 0)
                    continue;
                var bounds = TransformBounds(page, left, right, bottom, top);
                if (bounds.Width > 0 && bounds.Height > 0)
                {
                    var angle = Native.FPDFText_GetCharAngle(textPage, index);
                    Native.FPDF_PageToDevice(page, 0, 0, 100_000, 100_000, 0, left, bottom, out var x1, out var y1);
                    Native.FPDF_PageToDevice(page, 0, 0, 100_000, 100_000, 0,
                        left + Math.Cos(angle), bottom + Math.Sin(angle), out var x2, out var y2);
                    glyphs.Add(new PdfTextGlyph(offset, value.Length, bounds, (x2 - x1) / 100_000d, (y2 - y1) / 100_000d));
                }
            }
            return new(width, height, text.ToString(), glyphs);
        }
        finally { Native.FPDFText_ClosePage(textPage); }
    }

    private static PdfTextBounds TransformBounds(IntPtr page, double left, double right, double bottom, double top)
    {
        const int units = 100_000;
        Native.FPDF_PageToDevice(page, 0, 0, units, units, 0, left, top, out var x1, out var y1);
        Native.FPDF_PageToDevice(page, 0, 0, units, units, 0, right, bottom, out var x2, out var y2);
        return new(Math.Min(x1, x2) / (double)units, Math.Min(y1, y2) / (double)units,
            Math.Abs(x2 - x1) / (double)units, Math.Abs(y2 - y1) / (double)units);
    }

    private string ReadMetadata(string tag)
    {
        var length = Native.FPDF_GetMetaText(_document, tag, null, 0);
        if (length <= 2 || length > 1_000_000) return string.Empty;
        var buffer = new byte[length];
        Native.FPDF_GetMetaText(_document, tag, buffer, length);
        return Encoding.Unicode.GetString(buffer).TrimEnd('\0').Trim();
    }

    private void ReadOutline(IntPtr parent, int level, List<PdfOutlineItem> items,
        HashSet<IntPtr> visited, CancellationToken cancellationToken)
    {
        if (level > 64) return;
        for (var node = Native.FPDFBookmark_GetFirstChild(_document, parent);
             node != IntPtr.Zero && visited.Count < 20_000 && visited.Add(node);
             node = Native.FPDFBookmark_GetNextSibling(_document, node))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = Native.FPDFBookmark_GetDest(_document, node);
            if (destination == IntPtr.Zero)
            {
                var action = Native.FPDFBookmark_GetAction(node);
                if (action != IntPtr.Zero && Native.FPDFAction_GetType(action) == 1)
                    destination = Native.FPDFAction_GetDest(_document, action);
            }
            var pageIndex = destination != IntPtr.Zero ? Native.FPDFDest_GetDestPageIndex(_document, destination) : -1;
            var length = Native.FPDFBookmark_GetTitle(node, null, 0);
            var title = string.Empty;
            if (length > 2 && length < 100_000)
            {
                var bytes = new byte[length];
                Native.FPDFBookmark_GetTitle(node, bytes, length);
                title = Encoding.Unicode.GetString(bytes).TrimEnd('\0').Trim();
            }
            var itemIndex = items.Count;
            var added = false;
            if (pageIndex >= 0 && pageIndex < PageCount && title.Length > 0)
            {
                if (title.Length > 0)
                {
                    double? top = null;
                    var hasY = Native.FPDFDest_GetLocationInPage(destination, out _, out var presentY, out _, out _, out var y, out _) != 0 && presentY != 0;
                    if (!hasY)
                    {
                        var parameters = new float[4];
                        var view = Native.FPDFDest_GetView(destination, out var parameterCount, parameters);
                        if ((view.Value == 3 || view.Value == 7) && parameterCount.Value > 0)
                        {
                            y = parameters[0];
                            hasY = true;
                        }
                    }
                    if (hasY && float.IsFinite(y))
                    {
                        var page = Native.FPDF_LoadPage(_document, pageIndex);
                        if (page != IntPtr.Zero)
                        {
                            try
                            {
                                var bounds = TransformBounds(page, 0, 0, y, y);
                                top = Math.Clamp(bounds.Y, 0, 1);
                            }
                            finally { Native.FPDF_ClosePage(page); }
                        }
                    }
                    items.Add(new(title, pageIndex + 1, level, top));
                    added = true;
                }
            }
            ReadOutline(node, title.Length > 0 ? level + 1 : level, items, visited, cancellationToken);
            // A heading without its own destination still groups children and
            // navigates to the first child, like a container in an EPUB TOC.
            if (!added && title.Length > 0 && items.Count > itemIndex)
                items.Insert(itemIndex, new(title, items[itemIndex].PageNumber, level, items[itemIndex].Top));
        }
    }

    private void CheckOpen(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_document == IntPtr.Zero, this);
    }

    public void Dispose()
    {
        lock (NativeGate)
        {
            if (_document == IntPtr.Zero) return;
            Native.FPDF_CloseDocument(_document);
            _document = IntPtr.Zero;
        }
        GC.SuppressFinalize(this);
    }

    ~PdfDocumentService() => Dispose();

    private static class Native
    {
        private const string Library = "pdfium";
        // PDFium's public ABI uses stdcall on Windows and the platform default
        // elsewhere. Winapi selects the correct convention on each runtime.
        [DllImport(Library)] internal static extern void FPDF_InitLibrary();
        [DllImport(Library)] internal static extern IntPtr FPDF_LoadDocument([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [MarshalAs(UnmanagedType.LPUTF8Str)] string? password);
        [DllImport(Library)] internal static extern uint FPDF_GetLastError();
        [DllImport(Library)] internal static extern void FPDF_CloseDocument(IntPtr document);
        [DllImport(Library)] internal static extern int FPDF_GetPageCount(IntPtr document);
        [DllImport(Library)] internal static extern IntPtr FPDF_LoadPage(IntPtr document, int index);
        [DllImport(Library)] internal static extern void FPDF_ClosePage(IntPtr page);
        [DllImport(Library)] internal static extern double FPDF_GetPageWidth(IntPtr page);
        [DllImport(Library)] internal static extern double FPDF_GetPageHeight(IntPtr page);
        [DllImport(Library)] internal static extern IntPtr FPDFBitmap_CreateEx(int width, int height, int format, IntPtr buffer, int stride);
        [DllImport(Library)] internal static extern void FPDFBitmap_Destroy(IntPtr bitmap);
        [DllImport(Library)] internal static extern void FPDFBitmap_FillRect(IntPtr bitmap, int left, int top, int width, int height, uint color);
        [DllImport(Library)] internal static extern void FPDF_RenderPageBitmap(IntPtr bitmap, IntPtr page, int x, int y, int width, int height, int rotate, int flags);
        [DllImport(Library)] internal static extern int FPDF_PageToDevice(IntPtr page, int x, int y, int width, int height, int rotate, double pageX, double pageY, out int deviceX, out int deviceY);
        [DllImport(Library)] internal static extern IntPtr FPDFText_LoadPage(IntPtr page);
        [DllImport(Library)] internal static extern void FPDFText_ClosePage(IntPtr textPage);
        [DllImport(Library)] internal static extern int FPDFText_CountChars(IntPtr textPage);
        [DllImport(Library)] internal static extern uint FPDFText_GetUnicode(IntPtr textPage, int index);
        [DllImport(Library)] internal static extern float FPDFText_GetCharAngle(IntPtr textPage, int index);
        [DllImport(Library)] internal static extern int FPDFText_GetCharBox(IntPtr textPage, int index, out double left, out double right, out double bottom, out double top);
        [DllImport(Library)] internal static extern uint FPDF_GetMetaText(IntPtr document, [MarshalAs(UnmanagedType.LPUTF8Str)] string tag, [Out] byte[]? buffer, uint size);
        [DllImport(Library)] internal static extern IntPtr FPDFBookmark_GetFirstChild(IntPtr document, IntPtr bookmark);
        [DllImport(Library)] internal static extern IntPtr FPDFBookmark_GetNextSibling(IntPtr document, IntPtr bookmark);
        [DllImport(Library)] internal static extern uint FPDFBookmark_GetTitle(IntPtr bookmark, [Out] byte[]? buffer, uint size);
        [DllImport(Library)] internal static extern IntPtr FPDFBookmark_GetDest(IntPtr document, IntPtr bookmark);
        [DllImport(Library)] internal static extern IntPtr FPDFBookmark_GetAction(IntPtr bookmark);
        [DllImport(Library)] internal static extern uint FPDFAction_GetType(IntPtr action);
        [DllImport(Library)] internal static extern IntPtr FPDFAction_GetDest(IntPtr document, IntPtr action);
        [DllImport(Library)] internal static extern int FPDFDest_GetDestPageIndex(IntPtr document, IntPtr destination);
        [DllImport(Library)] internal static extern CULong FPDFDest_GetView(IntPtr destination, out CULong count, [Out] float[] parameters);
        [DllImport(Library)] internal static extern int FPDFDest_GetLocationInPage(IntPtr destination, out int hasX, out int hasY, out int hasZoom, out float x, out float y, out float zoom);
    }
}
