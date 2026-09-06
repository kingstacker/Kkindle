using System.Runtime.InteropServices;
using Kkindle.Core;

namespace Kkindle.Infrastructure;

/// <summary>
/// Collects fast, local capability checks used by the settings diagnostics
/// page. Checks are deliberately non-invasive: no downloads, device writes or
/// network requests are performed here.
/// </summary>
public sealed class PlatformDiagnosticsService
{
    private readonly AppPaths _paths;

    public PlatformDiagnosticsService(AppPaths paths)
    {
        _paths = paths;
    }

    public Task<IReadOnlyList<PlatformDiagnostic>> CollectAsync(
        string? calibrePath,
        bool kindleAvailable,
        CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<PlatformDiagnostic>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var diagnostics = new List<PlatformDiagnostic>
            {
                new(
                    "操作系统",
                    PlatformDiagnosticStatus.Ready,
                    $"{RuntimeInformation.OSDescription.Trim()} · {RuntimeInformation.ProcessArchitecture}"),
                new(
                    "运行时",
                    PlatformDiagnosticStatus.Ready,
                    $".NET {Environment.Version} · {RuntimeInformation.FrameworkDescription}")
            };

            diagnostics.Add(CheckWritable("数据目录", _paths.Data));
            diagnostics.Add(CheckWritable("书库目录", _paths.Library));
            diagnostics.Add(CheckWritable("回收站目录", _paths.Trash));
            diagnostics.Add(CheckWebView());
            diagnostics.Add(CheckPdfParser());
            diagnostics.Add(CheckCalibre(calibrePath));
            diagnostics.Add(kindleAvailable
                ? new PlatformDiagnostic("Kindle", PlatformDiagnosticStatus.Ready, "已加载当前平台 Kindle 设备服务。")
                : new PlatformDiagnostic("Kindle", PlatformDiagnosticStatus.Warning, "当前平台没有可用的 Kindle 设备服务；书库和阅读功能仍可使用。"));
            return diagnostics;
        }, cancellationToken);

    private static PlatformDiagnostic CheckWritable(string name, string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            var marker = Path.Combine(path, $".kkindle-diagnostic-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(marker, "ok");
            File.Delete(marker);
            return new PlatformDiagnostic(name, PlatformDiagnosticStatus.Ready, "目录可读写。 ");
        }
        catch (Exception exception)
        {
            return new PlatformDiagnostic(
                name,
                PlatformDiagnosticStatus.Unavailable,
                $"目录不可写：{exception.Message}");
        }
    }

    private static PlatformDiagnostic CheckWebView()
    {
        if (OperatingSystem.IsWindows())
            return new PlatformDiagnostic("阅读器 WebView", PlatformDiagnosticStatus.Ready, "Windows 使用系统 WebView2；打开阅读器可验证运行环境。 ");
        if (OperatingSystem.IsMacOS())
            return new PlatformDiagnostic("阅读器 WebView", PlatformDiagnosticStatus.Ready, "macOS 使用系统 WKWebView；打开阅读器可验证运行环境。 ");
        if (!OperatingSystem.IsLinux())
            return new PlatformDiagnostic("阅读器 WebView", PlatformDiagnosticStatus.Warning, "当前操作系统未列入已验证平台。 ");

        foreach (var library in new[]
                 {
                     "libWPEWebKit-2.0.so.1",
                     "libwebkit2gtk-4.1.so.0",
                     "libwebkit2gtk-4.0.so.0"
                 })
        {
            if (!NativeLibrary.TryLoad(library, out var handle)) continue;
            NativeLibrary.Free(handle);
            return new PlatformDiagnostic("阅读器 WebView", PlatformDiagnosticStatus.Ready, $"检测到 {library}。 ");
        }

        return new PlatformDiagnostic(
            "阅读器 WebView",
            PlatformDiagnosticStatus.Unavailable,
            "未检测到 WebKitGTK/WPE 动态库；Linux 阅读器可能无法显示。 ");
    }

    private static PlatformDiagnostic CheckPdfParser()
    {
        var version = typeof(UglyToad.PdfPig.PdfDocument).Assembly.GetName().Version?.ToString() ?? "未知版本";
        return new PlatformDiagnostic("PDF 文本解析", PlatformDiagnosticStatus.Ready, $"PdfPig {version} 已包含在应用中。 ");
    }

    private static PlatformDiagnostic CheckCalibre(string? configuredPath)
    {
        try
        {
            using var setup = new CalibreSetupService();
            var path = setup.LocateCalibre(configuredPath);
            return string.IsNullOrWhiteSpace(path)
                ? new PlatformDiagnostic("Calibre", PlatformDiagnosticStatus.Warning, "未检测到 Calibre；格式转换和 KFX 插件功能不可用。 ")
                : new PlatformDiagnostic("Calibre", PlatformDiagnosticStatus.Ready, path);
        }
        catch (Exception exception)
        {
            return new PlatformDiagnostic("Calibre", PlatformDiagnosticStatus.Warning, $"检测失败：{exception.Message}");
        }
    }
}
