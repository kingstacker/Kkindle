namespace Kkindle;

internal static class ReaderAppearanceScripts
{
    // Use the Unicode/CSS line-breaking model for both horizontal and
    // vertical text. `strict` keeps closing punctuation with the preceding
    // character and opening punctuation with the following character; the
    // EPUB/WebKit-prefixed forms keep the same policy on older reading hosts.
    // Long preformatted tokens receive a narrowly-scoped emergency wrap in
    // BuildReaderAppearanceCss instead of disabling punctuation rules for the
    // entire book.
    public const string StandardLineBreakingCss = """
        body,
        body * {
          line-break: strict !important;
          -webkit-line-break: strict !important;
          -epub-line-break: strict !important;
          word-break: normal !important;
          overflow-wrap: normal !important;
        }
        """;

    // Apple Books-style publication typography: set the inherited defaults on
    // the content root, then let the EPUB and WebKit own every inline decision.
    // In particular, tate-chu-yoko belongs to publication-authored
    // text-combine, not reader-generated digit boxes. The private selectors
    // only neutralize wrappers left in old extraction caches.
    public const string VerticalPublicationTypographyCss = """
        body {
          text-orientation: mixed !important;
          -webkit-text-orientation: mixed !important;
          -epub-text-orientation: mixed !important;
          font-kerning: normal !important;
          /* Keep the font's native advances. CJK fonts already encode the
             correct half/full-width punctuation space, and mixed-orientation
             Latin needs its own natural word metrics. */
          letter-spacing: normal !important;
          word-spacing: normal !important;
        }
        body {
          line-break: strict !important;
          -webkit-line-break: strict !important;
          -epub-line-break: strict !important;
          word-break: normal !important;
          overflow-wrap: normal !important;
          /* Never switch this to rtl. In vertical-rl the `direction` property
             selects the *inline* axis, not the column axis, so an rtl value
             aligns every partially filled line to the bottom of its column:
             a paragraph's last column, short paragraphs and the 2em first
             line indent all end up hugging the page bottom. Measured in
             WebKitGTK, rtl also changes nothing about the scrollLeft range
             (vertical-rl alone already produces the negative range the
             paginator uses), so it is cost without benefit. */
          direction: ltr !important;
        }
        /* Remove every Kreader-generated layout box while retaining its text.
           Publication-authored text-combine-upright remains untouched because
           it does not carry these private Kreader classes. */
        body :where(
          .kkindle-tcy,
          .kkindle-tcy-all,
          .kkindle-tcy-inner,
          .kkindle-cell-inner,
          .kkindle-vertical-digit,
          .kkindle-vertical-punctuation,
          .kkindle-vertical-latin,
          .kkindle-vertical-number,
          .kkindle-native-vertical-digits,
          .kkindle-native-vertical-digit,
          .kkindle-native-vertical-footnote) {
          display: contents !important;
        }
        /* In vertical writing, line-height controls the horizontal distance
           between columns; it is not the top-to-bottom character advance.
           Give Han, digits and punctuation the same real one-em square so the
           boxes participate in layout, never overlap, and scale from the
           element's own font size (including headings). */
        body .kkindle-linux-vertical-cjk,
        body .kkindle-linux-vertical-single,
        body .kkindle-linux-vertical-single-punctuation,
        body .kkindle-linux-vertical-number > .kkindle-linux-vertical-digit {
          display: inline-grid !important;
          grid-template-columns: minmax(0, 1fr) !important;
          grid-template-rows: minmax(0, 1fr) !important;
          align-items: center !important;
          justify-items: center !important;
          /* The cells must stay in the parent's vertical-rl flow. An earlier
             revision forced horizontal-tb here, but orthogonal atomic boxes
             make WebKitGTK size the whole line box to the 1em cell height
             (dropping the paragraph strut): every column collapsed to one em
             and dense chapters marched downward and clipped mid-sentence.
             Same-flow inline grids restore the strut, and center alignment
             keeps the glyph identical in both writing modes. */
          width: 1em !important;
          height: 1em !important;
          line-height: 1em !important;
          vertical-align: baseline !important;
          box-sizing: border-box !important;
          text-indent: 0 !important;
          white-space: nowrap !important;
          letter-spacing: 0 !important;
          margin: 0 !important;
          margin-block: calc((var(--kkindle-vertical-line-pitch, 1.8em) - 1em) / 2) !important;
          padding: 0 !important;
          overflow: hidden !important;
        }
        /* The outer square owns layout; this inner square only centers the
           painted glyph and may receive a small font-bearing correction. */
        body .kkindle-linux-vertical-cjk-ink {
          display: grid !important;
          place-items: center !important;
          position: static !important;
          width: 100% !important;
          height: 100% !important;
          line-height: 1em !important;
          writing-mode: horizontal-tb !important;
          -webkit-writing-mode: horizontal-tb !important;
          text-indent: 0 !important;
          text-align: center !important;
          white-space: nowrap !important;
          margin: 0 !important;
          padding: 0 !important;
          transform: none !important;
        }
        body :where(
          .kkindle-linux-vertical-single,
          .kkindle-linux-vertical-single-punctuation,
          .kkindle-linux-vertical-number > .kkindle-linux-vertical-digit,
          .kkindle-linux-vertical-pair-punctuation) > .kkindle-linux-vertical-cell-inner {
          display: grid !important;
          place-items: center !important;
          width: 100% !important;
          max-width: 100% !important;
          height: 100% !important;
          line-height: 1em !important;
          writing-mode: horizontal-tb !important;
          text-indent: 0 !important;
          white-space: nowrap !important;
          letter-spacing: 0 !important;
          text-align: center !important;
          margin: 0 !important;
          padding: 0 !important;
        }
        /* The cell remains centred in the vertical line grid. A separate
           inline glyph child lets Linux correct the font's optical side
           bearing without moving the cell itself or changing text flow. */
        body :where(
          .kkindle-linux-vertical-single,
          .kkindle-linux-vertical-single-punctuation,
          .kkindle-linux-vertical-number > .kkindle-linux-vertical-digit)
          > .kkindle-linux-vertical-cell-inner
          > .kkindle-linux-vertical-glyph,
        body .kkindle-linux-vertical-cjk
          > .kkindle-linux-vertical-cjk-ink
          > .kkindle-linux-vertical-glyph {
          display: inline-block !important;
          line-height: 1em !important;
          white-space: nowrap !important;
          transform: translate(
            var(--kkindle-vertical-ink-shift, 0px),
            var(--kkindle-vertical-ink-shift-y, 0px)) !important;
        }
        /* WebKitGTK does not consistently select the OpenType vertical forms
           once punctuation is isolated inside a horizontal compatibility
           cell. Rotate long dashes and ellipses explicitly around the exact
           cell centre: the dash becomes a vertical rule and the ellipsis a
           vertical dot run without changing the shared inline advance. */
        body .kkindle-linux-vertical-centered-mark > .kkindle-linux-vertical-cell-inner,
        body .kkindle-linux-vertical-rotated-punctuation > .kkindle-linux-vertical-cell-inner {
          transform: rotate(90deg) scale(0.94) !important;
          transform-origin: 50% 50% !important;
        }
        body .kkindle-linux-vertical-tcy {
          display: inline-grid !important;
          grid-template-columns: minmax(0, 1fr) !important;
          grid-template-rows: minmax(0, 1fr) !important;
          place-items: center !important;
          width: 1em !important;
          height: 1em !important;
          line-height: 1em !important;
          vertical-align: baseline !important;
          box-sizing: border-box !important;
          overflow: hidden !important;
          white-space: nowrap !important;
          letter-spacing: 0 !important;
          word-spacing: 0 !important;
          text-indent: 0 !important;
          margin: 0 !important;
          margin-block: calc((var(--kkindle-vertical-line-pitch, 1.8em) - 1em) / 2) !important;
          padding: 0 !important;
        }
        body .kkindle-linux-vertical-tcy-inner {
          display: block !important;
          width: max-content !important;
          max-width: 100% !important;
          height: 100% !important;
          line-height: 1em !important;
          writing-mode: horizontal-tb !important;
          text-align: center !important;
          white-space: nowrap !important;
          letter-spacing: 0 !important;
          word-spacing: 0 !important;
          margin: 0 !important;
          padding: 0 !important;
          transform-origin: 50% 50% !important;
          /* Two half-width digits should occupy one centred CJK cell even
             before the post-font measurement pass runs. That pass may replace
             this conservative fallback with a precise scale. */
          transform: scaleX(0.72) !important;
        }
        body .kkindle-linux-vertical-number {
          display: inline-flex !important;
          /* Stay in the parent's vertical-rl flow (see the cell note above):
             the inline axis is vertical, so a row stacks the digit cells
             from top to bottom inside the one-em column. */
          flex-direction: row !important;
          align-items: center !important;
          justify-content: flex-start !important;
          width: 1em !important;
          height: auto !important;
          line-height: 1em !important;
          vertical-align: baseline !important;
          text-indent: 0 !important;
          letter-spacing: 0 !important;
          word-spacing: 0 !important;
          white-space: nowrap !important;
          margin: 0 !important;
          margin-block: calc((var(--kkindle-vertical-line-pitch, 1.8em) - 1em) / 2) !important;
          padding: 0 !important;
          box-sizing: border-box !important;
        }
        body .kkindle-linux-vertical-number > .kkindle-linux-vertical-digit {
          display: inline-grid !important;
          place-items: center !important;
          flex: 0 0 1em !important;
        }
        /* Do not add a special margin at a Han-to-number boundary. A special
           top/inline-start margin makes the number's front gap differ from its
           back gap and moves the number off the shared vertical rhythm. The
           fixed cell plus its clipped/centred inner glyph is the complete
           collision boundary for all fonts. */
        body :where(
          .kkindle-linux-vertical-single,
          .kkindle-linux-vertical-number,
          .kkindle-linux-vertical-tcy).kkindle-cjk-before-number {
          margin-inline: 0 !important;
        }
        /* Keep the complete footnote marker, including its brackets, in one
           upright body-text cell. Only the marker ink is reduced; the outer
           box retains a full 1em advance and remains the link hit target. */
        body a.kkindle-linux-vertical-footnote {
          display: inline-grid !important;
          grid-template-columns: minmax(0, 1fr) !important;
          grid-template-rows: minmax(0, 1fr) !important;
          place-items: center !important;
          width: 1em !important;
          height: 1em !important;
          line-height: 1em !important;
          vertical-align: baseline !important;
          white-space: nowrap !important;
          overflow: hidden !important;
          letter-spacing: 0 !important;
          word-spacing: 0 !important;
          text-decoration: none !important;
          margin: 0 !important;
          margin-block: calc((var(--kkindle-vertical-line-pitch, 1.8em) - 1em) / 2) !important;
          padding: 0 !important;
        }
        body .kkindle-linux-vertical-footnote-inner {
          display: flex !important;
          align-items: center !important;
          justify-content: center !important;
          width: 100% !important;
          height: 100% !important;
          font-size: var(--kkindle-linux-footnote-scale, 0.62em) !important;
          line-height: 1em !important;
          text-align: center !important;
          /* Keep the complete label in the same vertical grid cell as the
             anchor. Translating the flex child by a whole cell moves its
             visible ink outside the hit box on WebKitGTK; centring the flex
             child itself already gives brackets and digits the same centre. */
          transform: none !important;
          letter-spacing: 0 !important;
          word-spacing: 0 !important;
          white-space: nowrap !important;
          margin: 0 !important;
          padding: 0 !important;
        }
        body .kkindle-linux-vertical-footnote-inner * {
          font-size: 1em !important;
          line-height: 1em !important;
          margin: 0 !important;
          padding: 0 !important;
          vertical-align: baseline !important;
        }
        /* Paired punctuation gets a stable line-height cell as well. Its inner
           writing mode is horizontal-tb so grid start/end mean the actual
           physical left/right edges even though the surrounding book is
           vertical-rl. This avoids using padding as a positional surrogate,
           which changes the available glyph width and can move the mark in the
           opposite direction on different fonts. Opening and closing marks
           intentionally use the same centred cell: their ink may have
           different side bearings, but their advance and geometric centre do
           not. */
        body .kkindle-linux-vertical-pair-punctuation {
          display: inline-grid !important;
          grid-template-columns: minmax(0, 1fr) !important;
          grid-template-rows: minmax(0, 1fr) !important;
          align-items: center !important;
          justify-items: center !important;
          width: 1em !important;
          height: 1em !important;
          box-sizing: border-box !important;
          line-height: 1em !important;
          vertical-align: baseline !important;
          text-indent: 0 !important;
          white-space: nowrap !important;
          letter-spacing: 0 !important;
          margin: 0 !important;
          margin-block: calc((var(--kkindle-vertical-line-pitch, 1.8em) - 1em) / 2) !important;
          padding: 0 !important;
          overflow: hidden !important;
        }
        body .kkindle-linux-vertical-pair-open {
          justify-items: center !important;
        }
        body .kkindle-linux-vertical-pair-close {
          justify-items: center !important;
        }
        body .kkindle-linux-vertical-pair-open > .kkindle-linux-vertical-cell-inner {
          justify-self: center !important;
          text-align: center !important;
          margin: 0 !important;
          padding: 0 !important;
        }
        body .kkindle-linux-vertical-pair-close > .kkindle-linux-vertical-cell-inner {
          justify-self: center !important;
          text-align: center !important;
          margin: 0 !important;
          padding: 0 !important;
        }
        /* Draw paired punctuation with its Unicode vertical presentation
           form. The original character remains in the DOM for search,
           selection and copy, while the visible glyph occupies an exact
           centred full-height cell with no physical X translation. Quotes follow
           this same rule; they must not use opposite edge alignment. */
        body :where(
          .kkindle-linux-vertical-single-punctuation,
          .kkindle-linux-vertical-pair-punctuation)
          > .kkindle-linux-vertical-cell-inner[data-kkindle-vertical-glyph] {
          font-size: 1em !important;
          color: transparent !important;
          -webkit-text-fill-color: transparent !important;
          transform: none !important;
        }
        body :where(
          .kkindle-linux-vertical-single-punctuation,
          .kkindle-linux-vertical-pair-punctuation)
          > .kkindle-linux-vertical-cell-inner[data-kkindle-vertical-glyph]::before {
          content: attr(data-kkindle-vertical-glyph) !important;
          display: grid !important;
          place-items: center !important;
          width: 100% !important;
          height: 100% !important;
          margin: 0 !important;
          padding: 0 !important;
          font-size: 1em !important;
          line-height: 1em !important;
          color: #111111 !important;
          -webkit-text-fill-color: #111111 !important;
          text-align: center !important;
          transform: translate(
            var(--kkindle-vertical-ink-shift, 0px),
            var(--kkindle-vertical-ink-shift-y, 0px)) !important;
        }
        /* The vertical closing-parenthesis presentation glyph has its ink
           near the top of the em even after the font-metric correction. Give
           the closing side the same optical centre as the opening side; this
           is a visual shift only and does not change the one-cell advance. */
        body .kkindle-linux-vertical-pair-close
          > .kkindle-linux-vertical-cell-inner[data-kkindle-vertical-glyph]::before {
          transform: translate(
            var(--kkindle-vertical-ink-shift, 0px),
            calc(var(--kkindle-vertical-ink-shift-y, 0px) + 0.22em)) !important;
        }
        body .kkindle-linux-vertical-single-punctuation {
          overflow: hidden !important;
        }
        """;

