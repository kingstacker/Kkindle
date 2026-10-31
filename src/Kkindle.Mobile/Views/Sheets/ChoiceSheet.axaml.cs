using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Kkindle.Mobile.Views.Sheets;

public sealed record ChoiceItem(
    string Title,
    string? Detail = null,
    bool Selected = false,
    int Indent = 0,
    object? Tag = null)
{
    public Thickness IndentMargin => new(Math.Min(Indent, 3) * 16, 0, 0, 0);
}

/// <summary>Single-pick list used for shelves, sorting and the table of contents.</summary>
public partial class ChoiceSheet : UserControl, IFullHeightSheet
{
    private const int LongListThreshold = 12;

    private readonly Action<ChoiceItem>? _onPick;

    /// <summary>Design-time only.</summary>
    public ChoiceSheet() => InitializeComponent();

    public ChoiceSheet(IReadOnlyList<ChoiceItem> items, Action<ChoiceItem> onPick)
    {
        _onPick = onPick;
        InitializeComponent();
        IsLong = items.Count > LongListThreshold;
        if (!IsLong)
        {
            // A virtualizing panel only estimates its height, which clips short sheets.
            Choices.ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel());
            Choices.ItemsSource = items;
            return;
        }

        // Virtualization needs a viewport, which a sheet only has once it is laid
        // out; items bound earlier would all be realized and stall the UI thread.
        var selected = items.ToList().FindIndex(item => item.Selected);
        Loaded += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            Choices.ItemsSource = items;
            if (selected >= 0) Choices.ScrollIntoView(selected);
        }, DispatcherPriority.Background);
    }

    /// <summary>Long lists fill the sheet, giving them a known viewport up front.</summary>
    public bool IsLong { get; }

    public bool FillsHeight => IsLong;

    private void OnChoiceClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ChoiceItem item }) _onPick?.Invoke(item);
    }
}
