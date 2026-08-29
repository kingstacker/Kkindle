using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Kkindle.Layout;

/// <summary>
/// Loads one sanitized spine XHTML chapter into typed blocks. Body text
/// offsets follow the same convention the WebKit bridge used: the verbatim
/// concatenation of every body text node in document order, so annotations
/// and search jumps captured against the live document keep pointing at the
/// same characters. Footnote definitions and ruby phonetics stay in the
/// offset stream but are marked ghost / skipped.
/// </summary>
public sealed class XhtmlChapterLoader
{
    private readonly bool _paragraphIndent;
    private readonly StringBuilder _body = new();
    private readonly List<ContentBlock> _blocks = new();
    private readonly HashSet<string> _fragmentIds = new(StringComparer.Ordinal);
    private readonly List<InlineItem> _pending = new();
    private MicroCss? _css;
    private string _chapterDir = string.Empty;

    public XhtmlChapterLoader(bool paragraphIndent = true)
    {
        _paragraphIndent = paragraphIndent;
    }

    public ChapterContent Load(string chapterPath)
    {
        _body.Clear();
        _blocks.Clear();
        _fragmentIds.Clear();
        _pending.Clear();
        _css = null;
        _chapterDir = Path.GetDirectoryName(chapterPath) ?? string.Empty;

        var document = LoadDocument(chapterPath);
        if (document?.Root is null)
        {
            return new ChapterContent
            {
                ChapterPath = chapterPath,
                BodyText = string.Empty,
                Blocks = Array.Empty<ContentBlock>(),
                FragmentIds = _fragmentIds,
            };
        }

        _css = CollectStylesheets(document, _chapterDir);

        var body = document.Root
            .Descendants()
            .FirstOrDefault(e => e.Name.LocalName.Equals("body", StringComparison.OrdinalIgnoreCase));
        if (body is not null)
        {
            WalkFlow(body, FlowContext.Root(_paragraphIndent));
            FlushParagraph(FlowContext.Root(_paragraphIndent));
        }

        PromoteLeadingTitle();

        return new ChapterContent
        {
            ChapterPath = chapterPath,
            BodyText = _body.ToString(),
            Blocks = _blocks,
            FragmentIds = _fragmentIds,
        };
    }

