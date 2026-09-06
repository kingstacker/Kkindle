using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Kkindle.Core;
using System.Collections.ObjectModel;
using System.Text.RegularExpressions;

namespace Kkindle;

public sealed record ReaderSearchHighlightRange(int Start, int Length);

public sealed class ReaderSearchResultViewModel : ObservableObject
{
    private static readonly IBrush SearchResultBorderBrush = new SolidColorBrush(Colors.Black);
    private bool _isSelected;

    public ReaderSearchResultViewModel(
        string title,
        string excerpt,
        int chapterIndex,
        string chapterPath,
        string? target = null,
        int? pageNumber = null,
        string? query = null)
    {
        Title = title;
        var presentation = string.IsNullOrWhiteSpace(query)
            ? (excerpt, (IReadOnlyList<ReaderSearchHighlightRange>)Array.Empty<ReaderSearchHighlightRange>())
            : ReaderSearchPresentation.CreateSnippet(excerpt, query);
        Excerpt = presentation.Item1;
        ChapterIndex = chapterIndex;
        ChapterPath = chapterPath;
        Target = target;
        PageNumber = pageNumber;
        Query = query;
        ExcerptHighlightRanges = presentation.Item2;
    }

    public ReaderSearchResultViewModel(
        BookContentChunk source,
        string query,
        string? target = null)
    {
        var presentation = ReaderSearchPresentation.CreateSnippet(source.Content, query);
        // A title-only hit has no matching characters in the body chunk, so
        // showing the body's opening text would produce a result card with no
        // visible search term. Keep the result, but use the matching title as
        // its excerpt so every card exposes the reason it was returned.
        if (presentation.HighlightRanges.Count == 0)
        {
            var titlePresentation = ReaderSearchPresentation.CreateSnippet(
                source.ChapterTitle,
                query);
            if (titlePresentation.HighlightRanges.Count > 0)
                presentation = titlePresentation;
        }
        Title = source.ChapterTitle;
        Excerpt = presentation.Snippet;
        ChapterIndex = source.ChapterIndex;
        ChapterPath = source.ChapterPath;
        Target = target;
        Query = query;
        Source = source;
        ExcerptHighlightRanges = presentation.HighlightRanges;
    }

    public string Title { get; }
    public string Excerpt { get; }
    public int ChapterIndex { get; }
    public string ChapterPath { get; }
    public string? Target { get; }
    public int? PageNumber { get; }
    public string? Query { get; }
    public BookContentChunk? Source { get; }
    public IReadOnlyList<ReaderSearchHighlightRange> ExcerptHighlightRanges { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (!SetProperty(ref _isSelected, value)) return;
            OnPropertyChanged(nameof(ResultBorderThickness));
        }
    }

    public IBrush ResultBorderBrush => SearchResultBorderBrush;

    public double ResultBorderThickness => IsSelected ? 2 : 1;
}

internal static class ReaderSearchPresentation
{
    public static (string Snippet, IReadOnlyList<ReaderSearchHighlightRange> HighlightRanges) CreateSnippet(
        string? content,
        string? query)
    {
        var normalized = Regex.Replace(content ?? string.Empty, @"\s+", " ").Trim();
        var normalizedQuery = Regex.Replace(query?.Trim() ?? string.Empty, @"\s+", " ").Trim();
        if (normalized.Length == 0 || normalizedQuery.Length == 0)
            return (string.Empty, Array.Empty<ReaderSearchHighlightRange>());

        const int maximumLength = 150;
        var match = normalized.IndexOf(normalizedQuery, StringComparison.CurrentCultureIgnoreCase);
        var runs = normalizedQuery.Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var useRuns = match < 0 && runs.Length > 0;
        if (useRuns)
        {
            match = runs
                .Select(term => normalized.IndexOf(term, StringComparison.CurrentCultureIgnoreCase))
                .Where(index => index >= 0)
                .DefaultIfEmpty(-1)
                .Min();
        }
        var hasMatch = match >= 0;
        if (match < 0) match = 0;

        // Keep the hit near the beginning of the excerpt. The result row is
        // intentionally limited to four lines; shifting a late hit backward
        // to include the tail can push the actual keyword below that visual
        // limit, making a valid result look unrelated.
        var start = Math.Max(0, match - 36);
        var length = Math.Min(maximumLength, normalized.Length - start);
        var snippet = normalized.Substring(start, length);
        var prefix = start > 0 ? "…" : string.Empty;
        var suffix = start + length < normalized.Length ? "…" : string.Empty;
        var display = prefix + snippet + suffix;

        var rawRanges = new List<ReaderSearchHighlightRange>();
        if (useRuns)
        {
            foreach (var run in runs.Distinct(StringComparer.CurrentCultureIgnoreCase))
                AddSnippetOccurrences(rawRanges, normalized, run, start, length);
        }
        else
        {
            AddSnippetOccurrences(rawRanges, normalized, normalizedQuery, start, length);
        }

        // A query longer than the visible 150-character window still needs a
        // visible highlight. This is also the fallback for a title-only hit
        // where the query is not present in the body chunk itself.
        if (rawRanges.Count == 0 && hasMatch)
        {
            var visibleStart = Math.Max(match, start);
            var visibleEnd = Math.Min(match + normalizedQuery.Length, start + length);
            if (visibleEnd > visibleStart)
                rawRanges.Add(new ReaderSearchHighlightRange(
                    visibleStart - start,
                    visibleEnd - visibleStart));
        }

        var offset = prefix.Length;
        var ranges = MergeRanges(rawRanges.Select(range => new ReaderSearchHighlightRange(
            range.Start + offset,
            range.Length)));
        return (display, ranges);
    }

