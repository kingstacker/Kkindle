using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;

namespace Kkindle;

public partial class MainWindow
{
    private void InitializeReaderAiTurnNavigation()
    {
        ReaderAiMessages.CollectionChanged += (_, _) => RefreshReaderAiTurnNavigation();
        ReaderAiScrollViewer.AttachedToVisualTree += (_, _) => UpdateReaderAiTurnNavigationSelection();
    }

    private void RefreshReaderAiTurnNavigation()
    {
        foreach (var old in ReaderAiTurnMarkers.Children)
            ToolTip.SetIsOpen(old, false);
        ReaderAiTurnMarkers.Children.Clear();
        foreach (var question in ReaderAiMessages.Where(message => message.IsUser))
        {
            var marker = new Button
            {
                Classes = { "readerAiTurnMarker" }, Tag = question,
                Content = new Border { Classes = { "readerAiTurnLine" }, IsHitTestVisible = false }
            };
            AutomationProperties.SetName(marker, ReaderAiTurnPreviewText(question.Content, 100));
            marker.Click += ReaderAiTurnMarker_Click;
            ToolTip.SetPlacement(marker, PlacementMode.Right);
            ToolTip.SetShowDelay(marker, 150);
            // Build previews only when visited, not on every streamed token.
            marker.PointerEntered += (_, _) => ToolTip.SetTip(marker, CreateReaderAiTurnPreview(question));
            ReaderAiTurnMarkers.Children.Add(marker);
        }
        ReaderAiTurnNavigation.IsVisible = ReaderAiTurnMarkers.Children.Count > 0;
        Dispatcher.UIThread.Post(UpdateReaderAiTurnNavigationSelection, DispatcherPriority.Loaded);
    }

    private Control CreateReaderAiTurnPreview(ReaderAiMessageViewModel question)
    {
        var index = ReaderAiMessages.IndexOf(question);
        var answer = index < 0 ? null : ReaderAiMessages.Skip(index + 1)
            .TakeWhile(message => !message.IsUser).FirstOrDefault();
        var preview = new StackPanel { Spacing = 6, MaxWidth = 280 };
        var title = new TextBlock
        {
            Text = ReaderAiTurnPreviewText(question.Content, 120), FontSize = 12,
            FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap,
            MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis
        };
        title.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("ReaderInkBrush"));
        var response = new TextBlock
        {
            FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxLines = 4,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        response.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("ReaderMutedBrush"));
        if (answer is not null)
            response.Bind(TextBlock.TextProperty, new Binding(nameof(ReaderAiMessageViewModel.Content))
            {
                Source = answer, Converter = ReaderAiTurnPreviewConverter.Instance
            });
        preview.Children.Add(title);
        preview.Children.Add(response);
        return preview;
    }

    private void ReaderAiTurnMarker_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ReaderAiMessageViewModel question } marker
            || ReaderChatMessagesPanel.ContainerFromItem(question) is not { } container
            || container.TranslatePoint(default, ReaderAiScrollViewer) is not { } position) return;
        ToolTip.SetIsOpen(marker, false);
        // Jump to the question, keeping it below the viewport's top padding.
        _readerAiFollowOutput = false;
        var target = ReaderAiScrollViewer.Offset.Y + position.Y - ReaderAiScrollViewer.Padding.Top;
        ReaderAiScrollViewer.Offset = new Vector(ReaderAiScrollViewer.Offset.X,
            Math.Clamp(target, 0, Math.Max(0, ReaderAiScrollViewer.Extent.Height - ReaderAiScrollViewer.Viewport.Height)));
        UpdateReaderAiTurnNavigationSelection();
    }

    private void UpdateReaderAiTurnNavigationSelection()
    {
        Button? active = null;
        foreach (var marker in ReaderAiTurnMarkers.Children.OfType<Button>())
        {
            if (marker.Tag is not ReaderAiMessageViewModel question
                || ReaderChatMessagesPanel.ContainerFromItem(question) is not { } container
                || container.TranslatePoint(default, ReaderAiScrollViewer) is not { } position) continue;
            active ??= marker;
            if (position.Y <= ReaderAiScrollViewer.Padding.Top + 4) active = marker;
        }
        // The final turn may be shorter than the viewport and cannot reach its top.
        if (ReaderAiScrollViewer.Offset.Y > 0 && ReaderAiScrollViewer.Extent.Height
            - ReaderAiScrollViewer.Viewport.Height - ReaderAiScrollViewer.Offset.Y < 2)
            active = ReaderAiTurnMarkers.Children.OfType<Button>().LastOrDefault();
        var revealActive = active is not null && !active.Classes.Contains("active");
        foreach (var marker in ReaderAiTurnMarkers.Children.OfType<Button>())
            marker.Classes.Set("active", ReferenceEquals(marker, active));
        if (revealActive && active!.IsEffectivelyVisible) active.BringIntoView();
    }

    private static string ReaderAiTurnPreviewText(string text, int limit)
    {
        // Preview text stays compact while the full message retains rich Markdown.
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text.Length <= limit ? text : text[..limit] + "…";
    }

    private sealed class ReaderAiTurnPreviewConverter : Avalonia.Data.Converters.IValueConverter
    {
        public static readonly ReaderAiTurnPreviewConverter Instance = new();
        public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            ReaderAiTurnPreviewText(value as string ?? string.Empty, 280);
        public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            AvaloniaProperty.UnsetValue;
    }
}
