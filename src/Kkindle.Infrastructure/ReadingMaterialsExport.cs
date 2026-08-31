using System.Text;
using Kkindle.Core;

namespace Kkindle.Infrastructure;

public static class ReadingMaterialsExport
{
    public static string BuildMarkdown(IReadOnlyList<ReadingMaterialRecord> records)
    {
        var builder = new StringBuilder("# Kkindle " + UiText.Get("阅读资料") + "\n\n");
        AppendGroups(records, (source, book, items) =>
        {
            builder.Append("## ").Append(source).Append(" · ").AppendLine(book).AppendLine();
            foreach (var item in items)
            {
                builder.Append("### ").AppendLine(item.Type);
                if (!string.IsNullOrWhiteSpace(item.Quote)) builder.Append("> ").AppendLine(item.Quote.ReplaceLineEndings("\n> "));
                if (!string.IsNullOrWhiteSpace(item.Note)) builder.AppendLine().Append(UiText.Get("笔记：")).AppendLine(item.Note);
                if (!string.IsNullOrWhiteSpace(item.Location)) builder.AppendLine().Append(UiText.Get("位置：")).AppendLine(item.Location);
                if (item.UpdatedAt is { } time) builder.Append(UiText.Get("时间：")).AppendLine(time.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
                builder.AppendLine();
            }
        });
        if (records.Count == 0) builder.AppendLine(UiText.Get("暂无划线与笔记。"));
        return builder.ToString();
    }

    public static string BuildPlainText(IReadOnlyList<ReadingMaterialRecord> records)
    {
        var builder = new StringBuilder("Kkindle " + UiText.Get("阅读资料") + "\r\n================\r\n\r\n");
        AppendGroups(records, (source, book, items) =>
        {
            builder.Append('[').Append(source).Append("] ").AppendLine(book);
            foreach (var item in items)
            {
                builder.Append("- ").Append(item.Type);
                if (!string.IsNullOrWhiteSpace(item.Location)) builder.Append(" · ").Append(item.Location);
                builder.AppendLine();
                if (!string.IsNullOrWhiteSpace(item.Quote)) builder.AppendLine(item.Quote);
                if (!string.IsNullOrWhiteSpace(item.Note)) builder.Append(UiText.Get("笔记：")).AppendLine(item.Note);
                if (item.UpdatedAt is { } time) builder.Append(UiText.Get("时间：")).AppendLine(time.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
                builder.AppendLine();
            }
        });
        if (records.Count == 0) builder.AppendLine(UiText.Get("暂无划线与笔记。"));
        return builder.ToString();
    }

    private static void AppendGroups(
        IReadOnlyList<ReadingMaterialRecord> records,
        Action<string, string, IReadOnlyList<ReadingMaterialRecord>> append)
    {
        foreach (var sourceGroup in records.GroupBy(item => item.Source))
        foreach (var bookGroup in sourceGroup.GroupBy(item => item.BookTitle, StringComparer.CurrentCultureIgnoreCase))
            append(sourceGroup.Key == ReadingMaterialSource.Local ? UiText.Get("本地书籍") : "Kindle", bookGroup.Key, bookGroup.ToArray());
    }
}
