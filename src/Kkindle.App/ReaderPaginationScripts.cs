using System.Globalization;
using Kkindle.Core;

namespace Kkindle;

internal static class ReaderPaginationScripts
{
    public const string VerticalTypographyGridCss =
        // Calibre commonly wraps the chapter heading in one div and every
        // body paragraph in a second div. In vertical-rl those wrapper boxes
        // acquire their own fractional block-size (the real Knut chapter was
        // 16.3125px), shifting all following paragraph columns by half a line
        // even though each paragraph itself advances on the 34.56px grid.
        // Flatten only structural wrappers that contain block children; divs
        // that directly carry prose keep their paragraph boundary.
        "\nbody :where(div, section, article, main):not(#kkindle-selection-bar, #kkindle-selection-bar *):has(> :where(p, div, section, article, main, ul, ol, blockquote, pre, table, h1, h2, h3, h4, h5, h6)) { display: contents !important; }"
        + "\nbody :where(p, div, section, article, main, ul, ol, li, blockquote, pre, table, thead, tbody, tr, td, th, h1, h2, h3, h4, h5, h6):not(#kkindle-selection-bar, #kkindle-selection-bar *) { margin-block: 0 !important; padding-block: 0 !important; border-block-width: 0 !important; block-size: auto !important; min-block-size: 0 !important; max-block-size: none !important; }"
        + "\nbody :where(h1, h2, h3, h4, h5, h6):not(#kkindle-selection-bar, #kkindle-selection-bar *) { font-size: 1rem !important; line-height: inherit !important; margin-block: 1lh !important; padding-block: 0 !important; block-size: auto !important; min-block-size: 0 !important; max-block-size: none !important; }"
        // Calibre often serializes a chapter title as a plain first <p> with
        // nested large-font spans instead of a semantic heading. The host
        // marks that block after inspecting the chapter start. Treat it like
        // h1-h6 in vertical writing: publisher text-align:right otherwise
        // means inline-end (the bottom of the column), while an oversized
        // descendant glyph can cross the right page mask after a side panel
        // narrows the viewport.
        + "\nbody .kkindle-chapter-heading:not(#kkindle-selection-bar), body .kkindle-chapter-heading:not(#kkindle-selection-bar) * { font-size: 1rem !important; line-height: inherit !important; }"
        + "\nbody .kkindle-chapter-heading:not(#kkindle-selection-bar) { text-align: center !important; text-indent: 0 !important; font-weight: 700 !important; margin-block: 1lh !important; padding-block: 0 !important; block-size: auto !important; min-block-size: 0 !important; max-block-size: none !important; break-inside: avoid !important; }"
        // Superscript footnote wrappers shift along the physical X axis in
        // vertical writing. Their UA vertical-align can widen one line box and
        // permanently move every following column off the body glyph grid.
        + "\nbody :where(sup, sub), body a.kkindle-footnote-reference, body span.kkindle-footnote-marker { line-height: 0 !important; vertical-align: baseline !important; }"
        + "\nbody a.kkindle-footnote-reference :where(img, svg), body a[href*='footnote'] img, body a[href*='endnote'] img { margin: 0 !important; vertical-align: baseline !important; }";

    // Chromium lays out multicolumn pages against the scrolling element's
    // integer CSS-pixel width. Always read that live width at the point of
    // navigation; an Avalonia host width can be fractional and becomes stale
    // while native side panes are resizing the WebView.
    public const string PageStepExpression =
        "document.scrollingElement?.clientWidth"
        + " || document.documentElement.clientWidth"
        + " || window.innerWidth || window.visualViewport?.width || 0";

