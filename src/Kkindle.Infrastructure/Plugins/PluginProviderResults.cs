using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kkindle.Infrastructure.Plugins;

/// <summary>One sense of a dictionary entry returned by a plugin.</summary>
public sealed record PluginDictionarySense(string? PartOfSpeech, string Text, IReadOnlyList<string> Examples);

/// <summary>A dictionary entry returned by a plugin's dictionary provider.</summary>
public sealed record PluginDictionaryEntry(
    string Headword,
    string? Phonetic,
    IReadOnlyList<PluginDictionarySense> Senses,
    string? Note);

/// <summary>
/// Normalizes what provider handlers return. Plugins may return a plain
/// string, a single entry, an array of entries or <c>{ entries: [...] }</c>;
/// anything else is ignored rather than rendered raw. Sizes are capped so a
/// misbehaving plugin cannot flood the popup.
/// </summary>
public static class PluginProviderResults
{
    public const int MaximumEntries = 20;
    public const int MaximumSenses = 30;
    public const int MaximumExamples = 5;
    public const int MaximumTextLength = 4000;
    public const int MaximumTranslationLength = 100_000;

    public static IReadOnlyList<PluginDictionaryEntry> ParseDictionary(JsonNode? value, string term)
    {
        var entries = new List<PluginDictionaryEntry>();
        switch (value)
        {
            case JsonValue text when TryString(text) is { Length: > 0 } plain:
                entries.Add(new PluginDictionaryEntry(term, null, [new PluginDictionarySense(null, Clip(plain), [])], null));
                break;
            case JsonArray array:
                AddEntries(array, term, entries);
                break;
            case JsonObject obj when obj["entries"] is JsonArray array:
                AddEntries(array, term, entries);
                break;
            case JsonObject obj:
                if (ParseEntry(obj, term) is { } single) entries.Add(single);
                break;
        }
        return entries;
    }

    /// <summary>The translated text from a translator's result, or null when there is none.</summary>
    public static string? ParseTranslation(JsonNode? value)
    {
        var text = value switch
        {
            JsonValue plain => TryString(plain),
            JsonObject obj => obj["text"] is JsonValue inner ? TryString(inner)
                : obj["translation"] is JsonValue alternate ? TryString(alternate)
                : null,
            _ => null
        };
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        return text.Length <= MaximumTranslationLength ? text : text[..MaximumTranslationLength];
    }

    private static void AddEntries(JsonArray array, string term, List<PluginDictionaryEntry> entries)
    {
        foreach (var item in array)
        {
            if (entries.Count >= MaximumEntries) break;
            var entry = item switch
            {
                JsonObject obj => ParseEntry(obj, term),
                JsonValue text when TryString(text) is { Length: > 0 } plain =>
                    new PluginDictionaryEntry(term, null, [new PluginDictionarySense(null, Clip(plain), [])], null),
                _ => null
            };
            if (entry is not null) entries.Add(entry);
        }
    }

    private static PluginDictionaryEntry? ParseEntry(JsonObject obj, string term)
    {
        var headword = Text(obj, "headword") ?? Text(obj, "word") ?? term;
        var senses = new List<PluginDictionarySense>();
        if (obj["senses"] is JsonArray array)
        {
            foreach (var item in array)
            {
                if (senses.Count >= MaximumSenses) break;
                switch (item)
                {
                    case JsonValue text when TryString(text) is { Length: > 0 } plain:
                        senses.Add(new PluginDictionarySense(null, Clip(plain), []));
                        break;
                    case JsonObject sense when (Text(sense, "text") ?? Text(sense, "definition")) is { } definition:
                        senses.Add(new PluginDictionarySense(
                            Text(sense, "partOfSpeech") ?? Text(sense, "pos"),
                            definition,
                            Strings(sense["examples"])));
                        break;
                }
            }
        }
        else if ((Text(obj, "definition") ?? Text(obj, "text")) is { } definition)
        {
            senses.Add(new PluginDictionarySense(Text(obj, "partOfSpeech") ?? Text(obj, "pos"), definition, Strings(obj["examples"])));
        }

        var note = Text(obj, "note");
        if (senses.Count == 0 && note is null) return null;
        return new PluginDictionaryEntry(
            Clip(headword, 200),
            Text(obj, "phonetic") ?? Text(obj, "pronunciation"),
            senses,
            note);
    }

    private static IReadOnlyList<string> Strings(JsonNode? node) =>
        node is JsonArray array
            ? array.OfType<JsonValue>()
                .Select(TryString)
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .Take(MaximumExamples)
                .Select(text => Clip(text!))
                .ToArray()
            : [];

    private static string? Text(JsonObject obj, string name) =>
        obj[name] is JsonValue value && TryString(value) is { } text && !string.IsNullOrWhiteSpace(text)
            ? Clip(text.Trim())
            : null;

    private static string? TryString(JsonValue value) =>
        value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    private static string Clip(string value, int length = MaximumTextLength) =>
        value.Length <= length ? value : value[..length] + "…";
}
