using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using HtmlAgilityPack;
using Kkindle.Core;
using MdxParser;

namespace Kkindle.Infrastructure;

public sealed class DictionaryService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly HashSet<string> BlockElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "address", "article", "blockquote", "dd", "div", "dl", "dt", "figcaption", "footer",
        "h1", "h2", "h3", "h4", "h5", "h6", "header", "li", "main", "p", "section", "table", "tr"
    };
    private readonly AppPaths _paths;
    private readonly IBookFormatConverter? _formatConverter;

    static DictionaryService() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public DictionaryService(AppPaths paths, IBookFormatConverter? formatConverter = null)
    {
        _paths = paths;
        _formatConverter = formatConverter;
    }

    private string ManifestPath => Path.Combine(_paths.Dictionaries, "manifest.json");

    public async Task<IReadOnlyList<DictionaryDefinition>> ListAsync(CancellationToken cancellationToken = default)
    {
        _paths.EnsureDirectories();
        if (!File.Exists(ManifestPath)) return [];
        try
        {
            await using var stream = File.OpenRead(ManifestPath);
            return await JsonSerializer.DeserializeAsync<List<DictionaryDefinition>>(stream, JsonOptions, cancellationToken) ?? [];
        }
        catch (JsonException) { return []; }
    }

    public async Task<DictionaryDefinition> ImportAsync(string sourcePath, string? name = null, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("词典文件不存在。", sourcePath);
        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        var entries = extension switch
        {
            ".mdx" => await ParseMdxAsync(sourcePath, cancellationToken),
            ".azw" or ".azw3" or ".mobi" or ".prc" or ".kfx" => await ParseKindleDictionaryAsync(sourcePath, cancellationToken),
            ".txt" or ".tsv" or ".csv" => await ParseAsync(sourcePath, cancellationToken),
            _ => throw new NotSupportedException("支持 MDX、AZW、AZW3、MOBI、PRC、KFX、TXT、TSV 和 CSV 词典。")
        };
        if (entries.Count == 0)
            throw new InvalidDataException(extension is ".azw" or ".azw3" or ".mobi" or ".prc" or ".kfx"
                ? "转换后的 Kindle 词典中没有识别到词条。请确认文件是未受 DRM 保护的 Kindle 词典。"
                : "词典中没有识别到可用词条。");
        _paths.EnsureDirectories();
        var id = Guid.NewGuid().ToString("N");
        var relativePath = $"{id}.json";
        await using (var stream = File.Create(Path.Combine(_paths.Dictionaries, relativePath)))
            await JsonSerializer.SerializeAsync(stream, entries, JsonOptions, cancellationToken);
        var manifest = (await ListAsync(cancellationToken)).ToList();
        var definition = new DictionaryDefinition(id, string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(sourcePath) : name.Trim(), relativePath, entries.Count, DateTimeOffset.UtcNow);
        manifest.Add(definition);
        await SaveManifestAsync(manifest, cancellationToken);
        return definition;
    }

    public async Task RemoveAsync(string id, CancellationToken cancellationToken = default)
    {
        var manifest = (await ListAsync(cancellationToken)).ToList();
        var item = manifest.FirstOrDefault(entry => entry.Id == id);
        if (item is null) return;
        var path = ResolveSafePath(item.RelativePath);
        if (File.Exists(path)) File.Delete(path);
        manifest.Remove(item);
        await SaveManifestAsync(manifest, cancellationToken);
    }

    public async Task<IReadOnlyList<DictionaryEntry>> LookupAsync(string term, CancellationToken cancellationToken = default)
    {
        var normalized = term.Trim();
        if (normalized.Length == 0) return [];
        var result = new List<DictionaryEntry>();
        foreach (var dictionary in (await ListAsync(cancellationToken)).Where(item => item.Enabled))
        {
            var path = ResolveSafePath(dictionary.RelativePath);
            if (!File.Exists(path)) continue;
            await using var stream = File.OpenRead(path);
            var entries = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(stream, JsonOptions, cancellationToken);
            if (entries is null) continue;
            var match = entries.FirstOrDefault(pair => pair.Key.Equals(normalized, StringComparison.CurrentCultureIgnoreCase));
            if (!string.IsNullOrEmpty(match.Key)) result.Add(new DictionaryEntry(match.Key, match.Value, dictionary.Name));
            if (result.Count >= 8) break;
        }
        return result;
    }

    internal static async Task<Dictionary<string, string>> ParseAsync(string path, CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, string>(StringComparer.CurrentCultureIgnoreCase);
        using var reader = new StreamReader(path, Encoding.UTF8, true);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#')) continue;
            var separator = line.IndexOf('\t');
            if (separator <= 0) separator = line.IndexOf('=');
            if (separator <= 0) continue;
            var term = line[..separator].Trim();
            var definition = line[(separator + 1)..].Trim();
            if (term.Length > 0 && definition.Length > 0) result[term] = definition;
        }
        return result;
    }

    internal static async Task<Dictionary<string, string>> ParseMdxAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = new Dictionary<string, string>(StringComparer.CurrentCultureIgnoreCase);
                using var stream = File.OpenRead(path);
                var document = new MdxDocument(stream, Encoding.Unicode);
                foreach (var record in document.RecordDatas)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!record.IsBinary)
                        AddEntry(result, record.Key, ToReadableText(record.Text));
                }
                return result;
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new InvalidDataException("无法解析 MDX 词典，请确认文件完整且未加密。", exception);
        }
    }

    private async Task<Dictionary<string, string>> ParseKindleDictionaryAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (_formatConverter is null)
            throw new InvalidOperationException("导入 Kindle 词典需要 Calibre。请先在设置中配置 ebook-convert 路径。");

        var workDirectory = Path.Combine(
            Path.GetTempPath(),
            "Kkindle",
            "dictionary-import",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDirectory);
        var epubPath = Path.Combine(workDirectory, "dictionary.epub");
        try
        {
            await _formatConverter.ConvertAsync(path, epubPath, cancellationToken: cancellationToken);
            return await ParseKindleEpubAsync(epubPath, cancellationToken);
        }
        finally
        {
            TryDeleteDirectory(workDirectory);
        }
    }

    internal static async Task<Dictionary<string, string>> ParseKindleEpubAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, string>(StringComparer.CurrentCultureIgnoreCase);
        using var archive = ZipFile.OpenRead(path);
        var htmlEntries = archive.Entries
            .Where(entry => Path.GetExtension(entry.FullName).ToLowerInvariant() is ".html" or ".htm" or ".xhtml")
            .OrderBy(entry => entry.FullName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (htmlEntries.Length > 10000)
            throw new InvalidDataException("转换后的 Kindle 词典包含过多内容文件。");

        foreach (var entry in htmlEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Length > 32L * 1024 * 1024) continue;
            await using var stream = entry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var html = await reader.ReadToEndAsync(cancellationToken);
            ExtractKindleEntries(html, result);
        }
        return result;
    }

    internal static void ExtractKindleEntries(string html, IDictionary<string, string> result)
    {
        var document = new HtmlDocument();
        document.LoadHtml(html);

        foreach (var entry in document.DocumentNode.Descendants()
                     .Where(node => LocalName(node.Name).Equals("entry", StringComparison.OrdinalIgnoreCase)))
        {
            var orth = entry.Descendants()
                .FirstOrDefault(node => LocalName(node.Name).Equals("orth", StringComparison.OrdinalIgnoreCase));
            var term = orth?.GetAttributeValue("value", string.Empty);
            if (string.IsNullOrWhiteSpace(term)) term = orth?.InnerText;
            AddEntry(result, term, RemoveTermPrefix(ToReadableText(entry.InnerHtml), term));
        }

        foreach (var termNode in document.DocumentNode.Descendants("dt"))
        {
            var definitionNode = termNode.SelectSingleNode("following-sibling::dd[1]");
            if (definitionNode is not null)
                AddEntry(result, ToReadableText(termNode.InnerHtml), ToReadableText(definitionNode.InnerHtml));
        }

        foreach (var node in document.DocumentNode.Descendants())
        {
            var term = node.GetAttributeValue("data-headword", string.Empty);
            if (string.IsNullOrWhiteSpace(term)) term = node.GetAttributeValue("data-term", string.Empty);
            if (string.IsNullOrWhiteSpace(term)) term = node.GetAttributeValue("data-word", string.Empty);
            if (!string.IsNullOrWhiteSpace(term))
                AddEntry(result, term, RemoveTermPrefix(ToReadableText(node.InnerHtml), term));
        }
    }


    private static void AddEntry(IDictionary<string, string> entries, string? term, string? definition)
    {
        term = WebUtility.HtmlDecode(term ?? string.Empty).Trim().Trim('\0');
        definition = definition?.Trim();
        if (term.Length == 0 || string.IsNullOrWhiteSpace(definition)) return;
        if (entries.TryGetValue(term, out var existing)
            && !existing.Contains(definition, StringComparison.CurrentCultureIgnoreCase))
            entries[term] = existing + Environment.NewLine + Environment.NewLine + definition;
        else
            entries[term] = definition;
    }

    private static string RemoveTermPrefix(string definition, string? term)
    {
        term = WebUtility.HtmlDecode(term ?? string.Empty).Trim();
        if (term.Length == 0 || definition.Length <= term.Length
            || !definition.StartsWith(term, StringComparison.CurrentCultureIgnoreCase))
            return definition;
        var boundary = definition[term.Length];
        if (!char.IsWhiteSpace(boundary) && boundary is not ':' and not '：' and not '-' and not '—')
            return definition;
        return definition[term.Length..].TrimStart(' ', '\t', '\r', '\n', ':', '：', '-', '—');
    }

    private static string ToReadableText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;
        var document = new HtmlDocument();
        document.LoadHtml(html);
        var builder = new StringBuilder();
        AppendReadableText(document.DocumentNode, builder);
        var decoded = WebUtility.HtmlDecode(builder.ToString()).Replace('\u00A0', ' ');
        var lines = decoded.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Select(line => string.Join(' ', line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)))
            .Where(line => line.Length > 0);
        return string.Join(Environment.NewLine, lines);
    }

    private static void AppendReadableText(HtmlNode node, StringBuilder builder)
    {
        var name = LocalName(node.Name);
        if (name.Equals("script", StringComparison.OrdinalIgnoreCase)
            || name.Equals("style", StringComparison.OrdinalIgnoreCase))
            return;
        if (name.Equals("br", StringComparison.OrdinalIgnoreCase))
        {
            builder.AppendLine();
            return;
        }

        var isBlock = BlockElements.Contains(name);
        if (isBlock && builder.Length > 0) builder.AppendLine();
        if (node.NodeType == HtmlNodeType.Text) builder.Append(node.InnerText);
        else
            foreach (var child in node.ChildNodes) AppendReadableText(child, builder);
        if (isBlock) builder.AppendLine();
    }

    private static string LocalName(string name)
    {
        var separator = name.LastIndexOf(':');
        return separator >= 0 ? name[(separator + 1)..] : name;
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private string ResolveSafePath(string relativePath)
    {
        var root = Path.GetFullPath(_paths.Dictionaries) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(_paths.Dictionaries, relativePath));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("词典路径无效。");
        return path;
    }

    private async Task SaveManifestAsync(IReadOnlyList<DictionaryDefinition> items, CancellationToken cancellationToken)
    {
        var temporary = ManifestPath + ".tmp";
        await using (var stream = File.Create(temporary))
            await JsonSerializer.SerializeAsync(stream, items, JsonOptions, cancellationToken);
        File.Move(temporary, ManifestPath, true);
    }
}
