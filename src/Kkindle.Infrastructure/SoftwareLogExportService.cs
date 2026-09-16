using System.IO.Compression;
using System.Text;

namespace Kkindle.Infrastructure;

/// <summary>
/// Packages runtime logs for support without copying the application's
/// settings, library, database, backups, or browser data.
/// </summary>
public sealed class SoftwareLogExportService
{
    private sealed record LogSource(string FilePath, string ArchivePath);
    private sealed record IncludedLog(string ArchivePath, long Length);

    public Task ExportAsync(
        AppPaths paths,
        string destinationPath,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        return Task.Run(
            () => Export(paths, destinationPath, metadata, cancellationToken),
            cancellationToken);
    }

    private static void Export(
        AppPaths paths,
        string destinationPath,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken cancellationToken)
    {
        var sources = DiscoverLogFiles(paths, out var discoveryWarnings);
        var included = new List<IncludedLog>();
        var skipped = new List<string>(discoveryWarnings);
        var destination = Path.GetFullPath(destinationPath);
        var destinationDirectory = Path.GetDirectoryName(destination);
        if (string.IsNullOrWhiteSpace(destinationDirectory))
            throw new InvalidOperationException("The selected export path has no parent directory.");

        Directory.CreateDirectory(destinationDirectory);
        var temporaryPath = $"{destination}.${Guid.NewGuid():N}.tmp";
        try
        {
            using (var output = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       64 * 1024,
                       FileOptions.SequentialScan))
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create))
            {
                foreach (var source in sources)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var length = AddFileEntry(archive, source, cancellationToken);
                        included.Add(new IncludedLog(source.ArchivePath, length));
                    }
                    catch (IOException exception)
                    {
                        skipped.Add($"{source.ArchivePath} ({exception.GetType().Name})");
                    }
                    catch (UnauthorizedAccessException exception)
                    {
                        skipped.Add($"{source.ArchivePath} ({exception.GetType().Name})");
                    }
                }

                WriteTextEntry(
                    archive,
                    "diagnostics.txt",
                    BuildDiagnosticsText(metadata, included, skipped));
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                try { File.Delete(temporaryPath); }
                catch { /* Preserve the original export error. */ }
            }
        }
    }

    private static List<LogSource> DiscoverLogFiles(AppPaths paths, out List<string> warnings)
    {
        warnings = [];
        var sources = new List<LogSource>();
        var archivePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddIfExists(
            sources,
            archivePaths,
            Path.Combine(paths.Root, "kkindle-crash.log"),
            "logs/kkindle-crash.log");

        if (!Directory.Exists(paths.Logs)) return sources;

        try
        {
            foreach (var filePath in Directory.EnumerateFiles(
                         paths.Logs,
                         "*.log",
                         SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                var relativePath = Path.GetRelativePath(paths.Logs, filePath);
                var archivePath = ToArchivePath("logs", relativePath);
                if (archivePaths.Add(archivePath))
                    sources.Add(new LogSource(filePath, archivePath));
            }
        }
        catch (IOException exception)
        {
            warnings.Add($"log directory enumeration ({exception.GetType().Name})");
        }
        catch (UnauthorizedAccessException exception)
        {
            warnings.Add($"log directory enumeration ({exception.GetType().Name})");
        }

        return sources;
    }

    private static void AddIfExists(
        ICollection<LogSource> sources,
        ISet<string> archivePaths,
        string filePath,
        string archivePath)
    {
        if (File.Exists(filePath) && archivePaths.Add(archivePath))
            sources.Add(new LogSource(filePath, archivePath));
    }

    private static string ToArchivePath(string prefix, string relativePath)
    {
        var segments = relativePath
            .Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries)
            .Where(segment => segment is not "." and not "..")
            .Select(segment => segment.Replace(":", "_", StringComparison.Ordinal));
        return $"{prefix}/{string.Join('/', segments)}";
    }

    private static long AddFileEntry(
        ZipArchive archive,
        LogSource source,
        CancellationToken cancellationToken)
    {
        using var input = new FileStream(
            source.FilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            64 * 1024,
            FileOptions.SequentialScan);
        using var entry = archive.CreateEntry(source.ArchivePath, CompressionLevel.Fastest).Open();
        input.CopyTo(entry, 64 * 1024);
        cancellationToken.ThrowIfCancellationRequested();
        return input.Length;
    }

    private static void WriteTextEntry(ZipArchive archive, string name, string content)
    {
        using var writer = new StreamWriter(
            archive.CreateEntry(name, CompressionLevel.Fastest).Open(),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    private static string BuildDiagnosticsText(
        IReadOnlyDictionary<string, string>? metadata,
        IReadOnlyList<IncludedLog> included,
        IReadOnlyList<string> skipped)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Kkindle software log export / 软件日志导出");
        builder.AppendLine($"Generated at (UTC): {DateTimeOffset.UtcNow:O}");
        builder.AppendLine();
        builder.AppendLine("Environment / 环境");
        if (metadata is not null)
        {
            foreach (var item in metadata.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
                builder.AppendLine($"{Clean(item.Key)}: {Clean(item.Value)}");
        }

        builder.AppendLine();
        builder.AppendLine("Included log files / 已包含日志");
        if (included.Count == 0)
            builder.AppendLine("(none / 无)");
        else
        {
            foreach (var item in included)
                builder.AppendLine($"{item.ArchivePath} ({item.Length} bytes)");
        }

        if (skipped.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Skipped items / 未能读取的项目");
            foreach (var item in skipped)
                builder.AppendLine(Clean(item));
        }

        builder.AppendLine();
        builder.AppendLine(
            "Excluded by design: application settings and secrets (including email/SMTP passwords and API keys), database, book files, backups, and browser data.");
        builder.AppendLine(
            "Log contents may still contain book titles or local file paths; review the archive before sharing it.");
        return builder.ToString();
    }

    private static string Clean(string value) =>
        (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ');
}
