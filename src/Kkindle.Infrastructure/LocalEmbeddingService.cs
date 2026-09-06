using Kkindle.Core;

namespace Kkindle.Infrastructure;

/// <summary>
/// Keeps one ONNX embedding session per supported local model and routes all
/// embedding calls to the model selected by the user. Sessions are created
/// lazily, so selecting a model does not allocate its memory until it is used.
/// </summary>
public sealed class LocalEmbeddingService : IEmbeddingService, IEmbeddingAvailability, IDisposable
{
    private readonly Dictionary<string, OnnxEmbeddingService> _services;
    private readonly object _gate = new();
    private OnnxEmbeddingService _activeService;
    private bool _disposed;

    public LocalEmbeddingService(AppPaths paths, string? selectedModelId = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _services = EmbeddingModelPackage.Supported.ToDictionary(
            package => package.ModelId,
            package => new OnnxEmbeddingService(CreateOptions(paths, package)),
            StringComparer.OrdinalIgnoreCase);
        _activeService = _services[EmbeddingModelPackage.Default.ModelId];
        SelectModel(selectedModelId);
    }

    public EmbeddingModelPackage SelectedPackage =>
        EmbeddingModelPackage.Find(ModelId) ?? EmbeddingModelPackage.Default;

    public int Dimension => ActiveService.Dimension;

    public string ModelId => ActiveService.ModelId;

    public bool IsAvailable => ActiveService.IsAvailable;

    public EmbeddingModelPackage SelectModel(string? modelId)
    {
        var package = EmbeddingModelPackage.Find(modelId) ?? EmbeddingModelPackage.Default;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _activeService = _services[package.ModelId];
        }
        return package;
    }

    public Task<EmbeddingAvailability> CheckAvailabilityAsync(
        CancellationToken cancellationToken = default) =>
        ActiveService.CheckAvailabilityAsync(cancellationToken);

    public void ResetLoadFailure() => ActiveService.ResetLoadFailure();

    public Task<float[]> EmbedAsync(
        string text,
        CancellationToken cancellationToken = default) =>
        ActiveService.EmbedAsync(text, cancellationToken);

    public Task<IReadOnlyList<float[]>> EmbedBatchAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default) =>
        ActiveService.EmbedBatchAsync(texts, cancellationToken);

    public Task<float[]> EmbedQueryAsync(
        string text,
        CancellationToken cancellationToken = default) =>
        ActiveService.EmbedQueryAsync(text, cancellationToken);

    public Task<IReadOnlyList<float[]>> EmbedPassagesAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default) =>
        ActiveService.EmbedPassagesAsync(texts, cancellationToken);

    private OnnxEmbeddingService ActiveService
    {
        get
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _activeService;
            }
        }
    }

    private static OnnxEmbeddingOptions CreateOptions(
        AppPaths paths,
        EmbeddingModelPackage package) =>
        new()
        {
            ModelId = package.ModelId,
            ModelDirectory = Path.Combine(paths.EmbeddingModels, package.DirectoryName),
            ExpectedDimension = package.Dimension,
            MaxSequenceLength = package.MaxSequenceLength,
            TokenizerKind = package.TokenizerKind,
            TokenizerFileName = package.TokenizerFileName,
            PaddingTokenId = package.PaddingTokenId,
            QueryPrefix = package.QueryPrefix,
            PassagePrefix = package.PassagePrefix
        };

    public void Dispose()
    {
        OnnxEmbeddingService[] services;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            services = _services.Values.ToArray();
        }

        foreach (var service in services)
            service.Dispose();
    }
}
