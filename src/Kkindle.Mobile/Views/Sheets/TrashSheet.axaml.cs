using Avalonia.Controls;
using Avalonia.Interactivity;
using Kkindle.Core;

namespace Kkindle.Mobile.Views.Sheets;

public partial class TrashSheet : UserControl, IFullHeightSheet
{
    private readonly IBookLibraryService? _library;
    private readonly MobileShell? _shell;
    private LibraryTrashItem? _pendingPurge;
    private bool _busy;

    public TrashSheet() => InitializeComponent();

    public TrashSheet(IBookLibraryService library, MobileShell shell)
    {
        _library = library;
        _shell = shell;
        InitializeComponent();
        Loaded += OnLoaded;
    }

    public bool FillsHeight => true;

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await ReloadAsync();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        Rows.IsEnabled = !busy && _pendingPurge is null;
        RefreshButton.IsEnabled = !busy && _pendingPurge is null;
        ConfirmPanel.IsEnabled = !busy;
    }

    private async Task ReloadAsync()
    {
        if (_busy || _library is null) return;
        using var operation = MobileDataCoordinator.TryBeginOperation();
        if (operation is null) return;
        SetBusy(true);
        try
        {
            await _library.InitializeAsync();
            var items = await _library.GetTrashItemsAsync();
            Rows.ItemsSource = items;
            EmptyText.IsVisible = items.Count == 0;
            StatusText.Text = items.Count == 0 ? "暂无已删除的项目" : $"共 {items.Count} 项";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            EmptyText.IsVisible = false;
            StatusText.Text = "读取回收站失败：" + UiText.Localize(exception.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnRefreshClick(object? sender, RoutedEventArgs e) => await ReloadAsync();

    private async void OnRestoreClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _library is null || sender is not Button { DataContext: LibraryTrashItem item }) return;
        using var operation = MobileDataCoordinator.TryBeginOperation();
        if (operation is null) return;
        SetBusy(true);
        try
        {
            await _library.RestoreTrashItemAsync(item.Id);
            _shell?.ShowToast("已恢复《" + item.DisplayTitle + "》");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _shell?.ShowToast("恢复失败：" + UiText.Localize(exception.Message));
        }
        finally
        {
            SetBusy(false);
        }
        await ReloadAsync();
    }

    private void OnPurgeClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || sender is not Button { DataContext: LibraryTrashItem item }) return;
        _pendingPurge = item;
        ConfirmText.Text = $"《{item.DisplayTitle}》将被永久删除，之后无法恢复。确定继续吗？";
        ConfirmPanel.IsVisible = true;
        SetBusy(false);
    }

    private void OnCancelPurgeClick(object? sender, RoutedEventArgs e) => ClearConfirmation();

    private void ClearConfirmation()
    {
        _pendingPurge = null;
        ConfirmPanel.IsVisible = false;
        SetBusy(false);
    }

    private async void OnConfirmPurgeClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _library is null || _pendingPurge is not { } item) return;
        using var operation = MobileDataCoordinator.TryBeginOperation();
        if (operation is null) return;
        SetBusy(true);
        try
        {
            await _library.PurgeTrashItemAsync(item.Id);
            _shell?.ShowToast("已永久删除《" + item.DisplayTitle + "》");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _shell?.ShowToast("永久删除失败：" + UiText.Localize(exception.Message));
        }
        finally
        {
            ClearConfirmation();
        }
        await ReloadAsync();
    }
}
