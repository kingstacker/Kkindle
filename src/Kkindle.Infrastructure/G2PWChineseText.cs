using System.Text;

namespace Kkindle.Infrastructure;

internal static class G2PWChineseText
{
    private static readonly IReadOnlyDictionary<char, char> SimplifiedToTraditional = LoadMapping();

    // Use the same one-character mapping as g2pW's simplified-Chinese mode.
    // Only model input is converted; output and ruby offsets retain the source.
    public static string ToTraditional(string text)
    {
        var characters = text.ToCharArray();
        for (var index = 0; index < characters.Length; index++)
        {
            if (SimplifiedToTraditional.TryGetValue(characters[index], out var traditional))
                characters[index] = traditional;
        }
        return new string(characters);
    }

    private static IReadOnlyDictionary<char, char> LoadMapping()
    {
        using var stream = typeof(G2PWChineseText).Assembly.GetManifestResourceStream(
            "Kkindle.Infrastructure.ThirdParty.G2PW.bert-base-chinese_s2t_dict.txt")
            ?? throw new InvalidOperationException("内置 g2pW 简繁映射缺失。");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var mapping = new Dictionary<char, char>();
        while (reader.ReadLine() is { } line)
        {
            var pair = line.Split('\t');
            if (pair.Length == 2 && pair[0].Length == 1 && pair[1].Length == 1)
                mapping[pair[0][0]] = pair[1][0];
        }
        return mapping;
    }
}
