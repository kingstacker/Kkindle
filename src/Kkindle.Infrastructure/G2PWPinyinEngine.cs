using System.Text;
using System.Text.Json;
using DotNetG2P.Chinese;
using Kkindle.Core;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Kkindle.Infrastructure;

public interface IPinyinEngine : IDisposable
{
    string EngineId { get; }

    /// <summary>Returns one entry per UTF-16 source position, including non-Han text.</summary>
    string[] ToPinyinList(string text, PinyinBookOutputStyle style);

    string[] ToPinyinList(string text, PinyinBookOutputStyle style, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ToPinyinList(text, style);
    }

    bool ContainsChar(char character);

    IReadOnlyList<string> LookupChar(char character);
}

internal sealed class DotNetG2PPinyinEngine : IPinyinEngine
{
    private readonly ChineseG2PEngine _engine = new();

    public string EngineId => PinyinBookEngineCatalog.DotNetG2PId;

    public string[] ToPinyinList(string text, PinyinBookOutputStyle style) =>
        ToPinyinList(text, style, CancellationToken.None);

    public string[] ToPinyinList(string text, PinyinBookOutputStyle style, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var syllables = _engine.ToPinyinList(text, style switch
        {
            PinyinBookOutputStyle.ToneNumber => PinyinStyle.ToneNumber,
            PinyinBookOutputStyle.NoTone => PinyinStyle.Normal,
            _ => PinyinStyle.ToneMarked
        });
        cancellationToken.ThrowIfCancellationRequested();
        if (syllables.Length == text.Length) return syllables;

        // DotNetG2P returns one entry for an emoji/supplementary character, while
        // XHTML text and ruby targets use UTF-16 offsets. Keep later Han aligned.
        var result = new string[text.Length];
        var offset = 0;
        var syllableIndex = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            result[offset] = syllableIndex < syllables.Length ? syllables[syllableIndex] : rune.ToString();
            if (rune.Utf16SequenceLength == 2) result[offset + 1] = string.Empty;
            offset += rune.Utf16SequenceLength;
            syllableIndex++;
        }
        return result;
    }

    public bool ContainsChar(char character) => _engine.ContainsChar(character);

    public IReadOnlyList<string> LookupChar(char character) => _engine.LookupChar(character);

    public void Dispose()
    {
        _engine.Dispose();
    }
}

internal static class PinyinEngineFactory
{
    public static IPinyinEngine Create(AppPaths paths, PinyinBookEngineKind engine)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return engine == PinyinBookEngineKind.G2PW
            ? new G2PWPinyinEngine(Path.Combine(paths.PinyinModels, G2PWModelPackage.DirectoryName))
            : new DotNetG2PPinyinEngine();
    }
}

/// <summary>
/// Local g2pW v2 inference backed by the official ONNX checkpoint. The
/// generated ruby text stays compatible with the existing pinyin pipeline;
/// g2pW only replaces the contextual pronunciation decision.
/// </summary>
internal sealed class G2PWPinyinEngine : IPinyinEngine
{
    private const int ContextWindowSize = 32;
    private const int InferenceBatchSize = 64;

    private readonly string _modelDirectory;
    private readonly DotNetG2PPinyinEngine _fallback = new();
    private readonly object _loadGate = new();
    private readonly SemaphoreSlim _inferenceGate = new(1, 1);
    private InferenceSession? _session;
    private G2PWWordPieceTokenizer? _tokenizer;
    private Dictionary<char, int[]> _candidateLabelIds = [];
    private Dictionary<char, int> _characterIds = [];
    private Dictionary<string, int> _labelIds = new(StringComparer.Ordinal);
    private Dictionary<string, string> _bopomofoToPinyin = new(StringComparer.Ordinal);
    private HashSet<char> _polyphonicCharacters = [];
    private string[] _labels = [];
    private Exception? _loadFailure;
    private bool _disposed;

    public G2PWPinyinEngine(string modelDirectory)
    {
        _modelDirectory = Path.GetFullPath(modelDirectory ?? throw new ArgumentNullException(nameof(modelDirectory)));
    }

    public string EngineId => PinyinBookEngineCatalog.G2PWId;

    public string[] ToPinyinList(string text, PinyinBookOutputStyle style) =>
        ToPinyinList(text, style, CancellationToken.None);

    public string[] ToPinyinList(string text, PinyinBookOutputStyle style, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(text)) return [];

