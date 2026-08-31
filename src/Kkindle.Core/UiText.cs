using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Kkindle.Core;

/// <summary>
/// Small UI-localization boundary shared by the core presentation models and
/// the Avalonia application. The core project does not know about Avalonia
/// resources; the app supplies the resource resolver during startup.
/// </summary>
public static class UiText
{
    private static Func<string, string?>? _resolver;
    private static CultureInfo _culture = CultureInfo.GetCultureInfo(DetectSystemLanguage());

    public static string CurrentLanguage { get; private set; } = DetectSystemLanguage();

    public static CultureInfo CurrentCulture => _culture;

    public static bool IsEnglish => CurrentLanguage.Equals("en-US", StringComparison.Ordinal);

    public static event EventHandler? LanguageChanged;

    /// <summary>
    /// Returns the closest supported UI language for the current operating
    /// system. Kkindle currently ships Simplified Chinese and English; other
    /// system languages fall back to English.
    /// </summary>
    public static string DetectSystemLanguage()
    {
        var cultures = new[] { CultureInfo.CurrentUICulture, CultureInfo.CurrentCulture };
        return cultures.Any(culture => culture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            ? "zh-CN"
            : "en-US";
    }

    public static void Configure(Func<string, string?> resolver, string? language = null)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        _resolver = resolver;
        SetLanguage(language ?? DetectSystemLanguage());
    }

    public static string NormalizeLanguage(string? language)
    {
        var normalized = language?.Trim();
        if (normalized?.StartsWith("en", StringComparison.OrdinalIgnoreCase) == true)
            return "en-US";
        if (normalized?.StartsWith("zh", StringComparison.OrdinalIgnoreCase) == true)
            return "zh-CN";
        return DetectSystemLanguage();
    }

    public static void SetLanguage(string? language)
    {
        var normalized = NormalizeLanguage(language);
        var changed = !string.Equals(CurrentLanguage, normalized, StringComparison.Ordinal);
        CurrentLanguage = normalized;
        _culture = CultureInfo.GetCultureInfo(normalized);
        if (changed)
            LanguageChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>
    /// Resolves a Chinese source template through the active resource set.
    /// Keeping the source as the fallback makes old settings and unit tests
    /// safe even before the Avalonia resource resolver has been configured.
    /// </summary>
    public static string Get(string source, params object?[] args)
    {
        ArgumentNullException.ThrowIfNull(source);
        var translated = _resolver?.Invoke(source);
        var text = string.IsNullOrWhiteSpace(translated) ? source : translated;
        if (args.Length == 0) return text;

        try
        {
            return string.Format(CurrentCulture, text, args);
        }
        catch (FormatException)
        {
            // A malformed external translation must not break an operation or
            // hide the original status text.
            return text;
        }
    }

    /// <summary>
    /// Translates an already-composed UI string when it is present as an exact
    /// resource entry. This is useful at UI boundaries for messages produced
    /// by lower-level services; templated messages should use <see cref="Get"/>
    /// at the call site instead.
    /// </summary>
    public static string Localize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var translated = _resolver?.Invoke(value);
        return string.IsNullOrWhiteSpace(translated) ? value : translated;
    }

    /// <summary>
    /// Stable resource key for a source string. Hash keys keep XAML markup
    /// compact even for long explanatory sentences and avoid punctuation
    /// escaping problems in Avalonia markup extensions.
    /// </summary>
    public static string ResourceKey(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(source));
        return $"Ui.Auto.{Convert.ToHexString(hash)[..16]}";
    }
}
