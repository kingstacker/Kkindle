using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Kkindle;

/// <summary>
/// Lightweight Markdown renderer for AI assistant bubbles. The WinUI
/// reference rendered answers with MarkdownRichTextBlock; Avalonia ships no
/// equivalent, so this TextBlock subclass rebuilds its inlines from the plain
/// markdown text. Supports headings, lists, quotes, fenced code blocks,
/// separators, bold/italic/strikethrough, inline code, links (rendered as underlined
/// text), and embedded images. AI source citations such as [S1] are rendered as clickable buttons
/// through CitationAction. Text stays selectable like the reference.
/// </summary>
public sealed class KreaderMarkdownTextBlock : TextBlock
{
    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<KreaderMarkdownTextBlock, string?>(nameof(Markdown));

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    public static readonly StyledProperty<Action<string>?> CitationActionProperty =
        AvaloniaProperty.Register<KreaderMarkdownTextBlock, Action<string>?>(nameof(CitationAction));

    private static readonly Regex InlineTokenPattern = new(
        @"(!\[[^\]\n]*\]\([^)]*\)|\[[Ss]\d+\]|\*\*[^*]+\*\*|`[^`]+`|~~[^~]+~~|\*[^*]+\*|\[[^\]\n]+\]\([^)]*\))",
        RegexOptions.Compiled);
    private static readonly Regex ImagePattern = new(
        @"^\s*!\[(?<alt>[^\]]*)\]\((?<source>[^)\s\r\n]+)(?:\s*=\s*(?<width>\d+)(?:x(?<height>\d+))?)?\)\s*$",
        RegexOptions.Compiled);

    private static readonly Regex HeadingPattern = new(
        @"^(#{1,4})\s+(.*)$",
        RegexOptions.Compiled);

    private static readonly Regex ListPattern = new(
        @"^\s*(?:[-*+]|\d+[.)])\s+(.*)$",
        RegexOptions.Compiled);

    private static readonly Regex TaskListPattern = new(
        @"^\s*(?:[-*+])\s+\[(?<done>[ xX])\]\s+(?<text>.*)$",
        RegexOptions.Compiled);

    private static readonly Regex QuotePattern = new(
        @"^>\s?(.*)$",
        RegexOptions.Compiled);

    private static readonly Regex SeparatorPattern = new(
        @"^\s*(?:-{3,}|\*{3,}|_{3,})\s*$",
        RegexOptions.Compiled);

    private readonly List<Bitmap> _markdownBitmaps = [];

    public KreaderMarkdownTextBlock()
    {
        TextWrapping = TextWrapping.Wrap;
    }

    public Action<string>? CitationAction
    {
        get => GetValue(CitationActionProperty);
        set => SetValue(CitationActionProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // TextBlock.Text and Inlines share storage. Binding the markdown to
        // Text lets inline mutations clear the source and erase the answer.
        if (change.Property == MarkdownProperty || change.Property == CitationActionProperty)
            RebuildMarkdownInlines(Markdown);
    }

    private void RebuildMarkdownInlines(string? markdown)
    {
        foreach (var bitmap in _markdownBitmaps)
            bitmap.Dispose();
        _markdownBitmaps.Clear();
        Inlines = new InlineCollection();
        if (string.IsNullOrWhiteSpace(markdown)) return;

        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var paragraph = new StringBuilder();
        var inCodeBlock = false;
        var codeBuilder = new StringBuilder();

        void FlushParagraph()
        {
            if (paragraph.Length == 0) return;
            AppendInlineMarkup(paragraph.ToString());
            paragraph.Clear();
        }

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var trimmed = line.Trim();

            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                FlushParagraph();
                if (!inCodeBlock)
                {
                    inCodeBlock = true;
                    codeBuilder.Clear();
                }
                else
                {
                    inCodeBlock = false;
                    AppendCodeBlock(codeBuilder.ToString());
                    codeBuilder.Clear();
                }
                continue;
            }

            if (inCodeBlock)
            {
                codeBuilder.AppendLine(line);
                continue;
            }

            if (string.IsNullOrWhiteSpace(trimmed))
            {
                FlushParagraph();
                AddLineBreak();
                continue;
            }

            if (SeparatorPattern.IsMatch(trimmed))
            {
                FlushParagraph();
                AddSeparator();
                continue;
            }

            var heading = HeadingPattern.Match(trimmed);
            if (heading.Success)
            {
                FlushParagraph();
                AddHeading(heading.Groups[2].Value, heading.Groups[1].Value.Length);
                continue;
            }

            var quote = QuotePattern.Match(trimmed);
            if (quote.Success)
            {
                paragraph.Append("▎ ").Append(quote.Groups[1].Value).Append('\n');
                continue;
            }

            var task = TaskListPattern.Match(trimmed);
            if (task.Success)
            {
                paragraph.Append(task.Groups["done"].Value is "x" or "X" ? "☑ " : "☐ ")
                    .Append(task.Groups["text"].Value)
                    .Append('\n');
                continue;
            }

            var list = ListPattern.Match(trimmed);
            if (list.Success)
            {
                paragraph.Append("• ").Append(list.Groups[1].Value).Append('\n');
                continue;
            }

            paragraph.Append(line).Append('\n');
        }