    private static XDocument? LoadDocument(string path)
    {
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                XmlResolver = null,
                IgnoreComments = false,
            };
            using var reader = XmlReader.Create(path, settings);
            return XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return null;
        }
    }

    private MicroCss CollectStylesheets(XDocument document, string chapterDir)
    {
        var sources = new List<string>();
        var head = document.Root?
            .Descendants()
            .FirstOrDefault(e => e.Name.LocalName.Equals("head", StringComparison.OrdinalIgnoreCase));
        if (head is not null)
        {
            foreach (var link in head.Descendants().Where(e => e.Name.LocalName.Equals("link", StringComparison.OrdinalIgnoreCase)))
            {
                var rel = (string?)link.Attribute("rel");
                var href = (string?)link.Attribute("href");
                if (rel?.Contains("stylesheet", StringComparison.OrdinalIgnoreCase) != true
                    || string.IsNullOrWhiteSpace(href))
                {
                    continue;
                }

                var cssPath = ResolveLocalPath(href, chapterDir);
                if (cssPath is not null && File.Exists(cssPath))
                {
                    try
                    {
                        sources.Add(File.ReadAllText(cssPath));
                    }
                    catch (IOException)
                    {
                        // Unreadable stylesheet: the block still lays out plain.
                    }
                }
            }

            foreach (var style in head.Descendants().Where(e => e.Name.LocalName.Equals("style", StringComparison.OrdinalIgnoreCase)))
            {
                sources.Add(style.Value);
            }
        }

        return MicroCss.Parse(sources.ToArray());
    }

    private static string? ResolveLocalPath(string href, string baseDir)
    {
        if (string.IsNullOrWhiteSpace(href))
        {
            return null;
        }

        var withoutFragment = href.Split('#')[0];
        if (withoutFragment.Length == 0)
        {
            return null;
        }

        try
        {
            var decoded = Uri.UnescapeDataString(withoutFragment);
            return Path.GetFullPath(Path.Combine(baseDir, decoded));
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    // ---- flow walking ----------------------------------------------------

    private void WalkFlow(XElement element, FlowContext ctx)
    {
        foreach (var node in element.Nodes())
        {
            switch (node)
            {
                case XText text:
                    AppendFlowText(text.Value, ctx);
                    break;
                case XElement child:
                    HandleFlowElement(child, ctx);
                    break;
            }
        }
    }

    private void HandleFlowElement(XElement element, FlowContext ctx)
    {
        var local = element.Name.LocalName.ToLowerInvariant();
        if (IsHiddenFootnote(local, element))
        {
            FlushParagraph(ctx);
            WalkGhost(element);
            return;
        }

        TrackId(element);

        switch (local)
        {
            case "p":
                FlushParagraph(ctx);
                WalkBlockParagraph(element, BlockKind.Paragraph, ctx);
                break;
            case "h1":
            case "h2":
            case "h3":
            case "h4":
            case "h5":
            case "h6":
                FlushParagraph(ctx);
                WalkBlockParagraph(element, BlockKind.Heading, ctx);
                break;
            case "li":
                FlushParagraph(ctx);
                WalkBlockParagraph(element, BlockKind.ListItem, ctx);
                break;
            case "dd":
            case "dt":
            case "figcaption":
            case "caption":
                FlushParagraph(ctx);
                WalkBlockParagraph(element, BlockKind.Paragraph, ctx);
                break;
            case "blockquote":
                FlushParagraph(ctx);
                WalkFlow(element, ctx.AsQuote());
                FlushParagraph(ctx.AsQuote());
                break;
            case "ul":
            case "ol":
            case "table":
            case "thead":
            case "tbody":
            case "tr":
            case "td":
            case "th":
            case "figure":
                FlushParagraph(ctx);
                WalkFlow(element, ctx);
                break;
            case "hr":
                FlushParagraph(ctx);
                _blocks.Add(new ContentBlock { Kind = BlockKind.Rule, ElementId = Id(element) });
                break;
            case "img":
                FlushParagraph(ctx);
                AddImageBlock(element);
                break;
            case "style":
            case "script":
            case "head":
            case "title":
                break;
            case "svg":
                WalkGhost(element);
                break;
            default:
                if (IsInlineTag(local))
                {
                    AppendInlineElement(element, ctx);
                }
                else
                {
                    FlushParagraph(ctx);
                    WalkFlow(element, ctx);
                }

                break;
        }
    }

    private void AppendFlowText(string value, FlowContext ctx)
    {
        if (value.Length == 0)
        {
            return;
        }

        var start = AppendBodyText(value);
        if (!string.IsNullOrWhiteSpace(value))
        {
            _pending.Add(new InlineItem
            {
                Kind = InlineKind.Text,
                Text = value,
                TextStart = start,
                Style = ctx.BaseStyle,
                LinkHref = ctx.LinkHref,
                FootnoteHref = ctx.FootnoteHref,
            });
        }
    }

    private void WalkBlockParagraph(XElement element, BlockKind kind, FlowContext ctx)
    {
        var id = Id(element);
        var hints = ResolveHints(element);
        var paragraphCtx = ctx.ForBlock(kind, hints, id);
        WalkInlineContent(element, paragraphCtx);
        FlushParagraph(paragraphCtx);
    }

    private void WalkInlineContent(XElement element, FlowContext ctx)
    {
        foreach (var node in element.Nodes())
        {
            switch (node)
            {
                case XText text:
                    AppendFlowText(text.Value, ctx);
                    break;
                case XElement child:
                    WalkInlineElement(child, ctx);
                    break;
            }
        }
    }

    private void WalkInlineElement(XElement element, FlowContext ctx)
    {
        var local = element.Name.LocalName.ToLowerInvariant();
        TrackId(element);

        switch (local)
        {
            case "br":
                _pending.Add(new InlineItem
                {
                    Kind = InlineKind.LineBreak,
                    TextStart = -1,
                    Style = ctx.BaseStyle,
                });
                break;
            case "img":
                FlushParagraph(ctx);
                AddImageBlock(element);
                break;
            case "rt":
            case "rp":
                AppendGhost(element);
                break;
            case "hr":
                FlushParagraph(ctx);
                _blocks.Add(new ContentBlock { Kind = BlockKind.Rule, ElementId = Id(element) });
                break;
            case "a":
            {
                var href = (string?)element.Attribute("href");
                var inner = ctx.ForLink(href);
                WalkInlineContent(element, inner);
                break;
            }
            default:
                AppendInlineElement(element, ctx);
                break;
        }
    }

    private void AppendInlineElement(XElement element, FlowContext ctx)
    {
        var local = element.Name.LocalName.ToLowerInvariant();
        var style = ctx.BaseStyle;

        switch (local)
        {
            case "b":
            case "strong":
                style = style with { Bold = true };
                break;
            case "em":
            case "i":
            case "cite":
            case "dfn":
            case "var":
                style = style with { Italic = true };
                break;
            case "u":
            case "ins":
                style = style with { Underline = true };
                break;
            case "s":
            case "strike":
            case "del":
                style = style with { Strikeout = true };
                break;
            case "sup":
                style = style with { Superscript = true };
                break;
        }

        var hints = ResolveHints(element);
        if (hints.Bold)
        {
            style = style with { Bold = true };
        }

        if (hints.Italic)
        {
            style = style with { Italic = true };
        }

        if (hints.NoWrap)
        {
            style = style with { NoWrap = true };
        }

        WalkInlineContent(element, ctx.WithStyle(style));
    }

    private void AppendGhost(XElement element)
    {
        foreach (var text in element.DescendantNodes().OfType<XText>())
        {
            AppendBodyText(text.Value);
        }
    }

    private void WalkGhost(XElement element)
    {
        foreach (var node in element.DescendantNodes())
        {
            if (node is XText text)
            {
                AppendBodyText(text.Value);
            }
        }
    }

    private int AppendBodyText(string value)
    {
        var start = _body.Length;
        _body.Append(value);
        return start;
    }

    private void FlushParagraph(FlowContext ctx)
    {
        if (_pending.Count == 0)
        {
            return;
        }

        var hasVisible = _pending.Any(i =>
            (!i.Ghost && i.Kind == InlineKind.Image) ||
            (!i.Ghost && i.Text.Length > 0 && !string.IsNullOrWhiteSpace(i.Text)) ||
            i.Kind == InlineKind.LineBreak);

        if (!hasVisible)
        {
            _pending.Clear();
            return;
        }

        var items = _pending.ToList();
        _pending.Clear();

        _blocks.Add(new ContentBlock
        {
            Kind = ctx.Kind == BlockKind.Paragraph && ctx.IsQuote
                ? BlockKind.Blockquote
                : ctx.Kind,
            ElementId = ctx.ElementId,
            Style = ctx.BaseStyle,
            Center = ctx.Center,
            Justify = ctx.Justify,
            TextIndentEm = ctx.TextIndentEm,
            SpaceBeforeLines = ctx.SpaceBeforeLines,
            SpaceAfterLines = ctx.SpaceAfterLines,
            Items = items,
        });
    }

    private void AddImageBlock(XElement element)
    {
        var src = (string?)element.Attribute("src") ?? (string?)element.Attribute("data-src");
        var path = ResolveLocalPath(src ?? string.Empty, _chapterDir);
        if (path is null)
        {
            return;
        }

        var id = Id(element);
        var hints = ResolveHints(element);
        _blocks.Add(new ContentBlock
        {
            Kind = BlockKind.Image,
            ElementId = id,
            Center = true,
            SpaceBeforeLines = 1.8f * 1f / 1.8f,
            SpaceAfterLines = 1.8f / 1.8f,
            Items = new List<InlineItem>
            {
                new()
                {
                    Kind = InlineKind.Image,
                    ImagePath = path,
                    Style = hints.Bold ? new TypesetInlineStyle(Bold: true) : new TypesetInlineStyle(),
                },
            },
        });
        _pending.Clear();
    }

    // ---- helpers ---------------------------------------------------------

    private static string? Id(XElement element) => (string?)element.Attribute("id");

    private void TrackId(XElement element)
    {
        var id = Id(element);
        if (!string.IsNullOrEmpty(id))
        {
            _fragmentIds.Add(id);
        }
    }

    private CssHints ResolveHints(XElement element)
    {
        var classes = ((string?)element.Attribute("class") ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var hints = _css?.Resolve(element.Name.LocalName.ToLowerInvariant(), classes, Id(element)) ?? CssHints.None;
        var inline = MicroCss.ParseInlineStyle((string?)element.Attribute("style"));

        return new CssHints
        {
            Bold = hints.Bold || inline.Bold,
            Italic = hints.Italic || inline.Italic,
            Center = hints.Center || inline.Center,
            NoWrap = hints.NoWrap || inline.NoWrap,
            TextIndentEm = float.IsNaN(inline.TextIndentEm) ? hints.TextIndentEm : inline.TextIndentEm,
            FontSizeFactor = float.IsNaN(inline.FontSizeFactor) ? hints.FontSizeFactor : inline.FontSizeFactor,
        };
    }

    private static bool IsInlineTag(string local) =>
        local is "span" or "a" or "b" or "strong" or "em" or "i" or "u" or "s"
            or "strike" or "del" or "ins" or "sub" or "sup" or "ruby" or "rt" or "rp"
            or "small" or "big" or "mark" or "q" or "cite" or "dfn" or "var" or "abbr"
            or "time" or "font" or "label" or "code" or "kbd" or "samp";

    private static bool IsHiddenFootnote(string local, XElement element)
    {
        if (local is not ("aside" or "div" or "section" or "p" or "li"))
        {
            return false;
        }

        var id = (Id(element) ?? string.Empty).ToLowerInvariant();
        var classes = ((string?)element.Attribute("class") ?? string.Empty).ToLowerInvariant();

        return id.Contains("footnote") || id.Contains("endnote")
            || classes.Contains("footnote") || classes.Contains("endnote")
            || classes.Contains("duokan-footnote");
    }

    private void PromoteLeadingTitle()
    {
        if (_blocks.Count < 2)
        {
            return;
        }

        var first = _blocks[0];
        if (first.Kind != BlockKind.Paragraph)
        {
            return;
        }

        var text = string.Concat(first.Items
            .Where(i => !i.Ghost && i.Kind == InlineKind.Text)
            .Select(i => i.Text));
        if (text.Length == 0 || text.Length > 40)
        {
            return;
        }

        _blocks[0] = new ContentBlock
        {
            Kind = BlockKind.Heading,
            ElementId = first.ElementId,
            Style = first.Style with { Bold = true },
            Center = true,
            Justify = false,
            TextIndentEm = 0f,
            SpaceBeforeLines = 1.15f,
            SpaceAfterLines = 1.25f,
            Items = first.Items,
        };
    }

    private sealed record FlowContext
    {
        public BlockKind Kind { get; private init; }
        public bool IsQuote { get; private init; }
        public bool Center { get; private init; }
        public bool Justify { get; private init; }
        public float TextIndentEm { get; private init; }
        public float SpaceBeforeLines { get; private init; }
        public float SpaceAfterLines { get; private init; }
        public TypesetInlineStyle BaseStyle { get; private init; }
        public string? LinkHref { get; private init; }
        public string? FootnoteHref { get; private init; }
        public string? ElementId { get; private init; }
        public bool ParagraphIndentEnabled { get; private init; }

        public static FlowContext Root(bool paragraphIndent) => new()
        {
            Kind = BlockKind.Paragraph,
            Justify = true,
            ParagraphIndentEnabled = paragraphIndent,
        };

        public FlowContext AsQuote() => this with
        {
            IsQuote = true,
            Justify = true,
            TextIndentEm = ParagraphIndentEnabled ? 2f : 0f,
            SpaceBeforeLines = 0.78f,
            SpaceAfterLines = 0.78f,
        };

        public FlowContext WithElementId(string? elementId) => this with { ElementId = elementId };

        public FlowContext ForBlock(BlockKind kind, CssHints hints, string? elementId)
        {
            var isHeading = kind == BlockKind.Heading;
            var indented = !isHeading
                && !hints.Center
                && ParagraphIndentEnabled
                && kind is BlockKind.Paragraph or BlockKind.ListItem;
            return this with
            {
                Kind = kind,
                ElementId = elementId,
                Center = isHeading || hints.Center,
                Justify = !isHeading && !hints.Center,
                TextIndentEm = indented ? 2f : 0f,
                SpaceBeforeLines = isHeading ? 1.15f : SpaceBeforeLines,
                SpaceAfterLines = isHeading ? 1.25f : SpaceAfterLines,
                BaseStyle = isHeading ? BaseStyle with { Bold = true } : BaseStyle,
            };
        }

        public FlowContext ForLink(string? href)
        {
            if (string.IsNullOrWhiteSpace(href))
            {
                return this;
            }

            var lowered = href.ToLowerInvariant();
            return lowered.Contains("footnote") || lowered.Contains("endnote")
                ? this with
                {
                    FootnoteHref = href,
                    BaseStyle = BaseStyle with { NoWrap = true },
                }
                : this with { LinkHref = href };
        }

        public FlowContext WithStyle(TypesetInlineStyle style) => this with { BaseStyle = style };
    }
}
