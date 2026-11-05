using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Kkindle.ApplicationServices;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Kkindle.Mobile.Views.Sheets;

namespace Kkindle.Mobile.Views;

/// <summary>
/// Book metadata editor, matching the desktop detail pane. Douban lookup is a
/// developer-mode feature; its results only fill the form, nothing is written
/// until the reader saves.
/// </summary>
public partial class BookEditPage : UserControl, IShellPage
{
    private const int CoverWidth = 216;

    private readonly Book _book;
    private readonly ApplicationModules _modules;
    private readonly MobileShell _shell;
    private readonly CancellationTokenSource _lifetime = new();
    private DoubanBookMetadata? _douban;
    private byte[]? _pendingCover;

    /// <summary>Design-time only.</summary>
    public BookEditPage()
    {
        _book = null!;
        _modules = null!;
        _shell = null!;
        InitializeComponent();
    }

    public BookEditPage(Book book, ApplicationModules modules, MobileShell shell)
    {
        _book = book;
        _modules = modules;
        _shell = shell;
        InitializeComponent();
        KeyboardAvoidance.Attach(this, FormScroller, ActionBar);
        DoubanButton.IsVisible = shell.IsDeveloperMode;
        Present();
        _ = LoadCoverAsync();
    }

    private void Present()
    {
        TitleBox.Text = _book.Title;
        AuthorsBox.Text = _book.Authors;
        SeriesBox.Text = _book.Series;
        PublisherBox.Text = _book.Publisher;
        PublishDateBox.Text = _book.PublishDate;
        IsbnBox.Text = _book.Isbn;
        PageCountBox.Text = _book.PageCount;
        BindingBox.Text = _book.Binding;
        TagsBox.Text = _book.Tags;
        CategoryBox.Text = _book.Category;
        DescriptionBox.Text = _book.Description;
        PresentRating(_book.DoubanRating, _book.DoubanRatingCount);
        CoverInitial.Text = string.IsNullOrEmpty(_book.Title) ? "?" : _book.Title[..1];
    }

    private void PresentRating(double? rating, int? count) =>
        RatingText.Text = rating is { } value ? $"豆瓣 {value:0.0} · {count ?? 0} 人评价" : "尚未匹配豆瓣";

    private async Task LoadCoverAsync()
    {
        var cover = await _shell.Covers.GetAsync(_book, CoverWidth);
        CoverImage.Source = cover;
        CoverInitial.IsVisible = cover is null;
    }

    private void ShowStatus(string? text)
    {
        StatusText.Text = text;
        StatusText.IsVisible = !string.IsNullOrEmpty(text);
    }

    // ---------- Douban

    private void OnDoubanClick(object? sender, RoutedEventArgs e)
    {
        var query = string.Join(' ', new[] { TitleBox.Text?.Trim(), AuthorsBox.Text?.Trim() }
            .Where(value => !string.IsNullOrWhiteSpace(value) && value != "未知作者"));
        _shell.ShowSheet("从豆瓣获取", new SearchSheet(SearchDoubanAsync, hit => _ = PickAsync(hit), query));
    }

    private async Task<IReadOnlyList<SearchHit>> SearchDoubanAsync(
        string query, IProgress<string> progress, CancellationToken token)
    {
        progress.Report("正在搜索豆瓣…");
        var candidates = await _modules.Library.Douban.SearchAsync(query, null, token);
        var hits = candidates
            .Select(candidate => new SearchHit(
                candidate.Rating is > 0 and var rating ? $"{candidate.Title} · 豆瓣 {rating:0.0}" : candidate.Title,
                candidate.Abstract,
                query,
                candidate,
                showsCover: true))
            .ToList();
        foreach (var hit in hits) _ = LoadThumbnailAsync(hit, token);
        return hits;
    }

    private const int ThumbnailWidth = 132;

    /// <summary>Thumbnails load together; a missing one just leaves the slot empty.</summary>
    private async Task LoadThumbnailAsync(SearchHit hit, CancellationToken token)
    {
        if (hit.Source is not DoubanBookCandidate { CoverUrl: { Length: > 0 } url }) return;
        try
        {
            var bytes = await _modules.Library.Douban.DownloadCoverAsync(url, token);
            using var stream = new MemoryStream(bytes);
            hit.Cover = Bitmap.DecodeToWidth(stream, ThumbnailWidth);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Cancelled or unavailable covers are cosmetic.
        }
    }

    private async Task PickAsync(SearchHit hit)
    {
        if (hit.Source is not DoubanBookCandidate candidate) return;
        _shell.HideSheet();
        ShowStatus("正在读取豆瓣详情…");
        DoubanButton.IsEnabled = false;
        try
        {
            var metadata = await _modules.Library.Douban.GetDetailsAsync(candidate, _lifetime.Token);
            ShowStatus(null);
            _shell.ShowSheet("选择要更新的信息", new DoubanApplySheet(metadata, choices => _ = ApplyAsync(metadata, choices)));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ShowStatus("读取豆瓣详情失败：" + UiText.Localize(exception.Message));
        }
        finally
        {
            DoubanButton.IsEnabled = true;
        }
    }

