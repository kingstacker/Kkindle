using System.Text;

namespace Kkindle.Layout;

public sealed class CssHints
{
    public bool Bold { get; init; }
    public bool Italic { get; init; }
    public bool Center { get; init; }
    public bool AlignRight { get; init; }
    /// <summary>The publisher asked for ragged-right text (`text-align: left`).</summary>
    public bool NoJustify { get; init; }
    public bool NoWrap { get; init; }
    /// <summary>-1 means unspecified, 0 means none, positive values limit digits; int.MaxValue means all.</summary>
    public int VerticalTextCombineLimit { get; init; } = -1;
    public TypesetVerticalOrientation? VerticalTextOrientation { get; init; }
    public float TextIndentEm { get; init; } = float.NaN;
    public float FontSizeFactor { get; init; } = float.NaN;
    /// <summary>Percentage width as a 0..1 factor, primarily for images.</summary>
    public float ImageWidthFactor { get; init; } = float.NaN;
    /// <summary>Font-relative image height in em units, primarily for inline glyph images.</summary>
    public float ImageHeightEm { get; init; } = float.NaN;
    /// <summary>Publisher `line-height` as a multiple of the font size; NaN means unspecified.</summary>
    public float LineHeightFactor { get; init; } = float.NaN;
    public float MarginTopEm { get; init; } = float.NaN;
    public float MarginBottomEm { get; init; } = float.NaN;
    public float MarginLeftEm { get; init; } = float.NaN;
    public float MarginRightEm { get; init; } = float.NaN;
    public float PaddingLeftEm { get; init; } = float.NaN;
    public float PaddingRightEm { get; init; } = float.NaN;
    /// <summary>Visible border widths in em; NaN means no border was declared.</summary>
    public float BorderTopEm { get; init; } = float.NaN;
    public float BorderRightEm { get; init; } = float.NaN;
    public float BorderBottomEm { get; init; } = float.NaN;
    public float BorderLeftEm { get; init; } = float.NaN;

    public static readonly CssHints None = new();
}

