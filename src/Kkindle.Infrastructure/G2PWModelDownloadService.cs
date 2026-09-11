using System.IO.Compression;
using System.Net.Http.Headers;

namespace Kkindle.Infrastructure;

public sealed record G2PWModelDownloadSource(
    Uri ArchiveUri,
    Uri VocabularyUri,
    Uri PinyinMapUri,
    long? ExpectedArchiveBytes = null);

public static class G2PWModelPackage
{
    public const string Id = "g2pw";
    public const string DirectoryName = "g2pw-v2";
    public const string DisplayName = "g2pW v2（高精度）";
    public const string EstimatedSizeText = "约 590 MB";
    public const long ArchiveExpectedBytes = 589_075_404;

    public static G2PWModelDownloadSource DefaultSource { get; } = new(
        new Uri("https://storage.googleapis.com/esun-ai/g2pW/G2PWModel-v2-onnx.zip"),
        new Uri("https://huggingface.co/google-bert/bert-base-chinese/resolve/main/vocab.txt"),
        new Uri("https://raw.githubusercontent.com/GitYCC/g2pW/master/g2pw/bopomofo_to_pinyin_wo_tune_dict.json"),
        ArchiveExpectedBytes);

    public static IReadOnlyList<string> RequiredFiles { get; } =
    [
        "g2pw.onnx",
        "config.py",
        "MONOPHONIC_CHARS.txt",
        "POLYPHONIC_CHARS.txt",
        "version",
        "vocab.txt",
        "bopomofo_to_pinyin_wo_tune_dict.json"
    ];
}

