#if DEBUG
using System.IO.Compression;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Kkindle.Core;

namespace Kkindle;

public partial class MainWindow
{
    // DEBUG-only diagnostics: opens the validation EPUB in paged mode and
    // drives a real page turn for every page-turn animation, photographing the
    // reader surface (snapshot overlay included) mid-transition so both the
    // turn pipeline and the visual overlays are covered by the same probe.
    private async Task RunKreaderAnimationProbeAndExitAsync()
    {
        var logPath = Environment.GetEnvironmentVariable("KKINDLE_ANIMATION_PROBE_LOG");
        if (string.IsNullOrWhiteSpace(logPath))
            logPath = Path.Combine(_paths.Logs, "kreader-animation-probe.log");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

        var exitCode = 0;
        await using var log = new StreamWriter(logPath, append: false, new UTF8Encoding(false));
        try
        {
            await log.WriteLineAsync($"Kreader animation probe started: {DateTimeOffset.Now:O}");

            var assetRoot = Path.Combine(Path.GetTempPath(), "KkindleAnimationProbe", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(assetRoot);
            var epubPath = Path.Combine(assetRoot, "animation-probe.epub");
            CreateKreaderValidationEpub(epubPath);

            var importResult = await ViewModel.ImportAsync([epubPath], cancellationToken: _lifetimeCancellation.Token);
            if (importResult.FailureCount != 0)
                throw new InvalidOperationException("import failed");
            var epubCard = ViewModel.Books.First(card => card.Title.Contains("Linux Kreader Validation", StringComparison.OrdinalIgnoreCase));
            var epubFile = epubCard.Book.Files.First(file => file.Format.Equals("epub", StringComparison.OrdinalIgnoreCase));
            await OpenBookAsync(epubCard, epubFile);
            if (CurrentReaderHost is not NativeReaderHost nativeHost)
                throw new InvalidOperationException("reader host is not the native surface");
            await nativeHost.ReadyTask;
            await SetKreaderValidationLayoutAsync(flowMode: 1, twoPage: false);
            await WaitForKreaderNativePageAsync(nativeHost);
            await log.WriteLineAsync("PASS native reader ready");

            foreach (var (name, animation) in new[]
                     {
                         ("fade", ReaderAnimationFade),
                         ("slide", ReaderAnimationSlide),
                         ("wave", ReaderAnimationWave)
                     })
            {
                _readerPageAnimation = animation;
                SyncReaderAnimationMenu();
                await Task.Delay(150);
                await log.WriteLineAsync($"--- {name} ---");

                // Drive the app's real turn pipeline without awaiting it, then
                // photograph the mid-transition frames as they render.
                var turn = TurnReaderPageCoreAsync(1, chapterOnly: false);
                for (var sample = 0; sample < 14; sample++)
                {
                    await Task.Delay(50);
                    await SaveAnimationProbeFrameAsync(name, sample);
                }
                await turn;
                await log.WriteLineAsync($"PASS {name} turn completed");
                await Task.Delay(300);
            }

            await log.WriteLineAsync("probe completed");
        }
        catch (Exception exception)
        {
            exitCode = 2;
            await log.WriteLineAsync("FAIL " + exception);
        }
        finally
        {
            await log.FlushAsync();
            Environment.Exit(exitCode);
        }
    }

    private static async Task WaitForKreaderNativePageAsync(
        NativeReaderHost host)
    {
        for (var attempt = 0; attempt < 80; attempt++)
        {
            if (host.CaptureVisiblePageAsync(CancellationToken.None).Result is { Length: > 0 })
                return;
            await Task.Delay(100);
        }
        throw new InvalidOperationException("native reader page did not render.");
    }

    private async Task SaveAnimationProbeFrameAsync(string name, int sample)
    {
        try
        {
            var visual = ReaderWebViewHost as Visual ?? (Visual)this;
            var bounds = visual.Bounds;
            var scaling = TopLevel.GetTopLevel(visual)?.RenderScaling ?? 1;
            var pixelWidth = (int)Math.Ceiling(bounds.Width * scaling);
            var pixelHeight = (int)Math.Ceiling(bounds.Height * scaling);
            if (pixelWidth < 16 || pixelHeight < 16) return;

            using var bitmap = new RenderTargetBitmap(
                new PixelSize(pixelWidth, pixelHeight),
                new Vector(96 * scaling, 96 * scaling));
            bitmap.Render(visual);
            using var stream = new MemoryStream();
            bitmap.Save(stream);
            var logPath = Environment.GetEnvironmentVariable("KKINDLE_ANIMATION_PROBE_LOG");
            var directory = string.IsNullOrWhiteSpace(logPath)
                ? _paths.Logs
                : Path.GetDirectoryName(logPath)!;
            await File.WriteAllBytesAsync(
                Path.Combine(directory, $"kreader-animation-{name}-{sample}.png"),
                stream.ToArray());
        }
        catch
        {
            // Diagnostics must never affect reading or navigation.
        }
    }
}
#endif
