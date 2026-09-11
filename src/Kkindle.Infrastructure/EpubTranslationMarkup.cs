using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Kkindle.Infrastructure;

/// <summary>
/// Keeps provider text separate from EPUB markup. Only text and opaque markers
/// leave the document; attributes, links and note references stay local.
/// </summary>
internal sealed class EpubTranslationMarkup
{
    private static readonly HashSet<string> HiddenNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "head", "script", "style", "noscript", "svg", "math", "rt", "rp", "rtc"
    };

    private readonly List<InlineToken> _tokens = [];
    private readonly Regex _markerPattern;
    private readonly string _markerPrefix;

    public EpubTranslationMarkup(IReadOnlyList<XNode> nodes)
    {
        Nodes = nodes;
        Text = Normalize(string.Concat(nodes.SelectMany(VisibleText).Select(node => node.Value)));
        _markerPrefix = "__KKINDLE_INLINE_";
        while (string.Concat(nodes.Select(node => node.ToString())).Contains(_markerPrefix, StringComparison.Ordinal))
            _markerPrefix = "_" + _markerPrefix;
        _markerPattern = new Regex(Regex.Escape(_markerPrefix) + @"\d+__", RegexOptions.CultureInvariant);
        var builder = new StringBuilder();
        foreach (var node in nodes) AppendSource(node, builder);
        RequestText = Normalize(builder.ToString());
    }

    public IReadOnlyList<XNode> Nodes { get; }
    public string Text { get; }
    public string RequestText { get; }
    public bool HasMarkers => _tokens.Count > 0;
    public bool HasTranslatableText => Parts().Any(part => !part.IsMarker && part.Text.EnumerateRunes().Any(Rune.IsLetter));

    public static string Normalize(string text) => Regex.Replace(text.Replace('\u00a0', ' '), @"\s+", " ").Trim();

    public static bool IsHidden(XElement element) => HiddenNames.Contains(element.Name.LocalName)
        || element.Attribute("hidden") is not null
        || string.Equals(element.Attribute("aria-hidden")?.Value, "true", StringComparison.OrdinalIgnoreCase)
        || HasToken(element.Attribute("class")?.Value, "kkindle-translation");

    public static IEnumerable<XText> VisibleText(XNode node)
    {
        if (node is XText text) yield return text;
        if (node is not XElement element || IsHidden(element)) yield break;
        foreach (var child in element.Nodes())
            foreach (var descendant in VisibleText(child))
                yield return descendant;
    }

    public IEnumerable<(string Text, bool IsMarker)> Parts()
    {
        var cursor = 0;
        foreach (Match match in _markerPattern.Matches(RequestText))
        {
            if (match.Index > cursor) yield return (RequestText[cursor..match.Index], false);
            yield return (match.Value, true);
            cursor = match.Index + match.Length;
        }
        if (cursor < RequestText.Length) yield return (RequestText[cursor..], false);
    }

    public bool IsValidTranslation(string translated) =>
        (!HasTranslatableText || !string.IsNullOrWhiteSpace(_markerPattern.Replace(translated, string.Empty)))
        && TryRender(translated, out _);

    public string DisplayText(string translated) => TryRender(translated, out var nodes)
        ? Normalize(string.Concat(nodes.SelectMany(VisibleText).Select(node => node.Value)))
        : _markerPattern.Replace(translated, string.Empty);

    public bool TryRender(string translated, out IReadOnlyList<XNode> nodes)
    {
        nodes = [];
        try { XmlConvert.VerifyXmlChars(translated); }
        catch (XmlException) { return false; }

        var matches = _markerPattern.Matches(translated);
        if (matches.Count != _tokens.Count) return false;
        for (var index = 0; index < matches.Count; index++)
            if (matches[index].Value != Marker(index)) return false;

        var root = new XElement("translation");
        var current = root;
        var cursor = 0;
        for (var index = 0; index < matches.Count; index++)
        {
            var match = matches[index];
            if (match.Index > cursor) current.Add(new XText(translated[cursor..match.Index]));
            var token = _tokens[index];
            switch (token.Kind)
            {
                case TokenKind.Open:
                    var original = (XElement)token.Node;
                    var child = new XElement(original.Name, original.Attributes());
                    current.Add(child);
                    current = child;
                    break;
                case TokenKind.Close:
                    current = current.Parent!;
                    break;
                case TokenKind.Protected:
                    // LINQ to XML clones an attached node when it is added.
                    current.Add(Clone(token.Node));
                    break;
            }
            cursor = match.Index + match.Length;
        }
        if (cursor < translated.Length) current.Add(new XText(translated[cursor..]));
        nodes = root.Nodes().ToArray();
        return true;
    }

    private void AppendSource(XNode node, StringBuilder builder)
    {
        if (node is XText text)
        {
            builder.Append(text.Value);
            return;
        }
        if (node is not XElement element)
        {
            AddToken(TokenKind.Protected, node, builder);
            return;
        }

        var name = element.Name.LocalName.ToLowerInvariant();
        // Ruby readings describe the original language. Translate just the base
        // characters, without carrying those readings onto a different word.
        if (name is "rt" or "rp" or "rtc") return;
        if (name is "ruby" or "rb" or "rbc")
        {
            foreach (var child in element.Nodes()) AppendSource(child, builder);
            return;
        }
        if (IsHidden(element) || IsNoteReference(element) || !element.Nodes().Any())
        {
            AddToken(TokenKind.Protected, element, builder);
            return;
        }

        AddToken(TokenKind.Open, element, builder);
        foreach (var child in element.Nodes()) AppendSource(child, builder);
        AddToken(TokenKind.Close, element, builder);
    }

    private static bool IsNoteReference(XElement element)
    {
        var type = element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == "type")?.Value;
        var role = element.Attribute("role")?.Value;
        var classes = element.Attribute("class")?.Value;
        return HasToken(type, "noteref") || HasToken(type, "backlink")
            || HasToken(role, "doc-noteref") || HasToken(role, "doc-backlink")
            || HasToken(classes, "footnote-ref") || HasToken(classes, "footnote-backref")
            || (element.Name.LocalName.Equals("a", StringComparison.OrdinalIgnoreCase)
                && element.Attribute("href")?.Value.StartsWith('#') == true
                && !element.Value.EnumerateRunes().Any(Rune.IsLetter));
    }

    private static bool HasToken(string? value, string token) =>
        value?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Contains(token, StringComparer.OrdinalIgnoreCase) == true;

    private void AddToken(TokenKind kind, XNode node, StringBuilder builder)
    {
        builder.Append(Marker(_tokens.Count));
        _tokens.Add(new InlineToken(kind, node));
    }

    private string Marker(int index) => _markerPrefix + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + "__";

    private static XNode Clone(XNode node) => node switch
    {
        XElement element => new XElement(element),
        XCData data => new XCData(data.Value),
        XText text => new XText(text.Value),
        XComment comment => new XComment(comment.Value),
        XProcessingInstruction instruction => new XProcessingInstruction(instruction.Target, instruction.Data),
        _ => throw new InvalidDataException("EPUB 包含无法复制的行内节点。")
    };

    private enum TokenKind { Open, Close, Protected }
    private sealed record InlineToken(TokenKind Kind, XNode Node);
}
