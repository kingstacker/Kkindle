using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kkindle.Core;

namespace Kkindle.Infrastructure;

/// <summary>
/// Stores generated audio under the user's cache directory. The file name is
/// derived from all request inputs, so changing provider, model, voice, rate,
/// pitch, format, sample rate or volume automatically selects a different file.
/// </summary>
public sealed class TtsCacheManager
{
    public const long DefaultMaximumBytes = 1024L * 1024 * 1024;
    private static readonly string[] CacheAudioExtensions = [".mp3", ".wav", ".ogg", ".opus"];
    private readonly string _temporaryRoot;
    private readonly long _maximumBytes;
    private readonly SemaphoreSlim _capacityGate = new(1, 1);

    public TtsCacheManager(
        string? cacheRoot = null,
        long maximumBytes = DefaultMaximumBytes)
    {
        if (maximumBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        CacheRoot = Path.GetFullPath(
            string.IsNullOrWhiteSpace(cacheRoot)
                ? ResolveDefaultCacheRoot()
                : cacheRoot);
        _temporaryRoot = Path.GetFullPath(Path.GetTempPath());
        _maximumBytes = maximumBytes;
    }

    public string CacheRoot { get; }
    public long MaximumBytes => _maximumBytes;

    public string GetCacheKey(string text, TtsOptions options)
    {
        ArgumentNullException.ThrowIfNull(text);
        var normalized = TtsOptions.Normalize(options);
        var canonical = string.Join(
            "\u001f",
            text,
            normalized.Provider,
            normalized.Model,
            normalized.Voice,
            normalized.AudioFormat,
            normalized.SampleRate.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            normalized.RatePercent.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            normalized.PitchHz.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            normalized.VolumePercent.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        return Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }

    public string GetCachePath(
        string bookKey,
        string chapterKey,
        string text,
        TtsOptions options)
    {
        var bookDirectory = Path.Combine(
            CacheRoot,
            "book_" + ShortHash(bookKey));
        var chapterDirectory = Path.Combine(
            bookDirectory,
            "chapter_" + ShortHash(chapterKey));
        var extension = TtsOptions.Normalize(options).AudioFormat;
        return Path.Combine(
            chapterDirectory,
            GetCacheKey(text, options) + "." + extension);
    }

    public Task<string?> FindAsync(
        string bookKey,
        string chapterKey,
        string text,
        TtsOptions options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = GetCachePath(bookKey, chapterKey, text, options);
        if (!File.Exists(path)) return Task.FromResult<string?>(null);
        try
        {
            if (new FileInfo(path).Length <= 0)
                return Task.FromResult<string?>(null);
            Touch(path);
            return Task.FromResult<string?>(path);
        }
        catch (FileNotFoundException)
        {
            return Task.FromResult<string?>(null);
        }
        catch (DirectoryNotFoundException)
        {
            return Task.FromResult<string?>(null);
        }
    }

    private sealed record TimingFile(string AudioHash, TtsTimingMarker[] Markers);

    public async Task<IReadOnlyList<TtsTimingMarker>?> ReadTimingAsync(
        string audioPath, CancellationToken cancellationToken = default)
    {
        try
        {
            var metadataPath = audioPath + ".timing.json";
            if (!File.Exists(metadataPath)) return null;
            await using var metadata = File.OpenRead(metadataPath);
            var timing = await JsonSerializer.DeserializeAsync<TimingFile>(metadata, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (timing?.Markers is not { Length: > 0 }) return null;
            await using var audio = File.OpenRead(audioPath);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(audio, cancellationToken).ConfigureAwait(false));
            return string.Equals(hash, timing.AudioHash, StringComparison.Ordinal) ? timing.Markers : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    public async Task WriteTimingAsync(string audioPath, string sourcePath,
        IReadOnlyList<TtsTimingMarker>? markers, CancellationToken cancellationToken = default)
    {
        if (markers is not { Count: > 0 }) return;
        var temporaryPath = audioPath + ".timing.tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using var source = File.OpenRead(sourcePath);
            var sourceHash = Convert.ToHexString(await SHA256.HashDataAsync(source, cancellationToken).ConfigureAwait(false));
            await using var audio = File.OpenRead(audioPath);
            var audioHash = Convert.ToHexString(await SHA256.HashDataAsync(audio, cancellationToken).ConfigureAwait(false));
            if (!string.Equals(sourceHash, audioHash, StringComparison.Ordinal)) return;
            await using (var metadata = File.Create(temporaryPath))
                await JsonSerializer.SerializeAsync(metadata, new TimingFile(audioHash, markers.ToArray()),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, audioPath + ".timing.json", overwrite: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { /* Timing is optional; a cache error must not break audio playback. */ }
        finally { DeleteFileQuietly(temporaryPath); }
    }

    /// <summary>
    /// Copies a successful engine result into the cache atomically. The source
    /// is left to the caller's cleanup policy; the queue deletes its temporary
    /// file after this method returns.
    /// </summary>
    public async Task<string> WriteAsync(
        string bookKey,
        string chapterKey,
        string text,
        TtsOptions options,
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("TTS 音频文件不存在。", sourcePath);

        var destination = GetCachePath(bookKey, chapterKey, text, options);
        if (File.Exists(destination))
        {
            try
            {
                if (new FileInfo(destination).Length > 0)
                {
                    Touch(destination);
                    return destination;
                }
            }
            catch (FileNotFoundException)
            {
            }
            catch (DirectoryNotFoundException)
            {
            }
        }

        var directory = Path.GetDirectoryName(destination);
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException("TTS 缓存目录无效。");
        Directory.CreateDirectory(directory);

        var temporaryPath = destination + $".tmp-{Guid.NewGuid():N}";
        try
        {
            await using (var input = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81920,
                useAsync: true))
            await using (var output = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                useAsync: true))
            {
                await input.CopyToAsync(output, 81920, cancellationToken)
                    .ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, destination, overwrite: true);
            await EnforceCapacityAsync(destination, cancellationToken)
                .ConfigureAwait(false);
            return destination;
        }
        catch
        {
            DeleteFileQuietly(temporaryPath);
            throw;
        }
    }

    public Task DeleteBookAsync(
        string bookKey,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = Path.Combine(CacheRoot, "book_" + ShortHash(bookKey));
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        return Task.CompletedTask;
    }

    public Task ClearAllAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(CacheRoot)) return Task.CompletedTask;

        foreach (var path in Directory.EnumerateFiles(
                     CacheRoot,
                     "*",
                     SearchOption.AllDirectories)
                     .ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeleteFileQuietly(path);
        }

        foreach (var path in Directory.EnumerateDirectories(
                     CacheRoot,
                     "*",
                     SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Length)
                     .ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { Directory.Delete(path, recursive: false); }
            catch (DirectoryNotFoundException) { }
        }

        return Task.CompletedTask;
    }

    public Task<TtsCacheStatistics> GetStatisticsAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(CacheRoot))
            return Task.FromResult(new TtsCacheStatistics(0, 0));

