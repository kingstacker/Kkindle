using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using CSharpMath.Atom;
using CSharpMath.Avalonia;

namespace Kkindle;

public sealed partial class KreaderMarkdownTextBlock
{
    // Protect complete formulas before Markdown sees underscores, stars, or
    // blank lines inside them. Code samples must retain their literal source.
    private static readonly Regex MathTokenPattern = new(
        @"(?<code>(?<fence>`{3,}|~{3,})[^\n]*\n[\s\S]*?(?:\k<fence>[^\n]*(?:\n|$)|\z)|(?<ticks>`+)[^\n]*?\k<ticks>)|(?<citation>(?<!!)\[[Ss]\d+\])(?!\()|(?<!\\)(?:\\\[(?<displayBracket>[\s\S]*?)\\\]|\$\$(?<displayDollar>[\s\S]*?)\$\$|\\\((?<inlineBracket>[\s\S]*?)\\\)|(?<!\w)\$(?![\s$])(?<inlineDollar>[^$\n]*?[^\s\\])\$(?!\d))",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly List<(string Latex, bool Display, string Source)> _markdownMath = [];
    private readonly List<(MathView Formula, Viewbox View)> _mathViews = [];

    private string ProtectMath(string markdown) => MathTokenPattern.Replace(markdown, match =>
    {
        if (match.Groups["code"].Success) return match.Value;
        if (match.Groups["citation"].Success)
        {
            var citationIndex = _markdownMath.Count;
            _markdownMath.Add((string.Empty, false, match.Value));
            return "\uE000" + citationIndex.ToString(CultureInfo.InvariantCulture) + "\uE001";
        }
        var display = match.Groups["displayBracket"].Success || match.Groups["displayDollar"].Success;
        var latex = new[] { "displayBracket", "displayDollar", "inlineBracket", "inlineDollar" }
            .Select(name => match.Groups[name]).First(group => group.Success).Value.Trim();
        var index = _markdownMath.Count;
        _markdownMath.Add((latex, display, match.Value));
        var token = "\uE000" + index.ToString(CultureInfo.InvariantCulture) + "\uE001";
        return display ? "\n" + token + "\n" : token;
    });

    private void AppendMath(string token, InlineCollection target)
    {
        if (!int.TryParse(token.AsSpan(1, token.Length - 2), out var index)
            || index < 0 || index >= _markdownMath.Count)
        {
            target.Add(new Run(token));
            return;
        }

        var (latex, display, source) = _markdownMath[index];
        if (latex.Length == 0 && source.StartsWith('['))
        {
            AppendCitation(source, target);
            return;
        }
        var formula = new MathView
        {
            LaTeX = latex,
            FontSize = (float)FontSize,
            LineStyle = display ? LineStyle.Display : LineStyle.Text,
            TextColor = (Foreground as ISolidColorBrush)?.Color ?? Colors.Black,
            DisplayErrorInline = false,
            IsHitTestVisible = false
        };
        // Streaming and unsupported TeX stay readable, without replacing the
        // whole answer with a parser error or losing its original content.
        if (formula.ErrorMessage is not null || string.IsNullOrWhiteSpace(latex))
        {
            target.Add(new Run(source));
            return;
        }

        var view = new Viewbox
        {
            Child = formula,
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            MaxWidth = MathWidth,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = display ? new Thickness(0, 6) : new Thickness(1, 0)
        };
        AutomationProperties.SetName(view, latex);
        ToolTip.SetTip(view, source);
        _mathViews.Add((formula, view));
        target.Add(new InlineUIContainer(view) { BaselineAlignment = BaselineAlignment.Center });
    }

    private double MathWidth => Bounds.Width > 0 ? Math.Max(1, Bounds.Width - 2) : 320;

    private void UpdateMathAppearance()
    {
        foreach (var (formula, view) in _mathViews)
        {
            formula.FontSize = (float)FontSize;
            formula.TextColor = (Foreground as ISolidColorBrush)?.Color ?? Colors.Black;
            view.MaxWidth = MathWidth;
        }
    }
}
