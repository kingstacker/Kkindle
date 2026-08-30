using HarfBuzzSharp;
using SkiaSharp;

namespace Kkindle.Layout;

/// <summary>
/// Owns the bundled font faces. HarfBuzz faces are created per file and reused;
/// glyph indices HarfBuzz reports are valid for Skia because both libraries
/// read the same font file.
/// </summary>
public sealed class TypesetFontLibrary : IDisposable
{
    private sealed class FontFaceEntry : IDisposable
    {
        public required string Path { get; init; }
        public required Blob Blob { get; init; }
        public required Face Face { get; init; }
        public required Font Font { get; init; }
        public required SKTypeface Typeface { get; init; }
        public required int Upem { get; init; }

        public void Dispose()
        {
            Font.Dispose();
            Face.Dispose();
            Blob.Dispose();
            Typeface.Dispose();
        }
    }

    private readonly Dictionary<string, FontFaceEntry> _faces = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _order = new();
    private bool _disposed;

    public string MainFontPath { get; }

    public TypesetFontLibrary(string mainFontPath, IEnumerable<string>? fallbackFontPaths = null)
    {
        MainFontPath = mainFontPath;
        Register(mainFontPath);
        if (fallbackFontPaths is not null)
        {
            foreach (var path in fallbackFontPaths)
            {
                Register(path);
            }
        }
    }

    private void Register(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || _faces.ContainsKey(path))
        {
            return;
        }

        var blob = HarfBuzzSharp.Blob.FromFile(path);
        var face = new Face(blob, 0);
        var font = new Font(face);
        var typeface = SKTypeface.FromFile(path, 0);
        // Scale stays at units-per-em; the shaper converts to pixels so the
        // conversion is one explicit float multiply and identical on all
        // platforms. Upem comes from the same file Skia reads.
        var upem = typeface.UnitsPerEm;
        font.SetScale(upem, upem);
        var entry = new FontFaceEntry
        {
            Path = path,
            Blob = blob,
            Face = face,
            Font = font,
            Typeface = typeface,
            Upem = upem,
        };
        _faces[path] = entry;
        _order.Add(path);
    }

    public SKTypeface GetTypeface(string fontPath) => Entry(fontPath).Typeface;

    public int GetUpem(string fontPath) => Entry(fontPath).Upem;

    internal Font GetHarfBuzzFont(string fontPath) => Entry(fontPath).Font;

    internal IEnumerable<string> FontPaths => _order;

    private FontFaceEntry Entry(string fontPath) =>
        _faces.TryGetValue(fontPath, out var entry)
            ? entry
            : throw new InvalidOperationException($"Font not registered: {fontPath}");

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var entry in _faces.Values)
        {
            entry.Dispose();
        }

        _faces.Clear();
        _order.Clear();
    }
}

public sealed class ShapedText
{
    public required ushort[] GlyphIds { get; init; }
    /// <summary>Per-glyph index into the shaped substring.</summary>
    public required int[] Clusters { get; init; }
    /// <summary>Per-glyph advance in pixels along the shaping direction.</summary>
    public required float[] Advances { get; init; }
    public required float[] OffsetsX { get; init; }
    public required float[] OffsetsY { get; init; }
    public required float TotalAdvance { get; init; }
}

/// <summary>
/// Shapes text with HarfBuzz and converts advances to device-independent
/// pixels. The same input, font file and library versions always produce the
/// same output regardless of platform, which is the basis of cross-platform
/// pagination equality.
/// </summary>
public sealed class GlyphShaper
{
    private readonly TypesetFontLibrary _library;

    // HarfBuzz normally enables `vert` as a side effect of a vertical buffer
    // direction. Keep it explicit so vertical presentation forms are selected
    // consistently when the same EPUB is laid out on different hosts.
    private static readonly Feature VerticalFeature = Feature.Parse("vert");

    public GlyphShaper(TypesetFontLibrary library)
    {
        _library = library;
    }

