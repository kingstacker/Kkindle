using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Kkindle.Core;

namespace Kkindle;

using AvaloniaPath = Avalonia.Controls.Shapes.Path;
using AvaloniaRectangle = Avalonia.Controls.Shapes.Rectangle;

internal sealed class BookReflectionEditorSurface : Border
{
    private const double DefaultImageMaxWidth = 900;
    private const double DefaultImageMaxHeight = 620;
    private const double MinimumImageWidth = 48;
    private const double ImageZoomFactor = 1.2;

    // Toolbar geometry. Buttons and icons share one square content box so the
    // text labels and the line icons line up on the same optical centre.
    private const double ToolbarButtonWidth = 34;
    private const double ToolbarButtonHeight = 30;
    private const double ToolbarIconSize = 18;
    private const double ToolbarIconCanvas = 24;
    private const double ToolbarIconStroke = 2;
    private const double ToolbarLabelFontSize = 12.5;
    private const double ToolbarBoldFontSize = 13;

    // Latin toolbar labels use the bundled Inter with a system sans fallback.
    private static readonly FontFamily ToolbarLabelFontFamily =
        new("Inter, Segoe UI, Helvetica Neue, Arial, sans-serif");

    private static readonly Regex HeadingPattern = new(
        @"^\s*(?<marks>#{1,6})(?:\s+(?<text>.*))?$",
        RegexOptions.Compiled);
    private static readonly Regex UnorderedPattern = new(
        @"^\s*[-*+](?:\s+(?<text>.*))?$",
        RegexOptions.Compiled);
    private static readonly Regex OrderedPattern = new(
        @"^\s*(?<number>\d+)[.)](?:\s+(?<text>.*))?$",
        RegexOptions.Compiled);
    private static readonly Regex ImagePattern = new(
        @"^\s*!\[(?<alt>[^\]]*)\]\((?<source>[^)\s\r\n]+)(?:\s*=\s*(?<width>\d+)(?:x(?<height>\d+))?)?\)\s*$",
        RegexOptions.Compiled);
    private static readonly Regex InlinePattern = new(
        @"(\*\*(?<bold>[^*\r\n]+)\*\*|\[(?<label>[^\]\r\n]+)\]\((?<url>[^)\r\n]+)\))",
        RegexOptions.Compiled);

    private readonly StackPanel _blockStack;
    public Control FormattingToolbar { get; }
    private readonly List<BlockView> _blocks = [];
    private BlockView? _activeBlock;
    private BlockView? _selectedImage;
    private ImageResizeSession? _imageResizeSession;
    private SelectionSnapshot? _selection;
    private bool _suppressTextChanged;
    private bool _disposed;

    public event EventHandler? ContentChanged;

    public string Markdown => string.Join("\n", _blocks.Select(block => block.State.ToMarkdown()));

    public int CharacterCount => _blocks
        .Where(block => block.State.Kind != BlockKind.Image)
        .Sum(block => block.State.PlainText.Length);

