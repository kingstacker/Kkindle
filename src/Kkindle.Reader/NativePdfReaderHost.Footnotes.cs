using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using Kkindle.Infrastructure;

namespace Kkindle;

public sealed partial class NativePdfReaderHost
{
    private Popup? _footnotePopup;
    private DispatcherTimer? _footnoteTimer;
    private (int Page, PdfFootnoteReference Note, Rect Anchor)? _footnoteTarget;
    private bool _openingFootnote;

    private void UpdateFootnoteHover(Point point, bool enabled)
    {
        var page = enabled ? PageAtPoint(point) : 0;
        if (page > 0 && _pages.TryGetValue(page, out var cached))
        {
            foreach (var note in cached.Footnotes)
            {
                var rectangles = GetRangeBounds(page, note.Offset, note.Offset + note.Marker.Length);
                if (!rectangles.Any(rect => rect.Inflate(3).Contains(point))) continue;
                if (_footnoteTarget is { } target && target.Page == page && target.Note.Offset == note.Offset)
                {
                    if (_footnotePopup is not null) _footnoteTimer?.Stop();
                    else if (!_openingFootnote) ScheduleFootnotePreview(true);
                    return;
                }
                CloseFootnotePreview();
                _footnoteTarget = (page, note, rectangles.Aggregate((a, b) => a.Union(b)));
                ScheduleFootnotePreview(true);
                return;
            }
        }
        if (_footnoteTarget is not null && (_openingFootnote || _footnoteTimer?.IsEnabled != true))
            ScheduleFootnotePreview(false);
    }

    private void ScheduleFootnotePreview(bool open)
    {
        if (_footnoteTarget is null) return;
        _footnoteTimer ??= CreateFootnoteTimer();
        _footnoteTimer.Stop();
        _openingFootnote = open;
        _footnoteTimer.Interval = TimeSpan.FromMilliseconds(open ? 320 : 250);
        _footnoteTimer.Start();
    }

    private DispatcherTimer CreateFootnoteTimer()
    {
        var timer = new DispatcherTimer();
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_openingFootnote) OpenFootnotePreview();
            else if (_footnotePopup?.Child?.IsPointerOver != true) CloseFootnotePreview();
        };
        return timer;
    }

    private void OpenFootnotePreview()
    {
        if (_footnotePopup is not null || _footnoteTarget is not { } target || !_documentReady) return;
        var heading = new TextBlock
        {
            Text = $"脚注 {target.Note.Marker} · 第 {target.Page} 页", FontSize = 12,
            FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap
        };
        var original = new SelectableTextBlock
        {
            Text = target.Note.Text, FontSize = 13, TextWrapping = TextWrapping.Wrap, LineSpacing = 3
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
            Width = Math.Min(420, Math.Max(180, (TopLevel.GetTopLevel(this)?.Bounds.Width ?? 460) - 40)),
            Padding = new Thickness(12), BorderThickness = new Thickness(1), Child = content
        };
        card.Bind(Border.BackgroundProperty, new DynamicResourceExtension("ReaderPageBrush"));
        card.Bind(Border.BorderBrushProperty, new DynamicResourceExtension("ReaderBorderBrush"));
        original.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("ReaderInkBrush"));
        heading.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("ReaderMutedBrush"));
        card.PointerEntered += (_, _) => _footnoteTimer?.Stop();
        card.PointerExited += (_, _) => ScheduleFootnotePreview(false);
        card.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            Focus();
            CloseFootnotePreview();
            e.Handled = true;
        };
        _footnotePopup = new Popup
        {
            Child = card, PlacementTarget = this, PlacementRect = target.Anchor,
            Placement = PlacementMode.BottomEdgeAlignedLeft, VerticalOffset = 4,
            ShouldUseOverlayLayer = true, IsLightDismissEnabled = false, TakesFocusFromNativeControl = false
        };
        ((ISetLogicalParent)_footnotePopup).SetParent(this);
        _footnotePopup.IsOpen = true;
    }

    private void CloseFootnotePreview()
    {
        _footnoteTimer?.Stop();
        _footnoteTarget = null;
        _openingFootnote = false;
        if (_footnotePopup is null) return;
        _footnotePopup.IsOpen = false;
        ((ISetLogicalParent)_footnotePopup).SetParent(null);
        _footnotePopup.Child = null;
        _footnotePopup = null;
    }
}
