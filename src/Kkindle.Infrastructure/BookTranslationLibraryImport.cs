using Kkindle.Core;

namespace Kkindle.Infrastructure;

/// <summary>Retires old translations only after every replacement is in the library.</summary>
public static class BookTranslationLibraryImport
{
    public static async Task<ImportBatchResult> ImportAsync(
        IBookLibraryService library,
        IReadOnlyDictionary<string, IReadOnlyList<BookFile>> replacements,
        CancellationToken cancellationToken = default)
    {
        var result = await library.ImportAsync(
            replacements.Keys,
            cancellationToken: cancellationToken,
            conflictResolver: static _ => Task.FromResult(ImportConflictResolution.AddAsFormat));

        // ImportAsync reports per-file failures without throwing. A partial
        // import, exception or cancellation must leave all old versions intact.
        if (result.FailureCount > 0 || result.Items.Count != replacements.Count)
            return result;

        var removable = new Dictionary<Guid, BookFile>();
        var retained = new HashSet<Guid>();
        foreach (var item in result.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!item.Succeeded || !replacements.TryGetValue(item.SourcePath, out var existingFiles))
                return result;
            var bookId = item.BookId ?? item.Book?.Id;
            if (bookId is null) return result;
            var importedBook = await library.GetBookAsync(bookId.Value, cancellationToken);
            var hash = await Hashing.Sha256Async(item.SourcePath, cancellationToken);
            var importedFile = importedBook?.Files.FirstOrDefault(file =>
                file.Sha256.Equals(hash, StringComparison.OrdinalIgnoreCase)
                && File.Exists(library.GetAbsoluteFilePath(file)));
            if (importedFile is null) return result;
            retained.Add(importedFile.Id);
            foreach (var oldFile in existingFiles)
            {
                if (oldFile.Sha256.Equals(hash, StringComparison.OrdinalIgnoreCase))
                    retained.Add(oldFile.Id);
                else
                    removable[oldFile.Id] = oldFile;
            }
        }

        foreach (var file in removable.Values.Where(file => !retained.Contains(file.Id)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await library.DeleteFileAsync(file.BookId, file.Id, cancellationToken);
        }
        return result;
    }
}
