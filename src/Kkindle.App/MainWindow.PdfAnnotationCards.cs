using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Kkindle.Core;

namespace Kkindle;

public partial class MainWindow
{
    private readonly List<Popup> _readerPdfAnnotationPopups = [];
    private ContextMenu? _readerPdfAnnotationMenu;

    // Right-click menu for a selected PDF annotation.
    private void ShowReaderPdfAnnotationMenu(ReaderAnnotation annotation)
    {
        HideReaderPdfAnnotationMenu();
        if (CurrentReaderHost is not NativePdfReaderHost pdf) return;
        var menu = new ContextMenu { Classes = { "shelfViewMenu" }, Placement = PlacementMode.Pointer };
        var edit = new MenuItem { Header = T("编辑批注"), Tag = "edit" };
        edit.Click += (_, _) =>
        {
            HideReaderPdfAnnotationMenu();
            EditReaderPdfAnnotation(annotation);
        };
        var delete = new MenuItem { Header = T("删除批注"), Tag = "delete" };
        delete.Click += async (_, _) =>
        {
            HideReaderPdfAnnotationMenu();
            await DeleteReaderAnnotationAsync(annotation);
        };
        menu.Items.Add(edit);
        menu.Items.Add(delete);

        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_readerPdfAnnotationMenu, menu)) _readerPdfAnnotationMenu = null;
        };
        _readerPdfAnnotationMenu = menu;
        var target = pdf.View is Control pdfView
            ? pdfView
            : (Control)ReaderWebViewHost;
        // Open after the current pointer event finishes: opening inline lets the
        // same right-click's release reach the new menu and light-dismiss it.
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(_readerPdfAnnotationMenu, menu)) menu.Open(target);
        });
    }

    private void HideReaderPdfAnnotationMenu()
    {
        if (_readerPdfAnnotationMenu is not { } menu) return;
        _readerPdfAnnotationMenu = null;
        menu.Close();
    }

    private void ShowAdditionalReaderPdfAnnotations(NativePdfReaderHost pdf, IEnumerable<ReaderAnnotation> annotations)
    {
        var callouts = new List<(ReaderAnnotation Annotation, Action<Rect> Moved)>();
        foreach (var annotation in annotations)
        {
            var text = new TextBlock { Text = annotation.Note, FontSize = 14, TextWrapping = TextWrapping.Wrap };
            text.Foreground = (IBrush)Resources["ReaderInkBrush"]!;
            var close = new Button { Content = "×", Width = 24, Height = 24, Padding = new Thickness(0),
                FontSize = 18, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                Foreground = (IBrush)Resources["ReaderMutedBrush"]!, HorizontalAlignment = HorizontalAlignment.Right };
            var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            heading.Children.Add(new TextBlock { Text = "●  " + T("批注"), FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center, Foreground = (IBrush)Resources["ReaderMutedBrush"]! });
            Grid.SetColumn(close, 1);
            heading.Children.Add(close);
            var content = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), RowSpacing = 6 };
            content.Children.Add(heading);
            var scroll = new ScrollViewer { Content = text, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Grid.SetRow(scroll, 1);
            content.Children.Add(scroll);
            var card = new Border { Width = 220, Height = 88, Padding = new Thickness(8),
                CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1.25), Child = content,
                Background = (IBrush)Resources["ReaderPageBrush"]!, BorderBrush = (IBrush)Resources["ReaderAccentBrush"]!,
                IsVisible = false };
            var popup = new Popup { Child = card, PlacementTarget = ReaderWebViewHost,
                Placement = PlacementMode.AnchorAndGravity, PlacementAnchor = PopupAnchor.TopLeft,
                PlacementGravity = PopupGravity.BottomRight, PlacementConstraintAdjustment = PopupPositionerConstraintAdjustment.None,
                ShouldUseOverlayLayer = false, TakesFocusFromNativeControl = false, IsLightDismissEnabled = false };
            // Give dynamically created native popups the same logical owner as the existing popup.
            ((ISetLogicalParent)popup).SetParent(ReaderWebViewHost);
            close.Click += (_, e) =>
            {
                e.Handled = true;
                popup.IsOpen = false;
                pdf.DismissAnnotationCallout(annotation.Id);
            };
            scroll.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(scroll).Properties.IsLeftButtonPressed) return;
                e.Handled = true;
                EditReaderPdfAnnotation(annotation);
                pdf.SelectAnnotation(annotation.Id);
            };
            card.PointerPressed += (_, e) =>
            {
                var properties = e.GetCurrentPoint(card).Properties;
                if (properties.IsRightButtonPressed)
                {
                    e.Handled = true;
                    ShowReaderPdfAnnotationMenu(annotation);
                }
                else if (properties.IsLeftButtonPressed)
                {
                    e.Handled = true;
                    pdf.SelectAnnotation(annotation.Id);
                }
            };
            _readerPdfAnnotationPopups.Add(popup);
            callouts.Add((annotation, bounds =>
            {
                card.IsVisible = bounds.Width > 0;
                if (bounds.Width <= 0) return;
                card.Height = bounds.Height;
                card.Width = bounds.Width;
                if (pdf.TranslatePoint(bounds.TopLeft, ReaderWebViewHost) is { } origin)
                    popup.PlacementRect = new Rect(origin, new Size(1, 1));
            }));
            popup.IsOpen = true;
        }
        pdf.SetAdditionalAnnotationCallouts(callouts);
    }
}