        var result = _fallback.ToPinyinList(text, style, cancellationToken);
        var modelText = G2PWChineseText.ToTraditional(text);
        EnsureLoaded();
        var queries = new List<G2PWQuery>();
        for (var index = 0; index < modelText.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_polyphonicCharacters.Contains(modelText[index])) continue;
            var start = Math.Max(0, index - ContextWindowSize / 2);
            var end = Math.Min(modelText.Length, index + ContextWindowSize / 2);
            if (start > 0 && char.IsSurrogatePair(modelText, start - 1)) start--;
            if (end < modelText.Length && end > 0 && char.IsSurrogatePair(modelText, end - 1)) end++;
            var context = modelText[start..end];
            var tokenized = _tokenizer!.Tokenize(context, index - start);
            if (tokenized.QueryTokenIndex < 0
                || !_candidateLabelIds.TryGetValue(modelText[index], out var candidateIds)
                || candidateIds.Length == 0)
                continue;

            // Simplification can merge distinct characters. The official map
            // turns 发 into 發, whose labels cannot express the fà in 头发 (髮).
            // Do not overwrite a local reading that this converted character
            // cannot represent, even when the model strongly prefers a label.
            if (modelText[index] != text[index]
                && !candidateIds.Any(labelId => string.Equals(
                    PinyinToneConverter.FromG2PWLabel(_labels[labelId], _bopomofoToPinyin, style),
                    result[index], StringComparison.OrdinalIgnoreCase)))
                continue;

