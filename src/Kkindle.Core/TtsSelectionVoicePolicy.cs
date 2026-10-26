namespace Kkindle.Core;

/// <summary>Matches short reader selections to Chinese or American English voices.</summary>
public static class TtsSelectionVoicePolicy
{
    public const string EnglishVoice = "en-US-AriaNeural";
    public const string MixedVoice = "en-US-AvaMultilingualNeural";

    public static bool IsAmericanEnglishVoice(string? voice)
        => voice?.StartsWith("en-US-", StringComparison.OrdinalIgnoreCase) == true;

    public static bool IsSupportedVoice(TtsVoiceInfo voice)
        => IsAmericanEnglishVoice(voice.Id)
            || voice.Id.StartsWith("zh-CN-", StringComparison.OrdinalIgnoreCase);

    public static string SelectVoice(string text, string? preferredVoice)
    {
        var hasChinese = text.EnumerateRunes().Any(rune =>
            rune.Value is >= 0x3400 and <= 0x9FFF or >= 0x20000 and <= 0x323AF);
        var hasEnglish = text.Any(character => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z');
        if (hasChinese && hasEnglish)
        {
            // A multilingual US voice preserves both languages within one sentence.
            return IsAmericanEnglishVoice(preferredVoice)
                && preferredVoice!.Contains("Multilingual", StringComparison.OrdinalIgnoreCase)
                    ? preferredVoice : MixedVoice;
        }

        if (hasEnglish)
            return IsAmericanEnglishVoice(preferredVoice) ? preferredVoice! : EnglishVoice;
        return preferredVoice?.StartsWith("zh-CN-", StringComparison.OrdinalIgnoreCase) == true
            ? preferredVoice : TtsOptions.DefaultVoice;
    }
}
