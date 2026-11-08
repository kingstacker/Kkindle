using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Kkindle.Core;

namespace Kkindle.Mobile.Views.Sheets;

public sealed class SearchHit(string title, string excerpt, string query, object source, bool showsCover = false)
    : ObservableObject
{
    private Bitmap? _cover;

    public string Title { get; } = title;
    public string Excerpt { get; } = excerpt;
    public string Query { get; } = query;
    public object Source { get; } = source;

    /// <summary>Book results reserve a cover slot; in-book text results do not.</summary>
    public bool ShowsCover { get; } = showsCover;

    public Bitmap? Cover
    {
        get => _cover;
        set => SetProperty(ref _cover, value);
    }
}

/// <summary>Whole-book search; the reader supplies the search and the jump.</summary>
public partial class SearchSheet : UserControl, IFullHeightSheet
{
    private readonly Func<string, IProgress<string>, CancellationToken, Task<IReadOnlyList<SearchHit>>>? _search;
    private readonly Action<SearchHit>? _pick;
    private CancellationTokenSource? _running;

    /// <summary>Design-time only.</summary>
    public SearchSheet() => InitializeComponent();

    public SearchSheet(
        Func<string, IProgress<string>, CancellationToken, Task<IReadOnlyList<SearchHit>>> search,
        Action<SearchHit> pick,
        string? lastQuery,
        bool searchImmediately = false)
    {
        _search = search;
        _pick = pick;
        InitializeComponent();
        QueryBox.Text = lastQuery;
        Loaded += async (_, _) =>
        {
            if (searchImmediately) await SearchAsync();
            else QueryBox.Focus();
        };
        DetachedFromVisualTree += (_, _) => _running?.Cancel();
    }

    public bool FillsHeight => true;

    public string? Query => QueryBox.Text?.Trim();

    private async void OnSearchClick(object? sender, RoutedEventArgs e) => await SearchAsync();

    private async void OnQueryKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await SearchAsync();
    }

    private async Task SearchAsync()
    {
        if (_search is null || string.IsNullOrWhiteSpace(Query)) return;
        _running?.Cancel();
        var running = _running = new CancellationTokenSource();
        Results.ItemsSource = null;
        ShowStatus("正在搜索…");
        try
        {
            var hits = await _search(Query, new Progress<string>(ShowStatus), running.Token);
            if (running.IsCancellationRequested) return;
            Results.ItemsSource = hits;
            ShowStatus(hits.Count == 0 ? "没有找到相关内容" : $"共 {hits.Count} 条");
        }
        catch (OperationCanceledException) when (running.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ShowStatus("搜索失败：" + UiText.Localize(exception.Message));
        }
    }

    private void ShowStatus(string text)
    {
        StatusText.Text = text;
        StatusText.IsVisible = true;
    }

    private void OnHitClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: SearchHit hit }) _pick?.Invoke(hit);
    }
}