            queries.Add(new G2PWQuery(
                index,
                tokenized,
                candidateIds,
                _characterIds[modelText[index]]));
        }

        for (var offset = 0; offset < queries.Count; offset += InferenceBatchSize)
        {
            var batch = queries.Skip(offset).Take(InferenceBatchSize).ToArray();
            InferBatch(batch, result, style, cancellationToken);
        }
        return result;
    }

    public bool ContainsChar(char character) => _fallback.ContainsChar(character);

    public IReadOnlyList<string> LookupChar(char character) => _fallback.LookupChar(character);

    private void EnsureLoaded()
    {
        lock (_loadGate)
        {
            if (_session is not null) return;
            if (_loadFailure is not null)
                throw new InvalidDataException("g2pW 模型加载失败。", _loadFailure);

            try
            {
                var modelPath = RequireFile("g2pw.onnx");
                var polyphonicPath = RequireFile("POLYPHONIC_CHARS.txt");
                var mapPath = RequireFile("bopomofo_to_pinyin_wo_tune_dict.json");
                var vocabularyPath = RequireFile("vocab.txt");

                var candidateLabels = new Dictionary<char, List<string>>();
                foreach (var line in File.ReadLines(polyphonicPath, Encoding.UTF8))
                {
                    var separator = line.IndexOf('\t');
                    if (separator <= 0 || separator >= line.Length - 1) continue;
                    var characterText = line[..separator].Trim();
                    var label = line[(separator + 1)..].Trim();
                    if (characterText.Length != 1 || label.Length == 0) continue;
                    var character = characterText[0];
                    if (!candidateLabels.TryGetValue(character, out var characterLabels))
                    {
                        characterLabels = [];
                        candidateLabels[character] = characterLabels;
                    }
                    if (!characterLabels.Contains(label, StringComparer.Ordinal)) characterLabels.Add(label);
                }

                var allLabels = candidateLabels
                    .SelectMany(item => item.Value)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray();
                if (allLabels.Length == 0)
                    throw new InvalidDataException("g2pW 多音字读音表为空。");

                var labelIds = allLabels
                    .Select((label, index) => (label, index))
                    .ToDictionary(item => item.label, item => item.index, StringComparer.Ordinal);
                var candidateIds = candidateLabels.ToDictionary(
                    item => item.Key,
                    item => item.Value
                        .Select(label => labelIds[label])
                        .OrderBy(index => index)
                        .ToArray());

                var map = JsonSerializer.Deserialize<Dictionary<string, string>>(
                    File.ReadAllText(mapPath, Encoding.UTF8))
                    ?? throw new InvalidDataException("g2pW 拼音映射为空。");
                var tokenizer = new G2PWWordPieceTokenizer(vocabularyPath);
                var session = new InferenceSession(modelPath);
                var requiredInputs = new[]
                {
                    "input_ids",
                    "token_type_ids",
                    "attention_mask",
                    "phoneme_mask",
                    "char_ids",
                    "position_ids"
                };
                if (requiredInputs.Any(input => !session.InputMetadata.ContainsKey(input)))
                {
                    session.Dispose();
                    throw new InvalidDataException("g2pW ONNX 模型输入格式不兼容。");
                }

                _labels = allLabels;
                _labelIds = labelIds;
                _candidateLabelIds = candidateIds;
                _polyphonicCharacters = candidateLabels.Keys.ToHashSet();
                _characterIds = candidateLabels.Keys
                    .OrderBy(character => character)
                    .Select((character, index) => (character, index))
                    .ToDictionary(item => item.character, item => item.index);
                _bopomofoToPinyin = new Dictionary<string, string>(map, StringComparer.Ordinal);
                _tokenizer = tokenizer;
                _session = session;
            }
            catch (Exception exception)
            {
                _loadFailure = exception;
                throw new InvalidDataException(
                    "g2pW 模型加载失败，请在设置中重新下载模型。",
                    exception);
            }
        }
    }

    private void InferBatch(
        IReadOnlyList<G2PWQuery> queries,
        string[] result,
        PinyinBookOutputStyle style,
        CancellationToken cancellationToken)
    {
        if (queries.Count == 0 || _session is null) return;
        _inferenceGate.Wait(cancellationToken);
        try
        {
            var maxLength = queries.Max(query => query.Tokenized.InputIds.Length);
            var batchSize = queries.Count;
            var inputIds = new long[batchSize * maxLength];
            var tokenTypeIds = new long[batchSize * maxLength];
            var attentionMask = new long[batchSize * maxLength];
            var phonemeMask = new float[batchSize * _labels.Length];
            var charIds = new long[batchSize];
            var positionIds = new long[batchSize];
            var padId = _tokenizer!.PaddingTokenId;

            for (var batchIndex = 0; batchIndex < batchSize; batchIndex++)
            {
                var query = queries[batchIndex];
                var tokenized = query.Tokenized;
                var inputOffset = batchIndex * maxLength;
                Array.Fill(inputIds, padId, inputOffset, maxLength);
                Array.Copy(tokenized.InputIds, 0, inputIds, inputOffset, tokenized.InputIds.Length);
                Array.Copy(tokenized.AttentionMask, 0, attentionMask, inputOffset, tokenized.AttentionMask.Length);
                Array.Copy(tokenized.TokenTypeIds, 0, tokenTypeIds, inputOffset, tokenized.TokenTypeIds.Length);
                foreach (var labelId in query.CandidateLabelIds)
                    phonemeMask[batchIndex * _labels.Length + labelId] = 1f;
                charIds[batchIndex] = query.CharacterId;
                positionIds[batchIndex] = tokenized.QueryTokenIndex;
            }

            var inputs = new[]
            {
                NamedOnnxValue.CreateFromTensor(
                    "input_ids",
                    new DenseTensor<long>(inputIds, [batchSize, maxLength])),
                NamedOnnxValue.CreateFromTensor(
                    "token_type_ids",
                    new DenseTensor<long>(tokenTypeIds, [batchSize, maxLength])),
                NamedOnnxValue.CreateFromTensor(
                    "attention_mask",
                    new DenseTensor<long>(attentionMask, [batchSize, maxLength])),
                NamedOnnxValue.CreateFromTensor(
                    "phoneme_mask",
                    new DenseTensor<float>(phonemeMask, [batchSize, _labels.Length])),
                NamedOnnxValue.CreateFromTensor(
                    "char_ids",
                    new DenseTensor<long>(charIds, [batchSize])),
                NamedOnnxValue.CreateFromTensor(
                    "position_ids",
                    new DenseTensor<long>(positionIds, [batchSize]))
            };

            using var runOptions = new RunOptions();
            using var runCancellation = cancellationToken.Register(() => runOptions.Terminate = true);
            using var outputs = _session.Run(inputs, _session.OutputNames, runOptions);
            cancellationToken.ThrowIfCancellationRequested();
            var output = outputs.FirstOrDefault()?.AsTensor<float>()?.ToArray();
            if (output is null || output.Length < batchSize * _labels.Length) return;

            for (var batchIndex = 0; batchIndex < batchSize; batchIndex++)
            {
                var query = queries[batchIndex];
                var rowOffset = batchIndex * _labels.Length;
                var bestLabelId = query.CandidateLabelIds
                    .Where(labelId => labelId >= 0 && labelId < _labels.Length)
                    .MaxBy(labelId => output[rowOffset + labelId]);
                if (!_labelIds.ContainsKey(_labels[bestLabelId])) continue;
                var pinyin = PinyinToneConverter.FromG2PWLabel(
                    _labels[bestLabelId],
                    _bopomofoToPinyin,
                    style);
                if (pinyin.Length > 0) result[query.TextIndex] = pinyin;
            }
        }
        catch (OnnxRuntimeException exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("g2pW 注音已取消。", exception, cancellationToken);
        }
        finally
        {
            _inferenceGate.Release();
        }
    }

    private string RequireFile(string fileName)
    {
        var path = Path.Combine(_modelDirectory, fileName);
        if (!File.Exists(path) || new FileInfo(path).Length <= 0)
            throw new FileNotFoundException($"g2pW 模型缺少 {fileName}。", path);
        return path;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _session?.Dispose();
        _inferenceGate.Dispose();
        _fallback.Dispose();
    }

    private sealed record G2PWQuery(
        int TextIndex,
        G2PWTokenizedText Tokenized,
        int[] CandidateLabelIds,
        int CharacterId);
}

