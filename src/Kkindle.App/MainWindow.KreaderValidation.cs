#if DEBUG
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.VisualTree;
using Kkindle.Core;

namespace Kkindle;

public partial class MainWindow
{

    private Task ValidateExternalVerticalEpubAsync(StreamWriter log)
    {
        // The WebKit-specific vertical harness died with the WebView
        // typesetting engine; rewrite these probes against native pages.
        return log.WriteLineAsync("SKIPPED vertical harness: native engine pending");
    }

    private Task ValidateCurrentEpubReaderAsync(StreamWriter log)
    {
        return log.WriteLineAsync("SKIPPED reader harness: native engine pending");
    }
    private async Task RunKreaderValidationAndExitAsync()
    {
        var logPath = Environment.GetEnvironmentVariable("KKINDLE_KREADER_VALIDATE_LOG");
        if (string.IsNullOrWhiteSpace(logPath))
            logPath = Path.Combine(_paths.Logs, "linux-kreader-validation.log");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

        var exitCode = 0;
        await using var log = new StreamWriter(logPath, append: false, new UTF8Encoding(false));
        try
        {
            await log.WriteLineAsync($"Kreader Linux validation started: {DateTimeOffset.Now:O}");
            await log.WriteLineAsync($"OS: {Environment.OSVersion}");
            await log.WriteLineAsync($"DISPLAY={Environment.GetEnvironmentVariable("DISPLAY")}; WAYLAND_DISPLAY={Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")}");

            Width = 1180;
            Height = 820;
            if (double.TryParse(
                    Environment.GetEnvironmentVariable("KKINDLE_KREADER_VALIDATE_WIDTH"),
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var requestedWidth))
            {
                Width = Math.Clamp(requestedWidth, 900, 1800);
            }
            if (double.TryParse(
                    Environment.GetEnvironmentVariable("KKINDLE_KREADER_VALIDATE_HEIGHT"),
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var requestedHeight))
            {
                Height = Math.Clamp(requestedHeight, 650, 1200);
            }
            WindowStartupLocation = Avalonia.Controls.WindowStartupLocation.CenterScreen;
            await Task.Delay(500);

            var externalEpubPath = Environment.GetEnvironmentVariable(
                "KKINDLE_KREADER_VALIDATE_EPUB");
            if (!string.IsNullOrWhiteSpace(externalEpubPath))
            {
                externalEpubPath = Path.GetFullPath(externalEpubPath);
                Require(File.Exists(externalEpubPath), "external validation EPUB does not exist");
                var externalImport = await ViewModel.ImportAsync(
                    [externalEpubPath],
                    cancellationToken: _lifetimeCancellation.Token);
                Require(
                    externalImport.FailureCount == 0,
                    $"external import failed: {string.Join("; ", externalImport.Items.Select(item => item.Message))}");
                var expectedTitle = Path.GetFileNameWithoutExtension(externalEpubPath);
                var externalCard = await ResolveImportedValidationBookAsync(
                    externalImport,
                    externalEpubPath,
                    titleHint: expectedTitle);
                var externalFile = await ResolveImportedValidationFile(
                    externalCard,
                    externalEpubPath,
                    log);
                await log.WriteLineAsync("PASS import external EPUB " + externalCard.Title);
                await OpenBookAsync(externalCard, externalFile);
                await ValidateExternalVerticalEpubAsync(log);
                await CloseReaderAsync();
                await log.WriteLineAsync("PASS external EPUB vertical validation completed");
            }
            else
            {
                var assetRoot = Path.Combine(Path.GetTempPath(), "KkindleKreaderValidation", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(assetRoot);
                var epubPath = Path.Combine(assetRoot, "linux-kreader-validation.epub");
                CreateKreaderValidationEpub(epubPath);

                var importResult = await ViewModel.ImportAsync([epubPath], cancellationToken: _lifetimeCancellation.Token);
                Require(importResult.FailureCount == 0, $"import failed: {string.Join("; ", importResult.Items.Select(item => item.Message))}");
                await log.WriteLineAsync("PASS import EPUB");

                // Earlier validation runs may have merged an older fixture with
                // the same metadata into this book card. Opening "the first
                // EPUB of the matching title" would then validate a stale
                // extraction instead of today's bytes, so resolve both the card
                // and the file by the imported content hash.
                var epubCard = await ResolveImportedValidationBookAsync(
                    importResult,
                    epubPath,
                    titleHint: "Linux Kreader Validation Long Numbers");
                var epubFile = await ResolveImportedValidationFile(epubCard, epubPath, log);
                await OpenBookAsync(epubCard, epubFile);
                await ValidateCurrentEpubReaderAsync(log);
                await CloseReaderAsync();
            }

            await log.WriteLineAsync("PASS Kreader Linux validation completed");
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

    private async Task<BookCardViewModel> ResolveImportedValidationBookAsync(
        ImportBatchResult importResult,
        string epubPath,
        string titleHint)
    {
        var expectedSha = await ComputeValidationEpubShaAsync(epubPath);
        var importedBookId = importResult.Items
            .FirstOrDefault(item => item.Succeeded && item.Book is not null)?.Book?.Id;
        return ViewModel.Books.FirstOrDefault(card =>
                card.Book.Files.Any(file => file.Sha256.Equals(expectedSha, StringComparison.OrdinalIgnoreCase))
                || (importedBookId is not null && card.Book.Id == importedBookId))
            ?? ViewModel.Books.First(card => card.Title.Contains(titleHint, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<BookFile> ResolveImportedValidationFile(
        BookCardViewModel card,
        string epubPath,
        TextWriter log)
    {
        var expectedSha = await ComputeValidationEpubShaAsync(epubPath);
        var exact = card.Book.Files.FirstOrDefault(file =>
            file.Format.Equals("epub", StringComparison.OrdinalIgnoreCase)
            && file.Sha256.Equals(expectedSha, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;
        await log.WriteLineAsync(
            "WARN no library file matches the imported hash; falling back to the first EPUB file");
        return card.Book.Files.First(file => file.Format.Equals("epub", StringComparison.OrdinalIgnoreCase));
    }

    private static Task<string> ComputeValidationEpubShaAsync(string epubPath) =>
        Task.FromResult(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(epubPath))));

    private async Task ValidateLinuxPageComposeModeAsync(TextWriter log)
    {
        await SetKreaderValidationLayoutAsync(flowMode: 1, twoPage: false, vertical: true);
        var host = CurrentReaderHost
            ?? throw new InvalidOperationException("Linux page-compose reader host missing");
        Require(IsReaderPageComposeMode, "page-compose mode was not detected for vertical paginated layout");
        await Task.Delay(200, ReaderToken);

        var state = await ReadPageComposeStateAsync(host);
        Require(
            !state.TryGetProperty("missing", out _) || !state.GetProperty("missing").GetBoolean(),
            "page-compose containers are missing: " + state);
        Require(
            state.GetProperty("flowChildren").GetInt32() > 0,
            "page-compose flow is empty: " + state);
        Require(
            state.GetProperty("sw").GetDouble() <= state.GetProperty("w").GetDouble() + 1.5,
            "page-compose flow overflows its viewport — the fill loop regressed: " + state);
        Require(
            (state.GetProperty("text").GetString() ?? string.Empty).Trim().Length > 0,
            "page-compose flow has no text: " + state);
        Require(
            state.GetProperty("writingMode").GetString() == "vertical-rl",
            "page-compose flow lost vertical-rl: " + state);
        Require(
            state.GetProperty("bank").GetInt32() > 0 || state.GetProperty("complete").GetBoolean(),
            "page-compose bank is empty on a multi-page chapter without the complete flag: " + state);
        await log.WriteLineAsync(
            "PASS page-compose structure: flow fills the viewport with "
            + state.GetProperty("flowChildren").GetInt32() + " blocks, bank "
            + state.GetProperty("bank").GetInt32() + " blocks");

        var inline = await ReadKreaderNativeVerticalInlineDiagnosticsAsync(host);
        Require(
            inline.GetProperty("preparedVersion").GetString() == "publication-native-compat-1"
            && inline.GetProperty("linuxNumberRunCount").GetInt32() >= 0
            && inline.GetProperty("linuxNumericStyleErrorCount").GetInt32() == 0
            && inline.GetProperty("syntheticLinuxNumberLayoutCount").GetInt32() == 0
            && inline.GetProperty("legacyWrapperCount").GetInt32() == 0
            && inline.GetProperty("syntheticLayoutCount").GetInt32() == 0
            && inline.GetProperty("boundaryOverlapCount").GetInt32() == 0,
            "page-compose inline invariants failed: " + inline);
        await log.WriteLineAsync("PASS page-compose inline cell geometry");

        var pitch = await ReadPageComposeColumnPitchAsync(host);
        Require(
            pitch.GetProperty("collapsed").GetInt32() == 0,
            "page-compose column pitch collapsed: " + pitch);
        await log.WriteLineAsync(
            $"PASS page-compose column pitch >= 0.7x line grid (min {pitch.GetProperty("minPitch").GetDouble():F1}px)");

        var snapshot = CurrentReaderHost is IReaderPageSnapshotProvider snapshotProvider
            ? await snapshotProvider.CaptureVisiblePageAsync(ReaderToken)
            : null;
        if (snapshot is not { Length: > 4 })
            await log.WriteLineAsync("WARN page-compose snapshot unavailable");
        else
            await log.WriteLineAsync("PASS page-compose hold-overlay snapshot");

        var state0 = await ReadPageComposeStateAsync(host);
        RequirePageStatePresent(state0, "post-snapshot");
        var h0 = state0.GetProperty("h").GetDouble();
        await TurnReaderPageAsync(1);
        await Task.Delay(500, ReaderToken);
        var state1 = await ReadPageComposeStateAsync(host);
        RequirePageStatePresent(state1, "forward turn");
        Require(
            state1.GetProperty("h").GetDouble() > h0 + 1,
            $"page-compose forward turn did not advance the page start ({h0:F0} -> "
            + $"{state1.GetProperty("h").GetDouble():F0})");
        Require(
            state1.GetProperty("sw").GetDouble() <= state1.GetProperty("w").GetDouble() + 1.5,
            "page-compose forward turn produced an overflowing page: " + state1);
        await log.WriteLineAsync("PASS page-compose forward turn");

        await TurnReaderPageAsync(-1);
        await Task.Delay(500, ReaderToken);
        var state2 = await ReadPageComposeStateAsync(host);
        RequirePageStatePresent(state2, "backward turn");
        Require(
            Math.Abs(state2.GetProperty("h").GetDouble() - h0) <= 1.5,
            $"page-compose backward turn did not return to the prior page start: " + state2);
        await log.WriteLineAsync("PASS page-compose backward turn");

        await ClickReaderPageZoneAsync(host, leftZone: true);
        await Task.Delay(700, ReaderToken);
        var clicked = await ReadPageComposeStateAsync(host);
        RequirePageStatePresent(clicked, "click zone");
        Require(
            clicked.GetProperty("h").GetDouble() > h0 + 1,
            "a page-compose click-zone turn did not advance: " + clicked);

        await host.InvokeScriptAsync("""
            (() => {
              document.dispatchEvent(new WheelEvent('wheel', {
                bubbles: true, cancelable: true, deltaY: 120 }));
              return true;
            })();
            """);
        await Task.Delay(700, ReaderToken);
        var wheeled = await ReadPageComposeStateAsync(host);
        RequirePageStatePresent(wheeled, "wheel");
        Require(
            wheeled.GetProperty("h").GetDouble() > clicked.GetProperty("h").GetDouble(),
            "a forward wheel gesture did not advance the page-compose page: " + wheeled);
        await log.WriteLineAsync("PASS vertical page turns via click zones and wheel gestures");

        var total = wheeled.GetProperty("f").GetDouble() + wheeled.GetProperty("h").GetDouble();
        var midChar = (int)Math.Min(
            total - 1,
            Math.Max(1, total * 0.5));
        await host.InvokeScriptAsync(
            "window.__pgComposeAt(" + midChar.ToString(
                System.Globalization.CultureInfo.InvariantCulture) + "); true;");
        await Task.Delay(400, ReaderToken);
        var midState = await ReadPageComposeStateAsync(host);
        RequirePageStatePresent(midState, "mid-chapter");
        Require(
            (midState.GetProperty("text").GetString() ?? string.Empty).Trim().Length > 0
            && midState.GetProperty("flowChildren").GetInt32() > 0,
            "page-compose at a mid-chapter offset produced an empty page: " + midState);
        Require(
            midState.GetProperty("sw").GetDouble() <= midState.GetProperty("w").GetDouble() + 1.5,
            "page-compose mid-chapter page overflows: " + midState);
        await log.WriteLineAsync("PASS page-compose mid-chapter composition");
        await SaveKreaderValidationSnapshotAsync(log, "page-compose-page");

        await PauseKreaderValidationAtVerticalPageAsync(log);
    }

    /// <summary>
    /// Page-compose state probe: container counts, fit, and a text sample.
    /// </summary>
    /// <summary>
    /// Samples the reader scroll position every 150 ms so an interaction test
    /// can tell "turned late" from "never turned".
    /// </summary>
    private async Task<string> PollReaderScrollTrailAsync(IReaderHost host, int samples)
    {
        var trail = new List<string>();
        for (var index = 0; index < samples; index++)
        {
            await Task.Delay(150, ReaderToken);
            var metrics = await ReadKreaderDomMetricsAsync(host);
            if (metrics.TryGetProperty("scrollLeft", out var scrollLeft))
                trail.Add(scrollLeft.GetDouble().ToString("0", System.Globalization.CultureInfo.InvariantCulture));
        }

        return string.Join(",", trail);
    }

    private async Task SaveKreaderValidationSnapshotAsync(TextWriter log, string name)
    {
        if (Environment.GetEnvironmentVariable("KKINDLE_KREADER_VALIDATE_SHOT_DIR")
                is not { Length: > 0 } shotDir
            || CurrentReaderHost is not IReaderPageSnapshotProvider provider)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(shotDir);
            var png = await provider.CaptureVisiblePageAsync(ReaderToken);
            if (png is { Length: > 0 })
            {
                var path = Path.Combine(shotDir, $"{name}-{DateTime.Now:HHmmss-fff}.png");
                await File.WriteAllBytesAsync(path, png);
                await log.WriteLineAsync($"DEBUG snapshot {name} -> {path} ({png.Length} bytes)");
            }
            else
            {
                await log.WriteLineAsync($"DEBUG snapshot {name} unavailable");
            }
        }
        catch (Exception exception)
        {
            await log.WriteLineAsync($"DEBUG snapshot {name} failed: {exception.Message}");
        }
    }

    private static async Task ClickReaderPageZoneAsync(IReaderHost host, bool leftZone)
    {
        await host.InvokeScriptAsync($$"""
            (() => {
              const width = window.innerWidth || document.documentElement.clientWidth || 0;
              const height = window.innerHeight || document.documentElement.clientHeight || 0;
              if (width <= 0 || height <= 0) return false;
              const x = {{(leftZone ? "width * 0.16" : "width * 0.84")}};
              const isPageTurnTarget = element =>
                element instanceof Element
                && !element.closest('a, button, input, textarea, select, option, label, #kkindle-selection-bar');
              let point = null;
              for (const fraction of [0.5, 0.35, 0.65, 0.2, 0.8]) {
                const y = height * fraction;
                if (isPageTurnTarget(document.elementFromPoint(x, y))) {
                  point = { x, y };
                  break;
                }
              }
              if (point === null) point = { x, y: height / 2 };
              const selection = window.getSelection?.();
              if (selection && !selection.isCollapsed) selection.removeAllRanges();
              const init = {
                bubbles: true, cancelable: true, composed: true,
                pointerId: 7, isPrimary: true, button: 0,
                clientX: point.x, clientY: point.y
              };
              document.body.dispatchEvent(new PointerEvent('pointerdown', init));
              document.body.dispatchEvent(new PointerEvent('pointerup', init));
              return true;
            })();
            """);
    }

    private static void RequirePageStatePresent(JsonElement state, string context)
    {
        if (state.TryGetProperty("missing", out _))
            throw new InvalidOperationException(
                $"page-compose containers missing at {context}: {state.GetRawText()}");
    }

    private async Task<JsonElement> ReadPageComposeStateAsync(IReaderHost host)
    {
        var result = await host.InvokeScriptAsync(
            "JSON.stringify((() => { const f = document.getElementById('kkindle-page-flow');"
            + " const b = document.getElementById('kkindle-page-bank');"
            + " const hEl = document.getElementById('kkindle-page-history');"
            + " if (!f || !b || !hEl) return { missing: true,"
            + " bodyChildren: document.body.children.length,"
            + " ids: Array.from(document.body.children).slice(0, 8).map(c => c.id || c.className || c.tagName).join('|'),"
            + " styleEl: !!document.getElementById('kkindle-page-mode-style'),"
            + " removed: ((window.__pgRemovedTrace || '') + ' ||| CALLER: ' + (window.__pgTeardownCaller || 'none')).slice(0, 1200) };"
            + " return { bank: b.children.length, hist: hEl.children.length,"
            + " flowChildren: f.children.length, w: f.clientWidth, hgt: f.clientHeight,"
            + " sw: f.scrollWidth, text: (f.textContent || '').slice(0, 60),"
            + " h: window.__pg.h, f: window.__pg.f, complete: window.__pg.complete,"
            + " writingMode: getComputedStyle(f).writingMode }; })())");
        var raw = DecodeReaderScriptString(result) ?? result ?? "{}";
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }

    /// <summary>
    /// Measures adjacent text-column pitches inside the composed page and
    /// counts collapses (the WebKitGTK line-box regression signature).
    /// </summary>
    private async Task<JsonElement> ReadPageComposeColumnPitchAsync(IReaderHost host)
    {
        var result = await host.InvokeScriptAsync("""
            (() => {
              const f = document.getElementById('kkindle-page-flow');
              if (!f) return null;
              const line = parseFloat(getComputedStyle(f).lineHeight) || 0;
              const lefts = [];
              const walker = document.createTreeWalker(f, NodeFilter.SHOW_TEXT);
              let node;
              while ((node = walker.nextNode())) {
                const range = document.createRange();
                range.selectNodeContents(node);
                for (const rect of Array.from(range.getClientRects())) {
                  if (rect.width > 2 && rect.height > 2)
                    lefts.push(rect.left);
                }
              }
              lefts.sort((a, b) => a - b);
              const unique = [];
              // Cluster lefts with a tolerance proportional to the line grid:
              // cells, ink-shifted glyphs and text fragments inside one column
              // report lefts within about half a cell of each other, while
              // true adjacent columns sit a full line pitch apart.
              const clusterTolerance = Math.max(4, line * 0.35);
              for (const left of lefts) {
                if (!unique.length || left - unique[unique.length - 1] > clusterTolerance) unique.push(left);
              }
              let collapsed = 0;
              let minPitch = 0;
              for (let index = 1; index < unique.length; index++) {
                const pitch = unique[index] - unique[index - 1];
                if (index === 1 || pitch < minPitch) minPitch = pitch;
                if (pitch > 0.5 && pitch < line * 0.7) collapsed++;
              }
              return JSON.stringify({
                line, columns: unique.length, minPitch, collapsed
              });
            })();
            """);
        var raw = DecodeReaderScriptString(result) ?? result ?? "{}";
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }


    private async Task<JsonElement> ReadVerticalFixtureSpacingAsync(IReaderHost host)
    {
        var result = await host.InvokeScriptAsync("""
            (() => {
              const sample = document.querySelector('.vertical-fixture-sample');
              if (!sample) return null;
              const items = [];
              const walker = document.createTreeWalker(sample, NodeFilter.SHOW_TEXT);
              for (let node = walker.nextNode(); node; node = walker.nextNode()) {
                const text = node.nodeValue || '';
                if (node.parentElement?.closest(
                    '.kkindle-linux-vertical-single, '
                      + '.kkindle-linux-vertical-single-punctuation, '
                      + '.kkindle-linux-vertical-tcy, '
                      + '.kkindle-linux-vertical-number, '
                      + '.kkindle-linux-vertical-cjk, '
                      + '.kkindle-linux-vertical-pair-punctuation, '
                      + '.kkindle-linux-vertical-footnote'))
                  continue;
                for (let index = 0; index < text.length; index++) {
                  if (/\s/.test(text[index])) continue;
                  const range = document.createRange();
                  range.setStart(node, index);
                  range.setEnd(node, index + 1);
                  const rect = range.getBoundingClientRect();
                  if (rect.width <= 0 || rect.height <= 0) continue;
                  items.push({ text: text[index], top: rect.top, bottom: rect.bottom,
                    left: rect.left, right: rect.right });
                }
              }
              let adjacentOverlapCount = 0;
              let maxOverlap = 0;
              for (let index = 1; index < items.length; index++) {
                const previous = items[index - 1];
                const current = items[index];
                const previousCenter = (previous.left + previous.right) / 2;
                const currentCenter = (current.left + current.right) / 2;
                if (Math.abs(previousCenter - currentCenter) > 2) continue;
                const overlap = previous.bottom - current.top;
                if (overlap <= 1.05) continue;
                adjacentOverlapCount++;
                maxOverlap = Math.max(maxOverlap, overlap);
              }
              const alignmentItems = [];
              const alignmentWalker = document.createTreeWalker(
                sample,
                NodeFilter.SHOW_ELEMENT | NodeFilter.SHOW_TEXT);
              const generatedAlignmentSelector = [
                '.kkindle-linux-vertical-single',
                '.kkindle-linux-vertical-single-punctuation',
                '.kkindle-linux-vertical-tcy',
                '.kkindle-linux-vertical-number',
                '.kkindle-linux-vertical-cjk',
                '.kkindle-linux-vertical-pair-punctuation'
              ].join(',');
              const alignmentCategory = character => {
                if (/[A-Za-z]/.test(character)) return 'latin';
                if (/[0-9]/.test(character)) return 'digit';
                if (/[!"#$%&'()*+,\-./:;<=>?@[\\\]^_`{|}~，。！？、；：“”‘’（）《》〈〉【】〔〕［］｛｝…—]/.test(character))
                  return 'punctuation';
                if (/[⺀-鿿豈-﫿]/.test(character)) return 'cjk';
                return 'other';
              };
              for (let node = alignmentWalker.nextNode(); node; node = alignmentWalker.nextNode()) {
                if (node.nodeType === Node.ELEMENT_NODE) {
                  if (!node.matches?.(generatedAlignmentSelector)) continue;
                  const rect = node.getBoundingClientRect();
                  if (rect.width > 0 && rect.height > 0)
                    alignmentItems.push({
                      category: node.matches(
                        '.kkindle-linux-vertical-single-punctuation, '
                          + '.kkindle-linux-vertical-pair-punctuation')
                        ? 'punctuation'
                        : node.matches('.kkindle-linux-vertical-cjk')
                          ? 'cjk' : 'digit',
                      text: node.textContent || '',
                      rect
                    });
                  continue;
                }
                const parent = node.parentElement;
                if (!parent || parent.closest(
                    generatedAlignmentSelector + ', .kkindle-linux-vertical-footnote'))
                  continue;
                const value = node.nodeValue || '';
                for (let index = 0; index < value.length; index++) {
                  if (/\s/.test(value[index])) continue;
                  const range = document.createRange();
                  range.setStart(node, index);
                  range.setEnd(node, index + 1);
                  const rect = range.getBoundingClientRect();
                  if (rect.width > 0 && rect.height > 0)
                    alignmentItems.push({
                      category: alignmentCategory(value[index]),
                      text: value[index],
                      rect
                    });
                }
              }
              let verticalCenterErrorCount = 0;
              let maxVerticalCenterDelta = 0;
              const verticalCenterSamples = [];
              for (let index = 1; index < alignmentItems.length; index++) {
                const previous = alignmentItems[index - 1];
                const current = alignmentItems[index];
                if (current.rect.top + 1.05 < previous.rect.top) continue;
                const previousCenter = previous.rect.left + previous.rect.width / 2;
                const currentCenter = current.rect.left + current.rect.width / 2;
                const sameColumn = Math.abs(previousCenter - currentCenter)
                  <= Math.max(8, previous.rect.width, current.rect.width) * 0.75;
                if (!sameColumn) continue;
                const delta = Math.abs(previousCenter - currentCenter);
                maxVerticalCenterDelta = Math.max(maxVerticalCenterDelta, delta);
                if (delta <= 1.1) continue;
                verticalCenterErrorCount++;
                if (verticalCenterSamples.length < 12) {
                  verticalCenterSamples.push({
                    previous: previous.text,
                    current: current.text,
                    delta: +delta.toFixed(2),
                    previousRect: [previous.rect.left, previous.rect.top, previous.rect.width, previous.rect.height].map(v => +v.toFixed(2)),
                    currentRect: [current.rect.left, current.rect.top, current.rect.width, current.rect.height].map(v => +v.toFixed(2))
                  });
                }
              }
              let cellOverlapCount = 0;
              let maxCellOverlap = 0;
              const cellOverlapItems = [];
              for (let index = 1; index < alignmentItems.length; index++) {
                const previous = alignmentItems[index - 1];
                const current = alignmentItems[index];
                if (current.rect.top + 1.05 < previous.rect.top) continue;
                const previousCenter = previous.rect.left + previous.rect.width / 2;
                const currentCenter = current.rect.left + current.rect.width / 2;
                if (Math.abs(previousCenter - currentCenter) > 1.1) continue;
                const overlap = previous.rect.bottom - current.rect.top;
                if (overlap <= 1.05) continue;
                cellOverlapCount++;
                maxCellOverlap = Math.max(maxCellOverlap, overlap);
                if (cellOverlapItems.length < 12) {
                  cellOverlapItems.push({
                    previous: previous.text,
                    current: current.text,
                    overlap: +overlap.toFixed(2),
                    previousRect: [previous.rect.left, previous.rect.top, previous.rect.width, previous.rect.height].map(v => +v.toFixed(2)),
                    currentRect: [current.rect.left, current.rect.top, current.rect.width, current.rect.height].map(v => +v.toFixed(2))
                  });
                }
              }
              const numberBoundaryGaps = [];
              for (const run of sample.querySelectorAll('.kkindle-cjk-before-number')) {
                const previous = run.previousSibling;
                if (!previous) continue;
                let previousText = '';
                let previousRect = null;
                if (previous.nodeType === Node.TEXT_NODE) {
                  previousText = previous.nodeValue || '';
                  let index = previousText.length - 1;
                  while (index >= 0 && /\s/.test(previousText[index])) index--;
                  if (index < 0) continue;
                  const range = document.createRange();
                  range.setStart(previous, index);
                  range.setEnd(previous, index + 1);
                  previousRect = range.getBoundingClientRect();
                  previousText = previousText[index];
                } else if (previous.matches?.('.kkindle-linux-vertical-cjk')) {
                  previousText = previous.textContent || '';
                  previousRect = previous.getBoundingClientRect();
                }
                if (!previousRect || !previousText) continue;
                const runRect = run.getBoundingClientRect();
                const previousCenter = (previousRect.left + previousRect.right) / 2;
                const runCenter = (runRect.left + runRect.right) / 2;
                // An orthogonal horizontal-tb compatibility cell can have a
                // different physical X origin from the native Han Range even
                // when it occupies the same vertical column. Compare using a
                // fraction of the cell width, not a hard two-pixel cutoff.
                const sameColumnTolerance = Math.max(
                  2,
                  parseFloat(getComputedStyle(run).fontSize || '0') * 0.75,
                  Math.max(previousRect.width, runRect.width) * 0.75);
                if (Math.abs(previousCenter - runCenter) > sameColumnTolerance) continue;
                numberBoundaryGaps.push({
                  boundary: previousText + '->' + (run.textContent || ''),
                  gap: +(runRect.top - previousRect.bottom).toFixed(2)
                });
              }
              const text = sample.textContent || '';
              const footnote = sample.querySelector('.kkindle-linux-vertical-footnote');
              const footnoteStyle = footnote ? getComputedStyle(footnote) : null;
              const bodyStyle = getComputedStyle(document.body);
              const bodyFontSize = parseFloat(bodyStyle.fontSize) || 0;
              const footnoteRect = footnote?.getBoundingClientRect();
              const footnoteInner = footnote?.querySelector(
                ':scope > .kkindle-linux-vertical-footnote-inner');
              let footnoteInkRect = null;
              if (footnoteInner) {
                const textWalker = document.createTreeWalker(
                  footnoteInner, NodeFilter.SHOW_TEXT);
                for (let node = textWalker.nextNode(); node; node = textWalker.nextNode()) {
                  if (!(node.nodeValue || '').trim()) continue;
                  const range = document.createRange();
                  range.selectNodeContents(node);
                  footnoteInkRect = range.getBoundingClientRect();
                  break;
                }
              }
              const footnoteCenterDelta = footnoteRect && footnoteInkRect
                ? Math.max(
                    Math.abs((footnoteRect.left + footnoteRect.width / 2)
                      - (footnoteInkRect.left + footnoteInkRect.width / 2)),
                    Math.abs((footnoteRect.top + footnoteRect.height / 2)
                      - (footnoteInkRect.top + footnoteInkRect.height / 2)))
                : Number.POSITIVE_INFINITY;
              const pairOpen = Array.from(sample.querySelectorAll('.kkindle-linux-vertical-pair-open'));
              const pairClose = Array.from(sample.querySelectorAll('.kkindle-linux-vertical-pair-close'));
              const readCellGeometry = span => {
                const outer = span.getBoundingClientRect();
                const inner = span.querySelector(':scope > .kkindle-linux-vertical-cell-inner');
                const glyph = inner?.querySelector(':scope > .kkindle-linux-vertical-glyph');
                // Mapped punctuation is painted by ::before. The original
                // text node remains transparent and WebKit reports its
                // untransformed range, so using that range would measure a
                // phantom glyph outside the visible centred cell.
                if (inner?.dataset.kkindleVerticalGlyph)
                  return { outer, inner: inner.getBoundingClientRect(), glyph: inner.getBoundingClientRect() };
                // For a real generated glyph, measure the element rather than
                // a Range over its text node. WebKitGTK can leave a Range in
                // the pre-transform coordinate space when the parent cell is
                // rotated (vertical ellipsis/dash) or relatively shifted.
                if (inner && glyph)
                  return { outer, inner: inner.getBoundingClientRect(), glyph: glyph.getBoundingClientRect() };
                const node = glyph?.firstChild || inner?.firstChild || span.firstChild;
                if (!node || node.nodeType !== Node.TEXT_NODE)
                  return { outer, inner: inner?.getBoundingClientRect() || null, glyph: null };
                const range = document.createRange();
                range.selectNodeContents(node);
                return {
                  outer,
                  inner: inner?.getBoundingClientRect() || null,
                  glyph: range.getBoundingClientRect()
                };
              };
              const openGeometry = pairOpen[0] ? readCellGeometry(pairOpen[0]) : null;
              const closeGeometry = pairClose[0] ? readCellGeometry(pairClose[0]) : null;
              const pairOpenStyle = pairOpen[0] ? getComputedStyle(pairOpen[0]) : null;
              const pairCloseStyle = pairClose[0] ? getComputedStyle(pairClose[0]) : null;
              const pairOpenLeftInset = openGeometry?.glyph
                ? openGeometry.glyph.left - openGeometry.outer.left : Number.POSITIVE_INFINITY;
              const pairCloseRightInset = closeGeometry?.glyph
                ? closeGeometry.outer.right - closeGeometry.glyph.right : Number.POSITIVE_INFINITY;
              const pairOpenTopInset = openGeometry?.glyph
                ? openGeometry.glyph.top - openGeometry.outer.top : Number.NEGATIVE_INFINITY;
              const pairOpenBottomInset = openGeometry?.glyph
                ? openGeometry.outer.bottom - openGeometry.glyph.bottom : Number.NEGATIVE_INFINITY;
              const cellCenterDelta = geometry => geometry?.inner
                ? Math.max(
                    Math.abs((geometry.outer.left + geometry.outer.width / 2)
                      - (geometry.inner.left + geometry.inner.width / 2)),
                    Math.abs((geometry.outer.top + geometry.outer.height / 2)
                      - (geometry.inner.top + geometry.inner.height / 2)))
                : Number.POSITIVE_INFINITY;
              const singlePunctuation = Array.from(
                sample.querySelectorAll('.kkindle-linux-vertical-single-punctuation'));
              let singlePunctuationStyleErrorCount = 0;
              let singlePunctuationCenterErrorCount = 0;
              for (const span of singlePunctuation) {
                const style = getComputedStyle(span);
                // The cell shares the parent's vertical-rl flow (the
                // orthogonal horizontal-tb cells collapsed WebKitGTK line
                // boxes); center alignment along both axes is what matters.
                if (style.display !== 'inline-grid'
                    || style.writingMode !== 'vertical-rl'
                    || style.alignItems !== 'center'
                    || style.justifyItems !== 'center')
                  singlePunctuationStyleErrorCount++;
                const geometry = readCellGeometry(span);
                const centeredBox = geometry.inner || geometry.glyph;
                if (!centeredBox) {
                  singlePunctuationCenterErrorCount++;
                  continue;
                }
                const cellCenterX = geometry.outer.left + geometry.outer.width / 2;
                const cellCenterY = geometry.outer.top + geometry.outer.height / 2;
                const glyphCenterX = centeredBox.left + centeredBox.width / 2;
                const glyphCenterY = centeredBox.top + centeredBox.height / 2;
                if (Math.max(
                    Math.abs(cellCenterX - glyphCenterX),
                    Math.abs(cellCenterY - glyphCenterY)) > 1.1)
                  singlePunctuationCenterErrorCount++;
              }
              const verticalCenteredMarks = Array.from(
                sample.querySelectorAll('.kkindle-linux-vertical-centered-mark'));
              let verticalCenteredMarkRotationErrorCount = 0;
              let verticalCenteredMarkCenterErrorCount = 0;
              for (const span of verticalCenteredMarks) {
                const inner = span.querySelector(':scope > .kkindle-linux-vertical-cell-inner');
                const matrix = new DOMMatrix(getComputedStyle(inner).transform);
                if (Math.abs(matrix.b) < 0.8 || Math.abs(matrix.c) < 0.8)
                  verticalCenteredMarkRotationErrorCount++;
                const geometry = readCellGeometry(span);
                const centeredBox = geometry.inner || geometry.glyph;
                if (!centeredBox) {
                  verticalCenteredMarkCenterErrorCount++;
                  continue;
                }
                const dx = Math.abs(
                  geometry.outer.left + geometry.outer.width / 2
                  - centeredBox.left - centeredBox.width / 2);
                const dy = Math.abs(
                  geometry.outer.top + geometry.outer.height / 2
                  - centeredBox.top - centeredBox.height / 2);
                if (Math.max(dx, dy) > 1.1)
                  verticalCenteredMarkCenterErrorCount++;
              }
              const singleDigits = Array.from(
                sample.querySelectorAll('.kkindle-linux-vertical-single'));
              let singleDigitCenterErrorCount = 0;
              for (const span of singleDigits) {
                const geometry = readCellGeometry(span);
                const centeredBox = geometry.inner || geometry.glyph;
                if (!centeredBox) {
                  singleDigitCenterErrorCount++;
                  continue;
                }
                const cellCenterX = geometry.outer.left + geometry.outer.width / 2;
                const cellCenterY = geometry.outer.top + geometry.outer.height / 2;
                const glyphCenterX = centeredBox.left + centeredBox.width / 2;
                const glyphCenterY = centeredBox.top + centeredBox.height / 2;
                if (Math.max(
                    Math.abs(cellCenterX - glyphCenterX),
                    Math.abs(cellCenterY - glyphCenterY)) > 1.1)
                  singleDigitCenterErrorCount++;
              }
              const cjkCells = Array.from(
                sample.querySelectorAll('.kkindle-linux-vertical-cjk'));
              let cjkCellSizeErrorCount = 0;
              let cjkGlyphCenterErrorCount = 0;
              let cjkMaxGlyphCenterDelta = 0;
              for (const cell of cjkCells) {
                const outer = cell.getBoundingClientRect();
                const style = getComputedStyle(cell);
                const fontSize = parseFloat(style.fontSize) || 0;
                if (!(fontSize > 0)
                    || Math.abs(outer.width - fontSize) > 1.1
                    || Math.abs(outer.height - fontSize) > 1.1)
                  cjkCellSizeErrorCount++;
                const glyph = cell.querySelector(
                  ':scope > .kkindle-linux-vertical-cjk-ink > .kkindle-linux-vertical-glyph');
                if (!glyph) {
                  cjkGlyphCenterErrorCount++;
                  continue;
                }
                const ink = glyph.getBoundingClientRect();
                const delta = Math.max(
                  Math.abs(outer.left + outer.width / 2 - ink.left - ink.width / 2),
                  Math.abs(outer.top + outer.height / 2 - ink.top - ink.height / 2));
                cjkMaxGlyphCenterDelta = Math.max(cjkMaxGlyphCenterDelta, delta);
                if (delta > 1.1) cjkGlyphCenterErrorCount++;
              }
              const singlePunctuationSample = singlePunctuation[0]
                ? readCellGeometry(singlePunctuation[0]) : null;
              const singleDigitSample = singleDigits[0]
                ? readCellGeometry(singleDigits[0]) : null;
              return JSON.stringify({
                adjacentOverlapCount,
                maxOverlap: +maxOverlap.toFixed(2),
                verticalCenterErrorCount,
                maxVerticalCenterDelta: +maxVerticalCenterDelta.toFixed(2),
                verticalCenterSamples,
                cellOverlapCount,
                maxCellOverlap: +maxCellOverlap.toFixed(2),
                cellOverlapItems,
                numberBoundaryGaps,
                footnoteFound: !!footnote,
                footnoteText: footnote?.textContent || '',
                footnoteWidth: +(footnoteRect?.width || 0).toFixed(2),
                footnoteHeight: +(footnoteRect?.height || 0).toFixed(2),
                footnoteFontSize: +(parseFloat(footnoteStyle?.fontSize || '0') || 0).toFixed(2),
                footnoteInlineAdvance: +bodyFontSize.toFixed(2),
                footnoteCenterDelta: +footnoteCenterDelta.toFixed(2),
                footnoteOuter: footnoteRect
                  ? [footnoteRect.left, footnoteRect.top, footnoteRect.width, footnoteRect.height].map(v => +v.toFixed(2)) : null,
                footnoteInk: footnoteInkRect
                  ? [footnoteInkRect.left, footnoteInkRect.top, footnoteInkRect.width, footnoteInkRect.height].map(v => +v.toFixed(2)) : null,
                pairOpenCount: pairOpen.length,
                pairCloseCount: pairClose.length,
                pairOpenJustifyItems: pairOpenStyle?.justifyItems || '',
                pairCloseJustifyItems: pairCloseStyle?.justifyItems || '',
                pairOpenLeftInset: +pairOpenLeftInset.toFixed(2),
                pairCloseRightInset: +pairCloseRightInset.toFixed(2),
                pairOpenTopInset: +pairOpenTopInset.toFixed(2),
                pairOpenBottomInset: +pairOpenBottomInset.toFixed(2),
                pairOpenCenterDelta: +cellCenterDelta(openGeometry).toFixed(2),
                pairCloseCenterDelta: +cellCenterDelta(closeGeometry).toFixed(2),
                pairOpenOuter: openGeometry?.outer
                  ? [openGeometry.outer.left, openGeometry.outer.top, openGeometry.outer.width, openGeometry.outer.height].map(v => +v.toFixed(2)) : null,
                pairOpenGlyph: openGeometry?.glyph
                  ? [openGeometry.glyph.left, openGeometry.glyph.top, openGeometry.glyph.width, openGeometry.glyph.height].map(v => +v.toFixed(2)) : null,
                pairOpenWritingMode: pairOpenStyle?.writingMode || '',
                singlePunctuationOuter: singlePunctuationSample?.outer
                  ? [singlePunctuationSample.outer.left, singlePunctuationSample.outer.top, singlePunctuationSample.outer.width, singlePunctuationSample.outer.height].map(v => +v.toFixed(2)) : null,
                singlePunctuationGlyph: singlePunctuationSample?.glyph
                  ? [singlePunctuationSample.glyph.left, singlePunctuationSample.glyph.top, singlePunctuationSample.glyph.width, singlePunctuationSample.glyph.height].map(v => +v.toFixed(2)) : null,
                singleDigitOuter: singleDigitSample?.outer
                  ? [singleDigitSample.outer.left, singleDigitSample.outer.top, singleDigitSample.outer.width, singleDigitSample.outer.height].map(v => +v.toFixed(2)) : null,
                singleDigitGlyph: singleDigitSample?.glyph
                  ? [singleDigitSample.glyph.left, singleDigitSample.glyph.top, singleDigitSample.glyph.width, singleDigitSample.glyph.height].map(v => +v.toFixed(2)) : null,
                singlePunctuationCount: singlePunctuation.length,
                singlePunctuationStyleErrorCount,
                singlePunctuationCenterErrorCount,
                verticalCenteredMarkCount: verticalCenteredMarks.length,
                verticalCenteredMarkRotationErrorCount,
                verticalCenteredMarkCenterErrorCount,
                singleDigitCenterErrorCount,
                cjkCellCount: cjkCells.length,
                cjkCellSizeErrorCount,
                cjkGlyphCenterErrorCount,
                cjkMaxGlyphCenterDelta: +cjkMaxGlyphCenterDelta.toFixed(2),
                dnaFound: text.includes('DNA'),
                fpgaFound: text.includes('FPGA')
              });
            })();
            """);
        var raw = DecodeReaderScriptString(result) ?? result ?? "{}";
        Require(!string.IsNullOrWhiteSpace(raw), "vertical fixture spacing probe returned no data");
        using var document = JsonDocument.Parse(raw!);
        return document.RootElement.Clone();
    }

    /// <summary>
    /// Walks consecutive real-book chapters in vertical mode and asserts the
    /// same no-clipping/margin invariants page by page, while tallying which
    /// vertical run classes (digits, tate-chu-yoko pairs, long numeric runs,
    /// Latin words, ASCII punctuation) and how much CJK text the sweep
    /// exercised. Real books open on arbitrary front matter, so this phase is
    /// what proves mixed-script body text renders on the standard grid.
    /// </summary>
    private static async Task<JsonElement> ShowAndReadKreaderSelectionBarAsync(IReaderHost host)
    {
        var selected = DecodeReaderScriptString(await host.InvokeScriptAsync("""
            (() => {
              const selection = window.getSelection?.();
              if (!selection || !document.body) return '';
              selection.removeAllRanges();
              const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
              for (let node = walker.nextNode(); node; node = walker.nextNode()) {
                const parent = node.parentElement;
                if (!parent || parent.closest('#kkindle-selection-bar, script, style, noscript')) continue;
                const text = node.nodeValue || '';
                const start = text.search(/\S/);
                if (start < 0) continue;
                const probe = document.createRange();
                probe.setStart(node, start);
                probe.setEnd(node, Math.min(text.length, start + 1));
                const rect = probe.getBoundingClientRect();
                if (rect.bottom <= 0 || rect.top >= window.innerHeight
                    || rect.right <= 0 || rect.left >= window.innerWidth) continue;
                const range = document.createRange();
                range.setStart(node, start);
                range.setEnd(node, Math.min(text.length, start + 6));
                selection.addRange(range);
                return selection.toString();
              }
              return '';
            })();
            """));
        Require(!string.IsNullOrWhiteSpace(selected), "could not create visible reader selection");
        await Task.Delay(250);

        var result = await host.InvokeScriptAsync("""
            (() => {
              const bar = document.getElementById('kkindle-selection-bar');
              const firstButton = bar?.querySelector('button');
              if (!bar || !firstButton) return null;
              const style = getComputedStyle(bar);
              const buttonStyle = getComputedStyle(firstButton);
              const rect = bar.getBoundingClientRect();
              const buttonRect = firstButton.getBoundingClientRect();
              const signature = [
                style.display,
                style.writingMode,
                style.textOrientation,
                style.direction,
                style.paddingTop,
                style.paddingRight,
                style.paddingBottom,
                style.paddingLeft,
                rect.width.toFixed(2),
                rect.height.toFixed(2),
                bar.querySelectorAll(':scope > button').length,
                bar.querySelectorAll('.kk-sel-sep').length,
                buttonStyle.display,
                buttonStyle.fontFamily,
                buttonStyle.fontSize,
                buttonStyle.lineHeight,
                buttonRect.width.toFixed(2),
                buttonRect.height.toFixed(2)
              ].join('|');
              return JSON.stringify({
                display: style.display,
                writingMode: style.writingMode,
                textOrientation: style.textOrientation,
                direction: style.direction,
                width: rect.width,
                height: rect.height,
                buttonCount: bar.querySelectorAll('button').length,
                signature
              });
            })();
            """);
        var raw = DecodeReaderScriptString(result) ?? result ?? "{}";
        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidOperationException("selection bar metric script returned empty result.");
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }

    private static string NormalizeKreaderSelectionBarSignature(JsonElement metrics)
    {
        var fields = (metrics.GetProperty("signature").GetString() ?? string.Empty).Split('|');
        // The bar is positioned against the selected glyph range. A vertical
        // Latin run can legitimately move that range by a few pixels, which
        // changes the auto-sized outer width/height without changing the bar
        // controls or their writing direction. Compare the stable style and
        // control dimensions, not those two placement-dependent fields.
        if (fields.Length > 9)
        {
            fields[8] = "<dynamic-width>";
            fields[9] = "<dynamic-height>";
        }
        return string.Join('|', fields);
    }

    private static async Task ClearKreaderValidationSelectionAsync(IReaderHost host)
    {
        await host.InvokeScriptAsync("window.getSelection?.()?.removeAllRanges();");
        await Task.Delay(100);
    }

    private async Task ValidateWindowsFootnoteHoverAndClickAsync(IReaderHost host, TextWriter log)
    {
        if (!OperatingSystem.IsWindows() || host.View is not Avalonia.Controls.Control view)
            return;

        await host.InvokeScriptAsync("""
            (() => {
              const anchor = document.querySelector('a[epub\\:type*="noteref"], a[role*="doc-noteref"]');
              anchor?.scrollIntoView({ block: 'center', inline: 'center', behavior: 'instant' });
            })();
            """);
        await Task.Delay(150);
        var rawRect = DecodeReaderScriptString(await host.InvokeScriptAsync("""
            (() => {
              const anchor = document.querySelector('a[epub\\:type*="noteref"], a[role*="doc-noteref"]');
              if (!anchor) return null;
              const rect = anchor.getBoundingClientRect();
              return JSON.stringify({
                x: rect.left + rect.width / 2,
                y: rect.top + rect.height / 2,
                viewportWidth: document.documentElement.clientWidth || window.innerWidth || 1,
                viewportHeight: document.documentElement.clientHeight || window.innerHeight || 1
              });
            })();
            """));
        Require(!string.IsNullOrWhiteSpace(rawRect), "footnote marker rectangle missing");
        using var rectDocument = JsonDocument.Parse(rawRect!);
        var rect = rectDocument.RootElement;
        var topLeft = view.PointToScreen(new Avalonia.Point(0, 0));
        var scaling = Avalonia.Controls.TopLevel.GetTopLevel(view)?.RenderScaling ?? 1d;
        var width = Math.Max(1, view.Bounds.Width * scaling);
        var height = Math.Max(1, view.Bounds.Height * scaling);
        var screenX = topLeft.X + (int)Math.Round(rect.GetProperty("x").GetDouble()
            * width / Math.Max(1, rect.GetProperty("viewportWidth").GetDouble()));
        var screenY = topLeft.Y + (int)Math.Round(rect.GetProperty("y").GetDouble()
            * height / Math.Max(1, rect.GetProperty("viewportHeight").GetDouble()));
        Require(SetCursorPos(screenX - 40, screenY), "failed to move cursor away from footnote marker");
        await Task.Delay(80);
        Require(SetCursorPos(screenX, screenY), "failed to position cursor over footnote marker");

        var hoverStates = new List<bool>();
        for (var sample = 0; sample < 8; sample++)
        {
            await Task.Delay(100);
            hoverStates.Add(ReaderFootnoteHostPopup.IsOpen);
        }
        if (!ReaderFootnoteHostPopup.IsOpen)
        {
            var hit = DecodeReaderScriptString(await host.InvokeScriptAsync($$"""
                (() => {
                  const x = {{rect.GetProperty("x").GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture)}};
                  const y = {{rect.GetProperty("y").GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture)}};
                  const element = document.elementFromPoint(x, y);
                  return JSON.stringify({
                    tag: element?.tagName || '',
                    href: element?.closest?.('a')?.href || '',
                    text: element?.textContent || ''
                  });
                })();
                """));
            await log.WriteLineAsync($"DEBUG footnote hover states={string.Join(',', hoverStates)} hit={hit}");
        }
        Require(ReaderFootnoteHostPopup.IsOpen, "footnote popup closed while pointer stayed over marker");
        Require(
            ReaderFootnoteText.Text?.Contains("Footnote validation body", StringComparison.Ordinal) == true,
            "footnote popup text missing");

        await host.InvokeScriptAsync("""
            (() => {
              const anchor = document.querySelector('a[epub\\:type*="noteref"], a[role*="doc-noteref"]');
              anchor?.click();
              return !!anchor;
            })();
            """);
        var navigated = false;
        for (var attempt = 0; attempt < 20 && !navigated; attempt++)
        {
            await Task.Delay(100);
            var result = DecodeReaderScriptString(await host.InvokeScriptAsync("""
                (() => {
                  const target = document.getElementById('footnote-1');
                  return JSON.stringify({
                    visible: !!target && getComputedStyle(target).display !== 'none',
                    hash: window.__kkindleReaderLogicalHash || location.hash || ''
                  });
                })();
                """));
            if (string.IsNullOrWhiteSpace(result)) continue;
            using var resultDocument = JsonDocument.Parse(result);
            var root = resultDocument.RootElement;
            navigated = root.GetProperty("visible").GetBoolean()
                && root.GetProperty("hash").GetString()?.Contains("footnote-1", StringComparison.Ordinal) == true;
        }
        if (!navigated)
        {
            var diagnostic = DecodeReaderScriptString(await host.InvokeScriptAsync("""
                (() => {
                  const anchor = document.querySelector('a[epub\\:type*="noteref"], a[role*="doc-noteref"]');
                  const target = document.getElementById('footnote-1');
                  return JSON.stringify({
                    href: anchor?.href || '',
                    targetDisplay: target ? getComputedStyle(target).display : '',
                    targetInlineDisplay: target?.style?.getPropertyValue('display') || '',
                    hash: window.__kkindleReaderLogicalHash || location.hash || ''
                  });
                })();
                """));
            await log.WriteLineAsync(
                $"DEBUG footnote click navigation: {diagnostic}; currentFragment={_readerCurrentFragment}; status={ReaderStatusText.Text}");
        }
        Require(navigated, "clicking footnote marker did not reveal and navigate to its target");
        HideReaderFootnotePopup();
        await log.WriteLineAsync("PASS Windows footnote hover stability and click navigation");
    }

    private async Task ValidateWindowsSelectionHighlightHoverAsync(IReaderHost host, TextWriter log)
    {
        if (!OperatingSystem.IsWindows() || host.View is not Avalonia.Controls.Control view)
            return;

        await host.InvokeScriptAsync("""
            (() => {
              const paragraph = Array.from(document.querySelectorAll('p'))
                .find(item => (item.textContent || '').trim().length >= 24);
              const textNode = paragraph?.firstChild;
              if (!textNode || textNode.nodeType !== Node.TEXT_NODE) return false;
              const range = document.createRange();
              range.setStart(textNode, 0);
              range.setEnd(textNode, Math.min(20, textNode.textContent?.length || 0));
              const selection = window.getSelection();
              selection?.removeAllRanges();
              selection?.addRange(range);
              document.dispatchEvent(new Event('selectionchange'));
              return true;
            })();
            """);
        await Task.Delay(150);
        var rawButtonRect = DecodeReaderScriptString(await host.InvokeScriptAsync("""
            (() => {
              const bar = document.getElementById('kkindle-selection-bar');
              const button = document.getElementById('kk-sel-highlight');
              const panel = document.getElementById('kk-sel-styles');
              if (!bar || !button || !panel) return null;
              if (!window.__kkindleValidationPointerInstalled) {
                window.__kkindleValidationPointerInstalled = true;
                window.__kkindleValidationMenuTrace = [];
                const trace = (label, event) => window.__kkindleValidationMenuTrace.push({
                  at: Math.round(performance.now()),
                  label,
                  target: event?.target?.id || event?.target?.dataset?.highlight || event?.target?.tagName || '',
                  related: event?.relatedTarget?.id || event?.relatedTarget?.dataset?.highlight || event?.relatedTarget?.tagName || '',
                  classes: panel.className
                });
                for (const type of ['mouseenter', 'mouseleave', 'mouseover', 'mouseout']) {
                  button.addEventListener(type, event => trace('button:' + type, event));
                  panel.addEventListener(type, event => trace('panel:' + type, event));
                }
                document.documentElement.addEventListener('mouseleave', event => trace('html:mouseleave', event));
                new MutationObserver(() => trace('panel:class', null))
                  .observe(panel, { attributes: true, attributeFilter: ['class'] });
                document.addEventListener('mousemove', event => {
                  window.__kkindleValidationPointer = {
                    x: event.clientX,
                    y: event.clientY,
                    target: event.target?.id || event.target?.dataset?.highlight || event.target?.tagName || ''
                  };
                }, true);
              }
              panel.classList.remove('open', 'above');
              bar.style.display = 'flex';
              bar.style.left = '120px';
              bar.style.top = '120px';
              const rect = button.getBoundingClientRect();
              return JSON.stringify({
                x: rect.left + rect.width / 2,
                y: rect.top + rect.height / 2,
                viewportWidth: document.documentElement.clientWidth || window.innerWidth || 1,
                viewportHeight: document.documentElement.clientHeight || window.innerHeight || 1
              });
            })();
            """));
        Require(!string.IsNullOrWhiteSpace(rawButtonRect), "selection highlight button rectangle missing");
        using var buttonDocument = JsonDocument.Parse(rawButtonRect!);
        var buttonRect = buttonDocument.RootElement;
        var topLeft = view.PointToScreen(new Avalonia.Point(0, 0));
        var scaling = Avalonia.Controls.TopLevel.GetTopLevel(view)?.RenderScaling ?? 1d;
        var width = Math.Max(1, view.Bounds.Width * scaling);
        var height = Math.Max(1, view.Bounds.Height * scaling);
        int ToScreenX(double x, double viewportWidth) =>
            topLeft.X + (int)Math.Round(x * width / Math.Max(1, viewportWidth));
        int ToScreenY(double y, double viewportHeight) =>
            topLeft.Y + (int)Math.Round(y * height / Math.Max(1, viewportHeight));
        var viewportWidth = buttonRect.GetProperty("viewportWidth").GetDouble();
        var viewportHeight = buttonRect.GetProperty("viewportHeight").GetDouble();
        Require(
            SetCursorPos(
                ToScreenX(buttonRect.GetProperty("x").GetDouble(), viewportWidth),
                ToScreenY(buttonRect.GetProperty("y").GetDouble(), viewportHeight)),
            "failed to position cursor over selection highlight button");
        await Task.Delay(300);
        Require(await IsKreaderHighlightMenuOpenAsync(host), "selection highlight submenu did not open on hover");

        var rawPanelRect = DecodeReaderScriptString(await host.InvokeScriptAsync("""
            (() => {
              const panel = document.getElementById('kk-sel-styles');
              if (!panel) return null;
              const rect = panel.getBoundingClientRect();
              return JSON.stringify({ x: rect.left + rect.width / 2, y: rect.top + rect.height / 2 });
            })();
            """));
        Require(!string.IsNullOrWhiteSpace(rawPanelRect), "selection highlight submenu rectangle missing");
        using var panelDocument = JsonDocument.Parse(rawPanelRect!);
        var panelRect = panelDocument.RootElement;
        Require(
            SetCursorPos(
                ToScreenX(panelRect.GetProperty("x").GetDouble(), viewportWidth),
                ToScreenY(panelRect.GetProperty("y").GetDouble(), viewportHeight)),
            "failed to position cursor inside selection highlight submenu");
        await Task.Delay(450);
        var remainedOpen = await IsKreaderHighlightMenuOpenAsync(host);
        if (!remainedOpen)
        {
            var diagnostic = DecodeReaderScriptString(await host.InvokeScriptAsync("""
                (() => {
                  const bar = document.getElementById('kkindle-selection-bar');
                  const button = document.getElementById('kk-sel-highlight');
                  const panel = document.getElementById('kk-sel-styles');
                  const pointer = window.__kkindleValidationPointer || { x: -1, y: -1, target: '' };
                  const rect = element => {
                    const value = element?.getBoundingClientRect();
                    return value ? { left: value.left, top: value.top, right: value.right, bottom: value.bottom } : null;
                  };
                  return JSON.stringify({
                    pointer,
                    hit: document.elementFromPoint(pointer.x, pointer.y)?.outerHTML || '',
                    button: rect(button),
                    panel: rect(panel),
                    barDisplay: bar ? getComputedStyle(bar).display : '',
                    panelDisplay: panel ? getComputedStyle(panel).display : '',
                    selection: window.getSelection()?.toString() || '',
                    trace: window.__kkindleValidationMenuTrace || []
                  });
                })();
                """));
            await log.WriteLineAsync("DEBUG selection submenu dismissed: " + diagnostic);
        }
        Require(remainedOpen, "selection highlight submenu closed while pointer was inside it");

        Require(
            SetCursorPos(ToScreenX(20, viewportWidth), ToScreenY(20, viewportHeight)),
            "failed to position cursor outside selection highlight controls");
        await Task.Delay(400);
        Require(!await IsKreaderHighlightMenuOpenAsync(host), "selection highlight submenu stayed open after pointer left button and menu");
        await host.InvokeScriptAsync("""
            (() => {
              const bar = document.getElementById('kkindle-selection-bar');
              document.getElementById('kk-sel-styles')?.classList.remove('open', 'above');
              if (bar) bar.style.display = 'none';
              window.getSelection()?.removeAllRanges();
            })();
            """);
        await log.WriteLineAsync("PASS Windows selection highlight submenu hover open, retain, dismiss");
    }

    private static async Task<bool> IsKreaderHighlightMenuOpenAsync(IReaderHost host)
    {
        var value = DecodeReaderScriptString(await host.InvokeScriptAsync("""
            document.getElementById('kk-sel-styles')?.classList.contains('open') ? 'open' : 'closed';
            """));
        return string.Equals(value, "open", StringComparison.Ordinal);
    }

    private async Task SetKreaderValidationLayoutAsync(int flowMode, bool twoPage, bool vertical = false)
    {
        _readerLayout = NormalizeReaderLayoutForPlatform(_readerLayout with
        {
            FlowMode = flowMode,
            TwoPageMode = twoPage,
            VerticalWriting = vertical
        });
        await ApplyReaderLayoutToHostsAsync(ReaderToken);
        await Task.Delay(250, ReaderToken);
    }

    private static async Task PauseKreaderValidationAtInitialPageAsync(TextWriter log)
    {
        if (!int.TryParse(
                Environment.GetEnvironmentVariable("KKINDLE_KREADER_VALIDATE_INITIAL_PAUSE_MS"),
                out var milliseconds)
            || milliseconds <= 0)
        {
            return;
        }

        milliseconds = Math.Clamp(milliseconds, 1, 30_000);
        await log.WriteLineAsync($"DEBUG pausing on initial EPUB page for {milliseconds} ms");
        await Task.Delay(milliseconds);
    }

    private static async Task PauseKreaderValidationAtVerticalPageAsync(TextWriter log)
    {
        if (!int.TryParse(
                Environment.GetEnvironmentVariable("KKINDLE_KREADER_VALIDATE_VERTICAL_PAUSE_MS"),
                out var milliseconds)
            || milliseconds <= 0)
        {
            return;
        }

        milliseconds = Math.Clamp(milliseconds, 1, 30_000);
        await log.WriteLineAsync($"SCREENSHOT_READY vertical page; pausing for {milliseconds} ms");
        await log.FlushAsync();
        await Task.Delay(milliseconds);
    }

    private async Task WaitForKreaderDocumentAsync(IReaderHost host)
    {
        await host.ReadyTask;
        for (var attempt = 0; attempt < 80; attempt++)
        {
            var metrics = await ReadKreaderDomMetricsAsync(host);
            if (metrics.TryGetProperty("ready", out var ready)
                && ready.ValueKind == JsonValueKind.True)
            {
                return;
            }
            await Task.Delay(100, ReaderToken);
        }
        throw new InvalidOperationException("Kreader document did not become ready.");
    }

    private async Task<JsonElement> WaitForKreaderMetricsTextAsync(
        IReaderHost host,
        string expectedText)
    {
        JsonElement last = default;
        for (var attempt = 0; attempt < 80; attempt++)
        {
            last = await ReadKreaderDomMetricsAsync(host);
            if (last.GetProperty("text").GetString()?.Contains(expectedText, StringComparison.Ordinal) == true)
                return last;
            await Task.Delay(100, ReaderToken);
        }
        return last;
    }

    private async Task<JsonElement> ReadKreaderDomMetricsAsync(IReaderHost host)
    {
        var result = await host.InvokeScriptAsync(
            """
            (() => {
              const root = document.documentElement;
              const body = document.body;
              const el = document.scrollingElement || root;
              const style = body ? getComputedStyle(body) : null;
              return JSON.stringify({
                ready: !!window.__kkindleReaderBridgeInstalled && !!body,
                text: (body?.textContent || body?.innerText || '').slice(0, 4000),
                flowMode: Number(window.__kkindleReaderFlowMode || 0),
                twoPage: window.__kkindleReaderTwoPage === true,
                vertical: window.__kkindleReaderVertical === true,
                bodyDisplay: style?.display || '',
                bodyVisibility: style?.visibility || '',
                bodyOpacity: style?.opacity || '',
                columnCount: style?.columnCount || '',
                columnWidth: style?.columnWidth || '',
                columnGap: style?.columnGap || '',
                writingMode: style?.writingMode || '',
                lineHeight: style?.lineHeight || '',
                verticalPageStep: getComputedStyle(root).getPropertyValue('--kkindle-vertical-page-step'),
                clientWidth: el?.clientWidth || 0,
                clientHeight: el?.clientHeight || 0,
                scrollWidth: el?.scrollWidth || 0,
                scrollHeight: el?.scrollHeight || 0,
                scrollLeft: el?.scrollLeft || 0,
                scrollTop: el?.scrollTop || 0,
                selectionBar: !!document.getElementById('kkindle-selection-bar'),
                bookmarkCorner: !!document.getElementById('kkindle-bookmark-corner'),
                footnoteLinks: document.querySelectorAll('a[epub\\:type*="noteref"], a[role*="doc-noteref"], a[href*="footnote"]').length,
                annotationMarks: document.querySelectorAll('.kkindle-saved-annotation').length
              });
            })();
            """);
        var raw = DecodeReaderScriptString(result) ?? result ?? "{}";
        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidOperationException("Kreader metric script returned empty result.");
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }

    private async Task<JsonElement> ReadKreaderVerticalEdgeDiagnosticsAsync(IReaderHost host)
    {
        var result = await host.InvokeScriptAsync(
            """
            (() => {
              const root = document.documentElement;
              const body = document.body;
              const el = document.scrollingElement || root;
              if (!root || !body || !el) return null;
              const bodyStyle = getComputedStyle(body);
              const rootStyle = getComputedStyle(root);
              const viewport = el.clientWidth || root.clientWidth || window.innerWidth || 0;
              const pageStep = parseFloat(rootStyle.getPropertyValue('--kkindle-vertical-page-step')) || 0;
              const originShift = parseFloat(rootStyle.getPropertyValue('--kkindle-vertical-origin-shift')) || 0;
              const contentShift = parseFloat(rootStyle.getPropertyValue('--kkindle-vertical-content-shift')) || 0;
              const trailingExtent = parseFloat(
                rootStyle.getPropertyValue('--kkindle-vertical-trailing-extent')) || 0;
              const leftSide = Math.max(0, (parseFloat(bodyStyle.paddingLeft) || 0) - trailingExtent);
              const rightSide = parseFloat(bodyStyle.paddingRight) || 0;
              const baseSide = (leftSide + rightSide) / 2;
              const baseLeft = viewport - pageStep - baseSide;
              const baseRight = viewport - baseSide;
              const nominalSafeLeft = baseLeft + originShift;
              const nominalSafeRight = baseRight + originShift;
              const parsedSafeLeft = parseFloat(
                rootStyle.getPropertyValue('--kkindle-vertical-safe-left'));
              const parsedSafeRight = parseFloat(
                rootStyle.getPropertyValue('--kkindle-vertical-safe-right'));
              const safeLeft = Number.isFinite(parsedSafeLeft)
                ? Math.max(nominalSafeLeft, parsedSafeLeft)
                : nominalSafeLeft;
              const safeRight = Number.isFinite(parsedSafeRight)
                ? Math.min(nominalSafeRight, parsedSafeRight)
                : nominalSafeRight;
              const tolerance = 0.75;
              // The usable column band along the inline (vertical) axis. In
              // vertical-rl every column starts at the top of this band and
              // grows downward; anything painted outside it either overflows
              // an atomic run past the page or has been pushed to the bottom
              // by an rtl inline direction.
              const contentTop = parseFloat(bodyStyle.paddingTop) || 0;
              const contentBottom = (el.clientHeight || 0) - (parseFloat(bodyStyle.paddingBottom) || 0);
              const bodyFontSize = parseFloat(bodyStyle.fontSize) || 16;
              const bandTolerance = 2;
              const partialGlyphs = [];
              const visibleMedia = [];
              const overflowGlyphs = [];
              const columnBands = [];
              // Glyphs are grouped into columns by proximity, not by exact
              // left: an upright digit or ASCII punctuation cell centers its
              // ink geometrically (`kkindle-cell-inner`), shifting the glyph
              // rect a few pixels off the host column edge. Grouping by raw
              // rect.left turned every such cell into a phantom one-glyph
              // "column", and a full column that simply ended on such a cell
              // was misread as a bottom-aligned line.
              const columnMergeTolerance = Math.min(bodyFontSize * 0.75, 24);
              let glyphCount = 0;
              let overflowTopCount = 0;
              let overflowBottomCount = 0;
              let hangingPunctuationCount = 0;
              const hangingGlyphs = [];
              let inspectedCharacters = 0;
              let minGlyphLeft = Number.POSITIVE_INFINITY;
              let maxGlyphRight = Number.NEGATIVE_INFINITY;
              const columnLefts = [];
              const walker = document.createTreeWalker(body, NodeFilter.SHOW_TEXT);
              for (let node = walker.nextNode(); node; node = walker.nextNode()) {
                const parent = node.parentElement;
                if (!parent || parent.closest('#kkindle-selection-bar, script, style, noscript')) continue;
                const text = node.nodeValue || '';
                const nodeRange = document.createRange();
                nodeRange.selectNodeContents(node);
                const nodeIsVisible = Array.from(nodeRange.getClientRects()).some(rect =>
                  rect.bottom > 0 && rect.top < el.clientHeight
                  && rect.right > safeLeft - 48 && rect.left < safeRight + 48);
                if (!nodeIsVisible) continue;
                for (let index = 0; index < text.length && inspectedCharacters < 12000; index++) {
                  inspectedCharacters++;
                  if (/\s/.test(text[index])) continue;
                  const range = document.createRange();
                  range.setStart(node, index);
                  range.setEnd(node, index + 1);
                  for (const rect of range.getClientRects()) {
                    if (rect.bottom <= 0 || rect.top >= el.clientHeight) continue;
                    if (rect.right <= safeLeft + tolerance || rect.left >= safeRight - tolerance) continue;
                    glyphCount++;
                    minGlyphLeft = Math.min(minGlyphLeft, rect.left);
                    maxGlyphRight = Math.max(maxGlyphRight, rect.right);
                    let band = null;
                    let bestDistance = Number.POSITIVE_INFINITY;
                    for (const candidate of columnBands) {
                      const distance = Math.abs(candidate.left - rect.left);
                      if (distance < bestDistance) {
                        bestDistance = distance;
                        band = candidate;
                      }
                    }
                    if (!band || bestDistance > columnMergeTolerance) {
                      band = { left: rect.left, top: rect.top, bottom: rect.bottom };
                      columnBands.push(band);
                      columnLefts.push(band.left);
                    } else {
                      band.top = Math.min(band.top, rect.top);
                      band.bottom = Math.max(band.bottom, rect.bottom);
                    }
                    // Kinsoku lets a column's final closing mark (or an
                    // interval dot at the top edge) hang into the page margin
                    // instead of starting the next column. That overhang is
                    // correct vertical typography — the continuous-flow model
                    // deliberately kept those glyphs visible — so classify a
                    // small punctuation overhang as a hang, not an overflow.
                    // Letters, digits and deep non-punctuation intrusions are
                    // still real overflows: an atomic sideways run escaped
                    // the page band.
                    const hangAllowance = Math.max(6, bodyFontSize * 1.3);
                    const isHangPunctuation = character =>
                      /[.,:;!?、。，．：；？！…—―·‥〉》」』）〕］｝〟’”~～﹀﹂﹄〕〗〙〛@#%&*_+=]/.test(character);
                    if (rect.top < contentTop - bandTolerance
                        || rect.bottom > contentBottom + bandTolerance) {
                      const hangsTop = rect.top < contentTop - bandTolerance
                        && contentTop - rect.top <= hangAllowance
                        && isHangPunctuation(text[index]);
                      const hangsBottom = rect.bottom > contentBottom + bandTolerance
                        && rect.bottom - contentBottom <= hangAllowance
                        && isHangPunctuation(text[index]);
                      if (rect.top < contentTop - bandTolerance) {
                        if (hangsTop) hangingPunctuationCount++; else overflowTopCount++;
                      }
                      if (rect.bottom > contentBottom + bandTolerance) {
                        if (hangsBottom) hangingPunctuationCount++; else overflowBottomCount++;
                      }
                      if (!hangsTop && !hangsBottom && overflowGlyphs.length < 12) {
                        overflowGlyphs.push({
                          glyph: text[index],
                          left: rect.left,
                          right: rect.right,
                          top: rect.top,
                          bottom: rect.bottom
                        });
                      }
                      if ((hangsTop || hangsBottom) && hangingGlyphs.length < 12) {
                        hangingGlyphs.push({
                          glyph: text[index],
                          top: rect.top,
                          bottom: rect.bottom
                        });
                      }
                    }
                    if (rect.left < safeLeft - tolerance || rect.right > safeRight + tolerance) {
                      partialGlyphs.push({
                        glyph: text[index],
                        left: rect.left,
                        right: rect.right,
                        top: rect.top,
                        bottom: rect.bottom
                      });
                    }
                  }
                }
                if (inspectedCharacters >= 12000) break;
              }
              for (const media of body.querySelectorAll('img, svg, canvas, video, table')) {
                const rect = media.getBoundingClientRect();
                if (rect.width <= 0 || rect.height <= 0) continue;
                if (rect.bottom <= 0 || rect.top >= el.clientHeight) continue;
                if (rect.right <= safeLeft + tolerance || rect.left >= safeRight - tolerance) continue;
                visibleMedia.push({
                  tag: media.tagName,
                  left: rect.left,
                  right: rect.right,
                  top: rect.top,
                  bottom: rect.bottom
                });
              }
              // A column whose text starts well below the top of the band and
              // ends flush against its bottom is the signature of a bottom
              // aligned inline direction (`direction: rtl` under vertical-rl).
              // Columns that merely share space with an image are excluded:
              // there the text legitimately starts below the picture.
              let bottomAlignedColumnCount = 0;
              const bottomAlignedColumns = [];
              for (const band of columnBands) {
                if (band.top <= contentTop + bodyFontSize * 3) continue;
                if (band.bottom < contentBottom - bodyFontSize * 0.5) continue;
                if (visibleMedia.some(item =>
                    item.right > band.left - 1 && item.left < band.left + bodyFontSize * 2)) continue;
                bottomAlignedColumnCount++;
                if (bottomAlignedColumns.length < 8)
                  bottomAlignedColumns.push({ left: band.left, top: band.top, bottom: band.bottom });
              }
              // Reading direction is not a geometric detail: under vertical-rl
              // `direction` selects the inline axis, so rtl silently bottom
              // aligns every partially filled line. Report the computed value
              // of the body and of a real text block rather than trusting the
              // stylesheet to still say ltr.
              const textBlock = Array.from(body.querySelectorAll('p, li, blockquote, div'))
                .find(node => (node.textContent || '').trim().length > 0);
              const blockDirection = textBlock ? getComputedStyle(textBlock).direction : '';
              return JSON.stringify({
                viewport,
                scrollLeft: el.scrollLeft || 0,
                scrollWidth: el.scrollWidth || 0,
                pageStep,
                originShift,
                contentShift,
                safeLeft,
                safeRight,
                nominalSafeLeft,
                nominalSafeRight,
                leftMargin: safeLeft,
                rightMargin: viewport - safeRight,
                marginDelta: Math.abs(nominalSafeLeft - (viewport - nominalSafeRight)),
                maskMarginDelta: Math.abs(safeLeft - (viewport - safeRight)),
                lineHeight: parseFloat(bodyStyle.lineHeight) || 0,
                paddingLeft: parseFloat(bodyStyle.paddingLeft) || 0,
                paddingRight: parseFloat(bodyStyle.paddingRight) || 0,
                pageIndex: pageStep > 0 ? Math.round(Math.abs(el.scrollLeft || 0) / pageStep) : 0,
                minGlyphLeft: Number.isFinite(minGlyphLeft) ? minGlyphLeft : null,
                maxGlyphRight: Number.isFinite(maxGlyphRight) ? maxGlyphRight : null,
                columnCount: columnLefts.length,
                columnLefts: columnLefts.sort((a, b) => a - b).slice(0, 80),
                glyphCount,
                visibleMediaCount: visibleMedia.length,
                visibleMedia: visibleMedia.slice(0, 12),
                contentTop,
                contentBottom,
                direction: bodyStyle.direction || '',
                blockDirection,
                overflowTopCount,
                overflowBottomCount,
                hangingPunctuationCount,
                hangingGlyphs,
                overflowGlyphs,
                bottomAlignedColumnCount,
                bottomAlignedColumns,
                inspectedCharacters,
                partialGlyphCount: partialGlyphs.length,
                partialGlyphs: partialGlyphs.slice(0, 12)
              });
            })();
            """);
        var raw = DecodeReaderScriptString(result) ?? result ?? "{}";
        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidOperationException("Kreader vertical edge diagnostic returned empty result.");
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }

    private async Task<JsonElement> ReadKreaderNativeVerticalInlineDiagnosticsAsync(IReaderHost host)
    {
        var result = await host.InvokeScriptAsync(
            """
            (() => {
              const body = document.body;
              if (!body) return null;
              const tcyRuns = Array.from(body.querySelectorAll(
                '.kkindle-tcy[data-kkindle-vertical-run="1"]'));
              const nativeDigitRuns = Array.from(body.querySelectorAll(
                '.kkindle-native-vertical-digits[data-kkindle-native-vertical-digits="1"]'));
              const nativeDigits = Array.from(body.querySelectorAll(
                '.kkindle-native-vertical-digit[data-kkindle-native-vertical-digit="1"]'));
              const nativeFootnotes = Array.from(body.querySelectorAll(
                '.kkindle-native-vertical-footnote[data-kkindle-native-vertical-footnote="1"]'));
              const linuxNumberRuns = Array.from(body.querySelectorAll(
                '.kkindle-linux-vertical-number[data-kkindle-linux-vertical-number="1"]'));
              const linuxSingles = Array.from(body.querySelectorAll(
                '.kkindle-linux-vertical-single[data-kkindle-linux-vertical-single="1"]'));
              const linuxTcyRuns = Array.from(body.querySelectorAll(
                '.kkindle-linux-vertical-tcy[data-kkindle-linux-vertical-tcy="1"]'));
              const cjkSeparatedNumbers = Array.from(body.querySelectorAll(
                '.kkindle-cjk-before-number'));
              const legacySelector = [
                '.kkindle-vertical-digit',
                '.kkindle-vertical-number',
                '.kkindle-vertical-latin',
                '.kkindle-vertical-punctuation',
                '.kkindle-cell-inner',
                '.kkindle-tcy-inner'
              ].join(',');
              const legacy = Array.from(body.querySelectorAll(legacySelector));
              let linuxNumericStyleErrorCount = 0;
              let cjkNumberSpacingErrorCount = 0;
              let syntheticLinuxNumberLayoutCount = 0;
              for (const run of [...linuxSingles, ...linuxTcyRuns, ...linuxNumberRuns]) {
                const style = getComputedStyle(run);
                const isTcy = run.classList.contains('kkindle-linux-vertical-tcy');
                const isSingle = run.classList.contains('kkindle-linux-vertical-single');
                // Compatibility cells must share the parent's vertical-rl
                // flow. The historical horizontal-tb cells were orthogonal
                // atomic boxes and WebKitGTK sized their line box to the
                // 1em cell, collapsing the column pitch and clipping the
                // chapter — the geometric pitch guard in
                // RequireKreaderVerticalFlowInvariants watches for a
                // regression of that bug.
                if (style.writingMode !== 'vertical-rl')
                  linuxNumericStyleErrorCount++;
                if (style.position === 'absolute'
                    || (style.transform && style.transform !== 'none'))
                  syntheticLinuxNumberLayoutCount++;
                if (isSingle) {
                  const rect = run.getBoundingClientRect();
                  const fontSize = parseFloat(style.fontSize) || 0;
                  if (!(fontSize > 0)
                      || Math.abs(rect.width - fontSize) > 1.05
                      || Math.abs(rect.height - fontSize) > 1.05)
                  linuxNumericStyleErrorCount++;
                }
              }
              for (const run of cjkSeparatedNumbers) {
                const style = getComputedStyle(run);
                // Only the inline axis (top/bottom in vertical-rl) must stay
                // margin-free: a front or back gap at the Han→number boundary
                // moves the number off the shared vertical rhythm. The block
                // axis now carries the deliberate symmetric pitch margin that
                // keeps the cell's line-box extent on the paragraph grid.
                const margins = [
                  style.marginTop,
                  style.marginBottom,
                  style.marginInlineStart,
                  style.marginInlineEnd
                ].map(value => Math.abs(parseFloat(value) || 0));
                if (margins.some(margin => margin > 1.05))
                  cjkNumberSpacingErrorCount++;
              }
              let syntheticLayoutCount = 0;
              for (const node of legacy) {
                const style = getComputedStyle(node);
                if (style.display !== 'contents'
                    || style.position === 'absolute'
                    || (style.transform && style.transform !== 'none'))
                  syntheticLayoutCount++;
              }

              let digitCellStyleErrorCount = 0;
              let digitCellSizeErrorCount = 0;
              let syntheticNativeDigitLayoutCount = 0;
              for (const digit of nativeDigits) {
                const style = getComputedStyle(digit);
                const rect = digit.getBoundingClientRect();
                const fontSize = parseFloat(style.fontSize) || 0;
                if (style.display !== 'inline-block'
                    || style.writingMode !== 'horizontal-tb')
                  digitCellStyleErrorCount++;
                if (!(fontSize > 0)
                    || Math.abs(rect.width - fontSize) > 1.05
                    || Math.abs(rect.height - fontSize) > 1.05)
                  digitCellSizeErrorCount++;
                if (style.position === 'absolute'
                    || (style.transform && style.transform !== 'none'))
                  syntheticNativeDigitLayoutCount++;
              }

              let digitOverlapCount = 0;
              let maxDigitOverlap = 0;
              for (const run of nativeDigitRuns) {
                const digits = Array.from(run.querySelectorAll(
                  ':scope > .kkindle-native-vertical-digit'));
                for (let index = 1; index < digits.length; index++) {
                  const previous = digits[index - 1].getBoundingClientRect();
                  const current = digits[index].getBoundingClientRect();
                  const previousCenter = previous.left + previous.width / 2;
                  const currentCenter = current.left + current.width / 2;
                  const sameColumn = Math.abs(previousCenter - currentCenter)
                    <= Math.max(previous.width, current.width) * 0.75;
                  if (!sameColumn) continue;
                  const overlap = previous.bottom - current.top;
                  if (overlap <= 1.05) continue;
                  digitOverlapCount++;
                  maxDigitOverlap = Math.max(maxDigitOverlap, overlap);
                }
              }

              let footnoteCombineStyleErrorCount = 0;
              let syntheticFootnoteLayoutCount = 0;
              for (const footnote of nativeFootnotes) {
                const style = getComputedStyle(footnote);
                if (style.textCombineUpright !== 'all')
                  footnoteCombineStyleErrorCount++;
                if (style.position === 'absolute'
                    || (style.transform && style.transform !== 'none'))
                  syntheticFootnoteLayoutCount++;
              }
              syntheticNativeDigitLayoutCount += syntheticFootnoteLayoutCount;

              let tcyStyleErrorCount = 0;
              let minTcyInlineRatio = Number.POSITIVE_INFINITY;
              let maxTcyInlineRatio = 0;
              for (const run of tcyRuns) {
                const style = getComputedStyle(run);
                const rect = run.getBoundingClientRect();
                const fontSize = parseFloat(style.fontSize) || 0;
                if (style.textCombineUpright !== 'all') tcyStyleErrorCount++;
                if (fontSize > 0) {
                  const ratio = rect.height / fontSize;
                  minTcyInlineRatio = Math.min(minTcyInlineRatio, ratio);
                  maxTcyInlineRatio = Math.max(maxTcyInlineRatio, ratio);
                }
              }

              const units = [];
              const seenTcy = new Set();
              const generatedSelector = [
                '.kkindle-linux-vertical-single',
                '.kkindle-linux-vertical-single-punctuation',
                '.kkindle-linux-vertical-tcy',
                '.kkindle-linux-vertical-number',
                '.kkindle-linux-vertical-cjk',
                '.kkindle-linux-vertical-pair-punctuation'
              ].join(',');
              const walker = document.createTreeWalker(
                body,
                NodeFilter.SHOW_ELEMENT | NodeFilter.SHOW_TEXT);
              const classify = character => {
                if (/[A-Za-z]/.test(character)) return 'latin';
                if (/[0-9]/.test(character)) return 'digit';
                if (/[!"#$%&'()*+,\-./:;<=>?@[\\\]^_`{|}~，。！？、；：“”‘’（）《》〈〉【】…—]/.test(character))
                  return 'punctuation';
                if (/[⺀-鿿豈-﫿]/.test(character)) return 'cjk';
                return 'other';
              };
              let latinCount = 0;
              let digitCount = 0;
              let punctuationCount = 0;
              for (let node = walker.nextNode(); node; node = walker.nextNode()) {
                if (node.nodeType === Node.ELEMENT_NODE) {
                  if (node.matches?.('.kkindle-tcy[data-kkindle-vertical-run="1"]')
                      && !seenTcy.has(node)) {
                    seenTcy.add(node);
                    const rect = node.getBoundingClientRect();
                    units.push({ category: 'digit', text: node.textContent || '', rect });
                    digitCount += (node.textContent || '').length;
                    continue;
                  }
                  if (node.matches?.(generatedSelector)) {
                    const rect = node.getBoundingClientRect();
                    if (rect.width > 0 && rect.height > 0) {
                      const text = node.textContent || '';
                      const category = node.matches(
                        '.kkindle-linux-vertical-single-punctuation, '
                          + '.kkindle-linux-vertical-pair-punctuation')
                        ? 'punctuation'
                        : node.matches('.kkindle-linux-vertical-cjk')
                          ? 'cjk'
                        : 'digit';
                      units.push({ category, text, rect });
                      if (category === 'punctuation') punctuationCount++;
                      else digitCount += text.replace(/[^0-9]/g, '').length;
                    }
                    continue;
                  }
                  continue;
                }
                const parent = node.parentElement;
                if (!parent
                    || parent.closest('#kkindle-selection-bar, script, style, noscript')
                    || parent.closest('.kkindle-tcy[data-kkindle-vertical-run="1"], '
                      + '.kkindle-linux-vertical-footnote, ' + generatedSelector))
                  continue;
                const text = node.nodeValue || '';
                for (let index = 0; index < text.length; index++) {
                  const character = text[index];
                  if (/\s/.test(character)) continue;
                  const range = document.createRange();
                  range.setStart(node, index);
                  range.setEnd(node, index + 1);
                  const rect = range.getBoundingClientRect();
                  if (!(rect.width > 0) || !(rect.height > 0)) continue;
                  const category = classify(character);
                  if (category === 'latin') latinCount++;
                  else if (category === 'digit') digitCount++;
                  else if (category === 'punctuation') punctuationCount++;
                  units.push({ category, text: character, rect });
                }
              }

              let boundaryOverlapCount = 0;
              let maxBoundaryOverlap = 0;
              const overlapItems = [];
              for (let index = 1; index < units.length; index++) {
                const previous = units[index - 1];
                const current = units[index];
                if (previous.category === current.category) continue;
                if (!['latin', 'digit', 'punctuation', 'cjk'].includes(previous.category)
                    && !['latin', 'digit', 'punctuation', 'cjk'].includes(current.category))
                  continue;
                const previousCenter = previous.rect.left + previous.rect.width / 2;
                const currentCenter = current.rect.left + current.rect.width / 2;
                const sameColumn = Math.abs(previousCenter - currentCenter)
                  <= Math.max(previous.rect.width, current.rect.width) * 0.75;
                if (!sameColumn) continue;
                // DOM order advances to the next vertical column after the
                // current column reaches its bottom. Its range therefore
                // starts near the top again; that is a column transition, not
                // two glyphs sharing the same inline space.
                if (current.rect.top + 1.05 < previous.rect.top) continue;
                const overlap = previous.rect.bottom - current.rect.top;
                // WebKitGTK exposes adjacent Range advance boxes with up to
                // one device pixel of shared edge after fractional layout is
                // rounded. Treat only overlap beyond that rounding allowance
                // as a real inline collision.
                if (overlap <= 1.05) continue;
                boundaryOverlapCount++;
                maxBoundaryOverlap = Math.max(maxBoundaryOverlap, overlap);
                if (overlapItems.length < 12) {
                  overlapItems.push({
                    previous: previous.text,
                    current: current.text,
                    overlap: +overlap.toFixed(2),
                    previousRect: [previous.rect.left, previous.rect.top, previous.rect.width, previous.rect.height].map(v => +v.toFixed(2)),
                    currentRect: [current.rect.left, current.rect.top, current.rect.width, current.rect.height].map(v => +v.toFixed(2))
                  });
                }
              }
              return JSON.stringify({
                preparedVersion: body.dataset.kkindleVerticalInlinePrepared || '',
                writingMode: getComputedStyle(body).writingMode,
                textOrientation: getComputedStyle(body).textOrientation,
                tcyCount: tcyRuns.length,
                nativeDigitRunCount: nativeDigitRuns.length,
                nativeDigitCount: nativeDigits.length,
                digitCellStyleErrorCount,
                digitCellSizeErrorCount,
                digitOverlapCount,
                maxDigitOverlap,
                nativeFootnoteCount: nativeFootnotes.length,
                linuxNumberRunCount: linuxNumberRuns.length,
                linuxSingleCount: linuxSingles.length,
                linuxTcyCount: linuxTcyRuns.length,
                cjkSeparatedNumberCount: cjkSeparatedNumbers.length,
                cjkNumberSpacingErrorCount,
                linuxNumericStyleErrorCount,
                syntheticLinuxNumberLayoutCount,
                footnoteCombineStyleErrorCount,
                syntheticNativeDigitLayoutCount,
                tcyStyleErrorCount,
                minTcyInlineRatio: Number.isFinite(minTcyInlineRatio) ? minTcyInlineRatio : 0,
                maxTcyInlineRatio,
                legacyWrapperCount: legacy.length,
                syntheticLayoutCount,
                latinCount,
                digitCount,
                punctuationCount,
                boundaryOverlapCount,
                maxBoundaryOverlap,
                overlapItems
              });
            })();
            """);
        var raw = DecodeReaderScriptString(result) ?? result ?? "{}";
        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidOperationException("Kreader native vertical inline diagnostic returned empty result.");
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }

    private async Task<JsonElement> ReadKreaderTcyDiagnosticsAsync(IReaderHost host)
    {
        var result = await host.InvokeScriptAsync(
            """
            (() => {
              const body = document.body;
              if (!body) return null;
              const tolerance = 0.8;
              const runs = Array.from(body.querySelectorAll(
                '.kkindle-tcy[data-kkindle-vertical-run="1"], .kkindle-tcy-all[data-kkindle-vertical-run="1"]'));
              let missingInnerCount = 0;
              let outsideCount = 0;
              let maxCenterDelta = 0;
              let minInnerHeightRatio = Number.POSITIVE_INFINITY;
              let minInnerWidthRatio = Number.POSITIVE_INFINITY;
              let maxCellSizeError = 0;
              const items = [];
              const outsideItems = [];
              for (const run of runs) {
                const inner = run.querySelector(':scope > .kkindle-tcy-inner');
                if (!inner) {
                  missingInnerCount++;
                  continue;
                }
                const outer = run.getBoundingClientRect();
                const ink = inner.getBoundingClientRect();
                const fontSize = parseFloat(getComputedStyle(run).fontSize) || 0;
                const centerDeltaX = Math.abs((outer.left + outer.width / 2) - (ink.left + ink.width / 2));
                const centerDeltaY = Math.abs((outer.top + outer.height / 2) - (ink.top + ink.height / 2));
                const centerDelta = Math.max(centerDeltaX, centerDeltaY);
                const innerHeightRatio = fontSize > 0 ? ink.height / fontSize : 0;
                const cellSizeError = fontSize > 0
                  ? Math.max(Math.abs(outer.width - fontSize), Math.abs(outer.height - fontSize))
                  : Number.POSITIVE_INFINITY;
                const outside = ink.left < outer.left - tolerance
                  || ink.right > outer.right + tolerance
                  || ink.top < outer.top - tolerance
                  || ink.bottom > outer.bottom + tolerance;
                if (outside) outsideCount++;
                if (outside && outsideItems.length < 16) {
                  outsideItems.push({
                    text: run.textContent,
                    className: run.className,
                    outer: [outer.left, outer.top, outer.width, outer.height].map(value => +value.toFixed(2)),
                    inner: [ink.left, ink.top, ink.width, ink.height].map(value => +value.toFixed(2)),
                    fontSize: +fontSize.toFixed(2),
                    fontWeight: getComputedStyle(inner).fontWeight,
                    fontStyle: getComputedStyle(inner).fontStyle
                  });
                }
                maxCenterDelta = Math.max(maxCenterDelta, centerDelta);
                minInnerHeightRatio = Math.min(minInnerHeightRatio, innerHeightRatio);
                minInnerWidthRatio = Math.min(
                  minInnerWidthRatio,
                  fontSize > 0 ? ink.width / fontSize : 0);
                maxCellSizeError = Math.max(maxCellSizeError, cellSizeError);
                if (items.length < 16) {
                  items.push({
                    text: run.textContent,
                    length: run.dataset.kkindleTcyLength || '',
                    outer: [outer.left, outer.top, outer.width, outer.height].map(value => +value.toFixed(2)),
                    inner: [ink.left, ink.top, ink.width, ink.height].map(value => +value.toFixed(2)),
                    fontSize: +fontSize.toFixed(2),
                    fontFamily: getComputedStyle(inner).fontFamily,
                    transform: getComputedStyle(inner).transform,
                    centerDelta: +centerDelta.toFixed(3),
                    innerHeightRatio: +innerHeightRatio.toFixed(3),
                    outside
                  });
                }
              }
              const threeDigitRuns = Array.from(body.querySelectorAll(
                '.kkindle-vertical-number[data-kkindle-vertical-run="1"]'))
                .filter(run => /^\d{3}$/.test(run.textContent || ''));
              // Single digits and ASCII punctuation cells must carry the
              // same centered inner box as tate-chu-yoko; baseline-positioned
              // ink drifts across the neighbouring CJK cell edge.
              const centeredCells = Array.from(body.querySelectorAll(
                '.kkindle-vertical-digit[data-kkindle-vertical-run="1"], '
                + '.kkindle-vertical-punctuation[data-kkindle-vertical-run="1"]'));
              let centeredMissingInnerCount = 0;
              let centeredOutsideCount = 0;
              let centeredMaxDelta = 0;
              let centeredDigitCount = 0;
              const centeredItems = [];
              for (const cell of centeredCells) {
                if (cell.classList.contains('kkindle-vertical-digit')) centeredDigitCount++;
                const inner = cell.querySelector(':scope > .kkindle-cell-inner');
                if (!inner) {
                  centeredMissingInnerCount++;
                  continue;
                }
                const outer = cell.getBoundingClientRect();
                const ink = inner.getBoundingClientRect();
                const dx = Math.abs((outer.left + outer.width / 2) - (ink.left + ink.width / 2));
                const dy = Math.abs((outer.top + outer.height / 2) - (ink.top + ink.height / 2));
                const delta = Math.max(dx, dy);
                centeredMaxDelta = Math.max(centeredMaxDelta, delta);
                const outside = ink.left < outer.left - tolerance
                  || ink.right > outer.right + tolerance
                  || ink.top < outer.top - tolerance
                  || ink.bottom > outer.bottom + tolerance;
                if (outside) {
                  centeredOutsideCount++;
                  if (centeredItems.length < 12) {
                    centeredItems.push({
                      text: cell.textContent,
                      className: cell.className,
                      outer: [outer.left, outer.top, outer.width, outer.height].map(v => +v.toFixed(2)),
                      inner: [ink.left, ink.top, ink.width, ink.height].map(v => +v.toFixed(2)),
                      delta: +delta.toFixed(3)
                    });
                  }
                }
              }
              let threeDigitOrientationErrorCount = 0;
              let minThreeDigitInlineRatio = Number.POSITIVE_INFINITY;
              let maxThreeDigitBlockRatio = 0;
              for (const run of threeDigitRuns) {
                const rect = run.getBoundingClientRect();
                const style = getComputedStyle(run);
                const fontSize = parseFloat(style.fontSize) || 0;
                if (style.textOrientation !== 'upright')
                  threeDigitOrientationErrorCount++;
                if (fontSize > 0) {
                  minThreeDigitInlineRatio = Math.min(
                    minThreeDigitInlineRatio,
                    rect.height / fontSize);
                  maxThreeDigitBlockRatio = Math.max(
                    maxThreeDigitBlockRatio,
                    rect.width / fontSize);
                }
              }
              return JSON.stringify({
                preparedVersion: body.dataset.kkindleVerticalInlinePrepared || '',
                count: runs.length,
                missingInnerCount,
                outsideCount,
                maxCenterDelta,
                minInnerHeightRatio: Number.isFinite(minInnerHeightRatio) ? minInnerHeightRatio : 0,
                minInnerWidthRatio: Number.isFinite(minInnerWidthRatio) ? minInnerWidthRatio : 0,
                maxCellSizeError,
                threeDigitCount: threeDigitRuns.length,
                threeDigitOrientationErrorCount,
                minThreeDigitInlineRatio: Number.isFinite(minThreeDigitInlineRatio)
                  ? minThreeDigitInlineRatio : 0,
                maxThreeDigitBlockRatio,
                centeredCellCount: centeredCells.length,
                centeredDigitCount,
                centeredMissingInnerCount,
                centeredOutsideCount,
                centeredMaxDelta,
                outsideItems,
                items,
                centeredItems
              });
            })();
            """);
        var raw = DecodeReaderScriptString(result) ?? result ?? "{}";
        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidOperationException("Kreader tate-chu-yoko diagnostic returned empty result.");
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }

    private static void RequireReaderBodyVisible(JsonElement metrics, string stage)
    {
        Require(metrics.GetProperty("bodyDisplay").GetString() != "none", $"EPUB body display hidden after {stage}");
        Require(metrics.GetProperty("bodyVisibility").GetString() == "visible", $"EPUB body visibility hidden after {stage}");
        Require(metrics.GetProperty("bodyOpacity").GetString() == "1", $"EPUB body opacity hidden after {stage}");
    }

    // The Linux reader has two candidate surfaces: the native webview slot and
    // the plain-text recovery overlay. The native surface paints over Avalonia
    // content, so showing both at once hides the overlay behind a blank
    // webview — exactly the "blank page after opening a book" failure.
    private void RequireLinuxVisibleReaderSurface(string stage)
    {
        if (!OperatingSystem.IsLinux()) return;
        var overlay = ReaderLinuxTextFallbackOverlay.IsVisible;
        var webview = ReaderActiveHostSlot.IsVisible;
        Require(
            overlay || webview,
            $"Linux visible reader surface missing after {stage}");
        Require(
            !(overlay && webview),
            $"Linux reader shows the webview over the text overlay after {stage}");
    }

    private async Task SaveKreaderValidationAnnotationAsync()
    {
        var card = _readerBookCard
            ?? throw new InvalidOperationException("reader book card missing");
        var file = _readerBookFile
            ?? throw new InvalidOperationException("reader book file missing");
        _ = _readerDocument
            ?? throw new InvalidOperationException("reader document missing");
        var chapterPath = GetReaderChapterPath()
            ?? throw new InvalidOperationException("reader chapter path missing");
        var annotation = new ReaderAnnotation
        {
            Id = Guid.NewGuid(),
            BookId = card.Book.Id,
            BookFileId = file.Id,
            ChapterPath = chapterPath,
            StartOffset = 0,
            EndOffset = 32,
            SelectedText = "Linux validation paragraph 001",
            Prefix = string.Empty,
            Suffix = "search-token-linux",
            Note = "Linux validation note",
            Color = "#000000",
            UnderlineStyle = "solid",
            CreatedAt = DateTimeOffset.UtcNow
        };
        await _readerData.SaveAnnotationAsync(annotation, ReaderToken);
        await RefreshReaderAnnotationsAsync(ReaderToken);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    /// <summary>
    /// Invariants that must hold on every vertical page whatever the content.
    /// The clipping and margin checks alone cannot see either of these: a page
    /// whose lines are all bottom aligned, or one whose over-long atomic run
    /// spills past the page bottom into the margin mask, both keep perfect
    /// left/right edges and symmetric margins.
    /// </summary>
    /// <param name="requireTopAlignedColumns">
    /// Only for content whose column layout is known. Real books legitimately
    /// place text below a figure or a centered heading, so the bottom-aligned
    /// column count is logged rather than asserted there.
    /// </param>
    private static void RequireKreaderVerticalFlowInvariants(
        JsonElement edge,
        string context,
        bool requireTopAlignedColumns)
    {
        // Under vertical-rl `direction` selects the inline axis, not the
        // column axis: rtl bottom aligns every partially filled line, the 2em
        // first-line indent included, while changing nothing the other
        // diagnostics measure.
        Require(
            edge.GetProperty("direction").GetString() == "ltr",
            $"{context}: body inline direction is not ltr, so partially filled columns "
            + "are bottom aligned: " + edge);
        var blockDirection = edge.GetProperty("blockDirection").GetString();
        Require(
            string.IsNullOrEmpty(blockDirection) || blockDirection == "ltr",
            $"{context}: a text block inherited a non-ltr inline direction: " + edge);
        Require(
            edge.GetProperty("overflowTopCount").GetInt32() == 0
            && edge.GetProperty("overflowBottomCount").GetInt32() == 0,
            $"{context}: glyphs are painted outside the column band, so an atomic "
            + "sideways run overflows the page: " + edge);
        // Column pitch regression guard. The WebKitGTK orthogonal-cell bug
        // collapsed every line box to the 1em cell height: the visible
        // signature is adjacent glyph columns pitched at roughly 0.55× the
        // configured line height. Ink-band edges of centred cells and heading
        // margins legitimately measure between 0.7× and 2× the grid, so only
        // a pitch below 0.7× is a collapse.
        var lineHeight = edge.GetProperty("lineHeight").GetDouble();
        var columnLefts = edge.GetProperty("columnLefts").EnumerateArray()
            .Select(value => value.GetDouble())
            .ToArray();
        if (lineHeight > 1 && columnLefts.Length > 2)
        {
            var collapsedPitches = 0;
            double? worstPitch = null;
            for (var index = 1; index < columnLefts.Length; index++)
            {
                var pitch = columnLefts[index] - columnLefts[index - 1];
                if (pitch <= 0.5 || pitch >= lineHeight * 0.7) continue;
                collapsedPitches++;
                worstPitch = worstPitch is { } worst ? Math.Min(worst, pitch) : pitch;
            }
            Require(
                collapsedPitches == 0,
                $"{context}: {collapsedPitches} column pitches collapsed below the "
                + $"{lineHeight:F1}px line grid (worst {worstPitch:F1}px) — the WebKitGTK "
                + "orthogonal-cell line-box regression returned: " + edge);
        }
        if (requireTopAlignedColumns)
        {
            Require(
                edge.GetProperty("bottomAlignedColumnCount").GetInt32() == 0,
                $"{context}: a column's text is pushed against the page bottom: " + edge);
        }
    }

    private static void CreateKreaderValidationEpub(string path)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        AddValidationZipEntry(archive, "META-INF/container.xml", """
            <?xml version="1.0"?>
            <container xmlns="urn:oasis:names:tc:opendocument:xmlns:container" version="1.0">
              <rootfiles><rootfile full-path="EPUB/package.opf" media-type="application/oebps-package+xml" /></rootfiles>
            </container>
            """);
        AddValidationZipEntry(archive, "EPUB/package.opf", """
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="book-id">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                <dc:identifier id="book-id">urn:uuid:1db98b52-2d1f-4d80-a3e9-kreaderlinux</dc:identifier>
                <dc:title>Linux Kreader Validation Long Numbers EPUB</dc:title>
                <dc:creator>Kkindle Validation</dc:creator>
                <dc:language>zh-CN</dc:language>
                <dc:description>Linux Kreader validation fixture</dc:description>
              </metadata>
              <manifest>
                <item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav" />
                <item id="one" href="chapter1.xhtml" media-type="application/xhtml+xml" />
                <item id="two" href="chapter2.xhtml" media-type="application/xhtml+xml" />
              </manifest>
              <spine><itemref idref="one" /><itemref idref="two" /></spine>
            </package>
            """);
        AddValidationZipEntry(archive, "EPUB/nav.xhtml", """
            <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops">
              <body><nav epub:type="toc"><ol>
                <li><a href="chapter1.xhtml">Chapter One</a></li>
                <li><a href="chapter2.xhtml">Chapter Two</a></li>
              </ol></nav></body>
            </html>
            """);
        AddValidationZipEntry(archive, "EPUB/chapter1.xhtml", BuildValidationChapter("Chapter One", "001", includeFootnote: true));
        AddValidationZipEntry(archive, "EPUB/chapter2.xhtml", BuildValidationChapter("Chapter Two", "201", includeFootnote: false));
    }

    private static string BuildValidationChapter(string title, string start, bool includeFootnote)
    {
        var builder = new StringBuilder();
        builder.AppendLine("""<html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops"><head><title>""" + title + """</title><style>.publication-tcy { text-combine-upright: all; -webkit-text-combine: horizontal; }</style></head><body>""");
        builder.Append("<h1>").Append(title).AppendLine("</h1>");
        if (includeFootnote)
        {
            builder.AppendLine("""<p>Linux validation paragraph 001 includes COPYRIGHT, ISBN, A1, 单数字7，出版物直排横书<span class="publication-tcy">12</span>，三位数200，search-token-linux and AI context linux.竖排长数字12345678901234567890不能断开。<a epub:type="noteref" role="doc-noteref" href="#footnote-1">[1]</a></p>""");
            builder.AppendLine("""<aside id="footnote-1" epub:type="footnote"><p>Footnote validation body for Linux WebKit.</p></aside>""");
        }
        builder.AppendLine("""<p class="vertical-fixture-sample">数字：单数字7，双位数12，三位数200。脚注<a epub:type="noteref" role="doc-noteref" href="#fixture-note">[12]</a>。缩写：DNA、FPGA、CPU、AI。中文标点：逗号，句号。问号？叹号！顿号、分号；冒号：双引号“甲”单引号‘乙’圆括号（丙）书名号《丁》〈戊〉方括号【己】〔庚〕［辛］花括号｛壬｝破折号——省略号……间隔号·。ASCII标点：!&quot;#$%&amp;'()*+,-./:;&lt;=&gt;?@[\]^_`{|}~。</p><aside id="fixture-note" epub:type="footnote"><p>Fixture footnote.</p></aside>""");
        var first = int.Parse(start);
        for (var i = 0; i < 90; i++)
        {
            builder.Append("<p>Linux validation paragraph ")
                .Append((first + i).ToString("000"))
                .Append(" keeps enough text for pagination, scrolling, 双位数12，三位数200，search-token-linux, and AI context linux validation inside WPE WebKit. ")
                .Append("The sentence repeats to create measurable layout width and height on Linux desktop. ")
                .Append("竖排正文验证：天地玄黄，宇宙洪荒；日月盈昃，辰宿列张。标点必须留在完整字列内，不能在页面两侧被裁切。</p>");
        }
        builder.AppendLine("</body></html>");
        return builder.ToString();
    }

    private static void AddValidationZipEntry(ZipArchive archive, string name, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

}
#endif
