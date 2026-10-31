using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Kkindle.ApplicationServices;
using Kkindle.Core;
using Kkindle.Mobile.Views.Sheets;

namespace Kkindle.Mobile.Views;

/// <summary>One search result; its cover and download state change after it is shown.</summary>
public sealed class ZLibraryRow(ZLibraryBook book) : ObservableObject
{
    private Bitmap? _cover;
    private string? _state;

    public ZLibraryBook Book { get; } = book;

    public string Info => string.Join(" · ", new[]
    {
        Book.InfoLabel,
        Book.Year is > 0 ? Book.Year.Value.ToString(CultureInfo.InvariantCulture) : null
    }.Where(part => !string.IsNullOrWhiteSpace(part)));

    public string Initial => string.IsNullOrEmpty(Book.Title) ? "?" : Book.Title[..1];
    public bool HasCover => Cover is not null;
    public bool HasState => !string.IsNullOrEmpty(State);

    public Bitmap? Cover
    {
        get => _cover;
        set
        {
            if (SetProperty(ref _cover, value)) OnPropertyChanged(nameof(HasCover));
        }
    }

    public string? State
    {
        get => _state;
        set
        {
            if (SetProperty(ref _state, value)) OnPropertyChanged(nameof(HasState));
        }
    }
}

/// <summary>Developer-mode Z-Library search with download into the library, as on desktop.</summary>
public partial class ZLibraryPage : UserControl
{
    private const int PageSize = 20;
    private const int ThumbnailWidth = 156;

    private readonly ApplicationModules? _modules;
    private readonly MobileShell? _shell;
    private readonly ZLibraryDownloader? _downloader;
    private readonly ObservableCollection<ZLibraryRow> _rows = [];
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _search;
    private string _query = string.Empty;
    private string? _format;
    private int _page;
    private int _pageCount;

    /// <summary>Design-time only.</summary>
    public ZLibraryPage() => InitializeComponent();

    public ZLibraryPage(ApplicationModules modules, MobileShell shell)
    {
        _modules = modules;
        _shell = shell;
        _downloader = new ZLibraryDownloader(modules);
        InitializeComponent();
        ResultsList.ItemsSource = _rows;
        DetachedFromVisualTree += (_, _) => _lifetime.Cancel();
    }

    private void ShowStatus(string? text)
    {
        StatusText.Text = text;
        StatusText.IsVisible = !string.IsNullOrEmpty(text);
    }

    private async void OnSearchClick(object? sender, RoutedEventArgs e) => await SearchAsync(reset: true);

    private async void OnQueryKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await SearchAsync(reset: true);
    }

    private async void OnMoreClick(object? sender, RoutedEventArgs e) => await SearchAsync(reset: false);

    private async void OnFormatClick(object? sender, RoutedEventArgs e)
    {
        _format = sender switch
        {
            Button { Name: nameof(FormatEpub) } => "epub",
            Button { Name: nameof(FormatPdf) } => "pdf",
            Button { Name: nameof(FormatMobi) } => "mobi",
            Button { Name: nameof(FormatAzw3) } => "azw3",
            _ => null
        };
        FormatAll.Classes.Set("on", _format is null);
        FormatEpub.Classes.Set("on", _format == "epub");
        FormatPdf.Classes.Set("on", _format == "pdf");
        FormatMobi.Classes.Set("on", _format == "mobi");
        FormatAzw3.Classes.Set("on", _format == "azw3");
        if (_query.Length > 0) await SearchAsync(reset: true);
    }

    private async Task SearchAsync(bool reset)
    {
        if (reset)
        {
            _query = QueryBox.Text?.Trim() ?? string.Empty;
            _page = 0;
        }
        if (_query.Length == 0) return;

        _search?.Cancel();
        var search = _search = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        MoreButton.IsEnabled = false;
        EmptyState.IsVisible = false;
        ShowStatus(reset ? "正在搜索…" : "正在加载…");
        try
        {
            var settings = await _modules!.Integrations.ZLibrarySettings.LoadAsync(search.Token);
            var service = _downloader!.Service;
            // Search works without an account; logging in first uses its mirror.
            if (settings.IsConfigured && !service.IsLoggedIn)
                await service.LoginAsync(settings.Email, settings.Password, settings.BaseUrl, search.Token);
            var result = await service.SearchAsync(
                _query, _page + 1, PageSize, _format is null ? null : [_format], cancellationToken: search.Token);
            if (search.IsCancellationRequested) return;

            if (reset) _rows.Clear();
            var library = await _modules.Library.Books.GetLibraryMatchRecordsAsync(search.Token);
            foreach (var book in result.Books)
            {
                var row = new ZLibraryRow(book)
                {
                    State = library.Any(local => ZLibraryMetadata.Matches(local, book)) ? "已在书库" : null
                };
                _rows.Add(row);
                _ = LoadCoverAsync(row, search.Token);
            }
            _page = result.Page;
            _pageCount = result.PageCount;
            MoreButton.IsVisible = _page < _pageCount;
            ShowStatus(_rows.Count == 0 ? "没有找到相关书籍" : $"共 {result.Total} 本");
            if (reset) ResultsScroller.ScrollToHome();
        }
        catch (OperationCanceledException) when (search.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ShowStatus("搜索失败：" + UiText.Localize(exception.Message));
        }
        finally
        {
            MoreButton.IsEnabled = true;
        }
    }

    private static async Task LoadCoverAsync(ZLibraryRow row, CancellationToken token)
    {
        if (await ZLibraryDownloader.TryGetCoverAsync(row.Book.CoverUrl, token) is not { Length: > 0 } bytes) return;
        try
        {
            using var stream = new MemoryStream(bytes);
            row.Cover = Bitmap.DecodeToWidth(stream, ThumbnailWidth);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Unreadable covers keep the title initial.
        }
    }

    private void OnRowClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ZLibraryRow row }) return;
        _shell!.ShowSheet("书籍详情", new ZLibraryBookSheet(row, () => _ = DownloadAsync(row)));
    }

    private async Task DownloadAsync(ZLibraryRow row)
    {
        var sheet = _shell!.CurrentSheet as ZLibraryBookSheet;
        try
        {
            if (await _downloader!.FindInLibraryAsync(row.Book, _lifetime.Token) is { } existing)
            {
                row.State = "已在书库";
                sheet?.ShowStatus($"书库中已有《{existing.Title}》，未重复下载");
                return;
            }

            sheet?.SetBusy(true);
            var progress = new Progress<TransferProgress>(value =>
            {
                var text = value.TotalBytes > 0
                    ? $"正在下载 {value.Percentage:0}%（{MobileFormat.Size(value.BytesCopied)} / {MobileFormat.Size(value.TotalBytes)}）"
                    : $"正在下载 {MobileFormat.Size(value.BytesCopied)}";
                row.State = text;
                sheet?.ShowStatus(text);
            });
            row.State = "正在下载…";
            sheet?.ShowStatus("正在登录并获取下载地址…");
            var book = await _downloader.DownloadAsync(row.Book, progress, _lifetime.Token);
            row.State = "已在书库";
            sheet?.ShowStatus("已下载并导入书库，书籍详情已写入");
            _shell.ShowToast($"《{book.Title}》已加入书库");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            row.State = "下载失败";
            sheet?.ShowStatus("下载失败：" + UiText.Localize(exception.Message));
        }
        finally
        {
            sheet?.SetBusy(false);
        }
    }

    private void OnAccountClick(object? sender, RoutedEventArgs e) => _shell?.OpenZLibraryAccount();

    private void OnBackClick(object? sender, RoutedEventArgs e) => _shell?.GoBack();
}
