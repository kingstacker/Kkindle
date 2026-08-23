using System.Globalization;
using Kkindle.Core;

namespace Kkindle;

internal static class ReaderPaginationScripts
{
    public const string VerticalTypographyGridCss =
        "\nbody :where(p, div, section, article, main, ul, ol, li, blockquote, pre, table, thead, tbody, tr, td, th, h1, h2, h3, h4, h5, h6) { margin-block: 0 !important; padding-block: 0 !important; border-block-width: 0 !important; block-size: auto !important; min-block-size: 0 !important; max-block-size: none !important; }"
        + "\nbody p, body div, body section, body article, body main, body ul, body ol, body li, body blockquote, body pre, body table, body thead, body tbody, body tr, body td, body th { margin-block: 0 !important; padding-block: 0 !important; border-block-width: 0 !important; block-size: auto !important; min-block-size: 0 !important; max-block-size: none !important; }"
        + "\nbody :where(h1, h2, h3, h4, h5, h6), body h1, body h2, body h3, body h4, body h5, body h6 { font-size: 1rem !important; line-height: inherit !important; margin-block: 1lh !important; padding-block: 0 !important; block-size: auto !important; min-block-size: 0 !important; max-block-size: none !important; }";

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
        + " if (!body || viewport <= 0) return viewport;"
        + " const style = getComputedStyle(body);"
        + " const sides = (parseFloat(style.paddingLeft) || 0) + (parseFloat(style.paddingRight) || 0);"
        + " const available = Math.max(1, viewport - sides);"
        + " const line = parseFloat(style.lineHeight) || 0;"
        + " const step = line > 0 ? Math.max(1, Math.floor(available / line) * line) : available;"
        + " document.documentElement.style.setProperty('--kkindle-vertical-page-step', Math.min(available, step) + 'px');"
        + " return Math.min(available, step);"
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
            return $"html {{ --kkindle-vertical-page-side: {responsiveSidePadding}; --kkindle-vertical-page-top: {topPadding}px; --kkindle-vertical-page-bottom: {bottomPadding}px; --kkindle-vertical-page-step: calc(100vw - var(--kkindle-vertical-page-side) - var(--kkindle-vertical-page-side)); width: 100%; height: 100%; overflow: hidden !important; writing-mode: vertical-rl !important; text-orientation: mixed !important; }}"
                + $" body {{ width: max-content !important; min-width: 100% !important; height: 100% !important; min-height: 0 !important; margin: 0 !important; overflow: visible !important;"
                + $" padding: var(--kkindle-vertical-page-top) var(--kkindle-vertical-page-side) var(--kkindle-vertical-page-bottom) !important; box-sizing: border-box !important;"
                + $" writing-mode: vertical-rl !important; text-orientation: mixed !important;"
                + $" column-width: auto !important; column-gap: normal !important; column-fill: balance !important; column-count: auto !important;"
                + $" max-width: none !important; max-height: 100% !important; }}"
                // Natural vertical flow is continuous, so a viewport edge can
                // otherwise bisect a glyph column. Fixed masks create the four
                // page margins; the left mask absorbs the sub-line remainder
                // left after snapping the page step to a whole line grid.
                + $" html::before, html::after, body::before, body::after {{ content: \"\" !important; position: fixed !important; display: block !important; pointer-events: none !important; z-index: 2147483000 !important; background: #FFFFFF !important; margin: 0 !important; padding: 0 !important; }}"
                + $" html::before {{ left: 0 !important; top: 0 !important; bottom: 0 !important; width: calc(100vw - var(--kkindle-vertical-page-step) - var(--kkindle-vertical-page-side)) !important; }}"
                + $" html::after {{ right: 0 !important; top: 0 !important; bottom: 0 !important; width: var(--kkindle-vertical-page-side) !important; }}"
                + $" body::before {{ left: 0 !important; right: 0 !important; top: 0 !important; height: var(--kkindle-vertical-page-top) !important; }}"
                + $" body::after {{ left: 0 !important; right: 0 !important; bottom: 0 !important; height: var(--kkindle-vertical-page-bottom) !important; }}";
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
        bool vertical = false)
    {
        var safeLeft = double.IsFinite(left) ? Math.Max(0, left) : 0;
        var safeTop = double.IsFinite(top) ? Math.Max(0, top) : 0;
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
              const pageIndex = Math.round(requested / step);
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
