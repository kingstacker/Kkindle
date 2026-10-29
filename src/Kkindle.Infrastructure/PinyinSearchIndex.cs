using System.Text;
using Kkindle.Core;

namespace Kkindle.Infrastructure;

/// <summary>Compact full-pinyin and initials keys for short library metadata.</summary>
public sealed class PinyinSearchIndex
{
    private static readonly Lazy<DotNetG2PPinyinEngine> Engine = new(() => new DotNetG2PPinyinEngine());
    private static readonly object EngineGate = new();
    private readonly (string Full, string Initials)[] _keys;

    private PinyinSearchIndex((string Full, string Initials)[] keys) => _keys = keys;

    public static PinyinSearchIndex Create(params string?[] values)
    {
        var keys = new List<(string, string)>();
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value) || !value.Any(IsHan)) continue;
            string[] syllables;
            lock (EngineGate) syllables = Engine.Value.ToPinyinList(value, PinyinBookOutputStyle.NoTone);
            var full = new StringBuilder();
            var initials = new StringBuilder();
            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                if (IsHan(character))
                {
                    var syllable = Normalize(syllables[index]);
                    if (syllable.Length == 0) continue;
                    full.Append(syllable);
                    initials.Append(syllable[0]);
                }
                else if (char.IsAsciiLetterOrDigit(character))
                {
                    var letter = char.ToLowerInvariant(character);
                    full.Append(letter);
                    initials.Append(letter);
                }
            }
            keys.Add((full.ToString(), initials.ToString()));
        }
        return new PinyinSearchIndex(keys.ToArray());
    }

    public bool Matches(string query)
    {
        var normalized = NormalizeQuery(query);
        return normalized.Length > 0 && _keys.Any(key =>
            key.Full.Contains(normalized, StringComparison.Ordinal) || key.Initials.Contains(normalized, StringComparison.Ordinal));
    }

    public static string NormalizeQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Any(character => !char.IsAsciiLetterOrDigit(character) && !char.IsWhiteSpace(character) && character is not '-' and not '\''))
            return string.Empty;
        return Normalize(query);
    }

    private static string Normalize(string value) => string.Concat(value.Where(char.IsAsciiLetterOrDigit)).ToLowerInvariant();
    private static bool IsHan(char character) => character is >= '\u3400' and <= '\u9fff' or >= '\uf900' and <= '\ufaff';
}