        var fileCount = 0;
        long totalBytes = 0;
        foreach (var bookDirectory in Directory.EnumerateDirectories(
                     CacheRoot,
                     "book_*",
                     SearchOption.TopDirectoryOnly))
        {
            foreach (var path in EnumerateCacheFiles(bookDirectory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var length = new FileInfo(path).Length;
                    if (length <= 0) continue;
                    fileCount++;
                    totalBytes += length;
                }
                catch (FileNotFoundException)
                {
                }
                catch (DirectoryNotFoundException)
                {
                }
            }
        }

        return Task.FromResult(new TtsCacheStatistics(fileCount, totalBytes));
    }

    /// <summary>
    /// Deletes only files below the process temp directory. Cached files are
    /// never accepted here, which makes cancellation cleanup safe.
    /// </summary>
    public void DeleteTemporaryFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            var candidate = Path.GetFullPath(path);
            var relative = Path.GetRelativePath(_temporaryRoot, candidate);
            if (relative == ".."
                || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || Path.IsPathRooted(relative))
            {
                return;
            }

            DeleteFileQuietly(candidate);
        }
        catch
        {
        }
    }

    private async Task EnforceCapacityAsync(
        string protectedPath,
        CancellationToken cancellationToken)
    {
        await _capacityGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!Directory.Exists(CacheRoot)) return;

            var files = new List<(string Path, long Length, DateTime LastAccessUtc)>();
            long totalBytes = 0;
            foreach (var path in EnumerateCacheFiles(CacheRoot))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var info = new FileInfo(path);
                    if (info.Length <= 0) continue;
                    files.Add((path, info.Length, info.LastAccessTimeUtc));
                    totalBytes += info.Length;
                }
                catch (FileNotFoundException)
                {
                }
                catch (DirectoryNotFoundException)
                {
                }
            }

            if (totalBytes <= _maximumBytes) return;
            var keep = Path.GetFullPath(protectedPath);
            foreach (var file in files
                         .OrderBy(item => item.LastAccessUtc)
                         .ThenBy(item => item.Path, StringComparer.Ordinal))
            {
                if (string.Equals(file.Path, keep, StringComparison.OrdinalIgnoreCase))
                    continue;
                DeleteFileQuietly(file.Path);
                DeleteFileQuietly(file.Path + ".timing.json");
                totalBytes -= file.Length;
                if (totalBytes <= _maximumBytes) break;
            }
        }
        finally
        {
            _capacityGate.Release();
        }
    }

    private static IEnumerable<string> EnumerateCacheFiles(string root)
    {
        if (!Directory.Exists(root)) yield break;
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (CacheAudioExtensions.Contains(
                    Path.GetExtension(path),
                    StringComparer.OrdinalIgnoreCase))
            {
                yield return path;
            }
        }
    }

    private static void Touch(string path)
    {
        try { File.SetLastAccessTimeUtc(path, DateTime.UtcNow); }
        catch
        {
            // Cache access metadata is best effort and must never block playback.
        }
    }

    private static string ResolveDefaultCacheRoot()
    {
        string? baseDirectory;
        if (OperatingSystem.IsWindows())
        {
            baseDirectory = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData);
        }
        else if (OperatingSystem.IsMacOS() || OperatingSystem.IsIOS())
        {
            // macOS convention: disposable caches live in ~/Library/Caches,
            // never in ~/.cache (which is a Linux XDG location).
            var home = Environment.GetFolderPath(
                Environment.SpecialFolder.UserProfile);
            baseDirectory = string.IsNullOrWhiteSpace(home)
                ? null
                : Path.Combine(home, "Library", "Caches");
        }
        else
        {
            baseDirectory = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
            if (string.IsNullOrWhiteSpace(baseDirectory)
                || !Path.IsPathRooted(baseDirectory))
            {
                var home = Environment.GetFolderPath(
                    Environment.SpecialFolder.UserProfile);
                baseDirectory = Path.Combine(home, ".cache");
            }
        }

        if (string.IsNullOrWhiteSpace(baseDirectory))
            baseDirectory = AppContext.BaseDirectory;
        return Path.Combine(baseDirectory, "Kkindle", "tts");
    }

    private static string ShortHash(string value)
    {
        var hash = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(value ?? string.Empty)))
            .ToLowerInvariant();
        return hash[..16];
    }

    private static void DeleteFileQuietly(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
        }
    }
}
