using System.Text.Json;

namespace Kkindle.Infrastructure;

public sealed partial class AppBackupService
{
    internal const string ImportJournalFileName = "import-journal.json";
    private static readonly string[] ManagedImportPaths =
        ["library", "covers", "trash", AiSettingsPath, KindleEmailSettingsPath, S3SyncSettingsPath];

    internal enum ImportPhase { Preparing, Replacing, Committed, Restored }

    internal sealed class ImportJournal
    {
        public int Version { get; set; } = 1;
        public ImportPhase Phase { get; set; }
        public string StagingDirectory { get; set; } = "";
        public List<string> OriginalPaths { get; set; } = [];
        public string? PreviousBaselineResetMarker { get; set; }
    }

    // Call under AppDataProcessLock, before opening or migrating the live DB.
    internal static async Task RecoverPendingImportsAsync(AppPaths paths, CancellationToken token)
    {
        if (!Directory.Exists(paths.Data)) return;
        foreach (var root in Directory.GetDirectories(paths.Data, ".kkindle-rollback-*"))
        {
            ValidateWorkingDirectory(paths, root, ".kkindle-rollback-");
            var journalPath = Path.Combine(root, ImportJournalFileName);
            if (!File.Exists(journalPath)) continue; // Preserve pre-journal recovery copies.
            var journal = JsonSerializer.Deserialize<ImportJournal>(await File.ReadAllTextAsync(journalPath, token), JsonOptions)
                ?? throw new InvalidDataException($"Invalid backup import recovery journal: {root}");
            if (journal.Version != 1 || !Enum.IsDefined(journal.Phase)
                || string.IsNullOrWhiteSpace(journal.StagingDirectory)
                || journal.OriginalPaths is null || journal.OriginalPaths.Except(ManagedImportPaths).Any())
                throw new InvalidDataException($"Invalid backup import recovery journal: {root}");
            var staging = Path.Combine(paths.Data, journal.StagingDirectory);
            ValidateWorkingDirectory(paths, staging, ".kkindle-import-");
            if (journal.Phase == ImportPhase.Replacing)
                await RestoreImportAsync(paths, root, journal, token);
            if (journal.Phase != ImportPhase.Committed)
                RestoreBaselineResetMarker(paths, journal);
            TryDeleteDirectory(staging);
            TryDeleteDirectory(root);
        }
    }

    private static async Task WriteImportJournalAsync(string root, ImportJournal journal, CancellationToken token)
    {
        var path = Path.Combine(root, ImportJournalFileName);
        var temporary = path + ".tmp";
        await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(output, journal, JsonOptions, token);
            await output.FlushAsync(token);
            output.Flush(flushToDisk: true);
        }
        SettingsFile.Publish(temporary, path);
    }

    private static async Task RestoreImportAsync(AppPaths paths, string root, ImportJournal journal, CancellationToken token)
    {
        ValidateWorkingDirectory(paths, root, ".kkindle-rollback-");
        var database = Path.Combine(root, "kkindle.db");
        if (!File.Exists(database)) throw new IOException($"Backup recovery database is missing: {root}");
        foreach (var name in ManagedImportPaths)
        {
            token.ThrowIfCancellationRequested();
            var backup = Path.Combine(root, name);
            var target = Path.Combine(paths.Data, name);
            if (PathExists(backup))
            {
                RejectReparsePoints(target);
                DeletePath(target);
                // Keep recovery originals until the entire rollback commits,
                // so another interruption can safely repeat the copy.
                await CopyRecoveryPathAsync(backup, target, token);
            }
            else if (!journal.OriginalPaths.Contains(name))
            {
                RejectReparsePoints(target);
                DeletePath(target);
            }
        }
        await CopyDatabaseAsync(database, paths.Database, token);
        RestoreBaselineResetMarker(paths, journal);
        journal.Phase = ImportPhase.Restored;
        await WriteImportJournalAsync(root, journal, token);
    }

    private static async Task CopyRecoveryPathAsync(string source, string target, CancellationToken token)
    {
        RejectReparsePoints(source);
        if (File.Exists(source))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await input.CopyToAsync(output, token);
            await output.FlushAsync(token);
            output.Flush(flushToDisk: true);
            return;
        }
        Directory.CreateDirectory(target);
        foreach (var path in Directory.EnumerateFileSystemEntries(source))
            await CopyRecoveryPathAsync(path, Path.Combine(target, Path.GetFileName(path)), token);
    }

    private static bool PathExists(string path) => File.Exists(path) || Directory.Exists(path);

    private static void RestoreBaselineResetMarker(AppPaths paths, ImportJournal journal)
    {
        var marker = Path.Combine(paths.Data, PendingSyncBaselineResetFileName);
        if (journal.PreviousBaselineResetMarker is { } previous) File.WriteAllText(marker, previous);
        else File.Delete(marker);
    }

    private static void ValidateWorkingDirectory(AppPaths paths, string directory, string prefix)
    {
        var full = Path.GetFullPath(directory);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var name = Path.GetFileName(full);
        if (!string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(paths.Data), comparison)
            || !name.StartsWith(prefix, StringComparison.Ordinal)
            || !Guid.TryParseExact(name[prefix.Length..], "N", out _))
            throw new InvalidDataException("Backup recovery path is outside the managed working directory.");
        RejectReparsePoints(full);
    }

    private static void RejectReparsePoints(string path)
    {
        if (!PathExists(path)) return;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"Refusing to follow a reparse point during backup recovery: {path}");
        if (Directory.Exists(path))
            foreach (var child in Directory.EnumerateFileSystemEntries(path)) RejectReparsePoints(child);
    }
}
