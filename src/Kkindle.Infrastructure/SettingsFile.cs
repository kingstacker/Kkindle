namespace Kkindle.Infrastructure;

internal static class SettingsFile
{
    // Callers serialize writers with SettingsWriteLock. ReplaceFile on Windows
    // also lets a reader finish using its previous snapshot when that reader
    // grants FileShare.Delete; MoveFileEx with overwrite can still deny access.
    public static void Publish(string temporaryPath, string destinationPath)
    {
        if (File.Exists(destinationPath))
            File.Replace(temporaryPath, destinationPath, destinationBackupFileName: null);
        else
            File.Move(temporaryPath, destinationPath);
    }
}
