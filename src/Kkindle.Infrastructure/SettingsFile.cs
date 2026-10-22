namespace Kkindle.Infrastructure;

internal static class SettingsFile
{
    // Callers serialize writers with SettingsWriteLock. ReplaceFile on Windows
    // also lets a reader finish using its previous snapshot when that reader
    // grants FileShare.Delete; MoveFileEx with overwrite can still deny access.
    public static void Publish(string temporaryPath, string destinationPath)
    {
        if (File.Exists(destinationPath))
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.Replace(temporaryPath, destinationPath, destinationBackupFileName: null);
                    break;
                }
                catch (IOException exception) when (attempt < 4 &&
                    (exception.HResult & 0xffff) is 32 or 33 or 1175 or 1176 or 1177)
                {
                    Thread.Sleep(20 * (attempt + 1));
                }
            }
        }
        else
            File.Move(temporaryPath, destinationPath);
    }
}