    // Temporary Linux layout probe. It is appended only when
    // KKINDLE_VERTICAL_DEBUG_BOXES=1 is set on the debug process. The outer
    // compatibility cell is solid, its inner glyph cell is dashed, and the
    // range probe in MainWindow.ReaderInteraction.cs draws native Latin text
    // without changing document layout. Han cells are real one-em layout
    // squares, so their green outline is the actual box rather than a second
    // fixed-position approximation.
    public const string VerticalDebugOutlineCss = """
        body[data-kkindle-vertical-debug-boxes="1"]
          .kkindle-linux-vertical-single,
        body[data-kkindle-vertical-debug-boxes="1"]
          .kkindle-linux-vertical-single-punctuation,
        body[data-kkindle-vertical-debug-boxes="1"]
          .kkindle-linux-vertical-tcy,
        body[data-kkindle-vertical-debug-boxes="1"]
          .kkindle-linux-vertical-number,
        body[data-kkindle-vertical-debug-boxes="1"]
          .kkindle-linux-vertical-number > .kkindle-linux-vertical-digit,
        body[data-kkindle-vertical-debug-boxes="1"]
          .kkindle-linux-vertical-pair-punctuation,
        body[data-kkindle-vertical-debug-boxes="1"]
          .kkindle-linux-vertical-footnote {
          outline: 1px solid rgba(220, 38, 38, 0.92) !important;
          outline-offset: -1px !important;
        }
        body[data-kkindle-vertical-debug-boxes="1"]
          .kkindle-linux-vertical-cjk {
          outline: 1px solid rgba(22, 163, 74, 0.92) !important;
          outline-offset: -1px !important;
        }
        body[data-kkindle-vertical-debug-boxes="1"]
          .kkindle-linux-vertical-cell-inner,
        body[data-kkindle-vertical-debug-boxes="1"]
          .kkindle-linux-vertical-tcy-inner,
        body[data-kkindle-vertical-debug-boxes="1"]
          .kkindle-linux-vertical-cjk-ink {
          outline: 1px dashed rgba(37, 99, 235, 0.92) !important;
          outline-offset: -1px !important;
        }
        """;

