using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using Kkindle.ApplicationServices;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Kkindle.Mobile.Views.Sheets;

namespace Kkindle.Mobile.Views;

public enum MaterialKind
{
    Highlight,
    Note,
    Bookmark
}

public sealed record MaterialEntry(
    Book Book,
    MaterialKind Kind,
    string Quote,
    string? Note,
    string Meta,
    DateTimeOffset UpdatedAt,
    ReaderProgressRow Location,
    string ExportLocation)
{
    public bool HasNote => !string.IsNullOrWhiteSpace(Note);
    public bool IsBookmark => Kind == MaterialKind.Bookmark;
}

public sealed class MaterialBookGroup(Book book, IReadOnlyList<MaterialEntry> entries, Bitmap? cover, bool expanded) : ObservableObject
{
    private bool _isExpanded = expanded;
    public Book Book { get; } = book;
    public IReadOnlyList<MaterialEntry> Entries { get; } = entries;
    public Bitmap? Cover { get; } = cover;
    public bool HasCover => Cover is not null;
    public string Title => Book.Title;
    public string TitleInitial => string.IsNullOrWhiteSpace(Title) ? "?" : Title[..1];
    public string CountText => $"{Entries.Count} 条资料";
    public bool IsExpanded { get => _isExpanded; set => SetProperty(ref _isExpanded, value); }
}

public partial class MaterialsPage : UserControl
{
    private readonly ApplicationModules? _modules;
    private readonly MobileShell? _shell;
    private List<MaterialEntry> _entries = [];
    private MaterialKind? _filter;
    private readonly Dictionary<Guid, Bitmap?> _covers = [];
    private readonly HashSet<Guid> _expandedBooks = [];
    private bool _exporting;

    public MaterialsPage() => InitializeComponent();

    public MaterialsPage(ApplicationModules modules, MobileShell shell)
    {
        _modules = modules;
        _shell = shell;
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (_modules is null || _shell is null) return;
        using var operation = MobileDataCoordinator.TryBeginOperation();
        if (operation is null) return;
        try
        {
            await _modules.Library.Books.InitializeAsync();
            await _shell.EnsureReaderDataAsync();
            var books = await _modules.Library.Books.SearchAsync();
            var entries = new List<MaterialEntry>();
            foreach (var book in books)
            foreach (var file in book.Files)
            {
                foreach (var bookmark in await _modules.Reader.Data.GetBookmarksAsync(file.Id))
                {
                    entries.Add(new MaterialEntry(book, MaterialKind.Bookmark, bookmark.DisplayQuote, null,
                        $"书签 · {bookmark.DisplayTitle}", bookmark.CreatedAt,
                        new ReaderProgressRow(book.Id, file.Id, bookmark.ChapterPath, null, bookmark.ChapterIndex,
                            bookmark.ScrollPosition ?? 0, 0, bookmark.FlowMode, bookmark.CreatedAt),
                        bookmark.DisplayTitle));
                }
                foreach (var annotation in await _modules.Reader.Data.GetAnnotationsAsync(file.Id))
                {
                    var hasNote = !string.IsNullOrWhiteSpace(annotation.Note);
                    entries.Add(new MaterialEntry(book,
                        hasNote ? MaterialKind.Note : MaterialKind.Highlight,
                        annotation.DisplayQuote,
                        annotation.Note,
                        $"{(hasNote ? "笔记" : "划线")} · {annotation.UpdatedAt.LocalDateTime:yyyy/MM/dd}",
                        annotation.UpdatedAt,
                        new ReaderProgressRow(book.Id, file.Id, annotation.ChapterPath, null, ChapterIndexOf(annotation),
                            0, 0, 1, annotation.UpdatedAt)
                        {
                            ContentPosition = new ReaderContentPosition { TextOffset = annotation.StartOffset }
                        },
                        ChapterIndexOf(annotation) is var pageIndex && pageIndex >= 0
                            ? $"第 {pageIndex + 1} 页"
                            : annotation.ChapterPath));
                }
            }
            _entries = entries;
            foreach (var book in entries.Select(entry => entry.Book).DistinctBy(book => book.Id))
                _covers[book.Id] = await _shell.Covers.GetAsync(book, 120);
            StatusText.IsVisible = false;
            Present();
        }
        catch (Exception exception)
        {
            StatusText.Text = "读取阅读资料失败：" + exception.Message;
            StatusText.IsVisible = true;
        }
    }

    private static int ChapterIndexOf(ReaderAnnotation annotation) =>
        annotation.ChapterPath.StartsWith("pdf:page:", StringComparison.Ordinal)
        && int.TryParse(annotation.ChapterPath["pdf:page:".Length..], out var page)
            ? page - 1
            : -1;

