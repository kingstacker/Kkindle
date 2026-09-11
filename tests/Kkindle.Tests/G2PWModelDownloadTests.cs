using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Xunit;

namespace Kkindle.Tests;

public sealed class G2PWModelDownloadTests
{
    [Fact]
    public void PinyinEngineSettingsNormalizeToKnownIds()
    {
        Assert.Equal(PinyinBookEngineCatalog.DotNetG2PId, AppSettings.Normalize(new AppSettings()).PinyinEngineId);
        Assert.Equal(PinyinBookEngineCatalog.G2PWId, AppSettings.Normalize(new AppSettings
        {
            PinyinEngineId = " G2PW "
        }).PinyinEngineId);
        Assert.Equal(PinyinBookEngineKind.G2PW, PinyinBookEngineCatalog.Parse("g2pw"));
        Assert.Equal(PinyinBookEngineKind.DotNetG2P, PinyinBookEngineCatalog.Parse("unknown"));
    }

    [Fact]
    public async Task ModelDownloadInstallsArchiveAndSupportingFilesAtomically()
    {
        var root = Path.Combine(Path.GetTempPath(), "kkindle-g2pw-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var archive = CreateArchive();
            var vocabulary = Encoding.UTF8.GetBytes("[PAD]\n[UNK]\n[CLS]\n[SEP]\n你\n");
            var mapping = Encoding.UTF8.GetBytes("{\"ㄋㄧ\":\"ni\"}");
            using var client = new HttpClient(new StaticHandler(new Dictionary<string, byte[]>
            {
                ["https://storage.googleapis.com/g2pw.zip"] = archive,
                ["https://huggingface.co/vocab.txt"] = vocabulary,
                ["https://raw.githubusercontent.com/map.json"] = mapping
            }));
            var source = new G2PWModelDownloadSource(
                new Uri("https://storage.googleapis.com/g2pw.zip"),
                new Uri("https://huggingface.co/vocab.txt"),
                new Uri("https://raw.githubusercontent.com/map.json"),
                archive.LongLength);
            var paths = new AppPaths(root);
            using var service = new G2PWModelDownloadService(paths, client, source);

            var installedPath = await service.DownloadAsync();

            Assert.True(service.IsInstalled());
            Assert.Equal(Path.Combine(paths.PinyinModels, G2PWModelPackage.DirectoryName), installedPath);
            foreach (var file in G2PWModelPackage.RequiredFiles)
                Assert.True(new FileInfo(Path.Combine(installedPath, file)).Length > 0, file);
            Assert.Empty(Directory.EnumerateDirectories(paths.PinyinModels, "g2pw-v2.partial-*"));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static byte[] CreateArchive()
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddEntry(archive, "G2PWModel/config.py", "window_size = 32");
            AddEntry(archive, "G2PWModel/MONOPHONIC_CHARS.txt", "你\tㄋㄧ3");
            AddEntry(archive, "G2PWModel/POLYPHONIC_CHARS.txt", "行\tㄒㄧㄥ2\n行\tㄏㄤ2");
            AddEntry(archive, "G2PWModel/version", "v2");
            var model = archive.CreateEntry("G2PWModel/g2pw.onnx");
            using var modelStream = model.Open();
            modelStream.Write(new byte[1_048_577]);
        }
        return output.ToArray();
    }

    private static void AddEntry(ZipArchive archive, string name, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open(), Encoding.UTF8);
        writer.Write(content);
    }

    private sealed class StaticHandler(IReadOnlyDictionary<string, byte[]> files) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (!files.TryGetValue(request.RequestUri?.ToString() ?? string.Empty, out var body))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    RequestMessage = request
                });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new ByteArrayContent(body)
            });
        }
    }
}