    // WebView2 uses Chromium scrollbars, so mirror the native reader scrollbar
    // resources with the same 10px monochrome geometry.
    public const string MonochromeScrollbarCss = """
        html, body, html * {
          scrollbar-color: #000000 #FFFFFF !important;
          scrollbar-width: auto !important;
        }
        html::-webkit-scrollbar,
        body::-webkit-scrollbar,
        html *::-webkit-scrollbar {
          width: 10px;
          height: 10px;
        }
        html::-webkit-scrollbar-track,
        body::-webkit-scrollbar-track,
        html *::-webkit-scrollbar-track {
          background: #FFFFFF;
          border: 0;
        }
        html::-webkit-scrollbar-thumb,
        body::-webkit-scrollbar-thumb,
        html *::-webkit-scrollbar-thumb {
          background: #000000;
          border: 1px solid #000000;
          border-radius: 0;
          min-height: 8px;
        }
        html::-webkit-scrollbar-thumb:hover,
        body::-webkit-scrollbar-thumb:hover,
        html *::-webkit-scrollbar-thumb:hover {
          background: #111111;
        }
        html::-webkit-scrollbar-thumb:active,
        body::-webkit-scrollbar-thumb:active,
        html *::-webkit-scrollbar-thumb:active {
          background: #000000;
        }
        html::-webkit-scrollbar-corner,
        body::-webkit-scrollbar-corner,
        html *::-webkit-scrollbar-corner {
          background: #FFFFFF;
        }
        """;
}
