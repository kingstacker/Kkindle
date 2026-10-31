namespace Kkindle.ApplicationServices;

/// <summary>A platform-selected file, including files without a local path.</summary>
public sealed record ImportFileSource(
    string Name,
    string? LocalPath,
    Func<CancellationToken, Task<Stream>> OpenReadAsync);

/// <summary>
/// Makes provider-backed files available to the existing path-based importer.
/// The caller owns the returned staging lifetime and must await import before
/// disposing it. Local files are passed through without copying or deletion.
/// </summary>
public sealed class ImportFileStaging : IDisposable
{
    private readonly string? _stagingDirectory;
    private ImportFileStaging(IReadOnlyList<string> paths, string? stagingDirectory)
    {
        Paths = paths;
        _stagingDirectory = stagingDirectory;
    }

    public IReadOnlyList<string> Paths { get; }

    public static async Task<ImportFileStaging> PrepareAsync(
        IEnumerable<ImportFileSource> sources,
        string stagingRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);
        var paths = new List<string>();
        string? directory = null;
        try
        {
            foreach (var source in sources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.IsNullOrWhiteSpace(source.LocalPath))
                {
                    paths.Add(source.LocalPath);
                    continue;
                }

                directory ??= Path.Combine(Path.GetFullPath(stagingRoot), Guid.NewGuid().ToString("N"));
                var itemDirectory = Path.Combine(directory, paths.Count.ToString("D8", System.Globalization.CultureInfo.InvariantCulture));
                Directory.CreateDirectory(itemDirectory);
                var name = SafeFileName(source.Name);
                var path = Path.Combine(itemDirectory, name);
                await using var input = await source.OpenReadAsync(cancellationToken);
                await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                await input.CopyToAsync(output, cancellationToken);
                paths.Add(path);
            }
            return new ImportFileStaging(paths, directory);
        }
        catch
        {
            DeleteStaging(directory);
            throw;
        }
    }

    private static string SafeFileName(string name)
    {
        var leaf = Path.GetFileName((name ?? string.Empty).Replace('\\', '/'));
        var invalid = Path.GetInvalidFileNameChars();
        leaf = new string(leaf.Select(c => invalid.Contains(c) || "<>:\"|?*".Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(leaf) || leaf is "." or "..")
            throw new ArgumentException("The selected file has no usable name.", nameof(name));
        var stem = Path.GetFileNameWithoutExtension(leaf);
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                                    || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                                 && stem[3] is >= '1' and <= '9')
            leaf = "_" + leaf;
        if (leaf.Length > 180)
        {
            var extension = Path.GetExtension(leaf);
            if (extension.Length > 32) extension = string.Empty;
            leaf = leaf[..(180 - extension.Length)] + extension;
        }
        return leaf;
    }

    public void Dispose() => DeleteStaging(_stagingDirectory);

    private static void DeleteStaging(string? directory)
    {
        if (directory is null) return;
        try { Directory.Delete(directory, recursive: true); }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