internal sealed class G2PWWordPieceTokenizer
{
    private readonly Dictionary<string, long> _vocabulary;
    private readonly long _unknownId;
    private readonly long _classId;
    private readonly long _separatorId;

    public G2PWWordPieceTokenizer(string vocabularyPath)
    {
        _vocabulary = new Dictionary<string, long>(StringComparer.Ordinal);
        long id = 0;
        foreach (var rawLine in File.ReadLines(vocabularyPath, Encoding.UTF8))
        {
            var token = rawLine.TrimEnd('\r', '\n');
            if (token.Length > 0 && !_vocabulary.ContainsKey(token))
                _vocabulary[token] = id;
            id++;
        }

        _unknownId = GetRequiredId("[UNK]");
        _classId = GetRequiredId("[CLS]");
        _separatorId = GetRequiredId("[SEP]");
        PaddingTokenId = _vocabulary.TryGetValue("[PAD]", out var paddingId) ? paddingId : 0;
    }

    public long PaddingTokenId { get; }

    public G2PWTokenizedText Tokenize(string text, int queryCharacterIndex)
    {
        var pieces = new List<(long Id, int Start, int End)>();
        foreach (var basic in BasicTokenize(text))
        {
            var wordPieces = WordPieceTokenize(basic.Value);
            foreach (var piece in wordPieces)
                pieces.Add((piece, basic.Start, basic.End));
        }

        var queryPiece = -1;
        for (var index = 0; index < pieces.Count; index++)
        {
            if (queryCharacterIndex >= pieces[index].Start
                && queryCharacterIndex < pieces[index].End)
            {
                queryPiece = index + 1;
                break;
            }
        }

        var inputIds = new long[pieces.Count + 2];
        var attentionMask = new long[inputIds.Length];
        var tokenTypeIds = new long[inputIds.Length];
        inputIds[0] = _classId;
        attentionMask[0] = 1;
        for (var index = 0; index < pieces.Count; index++)
        {
            inputIds[index + 1] = pieces[index].Id;
            attentionMask[index + 1] = 1;
        }
        inputIds[^1] = _separatorId;
        attentionMask[^1] = 1;
        return new G2PWTokenizedText(inputIds, attentionMask, tokenTypeIds, queryPiece);
    }

    private IEnumerable<(string Value, int Start, int End)> BasicTokenize(string text)
    {
        var index = 0;
        while (index < text.Length)
        {
            if (char.IsWhiteSpace(text[index]))
            {
                index++;
                continue;
            }

            var start = index;
            if (IsCjk(text[index]))
            {
                index++;
            }
            else if (IsAsciiLetterOrDigit(text[index]))
            {
                index++;
                while (index < text.Length && IsAsciiLetterOrDigit(text[index])) index++;
            }
            else
            {
                index += char.IsSurrogatePair(text, index) ? 2 : 1;
            }

            yield return (text[start..index].ToLowerInvariant(), start, index);
        }
    }

    private IReadOnlyList<long> WordPieceTokenize(string token)
    {
        if (_vocabulary.TryGetValue(token, out var directId)) return [directId];
        if (token.Length == 0) return [];

        var pieces = new List<long>();
        var start = 0;
        while (start < token.Length)
        {
            var end = token.Length;
            long matchedId = _unknownId;
            var matched = false;
            while (end > start)
            {
                var candidate = token[start..end];
                if (start > 0) candidate = "##" + candidate;
                if (_vocabulary.TryGetValue(candidate, out matchedId))
                {
                    matched = true;
                    break;
                }
                end--;
            }

            if (!matched) return [_unknownId];
            pieces.Add(matchedId);
            start = end;
        }
        return pieces;
    }