    public static IReadOnlyList<ReaderSearchHighlightRange> FindTermOccurrences(
        string? text,
        string? query)
    {
        var value = text ?? string.Empty;
        var normalizedQuery = Regex.Replace(query?.Trim() ?? string.Empty, @"\s+", " ").Trim();
        if (value.Length == 0 || normalizedQuery.Length == 0)
            return Array.Empty<ReaderSearchHighlightRange>();

        var ranges = new List<ReaderSearchHighlightRange>();
        foreach (var term in normalizedQuery.Split(
                     ' ',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                 .Distinct(StringComparer.CurrentCultureIgnoreCase))
        {
            AddOccurrences(ranges, value, term);
        }
        return MergeRanges(ranges);
    }

    private static void AddSnippetOccurrences(
        List<ReaderSearchHighlightRange> ranges,
        string normalized,
        string term,
        int windowStart,
        int windowLength)
    {
        if (term.Length == 0) return;
        var windowEnd = windowStart + windowLength;
        var searchStart = windowStart;
        while (searchStart < windowEnd)
        {
            var index = normalized.IndexOf(
                term,
                searchStart,
                StringComparison.CurrentCultureIgnoreCase);
            if (index < 0 || index >= windowEnd) break;
            var visibleEnd = Math.Min(index + term.Length, windowEnd);
            if (visibleEnd > index)
                ranges.Add(new ReaderSearchHighlightRange(
                    index - windowStart,
                    visibleEnd - index));
            searchStart = index + Math.Max(1, term.Length);
        }
    }

    private static void AddOccurrences(
        List<ReaderSearchHighlightRange> ranges,
        string text,
        string term)
    {
        var start = 0;
        while (start < text.Length)
        {
            var index = text.IndexOf(term, start, StringComparison.CurrentCultureIgnoreCase);
            if (index < 0) break;
            ranges.Add(new ReaderSearchHighlightRange(index, term.Length));
            start = index + Math.Max(1, term.Length);
        }
    }

    private static IReadOnlyList<ReaderSearchHighlightRange> MergeRanges(
        IEnumerable<ReaderSearchHighlightRange> ranges)
    {
        var merged = new List<ReaderSearchHighlightRange>();
        foreach (var range in ranges
                     .Where(item => item.Start >= 0 && item.Length > 0)
                     .OrderBy(item => item.Start))
        {
            if (merged.Count > 0
                && range.Start <= merged[^1].Start + merged[^1].Length)
            {
                var previous = merged[^1];
                merged[^1] = new ReaderSearchHighlightRange(
                    previous.Start,
                    Math.Max(
                        previous.Start + previous.Length,
                        range.Start + range.Length) - previous.Start);
            }
            else
            {
                merged.Add(range);
            }
        }
        return merged;
    }
}

/// <summary>
/// A TextBlock that paints the query terms of a whole-book search result in
/// black-on-white inverse text, mirroring the WinUI reference's
/// TextHighlighters on the result title and snippet.
/// </summary>
public sealed class ReaderSearchHighlightTextBlock : TextBlock
{
    public static readonly StyledProperty<string?> SourceProperty =
        AvaloniaProperty.Register<ReaderSearchHighlightTextBlock, string?>(nameof(Source));

    public static readonly StyledProperty<string?> QueryProperty =
        AvaloniaProperty.Register<ReaderSearchHighlightTextBlock, string?>(nameof(Query));