public sealed record G2PWModelDownloadProgress(
    string Stage,
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
/// Downloads and installs the official g2pW v2 ONNX package. The archive is
/// unpacked into a private staging directory and only becomes visible as an
/// installed model after every required file has arrived.
/// </summary>
public sealed class G2PWModelDownloadService : IDisposable
{
    private const string DefaultUserAgent = "Kkindle-g2pW-Model";
    private const long MaximumArchiveBytes = 700L * 1024 * 1024;
    private const long MaximumArchiveEntryBytes = 700L * 1024 * 1024;
    private const long MaximumVocabularyBytes = 2L * 1024 * 1024;
    private const long MaximumPinyinMapBytes = 2L * 1024 * 1024;

    private readonly AppPaths _paths;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly G2PWModelDownloadSource _source;
    private readonly SemaphoreSlim _downloadGate = new(1, 1);
    private bool _disposed;

    public G2PWModelDownloadService(
        AppPaths paths,
        HttpClient? httpClient = null,
        G2PWModelDownloadSource? source = null,
        string? proxyAddress = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _source = source ?? G2PWModelPackage.DefaultSource;
        ValidateSource(_source);
        _httpClient = httpClient ?? new HttpClient(CreateHttpHandler(proxyAddress))
        {
            Timeout = TimeSpan.FromHours(2)
        };
        _ownsHttpClient = httpClient is null;
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
            _httpClient.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue(DefaultUserAgent, "1.0"));
    }

    public string ModelDirectory => Path.Combine(_paths.PinyinModels, G2PWModelPackage.DirectoryName);

    public bool IsInstalled()
    {
        var directory = ModelDirectory;
        if (!Directory.Exists(directory)) return false;
        foreach (var file in G2PWModelPackage.RequiredFiles)
        {
            try
            {
                var length = new FileInfo(Path.Combine(directory, file)).Length;
                if (length <= 0) return false;
                if (file.Equals("g2pw.onnx", StringComparison.OrdinalIgnoreCase)
                    && length < 1L * 1024 * 1024)
                    return false;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }
        return true;
    }

    public Task<string> DownloadAsync(
        IProgress<G2PWModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        DownloadAsync(false, progress, cancellationToken);

    public async Task<string> DownloadAsync(
        bool force,
        IProgress<G2PWModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _downloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? stagingDirectory = null;
        try
        {
            if (!force && IsInstalled()) return ModelDirectory;

            _paths.EnsureDirectories();
            stagingDirectory = Path.Combine(
                _paths.PinyinModels,
                $"{G2PWModelPackage.DirectoryName}.partial-{Guid.NewGuid():N}");
            Directory.CreateDirectory(stagingDirectory);

            Report(progress, "下载 g2pW 模型", "G2PWModel-v2-onnx.zip", 0, _source.ExpectedArchiveBytes, 0);
            var archivePath = Path.Combine(stagingDirectory, "G2PWModel-v2-onnx.zip");
            var archiveBytes = await DownloadFileAsync(
                _source.ArchiveUri,
                archivePath,
                MaximumArchiveBytes,
                _source.ExpectedArchiveBytes,
                "下载 g2pW 模型",
                "G2PWModel-v2-onnx.zip",
                0,
                progress,
                cancellationToken).ConfigureAwait(false);

            Report(progress, "解压 g2pW 模型", "g2pw.onnx", 0, null, archiveBytes);
            await ExtractModelArchiveAsync(archivePath, stagingDirectory, progress, cancellationToken)
                .ConfigureAwait(false);
            TryDeleteFile(archivePath);

            var vocabularyPath = Path.Combine(stagingDirectory, "vocab.txt");
            var mapPath = Path.Combine(stagingDirectory, "bopomofo_to_pinyin_wo_tune_dict.json");
            var completedBytes = archiveBytes;
            var vocabularyBytes = await DownloadFileAsync(
                _source.VocabularyUri,
                vocabularyPath,
                MaximumVocabularyBytes,
                expectedBytes: null,
                "下载 BERT 词表",
                "vocab.txt",
                completedBytes,
                progress,
                cancellationToken).ConfigureAwait(false);
            completedBytes += vocabularyBytes;
            await DownloadFileAsync(
                _source.PinyinMapUri,
                mapPath,
                MaximumPinyinMapBytes,
                expectedBytes: null,
                "下载拼音映射",
                "bopomofo_to_pinyin_wo_tune_dict.json",
                completedBytes,
                progress,
                cancellationToken).ConfigureAwait(false);

            ValidateInstalledDirectory(stagingDirectory);
            InstallStagedDirectory(stagingDirectory);
            stagingDirectory = null;
            Report(progress, "完成", G2PWModelPackage.DisplayName, 1, 1, completedBytes);
            return ModelDirectory;
        }
        finally
        {
            if (stagingDirectory is not null)
                TryDeleteDirectory(stagingDirectory);
            _downloadGate.Release();
        }
    }

    private async Task<long> DownloadFileAsync(
        Uri uri,
        string destination,
        long maximumBytes,
        long? expectedBytes,
        string stage,
        string fileName,
        long completedBytes,
        IProgress<G2PWModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var partialPath = destination + ".partial";
        try
        {
            TryDeleteFile(partialPath);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            EnsureTrustedDownloadUri(response.RequestMessage?.RequestUri ?? uri);

            var responseLength = response.Content.Headers.ContentLength;
            if (responseLength is { } length && length > maximumBytes)
                throw new InvalidDataException($"g2pW 文件 {fileName} 超过大小限制。");
            if (expectedBytes is > 0 && responseLength is > 0 && responseLength != expectedBytes)
                throw new InvalidDataException($"g2pW 文件 {fileName} 大小校验失败。");

            await using var input = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            long received = 0;
            await using (var output = new FileStream(
                partialPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[81920];
                while (true)
                {
                    var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0) break;
                    received += read;
                    if (received > maximumBytes)
                        throw new InvalidDataException($"g2pW 文件 {fileName} 超过大小限制。");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                        .ConfigureAwait(false);
                    Report(
                        progress,
                        stage,
                        fileName,
                        received,
                        responseLength ?? expectedBytes,
                        completedBytes + received);
                }
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (expectedBytes is > 0 && received != expectedBytes.Value)
                throw new InvalidDataException($"g2pW 文件 {fileName} 大小校验失败。");
            File.Move(partialPath, destination, overwrite: true);
            return received;
        }
        finally
        {
            TryDeleteFile(partialPath);
        }
    }

    private static async Task ExtractModelArchiveAsync(
        string archivePath,
        string stagingDirectory,
        IProgress<G2PWModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var entries = archive.Entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Name))
            .ToDictionary(entry => Path.GetFileName(entry.FullName), StringComparer.OrdinalIgnoreCase);
        foreach (var file in new[]
        {
            "g2pw.onnx",
            "config.py",
            "MONOPHONIC_CHARS.txt",
            "POLYPHONIC_CHARS.txt",
            "version"
        })
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!entries.TryGetValue(file, out var entry))
                throw new InvalidDataException($"g2pW 模型压缩包缺少 {file}。");
            if (entry.Length <= 0 || entry.Length > MaximumArchiveEntryBytes)
                throw new InvalidDataException($"g2pW 模型文件 {file} 大小无效。");

            var destination = Path.Combine(stagingDirectory, file);
            await using var input = entry.Open();
            await using var output = new FileStream(
                destination,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await input.CopyToAsync(output, 81920, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            Report(progress, "解压 g2pW 模型", file, entry.Length, entry.Length, entry.Length);
        }
    }

    private void InstallStagedDirectory(string stagingDirectory)
    {
        var finalDirectory = ModelDirectory;
        var backupDirectory = finalDirectory + $".backup-{Guid.NewGuid():N}";
        var movedOldDirectory = false;
        try
        {
            if (Directory.Exists(finalDirectory))
            {
                Directory.Move(finalDirectory, backupDirectory);
                movedOldDirectory = true;
            }
            Directory.Move(stagingDirectory, finalDirectory);
            if (movedOldDirectory) TryDeleteDirectory(backupDirectory);
        }
        catch
        {
            if (!Directory.Exists(finalDirectory) && movedOldDirectory && Directory.Exists(backupDirectory))
                Directory.Move(backupDirectory, finalDirectory);
            throw;
        }
    }

    private static void ValidateInstalledDirectory(string directory)
    {
        foreach (var file in G2PWModelPackage.RequiredFiles)
        {
            var path = Path.Combine(directory, file);
            if (!File.Exists(path) || new FileInfo(path).Length <= 0)
                throw new InvalidDataException($"g2pW 模型缺少 {file}。");
        }
        if (new FileInfo(Path.Combine(directory, "g2pw.onnx")).Length < 1L * 1024 * 1024)
            throw new InvalidDataException("g2pW ONNX 模型文件过小。");
    }

    private static void Report(
        IProgress<G2PWModelDownloadProgress>? progress,
        string stage,
        string fileName,
        long bytesReceived,
        long? fileTotalBytes,
        long completedBytes) => progress?.Report(new G2PWModelDownloadProgress(
            stage,
            fileName,
            bytesReceived,
            fileTotalBytes,
            completedBytes,
            G2PWModelPackage.ArchiveExpectedBytes));

    private static void ValidateSource(G2PWModelDownloadSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        EnsureTrustedDownloadUri(source.ArchiveUri);
        EnsureTrustedDownloadUri(source.VocabularyUri);
        EnsureTrustedDownloadUri(source.PinyinMapUri);
        if (source.ExpectedArchiveBytes is <= 0 or > MaximumArchiveBytes)
            throw new ArgumentException("g2pW 模型压缩包大小校验值无效。", nameof(source));
    }

    private static HttpMessageHandler CreateHttpHandler(string? proxyAddress)
    {
        if (!string.IsNullOrWhiteSpace(proxyAddress))
        {
            try
            {
                return TranslationProxy.CreateHandler(
                    TranslationProxy.NormalizeAddress(proxyAddress));
            }
            catch (ArgumentException)
            {
                // A malformed translation proxy must not prevent the app from
                // opening; the model download will fall back to system proxy.
            }
        }

        return new HttpClientHandler
        {
            AllowAutoRedirect = true,
            UseProxy = true
        };
    }

    private static void EnsureTrustedDownloadUri(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || !IsTrustedHost(uri.Host))
            throw new InvalidDataException("g2pW 模型下载地址不是受信任的 HTTPS 地址。");
    }

    private static bool IsTrustedHost(string host) =>
        host.Equals("storage.googleapis.com", StringComparison.OrdinalIgnoreCase)
        || host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
        || host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
        || host.Equals("huggingface.co", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".huggingface.co", StringComparison.OrdinalIgnoreCase)
        || host.Equals("hf.co", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".hf.co", StringComparison.OrdinalIgnoreCase);

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _downloadGate.Dispose();
        if (_ownsHttpClient) _httpClient.Dispose();
    }
}
