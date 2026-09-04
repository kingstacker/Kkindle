using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Kkindle;

/// <summary>
/// Lightweight Markdown renderer for AI assistant bubbles. The WinUI
/// reference rendered answers with MarkdownRichTextBlock; Avalonia ships no
/// equivalent, so this TextBlock subclass rebuilds its inlines from the plain
/// markdown text. Supports headings, lists, quotes, fenced code blocks,
/// separators, bold/italic, inline code and links (rendered as underlined
/// text). AI source citations such as [S1] are rendered as clickable buttons
/// through CitationAction. Text stays selectable like the reference.
/// </summary>
public sealed class KreaderMarkdownTextBlock : TextBlock
{
    public static readonly StyledProperty<Action<string>?> CitationActionProperty =
        AvaloniaProperty.Register<KreaderMarkdownTextBlock, Action<string>?>(nameof(CitationAction));

    private static readonly Regex InlineTokenPattern = new(
        @"(\[[Ss]\d+\]|\*\*[^*]+\*\*|`[^`]+`|\*[^*]+\*|\[[^\]\n]+\]\([^)\n]+\))",
        RegexOptions.Compiled);

    private static readonly Regex HeadingPattern = new(
        @"^(#{1,4})\s+(.*)$",
        RegexOptions.Compiled);

    private static readonly Regex ListPattern = new(
        @"^\s*(?:[-*+]|\d+[.)])\s+(.*)$",
        RegexOptions.Compiled);

    private static readonly Regex QuotePattern = new(
        @"^>\s?(.*)$",
        RegexOptions.Compiled);

    private static readonly Regex SeparatorPattern = new(
        @"^\s*(?:-{3,}|\*{3,}|_{3,})\s*$",
        RegexOptions.Compiled);

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
        if (change.Property == TextProperty || change.Property == CitationActionProperty)
            RebuildMarkdownInlines(change.Property == TextProperty
                ? change.GetNewValue<string?>()
                : Text);
    }

    private void RebuildMarkdownInlines(string? markdown)
    {
        Inlines?.Clear();
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
            if (Regex.IsMatch(token, @"^\[[Ss]\d+\]$", RegexOptions.CultureInvariant))
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
