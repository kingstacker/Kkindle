using System.IO.Compression;
using Kkindle.Infrastructure;

namespace Kkindle.Tests;

public sealed class SoftwareLogExportTests
{
    [Fact]
    public async Task ExportsRuntimeLogsAndDiagnosticsWithoutSettings()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(root);
            paths.EnsureDirectories();
            await File.WriteAllTextAsync(
                Path.Combine(paths.Logs, "send-diagnostic.log"),
                "SMTP connection failed.");
            var readerLogDirectory = Path.Combine(paths.Logs, "reader");
            Directory.CreateDirectory(readerLogDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(readerLogDirectory, "reader-timing.log"),
                "reader timing");
            await File.WriteAllTextAsync(
                Path.Combine(paths.Root, "kkindle-crash.log"),
                "crash details");
            await File.WriteAllTextAsync(paths.Settings, "{\"password\":\"email-secret\"}");

            var destination = Path.Combine(root, "support", "Kkindle-logs.zip");
            await new SoftwareLogExportService().ExportAsync(
                paths,
                destination,
                new Dictionary<string, string>
                {
                    ["Application version"] = "debug-test",
                    ["UI language"] = "zh-CN"
                });

            using var archive = ZipFile.OpenRead(destination);
            var names = archive.Entries.Select(entry => entry.FullName).ToArray();
            Assert.Contains("logs/send-diagnostic.log", names);
            Assert.Contains("logs/reader/reader-timing.log", names);
            Assert.Contains("logs/kkindle-crash.log", names);
            Assert.Contains("diagnostics.txt", names);
            Assert.DoesNotContain(names, name => name.Contains("app-settings", StringComparison.OrdinalIgnoreCase));

            var diagnostics = await ReadEntryAsync(archive, "diagnostics.txt");
            Assert.Contains("debug-test", diagnostics, StringComparison.Ordinal);
            Assert.Contains("email/SMTP passwords", diagnostics, StringComparison.Ordinal);
            Assert.DoesNotContain("email-secret", diagnostics, StringComparison.Ordinal);
        }
        finally
        {
            TestHelpers.TryDelete(root);
        }
    }

    [Fact]
    public async Task CreatesDiagnosticsArchiveWhenNoRuntimeLogExists()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(root);
            paths.EnsureDirectories();
            var destination = Path.Combine(root, "Kkindle-logs.zip");

            await new SoftwareLogExportService().ExportAsync(paths, destination);

            using var archive = ZipFile.OpenRead(destination);
            Assert.Single(archive.Entries);
            Assert.Equal("diagnostics.txt", archive.Entries[0].FullName);
            var diagnostics = await ReadEntryAsync(archive, "diagnostics.txt");
            Assert.Contains("(none / 无)", diagnostics, StringComparison.Ordinal);
        }
        finally
        {
            TestHelpers.TryDelete(root);
        }
    }

    private static async Task<string> ReadEntryAsync(ZipArchive archive, string name)
    {
        await using var stream = archive.GetEntry(name)!.Open();
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }
}
