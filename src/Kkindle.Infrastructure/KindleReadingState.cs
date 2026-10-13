namespace Kkindle.Infrastructure;

public static class KindleReadingState
{
    public static void DeleteForRemovedBook(string bookPath)
    {
        var state = Path.ChangeExtension(bookPath, ".sdr");
        if (!Directory.Exists(state)) return;
        if ((File.GetAttributes(state) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("书籍阅读记录目录是链接，无法安全删除。");
        Directory.Delete(state, recursive: true);
    }

}