    private long GetRequiredId(string token) =>
        _vocabulary.TryGetValue(token, out var id)
            ? id
            : throw new InvalidDataException($"BERT 词表缺少 {token}。");

    private static bool IsAsciiLetterOrDigit(char character) =>
        character is >= 'a' and <= 'z'
            or >= 'A' and <= 'Z'
            or >= '0' and <= '9';

    private static bool IsCjk(char character) =>
        character is >= '\u3400' and <= '\u4DBF'
            or >= '\u4E00' and <= '\u9FFF'
            or >= '\uF900' and <= '\uFAFF';
}

internal sealed record G2PWTokenizedText(
    long[] InputIds,
    long[] AttentionMask,
    long[] TokenTypeIds,
    int QueryTokenIndex);

internal static class PinyinToneConverter
{
    private static readonly IReadOnlyDictionary<char, (char Base, int Tone)> ToneMarks =
        new Dictionary<char, (char Base, int Tone)>
        {
            ['ā'] = ('a', 1), ['á'] = ('a', 2), ['ǎ'] = ('a', 3), ['à'] = ('a', 4),
            ['ē'] = ('e', 1), ['é'] = ('e', 2), ['ě'] = ('e', 3), ['è'] = ('e', 4),
            ['ī'] = ('i', 1), ['í'] = ('i', 2), ['ǐ'] = ('i', 3), ['ì'] = ('i', 4),
            ['ō'] = ('o', 1), ['ó'] = ('o', 2), ['ǒ'] = ('o', 3), ['ò'] = ('o', 4),
            ['ū'] = ('u', 1), ['ú'] = ('u', 2), ['ǔ'] = ('u', 3), ['ù'] = ('u', 4),
            ['ǖ'] = ('ü', 1), ['ǘ'] = ('ü', 2), ['ǚ'] = ('ü', 3), ['ǜ'] = ('ü', 4)
        };

    private static readonly IReadOnlyDictionary<(char Base, int Tone), char> ToneMarkByBaseAndTone =
        ToneMarks.ToDictionary(pair => pair.Value, pair => pair.Key);

    public static string FromG2PWLabel(
        string label,
        IReadOnlyDictionary<string, string> mapping,
        PinyinBookOutputStyle style)
    {
        if (string.IsNullOrWhiteSpace(label)) return string.Empty;
        var tone = 5;
        var baseBopomofo = label.Trim();
        if (baseBopomofo[^1] is >= '1' and <= '5')
        {
            tone = baseBopomofo[^1] - '0';
            baseBopomofo = baseBopomofo[..^1];
        }
        if (!mapping.TryGetValue(baseBopomofo, out var basePinyin)) return string.Empty;
        basePinyin = basePinyin.Trim().ToLowerInvariant()
            .Replace("u:", "ü", StringComparison.Ordinal)
            .Replace("v", "ü", StringComparison.Ordinal);
        if (basePinyin.Length == 0) return string.Empty;

        return style switch
        {
            PinyinBookOutputStyle.ToneNumber => tone is >= 1 and <= 4
                ? basePinyin + tone
                : basePinyin,
            PinyinBookOutputStyle.NoTone => basePinyin,
            _ => ConvertToneNumberToMarked(basePinyin, tone)
        };
    }

    private static string ConvertToneNumberToMarked(string basePinyin, int tone)
    {
        if (tone is < 1 or > 4) return basePinyin;
        var placement = FindToneVowelIndex(basePinyin);
        if (placement < 0) return basePinyin;
        var vowel = basePinyin[placement];
        return basePinyin[..placement]
            + (ToneMarkByBaseAndTone.TryGetValue((vowel, tone), out var marked)
                ? marked.ToString()
                : vowel.ToString())
            + basePinyin[(placement + 1)..];
    }

    private static int FindToneVowelIndex(string value)
    {
        var a = value.IndexOf('a');
        if (a >= 0) return a;
        var e = value.IndexOf('e');
        if (e >= 0) return e;
        var ou = value.IndexOf("ou", StringComparison.Ordinal);
        if (ou >= 0) return ou;
        for (var index = value.Length - 1; index >= 0; index--)
        {
            if (value[index] is 'a' or 'e' or 'i' or 'o' or 'u' or 'ü') return index;
        }
        return -1;
    }
}