    /// <summary>Fills the form from Douban; the reader can still edit before saving.</summary>
    private async Task ApplyAsync(DoubanBookMetadata metadata, DoubanApplyChoices choices)
    {
        _shell.HideSheet();
        static void Set(TextBox box, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) box.Text = value.Trim();
        }
        if (choices.Title) Set(TitleBox, metadata.Title);
        if (choices.Authors) Set(AuthorsBox, metadata.Authors);
        if (choices.Series) Set(SeriesBox, metadata.Series);
        if (choices.Description) Set(DescriptionBox, metadata.Description);
        if (choices.Publication)
        {
            Set(PublisherBox, metadata.Publisher);
            Set(PublishDateBox, metadata.PublishDate);
            Set(IsbnBox, metadata.Isbn);
            Set(PageCountBox, metadata.Pages);
            Set(BindingBox, metadata.Binding);
        }
        _douban = metadata;
        PresentRating(metadata.Rating, metadata.RatingCount);

        if (choices.Cover && !string.IsNullOrWhiteSpace(metadata.CoverUrl))
        {
            ShowStatus("正在下载封面…");
            try
            {
                _pendingCover = await _modules.Library.Douban.DownloadCoverAsync(metadata.CoverUrl, _lifetime.Token);
                using var stream = new MemoryStream(_pendingCover);
                CoverImage.Source = Bitmap.DecodeToWidth(stream, CoverWidth);
                CoverInitial.IsVisible = false;
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _pendingCover = null;
                ShowStatus("封面下载失败，其余信息已填入：" + UiText.Localize(exception.Message));
                return;
            }
        }
        ShowStatus("已填入豆瓣信息，确认后点保存");
    }

    // ---------- save

    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        var title = TitleBox.Text?.Trim();
        if (string.IsNullOrEmpty(title))
        {
            ShowStatus("书名不能为空");
            return;
        }

        using var operation = MobileDataCoordinator.TryBeginOperation();
        if (operation is null) return;
        static string? Optional(TextBox box) => string.IsNullOrWhiteSpace(box.Text) ? null : box.Text.Trim();
        SaveButton.IsEnabled = false;
        // _book is the instance the detail page shows; a failed save must not leave it edited.
        var restore = SnapshotEditableFields();
        try
        {
            _book.Title = title;
            _book.Authors = string.IsNullOrWhiteSpace(AuthorsBox.Text) ? "未知作者" : AuthorsBox.Text.Trim();
            _book.Series = Optional(SeriesBox);
            _book.Publisher = Optional(PublisherBox);
            _book.PublishDate = Optional(PublishDateBox);
            _book.Isbn = Optional(IsbnBox);
            _book.PageCount = Optional(PageCountBox);
            _book.Binding = Optional(BindingBox);
            _book.Tags = TagsBox.Text?.Trim() ?? string.Empty;
            _book.Category = CategoryBox.Text?.Trim() ?? string.Empty;
            _book.Description = Optional(DescriptionBox);
            if (_douban is not null)
            {
                _book.DoubanRating = _douban.Rating ?? _book.DoubanRating;
                _book.DoubanRatingCount = _douban.RatingCount;
            }
            if (_pendingCover is { } cover) _book.CoverPath = await SaveCoverAsync(cover);

            await _modules.Library.Books.UpdateMetadataAsync(_book, _lifetime.Token);
            _shell.ShowToast("书籍信息已保存");
            _shell.GoBack();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            restore();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            restore();
            ShowStatus("保存失败：" + UiText.Localize(exception.Message));
        }
        finally
        {
            SaveButton.IsEnabled = true;
        }
    }

    private Action SnapshotEditableFields()
    {
        var book = _book;
        var (title, authors, series, publisher, publishDate, isbn) =
            (book.Title, book.Authors, book.Series, book.Publisher, book.PublishDate, book.Isbn);
        var (pageCount, binding, tags, category, description) =
            (book.PageCount, book.Binding, book.Tags, book.Category, book.Description);
        var (rating, ratingCount, coverPath) = (book.DoubanRating, book.DoubanRatingCount, book.CoverPath);
        return () =>
        {
            book.Title = title;
            book.Authors = authors;
            book.Series = series;
            book.Publisher = publisher;
            book.PublishDate = publishDate;
            book.Isbn = isbn;
            book.PageCount = pageCount;
            book.Binding = binding;
            book.Tags = tags;
            book.Category = category;
            book.Description = description;
            book.DoubanRating = rating;
            book.DoubanRatingCount = ratingCount;
            book.CoverPath = coverPath;
        };
    }

    /// <summary>Same location and naming as the desktop Douban match.</summary>
    private async Task<string> SaveCoverAsync(byte[] cover)
    {
        var paths = _modules.Paths;
        paths.EnsureDirectories();
        var path = Path.Combine(paths.Covers, $"{_book.Id:N}-douban.jpg");
        var temporary = path + ".tmp";
        await File.WriteAllBytesAsync(temporary, cover, _lifetime.Token);
        File.Move(temporary, path, overwrite: true);
        _shell.Covers.Forget(path);
        return Path.GetRelativePath(paths.Data, path);
    }

    private void OnBackClick(object? sender, RoutedEventArgs e) => _shell.GoBack();

    /// <summary>Pushed pages only detach this one; work ends when it leaves history.</summary>
    public void OnClosed() => _lifetime.Cancel();
}
