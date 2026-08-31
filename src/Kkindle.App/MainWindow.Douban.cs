using System.Text;
using System.ComponentModel;
using Avalonia.Media.Imaging;
using Avalonia.Interactivity;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle;

public sealed class DoubanCandidateViewModel : INotifyPropertyChanged, IDisposable
{
    private Bitmap? _coverImage;

    public DoubanCandidateViewModel(DoubanBookCandidate candidate)
    {
        Candidate = candidate;
        UiText.LanguageChanged += OnLanguageChanged;
    }

    public DoubanBookCandidate Candidate { get; }
    public string Title => Candidate.Title;
    public string Abstract => string.IsNullOrWhiteSpace(Candidate.Abstract)
        ? UiText.Get("豆瓣未提供简要出版信息")
        : Candidate.Abstract;
    public string SubjectText => Candidate.SubjectId > 0
        ? UiText.Get("豆瓣条目 #{0}", Candidate.SubjectId)
        : UiText.Get("豆瓣图书条目");
    public string RatingText => Candidate.Rating is null
        ? UiText.Get("暂无评分")
        : UiText.Get("{0:0.0} / {1} 人", Candidate.Rating, Candidate.RatingCount);

    public Bitmap? CoverImage
    {
        get => _coverImage;
        set
        {
            if (ReferenceEquals(_coverImage, value)) return;
            _coverImage?.Dispose();
            _coverImage = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CoverImage)));
        }
    }

    public void Dispose()
    {
        UiText.LanguageChanged -= OnLanguageChanged;
        _coverImage?.Dispose();
        _coverImage = null;
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Abstract)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SubjectText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RatingText)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

// One-click Douban batch matching. The per-book flow (manual candidate pick,
// field-by-field confirmation) lives in MainWindow.axaml.cs; this partial adds
// the automated pipeline: search by title+authors, score the candidates, apply
// metadata for high-confidence hits and report everything else for manual
// follow-up. Requests stay serialized through DoubanMetadataService's built-in
// rate limit, matching the one-book-at-a-time etiquette but bulk-driven.
public partial class MainWindow
{
    private const string DefaultBookTitle = "未命名书籍";
    private const string DefaultBookAuthors = "未知作者";
    private static readonly TimeSpan DoubanBatchCooldown = TimeSpan.FromSeconds(30);
    // Exact title match alone auto-applies; a containment match also needs the
    // author to appear in the candidate abstract before trusting it.
    private const double DoubanAutoApplyScore = 80;

    // Batch runs hammer Douban far longer than the one-book manual flow, so
    // they use a dedicated client with a gentler request interval. Manual and
    // batch flows share _doubanMatchCancellation, so they never interleave.
    private DoubanMetadataService? _doubanBatchService;
    private DateTimeOffset? _lastDoubanBatchMatchAt;

    private DoubanMetadataService DoubanBatchService => _doubanBatchService ??= new DoubanMetadataService(
        minimumInterval: TimeSpan.FromSeconds(2.5));

    private async void DoubanBatchMatchButton_Click(object? sender, RoutedEventArgs e)
    {
        var cards = ViewModel.Books.Where(card => card.Book is not null).ToArray();
        if (cards.Length == 0)
        {
            SetTaskStatus(T("书库是空的，先导入书籍再使用一键豆瓣匹配。"));
            return;
        }

        if (!await ConfirmAsync(
                T("一键豆瓣匹配"),
                T("将按书名和作者自动匹配并写入 {0} 本书的豆瓣信息。此功能不推荐频繁使用，可能触发豆瓣访问限制；是否继续？", cards.Length),
                T("继续匹配")))
            return;
        await RunDoubanBatchMatchAsync(cards);
    }

