#if DEBUG
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Kkindle.Core;

namespace Kkindle;

public partial class MainWindow
{
    // DEBUG-only diagnostics: opens the validation EPUB in paged mode, clicks
    // the right side of the document for every page-turn animation and samples
    // the DOM so both the injected pointer bridge and the visual pipeline are
    // covered by the same probe.
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
            var host = CurrentReaderHost ?? throw new InvalidOperationException("reader host missing");
            await WaitForKreaderDocumentAsync(host);
            await SetKreaderValidationLayoutAsync(flowMode: 1, twoPage: false);

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

                // Return to a known page first.
                await host.InvokeScriptAsync(
                    "(() => { (document.scrollingElement||document.documentElement).scrollLeft = 0; return true; })();");
                await Task.Delay(120);

                if (!await TriggerKreaderPointerPageTurnAsync(host))
                    throw new InvalidOperationException($"{name} pointer click could not be dispatched");
                var observations = new List<string>();
                for (var sample = 0; sample < 24; sample++)
                {
                    await Task.Delay(40);
                    observations.Add(await ReadAnimationProbeSampleAsync(host));
                    if (sample is 2 or 8 or 14)
                        await SaveAnimationProbeFrameAsync(host, name, sample);
                }

                foreach (var line in observations.Where(o => o.Contains('|')))
                    await log.WriteLineAsync(line);
                var summary = SummarizeProbeObservations(name, observations);
                await log.WriteLineAsync("SUMMARY " + summary);

                await Task.Delay(400);
                var final = await ReadAnimationProbeSampleAsync(host);
                await log.WriteLineAsync("FINAL " + final);
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

    private static async Task<bool> TriggerKreaderPointerPageTurnAsync(IReaderHost host)
    {
        var result = await host.InvokeScriptAsync("""
            (() => {
              const width = window.innerWidth || document.documentElement.clientWidth || 0;
              const height = window.innerHeight || document.documentElement.clientHeight || 0;
              if (width <= 0 || height <= 0 || typeof PointerEvent !== 'function') return false;
              const x = Math.floor(width * .9);
              const y = Math.floor(height * .5);
              const interactive = 'a, button, input, textarea, select, option, label, #kkindle-selection-bar';
              const target = (document.elementsFromPoint?.(x, y) || [])
                .find(element => element instanceof Element && !element.closest(interactive))
                || document.body;
              if (!(target instanceof Element)) return false;
              const options = {
                bubbles: true,
                cancelable: true,
                composed: true,
                pointerId: 177,
                pointerType: 'mouse',
                isPrimary: true,
                button: 0,
                clientX: x,
                clientY: y
              };
              target.dispatchEvent(new PointerEvent('pointerdown', { ...options, buttons: 1 }));
              target.dispatchEvent(new PointerEvent('pointerup', { ...options, buttons: 0 }));
              return true;
            })();
            """);
        return string.Equals(result?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
    }

    private async Task SaveAnimationProbeFrameAsync(IReaderHost host, string name, int sample)
    {
        if (host is not IReaderPageSnapshotProvider provider) return;
        try
        {
            var png = await provider.CaptureVisiblePageAsync(CancellationToken.None);
            if (png is { Length: > 0 })
            {
                var logPath = Environment.GetEnvironmentVariable("KKINDLE_ANIMATION_PROBE_LOG");
                var directory = string.IsNullOrWhiteSpace(logPath)
                    ? _paths.Logs
                    : Path.GetDirectoryName(logPath)!;
                await File.WriteAllBytesAsync(
                    Path.Combine(directory, $"kreader-animation-{name}-{sample}.png"),
                    png);
            }
        }
        catch
        {
            // Diagnostics must never affect reading or navigation.
        }
    }

    private async Task<string> ReadAnimationProbeSampleAsync(IReaderHost host)
    {
        try
        {
            var result = await host.InvokeScriptAsync("""
                (() => {
                  const el = document.scrollingElement || document.documentElement;
                  const body = document.body || document.documentElement;
                  let vtActive = null;
                  try {
                    const rootPseudo = document.documentElement;
                    vtActive = !!window.__kkindleViewTransition;
                  } catch (_) {}
                  return JSON.stringify({
                    op: body.style ? (body.style.opacity || '') : '',
                    cop: (() => { try { return getComputedStyle(body).opacity; } catch (_) { return ''; } })(),
                    wave: !!document.getElementById('kk-wave'),
                    waveImg: (() => {
                      const c = document.getElementById('kk-wave-image');
                      return c ? (c.dataset.kkReady || '?') : '';
                    })(),
                    slide: !!document.getElementById('kk-slide'),
                    slideImg: (() => {
                      const c = document.getElementById('kk-slide-image');
                      return c ? (c.dataset.kkReady || '?') : '';
                    })(),
                    vtStyle: !!document.getElementById('kk-view-transition-style'),
                    vt: vtActive,
                    vtReady: window.__kkindleViewTransitionReady === true,
                    sl: Math.round(el?.scrollLeft || 0),
                    vtSupported: typeof document.startViewTransition === 'function'
                  });
                })();
                """);
            var raw = DecodeReaderScriptString(result) ?? result;
            if (string.IsNullOrWhiteSpace(raw))
                return "sample-failed empty script result";
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            return $"{root.GetProperty("cop").GetString()}|wave={root.GetProperty("wave").GetBoolean()}/{root.GetProperty("waveImg").GetString()}"
                   + $"|slide={root.GetProperty("slide").GetBoolean()}/{root.GetProperty("slideImg").GetString()}"
                   + $"|vt={root.GetProperty("vt").GetBoolean()}|vtReady={root.GetProperty("vtReady").GetBoolean()}"
                   + $"|vtStyle={root.GetProperty("vtStyle").GetBoolean()}"
                   + $"|sl={root.GetProperty("sl").GetInt32()}"
                   + $"|supportsVT={root.GetProperty("vtSupported").GetBoolean()}";
        }
        catch (Exception exception)
        {
            return "sample-failed " + exception.Message;
        }
    }

    private static string SummarizeProbeObservations(string name, List<string> observations)
    {
        var parsed = observations
            .Where(o => o.StartsWith("1|") || o.StartsWith("0.") || char.IsDigit(o.FirstOrDefault()))
            .ToList();
        var anyFade = parsed.Any(o =>
        {
            var head = o.Split('|')[0];
            return double.TryParse(head, System.Globalization.CultureInfo.InvariantCulture, out var value) && value < 0.95;
        });
        var anyWave = parsed.Any(o => o.Contains("|wave=true", StringComparison.OrdinalIgnoreCase));
        var anySlide = parsed.Any(o => o.Contains("|slide=true", StringComparison.OrdinalIgnoreCase));
        var anyVt = parsed.Any(o => o.Contains("|vt=true", StringComparison.OrdinalIgnoreCase));
        var advanced = parsed.Any(o =>
        {
            var index = o.IndexOf("|sl=", StringComparison.Ordinal);
            if (index < 0) return false;
            return int.TryParse(o[(index + 4)..].Split('|')[0], out var value) && value > 0;
        });
        return $"fadeSeen={anyFade} waveOverlay={anyWave} slideOverlay={anySlide} viewTransition={anyVt} pageAdvanced={advanced}";
    }
}
#endif
