using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Kkindle.Core;

namespace Kkindle.Infrastructure;

public sealed class UpdateService : IDisposable
{
    private const string LatestManifestUrl = "https://github.com/kingstacker/Kkindle/releases/latest/download/update-manifest.json";
    private const string LatestReleasePageUrl = "https://github.com/kingstacker/Kkindle/releases/latest";
    private const string LatestReleaseUrl = "https://api.github.com/repos/kingstacker/Kkindle/releases/latest";
    private const long MaximumChecksumFileSize = 1024 * 1024;
    private static readonly Regex VersionPattern = new(
        "^[vV]?(?<major>0|[1-9][0-9]*)\\.(?<minor>0|[1-9][0-9]*)\\.(?<patch>0|[1-9][0-9]*)(?:\\.(?<revision>0|[1-9][0-9]*))?(?:-(?<prerelease>[0-9A-Za-z.-]+))?(?:\\+[0-9A-Za-z.-]+)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ChecksumLinePattern = new(
        "^(?<hash>[0-9a-fA-F]{64})[ \\t]+[*]?(?<name>.+?)[ \\t]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IAppUpdateInstaller _installer;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly string _downloadRoot;

    public UpdateService(IAppUpdateInstaller installer)
        : this(
            installer,
            new HttpClient { Timeout = TimeSpan.FromMinutes(10) },
            Path.Combine(Path.GetTempPath(), "Kkindle", "updates"),
            ownsHttpClient: true)
    {
    }

    internal UpdateService(
        IAppUpdateInstaller installer,
        HttpClient httpClient,
        string downloadRoot,
        bool ownsHttpClient = false)
    {
        _installer = installer ?? throw new ArgumentNullException(nameof(installer));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _downloadRoot = Path.GetFullPath(downloadRoot);
        _ownsHttpClient = ownsHttpClient;
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
            _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Kkindle-Updater", "1.0"));
        _httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public bool CanInstall => _installer.CanInstall;
    public string UnavailableReason => _installer.UnavailableReason;

    public async Task<AppUpdateInfo?> CheckForUpdateAsync(
        string currentVersion,
        CancellationToken cancellationToken = default)
    {
        var current = SemanticVersion.Parse(currentVersion, "当前应用版本");
        var release = await GetLatestReleaseAsync(cancellationToken);

        if (release.Draft || release.Prerelease) return null;
        var tagName = release.TagName?.Trim() ?? string.Empty;
        var latest = SemanticVersion.Parse(tagName, "GitHub Release 版本");
        if (latest.CompareTo(current) <= 0) return null;

        var version = tagName.TrimStart('v', 'V');
        var packageName = _installer.GetPackageAssetName(version);
        var package = FindAsset(release.Assets, packageName)
            ?? throw new InvalidDataException($"GitHub Release 缺少更新包 {packageName}。");
        var checksums = FindAsset(release.Assets, "SHA256SUMS.txt")
            ?? throw new InvalidDataException("GitHub Release 缺少 SHA256SUMS.txt，已拒绝下载未校验的更新包。");

        return new AppUpdateInfo(
            version,
            tagName,
            release.Body?.Trim() ?? string.Empty,
            ParseHttpsUri(release.HtmlUrl, "GitHub Release 页面"),
            package,
            checksums);
    }

    public async Task<string> DownloadAsync(
        AppUpdateInfo update,
        IProgress<AppUpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        var expectedPackageName = _installer.GetPackageAssetName(update.Version);
        if (!string.Equals(update.Package.Name, expectedPackageName, StringComparison.Ordinal)
            || !string.Equals(Path.GetFileName(update.Package.Name), update.Package.Name, StringComparison.Ordinal))
            throw new InvalidDataException("更新包文件名与当前平台不匹配。");

        var checksumText = await DownloadChecksumFileAsync(update.Checksums, cancellationToken);
        var expectedHash = FindExpectedHash(checksumText, update.Package.Name)
            ?? throw new InvalidDataException($"SHA256SUMS.txt 中没有 {update.Package.Name} 的校验值。");

        var versionDirectory = Path.Combine(_downloadRoot, update.Version);
        Directory.CreateDirectory(versionDirectory);
        var destinationPath = Path.Combine(versionDirectory, update.Package.Name);
        if (File.Exists(destinationPath)
            && await FileMatchesHashAsync(destinationPath, expectedHash, cancellationToken))
        {
            var existingLength = new FileInfo(destinationPath).Length;
            progress?.Report(new AppUpdateDownloadProgress(existingLength, existingLength));
            return destinationPath;
        }

        var partialPath = destinationPath + ".partial";
        try
        {
            File.Delete(partialPath);
            using var response = await _httpClient.GetAsync(
                update.Package.DownloadUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();
            var totalBytes = response.Content.Headers.ContentLength is > 0
                ? response.Content.Headers.ContentLength
                : update.Package.Size > 0 ? update.Package.Size : null;
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
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
                    var read = await input.ReadAsync(buffer, cancellationToken);
                    if (read == 0) break;
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    hasher.AppendData(buffer, 0, read);
                    received += read;
                    progress?.Report(new AppUpdateDownloadProgress(received, totalBytes));
                }
                await output.FlushAsync(cancellationToken);
            }

            var actualHash = Convert.ToHexString(hasher.GetHashAndReset());
            if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("更新包 SHA256 校验失败，下载内容可能不完整或已被篡改。");

            File.Move(partialPath, destinationPath, overwrite: true);
            progress?.Report(new AppUpdateDownloadProgress(received, received));
            return destinationPath;
        }
        finally
        {
            try
            {
                if (File.Exists(partialPath)) File.Delete(partialPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    public void LaunchInstaller(string packagePath) => _installer.LaunchInstaller(packagePath);

    internal static int CompareVersions(string left, string right) =>
        SemanticVersion.Parse(left, nameof(left)).CompareTo(SemanticVersion.Parse(right, nameof(right)));

    private async Task<GitHubRelease> GetLatestReleaseAsync(CancellationToken cancellationToken)
    {
        using (var manifestResponse = await _httpClient.GetAsync(LatestManifestUrl, cancellationToken))
        {
            if (manifestResponse.IsSuccessStatusCode)
                return await DeserializeReleaseAsync(manifestResponse, cancellationToken);
        }

        using (var pageResponse = await _httpClient.GetAsync(
            LatestReleasePageUrl,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken))
        {
            if (pageResponse.IsSuccessStatusCode
                && TryCreateReleaseFromRedirect(pageResponse.RequestMessage?.RequestUri) is { } redirectedRelease)
                return redirectedRelease;
        }

        using var apiResponse = await _httpClient.GetAsync(LatestReleaseUrl, cancellationToken);
        apiResponse.EnsureSuccessStatusCode();
        return await DeserializeReleaseAsync(apiResponse, cancellationToken);
    }

    private static async Task<GitHubRelease> DeserializeReleaseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<GitHubRelease>(
            responseStream,
            cancellationToken: cancellationToken)
            ?? throw new InvalidDataException("GitHub 返回了空的版本信息。");
    }

    private GitHubRelease? TryCreateReleaseFromRedirect(Uri? releaseUri)
    {
        if (releaseUri is null
            || !releaseUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return null;
        const string tagMarker = "/releases/tag/";
        var markerIndex = releaseUri.AbsolutePath.IndexOf(tagMarker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0) return null;
        var encodedTag = releaseUri.AbsolutePath[(markerIndex + tagMarker.Length)..].Trim('/');
        if (encodedTag.Length == 0 || encodedTag.Contains('/')) return null;
        var tagName = Uri.UnescapeDataString(encodedTag);
        try
        {
            _ = SemanticVersion.Parse(tagName, "GitHub Release 版本");
        }
        catch (InvalidDataException)
        {
            return null;
        }

        var version = tagName.TrimStart('v', 'V');
        var packageName = _installer.GetPackageAssetName(version);
        var downloadBase = $"https://github.com/kingstacker/Kkindle/releases/download/{Uri.EscapeDataString(tagName)}";
        return new GitHubRelease
        {
            TagName = tagName,
            HtmlUrl = releaseUri.AbsoluteUri,
            Body = string.Empty,
            Assets =
            [
                new GitHubAsset
                {
                    Name = packageName,
                    BrowserDownloadUrl = $"{downloadBase}/{Uri.EscapeDataString(packageName)}"
                },
                new GitHubAsset
                {
                    Name = "SHA256SUMS.txt",
                    BrowserDownloadUrl = $"{downloadBase}/SHA256SUMS.txt"
                }
            ]
        };
    }

    private async Task<string> DownloadChecksumFileAsync(
        AppUpdateAsset checksums,
        CancellationToken cancellationToken)
    {
        if (checksums.Size > MaximumChecksumFileSize)
            throw new InvalidDataException("GitHub Release 的校验文件大小异常。");
        using var response = await _httpClient.GetAsync(checksums.DownloadUrl, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaximumChecksumFileSize)
            throw new InvalidDataException("GitHub Release 的校验文件大小异常。");
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (text.Length > MaximumChecksumFileSize)
            throw new InvalidDataException("GitHub Release 的校验文件大小异常。");
        return text;
    }

    private static AppUpdateAsset? FindAsset(IEnumerable<GitHubAsset>? assets, string name)
    {
        var asset = assets?.FirstOrDefault(item =>
            string.Equals(item.Name, name, StringComparison.Ordinal));
        if (asset?.Name is null || asset.BrowserDownloadUrl is null) return null;
        return new AppUpdateAsset(
            asset.Name,
            ParseHttpsUri(asset.BrowserDownloadUrl, $"更新文件 {asset.Name}"),
            Math.Max(0, asset.Size));
    }

    private static string? FindExpectedHash(string checksumText, string packageName)
    {
        foreach (var line in checksumText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var match = ChecksumLinePattern.Match(line);
            if (!match.Success) continue;
            var name = match.Groups["name"].Value.Trim();
            if (string.Equals(Path.GetFileName(name), packageName, StringComparison.Ordinal))
                return match.Groups["hash"].Value;
        }
        return null;
    }

    private static async Task<bool> FileMatchesHashAsync(
        string path,
        string expectedHash,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).Equals(expectedHash, StringComparison.OrdinalIgnoreCase);
    }

    private static Uri ParseHttpsUri(string? value, string description)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{description}不是有效的 HTTPS 地址。");
        return uri;
    }

    public void Dispose()
    {
        if (_ownsHttpClient) _httpClient.Dispose();
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; init; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; init; }

        [JsonPropertyName("body")]
        public string? Body { get; init; }

        [JsonPropertyName("draft")]
        public bool Draft { get; init; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; init; }

        [JsonPropertyName("assets")]
        public GitHubAsset[] Assets { get; init; } = [];
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; init; }

        [JsonPropertyName("size")]
        public long Size { get; init; }
    }

    private readonly record struct SemanticVersion(
        int Major,
        int Minor,
        int Patch,
        int Revision,
        string[] Prerelease) : IComparable<SemanticVersion>
    {
        public static SemanticVersion Parse(string value, string description)
        {
            var match = VersionPattern.Match(value?.Trim() ?? string.Empty);
            if (!match.Success
                || !int.TryParse(match.Groups["major"].Value, out var major)
                || !int.TryParse(match.Groups["minor"].Value, out var minor)
                || !int.TryParse(match.Groups["patch"].Value, out var patch)
                || (match.Groups["revision"].Success
                    && !int.TryParse(match.Groups["revision"].Value, out _)))
                throw new InvalidDataException($"{description}“{value}”不是有效的版本号。");
            var revision = match.Groups["revision"].Success
                ? int.Parse(match.Groups["revision"].Value)
                : 0;
            var prerelease = match.Groups["prerelease"].Success
                ? match.Groups["prerelease"].Value.Split('.')
                : [];
            if (prerelease.Any(identifier => identifier.Length == 0))
                throw new InvalidDataException($"{description}“{value}”不是有效的版本号。");
            return new SemanticVersion(major, minor, patch, revision, prerelease);
        }

        public int CompareTo(SemanticVersion other)
        {
            var numeric = Major.CompareTo(other.Major);
            if (numeric == 0) numeric = Minor.CompareTo(other.Minor);
            if (numeric == 0) numeric = Patch.CompareTo(other.Patch);
            if (numeric == 0) numeric = Revision.CompareTo(other.Revision);
            if (numeric != 0) return numeric;
            if (Prerelease.Length == 0) return other.Prerelease.Length == 0 ? 0 : 1;
            if (other.Prerelease.Length == 0) return -1;

            for (var index = 0; index < Math.Min(Prerelease.Length, other.Prerelease.Length); index++)
            {
                var identifier = ComparePrereleaseIdentifier(Prerelease[index], other.Prerelease[index]);
                if (identifier != 0) return identifier;
            }
            return Prerelease.Length.CompareTo(other.Prerelease.Length);
        }

        private static int ComparePrereleaseIdentifier(string left, string right)
        {
            var leftNumeric = left.All(char.IsAsciiDigit);
            var rightNumeric = right.All(char.IsAsciiDigit);
            if (leftNumeric && !rightNumeric) return -1;
            if (!leftNumeric && rightNumeric) return 1;
            if (!leftNumeric) return string.Compare(left, right, StringComparison.Ordinal);

            var normalizedLeft = left.TrimStart('0');
            var normalizedRight = right.TrimStart('0');
            if (normalizedLeft.Length == 0) normalizedLeft = "0";
            if (normalizedRight.Length == 0) normalizedRight = "0";
            var length = normalizedLeft.Length.CompareTo(normalizedRight.Length);
            return length != 0
                ? length
                : string.Compare(normalizedLeft, normalizedRight, StringComparison.Ordinal);
        }
    }
}
