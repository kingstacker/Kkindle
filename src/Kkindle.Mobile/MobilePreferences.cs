using System.Text.Json;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle.Mobile;

public enum MobileThemeMode
{
    System = 0,
    Light = 1,
    Dark = 2
}

public enum MobileBookSort
{
    Recent = 0,
    Title = 1,
    Author = 2,
    Added = 3
}

public enum MobilePageAnimation
{
    None = 0,
    Fade = 1,
    Slide = 2,
    Wave = 3
}

/// <summary>Phone-only interface choices; shared library data stays in the database.</summary>
public sealed record MobilePreferences
{
    public MobileThemeMode ThemeMode { get; init; }
    public bool ListView { get; init; }
    /// <summary>Grid gallery: show covers only, hiding title, author and progress.</summary>
    public bool GalleryMode { get; init; }
    /// <summary>Use colours sampled from book covers for the native app background.</summary>
    public bool ColorfulMode { get; init; }
    public bool ShowBookSyncStatus { get; init; }
    public bool CollectionsView { get; init; }
    public double PdfZoom { get; init; } = 1;
    public MobileBookSort Sort { get; init; }
    public ReaderTheme ReaderTheme { get; init; }
    /// <summary>Follow the app's effective light or dark appearance for reader paper.</summary>
    public bool ReaderThemeFollowsAppearance { get; init; } = true;
    public double ReaderFontScale { get; init; } = 1.35;
    public double ReaderLineHeight { get; init; } = 1.75;
    public double ReaderParagraphSpacing { get; init; } = ReaderLayoutDefaults.DefaultParagraphSpacing;
    /// <summary>Side margin of the reading column, in device-independent pixels.</summary>
    public double ReaderBodyPadding { get; init; } = ReaderLayoutDefaults.DefaultBodyPadding;
    /// <summary>Indent the first line of each paragraph by two characters.</summary>
    public bool ReaderParagraphIndent { get; init; }
    /// <summary>Vertical right-to-left typesetting, the traditional CJK layout.</summary>
    public bool ReaderVerticalWriting { get; init; }
    public MobilePageAnimation ReaderPageAnimation { get; init; } = MobilePageAnimation.Fade;

    public ReaderTheme ResolveReaderTheme(bool darkAppearance) => ReaderThemeFollowsAppearance
        ? (darkAppearance ? Kkindle.Core.ReaderTheme.Ink : Kkindle.Core.ReaderTheme.Classic)
        : ReaderTheme;
}

/// <summary>
/// Reads on the UI thread at startup, so it stays a small synchronous JSON file.
/// A damaged file only loses interface choices and must never block launch.
/// </summary>
public sealed class MobilePreferencesStore(AppPaths paths)
{
    private readonly string _path = Path.Combine(paths.Root, "mobile-preferences.json");

    public MobilePreferences Current { get; private set; } = new();

    public void Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(_path));
                var root = document.RootElement;
                var current = root.Deserialize<MobilePreferences>() ?? new();
                // Older settings cannot tell whether the paper was chosen manually.
                // Preserve their existing appearance instead of changing it on upgrade.
                if (root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty(nameof(MobilePreferences.ReaderTheme), out _)
                    && !root.TryGetProperty(nameof(MobilePreferences.ReaderThemeFollowsAppearance), out _))
                    current = current with { ReaderThemeFollowsAppearance = false };
                Current = current;
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException
            or UnauthorizedAccessException)
        {
            Current = new();
        }
    }

    public void Update(Func<MobilePreferences, MobilePreferences> change)
    {
        Current = change(Current);
        try
        {
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(Current));
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The choice still applies for this session.
        }
    }
}