    internal async Task RunDoubanBatchMatchAsync(IReadOnlyList<BookCardViewModel> cards)
    {
        if (_doubanMatchCancellation is not null)
        {
            SetTaskStatus(T("豆瓣匹配正在进行中，请稍候。"));
            return;
        }
        if (!_appSettings.NetworkEnabled)
        {
            await ShowMessageAsync(T("网络功能已关闭"), T("请先在应用设置中允许网络功能，再使用豆瓣匹配。"));
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (_lastDoubanBatchMatchAt is { } last)
        {
            var elapsed = now - last;
            if (elapsed < DoubanBatchCooldown)
            {
                var remaining = DoubanBatchCooldown - elapsed;
                await ShowMessageAsync(
                    T("豆瓣匹配暂不可用"),
                    T("短时间内只能使用一次一键豆瓣匹配，请约 {0} 秒后再试。频繁请求可能触发豆瓣访问限制。", Math.Ceiling(remaining.TotalSeconds)));
                return;
            }
        }
        _lastDoubanBatchMatchAt = now;

        var cancellation = new CancellationTokenSource();
        _doubanMatchCancellation = cancellation;
        var matched = new List<string>();
        var uncertain = new List<string>();
        var missing = new List<string>();
        var failed = new List<string>();
        try
        {
            ShowTaskProgressPopup();
            TaskProgressPopupBar.IsIndeterminate = false;
            TaskProgressPopupBar.Minimum = 0;
            TaskProgressPopupBar.Maximum = cards.Count;
            TaskProgressPopupBar.Value = 0;

            for (var i = 0; i < cards.Count; i++)
            {
                var card = cards[i];
                if (card.Book is not { } book)
                {
                    missing.Add(card.Title);
                    continue;
                }
                TaskProgressPopupBar.Value = i;
                var searchTitle = CleanDoubanTitle(book.Title);
                var searchAuthor = CleanDoubanAuthor(book.Authors);
                var normalizedSearchTitle = NormalizeDoubanText(searchTitle);
                if (normalizedSearchTitle.Length < 2
                    || DoubanGuidRegex().IsMatch(book.Title)
                    || (normalizedSearchTitle.All(char.IsAsciiDigit) && normalizedSearchTitle.Length >= 3))
                {
                    missing.Add(T("{0}（本地书名无效，请先修正）", book.Title));
                    continue;
                }
                TaskProgressPopupText.Text = T("({0}/{1}) 正在匹配《{2}》…", i + 1, cards.Count, searchTitle);
                SetTaskStatus(T("({0}/{1}) 正在匹配《{2}》的豆瓣信息…", i + 1, cards.Count, searchTitle));

                try
                {
                    var candidates = await DoubanBatchService.SearchAsync(searchTitle, searchAuthor, cancellation.Token);
                    if (candidates.Count == 0 && !string.IsNullOrWhiteSpace(searchAuthor))
                    {
                        // Dirty author strings ("【日】伊坂幸太郎", translators mixed
                        // in) poison the query; retry with the title alone.
                        candidates = await DoubanBatchService.SearchAsync(searchTitle, null, cancellation.Token);
                    }
                    if (candidates.Count == 0)
                    {
                        missing.Add(book.Title);
                        continue;
                    }

                    var best = candidates
                        .Select(candidate => (Candidate: candidate, Score: ScoreDoubanCandidate(book, candidate)))
                        .OrderByDescending(entry => entry.Score)
                        .First();
                    if (best.Score < DoubanAutoApplyScore)
                    {
                        uncertain.Add(T("{0}（最接近：《{1}》）", book.Title, best.Candidate.Title));
                        continue;
                    }

                    TaskProgressPopupText.Text = T("({0}/{1}) 正在读取《{2}》的详情…", i + 1, cards.Count, best.Candidate.Title);
                    var metadata = await DoubanBatchService.GetDetailsAsync(best.Candidate, cancellation.Token);
                    await ApplyDoubanMetadataToBookAsync(book, metadata, cancellation.Token);
                    matched.Add(book.Title);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    throw;
                }
                catch (InvalidOperationException exception)
                    when (exception.Message.Contains("访问验证") || exception.Message.Contains("限制了访问"))
                {
                    // Douban started challenging requests; hammering on would only
                    // extend the block, so stop the whole batch here.
                    SetTaskStatus(T("豆瓣触发访问验证，批量匹配已停止（已完成 {0} 本）。请稍后重试。", matched.Count));
                    return;
                }
                catch (Exception exception)
                {
                    failed.Add(T("{0}（{1}）", book.Title, UiText.Localize(exception.Message)));
                }
            }

            TaskProgressPopupBar.Value = cards.Count;
            await RefreshLibraryAsync();
            SetTaskStatus(T("豆瓣批量匹配完成：自动匹配 {0} 本，待确认 {1} 本，未找到 {2} 本。", matched.Count, uncertain.Count, missing.Count));
            await ShowMessageAsync(
                T("一键豆瓣匹配完成"),
                BuildDoubanBatchSummary(matched, uncertain, missing, failed, aborted: false));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            await RefreshLibraryAsync();
            SetTaskStatus(T("豆瓣批量匹配已取消。"));
        }
        catch (Exception exception)
        {
            await RefreshLibraryAsync();
            SetTaskStatus(T("豆瓣批量匹配失败：{0}", UiText.Localize(exception.Message)));
            await ShowMessageAsync(T("豆瓣批量匹配失败"), UiText.Localize(exception.Message));
        }
        finally
        {
            TaskProgressPopupBar.IsIndeterminate = false;
            HideTaskProgressPopup();
            if (ReferenceEquals(_doubanMatchCancellation, cancellation)) _doubanMatchCancellation = null;
            cancellation.Dispose();
        }
    }

    private static string BuildDoubanBatchSummary(
        List<string> matched,
        List<string> uncertain,
        List<string> missing,
        List<string> failed,
        bool aborted)
    {
        string Section(string label, List<string> items) => items.Count == 0
            ? string.Empty
            : T("\n\n{0}（{1}）：\n{2}{3}",
                label,
                items.Count,
                string.Join('\n', items.Take(8)),
                items.Count > 8 ? T("\n… 等共 {0} 项", items.Count) : string.Empty);

        return T("自动匹配成功：{0} 本", matched.Count)
            + Section(T("自动匹配"), matched)
            + Section(T("待人工确认（可右键书籍用「匹配豆瓣」逐本选择）"), uncertain)
            + Section(T("豆瓣未找到"), missing)
            + Section(T("请求失败"), failed)
            + (aborted ? T("\n\n本次因触发豆瓣访问验证提前停止，其余书籍未处理。") : string.Empty);
    }

    private async Task ApplyDoubanMetadataToBookAsync(Book book, DoubanBookMetadata metadata, CancellationToken cancellationToken)
    {
        // The local title/authors are what the user filed the book under, so
        // only replace them when they are still the import placeholders.
        if (string.Equals(book.Title, DefaultBookTitle, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(metadata.Title))
            book.Title = metadata.Title.Trim();
        if (string.Equals(book.Authors, DefaultBookAuthors, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(metadata.Authors))
            book.Authors = metadata.Authors.Trim();
        if (!string.IsNullOrWhiteSpace(metadata.Series)) book.Series = metadata.Series.Trim();
        if (!string.IsNullOrWhiteSpace(metadata.Description)) book.Description = metadata.Description.Trim();
        if (!string.IsNullOrWhiteSpace(metadata.Publisher)) book.Publisher = metadata.Publisher.Trim();
        if (!string.IsNullOrWhiteSpace(metadata.PublishDate)) book.PublishDate = metadata.PublishDate.Trim();
        if (!string.IsNullOrWhiteSpace(metadata.Isbn)) book.Isbn = metadata.Isbn.Trim();
        if (!string.IsNullOrWhiteSpace(metadata.Pages)) book.PageCount = metadata.Pages.Trim();
        if (!string.IsNullOrWhiteSpace(metadata.Binding)) book.Binding = metadata.Binding.Trim();
        if (metadata.Rating is not null) book.DoubanRating = metadata.Rating;
        book.DoubanRatingCount = metadata.RatingCount;

        if (!string.IsNullOrWhiteSpace(metadata.CoverUrl))
        {
            try
            {
                var coverBytes = await DoubanBatchService.DownloadCoverAsync(metadata.CoverUrl, cancellationToken);
                _paths.EnsureDirectories();
                var coverPath = Path.Combine(_paths.Covers, $"{book.Id:N}-douban.jpg");
                var temporaryPath = coverPath + ".tmp";
                await File.WriteAllBytesAsync(temporaryPath, coverBytes, cancellationToken);
                File.Move(temporaryPath, coverPath, overwrite: true);
                book.CoverPath = Path.GetRelativePath(_paths.Data, coverPath);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // A failed cover download must not roll back the text metadata.
            }
        }

        await _library.UpdateMetadataAsync(book, _lifetimeCancellation.Token);
    }

    private static double ScoreDoubanCandidate(Book book, DoubanBookCandidate candidate)
    {
        // Import metadata is noisy: subtitles in brackets, "书名: 副标题",
        // series numbers, nationality tags on authors. Compare both the raw
        // and the cleaned-down core titles so an edition with extra noise on
        // either side still scores as a match.
        var localFull = NormalizeDoubanText(book.Title);
        var localCore = NormalizeDoubanText(CleanDoubanTitle(book.Title));
        var candidateFull = NormalizeDoubanText(candidate.Title);
        var candidateCore = NormalizeDoubanText(CleanDoubanTitle(candidate.Title));
        if (localCore.Length == 0 || candidateCore.Length == 0) return 0;

        double score;
        if (TitleKeysEqual(localFull, localCore, candidateFull, candidateCore))
            score = 100;
        else if (TitleKeysContain(localFull, localCore, candidateFull, candidateCore))
            score = 75;
        else if (DoubanBigramSimilarity(localCore, candidateCore) >= 0.6)
            score = 70;
        else
            return 0;

        // The search abstract is "作者 / 出版社 / 日期". Match by shared token
        // or by the cleaned author string appearing inside the abstract, which
        // survives punctuation differences in foreign names.
        var normalizedAbstract = NormalizeDoubanText(candidate.Abstract);
        var authorTokens = SplitDoubanNameTokens(book.Authors);
        var cleanedAuthor = NormalizeDoubanText(CleanDoubanAuthor(book.Authors));
        if ((authorTokens.Count > 0 && SplitDoubanNameTokens(candidate.Abstract).Any(token => authorTokens.Contains(token)))
            || (cleanedAuthor.Length >= 2 && normalizedAbstract.Contains(cleanedAuthor, StringComparison.Ordinal)))
            score += 10;
        if (candidate.RatingCount > 0)
            score += Math.Min(5, Math.Log10(candidate.RatingCount + 1));
        return score;
    }

    private static bool TitleKeysEqual(string localFull, string localCore, string candidateFull, string candidateCore) =>
        string.Equals(localFull, candidateFull, StringComparison.Ordinal)
        || string.Equals(localCore, candidateCore, StringComparison.Ordinal)
        || string.Equals(localCore, candidateFull, StringComparison.Ordinal)
        || string.Equals(localFull, candidateCore, StringComparison.Ordinal);

    private static bool TitleKeysContain(string localFull, string localCore, string candidateFull, string candidateCore) =>
        ContainsAnyWay(localFull, candidateFull)
        || ContainsAnyWay(localCore, candidateCore)
        || ContainsAnyWay(localCore, candidateFull)
        || ContainsAnyWay(localFull, candidateCore);

    private static bool ContainsAnyWay(string left, string right) =>
        (left.Length >= 2 && right.Contains(left, StringComparison.Ordinal))
        || (right.Length >= 2 && left.Contains(right, StringComparison.Ordinal));

    // Dice coefficient over character bigrams; the only fuzzy tier in the
    // pipeline, tuned conservatively (≥0.6 keeps 三体/三体Ⅱ together but
    // rejects different books sharing a genre word).
    private static double DoubanBigramSimilarity(string left, string right)
    {
        if (left == right) return 1;
        if (left.Length < 2 || right.Length < 2) return 0;
        var leftBigrams = new Dictionary<string, int>();
        for (var i = 0; i < left.Length - 1; i++)
        {
            var bigram = left.Substring(i, 2);
            leftBigrams[bigram] = leftBigrams.TryGetValue(bigram, out var count) ? count + 1 : 1;
        }
        var hits = 0;
        for (var i = 0; i < right.Length - 1; i++)
        {
            var bigram = right.Substring(i, 2);
            if (leftBigrams.TryGetValue(bigram, out var count) && count > 0)
            {
                leftBigrams[bigram] = count - 1;
                hits++;
            }
        }
        return 2d * hits / (left.Length - 1 + right.Length - 1);
    }

    // Strips the noise import metadata carries: 《》 wrappers, bracketed
    // subtitles/awards/series, "书名: 副标题", and the "12345_书名_作者"
    // filename pattern that produces titles like "139261_因为独特_李翔".
    private static string CleanDoubanTitle(string title)
    {
        var text = (title ?? string.Empty).Trim().Trim('《', '》');
        if (text.Contains('_'))
        {
            var parts = text.Split('_', StringSplitOptions.RemoveEmptyEntries)
                .Where(part => !IsDoubanNoiseToken(part))
                .ToArray();
            if (parts.Length > 0) text = parts[0];
        }
        text = DoubanBracketRegex().Replace(text, " ");
        var colon = text.IndexOfAny(['：', ':']);
        // Treat a colon as "title: subtitle" only when the left side reads like
        // a title; "777：杀手大乱斗" must keep its numeric head.
        if (colon >= 2 && !text[..colon].All(char.IsAsciiDigit))
            text = text[..colon];
        return DoubanSpaceRegex().Replace(text, " ").Trim();
    }

    // Removes nationality tags ("【日】/ [美]") and keeps only the first
    // author, dropping translators that the import stuffed into the field.
    private static string CleanDoubanAuthor(string authors)
    {
        var text = DoubanBracketRegex().Replace(authors ?? string.Empty, " ");
        foreach (var separator in new[] { ' ', '　', ',', '，', '、', '/', '；', ';' })
        {
            var index = text.IndexOf(separator);
            if (index >= 2)
            {
                text = text[..index];
                break;
            }
        }
        return text.Trim();
    }

    private static bool IsDoubanNoiseToken(string token) =>
        token.Length == 0
        || (token.All(char.IsAsciiDigit) && token.Length >= 3)
        || DoubanGuidRegex().IsMatch(token);

    [System.Text.RegularExpressions.GeneratedRegex(@"[（(【\[〔][^）)】\]〕]*[）)】\]〕]")]
    private static partial System.Text.RegularExpressions.Regex DoubanBracketRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"\s+")]
    private static partial System.Text.RegularExpressions.Regex DoubanSpaceRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4,}$")]
    private static partial System.Text.RegularExpressions.Regex DoubanGuidRegex();

    private static string NormalizeDoubanText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (!char.IsLetterOrDigit(character)) continue;
            var normalized = character switch
            {
                'Ⅰ' or 'ⅰ' => '1',
                'Ⅱ' or 'ⅱ' => '2',
                'Ⅲ' or 'ⅲ' => '3',
                'Ⅳ' or 'ⅳ' => '4',
                'Ⅴ' or 'ⅴ' => '5',
                _ => char.ToLowerInvariant(character)
            };
            builder.Append(normalized);
        }
        return builder.ToString();
    }

    private static HashSet<string> SplitDoubanNameTokens(string? text)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text)) return tokens;
        foreach (var part in text.Split([' ', '　', ',', '，', '、', '/', '／', '·', ';', '；'], StringSplitOptions.RemoveEmptyEntries))
        {
            var normalized = NormalizeDoubanText(part);
            if (normalized.Length >= 2) tokens.Add(normalized);
        }
        return tokens;
    }
}
