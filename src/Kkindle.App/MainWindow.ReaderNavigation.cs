using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Kkindle.Core;

namespace Kkindle;

public partial class MainWindow
{
    private string _readerReturnLabelSource = "返回书库";
    private Control? _readerReturnFocus;
    private static string ReaderReturnShortcut => OperatingSystem.IsMacOS() ? "⌘[" : "Ctrl+Shift+B";

    private void InitializeReaderNavigation()
    {
        ReaderRoot.PropertyChanged += (_, e) =>
        {
            if (e.Property != IsVisibleProperty || !ReaderRoot.IsVisible) return;
            _readerReturnFocus = FocusManager?.GetFocusedElement() as Control;
            _readerReturnLabelSource = ReadingMaterialsPage.IsVisible ? "返回笔记"
                : ReadingDashboardPage.IsVisible ? "返回阅读数据"
                : DevicePage.IsVisible ? "返回设备书库"
                : SettingsPage.IsVisible ? "返回设置" : "返回书库";
            UpdateReaderNavigationLabels();
        };
        InitializeReaderNavigationMenus();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == NativeMenu.MenuProperty) InitializeReaderNavigationMenus();
        };
        UpdateReaderNavigationLabels();
    }

    private NativeMenu? _readerNavigationMenu;

    private void InitializeReaderNavigationMenus()
    {
        if (!OperatingSystem.IsMacOS() || NativeMenu.GetMenu(this) is not { } menu
            || ReferenceEquals(menu, _readerNavigationMenu)) return;
        var file = menu.Items.OfType<NativeMenuItem>().FirstOrDefault(item => item.Header == T("文件"))?.Menu;
        var view = menu.Items.OfType<NativeMenuItem>().FirstOrDefault(item => item.Header == T("视图"))?.Menu;
        if (file is null || view is null) return;
        _readerNavigationMenu = menu;
        var read = new NativeMenuItem(T("阅读所选书籍")) { Gesture = new KeyGesture(Key.O, KeyModifiers.Meta) };
        read.Click += (_, _) =>
        {
            if (LibraryRoot.IsVisible && LibraryWorkspace.IsVisible && GetSingleSelectedLibraryBook() is { } card)
                _ = ObserveReaderTaskAsync(OpenBookAsync(card));
        };
        file.Items.Insert(0, read);
        file.NeedsUpdate += (_, _) => read.IsEnabled = LibraryRoot.IsVisible
            && LibraryWorkspace.IsVisible && GetSingleSelectedLibraryBook() is not null;
        var back = new NativeMenuItem(T("返回书库")) { Gesture = new KeyGesture(Key.OemOpenBrackets, KeyModifiers.Meta) };
        back.Click += (_, _) => { _ = ReturnFromReaderAsync(); };
        view.Items.Add(back);
        view.NeedsUpdate += (_, _) =>
        {
            back.Header = T(_readerReturnLabelSource);
            back.IsEnabled = ReaderRoot.IsVisible;
        };
    }

    private static bool IsReaderOpenGesture(KeyEventArgs e) => e.Key == Key.O
        && e.KeyModifiers == (OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control);

    private static string ReaderShortcutLabel(string text) => OperatingSystem.IsMacOS()
        ? text.Replace("Ctrl+", "⌘", StringComparison.OrdinalIgnoreCase) : text;

    private void UpdateReaderNavigationLabels()
    {
        if (ReaderBackButton is null) return;
        var label = T(_readerReturnLabelSource);
        foreach (var button in new[] { ReaderBackButton, ReaderCollapsedBackButton })
        {
            ToolTip.SetTip(button, label + " · " + ReaderReturnShortcut);
            Avalonia.Automation.AutomationProperties.SetName(button, label);
        }
        ReaderZenPopupBackButton.Content = label;
    }

    private static bool IsReaderReturnGesture(KeyEventArgs e) => OperatingSystem.IsMacOS()
        ? e.Key == Key.OemOpenBrackets && e.KeyModifiers == KeyModifiers.Meta
        : e.Key == Key.B && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift);

    private async void ReaderBackButton_Click(object? sender, RoutedEventArgs e) => await ReturnFromReaderAsync();

    private async Task ReturnFromReaderAsync()
    {
        if (!ReaderRoot.IsVisible || Volatile.Read(ref _readerCloseInProgress) != 0) return;
        await CloseReaderAsync();
        if (_readerReturnFocus is { IsEffectivelyVisible: true } target) target.Focus();
        else if (ReadingMaterialsPage.IsVisible) ReaderNotesNavigationButton.Focus();
        else if (ReadingDashboardPage.IsVisible) ReadingDashboardButton.Focus();
        else if (LibraryWorkspace.IsVisible) (BookList.IsVisible ? (Control)BookList : BookGrid).Focus();
        _readerReturnFocus = null;
    }

    private BookCardViewModel? GetSingleSelectedLibraryBook() => _selectedBookIds.Count == 1
        ? ViewModel.Books.FirstOrDefault(card => _selectedBookIds.Contains(card.Book.Id)) : null;

    private async void BookCoverReadButton_Click(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        var card = (sender as Control)?.DataContext as BookCardViewModel ?? GetSingleSelectedLibraryBook();
        if (card is not null) await OpenBookAsync(card);
    }
}
