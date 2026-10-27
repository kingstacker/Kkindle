using Avalonia.Controls;
using Avalonia.Threading;
using Kkindle.Core;

namespace Kkindle;

public partial class MainWindow
{
    private ContextMenu? _readerAnnotationMenu;

    private void ShowReaderAnnotationMenu(ReaderAnnotation annotation)
    {
        HideReaderAnnotationMenu();
        if (CurrentReaderHost is not { View: Control target }) return;
        HideReaderSelectionPopup();
        var menu = new ContextMenu { Classes = { "shelfViewMenu" }, Placement = PlacementMode.Pointer };
        var edit = new MenuItem { Header = T("编辑批注"), Tag = "edit" };
        edit.Click += (_, _) =>
        {
            HideReaderAnnotationMenu();
            if (_readerIsPdf)
                EditReaderPdfAnnotation(annotation);
            else
            {
                EditReaderAnnotation(annotation);
                HideReaderSelectionPopup();
                ShowReaderAnnotationInputPopup();
            }
        };
        var delete = new MenuItem { Header = T("删除批注"), Tag = "delete" };
        delete.Click += async (_, _) =>
        {
            HideReaderAnnotationMenu();
            await DeleteReaderAnnotationAsync(annotation);
        };
        menu.Items.Add(edit);
        menu.Items.Add(delete);
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_readerAnnotationMenu, menu)) _readerAnnotationMenu = null;
        };
        _readerAnnotationMenu = menu;
        // Wait until the right-click release is handled so it cannot dismiss
        // the menu that this same pointer event just opened.
        Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(_readerAnnotationMenu, menu)) menu.Open(target);
        });
    }

    private void HideReaderAnnotationMenu()
    {
        if (_readerAnnotationMenu is not { } menu) return;
        _readerAnnotationMenu = null;
        menu.Close();
    }
}
