using System.IO.Compression;
using Kkindle.Core;

namespace Kkindle.Tests;

internal static class TestHelpers
{
    internal static IDeviceFeatureAccess ActivatedFeatures { get; } = new Kkindle.TestFixtures.ActivatedDeviceFeatures();
    internal static Kkindle.Infrastructure.AiChatClient CreateActivatedAiClient(HttpMessageHandler? handler = null) => new(handler);
    /// <summary>
    /// Stands in for the OS keystore so settings tests exercise the
    /// persistence round trip without binding to a machine account. The real
    /// encryption belongs to the platform implementation and is verified
    /// there, not here.
    /// </summary>
    internal sealed class PlaintextSecretProtector : ISecretProtector
    {
        public byte[] Protect(byte[] value) => (byte[])value.Clone();

        public byte[] Unprotect(byte[] value) => (byte[])value.Clone();
    }

    public static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "KkindleTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    public static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    public static void AddZipEntry(ZipArchive archive, string name, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open());
        writer.Write(content);
    }

    internal sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }

    internal sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }
}
