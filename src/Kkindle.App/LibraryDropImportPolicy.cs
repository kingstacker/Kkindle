using Avalonia.Input;
using Avalonia.Platform.Storage;
using System.Text;

namespace Kkindle;

internal static class LibraryDropImportPolicy
{
    private static readonly HashSet<string> SupportedExtensions =
        new([".epub", ".pdf", ".mobi", ".azw3"], StringComparer.OrdinalIgnoreCase);

    public static bool CanAccept(IDataTransfer dataTransfer) =>
        dataTransfer.Contains(DataFormat.File)
        || dataTransfer.Formats.Any(IsLinuxUriListFormat);

    public static string[] GetLocalPaths(IDataTransfer dataTransfer)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in dataTransfer.TryGetFiles() ?? [])
            AddStorageItemPath(paths, item);

        // Some Linux desktop/file-manager combinations expose external drops
        // as raw text/uri-list instead of Avalonia's universal File format.
        foreach (var item in dataTransfer.Items)
        {
            foreach (var format in item.Formats.Where(IsLinuxUriListFormat))
            {
                var value = item.TryGetRaw(format);
                switch (value)
                {
                    case IStorageItem storageItem:
                        AddStorageItemPath(paths, storageItem);
                        break;
                    case IEnumerable<IStorageItem> storageItems:
                        foreach (var storage in storageItems)
                            AddStorageItemPath(paths, storage);
                        break;
                    case string uriList:
                        AddUriListPaths(paths, uriList);
                        break;
                    case byte[] bytes:
                        AddUriListPaths(paths, Encoding.UTF8.GetString(bytes));
                        break;
                }
            }
        }

        return paths.OrderBy(path => path, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public static string[] ExpandImportableFiles(IEnumerable<string> selectedPaths)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var selectedPath in selectedPaths)
        {
            try
            {
                var fullPath = Path.GetFullPath(selectedPath);
                if (File.Exists(fullPath))
                {
                    if (SupportedExtensions.Contains(Path.GetExtension(fullPath)))
                        files.Add(fullPath);
                    continue;
                }

                if (!Directory.Exists(fullPath)) continue;
                foreach (var file in Directory.EnumerateFiles(fullPath, "*", SearchOption.AllDirectories))
                {
                    if (SupportedExtensions.Contains(Path.GetExtension(file)))
                        files.Add(Path.GetFullPath(file));
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return files.OrderBy(path => path, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private static string? GetLocalPath(IStorageItem item)
    {
        var localPath = item.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(localPath)) return localPath;

        // Linux file managers can expose a dropped directory as a file URI
        // without a FileSystemInfo-backed storage item.
        var uri = item.Path;
        return uri.IsAbsoluteUri && uri.IsFile
            ? Uri.UnescapeDataString(uri.LocalPath)
            : null;
    }

    private static bool IsLinuxUriListFormat(DataFormat format) =>
        string.Equals(format.Identifier, "text/uri-list", StringComparison.OrdinalIgnoreCase)
        || string.Equals(format.Identifier, "x-special/gnome-copied-files", StringComparison.OrdinalIgnoreCase);

    private static void AddStorageItemPath(ISet<string> paths, IStorageItem item)
    {
        var path = GetLocalPath(item);
        if (!string.IsNullOrWhiteSpace(path)) paths.Add(Path.GetFullPath(path));
    }

    private static void AddUriListPaths(ISet<string> paths, string uriList)
    {
        foreach (var rawLine in uriList.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim().TrimEnd('\0');
            if (line.Length == 0
                || line[0] == '#'
                || string.Equals(line, "copy", StringComparison.OrdinalIgnoreCase)
                || string.Equals(line, "cut", StringComparison.OrdinalIgnoreCase))
                continue;

            if (Uri.TryCreate(line, UriKind.Absolute, out var uri) && uri.IsFile)
            {
                paths.Add(Path.GetFullPath(Uri.UnescapeDataString(uri.LocalPath)));
                continue;
            }

            if (Path.IsPathRooted(line) && (File.Exists(line) || Directory.Exists(line)))
                paths.Add(Path.GetFullPath(line));
        }
    }
}
