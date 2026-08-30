using SkiaSharp;

namespace Kkindle.Layout;

public enum TypesetWritingMode
{
    HorizontalTb,
    VerticalRl,
}

public enum BlockKind
{
    Paragraph,
    Heading,
    ListItem,
    Blockquote,
    Image,
    Rule,
}

public enum InlineKind
{
    Text,
    LineBreak,
    Image,
    FootnoteMarker,
}

public enum TypesetVerticalOrientation
{
    Mixed,
    Upright,
    Sideways,
}

public enum HotZoneKind
{
    Link,
    FootnoteMarker,
}

/// <summary>
/// Resolved inline emphasis. The engine has no cascade; the loader resolves
/// everything up front so layout and paint are pure functions of these values.
/// </summary>
public readonly record struct TypesetInlineStyle(
    bool Bold = false,
    bool Italic = false,
    bool Underline = false,
    bool Strikeout = false,
    bool Superscript = false,
    bool NoWrap = false)
{
    /// <summary>
    /// Null means that the document did not specify text-combine-upright;
    /// the reader's publication default is two digits.
    /// </summary>
    public int? VerticalTextCombineLimit { get; init; }

    /// <summary>Null means mixed orientation, subject to the reader default.</summary>
    public TypesetVerticalOrientation? VerticalTextOrientation { get; init; }
}

/// <summary>
/// One piece of inline content in document order. Non-text items still carry
/// <see cref="TextStart"/> == -1; text items carry the offset of their first
/// character in <see cref="ChapterContent.BodyText"/>, the same coordinate
/// space the annotations and whole-book search jumps persist.
/// </summary>
public sealed class InlineItem
{
    public InlineKind Kind { get; init; } = InlineKind.Text;
    public string Text { get; init; } = string.Empty;
    public int TextStart { get; init; } = -1;
    public TypesetInlineStyle Style { get; init; }
    public string? ImagePath { get; init; }
    /// <summary>Requested inline image height in em units, when the EPUB provides one.</summary>
    public float? ImageHeightEm { get; init; }
    /// <summary>Requested image width as a fraction of the content width.</summary>
    public float? ImageWidthFactor { get; init; }
    /// <summary>Decorative quote artwork uses a readable font-relative cap on wide pages.</summary>
    public bool DecorativeQuote { get; init; }
    public string? LinkHref { get; init; }
    public string? FootnoteHref { get; init; }
    /// <summary>Inline note text for publishers that encode a footnote in an image alt attribute.</summary>
    public string? FootnoteText { get; init; }
    /// <summary>Ghost text is part of the offset stream (ruby rt) but never rendered.</summary>
    public bool Ghost { get; init; }
}

/// <summary>
/// A resolved block (paragraph, heading, image, ...). Block-level metrics are
/// expressed in body-line units so both writing modes share one grid.
/// </summary>
public sealed class ContentBlock
{
    public BlockKind Kind { get; init; } = BlockKind.Paragraph;
    public string? ElementId { get; init; }
    /// <summary>All fragment ids contained by this block, including inline anchors.</summary>
    public IReadOnlyList<string> FragmentIds { get; init; } = Array.Empty<string>();
    public TypesetInlineStyle Style { get; init; }
    public bool Center { get; init; }
    public bool AlignRight { get; init; }
    public bool Justify { get; init; }
    public float TextIndentEm { get; init; }
    public float SpaceBeforeLines { get; init; }
    public float SpaceAfterLines { get; init; }
    public List<InlineItem> Items { get; init; } = new();
}

/// <summary>
/// A chapter reduced to typed blocks. <see cref="BodyText"/> is the verbatim
/// concatenation of every body text node, including footnote definitions, so
/// character offsets stay compatible with annotations that were captured
/// against the live document's textContent.
/// </summary>
public sealed class ChapterContent
{
    public required string ChapterPath { get; init; }
    public required string BodyText { get; init; }
    public required IReadOnlyList<ContentBlock> Blocks { get; init; }
    /// <summary>Element ids that must still answer fragment navigation.</summary>
    public IReadOnlySet<string> FragmentIds { get; init; } = new HashSet<string>();
    /// <summary>Text offsets at which visible fragment ids begin, when they have one.</summary>
    public IReadOnlyDictionary<string, int> FragmentTextOffsets { get; init; } =
        new Dictionary<string, int>(StringComparer.Ordinal);
}

public sealed class TypesetLayoutOptions
{
    public TypesetWritingMode WritingMode { get; init; } = TypesetWritingMode.HorizontalTb;
    /// <summary>Base body font size in device-independent pixels (1rem).</summary>
    public float BaseFontSize { get; init; } = 17f;
    public float LineHeight { get; init; } = 1.8f;
    public float LetterSpacingEm { get; init; } = 0.012f;
    public bool ParagraphIndent { get; init; } = true;
    public float ViewportWidth { get; init; } = 800f;
    public float ViewportHeight { get; init; } = 600f;
    public float InsetHorizontal { get; init; } = 24f;
    public float InsetVertical { get; init; } = 24f;

