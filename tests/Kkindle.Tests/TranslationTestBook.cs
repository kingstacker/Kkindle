using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle.Tests;

internal sealed class TranslationTestBook : IDisposable
{
    public string Root { get; } = TestHelpers.CreateTempDirectory();
    public string Source { get; }
    public string Output => Path.Combine(Root, "output");
    public AppPaths Paths => new(Path.Combine(Root, "app"));
    public string CachePath => Path.Combine(Output, ".kkindle-translation-cache.jsonl");
    public static BookTranslationSettings Settings => new()
    {
        Provider = BookTranslationProvider.GoogleFree,
        SourceLanguage = "en", TargetLanguage = "zh-CN",
        OutputMode = BookTranslationOutputMode.Translated
    };

    public TranslationTestBook(string body, string? ncxTitle = null, bool hasLanguage = true)
    {
        Source = Create("source.epub", body, ncxTitle, hasLanguage);
    }

    public string Create(string name, string body, string? ncxTitle = null, bool hasLanguage = true)
    {
        var path = Path.Combine(Root, name);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        TestHelpers.AddZipEntry(archive, "mimetype", "application/epub+zip");
        TestHelpers.AddZipEntry(archive, "META-INF/container.xml", """
            <container xmlns="urn:oasis:names:tc:opendocument:xmlns:container"><rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml" /></rootfiles></container>
            """);
        TestHelpers.AddZipEntry(archive, "OEBPS/content.opf", $"""
            <package xmlns="http://www.idpf.org/2007/opf" version="2.0" unique-identifier="book-id">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:identifier id="book-id">regression</dc:identifier><dc:title>Translation regression</dc:title><dc:creator>Test author</dc:creator>{(hasLanguage ? "<dc:language>en</dc:language>" : "")}</metadata>
              <manifest><item id="chapter" href="chapter.xhtml" media-type="application/xhtml+xml" />{(ncxTitle is null ? "" : "<item id=\"ncx\" href=\"toc.ncx\" media-type=\"application/x-dtbncx+xml\" />")}</manifest>
              <spine{(ncxTitle is null ? "" : " toc=\"ncx\"")}><itemref idref="chapter" /></spine>
            </package>
            """);
        TestHelpers.AddZipEntry(archive, "OEBPS/chapter.xhtml", $"""
            <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops" lang="en" xml:lang="en"><head><title>Test</title></head><body>{body}</body></html>
            """);
        if (ncxTitle is not null)
            TestHelpers.AddZipEntry(archive, "OEBPS/toc.ncx", $"""
                <ncx xmlns="http://www.daisy.org/z3986/2005/ncx/" xml:lang="en"><navMap><navPoint id="chapter"><navLabel><text>{new XText(ncxTitle)}</text></navLabel><content src="chapter.xhtml" /></navPoint></navMap></ncx>
                """);
        return path;
    }

    public EpubTranslationService Service(HttpMessageHandler? handler = null, AiChatClient? ai = null) =>
        new(Paths, new TestHelpers.PlaintextSecretProtector(), ai, handler);

    public static XDocument ReadXml(string path, string entry = "OEBPS/chapter.xhtml")
    {
        using var archive = ZipFile.OpenRead(path);
        using var stream = archive.GetEntry(entry)!.Open();
        return XDocument.Load(stream, LoadOptions.PreserveWhitespace);
    }

    public static XElement Body(string path) => ReadXml(path).Descendants().Single(element => element.Name.LocalName == "body");
    public void Dispose() => TestHelpers.TryDelete(Root);

    internal sealed class GoogleHandler(Func<string, string> reply) : HttpMessageHandler
    {
        public List<string> Inputs { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var query = request.RequestUri!.Query;
            var source = Uri.UnescapeDataString(query[(query.IndexOf("q=", StringComparison.Ordinal) + 2)..]);
            Inputs.Add(source);
            var answer = reply(source);
            return Task.FromResult(new HttpResponseMessage
            {
                Content = new StringContent("[[[" + JsonSerializer.Serialize(answer) + "]]]", Encoding.UTF8, "application/json")
            });
        }
    }
}
