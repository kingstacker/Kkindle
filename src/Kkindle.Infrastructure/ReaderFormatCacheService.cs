using Kkindle.Core;
using System.IO.Compression;
using System.Xml.Linq;

namespace Kkindle.Infrastructure;

public sealed record ReaderFormatCacheResult(string EpubPath, string CacheKey, bool CacheHit);

/// <summary>
/// Keeps native AZW3 resources and Calibre MOBI conversions for repeated opens.
/// </summary>
public sealed class ReaderFormatCacheService
{
    private const string CacheVersion = "v3";
    private const string Azw3CacheVersion = "v4";
    private readonly string _cacheDirectory;
    private readonly IBookFormatConverter _converter;
    private readonly SemaphoreSlim _conversionGate = new(1, 1);

    public ReaderFormatCacheService(AppPaths paths, IBookFormatConverter converter)
    {
        _cacheDirectory = Path.Combine(paths.ReaderCache, "format-conversions");
        _converter = converter;
    }

    public async Task<ReaderFormatCacheResult> PrepareEpubAsync(
        string sourcePath,
        string sourceHash,
        string sourceFormat,
        CancellationToken cancellationToken = default)
    {
        var cacheKey = NormalizeHash(sourceHash);
        var format = sourceFormat.Trim().TrimStart('.').ToLowerInvariant();
        if (format is not ("azw3" or "mobi"))
            throw new NotSupportedException($"不支持为 {sourceFormat} 创建阅读缓存。");
        var cacheVersion = format == "azw3" ? Azw3CacheVersion : CacheVersion;

        Directory.CreateDirectory(_cacheDirectory);
        var destination = Path.GetFullPath(Path.Combine(_cacheDirectory, $"{cacheVersion}-{format}-{cacheKey}.epub"));
        EnsureContainedPath(destination);
        if (IsUsable(destination))
            return new ReaderFormatCacheResult(destination, cacheKey, CacheHit: true);

        await _conversionGate.WaitAsync(cancellationToken);
        try
        {
            if (IsUsable(destination))
                return new ReaderFormatCacheResult(destination, cacheKey, CacheHit: true);

            if (File.Exists(destination)) File.Delete(destination);
            var temporary = Path.Combine(
                _cacheDirectory,
                $".{cacheVersion}-{format}-{cacheKey}-{Guid.NewGuid():N}.tmp.epub");
            EnsureContainedPath(temporary);
            try
            {
                if (format == "azw3")
                    await Azw3ReaderService.PrepareEpubAsync(sourcePath, temporary, cancellationToken);
                else
                    await _converter.ConvertAsync(sourcePath, temporary, cancellationToken: cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsUsable(temporary))
                    throw new InvalidDataException("AZW3/MOBI 转换未生成有效的 EPUB 阅读缓存，请重试。");
                File.Move(temporary, destination, overwrite: false);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch { }
            }

            return new ReaderFormatCacheResult(destination, cacheKey, CacheHit: false);
        }
        finally
        {
            _conversionGate.Release();
        }
    }

    private static string NormalizeHash(string hash)
    {
        var normalized = string.Concat(hash.Where(Uri.IsHexDigit)).ToLowerInvariant();
        if (normalized.Length != 64)
            throw new InvalidDataException("书籍校验值无效。");
        return normalized;
    }

    private static bool IsUsable(string path)
    {
        try
        {
            if (new FileInfo(path) is not { Exists: true, Length: > 0 }) return false;
            using var archive = ZipFile.OpenRead(path);
            var container = archive.GetEntry("META-INF/container.xml");
            if (container is null) return false;
            using var stream = container.Open();
            var packagePath = XDocument.Load(stream).Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "rootfile")?.Attribute("full-path")?.Value;
            var package = string.IsNullOrWhiteSpace(packagePath) ? null : archive.GetEntry(packagePath);
            if (package is null) return false;
            using var packageStream = package.Open();
            return XDocument.Load(packageStream).Descendants().Any(e => e.Name.LocalName == "itemref");
        }
        catch { return false; }
    }

    private void EnsureContainedPath(string path)
    {
        var root = Path.GetFullPath(_cacheDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("阅读转换缓存路径无效。");
    }
}