    // Keep vertical page boundaries on the text line grid inside the visible
    // left/right safe area. The expression also publishes the resolved step to
    // CSS so the fixed edge masks expose exactly one logical page: no clipped
    // half-column and no repeated column at the adjoining page boundary.
    public const string VerticalStepExpression =
        "(() => {"
        + " const root = document.scrollingElement || document.documentElement;"
        + " const body = document.body;"
        + " const viewport = root?.clientWidth || document.documentElement.clientWidth || window.innerWidth || window.visualViewport?.width || 0;"
        + " if (viewport > 0) document.documentElement.style.setProperty('--kkindle-vertical-viewport-width', viewport + 'px');"
        + " if (!body || viewport <= 0) return viewport;"
        + " const style = getComputedStyle(body);"
        + " const line = parseFloat(style.lineHeight) || 0;"
        + " const trailingKey = [location.href, viewport, line].join('|');"
        + " let trailingExtent = window.__kkindleVerticalTrailingKey === trailingKey"
        + "  ? Number(window.__kkindleVerticalTrailingExtent || 0) : 0;"
        + " if (window.__kkindleVerticalTrailingKey !== trailingKey) {"
        + "  document.documentElement.style.setProperty('--kkindle-vertical-trailing-extent', '0px');"
        + "  void body.offsetWidth;"
        + " }"
        + " const leftSide = Math.max(0, (parseFloat(style.paddingLeft) || 0) - trailingExtent);"
        + " const rightSide = parseFloat(style.paddingRight) || 0;"
        + " const sides = leftSide + rightSide;"
        + " const available = Math.max(1, viewport - sides);"
        + " const step = line > 0 ? Math.max(1, Math.ceil(available / line) * line) : available;"
        + " const resolvedStep = step;"
        // Chromium clamps vertical-rl scrolling at the natural content edge.
        // When that edge is between logical page boundaries, the final view
        // exposes columns through both masks. Extend only the scroll extent to
        // the next whole page; extra physical left padding carries no content
        // and extends the negative scroll range used by vertical-rl.
        + " if (window.__kkindleVerticalTrailingKey !== trailingKey) {"
        + "  const naturalMax = Math.max(0, root.scrollWidth - viewport);"
        + "  const alignedMax = naturalMax > 0 ? Math.ceil(Math.max(0, naturalMax - 0.5) / resolvedStep) * resolvedStep : 0;"
        + "  trailingExtent = Math.max(0, alignedMax - naturalMax);"
        + "  document.documentElement.style.setProperty('--kkindle-vertical-trailing-extent', trailingExtent + 'px');"
        + "  window.__kkindleVerticalTrailingKey = trailingKey;"
        + "  window.__kkindleVerticalTrailingExtent = trailingExtent;"
        + "  void body.offsetWidth;"
        + " }"
        + " const baseSide = (leftSide + rightSide) / 2;"
        + " const baseLeft = viewport - resolvedStep - baseSide;"
        + " const baseRight = viewport - baseSide;"
        + " const originShift = (viewport - baseLeft - baseRight) / 2;"
        + " const safeLeft = baseLeft + originShift;"
        + " const safeRight = baseRight + originShift;"
        // A whole-number line step preserves the grid phase but does not pick
        // the phase itself. Real Calibre chapters can place the first glyph
        // column half a line away from body padding, so both page masks cut
        // through the same column on adjoining pages. Inspect the rendered
        // glyph rectangles once per layout and translate both masks to the
        // nearest genuine inter-column gap; the page step remains unchanged.
        + " const pageIndex = Math.round(Math.abs(root.scrollLeft || 0) / resolvedStep);"
        + " const phaseKey = [location.href, viewport, resolvedStep, line, body.scrollWidth || 0, pageIndex].join('|');"
        + " let contentShift = Number(window.__kkindleVerticalContentShift || 0);"
        + " if (window.__kkindleVerticalOriginPhaseKey !== phaseKey) {"
        + "  contentShift = 0;"
        + "  document.documentElement.style.setProperty('--kkindle-vertical-content-shift', '0px');"
        + "  void body.offsetWidth;"
        + "  const tolerance = 0.75;"
        + "  const clearance = 1;"
        + "  const visibleRects = [];"
        + "  const candidates = [0];"
        + "  const walker = document.createTreeWalker(body, NodeFilter.SHOW_TEXT);"
        + "  let inspected = 0;"
        + "  for (let node = walker.nextNode(); node; node = walker.nextNode()) {"
        + "   const parent = node.parentElement;"
        + "   if (!parent || parent.closest('#kkindle-selection-bar, script, style, noscript')) continue;"
        + "   const text = node.nodeValue || '';"
        + "   const nodeRange = document.createRange();"
        + "   nodeRange.selectNodeContents(node);"
        + "   const nodeIsVisible = Array.from(nodeRange.getClientRects()).some(rect =>"
        + "    rect.bottom > 0 && rect.top < root.clientHeight"
        + "    && rect.right > safeLeft - line && rect.left < safeRight + line);"
        + "   if (!nodeIsVisible) continue;"
        + "   for (let index = 0; index < text.length && inspected < 12000; index++) {"
        + "    inspected++;"
        + "    if (/\\s/.test(text[index])) continue;"
        + "    const range = document.createRange();"
        + "    range.setStart(node, index); range.setEnd(node, index + 1);"
        + "    for (const rect of range.getClientRects()) {"
        + "     if (rect.bottom <= 0 || rect.top >= root.clientHeight) continue;"
        + "     visibleRects.push({ left: rect.left, right: rect.right });"
        + "     if (rect.left < safeLeft - tolerance && rect.right > safeLeft + tolerance) {"
        + "      candidates.push(safeLeft - rect.left + clearance, safeLeft - rect.right - clearance);"
        + "     }"
        + "     if (rect.left < safeRight - tolerance && rect.right > safeRight + tolerance) {"
        + "      candidates.push(safeRight - rect.left + clearance, safeRight - rect.right - clearance);"
        + "     }"
        + "    }"
        + "   }"
        + "   if (inspected >= 12000) break;"
        + "  }"
        + "  let bestCrossings = Number.MAX_SAFE_INTEGER;"
        + "  let bestMagnitude = Number.MAX_VALUE;"
        + "  for (const candidate of candidates) {"
        + "   if (!Number.isFinite(candidate) || candidate < -baseSide + 1 || candidate > baseSide - 1) continue;"
        + "   let crossings = 0;"
        + "   for (const rect of visibleRects) {"
        + "    const left = rect.left + candidate; const right = rect.right + candidate;"
        + "    if ((left < safeLeft - tolerance && right > safeLeft + tolerance)"
        + "      || (left < safeRight - tolerance && right > safeRight + tolerance)) crossings++;"
        + "   }"
        + "   const magnitude = Math.abs(candidate);"
        + "   if (crossings < bestCrossings || (crossings === bestCrossings && magnitude < bestMagnitude)) {"
        + "    bestCrossings = crossings; bestMagnitude = magnitude; contentShift = candidate;"
        + "   }"
        + "  }"
        + "  window.__kkindleVerticalOriginPhaseKey = phaseKey;"
        + "  window.__kkindleVerticalContentShift = contentShift;"
        // Keep the nominal page margins symmetric, but move an individual
        // mask outward when the current page has a wider glyph column crossing
        // that edge. A translation alone cannot solve pages that contain a
        // partial column at both edges; the mask must land in the next real
        // inter-column gap or the user sees a clipped half-glyph.
        + "  let maskLeft = safeLeft;"
        + "  let maskRight = safeRight;"
        + "  const maskRects = visibleRects.map(rect => ({ left: rect.left + contentShift, right: rect.right + contentShift }));"
        + "  for (let pass = 0; pass < 4; pass++) {"
        + "   let changed = false;"
        + "   for (const rect of maskRects) {"
        + "    if (rect.left < maskLeft - tolerance && rect.right > maskLeft + tolerance) { maskLeft = rect.right + clearance; changed = true; }"
        + "    if (rect.left < maskRight - tolerance && rect.right > maskRight + tolerance) { maskRight = rect.left - clearance; changed = true; }"
        + "   }"
        + "   if (!changed) break;"
        + "  }"
        + "  if (maskLeft < maskRight) {"
        + "   document.documentElement.style.setProperty('--kkindle-vertical-safe-left', maskLeft + 'px');"
        + "   document.documentElement.style.setProperty('--kkindle-vertical-safe-right', maskRight + 'px');"
        + "  } else {"
        + "   document.documentElement.style.setProperty('--kkindle-vertical-safe-left', safeLeft + 'px');"
        + "   document.documentElement.style.setProperty('--kkindle-vertical-safe-right', safeRight + 'px');"
        + "  }"
        // Keep the real WebKit edge-mask nodes in the same calibrated phase as
        // the CSS variables. Inline geometry is intentional here: WebKitGTK
        // can retain the first computed value of a max()/min() pseudo-element
        // while a negative vertical scroll page is settling.
        + "  const leftMask = document.getElementById('kkindle-vertical-edge-mask-left');"
        + "  const rightMask = document.getElementById('kkindle-vertical-edge-mask-right');"
        + "  if (leftMask && rightMask) {"
        + "   const finalLeft = maskLeft < maskRight ? maskLeft : safeLeft;"
        + "   const finalRight = maskLeft < maskRight ? maskRight : safeRight;"
        + "   leftMask.style.setProperty('width', Math.max(0, finalLeft) + 'px', 'important');"
        + "   rightMask.style.setProperty('left', Math.max(0, Math.min(viewport, finalRight)) + 'px', 'important');"
        + "  }"
        + " }"
        // Keep the visible window centered. Calibrate the rendered content
        // directly against those final safe edges. Using the uncentered base
        // edges here clipped wide chapter-title glyphs after a viewport resize.
        + " contentShift = Math.max(-baseSide + 1, Math.min(baseSide - 1, contentShift));"
        + " document.documentElement.style.setProperty('--kkindle-vertical-page-step', resolvedStep + 'px');"
        + " document.documentElement.style.setProperty('--kkindle-vertical-origin-shift', originShift + 'px');"
        + " document.documentElement.style.setProperty('--kkindle-vertical-content-shift', contentShift + 'px');"
        + " return resolvedStep;"
        + " })()";

