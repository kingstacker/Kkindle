using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Kkindle.Core;

namespace Kkindle;

public partial class MainWindow
{
    private ReadingDashboard? _readingMedalsDashboard;
    private bool _showUnearnedReadingMedals;

    private void DashboardUnlockedMedalsButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => SetReadingMedalsVisibility(false);

    private void DashboardAllMedalsButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => SetReadingMedalsVisibility(true);

    private void SetReadingMedalsVisibility(bool showAll)
    {
        if (_showUnearnedReadingMedals == showAll) return;
        _showUnearnedReadingMedals = showAll;
        if (_readingMedalsDashboard is { } dashboard) PopulateReadingMedals(dashboard);
        ScheduleAppSettingsAutoSave(showSavedStatus: false);
    }

    private void PopulateReadingMedals(ReadingDashboard dashboard)
    {
        _readingMedalsDashboard = dashboard;
        string L(string chinese, string english) => UiText.IsEnglish ? english : chinese;
        var medals = ReadingMedalCatalog.Build(dashboard);
        DashboardMedalsTitle.Text = L("阅读勋章", "Reading medals");
        DashboardMedalsSummary.Text = L("已解锁", "Unlocked");
        DashboardUnlockedMedalsButton.Content = medals.Count(m => m.Value >= m.Target).ToString();
        DashboardAllMedalsButton.Content = medals.Count.ToString();
        DashboardUnlockedMedalsButton.Classes.Set("active", !_showUnearnedReadingMedals);
        DashboardAllMedalsButton.Classes.Set("active", _showUnearnedReadingMedals);
        ToolTip.SetTip(DashboardUnlockedMedalsButton, L("只显示已解锁勋章", "Show unlocked medals only"));
        ToolTip.SetTip(DashboardAllMedalsButton, L("显示全部勋章", "Show all medals"));
        Avalonia.Automation.AutomationProperties.SetName(DashboardUnlockedMedalsButton,
            L($"显示 {DashboardUnlockedMedalsButton.Content} 枚已解锁勋章", $"Show {DashboardUnlockedMedalsButton.Content} unlocked medals"));
        Avalonia.Automation.AutomationProperties.SetName(DashboardAllMedalsButton,
            L($"显示全部 {medals.Count} 枚勋章", $"Show all {medals.Count} medals"));
        DashboardMedalsPanel.Children.Clear();
        var visibleMedals = medals
            .Where(m => m.Value >= m.Target || _showUnearnedReadingMedals)
            .OrderByDescending(m => m.Value >= m.Target)
            .ToArray();
        DashboardMedalsEmptyText.IsVisible = visibleMedals.Length == 0;
        foreach (var medal in visibleMedals)
        {
            var unlocked = medal.Value >= medal.Target;
            var fraction = Math.Clamp((double)medal.Value / medal.Target, 0, 1);
            var ink = unlocked ? "InkBrush" : "MutedInkBrush";
            Avalonia.Controls.Shapes.Path CreateIcon(IBrush stroke)
            {
                var geometry = Geometry.Parse(medal.Icon);
                var bounds = geometry.Bounds;
                var scale = 20 / Math.Max(1, Math.Max(bounds.Width, bounds.Height));
                // Path coordinates have different origins. Fit the artwork, rather than
                // its coordinate canvas, into a common slot centered at (11, 11).
                geometry.Transform = new MatrixTransform(new Matrix(scale, 0, 0, scale,
                    11 - bounds.Center.X * scale, 11 - bounds.Center.Y * scale));
                return new Avalonia.Controls.Shapes.Path
                {
                    Data = geometry, Width = 22, Height = 22,
                    Stretch = Stretch.None, StrokeThickness = 1.35, Stroke = stroke,
                    StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                };
            }
            // Draw a quiet base, then reveal the black medal clockwise from 12 o'clock.
            // Both the outline and glyph share the same pie clip, so the medal is the progress indicator.
            var emblem = new Grid
            {
                Width = 42, Height = 42,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left
            };
            emblem.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(21), Background = Brushes.White,
                BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(1),
                Child = CreateIcon(Brushes.LightGray)
            });
            if (fraction > 0)
            {
                var revealed = new Border
                {
                    CornerRadius = new CornerRadius(21), BorderBrush = Brushes.Black,
                    BorderThickness = new Thickness(1), Child = CreateIcon(Brushes.Black)
                };
                if (fraction < 1)
                {
                    var angle = fraction * Math.PI * 2;
                    // Radius exceeds the medal bounds so the clip includes the complete outer stroke.
                    var x = 21 + 30 * Math.Sin(angle);
                    var y = 21 - 30 * Math.Cos(angle);
                    revealed.Clip = Geometry.Parse(FormattableString.Invariant(
                        $"M21,21 L21,-9 A30,30 0 {(fraction > 0.5 ? 1 : 0)},1 {x},{y} Z"));
                }
                emblem.Children.Add(revealed);
            }
            TextBlock Label(string text, double size, string brush) {
                var label = new TextBlock { Text = text, FontSize = size, TextAlignment = TextAlignment.Center,
                    MaxWidth = 76, TextTrimming = TextTrimming.CharacterEllipsis,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
                label.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension(brush));
                return label;
            }
            var content = new StackPanel
            {
                Spacing = 6,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            content.Children.Add(emblem);
            content.Children.Add(Label(medal.Name, 11, ink));
            var percent = fraction * 100;
            var status = unlocked ? L("已解锁", "Unlocked") : L($"待解锁 · {percent:0}%", $"Locked · {percent:0}%");
            var details = new StackPanel { Spacing = 4 };
            details.Children.Add(new TextBlock { Text = medal.Rule, FontSize = 12 });
            details.Children.Add(new TextBlock { Text = status, FontSize = 11 });
            ToolTip.SetTip(emblem, details);
            ToolTip.SetShowDelay(emblem, 200);
            Avalonia.Automation.AutomationProperties.SetName(emblem, $"{medal.Name}，{medal.Rule}，{status}");
            var card = new Border
            {
                Width = 84, Height = 84, Padding = new Thickness(0),
                Margin = new Thickness(0, 4, 8, 4), Child = content
            };
            DashboardMedalsPanel.Children.Add(card);
        }
    }
}