    public float BodyLineHeight => BaseFontSize * LineHeight;
    public float ContentWidth => Math.Max(1f, ViewportWidth - InsetHorizontal * 2f);
    public float ContentHeight => Math.Max(1f, ViewportHeight - InsetVertical * 2f);
}

public enum DecorationKind
{
    Underline,
    Strikeout,
    BlockquoteBar,
    Rule,
    Selection,
    Highlight,
    SearchMark,
}

public sealed class PlacedRect
{
    public required DecorationKind Kind { get; init; }
    public required SKRect Rect { get; init; }
    /// <summary>Global body-text offset this decoration starts at, or -1 for structural rules.</summary>
    public int TextStart { get; init; } = -1;
    public int TextLength { get; init; }
}

/// <summary>
/// A persisted reader annotation projected onto the visible page. The layout
/// package deliberately receives only geometry and paint information here so
/// the native painter does not depend on the application's data layer.
/// </summary>
public sealed class TypesetAnnotationOverlay
{
    public required IReadOnlyList<SKRect> Bands { get; init; }
    public required string Style { get; init; }
    public required SKColor Color { get; init; }
}

public enum TypesetDebugBoxKind
{
    HanCell,
    CompatibilityCell,
    Glyph,
}

/// <summary>One diagnostic frame emitted by the native vertical composer.</summary>
public readonly record struct TypesetDebugBox(SKRect Rect, TypesetDebugBoxKind Kind);

public sealed class PlacedImage
{
    public required string Path { get; init; }
    public required SKRect Rect { get; init; }
    /// <summary>Clockwise rotation applied by the painter, used by vertical inline formulas.</summary>
    public float RotationDegrees { get; init; }
    public string? LinkHref { get; init; }
    public string? FootnoteHref { get; init; }
    public string? FootnoteText { get; init; }
}

public sealed class PlacedHotZone
{
    public required HotZoneKind Kind { get; init; }
    public required SKRect Rect { get; init; }
    public required string Href { get; init; }
    public string? FootnoteText { get; init; }
}

/// <summary>
/// A shaped glyph run placed on a page. Positions are relative to
/// <see cref="OriginX"/>/<see cref="OriginY"/> so the painter can rotate the
/// whole run for sideways vertical text. Glyph ids index the font file named
/// by <see cref="FontPath"/> (HarfBuzz and Skia agree on indices for the same
/// file).
/// </summary>
public sealed record PlacedRun
{
    public required string FontPath { get; init; }
    public required float FontSize { get; init; }
    public required ushort[] Glyphs { get; init; }
    /// <summary>Per-glyph pen positions relative to the run origin.</summary>
    public required float[] X { get; init; }
    public required float[] Y { get; init; }
    public float OriginX { get; init; }
    public float OriginY { get; init; }
    /// <summary>Advance along the flow axis (width horizontally, height vertically).</summary>
    public float FlowAdvance { get; init; }
    /// <summary>Vertical mode only: the run is shaped horizontally and painted rotated 90° clockwise.</summary>
    public bool Sideways { get; init; }
    public bool SyntheticBold { get; init; }
    /// <summary>Uniform paint scale for combined (tate-chu-yoko) digit cells.</summary>
    public float Scale { get; init; } = 1f;
    /// <summary>
    /// Width of an upright vertical cell in the cross-flow direction. Zero
    /// keeps the legacy glyph-bounds behavior used by horizontal and sideways
    /// runs.
    /// </summary>
    public float CellWidth { get; init; }
    public required int TextStart { get; init; }
    public required int TextLength { get; init; }
    /// <summary>Per-glyph offset from <see cref="TextStart"/> into the chapter text.</summary>
    public int[] Clusters { get; init; } = Array.Empty<int>();
    public TypesetInlineStyle Style { get; init; }
}

/// <summary>One laid-out page. Immutable once produced.</summary>
public sealed class LayoutPage
{
    public required int Index { get; init; }
    public required TypesetWritingMode WritingMode { get; init; }
    public required float Width { get; init; }
    public required float Height { get; init; }
    public required float InsetHorizontal { get; init; }
    public required float InsetVertical { get; init; }
    /// <summary>Global body-text offset of the first text on the page (-1 when the page has no text).</summary>
    public int TextStartOffset { get; set; } = -1;
    /// <summary>Exclusive end offset, or -1 when the page has no text.</summary>
    public int TextEndOffset { get; set; } = -1;
    public bool EndOfChapter { get; init; }
    public List<PlacedRun> Runs { get; init; } = new();
    public List<PlacedImage> Images { get; init; } = new();
    public List<PlacedRect> Decorations { get; init; } = new();
    public List<PlacedHotZone> HotZones { get; init; } = new();
    public List<TypesetDebugBox> DebugBoxes { get; init; } = new();
}

public sealed class ChapterLayout
{
    public required IReadOnlyList<LayoutPage> Pages { get; init; }
    public required int BodyTextLength { get; init; }
    /// <summary>Element id → page index for fragment navigation.</summary>
    public required IReadOnlyDictionary<string, int> FragmentPages { get; init; }
    public required TypesetLayoutOptions Options { get; init; }
}
