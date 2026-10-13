using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace Kkindle.Core;

public static class ZLibraryMetadata
{
    public static bool Matches(LibraryBookMatch local, ZLibraryBook online)
    {
        static string Normalize(string? value) => string.Concat((value ?? string.Empty)
            .Normalize(System.Text.NormalizationForm.FormKC).Where(c => !char.IsWhiteSpace(c))).ToUpperInvariant();
        static HashSet<string> Isbns(string? value) => Regex.Split(value ?? string.Empty, @"[,;/|，；、\r\n]+")
            .Select(token => Regex.Replace(token.ToUpperInvariant(), @"[^0-9X]", ""))
            .Where(token => token.Length is 10 or 13).ToHashSet(StringComparer.Ordinal);
        if (Isbns(local.Isbn).Overlaps(Isbns(online.Identifier))) return true;
        var title = Normalize(online.Title);
        if (title.Length == 0 || title is "未命名书籍" or "UNTITLED" || title != Normalize(local.Title)) return false;
        static string Authors(string? value) => string.Join("|", Regex.Split(value ?? string.Empty, @"[,，;；&·、]+")
            .Select(Normalize).Where(token => token.Length > 0).Distinct().Order(StringComparer.Ordinal));
        var localAuthors = Authors(local.Authors);
        var onlineAuthors = Authors(online.Author);
        return localAuthors == onlineAuthors
            || localAuthors is "" or "未知作者" or "UNKNOWN AUTHOR"
            || onlineAuthors is "" or "未知作者" or "UNKNOWN AUTHOR";
    }

    public static string Description(ZLibraryBook source) => Regex.Replace(
        WebUtility.HtmlDecode(Regex.Replace(source.Description ?? string.Empty, "<[^>]+>", " ")),
        @"\s+", " ").Trim();

    public static void Apply(Book target, ZLibraryBook source, bool replaceExisting)
    {
        string? Merge(string? existing, string? incoming) =>
            !string.IsNullOrWhiteSpace(incoming) && (replaceExisting || string.IsNullOrWhiteSpace(existing))
                ? incoming.Trim() : existing;
        target.Title = Merge(target.Title, source.Title)!;
        target.Authors = Merge(target.Authors, source.Author)!;
        target.Series = Merge(target.Series, source.Series);
        target.Publisher = Merge(target.Publisher, source.Publisher);
        target.PublishDate = Merge(target.PublishDate, source.Year is > 0 ? source.Year.Value.ToString(CultureInfo.InvariantCulture) : null);
        target.Isbn = Merge(target.Isbn, source.Identifier);
        target.PageCount = Merge(target.PageCount, source.Pages is > 0 ? source.Pages.Value.ToString(CultureInfo.InvariantCulture) : null);
        var details = new List<string>();
        var description = Description(source);
        if (description.Length == 0 && replaceExisting) description = target.Description ?? string.Empty;
        if (description.Length > 0) details.Add(description);
        if (!string.IsNullOrWhiteSpace(source.Language)) details.Add(UiText.Get("语言：{0}", source.Language));
        if (!string.IsNullOrWhiteSpace(source.Edition)) details.Add(UiText.Get("版本：{0}", source.Edition));
        if (!string.IsNullOrWhiteSpace(source.Volume)) details.Add(UiText.Get("卷：{0}", source.Volume));
        target.Description = Merge(target.Description, string.Join(Environment.NewLine + Environment.NewLine, details));
    }
}
