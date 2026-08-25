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

    // Publication-style vertical typography. The browser remains responsible
    // for Unicode vertical orientation and OpenType shaping; these rules make
    // that contract explicit and add the standard horizontal-in-vertical
    // treatment used for short numeric runs.
    public const string VerticalPublicationTypographyCss = """
        body,
        body * {
          text-orientation: mixed !important;
          font-kerning: normal !important;
          font-variant-numeric: tabular-nums lining-nums !important;
          font-feature-settings: "kern" 1, "vert" 1, "vrt2" 1 !important;
        }
        body {
          text-combine-upright: digits 4 !important;
          -webkit-text-combine-upright: digits 4 !important;
          -epub-text-combine: horizontal !important;
          line-break: strict !important;
          -webkit-line-break: strict !important;
          -epub-line-break: strict !important;
          word-break: normal !important;
          overflow-wrap: normal !important;
        }
        body .kkindle-tcy {
          text-combine-upright: all !important;
          -webkit-text-combine: horizontal !important;
          display: inline !important;
          white-space: nowrap !important;
        }
        body .kkindle-tcy-all {
          text-combine-upright: all !important;
          -webkit-text-combine: horizontal !important;
          display: inline !important;
          white-space: nowrap !important;
        }
        body .kkindle-vertical-digit {
          text-combine-upright: none !important;
          -webkit-text-combine-upright: none !important;
          -webkit-text-combine: none !important;
          text-orientation: upright !important;
          white-space: nowrap !important;
          line-height: 1 !important;
          vertical-align: baseline !important;
        }
        body :where(ruby) {
          ruby-position: over !important;
          ruby-align: center !important;
        }
        body :where(rt) {
          text-orientation: mixed !important;
          white-space: nowrap !important;
          line-height: 1 !important;
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
