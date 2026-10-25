using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Markdig;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Kkindle;

/// <summary>
/// Native, offline Markdown rendering for answers and reflection previews.
/// CommonMark parsing preserves nested formatting, lists, code and tables.
/// Citations and TeX are embedded without changing the original answer.
/// </summary>
public sealed partial class KreaderMarkdownTextBlock : TextBlock
{
    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<KreaderMarkdownTextBlock, string?>(nameof(Markdown));
    public static readonly StyledProperty<Action<string>?> CitationActionProperty =
        AvaloniaProperty.Register<KreaderMarkdownTextBlock, Action<string>?>(nameof(CitationAction));
    public static readonly StyledProperty<Func<string, ReaderAiSourceViewModel?>?> CitationSourceResolverProperty =
        AvaloniaProperty.Register<KreaderMarkdownTextBlock, Func<string, ReaderAiSourceViewModel?>?>(nameof(CitationSourceResolver));
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables().UseTaskLists().UseEmphasisExtras(EmphasisExtraOptions.Strikethrough)
        .UseAutoLinks().UseCjkFriendlyEmphasis().DisableHtml().Build();
    private static readonly Regex ProtectedTokenPattern = new(@"\uE000\d+\uE001", RegexOptions.Compiled);
    private static readonly Regex ImagePattern = new(
        @"^\s*!\[(?<alt>[^\]]*)\]\((?<source>[^)\s\r\n]+)(?:\s*=\s*(?<width>\d+)(?:x(?<height>\d+))?)?\)\s*$",
        RegexOptions.Compiled);
    private readonly List<Bitmap> _markdownBitmaps = [];
    private readonly List<Control> _embeddedBlocks = [];
    private readonly List<(Border Border, bool Fill)> _themedBorders = [];

