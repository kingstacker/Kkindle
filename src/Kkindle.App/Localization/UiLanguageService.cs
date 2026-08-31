using Avalonia;
using Avalonia.Markup.Xaml.Styling;
using Kkindle.Core;

namespace Kkindle;

/// <summary>
/// Loads the language resource dictionary used by DynamicResource bindings and
/// connects it to the culture-neutral UiText facade used by code-behind and
/// presentation models.
/// </summary>
public sealed class UiLanguageService
{
    private const string ResourcePrefix = "avares://Kkindle.App/Resources/Strings.";
    private readonly Application _application;

    public UiLanguageService(Application application)
    {
        _application = application;
        UiText.Configure(Resolve, UiText.CurrentLanguage);
        Apply(UiText.CurrentLanguage);
    }

    public void Apply(string? language)
    {
        var normalized = UiText.NormalizeLanguage(language);
        var dictionaries = _application.Resources.MergedDictionaries;
        var oldLanguageDictionary = dictionaries
            .OfType<ResourceInclude>()
            .FirstOrDefault(include =>
                include.Source?.ToString()?.Contains("/Resources/Strings.", StringComparison.Ordinal) == true);

        if (oldLanguageDictionary is not null)
            dictionaries.Remove(oldLanguageDictionary);

        dictionaries.Insert(
            0,
            new ResourceInclude(new Uri($"{ResourcePrefix}{normalized}.axaml"))
            {
                Source = new Uri($"{ResourcePrefix}{normalized}.axaml")
            });
        UiText.SetLanguage(normalized);
    }

    private string? Resolve(string source)
    {
        var key = UiText.ResourceKey(source);
        return _application.TryGetResource(key, _application.ActualThemeVariant, out var value)
            ? value?.ToString()
            : null;
    }
}
