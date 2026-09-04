using System.Net;
using System.Security.Cryptography;
using Kkindle.Infrastructure;

namespace Kkindle.Tests;

public sealed class EmbeddingModelDownloadTests
{
    [Fact]
    public async Task DownloadsThePackageAtomicallyAndReportsProgress()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var responses = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["model.onnx"] = [1, 2, 3, 4, 5],
                ["vocab.txt"] = [(byte)'v', (byte)'o', (byte)'c', (byte)'a', (byte)'b'],
                ["tokenizer_config.json"] = [(byte)'{', (byte)'}']
            };
            var package = CreatePackage(responses);
            var progress = new RecordingProgress();
            using var client = new HttpClient(new StaticResponseHandler(responses));
            using var service = new EmbeddingModelDownloadService(
                new AppPaths(Path.Combine(root, "app")),
                client);

            var directory = await service.DownloadAsync(
                package,
                force: true,
                progress: progress,
                cancellationToken: CancellationToken.None);

            Assert.True(service.IsInstalled(package));
            Assert.Equal(responses["model.onnx"], await File.ReadAllBytesAsync(
                Path.Combine(directory, "model.onnx")));
            Assert.Empty(Directory.EnumerateFiles(directory, "*.partial"));
            Assert.Contains(progress.Items, item =>
                item.FileName == "model.onnx"
                && item.BytesReceived == responses["model.onnx"].Length
                && item.FilePercentage == 100);
        }
        finally
        {
            TestHelpers.TryDelete(root);
        }
    }

    [Fact]
    public async Task RejectsAnOnnxFileWithTheWrongHashAndCleansPartialFile()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var responses = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["model.onnx"] = [9, 8, 7],
                ["vocab.txt"] = [1],
                ["tokenizer_config.json"] = [2]
            };
            var package = CreatePackage(responses) with
            {
                Files =
                [
                    new EmbeddingModelFile(
                        "model.onnx",
                        new Uri("https://huggingface.co/test/model.onnx"),
                        MaximumBytes: 1024,
                        ExpectedSha256: new string('0', 64)),
                    ..CreatePackage(responses).Files.Skip(1)
                ]
            };
            using var client = new HttpClient(new StaticResponseHandler(responses));
            using var service = new EmbeddingModelDownloadService(
                new AppPaths(Path.Combine(root, "app")),
                client);

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                service.DownloadAsync(package, force: true));

            var directory = service.GetModelDirectory(package);
            Assert.False(File.Exists(Path.Combine(directory, "model.onnx.partial")));
        }
        finally
        {
            TestHelpers.TryDelete(root);
        }
    }

    private static EmbeddingModelPackage CreatePackage(
        IReadOnlyDictionary<string, byte[]> responses) =>
        new(
            "test-embedding",
            "测试模型",
            "test-model",
            3,
            "约 1 KB",
            [
                new EmbeddingModelFile(
                    "model.onnx",
                    new Uri("https://huggingface.co/test/model.onnx"),
                    MaximumBytes: 1024,
                    ExpectedSha256: Convert.ToHexString(
                        SHA256.HashData(responses["model.onnx"]))),
                new EmbeddingModelFile(
                    "vocab.txt",
                    new Uri("https://huggingface.co/test/vocab.txt"),
                    MaximumBytes: 1024),
                new EmbeddingModelFile(
                    "tokenizer_config.json",
                    new Uri("https://huggingface.co/test/tokenizer_config.json"),
                    MaximumBytes: 1024)
            ]);

    private sealed class RecordingProgress : IProgress<EmbeddingModelDownloadProgress>
    {
        public List<EmbeddingModelDownloadProgress> Items { get; } = [];

        public void Report(EmbeddingModelDownloadProgress value) => Items.Add(value);
    }

    private sealed class StaticResponseHandler(
        IReadOnlyDictionary<string, byte[]> responses) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var fileName = Path.GetFileName(request.RequestUri?.AbsolutePath);
            if (fileName is null || !responses.TryGetValue(fileName, out var bytes))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    RequestMessage = request
                });

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new ByteArrayContent(bytes)
            };
            return Task.FromResult(response);
        }
    }
}
