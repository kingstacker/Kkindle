using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Kkindle.Core;

namespace Kkindle;

public sealed class ReaderAiFollowUpEventArgs(string text) : RoutedEventArgs(KreaderSelectableTextBlock.FollowUpRequestedEvent)
{
    public string Text { get; } = text;
}

/// <summary>Selectable rich text with mouse copy and an optional conversation follow-up.</summary>
public class KreaderSelectableTextBlock : SelectableTextBlock
{
    // Inline controls occupy one native selection character. Preserve their
    // readable content when copying across citations, formulas and rich blocks.
    public static readonly AttachedProperty<string?> InlineCopyTextProperty =
        AvaloniaProperty.RegisterAttached<KreaderSelectableTextBlock, Control, string?>("InlineCopyText");
    public static readonly StyledProperty<bool> IsFollowUpEnabledProperty =
        AvaloniaProperty.Register<KreaderSelectableTextBlock, bool>(nameof(IsFollowUpEnabled), inherits: true);
    public static readonly RoutedEvent<ReaderAiFollowUpEventArgs> FollowUpRequestedEvent =
        RoutedEvent.Register<KreaderSelectableTextBlock, ReaderAiFollowUpEventArgs>(nameof(FollowUpRequested), RoutingStrategies.Bubble);

    private Popup? _selectionPopup;
    private bool _preservingSelection;

