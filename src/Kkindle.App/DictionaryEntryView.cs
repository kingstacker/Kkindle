using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle;

/// <summary>Shares the same dictionary layout between settings and KReader lookup.</summary>
public sealed class DictionaryEntryView : Grid
{
    private IReadOnlyList<DictionaryEntry> _entries = [];
    private NativeWebViewReaderHost? _host;
    private readonly TextBlock _fallback = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private int _renderSequence;
    private bool _loadingHtml;
    public bool UseReaderTheme { get; set; }

    public event EventHandler<string>? TermRequested;

    public DictionaryEntryView()
    {
        Children.Add(new ScrollViewer { Content = _fallback });
        AttachedToVisualTree += (_, _) => RenderEntries();
        DetachedFromVisualTree += (_, _) => ReleaseBrowser();
    }

    public void SetEntries(IReadOnlyList<DictionaryEntry> entries)
    {
        _entries = entries;
        if (_host is { } previousHost) ((Control)previousHost.View).IsVisible = false;
        _fallback.Text = entries.Any(e => e.Html is not null) ? UiText.Localize("正在加载词条…") : PlainText();
        _fallback.IsVisible = true;
        IsVisible = entries.Count > 0;
        RenderEntries();
    }

    private async void RenderEntries()
    {
        var sequence = ++_renderSequence;
        if (!this.IsAttachedToVisualTree()) return;
        if (!_entries.Any(e => e.Html is not null))
        {
            if (_entries.Count > 0) ReleaseBrowser();
            return;
        }
        NativeWebViewReaderHost? host = null;
        try
        {
            if (_host is null)
            {
                host = new NativeWebViewReaderHost(); _host = host;
                host.NavigationStarting += (_, e) =>
                {
                    if (e.Request?.Scheme == "kkindle-dictionary")
                    {
                        e.Cancel = true;
                        var term = Uri.UnescapeDataString(e.Request.Query.TrimStart('?'));
                        if (term.Length is > 0 and <= 256)
                            Dispatcher.UIThread.Post(() => TermRequested?.Invoke(this, term));
                    }
                    // NativeWebView implements NavigateToString using a data URL on Windows.
                    else if (_loadingHtml && e.Request?.OriginalString.StartsWith("data:text/html", StringComparison.OrdinalIgnoreCase) == true) { }
                    else if (e.Request is { } uri && uri.Scheme != "about"
                        && uri.AbsoluteUri != "https://kkindle.invalid/dictionary/") e.Cancel = true;
                };
                host.NavigationCompleted += (_, e) =>
                {
                    if (!ReferenceEquals(_host, host)) return;
                    _loadingHtml = false;
                    if (e.IsSuccess) { _fallback.IsVisible = false; ((Control)host.View).IsVisible = true; }
                    else { ReleaseBrowser(); _fallback.Text = PlainText(); }
                };
                Children.Add((Control)host.View);
                }
            else host = _host;
            var palette = ReadPalette();
            var entries = _entries;
            var htmlTask = Task.Run(() => DictionaryHtmlRenderer.Render(entries, palette));
            // NativeWebView needs an attached adapter before accepting HTML navigation.
            await host.ReadyTask.WaitAsync(TimeSpan.FromSeconds(15));
            var html = await htmlTask;
            if (!ReferenceEquals(_host, host) || sequence != _renderSequence) return;
            _loadingHtml = true;
            ((Control)host.View).IsVisible = false;
            host.NavigateToString(html, new Uri("https://kkindle.invalid/dictionary/"));
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.WriteLine("Dictionary HTML view: " + exception);
            if (ReferenceEquals(_host, host) && sequence == _renderSequence) { ReleaseBrowser(); _fallback.Text = PlainText(); }
        }
    }

    private string PlainText() => string.Join("\n\n", _entries.Select(e => $"{e.Term} · {e.DictionaryName}\n{e.Definition}"));

    private DictionaryHtmlPalette ReadPalette()
    {
        string Color(string reader, string app, string fallback)
        {
            if (UseReaderTheme && this.FindResource(reader.Replace("Brush", "Color")) is Avalonia.Media.Color readerColor)
                return $"#{readerColor.R:x2}{readerColor.G:x2}{readerColor.B:x2}";
            var brush = this.FindResource(UseReaderTheme ? reader : app) as ISolidColorBrush;
            return brush is null ? fallback : $"#{brush.Color.R:x2}{brush.Color.G:x2}{brush.Color.B:x2}";
        }
        return new(Color("ReaderPageBrush", "PaperBrush", "#ffffff"), Color("ReaderInkBrush", "InkBrush", "#292d29"),
            Color("ReaderMutedBrush", "MutedInkBrush", "#606a60"), Color("ReaderHoverBrush", "SoftHoverBrush", "#e8ece6"),
            Color("ReaderBorderBrush", "HairlineBrush", "#cdd4c9"));
    }

    private void ReleaseBrowser()
    {
        if (_host is not { } host) return;
        _host = null;
        _loadingHtml = false;
        host.Dispose();
        Children.Remove((Control)host.View);
    }
}
