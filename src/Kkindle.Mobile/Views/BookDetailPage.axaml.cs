using Avalonia.Controls;
using Avalonia.Interactivity;
using Kkindle.ApplicationServices;
using Kkindle.Core;
using Kkindle.Mobile.Views.Sheets;

namespace Kkindle.Mobile.Views;

public sealed record BookFact(string Label, string Value);

public partial class BookDetailPage : UserControl
{
    private const int CoverWidth = 492;
    private const int CollapsedDescriptionLines = 5;

    private readonly Book _book;
    private readonly ApplicationModules _modules;
    private readonly MobileShell _shell;

    /// <summary>Design-time only: keeps the XAML previewer and runtime loader working.</summary>
    public BookDetailPage()
    {
        _book = null!;
        _modules = null!;
        _shell = null!;
        InitializeComponent();
    }

    public BookDetailPage(Book book, ApplicationModules modules, MobileShell shell)
    {
        _book = book;
        _modules = modules;
        _shell = shell;
        InitializeComponent();
        Present();
        // Also runs when the reader or editor is popped, so the page stays current.
        Loaded += async (_, _) =>
        {
            Present();
            await LoadCoverAsync();
            await LoadProgressAsync();
        };
    }

    private void Present()
    {
        TitleText.Text = _book.Title;
        AuthorText.Text = string.IsNullOrWhiteSpace(_book.Authors) ? "未知作者" : _book.Authors;
        PresentMeta(0);
        CoverInitial.Text = string.IsNullOrEmpty(_book.Title) ? "?" : _book.Title[..1];
        FavoriteIcon.Filled = _book.IsFavorite;

        var description = _book.Description?.Trim();
        DescriptionPanel.IsVisible = !string.IsNullOrEmpty(description);
        DescriptionText.Text = description;
        ExpandButton.IsVisible = description?.Length > 120;

        var facts = new List<BookFact>();
        void Add(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) facts.Add(new BookFact(label, value.Trim()));
        }
        if (_book.DoubanRating is { } rating)
            Add("豆瓣评分", $"{rating:0.0}（{_book.DoubanRatingCount ?? 0} 人评价）");
        Add("丛书", _book.Series);
        Add("出版社", _book.Publisher);
        Add("出版日期", _book.PublishDate);
        Add("ISBN", _book.Isbn);
        Add("页数", _book.PageCount);
        Add("装帧", _book.Binding);
        Add("标签", _book.Tags);
        Add("分类", _book.Category);
        Add("文件", _book.FormatSummary);
        Add("添加时间", _book.CreatedAt.LocalDateTime.ToString("yyyy/MM/dd"));
        FactsList.ItemsSource = facts;
    }

    private void PresentMeta(double percent)
    {
        var format = _book.Files.FirstOrDefault()?.Format.ToUpperInvariant() ?? "—";
        var size = MobileFormat.Size(_book.Files.Sum(file => file.Size));
        MetaText.Text = percent > 0 ? $"{format} · {size} · 已读 {percent:0}%" : $"{format} · {size}";
    }

    private async Task LoadCoverAsync()
    {
        var cover = await _shell.Covers.GetAsync(
            _book, CoverWidth);
        CoverImage.Source = cover;
        CoverInitial.IsVisible = cover is null;
    }

    private async Task LoadProgressAsync()
    {
        using var operation = MobileDataCoordinator.TryBeginOperation();
        if (operation is null) return;
        var file = _book.Files.FirstOrDefault();
        if (file is null) return;
        try
        {
            await _shell.EnsureReaderDataAsync();
            var percent = (await _modules.Reader.Data.GetProgressAsync(file.Id))?.ProgressPercent ?? 0;
            PresentMeta(percent);
            StartReadButton.Content = percent > 0 ? $"继续阅读 · {percent:0}%" : "开始阅读";
        }
        catch (Exception exception)
        {
            _shell.ShowToast("读取阅读进度失败：" + exception.Message);
        }
    }

    private void OnBackClick(object? sender, RoutedEventArgs e) => _shell.GoBack();

    private void OnStartReadClick(object? sender, RoutedEventArgs e) => _shell.OpenReader(_book);

    private void OnExpandClick(object? sender, RoutedEventArgs e)
    {
        var expanded = DescriptionText.MaxLines == 0;
        DescriptionText.MaxLines = expanded ? CollapsedDescriptionLines : 0;
        ExpandButton.Content = expanded ? "展开全部" : "收起";
    }

    private async void OnFavoriteClick(object? sender, RoutedEventArgs e)
    {
        using var operation = MobileDataCoordinator.TryBeginOperation();
        if (operation is null) return;
        var previous = _book.IsFavorite;
        try
        {
            _book.IsFavorite = !previous;
            FavoriteIcon.Filled = _book.IsFavorite;
            await _modules.Library.Books.UpdateMetadataAsync(_book);
            _shell.ShowToast(_book.IsFavorite ? "已收藏" : "已取消收藏");
        }
        catch (Exception exception)
        {
            _book.IsFavorite = previous;
            FavoriteIcon.Filled = previous;
            _shell.ShowToast("操作失败：" + exception.Message);
        }
    }

    private void OnEditClick(object? sender, RoutedEventArgs e) => _shell.OpenBookEditor(_book);

    /// <summary>Amazon's document service accepts EPUB and PDF by e-mail.</summary>
    private async void OnSendToKindleClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var settings = await _modules.Integrations.KindleEmailSettings.LoadAsync();
            if (!settings.IsConfigured)
            {
                _shell.ShowToast("请先设置发送邮箱");
                _shell.OpenKindleSettings();
                return;
            }
            var file = _book.Files.FirstOrDefault(item => item.Format.Equals("epub", StringComparison.OrdinalIgnoreCase))
                ?? _book.Files.FirstOrDefault(item => item.Format.Equals("pdf", StringComparison.OrdinalIgnoreCase));
            if (file is null)
            {
                _shell.ShowToast("Kindle 邮箱只接收 EPUB 或 PDF");
                return;
            }
            var path = _modules.Library.Books.GetAbsoluteFilePath(file);
            if (!File.Exists(path))
            {
                _shell.ShowToast("书籍文件不在本机，请先打开一次以下载");
                return;
            }
            _shell.ShowToast("正在发送…");
            await _modules.Integrations.KindleEmail.SendAsync(settings, path, _book.Title);
            _shell.ShowToast($"已发送到 {settings.KindleEmailAddress}");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _shell.ShowToast("发送失败：" + UiText.Localize(exception.Message));
        }
    }

    private async void OnAddToShelfClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var shelves = await _modules.Library.Books.GetCollectionsAsync();
            var items = shelves
                .Where(shelf => !MobileCollections.IsUncollected(shelf))
                .Select(shelf => new ChoiceItem(shelf.Name, Selected: _book.CollectionIds.Contains(shelf.Id), Tag: shelf))
                .Append(new ChoiceItem("新建收藏夹…"))
                .ToList();
            _shell.ShowSheet("加入收藏夹", new ChoiceSheet(items, choice =>
            {
                if (choice.Tag is BookCollection shelf) _ = ToggleShelfAsync(shelf, choice.Selected);
                else PromptNewShelf();
            }));
        }
        catch (Exception exception)
        {
            _shell.ShowToast("读取收藏夹失败：" + exception.Message);
        }
    }

    private async Task ToggleShelfAsync(BookCollection shelf, bool wasIn)
    {
        using var operation = MobileDataCoordinator.TryBeginOperation();
        if (operation is null) return;
        _shell.HideSheet();
        try
        {
            if (wasIn)
            {
                await _modules.Library.Books.RemoveBookFromCollectionAsync(_book.Id, shelf.Id);
                _book.CollectionIds.Remove(shelf.Id);
            }
            else
            {
                await _modules.Library.Books.AddBookToCollectionAsync(_book.Id, shelf.Id);
                _book.CollectionIds.Add(shelf.Id);
            }
            _shell.ShowToast(wasIn ? $"已移出「{shelf.Name}」" : $"已加入「{shelf.Name}」");
        }
        catch (Exception exception)
        {
            _shell.ShowToast("操作失败：" + exception.Message);
        }
    }

    private void PromptNewShelf()
    {
        _shell.ShowSheet("新建收藏夹", new PromptSheet(null, "创建并加入", async name =>
        {
            using var operation = MobileDataCoordinator.TryBeginOperation();
            if (operation is null) return;
            try
            {
                var shelf = await _modules.Library.Books.CreateCollectionAsync(name);
                await ToggleShelfAsync(shelf, wasIn: false);
            }
            catch (Exception exception)
            {
                _shell.ShowToast("创建收藏夹失败：" + exception.Message);
            }
        }, placeholder: "收藏夹名称"));
    }

    private void OnRenameClick(object? sender, RoutedEventArgs e)
    {
        _shell.ShowSheet("修改书名", new PromptSheet(null, "保存", async title =>
        {
            using var operation = MobileDataCoordinator.TryBeginOperation();
            if (operation is null) return;
            try
            {
                await _modules.Library.Books.RenameBookAsync(_book.Id, title);
                var updated = await _modules.Library.Books.GetBookAsync(_book.Id)
                    ?? throw new InvalidOperationException("书籍已不存在。");
                _book.Title = updated.Title;
                _book.Files = updated.Files;
                Present();
                _shell.HideSheet();
                _shell.ShowToast("书名已修改");
            }
            catch (Exception exception)
            {
                _shell.ShowToast("修改书名失败：" + exception.Message);
            }
        }, initialText: _book.Title, placeholder: "输入书名"));
    }

    private void OnDeleteClick(object? sender, RoutedEventArgs e)
    {
        _shell.ShowSheet("删除这本书", new PromptSheet($"《{_book.Title}》将移到回收站。", "移到回收站", async _ =>
        {
            using var operation = MobileDataCoordinator.TryBeginOperation();
            if (operation is null) return;
            try
            {
                await _modules.Library.Books.DeleteAsync(_book.Id);
                _shell.ShowLibrary();
                _shell.ShowToast("已移到回收站");
            }
            catch (Exception exception)
            {
                _shell.ShowToast("删除失败：" + exception.Message);
            }
        }));
    }
}
