using System.Text.Json;
using Kkindle.Core;

namespace Kkindle.Infrastructure;

/// <summary>Local libraries and sync credentials cannot travel between regions.</summary>
public static class AccountRegionPaths
{
    public static AppPaths ForRegion(AppPaths installation, AccountRegion region) => region switch
    {
        // Preserve the existing installation's library as the China library.
        AccountRegion.China => installation,
        AccountRegion.International => new(Path.Combine(installation.Root, "regions", "international")),
        _ => throw new ArgumentOutOfRangeException(nameof(region))
    };

    public static AccountRegion ReadActiveRegion(AppPaths installation)
    {
        var path = Path.Combine(installation.Data, "account-session.json");
        if (!File.Exists(path)) return AccountRegion.China;
        try
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            using var document = JsonDocument.Parse(input);
            if (document.RootElement.TryGetProperty("activeRegion", out var property)
                && property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var region) && Enum.IsDefined((AccountRegion)region))
                return (AccountRegion)region;
        }
        catch (JsonException) { }
        return AccountRegion.China;
    }
}
