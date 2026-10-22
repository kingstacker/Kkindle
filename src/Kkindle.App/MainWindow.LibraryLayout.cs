using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using System.Runtime.CompilerServices;

namespace Kkindle;

public partial class MainWindow
{
    private Popup? _shelfSortPopup;

    private void ShelfSortButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        var sortBox = button.Tag as string == "DeviceBookSortBox" ? DeviceBookSortBox : LibrarySortBox;

        if (_shelfSortPopup is { IsOpen: true } openPopup
            && ReferenceEquals(openPopup.PlacementTarget, button))
        {
            CloseShelfSortPopup();
            e.Handled = true;
            return;
        }
        CloseShelfSortPopup();
        var items = new StackPanel();
        for (var index = 0; index < sortBox.Items.Count; index++)
        {
            var selectedIndex = index;
            var value = sortBox.Items[index];
            var item = new Button
            {
                Content = value is ComboBoxItem option ? option.Content : value,
                Classes = { "shelfSortItem" },
                FontSize = 14,
            };
            item.Click += (_, _) =>
            {
                sortBox.SelectedIndex = selectedIndex;
                CloseShelfSortPopup();
            };
            items.Children.Add(item);
        }

        // Keep shelf sorting in the window overlay. A native popup window can
        // be composited as a single clipped corner on some Windows setups.
        var popup = new Popup
        {
            Child = new Border
            {
                Background = Avalonia.Application.Current!.FindResource("PaperBrush") as Avalonia.Media.IBrush,
                BorderBrush = Avalonia.Application.Current!.FindResource("InkBrush") as Avalonia.Media.IBrush,
                BorderThickness = new Thickness(1),
                Child = items
            },
            PlacementTarget = button,
            Placement = PlacementMode.BottomEdgeAlignedLeft,
            ShouldUseOverlayLayer = true,
            IsLightDismissEnabled = true
        };
        popup.Closed += (_, _) =>
        {
            if (ReferenceEquals(_shelfSortPopup, popup))
                _shelfSortPopup = null;
            if (popup.Parent is Panel parent)
                parent.Children.Remove(popup);
        };
        _shelfSortPopup = popup;
        LibraryWorkspace.Children.Add(popup);
        popup.IsOpen = true;
        e.Handled = true;
    }

    private void CloseShelfSortPopup()
    {
        if (_shelfSortPopup is not { } popup) return;
        _shelfSortPopup = null;
        popup.IsOpen = false;
        if (popup.Parent is Panel parent)
            parent.Children.Remove(popup);
    }

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


        AlignShelfToolbar(LibraryToolbar, LibraryBody, LibraryContentHost, alignDetailBoundary: true);
        AlignCollectionHeader(LibraryToolbar, CollectionHeader);
        AlignLibraryListRightEdge(LibraryToolbar, BookList);
    }

    // LayoutUpdated fires on every layout pass (scrolling, animations, detail
    // pane motion). The sort-chrome children are template parts that never
    // change after the first lookup, so cache them instead of walking the
    // visual tree each time.
    private sealed class SortChrome
    {
        public Grid? Grid { get; init; }
        public Border? Background { get; init; }
    }

    private static readonly ConditionalWeakTable<ComboBox, SortChrome> SortChromeCache = new();

    private static void NormalizeLibrarySortChrome(ComboBox sortBox)
    {
        if (!SortChromeCache.TryGetValue(sortBox, out var chrome)
            || (TopLevel.GetTopLevel(chrome.Grid) is null && TopLevel.GetTopLevel(chrome.Background) is null))
        {
            chrome = new SortChrome
            {
                // The Fluent template reserves a fixed column even when its arrow is hidden.
                Grid = sortBox.GetVisualDescendants().OfType<Grid>()
                    .FirstOrDefault(grid => grid.Children.Any(child => child.Name == "DropDownGlyph")),
                Background = sortBox.GetVisualDescendants().OfType<Border>()
                    .FirstOrDefault(border => border.Name == "Background")
            };
            if (chrome.Grid is not null || chrome.Background is not null)
                SortChromeCache.AddOrUpdate(sortBox, chrome);
        }

        if (chrome.Grid is { ColumnDefinitions.Count: 2 } sortGrid
            && sortGrid.ColumnDefinitions[1].Width != new GridLength(0))
            sortGrid.ColumnDefinitions[1].Width = new GridLength(0);
        if (chrome.Background is { } sortBackground && sortBackground.MinWidth != 0)
            sortBackground.MinWidth = 0;
    }

    private static void AlignShelfToolbar(Grid toolbar, Control workspace, Control shelfViewport, bool alignDetailBoundary = false)
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
        // Book content keeps its own top inset; the detail pane starts directly
        // at the toolbar boundary, including when actions wrap to a second row.
        var margin = new Thickness(left, 0, right, alignDetailBoundary ? 0 : 18);
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
        if (LibraryToolbar is null || LibraryToolbarActions is null || LibrarySearchField is null)
            return;
        ArrangeShelfToolbar(LibraryToolbar, LibrarySearchField, LibraryToolbarActions);
    }

    private static void ArrangeShelfToolbar(Grid toolbar, Control searchBox, StackPanel actions)
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
        if (double.IsNaN(searchBox.Width) || Math.Abs(searchBox.Width - Math.Max(0, searchWidth - searchBox.Margin.Left - searchBox.Margin.Right)) > 0.5)
            searchBox.Width = Math.Max(0, searchWidth - searchBox.Margin.Left - searchBox.Margin.Right);
    }

    private void LibraryDetailCloseButton_Click(object? sender, RoutedEventArgs e)
    {
        BookGrid.SelectedItems?.Clear();
        BookList.SelectedItems?.Clear();
        ClearSelectedBook();
        if (BookList.IsVisible)
            BookList.Focus();
        else
            BookGrid.Focus();
    }
}
