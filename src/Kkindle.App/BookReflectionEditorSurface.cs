using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Kkindle.Core;

namespace Kkindle;

/// <summary>
/// A single-surface Markdown editor. Each committed line remains editable,
/// but its Markdown block marker is rendered as formatting after Enter rather
/// than staying visible as source text.
/// </summary>
internal sealed class BookReflectionEditorSurface : Border
{
    private static readonly Regex HeadingPattern = new(
        "^\\s*(?<marks>#{1,4})(?:\\s*)(?<text>.*)$",
        RegexOptions.Compiled);
    private static readonly Regex QuotePattern = new(
        "^\\s*>\\s?(?<text>.*)$",
        RegexOptions.Compiled);
    private static readonly Regex TaskPattern = new(
        "^\\s*[-*+]\\s+\\[(?<done>[ xX])\\]\\s+(?<text>.*)$",
        RegexOptions.Compiled);
    private static readonly Regex UnorderedPattern = new(
        "^\\s*[-*+]\\s+(?<text>.*)$",
        RegexOptions.Compiled);
    private static readonly Regex OrderedPattern = new(
        "^\\s*(?<number>\\d+)[.)]\\s+(?<text>.*)$",
        RegexOptions.Compiled);
    private static readonly Regex BoldPattern = new(
        "^\\*\\*(?<text>.*)\\*\\*$",
        RegexOptions.Compiled);
    private static readonly Regex ItalicPattern = new(
        "^\\*(?<text>.*)\\*$",
        RegexOptions.Compiled);
    private static readonly Regex StrikePattern = new(
        "^~~(?<text>.*)~~$",
        RegexOptions.Compiled);
    private static readonly Regex SeparatorPattern = new(
        "^\\s*(?:-{3,}|\\*{3,}|_{3,})\\s*$",
        RegexOptions.Compiled);

    private readonly StackPanel _lineStack;
    private readonly List<LineState> _lines = [];
    private TextBox? _activeEditor;
    private bool _suppressTextChanged;

    public event EventHandler? ContentChanged;

    public string Markdown => string.Join("\n", _lines.Select(line => line.ToMarkdown()));

