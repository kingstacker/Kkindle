using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia;
using Avalonia.VisualTree;

namespace Kkindle;

public partial class MainWindow
{
    private bool _deviceShelfControlsReady;

    private void InitializeDeviceShelfControls()
    {
        _deviceShelfControlsReady = true;
        SetDeviceBookView(true);
    }

    private void DeviceToolbar_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (DeviceToolbar is not null && DeviceBookSearchBox is not null && DeviceToolbarActions is not null)
            ArrangeShelfToolbar(DeviceToolbar, DeviceBookSearchBox, DeviceToolbarActions);
    }

    private void DeviceToolbar_LayoutUpdated(object? sender, EventArgs e)
    {
        if (!_deviceShelfControlsReady || !DeviceToolbar.IsEffectivelyVisible) return;
        NormalizeLibrarySortChrome(DeviceBookSortBox);
        AlignShelfToolbar(DeviceToolbar, DevicePage, DevicePage);
        ArrangeShelfToolbar(DeviceToolbar, DeviceBookSearchBox, DeviceToolbarActions);
    }

    private void DeviceViewToggleButton_Click(object? sender, RoutedEventArgs e) =>
        DeviceViewToggleButton.ContextMenu?.Open(DeviceViewToggleButton);

    private void DeviceViewMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: "Grid" }) SetDeviceBookView(true);
        else if (sender is MenuItem { Tag: "List" }) SetDeviceBookView(false);
    }

    private void RefreshDeviceShelfLanguage()
    {
        if (!_deviceShelfControlsReady) return;
        SetDeviceBookView(_deviceGridView);
        // Refresh the ComboBox's cached selected content after the language resources change.
        _deviceShelfControlsReady = false;
        try { RestoreComboBoxSelection(DeviceBookSortBox, DeviceBookSortBox.SelectedIndex); }
        finally { _deviceShelfControlsReady = true; }
    }

    private void LibrarySort_PointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        var sortBox = (sender as Control)?.GetVisualDescendants().OfType<ComboBox>().FirstOrDefault();
        if (sortBox is null || IsSourceWithin(e.Source, sortBox)
            || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        sortBox.Focus();
        sortBox.IsDropDownOpen = true;
        e.Handled = true;
    }

    private void LibraryToolbar_LayoutUpdated(object? sender, EventArgs e)
    {
        if (LibraryToolbar is null || LibraryWorkspace is null || LibraryContentHost is null)
            return;

        NormalizeLibrarySortChrome(LibrarySortBox);
        AlignShelfToolbar(LibraryToolbar, LibraryWorkspace, LibraryContentHost);
        AlignCollectionHeader(LibraryToolbar, CollectionHeader);
        AlignLibraryListRightEdge(LibraryToolbar, BookList);
    }

    private static void NormalizeLibrarySortChrome(ComboBox sortBox)
    {
        // The Fluent template reserves a fixed column even when its arrow is hidden.
        var sortGrid = sortBox.GetVisualDescendants().OfType<Grid>()
            .FirstOrDefault(grid => grid.Children.Any(child => child.Name == "DropDownGlyph"));
        if (sortGrid is { ColumnDefinitions.Count: 2 } && sortGrid.ColumnDefinitions[1].Width != new GridLength(0))
            sortGrid.ColumnDefinitions[1].Width = new GridLength(0);
        var sortBackground = sortBox.GetVisualDescendants().OfType<Border>()
            .FirstOrDefault(border => border.Name == "Background");
        if (sortBackground is not null && sortBackground.MinWidth != 0)
            sortBackground.MinWidth = 0;
    }

    private static void AlignShelfToolbar(Grid toolbar, Control workspace, Control shelfViewport)
    {
        if (shelfViewport.Bounds.Width <= 0
            || shelfViewport.TranslatePoint(default, workspace) is not { } origin)
            return;

        // Use the live book-area width even when its grid is hidden. Switching
        // views or resizing in list/collection view keeps the same column alignment.
        var actions = toolbar.Children.OfType<StackPanel>().First();
        var columns = Math.Max(1, Math.Floor(shelfViewport.Bounds.Width / BookGridSlotWidth));
        var left = Math.Max(0, origin.X + 6);
        var right = Math.Max(6, workspace.Bounds.Width - origin.X - columns * BookGridSlotWidth + 6);

        // Keep the action row usable when a docked detail pane leaves only a narrow book column.
        right = Math.Min(right, Math.Max(6, workspace.Bounds.Width - left - actions.DesiredSize.Width));
        var margin = new Thickness(left, 22, right, 18);
        if (toolbar.Margin != margin)
            toolbar.Margin = margin;
    }

    private static void AlignCollectionHeader(Grid toolbar, Border header)
    {
        // The toolbar margin is calculated from the live book area so its
        // content lines up with the book columns. Keep the collection title
        // row and its divider on the same horizontal bounds as that toolbar.
        var toolbarMargin = toolbar.Margin;
        var headerMargin = header.Margin;
        var margin = new Thickness(
            toolbarMargin.Left,
            headerMargin.Top,
            toolbarMargin.Right,
            headerMargin.Bottom);
        if (headerMargin != margin)
            header.Margin = margin;
    }

    private static void AlignLibraryListRightEdge(Grid toolbar, ListBox list)
    {
        // List rows are full-width controls, while the toolbar is inset to the
        // last complete gallery slot. Reuse that live right inset so list rows
        // never extend beyond the horizontal boundary established above them.
        var listMargin = list.Margin;
        var margin = new Thickness(
            listMargin.Left,
            listMargin.Top,
            toolbar.Margin.Right,
            listMargin.Bottom);
        if (listMargin != margin)
            list.Margin = margin;
    }

    private void LibraryToolbar_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        // XAML initialization and language changes can resize either group.
        if (LibraryToolbar is null || LibraryToolbarActions is null || SearchBox is null)
            return;
        ArrangeShelfToolbar(LibraryToolbar, SearchBox, LibraryToolbarActions);
    }

    private static void ArrangeShelfToolbar(Grid toolbar, TextBox searchBox, StackPanel actions)
    {
        var width = toolbar.Bounds.Width;
        var actionsWidth = actions.DesiredSize.Width;
        if (width <= 0 || actionsWidth <= 0) return;

        const double minimumSearchWidth = 240;
        const double groupSpacing = 24;
        var stacked = width < minimumSearchWidth + groupSpacing + actionsWidth;
        Grid.SetColumnSpan(searchBox, stacked ? 2 : 1);
        Grid.SetRow(actions, stacked ? 1 : 0);
        Grid.SetColumn(actions, stacked ? 0 : 1);
        Grid.SetColumnSpan(actions, stacked ? 2 : 1);
        toolbar.ColumnSpacing = stacked ? 0 : groupSpacing;
        toolbar.RowSpacing = stacked ? 10 : 0;

        var searchWidth = stacked ? width : Math.Min(420, width - actionsWidth - groupSpacing);
        if (double.IsNaN(searchBox.Width) || Math.Abs(searchBox.Width - searchWidth) > 0.5)
            searchBox.Width = searchWidth;
    }

    private void LibraryDetailCloseButton_Click(object? sender, RoutedEventArgs e)
    {
        ClearSelectedBook();
        if (BookList.IsVisible)
            BookList.Focus();
        else
            BookGrid.Focus();
    }
}
