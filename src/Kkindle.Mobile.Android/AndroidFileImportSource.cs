using Kkindle.Mobile;

namespace Kkindle.AndroidHead;

/// <summary>
/// Copies documents picked through the Storage Access Framework into the app
/// cache so the library, which works with local paths, can import them.
/// </summary>
public sealed class AndroidFileImportSource : IFileImportSource
{
    public async Task<IReadOnlyList<string>> PickFilesAsync(CancellationToken cancellationToken = default)
    {
        var activity = MainActivity.Current
            ?? throw new InvalidOperationException("The main activity is not running.");
        var uris = await activity.PickDocumentsAsync();
        if (uris.Count == 0) return [];

        var directory = Path.Combine(activity.CacheDir!.AbsolutePath, "imports");
        Directory.CreateDirectory(directory);

        var files = new List<string>(uris.Count);
        foreach (var uri in uris)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = SafeFileName(activity.GetDisplayName(uri));
            var target = UniquePath(directory, name);

            await using var input = activity.ContentResolver?.OpenInputStream(uri);
            if (input is null) continue;
            await using var output = File.Create(target);
            await input.CopyToAsync(output, cancellationToken);
            files.Add(target);
        }

        return files;
    }

    private static string SafeFileName(string? name)
    {
        var trimmed = Path.GetFileName(name);
        if (string.IsNullOrWhiteSpace(trimmed)) trimmed = $"import-{Guid.NewGuid():N}";
        foreach (var invalid in Path.GetInvalidFileNameChars())
            trimmed = trimmed.Replace(invalid, '_');
        return trimmed;
    }

    private static string UniquePath(string directory, string fileName)
    {
        var target = Path.Combine(directory, fileName);
        if (!File.Exists(target)) return target;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        return Path.Combine(directory, $"{stem}-{Guid.NewGuid():N}{extension}");
    }
}
