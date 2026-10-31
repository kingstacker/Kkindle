using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Kkindle.ApplicationServices;
using Kkindle.Core;

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
    ReaderProgressRow Location)
{
    public string BookTitle => Book.Title;
    public bool HasNote => !string.IsNullOrWhiteSpace(Note);
    public bool IsFirstOfBook { get; init; }
}

public partial class MaterialsPage : UserControl
{
    private readonly ApplicationModules? _modules;
    private readonly MobileShell? _shell;
    private List<MaterialEntry> _entries = [];
    private MaterialKind? _filter;

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
                            bookmark.ScrollPosition ?? 0, 0, bookmark.FlowMode, bookmark.CreatedAt)));
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
                        }));
                }
            }
            _entries = entries;
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

    /// <summary>Groups by book, newest book first, so quotes read as one passage.</summary>
    private void Present()
    {
        var visible = _entries
            .Where(entry => _filter is null || entry.Kind == _filter)
            .GroupBy(entry => entry.Book.Id)
            .OrderByDescending(group => group.Max(entry => entry.UpdatedAt))
            .SelectMany(group => group
                .OrderByDescending(entry => entry.UpdatedAt)
                .Select((entry, index) => entry with { IsFirstOfBook = index == 0 }))
            .ToList();
        MaterialsList.ItemsSource = visible;
        SummaryText.Text = $"共 {_entries.Count} 条";
        EmptyState.IsVisible = visible.Count == 0;
        EmptyTitle.Text = _entries.Count == 0 ? "还没有阅读资料" : "这一类还没有内容";
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

    private void OnItemTapped(object? sender, TappedEventArgs e)
    {
        StyledElement? source = e.Source as StyledElement;
        while (source is not null && source.DataContext is not MaterialEntry)
            source = source.Parent as StyledElement;
        if (source?.DataContext is MaterialEntry entry) _shell?.OpenReader(entry.Book, entry.Location);
    }
}
