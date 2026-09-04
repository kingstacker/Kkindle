using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace Kkindle.Infrastructure;

/// <summary>
/// One file in a fixed, trusted embedding-model package. The target name is
/// deliberately separate from the remote name because the ONNX runtime uses
/// the conventional local name <c>model.onnx</c>.
/// </summary>
public sealed record EmbeddingModelFile(
    string TargetFileName,
    Uri DownloadUri,
    long MaximumBytes,
    long? ExpectedBytes = null,
    string? ExpectedSha256 = null);

/// <summary>
/// Metadata for a model that can be downloaded by the application UI.
/// Download URLs are part of the application catalog rather than user input.
/// </summary>
public sealed record EmbeddingModelPackage(
    string ModelId,
    string DisplayName,
    string DirectoryName,
    int Dimension,
    string EstimatedSizeText,
    IReadOnlyList<EmbeddingModelFile> Files)
{
    public long? ExpectedTotalBytes => Files.All(file => file.ExpectedBytes is not null)
        ? Files.Sum(file => file.ExpectedBytes!.Value)
        : null;

    /// <summary>
    /// CPU-friendly BGE model. Qdrant publishes the ONNX export together with
    /// the matching BERT vocabulary/configuration; the model hash pins the
    /// large binary while the small tokenizer files remain bounded by size.
    /// </summary>
    public static EmbeddingModelPackage BgeSmallZhV15 { get; } = new(
        OnnxEmbeddingOptions.DefaultModelId,
        "BGE Small 中文语义模型",
        "bge-small-zh-v1.5",
        512,
        "约 95 MB",
        [
            new EmbeddingModelFile(
                "model.onnx",
                new Uri("https://huggingface.co/Qdrant/bge-small-zh-v1.5/resolve/main/model_optimized.onnx"),
                MaximumBytes: 150L * 1024 * 1024,
                ExpectedSha256: "1294ea4b6331115a353d81f96b85e8c8d7fdcc284453d5b2fab5b016230aad38"),
            new EmbeddingModelFile(
                "vocab.txt",
                new Uri("https://huggingface.co/Qdrant/bge-small-zh-v1.5/resolve/main/vocab.txt"),
                MaximumBytes: 2L * 1024 * 1024),
            new EmbeddingModelFile(
                "tokenizer_config.json",
                new Uri("https://huggingface.co/Qdrant/bge-small-zh-v1.5/resolve/main/tokenizer_config.json"),
                MaximumBytes: 1L * 1024 * 1024)
        ]);
}

public sealed record EmbeddingModelDownloadProgress(
    string ModelId,
    string FileName,
    long BytesReceived,
    long? FileTotalBytes,
    long CompletedBytes,
    long? TotalBytes)
{
    public double? FilePercentage => FileTotalBytes is > 0
        ? Math.Clamp(BytesReceived * 100d / FileTotalBytes.Value, 0d, 100d)
        : null;

    public double? OverallPercentage => TotalBytes is > 0
        ? Math.Clamp(CompletedBytes * 100d / TotalBytes.Value, 0d, 100d)
        : null;
}

/// <summary>
/// Downloads a known embedding package into AppPaths. Files are written to a
/// partial path and moved into place only after the response and optional
/// SHA-256 check succeed, so a cancelled download never looks installed.
/// </summary>
public sealed class EmbeddingModelDownloadService : IDisposable
{
    private const string DefaultUserAgent = "Kkindle-Embedding-Model";

    private readonly AppPaths _paths;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly SemaphoreSlim _downloadGate = new(1, 1);
    private bool _disposed;

