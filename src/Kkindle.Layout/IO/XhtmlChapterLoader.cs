using System.Text;
using System.Text.RegularExpressions;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Kkindle.Layout;

/// <summary>
/// Loads one sanitized spine XHTML chapter into typed blocks. Body text
/// offsets follow the same convention the WebKit bridge used: the verbatim
/// concatenation of every body text node in document order, so annotations
/// and search jumps captured against the live document keep pointing at the
/// same characters. Footnote definitions stay in the offset stream, but are
/// skipped by layout so their references can show the definition in a popup;
/// ruby phonetics remain marked ghost / skipped.
/// </summary>
public sealed class XhtmlChapterLoader
{
    private static readonly Regex FootnoteReferencePattern = new(
        @"(?:noteref|doc-noteref|footnote(?:[-_ ]?ref)?|endnote(?:[-_ ]?ref)?|note[-_ ]?ref|fn[-_ ]?ref)|(?:^|[#\s_-])(?:notes?|fn|ftn|footnotes?|zww?)[-_:]?\d*(?:n|ref)?(?:$|[\s#_-])",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FootnoteDefinitionTypePattern = new(
        @"(?:^|[\s:])(?:doc-)?(?:footnote|endnote)(?:$|[\s:])",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FootnoteDefinitionIdentityPattern = new(
        @"(?:^|[\s_-])(?:duokan-)?(?:footnotes?|endnotes?|fnote|notes?)(?:[\s_-]|\d|$)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FootnoteReferenceIdentityPattern = new(
        @"(?:noteref|doc-noteref|footnote[-_ ]?(?:ref|reference)|endnote[-_ ]?(?:ref|reference)|note[-_ ]?ref|fn[-_ ]?ref)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FormulaImageNamePattern = new(
        @"^w\d+$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex InlineEmHeightPattern = new(
        @"(?:^|;)\s*height\s*:\s*([0-9]+(?:\.[0-9]+)?)\s*em\s*(?:;|$)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly bool _paragraphIndent;
    private readonly StringBuilder _body = new();
    private readonly List<ContentBlock> _blocks = new();
    private readonly HashSet<string> _fragmentIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _fragmentTextOffsets = new(StringComparer.Ordinal);
    private readonly List<string> _pendingFragmentIds = new();
    private readonly List<InlineItem> _pending = new();
    private MicroCss? _css;
    private string _chapterPath = string.Empty;
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
        _fragmentTextOffsets.Clear();
        _pendingFragmentIds.Clear();
        _pending.Clear();
        _css = null;
        _chapterPath = Path.GetFullPath(chapterPath);
        _chapterDir = Path.GetDirectoryName(_chapterPath) ?? string.Empty;

        var document = LoadDocument(_chapterPath);
        if (document?.Root is null)
        {
            return new ChapterContent
            {
                ChapterPath = _chapterPath,
                BodyText = string.Empty,
                Blocks = Array.Empty<ContentBlock>(),
                FragmentIds = _fragmentIds,
                FragmentTextOffsets = _fragmentTextOffsets,
            };
        }

        _css = CollectStylesheets(document, _chapterDir);

        var body = document.Root
            .Descendants()
            .FirstOrDefault(e => e.Name.LocalName.Equals("body", StringComparison.OrdinalIgnoreCase));
        if (body is not null)
        {
            TrackId(body);
            WalkFlow(body, FlowContext.Root(_paragraphIndent));
            FlushParagraph(FlowContext.Root(_paragraphIndent));
        }

        PromoteLeadingTitle();

        return new ChapterContent
        {
            ChapterPath = _chapterPath,
            BodyText = _body.ToString(),
            Blocks = _blocks,
            FragmentIds = _fragmentIds,
            FragmentTextOffsets = _fragmentTextOffsets,
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

        var pathPart = StripQueryAndFragment(href);
        if (pathPart.Length == 0)
        {
            return null;
        }

        try
        {
            if (Uri.TryCreate(pathPart, UriKind.Absolute, out var absolute))
            {
                if (!absolute.IsFile)
                {
                    return null;
                }

                pathPart = absolute.LocalPath;
            }

            var decoded = Uri.UnescapeDataString(pathPart);
            return Path.GetFullPath(Path.Combine(baseDir, decoded));
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    private static string StripQueryAndFragment(string value)
    {
        var end = value.Length;
        var query = value.IndexOf('?');
        var fragment = value.IndexOf('#');
        if (query >= 0)
        {
            end = Math.Min(end, query);
        }

        if (fragment >= 0)
        {
            end = Math.Min(end, fragment);
        }

        return value[..end];
    }

    private string? ResolveHref(string? href)
    {
        if (string.IsNullOrWhiteSpace(href))
        {
            return null;
        }

        href = href.Trim();
        if (href.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)
            || href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            if (Uri.TryCreate(href, UriKind.Absolute, out var absolute)
                && !absolute.IsFile)
            {
                return absolute.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                    || absolute.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                    ? absolute.AbsoluteUri
                    : null;
            }

            var hash = href.IndexOf('#');
            var pathPart = hash >= 0 ? href[..hash] : href;
            var fragmentPart = hash >= 0 ? href[(hash + 1)..] : null;
            var targetPath = pathPart.Length == 0
                ? _chapterPath
                : ResolveLocalPath(pathPart, _chapterDir);
            if (targetPath is null)
            {
                return null;
            }

            var target = new Uri(Path.GetFullPath(targetPath), UriKind.Absolute);
            if (fragmentPart is null)
            {
                return target.AbsoluteUri;
            }

            try
            {
                fragmentPart = Uri.UnescapeDataString(fragmentPart);
            }
            catch (UriFormatException)
            {
                // Keep the raw fragment when a publisher emitted malformed
                // percent-encoding; the navigation layer will still fail
                // closed if it cannot resolve the target.
            }

            var builder = new UriBuilder(target) { Fragment = fragmentPart };
            return builder.Uri.AbsoluteUri;
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
        if (IsFootnoteDefinition(element))
        {
            FlushParagraph(ctx);
            PreserveFootnoteDefinition(element);
            return;
        }

        switch (local)
        {
            case "p":
                FlushParagraph(ctx);
                TrackId(element);
                WalkBlockParagraph(element, BlockKind.Paragraph, ctx);
                break;
            case "h1":
            case "h2":
            case "h3":
            case "h4":
            case "h5":
            case "h6":
                FlushParagraph(ctx);
                TrackId(element);
                WalkBlockParagraph(element, BlockKind.Heading, ctx);
                break;
            case "li":
                FlushParagraph(ctx);
                TrackId(element);
                WalkBlockParagraph(element, BlockKind.ListItem, ctx);
                break;
            case "dd":
            case "dt":
            case "figcaption":
            case "caption":
                FlushParagraph(ctx);
                TrackId(element);
                WalkBlockParagraph(element, BlockKind.Paragraph, ctx);
                break;
            case "blockquote":
                FlushParagraph(ctx);
                TrackId(element);
                WalkFlow(element, ctx.AsQuote());
                FlushParagraph(ctx.AsQuote());
                break;
            case "div":
                FlushParagraph(ctx);
                TrackId(element);
                var containerContext = ctx.WithContainerHints(ResolveHints(element));
                WalkFlow(element, containerContext);
                FlushParagraph(containerContext);
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
                TrackId(element);
                WalkFlow(element, ctx);
                break;
            case "hr":
                FlushParagraph(ctx);
                TrackId(element);
                _blocks.Add(new ContentBlock
                {
                    Kind = BlockKind.Rule,
                    ElementId = Id(element),
                    FragmentIds = TakePendingFragmentIds(),
                });
                break;
            case "img":
                if (ctx.FootnoteHref is not null || IsFootnoteImage(element))
                {
                    TrackId(element);
                    AddFootnoteMarker(element, ctx);
                }
                else if (IsInlineFormulaImage(element) || IsLeftQuoteImage(element))
                {
                    TrackId(element);
                    AddInlineImage(element, ctx);
                }
                else
                {
                    FlushParagraph(ctx);
                    TrackId(element);
                    AddImageBlock(element, ctx);
                }
                break;
            case "a":
                // Anchors are normally handled by WalkInlineElement inside a
                // paragraph. Some EPUBs put a cover/image anchor directly in
                // a figure or body, so carry its resolved target through the
                // flow walker as well.
                FlushParagraph(ctx);
                TrackId(element);
                WalkFlow(element, ctx.ForLink(ResolveHref((string?)element.Attribute("href")), element));
                FlushParagraph(ctx);
                break;
            case "style":
            case "script":
            case "head":
            case "title":
                break;
            case "svg":
                FlushParagraph(ctx);
                TrackId(element);
                AddSvgImageBlock(element, ctx);
                break;
            default:
                if (IsInlineTag(local))
                {
                    TrackId(element);
                    AppendInlineElement(element, ctx);
                }
                else
                {
                    FlushParagraph(ctx);
                    TrackId(element);
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
        foreach (var fragmentId in _pendingFragmentIds)
        {
            _fragmentTextOffsets.TryAdd(fragmentId, start);
        }

        if (value.Length > 0)
        {
            _pending.Add(new InlineItem
            {
                // Keep a linked footnote reference such as "[3]" together.
                // If it is emitted as ordinary text, the cell factory splits
                // the brackets and number into separate layout cells, which
                // breaks superscript alignment (especially in vertical mode).
                Kind = ctx.FootnoteHref is not null
                    ? InlineKind.FootnoteMarker
                    : InlineKind.Text,
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
        if (IsFootnoteDefinition(element))
        {
            FlushParagraph(ctx);
            PreserveFootnoteDefinition(element);
            return;
        }

        switch (local)
        {
            case "br":
                TrackId(element);
                _pending.Add(new InlineItem
                {
                    Kind = InlineKind.LineBreak,
                    TextStart = -1,
                    Style = ctx.BaseStyle,
                });
                break;
            case "img":
                if (ctx.FootnoteHref is not null || IsFootnoteImage(element))
                {
                    TrackId(element);
                    AddFootnoteMarker(element, ctx);
                }
                else if (IsInlineFormulaImage(element) || IsLeftQuoteImage(element))
                {
                    TrackId(element);
                    AddInlineImage(element, ctx);
                }
                else
                {
                    // FlushParagraph consumes fragment ids for the text that
                    // precedes an inline image. Track the image afterwards so
                    // an id on <img> (or on an enclosing link) resolves to the
                    // image page rather than the preceding paragraph.
                    FlushParagraph(ctx);
                    TrackId(element);
                    AddImageBlock(element, ctx);
                }
                break;
            case "svg":
                FlushParagraph(ctx);
                TrackId(element);
                AddSvgImageBlock(element, ctx);
                break;
            case "rt":
            case "rp":
                TrackId(element);
                AppendGhost(element);
                break;
            case "hr":
                FlushParagraph(ctx);
                TrackId(element);
                _blocks.Add(new ContentBlock
                {
                    Kind = BlockKind.Rule,
                    ElementId = Id(element),
                    FragmentIds = TakePendingFragmentIds(),
                });
                break;
            case "a":
            {
                TrackId(element);
                var href = ResolveHref((string?)element.Attribute("href"));
                var inner = ctx.ForLink(href, element);
                WalkInlineContent(element, inner);
                break;
            }
            default:
                TrackId(element);
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

        if (hints.VerticalTextCombineLimit >= 0)
        {
            style = style with { VerticalTextCombineLimit = hints.VerticalTextCombineLimit };
        }

        if (hints.VerticalTextOrientation is { } orientation)
        {
            style = style with { VerticalTextOrientation = orientation };
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

    private void PreserveFootnoteDefinition(XElement element)
    {
        var start = _body.Length;

        // An id can be pending because it belongs to the body or to a
        // wrapper immediately before the hidden definition. Keep those
        // anchors resolvable even though no visible block consumes them.
        foreach (var fragmentId in _pendingFragmentIds)
        {
            _fragmentTextOffsets.TryAdd(fragmentId, start);
        }

        var definitionIds = element
            .DescendantsAndSelf()
            .Select(Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        foreach (var fragmentId in definitionIds)
        {
            _fragmentIds.Add(fragmentId);
            _fragmentTextOffsets.TryAdd(fragmentId, start);
        }

        // Keep the exact text coordinate space used by annotations and
        // search, without creating any InlineItems for the footnote body.
        WalkGhost(element);
        _pendingFragmentIds.Clear();
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
            i.Kind == InlineKind.LineBreak ||
            i.Kind == InlineKind.FootnoteMarker);

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
            FragmentIds = TakePendingFragmentIds(),
            Items = items,
        });
    }

    private void AddFootnoteMarker(XElement element, FlowContext ctx)
    {
        var href = ctx.FootnoteHref ?? CreateInlineFootnoteHref(element);
        _pending.Add(new InlineItem
        {
            Kind = InlineKind.FootnoteMarker,
            Text = "注",
            TextStart = -1,
            Style = ctx.BaseStyle with
            {
                Superscript = true,
                NoWrap = true,
            },
            FootnoteHref = href,
            FootnoteText = GetFootnoteText(element),
        });
    }

    private string CreateInlineFootnoteHref(XElement element)
    {
        var fragment = Id(element);
        if (string.IsNullOrWhiteSpace(fragment))
        {
            fragment = $"__kkindle-inline-footnote-{_fragmentIds.Count}-{_body.Length}-{_pending.Count}";
        }

        return new UriBuilder(new Uri(_chapterPath, UriKind.Absolute))
        {
            Fragment = fragment,
        }.Uri.AbsoluteUri;
    }

    private static string? GetFootnoteText(XElement element)
    {
        var value = (string?)element.Attribute("alt")
            ?? (string?)element.Attribute("title")
            ?? (string?)element.Attribute("data-footnote-text");
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = string.Join(
            " ",
            value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= 1200 ? normalized : normalized[..1200] + "…";
    }

    private void AddImageBlock(XElement element, FlowContext? ctx = null)
    {
        var src = GetImageReference(element);
        var path = ResolveLocalPath(src ?? string.Empty, _chapterDir);
        path = ResolveRasterImagePath(path);
        if (path is null)
        {
            _pendingFragmentIds.Clear();
            return;
        }

        var id = Id(element);
        var hints = ResolveHints(element);
        _blocks.Add(new ContentBlock
        {
            Kind = BlockKind.Image,
            ElementId = id,
            FragmentIds = TakePendingFragmentIds(),
            Center = !IsLeftQuoteImage(element) && !(ctx?.AlignRight ?? false),
            AlignRight = ctx?.AlignRight == true || IsRightQuoteImage(element),
            SpaceBeforeLines = 1.8f * 1f / 1.8f,
            SpaceAfterLines = 1.8f / 1.8f,
            Items = new List<InlineItem>
            {
                new()
                {
                    Kind = InlineKind.Image,
                    ImagePath = path,
                    ImageWidthFactor = float.IsNaN(hints.ImageWidthFactor)
                        ? null
                        : hints.ImageWidthFactor,
                    DecorativeQuote = IsLeftQuoteImage(element) || IsRightQuoteImage(element),
                    Style = hints.Bold ? new TypesetInlineStyle(Bold: true) : new TypesetInlineStyle(),
                    LinkHref = ctx?.LinkHref,
                    FootnoteHref = ctx?.FootnoteHref,
                },
            },
        });
        _pending.Clear();
    }

    private void AddInlineImage(XElement element, FlowContext ctx)
    {
        var src = GetImageReference(element);
        var path = ResolveRasterImagePath(ResolveLocalPath(src ?? string.Empty, _chapterDir));
        if (path is null || !File.Exists(path))
        {
            return;
        }

        var hints = ResolveHints(element);
        _pending.Add(new InlineItem
        {
            Kind = InlineKind.Image,
            ImagePath = path,
            ImageHeightEm = GetImageHeightEm(element),
            ImageWidthFactor = float.IsNaN(hints.ImageWidthFactor)
                ? null
                : hints.ImageWidthFactor,
            DecorativeQuote = IsLeftQuoteImage(element) || IsRightQuoteImage(element),
            Style = ctx.BaseStyle with
            {
                Bold = ctx.BaseStyle.Bold || hints.Bold,
            },
            LinkHref = ctx.LinkHref,
            FootnoteHref = ctx.FootnoteHref,
        });
    }

    private void AddSvgImageBlock(XElement element, FlowContext ctx)
    {
        // Most EPUB SVGs are wrappers around a raster image. Resolve that
        // image directly so the native renderer does not need an SVG browser.
        var source = GetImageReference(element);
        if (source is null)
        {
            var image = element.Descendants()
                .FirstOrDefault(child => child.Name.LocalName.Equals("image", StringComparison.OrdinalIgnoreCase));
            source = image is null ? null : GetImageReference(image);
        }
        var path = ResolveRasterImagePath(ResolveLocalPath(source ?? string.Empty, _chapterDir));
        if (path is null)
        {
            // Preserve the SVG's text nodes in the offset stream even when a
            // vector-only illustration cannot be rasterized by SkiaCodec.
            WalkGhost(element);
            _pendingFragmentIds.Clear();
            return;
        }

        var id = Id(element);
        _blocks.Add(new ContentBlock
        {
            Kind = BlockKind.Image,
            ElementId = id,
            FragmentIds = TakePendingFragmentIds(),
            Center = true,
            SpaceBeforeLines = 1f,
            SpaceAfterLines = 1f,
            Items = new List<InlineItem>
            {
                new()
                {
                    Kind = InlineKind.Image,
                    ImagePath = path,
                    LinkHref = ctx.LinkHref,
                    FootnoteHref = ctx.FootnoteHref,
                },
            },
        });
        _pending.Clear();
    }

    private static string? GetImageReference(XElement element)
    {
        var direct = (string?)element.Attribute("src")
            ?? (string?)element.Attribute("data-src")
            ?? (string?)element.Attribute("href")
            ?? (string?)element.Attribute(XName.Get("href", "http://www.w3.org/1999/xlink"));
        if (!string.IsNullOrWhiteSpace(direct))
        {
            return direct;
        }

        foreach (var attributeName in new[] { "srcset", "data-srcset", "data-lazy-srcset", "data-original-set" })
        {
            var srcSet = (string?)element.Attribute(attributeName);
            if (string.IsNullOrWhiteSpace(srcSet))
            {
                continue;
            }

            var candidate = srcSet.Split(',')
                .Select(value => value.Trim())
                .Where(value => value.Length > 0)
                .Select(value => value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault())
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            if (candidate is not null)
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool IsInlineFormulaImage(XElement element)
    {
        var alt = ((string?)element.Attribute("alt") ?? string.Empty).Trim();
        if (FormulaImageNamePattern.IsMatch(alt))
        {
            return true;
        }

        var source = GetImageReference(element);
        if (string.IsNullOrWhiteSpace(source))
        {
            return false;
        }

        var cleanSource = source.Split('?', '#')[0];
        var name = Path.GetFileNameWithoutExtension(cleanSource);
        return FormulaImageNamePattern.IsMatch(name);
    }

    private static bool IsLeftQuoteImage(XElement element)
    {
        var classes = ((string?)element.Attribute("class") ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (classes.Any(value => value.Equals("yinhao_l", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var source = GetImageReference(element);
        var name = source is null
            ? string.Empty
            : Path.GetFileNameWithoutExtension(source.Split('?', '#')[0]);
        return name.Equals("yinhao-left", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRightQuoteImage(XElement element)
    {
        var classes = ((string?)element.Attribute("class") ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (classes.Any(value => value.Equals("yinhao_r", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var source = GetImageReference(element);
        var name = source is null
            ? string.Empty
            : Path.GetFileNameWithoutExtension(source.Split('?', '#')[0]);
        return name.Equals("yinhao-right", StringComparison.OrdinalIgnoreCase);
    }

    private static float? GetImageHeightEm(XElement element)
    {
        var style = (string?)element.Attribute("style");
        var match = style is null ? null : InlineEmHeightPattern.Match(style);
        if (match is not null && match.Success
            && float.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var height)
            && height > 0f)
        {
            return height;
        }

        var attribute = ((string?)element.Attribute("height"))?.Trim();
        if (attribute is not null
            && attribute.EndsWith("em", StringComparison.OrdinalIgnoreCase)
            && float.TryParse(attribute[..^2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out height)
            && height > 0f)
        {
            return height;
        }

        return null;
    }

    private static string? ResolveRasterImagePath(string? path)
    {
        if (path is null || !path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        try
        {
            var svg = XDocument.Load(path, LoadOptions.PreserveWhitespace);
            var baseDir = Path.GetDirectoryName(path) ?? string.Empty;
            foreach (var image in svg.Descendants().Where(e => e.Name.LocalName.Equals("image", StringComparison.OrdinalIgnoreCase)))
            {
                var reference = GetImageReference(image);
                var candidate = ResolveLocalPath(reference ?? string.Empty, baseDir);
                if (candidate is not null && File.Exists(candidate)
                    && !candidate.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or XmlException)
        {
            // Invalid SVGs remain non-rendering rather than aborting the whole
            // chapter composition.
        }

        return null;
    }

    private IReadOnlyList<string> TakePendingFragmentIds()
    {
        var ids = _pendingFragmentIds.ToArray();
        _pendingFragmentIds.Clear();
        return ids;
    }

    // ---- helpers ---------------------------------------------------------

    private static string? Id(XElement element) => (string?)element.Attribute("id");

    private static bool IsFootnoteImage(XElement element)
    {
        var markerAttribute = element.Attributes().Any(attribute =>
            attribute.Name.LocalName.Equals("data-footnote", StringComparison.OrdinalIgnoreCase)
            || attribute.Name.LocalName.Equals("data-footnote-text", StringComparison.OrdinalIgnoreCase)
            || attribute.Name.LocalName.Equals("data-note", StringComparison.OrdinalIgnoreCase));
        if (markerAttribute)
        {
            return true;
        }

        var classes = ((string?)element.Attribute("class") ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return classes.Any(value =>
            value.Contains("footnote", StringComparison.OrdinalIgnoreCase)
            || value.Contains("endnote", StringComparison.OrdinalIgnoreCase)
            || value.Equals("fnote", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsFootnoteDefinition(XElement element)
    {
        var semanticType = string.Join(
            " ",
            element.Attributes()
                .Where(attribute => attribute.Name.LocalName is "type" or "role")
                .Select(attribute => attribute.Value));
        if (FootnoteDefinitionTypePattern.IsMatch(semanticType))
        {
            return true;
        }

        var local = element.Name.LocalName;
        if (local is not ("aside" or "section" or "div" or "ol" or "ul" or "li"
            or "p" or "blockquote" or "article"))
        {
            return false;
        }

        var identity = string.Join(
            " ",
            element.Attributes()
                .Where(attribute => attribute.Name.LocalName is "class" or "id")
                .Select(attribute => attribute.Value));
        if (FootnoteReferenceIdentityPattern.IsMatch(identity))
        {
            return false;
        }

        return FootnoteDefinitionIdentityPattern.IsMatch(identity);
    }

    private void TrackId(XElement element)
    {
        var id = Id(element);
        if (!string.IsNullOrEmpty(id))
        {
            _fragmentIds.Add(id);
            _pendingFragmentIds.Add(id);
        }
    }

    private CssHints ResolveHints(XElement element)
    {
        var classes = ((string?)element.Attribute("class") ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var ancestorClasses = element.Ancestors()
            .SelectMany(ancestor => ((string?)ancestor.Attribute("class") ?? string.Empty)
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToArray();
        var hints = _css?.Resolve(
            element.Name.LocalName.ToLowerInvariant(),
            classes,
            Id(element),
            ancestorClasses) ?? CssHints.None;
        var inline = MicroCss.ParseInlineStyle((string?)element.Attribute("style"));

        return new CssHints
        {
            Bold = hints.Bold || inline.Bold,
            Italic = hints.Italic || inline.Italic,
            Center = hints.Center || inline.Center,
            AlignRight = hints.AlignRight || inline.AlignRight,
            NoWrap = hints.NoWrap || inline.NoWrap,
            VerticalTextCombineLimit = inline.VerticalTextCombineLimit >= 0
                ? inline.VerticalTextCombineLimit
                : hints.VerticalTextCombineLimit,
            VerticalTextOrientation = inline.VerticalTextOrientation ?? hints.VerticalTextOrientation,
            TextIndentEm = float.IsNaN(inline.TextIndentEm) ? hints.TextIndentEm : inline.TextIndentEm,
            FontSizeFactor = float.IsNaN(inline.FontSizeFactor) ? hints.FontSizeFactor : inline.FontSizeFactor,
            ImageWidthFactor = float.IsNaN(inline.ImageWidthFactor)
                ? hints.ImageWidthFactor
                : inline.ImageWidthFactor,
        };
    }

    private static bool IsInlineTag(string local) =>
        local is "span" or "a" or "b" or "strong" or "em" or "i" or "u" or "s"
            or "strike" or "del" or "ins" or "sub" or "sup" or "ruby" or "rt" or "rp"
            or "small" or "big" or "mark" or "q" or "cite" or "dfn" or "var" or "abbr"
            or "time" or "font" or "label" or "code" or "kbd" or "samp";

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

        // A short paragraph that starts with a decorative image is not a
        // chapter title. The dedication page uses a quote graphic followed by
        // one bold character; promoting that mixed inline line would center
        // and re-space the graphic instead of honoring the EPUB layout.
        if (first.Items.Any(item => item.Kind != InlineKind.Text || item.Ghost))
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
            FragmentIds = first.FragmentIds,
            Items = first.Items,
        };
    }

    private sealed record FlowContext
    {
        public BlockKind Kind { get; private init; }
        public bool IsQuote { get; private init; }
        public bool Center { get; private init; }
        public bool AlignRight { get; private init; }
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

        public FlowContext WithContainerHints(CssHints hints) => this with
        {
            Center = Center || hints.Center,
            AlignRight = AlignRight || hints.AlignRight,
            BaseStyle = BaseStyle with
            {
                VerticalTextCombineLimit = hints.VerticalTextCombineLimit >= 0
                    ? hints.VerticalTextCombineLimit
                    : BaseStyle.VerticalTextCombineLimit,
                VerticalTextOrientation = hints.VerticalTextOrientation ?? BaseStyle.VerticalTextOrientation,
            },
        };

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
                AlignRight = AlignRight || hints.AlignRight,
                Justify = !isHeading && !hints.Center,
                TextIndentEm = indented ? 2f : 0f,
                SpaceBeforeLines = isHeading ? 1.15f : SpaceBeforeLines,
                SpaceAfterLines = isHeading ? 1.25f : SpaceAfterLines,
                BaseStyle = (isHeading ? BaseStyle with { Bold = true } : BaseStyle) with
                {
                    VerticalTextCombineLimit = hints.VerticalTextCombineLimit >= 0
                        ? hints.VerticalTextCombineLimit
                        : BaseStyle.VerticalTextCombineLimit,
                    VerticalTextOrientation = hints.VerticalTextOrientation ?? BaseStyle.VerticalTextOrientation,
                },
            };
        }

        public FlowContext ForLink(string? href, XElement element)
        {
            var metadata = string.Join(
                " ",
                element.Attributes()
                    .Where(attribute => attribute.Name.LocalName is "type" or "role" or "rel" or "class" or "id")
                    .Select(attribute => attribute.Value));
            var reference = string.Join(" ", href, metadata);
            var isFootnoteReference = XhtmlChapterLoader.IsFootnoteReference(reference)
                || XhtmlChapterLoader.IsLegacyFootnoteReference(href, element);
            if (string.IsNullOrWhiteSpace(href) && !isFootnoteReference)
            {
                return this;
            }

            if (!isFootnoteReference)
            {
                return this with { LinkHref = href };
            }

            // The link on a footnote definition points back to the body
            // reference (for example id="notes1n" href="#notes1"). It is
            // still a link, but it is not a superscript reference marker.
            // Treating both directions alike made the definition's [1]
            // shrink and rise above the baseline, which is especially
            // noticeable on a page containing many notes.
            if (XhtmlChapterLoader.IsFootnoteDefinitionBacklink(href, element))
            {
                return this with
                {
                    LinkHref = href,
                    BaseStyle = BaseStyle with
                    {
                        NoWrap = true,
                        Superscript = false,
                    },
                };
            }

            return this with
            {
                FootnoteHref = href,
                BaseStyle = BaseStyle with
                {
                    NoWrap = true,
                    Superscript = true,
                },
            };
        }

        public FlowContext WithStyle(TypesetInlineStyle style) => this with { BaseStyle = style };
    }

    private static bool IsFootnoteReference(string value) =>
        !string.IsNullOrWhiteSpace(value) && FootnoteReferencePattern.IsMatch(value);

    private static bool IsLegacyFootnoteReference(string? href, XElement element)
    {
        var targetFragment = GetHrefFragment(href);
        if (!HasNumericIdPrefix(targetFragment, 'm'))
        {
            return false;
        }

        if (HasNumericIdPrefix(Id(element), 'w'))
        {
            return true;
        }

        // Some converted EPUBs put the body anchor in an empty sibling just
        // before the actual link, for example: <a id="w1"></a><a
        // href="#m1"><sup>[1]</sup></a>.
        var previousAnchor = element.ElementsBeforeSelf()
            .Reverse()
            .FirstOrDefault(candidate => candidate.Name.LocalName.Equals("a", StringComparison.OrdinalIgnoreCase));
        return previousAnchor is not null && HasNumericIdPrefix(Id(previousAnchor), 'w');
    }

    private static bool HasNumericIdPrefix(string? value, char prefix)
    {
        return value is { Length: > 1 }
            && value[0] == prefix
            && value[1..].All(char.IsDigit);
    }

    private static bool IsFootnoteDefinitionBacklink(string? href, XElement element)
    {
        var id = Id(element);
        var targetFragment = GetHrefFragment(href);
        return id is { Length: > 1 }
            && id.EndsWith('n')
            && targetFragment is { Length: > 0 }
            && !targetFragment.EndsWith('n');
    }

    private static string? GetHrefFragment(string? href)
    {
        if (string.IsNullOrWhiteSpace(href)) return null;
        var hash = href.IndexOf('#');
        if (hash < 0 || hash == href.Length - 1)
        {
            return null;
        }

        var fragment = href[(hash + 1)..];
        try
        {
            return Uri.UnescapeDataString(fragment);
        }
        catch (UriFormatException)
        {
            return fragment;
        }
    }
}
