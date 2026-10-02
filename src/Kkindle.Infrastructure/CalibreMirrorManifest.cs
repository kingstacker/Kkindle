using System.Text.Json.Serialization;

namespace Kkindle.Infrastructure;

internal enum CalibreMirrorTarget { WindowsMsi, MacOsDmg, KfxInputPlugin }

internal sealed class CalibreMirrorManifest
{
    [JsonPropertyName("version")]
    public string? Version { get; init; }
    [JsonPropertyName("files")]
    public CalibreMirrorFile[] Files { get; init; } = [];

    internal CalibreMirrorFile? SelectFile(CalibreMirrorTarget target)
    {
        var files = Files ?? [];
        if (target == CalibreMirrorTarget.WindowsMsi
            && files.FirstOrDefault(file => file.Name == $"calibre-64bit-{Version}.msi") is { } exact)
            return exact;
        return files.FirstOrDefault(file => file.Name is { } name && target switch
        {
            CalibreMirrorTarget.WindowsMsi => name.StartsWith("calibre-64bit-", StringComparison.Ordinal) && name.EndsWith(".msi", StringComparison.Ordinal),
            CalibreMirrorTarget.MacOsDmg => name.StartsWith("calibre-", StringComparison.Ordinal) && name.EndsWith(".dmg", StringComparison.Ordinal) && !name.Contains("portable", StringComparison.OrdinalIgnoreCase),
            CalibreMirrorTarget.KfxInputPlugin => name.StartsWith("kfx-input-", StringComparison.Ordinal) && name.EndsWith(".zip", StringComparison.Ordinal),
            _ => false
        });
    }
}

internal sealed class CalibreMirrorFile
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }
    [JsonPropertyName("size")]
    public long Size { get; init; }
    [JsonPropertyName("sha256")]
    public string? Sha256 { get; init; }
    [JsonPropertyName("url")]
    public string? Url { get; init; }
    [JsonPropertyName("official_url")]
    public string? OfficialUrl { get; init; }

    internal Uri GetDownloadUri()
    {
        if (Size <= 0 || Sha256 is not { Length: 64 } || !Sha256.All(Uri.IsHexDigit)
            || string.IsNullOrWhiteSpace(Url))
            throw new InvalidDataException("Calibre 镜像下载项无效。");
        var value = Url.StartsWith('/') ? "https://kkindle.stacker.beauty" + Url : Url;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !uri.IdnHost.Equals("kkindle.stacker.beauty", StringComparison.OrdinalIgnoreCase)
            || !uri.IsDefaultPort || uri.UserInfo.Length != 0)
            throw new InvalidDataException("Calibre 镜像地址无效。");
        return uri;
    }
}