    public EmbeddingModelDownloadService(AppPaths paths, HttpClient? httpClient = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _httpClient = httpClient ?? new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = true
        })
        {
            Timeout = TimeSpan.FromMinutes(30)
        };
        _ownsHttpClient = httpClient is null;
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
            _httpClient.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue(DefaultUserAgent, "1.0"));
    }

    public string GetModelDirectory(EmbeddingModelPackage package)
    {
        ValidatePackage(package);
        return Path.Combine(_paths.EmbeddingModels, package.DirectoryName);
    }

    public bool IsInstalled(EmbeddingModelPackage package)
    {
        ValidatePackage(package);
        var directory = GetModelDirectory(package);
        return package.Files.All(file =>
        {
            var path = Path.Combine(directory, file.TargetFileName);
            try
            {
                return new FileInfo(path).Length > 0;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        });
    }

    public Task<string> DownloadAsync(
        EmbeddingModelPackage package,
        IProgress<EmbeddingModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        DownloadAsync(
            package,
            force: false,
            progress: progress,
            cancellationToken: cancellationToken);

    public async Task<string> DownloadAsync(
        EmbeddingModelPackage package,
        bool force,
        IProgress<EmbeddingModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidatePackage(package);
        await _downloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = GetModelDirectory(package);
            Directory.CreateDirectory(directory);
            var completedBytes = 0L;

            foreach (var file in package.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destination = GetDestinationPath(directory, file.TargetFileName);
                if (!force && await IsFileUsableAsync(file, destination, cancellationToken).ConfigureAwait(false))
                {
                    var existingLength = new FileInfo(destination).Length;
                    completedBytes += existingLength;
                    ReportProgress(
                        progress,
                        package,
                        file,
                        existingLength,
                        existingLength,
                        completedBytes);
                    continue;
                }

                var received = await DownloadFileAsync(
                    package,
                    file,
                    destination,
                    completedBytes,
                    progress,
                    cancellationToken).ConfigureAwait(false);
                completedBytes += received;
            }

            if (!IsInstalled(package))
                throw new InvalidDataException("Embedding 模型下载完成，但模型文件不完整。" );
            return directory;
        }
        finally
        {
            _downloadGate.Release();
        }
    }

    private async Task<bool> IsFileUsableAsync(
        EmbeddingModelFile file,
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return false;
        var length = new FileInfo(path).Length;
        if (length <= 0 || length > file.MaximumBytes) return false;
        if (file.ExpectedBytes is > 0 && length != file.ExpectedBytes.Value) return false;
        if (string.IsNullOrWhiteSpace(file.ExpectedSha256)) return true;

        await using var input = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            hasher.AppendData(buffer, 0, read);
        }
        var actualHash = Convert.ToHexString(hasher.GetHashAndReset());
        return actualHash.Equals(file.ExpectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<long> DownloadFileAsync(
        EmbeddingModelPackage package,
        EmbeddingModelFile file,
        string destination,
        long completedBytes,
        IProgress<EmbeddingModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var partialPath = destination + ".partial";
        try
        {
            TryDelete(partialPath);
            using var request = new HttpRequestMessage(HttpMethod.Get, file.DownloadUri);
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            EnsureTrustedDownloadUri(response.RequestMessage?.RequestUri ?? file.DownloadUri);

            var responseLength = response.Content.Headers.ContentLength;
            if (responseLength is <= 0) responseLength = file.ExpectedBytes;
            if (responseLength is > 0 && responseLength > file.MaximumBytes)
                throw new InvalidDataException($"Embedding 模型文件 {file.TargetFileName} 超过大小限制。" );

            await using var input = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long received = 0;
            await using (var output = new FileStream(
                partialPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                while (true)
                {
                    var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0) break;
                    received += read;
                    if (received > file.MaximumBytes)
                        throw new InvalidDataException($"Embedding 模型文件 {file.TargetFileName} 超过大小限制。" );
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    hasher.AppendData(buffer, 0, read);
                    ReportProgress(
                        progress,
                        package,
                        file,
                        received,
                        responseLength,
                        completedBytes + received);
                }
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (file.ExpectedBytes is > 0 && received != file.ExpectedBytes.Value)
                throw new InvalidDataException($"Embedding 模型文件 {file.TargetFileName} 大小校验失败。" );
            var actualHash = Convert.ToHexString(hasher.GetHashAndReset());
            if (!string.IsNullOrWhiteSpace(file.ExpectedSha256)
                && !actualHash.Equals(file.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Embedding 模型文件 {file.TargetFileName} SHA-256 校验失败。" );
            }

            File.Move(partialPath, destination, overwrite: true);
            ReportProgress(
                progress,
                package,
                file,
                received,
                responseLength ?? received,
                completedBytes + received);
            return received;
        }
        finally
        {
            TryDelete(partialPath);
        }
    }

    private static void ReportProgress(
        IProgress<EmbeddingModelDownloadProgress>? progress,
        EmbeddingModelPackage package,
        EmbeddingModelFile file,
        long bytesReceived,
        long? fileTotalBytes,
        long completedBytes)
    {
        progress?.Report(new EmbeddingModelDownloadProgress(
            package.ModelId,
            file.TargetFileName,
            bytesReceived,
            fileTotalBytes,
            completedBytes,
            package.ExpectedTotalBytes));
    }

    private static string GetDestinationPath(string directory, string targetFileName)
    {
        var destination = Path.GetFullPath(Path.Combine(directory, targetFileName));
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Embedding 模型文件名无效。" );
        return destination;
    }

    private static void ValidatePackage(EmbeddingModelPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (string.IsNullOrWhiteSpace(package.ModelId)
            || string.IsNullOrWhiteSpace(package.DirectoryName)
            || package.Files.Count == 0)
            throw new ArgumentException("Embedding 模型包信息不完整。", nameof(package));
        if (Path.GetFileName(package.DirectoryName) != package.DirectoryName)
            throw new ArgumentException("Embedding 模型目录名无效。", nameof(package));

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in package.Files)
        {
            if (string.IsNullOrWhiteSpace(file.TargetFileName)
                || Path.GetFileName(file.TargetFileName) != file.TargetFileName
                || file.MaximumBytes <= 0
                || !names.Add(file.TargetFileName))
                throw new ArgumentException("Embedding 模型文件清单无效。", nameof(package));
            EnsureTrustedDownloadUri(file.DownloadUri);
            if (file.ExpectedSha256 is not null
                && (file.ExpectedSha256.Length != 64
                    || file.ExpectedSha256.Any(value => !Uri.IsHexDigit(value))))
                throw new ArgumentException("Embedding 模型 SHA-256 校验值无效。", nameof(package));
        }
    }

    private static void EnsureTrustedDownloadUri(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || !IsTrustedHost(uri.Host))
            throw new InvalidDataException("Embedding 模型下载地址不是受信任的 HTTPS 地址。" );
    }

    private static bool IsTrustedHost(string host) =>
        host.Equals("huggingface.co", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".huggingface.co", StringComparison.OrdinalIgnoreCase)
        || host.Equals("hf.co", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".hf.co", StringComparison.OrdinalIgnoreCase);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _downloadGate.Dispose();
        if (_ownsHttpClient) _httpClient.Dispose();
    }
}
