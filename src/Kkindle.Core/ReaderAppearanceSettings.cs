using System.Text.Json.Serialization;

namespace Kkindle.Core;

public enum ReaderTheme
{
    Classic = 0,
    Night = 1,
    Green = 2,
    WarmBrown = 3,
    Ivory = 4
}

/// <summary>Global paint preferences, deliberately separate from pagination settings.</summary>
public sealed record ReaderAppearanceSettings
{
    public const double DefaultPaperStrength = 0.45;

    public ReaderTheme Theme { get; init; } = ReaderTheme.Classic;
    public bool PaperEnabled { get; init; }
    public double PaperStrength { get; init; } = DefaultPaperStrength;
    public bool FibersEnabled { get; init; }

    public static ReaderAppearanceSettings Normalize(ReaderAppearanceSettings? settings)
    {
        settings ??= new();
        return settings with
        {
            Theme = Enum.IsDefined(settings.Theme) ? settings.Theme : ReaderTheme.Classic,
            PaperStrength = double.IsFinite(settings.PaperStrength)
                ? Math.Clamp(settings.PaperStrength, 0, 1)
                : DefaultPaperStrength
        };
    }

    // Keep the user's strength/fiber preferences when paper is disabled. Night
    // paper uses less contrast without changing the saved slider position.
    [JsonIgnore]
    public double EffectivePaperStrength => PaperEnabled
        ? PaperStrength * (Theme == ReaderTheme.Night ? 0.38 : 1)
        : 0;
}
