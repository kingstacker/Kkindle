namespace Kkindle.Core;

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