    public KreaderSelectableTextBlock()
    {
        SelectionBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#665B9BD5"));
        var copy = new MenuItem { Header = UiText.Get("复制") };
        var followUp = new MenuItem { Header = UiText.Get("追问") };
        var selectAll = new MenuItem { Header = UiText.Get("全选") };
        var menuSelection = string.Empty;
        ContextMenu = new ContextMenu { ItemsSource = new Control[] { copy, followUp, new Separator(), selectAll } };
        void PrepareMenu()
        {
            CloseSelectionPopup();
            menuSelection = GetSelectionText();
            copy.IsEnabled = !string.IsNullOrWhiteSpace(menuSelection);
            followUp.IsVisible = IsFollowUpEnabled;
            followUp.IsEnabled = copy.IsEnabled;
        }
        ContextMenu.Opening += (_, _) => PrepareMenu();
        ContextMenu.Opened += (_, _) => PrepareMenu();
        copy.Click += async (_, _) => await CopySelectionAsync(menuSelection);
        followUp.Click += (_, _) => RequestFollowUp(menuSelection);
        selectAll.Click += (_, _) => { Focus(); SelectAll(); };
        CopyingToClipboard += async (_, e) =>
        {
            if (!ReferenceEquals(e.Source, this)) return;
            e.Handled = true;
            await CopySelectionAsync(GetSelectionText());
        };
        AddHandler(PointerReleasedEvent, SelectionPointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        DetachedFromVisualTree += (_, _) => CloseSelectionPopup();
        EffectiveViewportChanged += (_, e) =>
        {
            if (!e.EffectiveViewport.Intersects(new Rect(Bounds.Size))) CloseSelectionPopup();
        };
    }

    public bool IsFollowUpEnabled
    {
        get => GetValue(IsFollowUpEnabledProperty);
        set => SetValue(IsFollowUpEnabledProperty, value);
    }

    public event EventHandler<ReaderAiFollowUpEventArgs> FollowUpRequested
    {
        add => AddHandler(FollowUpRequestedEvent, value);
        remove => RemoveHandler(FollowUpRequestedEvent, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (!_preservingSelection && (change.Property == SelectionStartProperty || change.Property == SelectionEndProperty
            || change.Property == TextProperty || change.Property == IsFollowUpEnabledProperty))
            CloseSelectionPopup();
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        if (_selectionPopup is not { IsOpen: true })
        {
            base.OnLostFocus(e);
            return;
        }
        // A light-dismiss popup can temporarily move focus while opening.
        // Keep the native selection available to Copy and the follow-up action.
        var start = SelectionStart;
        var end = SelectionEnd;
        _preservingSelection = true;
        try
        {
            base.OnLostFocus(e);
            SetCurrentValue(SelectionStartProperty, start);
            SetCurrentValue(SelectionEndProperty, end);
        }
        finally { _preservingSelection = false; }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _selectionPopup is not null)
        {
            CloseSelectionPopup();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    private void SelectionPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        // Embedded citation/link buttons and nested selectable blocks own their gestures.
        if (!ReferenceEquals(e.Source, this) || e.InitialPressMouseButton != MouseButton.Left
            || !IsFollowUpEnabled || string.IsNullOrWhiteSpace(SelectedText)) return;
        ShowSelectionPopup(e.GetPosition(this), GetSelectionText());
    }

    private string GetSelectionText() => Inlines is { Count: > 0 } inlines
        ? GetCopyText(inlines, Math.Min(SelectionStart, SelectionEnd), Math.Abs(SelectionEnd - SelectionStart))
        : SelectedText;

    internal static string GetCopyText(InlineCollection inlines, int start = 0, int length = int.MaxValue)
    {
        var result = new System.Text.StringBuilder();
        var position = 0;
        var end = (long)start + length;
        void Append(InlineCollection collection)
        {
            foreach (var inline in collection)
            {
                if (inline is Span span) { Append(span.Inlines); continue; }
                var native = inline switch
                {
                    Run run => run.Text ?? string.Empty,
                    LineBreak => Environment.NewLine,
                    InlineUIContainer => "\uFFFC",
                    _ => string.Empty
                };
                var from = Math.Max(0, start - position);
                var to = (int)Math.Min(native.Length, end - position);
                if (to > from)
                {
                    if (inline is InlineUIContainer { Child: { } child })
                        result.Append(child.GetValue(InlineCopyTextProperty) ?? string.Empty);
                    else result.Append(native.AsSpan(from, to - from));
                }
                position += native.Length;
            }
        }
        Append(inlines);
        return result.ToString();
    }

    private void ShowSelectionPopup(Point point, string selection)
    {
        CloseSelectionPopup();
        var copy = new Button { Content = UiText.Get("复制"), Focusable = false };
        var followUp = new Button { Content = UiText.Get("追问"), Focusable = false };
        copy.Classes.Add("readerAiReasoningToggle");
        followUp.Classes.Add("readerAiReasoningToggle");
        copy.Classes.Add("readerAiSelectionAction");
        followUp.Classes.Add("readerAiSelectionAction");
        copy.Click += async (_, _) => { await CopySelectionAsync(selection); CloseSelectionPopup(); };
        followUp.Click += (_, _) => { CloseSelectionPopup(); RequestFollowUp(selection); };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        actions.Children.Add(copy);
        actions.Children.Add(followUp);
        var bar = new Border { Padding = new Thickness(4), BorderThickness = new Thickness(1), Child = actions };
        bar.Bind(Border.BackgroundProperty, new DynamicResourceExtension("ReaderPageBrush"));
        bar.Bind(Border.BorderBrushProperty, new DynamicResourceExtension("ReaderBorderBrush"));
        copy.Bind(ForegroundProperty, new DynamicResourceExtension("ReaderInkBrush"));
        followUp.Bind(ForegroundProperty, new DynamicResourceExtension("ReaderInkBrush"));
        _selectionPopup = new Popup
        {
            Child = bar, PlacementTarget = this, Placement = PlacementMode.TopEdgeAlignedLeft,
            PlacementRect = new Rect(point, new Size(1, 1)), VerticalOffset = -8,
            ShouldUseOverlayLayer = true, IsLightDismissEnabled = true,
            OverlayDismissEventPassThrough = true, TakesFocusFromNativeControl = false
        };
        var popup = _selectionPopup;
        popup.Closed += (_, _) =>
        {
            if (ReferenceEquals(_selectionPopup, popup)) CloseSelectionPopup();
        };
        ((ISetLogicalParent)_selectionPopup).SetParent(this);
        _selectionPopup.IsOpen = true;
        Focus();
    }

    protected void CloseSelectionPopup()
    {
        if (_selectionPopup is not { } popup) return;
        _selectionPopup = null;
        popup.IsOpen = false;
        ((ISetLogicalParent)popup).SetParent(null);
        popup.Child = null;
    }

    private async Task CopySelectionAsync(string selection)
    {
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                await clipboard.SetTextAsync(selection);
        }
        catch (Exception exception) { System.Diagnostics.Trace.TraceWarning($"Copy selected conversation text: {exception.Message}"); }
    }

    private void RequestFollowUp(string selection)
    {
        if (IsFollowUpEnabled && !string.IsNullOrWhiteSpace(selection))
            RaiseEvent(new ReaderAiFollowUpEventArgs(selection));
    }
}