    public static string CreateFlowCss(
        bool pagination,
        bool vertical,
        bool twoPage = false,
        double horizontalPadding = ReaderPaginationDefaults.HorizontalPadding,
        double maxContentWidth = ReaderLayoutDefaults.DefaultMaxWidth)
    {
        // Vertical writing has one supported geometry: one paginated column
        // per viewport. Defend this boundary even when a stale caller passes
        // the old scroll/two-page combination.
        if (vertical)
        {
            pagination = true;
            twoPage = false;
        }

        if (!pagination)
        {
            return OperatingSystem.IsLinux()
                ? "html { width: 100% !important; height: 100% !important; min-height: 0 !important; overflow-x: hidden !important; overflow-y: auto !important; writing-mode: horizontal-tb !important; } body { min-height: 100% !important; margin: 0 !important; overflow: visible !important; column-width: auto !important; column-count: auto !important; column-gap: normal !important; writing-mode: horizontal-tb !important; }"
                : "html, body { min-height: 100%; overflow-x: hidden !important; } body { column-width: auto !important; column-count: auto !important; column-gap: normal !important; writing-mode: horizontal-tb !important; }";
        }

        var topPadding = Format(ReaderPaginationDefaults.TopPadding);
        var safeHorizontalPadding = double.IsFinite(horizontalPadding)
            ? Math.Clamp(
                horizontalPadding,
                ReaderLayoutDefaults.MinBodyPadding,
                ReaderLayoutDefaults.MaxBodyPadding)
            : ReaderPaginationDefaults.HorizontalPadding;
        var safeMaxContentWidth = double.IsFinite(maxContentWidth)
            ? Math.Clamp(
                maxContentWidth,
                ReaderLayoutDefaults.MinMaxWidth,
                ReaderLayoutDefaults.MaxMaxWidth)
            : ReaderLayoutDefaults.DefaultMaxWidth;
        var horizontalPaddingCss = Format(safeHorizontalPadding);
        var maxContentWidthCss = Format(safeMaxContentWidth);
        var bottomPadding = Format(ReaderPaginationDefaults.BottomPadding);
        // The distance from one column start to the next must remain exactly
        // one viewport (or half a viewport in a two-page spread). Treat the
        // inter-column gap as the adjoining right + left page margins. Grow
        // that gap when the viewport is wider than the requested text width;
        // this centers a capped text column without changing the page step.
        // A fixed 68 px book preference consumes too much of the text column
        // when TOC and assistant panes are both open. Keep the configured
        // value as the upper bound, but reduce it responsively down to 24 px
        // as the actual WebView narrows.
        var responsiveSidePadding =
            $"min({horizontalPaddingCss}px, max({Format(ReaderLayoutDefaults.MinBodyPadding)}px, 5vw))";
        var minimumColumnGap =
            $"calc({responsiveSidePadding} + {responsiveSidePadding})";
        var columnGap = twoPage
            ? $"max({minimumColumnGap}, calc((100vw - {Format(safeMaxContentWidth * 2)}px) / 2))"
            : $"max({minimumColumnGap}, calc(100vw - {maxContentWidthCss}px))";
        // Pin the number of visible columns instead of asking Chromium to
        // infer it from a calculated column-width. Under WebView DPI scaling,
        // innerWidth can briefly differ from the CSS layout viewport; the
        // inferred two-page layout then collapses to one wide column. With an
        // explicit count, one page is always one column and a spread is always
        // two columns, while padding + gaps still total exactly one viewport.
        var columnCount = twoPage ? 2 : 1;
        if (vertical)
        {
            // A fixed inline size makes glyphs wrap from top to bottom. The
            // max-content block size then grows naturally toward the left as
            // the book adds vertical lines and paragraphs. The root exposes
            // that block overflow as a negative scrollLeft range.
            var linuxVerticalEdgeMaskCss = OperatingSystem.IsLinux()
                ? " #kkindle-vertical-edge-mask-left, #kkindle-vertical-edge-mask-right { position: fixed !important; display: block !important; z-index: 2147483647 !important; pointer-events: none !important; background: #FFFFFF !important; writing-mode: horizontal-tb !important; margin: 0 !important; padding: 0 !important; border: 0 !important; opacity: 1 !important; }"
                    + " #kkindle-vertical-edge-mask-left { left: 0 !important; top: 0 !important; bottom: 0 !important; width: max(calc(var(--kkindle-vertical-viewport-width) - var(--kkindle-vertical-page-step) - var(--kkindle-vertical-page-side) + var(--kkindle-vertical-origin-shift)), var(--kkindle-vertical-safe-left)) !important; }"
                    + " #kkindle-vertical-edge-mask-right { left: min(calc(var(--kkindle-vertical-viewport-width) - var(--kkindle-vertical-page-side) + var(--kkindle-vertical-origin-shift)), var(--kkindle-vertical-safe-right)) !important; right: 0 !important; top: 0 !important; bottom: 0 !important; }"
                : string.Empty;
            return $"html {{ --kkindle-vertical-viewport-width: 100%; --kkindle-vertical-page-side: {responsiveSidePadding}; --kkindle-vertical-page-top: {topPadding}px; --kkindle-vertical-page-bottom: {bottomPadding}px; --kkindle-vertical-page-step: calc(var(--kkindle-vertical-viewport-width) - var(--kkindle-vertical-page-side) - var(--kkindle-vertical-page-side)); --kkindle-vertical-origin-shift: 0px; --kkindle-vertical-content-shift: 0px; --kkindle-vertical-trailing-extent: 0px; --kkindle-vertical-safe-left: 0px; --kkindle-vertical-safe-right: 100000px; width: 100%; height: 100%; overflow: hidden !important; writing-mode: vertical-rl !important; text-orientation: mixed !important; }}"
                + $" body {{ width: max-content !important; min-width: 100% !important; height: 100% !important; min-height: 0 !important; margin: 0 !important; overflow: visible !important;"
                + $" padding: var(--kkindle-vertical-page-top) calc(var(--kkindle-vertical-page-side) - var(--kkindle-vertical-content-shift)) var(--kkindle-vertical-page-bottom) calc(var(--kkindle-vertical-page-side) + var(--kkindle-vertical-content-shift) + var(--kkindle-vertical-trailing-extent)) !important; box-sizing: border-box !important;"
                + $" writing-mode: vertical-rl !important; text-orientation: mixed !important;"
                + $" column-width: auto !important; column-gap: normal !important; column-fill: balance !important; column-count: auto !important;"
                + $" max-width: none !important; max-height: 100% !important; }}"
                // Natural vertical flow is continuous, so a viewport edge can
                // otherwise bisect a glyph column. Fixed masks create the four
                // page margins; the left mask absorbs the sub-line remainder
                // left after snapping the page step to a whole line grid.
                + $" html::before, html::after, body::before, body::after {{ content: \"\" !important; position: fixed !important; display: block !important; pointer-events: none !important; z-index: 2147483000 !important; background: #FFFFFF !important; margin: 0 !important; padding: 0 !important; }}"
                + $" html::before {{ left: 0 !important; top: 0 !important; bottom: 0 !important; width: max(calc(var(--kkindle-vertical-viewport-width) - var(--kkindle-vertical-page-step) - var(--kkindle-vertical-page-side) + var(--kkindle-vertical-origin-shift)), var(--kkindle-vertical-safe-left)) !important; }}"
                + $" html::after {{ left: min(calc(var(--kkindle-vertical-viewport-width) - var(--kkindle-vertical-page-side) + var(--kkindle-vertical-origin-shift)), var(--kkindle-vertical-safe-right)) !important; right: 0 !important; top: 0 !important; bottom: 0 !important; }}"
                + $" body::before {{ left: 0 !important; right: 0 !important; top: 0 !important; height: var(--kkindle-vertical-page-top) !important; }}"
                + $" body::after {{ left: 0 !important; right: 0 !important; bottom: 0 !important; height: var(--kkindle-vertical-page-bottom) !important; }}"
                // WebKitGTK can paint an html pseudo-element below the body
                // stacking context. Real fixed elements are used for the
                // horizontal edge masks; the host creates them once after
                // the document style is installed.
                + linuxVerticalEdgeMaskCss;
        }
        if (OperatingSystem.IsLinux())
        {
            var columnWidth = twoPage
                ? $"calc((100vw - (var(--kkindle-page-column-gap) * 2)) / 2)"
                : $"calc(100vw - var(--kkindle-page-column-gap))";
            return $"html {{ height: 100%; overflow: hidden !important; writing-mode: horizontal-tb !important; }}"
                + $" body {{ --kkindle-page-column-gap: {columnGap}; width: 100% !important; min-width: 0 !important; height: 100% !important; margin: 0 !important; overflow: visible !important; padding: {topPadding}px calc(var(--kkindle-page-column-gap) / 2) {bottomPadding}px !important; box-sizing: border-box !important;"
                + $" writing-mode: horizontal-tb !important; -webkit-column-width: {columnWidth} !important; column-width: {columnWidth} !important;"
                + $" -webkit-column-count: auto !important; column-count: auto !important;"
                + $" -webkit-column-gap: var(--kkindle-page-column-gap) !important; column-gap: var(--kkindle-page-column-gap) !important;"
                + $" -webkit-column-fill: auto !important; column-fill: auto !important; max-width: none !important; }}"
                + $" body::after {{ content: \"\"; display: block; height: 0.1px; width: calc(100% + var(--kkindle-page-column-gap) / 2); }}";
        }
        return $"html {{ height: 100%; overflow: hidden !important; writing-mode: horizontal-tb !important; }}"
            + $" body {{ --kkindle-page-column-gap: {columnGap}; width: 100% !important; min-width: 0 !important; height: 100% !important; margin: 0 !important; overflow: visible !important; padding: {topPadding}px calc(var(--kkindle-page-column-gap) / 2) {bottomPadding}px !important; box-sizing: border-box !important;"
            + $" writing-mode: horizontal-tb !important; column-width: auto !important; column-count: {columnCount} !important;"
            + $" column-gap: var(--kkindle-page-column-gap) !important; column-fill: auto !important; max-width: none !important; }}"
            // Chromium's scrollWidth for the overflowing multicolumns does not
            // include the body's right padding, so the maximum scroll position
            // lands with the LAST column's text flush against the viewport's
            // right edge. This invisible trailing block overflows its column by
            // exactly the half-gap, extending scrollWidth by that inset so the
            // final page can center its column like every other page.
            + $" body::after {{ content: \"\"; display: block; height: 0.1px; width: calc(100% + var(--kkindle-page-column-gap) / 2); }}";
    }