    public BookReflectionEditorSurface(string initialContent)
    {
        Background = AppAppearanceResources.GetBrush("PaperBrush");
        BorderBrush = AppAppearanceResources.GetBrush("HairlineBrush");
        BorderThickness = new Thickness(1, 1, 1, 0);
        Padding = new Thickness(14);
        ClipToBounds = true;

        _lineStack = new StackPanel
        {
            Spacing = 2,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var scrollViewer = new ScrollViewer
        {
            Background = Brushes.Transparent,
            Content = _lineStack,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Top
        };
        ScrollViewer.SetVerticalScrollBarVisibility(scrollViewer, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(scrollViewer, ScrollBarVisibility.Disabled);
        Child = scrollViewer;

        Load(initialContent ?? string.Empty);
    }

    public void FocusEditor()
    {
        var editor = _activeEditor ?? _lineStack.Children.OfType<TextBox>().LastOrDefault();
        if (editor is null) return;

        _activeEditor = editor;
        editor.Focus();
        editor.CaretIndex = editor.Text?.Length ?? 0;
        editor.BringIntoView();
    }

    public void ApplyMarkdownShortcut(string tag)
    {
        var editor = _activeEditor ?? _lineStack.Children.OfType<TextBox>().LastOrDefault();
        if (editor is null || editor.Tag is not LineState state) return;

        var source = editor.Text ?? string.Empty;
        var selectionStart = Math.Clamp(editor.SelectionStart, 0, source.Length);
        var selectionEnd = Math.Clamp(editor.SelectionEnd, 0, source.Length);
        var start = Math.Min(selectionStart, selectionEnd);
        var end = Math.Max(selectionStart, selectionEnd);
        var selected = source[start..end];

        if (TryCreateBlockState(tag, selected, out var blockState))
        {
            if (string.IsNullOrEmpty(selected))
                blockState.Content = PlaceholderFor(tag);
            ReplaceLine(editor, blockState);
            if (string.IsNullOrEmpty(selected))
                SelectContent(editor, blockState.Content.Length);
            RaiseContentChanged();
            return;
        }

        var replacement = tag switch
        {
            "bold" => WrapInline(selected, "**", UiText.Get("粗体")),
            "italic" => WrapInline(selected, "*", UiText.Get("斜体")),
            "strikethrough" => WrapInline(selected, "~~", UiText.Get("删除线")),
            "inline-code" => WrapInline(selected, "`", UiText.Get("代码")),
            "code-block" => string.IsNullOrEmpty(selected)
                ? $"```\n{UiText.Get("代码")}\n```"
                : $"```\n{selected}\n```",
            "link" => WrapLink(selected),
            _ => selected
        };

        state.Kind = MarkdownLineKind.Paragraph;
        state.Content = source.Remove(start, end - start).Insert(start, replacement);
        ReplaceLine(editor, state, state.Content.Length);
        RaiseContentChanged();
    }

    private void Load(string markdown)
    {
        var sourceLines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        if (sourceLines.Length == 0)
            sourceLines = [string.Empty];

        foreach (var sourceLine in sourceLines)
            AddLine(ParseLine(sourceLine));

        if (_lines.Count == 0)
            AddLine(new LineState());
    }

    private void AddLine(LineState state, int? index = null)
    {
        _lines.Insert(index ?? _lines.Count, state);
        var editor = CreateLineEditor(state);
        if (index is { } insertionIndex)
            _lineStack.Children.Insert(insertionIndex, editor);
        else
            _lineStack.Children.Add(editor);
    }

    private TextBox CreateLineEditor(LineState state)
    {
        var editor = new TextBox
        {
            Tag = state,
            Text = DisplayText(state),
            AcceptsReturn = false,
            TextWrapping = TextWrapping.Wrap,
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0, 4, 0, 4),
            MinHeight = 30,
            FontFamily = new FontFamily("fonts:Kkindle#KingHwaOldSong"),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            Foreground = AppAppearanceResources.GetBrush("InkBrush")
        };
        editor.Classes.Add("bookReflectionEditor");
        ApplyLineStyle(editor, state);
        editor.GotFocus += (_, _) => _activeEditor = editor;
        editor.TextChanged += LineEditor_TextChanged;
        editor.AddHandler(InputElement.KeyDownEvent, LineEditor_KeyDown, RoutingStrategies.Tunnel);
        return editor;
    }

    private void LineEditor_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_suppressTextChanged || sender is not TextBox { Tag: LineState state } editor)
            return;

