using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using System.Globalization;
using Kkindle.ApplicationServices;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Kkindle.Mobile.Views.Sheets;

namespace Kkindle.Mobile.Views;

/// <summary>
/// Book metadata editor, matching the desktop detail pane. Nothing is written
/// until the reader saves.
/// </summary>
public partial class BookEditPage : UserControl, IShellPage
{
    private const int CoverWidth = 216;

    private readonly Book _book;
    private readonly ApplicationModules _modules;
    private readonly MobileShell _shell;
    private readonly CancellationTokenSource _lifetime = new();
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
        PresentRating(_book.OnlineRating);
        CoverInitial.Text = string.IsNullOrEmpty(_book.Title) ? "?" : _book.Title[..1];
    }

    private void PresentRating(double? rating) =>
        RatingBox.Text = rating?.ToString("0.################", CultureInfo.InvariantCulture) ?? string.Empty;

    private async void OnChangeCoverClick(object? sender, RoutedEventArgs e)
    {
        IReadOnlyList<string> files = [];
        try
        {
            files = await _shell.Services.ImportSource.PickFilesAsync(_lifetime.Token);
            if (files.Count == 0) return;
            var bytes = await File.ReadAllBytesAsync(files[0], _lifetime.Token);
            using var imageStream = new MemoryStream(bytes);
            var preview = Bitmap.DecodeToWidth(imageStream, CoverWidth);
            _pendingCover = bytes;
            CoverImage.Source = preview;
            CoverInitial.IsVisible = false;
            ShowStatus("封面已选择，确认后点保存");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ShowStatus("无法读取封面：" + UiText.Localize(exception.Message));
        }
        finally
        {
            _shell.Services.ImportSource.ReleaseFiles(files);
        }
    }

    private async Task LoadCoverAsync()
    {
        var cover = await _shell.Covers.GetAsync(_book, CoverWidth);
        if (_pendingCover is not null) return;
        CoverImage.Source = cover;
        CoverInitial.IsVisible = cover is null;
    }

    private void ShowStatus(string? text)
    {
        StatusText.Text = text;
        StatusText.IsVisible = !string.IsNullOrEmpty(text);
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

        if (!BookDetailEditing.TryParseRating(RatingBox.Text, out var rating))
        {
            ShowStatus("请输入 0 到 10 之间的评分，或留空清除评分");
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
            if (_book.OnlineRating != rating)
                _book.OnlineRatingCount = null;
            _book.OnlineRating = rating;
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
        var (rating, ratingCount, coverPath) = (book.OnlineRating, book.OnlineRatingCount, book.CoverPath);
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
            book.OnlineRating = rating;
            book.OnlineRatingCount = ratingCount;
            book.CoverPath = coverPath;
        };
    }

    /// <summary>Stores the picked cover beside the other library covers.</summary>
    private async Task<string> SaveCoverAsync(byte[] cover)
    {
        var paths = _modules.Paths;
        paths.EnsureDirectories();
        var path = Path.Combine(paths.Covers, $"{_book.Id:N}-manual-{Guid.NewGuid():N}.png");
        var temporary = path + ".tmp";
        using (var source = new MemoryStream(cover))
        using (var image = new Bitmap(source))
        await using (var output = File.Create(temporary))
            image.Save(output, PngBitmapEncoderOptions.Default);
        File.Move(temporary, path, overwrite: true);
        _shell.Covers.Forget(path);
        return Path.GetRelativePath(paths.Data, path);
    }

    private void OnBackClick(object? sender, RoutedEventArgs e) => _shell.GoBack();

    /// <summary>Pushed pages only detach this one; work ends when it leaves history.</summary>
    public void OnClosed() => _lifetime.Cancel();
}
