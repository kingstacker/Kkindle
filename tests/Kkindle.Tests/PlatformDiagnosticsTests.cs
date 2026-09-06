using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle.Tests;

public sealed class PlatformDiagnosticsTests
{
    [Fact]
    public async Task CollectsLocalCapabilitiesWithoutNetworkAccess()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(Path.Combine(root, "app"));
            var diagnostics = await new PlatformDiagnosticsService(paths)
                .CollectAsync(null, kindleAvailable: false);

            Assert.Contains(diagnostics, item => item.Name == "数据目录" && item.Status == PlatformDiagnosticStatus.Ready);
            Assert.Contains(diagnostics, item => item.Name == "回收站目录" && item.Status == PlatformDiagnosticStatus.Ready);
            Assert.Contains(diagnostics, item => item.Name == "PDF 文本解析" && item.Status == PlatformDiagnosticStatus.Ready);
            Assert.Contains(diagnostics, item => item.Name == "Kindle" && item.Status == PlatformDiagnosticStatus.Warning);
            Assert.Empty(Directory.EnumerateFiles(paths.Data, ".kkindle-diagnostic-*", SearchOption.AllDirectories));
        }
        finally { TestHelpers.TryDelete(root); }
    }
}
