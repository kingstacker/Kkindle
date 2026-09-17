namespace Kkindle.Core;

/// <summary>
/// A user-selected rectangle in a PDF page. Coordinates are normalized to
/// the page's unrotated PDFium surface so the selection survives zoom,
/// rotation and view-mode changes.
/// </summary>
public sealed record PdfRegionSelection(
    int PageNumber,
    double X,
    double Y,
    double Width,
    double Height)
{
    public PdfRegionSelection Normalize()
    {
        var x = Math.Clamp(double.IsFinite(X) ? X : 0, 0, 0.99);
        var y = Math.Clamp(double.IsFinite(Y) ? Y : 0, 0, 0.99);
        var width = Math.Clamp(double.IsFinite(Width) ? Width : 0, 0.01, 1 - x);
        var height = Math.Clamp(double.IsFinite(Height) ? Height : 0, 0.01, 1 - y);
        return this with { X = x, Y = y, Width = width, Height = height };
    }
}

/// <summary>
/// An image payload for an OpenAI-compatible multimodal request. The image
/// remains in memory and is never persisted by the reader.
/// </summary>
public sealed record AiImageAttachment(
    string MimeType,
    byte[] Data,
    string Detail = "high")
{
    public string DataUri =>
        $"data:{MimeType};base64,{Convert.ToBase64String(Data)}";
}