    public static readonly StyledProperty<IReadOnlyList<ReaderSearchHighlightRange>?> HighlightRangesProperty =
        AvaloniaProperty.Register<ReaderSearchHighlightTextBlock, IReadOnlyList<ReaderSearchHighlightRange>?>(
            nameof(HighlightRanges));

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty
            || change.Property == QueryProperty
            || change.Property == HighlightRangesProperty)
        {
            RebuildHighlightedInlines();
        }
    }

    public string? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public string? Query
    {
        get => GetValue(QueryProperty);
        set => SetValue(QueryProperty, value);
    }

    public IReadOnlyList<ReaderSearchHighlightRange>? HighlightRanges
    {
        get => GetValue(HighlightRangesProperty);
        set => SetValue(HighlightRangesProperty, value);
    }

    private void RebuildHighlightedInlines()
    {
        var text = Source ?? string.Empty;
        var query = Query?.Trim() ?? string.Empty;
        if (Inlines is null) return;
        Inlines.Clear();
        if (query.Length == 0)
        {
            Inlines.Add(new Run { Text = text });
            return;
        }

        var highlight = new SolidColorBrush(Color.FromRgb(255, 255, 255));
        var highlightBackground = new SolidColorBrush(Color.FromRgb(0, 0, 0));
        var ranges = HighlightRanges ?? ReaderSearchPresentation.FindTermOccurrences(text, query);
        var cursor = 0;
        foreach (var range in ranges)
        {
            var start = Math.Clamp(range.Start, 0, text.Length);
            var end = Math.Clamp(range.Start + range.Length, start, text.Length);
            if (start < cursor) start = cursor;
            if (start > cursor)
                Inlines.Add(new Run { Text = text[cursor..start] });
            if (end <= start) continue;
            Inlines.Add(new Run
            {
                Text = text[start..end],
                Foreground = highlight,
                Background = highlightBackground
            });
            cursor = end;
        }
        if (cursor < text.Length)
            Inlines.Add(new Run { Text = text[cursor..] });
    }
}

public sealed class ReaderAiSourceViewModel : ObservableObject, IDisposable
{
    public ReaderAiSourceViewModel(BookContentChunk chunk)
        : this(chunk, string.Empty, chunk.Content)
    {
    }

    public ReaderAiSourceViewModel(ReaderAiSource source)
        : this(source.Chunk, source.Id, source.Content)
    {
    }

    private ReaderAiSourceViewModel(BookContentChunk chunk, string sourceId, string content)
    {
        UiText.LanguageChanged += OnLanguageChanged;
        Chunk = chunk;
        SourceId = sourceId;
        Content = content;
    }

    public ReaderAiSourceViewModel(PdfPageText page, string sourceId = "S1")
    {
        UiText.LanguageChanged += OnLanguageChanged;
        Page = page;
        SourceId = sourceId;
        Content = page.Text;
    }

    public BookContentChunk? Chunk { get; }
    public PdfPageText? Page { get; }
    public string SourceId { get; }
    public string Content { get; }
    public string Label
    {
        get
        {
            var label = Chunk is { } chunk
                ? UiText.Get("{0} · 片段 {1}", chunk.ChapterTitle, chunk.ChunkIndex + 1)
                : Page is { } page
                    ? UiText.Get("第 {0} 页 · {1}", page.PageNumber, CreateExcerpt(page.Text, 100))
                    : string.Empty;
            return SourceId.Length == 0 ? label : $"[{SourceId}] {label}";
        }
    }

    private void OnLanguageChanged(object? sender, EventArgs e) =>
        OnPropertyChanged(nameof(Label));

    public void Dispose() =>
        UiText.LanguageChanged -= OnLanguageChanged;

    private static string CreateExcerpt(string value, int length)
    {
        var normalized = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= length ? normalized : normalized[..length] + "…";
    }
}

public sealed class ReaderAiMessageViewModel : ObservableObject, IDisposable
{
    private string _content;
    private string _reasoning;
    private bool _isReasoningExpanded;
    private bool _isThinking;
    private double _thinkingRotation;
    private bool _isSourcesExpanded;

    public ReaderAiMessageViewModel(
        string role,
        string content = "",
        string reasoning = "",
        Action<ReaderAiSourceViewModel>? citationAction = null)
    {
        UiText.LanguageChanged += OnLanguageChanged;
        Role = role;
        _content = content;
        _reasoning = reasoning;
        CitationAction = sourceId =>
        {
            var source = Sources.FirstOrDefault(item =>
                string.Equals(item.SourceId, sourceId, StringComparison.OrdinalIgnoreCase));
            if (source is not null) citationAction?.Invoke(source);
        };
    }

    public string Role { get; }
    public Action<string>? CitationAction { get; }

    public bool IsUser => Role.Equals("user", StringComparison.OrdinalIgnoreCase);

    public bool IsAssistant => !IsUser;

    public string RoleLabel => IsUser ? UiText.Get("你") : "Kreader AI";

    public IBrush BubbleBackground => IsUser ? new SolidColorBrush(Color.FromRgb(245, 245, 245)) : Brushes.Transparent;

    public IBrush BubbleForeground => new SolidColorBrush(Color.FromRgb(25, 25, 25));

    public IBrush BorderBrush => Brushes.Transparent;

