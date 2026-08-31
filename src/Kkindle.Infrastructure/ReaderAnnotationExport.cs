using System.Text;
using Kkindle.Core;

namespace Kkindle.Infrastructure;

/// <summary>
/// Builds local Markdown / plain-text exports of the reader annotations
/// (highlights + notes). Purely local read-only formatting: nothing is
/// uploaded and no network access happens here.
/// </summary>
public static class ReaderAnnotationExport
{
    public static string BuildMarkdown(
        string bookTitle,
        string authors,
        IReadOnlyList<ReaderAnnotation> annotations,
        Func<string, string>? chapterTitleResolver = null)
    {
        var builder = new StringBuilder();
        builder.Append("# ").AppendLine(Trim(bookTitle));
        builder.Append(UiText.Get("作者：")).AppendLine(Trim(authors));
        builder.AppendLine();
        if (annotations.Count == 0)
        {
            builder.AppendLine(UiText.Get("本书暂无划线与批注。"));
            return builder.ToString();
        }

        var ordered = annotations
            .OrderBy(annotation => annotation.ChapterPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(annotation => annotation.StartOffset)
            .ThenBy(annotation => annotation.CreatedAt)
            .ToArray();
        foreach (var annotation in ordered)
        {
            var chapterTitle = chapterTitleResolver?.Invoke(annotation.ChapterPath);
            builder.Append("## ").AppendLine(string.IsNullOrWhiteSpace(chapterTitle) ? annotation.ChapterPath : chapterTitle);
            builder.AppendLine();
            builder.Append("> ").AppendLine(Trim(annotation.SelectedText));
            builder.AppendLine();
            if (!string.IsNullOrWhiteSpace(annotation.Note))
                builder.Append(UiText.Get("批注：")).AppendLine(Trim(annotation.Note));
            builder.Append(UiText.Get("创建时间：")).AppendLine(annotation.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss zzz"));
            builder.Append(UiText.Get("定位：")).AppendLine(BuildLocationLabel(annotation));
            builder.AppendLine("---");
            builder.AppendLine();
        }
        return builder.ToString().TrimEnd();
    }

    public static string BuildPlainText(
        string bookTitle,
        string authors,
        IReadOnlyList<ReaderAnnotation> annotations,
        Func<string, string>? chapterTitleResolver = null)
    {
        var builder = new StringBuilder();
        builder.AppendLine(Trim(bookTitle));
        builder.Append(UiText.Get("作者：")).AppendLine(Trim(authors));
        builder.AppendLine();
        if (annotations.Count == 0)
        {
            builder.AppendLine(UiText.Get("本书暂无划线与批注。"));
            return builder.ToString();
        }

        var ordered = annotations
            .OrderBy(annotation => annotation.ChapterPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(annotation => annotation.StartOffset)
            .ThenBy(annotation => annotation.CreatedAt)
            .ToArray();
        for (var index = 0; index < ordered.Length; index++)
        {
            var annotation = ordered[index];
            var chapterTitle = chapterTitleResolver?.Invoke(annotation.ChapterPath);
            builder.Append('[').Append(index + 1).Append("] ")
                .AppendLine(string.IsNullOrWhiteSpace(chapterTitle) ? annotation.ChapterPath : chapterTitle);
            builder.AppendLine(Trim(annotation.SelectedText));
            if (!string.IsNullOrWhiteSpace(annotation.Note))
                builder.Append(UiText.Get("批注：")).AppendLine(Trim(annotation.Note));
            builder.Append(UiText.Get("创建时间：")).AppendLine(annotation.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss zzz"));
            builder.Append(UiText.Get("定位：")).AppendLine(BuildLocationLabel(annotation));
            builder.AppendLine();
        }
        return builder.ToString().TrimEnd();
    }

    private static string BuildLocationLabel(ReaderAnnotation annotation)
    {
        var location = annotation.ChapterPath;
        if (!string.IsNullOrWhiteSpace(annotation.Fragment))
            location += "#" + annotation.Fragment;
        return UiText.Get("{0}（偏移 {1}–{2}）", location, annotation.StartOffset, annotation.EndOffset);
    }

    private static string Trim(string value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return normalized.Replace('\r', ' ').Replace('\n', ' ');
    }
}