    public KreaderMarkdownTextBlock() => TextWrapping = TextWrapping.Wrap;
    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }
    public Action<string>? CitationAction
    {
        get => GetValue(CitationActionProperty);
        set => SetValue(CitationActionProperty, value);
    }
    public Func<string, ReaderAiSourceViewModel?>? CitationSourceResolver
    {
        get => GetValue(CitationSourceResolverProperty);
        set => SetValue(CitationSourceResolverProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // Text and Inlines share storage. Keep Markdown as a separate source
        // throughout streaming updates and citation rebinding.
        if (change.Property == MarkdownProperty || change.Property == CitationActionProperty || change.Property == CitationSourceResolverProperty)
            RebuildMarkdownInlines();
        else if (change.Property == BoundsProperty || change.Property == ForegroundProperty || change.Property == FontSizeProperty)
        {
            UpdateMathAppearance();
            UpdateBlockAppearance();
        }
    }

    private void RebuildMarkdownInlines()
    {
        foreach (var bitmap in _markdownBitmaps) bitmap.Dispose();
        _markdownBitmaps.Clear();
        _markdownMath.Clear();
        _mathViews.Clear();
        _embeddedBlocks.Clear();
        _themedBorders.Clear();
        Inlines = new InlineCollection();
        if (string.IsNullOrWhiteSpace(Markdown)) return;
        var document = Markdig.Markdown.Parse(ProtectMath(Markdown.ReplaceLineEndings("\n")), Pipeline);
        RenderBlocks(document, Inlines, 0);
        UpdateBlockAppearance();
    }

    private void RenderBlocks(ContainerBlock blocks, InlineCollection target, int depth)
    {
        var first = true;
        foreach (var block in blocks)
        {
            if (block is LinkReferenceDefinitionGroup) continue;
            if (!first) target.Add(new LineBreak());
            first = false;
            switch (block)
            {
                case HeadingBlock heading:
                    var headingSpan = new Span
                    {
                        FontSize = FontSize * (heading.Level switch { 1 => 1.5, 2 => 1.3, 3 => 1.15, _ => 1.05 }),
                        FontWeight = FontWeight.SemiBold
                    };
                    RenderInline(heading.Inline, headingSpan.Inlines);
                    target.Add(headingSpan);
                    break;
                case ParagraphBlock paragraph:
                    RenderInline(paragraph.Inline, target);
                    break;
                case ListBlock list:
                    RenderList(list, target, depth);
                    break;
                case QuoteBlock quote:
                    var quoteText = CreateBlockText();
                    RenderBlocks(quote, quoteText.Inlines!, depth);
                    var quoteBorder = new Border
                    {
                        BorderThickness = new Thickness(2, 0, 0, 0),
                        Padding = new Thickness(10, 3, 0, 3), Child = quoteText
                    };
                    _themedBorders.Add((quoteBorder, false));
                    AddBlock(quoteBorder, target);
                    break;
                case CodeBlock code:
                    AddCodeBlock(code, target);
                    break;
                case Table table:
                    AddTable(table, target);
                    break;
                case ThematicBreakBlock:
                    var rule = new Border { Height = 1, Width = MathWidth, Margin = new Thickness(0, 7) };
                    _themedBorders.Add((rule, true));
                    AddBlock(rule, target);
                    break;
                case ContainerBlock container:
                    RenderBlocks(container, target, depth);
                    break;
                case LeafBlock leaf:
                    RenderInline(leaf.Inline, target);
                    break;
            }
            while (target.LastOrDefault() is LineBreak) target.RemoveAt(target.Count - 1);
            target.Add(new LineBreak());
        }
        while (target.LastOrDefault() is LineBreak) target.RemoveAt(target.Count - 1);
    }

    private void RenderList(ListBlock list, InlineCollection target, int depth)
    {
        _ = int.TryParse(list.OrderedStart, CultureInfo.InvariantCulture, out var number);
        foreach (var item in list.OfType<ListItemBlock>())
        {
            var first = true;
            foreach (var block in item)
            {
                if (block is ParagraphBlock paragraph)
                {
                    target.Add(new Run(new string(' ', Math.Min(depth, 12) * 3)));
                    var task = paragraph.Inline?.FirstChild is TaskList;
                    if (first && !task) target.Add(new Run(list.IsOrdered ? $"{number}. " : "• "));
                    else if (!first) target.Add(new Run("   "));
                    RenderInline(paragraph.Inline, target);
                    target.Add(new LineBreak());
                }
                else if (block is ListBlock nested)
                    RenderList(nested, target, depth + 1);
                else if (block is ContainerBlock container)
                    RenderBlocks(container, target, depth + 1);
                else if (block is CodeBlock code)
                    AddCodeBlock(code, target);
                first = false;
            }
            number++;
        }
    }

    private void RenderInline(ContainerInline? container, InlineCollection target)
    {
        if (container is null) return;
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    AppendLiteral(literal.Content.ToString(), target);
                    break;
                case LineBreakInline:
                    target.Add(new LineBreak());
                    break;
                case CodeInline code:
                    target.Add(new Run(code.Content)
                    {
                        FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"),
                        FontSize = Math.Max(10, FontSize - 1)
                    });
                    break;
                case EmphasisInline emphasis:
                    var span = new Span();
                    if (emphasis.DelimiterChar == '~') span.TextDecorations = Avalonia.Media.TextDecorations.Strikethrough;
                    else if (emphasis.DelimiterCount >= 2) span.FontWeight = FontWeight.Bold;
                    else span.FontStyle = FontStyle.Italic;
                    RenderInline(emphasis, span.Inlines);
                    target.Add(span);
                    break;
                case LinkInline { IsImage: true } imageLink:
                    var alt = InlineText(imageLink);
                    var image = CreateMarkdownImage($"![{alt}]({imageLink.Url})");
                    target.Add(image is null ? new Run(alt) : new InlineUIContainer(image));
                    break;
                case LinkInline link:
                    AppendWebLink(InlineText(link), link.Url, target);
                    break;
                case AutolinkInline link:
                    AppendWebLink(link.Url, link.Url, target);
                    break;
                case HtmlEntityInline entity:
                    target.Add(new Run(entity.Transcoded.ToString()));
                    break;
                case TaskList task:
                    target.Add(new Run(task.Checked ? "☑" : "☐"));
                    break;
                case ContainerInline nested:
                    RenderInline(nested, target);
                    break;
            }
        }
    }

    private static string InlineText(ContainerInline container) => string.Concat(container.Select(inline => inline switch
    {
        LiteralInline text => text.Content.ToString(),
        ContainerInline nested => InlineText(nested),
        _ => string.Empty
    }));

    private void AppendWebLink(string title, string? url, InlineCollection target)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") || uri.UserInfo.Length > 0)
        {
            target.Add(new Run(title));
            return;
        }
        var text = new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap, TextDecorations = Avalonia.Media.TextDecorations.Underline };
        text.Bind(FontSizeProperty, new Binding(nameof(FontSize)) { Source = this });
        var button = new HyperlinkButton
        {
            Content = text, NavigateUri = uri, Padding = new Thickness(0),
            MinWidth = 0, MinHeight = 0, MaxWidth = MathWidth,
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(button, uri.AbsoluteUri);
        target.Add(new InlineUIContainer(button));
    }

    private void AppendLiteral(string text, InlineCollection target)
    {
        var position = 0;
        foreach (Match match in ProtectedTokenPattern.Matches(text))
        {
            if (match.Index > position) target.Add(new Run(text[position..match.Index]));
            AppendMath(match.Value, target);
            position = match.Index + match.Length;
        }
        if (position < text.Length) target.Add(new Run(text[position..]));
    }

    private void AppendCitation(string source, InlineCollection target)
    {
        var sourceId = source[1..^1].ToUpperInvariant();
        var button = new Button
        {
            Content = $"[{sourceId}]", Focusable = true, Padding = new Thickness(1, 0),
            VerticalAlignment = VerticalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center
        };
        button.Classes.Add("readerAiCitationMarker");
        AttachCitationPreview(button, sourceId);
        button.Click += (_, _) => CitationAction?.Invoke(sourceId);
        target.Add(new InlineUIContainer(button));
    }

    private TextBlock CreateBlockText()
    {
        var text = new TextBlock { Inlines = new InlineCollection(), TextWrapping = TextWrapping.Wrap };
        text.Bind(ForegroundProperty, new Binding(nameof(Foreground)) { Source = this });
        text.Bind(FontSizeProperty, new Binding(nameof(FontSize)) { Source = this });
        return text;
    }

    private void AddBlock(Control control, InlineCollection target)
    {
        control.MaxWidth = MathWidth;
        control.HorizontalAlignment = HorizontalAlignment.Left;
        _embeddedBlocks.Add(control);
        target.Add(new InlineUIContainer(control));
    }

    private void AddCodeBlock(CodeBlock code, InlineCollection target)
    {
        var contents = new StackPanel { Spacing = 6 };
        if (code is FencedCodeBlock { Info.Length: > 0 } fenced)
        {
            var language = new TextBlock { Text = fenced.Info, FontSize = 10, Opacity = 0.65 };
            language.Bind(ForegroundProperty, new Binding(nameof(Foreground)) { Source = this });
            contents.Children.Add(language);
        }
        var text = new SelectableTextBlock
        {
            Text = code.Lines.ToString(), FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"),
            FontSize = Math.Max(10, FontSize - 1), TextWrapping = TextWrapping.NoWrap
        };
        text.Bind(ForegroundProperty, new Binding(nameof(Foreground)) { Source = this });
        contents.Children.Add(new ScrollViewer
        {
            Content = text, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
        });
        var border = new Border { Padding = new Thickness(10), Child = contents, CornerRadius = new CornerRadius(3) };
        _themedBorders.Add((border, true));
        AddBlock(border, target);
    }

    private void AddTable(Table table, InlineCollection target)
    {
        var grid = new Grid();
        var columns = Math.Max(1, table.ColumnDefinitions.Count);
        for (var i = 0; i < columns; i++) grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        for (var rowIndex = 0; rowIndex < table.Count; rowIndex++)
        {
            if (table[rowIndex] is not TableRow row) continue;
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (var columnIndex = 0; columnIndex < row.Count; columnIndex++)
            {
                if (row[columnIndex] is not TableCell cell) continue;
                var text = CreateBlockText();
                text.MinWidth = 68;
                text.MaxWidth = 220;
                if (row.IsHeader) text.FontWeight = FontWeight.SemiBold;
                if (columnIndex < table.ColumnDefinitions.Count)
                    text.TextAlignment = table.ColumnDefinitions[columnIndex].Alignment switch
                    {
                        TableColumnAlign.Center => TextAlignment.Center,
                        TableColumnAlign.Right => TextAlignment.Right,
                        _ => TextAlignment.Left
                    };
                RenderBlocks(cell, text.Inlines!, 0);
                var border = new Border { Padding = new Thickness(8, 5), BorderThickness = new Thickness(0, 0, 0, 1), Child = text };
                _themedBorders.Add((border, row.IsHeader));
                Grid.SetRow(border, rowIndex);
                Grid.SetColumn(border, columnIndex);
                grid.Children.Add(border);
            }
        }
        AddBlock(new ScrollViewer
        {
            Content = grid, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
        }, target);
    }

    private void UpdateBlockAppearance()
    {
        foreach (var block in _embeddedBlocks)
        {
            block.MaxWidth = MathWidth;
            if (block is Border { Height: 1 }) block.Width = MathWidth;
        }
        var ink = (Foreground as ISolidColorBrush)?.Color ?? Colors.Black;
        foreach (var (border, fill) in _themedBorders)
        {
            border.BorderBrush = new SolidColorBrush(ink, 0.25);
            if (fill) border.Background = new SolidColorBrush(ink, 0.06);
        }
    }

    private Image? CreateMarkdownImage(string token)
    {
        var match = ImagePattern.Match(token);
        if (!match.Success) return null;
        var source = match.Groups["source"].Value;
        Bitmap? bitmap = null;
        try
        {
            if (source.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            {
                var comma = source.IndexOf(',');
                if (comma < 0) return null;
                var bytes = source[..comma].Contains(";base64", StringComparison.OrdinalIgnoreCase)
                    ? Convert.FromBase64String(source[(comma + 1)..])
                    : Encoding.UTF8.GetBytes(Uri.UnescapeDataString(source[(comma + 1)..]));
                using var stream = new MemoryStream(bytes, writable: false);
                bitmap = new Bitmap(stream);
            }
            else if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.IsFile && File.Exists(uri.LocalPath))
                bitmap = new Bitmap(uri.LocalPath);
            if (bitmap is null) return null;
            _markdownBitmaps.Add(bitmap);
            var image = new Image { Source = bitmap, Stretch = Stretch.Uniform, MaxWidth = MathWidth, MaxHeight = 360 };
            if (int.TryParse(match.Groups["width"].Value, out var width) && width > 0) image.Width = width;
            if (int.TryParse(match.Groups["height"].Value, out var height) && height > 0) image.Height = height;
            ToolTip.SetTip(image, match.Groups["alt"].Value);
            _embeddedBlocks.Add(image);
            return image;
        }
        catch
        {
            bitmap?.Dispose();
            return null;
        }
    }
}