    public Avalonia.Layout.HorizontalAlignment BubbleAlignment => IsUser
        ? Avalonia.Layout.HorizontalAlignment.Right
        : Avalonia.Layout.HorizontalAlignment.Stretch;

    public IBrush RoleBrush => Role.Equals("user", StringComparison.OrdinalIgnoreCase)
        ? new SolidColorBrush(Color.FromRgb(75, 75, 75))
        : new SolidColorBrush(Color.FromRgb(26, 26, 26));

    public string Content
    {
        get => _content;
        private set => SetProperty(ref _content, value);
    }

    public string Reasoning
    {
        get => _reasoning;
        private set => SetProperty(ref _reasoning, value);
    }

    public bool HasReasoning => !string.IsNullOrWhiteSpace(Reasoning);

    public bool IsReasoningVisible => HasReasoning && _isReasoningExpanded;

    public string ReasoningToggleLabel =>
        _isReasoningExpanded ? UiText.Get("收起思考过程") : UiText.Get("展开思考过程");

    public bool IsThinking
    {
        get => _isThinking;
        private set => SetProperty(ref _isThinking, value);
    }

    public bool IsThinkingRowVisible => IsThinking || HasReasoning;

    public bool IsThinkingLabelVisible => IsThinking && !HasReasoning;

    public string ThinkingLabel => UiText.Get("思考中…");

    public double ThinkingRotation
    {
        get => _thinkingRotation;
        private set => SetProperty(ref _thinkingRotation, value);
    }

    public ObservableCollection<ReaderAiSourceViewModel> Sources { get; } = [];

    private bool _isStreaming;
    private bool _canRetry;
    private string _status = string.Empty;
    public string Status { get => _status; set { if (SetProperty(ref _status, value)) OnPropertyChanged(nameof(HasStatus)); } }
    public bool HasStatus => !string.IsNullOrWhiteSpace(Status);
    public bool CanCopy => IsAssistant && !_isStreaming && !string.IsNullOrWhiteSpace(Content);
    public bool CanRetry { get => _canRetry; set => SetProperty(ref _canRetry, value); }
    public Action? RetryAction { get; set; }

    public bool HasSources => Sources.Count > 0;

    public bool IsSourcesExpanded => _isSourcesExpanded;

    public string SourcesToggleGlyph => _isSourcesExpanded ? "▾" : "▸";

    public void ToggleReasoning()
    {
        if (!HasReasoning) return;
        _isReasoningExpanded = !_isReasoningExpanded;
        OnPropertyChanged(nameof(IsReasoningVisible));
        OnPropertyChanged(nameof(ReasoningToggleLabel));
    }

    public void SetThinking(bool thinking)
    {
        var next = !IsUser && thinking;
        if (IsThinking == next) return;
        IsThinking = next;
        OnPropertyChanged(nameof(IsThinkingRowVisible));
        OnPropertyChanged(nameof(IsThinkingLabelVisible));
    }

    public void SetThinkingRotation(double angle) => ThinkingRotation = angle;

    public void SetSources(IEnumerable<ReaderAiSourceViewModel> sources)
    {
        foreach (var source in Sources) source.Dispose();
        Sources.Clear();
        foreach (var source in sources)
            Sources.Add(source);

        _isSourcesExpanded = false;
        OnPropertyChanged(nameof(HasSources));
        OnPropertyChanged(nameof(IsSourcesExpanded));
        OnPropertyChanged(nameof(SourcesToggleGlyph));
    }

    public void ToggleSources()
    {
        if (!HasSources) return;

        _isSourcesExpanded = !_isSourcesExpanded;
        OnPropertyChanged(nameof(IsSourcesExpanded));
        OnPropertyChanged(nameof(SourcesToggleGlyph));
    }

    public void Update(string content, string reasoning, bool isStreaming)
    {
        _isStreaming = isStreaming;
        Content = content;
        Reasoning = reasoning;
        SetThinking(isStreaming && string.IsNullOrWhiteSpace(Content));
        if (!HasReasoning) _isReasoningExpanded = false;
        OnPropertyChanged(nameof(HasReasoning));
        OnPropertyChanged(nameof(IsThinkingRowVisible));
        OnPropertyChanged(nameof(IsThinkingLabelVisible));
        OnPropertyChanged(nameof(IsReasoningVisible));
        OnPropertyChanged(nameof(ReasoningToggleLabel));
        OnPropertyChanged(nameof(CanCopy));
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(RoleLabel));
        OnPropertyChanged(nameof(ReasoningToggleLabel));
        OnPropertyChanged(nameof(ThinkingLabel));
    }

    public void Dispose()
    {
        UiText.LanguageChanged -= OnLanguageChanged;
        foreach (var source in Sources) source.Dispose();
        Sources.Clear();
        RetryAction = null;
    }
}