    // Page boundaries start at scroll origin 0; body padding stays inside
    // each viewport. The step is the live scrolling viewport width so native
    // side-pane and DPI changes cannot leave a stale page boundary behind.
    //
    // Vertical writing paginates along the same X axis, but Chromium anchors
    // vertical-rl overflow at the right edge and reports scrollLeft in a
    // NEGATIVE range (0 … -max). Every orientation-aware script therefore
    // measures the distance from the origin with abs(scrollLeft) and writes
    // the result back negated. Horizontal mode keeps the original math,
    // including the paddingRight trailing inset, unchanged.
    public static string Snap(bool vertical) => CreateSnapScript(vertical);

    private static string CreateSnapScript(bool vertical)
    {
        var tolerance = Format(ReaderPaginationDefaults.SnapTolerance);
        return $$"""
            (() => {
              const el = document.scrollingElement || document.documentElement;
              if (!el) return;
              const vertical = {{(vertical ? "true" : "false")}};
              const step = vertical ? {{VerticalStepExpression}} : {{PageStepExpression}};
              if (step <= 0) return;
              const rawMax = Math.max(0, el.scrollWidth - el.clientWidth);
              const trailingInset = parseFloat(getComputedStyle(document.body).paddingRight) || 0;
              // scrollWidth/clientWidth are integer-rounded, while scrollLeft
              // is device-pixel precise. Keep the logical page boundary here
              // and let Chromium clamp the request to its fractional maximum.
              const max = vertical
                ? rawMax
                : Math.max(0, Math.round(Math.max(0, rawMax - trailingInset) / step) * step);
              const distance = vertical ? Math.abs(el.scrollLeft || 0) : Math.max(0, el.scrollLeft || 0);
              const nearest = Math.round(distance / step) * step;
              const target = distance >= max - {{tolerance}}
                ? max
                : Math.max(0, Math.min(max, nearest));
              window.scrollTo({ left: vertical ? -target : target, top: 0, behavior: 'instant' });
              if (vertical) requestAnimationFrame(() => { {{VerticalStepExpression}}; });
            })();
            """;
    }

