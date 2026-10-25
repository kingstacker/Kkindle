using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;

namespace Kkindle;

public sealed partial class KreaderMarkdownTextBlock
{
    private void AttachCitationPreview(Button button, string sourceId)
    {
        Popup? popup = null;
        var timer = new DispatcherTimer();
        var opening = false;
        void Close()
        {
            timer.Stop();
            if (popup is null) return;
            popup.IsOpen = false;
            ((ISetLogicalParent)popup).SetParent(null);
            popup.Child = null;
            popup = null;
        }
        void Schedule(bool open)
        {
            timer.Stop();
            opening = open;
            timer.Interval = TimeSpan.FromMilliseconds(open ? 300 : 250);
            timer.Start();
        }
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!opening)
            {
                if (!button.IsPointerOver && popup?.Child?.IsPointerOver != true) Close();
                return;
            }
            if (popup is not null || CitationSourceResolver?.Invoke(sourceId) is not { } source) return;
            var heading = new TextBlock { FontSize = 12, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
            heading.Bind(TextProperty, new Binding(nameof(ReaderAiSourceViewModel.LocationLabel)) { Source = source });
            var original = new SelectableTextBlock
            {
                Text = source.Content, FontSize = 13, TextWrapping = TextWrapping.Wrap, LineSpacing = 3
            };
            var scroll = new ScrollViewer
            {
                Content = original, MaxHeight = 260,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            var content = new StackPanel { Spacing = 8 };
            content.Children.Add(heading);
            content.Children.Add(scroll);
            var card = new Border
            {
                Width = Math.Min(380, Math.Max(200, (TopLevel.GetTopLevel(button)?.Bounds.Width ?? 420) - 40)),
                Padding = new Thickness(12), BorderThickness = new Thickness(1), Child = content
            };
            card.Bind(Border.BackgroundProperty, new DynamicResourceExtension("ReaderPageBrush"));
            card.Bind(Border.BorderBrushProperty, new DynamicResourceExtension("ReaderBorderBrush"));
            original.Bind(ForegroundProperty, new DynamicResourceExtension("ReaderInkBrush"));
            heading.Bind(ForegroundProperty, new DynamicResourceExtension("ReaderMutedBrush"));
            card.PointerEntered += (_, _) => timer.Stop();
            card.PointerExited += (_, _) => Schedule(false);
            card.KeyDown += (_, e) => { if (e.Key == Key.Escape) { button.Focus(); Close(); e.Handled = true; } };
            popup = new Popup
            {
                Child = card, PlacementTarget = button, Placement = PlacementMode.BottomEdgeAlignedLeft,
                VerticalOffset = 4, ShouldUseOverlayLayer = true, IsLightDismissEnabled = false,
                TakesFocusFromNativeControl = false
            };
            ((ISetLogicalParent)popup).SetParent(button);
            popup.IsOpen = true;
        };
        button.PointerEntered += (_, _) => { if (popup is null) Schedule(true); else timer.Stop(); };
        button.PointerExited += (_, _) => Schedule(false);
        button.GotFocus += (_, _) => Schedule(true);
        button.LostFocus += (_, _) => Schedule(false);
        button.Click += (_, _) => Close();
        button.KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        button.DetachedFromVisualTree += (_, _) => Close();
        button.EffectiveViewportChanged += (_, e) =>
        {
            if (!e.EffectiveViewport.Intersects(new Rect(button.Bounds.Size))) Close();
        };
    }
}
