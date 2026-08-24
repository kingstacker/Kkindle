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
                var importedBookId = externalImport.Items
                    .FirstOrDefault(item => item.Succeeded)?.Book?.Id;
                var externalCard = ViewModel.Books.First(card =>
                    (importedBookId is not null && card.Book.Id == importedBookId)
                    || card.Title.Contains(expectedTitle, StringComparison.OrdinalIgnoreCase)
                    || expectedTitle.Contains(card.Title, StringComparison.OrdinalIgnoreCase));
                var externalFile = externalCard.Book.Files.First(file =>
                    file.Format.Equals("epub", StringComparison.OrdinalIgnoreCase));
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

                var epubCard = ViewModel.Books.First(card => card.Title.Contains("Linux Kreader Validation", StringComparison.OrdinalIgnoreCase));
                var epubFile = epubCard.Book.Files.First(file => file.Format.Equals("epub", StringComparison.OrdinalIgnoreCase));
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

        await SetKreaderValidationLayoutAsync(flowMode: 1, twoPage: false, vertical: true);
        var verticalHost = CurrentReaderHost
            ?? throw new InvalidOperationException("vertical reader host missing");
        await verticalHost.InvokeScriptAsync(
            ReaderPaginationScripts.CreateChapterBoundaryScript(
                moveToEnd: false,
                horizontal: true,
                vertical: true));
        await verticalHost.InvokeScriptAsync(ReaderPaginationScripts.Snap(vertical: true));
        var verticalFirstPage = await ReadKreaderDomMetricsAsync(verticalHost);
        Require(verticalFirstPage.GetProperty("vertical").GetBoolean(), "vertical writing flag not applied");
        Require(
            verticalFirstPage.GetProperty("writingMode").GetString() == "vertical-rl",
            "vertical-rl writing mode not applied");
        Require(
            verticalFirstPage.GetProperty("scrollWidth").GetDouble()
                > verticalFirstPage.GetProperty("clientWidth").GetDouble(),
            "vertical pagination has no horizontal extent");
        var verticalFirstEdge = await ReadKreaderVerticalEdgeDiagnosticsAsync(verticalHost);
        Require(
            verticalFirstEdge.GetProperty("partialGlyphCount").GetInt32() == 0,
            "vertical first page clips a glyph column at the page edge: " + verticalFirstEdge);

        var verticalOffsetBeforeTurn = verticalFirstPage.GetProperty("scrollLeft").GetDouble();
        await TurnReaderPageAsync(1);
        await Task.Delay(250, ReaderToken);
        var verticalSecondPage = await ReadKreaderDomMetricsAsync(verticalHost);
        Require(
            verticalSecondPage.GetProperty("scrollLeft").GetDouble() < verticalOffsetBeforeTurn - 1,
            "vertical page turn did not advance through Chromium's negative scroll range");
        var verticalSecondEdge = await ReadKreaderVerticalEdgeDiagnosticsAsync(verticalHost);
        Require(
            verticalSecondEdge.GetProperty("partialGlyphCount").GetInt32() == 0,
            "vertical second page clips a glyph column at the page edge: " + verticalSecondEdge);
        await log.WriteLineAsync("PASS EPUB vertical pagination keeps complete glyph columns on both page edges");
        await log.WriteLineAsync("DEBUG vertical first page " + verticalFirstEdge);
        await log.WriteLineAsync("DEBUG vertical second page " + verticalSecondEdge);
        await PauseKreaderValidationAtVerticalPageAsync(log);

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

    private async Task ValidateExternalVerticalEpubAsync(TextWriter log)
    {
        Require(!_readerIsPdf, "external EPUB reader did not open as EPUB");
        var originalGlobalVerticalWriting = _appSettings.DefaultReaderLayout.VerticalWriting;
        var host = CurrentReaderHost
            ?? throw new InvalidOperationException("external EPUB reader host missing");
        await WaitForKreaderDocumentAsync(host);

        var targetChapter = 5;
        if (int.TryParse(
                Environment.GetEnvironmentVariable("KKINDLE_KREADER_VALIDATE_CHAPTER"),
                out var requestedChapter))
        {
            targetChapter = Math.Max(0, requestedChapter);
        }
        while (_readerChapterIndex < targetChapter)
            await MoveReaderChapterAsync(1);
        while (_readerChapterIndex > targetChapter)
            await MoveReaderChapterAsync(-1);

        await SetKreaderValidationLayoutAsync(flowMode: 1, twoPage: false, vertical: false);
        var horizontalHost = CurrentReaderHost
            ?? throw new InvalidOperationException("external horizontal reader host missing");
        var horizontalSelectionBar = await ShowAndReadKreaderSelectionBarAsync(horizontalHost);
        Require(
            horizontalSelectionBar.GetProperty("writingMode").GetString() == "horizontal-tb",
            "horizontal selection bar is not horizontal");
        await ClearKreaderValidationSelectionAsync(horizontalHost);

        await SetKreaderValidationLayoutAsync(flowMode: 1, twoPage: false, vertical: true);
        var verticalHost = CurrentReaderHost
            ?? throw new InvalidOperationException("external vertical reader host missing");
        await verticalHost.InvokeScriptAsync(
            ReaderPaginationScripts.CreateChapterBoundaryScript(
                moveToEnd: false,
                horizontal: true,
                vertical: true));
        await verticalHost.InvokeScriptAsync(ReaderPaginationScripts.Snap(vertical: true));

        var validateAssistantLayout = string.Equals(
            Environment.GetEnvironmentVariable("KKINDLE_KREADER_VALIDATE_ASSISTANT"),
            "1",
            StringComparison.Ordinal);
        if (validateAssistantLayout)
        {
            SetReaderTocMinimal(true);
            await Task.Delay(350, ReaderToken);
            Require(_readerTocMinimal, "external EPUB minimal TOC was not enabled");
            Require(ReaderTocCompactPanel.IsVisible, "external EPUB minimal TOC rail is hidden");
            await log.WriteLineAsync("PASS external EPUB minimal TOC layout enabled");
        }

        if (string.Equals(
                Environment.GetEnvironmentVariable("KKINDLE_KREADER_VALIDATE_MAXIMIZE"),
                "1",
                StringComparison.Ordinal))
        {
            var normalMetrics = await ReadKreaderDomMetricsAsync(verticalHost);
            var normalWidth = normalMetrics.GetProperty("clientWidth").GetDouble();
            WindowState = Avalonia.Controls.WindowState.Maximized;
            await Task.Delay(1200, ReaderToken);
            _ = await WaitForReaderViewportToMatchHostAsync(verticalHost, ReaderToken);
            await Task.Delay(300, ReaderToken);
            await verticalHost.InvokeScriptAsync(ReaderPaginationScripts.Snap(vertical: true));
            await Task.Delay(150, ReaderToken);
            var maximizedMetrics = await ReadKreaderDomMetricsAsync(verticalHost);
            var maximizedWidth = maximizedMetrics.GetProperty("clientWidth").GetDouble();
            Require(
                maximizedWidth > normalWidth + 100,
                $"external EPUB maximize did not enlarge viewport: normal={normalWidth:F1}, maximized={maximizedWidth:F1}");
            await log.WriteLineAsync(
                $"PASS external EPUB runtime maximize resized viewport {normalWidth:F1}px -> {maximizedWidth:F1}px");
        }

        if (validateAssistantLayout)
        {
            var beforeAssistant = await ReadKreaderDomMetricsAsync(verticalHost);
            var beforeAssistantWidth = beforeAssistant.GetProperty("clientWidth").GetDouble();
            ReaderAssistantToggleButton_Click(null, new Avalonia.Interactivity.RoutedEventArgs());
            await Task.Delay(900, ReaderToken);
            _ = await WaitForReaderViewportToMatchHostAsync(verticalHost, ReaderToken);
            await Task.Delay(300, ReaderToken);
            await verticalHost.InvokeScriptAsync(ReaderPaginationScripts.VerticalStepExpression);
            await verticalHost.InvokeScriptAsync(ReaderPaginationScripts.Snap(vertical: true));
            await Task.Delay(150, ReaderToken);
            var afterAssistant = await ReadKreaderDomMetricsAsync(verticalHost);
            var afterAssistantWidth = afterAssistant.GetProperty("clientWidth").GetDouble();
            Require(ReaderAssistantPanel.IsVisible, "external EPUB AI assistant panel did not open");
            Require(_readerTocMinimal, "opening AI assistant disabled minimal TOC");
            Require(
                afterAssistantWidth < beforeAssistantWidth - 250,
                $"external EPUB AI assistant did not narrow viewport: before={beforeAssistantWidth:F1}, after={afterAssistantWidth:F1}");
            await log.WriteLineAsync(
                $"PASS external EPUB minimal TOC + AI assistant resized viewport {beforeAssistantWidth:F1}px -> {afterAssistantWidth:F1}px");
        }

        var firstPage = await ReadKreaderDomMetricsAsync(verticalHost);
        var firstEdge = await ReadKreaderVerticalEdgeDiagnosticsAsync(verticalHost);
        Require(firstPage.GetProperty("vertical").GetBoolean(), "external vertical flag not applied");
        Require(
            firstPage.GetProperty("writingMode").GetString() == "vertical-rl",
            "external vertical-rl writing mode not applied");
        Require(
            firstEdge.GetProperty("partialGlyphCount").GetInt32() == 0,
            "external EPUB first page clips glyphs: " + firstEdge);
        Require(
            firstEdge.GetProperty("marginDelta").GetDouble() <= 0.1,
            "external EPUB first page margins are asymmetric: " + firstEdge);
        var verticalSelectionBar = await ShowAndReadKreaderSelectionBarAsync(verticalHost);
        Require(
            verticalSelectionBar.GetProperty("writingMode").GetString() == "horizontal-tb",
            "vertical selection bar inherited vertical writing mode: " + verticalSelectionBar);
        Require(
            verticalSelectionBar.GetProperty("signature").GetString()
                == horizontalSelectionBar.GetProperty("signature").GetString(),
            "vertical selection bar differs from horizontal: horizontal="
            + horizontalSelectionBar + "; vertical=" + verticalSelectionBar);
        await log.WriteLineAsync("PASS external EPUB first vertical page has no clipped glyphs");
        await log.WriteLineAsync("DEBUG external vertical first page " + firstEdge);
        await log.WriteLineAsync("PASS vertical selection bar matches horizontal selection bar");
        await log.WriteLineAsync("DEBUG horizontal selection bar " + horizontalSelectionBar);
        await log.WriteLineAsync("DEBUG vertical selection bar " + verticalSelectionBar);
        await ClearKreaderValidationSelectionAsync(verticalHost);

        var viewport = firstEdge.GetProperty("viewport").GetDouble();
        var pageStep = firstEdge.GetProperty("pageStep").GetDouble();
        var scrollWidth = firstEdge.GetProperty("scrollWidth").GetDouble();
        var rawMax = Math.Max(0, scrollWidth - viewport);
        var rawPageIndex = pageStep > 0 ? rawMax / pageStep : 0;
        var roundedPageIndex = Math.Round(rawPageIndex);
        var lastPageIndex = pageStep > 0
            ? Math.Max(
                0,
                Math.Abs(rawMax - (roundedPageIndex * pageStep)) <= 4
                    ? (int)roundedPageIndex
                    : (int)Math.Ceiling(rawPageIndex))
            : 0;
        var screenshotPageIndex = Math.Max(0, lastPageIndex - 1);
        if (int.TryParse(
                Environment.GetEnvironmentVariable("KKINDLE_KREADER_VALIDATE_VERTICAL_SCREENSHOT_PAGE"),
                out var requestedScreenshotPage))
        {
            screenshotPageIndex = Math.Clamp(requestedScreenshotPage, 0, lastPageIndex);
        }
        var maxScrollError = 0d;
        for (var pageIndex = 0; pageIndex <= lastPageIndex; pageIndex++)
        {
            var target = Math.Min(rawMax, pageIndex * pageStep);
            var targetText = target.ToString(System.Globalization.CultureInfo.InvariantCulture);
            await verticalHost.InvokeScriptAsync($$"""
                (() => {
                  const el = document.scrollingElement || document.documentElement;
                  window.scrollTo({ left: -{{targetText}}, top: 0, behavior: 'instant' });
                })();
                """);
            await Task.Delay(60, ReaderToken);
            await verticalHost.InvokeScriptAsync(ReaderPaginationScripts.VerticalStepExpression);
            await Task.Delay(40, ReaderToken);
            var pageMetrics = await ReadKreaderDomMetricsAsync(verticalHost);
            var edge = await ReadKreaderVerticalEdgeDiagnosticsAsync(verticalHost);
            var actual = Math.Abs(pageMetrics.GetProperty("scrollLeft").GetDouble());
            maxScrollError = Math.Max(maxScrollError, Math.Abs(actual - target));
            Require(
                edge.GetProperty("glyphCount").GetInt32() > 0,
                $"external EPUB page {pageIndex + 1}/{lastPageIndex + 1} diagnostic inspected no visible glyphs: " + edge);
            Require(
                edge.GetProperty("partialGlyphCount").GetInt32() == 0,
                $"external EPUB page {pageIndex + 1}/{lastPageIndex + 1} clips glyphs: " + edge);
            Require(
                edge.GetProperty("marginDelta").GetDouble() <= 0.1,
                $"external EPUB page {pageIndex + 1}/{lastPageIndex + 1} margins are asymmetric: " + edge);
            await log.WriteLineAsync(
                $"PASS external vertical page {pageIndex + 1}/{lastPageIndex + 1} "
                + $"scrollLeft={actual:F3} partialGlyphs=0 marginDelta={edge.GetProperty("marginDelta").GetDouble():F3}");
            if (pageIndex == screenshotPageIndex)
                await PauseKreaderValidationAtVerticalPageAsync(log);
        }
        await log.WriteLineAsync(
            $"PASS external EPUB all {lastPageIndex + 1} vertical pages have no clipped glyphs; maxScrollError={maxScrollError:F3}px");

        await verticalHost.InvokeScriptAsync(
            ReaderPaginationScripts.CreateChapterBoundaryScript(
                moveToEnd: false,
                horizontal: true,
                vertical: true));
        await verticalHost.InvokeScriptAsync(ReaderPaginationScripts.Snap(vertical: true));
        await Task.Delay(100, ReaderToken);
        for (var pageIndex = 0; pageIndex <= lastPageIndex; pageIndex++)
        {
            var edge = await ReadKreaderVerticalEdgeDiagnosticsAsync(verticalHost);
            Require(
                edge.GetProperty("glyphCount").GetInt32() > 0,
                $"actual page turn {pageIndex + 1}/{lastPageIndex + 1} inspected no visible glyphs: " + edge);
            Require(
                edge.GetProperty("partialGlyphCount").GetInt32() == 0,
                $"actual page turn {pageIndex + 1}/{lastPageIndex + 1} clips glyphs: " + edge);
            Require(
                edge.GetProperty("marginDelta").GetDouble() <= 0.1,
                $"actual page turn {pageIndex + 1}/{lastPageIndex + 1} margins are asymmetric: " + edge);
            if (pageIndex < lastPageIndex)
            {
                await TurnReaderPageAsync(1);
                await Task.Delay(100, ReaderToken);
            }
        }
        await log.WriteLineAsync(
            $"PASS application page-turn path keeps all {lastPageIndex + 1} vertical pages unclipped and symmetric");

        // Exercise the real bookshelf checkpoint, not just the restore script
        // in isolation. Return to the normal full-TOC width so reopening uses
        // the same viewport, turn to an interior page, close immediately, and
        // verify that opening the same BookFile lands on that exact page.
        if (validateAssistantLayout && ReaderAssistantPanel.IsVisible)
            ReaderAssistantToggleButton_Click(null, new Avalonia.Interactivity.RoutedEventArgs());
        SetReaderTocMinimal(false);
        await Task.Delay(900, ReaderToken);
        _ = await WaitForReaderViewportToMatchHostAsync(verticalHost, ReaderToken);
        await verticalHost.InvokeScriptAsync(
            ReaderPaginationScripts.CreateChapterBoundaryScript(
                moveToEnd: false,
                horizontal: true,
                vertical: true));
        await verticalHost.InvokeScriptAsync(ReaderPaginationScripts.Snap(vertical: true));
        var expectedPageIndex = Math.Min(2, lastPageIndex);
        for (var pageIndex = 0; pageIndex < expectedPageIndex; pageIndex++)
            await TurnReaderPageAsync(1);

        var beforeBookshelf = await ReadKreaderDomMetricsAsync(verticalHost);
        var beforeStepText = beforeBookshelf.GetProperty("verticalPageStep").GetString();
        Require(
            double.TryParse(
                beforeStepText?.Trim().TrimEnd('p', 'x'),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var beforeStep)
            && beforeStep > 0,
            "bookshelf restore validation has no vertical page step: " + beforeBookshelf);
        var beforePosition = Math.Abs(beforeBookshelf.GetProperty("scrollLeft").GetDouble());
        var beforePageIndex = (int)Math.Round(beforePosition / beforeStep);
        Require(
            beforePageIndex == expectedPageIndex,
            $"bookshelf restore setup reached page {beforePageIndex + 1}, expected {expectedPageIndex + 1}");

        var reopenCard = _readerBookCard
            ?? throw new InvalidOperationException("bookshelf restore validation book card missing");
        var reopenFile = _readerBookFile
            ?? throw new InvalidOperationException("bookshelf restore validation book file missing");
        var expectedChapterIndex = _readerChapterIndex;
        // Reopening a book intentionally follows the persisted global writing
        // direction. Mirror the real vertical-mode setting path before testing
        // the bookshelf round trip so the assertion compares like-for-like pages.
        await SaveGlobalReaderVerticalWritingAsync(true, CancellationToken.None);
        await CloseReaderAsync();
        await OpenBookAsync(reopenCard, reopenFile, restoreProgress: true);
        var reopenedHost = CurrentReaderHost
            ?? throw new InvalidOperationException("bookshelf restore validation reopened host missing");
        await WaitForKreaderDocumentAsync(reopenedHost);
        await Task.Delay(500, ReaderToken);
        var afterBookshelf = await ReadKreaderDomMetricsAsync(reopenedHost);
        await SaveGlobalReaderVerticalWritingAsync(originalGlobalVerticalWriting, CancellationToken.None);
        var afterStepText = afterBookshelf.GetProperty("verticalPageStep").GetString();
        Require(
            double.TryParse(
                afterStepText?.Trim().TrimEnd('p', 'x'),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var afterStep)
            && afterStep > 0,
            "reopened reader has no vertical page step: " + afterBookshelf);
        var afterPosition = Math.Abs(afterBookshelf.GetProperty("scrollLeft").GetDouble());
        var afterPageIndex = (int)Math.Round(afterPosition / afterStep);
        Require(
            _readerChapterIndex == expectedChapterIndex,
            $"bookshelf restore changed chapter {_readerChapterIndex}, expected {expectedChapterIndex}");
        Require(
            afterPageIndex == expectedPageIndex,
            $"bookshelf restore returned to page {afterPageIndex + 1}, expected {expectedPageIndex + 1}; before={beforeBookshelf}; after={afterBookshelf}");
        await log.WriteLineAsync(
            $"PASS return to bookshelf restored exact vertical page {afterPageIndex + 1}/{lastPageIndex + 1} "
            + $"in chapter {expectedChapterIndex}; before={beforePosition:F3}px after={afterPosition:F3}px");
    }

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
        var raw = DecodeReaderScriptString(result) ?? result;
        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidOperationException("selection bar metric script returned empty result.");
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
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
        var raw = DecodeReaderScriptString(result) ?? result;
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
              const partialGlyphs = [];
              let glyphCount = 0;
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
                    if (!columnLefts.some(value => Math.abs(value - rect.left) < 0.5))
                      columnLefts.push(rect.left);
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
                inspectedCharacters,
                partialGlyphCount: partialGlyphs.length,
                partialGlyphs: partialGlyphs.slice(0, 12)
              });
            })();
            """);
        var raw = DecodeReaderScriptString(result) ?? result;
        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidOperationException("Kreader vertical edge diagnostic returned empty result.");
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
