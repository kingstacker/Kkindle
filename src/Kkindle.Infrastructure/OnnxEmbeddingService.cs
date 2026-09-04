using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Kkindle.Core;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Kkindle.Infrastructure;

/// <summary>
/// Configuration for a local BERT-style ONNX embedding model. The default
/// layout matches the files published for BAAI/bge-small-zh-v1.5:
/// model.onnx, vocab.txt and (optionally) tokenizer_config.json.
/// </summary>
public sealed record OnnxEmbeddingOptions
{
    public const string DefaultModelId = "BAAI/bge-small-zh-v1.5";

    public string ModelId { get; init; } = DefaultModelId;
    public string ModelDirectory { get; init; } = string.Empty;
    public string ModelFileName { get; init; } = "model.onnx";
    public int ExpectedDimension { get; init; } = 512;
    public int MaxSequenceLength { get; init; } = 512;
}

/// <summary>
/// CPU-only local embedding implementation. The model and vocabulary are
/// loaded once, while inference is serialized to keep memory use predictable
/// on the desktop platforms supported by Kkindle.
/// </summary>
public sealed class OnnxEmbeddingService : IEmbeddingService, IEmbeddingAvailability, IDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly SemaphoreSlim _inferenceGate = new(1, 1);
    private readonly OnnxEmbeddingOptions _options;
    private InferenceSession? _session;
    private BertVocabulary? _vocabulary;
    private Exception? _loadFailure;
    private int _dimension;
    private bool _disposed;

    public OnnxEmbeddingService(OnnxEmbeddingOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(_options.ModelId))
            throw new ArgumentException("Embedding 模型 ID 不能为空。", nameof(options));
        if (string.IsNullOrWhiteSpace(_options.ModelDirectory))
            throw new ArgumentException("Embedding 模型目录不能为空。", nameof(options));

        _dimension = Math.Max(1, _options.ExpectedDimension);
    }

    public int Dimension => Volatile.Read(ref _dimension);

    public string ModelId => _options.ModelId;

    public bool IsAvailable
    {
        get
        {
            lock (_gate) return !_disposed && _session is not null;
        }
    }

    public async Task<EmbeddingAvailability> CheckAvailabilityAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var modelPath = ResolveModelPath();
        if (modelPath is null)
        {
            return new EmbeddingAvailability(
                false,
                $"未找到本地 embedding 模型：{ModelId}。请将 ONNX 模型和 vocab.txt 放入模型目录。",
                Path.GetFullPath(_options.ModelDirectory),
                Dimension);
        }

        try
        {
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            return new EmbeddingAvailability(true, $"Embedding 模型已就绪：{ModelId}", modelPath, Dimension);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new EmbeddingAvailability(
                false,
                $"加载 embedding 模型失败：{exception.Message}",
                modelPath,
                Dimension);
        }
    }

    /// <summary>
    /// Clears a cached load error after the model files have been replaced.
    /// A successfully loaded session is kept alive so an in-flight reader
    /// request is never invalidated by a status refresh.
    /// </summary>
    public void ResetLoadFailure()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_session is null)
                _loadFailure = null;
        }
    }

    public async Task<float[]> EmbedAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        var embeddings = await EmbedBatchAsync([text], cancellationToken).ConfigureAwait(false);
        return embeddings.Count == 0 ? [] : embeddings[0];
    }

    public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(texts);
        if (texts.Count == 0) return [];

        var (session, vocabulary) = await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        return await Task.Run(
            () => RunBatch(session, vocabulary, texts, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<(InferenceSession Session, BertVocabulary Vocabulary)> EnsureLoadedAsync(
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_session is not null && _vocabulary is not null)
                return (_session, _vocabulary);
            if (_loadFailure is not null) throw _loadFailure;
        }

        await _loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_session is not null && _vocabulary is not null)
                    return (_session, _vocabulary);
                if (_loadFailure is not null) throw _loadFailure;
            }

            try
            {
                var modelPath = ResolveModelPath()
                    ?? throw new FileNotFoundException(
                        "未找到本地 embedding ONNX 模型。",
                        _options.ModelDirectory);
                var vocabulary = await Task.Run(
                    () => BertVocabulary.Load(_options.ModelDirectory),
                    cancellationToken).ConfigureAwait(false);
                var session = await Task.Run(
                    () => new InferenceSession(modelPath),
                    cancellationToken).ConfigureAwait(false);

                lock (_gate)
                {
                    if (_disposed)
                    {
                        session.Dispose();
                        throw new ObjectDisposedException(nameof(OnnxEmbeddingService));
                    }

                    _vocabulary = vocabulary;
                    _session = session;
                    Debug.WriteLine(
                        $"Embedding model loaded: {ModelId}; expected dimension={Dimension}; "
                        + $"path={modelPath}");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                lock (_gate) _loadFailure = exception;
                throw;
            }

            lock (_gate) return (_session!, _vocabulary!);
        }
        finally
        {
            _loadGate.Release();
        }
    }

    private IReadOnlyList<float[]> RunBatch(
        InferenceSession session,
        BertVocabulary vocabulary,
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken)
    {
        _inferenceGate.Wait(cancellationToken);
        try
        {
            var encoded = texts
                .Select(text => vocabulary.Encode(text, _options.MaxSequenceLength))
                .ToArray();
            try
            {
                return RunBatchCore(session, encoded, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch when (encoded.Length > 1)
            {
                // Some exported BGE models have a fixed batch dimension of 1.
                // Keep the public batch API useful by falling back to one
                // inference at a time without making that model requirement a
                // caller concern.
                var result = new List<float[]>(encoded.Length);
                foreach (var item in encoded)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    result.Add(RunBatchCore(session, [item], cancellationToken)[0]);
                }
                return result;
            }
        }
        finally
        {
            _inferenceGate.Release();
        }
    }

    private IReadOnlyList<float[]> RunBatchCore(
        InferenceSession session,
        IReadOnlyList<BertEncodedText> encoded,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var batchSize = encoded.Count;
        var sequenceLength = encoded[0].InputIds.Length;
        var inputIds = new long[batchSize * sequenceLength];
        var attentionMask = new long[inputIds.Length];
        var tokenTypeIds = new long[inputIds.Length];
        for (var batch = 0; batch < batchSize; batch++)
        {
            encoded[batch].InputIds.CopyTo(inputIds, batch * sequenceLength);
            encoded[batch].AttentionMask.CopyTo(attentionMask, batch * sequenceLength);
            encoded[batch].TokenTypeIds.CopyTo(tokenTypeIds, batch * sequenceLength);
        }

        var inputMetadata = session.InputMetadata;
        var inputIdName = FindTensorName(inputMetadata.Keys, "input_ids")
            ?? throw new InvalidDataException("Embedding 模型缺少 input_ids 输入。" );
        var attentionName = FindTensorName(inputMetadata.Keys, "attention_mask");
        var tokenTypeName = FindTensorName(inputMetadata.Keys, "token_type_ids");
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(
                inputIdName,
                new DenseTensor<long>(inputIds, [batchSize, sequenceLength]))
        };
        if (attentionName is not null)
        {
            inputs.Add(NamedOnnxValue.CreateFromTensor(
                attentionName,
                new DenseTensor<long>(attentionMask, [batchSize, sequenceLength])));
        }
        if (tokenTypeName is not null)
        {
            inputs.Add(NamedOnnxValue.CreateFromTensor(
                tokenTypeName,
                new DenseTensor<long>(tokenTypeIds, [batchSize, sequenceLength])));
        }

        using var outputs = session.Run(inputs);
        var output = outputs.FirstOrDefault(item =>
                item.Name.Contains("last_hidden_state", StringComparison.OrdinalIgnoreCase)
                || item.Name.Contains("token_embeddings", StringComparison.OrdinalIgnoreCase))
            ?? outputs.FirstOrDefault()
            ?? throw new InvalidDataException("Embedding 模型没有返回张量输出。" );
        var tensor = output.AsTensor<float>();
        var values = tensor.ToArray();
        var dimensions = tensor.Dimensions.ToArray();
        var result = new List<float[]>(batchSize);
        for (var batch = 0; batch < batchSize; batch++)
        {
            var embedding = ReadEmbedding(
                values,
                dimensions,
                batch,
                encoded[batch].AttentionMask);
            Volatile.Write(ref _dimension, embedding.Length);
            result.Add(embedding);
        }
        return result;
    }

    private static float[] ReadEmbedding(
        float[] values,
        int[] dimensions,
        int batch,
        IReadOnlyList<long> attentionMask)
    {
        if (dimensions.Length == 1)
        {
            if (batch != 0) throw new InvalidDataException("Embedding 输出 batch 维度无法识别。" );
            return Normalize(values);
        }

        if (dimensions.Length == 2)
        {
            var rowCount = dimensions[0];
            var dimension = dimensions[1];
            if (batch >= rowCount)
                throw new InvalidDataException("Embedding 输出 batch 维度不足。" );
            var row = new float[dimension];
            Array.Copy(values, batch * dimension, row, 0, dimension);
            return Normalize(row);
        }

        if (dimensions.Length != 3)
            throw new InvalidDataException("Embedding 模型输出必须是二维或三维浮点张量。" );

        var actualBatch = dimensions[0];
        var sequenceLength = dimensions[1];
        var hiddenDimension = dimensions[2];
        if (batch >= actualBatch)
            throw new InvalidDataException("Embedding 输出 batch 维度不足。" );

        var pooled = new float[hiddenDimension];
        var tokenCount = 0;
        var batchOffset = batch * sequenceLength * hiddenDimension;
        for (var token = 0; token < sequenceLength; token++)
        {
            if (token >= attentionMask.Count || attentionMask[token] == 0) continue;
            tokenCount++;
            var tokenOffset = batchOffset + token * hiddenDimension;
            for (var dimension = 0; dimension < hiddenDimension; dimension++)
                pooled[dimension] += values[tokenOffset + dimension];
        }

        if (tokenCount > 1)
        {
            for (var dimension = 0; dimension < pooled.Length; dimension++)
                pooled[dimension] /= tokenCount;
        }
        return Normalize(pooled);
    }

    private static float[] Normalize(float[] values)
    {
        var sum = 0d;
        foreach (var value in values)
        {
            if (!float.IsFinite(value))
                throw new InvalidDataException("Embedding 模型返回了无效的浮点数。" );
            sum += value * value;
        }

        var norm = Math.Sqrt(sum);
        if (norm <= double.Epsilon) return values;
        for (var index = 0; index < values.Length; index++)
            values[index] = (float)(values[index] / norm);
        return values;
    }

    private static string? FindTensorName(
        IEnumerable<string> names,
        string expected)
        => names.FirstOrDefault(name =>
            name.Equals(expected, StringComparison.OrdinalIgnoreCase));

    private string? ResolveModelPath()
    {
        try
        {
            var directory = Path.GetFullPath(_options.ModelDirectory);
            var candidates = new[]
            {
                Path.Combine(directory, _options.ModelFileName),
                Path.Combine(directory, "onnx", "model.onnx"),
                Path.Combine(directory, "model.onnx"),
                Path.Combine(directory, "model_quantized.onnx")
            };
            return candidates
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(File.Exists);
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        InferenceSession? session;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            session = _session;
            _session = null;
            _vocabulary = null;
        }

        session?.Dispose();
        _inferenceGate.Dispose();
        _loadGate.Dispose();
    }

    private sealed record BertEncodedText(
        long[] InputIds,
        long[] AttentionMask,
        long[] TokenTypeIds);

    private sealed class BertVocabulary
    {
        private readonly Dictionary<string, int> _ids;
        private readonly bool _lowerCase;
        private readonly int _unknownId;
        private readonly int _classId;
        private readonly int _separatorId;
        private readonly int _paddingId;

        private BertVocabulary(Dictionary<string, int> ids, bool lowerCase)
        {
            _ids = ids;
            _lowerCase = lowerCase;
            _unknownId = GetId("[UNK]", 0);
            _classId = GetId("[CLS]", _unknownId);
            _separatorId = GetId("[SEP]", _unknownId);
            _paddingId = GetId("[PAD]", 0);
        }

        public static BertVocabulary Load(string directory)
        {
            var vocabPath = Path.Combine(directory, "vocab.txt");
            if (!File.Exists(vocabPath))
                throw new FileNotFoundException("Embedding 模型缺少 vocab.txt。", vocabPath);

            var ids = new Dictionary<string, int>(StringComparer.Ordinal);
            var index = 0;
            foreach (var line in File.ReadLines(vocabPath, Encoding.UTF8))
            {
                var token = line.TrimEnd('\r', '\n');
                if (!ids.ContainsKey(token)) ids[token] = index;
                index++;
            }
            if (ids.Count == 0)
                throw new InvalidDataException("Embedding vocab.txt 为空。" );

            var lowerCase = true;
            var configPath = Path.Combine(directory, "tokenizer_config.json");
            if (File.Exists(configPath))
            {
                try
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(configPath, Encoding.UTF8));
                    if (document.RootElement.TryGetProperty("do_lower_case", out var value)
                        && value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    {
                        lowerCase = value.GetBoolean();
                    }
                }
                catch (JsonException)
                {
                    // The vocab itself remains sufficient for tokenization.
                }
            }
            return new BertVocabulary(ids, lowerCase);
        }

        public BertEncodedText Encode(string text, int configuredMaximumLength)
        {
            var maximumLength = Math.Clamp(configuredMaximumLength, 32, 4096);
            var tokens = new List<string> { "[CLS]" };
            foreach (var token in BasicTokenize(text ?? string.Empty))
            {
                tokens.AddRange(WordPieceTokenize(token));
                if (tokens.Count >= maximumLength - 1) break;
            }
            tokens.Add("[SEP]");
            if (tokens.Count > maximumLength)
                tokens = tokens[..maximumLength];

            var inputIds = Enumerable.Repeat((long)_paddingId, maximumLength).ToArray();
            var attentionMask = new long[maximumLength];
            var tokenTypeIds = new long[maximumLength];
            for (var index = 0; index < tokens.Count; index++)
            {
                inputIds[index] = GetId(tokens[index], _unknownId);
                attentionMask[index] = 1;
            }
            return new BertEncodedText(inputIds, attentionMask, tokenTypeIds);
        }

        private IReadOnlyList<string> BasicTokenize(string text)
        {
            var tokens = new List<string>();
            var current = new StringBuilder();
            foreach (var value in text.Normalize().Trim())
            {
                if (char.IsWhiteSpace(value))
                {
                    FlushCurrent(current);
                    continue;
                }

                if (IsCjk(value))
                {
                    FlushCurrent(current);
                    tokens.Add(value.ToString());
                    continue;
                }

                if (char.IsLetterOrDigit(value))
                {
                    current.Append(_lowerCase ? char.ToLowerInvariant(value) : value);
                    continue;
                }

                FlushCurrent(current);
                tokens.Add(value.ToString());
            }
            FlushCurrent(current);
            return tokens;

            void FlushCurrent(StringBuilder builder)
            {
                if (builder.Length == 0) return;
                var token = builder.ToString();
                builder.Clear();
                tokens.Add(token);
            }
        }

        private IEnumerable<string> WordPieceTokenize(string token)
        {
            if (_ids.ContainsKey(token))
            {
                yield return token;
                yield break;
            }

            var pieces = new List<string>();
            var start = 0;
            while (start < token.Length)
            {
                var end = token.Length;
                string? match = null;
                while (end > start)
                {
                    var candidate = token[start..end];
                    if (start > 0) candidate = "##" + candidate;
                    if (_ids.ContainsKey(candidate))
                    {
                        match = candidate;
                        break;
                    }
                    end--;
                }

                if (match is null)
                {
                    yield return "[UNK]";
                    yield break;
                }
                pieces.Add(match);
                start = end;
            }

            foreach (var piece in pieces) yield return piece;
        }

        private int GetId(string token, int fallback) =>
            _ids.TryGetValue(token, out var id) ? id : fallback;

        private static bool IsCjk(char value) =>
            value is >= '\u3400' and <= '\u4DBF'
                or >= '\u4E00' and <= '\u9FFF'
                or >= '\uF900' and <= '\uFAFF';
    }
}