    public static string CreateTurnScript(int direction, bool smooth = false, bool vertical = false)
    {
        var safeDirection = direction < 0 ? -1 : 1;
        var tolerance = Format(ReaderPaginationDefaults.SnapTolerance);
        var behavior = smooth ? "smooth" : "instant";
        return $$"""
            (() => {
              const el = document.scrollingElement || document.documentElement;
              if (!el) return false;
              const vertical = {{(vertical ? "true" : "false")}};
              const step = vertical ? {{VerticalStepExpression}} : {{PageStepExpression}};
              if (step <= 0) return false;
              const rawMax = Math.max(0, el.scrollWidth - el.clientWidth);
              const trailingInset = parseFloat(getComputedStyle(document.body).paddingRight) || 0;
              const max = vertical
                ? rawMax
                : Math.max(0, Math.round(Math.max(0, rawMax - trailingInset) / step) * step);
              const distance = vertical ? Math.abs(el.scrollLeft || 0) : Math.max(0, el.scrollLeft || 0);
              const nearest = Math.round(distance / step) * step;
              const current = distance >= max - 4
                ? max
                : Math.max(0, Math.min(max, nearest));
              if ({{safeDirection}} < 0 && current <= {{tolerance}}) return false;
              if ({{safeDirection}} > 0 && current >= max - {{tolerance}}) return false;
              const target = Math.max(
                0,
                Math.min(max, current + ({{safeDirection}} < 0 ? -step : step)));
              window.scrollTo({ left: vertical ? -target : target, top: 0, behavior: '{{behavior}}' });
              if (vertical) requestAnimationFrame(() => { {{VerticalStepExpression}}; });
              return true;
            })();
            """;
    }

