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
            WindowStartupLocation = Avalonia.Controls.WindowStartupLocation.CenterScreen;
            await Task.Delay(500);

            var assetRoot = Path.Combine(Path.GetTempPath(), "KkindleKreaderValidation", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(assetRoot);
            var epubPath = Path.Combine(assetRoot, "linux-kreader-validation.epub");
            CreateKreaderValidationEpub(epubPath);

            var importResult = await ViewModel.ImportAsync([epubPath], cancellationToken: _lifetimeCancellation.Token);
            Require(importResult.FailureCount == 0, $"import failed: {string.Join("; ", importResult.Items.Select(item => item.Message))}");
            await log.WriteLineAsync("PASS import EPUB");

            var epubCard = ViewModel.Books.First(card => card.Title.Contains("Linux Kreader Validation", StringComparison.OrdinalIgnoreCase));
            var epubFile = epubCard.Book.Files.First(file => file.Format.Equals("epub", StringComparison.OrdinalIgnoreCase));
            await OpenBookAsync(epubCard, epubFile);
            await ValidateCurrentEpubReaderAsync(log);
            await CloseReaderAsync();

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

    private async Task ValidateCurrentEpubReaderAsync(TextWriter log)
    {
        Require(!_readerIsPdf, "EPUB reader did not open as EPUB");
        var host = CurrentReaderHost
            ?? throw new InvalidOperationException("EPUB reader host missing");
        await WaitForKreaderDocumentAsync(host);

        var initial = await ReadKreaderDomMetricsAsync(host);
        Require(initial.GetProperty("ready").GetBoolean(), "EPUB bridge did not become ready");
        RequireReaderBodyVisible(initial, "initial render");
        RequireLinuxVisibleReaderSurface("initial render");
        Require(initial.GetProperty("text").GetString()?.Contains("Linux validation paragraph 001", StringComparison.Ordinal) == true, "EPUB text not rendered");
        Require(initial.GetProperty("selectionBar").GetBoolean(), "selection action bar missing");
        Require(initial.GetProperty("bookmarkCorner").GetBoolean(), "bookmark corner missing");
        Require(initial.GetProperty("footnoteLinks").GetInt32() > 0, "footnote link not detected");
        await log.WriteLineAsync("PASS EPUB initial render, bridge, footnote marker");
        await ValidateWindowsFootnoteHoverAndClickAsync(host, log);
        await ValidateWindowsSelectionHighlightHoverAsync(host, log);
        await log.WriteLineAsync(
            $"DEBUG surface initial: activeSlot={ReaderActiveHostSlot.IsVisible} overlay={ReaderLinuxTextFallbackOverlay.IsVisible}");
        await PauseKreaderValidationAtInitialPageAsync(log);

        await SetKreaderValidationLayoutAsync(flowMode: 1, twoPage: false);
        var paged = await ReadKreaderDomMetricsAsync(host);
        Require(paged.GetProperty("flowMode").GetInt32() == 1, "single-page flow mode not applied");
        RequireReaderBodyVisible(paged, "single-page layout");
        RequireLinuxVisibleReaderSurface("single-page layout");
        if (OperatingSystem.IsLinux())
            Require(paged.GetProperty("columnWidth").GetString()?.EndsWith("px", StringComparison.Ordinal) == true, "single-page WebKit column width was not resolved");
        else
            Require(paged.GetProperty("columnCount").GetString() == "1", "single-page column count is not 1");
        Require(paged.GetProperty("scrollWidth").GetDouble() > paged.GetProperty("clientWidth").GetDouble(), "single-page pagination has no horizontal extent");
        var fallbackOffsetBeforeTurn = _readerLinuxTextFallbackPageIndex;
        var offsetBeforeTurn = paged.GetProperty("scrollLeft").GetDouble();
        await TurnReaderPageAsync(1);
        await Task.Delay(250, ReaderToken);
        var pageTurned = await ReadKreaderDomMetricsAsync(host);
        RequireReaderBodyVisible(pageTurned, "single-page page turn");
        RequireLinuxVisibleReaderSurface("single-page page turn");
        if (IsLinuxReaderTextFallbackActive())
        {
            Require(
                _readerLinuxTextFallbackPageIndex > fallbackOffsetBeforeTurn,
                "Linux recovery-surface page turn did not advance");
        }
        else
        {
            Require(
                pageTurned.GetProperty("scrollLeft").GetDouble() > offsetBeforeTurn,
                "single-page page turn did not advance");
        }
        var domBefore = (await ReadKreaderDomMetricsAsync(host)).GetProperty("text").GetString() ?? "";
        await MoveReaderChapterAsync(1);
        await log.WriteLineAsync($"DEBUG dom before chapter move: {domBefore.Replace("\n"," ").Trim()[..60]}");
        var domAfter = (await ReadKreaderDomMetricsAsync(host)).GetProperty("text").GetString() ?? "";
        await log.WriteLineAsync($"DEBUG dom after chapter move:  {domAfter.Replace("\n"," ").Trim()[..60]}");
        await log.WriteLineAsync($"DEBUG host source: {CurrentReaderHost?.Source}");
        Require(_readerChapterIndex == 1, "EPUB next chapter navigation failed");
        RequireReaderBodyVisible(await ReadKreaderDomMetricsAsync(host), "next chapter navigation");
        RequireLinuxVisibleReaderSurface("next chapter navigation");
        await MoveReaderChapterAsync(-1);
        Require(_readerChapterIndex == 0, "EPUB previous chapter navigation failed");
        RequireReaderBodyVisible(await ReadKreaderDomMetricsAsync(host), "previous chapter navigation");
        RequireLinuxVisibleReaderSurface("previous chapter navigation");
        await log.WriteLineAsync("PASS EPUB single-page pagination and chapter navigation");
        await log.WriteLineAsync(
            $"DEBUG surface paged: activeSlot={ReaderActiveHostSlot.IsVisible} overlay={ReaderLinuxTextFallbackOverlay.IsVisible}");

        await SetKreaderValidationLayoutAsync(flowMode: 1, twoPage: true);
        var twoPage = await ReadKreaderDomMetricsAsync(host);
        Require(twoPage.GetProperty("twoPage").GetBoolean(), "two-page mode not applied");
        RequireReaderBodyVisible(twoPage, "two-page layout");
        RequireLinuxVisibleReaderSurface("two-page layout");
        if (OperatingSystem.IsLinux())
            Require(twoPage.GetProperty("columnWidth").GetString()?.EndsWith("px", StringComparison.Ordinal) == true, "two-page WebKit column width was not resolved");
        else
            Require(twoPage.GetProperty("columnCount").GetString() == "2", "two-page column count is not 2");
        await log.WriteLineAsync("PASS EPUB two-page layout");

        await SetKreaderValidationLayoutAsync(flowMode: 0, twoPage: false);
        var scroll = await ReadKreaderDomMetricsAsync(host);
        Require(scroll.GetProperty("flowMode").GetInt32() == 0, "scroll mode not applied");
        RequireReaderBodyVisible(scroll, "scroll layout");
        RequireLinuxVisibleReaderSurface("scroll layout");
        Require(scroll.GetProperty("scrollHeight").GetDouble() > scroll.GetProperty("clientHeight").GetDouble(), "scroll mode has no vertical extent");
        await log.WriteLineAsync("PASS EPUB continuous scroll layout");

        await SaveKreaderValidationAnnotationAsync();
        await ApplySavedAnnotationsAsync(host, ReaderToken);
        var annotationMetrics = await ReadKreaderDomMetricsAsync(host);
        RequireReaderBodyVisible(annotationMetrics, "annotation render");
        RequireLinuxVisibleReaderSurface("annotation render");
        Require(annotationMetrics.GetProperty("annotationMarks").GetInt32() > 0, "saved annotation did not render in EPUB");
        Require(ReaderAnnotations.Count > 0, "reader annotation list did not refresh");
        await log.WriteLineAsync("PASS EPUB annotation persistence and rendered highlight");

        ReaderInPageSearchBox.Text = "search-token-linux";
        var searchProbe = await host.InvokeScriptAsync("""
            (() => {
              try {
                const text = document.body?.textContent || '';
                return JSON.stringify({
                  length: text.length,
                  index: text.toLocaleLowerCase().indexOf('search-token-linux'),
                  sample: text.slice(0, 240)
                });
              } catch (error) {
                return JSON.stringify({ error: String(error) });
              }
            })();
            """);
        await log.WriteLineAsync("DEBUG search probe " + (DecodeReaderScriptString(searchProbe) ?? searchProbe ?? string.Empty));
        await ApplyReaderSearchAsync("search-token-linux", ++_readerSearchSequence);
        await log.WriteLineAsync($"DEBUG in-page search count {_readerSearchCount}");
        Require(_readerSearchCount > 0, "EPUB in-page search found no results");
        await RefreshReaderWholeSearchAsync("search-token-linux");
        Require(ReaderSearchResults.Count > 0, "EPUB whole-book search found no results");
        await _bookContent.EnsureIndexedAsync(_readerBookCard!.Book, _readerBookFile!, _readerDocument!, ReaderToken);
        var aiSources = await _readerData.SearchBookAsync(_readerBookCard.Book.Id, "AI context linux", 10, ReaderToken, exactPhraseOnly: true);
        Require(aiSources.Count > 0, "EPUB AI/search context index found no chunks");
        await log.WriteLineAsync("PASS EPUB in-page search, whole-book search, AI context index");
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

    private async Task SetKreaderValidationLayoutAsync(int flowMode, bool twoPage)
    {
        _readerLayout = NormalizeReaderLayoutForPlatform(_readerLayout with
        {
            FlowMode = flowMode,
            TwoPageMode = twoPage,
            VerticalWriting = false
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
                text: (body?.innerText || body?.textContent || '').slice(0, 4000),
                flowMode: Number(window.__kkindleReaderFlowMode || 0),
                twoPage: window.__kkindleReaderTwoPage === true,
                bodyDisplay: style?.display || '',
                bodyVisibility: style?.visibility || '',
                bodyOpacity: style?.opacity || '',
                columnCount: style?.columnCount || '',
                columnWidth: style?.columnWidth || '',
                columnGap: style?.columnGap || '',
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
        var raw = DecodeReaderScriptString(result) ?? result;
        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidOperationException("Kreader metric script returned empty result.");
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
                <dc:title>Linux Kreader Validation EPUB</dc:title>
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
        builder.AppendLine("""<html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops"><head><title>""" + title + "</title></head><body>");
        builder.Append("<h1>").Append(title).AppendLine("</h1>");
        if (includeFootnote)
        {
            builder.AppendLine("""<p>Linux validation paragraph 001 includes search-token-linux and AI context linux.<a epub:type="noteref" role="doc-noteref" href="#footnote-1">[1]</a></p>""");
            builder.AppendLine("""<aside id="footnote-1" epub:type="footnote"><p>Footnote validation body for Linux WebKit.</p></aside>""");
        }
        var first = int.Parse(start);
        for (var i = 0; i < 90; i++)
        {
            builder.Append("<p>Linux validation paragraph ")
                .Append((first + i).ToString("000"))
                .Append(" keeps enough text for pagination, scrolling, search-token-linux, and AI context linux validation inside WPE WebKit. ")
                .Append("The sentence repeats to create measurable layout width and height on Linux desktop.</p>");
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