    private void Present()
    {
        var visible = _entries
            .Where(entry => _filter is null || entry.Kind == _filter)
            .GroupBy(entry => entry.Book.Id)
            .OrderByDescending(group => group.Max(entry => entry.UpdatedAt))
            .Select(group => new MaterialBookGroup(group.First().Book,
                group.OrderByDescending(entry => entry.UpdatedAt).ToList(),
                _covers.GetValueOrDefault(group.Key), _expandedBooks.Contains(group.Key)))
            .ToList();
        MaterialsList.ItemsSource = visible;
        SummaryText.Text = $"共 {_entries.Count} 条";
        ExportButton.IsEnabled = !_exporting && visible.Count > 0;
        EmptyState.IsVisible = visible.Count == 0;
        EmptyTitle.Text = _entries.Count == 0 ? "还没有阅读资料" : "这一类还没有内容";
    }

    private void OnBookClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: MaterialBookGroup group }) return;
        group.IsExpanded = !group.IsExpanded;
        if (group.IsExpanded) _expandedBooks.Add(group.Book.Id);
        else _expandedBooks.Remove(group.Book.Id);
    }

    private void OnFilterClick(object? sender, RoutedEventArgs e)
    {
        _filter = sender switch
        {
            Button { Name: nameof(FilterHighlights) } => MaterialKind.Highlight,
            Button { Name: nameof(FilterNotes) } => MaterialKind.Note,
            Button { Name: nameof(FilterBookmarks) } => MaterialKind.Bookmark,
            _ => null
        };
        FilterAll.Classes.Set("on", _filter is null);
        FilterHighlights.Classes.Set("on", _filter == MaterialKind.Highlight);
        FilterNotes.Classes.Set("on", _filter == MaterialKind.Note);
        FilterBookmarks.Classes.Set("on", _filter == MaterialKind.Bookmark);
        Present();
    }

    private void OnExportClick(object? sender, RoutedEventArgs e)
    {
        if (_shell is null || _exporting) return;
        var entries = _entries.Where(entry => _filter is null || entry.Kind == _filter)
            .OrderByDescending(entry => entry.UpdatedAt).ToArray();
        if (entries.Length == 0) return;
        _shell.ShowSheet($"导出当前筛选 · {entries.Length} 条", new ChoiceSheet(
            [new ChoiceItem("Markdown", "适合整理和继续编辑", Tag: true),
             new ChoiceItem("纯文本", "适合在文本应用中打开", Tag: false)],
            choice =>
            {
                _shell.HideSheet();
                _ = ExportAsync(entries, (bool)choice.Tag!);
            }));
    }

    private async Task ExportAsync(IReadOnlyList<MaterialEntry> entries, bool markdown)
    {
        if (_shell is null || _exporting) return;
        _exporting = true;
        ExportButton.IsEnabled = false;
        StatusText.IsVisible = false;
        try
        {
            var records = entries.Select(entry => new ReadingMaterialRecord(
                ReadingMaterialSource.Local, entry.Book.Title,
                entry.Kind switch { MaterialKind.Note => "笔记", MaterialKind.Bookmark => "书签", _ => "划线" },
                entry.ExportLocation, entry.Quote, entry.Note ?? string.Empty, entry.UpdatedAt)).ToArray();
            var fileName = $"Kkindle-阅读资料-{DateTime.Now:yyyyMMdd-HHmmss}.{(markdown ? "md" : "txt")}";
            await using var content = new MemoryStream();
            if (markdown) await ReadingMaterialsExport.WriteMarkdownAsync(content, records);
            else await ReadingMaterialsExport.WritePlainTextAsync(content, records);

            if (_shell.Services.FileShare is { } share)
            {
                if (await share.ShareAsync(fileName, content.ToArray()))
                    _shell.ShowToast($"已分享 {records.Length} 条阅读资料");
                return;
            }

            var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storage is null || !storage.CanSave)
                throw new InvalidOperationException("此设备不支持保存文件。");
            var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "导出阅读资料",
                SuggestedFileName = fileName,
                FileTypeChoices = [new FilePickerFileType(markdown ? "Markdown" : "文本")
                {
                    Patterns = [markdown ? "*.md" : "*.txt"],
                    MimeTypes = [markdown ? "text/markdown" : "text/plain"]
                }]
            });
            if (file is null) return;
            await using (var destination = await file.OpenWriteAsync())
            {
                content.Position = 0;
                await content.CopyToAsync(destination);
            }
            _shell.ShowToast($"已导出 {records.Length} 条阅读资料");
        }
        catch (Exception exception)
        {
            StatusText.Text = "导出阅读资料失败：" + exception.Message;
            StatusText.IsVisible = true;
        }
        finally
        {
            _exporting = false;
            Present();
        }
    }

    private void OnItemTapped(object? sender, TappedEventArgs e)
    {
        StyledElement? source = e.Source as StyledElement;
        while (source is not null && source.DataContext is not MaterialEntry)
            source = source.Parent as StyledElement;
        if (source?.DataContext is MaterialEntry entry) _shell?.OpenReader(entry.Book, entry.Location);
    }
}
