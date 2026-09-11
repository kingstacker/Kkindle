using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia;
using Avalonia.VisualTree;

namespace Kkindle;

public partial class MainWindow
{
    private void LibrarySort_PointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (IsSourceWithin(e.Source, LibrarySortBox)
            || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        LibrarySortBox.Focus();
        LibrarySortBox.IsDropDownOpen = true;
        e.Handled = true;
    }

    private void LibraryToolbar_LayoutUpdated(object? sender, EventArgs e)
    {
        if (LibraryToolbar is null || LibraryWorkspace is null || BookGrid is null)
            return;

        // The Fluent template reserves a fixed column even when its arrow is hidden.
        var sortGrid = LibrarySortBox.GetVisualDescendants().OfType<Grid>()
            .FirstOrDefault(grid => grid.Children.Any(child => child.Name == "DropDownGlyph"));
        if (sortGrid is { ColumnDefinitions.Count: 2 } && sortGrid.ColumnDefinitions[1].Width != new GridLength(0))
            sortGrid.ColumnDefinitions[1].Width = new GridLength(0);
        var sortBackground = LibrarySortBox.GetVisualDescendants().OfType<Border>()
            .FirstOrDefault(border => border.Name == "Background");
        if (sortBackground is not null && sortBackground.MinWidth != 0)
            sortBackground.MinWidth = 0;

        var left = 6d;
        var right = 6d;
        if (BookGrid.IsVisible && BookGrid.GetVisualDescendants().OfType<WrapPanel>().FirstOrDefault() is { } panel
            && panel.Bounds.Width > 0 && panel.ItemWidth > 0
            && panel.TranslatePoint(default, LibraryWorkspace) is { } origin)
        {
            var columns = Math.Max(1, Math.Floor(panel.Bounds.Width / panel.ItemWidth));
            left = Math.Max(0, origin.X + 6);
            right = Math.Max(6, LibraryWorkspace.Bounds.Width - origin.X - columns * panel.ItemWidth + 6);
        }

        // Keep the action row usable when a docked detail pane leaves only a narrow book column.
        right = Math.Min(right, Math.Max(6, LibraryWorkspace.Bounds.Width - left - LibraryToolbarActions.DesiredSize.Width));
        var margin = new Thickness(left, 22, right, 18);
        if (LibraryToolbar.Margin != margin)
            LibraryToolbar.Margin = margin;
    }

    private void LibraryToolbar_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        // XAML initialization and language changes can resize either group.
        if (LibraryToolbar is null || LibraryToolbarActions is null || SearchBox is null)
            return;
        var width = LibraryToolbar.Bounds.Width;
        var actionsWidth = LibraryToolbarActions.DesiredSize.Width;
        if (width <= 0 || actionsWidth <= 0) return;

        const double minimumSearchWidth = 240;
        const double groupSpacing = 24;
        var stacked = width < minimumSearchWidth + groupSpacing + actionsWidth;
        Grid.SetColumnSpan(SearchBox, stacked ? 2 : 1);
        Grid.SetRow(LibraryToolbarActions, stacked ? 1 : 0);
        Grid.SetColumn(LibraryToolbarActions, stacked ? 0 : 1);
        Grid.SetColumnSpan(LibraryToolbarActions, stacked ? 2 : 1);
        LibraryToolbar.ColumnSpacing = stacked ? 0 : groupSpacing;
        LibraryToolbar.RowSpacing = stacked ? 10 : 0;

        var searchWidth = stacked ? width : Math.Min(420, width - actionsWidth - groupSpacing);
        if (Math.Abs(SearchBox.Width - searchWidth) > 0.5)
            SearchBox.Width = searchWidth;
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
