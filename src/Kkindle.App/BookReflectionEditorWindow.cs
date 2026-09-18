using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Kkindle.Core;

namespace Kkindle;

using AvaloniaPath = Avalonia.Controls.Shapes.Path;

internal sealed record BookReflectionEditorResult(string Content);

/// <summary>
/// Full-size editor for a book-level reading reflection. The library detail
/// pane remains a compact preview; this window gives longer reflections a
/// comfortable, resizable writing surface without changing the reader pane.
/// </summary>
internal sealed class BookReflectionEditorWindow : Window
{
    private const string MaximizeGlyphData = "M 0.5,0.5 H 9.5 V 9.5 H 0.5 Z";
    private const string RestoreGlyphData = "M 2.5,0.5 H 9.5 V 7.5 M 0.5,2.5 H 7.5 V 9.5 H 0.5 Z";
    private readonly TextBlock _counter;
    private readonly BookReflectionEditorSurface _editorSurface;
    private readonly Button _maximizeButton;
    private readonly AvaloniaPath _maximizeGlyph;
    private readonly Grid _windowResizeLayer;
    private readonly TaskCompletionSource<BookReflectionEditorResult?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _completed;

    public BookReflectionEditorWindow(string bookTitle, string initialContent)
    {
        Title = UiText.Get("读后思考编辑");
        Width = 900;
        Height = 700;
        MinWidth = 620;
        MinHeight = 440;
        CanResize = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = AppAppearanceResources.GetBrush("PaperBrush");
        WindowDecorations = Avalonia.Controls.WindowDecorations.None;
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaTitleBarHeightHint = -1;
        UseLayoutRounding = true;

        var titleBar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Background = AppAppearanceResources.GetBrush("PaperBrush")
        };
        var titleDragRegion = new Border
        {
            Background = Brushes.Transparent,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Margin = new Thickness(16, 0),
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
                Children =
                {
                    new Border
                    {
                        Width = 18,
                        Height = 18,
                        Background = AppAppearanceResources.GetBrush("InkBrush"),
                        Child = new TextBlock
                        {
                            Text = "K",
                            Foreground = AppAppearanceResources.GetBrush("PaperBrush"),
                            FontSize = 13,
                            FontWeight = FontWeight.Bold,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center
                        }
                    },
                    new TextBlock
                    {
                        Text = "Kkindle",
                        FontSize = 12,
                        Foreground = AppAppearanceResources.GetBrush("MutedInkBrush"),
                        VerticalAlignment = VerticalAlignment.Center
                    },
                    new TextBlock
                    {
                        Text = "·",
                        FontSize = 12,
                        Foreground = AppAppearanceResources.GetBrush("MutedInkBrush"),
                        VerticalAlignment = VerticalAlignment.Center
                    },
                    new TextBlock
                    {
                        Text = UiText.Get("读后思考"),
                        FontSize = 12,
                        Foreground = AppAppearanceResources.GetBrush("InkBrush"),
                        VerticalAlignment = VerticalAlignment.Center
                    }
                }
            }
        };
        titleDragRegion.PointerPressed += TitleBarDragRegion_PointerPressed;
        titleBar.Children.Add(titleDragRegion);

        var captionButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 0
        };
        var minimizeButton = CreateCaptionButton(
            "M 0,5 H 10",
            UiText.Get("最小化"),
            MinimizeWindowButton_Click);
        _maximizeGlyph = new AvaloniaPath
        {
            Data = Geometry.Parse(MaximizeGlyphData),
            Width = 10,
            Height = 10
        };
        _maximizeButton = CreateCaptionButton(_maximizeGlyph, UiText.Get("最大化"), MaximizeWindowButton_Click);
        var closeButton = CreateCaptionButton(
            "M 0,0 L 10,10 M 10,0 L 0,10",
            UiText.Get("关闭"),
            CloseWindowButton_Click);
        captionButtons.Children.Add(minimizeButton);
        captionButtons.Children.Add(_maximizeButton);
        captionButtons.Children.Add(closeButton);
        Grid.SetColumn(captionButtons, 1);
        titleBar.Children.Add(captionButtons);

        var titleBarBorder = new Border
        {
            BorderBrush = AppAppearanceResources.GetBrush("HairlineBrush"),
            BorderThickness = new Thickness(0),
            Child = titleBar
        };

        var book = new TextBlock
        {
            Text = $"《{bookTitle}》",
            FontSize = 22,
            FontWeight = FontWeight.SemiBold,
            Foreground = AppAppearanceResources.GetBrush("InkBrush"),
            TextWrapping = TextWrapping.Wrap
        };
        _editorSurface = new BookReflectionEditorSurface(initialContent)
        {
            MinHeight = 280
        };
        _editorSurface.ContentChanged += EditorSurface_ContentChanged;
        _editorSurface.AddHandler(InputElement.KeyDownEvent, Editor_KeyDown, RoutingStrategies.Tunnel);

        _editorSurface.FormattingToolbar.HorizontalAlignment = HorizontalAlignment.Left;
        _editorSurface.FormattingToolbar.VerticalAlignment = VerticalAlignment.Center;

        _counter = new TextBlock
        {
            FontSize = 11,
            Foreground = AppAppearanceResources.GetBrush("MutedInkBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };
        UpdateCounter();

        var cancelButton = new Button
        {
            Content = UiText.Get("取消"),
            Padding = new Thickness(16, 8)
        };
        cancelButton.Classes.Add("quiet");
        cancelButton.Click += (_, _) => Complete(null);

        var saveButton = new Button
        {
            Content = UiText.Get("保存"),
            Padding = new Thickness(16, 8),
            MinWidth = 96
        };
        saveButton.Click += (_, _) =>
            Complete(new BookReflectionEditorResult(_editorSurface.Markdown.Trim()));

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { cancelButton, saveButton }
        };

        var footer = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 12,
            Children = { _counter, actions }
        };
        Grid.SetColumn(actions, 1);

        var footerBorder = new Border
        {
            BorderBrush = AppAppearanceResources.GetBrush("HairlineBrush"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(0, 10, 0, 0),
            Child = footer
        };

        var content = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"),
            RowSpacing = 10,
            Children = { book, _editorSurface.FormattingToolbar, _editorSurface, footerBorder }
        };
        Grid.SetRow(_editorSurface.FormattingToolbar, 1);
        Grid.SetRow(_editorSurface, 2);
        Grid.SetRow(footerBorder, 3);

        var editorBorder = new Border
        {
            Padding = new Thickness(28),
            Background = Background,
            Child = content
        };

        var frame = new Grid
        {
            RowDefinitions = new RowDefinitions("38,*"),
            Background = Background,
            Children = { titleBarBorder, editorBorder }
        };
        Grid.SetRow(editorBorder, 1);
        var frameBorder = new Border
        {
            BorderBrush = AppAppearanceResources.GetBrush("InkBrush"),
            BorderThickness = new Thickness(1),
            Background = Background,
            Child = frame
        };
        _windowResizeLayer = CreateResizeLayer();
        var frameBottomLine = new Border
        {
            Background = AppAppearanceResources.GetBrush("InkBrush"),
            Height = 1,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Bottom,
            IsHitTestVisible = false
        };
        Content = new Grid
        {
            Children = { frameBorder, _windowResizeLayer, frameBottomLine }
        };
        _windowResizeLayer.ZIndex = 10;
        frameBottomLine.ZIndex = 20;
        UpdateWindowChrome();

        Opened += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            _editorSurface.FocusEditor();
        });
        Closed += (_, _) =>
        {
            _editorSurface.Dispose();
            Complete(null);
        };
    }

    public Task<BookReflectionEditorResult?> ShowAsync(Window owner)
    {
        Show(owner);
        return _completion.Task;
    }

    private void EditorSurface_ContentChanged(object? sender, EventArgs e) => UpdateCounter();

    private void UpdateCounter()
    {
        _counter.Text = UiText.Get("{0} 字", _editorSurface.CharacterCount);
    }

    private void Editor_KeyDown(object? sender, KeyEventArgs e)
    {
        var command = e.KeyModifiers.HasFlag(KeyModifiers.Control)
            || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Complete(null);
        }
        else if (command && (e.Key == Key.Enter || e.Key == Key.S))
        {
            e.Handled = true;
            Complete(new BookReflectionEditorResult(_editorSurface.Markdown.Trim()));
        }
    }

    private void Complete(BookReflectionEditorResult? result)
    {
        if (_completed) return;
        _completed = true;
        _completion.TrySetResult(result);
        Close();
    }

    private static Button CreateCaptionButton(
        string geometry,
        string label,
        EventHandler<RoutedEventArgs> handler)
    {
        var path = new AvaloniaPath { Data = Geometry.Parse(geometry), Width = 10, Height = 10 };
        return CreateCaptionButton(path, label, handler);
    }

    private static Button CreateCaptionButton(
        Control content,
        string label,
        EventHandler<RoutedEventArgs> handler)
    {
        var button = new Button
        {
            Content = content
        };
        button.Classes.Add("caption");
        AutomationProperties.SetName(button, label);
        ToolTip.SetTip(button, label);
        button.Click += handler;
        return button;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty)
            UpdateWindowChrome();
    }

    private void TitleBarDragRegion_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.ClickCount == 2)
            ToggleMaximized();
        else
            BeginMoveDrag(e);
        e.Handled = true;
    }

    private void MinimizeWindowButton_Click(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void MaximizeWindowButton_Click(object? sender, RoutedEventArgs e) => ToggleMaximized();

    private void CloseWindowButton_Click(object? sender, RoutedEventArgs e) => Complete(null);

    private void ToggleMaximized() => WindowState = WindowState is WindowState.Maximized or WindowState.FullScreen
        ? WindowState.Normal
        : WindowState.Maximized;

    private void UpdateWindowChrome()
    {
        if (_maximizeGlyph is null || _maximizeButton is null || _windowResizeLayer is null) return;
        var maximized = WindowState is WindowState.Maximized or WindowState.FullScreen;
        _maximizeGlyph.Data = Geometry.Parse(maximized ? RestoreGlyphData : MaximizeGlyphData);
        var label = UiText.Get(maximized ? "还原" : "最大化");
        AutomationProperties.SetName(_maximizeButton, label);
        ToolTip.SetTip(_maximizeButton, label);
        _windowResizeLayer.IsVisible = !maximized;
    }

    private Grid CreateResizeLayer()
    {
        var layer = new Grid();
        AddResizeHandle(layer, "West", 6, double.NaN, HorizontalAlignment.Left, VerticalAlignment.Stretch, StandardCursorType.SizeWestEast);
        AddResizeHandle(layer, "East", 6, double.NaN, HorizontalAlignment.Right, VerticalAlignment.Stretch, StandardCursorType.SizeWestEast);
        AddResizeHandle(layer, "North", double.NaN, 6, HorizontalAlignment.Stretch, VerticalAlignment.Top, StandardCursorType.SizeNorthSouth);
        AddResizeHandle(layer, "South", double.NaN, 6, HorizontalAlignment.Stretch, VerticalAlignment.Bottom, StandardCursorType.SizeNorthSouth);
        AddResizeHandle(layer, "NorthWest", 12, 12, HorizontalAlignment.Left, VerticalAlignment.Top, StandardCursorType.TopLeftCorner);
        AddResizeHandle(layer, "NorthEast", 12, 12, HorizontalAlignment.Right, VerticalAlignment.Top, StandardCursorType.TopRightCorner);
        AddResizeHandle(layer, "SouthWest", 12, 12, HorizontalAlignment.Left, VerticalAlignment.Bottom, StandardCursorType.BottomLeftCorner);
        AddResizeHandle(layer, "SouthEast", 12, 12, HorizontalAlignment.Right, VerticalAlignment.Bottom, StandardCursorType.BottomRightCorner);
        return layer;
    }

    private void AddResizeHandle(
        Grid layer,
        string edge,
        double width,
        double height,
        HorizontalAlignment horizontalAlignment,
        VerticalAlignment verticalAlignment,
        StandardCursorType cursor)
    {
        var handle = new Rectangle
        {
            Width = width,
            Height = height,
            Fill = Brushes.Transparent,
            HorizontalAlignment = horizontalAlignment,
            VerticalAlignment = verticalAlignment,
            Cursor = new Cursor(cursor),
            Tag = edge
        };
        handle.PointerPressed += WindowResize_PointerPressed;
        layer.Children.Add(handle);
    }

    private void WindowResize_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (WindowState != WindowState.Normal
            || !CanResize
            || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            || sender is not Control { Tag: string edge }
            || !Enum.TryParse<WindowEdge>(edge, out var windowEdge))
            return;

        BeginResizeDrag(windowEdge, e);
        e.Handled = true;
    }
}
