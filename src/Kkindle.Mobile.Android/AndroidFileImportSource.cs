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
        try
        {
            foreach (var uri in uris)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = SafeFileName(activity.GetDisplayName(uri));
                await using var input = activity.ContentResolver?.OpenInputStream(uri);
                if (input is null) continue;
                var fileDirectory = Path.Combine(directory, Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(fileDirectory);
                var target = Path.Combine(fileDirectory, name);
                files.Add(target);
                await using var output = File.Create(target);
                await input.CopyToAsync(output, cancellationToken);
            }
            return files;
        }
        catch
        {
            ReleaseFiles(files);
            throw;
        }
    }

    public void ReleaseFiles(IReadOnlyList<string> files)
    {
        var activity = MainActivity.Current;
        if (activity?.CacheDir is null) return;
        var root = Path.GetFullPath(Path.Combine(activity.CacheDir.AbsolutePath, "imports")) + Path.DirectorySeparatorChar;
        foreach (var file in files)
        {
            var fullPath = Path.GetFullPath(file);
            if (!fullPath.StartsWith(root, StringComparison.Ordinal)) continue;
            try { File.Delete(fullPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            var parent = Path.GetDirectoryName(fullPath);
            if (parent is null || !parent.StartsWith(root, StringComparison.Ordinal)) continue;
            try { Directory.Delete(parent); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static string SafeFileName(string? name)
    {
        var trimmed = Path.GetFileName(name);
        if (string.IsNullOrWhiteSpace(trimmed)) trimmed = $"import-{Guid.NewGuid():N}";
        foreach (var invalid in Path.GetInvalidFileNameChars())
            trimmed = trimmed.Replace(invalid, '_');
        return trimmed;
    }

}
