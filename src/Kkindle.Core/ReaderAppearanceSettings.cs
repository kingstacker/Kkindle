using System.Text.Json.Serialization;

namespace Kkindle.Core;

public static class ReaderTocDefaults
{
    public const double DefaultFontSize = 14;
    public const double MinFontSize = 12;
    public const double MaxFontSize = 20;

    public static double NormalizeFontSize(double value) => double.IsFinite(value)
        ? Math.Clamp(value, MinFontSize, MaxFontSize)
        : DefaultFontSize;
}

public enum ReaderTheme
{
    Classic = 0,
    Beige = 1,
    Green = 2
}

public enum ReaderAnnotationDisplayMode
{
    Pinned = 0,
    Click = 1
}

/// <summary>Global paint preferences, deliberately separate from pagination settings.</summary>
public sealed record ReaderAppearanceSettings
{
    public const double DefaultPaperStrength = 0.45;

    public ReaderTheme Theme { get; init; } = ReaderTheme.Classic;
    public bool PaperEnabled { get; init; }
    public double PaperStrength { get; init; } = DefaultPaperStrength;
    public bool FibersEnabled { get; init; }
    public ReaderAnnotationDisplayMode AnnotationDisplayMode { get; init; } = ReaderAnnotationDisplayMode.Pinned;

    public static ReaderAppearanceSettings Normalize(ReaderAppearanceSettings? settings)
    {
        settings ??= new();
        return settings with
        {
            Theme = Enum.IsDefined(settings.Theme) ? settings.Theme : ReaderTheme.Classic,
            AnnotationDisplayMode = Enum.IsDefined(settings.AnnotationDisplayMode)
                ? settings.AnnotationDisplayMode
                : ReaderAnnotationDisplayMode.Pinned,
            PaperStrength = double.IsFinite(settings.PaperStrength)
                ? Math.Clamp(settings.PaperStrength, 0, 1)
                : DefaultPaperStrength
        };
    }

    // Keep the user's strength/fiber preferences when paper is disabled.
    [JsonIgnore]
    public double EffectivePaperStrength => PaperEnabled
        ? PaperStrength
        : 0;
}
