using System.Text;

namespace Kkindle.Layout;

public sealed class CssHints
{
    public bool Bold { get; init; }
    public bool Italic { get; init; }
    public bool Center { get; init; }
    public bool NoWrap { get; init; }
    public float TextIndentEm { get; init; } = float.NaN;
    public float FontSizeFactor { get; init; } = float.NaN;

    public static readonly CssHints None = new();
}

/// <summary>
/// A deliberately small CSS reader: no cascade, no inheritance, no
/// combinators. Only single compound selectors (tag, .class, #id) are kept,
/// and only the declarations the engine's v1 subset understands. Later rules
/// override earlier ones per property, which mirrors publisher stylesheets
/// closely enough for emphasis, centering and nowrap while staying
/// deterministic.
/// </summary>
public sealed class MicroCss
{
    private readonly List<(string Selector, Dictionary<string, string> Declarations)> _rules = new();

    public static MicroCss Parse(params string[] cssTexts)
    {
        var css = new MicroCss();
        foreach (var text in cssTexts)
        {
            css.Append(text);
        }

        return css;
    }

    public void Append(string? cssText)
    {
        if (string.IsNullOrWhiteSpace(cssText))
        {
            return;
        }

        var body = StripComments(cssText);
        var index = 0;
        while ((index = body.IndexOf('{', index)) >= 0)
        {
            var close = body.IndexOf('}', index + 1);
            if (close < 0)
            {
                break;
            }

            var selectorText = body[..index].Trim();
            var declarationText = body[(index + 1)..close];
            index = close + 1;

            var selector = selectorText
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(s => IsSimpleCompoundSelector(s));
            if (selector is null)
            {
                continue;
            }

            var declarations = ParseDeclarations(declarationText);
            if (declarations.Count > 0)
            {
                _rules.Add((selector, declarations));
            }
        }
    }

    public CssHints Resolve(string tag, IReadOnlyCollection<string> classes, string? id)
    {
        var bold = false;
        var italic = false;
        var center = false;
        var noWrap = false;
        var indent = float.NaN;
        var sizeFactor = float.NaN;
        var matched = false;

        foreach (var (selector, declarations) in _rules)
        {
            if (!Matches(selector, tag, classes, id))
            {
                continue;
            }

            matched = true;
            foreach (var (name, value) in declarations)
            {
                switch (name)
                {
                    case "font-weight":
                        bold = value is "bold" or "bolder" || (int.TryParse(value, out var weight) && weight >= 600);
                        break;
                    case "font-style":
                        italic = value == "italic" || value == "oblique";
                        break;
                    case "text-align":
                        center = value == "center";
                        break;
                    case "white-space":
                        noWrap = value == "nowrap" || value == "pre";
                        break;
                    case "text-indent":
                        indent = ParseEm(value);
                        break;
                    case "font-size":
                        sizeFactor = ParseEmOrPercent(value);
                        break;
                }
            }
        }

        return matched
            ? new CssHints
            {
                Bold = bold,
                Italic = italic,
                Center = center,
                NoWrap = noWrap,
                TextIndentEm = indent,
                FontSizeFactor = sizeFactor,
            }
            : CssHints.None;
    }

    /// <summary>Parses one inline style="" attribute into hints.</summary>
    public static CssHints ParseInlineStyle(string? style)
    {
        if (string.IsNullOrWhiteSpace(style))
        {
            return CssHints.None;
        }

        var css = new MicroCss();
        var declarations = ParseDeclarations(style);
        if (declarations.Count == 0)
        {
            return CssHints.None;
        }

        css._rules.Add(("*", declarations));
        return css.Resolve("*", Array.Empty<string>(), null);
    }

    private static bool Matches(string selector, string tag, IReadOnlyCollection<string> classes, string? id)
    {
        foreach (var part in SplitCompound(selector))
        {
            switch (part[0])
            {
                case '.':
                    if (!classes.Contains(part[1..]))
                    {
                        return false;
                    }

                    break;
                case '#':
                    if (!string.Equals(id, part[1..], StringComparison.Ordinal))
                    {
                        return false;
                    }

                    break;
                default:
                    if (!string.Equals(part, tag, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }

                    break;
            }
        }

        return true;
    }

    private static IEnumerable<string> SplitCompound(string selector)
    {
        // "p.calibre5" -> ["p", ".calibre5"]; ".c" -> [".c"]; "b#id.x" -> ["b", "#id", ".x"]
        var i = 0;
        while (i < selector.Length)
        {
            var start = i;
            if (selector[i] == '.' || selector[i] == '#')
            {
                i++;
            }

            while (i < selector.Length && selector[i] != '.' && selector[i] != '#')
            {
                i++;
            }

            yield return selector[start..i];
        }
    }

    private static bool IsSimpleCompoundSelector(string selector) =>
        selector.Length > 0
        && !selector.Contains(' ')
        && !selector.Contains('>')
        && !selector.Contains('+')
        && !selector.Contains('~')
        && !selector.Contains('[')
        && !selector.Contains(':')
        && !selector.Contains('*');

    private static Dictionary<string, string> ParseDeclarations(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var declaration in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = declaration.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var name = declaration[..colon].Trim().ToLowerInvariant();
            var value = declaration[(colon + 1)..].Trim();
            if (name.Length > 0 && value.Length > 0 && !result.ContainsKey(name))
            {
                result[name] = value;
            }
        }

        return result;
    }

    private static float ParseEm(string value)
    {
        value = value.Trim();
        if (value.EndsWith("em", StringComparison.OrdinalIgnoreCase)
            && float.TryParse(value[..^2], System.Globalization.CultureInfo.InvariantCulture, out var em))
        {
            return em;
        }

        return 0f;
    }

    private static float ParseEmOrPercent(string value)
    {
        value = value.Trim();
        if (value.EndsWith("em", StringComparison.OrdinalIgnoreCase)
            && float.TryParse(value[..^2], System.Globalization.CultureInfo.InvariantCulture, out var em))
        {
            return em;
        }

        if (value.EndsWith('%')
            && float.TryParse(value[..^1], System.Globalization.CultureInfo.InvariantCulture, out var percent))
        {
            return percent / 100f;
        }

        return float.NaN;
    }

    private static string StripComments(string css)
    {
        var builder = new StringBuilder(css.Length);
        var index = 0;
        while (index < css.Length)
        {
            var open = css.IndexOf("/*", index, StringComparison.Ordinal);
            if (open < 0)
            {
                builder.Append(css[index..]);
                break;
            }

            builder.Append(css[index..open]);
            var close = css.IndexOf("*/", open + 2, StringComparison.Ordinal);
            if (close < 0)
            {
                break;
            }

            index = close + 2;
        }

        return builder.ToString();
    }
}
