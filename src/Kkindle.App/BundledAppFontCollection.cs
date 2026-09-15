using System.Diagnostics.CodeAnalysis;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Platform;

namespace Kkindle;

/// <summary>Creates UI font variants from the bundled font resource.</summary>
public sealed class BundledAppFontCollection : EmbeddedFontCollection
{
    private static readonly Uri CollectionUri = new("fonts:Kkindle");
    private static readonly Uri SourceUri = new("avares://Kkindle.App/Assets/Fonts/KingHwaOldSong-v3.0.ttf");
    private readonly IFontManagerImpl _fontManager = AvaloniaLocator.Current.GetRequiredService<IFontManagerImpl>();

    public BundledAppFontCollection() : base(CollectionUri, SourceUri)
    {
    }

    public override bool TryCreateSyntheticGlyphTypeface(
        GlyphTypeface glyphTypeface,
        FontStyle style,
        FontWeight weight,
        FontStretch stretch,
        [NotNullWhen(true)] out GlyphTypeface? syntheticGlyphTypeface)
    {
        syntheticGlyphTypeface = null;
        if (!string.Equals(glyphTypeface.FamilyName, "KingHwaOldSong", StringComparison.OrdinalIgnoreCase)
            || (glyphTypeface.Style == style && glyphTypeface.Weight == weight && glyphTypeface.Stretch == stretch))
            return false;

        // This collection contains one regular face. Derive both simulations
        // from that source even when the nearest cached face is already bold
        // or italic, so requesting both does not discard the existing style.
        var simulations = FontSimulations.None;
        if (style != FontStyle.Normal) simulations |= FontSimulations.Oblique;
        if ((int)weight >= 600) simulations |= FontSimulations.Bold;
        if (simulations == FontSimulations.None) return false;

        // Avalonia 12.1.1 reopens the Skia typeface stream for synthesis. That
        // native read can crash during GC (see FontLifetimeTests). Reopening
        // our original managed asset avoids that path and owns its lifetime.
        using var stream = AssetLoader.Open(SourceUri);
        if (!_fontManager.TryCreateGlyphTypeface(stream, simulations, out var platformTypeface))
            return false;

        GlyphTypeface created;
        try
        {
            created = new GlyphTypeface(platformTypeface, simulations);
        }
        catch
        {
            platformTypeface.Dispose();
            throw;
        }

        if (TryAddGlyphTypeface(created, new FontCollectionKey(style, weight, stretch)))
        {
            syntheticGlyphTypeface = created;
            return true;
        }

        // Another request may have populated this variant while it was being
        // created. Keep the cached instance and release the duplicate.
        created.Dispose();
        return TryGetGlyphTypeface(glyphTypeface.FamilyName, style, weight, stretch, out syntheticGlyphTypeface);
    }
}