    public static string CreateCanTurnScript(int direction, bool vertical = false)
    {
        var safeDirection = direction < 0 ? -1 : 1;
        var tolerance = Format(ReaderPaginationDefaults.SnapTolerance);
        return $$"""
            (() => {
              const el = document.scrollingElement || document.documentElement;
              if (!el) return false;
              const vertical = {{(vertical ? "true" : "false")}};
              const step = vertical ? {{VerticalStepExpression}} : {{PageStepExpression}};
              if (step <= 0) return false;
              const rawMax = Math.max(0, el.scrollWidth - el.clientWidth);
              const trailingInset = parseFloat(getComputedStyle(document.body).paddingRight) || 0;
              const max = vertical
                ? rawMax
                : Math.max(0, Math.round(Math.max(0, rawMax - trailingInset) / step) * step);
              const distance = vertical ? Math.abs(el.scrollLeft || 0) : Math.max(0, el.scrollLeft || 0);
              const nearest = Math.round(distance / step) * step;
              const current = distance >= max - {{tolerance}}
                ? max
                : Math.max(0, Math.min(max, nearest));
              return {{safeDirection}} < 0
                ? current > {{tolerance}}
                : current < max - {{tolerance}};
            })();
            """;
    }

    public static string CreateRestorePositionScript(
        double left,
        double top,
        bool pagination,
        bool vertical = false,
        double? chapterRatio = null)
    {
        var safeLeft = double.IsFinite(left) ? Math.Max(0, left) : 0;
        var safeTop = double.IsFinite(top) ? Math.Max(0, top) : 0;
        var safeRatio = chapterRatio is { } ratio && double.IsFinite(ratio)
            ? Math.Clamp(ratio, 0, 1)
            : (double?)null;
        if (!pagination)
        {
            // Continuous vertical writing anchors scroll at the right edge
            // with a negative scrollLeft range; the saved position is stored
            // as a positive distance from that origin.
            return $$"""
            (() => {
              const vertical = {{(vertical ? "true" : "false")}};
              window.scrollTo({ left: vertical ? -{{Format(safeLeft)}} : {{Format(safeLeft)}}, top: {{Format(safeTop)}}, behavior: 'instant' });
            })();
            """;
        }

        // Persisted positions may come from a different WebView width. Resolve
        // the saved pixel to a page index first and write only the final page
        // boundary; briefly restoring the stale raw pixel exposes a clipped
        // column and lets asynchronous layout work preserve the bad offset.
        // A vertical save stores the raw negative scrollLeft, so measure its
        // absolute distance from the origin and negate the resolved boundary.
        return $$"""
            (() => {
              const el = document.scrollingElement || document.documentElement;
              const body = document.body;
              if (!el || !body) return false;
              const vertical = {{(vertical ? "true" : "false")}};
              const step = vertical ? {{VerticalStepExpression}} : {{PageStepExpression}};
              if (step <= 0) return false;
              const requestedRaw = {{Format(safeLeft)}};
              const requested = vertical ? Math.abs(requestedRaw) : requestedRaw;
              const rawMax = Math.max(0, el.scrollWidth - el.clientWidth);
              const trailingInset = parseFloat(getComputedStyle(body).paddingRight) || 0;
              const max = vertical
                ? rawMax
                : Math.max(
                    0,
                    Math.round(Math.max(0, rawMax - trailingInset) / step) * step);
              const ratio = {{(safeRatio is { } value ? Format(value) : "null")}};
              const rawPageIndex = Math.round(requested / step);
              const rawPageTarget = rawPageIndex * step;
              // A saved pixel is exact when the viewport did not change. If
              // returning from the bookshelf opens with a different TOC or AI
              // panel width, the old page step is no longer aligned; use the
              // persisted within-chapter ratio and snap it to the new grid.
              const rawIsAligned = Math.abs(requested - rawPageTarget) <= 4
                && requested <= max + 4;
              const pageIndex = rawIsAligned || ratio === null
                ? rawPageIndex
                : Math.round((max * ratio) / step);
              const target = requested >= max - 4
                ? max
                : Math.max(0, Math.min(max, pageIndex * step));
              window.scrollTo({ left: vertical ? -target : target, top: 0, behavior: 'instant' });
              return true;
            })();
            """;
    }

    // `vertical` paginated chapters scroll along the X axis with a NEGATIVE
    // scrollLeft range. Requesting a positive left there would clamp to the
    // origin (the first page), so the end edge must be requested negated.
    public static string CreateChapterBoundaryScript(bool moveToEnd, bool horizontal, bool vertical = false) =>
        $$"""
        (() => {
          const el = document.scrollingElement || document.documentElement;
          if (!el) return false;
          const moveToEnd = {{(moveToEnd ? "true" : "false")}};
          const horizontal = {{(horizontal ? "true" : "false")}};
          const vertical = {{(vertical ? "true" : "false")}};
          window.scrollTo(vertical
            ? { left: moveToEnd ? -(el.scrollWidth || 0) : 0, top: 0, behavior: 'instant' }
            : horizontal
              ? { left: moveToEnd ? el.scrollWidth || 0 : 0, top: 0, behavior: 'instant' }
              : { left: 0, top: moveToEnd ? el.scrollHeight || 0 : 0, behavior: 'instant' });
          return true;
        })();
        """;

    private static string Format(double value) =>
        value.ToString("0.##", CultureInfo.InvariantCulture);
}