/// <summary>
/// A deliberately small CSS reader: no cascade, no inheritance, no
/// combinators. Single compound selectors (tag, .class, #id) and one-level
/// class descendants are kept, and only the declarations the engine's v1
/// subset understands. Later rules override earlier ones per property, which
/// mirrors publisher stylesheets closely enough for emphasis, centering and
/// nowrap while staying deterministic.
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
        var ruleStart = 0;
        while (true)
        {
            var open = body.IndexOf('{', ruleStart);
            if (open < 0)
            {
                break;
            }

            var close = body.IndexOf('}', open + 1);
            if (close < 0)
            {
                break;
            }

            var selectorText = body[ruleStart..open].Trim();
            var declarationText = body[(open + 1)..close];
            ruleStart = close + 1;

            // Selector groups share one declaration block: `td, th { … }`
            // must apply to every simple selector, not just the first.
            var selectors = selectorText
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(IsSimpleCompoundSelector)
                .ToArray();
            if (selectors.Length == 0)
            {
                continue;
            }

            var declarations = ParseDeclarations(declarationText);
            foreach (var selector in selectors)
            {
                if (declarations.Count > 0)
                {
                    _rules.Add((selector, declarations));
                }
            }
        }
    }

    public CssHints Resolve(string tag, IReadOnlyCollection<string> classes, string? id) =>
        Resolve(tag, classes, id, Array.Empty<string>());

    public CssHints Resolve(
        string tag,
        IReadOnlyCollection<string> classes,
        string? id,
        IReadOnlyCollection<string> ancestorClasses)
    {
        var bold = false;
        var italic = false;
        var center = false;
        var alignRight = false;
        var noJustify = false;
        var noWrap = false;
        var verticalTextCombineLimit = -1;
        TypesetVerticalOrientation? verticalTextOrientation = null;
        var indent = float.NaN;
        var sizeFactor = float.NaN;
        var imageWidthFactor = float.NaN;
        var imageHeightEm = float.NaN;
        var lineHeightFactor = float.NaN;
        var marginTop = float.NaN;
        var marginBottom = float.NaN;
        var marginLeft = float.NaN;
        var marginRight = float.NaN;
        var paddingLeft = float.NaN;
        var paddingRight = float.NaN;
        var borderTop = float.NaN;
        var borderRight = float.NaN;
        var borderBottom = float.NaN;
        var borderLeft = float.NaN;
        var matched = false;

        foreach (var (selector, declarations) in _rules)
        {
            if (!Matches(selector, tag, classes, id, ancestorClasses))
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
                        alignRight = value == "right";
                        noJustify = value is "left" or "start";
                        break;
                    case "white-space":
                        noWrap = value == "nowrap" || value == "pre";
                        break;
                    case "text-combine-upright":
                    case "-webkit-text-combine":
                        verticalTextCombineLimit = ParseTextCombineLimit(value);
                        break;
                    case "text-orientation":
                        verticalTextOrientation = ParseVerticalOrientation(value);
                        break;
                    case "text-indent":
                        indent = ParseEm(value);
                        break;
                    case "font-size":
                        sizeFactor = ParseEmOrPercent(value);
                        break;
                    case "width":
                        imageWidthFactor = ParsePercent(value);
                        break;
                    case "height":
                        var parsedHeight = ParseEm(value);
                        if (parsedHeight > 0f)
                        {
                            imageHeightEm = parsedHeight;
                        }
                        break;
                    case "line-height":
                        lineHeightFactor = ParseLineHeightFactor(value);
                        break;
                    case "margin-top":
                        marginTop = ParseLengthEm(value);
                        break;
                    case "margin-bottom":
                        marginBottom = ParseLengthEm(value);
                        break;
                    case "margin-left":
                        marginLeft = ParseLengthEm(value);
                        break;
                    case "margin-right":
                        marginRight = ParseLengthEm(value);
                        break;
                    case "padding-left":
                        paddingLeft = ParseLengthEm(value);
                        break;
                    case "padding-right":
                        paddingRight = ParseLengthEm(value);
                        break;
                    case "margin":
                    case "padding":
                        var box = ParseBoxShorthand(value);
                        if (name == "margin")
                        {
                            marginTop = box.Top;
                            marginRight = box.Right;
                            marginBottom = box.Bottom;
                            marginLeft = box.Left;
                        }
                        else
                        {
                            paddingLeft = box.Left;
                            paddingRight = box.Right;
                        }

                        break;
                    case "border":
                        var border = ParseBorderSide(value);
                        if (!float.IsNaN(border))
                        {
                            borderTop = border;
                            borderRight = border;
                            borderBottom = border;
                            borderLeft = border;
                        }

                        break;
                    case "border-top":
                    case "border-right":
                    case "border-bottom":
                    case "border-left":
                        var sideWidth = ParseBorderSide(value);
                        if (!float.IsNaN(sideWidth))
                        {
                            if (name == "border-top")
                            {
                                borderTop = sideWidth;
                            }
                            else if (name == "border-right")
                            {
                                borderRight = sideWidth;
                            }
                            else if (name == "border-bottom")
                            {
                                borderBottom = sideWidth;
                            }
                            else
                            {
                                borderLeft = sideWidth;
                            }
                        }

                        break;
                    case "border-width":
                        var borderBox = ParseBoxShorthand(value);
                        if (!float.IsNaN(borderBox.Top))
                        {
                            borderTop = ClampBorderWidth(borderBox.Top);
                            borderRight = ClampBorderWidth(borderBox.Right);
                            borderBottom = ClampBorderWidth(borderBox.Bottom);
                            borderLeft = ClampBorderWidth(borderBox.Left);
                        }

                        break;
                    case "border-top-width":
                        borderTop = ClampBorderWidth(ParseLengthEm(value));
                        break;
                    case "border-right-width":
                        borderRight = ClampBorderWidth(ParseLengthEm(value));
                        break;
                    case "border-bottom-width":
                        borderBottom = ClampBorderWidth(ParseLengthEm(value));
                        break;
                    case "border-left-width":
                        borderLeft = ClampBorderWidth(ParseLengthEm(value));
                        break;
                    case "border-style":
                        ApplyBorderStyle(value, ref borderTop, ref borderRight, ref borderBottom, ref borderLeft);
                        break;
                    case "border-top-style":
                        ApplyBorderStyleSide(value, ref borderTop);
                        break;
                    case "border-right-style":
                        ApplyBorderStyleSide(value, ref borderRight);
                        break;
                    case "border-bottom-style":
                        ApplyBorderStyleSide(value, ref borderBottom);
                        break;
                    case "border-left-style":
                        ApplyBorderStyleSide(value, ref borderLeft);
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
                AlignRight = alignRight,
                NoJustify = noJustify,
                NoWrap = noWrap,
                VerticalTextCombineLimit = verticalTextCombineLimit,
                VerticalTextOrientation = verticalTextOrientation,
                TextIndentEm = indent,
                FontSizeFactor = sizeFactor,
                ImageWidthFactor = imageWidthFactor,
                ImageHeightEm = imageHeightEm,
                LineHeightFactor = lineHeightFactor,
                MarginTopEm = marginTop,
                MarginBottomEm = marginBottom,
                MarginLeftEm = marginLeft,
                MarginRightEm = marginRight,
                PaddingLeftEm = paddingLeft,
                PaddingRightEm = paddingRight,
                BorderTopEm = borderTop,
                BorderRightEm = borderRight,
                BorderBottomEm = borderBottom,
                BorderLeftEm = borderLeft,
            }
            : CssHints.None;
    }

    private static float ClampBorderWidth(float width) =>
        float.IsNaN(width) ? float.NaN : Math.Clamp(width, 0f, 0.5f);

    /// <summary>
    /// Parses `border`/`border-top` shorthand such as `1px solid #000` or
    /// `none`. Returns the visible width in em, 0 for an explicit none, and
    /// NaN when the declaration cannot be understood.
    /// </summary>
    private static float ParseBorderSide(string value)
    {
        var parts = value.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return float.NaN;
        }

        var hasStyle = false;
        var hidden = false;
        var width = float.NaN;
        foreach (var part in parts)
        {
            var token = part.ToLowerInvariant();
            if (token is "none" or "hidden")
            {
                hidden = true;
            }
            else if (IsBorderStyle(token))
            {
                hasStyle = true;
            }
            else if (!IsBorderColor(token))
            {
                var parsed = ParseLengthEm(token);
                if (!float.IsNaN(parsed))
                {
                    width = parsed;
                }
            }
        }

        if (hidden)
        {
            return 0f;
        }

        if (!hasStyle && float.IsNaN(width))
        {
            // A bare colour or `initial`/`inherit`: CSS keeps style none.
            return IsBorderColor(value.Trim().ToLowerInvariant()) ? 0f : float.NaN;
        }

        return ClampBorderWidth(float.IsNaN(width) ? 1f / 16f : width);
    }

    private static bool IsBorderStyle(string token) =>
        token is "solid" or "dashed" or "dotted" or "double"
            or "groove" or "ridge" or "inset" or "outset";

    private static bool IsBorderColor(string token)
    {
        if (token.Length == 0)
        {
            return false;
        }

        if (token[0] == '#'
            || token.StartsWith("rgb", StringComparison.Ordinal)
            || token.StartsWith("hsl", StringComparison.Ordinal)
            || token.StartsWith("var(", StringComparison.Ordinal))
        {
            return true;
        }

        return token is "transparent" or "currentcolor" or "initial" or "inherit"
            or "unset" or "revert"
            or "black" or "white" or "gray" or "grey" or "red" or "green" or "blue"
            or "silver" or "maroon" or "navy" or "olive" or "purple" or "teal"
            or "yellow" or "aqua" or "fuchsia" or "lime" or "orange";
    }

    private static void ApplyBorderStyle(
        string value,
        ref float top,
        ref float right,
        ref float bottom,
        ref float left)
    {
        var parts = value.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length is < 1 or > 4)
        {
            return;
        }

        static bool Visible(string token) => !(token is "none" or "hidden");

        var topVisible = Visible(parts[0]);
        var rightVisible = Visible(parts.Length > 1 ? parts[1] : parts[0]);
        var bottomVisible = Visible(parts.Length > 2 ? parts[2] : parts[0]);
        var leftVisible = Visible(parts.Length > 3 ? parts[3] : parts.Length > 1 ? parts[1] : parts[0]);

        top = StyleWidth(top, topVisible);
        right = StyleWidth(right, rightVisible);
        bottom = StyleWidth(bottom, bottomVisible);
        left = StyleWidth(left, leftVisible);
    }

    private static void ApplyBorderStyleSide(string value, ref float width)
    {
        var token = value.Trim().ToLowerInvariant();
        width = StyleWidth(width, token is not ("none" or "hidden"));
    }

    private static float StyleWidth(float current, bool visible)
    {
        if (!visible)
        {
            return 0f;
        }

        return float.IsNaN(current) || current <= 0f ? 1f / 16f : current;
    }

    private static int ParseTextCombineLimit(string value)
    {
        var parts = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || parts[0] == "none")
        {
            return 0;
        }

        if (parts[0] == "all")
        {
            return int.MaxValue;
        }

        if (parts[0] == "digits")
        {
            return parts.Length > 1 && int.TryParse(parts[1], out var limit)
                ? Math.Clamp(limit, 2, 8)
                : 2;
        }

        return -1;
    }

    private static TypesetVerticalOrientation? ParseVerticalOrientation(string value) =>
        value switch
        {
            "upright" => TypesetVerticalOrientation.Upright,
            "sideways" => TypesetVerticalOrientation.Sideways,
            "mixed" => TypesetVerticalOrientation.Mixed,
            _ => null,
        };

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

    private static bool Matches(
        string selector,
        string tag,
        IReadOnlyCollection<string> classes,
        string? id,
        IReadOnlyCollection<string> ancestorClasses)
    {
        var parts = selector.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2)
        {
            // The current EPUB uses selectors such as ".yinhao_r img" for
            // decorative quote assets. The ancestor side is intentionally
            // class-only; unsupported structural selectors are rejected when
            // the stylesheet is parsed.
            if (parts[0].Length < 2 || parts[0][0] != '.'
                || !ancestorClasses.Contains(parts[0][1..]))
            {
                return false;
            }

            return MatchesCompound(parts[1], tag, classes, id);
        }

        return parts.Length == 1 && MatchesCompound(parts[0], tag, classes, id);
    }

    private static bool MatchesCompound(
        string selector,
        string tag,
        IReadOnlyCollection<string> classes,
        string? id)
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

    private static bool IsSimpleCompoundSelector(string selector)
    {
        var parts = selector.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2
            && parts[0].StartsWith('.')
            && IsCompoundSelector(parts[1]))
        {
            return true;
        }

        return parts.Length == 1 && IsCompoundSelector(parts[0]);
    }

    private static bool IsCompoundSelector(string selector) =>
        selector.Length > 0
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
            // `!important` is not part of the value. With no cascade beyond
            // document order here, the flag rarely changes the winner, but
            // leaving it attached breaks keyword comparisons such as
            // `font-weight: bold !important`.
            var bang = value.LastIndexOf('!');
            if (bang >= 0
                && value[(bang + 1)..].Trim().Equals("important", StringComparison.OrdinalIgnoreCase))
            {
                value = value[..bang].TrimEnd();
            }

            if (name.Length > 0 && value.Length > 0)
            {
                // Later declarations in a rule win, as in CSS (`display:
                // -webkit-box; display: flex`).
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

        if (value.EndsWith("px", StringComparison.OrdinalIgnoreCase))
        {
            return ParseNumber(value[..^2], 1f / 16f);
        }

        if (value.EndsWith("pt", StringComparison.OrdinalIgnoreCase))
        {
            return ParseNumber(value[..^2], 1f / 12f);
        }

        return float.NaN;
    }

    /// <summary>
    /// Parses a CSS length into em units relative to the body font (1em =
    /// 16px = 12pt). Unsupported units, `auto` and percentages (whose
    /// containing block is not known here) become NaN.
    /// </summary>
    private static float ParseLengthEm(string value)
    {
        value = value.Trim();
        if (value.Length == 0)
        {
            return float.NaN;
        }

        if (value == "0" || value == "0.0")
        {
            return 0f;
        }

        if (value.EndsWith("rem", StringComparison.OrdinalIgnoreCase))
        {
            return ParseNumber(value[..^3], 1f);
        }

        if (value.EndsWith("em", StringComparison.OrdinalIgnoreCase))
        {
            return ParseNumber(value[..^2], 1f);
        }

        if (value.EndsWith("px", StringComparison.OrdinalIgnoreCase))
        {
            return ParseNumber(value[..^2], 1f / 16f);
        }

        if (value.EndsWith("pt", StringComparison.OrdinalIgnoreCase))
        {
            return ParseNumber(value[..^2], 1f / 12f);
        }

        return float.NaN;
    }

    private static float ParseNumber(string text, float scale)
    {
        return float.TryParse(
            text.Trim(),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var number)
            ? number * scale
            : float.NaN;
    }

    private static float ParseLineHeightFactor(string value)
    {
        value = value.Trim();
        if (value.Equals("normal", StringComparison.OrdinalIgnoreCase))
        {
            return float.NaN;
        }

        float factor;
        if (value.EndsWith('%'))
        {
            factor = ParseNumber(value[..^1], 0.01f);
        }
        else if (value.EndsWith("rem", StringComparison.OrdinalIgnoreCase))
        {
            factor = ParseNumber(value[..^3], 1f);
        }
        else if (value.EndsWith("em", StringComparison.OrdinalIgnoreCase))
        {
            factor = ParseNumber(value[..^2], 1f);
        }
        else if (value.EndsWith("px", StringComparison.OrdinalIgnoreCase))
        {
            factor = ParseNumber(value[..^2], 1f / 16f);
        }
        else if (value.EndsWith("pt", StringComparison.OrdinalIgnoreCase))
        {
            factor = ParseNumber(value[..^2], 1f / 12f);
        }
        else
        {
            factor = ParseNumber(value, 1f);
        }

        // Publishers occasionally emit pathological values (0, 40) meant for
        // another medium. Keep a readable band and treat the rest as absent.
        return factor is >= 1f and <= 4f ? factor : float.NaN;
    }

    private readonly record struct BoxEdges(float Top, float Right, float Bottom, float Left)
    {
        public static BoxEdges Unspecified { get; } =
            new(float.NaN, float.NaN, float.NaN, float.NaN);
    }

    private static BoxEdges ParseBoxShorthand(string value)
    {
        var parts = value.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length is < 1 or > 4)
        {
            return BoxEdges.Unspecified;
        }

        var lengths = new float[parts.Length];
        for (var index = 0; index < parts.Length; index++)
        {
            lengths[index] = ParseLengthEm(parts[index]);
            if (float.IsNaN(lengths[index]))
            {
                return BoxEdges.Unspecified;
            }
        }

        return lengths.Length switch
        {
            1 => new BoxEdges(lengths[0], lengths[0], lengths[0], lengths[0]),
            2 => new BoxEdges(lengths[0], lengths[1], lengths[0], lengths[1]),
            3 => new BoxEdges(lengths[0], lengths[1], lengths[2], lengths[1]),
            _ => new BoxEdges(lengths[0], lengths[1], lengths[2], lengths[3]),
        };
    }

    private static float ParsePercent(string value)
    {
        value = value.Trim();
        if (value.EndsWith('%')
            && float.TryParse(value[..^1], System.Globalization.CultureInfo.InvariantCulture, out var percent)
            && percent > 0f)
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