        FlushParagraph();
        if (inCodeBlock && codeBuilder.Length > 0)
            AppendCodeBlock(codeBuilder.ToString());
    }

    private void AppendInlineMarkup(string text)
    {
        var matches = InlineTokenPattern.Matches(text);
        var position = 0;
        foreach (Match match in matches)
        {
            if (match.Index > position)
                Inlines?.Add(new Run(text[position..match.Index]));
            var token = match.Value;
            if (token.StartsWith("![", StringComparison.Ordinal))
            {
                var image = CreateMarkdownImage(token);
                Inlines?.Add(image is null
                    ? new Run(token)
                    : new InlineUIContainer(image));
            }
            else if (Regex.IsMatch(token, @"^\[[Ss]\d+\]$", RegexOptions.CultureInvariant))
            {
                var sourceId = token[1..^1].ToUpperInvariant();
                var citationButton = new Button
                {
                    Content = $"[{sourceId}]",
                    Focusable = true,
                    Padding = new Thickness(1, 0),
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center
                };
                citationButton.Classes.Add("readerAiCitationMarker");
                citationButton.Click += (_, _) => CitationAction?.Invoke(sourceId);
                Inlines?.Add(new InlineUIContainer(citationButton));
            }
            else if (token.StartsWith("**", StringComparison.Ordinal) && token.EndsWith("**", StringComparison.Ordinal))
            {
                Inlines?.Add(new Run(token[2..^2]) { FontWeight = FontWeight.Bold });
            }
            else if (token.StartsWith('`') && token.EndsWith('`') && token.Length >= 2)
            {
                Inlines?.Add(new Run(token[1..^1])
                {
                    FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"),
                    FontSize = FontSize - 1,
                    Background = new SolidColorBrush(Color.FromArgb(255, 242, 242, 240)),
                    Foreground = new SolidColorBrush(Color.FromArgb(255, 36, 36, 36))
                });
            }
            else if (token.StartsWith("~~", StringComparison.Ordinal) && token.EndsWith("~~", StringComparison.Ordinal))
            {
                Inlines?.Add(new Run(token[2..^2])
                {
                    TextDecorations = Avalonia.Media.TextDecorations.Strikethrough
                });
            }
            else if (token.StartsWith('*') && token.EndsWith('*') && token.Length >= 2)
            {
                Inlines?.Add(new Run(token[1..^1]) { FontStyle = FontStyle.Italic });
            }
            else if (token.StartsWith('[') && token.EndsWith(')'))
            {
                var separator = token.IndexOf("](", StringComparison.Ordinal);
                if (separator > 1)
                {
                    Inlines?.Add(new Run(token[1..separator])
                    {
                        TextDecorations = Avalonia.Media.TextDecorations.Underline
                    });
                }
                else
                {
                    Inlines?.Add(new Run(token));
                }
            }
            else
            {
                Inlines?.Add(new Run(token));
            }
            position = match.Index + match.Length;
        }
        if (position < text.Length)
            Inlines?.Add(new Run(text[position..]));
    }

    private Image? CreateMarkdownImage(string token)
    {
        var match = ImagePattern.Match(token);
        if (!match.Success) return null;

        var alt = match.Groups["alt"].Value;
        var source = match.Groups["source"].Value;
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

            if (bitmap is null) return null;
            _markdownBitmaps.Add(bitmap);
            var image = new Image
            {
                Source = bitmap,
                Stretch = Stretch.Uniform,
                MaxWidth = 520,
                MaxHeight = 360
            };
            if (int.TryParse(match.Groups["width"].Value, out var width) && width > 0)
                image.Width = width;
            if (int.TryParse(match.Groups["height"].Value, out var height) && height > 0)
                image.Height = height;
            ToolTip.SetTip(image, alt);
            return image;
        }
        catch
        {
            bitmap?.Dispose();
            return null;
        }
    }

    private void AddHeading(string text, int level)
    {
        var size = level switch
        {
            1 => 16d,
            2 => 15d,
            3 => 14d,
            _ => 13d
        };
        Inlines?.Add(new Run(text)
        {
            FontSize = size,
            FontWeight = FontWeight.SemiBold
        });
        AddLineBreak();
    }

    private void AppendCodeBlock(string code)
    {
        AddLineBreak();
        Inlines?.Add(new Run(code.TrimEnd('\n'))
        {
            FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"),
            FontSize = FontSize - 1,
            Background = new SolidColorBrush(Color.FromArgb(255, 242, 242, 240)),
            Foreground = new SolidColorBrush(Color.FromArgb(255, 36, 36, 36))
        });
        AddLineBreak();
    }

    private void AddSeparator()
    {
        Inlines?.Add(new Run("――――――――")
        {
            Foreground = new SolidColorBrush(Color.FromArgb(255, 213, 213, 209))
        });
        AddLineBreak();
    }

    private void AddLineBreak() => Inlines?.Add(new LineBreak());
}