        state.Content = ContentFromDisplay(state, editor.Text ?? string.Empty);
        RaiseContentChanged();
    }

    private void LineEditor_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox editor || editor.Tag is not LineState state)
            return;

        var source = editor.Text ?? string.Empty;
        var caret = Math.Clamp(editor.CaretIndex, 0, source.Length);
        var before = source[..caret];
        var after = source[caret..];
        var index = _lineStack.Children.IndexOf(editor);
        if (index < 0 || index >= _lines.Count)
            return;

        var committed = state.Kind == MarkdownLineKind.Paragraph
            ? ParseLine(before)
            : state with { Content = ContentFromDisplay(state, before) };
        var next = ParseLine(after);

        _lines[index] = committed;
        ReplaceLine(editor, committed);
        AddLine(next, index + 1);
        var nextEditor = (TextBox)_lineStack.Children[index + 1];
        _activeEditor = nextEditor;
        nextEditor.Focus();
        nextEditor.CaretIndex = 0;
        nextEditor.BringIntoView();
        e.Handled = true;
        RaiseContentChanged();
    }

    private void ReplaceLine(TextBox editor, LineState state, int? caret = null)
    {
        var index = _lineStack.Children.IndexOf(editor);
        if (index >= 0 && index < _lines.Count)
            _lines[index] = state;

        _suppressTextChanged = true;
        try
        {
            editor.Tag = state;
            editor.Text = DisplayText(state);
            ApplyLineStyle(editor, state);
            if (caret is { } caretIndex)
                editor.CaretIndex = Math.Clamp(caretIndex, 0, editor.Text?.Length ?? 0);
        }
        finally
        {
            _suppressTextChanged = false;
        }
    }

    private void ApplyLineStyle(TextBox editor, LineState state)
    {
        editor.FontSize = state.Kind switch
        {
            MarkdownLineKind.Heading1 => 22,
            MarkdownLineKind.Heading2 => 18,
            MarkdownLineKind.Heading3 => 16,
            MarkdownLineKind.Heading4 => 15,
            _ => 15
        };
        editor.FontWeight = state.Kind is MarkdownLineKind.Heading1
            or MarkdownLineKind.Heading2
            or MarkdownLineKind.Heading3
            or MarkdownLineKind.Heading4
            or MarkdownLineKind.Bold
            ? FontWeight.SemiBold
            : FontWeight.Normal;
        editor.FontStyle = state.Kind == MarkdownLineKind.Italic
            ? FontStyle.Italic
            : FontStyle.Normal;
        editor.Foreground = state.Kind == MarkdownLineKind.Separator
            ? AppAppearanceResources.GetBrush("MutedInkBrush")
            : AppAppearanceResources.GetBrush("InkBrush");
        editor.MinHeight = state.Kind switch
        {
            MarkdownLineKind.Heading1 => 38,
            MarkdownLineKind.Heading2 => 34,
            _ => 30
        };
    }

    private static LineState ParseLine(string source)
    {
        source = source.Replace("\r", string.Empty, StringComparison.Ordinal);
        var heading = HeadingPattern.Match(source);
        if (heading.Success)
        {
            var level = heading.Groups["marks"].Value.Length;
            return new LineState
            {
                Kind = level switch
                {
                    1 => MarkdownLineKind.Heading1,
                    2 => MarkdownLineKind.Heading2,
                    3 => MarkdownLineKind.Heading3,
                    _ => MarkdownLineKind.Heading4
                },
                Content = heading.Groups["text"].Value
            };
        }

        var quote = QuotePattern.Match(source);
        if (quote.Success)
            return new LineState { Kind = MarkdownLineKind.Quote, Content = quote.Groups["text"].Value };

        var task = TaskPattern.Match(source);
        if (task.Success)
        {
            return new LineState
            {
                Kind = MarkdownLineKind.Task,
                Content = task.Groups["text"].Value,
                TaskDone = task.Groups["done"].Value is "x" or "X"
            };
        }

        var unordered = UnorderedPattern.Match(source);
        if (unordered.Success)
            return new LineState { Kind = MarkdownLineKind.Unordered, Content = unordered.Groups["text"].Value };

        var ordered = OrderedPattern.Match(source);
        if (ordered.Success)
        {
            return new LineState
            {
                Kind = MarkdownLineKind.Ordered,
                Content = ordered.Groups["text"].Value,
                OrderedNumber = ordered.Groups["number"].Value
            };
        }

        if (SeparatorPattern.IsMatch(source))
            return new LineState { Kind = MarkdownLineKind.Separator };

        var bold = BoldPattern.Match(source);
        if (bold.Success)
            return new LineState { Kind = MarkdownLineKind.Bold, Content = bold.Groups["text"].Value };

        var strike = StrikePattern.Match(source);
        if (strike.Success)
            return new LineState { Kind = MarkdownLineKind.Strikethrough, Content = strike.Groups["text"].Value };

        var italic = ItalicPattern.Match(source);
        if (italic.Success && italic.Groups["text"].Value.Length > 0)
            return new LineState { Kind = MarkdownLineKind.Italic, Content = italic.Groups["text"].Value };

        return new LineState { Content = source };
    }

    private static string DisplayText(LineState state) => state.Kind switch
    {
        MarkdownLineKind.Quote => "▎ " + state.Content,
        MarkdownLineKind.Unordered => "• " + state.Content,
        MarkdownLineKind.Ordered => $"{state.OrderedNumber}. {state.Content}",
        MarkdownLineKind.Task => (state.TaskDone ? "☑ " : "☐ ") + state.Content,
        MarkdownLineKind.Separator => "────────",
        _ => state.Content
    };

    private static string ContentFromDisplay(LineState state, string displayed)
    {
        var prefix = state.Kind switch
        {
            MarkdownLineKind.Quote => "▎ ",
            MarkdownLineKind.Unordered => "• ",
            MarkdownLineKind.Ordered => $"{state.OrderedNumber}. ",
            MarkdownLineKind.Task => state.TaskDone ? "☑ " : "☐ ",
            MarkdownLineKind.Separator => "────────",
            _ => string.Empty
        };
        return prefix.Length > 0 && displayed.StartsWith(prefix, StringComparison.Ordinal)
            ? displayed[prefix.Length..]
            : displayed;
    }

    private static bool TryCreateBlockState(string tag, string selected, out LineState state)
    {
        state = new LineState();
        var kind = tag switch
        {
            "heading1" => MarkdownLineKind.Heading1,
            "heading2" => MarkdownLineKind.Heading2,
            "quote" => MarkdownLineKind.Quote,
            "unordered" => MarkdownLineKind.Unordered,
            "ordered" => MarkdownLineKind.Ordered,
            "task" => MarkdownLineKind.Task,
            "separator" => MarkdownLineKind.Separator,
            _ => MarkdownLineKind.Paragraph
        };
        if (kind == MarkdownLineKind.Paragraph)
            return false;

        state.Kind = kind;
        state.Content = selected;
        return true;
    }

    private static string PlaceholderFor(string tag) => tag switch
    {
        "heading1" => UiText.Get("标题"),
        "heading2" => UiText.Get("小标题"),
        "quote" => UiText.Get("引用内容"),
        "unordered" or "ordered" => UiText.Get("列表项"),
        "task" => UiText.Get("待办事项"),
        _ => string.Empty
    };

    private static string WrapInline(string selected, string marker, string placeholder) =>
        marker + (string.IsNullOrEmpty(selected) ? placeholder : selected) + marker;

    private static string WrapLink(string selected)
    {
        var label = string.IsNullOrEmpty(selected) ? UiText.Get("链接文字") : selected;
        return $"[{label}](https://example.com)";
    }

    private void SelectContent(TextBox editor, int length)
    {
        editor.SelectionStart = 0;
        editor.SelectionEnd = Math.Clamp(length, 0, editor.Text?.Length ?? 0);
    }

    private void RaiseContentChanged() => ContentChanged?.Invoke(this, EventArgs.Empty);

    private enum MarkdownLineKind
    {
        Paragraph,
        Heading1,
        Heading2,
        Heading3,
        Heading4,
        Bold,
        Italic,
        Strikethrough,
        Quote,
        Unordered,
        Ordered,
        Task,
        Separator
    }

    private sealed record class LineState
    {
        public MarkdownLineKind Kind { get; set; }
        public string Content { get; set; } = string.Empty;
        public bool TaskDone { get; set; }
        public string OrderedNumber { get; set; } = "1";

        public string ToMarkdown() => Kind switch
        {
            MarkdownLineKind.Heading1 => "# " + Content,
            MarkdownLineKind.Heading2 => "## " + Content,
            MarkdownLineKind.Heading3 => "### " + Content,
            MarkdownLineKind.Heading4 => "#### " + Content,
            MarkdownLineKind.Bold => "**" + Content + "**",
            MarkdownLineKind.Italic => "*" + Content + "*",
            MarkdownLineKind.Strikethrough => "~~" + Content + "~~",
            MarkdownLineKind.Quote => "> " + Content,
            MarkdownLineKind.Unordered => "- " + Content,
            MarkdownLineKind.Ordered => OrderedNumber + ". " + Content,
            MarkdownLineKind.Task => $"- [{(TaskDone ? "x" : " ")}] {Content}",
            MarkdownLineKind.Separator => "---",
            _ => Content
        };
    }
}