    public BookReflectionEditorSurface(string initialContent)
    {
        Background = AppAppearanceResources.GetBrush("PaperBrush");
        BorderBrush = AppAppearanceResources.GetBrush("HairlineBrush");
        BorderThickness = new Thickness(1, 1, 1, 0);
        ClipToBounds = true;

        _blockStack = new StackPanel
        {
            Spacing = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var scrollViewer = new ScrollViewer
        {
            Background = Brushes.Transparent,
            Content = _blockStack,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Top
        };
        ScrollViewer.SetVerticalScrollBarVisibility(scrollViewer, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(scrollViewer, ScrollBarVisibility.Disabled);

        FormattingToolbar = CreateFormattingToolbar();
        Child = scrollViewer;

        SizeChanged += (_, _) => UpdateImageConstraints();

        Load(initialContent ?? string.Empty);
    }

    public void FocusEditor()
    {
        var block = _activeBlock ?? _blocks.LastOrDefault(view => view.Editor is not null);
        FocusBlock(block, null);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _imageResizeSession = null;
        _selectedImage = null;
        foreach (var block in _blocks)
            block.Dispose();
    }

    private void Load(string markdown)
    {
        var normalized = NormalizeMarkdown(markdown)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var lines = normalized.Split('\n');

        foreach (var line in lines)
            AddBlock(ParseBlock(line), _blocks.Count);

        if (_blocks.Count == 0)
            AddBlock(new BlockState(), 0);
    }

    private Control CreateFormattingToolbar()
    {
        var tools = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center
        };

        AddToolbarAction(
            tools,
            CreateToolbarIcon("M4 5V19M11 5V19M4 12H11M15.5 8L18 5.5V19"),
            "一级标题",
            "heading1");
        AddToolbarAction(
            tools,
            CreateToolbarIcon("M4 5V19M11 5V19M4 12H11M14.5 8.5A3 3 0 0 1 20.5 8.5C20.5 11.5 14.5 13 14.5 19H20.5"),
            "二级标题",
            "heading2");
        AddToolbarAction(
            tools,
            CreateToolbarIcon("M4 5V19M11 5V19M4 12H11M14.5 8.5C17 5.5 21 6.5 21 9.5C21 11.7 18.5 12 16.5 12C18.5 12 21 12.3 21 14.5C21 17.5 17 18.5 14.5 15.5"),
            "三级标题",
            "heading3");
        AddToolbarSeparator(tools);
        AddToolbarAction(tools, "B", "粗体", "bold");
        AddToolbarAction(
            tools,
            CreateToolbarIcon("M5 8H9V12H6C6 14 7 15 9 16M15 8H19V12H16C16 14 17 15 19 16"),
            "引用",
            "quote");
        AddToolbarAction(
            tools,
            CreateToolbarIcon("M4 5H7V8H4ZM4 11H7V14H4ZM4 17H7V20H4ZM10 6H20M10 12H20M10 18H20"),
            "无序列表",
            "unordered");
        AddToolbarAction(
            tools,
            CreateToolbarIcon("M5.5 5.5L7 4.5V7.5H5.5M5.5 11.5L7 10.5V13.5H5.5M5.5 17.5L7 16.5V19.5H5.5M10 6H20M10 12H20M10 18H20"),
            "有序列表",
            "ordered");
        AddToolbarAction(
            tools,
            CreateToolbarIcon("M10 13a5 5 0 0 0 7.54.54l1.09-1.09a5 5 0 0 0-7.07-7.07l-.63.63M14 11a5 5 0 0 0-7.54-.54l-1.09 1.09a5 5 0 0 0 7.07 7.07l.63-.63"),
            "链接",
            "link");
        AddToolbarAction(
            tools,
            CreateToolbarIcon("M4 5h16v14H4zM4 15l4-4 3 3 2-2 5 5M15 9h.01"),
            "插入图片",
            "image");

        return new Border
        {
            Padding = new Thickness(5, 4),
            Background = AppAppearanceResources.GetBrush("PaperBrush"),
            BorderBrush = AppAppearanceResources.GetBrush("HairlineBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = tools
        };

    }

    private void AddToolbarAction(StackPanel tools, string content, string tooltip, string tag) =>
        AddToolbarAction(tools, (object)content, tooltip, tag);

    private void AddToolbarAction(StackPanel tools, Control content, string tooltip, string tag) =>
        AddToolbarAction(tools, (object)content, tooltip, tag);

    private void AddToolbarAction(
        StackPanel tools,
        object content,
        string tooltip,
        string tag)
    {
        // Background is deliberately left unset so the shared style owns the
        // normal, pointerover and pressed states.
        var button = new Button
        {
            Content = NormalizeToolbarContent(content, tag),
            Tag = tag,
            Width = ToolbarButtonWidth,
            MinWidth = ToolbarButtonWidth,
            MaxWidth = ToolbarButtonWidth,
            Height = ToolbarButtonHeight,
            Padding = new Thickness(0),
            Focusable = false,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        button.Classes.Add("bookReflectionFormattingAction");
        ToolTip.SetTip(button, UiText.Get(tooltip));
        AutomationProperties.SetName(button, UiText.Get(tooltip));
        button.Click += ToolbarButton_Click;
        tools.Children.Add(button);
    }

    private static void AddToolbarSeparator(StackPanel tools) =>
        tools.Children.Add(new AvaloniaRectangle
        {
            Width = 1,
            Height = 16,
            Margin = new Thickness(5, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Fill = AppAppearanceResources.GetBrush("HairlineBrush")
        });

    // Icons are authored on a 24x24 grid. Rendering them through a Viewbox of a
    // fixed size gives every glyph the same scale and keeps the paths that draw
    // outside the old 18x18 box (the list and image icons) from being clipped.
    private static Control CreateToolbarIcon(string geometry)
    {
        var canvas = new Canvas
        {
            Width = ToolbarIconCanvas,
            Height = ToolbarIconCanvas,
            IsHitTestVisible = false
        };
        canvas.Children.Add(new AvaloniaPath
        {
            Data = Geometry.Parse(geometry),
            Stroke = AppAppearanceResources.GetBrush("InkBrush"),
            StrokeThickness = ToolbarIconStroke,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            IsHitTestVisible = false
        });

        return new Viewbox
        {
            Width = ToolbarIconSize,
            Height = ToolbarIconSize,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Child = canvas
        };
    }

    private static object NormalizeToolbarContent(object content, string tag)
    {
        if (content is string text)
        {
            // These labels are Latin and sit next to line icons. The bundled
            // KingHwaOldSong has a single face and only synthesises bold, so it
            // reads as a different language from the icons at this size; the
            // bundled Inter keeps the row on one optical weight.
            return new TextBlock
            {
                Text = text,
                Width = ToolbarIconSize,
                FontFamily = ToolbarLabelFontFamily,
                FontSize = tag == "bold" ? ToolbarBoldFontSize : ToolbarLabelFontSize,
                FontWeight = tag == "bold" ? FontWeight.Bold : FontWeight.Normal,
                Foreground = AppAppearanceResources.GetBrush("InkBrush"),
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        if (content is Control control)
        {
            control.HorizontalAlignment = HorizontalAlignment.Center;
            control.VerticalAlignment = VerticalAlignment.Center;
        }

        return content;
    }

    private StackPanel CreateImageActionBar(out List<Button> buttons)
    {
        buttons =
        [
            CreateImageActionButton("替换", "替换图片", 44),
            CreateImageActionButton("描述", "编辑图片描述", 44),
            CreateImageActionButton("−", "缩小图片", 30),
            CreateImageActionButton("＋", "放大图片", 30),
            CreateImageActionButton("删除", "删除图片", 44)
        ];
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            Background = AppAppearanceResources.GetBrush("ReaderPageBrush"),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(4),
            IsVisible = false,
            ZIndex = 20,
            Children = { buttons[0], buttons[1], buttons[2], buttons[3], buttons[4] }
        };
    }

    private Button CreateImageActionButton(
        string content,
        string tooltip,
        double width)
    {
        var button = new Button
        {
            Content = content,
            Width = width,
            MinWidth = width,
            Height = 26,
            Padding = new Thickness(4, 2),
            FontSize = 11,
            Focusable = false,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        button.Classes.Add("readerSelectionAction");
        ToolTip.SetTip(button, UiText.Get(tooltip));
        AutomationProperties.SetName(button, UiText.Get(tooltip));
        button.Click += ImageActionButton_Click;
        return button;
    }

    private void AddBlock(BlockState state, int index)
    {
        var view = CreateBlockView(state);
        _blocks.Insert(index, view);
        _blockStack.Children.Insert(index, view.Row);
    }

    private BlockView CreateBlockView(BlockState state)
    {
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            ColumnSpacing = 8,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var marker = new Border
        {
            Width = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            CornerRadius = new CornerRadius(2)
        };
        var contentHost = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        Grid.SetColumn(contentHost, 1);
        row.Children.Add(marker);
        row.Children.Add(contentHost);

        ReflectionRichTextBlock? richText = null;
        TextBox? editor = null;
        Border? imageHost = null;
        Bitmap? imageBitmap = null;
        Image? imageControl = null;
        Grid? imageFrame = null;
        TextBlock? imagePlaceholder = null;
        Border? imageSelectionBorder = null;
        StackPanel? imageActionBar = null;
        List<Button>? imageActionButtons = null;
        AvaloniaRectangle? resizeHandle = null;

        if (state.Kind == BlockKind.Image)
        {
            imageHost = new Border
            {
                Padding = new Thickness(0, 10, 0, 10),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            imageFrame = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = Brushes.Transparent,
                Focusable = true
            };
            AutomationProperties.SetName(imageFrame, UiText.Get("图片"));
            imageBitmap = LoadBitmap(state.ImageSource);
            imageControl = new Image
            {
                Source = imageBitmap,
                Stretch = Stretch.Uniform,
                MaxWidth = DefaultImageMaxWidth,
                MaxHeight = DefaultImageMaxHeight,
                HorizontalAlignment = HorizontalAlignment.Left,
                IsVisible = imageBitmap is not null
            };
            ToolTip.SetTip(imageControl, state.ImageAlt);
            imageFrame.Children.Add(imageControl);

            imagePlaceholder = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(state.ImageAlt)
                    ? UiText.Get("图片无法显示")
                    : state.ImageAlt,
                Foreground = AppAppearanceResources.GetBrush("MutedInkBrush"),
                TextWrapping = TextWrapping.Wrap,
                IsVisible = imageBitmap is null
            };
            imageFrame.Children.Add(imagePlaceholder);

            imageSelectionBorder = new Border
            {
                BorderBrush = AppAppearanceResources.GetBrush("InkBrush"),
                BorderThickness = new Thickness(1),
                IsVisible = false,
                IsHitTestVisible = false
            };
            imageFrame.Children.Add(imageSelectionBorder);

            if (imageBitmap is not null)
            {
                resizeHandle = new AvaloniaRectangle
                {
                    Width = 10,
                    Height = 10,
                    Fill = AppAppearanceResources.GetBrush("InkBrush"),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(0, 0, 4, 4),
                    Cursor = new Cursor(StandardCursorType.BottomRightCorner),
                    IsVisible = false
                };
                imageFrame.Children.Add(resizeHandle);
            }

            imageActionBar = CreateImageActionBar(out imageActionButtons);
            imageFrame.Children.Add(imageActionBar);
            imageHost.Child = imageFrame;
            contentHost.Children.Add(imageHost);
        }
        else
        {
            richText = new ReflectionRichTextBlock
            {
                IsHitTestVisible = false,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Top,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 5, 0, 5),
                FontFamily = new FontFamily("fonts:Kkindle#KingHwaOldSong")
            };

            editor = new TextBox
            {
                Tag = null,
                Text = state.PlainText,
                AcceptsReturn = false,
                TextWrapping = TextWrapping.Wrap,
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0, 5, 0, 5),
                MinHeight = 30,
                FontFamily = new FontFamily("fonts:Kkindle#KingHwaOldSong"),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Top,
                Foreground = Brushes.Transparent,
                CaretBrush = AppAppearanceResources.GetBrush("InkBrush"),
                SelectionBrush = new SolidColorBrush(Color.FromArgb(68, 17, 17, 17)),
                SelectionForegroundBrush = Brushes.Transparent,
                ClearSelectionOnLostFocus = true
            };
            editor.Classes.Add("bookReflectionEditor");

            contentHost.Children.Add(richText);
            contentHost.Children.Add(editor);
        }

        var view = new BlockView(
            state,
            row,
            marker,
            contentHost,
            richText,
            editor,
            imageHost,
            imageBitmap,
            imageControl,
            imageFrame,
            imagePlaceholder,
            imageSelectionBorder,
            imageActionBar,
            resizeHandle);
        if (editor is not null)
        {
            editor.Tag = view;
            editor.GotFocus += (_, _) =>
            {
                if (_activeBlock?.Editor is { } previousEditor
                    && previousEditor != editor)
                {
                    previousEditor.SelectionStart = previousEditor.CaretIndex;
                    previousEditor.SelectionEnd = previousEditor.CaretIndex;
                }
                _activeBlock = view;
                ClearImageSelection();
                UpdateSelectionToolbar(view);
            };
            editor.LostFocus += (_, _) =>
            {
                if (_selection?.Block == view)
                    _selection = null;
            };
            editor.TextChanged += BlockEditor_TextChanged;
            editor.PropertyChanged += BlockEditor_PropertyChanged;
            editor.AddHandler(
                InputElement.KeyDownEvent,
                BlockEditor_KeyDown,
                RoutingStrategies.Tunnel,
                handledEventsToo: true);
            editor.ContextFlyout = CreateReflectionEditorContextFlyout(view);
        }
        if (imageFrame is not null)
        {
            imageFrame.Tag = view;
            imageFrame.PointerPressed += ImageFrame_PointerPressed;
            imageFrame.PointerEntered += (_, _) => SetImageControls(view, true);
            imageFrame.PointerExited += (_, _) =>
            {
                if (_selectedImage != view && !view.IsResizing)
                    SetImageControls(view, false);
            };
            imageFrame.GotFocus += (_, _) => SelectImage(view);
            imageFrame.AddHandler(InputElement.KeyDownEvent, ImageFrame_KeyDown, RoutingStrategies.Bubble);
        }
        if (resizeHandle is not null)
        {
            resizeHandle.Tag = view;
            resizeHandle.PointerPressed += ImageResizeHandle_PointerPressed;
            resizeHandle.PointerMoved += ImageResizeHandle_PointerMoved;
            resizeHandle.PointerReleased += ImageResizeHandle_PointerReleased;
        }
        if (imageActionButtons is not null)
        {
            foreach (var (button, action) in imageActionButtons.Select((button, index) =>
                         (button, (ImageActionKind)index)))
            {
                button.Tag = new ImageActionRequest(view, action);
            }
        }

        UpdateBlockView(view);
        return view;
    }

    private void ImageFrame_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Grid { Tag: BlockView view }
            || e.Source is Button
            || !e.GetCurrentPoint(view.ImageFrame).Properties.IsLeftButtonPressed)
            return;

        SelectImage(view);
        e.Handled = true;
    }

    private void ImageFrame_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Source is Button
            || sender is not Grid { Tag: BlockView view })
            return;