    public ShapedText Shape(
        string text,
        int start,
        int length,
        string fontPath,
        float fontSize,
        bool vertical = false)
    {
        if (length <= 0)
        {
            return new ShapedText
            {
                GlyphIds = Array.Empty<ushort>(),
                Clusters = Array.Empty<int>(),
                Advances = Array.Empty<float>(),
                OffsetsX = Array.Empty<float>(),
                OffsetsY = Array.Empty<float>(),
                TotalAdvance = 0f,
            };
        }

        var font = _library.GetHarfBuzzFont(fontPath);
        var upem = _library.GetUpem(fontPath);

        using var buffer = new HarfBuzzSharp.Buffer();
        buffer.AddUtf16(text.AsSpan(start, length));
        buffer.GuessSegmentProperties();
        buffer.Direction = vertical ? Direction.TopToBottom : Direction.LeftToRight;
        if (vertical)
        {
            font.Shape(buffer, new[] { VerticalFeature });
        }
        else
        {
            font.Shape(buffer);
        }
        var infos = buffer.GlyphInfos.ToArray();
        var positions = buffer.GlyphPositions.ToArray();
        var count = infos.Length;
        var glyphIds = new ushort[count];
        var clusters = new int[count];
        var advances = new float[count];
        var offsetsX = new float[count];
        var offsetsY = new float[count];
        var scale = fontSize / upem;
        float total = 0f;

        for (var i = 0; i < count; i++)
        {
            var info = infos[i];
            var position = positions[i];
            glyphIds[i] = (ushort)info.Codepoint;
            clusters[i] = (int)info.Cluster;
            var advance = vertical ? position.YAdvance : position.XAdvance;
            advances[i] = advance * scale;
            offsetsX[i] = position.XOffset * scale;
            offsetsY[i] = position.YOffset * scale;
            total += advances[i];
        }

        return new ShapedText
        {
            GlyphIds = glyphIds,
            Clusters = clusters,
            Advances = advances,
            OffsetsX = offsetsX,
            OffsetsY = offsetsY,
            TotalAdvance = total,
        };
    }

    /// <summary>
    /// Resolves the glyph used for one upright vertical cell. Shaping a single
    /// character with a top-to-bottom direction selects the font's vertical
    /// presentation form (the OpenType vert feature) when it has one.
    /// </summary>
    public ushort GetVerticalGlyphId(char character, string fontPath, out bool isNotdef) =>
        GetVerticalGlyphId(character.ToString(), fontPath, out isNotdef);

    public ushort GetVerticalGlyphId(string text, string fontPath, out bool isNotdef)
    {
        var shaped = Shape(text, 0, text.Length, fontPath, 1000f, vertical: true);
        isNotdef = shaped.GlyphIds.Length == 0 || shaped.GlyphIds[0] == 0;
        return shaped.GlyphIds.Length == 0 ? (ushort)0 : shaped.GlyphIds[0];
    }

    /// <summary>
    /// True when a standalone punctuation mark has no usable vertical
    /// presentation glyph in the selected face and must be rotated.
    /// </summary>
    public bool NeedsVerticalRotation(char character, string fontPath) =>
        NeedsVerticalRotation(character.ToString(), fontPath);

    public bool NeedsVerticalRotation(string text, string fontPath)
    {
        if (!TypesetText.TryGetFirstScalar(text, out var scalar))
        {
            return false;
        }

        // This engine deliberately keeps the established reader behavior for
        // isolated ASCII digits: one digit is an upright cell and two digits
        // are tate-chu-yoko. Longer numeric/Latin runs are already marked as
        // sideways by VerticalTextUnits before this method is reached.
        if (scalar <= char.MaxValue && char.IsAsciiLetterOrDigit((char)scalar))
        {
            return false;
        }

        if (!TypesetText.IsVerticalTransformed(text))
        {
            // UAX #50 defaults unlisted non-CJK characters (including ASCII
            // semicolon, tilde, brackets and U+2014) to R: rotate them 90°
            // clockwise in mixed vertical text. CJK-native marks are upright
            // unless they are one of the explicit rotated fullwidth forms.
            return TypesetText.ShouldRotateInVertical(text);
        }

        // Tr characters are upright only after a typographic vertical
        // transformation. If the font has no alternate glyph, use the CSS
        // fallback of a clockwise rotation rather than leaving a horizontal
        // glyph in the vertical line.
        var horizontal = Shape(text, 0, text.Length, fontPath, 1000f, vertical: false);
        var vertical = Shape(text, 0, text.Length, fontPath, 1000f, vertical: true);
        if (vertical.GlyphIds.Length == 0 || vertical.GlyphIds[0] == 0)
        {
            return true;
        }

        return horizontal.GlyphIds.Length == 0
            || horizontal.GlyphIds[0] == 0
            || horizontal.GlyphIds.SequenceEqual(vertical.GlyphIds);
    }

    /// <summary>True when the font file covers the character (non-notdef glyph).</summary>
    public bool Covers(string fontPath, char character) =>
        Covers(fontPath, character.ToString());

    public bool Covers(string fontPath, string text)
    {
        var shaped = Shape(text, 0, text.Length, fontPath, 1000f, vertical: false);
        return shaped.GlyphIds.Length > 0 && shaped.GlyphIds[0] != 0;
    }

}
