using System.Text.Json;
using Kkindle.Core;

namespace Kkindle.Infrastructure;

internal static class ReaderContentPositionJson
{
    public static string? Serialize(ReaderContentPosition? position) =>
        ReaderContentPosition.Validate(position) is { } valid ? JsonSerializer.Serialize(valid) : null;

    public static ReaderContentPosition? Deserialize(string? json)
    {
        // Positions are small optional metadata. A damaged row or a newer
        // format must not prevent the legacy progress/bookmark from loading.
        if (string.IsNullOrWhiteSpace(json) || json.Length > 4096) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty(nameof(ReaderContentPosition.Version), out var version)
                || !version.TryGetInt32(out var number)
                || number != ReaderContentPosition.CurrentVersion) return null;
            return ReaderContentPosition.Validate(document.RootElement.Deserialize<ReaderContentPosition>());
        }
        catch (JsonException) { return null; }
        catch (InvalidOperationException) { return null; }
    }
}