        if (e.Key is Key.Delete or Key.Back)
        {
            DeleteImage(view);
            e.Handled = true;
        }
    }

    private async void ImageActionButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ImageActionRequest request })
            return;

        SelectImage(request.Block);
        switch (request.Action)
        {
            case ImageActionKind.Replace:
                await ReplaceImageAsync(request.Block);
                break;
            case ImageActionKind.EditAlt:
                await EditImageAltAsync(request.Block);
                break;
            case ImageActionKind.ZoomOut:
                ScaleImage(request.Block, 1 / ImageZoomFactor);
                break;
            case ImageActionKind.ZoomIn:
                ScaleImage(request.Block, ImageZoomFactor);
                break;
            case ImageActionKind.Delete:
                DeleteImage(request.Block);
                break;
        }
        e.Handled = true;
    }

    private void SelectImage(BlockView view)
    {
        if (_disposed || view.State.Kind != BlockKind.Image)
            return;

        if (_selectedImage is { } previous && previous != view)
            SetImageControls(previous, false);

        _selectedImage = view;
        _activeBlock = view;
        _selection = null;
        SetImageControls(view, true);
        view.ImageFrame?.Focus();
    }

    private void ClearImageSelection()
    {
        if (_selectedImage is not { } selected)
            return;

        SetImageControls(selected, false);
        _selectedImage = null;
    }

    private static void SetImageControls(BlockView view, bool visible)
    {
        view.ImageSelectionBorder?.SetCurrentValue(IsVisibleProperty, visible);
        view.ImageActionBar?.SetCurrentValue(IsVisibleProperty, visible);
        view.ResizeHandle?.SetCurrentValue(
            IsVisibleProperty,
            visible && view.ImageBitmap is not null);
    }

    private void ImageResizeHandle_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not AvaloniaRectangle { Tag: BlockView view }
            || view.ImageFrame is null
            || view.ImageBitmap is null
            || !e.GetCurrentPoint(view.ImageFrame).Properties.IsLeftButtonPressed)
            return;

        SelectImage(view);
        var size = GetImageDisplaySize(view);
        if (size.Width <= 0 || size.Height <= 0)
            return;

        _imageResizeSession = new ImageResizeSession(
            view,
            e.GetPosition(view.ImageFrame),
            size.Width,
            size.Height);
        view.IsResizing = true;
        e.Pointer.Capture(view.ResizeHandle);
        e.Handled = true;
    }

    private void ImageResizeHandle_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_imageResizeSession is not { } session
            || session.View.ImageFrame is null)
            return;

        var point = e.GetPosition(session.View.ImageFrame);
        var width = session.StartWidth + point.X - session.StartPoint.X;
        session.Changed |= SetImageWidth(
            session.View,
            width,
            session.StartHeight / session.StartWidth);
        e.Handled = true;
    }

    private void ImageResizeHandle_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_imageResizeSession is not { } session)
            return;

        session.View.IsResizing = false;
        e.Pointer.Capture(null);
        _imageResizeSession = null;
        if (session.Changed)
            RaiseContentChanged();
        e.Handled = true;
    }

    private void ScaleImage(BlockView view, double factor)
    {
        if (view.ImageBitmap is null || factor <= 0)
            return;

        if (SetImageWidth(view, GetImageDisplaySize(view).Width * factor))
            RaiseContentChanged();
    }

    private Size GetImageDisplaySize(BlockView view)
    {
        var width = view.ImageControl?.Bounds.Width ?? 0;
        var height = view.ImageControl?.Bounds.Height ?? 0;
        if (width > 0 && height > 0)
            return new Size(width, height);

        width = view.State.ImageWidth ?? view.ImageBitmap?.PixelSize.Width ?? 0;
        height = view.State.ImageHeight ?? view.ImageBitmap?.PixelSize.Height ?? 0;
        if (width > 0 && height <= 0 && view.ImageBitmap is { PixelSize.Width: > 0 } widthBitmap)
            height = width * widthBitmap.PixelSize.Height / widthBitmap.PixelSize.Width;
        if (height > 0 && width <= 0 && view.ImageBitmap is { PixelSize.Height: > 0 } heightBitmap)
            width = height * heightBitmap.PixelSize.Width / heightBitmap.PixelSize.Height;

        if (width <= 0 || height <= 0)
            return new Size(0, 0);

        var maxWidth = GetImageMaxWidth();
        var maxHeight = DefaultImageMaxHeight;
        var factor = Math.Min(1, Math.Min(maxWidth / width, maxHeight / height));
        return factor < 1
            ? new Size(width * factor, height * factor)
            : new Size(width, height);
    }

    private bool SetImageWidth(
        BlockView view,
        double width,
        double? aspectRatio = null)
    {
        if (view.ImageBitmap is not { PixelSize.Width: > 0, PixelSize.Height: > 0 })
            return false;

        var current = GetImageDisplaySize(view);
        if (current.Width <= 0 || current.Height <= 0)
            return false;

        var imageAspectRatio = aspectRatio is > 0
            ? aspectRatio.Value
            : current.Height / current.Width;
        var targetWidth = Math.Clamp(width, MinimumImageWidth, GetImageMaxWidth());
        var targetHeight = targetWidth * imageAspectRatio;
        if (targetHeight > DefaultImageMaxHeight)
        {
            targetHeight = DefaultImageMaxHeight;
            targetWidth = targetHeight / imageAspectRatio;
        }

        var imageWidth = Math.Max(1, (int)Math.Round(targetWidth));
        var imageHeight = Math.Max(1, (int)Math.Round(targetHeight));
        var changed = view.State.ImageWidth != imageWidth
            || view.State.ImageHeight != imageHeight;
        view.State.ImageWidth = imageWidth;
        view.State.ImageHeight = imageHeight;
        UpdateImageView(view);
        return changed;
    }

    private double GetImageMaxWidth()
    {
        var availableWidth = Bounds.Width;
        if (availableWidth <= 0)
            return DefaultImageMaxWidth;

        // Leave room for the vertical scrollbar and the surface border. This
        // keeps resized images inside the native editor at its minimum width.
        return Math.Max(
            MinimumImageWidth,
            Math.Min(DefaultImageMaxWidth, availableWidth - 24));
    }

    private void UpdateImageConstraints()
    {
        if (_disposed) return;
        foreach (var view in _blocks.Where(view => view.State.Kind == BlockKind.Image))
            UpdateImageView(view);
    }

    private void UpdateImageView(BlockView view)
    {
        if (view.State.Kind != BlockKind.Image)
            return;

        view.ImageControl?.SetCurrentValue(MaxWidthProperty, GetImageMaxWidth());
        view.ImageControl?.SetCurrentValue(MaxHeightProperty, DefaultImageMaxHeight);
        if (view.ImageControl is { } image)
        {
            image.Source = view.ImageBitmap;
            image.IsVisible = view.ImageBitmap is not null;
            if (view.State.ImageWidth is > 0)
            {
                image.Width = view.State.ImageWidth.Value;
                image.Height = view.State.ImageHeight is > 0
                    ? view.State.ImageHeight.Value
                    : double.NaN;
            }
            else
            {
                image.Width = double.NaN;
                image.Height = double.NaN;
            }
            ToolTip.SetTip(image, view.State.ImageAlt);
        }
        if (view.ImagePlaceholder is { } placeholder)
        {
            placeholder.Text = string.IsNullOrWhiteSpace(view.State.ImageAlt)
                ? UiText.Get("图片无法显示")
                : view.State.ImageAlt;
            placeholder.IsVisible = view.ImageBitmap is null;
        }
        SetImageControls(view, _selectedImage == view);
    }

    private async Task ReplaceImageAsync(BlockView view)
    {
        var topLevel = GetOwnerWindow();
        if (topLevel is null || view.State.Kind != BlockKind.Image)
            return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = UiText.Get("替换图片"),
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(UiText.Get("图片"))
                {
                    Patterns = ["*.png", "*.jpg", "*.jpeg", "*.gif", "*.webp", "*.bmp"]
                }
            ]
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;

        var bytes = await File.ReadAllBytesAsync(path);
        if (bytes.Length == 0)
            return;

        var source = $"data:{MimeTypeFor(path)};base64,{Convert.ToBase64String(bytes)}";
        var bitmap = LoadBitmap(source);
        if (bitmap is null)
            return;

        var previousBitmap = view.ImageBitmap;
        var width = view.State.ImageWidth;
        view.State.ImageSource = source;
        if (width is > 0)
        {
            view.State.ImageHeight = Math.Max(
                1,
                (int)Math.Round(width.Value * bitmap.PixelSize.Height / (double)bitmap.PixelSize.Width));
        }
        view.ImageBitmap = bitmap;
        UpdateImageView(view);
        previousBitmap?.Dispose();
        RaiseContentChanged();
    }

    private async Task EditImageAltAsync(BlockView view)
    {
        var alt = await PromptForImageAltAsync(view.State.ImageAlt);
        if (alt is null || view.State.Kind != BlockKind.Image)
            return;

        view.State.ImageAlt = alt;
        UpdateImageView(view);
        RaiseContentChanged();
    }

    private void DeleteImage(BlockView view)
    {
        var index = _blocks.IndexOf(view);
        if (index < 0 || view.State.Kind != BlockKind.Image)
            return;

        var focus = _blocks
            .Skip(index + 1)
            .Concat(_blocks.Take(index).Reverse())
            .FirstOrDefault(candidate => candidate.Editor is not null);
        ClearImageSelection();
        RemoveBlock(view);
        FocusBlock(focus ?? _blocks.LastOrDefault(candidate => candidate.Editor is not null), 0);
        RaiseContentChanged();
    }

    private void BlockEditor_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_suppressTextChanged
            || sender is not TextBox { Tag: BlockView view } editor)
            return;

        var nextText = editor.Text ?? string.Empty;
        var previousText = view.LastPlainText;
        if (string.Equals(previousText, nextText, StringComparison.Ordinal))
            return;

        var commonStart = 0;
        while (commonStart < previousText.Length
            && commonStart < nextText.Length
            && previousText[commonStart] == nextText[commonStart])
            commonStart++;

        var commonSuffix = 0;
        while (commonSuffix < previousText.Length - commonStart
            && commonSuffix < nextText.Length - commonStart
            && previousText[previousText.Length - 1 - commonSuffix]
                == nextText[nextText.Length - 1 - commonSuffix])
            commonSuffix++;

        var oldEnd = previousText.Length - commonSuffix;
        var newEnd = nextText.Length - commonSuffix;
        var inserted = nextText[commonStart..newEnd];
        var style = GetStyleAt(view.State.Inlines, commonStart);

        var before = SliceRuns(view.State.Inlines, 0, commonStart);
        var after = SliceRuns(view.State.Inlines, oldEnd, previousText.Length);
        if (inserted.Length > 0)
            before.Add(new InlineRun(inserted, style.Style, style.LinkUrl));

        before.AddRange(after);
        view.State.Inlines = MergeRuns(before);
        view.LastPlainText = nextText;
        UpdateBlockView(view);
        RaiseContentChanged();
        UpdateSelectionToolbar(view);
    }

    private void BlockEditor_PropertyChanged(
        object? sender,
        AvaloniaPropertyChangedEventArgs e)
    {
        if (sender is not TextBox { Tag: BlockView view } editor)
            return;

        if (e.Property == TextBox.SelectionStartProperty
            || e.Property == TextBox.SelectionEndProperty
            || e.Property == TextBox.CaretIndexProperty)
        {
            _activeBlock = view;
            UpdateSelectionToolbar(view);
        }
    }

    private async void BlockEditor_KeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { Tag: BlockView view } editor)
            return;

        var index = _blocks.IndexOf(view);
        if (index < 0) return;

        var text = editor.Text ?? string.Empty;
        var start = Math.Clamp(Math.Min(editor.SelectionStart, editor.SelectionEnd), 0, text.Length);
        var end = Math.Clamp(Math.Max(editor.SelectionStart, editor.SelectionEnd), 0, text.Length);

        var command = e.KeyModifiers.HasFlag(KeyModifiers.Control)
            || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        var pasteShortcut = (e.Key == Key.V && command)
            || (e.Key == Key.Insert && e.KeyModifiers.HasFlag(KeyModifiers.Shift));
        if (pasteShortcut)
        {
            e.Handled = true;
            await PasteClipboardAsync(
                view,
                editor,
                new SelectionSnapshot(view, start, end));
            return;
        }

        if (e.Key == Key.Space
            && start == end
            && end == text.Length
            && Regex.Match(text, @"^#{1,6}$") is { Success: true } headingShortcut)
        {
            view.State.Kind = (BlockKind)headingShortcut.Value.Length;
            view.State.Inlines = [];
            SetEditorText(view, string.Empty, 0);
            UpdateBlockView(view);
            e.Handled = true;
            RaiseContentChanged();
            return;
        }

        if (e.Key == Key.Enter)
        {
            SplitBlock(view, index, start, end);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Back
            && start == end
            && start == 0)
        {
            MergeWithPrevious(view, index);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Delete
            && start == end
            && end == text.Length)
        {
            MergeWithNext(view, index);
            e.Handled = true;
        }
    }

    private MenuFlyout CreateReflectionEditorContextFlyout(BlockView view)
    {
        var editor = view.Editor!;
        var cut = new MenuItem { Header = UiText.Get("剪切") };
        var copy = new MenuItem { Header = UiText.Get("复制") };
        var paste = new MenuItem { Header = UiText.Get("粘贴") };
        var selectAll = new MenuItem { Header = UiText.Get("全选") };

        cut.Click += (_, _) =>
        {
            editor.Focus();
            editor.Cut();
        };
        copy.Click += (_, _) =>
        {
            editor.Focus();
            editor.Copy();
        };
        paste.Click += async (_, _) =>
        {
            editor.Focus();
            await PasteClipboardAsync(view, editor);
        };
        selectAll.Click += (_, _) =>
        {
            editor.Focus();
            editor.SelectAll();
        };

        var menu = new MenuFlyout();
        menu.Items.Add(cut);
        menu.Items.Add(copy);
        menu.Items.Add(paste);
        menu.Items.Add(new Separator());
        menu.Items.Add(selectAll);
        menu.Opened += (_, _) =>
        {
            cut.IsEnabled = editor.CanCut;
            copy.IsEnabled = editor.CanCopy;
            paste.IsEnabled = !editor.IsReadOnly;
        };
        return menu;
    }

    private async Task PasteClipboardAsync(
        BlockView view,
        TextBox editor,
        SelectionSnapshot? selection = null)
    {
        var clipboard = TopLevel.GetTopLevel(editor)?.Clipboard;
        if (clipboard is null)
        {
            editor.Paste();
            return;
        }

        var clipboardImage = await TryGetClipboardImageAsync(clipboard);
        if (clipboardImage is null)
        {
            editor.Paste();
            return;
        }

        var text = editor.Text ?? string.Empty;
        var currentSelection = selection ?? new SelectionSnapshot(
            view,
            Math.Clamp(Math.Min(editor.SelectionStart, editor.SelectionEnd), 0, text.Length),
            Math.Clamp(Math.Max(editor.SelectionStart, editor.SelectionEnd), 0, text.Length));
        InsertImageBlock(
            currentSelection,
            clipboardImage.Value.Source,
            clipboardImage.Value.Alt);
    }

    private static async Task<(string Source, string Alt)?> TryGetClipboardImageAsync(
        IClipboard clipboard)
    {
        Bitmap? bitmap = null;
        try
        {
            bitmap = await clipboard.TryGetBitmapAsync();
        }
        catch
        {
            // Some clipboard providers do not expose CF_DIB/bitmap data. A
            // copied image file is handled by the file-list fallback below.
        }

        if (bitmap is not null)
        {
            try
            {
                using (bitmap)
                using (var stream = new MemoryStream())
                {
                    bitmap.Save(stream, PngBitmapEncoderOptions.Default);
                    return (
                        $"data:image/png;base64,{Convert.ToBase64String(stream.ToArray())}",
                        UiText.Get("粘贴的图片"));
                }
            }
            catch
            {
                // Fall through to copied image files, then to normal text.
            }
        }

        IReadOnlyList<IStorageItem>? files = null;
        try
        {
            files = await clipboard.TryGetFilesAsync();
        }
        catch
        {
        }

        if (files is null)
            return null;

        foreach (var file in files)
        {
            var path = file.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path)
                || !File.Exists(path)
                || !IsSupportedImagePath(path))
                continue;

            try
            {
                var bytes = await File.ReadAllBytesAsync(path);
                if (bytes.Length == 0)
                    continue;

                var source = $"data:{MimeTypeFor(path)};base64,{Convert.ToBase64String(bytes)}";
                using var validationBitmap = LoadBitmap(source);
                if (validationBitmap is null)
                    continue;

                return (
                    source,
                    Path.GetFileNameWithoutExtension(path));
            }
            catch
            {
                // Ignore an unsupported or unreadable clipboard file and
                // continue looking for another image item.
            }
        }

        return null;
    }

    private void SplitBlock(BlockView view, int index, int start, int end)
    {
        var state = view.State;
        var isList = state.Kind is BlockKind.UnorderedList or BlockKind.OrderedList;
        var beforeRuns = SliceRuns(state.Inlines, 0, start);
        var afterRuns = SliceRuns(state.Inlines, end, view.LastPlainText.Length);

        if (isList
            && beforeRuns.Count == 0
            && afterRuns.Count == 0)
        {
            state.Kind = BlockKind.Paragraph;
            state.Inlines = [];
            SetEditorText(view, string.Empty, 0);
            UpdateBlockView(view);
            AddBlock(new BlockState(), index + 1);
            FocusBlock(_blocks[index + 1], 0);
            RaiseContentChanged();
            return;
        }

        state.Inlines = beforeRuns;
        SetEditorText(view, state.PlainText, state.PlainText.Length);
        UpdateBlockView(view);

        var nextState = new BlockState
        {
            Kind = isList ? state.Kind : BlockKind.Paragraph,
            Inlines = afterRuns,
            OrderedNumber = state.Kind == BlockKind.OrderedList
                ? NextOrderedNumber(state.OrderedNumber)
                : 1
        };
        AddBlock(nextState, index + 1);
        FocusBlock(_blocks[index + 1], 0);
        RaiseContentChanged();
    }

    private void MergeWithPrevious(BlockView view, int index)
    {
        if (index == 0) return;

        if (view.State.Kind is BlockKind.UnorderedList or BlockKind.OrderedList
            && _blocks[index - 1].State.Kind is not (BlockKind.UnorderedList or BlockKind.OrderedList))
        {
            view.State.Kind = BlockKind.Paragraph;
            UpdateBlockView(view);
            FocusBlock(view, 0);
            RaiseContentChanged();
            return;
        }

        var previous = _blocks[index - 1];
        if (previous.State.Kind == BlockKind.Image)
            return;

        var caret = previous.State.PlainText.Length;
        previous.State.Inlines.AddRange(view.State.Inlines);
        previous.State.Inlines = MergeRuns(previous.State.Inlines);
        SetEditorText(previous, previous.State.PlainText, caret);
        RemoveBlock(view);
        FocusBlock(previous, caret);
        RaiseContentChanged();
    }

    private void MergeWithNext(BlockView view, int index)
    {
        if (index >= _blocks.Count - 1) return;

        var next = _blocks[index + 1];
        if (next.State.Kind == BlockKind.Image)
            return;

        var caret = view.State.PlainText.Length;
        view.State.Inlines.AddRange(next.State.Inlines);
        view.State.Inlines = MergeRuns(view.State.Inlines);
        SetEditorText(view, view.State.PlainText, caret);
        RemoveBlock(next);
        FocusBlock(view, caret);
        RaiseContentChanged();
    }

    private void SetEditorText(BlockView view, string text, int caret)
    {
        if (view.Editor is null) return;

        _suppressTextChanged = true;
        try
        {
            view.State.Inlines = view.State.Inlines.Count == 0
                ? []
                : MergeRuns(view.State.Inlines);
            view.LastPlainText = text;
            view.Editor.Text = text;
            view.Editor.CaretIndex = Math.Clamp(caret, 0, text.Length);
        }
        finally
        {
            _suppressTextChanged = false;
        }
    }

    private void RemoveBlock(BlockView view, bool ensurePlaceholder = true)
    {
        var index = _blocks.IndexOf(view);
        if (index < 0) return;

        _blocks.RemoveAt(index);
        _blockStack.Children.RemoveAt(index);
        view.Dispose();

        if (ensurePlaceholder && _blocks.Count == 0)
            AddBlock(new BlockState(), 0);
    }

    private void UpdateBlockView(BlockView view)
    {
        var state = view.State;
        if (state.Kind == BlockKind.Image)
        {
            view.Marker.IsVisible = false;
            view.Row.ColumnDefinitions[0].Width = new GridLength(0);
            UpdateImageView(view);
            return;
        }

        var isQuote = state.Kind == BlockKind.Quote;
        view.Marker.IsVisible = !isQuote;
        var markerText = state.Kind switch
        {
            BlockKind.UnorderedList => "•",
            BlockKind.OrderedList => $"{state.OrderedNumber}.",
            _ => string.Empty
        };
        var markerWidth = state.Kind switch
        {
            BlockKind.UnorderedList or BlockKind.OrderedList => 28,
            _ => 0
        };
        view.Row.ColumnDefinitions[0].Width = new GridLength(markerWidth);
        view.Marker.Width = markerWidth;
        view.Row.ColumnSpacing = isQuote ? 0 : 8;
        view.Row.Background = Brushes.Transparent;
        view.Marker.Margin = new Thickness(0);
        view.Marker.Background = Brushes.Transparent;
        view.ContentHost.HorizontalAlignment = isQuote
            ? HorizontalAlignment.Left
            : HorizontalAlignment.Stretch;
        view.ContentHost.VerticalAlignment = VerticalAlignment.Top;
        view.ContentHost.Background = isQuote
            ? AppAppearanceResources.GetBrush("PressedBrush")
            : Brushes.Transparent;
        view.Marker.Child = state.Kind is BlockKind.UnorderedList or BlockKind.OrderedList
            ? new TextBlock
            {
                Text = markerText,
                FontSize = 15,
                Foreground = AppAppearanceResources.GetBrush("MutedInkBrush"),
                TextAlignment = TextAlignment.Right,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 5, 0, 0)
            }
            : null;

        if (view.RichText is not null)
        {
            view.RichText.Foreground = isQuote
                ? AppAppearanceResources.GetBrush("InkBrush")
                : AppAppearanceResources.GetBrush("InkBrush");
            view.RichText.Margin = isQuote
                ? new Thickness(12, 10, 16, 10)
                : new Thickness(0, 5, 0, 5);
            view.RichText.FontStyle = isQuote
                ? FontStyle.Italic
                : FontStyle.Normal;
            view.RichText.FontSize = state.Kind switch
            {
                BlockKind.Heading1 => 28,
                BlockKind.Heading2 => 24,
                BlockKind.Heading3 => 21,
                BlockKind.Heading4 => 19,
                BlockKind.Heading5 => 17,
                BlockKind.Heading6 => 16,
                _ => 15
            };
            view.RichText.FontWeight = IsHeading(state.Kind)
                ? FontWeight.SemiBold
                : FontWeight.Normal;
            view.RichText.SetRuns(state.Inlines, isQuote);
        }

        if (view.Editor is not null)
        {
            view.Editor.FontSize = state.Kind switch
            {
                BlockKind.Heading1 => 28,
                BlockKind.Heading2 => 24,
                BlockKind.Heading3 => 21,
                BlockKind.Heading4 => 19,
                BlockKind.Heading5 => 17,
                BlockKind.Heading6 => 16,
                _ => 15
            };
            view.Editor.Padding = isQuote
                ? new Thickness(12, 10, 16, 10)
                : new Thickness(0, 5, 0, 5);
            view.Editor.FontStyle = isQuote
                ? FontStyle.Italic
                : FontStyle.Normal;
            view.Editor.MinHeight = state.Kind switch
            {
                BlockKind.Heading1 => 48,
                BlockKind.Heading2 => 43,
                BlockKind.Heading3 => 38,
                BlockKind.Heading4 => 35,
                BlockKind.Quote => 46,
                _ => 30
            };
        }
    }

    private void UpdateSelectionToolbar(BlockView view)
    {
        if (_disposed || view.Editor is null) return;

        var start = Math.Min(view.Editor.SelectionStart, view.Editor.SelectionEnd);
        var end = Math.Max(view.Editor.SelectionStart, view.Editor.SelectionEnd);
        if (start == end)
        {
            if (_selection?.Block == view)
                _selection = null;
            return;
        }

        _activeBlock = view;
        _selection = new SelectionSnapshot(view, start, end);
    }


    private void CollapseTextSelection(
        SelectionSnapshot? selection,
        int? caret = null,
        bool focusEditor = false)
    {
        _selection = null;
        if (selection?.Block.Editor is not { } editor)
            return;

        var textLength = editor.Text?.Length ?? 0;
        var position = Math.Clamp(caret ?? editor.CaretIndex, 0, textLength);
        editor.SelectionStart = position;
        editor.SelectionEnd = position;
        if (focusEditor)
            editor.Focus();
    }

    private SelectionSnapshot? GetToolbarSelection(bool allowCaret)
    {
        if (_selection is { } selection && _blocks.Contains(selection.Block))
            return selection;

        if (!allowCaret)
            return null;

        var view = _activeBlock ?? _blocks.LastOrDefault(block => block.Editor is not null);
        if (view?.Editor is not { } editor)
            return null;

        var textLength = editor.Text?.Length ?? 0;
        var start = Math.Clamp(Math.Min(editor.SelectionStart, editor.SelectionEnd), 0, textLength);
        var end = Math.Clamp(Math.Max(editor.SelectionStart, editor.SelectionEnd), start, textLength);
        return new SelectionSnapshot(view, start, end);
    }

    private async void ToolbarButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string action })
            return;

        e.Handled = true;
        var selection = GetToolbarSelection(allowCaret: true);
        switch (action)
        {
            case "bold":
                ApplyBold(selection);
                break;
            case "link":
                await ApplyLinkAsync(selection);
                break;
            case "image":
                await InsertImageAsync(selection);
                break;
            default:
                ApplyBlockAction(action, selection);
                break;
        }
    }

    private void ApplyBlockAction(string action, SelectionSnapshot? selection)
    {
        var view = selection?.Block ?? _activeBlock;
        if (view is null) return;

        var kind = action switch
        {
            "heading1" => BlockKind.Heading1,
            "heading2" => BlockKind.Heading2,
            "heading3" => BlockKind.Heading3,
            "heading4" => BlockKind.Heading4,
            "heading5" => BlockKind.Heading5,
            "heading6" => BlockKind.Heading6,
            "quote" => BlockKind.Quote,
            "unordered" => BlockKind.UnorderedList,
            "ordered" => BlockKind.OrderedList,
            _ => BlockKind.Paragraph
        };

        if (view.State.Kind == kind
            && kind is BlockKind.UnorderedList or BlockKind.OrderedList)
            kind = BlockKind.Paragraph;

        view.State.Kind = kind;
        UpdateBlockView(view);
        CollapseTextSelection(selection, selection?.End, focusEditor: true);
        RaiseContentChanged();
    }

    private void ApplyBold(SelectionSnapshot? selection)
    {
        if (selection is null) return;

        var allBold = SliceRuns(
                selection.Block.State.Inlines,
                selection.Start,
                selection.End)
            .All(run => run.Style == InlineStyle.Bold);
        var style = allBold ? InlineStyle.Plain : InlineStyle.Bold;
        selection.Block.State.Inlines = ApplyInlineStyle(
            selection.Block.State.Inlines,
            selection.Start,
            selection.End,
            style);
        RefreshTextBlock(selection.Block);
        CollapseTextSelection(selection, selection.End, focusEditor: true);
        RaiseContentChanged();
    }

    private async Task ApplyLinkAsync(SelectionSnapshot? selection)
    {
        var target = selection ?? GetToolbarSelection(allowCaret: true);
        if (target is null || !_blocks.Contains(target.Block)) return;

        var url = await PromptForLinkAsync();
        if (string.IsNullOrWhiteSpace(url)) return;

        var textLength = target.Block.LastPlainText.Length;
        var start = Math.Clamp(target.Start, 0, textLength);
        var end = Math.Clamp(target.End, start, textLength);
        if (start == end)
        {
            var before = SliceRuns(target.Block.State.Inlines, 0, start);
            before.Add(new InlineRun(url, InlineStyle.Link, url));
            before.AddRange(SliceRuns(target.Block.State.Inlines, end, textLength));
            target.Block.State.Inlines = MergeRuns(before);
            SetEditorText(target.Block, target.Block.State.PlainText, start + url.Length);
            UpdateBlockView(target.Block);
            CollapseTextSelection(target, start + url.Length, focusEditor: true);
        }
        else
        {
            target.Block.State.Inlines = ApplyInlineStyle(
                target.Block.State.Inlines,
                start,
                end,
                InlineStyle.Link,
                url);
            RefreshTextBlock(target.Block);
            CollapseTextSelection(target, end, focusEditor: true);
        }
        RaiseContentChanged();
    }

    private async Task InsertImageAsync(SelectionSnapshot? selection)
    {
        var target = selection ?? GetToolbarSelection(allowCaret: true);
        var topLevel = GetOwnerWindow();
        if (target is null || topLevel is null)
            return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = UiText.Get("插入图片"),
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(UiText.Get("图片"))
                {
                    Patterns = ["*.png", "*.jpg", "*.jpeg", "*.gif", "*.webp", "*.bmp"]
                }
            ]
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;

        var bytes = await File.ReadAllBytesAsync(path);
        if (bytes.Length == 0) return;

        var source = $"data:{MimeTypeFor(path)};base64,{Convert.ToBase64String(bytes)}";
        InsertImageBlock(target, source, Path.GetFileNameWithoutExtension(path));
    }

    private Window? GetOwnerWindow() =>
        TopLevel.GetTopLevel(this) as Window
        ?? TopLevel.GetTopLevel(FormattingToolbar) as Window;

    private void InsertImageBlock(
        SelectionSnapshot selection,
        string source,
        string alt)
    {
        var view = selection.Block;
        var index = _blocks.IndexOf(view);
        if (index < 0) return;

        var start = Math.Clamp(selection.Start, 0, view.LastPlainText.Length);
        var end = Math.Clamp(selection.End, start, view.LastPlainText.Length);
        var before = SliceRuns(view.State.Inlines, 0, start);
        var after = SliceRuns(view.State.Inlines, end, view.LastPlainText.Length);
        var imageState = new BlockState
        {
            Kind = BlockKind.Image,
            ImageSource = source,
            ImageAlt = alt
        };

        if (before.Count == 0 && after.Count == 0)
        {
            // RemoveBlock normally keeps one editable paragraph alive. The
            // image insertion path creates that paragraph itself so it does
            // not leave two empty paragraphs behind.
            RemoveBlock(view, ensurePlaceholder: false);
            AddBlock(imageState, Math.Min(index, _blocks.Count));
            AddBlock(new BlockState(), Math.Min(index + 1, _blocks.Count));
            FocusBlock(_blocks[Math.Min(index + 1, _blocks.Count - 1)], 0);
        }
        else
        {
            view.State.Inlines = before;
            SetEditorText(view, view.State.PlainText, view.State.PlainText.Length);
            UpdateBlockView(view);
            AddBlock(imageState, index + 1);
            AddBlock(
                new BlockState { Inlines = after },
                index + 2);
            FocusBlock(_blocks[index + 2], 0);
        }

        _selection = null;
        RaiseContentChanged();
    }

    private async Task<string?> PromptForLinkAsync()
    {
        var owner = GetOwnerWindow();
        if (owner is null) return null;

        var urlBox = new TextBox
        {
            Text = "https://",
            MinHeight = 34,
            FontSize = 14
        };
        var errorText = new TextBlock
        {
            Foreground = AppAppearanceResources.GetBrush("DangerBrush"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap
        };
        var completion = new TaskCompletionSource<string?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var dialog = new Window
        {
            Title = UiText.Get("链接"),
            Width = 460,
            Height = 190,
            MinWidth = 400,
            MinHeight = 180,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = AppAppearanceResources.GetBrush("PaperBrush")
        };

        void Complete(string? value)
        {
            if (value is not null)
                completion.TrySetResult(value);
            else
                completion.TrySetResult(null);
            dialog.Close();
        }

        var cancelButton = new Button
        {
            Content = UiText.Get("取消"),
            Padding = new Thickness(14, 7)
        };
        cancelButton.Classes.Add("quiet");
        cancelButton.Click += (_, _) => Complete(null);

        var saveButton = new Button
        {
            Content = UiText.Get("确定"),
            Padding = new Thickness(14, 7),
            MinWidth = 78
        };
        saveButton.Click += (_, _) =>
        {
            var value = urlBox.Text?.Trim() ?? string.Empty;
            if (value.Length == 0) return;
            if (!value.Contains(':', StringComparison.Ordinal))
                value = "https://" + value;
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("http" or "https" or "mailto"))
            {
                errorText.Text = UiText.Get("请输入有效链接。");
                return;
            }
            Complete(uri.ToString());
        };

        urlBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                saveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Complete(null);
            }
        };

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { cancelButton, saveButton }
        };
        dialog.Content = new Border
        {
            Padding = new Thickness(22),
            Child = new Grid
            {
                RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*"),
                RowSpacing = 8,
                Children =
                {
                    new TextBlock
                    {
                        Text = UiText.Get("链接地址"),
                        FontSize = 13
                    },
                    urlBox,
                    errorText,
                    actions
                }
            }
        };
        Grid.SetRow(urlBox, 1);
        Grid.SetRow(errorText, 2);
        Grid.SetRow(actions, 3);

        dialog.Closed += (_, _) => completion.TrySetResult(null);
        dialog.Opened += (_, _) =>
        {
            urlBox.Focus();
            urlBox.SelectAll();
        };
        dialog.Show(owner);
        return await completion.Task;
    }

    private async Task<string?> PromptForImageAltAsync(string currentAlt)
    {
        var owner = GetOwnerWindow();
        if (owner is null) return null;

        var altBox = new TextBox
        {
            Text = currentAlt,
            MinHeight = 34,
            FontSize = 14,
            PlaceholderText = UiText.Get("可选")
        };
        var completion = new TaskCompletionSource<string?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var dialog = new Window
        {
            Title = UiText.Get("编辑图片描述"),
            Width = 460,
            Height = 180,
            MinWidth = 400,
            MinHeight = 170,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = AppAppearanceResources.GetBrush("PaperBrush")
        };

        void Complete(string? value)
        {
            completion.TrySetResult(value);
            dialog.Close();
        }

        var cancelButton = new Button
        {
            Content = UiText.Get("取消"),
            Padding = new Thickness(14, 7)
        };
        cancelButton.Classes.Add("quiet");
        cancelButton.Click += (_, _) => Complete(null);

        var saveButton = new Button
        {
            Content = UiText.Get("确定"),
            Padding = new Thickness(14, 7),
            MinWidth = 78
        };
        saveButton.Click += (_, _) => Complete(altBox.Text?.Trim() ?? string.Empty);

        altBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                saveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Complete(null);
            }
        };

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { cancelButton, saveButton }
        };
        var fields = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*"),
            RowSpacing = 8,
            Children =
            {
                new TextBlock
                {
                    Text = UiText.Get("图片描述（可选）"),
                    FontSize = 13
                },
                altBox,
                actions
            }
        };
        Grid.SetRow(altBox, 1);
        Grid.SetRow(actions, 2);
        dialog.Content = new Border
        {
            Padding = new Thickness(22),
            Child = fields
        };

        dialog.Closed += (_, _) => completion.TrySetResult(null);
        dialog.Opened += (_, _) =>
        {
            altBox.Focus();
            altBox.SelectAll();
        };
        dialog.Show(owner);
        return await completion.Task;
    }

    private void RestoreSelection(SelectionSnapshot? selection)
    {
        if (selection?.Block.Editor is not { } editor)
            return;

        _activeBlock = selection.Block;
        editor.Focus();
        editor.SelectionStart = Math.Clamp(selection.Start, 0, editor.Text?.Length ?? 0);
        editor.SelectionEnd = Math.Clamp(selection.End, 0, editor.Text?.Length ?? 0);
        UpdateSelectionToolbar(selection.Block);
    }

    private void FocusBlock(BlockView? view, int? caret)
    {
        if (view?.Editor is not { } editor)
            return;

        _activeBlock = view;
        editor.Focus();
        var position = Math.Clamp(
            caret ?? editor.Text?.Length ?? 0,
            0,
            editor.Text?.Length ?? 0);
        editor.CaretIndex = position;
        editor.BringIntoView();
    }

    private void RefreshTextBlock(BlockView view)
    {
        if (view.Editor is not { } editor) return;
        SetEditorText(view, view.State.PlainText, editor.CaretIndex);
        UpdateBlockView(view);
    }

    private static List<InlineRun> ApplyInlineStyle(
        IReadOnlyList<InlineRun> runs,
        int start,
        int end,
        InlineStyle style,
        string? linkUrl = null)
    {
        var result = SliceRuns(runs, 0, start);
        result.AddRange(SliceRuns(runs, start, end)
            .Select(run => new InlineRun(run.Text, style, linkUrl)));
        result.AddRange(SliceRuns(runs, end, runs.Sum(run => run.Text.Length)));
        return MergeRuns(result);
    }

    private static InlineRun GetStyleAt(IReadOnlyList<InlineRun> runs, int index)
    {
        var position = 0;
        InlineRun? previous = null;
        foreach (var run in runs)
        {
            if (index < position + run.Text.Length)
                return run;
            position += run.Text.Length;
            previous = run;
        }
        return previous ?? new InlineRun(string.Empty);
    }

    private static List<InlineRun> SliceRuns(
        IReadOnlyList<InlineRun> runs,
        int start,
        int end)
    {
        var result = new List<InlineRun>();
        if (end <= start) return result;

        var position = 0;
        foreach (var run in runs)
        {
            var runEnd = position + run.Text.Length;
            var localStart = Math.Max(0, start - position);
            var localEnd = Math.Min(run.Text.Length, end - position);
            if (localEnd > localStart)
            {
                result.Add(new InlineRun(
                    run.Text[localStart..localEnd],
                    run.Style,
                    run.LinkUrl));
            }
            position = runEnd;
            if (position >= end) break;
        }
        return result;
    }

    private static List<InlineRun> MergeRuns(IEnumerable<InlineRun> runs)
    {
        var result = new List<InlineRun>();
        foreach (var run in runs.Where(run => run.Text.Length > 0))
        {
            if (result.LastOrDefault() is { } previous
                && previous.Style == run.Style
                && string.Equals(previous.LinkUrl, run.LinkUrl, StringComparison.Ordinal))
            {
                previous.Text += run.Text;
            }
            else
            {
                result.Add(new InlineRun(run.Text, run.Style, run.LinkUrl));
            }
        }
        return result;
    }

    private static BlockState ParseBlock(string source)
    {
        var image = ImagePattern.Match(source);
        if (image.Success && IsSupportedImageSource(image.Groups["source"].Value))
        {
            return new BlockState
            {
                Kind = BlockKind.Image,
                ImageAlt = image.Groups["alt"].Value,
                ImageSource = image.Groups["source"].Value,
                ImageWidth = ParsePositiveInt(image.Groups["width"].Value),
                ImageHeight = ParsePositiveInt(image.Groups["height"].Value)
            };
        }

        var heading = HeadingPattern.Match(source);
        if (heading.Success)
        {
            return new BlockState
            {
                Kind = (BlockKind)heading.Groups["marks"].Value.Length,
                Inlines = ParseInline(heading.Groups["text"].Value)
            };
        }

        var quote = Regex.Match(source, @"^\s*>\s?(?<text>.*)$");
        if (quote.Success)
        {
            return new BlockState
            {
                Kind = BlockKind.Quote,
                Inlines = ParseInline(quote.Groups["text"].Value)
            };
        }

        var unordered = UnorderedPattern.Match(source);
        if (unordered.Success)
        {
            return new BlockState
            {
                Kind = BlockKind.UnorderedList,
                Inlines = ParseInline(unordered.Groups["text"].Value)
            };
        }

        var ordered = OrderedPattern.Match(source);
        if (ordered.Success)
        {
            _ = int.TryParse(ordered.Groups["number"].Value, out var number);
            return new BlockState
            {
                Kind = BlockKind.OrderedList,
                OrderedNumber = Math.Max(1, number),
                Inlines = ParseInline(ordered.Groups["text"].Value)
            };
        }

        return new BlockState { Inlines = ParseInline(source) };
    }

    private static List<InlineRun> ParseInline(string text)
    {
        var result = new List<InlineRun>();
        var position = 0;
        foreach (Match match in InlinePattern.Matches(text))
        {
            if (match.Index > position)
                result.Add(new InlineRun(text[position..match.Index]));

            if (match.Groups["bold"].Success)
            {
                result.Add(new InlineRun(
                    match.Groups["bold"].Value,
                    InlineStyle.Bold));
            }
            else
            {
                result.Add(new InlineRun(
                    match.Groups["label"].Value,
                    InlineStyle.Link,
                    match.Groups["url"].Value));
            }
            position = match.Index + match.Length;
        }

        if (position < text.Length)
            result.Add(new InlineRun(text[position..]));

        return MergeRuns(result);
    }

    private void RaiseContentChanged() => ContentChanged?.Invoke(this, EventArgs.Empty);

    private static bool IsHeading(BlockKind kind) => kind is
        BlockKind.Heading1 or
        BlockKind.Heading2 or
        BlockKind.Heading3 or
        BlockKind.Heading4 or
        BlockKind.Heading5 or
        BlockKind.Heading6;

    private static int NextOrderedNumber(int number) => number >= int.MaxValue
        ? int.MaxValue
        : Math.Max(1, number + 1);

    private static int? ParsePositiveInt(string value) =>
        int.TryParse(value, out var number) && number > 0
            ? number
            : null;

    private static string MimeTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        _ => "image/png"
    };

    private static bool IsSupportedImagePath(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp";

    private static bool IsSupportedImageSource(string source) =>
        source.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)
        || (Uri.TryCreate(source, UriKind.Absolute, out var uri)
            && uri.IsFile
            && File.Exists(uri.LocalPath));

    private static Bitmap? LoadBitmap(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;

        Bitmap? bitmap = null;
        try
        {
            if (source.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            {
                var comma = source.IndexOf(',');
                if (comma < 0) return null;
                var payload = source[(comma + 1)..];
                var bytes = source[..comma].Contains(";base64", StringComparison.OrdinalIgnoreCase)
                    ? Convert.FromBase64String(payload)
                    : Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload));
                using var stream = new MemoryStream(bytes, writable: false);
                bitmap = new Bitmap(stream);
            }
            else if (Uri.TryCreate(source, UriKind.Absolute, out var uri)
                && uri.IsFile
                && File.Exists(uri.LocalPath))
            {
                bitmap = new Bitmap(uri.LocalPath);
            }
            return bitmap;
        }
        catch
        {
            bitmap?.Dispose();
            return null;
        }
    }

    internal static string NormalizeMarkdown(string markdown)
    {
        var normalized = markdown;
        for (var attempt = 0; attempt < 3 && LooksLikeNestedJsonString(normalized); attempt++)
        {
            try
            {
                var decoded = JsonSerializer.Deserialize<string>(normalized);
                if (decoded is null || decoded == normalized)
                    break;
                normalized = decoded;
            }
            catch (JsonException)
            {
                break;
            }
        }
        return normalized;
    }

    private static bool LooksLikeNestedJsonString(string value) =>
        value.Length >= 2
        && value[0] == '"'
        && value[^1] == '\"'
        && (value.Contains("\\n", StringComparison.Ordinal)
            || value.Contains("\\r", StringComparison.Ordinal)
            || value.Contains("\\\"", StringComparison.Ordinal)
            || value.Contains("\\\\", StringComparison.Ordinal));

    private sealed class ReflectionRichTextBlock : TextBlock
    {
        public void SetRuns(IReadOnlyList<InlineRun> runs, bool quote)
        {
            Inlines = new InlineCollection();
            if (quote)
                Inlines.Add(CreateQuoteMark("“"));

            foreach (var run in runs)
            {
                var textRun = new Run(run.Text);
                if (run.Style == InlineStyle.Bold)
                    textRun.FontWeight = FontWeight.Bold;
                else if (run.Style == InlineStyle.Link)
                {
                    textRun.Foreground = new SolidColorBrush(Color.Parse("#275F84"));
                    textRun.TextDecorations = Avalonia.Media.TextDecorations.Underline;
                }
                Inlines?.Add(textRun);
            }

            if (quote)
                Inlines?.Add(CreateQuoteMark("”"));
        }

        private Run CreateQuoteMark(string text) => new(text)
        {
            Foreground = AppAppearanceResources.GetBrush("AccentBrush"),
            FontSize = Math.Max(30, FontSize + 16),
            FontWeight = FontWeight.SemiBold
        };

        public void SetRuns(IReadOnlyList<InlineRun> runs)
        {
            SetRuns(runs, quote: false);
        }
    }

    private sealed class BlockView
    {
        public BlockView(
            BlockState state,
            Grid row,
            Border marker,
            Grid contentHost,
            ReflectionRichTextBlock? richText,
            TextBox? editor,
            Border? imageHost,
            Bitmap? imageBitmap,
            Image? imageControl,
            Grid? imageFrame,
            TextBlock? imagePlaceholder,
            Border? imageSelectionBorder,
            StackPanel? imageActionBar,
            AvaloniaRectangle? resizeHandle)
        {
            State = state;
            Row = row;
            Marker = marker;
            ContentHost = contentHost;
            RichText = richText;
            Editor = editor;
            ImageHost = imageHost;
            ImageBitmap = imageBitmap;
            ImageControl = imageControl;
            ImageFrame = imageFrame;
            ImagePlaceholder = imagePlaceholder;
            ImageSelectionBorder = imageSelectionBorder;
            ImageActionBar = imageActionBar;
            ResizeHandle = resizeHandle;
            LastPlainText = state.PlainText;
        }

        public BlockState State { get; }
        public Grid Row { get; }
        public Border Marker { get; }
        public Grid ContentHost { get; }
        public ReflectionRichTextBlock? RichText { get; }
        public TextBox? Editor { get; }
        public Border? ImageHost { get; }
        public Bitmap? ImageBitmap { get; set; }
        public Image? ImageControl { get; }
        public Grid? ImageFrame { get; }
        public TextBlock? ImagePlaceholder { get; }
        public Border? ImageSelectionBorder { get; }
        public StackPanel? ImageActionBar { get; }
        public AvaloniaRectangle? ResizeHandle { get; }
        public bool IsResizing { get; set; }
        public string LastPlainText { get; set; }

        public void Dispose()
        {
            ImageBitmap?.Dispose();
            ImageBitmap = null;
        }
    }

    private sealed class ImageResizeSession
    {
        public ImageResizeSession(
            BlockView view,
            Point startPoint,
            double startWidth,
            double startHeight)
        {
            View = view;
            StartPoint = startPoint;
            StartWidth = startWidth;
            StartHeight = startHeight;
        }

        public BlockView View { get; }
        public Point StartPoint { get; }
        public double StartWidth { get; }
        public double StartHeight { get; }
        public bool Changed { get; set; }
    }

    private sealed record ImageActionRequest(BlockView Block, ImageActionKind Action);

    private enum ImageActionKind
    {
        Replace,
        EditAlt,
        ZoomOut,
        ZoomIn,
        Delete
    }

    private sealed class SelectionSnapshot
    {
        public SelectionSnapshot(BlockView block, int start, int end)
        {
            Block = block;
            Start = start;
            End = end;
        }

        public BlockView Block { get; }
        public int Start { get; }
        public int End { get; }
    }

    private sealed class BlockState
    {
        public BlockKind Kind { get; set; }
        public List<InlineRun> Inlines { get; set; } = [];
        public int OrderedNumber { get; set; } = 1;
        public string ImageAlt { get; set; } = string.Empty;
        public string ImageSource { get; set; } = string.Empty;
        public int? ImageWidth { get; set; }
        public int? ImageHeight { get; set; }

        public string PlainText => string.Concat(Inlines.Select(run => run.Text));

        public string ToMarkdown()
        {
            if (Kind == BlockKind.Image)
            {
                var dimensions = ImageWidth is > 0
                    ? $" ={ImageWidth}{(ImageHeight is > 0 ? $"x{ImageHeight}" : string.Empty)}"
                    : string.Empty;
                return $"![{ImageAlt}]({ImageSource}{dimensions})";
            }

            var inline = ToInlineMarkdown();
            return Kind switch
            {
                BlockKind.Heading1 => "# " + inline,
                BlockKind.Heading2 => "## " + inline,
                BlockKind.Heading3 => "### " + inline,
                BlockKind.Heading4 => "#### " + inline,
                BlockKind.Heading5 => "##### " + inline,
                BlockKind.Heading6 => "###### " + inline,
                BlockKind.UnorderedList => "- " + inline,
                BlockKind.OrderedList => $"{Math.Max(1, OrderedNumber)}. " + inline,
                BlockKind.Quote => "> " + inline.ReplaceLineEndings("\n> "),
                _ => inline
            };
        }

        private string ToInlineMarkdown()
        {
            var builder = new StringBuilder();
            foreach (var run in Inlines)
            {
                if (run.Style == InlineStyle.Bold)
                    builder.Append("**").Append(run.Text).Append("**");
                else if (run.Style == InlineStyle.Link)
                    builder.Append('[').Append(run.Text).Append("](").Append(run.LinkUrl).Append(')');
                else
                    builder.Append(run.Text);
            }
            return builder.ToString();
        }
    }

    private sealed class InlineRun
    {
        public InlineRun(string text, InlineStyle style = InlineStyle.Plain, string? linkUrl = null)
        {
            Text = text;
            Style = style;
            LinkUrl = linkUrl;
        }

        public string Text { get; set; }
        public InlineStyle Style { get; }
        public string? LinkUrl { get; }
    }

    private enum InlineStyle
    {
        Plain,
        Bold,
        Link
    }

    private enum BlockKind
    {
        Paragraph,
        Heading1,
        Heading2,
        Heading3,
        Heading4,
        Heading5,
        Heading6,
        Quote,
        UnorderedList,
        OrderedList,
        Image
    }
}
