using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using System.Xml;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.TextFormatting;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle;

/// <summary>
/// Reader interactions that are independent of the native webview control:
/// layout injection, TOC/fragment navigation, in-page search, basic marks and
/// the small productivity tools that make the first reader surface usable.
/// </summary>
public partial class MainWindow
{
    internal const char ReaderLinuxTextFallbackFootnoteMarker = '\uE000';
    private const string ReaderWebBundledFontFamily = "KkindleKingHwaOldSong";
    private const string ReaderBundledFontTargetFileName = ".KingHwaOldSong-v3.0.ttf";
    private const int ReaderAnimationNone = 0;
    private const int ReaderAnimationFade = 1;
    private const int ReaderAnimationSlide = 2;
    private const int ReaderAnimationWave = 3;
    private const double ReaderZenActivationWidth = 380;
    private const double ReaderZenActivationHeight = 64;
    // The Linux reader must render the real EPUB in WebKitGTK, exactly like
    // the Windows reader does in WebView2: covers and images, the selection
    // action bar, footnote popovers and the injected page-turn/keyboard
    // handling all live in that document. Keep the old Avalonia text surface
    // in the source for diagnostics and regression tests, but never select it
    // as a production reader surface.
    private static readonly bool UseLinuxPlainTextRecoveryFallback = false;

    private const string PrepareVerticalInlineRunsScript = """
        (() => {
          // Publication 縦中横: a two-digit run sits horizontally inside one
          // one-em square, filling it like two half-width digits side by side
          // while keeping full text height. The base CSS uses a fixed
          // compression that suits common Linux fonts; here each cell is fit
          // against its real untransformed layout width (offsetWidth ignores
          // transforms), so DejaVu, Noto and embedded EPUB fonts all land on
          // the standard square instead of a squeezed sliver.
          const fitVerticalTcyRuns = () => {
            if (window.__kkindleReaderVertical !== true) return;
            const cells = document.querySelectorAll(
              '.kkindle-tcy[data-kkindle-vertical-run="1"], .kkindle-tcy-all[data-kkindle-vertical-run="1"]');
            for (const cell of cells) {
              const inner = cell.querySelector(':scope > .kkindle-tcy-inner');
              if (!inner) continue;
              const em = parseFloat(getComputedStyle(inner).fontSize);
              const natural = inner.offsetWidth;
              if (!(em > 0) || !(natural > 0)) continue;
              const scale = Math.min(1, (em * 0.96) / natural);
              inner.style.setProperty(
                'transform',
                'translate(-50%, -50%) scaleX(' + scale.toFixed(4) + ')',
                'important');
            }
          };

          // Sideways runs are atomic by default so a word or a number is never
          // torn apart mid-glyph. A run that is longer than the column it must
          // live in cannot honor that: kept nowrap it overflows past the page
          // bottom and the margin mask cuts it. Measure each run against the
          // usable column extent and let the ones that cannot fit wrap.
          // In vertical-rl the inline axis is vertical, so a run's border-box
          // height is its inline extent.
          const fitVerticalSidewaysRuns = () => {
            if (window.__kkindleReaderVertical !== true) return;
            const body = document.body;
            const el = document.scrollingElement || document.documentElement;
            if (!body || !el) return;
            const runs = body.querySelectorAll(
              '.kkindle-vertical-latin[data-kkindle-vertical-run="1"], '
              + '.kkindle-vertical-number[data-kkindle-vertical-run="1"]');
            for (const run of runs) run.classList.remove('kkindle-vertical-run-wrap');
            const style = getComputedStyle(body);
            const available = (el.clientHeight || 0)
              - (parseFloat(style.paddingTop) || 0)
              - (parseFloat(style.paddingBottom) || 0);
            if (!(available > 0)) return;
            for (const run of runs) {
              if (run.getBoundingClientRect().height > available + 0.5)
                run.classList.add('kkindle-vertical-run-wrap');
            }
          };

          const fitVerticalRuns = () => {
            fitVerticalTcyRuns();
            fitVerticalSidewaysRuns();
          };
          window.__kkindleFitVerticalTcyRuns = fitVerticalTcyRuns;
          window.__kkindleFitVerticalRuns = fitVerticalRuns;
          // The usable column extent changes with the viewport, so the wrap
          // decision has to be revisited on resize. Configuration passes only
          // rebind the closure; the listener itself is installed once.
          if (window.__kkindleVerticalFitResizeBound !== true) {
            window.__kkindleVerticalFitResizeBound = true;
            let pending = 0;
            window.addEventListener('resize', () => {
              if (pending) clearTimeout(pending);
              pending = setTimeout(() => {
                pending = 0;
                if (typeof window.__kkindleFitVerticalRuns === 'function')
                  window.__kkindleFitVerticalRuns();
              }, 120);
            });
          }

          if (typeof window.__kkindlePrepareVerticalInlineRuns === 'function')
            window.__kkindlePrepareVerticalInlineRuns();

          const prepare = () => {
            const body = document.body;
            if (!body || window.__kkindleReaderVertical !== true) return false;
            if (body.dataset.kkindleVerticalInlinePrepared === '65') return true;

            body.querySelectorAll('span[data-kkindle-vertical-run="1"]')
              .forEach(span => span.replaceWith(document.createTextNode(span.textContent || '')));

            // Keep these rules identical to MarkVerticalInlineRuns in
            // EpubReaderPreparationService and to the injected bridge copy.
            // A token carries its own internal connectors, so "don't", "AT&T"
            // and "well-known" survive whole; adjacent tokens separated by a
            // short ASCII gap merge into one phrase when either side has a
            // letter, so an English sentence becomes one sideways run instead
            // of one rotated box per word. Digit-only neighbours never merge.
            const verticalInlineTokenPattern = /[A-Za-z0-9]+(?:['’&.,:/+\-–—][A-Za-z0-9]+|%|°[CF])*/g;
            const numericTokenPattern = /^[0-9]+(?:[.,:/+\-–—][0-9]+|%|°[CF])*$/;
            const verticalPunctuationCharacters = "!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~";
            const maxPhraseGap = 3;
            const walker = document.createTreeWalker(body, NodeFilter.SHOW_TEXT);
            const textNodes = [];
            for (let node = walker.nextNode(); node; node = walker.nextNode()) {
              const parent = node.parentElement;
              if (!parent
                  || parent.closest('#kkindle-selection-bar, script, style, noscript, ruby, rt, [data-kkindle-vertical-run="1"]')
                  || !/[0-9A-Za-z!\"#$%&'()*+,\-./:;<=>?@[\\\]^_`{|}~]/.test(node.nodeValue || ''))
                continue;
              textNodes.push(node);
            }

            const canMerge = (value, left, right) => {
              if (!/[A-Za-z]/.test(left.token) && !/[A-Za-z]/.test(right.token)) return false;
              const gapStart = left.start + left.token.length;
              const gapLength = right.start - gapStart;
              if (gapLength < 1 || gapLength > maxPhraseGap) return false;
              for (let index = gapStart; index < right.start; index++) {
                const character = value[index];
                if (character !== ' ' && character !== '\t'
                    && !verticalPunctuationCharacters.includes(character))
                  return false;
              }
              return true;
            };

            const classify = (token, merged) => {
              const hasLatin = /[A-Za-z]/.test(token);
              if (merged) return hasLatin ? 'kkindle-vertical-latin' : null;
              const isNumericToken = numericTokenPattern.test(token);
              const digitCount = (token.match(/[0-9]/g) || []).length;
              if (!hasLatin && (!isNumericToken || digitCount === 0)) return null;
              if (!isNumericToken) return 'kkindle-vertical-latin';
              if (!/^[0-9]+$/.test(token)) return 'kkindle-vertical-number';
              return token.length === 1
                ? 'kkindle-vertical-digit'
                : token.length === 2
                ? 'kkindle-tcy'
                : 'kkindle-vertical-number';
            };

            for (const node of textNodes) {
              const value = node.nodeValue || '';
              const fragment = document.createDocumentFragment();
              let cursor = 0;
              let wrapped = false;
              const matches = [];
              verticalInlineTokenPattern.lastIndex = 0;
              for (let match = verticalInlineTokenPattern.exec(value);
                   match;
                   match = verticalInlineTokenPattern.exec(value)) {
                matches.push({ token: match[0], start: match.index });
              }

              const runs = [];
              for (let index = 0; index < matches.length;) {
                let last = index;
                while (last + 1 < matches.length
                    && canMerge(value, matches[last], matches[last + 1]))
                  last++;
                const start = matches[index].start;
                const end = matches[last].start + matches[last].token.length;
                const token = value.slice(start, end);
                const merged = last > index;
                index = last + 1;
                const className = classify(token, merged);
                if (!className) continue;
                runs.push({ start, length: end - start, token, className });
              }

              for (let index = 0; index < value.length; index++) {
                if (!verticalPunctuationCharacters.includes(value[index])
                    || runs.some(run => index >= run.start
                      && index < run.start + run.length))
                  continue;
                runs.push({
                  start: index,
                  length: 1,
                  token: value[index],
                  className: 'kkindle-vertical-punctuation'
                });
              }

              runs.sort((left, right) => left.start - right.start);
              for (const run of runs) {
                const { start, token, className } = run;

                if (start > cursor)
                  fragment.appendChild(document.createTextNode(value.slice(cursor, start)));
                const span = document.createElement('span');
                span.className = className;
                span.dataset.kkindleVerticalRun = '1';
                if (className === 'kkindle-tcy' || className === 'kkindle-tcy-all') {
                  span.dataset.kkindleTcyLength = String(token.length);
                  const inner = document.createElement('span');
                  inner.className = 'kkindle-tcy-inner';
                  inner.textContent = token;
                  span.appendChild(inner);
                } else if (
                    className === 'kkindle-vertical-digit'
                    || className === 'kkindle-vertical-punctuation') {
                  // Same centered-cell structure as tate-chu-yoko so digit
                  // and punctuation ink stays inside its one-em cell no
                  // matter which Linux font supplies the baseline metrics.
                  const inner = document.createElement('span');
                  inner.className = 'kkindle-cell-inner';
                  inner.textContent = token;
                  span.appendChild(inner);
                } else {
                  span.textContent = token;
                }
                fragment.appendChild(span);
                cursor = start + run.length;
                wrapped = true;
              }
              if (!wrapped) continue;
              if (cursor < value.length)
                fragment.appendChild(document.createTextNode(value.slice(cursor)));
              node.parentNode?.replaceChild(fragment, node);
            }
            body.dataset.kkindleVerticalInlinePrepared = '65';
            return true;
          };

          if (!document.body) {
            document.addEventListener('DOMContentLoaded', () => {
              prepare();
              fitVerticalRuns();
            }, { once: true });
            return true;
          }
          prepare();
          fitVerticalRuns();
          return true;
        })();
        """;

    // Match Apple Books' publication model: WebKit receives the original text
    // runs and the EPUB alone decides where tate-chu-yoko is appropriate via
    // text-combine. Kreader only unwraps private boxes from older caches.
    private const string PreparePublicationVerticalTextScript = """
        (() => {
          const prepare = () => {
            const body = document.body;
            if (!body || window.__kkindleReaderVertical !== true) return false;

            body.querySelectorAll('span[data-kkindle-vertical-run="1"]')
              .forEach(span => span.replaceWith(
                document.createTextNode(span.textContent || '')));
            body.querySelectorAll(
              '.kkindle-native-vertical-digits, .kkindle-native-vertical-footnote, '
              + '.kkindle-linux-vertical-single, .kkindle-linux-vertical-tcy, '
              + '.kkindle-linux-vertical-number, '
              + '.kkindle-linux-vertical-single-punctuation, '
              + '.kkindle-linux-vertical-cjk, '
              + '.kkindle-linux-vertical-pair-punctuation')
              .forEach(span => span.replaceWith(
                document.createTextNode(span.textContent || '')));
            body.querySelectorAll('.kkindle-linux-vertical-footnote-inner')
              .forEach(span => span.replaceWith(...span.childNodes));
            body.querySelectorAll('.kkindle-linux-vertical-footnote')
              .forEach(anchor => {
                anchor.classList.remove('kkindle-linux-vertical-footnote');
                anchor.removeAttribute('data-kkindle-linux-vertical-footnote');
                anchor.style.removeProperty('--kkindle-linux-footnote-scale');
              });
            body.normalize();
            body.dataset.kkindleVerticalInlinePrepared = 'publication-native-1';
            window.__kkindleFitVerticalTcyRuns = null;
            window.__kkindleFitVerticalRuns = null;
            return true;
          };

          if (!document.body) {
            document.addEventListener('DOMContentLoaded', prepare, { once: true });
            return true;
          }
          return prepare();
        })();
        """;

    // Vertical compatibility cells. Publication-authored text-combine always
    // wins; isolated punctuation and otherwise-unmarked pure numeric runs get
    // fixed one-em cells so their physical alignment is stable in vertical-rl
    // on both WebKitGTK and WebView2.
    private const string PrepareVerticalNumbersAndPunctuationScript = """
        (() => {
          const body = document.getElementById(window.__kkindleVertWrapRoot || '')
            || document.body;
          if (!body || window.__kkindleReaderVertical !== true) return false;
          // In vertical-rl the inline axis runs top-to-bottom. Each generated
          // Han/digit/punctuation box therefore advances by its own local 1em;
          // line-height belongs to the horizontal column pitch and must never
          // be reused as the cell height.
          const hasPublicationCombine = element => {
            for (let node = element; node && node !== body; node = node.parentElement) {
              const style = getComputedStyle(node);
              const value = style.textCombineUpright || style.webkitTextCombine || '';
              if (value && value !== 'none') return true;
            }
            return false;
          };
          for (const anchor of body.querySelectorAll('a')) {
            if (anchor.classList.contains('kkindle-linux-vertical-footnote')) continue;
            const label = (anchor.textContent || '').replace(/\s+/g, '');
            const metadata = [
              anchor.className || '',
              anchor.getAttribute('role') || '',
              anchor.getAttribute('epub:type') || '',
              anchor.getAttribute('href') || '',
              anchor.getAttribute('data-kkindle-footnote-href') || ''
            ].join(' ');
            const isFootnote = /kkindle-footnote-reference|doc-noteref|noteref|footnote|endnote/i.test(metadata)
              || (!!anchor.querySelector('sup') && /^\[?\d{1,3}\]?$/.test(label));
            if (!isFootnote) continue;
            if (!label || label.length > 6 || !/[0-9０-９一二三四五六七八九十]/.test(label))
              continue;
            const inner = document.createElement('span');
            inner.className = 'kkindle-linux-vertical-footnote-inner';
            while (anchor.firstChild) inner.appendChild(anchor.firstChild);
            anchor.appendChild(inner);
            anchor.classList.add('kkindle-linux-vertical-footnote');
            anchor.dataset.kkindleLinuxVerticalFootnote = '1';
            const scale = label.length <= 3 ? 0.62 : label.length === 4 ? 0.50 : 0.42;
            anchor.style.setProperty('--kkindle-linux-footnote-scale', scale + 'em');
          }

          const openingPunctuation = new Set([
            '（', '《', '〈', '【', '〔', '［', '｛', '“', '‘', '「', '『', '〖', '〘', '〚'
          ]);
          const closingPunctuation = new Set([
            '）', '》', '〉', '】', '〕', '］', '｝', '”', '’', '」', '』', '〗', '〙', '〛'
          ]);
          const openingQuotes = new Set(['“', '‘', '「', '『']);
          const closingQuotes = new Set(['”', '’', '」', '』']);
          const verticalPairGlyphs = new Map([
            ['（', '︵'], ['）', '︶'], ['(', '︵'], [')', '︶'],
            ['｛', '︷'], ['｝', '︸'], ['{', '︷'], ['}', '︸'],
            ['〔', '︹'], ['〕', '︺'], ['【', '︻'], ['】', '︼'],
            ['《', '︽'], ['》', '︾'], ['〈', '︿'], ['〉', '﹀'],
            ['［', '﹇'], ['］', '﹈'], ['[', '﹇'], [']', '﹈'],
            // Use the Unicode vertical quote forms instead of rotating the
            // horizontal quote glyph. The latter has different left/right
            // side bearings for open/close marks and was the source of the
            // visibly asymmetric quote/bracket edges on Linux.
            ['“', '﹁'], ['”', '﹂'], ['‘', '﹃'], ['’', '﹄'],
            ['「', '﹁'], ['」', '﹂'], ['『', '﹃'], ['』', '﹄']
          ]);
          // Horizontal punctuation glyphs are intentionally positioned toward
          // a corner of their em square. Use the Unicode vertical forms for
          // marks that have a dedicated form; the original character remains
          // in the text node for copy/search and the visible form is painted
          // by the centered cell pseudo-element below.
          const verticalSingleGlyphs = new Map([
            ['，', '︐'], ['、', '︑'], ['。', '︒'],
            ['：', '︓'], ['；', '︔'], ['！', '︕'], ['？', '︖'],
            ['｡', '︒'], ['､', '︑']
          ]);
          const addCellGlyph = (inner, value) => {
            const glyph = document.createElement('span');
            glyph.className = 'kkindle-linux-vertical-glyph';
            glyph.textContent = value;
            inner.replaceChildren(glyph);
            return glyph;
          };
          const asciiOpeningPunctuation = new Set(['(', '[', '{']);
          const asciiClosingPunctuation = new Set([')', ']', '}']);
          const singlePunctuation = new Set([
            '，', '。', '！', '？', '、', '；', '：', '…', '—', '―', '–', '－',
            '·', '・', '｡', '､', '．', '‥', '〃', '※', '〽', '﹏', '～', '〜'
          ]);
          const verticalCenteredMarks = new Set(['…', '‥', '—', '―', '–', '－']);
          // Fullwidth colon/semicolon use their Unicode vertical forms below;
          // only ASCII forms still need the explicit quarter-turn fallback.
          const verticalRotatedPunctuation = new Set([':', ';']);
          const asciiPunctuation = new Set([
            '!', '"', '#', '$', '%', '&', '*', '+', ',', '-', '.', '/', ':',
            ';', '<', '=', '>', '?', '@', '\\', '^', '_', '`', '|', '~'
          ]);
          const hasAsciiWordNeighbor = (value, index) =>
            /[A-Za-z0-9]/.test(value[index - 1] || '')
            || /[A-Za-z0-9]/.test(value[index + 1] || '');
          const isPunctuationCandidate = (value, index) => {
            const character = value[index];
            return openingPunctuation.has(character)
              || closingPunctuation.has(character)
              || ((asciiOpeningPunctuation.has(character)
                   || asciiClosingPunctuation.has(character))
                  && !hasAsciiWordNeighbor(value, index))
              || singlePunctuation.has(character)
              || (asciiPunctuation.has(character) && !hasAsciiWordNeighbor(value, index));
          };
          const containsPunctuationCandidate = value => {
            for (let index = 0; index < value.length; index++) {
              if (isPunctuationCandidate(value, index)) return true;
            }
            return false;
          };
          const punctuationNodes = [];
          const punctuationWalker = document.createTreeWalker(body, NodeFilter.SHOW_TEXT);
          for (let node = punctuationWalker.nextNode(); node; node = punctuationWalker.nextNode()) {
            const parent = node.parentElement;
            if (!parent
                || !containsPunctuationCandidate(node.nodeValue || '')
                || parent.closest('#kkindle-selection-bar, script, style, noscript, '
                  + '[data-kkindle-reader-chrome="1"], .kkindle-linux-vertical-footnote, '
                  + '.kkindle-linux-vertical-single-punctuation, '
                  + '.kkindle-linux-vertical-pair-punctuation')
                || hasPublicationCombine(parent))
              continue;
            punctuationNodes.push(node);
          }
          for (const node of punctuationNodes) {
            const value = node.nodeValue || '';
            const fragment = document.createDocumentFragment();
            let cursor = 0;
            for (let index = 0; index < value.length; index++) {
              const character = value[index];
              const isOpening = openingPunctuation.has(character)
                || (asciiOpeningPunctuation.has(character) && !hasAsciiWordNeighbor(value, index));
              const isClosing = closingPunctuation.has(character)
                || (asciiClosingPunctuation.has(character) && !hasAsciiWordNeighbor(value, index));
              const isSingle = singlePunctuation.has(character)
                || (asciiPunctuation.has(character) && !hasAsciiWordNeighbor(value, index));
              if (!isOpening && !isClosing && !isSingle) continue;
              if (index > cursor)
                fragment.appendChild(document.createTextNode(value.slice(cursor, index)));
              const span = document.createElement('span');
              if (isOpening || isClosing) {
                span.className = 'kkindle-linux-vertical-pair-punctuation '
                  + (isOpening
                    ? 'kkindle-linux-vertical-pair-open'
                    : 'kkindle-linux-vertical-pair-close');
                if (openingQuotes.has(character))
                  span.classList.add('kkindle-linux-vertical-quote-open');
                if (closingQuotes.has(character))
                  span.classList.add('kkindle-linux-vertical-quote-close');
                span.dataset.kkindleLinuxVerticalPairPunctuation = isOpening ? 'open' : 'close';
              } else {
                span.className = 'kkindle-linux-vertical-single-punctuation';
                if (verticalCenteredMarks.has(character))
                  span.classList.add('kkindle-linux-vertical-centered-mark');
                if (verticalRotatedPunctuation.has(character))
                  span.classList.add('kkindle-linux-vertical-rotated-punctuation');
                span.dataset.kkindleLinuxVerticalSinglePunctuation = '1';
              }
              const inner = document.createElement('span');
              inner.className = 'kkindle-linux-vertical-cell-inner';
              inner.textContent = character;
              if (isOpening || isClosing || verticalSingleGlyphs.has(character)) {
                inner.dataset.kkindleVerticalGlyph = (isOpening || isClosing)
                  ? (verticalPairGlyphs.get(character) || character)
                  : verticalSingleGlyphs.get(character);
              } else {
                // An unmapped single mark is rendered by a real child glyph.
                // Do not also attach the data attribute used by the pseudo
                // element for vertical presentation forms, or it would paint
                // the mark twice (and invalidate its optical centre).
                inner.removeAttribute('data-kkindle-vertical-glyph');
                addCellGlyph(inner, character);
              }
              span.appendChild(inner);
              fragment.appendChild(span);
              cursor = index + 1;
            }
            if (cursor < value.length)
              fragment.appendChild(document.createTextNode(value.slice(cursor)));
            node.parentNode?.replaceChild(fragment, node);
          }
          const nodes = [];
          const walker = document.createTreeWalker(body, NodeFilter.SHOW_TEXT);
          for (let node = walker.nextNode(); node; node = walker.nextNode()) {
            const parent = node.parentElement;
            if (!parent
                || !/[0-9]/.test(node.nodeValue || '')
                || parent.closest('#kkindle-selection-bar, script, style, noscript, '
                  + '[data-kkindle-reader-chrome="1"], .kkindle-linux-vertical-number, '
                  + '.kkindle-linux-vertical-footnote, '
                  + '.kkindle-linux-vertical-single-punctuation, '
                  + '.kkindle-linux-vertical-pair-punctuation')
                || hasPublicationCombine(parent))
              continue;
            nodes.push(node);
          }
          for (const node of nodes) {
            const value = node.nodeValue || '';
            const fragment = document.createDocumentFragment();
            const pattern = /[0-9]+/g;
            let cursor = 0;
            let wrapped = false;
            for (let match = pattern.exec(value); match; match = pattern.exec(value)) {
              const before = value[match.index - 1] || '';
              const after = value[match.index + match[0].length] || '';
              if (/[A-Za-z0-9]/.test(before) || /[A-Za-z0-9]/.test(after)) continue;
              if (match.index > cursor)
                fragment.appendChild(document.createTextNode(value.slice(cursor, match.index)));
              const span = document.createElement('span');
              if (match[0].length === 1) {
                span.className = 'kkindle-linux-vertical-single';
                span.dataset.kkindleLinuxVerticalSingle = '1';
                const inner = document.createElement('span');
                inner.className = 'kkindle-linux-vertical-cell-inner';
                inner.textContent = match[0];
                addCellGlyph(inner, match[0]);
                span.appendChild(inner);
              } else if (match[0].length === 2) {
                span.className = 'kkindle-linux-vertical-tcy';
                span.dataset.kkindleLinuxVerticalTcy = '1';
                const inner = document.createElement('span');
                inner.className = 'kkindle-linux-vertical-tcy-inner';
                inner.textContent = match[0];
                span.appendChild(inner);
              } else {
                span.className = 'kkindle-linux-vertical-number';
                span.dataset.kkindleLinuxVerticalNumber = '1';
                for (const digit of match[0]) {
                  const cell = document.createElement('span');
                  cell.className = 'kkindle-linux-vertical-digit';
                  const inner = document.createElement('span');
                  inner.className = 'kkindle-linux-vertical-cell-inner';
                  inner.textContent = digit;
                  addCellGlyph(inner, digit);
                  cell.appendChild(inner);
                  span.appendChild(cell);
                }
              }
              if (/[\u3400-\u4DBF\u4E00-\u9FFF\uF900-\uFAFF]/.test(before))
                span.classList.add('kkindle-cjk-before-number');
              fragment.appendChild(span);
              cursor = match.index + match[0].length;
              wrapped = true;
            }
            if (!wrapped) continue;
            if (cursor < value.length)
              fragment.appendChild(document.createTextNode(value.slice(cursor)));
            node.parentNode?.replaceChild(fragment, node);
          }
          // Two digits use one horizontal tate-chu-yoko cell. Fit the actual
          // loaded font's natural width into that cell, then let the grid
          // center the complete run on the surrounding vertical column.
          for (const cell of body.querySelectorAll(
              '.kkindle-linux-vertical-tcy[data-kkindle-linux-vertical-tcy="1"]')) {
            const inner = cell.querySelector(':scope > .kkindle-linux-vertical-tcy-inner');
            if (!inner) continue;
            const em = parseFloat(getComputedStyle(cell).fontSize);
            const naturalWidth = inner.offsetWidth;
            if (!(em > 0) || !(naturalWidth > 0)) continue;
            const scale = Math.min(1, (em * 0.9) / naturalWidth);
            inner.style.setProperty('transform', 'scaleX(' + scale.toFixed(4) + ')', 'important');
          }

          // WebKitGTK keeps an upright Han glyph on the font's native
          // baseline, which is not necessarily the visual centre of the
          // one-em advance box (KingHwaOldSong has a small right-side ink
          // overhang). Give every ordinary Han character the same explicit
          // cell as the compatibility marks. The outer cell is the layout
          // unit; only the inner glyph is nudged, so the column rhythm and
          // text offsets remain unchanged.
          const cjkCharacterPattern = /[\u3400-\u4DBF\u4E00-\u9FFF\uF900-\uFAFF]/;
          const cjkNodes = [];
          const cjkWalker = document.createTreeWalker(body, NodeFilter.SHOW_TEXT);
          for (let node = cjkWalker.nextNode(); node; node = cjkWalker.nextNode()) {
            const parent = node.parentElement;
            const value = node.nodeValue || '';
            if (!parent
                || !cjkCharacterPattern.test(value)
                || parent.closest(
                  '#kkindle-selection-bar, script, style, noscript, ruby, rt, '
                  + '[data-kkindle-reader-chrome="1"], '
                  + '.kkindle-linux-vertical-cjk, '
                  + '.kkindle-linux-vertical-single, '
                  + '.kkindle-linux-vertical-single-punctuation, '
                  + '.kkindle-linux-vertical-tcy, '
                  + '.kkindle-linux-vertical-number, '
                  + '.kkindle-linux-vertical-pair-punctuation, '
                  + '.kkindle-linux-vertical-footnote')
                || hasPublicationCombine(parent))
              continue;
            cjkNodes.push(node);
          }
          for (const node of cjkNodes) {
            const value = node.nodeValue || '';
            const fragment = document.createDocumentFragment();
            let textStart = 0;
            let wrapped = false;
            for (let index = 0; index < value.length;) {
              const codePoint = value.codePointAt(index);
              const character = String.fromCodePoint(codePoint || 0);
              const length = character.length;
              if (!cjkCharacterPattern.test(character)) {
                index += length;
                continue;
              }
              if (index > textStart)
                fragment.appendChild(document.createTextNode(value.slice(textStart, index)));
              const cell = document.createElement('span');
              cell.className = 'kkindle-linux-vertical-cjk';
              cell.dataset.kkindleLinuxVerticalCjk = '1';
              const ink = document.createElement('span');
              ink.className = 'kkindle-linux-vertical-cjk-ink';
              ink.dataset.kkindleVerticalInkCharacter = character;
              addCellGlyph(ink, character);
              cell.appendChild(ink);
              fragment.appendChild(cell);
              index += length;
              textStart = index;
              wrapped = true;
            }
            if (!wrapped) continue;
            if (textStart < value.length)
              fragment.appendChild(document.createTextNode(value.slice(textStart)));
            node.parentNode?.replaceChild(fragment, node);
          }

          // Centre the actual ink, rather than only the browser's advance
          // width. Canvas TextMetrics uses the same loaded font and weight as
          // the DOM. The correction is per glyph and scales automatically
          // for headings and bold titles.
          const inkContext = document.createElement('canvas').getContext('2d');
          const inkShiftCache = new Map();
          const applyInkShift = inner => {
            if (!inkContext) return;
            const glyph = inner.dataset.kkindleVerticalGlyph
              || inner.dataset.kkindleVerticalInkCharacter
              || (inner.textContent || '');
            if (!glyph || inner.classList.contains('kkindle-linux-vertical-tcy-inner')) return;
            const style = getComputedStyle(inner);
            const isRotatedMark = inner.parentElement?.classList.contains(
              'kkindle-linux-vertical-centered-mark')
              || inner.parentElement?.classList.contains(
                'kkindle-linux-vertical-rotated-punctuation');
            const key = [
              style.fontStyle,
              style.fontWeight,
              style.fontSize,
              style.fontFamily,
              glyph,
              isRotatedMark ? 'rotated' : 'upright'
            ].join('|');
            let shift = inkShiftCache.get(key);
            if (shift === undefined) {
              if (isRotatedMark) {
                // The parent cell is quarter-turned after this pass. An
                // upright optical Y correction would become a physical X
                // offset after rotation, putting a dash/ellipsis off the
                // shared centre line. Symmetric rotated marks use the grid
                // centre directly.
                shift = { x: 0, y: 0 };
                inkShiftCache.set(key, shift);
              } else {
              inkContext.font = style.fontStyle + ' ' + style.fontWeight + ' '
                + style.fontSize + ' ' + style.fontFamily;
              const metrics = inkContext.measureText(glyph);
              const advance = metrics.width;
              const left = metrics.actualBoundingBoxLeft;
              const right = metrics.actualBoundingBoxRight;
              const actualAscent = metrics.actualBoundingBoxAscent;
              const actualDescent = metrics.actualBoundingBoxDescent;
              const fontAscent = metrics.fontBoundingBoxAscent;
              const fontDescent = metrics.fontBoundingBoxDescent;
              const shiftX = Number.isFinite(advance)
                && advance > 0
                && Number.isFinite(left)
                && Number.isFinite(right)
                ? (advance - (right - left)) / 2
                : 0;
              // The browser centres the line box, not the painted ink.
              // Use the font box to recover the CSS baseline inside that
              // line box, then move the painted glyph so its actual ink
              // bounds have the same centre as the em cell. This matters
              // most for vertical presentation brackets, whose arc sits
              // toward opposite ends of the em by design.
              const shiftY = Number.isFinite(actualAscent)
                && Number.isFinite(actualDescent)
                && Number.isFinite(fontAscent)
                && Number.isFinite(fontDescent)
                ? (actualAscent + fontDescent - fontAscent - actualDescent) / 2
                : 0;
              shift = { x: shiftX, y: shiftY };
              inkShiftCache.set(key, shift);
              }
            }
            inner.style.setProperty(
              '--kkindle-vertical-ink-shift',
              shift.x.toFixed(3) + 'px',
              'important');
            inner.style.setProperty(
              '--kkindle-vertical-ink-shift-y',
              shift.y.toFixed(3) + 'px',
              'important');
            inner.dataset.kkindleVerticalOpticalShiftX = shift.x.toFixed(3);
            inner.dataset.kkindleVerticalOpticalShiftY = shift.y.toFixed(3);
          };
          body.querySelectorAll(
            '.kkindle-linux-vertical-cjk-ink, '
              + '.kkindle-linux-vertical-single > .kkindle-linux-vertical-cell-inner, '
              + '.kkindle-linux-vertical-single-punctuation > .kkindle-linux-vertical-cell-inner, '
              + '.kkindle-linux-vertical-number > .kkindle-linux-vertical-digit > .kkindle-linux-vertical-cell-inner, '
              + '.kkindle-linux-vertical-pair-punctuation > .kkindle-linux-vertical-cell-inner')
            .forEach(applyInkShift);

          // Keep the optical correction independent from the outer inline
          // span's DOMRect. WebKitGTK can report that parent rect from a stale
          // vertical line box after a font-size change; feeding its centre
          // back into the child's relative offset makes the glyph walk into a
          // neighbouring column every time the reader is zoomed. Canvas ink
          // bearings scale with the live computed font and are the stable
          // paint-only correction here.
          const restoreVerticalHanOpticalShift = () => body.querySelectorAll(
            '.kkindle-linux-vertical-cjk').forEach(cell => {
            const ink = cell.querySelector(':scope > .kkindle-linux-vertical-cjk-ink');
            if (!ink) return;
            const opticalX = parseFloat(ink.dataset.kkindleVerticalOpticalShiftX) || 0;
            const opticalY = parseFloat(ink.dataset.kkindleVerticalOpticalShiftY) || 0;
            ink.style.setProperty(
              '--kkindle-vertical-ink-shift',
              opticalX.toFixed(3) + 'px',
              'important');
            ink.style.setProperty(
              '--kkindle-vertical-ink-shift-y',
              opticalY.toFixed(3) + 'px',
              'important');
          });
          window.__kkindleVerticalRecenterHan = restoreVerticalHanOpticalShift;
          restoreVerticalHanOpticalShift();
          document.body.dataset.kkindleVerticalInlinePrepared = 'publication-native-compat-1';
          return true;
        })();
        """;

    // Draw layout boxes while investigating Linux vertical typography. Every
    // generated Han/digit/punctuation cell is outlined directly by CSS; only
    // native Latin/other publication text needs fixed Range rectangles in the
    // pointer-transparent layer. The refresh hooks remain layout-aware because
    // cover fitting and late font/image loads can move those native ranges.
    private const string PrepareVerticalDebugLayoutScript = """
        (() => {
          const body = document.body;
          const root = document.documentElement;
          if (!body || !root) return false;
          body.dataset.kkindleVerticalDebugBoxes = '1';

          let layer = document.getElementById('kkindle-vertical-debug-boxes');
          if (!layer) {
            layer = document.createElement('div');
            layer.id = 'kkindle-vertical-debug-boxes';
            layer.setAttribute('aria-hidden', 'true');
            layer.style.cssText = [
              'position:fixed',
              'left:0',
              'top:0',
              'width:0',
              'height:0',
              'margin:0',
              'padding:0',
              'border:0',
              'pointer-events:none',
              'z-index:2147483647',
              'writing-mode:horizontal-tb',
              'font-family:sans-serif'
            ].join(';');
            root.appendChild(layer);
          }

          const colors = {
            cjk: 'rgba(22, 163, 74, 0.92)',
            latin: 'rgba(124, 58, 237, 0.92)',
            digit: 'rgba(234, 88, 12, 0.92)',
            punctuation: 'rgba(219, 39, 119, 0.92)',
            other: 'rgba(107, 114, 128, 0.82)'
          };
          const classify = character => {
            if (/[A-Za-z]/.test(character)) return 'latin';
            if (/[0-9]/.test(character)) return 'digit';
            if (/[!"#$%&'()*+,\-./:;<=>?@[\\\]^_`{|}~，。！？、；：“”‘’（）《》〈〉【】〔〕［］｛｝…—]/.test(character))
              return 'punctuation';
            if (/[⺀-鿿豈-﫿]/.test(character)) return 'cjk';
            return 'other';
          };
          const isGenerated = node => !!node.closest?.(
            '.kkindle-linux-vertical-single, '
              + '.kkindle-linux-vertical-single-punctuation, '
              + '.kkindle-linux-vertical-cjk, '
              + '.kkindle-linux-vertical-tcy, '
              + '.kkindle-linux-vertical-number, '
              + '.kkindle-linux-vertical-pair-punctuation, '
              + '.kkindle-linux-vertical-footnote, '
              + '#kkindle-selection-bar, script, style, noscript');
          const isVisibleRect = rect => rect
            && rect.width > 0
            && rect.height > 0
            && Number.isFinite(rect.left)
            && Number.isFinite(rect.top);
          const appendFixedFrame = (fragment, rect, color, style = 'solid', kind = '') => {
            if (!isVisibleRect(rect)) return;
            const box = document.createElement('div');
            if (kind) box.dataset.kkindleVerticalDebugKind = kind;
            box.style.cssText = [
              'position:fixed',
              'left:' + rect.left.toFixed(2) + 'px',
              'top:' + rect.top.toFixed(2) + 'px',
              'width:' + rect.width.toFixed(2) + 'px',
              'height:' + rect.height.toFixed(2) + 'px',
              'box-sizing:border-box',
              'margin:0',
              'padding:0',
              'border:1px ' + style + ' ' + color,
              'pointer-events:none',
              'writing-mode:horizontal-tb'
            ].join(';');
            fragment.appendChild(box);
          };
          const queueRefresh = () => {
            if (queueRefresh.pending) return;
            queueRefresh.pending = true;
            requestAnimationFrame(() => {
              queueRefresh.pending = false;
              refresh();
            });
          };
          const refreshAfterLayout = () => {
            queueRefresh();
            requestAnimationFrame(() => {
              queueRefresh();
              requestAnimationFrame(queueRefresh);
            });
            // Image decode and the cover-fit pass can settle after two
            // animation frames on WebKitGTK. This delayed pass is still
            // bounded and only runs while the debug overlay is enabled.
            setTimeout(queueRefresh, 120);
          };
          const refresh = () => {
            const fragment = document.createDocumentFragment();
            const legend = document.createElement('div');
            legend.style.cssText = [
              'position:fixed',
              'left:8px',
              'top:8px',
              'padding:3px 5px',
              'border:1px solid rgba(17,24,39,.65)',
              'background:rgba(255,255,255,.88)',
              'color:#111',
              'font:11px/1.2 sans-serif',
              'white-space:nowrap',
              'writing-mode:horizontal-tb'
            ].join(';');
            legend.textContent = '红=兼容字格 蓝=内部字形框 绿=汉字 紫=英文 橙=数字 粉=标点';
            fragment.appendChild(legend);

            // Han/digit/punctuation frames are CSS outlines on their real
            // one-em layout cells. This fixed layer is only for native text
            // ranges (Latin and other unwrapped publication content).

            const viewportWidth = window.innerWidth || root.clientWidth || 0;
            const viewportHeight = window.innerHeight || root.clientHeight || 0;
            const walker = document.createTreeWalker(body, NodeFilter.SHOW_TEXT);
            let drawn = 0;
            for (let node = walker.nextNode(); node && drawn < 1400; node = walker.nextNode()) {
              const parent = node.parentElement;
              if (!parent || isGenerated(parent)) continue;
              const text = node.nodeValue || '';
              for (let index = 0; index < text.length && drawn < 1400; index++) {
                if (/\s/.test(text[index])) continue;
                const range = document.createRange();
                range.setStart(node, index);
                range.setEnd(node, index + 1);
                for (const rect of range.getClientRects()) {
                  if (!(rect.width > 0) || !(rect.height > 0)
                      || rect.right < 0 || rect.bottom < 0
                      || rect.left > viewportWidth || rect.top > viewportHeight)
                    continue;
                  appendFixedFrame(
                    fragment,
                    rect,
                    colors[classify(text[index])],
                    'solid',
                    classify(text[index]));
                  drawn++;
                }
              }
            }
            layer.replaceChildren(fragment);
          };
          queueRefresh.pending = false;
          window.__kkindleVerticalDebugQueueRefresh = queueRefresh;
          window.__kkindleVerticalDebugRefresh = refreshAfterLayout;
          if (window.__kkindleVerticalDebugListenersBound !== true) {
            window.__kkindleVerticalDebugListenersBound = true;
            const refreshLiveLayout = () =>
              window.__kkindleVerticalDebugQueueRefresh?.();
            const refreshLoadedLayout = () =>
              window.__kkindleVerticalDebugRefresh?.();
            window.__kkindleVerticalDebugLiveLayoutHandler = refreshLiveLayout;
            window.__kkindleVerticalDebugLoadHandler = refreshLoadedLayout;
            window.addEventListener('scroll', refreshLiveLayout, true);
            window.addEventListener('resize', refreshLiveLayout, true);
            window.addEventListener('load', refreshLoadedLayout, true);
            document.fonts?.ready?.then(() =>
              window.__kkindleVerticalDebugRefresh?.()).catch(() => {});
          }
          window.__kkindleVerticalDebugResizeObserver?.disconnect?.();
          if (typeof ResizeObserver === 'function') {
            const observer = new ResizeObserver(() =>
              window.__kkindleVerticalDebugQueueRefresh?.());
            observer.observe(root);
            observer.observe(body);
            window.__kkindleVerticalDebugResizeObserver = observer;
          }
          window.__kkindleVerticalDebugMutationObserver?.disconnect?.();
          if (typeof MutationObserver === 'function') {
            const observer = new MutationObserver(() =>
              window.__kkindleVerticalDebugQueueRefresh?.());
            observer.observe(body, {
              subtree: true,
              attributes: true,
              attributeFilter: ['class', 'style']
            });
            window.__kkindleVerticalDebugMutationObserver = observer;
          }
          refreshAfterLayout();
          return true;
        })();
        """;

    // The settings switch must remove more than the visible layer: leaving
    // observers and capture listeners alive would keep rebuilding detached
    // debug rectangles after the user turns the feature off.
    private const string RemoveVerticalDebugLayoutScript = """
        (() => {
          document.body?.removeAttribute('data-kkindle-vertical-debug-boxes');
          document.getElementById('kkindle-vertical-debug-boxes')?.remove();
          window.__kkindleVerticalDebugResizeObserver?.disconnect?.();
          window.__kkindleVerticalDebugMutationObserver?.disconnect?.();
          const liveHandler = window.__kkindleVerticalDebugLiveLayoutHandler;
          const loadHandler = window.__kkindleVerticalDebugLoadHandler;
          if (liveHandler) {
            window.removeEventListener('scroll', liveHandler, true);
            window.removeEventListener('resize', liveHandler, true);
          }
          if (loadHandler)
            window.removeEventListener('load', loadHandler, true);
          window.__kkindleVerticalDebugResizeObserver = null;
          window.__kkindleVerticalDebugMutationObserver = null;
          window.__kkindleVerticalDebugLiveLayoutHandler = null;
          window.__kkindleVerticalDebugLoadHandler = null;
          window.__kkindleVerticalDebugQueueRefresh = null;
          window.__kkindleVerticalDebugRefresh = null;
          window.__kkindleVerticalDebugListenersBound = false;
          return true;
        })();
        """;

    private const string RevealReaderDocumentScript = """
        (() => {
          const root = document.documentElement;
          const body = document.body;
          if (!root || !body) return false;
          root.style.setProperty('visibility', 'visible', 'important');
          root.style.setProperty('opacity', '1', 'important');
          body.style.setProperty('visibility', 'visible', 'important');
          body.style.setProperty('opacity', '1', 'important');
          window.__kkindleReaderConfigured = true;
          return true;
        })();
        """;

    private ReaderLayoutSettings _readerLayout = NormalizeReaderLayoutForPlatform(new ReaderLayoutSettings());
    private bool _readerVerticalDebugBoxesEnabled =
        Environment.GetEnvironmentVariable("KKINDLE_VERTICAL_DEBUG_BOXES") == "1";

    private bool ShouldShowReaderVerticalDebugBoxes() =>
        _readerLayout.VerticalWriting
        && _readerVerticalDebugBoxesEnabled;

    private void LoadReaderVerticalDebugBoxesSetting()
    {
        _readerVerticalDebugBoxesEnabled =
            _appSettings.ReaderVerticalDebugBoxesEnabled
            ?? Environment.GetEnvironmentVariable("KKINDLE_VERTICAL_DEBUG_BOXES") == "1";
    }

    private int _readerPageAnimation = ReaderAnimationFade;
    private readonly SemaphoreSlim _readerPageTurnGate = new(1, 1);
    private readonly SemaphoreSlim _readerLayoutGate = new(1, 1);
    private readonly SemaphoreSlim _readerBundledFontGate = new(1, 1);
    private CancellationTokenSource? _readerRelayoutCancellation;
    private IReaderHost? _readerPendingRelayoutHost;
    private ReaderScrollState? _readerPendingRelayoutState;
    private int _readerPendingKeyboardNavigation;
    private IReadOnlyList<EpubReaderNavigationItem> _readerTocItems = [];
    private ReaderProgressRow? _readerRestoredProgress;
    private int _readerSearchSequence;
    private int _readerSearchCount;
    private int _readerSearchIndex = -1;
    private int? _readerPendingChunkOffset;
    private string? _readerPendingSearchQuery;
    private string? _readerPendingSearchContext;
    private int _readerBookmarkIndicatorSequence;
    private int _readerFootnoteHoverSequence;
    private bool _readerFootnotePollRunning;
    private int _readerFootnoteHoverMissCount;
    private string? _readerFootnoteHref;
    private Point? _readerFootnotePlacementPoint;
    private PixelPoint? _readerFootnoteAnchorScreenPoint;
    private DispatcherTimer? _readerFootnoteHoverTimer;
    private DispatcherTimer? _readerSelectionHighlightPointerTimer;
    private long _readerSelectionHighlightOpenedTick;
    private int _readerSelectionHighlightOutsideTicks;
    private bool _readerLinuxTextFallbackUpdating;
    private bool _readerLinuxTextFallbackPointerPressed;
    private bool _readerLinuxTextFallbackSelectionAtPointerPress;
    private bool _readerLinuxTextFallbackSelectionDismissPress;
    private bool _readerLinuxVerticalFootnoteHandledRelease;
    private int _readerLinuxTextFallbackSelectionSyncSequence;
    private Point _readerLinuxTextFallbackPointerStart;
    private Vector _readerLinuxTextFallbackSelectionScrollOffset;
    private bool _readerLinuxTextFallbackSelectionScrollLocked;
    private bool _readerLinuxTextFallbackRestoringSelectionScroll;
    private bool _readerLinuxTextFallbackMoveToChapterEnd;
    private int _readerLinuxTextFallbackWheelDeltaRemainder;
    private int _readerLinuxTextFallbackContinuousWheelDirection;
    private long _readerLinuxTextFallbackContinuousWheelLastTick;
    private string? _readerLinuxTextFallbackTargetTitle;
    private string? _readerLinuxTextFallbackEndFragment;
    private string _readerLinuxTextFallbackText = string.Empty;
    private List<string> _readerLinuxTextFallbackPages = [];
    private List<ReaderLinuxTextFallbackPageItem> _readerLinuxTextFallbackPageItems = [];
    public ObservableCollection<ReaderLinuxTextFallbackBlock> ReaderLinuxTextFallbackBlocks { get; } = [];
    public ObservableCollection<ReaderLinuxTextFallbackImage> ReaderLinuxTextFallbackImages { get; } = [];
    private int _readerLinuxTextFallbackPageIndex;
    private int? _readerLinuxTextFallbackPendingReflowAnchor;
    private int _readerLinuxTextFallbackReflowSequence;
    private string? _readerPendingBookmarkQuote;
    private int? _readerPendingBookmarkPosition;
    private int _readerPendingBookmarkFlowMode;
    private string? _readerCurrentFragment;
    private ReaderAnnotation? _readerPendingAnnotation;
    private bool _suppressReaderTocSelectionNavigation;
    private readonly SemaphoreSlim _readerSearchMutationGate = new(1, 1);
    private string? _readerPendingSelection;
    private int _readerPendingSelectionStartOffset;
    private int _readerPendingSelectionEndOffset;
    private string _readerPendingSelectionPrefix = string.Empty;
    private string _readerPendingSelectionSuffix = string.Empty;
    private double _readerScrollPosition;
    private double _readerScrollRatio;
    private double _readerScrollWidth;
    private double _readerScrollHeight;
    private double _readerClientWidth;
    private double _readerClientHeight;
    private int[] _readerBookPageCounts = [];
    private CancellationTokenSource? _readerBookPageCountCancellation;
    private int _readerBookPageCountSequence;
    private DateTimeOffset _readerSessionStarted;
    private WindowState _readerWindowStateBeforeZen = WindowState.Normal;
    private bool _readerZenMode;
    private bool _readerAssistantVisibleBeforeZen = true;
    private bool _readerTocExpandedBeforeZen = true;
    private bool _readerTocMinimalBeforeZen;
    private long _readerActiveSeconds;
    private long _readerSessionSeconds;
    private long _readerStatsBaseSeconds;
    private DispatcherTimer? _readerStatsTimer;
    private readonly SemaphoreSlim _readerStatsFlushGate = new(1, 1);
    private int _readerTransientStatusSequence;
    private bool _readerContinuousLocked;
    private int _readerContinuousDirection;
    private bool _readerLastNearTop;
    private bool _readerLastNearBottom;
    private bool _readerContinuousPositionInitialized;
    private double _readerPreviousScrollPosition;
    private DateTimeOffset _readerLastChapterChange = DateTimeOffset.MinValue;
    private bool _readerScrollPollRunning;
    private int _readerWheelDeltaRemainder;
    private int _readerContinuousSkipDepth;
    private int _readerProgressSaveSequence;
    private bool _readerProgressSliderUpdating;
    private bool _readerAiBusy;
    private bool _suppressAiProviderChange;
    private bool _suppressAiModelChange;
    private bool _suppressAiReasoningDepthChange;
    private string _readerAiReasoningDepth = "auto";
    private readonly List<AiConversationTurn> _readerAiConversation = [];
    private CancellationTokenSource? _readerAiCancellation;
    private AiConnectionSettings _readerAiSettings = new();
    private IReadOnlyList<string> _readerAiAvailableModels = [];
    private IReadOnlyList<PdfPageText> _readerPdfPages = [];
    private int _readerPdfPage = 1;
    private string? _readerPdfSourcePath;
    private bool _readerIsPdf;
    private ReaderAnnotation? _selectedReaderAnnotation;

    private sealed record ReaderScrollState(
        double Position,
        double Ratio,
        double ScrollWidth,
        double ScrollHeight,
        double ClientWidth,
        double ClientHeight);

    public sealed class ReaderLinuxTextFallbackImage : INotifyPropertyChanged, IDisposable
    {
        private double _maxWidth;
        private double _maxHeight;

        public ReaderLinuxTextFallbackImage(Bitmap source, double maxWidth, double maxHeight)
        {
            Source = source;
            _maxWidth = maxWidth;
            _maxHeight = maxHeight;
        }

        public Bitmap Source { get; }

        public double MaxWidth
        {
            get => _maxWidth;
            private set
            {
                if (Math.Abs(_maxWidth - value) < 0.1) return;
                _maxWidth = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MaxWidth)));
            }
        }

        public double MaxHeight
        {
            get => _maxHeight;
            private set
            {
                if (Math.Abs(_maxHeight - value) < 0.1) return;
                _maxHeight = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MaxHeight)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public void Resize(double maxWidth, double maxHeight)
        {
            MaxWidth = maxWidth;
            MaxHeight = maxHeight;
        }

        public void Dispose() => Source.Dispose();
    }

    public sealed class ReaderLinuxTextFallbackBlock : INotifyPropertyChanged
    {
        private double _width = double.NaN;

        public ReaderLinuxTextFallbackBlock(
            string text,
            int textOffset,
            double fontSize,
            double lineHeight,
            double maxWidth,
            bool isChapterTitle = false)
        {
            Text = text;
            TextOffset = textOffset;
            FontSize = fontSize;
            LineHeight = lineHeight;
            MaxWidth = maxWidth;
            IsChapterTitle = isChapterTitle;
        }

        public ReaderLinuxTextFallbackBlock(ReaderLinuxTextFallbackImage image)
        {
            Image = image;
        }

        internal ReaderLinuxTextFallbackBlock(
            string text,
            int textOffset,
            double fontSize,
            double lineHeight,
            double maxWidth,
            IReadOnlyList<ReaderLinuxTextFallbackFootnote> inlineFootnotes,
            bool isChapterTitle = false)
            : this(text, textOffset, fontSize, lineHeight, maxWidth, isChapterTitle)
        {
            InlineFootnotes = inlineFootnotes;
        }

        public ReaderLinuxTextFallbackBlock(string footnoteLabel, string footnoteHref)
        {
            FootnoteLabel = string.IsNullOrWhiteSpace(footnoteLabel) ? UiText.Get("注") : footnoteLabel.Trim();
            FootnoteHref = footnoteHref;
        }

        public string Text { get; } = string.Empty;
        public int TextOffset { get; }
        public double FontSize { get; }
        public double LineHeight { get; }
        public double MaxWidth { get; }
        public bool IsChapterTitle { get; }
        public FontWeight DisplayFontWeight =>
            IsChapterTitle ? FontWeight.Bold : FontWeight.Normal;
        public TextAlignment DisplayTextAlignment =>
            IsChapterTitle ? TextAlignment.Center : TextAlignment.Left;
        public Thickness DisplayMargin =>
            IsText
                ? IsChapterTitle
                    ? new Thickness(
                        0,
                        LineHeight,
                        0,
                        LineHeight)
                    : new Thickness(0)
                : new Thickness(0, LineHeight, 0, LineHeight);
        public ReaderLinuxTextFallbackImage? Image { get; }
        public string FootnoteLabel { get; } = string.Empty;
        public string FootnoteHref { get; } = string.Empty;
        internal IReadOnlyList<ReaderLinuxTextFallbackFootnote> InlineFootnotes { get; } = [];
        public IReadOnlyList<ReaderLinuxTextFallbackAnnotationRange> AnnotationRanges { get; private set; } = [];
        internal bool HasInlineFootnotes => InlineFootnotes.Count > 0;
        public bool IsText => Image is null && string.IsNullOrWhiteSpace(FootnoteHref);
        public bool IsImage => Image is not null;
        public bool IsFootnote => !string.IsNullOrWhiteSpace(FootnoteHref);

        /// <summary>
        /// Fixed layout width of the selectable paragraph. Wrapping must not
        /// depend on the control's content-derived DesiredSize: SelectableTextBlock
        /// re-measures when a selection starts or ends on a line boundary, and a
        /// content-derived width makes that re-measure re-wrap the paragraph.
        /// </summary>
        public double Width
        {
            get => _width;
            private set
            {
                if (double.IsNaN(_width) && double.IsNaN(value)) return;
                if (!double.IsNaN(_width) && !double.IsNaN(value) && Math.Abs(_width - value) < 0.1) return;
                _width = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Width)));
            }
        }

        public void ResizeText(double width) => Width = width;

        internal void SetAnnotationRanges(IReadOnlyList<ReaderLinuxTextFallbackAnnotationRange> ranges)
        {
            AnnotationRanges = ranges;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AnnotationRanges)));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private sealed record ReaderLinuxTextFallbackRawBlock(
        string? Text,
        string? ImagePath,
        string? FootnoteHref = null,
        string? FootnoteLabel = null,
        bool IsChapterTitle = false,
        bool IsParagraphBoundary = false);

    private sealed record ReaderLinuxTextFallbackExtractedContent(
        string Text,
        IReadOnlyList<ReaderLinuxTextFallbackRawBlock> Blocks);

    internal sealed record ReaderLinuxTextFallbackFootnote(
        string Label,
        string Href);

    public sealed record ReaderLinuxTextFallbackAnnotationRange(
        int Start,
        int Length,
        string Style,
        string Color);

    private sealed record ReaderLinuxTextFallbackPageItem(
        string Text,
        int TextOffset,
        ReaderLinuxTextFallbackImage? Image,
        IReadOnlyList<ReaderLinuxTextFallbackFootnote>? Footnotes = null,
        int ChapterTitleStart = -1,
        int ChapterTitleLength = 0,
        int PaginationOffset = 0,
        bool StartsParagraph = false)
    {
        public IReadOnlyList<ReaderLinuxTextFallbackFootnote> InlineFootnotes { get; } =
            Footnotes ?? [];

        public bool IsImage => Image is not null;
        public bool HasInlineFootnotes => InlineFootnotes.Count > 0;
        public bool HasChapterTitle =>
            ChapterTitleStart >= 0 && ChapterTitleLength > 0;
    }

    /// <summary>
    /// Vertical paginated reading runs the application-side page composer:
    /// the chapter is banked once and pages are composed on demand, instead
    /// of the whole chapter flowing as one 100k-element document.
    /// </summary>
    private bool IsReaderPageComposeMode =>
        !_readerIsPdf && _readerLayout.VerticalWriting && _readerLayout.FlowMode == 1;

    /// <summary>
    /// Resolves the character offset the composed page should open at. New
    /// page-mode saves persist the page-start character offset in
    /// ScrollPosition; legacy pixel saves fall back to the chapter start.
    /// </summary>
    private static ReaderLayoutSettings NormalizeReaderLayoutForPlatform(ReaderLayoutSettings settings)
    {
        // Vertical writing is a paginated layout on every platform, including
        // Linux WebKitGTK. The vertical page step, edge masks and glyph-phase
        // probes are calibrated per viewport by VerticalStepExpression, so
        // fonts, DPI and native side-panel resizing re-resolve the geometry
        // instead of being hard platform limitations.
        return ReaderLayoutDefaults.Normalize(settings);
    }

    public ObservableCollection<ReaderBookmark> ReaderBookmarks { get; } = [];
    public ObservableCollection<ReaderAnnotation> ReaderAnnotations { get; } = [];
    public ObservableCollection<ReaderSearchResultViewModel> ReaderSearchResults { get; } = [];
    public ObservableCollection<ReaderAiMessageViewModel> ReaderAiMessages { get; } = [];
    public ObservableCollection<ReaderAiSourceViewModel> ReaderAiSources { get; } = [];

    private void ClearReaderAiCollections()
    {
        foreach (var message in ReaderAiMessages)
            message.Dispose();
        foreach (var source in ReaderAiSources)
            source.Dispose();
        ReaderAiMessages.Clear();
        ReaderAiSources.Clear();
    }

    private async Task InitializeReaderInteractionAsync(
        EpubReaderDocument document,
        BookFile file,
        CancellationToken cancellationToken)
    {
        var settings = await _readerData.GetLayoutSettingsAsync(file.Id, cancellationToken);
        var bookLayout = settings ?? _appSettings.DefaultReaderLayout;
        // Typography and margins remain per-book, but writing direction and
        // paragraph indentation are global reader preferences. Ignore stale
        // per-book values left by older builds so opening another book cannot
        // silently change either global option.
        _readerLayout = NormalizeReaderLayoutForPlatform(
            ReaderLayoutDefaults.ApplyGlobalPreferences(
                bookLayout,
                _appSettings.DefaultReaderLayout));
        _readerTocItems = BuildReaderNavigationItems(document);
        _readerRestoredProgress = null;
        _readerBookmarkIndicatorSequence++;
        _readerPendingBookmarkQuote = null;
        _readerPendingBookmarkPosition = null;
        _readerPendingBookmarkFlowMode = 0;
        _readerCurrentFragment = null;
        _readerPendingAnnotation = null;
        _readerPendingSelection = null;
        _readerPendingSelectionStartOffset = 0;
        _readerPendingSelectionEndOffset = 0;
        _readerPendingSelectionPrefix = string.Empty;
        _readerPendingSelectionSuffix = string.Empty;
        _readerScrollPosition = 0;
        _readerScrollRatio = 0;
        _readerScrollWidth = 0;
        _readerScrollHeight = 0;
        _readerClientWidth = 0;
        _readerClientHeight = 0;
        _readerBookPageCountCancellation?.Cancel();
        _readerBookPageCountCancellation?.Dispose();
        _readerBookPageCountCancellation = null;
        _readerBookPageCounts = [];
        _readerBookPageCountSequence++;
        _readerSearchCount = 0;
        _readerSearchIndex = -1;
        _readerPendingChunkOffset = null;
        _readerPendingSearchQuery = null;
        _readerPendingSearchContext = null;
        _readerSearchSequence++;
        _readerWholeSearchSequence++;
        _readerSearchVisible = false;
        _readerSearchLayoutCaptured = false;
        _readerSearchQuery = string.Empty;
        ClearReaderSearchResultSelection();
        _readerContinuousSkipDepth = 0;
        _readerLinuxTextFallbackMoveToChapterEnd = false;
        _readerContinuousLocked = false;
        _readerContinuousDirection = 0;
        ResetReaderContinuousEdgeTracking();
        _readerScrollPollRunning = false;
        _readerWheelDeltaRemainder = 0;
        _readerLinuxTextFallbackWheelDeltaRemainder = 0;
        Interlocked.Exchange(ref _readerPendingKeyboardNavigation, 0);
        _readerPdfSearchSequence++;
        _readerSessionStarted = DateTimeOffset.UtcNow;
        _readerZenMode = false;
        _readerAssistantVisibleBeforeZen = true;
        _readerIsPdf = false;
        UpdateReaderBookmarkCornerSurface();
        _readerPdfPages = [];
        _readerPdfPage = 1;
        _readerTocExpanded = true;
        _readerTocMinimal = false;
        _readerTocExpandedBeforeZen = true;
        _readerTocMinimalBeforeZen = false;
        _readerActiveSeconds = 0;
        _readerSessionSeconds = 0;
        _readerStatsBaseSeconds = 0;
        ReaderBookInfoText.Text = _readerBookCard?.Title ?? UiText.Get("目录");
        ReaderTocList.ItemsSource = _readerTocItems;
        ReaderTocList.SelectedIndex = -1;
        ReaderTocPanel.IsVisible = false;
        ReaderTocView.IsVisible = true;
        ReaderBookmarkPane.IsVisible = false;
        ReaderSearchPanel.IsVisible = false;
        ReaderBookmarkEmptyText.IsVisible = ReaderBookmarks.Count == 0;
        ReaderSearchResults.Clear();
        ReaderWholeSearchCountText.Text = string.Empty;
        ReaderSearchStatusText.IsVisible = true;
        ReaderSearchResultList.IsVisible = false;
        ReaderInPageSearchBar.IsVisible = false;
        ReaderLayoutSettingsPopup.IsOpen = false;
        HideReaderFootnotePopup();
        HideReaderAnnotationHoverPopup();
        ReaderBookmarkCornerMarker.IsVisible = false;
        ClearReaderAiCollections();
        ReaderAiEmptyState.IsVisible = true;
        ReaderAiView.IsVisible = true;
        ReaderNotesView.IsVisible = false;
        ReaderAiSettingsView.IsVisible = false;
        ReaderAiComposer.IsVisible = true;
        ReaderAiSendBar.IsVisible = true;
        ReaderNotesExportBar.IsVisible = false;
        ReaderAssistantPanel.IsVisible = false;
        ReaderRoot.ColumnDefinitions[2].Width = new GridLength(0);
        SetReaderCompactNavigationItems(_readerTocItems);
        SetReaderCompactSelectedItem(
            _readerTocItems.FirstOrDefault(item => item.ChapterIndex == _readerChapterIndex));
        ApplyReaderPanelLayout();
        UpdateReaderZoomLabel();
        await RefreshReaderBookmarksAsync(cancellationToken);
        await RefreshReaderAnnotationsAsync(cancellationToken);
        await InitializeReaderAiAsync(cancellationToken);
        UpdateReaderToolbar();
        await LoadReaderStatsBaseAsync();
        StartReaderStatsTimer();
        StartReaderFootnoteHoverPoll();
    }

    private static IReadOnlyList<EpubReaderNavigationItem> BuildReaderNavigationItems(
        EpubReaderDocument document)
        => EpubReaderNavigationPolicy.Build(
            document,
            UiText.Get("封面"),
            UiText.Get("目录"),
            chapterIndex => GetPreparedChapterTitle(document, chapterIndex));

    private static string GetPreparedChapterTitle(EpubReaderDocument document, int chapterIndex)
    {
        if (chapterIndex >= 0
            && chapterIndex < document.ChapterTitles.Count
            && !string.IsNullOrWhiteSpace(document.ChapterTitles[chapterIndex]))
        {
            return document.ChapterTitles[chapterIndex].Trim();
        }
        return chapterIndex == 0 ? UiText.Get("封面") : UiText.Get("第 {0} 章", chapterIndex + 1);
    }

    private async Task ConfigureReaderHostAsync(
        IReaderHost host,
        CancellationToken cancellationToken)
    {
        var revealTiming = System.Diagnostics.Stopwatch.StartNew();
        // The injected bridge hides the document on every navigation so the
        // fallback-font/vertical-cell transition is never visible. That makes
        // configuration the only thing that can put a chapter back on screen:
        // if it is cancelled or throws part way through — a superseded
        // navigation, a rapid chapter turn, a failed InvokeScript — the reader
        // would stay permanently blank. Reveal on every exit path.
        try
        {
            await ConfigureReaderHostCoreAsync(host, cancellationToken);
        }
        finally
        {
            if (!_readerIsPdf)
            {
                try
                {
                    await host.InvokeScriptAsync(RevealReaderDocumentScript);
                    LogReaderChapterTiming("cfg.revealed", revealTiming);
                }
                catch
                {
                    // The document can already be gone when the navigation was
                    // superseded. The next configuration reveals the new one.
                }
            }
        }
    }

    private async Task ConfigureReaderHostCoreAsync(
        IReaderHost host,
        CancellationToken cancellationToken)
    {
        var configTiming = System.Diagnostics.Stopwatch.StartNew();
        cancellationToken.ThrowIfCancellationRequested();
        if (host is not NativeReaderHost nativeReader)
        {
            // Only PDF still renders inside the platform webview viewer;
            // there is no reader document to configure there.
            return;
        }

        var restoreNative = ReferenceEquals(host, CurrentReaderHost)
            && _readerRestoredProgress is { } pendingNative
            && pendingNative.ChapterIndex == _readerChapterIndex;
        await nativeReader.Configure(
            _readerLayout,
            _readerScrollPosition,
            _readerCurrentFragment,
            restoreNative,
            showVerticalDebugBoxes: ShouldShowReaderVerticalDebugBoxes());
        if (restoreNative)
        {
            _readerRestoredProgress = null;
        }

        await ApplySavedAnnotationsAsync(host, cancellationToken);
        LogReaderChapterTiming("cfg.configured", configTiming);
    }
    private const string FitReaderCoverImageScript = """
        (() => {
          const root = document.documentElement;
          const body = document.body;
          if (!root || !body) return false;
          const fit = () => {
            const viewWidth = root.clientWidth || window.innerWidth || 0;
            const viewHeight = root.clientHeight || window.innerHeight || 0;
            if (viewWidth <= 0 || viewHeight <= 0) return false;

            const bodyStyle = getComputedStyle(body);
            const paddingTop = parseFloat(bodyStyle.paddingTop) || 0;
            const paddingBottom = parseFloat(bodyStyle.paddingBottom) || 0;
            const viewportHeight = root.clientHeight || window.innerHeight || body.clientHeight || 0;
            const contentHeight = viewportHeight - paddingTop - paddingBottom;
            if (contentHeight > 0)
              root.style.setProperty('--kkindle-page-content-h', contentHeight + 'px');

            const candidates = Array.from(
              document.querySelectorAll('body img, body svg, body svg image'));
            const shortImagePage = (body.innerText || body.textContent || '').trim().length <= 120
              && candidates.length > 0
              && candidates.length <= 4;
            // Cover pages converted from TXT often keep a bare caption such
            // as "封面" above the raster. The reading surface must show the
            // cover alone, so drop block-level captions whose entire text is
            // that label; sentences elsewhere are never touched because the
            // rule only runs on short image-only pages.
            if (shortImagePage) {
              document.querySelectorAll(
                'body p, body h1, body h2, body h3, body h4, body h5, body h6, body div, body span')
                .forEach(element => {
                  if (element.querySelector('img, svg, image')) return;
                  const label = (element.textContent || '').replace(/\s+/g, '');
                  if (label === '封面')
                    element.style.setProperty('display', 'none', 'important');
                });
            }
            for (let index = 0; index < candidates.length; index++) {
              const element = candidates[index];
              const tag = element.tagName.toLowerCase();
              const isSvgImage = tag === 'image';
              const rect = element.getBoundingClientRect?.() || { width: 0, height: 0 };
              const naturalWidth = element.naturalWidth
                || parseFloat(element.getAttribute('width')) || rect.width || 0;
              const naturalHeight = element.naturalHeight
                || parseFloat(element.getAttribute('height')) || rect.height || 0;
              if (naturalWidth <= 0 || naturalHeight <= 0) {
                if (tag === 'img' && !element.dataset.kkindleCoverLoadWatch) {
                  element.dataset.kkindleCoverLoadWatch = '1';
                  element.addEventListener('load', () => requestAnimationFrame(fit), { once: true });
                }
                continue;
              }

              const viewportArea = viewWidth * viewHeight;
              const largeImage = naturalWidth * naturalHeight >= viewportArea * 0.35
                || (naturalWidth >= viewWidth * 0.6 && naturalHeight >= viewHeight * 0.6);
              if (!largeImage && !(shortImagePage && index === 0)) continue;
              element.classList.add('kkindle-cover');
              if (isSvgImage && element.parentElement
                  && /^svg$/i.test(element.parentElement.tagName))
                element.parentElement.classList.add('kkindle-cover');
              return true;
            }
            return false;
          };
          window.__kkindleFitReaderCoverImage = fit;
          if (!window.__kkindleFitReaderCoverImageResizeWatch) {
            window.__kkindleFitReaderCoverImageResizeWatch = true;
            window.addEventListener('resize', () => requestAnimationFrame(fit), { passive: true });
          }
          const fitted = fit();
          requestAnimationFrame(fit);
          return fitted;
        })();
        """;

    // A WebView navigation completion only means that the document itself is
    // ready; it does not mean a font introduced by the injected style has
    // finished downloading. Keep the native host hidden while FontFaceSet
    // settles so a TOC swap cannot reveal fallback text and then reflow into
    // the bundled reading font.
    private static async Task WaitForReaderFontsAsync(
        IReaderHost host,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 24;
        const int delayMilliseconds = 25;
        const string waitScript = """
            (() => {
              const fonts = document.fonts;
              if (!fonts) return 'ready';
              if (!window.__kkindleReaderFontWaitStarted) {
                window.__kkindleReaderFontWaitStarted = true;
                const requests = [fonts.ready];
                try {
                  // Explicitly request the bundled family. It is the fallback
                  // used by the default reader stack and this also starts the
                  // load when the page's own CSS did not specify a font face.
                  requests.push(fonts.load('400 1em "KkindleKingHwaOldSong"', '祖母的退化论有人挠 AaWwii'));
                } catch (_) { }
                Promise.allSettled(requests).then(() => {
                  window.__kkindleReaderFontReady = true;
                });
              }
              return window.__kkindleReaderFontReady === true ? 'ready' : 'pending';
            })();
            """;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var result = await host.InvokeScriptAsync(waitScript);
                if (string.Equals(
                    result?.Trim().Trim('"'),
                    "ready",
                    StringComparison.OrdinalIgnoreCase))
                    return;
            }
            catch
            {
                // A fixed-layout or partially initialized document may reject
                // FontFaceSet access. The style has still been applied, so do
                // not turn a cosmetic wait into a chapter navigation failure.
                return;
            }

            await Task.Delay(delayMilliseconds, cancellationToken);
        }
    }

    // Fallback stack for the reading font, mirroring the WinUI reference's
    // BuildReaderFontStack: the chosen family first (comma-separated combined
    // families like "Source Han Serif SC, Noto Serif CJK SC" are split), then
    // the bundled KingHwaOldSong, common serif CJK fonts and sans-serif.
    private static string BuildReaderFontStack(string? fontFamily)
    {
        var families = new List<string>();
        void Add(string? family)
        {
            var value = family?.Trim();
            if (string.IsNullOrWhiteSpace(value)) return;
            // The settings label is the human-facing family name, while the
            // bundled TTF exposes the stable internal family name below.
            // Map the default explicitly so it cannot resolve to a different
            // system font before the app asset gets a chance to load.
            if (string.Equals(value, ReaderFontDefaults.DefaultFamily, StringComparison.OrdinalIgnoreCase))
                value = ReaderWebBundledFontFamily;
            if (families.Any(existing => string.Equals(existing, value, StringComparison.OrdinalIgnoreCase)))
                return;
            families.Add(value);
        }
        if (!string.IsNullOrWhiteSpace(fontFamily))
        {
            foreach (var part in fontFamily.Split(
                ',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                Add(part);
            }
        }
        Add(ReaderWebBundledFontFamily);
        Add("Source Han Serif SC");
        Add("Noto Serif CJK SC");
        if (OperatingSystem.IsLinux())
        {
            Add("Noto Sans CJK SC");
            Add("Source Han Sans SC");
            Add("DejaVu Sans");
        }
        else
        {
            Add("Microsoft YaHei UI");
        }
        families.Add("sans-serif");
        return string.Join(", ", families.Select(family => $"\"{family}\""));
    }

    private async Task<string?> GetBundledFontUriAsync(
        IReaderHost host,
        CancellationToken cancellationToken)
    {
        string sourcePath;
        try
        {
            sourcePath = Path.Combine(
                AppContext.BaseDirectory,
                "Assets",
                "Fonts",
                ReaderFontDefaults.BundledFontFileName);
        }
        catch
        {
            return null;
        }

        if (!File.Exists(sourcePath)) return null;

        var document = _readerDocument;
        if (document is null || string.IsNullOrWhiteSpace(document.RootPath))
            return new Uri(sourcePath).AbsoluteUri;

        string rootPath;
        string targetDirectory;
        string targetPath;
        try
        {
            rootPath = Path.GetFullPath(document.RootPath);
            targetDirectory = rootPath;
            if (!OperatingSystem.IsLinux()
                && host.Source is { IsFile: true } chapterSource
                && IsPathInside(rootPath, chapterSource.LocalPath))
            {
                var chapterDirectory = Path.GetDirectoryName(Path.GetFullPath(chapterSource.LocalPath));
                if (!string.IsNullOrWhiteSpace(chapterDirectory))
                    targetDirectory = chapterDirectory;
            }
            targetPath = Path.Combine(targetDirectory, ReaderBundledFontTargetFileName);
        }
        catch
        {
            return new Uri(sourcePath).AbsoluteUri;
        }

        await _readerBundledFontGate.WaitAsync(cancellationToken);
        try
        {
            var sourceLength = new FileInfo(sourcePath).Length;
            var targetInfo = new FileInfo(targetPath);
            if (!targetInfo.Exists || targetInfo.Length != sourceLength)
            {
                await using var input = new FileStream(
                    sourcePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 64 * 1024,
                    options: FileOptions.Asynchronous | FileOptions.SequentialScan);
                await using var output = new FileStream(
                    targetPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.Read,
                    bufferSize: 64 * 1024,
                    options: FileOptions.Asynchronous | FileOptions.SequentialScan);
                await input.CopyToAsync(output, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Keep a readable fallback if a read-only cache or a transient
            // filesystem error prevents the same-origin copy.
            return new Uri(sourcePath).AbsoluteUri;
        }
        finally
        {
            _readerBundledFontGate.Release();
        }

        if (host.Source is { IsFile: true } source
            && IsPathInside(rootPath, source.LocalPath))
        {
            var chapterDirectory = Path.GetDirectoryName(Path.GetFullPath(source.LocalPath));
            if (!string.IsNullOrWhiteSpace(chapterDirectory))
            {
                var relativePath = Path.GetRelativePath(chapterDirectory, targetPath)
                    .Replace(Path.DirectorySeparatorChar, '/')
                    .Replace(Path.AltDirectorySeparatorChar, '/');
                return string.Join(
                    "/",
                    relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
                        .Select(Uri.EscapeDataString));
            }
        }

        return new Uri(targetPath).AbsoluteUri;
    }

    private async Task UpdateLinuxReaderTextFallbackAsync(
        CancellationToken cancellationToken,
        IReaderHost? host = null)
    {
        // The native WebKit surface is authoritative for every Linux reading
        // mode, including vertical writing. The old Avalonia text surface is
        // intentionally kept unreachable so Linux uses the same HTML/CSS
        // layout engine for horizontal and vertical EPUB content.
        if (!UseLinuxPlainTextRecoveryFallback)
        {
            HideLinuxReaderTextFallback();
            if (OperatingSystem.IsLinux() && !_readerIsPdf)
                SetReaderHostLayer(revealActiveHost: true);
            return;
        }

        if (!OperatingSystem.IsLinux()
            || _readerIsPdf
            || _readerDocument is null
            || _readerChapterIndex < 0
            || _readerChapterIndex >= _readerDocument.Chapters.Count)
        {
            HideLinuxReaderTextFallback();
            return;
        }

        var chapterPath = _readerDocument.Chapters[_readerChapterIndex];
        if (!File.Exists(chapterPath))
        {
            HideLinuxReaderTextFallback();
            return;
        }

        // This branch is retained only for diagnostics and historical tests;
        // the production reader never enters it and always shows WebKit.

        var targetTitle = _readerLinuxTextFallbackTargetTitle
            ?? GetReaderChapterDisplayName(_readerChapterIndex);
        var fragment = string.IsNullOrWhiteSpace(targetTitle)
            ? null
            : DecodeReaderFragment(_readerCurrentFragment);
        var endFragment = DecodeReaderFragment(_readerLinuxTextFallbackEndFragment);
        var content = await Task.Run(
            () => ExtractReaderFallbackContent(chapterPath, fragment, targetTitle, endFragment),
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.Equals(
                Environment.GetEnvironmentVariable("KKINDLE_TRACE_FALLBACK_TITLE"),
                "1",
                StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                $"fallback-content target={targetTitle} fragment={fragment} end={endFragment} blocks="
                + string.Join(
                    " || ",
                    content.Blocks.Take(10).Select(block =>
                        $"title={block.IsChapterTitle},boundary={block.IsParagraphBoundary},text={block.Text}")));
        }

        var text = content.Text;
        if (string.IsNullOrWhiteSpace(text))
            text = GetReaderChapterDisplayName(_readerChapterIndex);

        var fontSize = 16d * _readerLayout.FontScale;
        var visible = !string.IsNullOrWhiteSpace(text)
            || content.Blocks.Any(block =>
                !string.IsNullOrWhiteSpace(block.ImagePath)
                || !string.IsNullOrWhiteSpace(block.FootnoteHref));
        _readerLinuxTextFallbackUpdating = true;
        try
        {
            ClearLinuxReaderTextFallbackSelectionState();
            _readerLinuxTextFallbackText = text;
            ReaderLinuxTextFallbackText.Text = text;
            ReaderLinuxTextFallbackText.FontSize = fontSize;
            ReaderLinuxTextFallbackText.LineHeight = fontSize * _readerLayout.LineHeight;
            ReaderLinuxTextFallbackText.MaxWidth = Math.Max(320, _readerLayout.MaxWidth);
            ReaderLinuxTextFallbackPageLeft.FontSize = fontSize;
            ReaderLinuxTextFallbackPageLeft.LineHeight = fontSize * _readerLayout.LineHeight;
            ReaderLinuxTextFallbackPageRight.FontSize = fontSize;
            ReaderLinuxTextFallbackPageRight.LineHeight = fontSize * _readerLayout.LineHeight;
            UpdateLinuxReaderTextFallbackBlocks(content.Blocks, text, fontSize);
            if (_readerLayout.FlowMode == 0)
                ReaderLinuxTextFallbackScroll.Offset = new Vector(0, Math.Max(0, _readerScrollPosition));
            else
                _readerLinuxTextFallbackPageIndex = _readerLinuxTextFallbackMoveToChapterEnd || _readerScrollPosition < 0
                    ? -1
                    : (int)Math.Round(Math.Max(0, _readerScrollPosition));
            ReaderLinuxTextFallbackOverlay.IsVisible = visible;
            UpdateLinuxReaderTextFallbackMode();
        }
        finally
        {
            _readerLinuxTextFallbackUpdating = false;
        }

        if (visible)
        {
            ReaderActiveHostSlot.IsVisible = false;
            ReaderActiveHostSlot.IsHitTestVisible = false;
            Dispatcher.UIThread.Post(
                () =>
                {
                    if (!IsLinuxReaderTextFallbackActive()) return;
                    ClampLinuxReaderTextFallbackOffset();
                    RebuildLinuxReaderTextFallbackPages();
                    SyncLinuxReaderTextFallbackState(saveProgress: false);
                    PrimeReaderContinuousEdgeTracking();
                    ReaderLinuxTextFallbackOverlay.Focus();
                },
                DispatcherPriority.Loaded);
        }
    }

    private void HideLinuxReaderTextFallback()
    {
        ReaderLinuxTextFallbackOverlay.IsVisible = false;
        ReaderLinuxTextFallbackText.Text = string.Empty;
        ReaderLinuxTextFallbackPageLeft.Inlines?.Clear();
        ReaderLinuxTextFallbackPageRight.Inlines?.Clear();
        ReaderLinuxTextFallbackPageLeft.Text = string.Empty;
        ReaderLinuxTextFallbackPageRight.Text = string.Empty;
        ReaderLinuxTextFallbackPageImageLeft.Source = null;
        ReaderLinuxTextFallbackPageImageRight.Source = null;
        ReaderLinuxTextFallbackPageImageLeft.IsVisible = false;
        ReaderLinuxTextFallbackPageImageRight.IsVisible = false;
        ReaderLinuxTextFallbackPageFootnoteLeft.Content = string.Empty;
        ReaderLinuxTextFallbackPageFootnoteLeft.Tag = null;
        ReaderLinuxTextFallbackPageFootnoteLeft.IsVisible = false;
        ReaderLinuxTextFallbackPageFootnoteRight.Content = string.Empty;
        ReaderLinuxTextFallbackPageFootnoteRight.Tag = null;
        ReaderLinuxTextFallbackPageFootnoteRight.IsVisible = false;
        ReaderLinuxTextFallbackPageVertical.FootnoteHrefResolver = null;
        ReaderLinuxTextFallbackPageVertical.Text = string.Empty;
        ReaderLinuxTextFallbackPageVertical.SelectionStart = 0;
        ReaderLinuxTextFallbackPageVertical.SelectionEnd = 0;
        ReaderLinuxTextFallbackPageVertical.IsVisible = false;
        _readerLinuxTextFallbackText = string.Empty;
        _readerLinuxTextFallbackPages.Clear();
        _readerLinuxTextFallbackPageItems.Clear();
        _readerLinuxTextFallbackMoveToChapterEnd = false;
        _readerLinuxTextFallbackEndFragment = null;
        _readerLinuxTextFallbackPendingReflowAnchor = null;
        _readerLinuxTextFallbackReflowSequence++;
        ClearLinuxReaderTextFallbackImages();
        ClearLinuxReaderTextFallbackSelectionState();
    }

    private void ReaderLinuxTextFallbackScroll_ScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_readerLinuxTextFallbackUpdating) return;
        if (_readerLayout.FlowMode != 0) return;
        if (_readerLinuxTextFallbackSelectionScrollLocked)
        {
            RestoreLinuxTextFallbackSelectionScrollOffset();
            return;
        }
        SyncLinuxReaderTextFallbackScrollState(saveProgress: true);
    }

    private void RestoreLinuxTextFallbackSelectionScrollOffset()
    {
        if (!_readerLinuxTextFallbackSelectionScrollLocked
            || _readerLinuxTextFallbackRestoringSelectionScroll)
        {
            return;
        }

        var target = new Vector(
            Math.Max(0, _readerLinuxTextFallbackSelectionScrollOffset.X),
            Math.Max(0, _readerLinuxTextFallbackSelectionScrollOffset.Y));
        var current = ReaderLinuxTextFallbackScroll.Offset;
        if (Math.Abs(current.X - target.X) <= 0.5
            && Math.Abs(current.Y - target.Y) <= 0.5)
            return;

        _readerLinuxTextFallbackRestoringSelectionScroll = true;
        try
        {
            ReaderLinuxTextFallbackScroll.Offset = target;
        }
        finally
        {
            _readerLinuxTextFallbackRestoringSelectionScroll = false;
        }
    }

    private void ReleaseLinuxTextFallbackSelectionScrollLock()
    {
        if (!_readerLinuxTextFallbackSelectionScrollLocked) return;

        RestoreLinuxTextFallbackSelectionScrollOffset();
        Dispatcher.UIThread.Post(
            () =>
            {
                if (!IsLinuxReaderTextFallbackActive() || _readerLayout.FlowMode != 0)
                {
                    _readerLinuxTextFallbackSelectionScrollLocked = false;
                    return;
                }

                RestoreLinuxTextFallbackSelectionScrollOffset();
                _readerLinuxTextFallbackSelectionScrollLocked = false;
                SyncLinuxReaderTextFallbackScrollState(saveProgress: false);
            },
            DispatcherPriority.Background);
    }

    private bool IsLinuxReaderTextFallbackActive() =>
        OperatingSystem.IsLinux()
        && !_readerIsPdf
        && ReaderLinuxTextFallbackOverlay.IsVisible;

    private ReaderScrollState? CaptureLinuxReaderTextFallbackState()
    {
        if (!IsLinuxReaderTextFallbackActive()) return null;

        if (_readerLayout.FlowMode == 0)
        {
            var extent = Math.Max(0, ReaderLinuxTextFallbackScroll.Extent.Height);
            var viewport = Math.Max(0, ReaderLinuxTextFallbackScroll.Viewport.Height);
            var position = Math.Max(0, ReaderLinuxTextFallbackScroll.Offset.Y);
            var maximum = Math.Max(0, extent - viewport);
            return new ReaderScrollState(
                position,
                maximum > 0 ? Math.Clamp(position / maximum, 0, 1) : 0,
                Math.Max(0, ReaderLinuxTextFallbackScroll.Extent.Width),
                extent,
                Math.Max(0, ReaderLinuxTextFallbackScroll.Viewport.Width),
                viewport);
        }

        var pageCount = GetLinuxReaderTextFallbackPageCount();
        var spreadSize = _readerLayout.TwoPageMode ? 2 : 1;
        var maximumPage = Math.Max(0, pageCount - spreadSize);
        var pageIndex = _readerLinuxTextFallbackPageIndex < 0
            ? maximumPage
            : Math.Clamp(_readerLinuxTextFallbackPageIndex, 0, maximumPage);
        if (_readerLayout.TwoPageMode && pageIndex % 2 != 0)
            pageIndex--;
        return new ReaderScrollState(
            pageIndex,
            maximumPage > 0 ? Math.Clamp(pageIndex / (double)maximumPage, 0, 1) : 0,
            pageCount,
            pageCount,
            spreadSize,
            spreadSize);
    }

    private bool TryApplyLinuxReaderTextFallbackState()
    {
        var state = CaptureLinuxReaderTextFallbackState();
        if (state is null) return false;
        ApplyReaderScrollState(state);
        return true;
    }

    private void ApplyReaderScrollState(ReaderScrollState state)
    {
        _readerScrollPosition = state.Position;
        _readerScrollRatio = state.Ratio;
        _readerScrollWidth = state.ScrollWidth;
        _readerScrollHeight = state.ScrollHeight;
        _readerClientWidth = state.ClientWidth;
        _readerClientHeight = state.ClientHeight;
    }

    private void SyncLinuxReaderTextFallbackScrollState(bool saveProgress)
    {
        if (!OperatingSystem.IsLinux()
            || _readerIsPdf
            || !ReaderLinuxTextFallbackOverlay.IsVisible)
        {
            return;
        }

        var extent = Math.Max(0, ReaderLinuxTextFallbackScroll.Extent.Height);
        var viewport = Math.Max(0, ReaderLinuxTextFallbackScroll.Viewport.Height);
        var maximum = Math.Max(0, extent - viewport);
        _readerScrollPosition = Math.Max(0, ReaderLinuxTextFallbackScroll.Offset.Y);
        _readerScrollRatio = maximum > 0 ? Math.Clamp(_readerScrollPosition / maximum, 0, 1) : 0;
        _readerScrollHeight = extent;
        _readerClientHeight = viewport;
        _readerScrollWidth = Math.Max(0, ReaderLinuxTextFallbackScroll.Extent.Width);
        _readerClientWidth = Math.Max(0, ReaderLinuxTextFallbackScroll.Viewport.Width);
        UpdateReaderToolbar();
        if (saveProgress)
            _ = ObserveReaderTaskAsync(SaveReaderProgressAsync(CancellationToken.None));
    }

    private void SyncLinuxReaderTextFallbackPagedState(bool saveProgress)
    {
        if (!IsLinuxReaderTextFallbackActive()) return;
        var pageCount = GetLinuxReaderTextFallbackPageCount();
        var spreadSize = _readerLayout.TwoPageMode ? 2 : 1;
        var maximum = Math.Max(0, pageCount - spreadSize);
        _readerLinuxTextFallbackPageIndex = _readerLinuxTextFallbackPageIndex < 0
            ? maximum
            : Math.Clamp(_readerLinuxTextFallbackPageIndex, 0, maximum);
        if (_readerLayout.TwoPageMode && _readerLinuxTextFallbackPageIndex % 2 != 0)
            _readerLinuxTextFallbackPageIndex--;
        RenderLinuxReaderTextFallbackPage();
        _readerScrollPosition = _readerLinuxTextFallbackPageIndex;
        _readerScrollRatio = maximum > 0
            ? Math.Clamp(_readerLinuxTextFallbackPageIndex / (double)maximum, 0, 1)
            : 0;
        _readerScrollWidth = pageCount;
        _readerClientWidth = spreadSize;
        _readerScrollHeight = pageCount;
        _readerClientHeight = spreadSize;
        UpdateReaderToolbar();
        if (saveProgress)
            _ = ObserveReaderTaskAsync(SaveReaderProgressAsync(CancellationToken.None));
    }

    private void SyncLinuxReaderTextFallbackState(bool saveProgress)
    {
        if (_readerLayout.FlowMode == 0)
            SyncLinuxReaderTextFallbackScrollState(saveProgress);
        else
            SyncLinuxReaderTextFallbackPagedState(saveProgress);
    }

    private void UpdateLinuxReaderTextFallbackMode()
    {
        var paged = _readerLayout.FlowMode == 1;
        ReaderLinuxTextFallbackScroll.IsVisible = !paged;
        ReaderLinuxTextFallbackPagedRoot.IsVisible = paged;
        var twoPage = paged && _readerLayout.TwoPageMode;
        ReaderLinuxTextFallbackPagedContent.ColumnDefinitions.Clear();
        if (twoPage)
        {
            ReaderLinuxTextFallbackPagedContent.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            ReaderLinuxTextFallbackPagedContent.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(28)));
            ReaderLinuxTextFallbackPagedContent.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            ReaderLinuxTextFallbackPagedContent.HorizontalAlignment = HorizontalAlignment.Stretch;
        }
        else
        {
            ReaderLinuxTextFallbackPagedContent.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            ReaderLinuxTextFallbackPagedContent.HorizontalAlignment = HorizontalAlignment.Center;
        }
        Grid.SetColumn(ReaderLinuxTextFallbackPageRight, twoPage ? 2 : 0);
        Grid.SetColumn(ReaderLinuxTextFallbackPageImageRight, twoPage ? 2 : 0);
        if (paged)
            RebuildLinuxReaderTextFallbackPages();
    }

    private void RebuildLinuxReaderTextFallbackPages(int? reflowAnchor = null)
    {
        if (!IsLinuxReaderTextFallbackActive()) return;
        if (_readerLayout.FlowMode != 1)
        {
            _readerLinuxTextFallbackPages.Clear();
            _readerLinuxTextFallbackPageItems.Clear();
            return;
        }

        var fontSize = Math.Max(10, 16d * _readerLayout.FontScale);
        var lineHeight = Math.Max(fontSize + 2, fontSize * _readerLayout.LineHeight);
        var overlayWidth = Math.Max(320, ReaderLinuxTextFallbackOverlay.Bounds.Width);
        var overlayHeight = Math.Max(320, ReaderLinuxTextFallbackOverlay.Bounds.Height);
        var pageInsets = ReaderPlatformLayoutPolicy.GetVerticalPageInsets(
            overlayWidth,
            overlayHeight,
            _readerLayout.BodyPadding,
            _readerLayout.MaxWidth);
        ReaderLinuxTextFallbackPagedRoot.Padding = new Thickness(
            pageInsets.Horizontal,
            pageInsets.Vertical);
        // Use the already-arranged paged surface when it is available. The
        // overlay can retain its previous width for a compositor frame while
        // the TOC opens/closes; using that stale width lets a centered page
        // extend into the TOC column before the next reflow pass.
        var availableContentWidth = Math.Max(
            1,
            overlayWidth - pageInsets.Horizontal * 2);
        // In vertical mode the page control is centered inside this grid and
        // its explicit Width is assigned below. Reading the grid's current
        // Bounds.Width here feeds the previous page width back into the next
        // pagination pass, so a narrow page keeps shrinking and leaves a
        // large unused area beside the text. Use the actual viewport width
        // for vertical pages; the horizontal path can still reuse the
        // arranged width for two-page spreads.
        var arrangedContentWidth = _readerLayout.VerticalWriting
            ? availableContentWidth
            : ReaderLinuxTextFallbackPagedContent.Bounds.Width;
        if (!double.IsFinite(arrangedContentWidth) || arrangedContentWidth <= 0)
            arrangedContentWidth = availableContentWidth;
        arrangedContentWidth = Math.Clamp(
            arrangedContentWidth,
            1,
            availableContentWidth);
        var pageWidth = _readerLayout.TwoPageMode
            ? Math.Max(1, (arrangedContentWidth - 28) / 2)
            : Math.Max(1, Math.Min(_readerLayout.MaxWidth, arrangedContentWidth));
        var pageHeight = Math.Max(
            180,
            overlayHeight - pageInsets.Vertical * 2);
        var linesPerPage = Math.Max(4, (int)Math.Floor(pageHeight / lineHeight));
        // Vertical writing paginates on the same column grid the drawing
        // surface uses, so a page boundary always lands on a column start.
        var verticalGrid = _readerLayout.VerticalWriting
            ? ReaderLinuxTextFallbackVerticalPage.ComputeGrid(
                pageWidth,
                pageHeight,
                fontSize,
                lineHeight)
            : default;
        // Use the real Avalonia text formatter for the visible fallback pages
        // so the split point is the same one used by the wrapping control.
        _readerLinuxTextFallbackPages.Clear();
        _readerLinuxTextFallbackPageItems.Clear();

        var textBuilder = new StringBuilder();
        var footnotes = new List<ReaderLinuxTextFallbackFootnote>();
        var textOffset = 0;
        var chapterTitleStart = -1;
        var chapterTitleLength = 0;
        var previousTextWasTitle = false;
        var paginationStreamOffset = 0;

        void FlushTextPages()
        {
            if (textBuilder.Length == 0) return;

            var mixedText = textBuilder.ToString();
            var measuredPages = new List<(string Text, int Start, bool StartsParagraph)>();

            void AddMeasuredPages(string segment, int segmentStart)
            {
                if (string.IsNullOrWhiteSpace(segment)) return;
                List<string> segmentPageTexts;
                if (_readerLayout.VerticalWriting)
                {
                    var startsWithParagraph = segmentStart == 0
                        || (segmentStart > 0 && mixedText[segmentStart - 1] == '\n');
                    segmentPageTexts = ReaderLinuxVerticalPagingPolicy.Paginate(
                        segment,
                        verticalGrid.CharsPerColumn,
                        verticalGrid.ColumnsPerPage,
                        _readerLayout.ParagraphIndent,
                        startsWithParagraph).Select(page => page.Text).ToList();
                }
                else
                {
                    segmentPageTexts = PaginateReaderPlainTextWithMeasuredLayout(
                        segment,
                        pageWidth,
                        linesPerPage,
                        fontSize,
                        lineHeight,
                        ReaderLinuxTextFallbackPageLeft.FontFamily);
                }
                var searchOffset = 0;
                foreach (var segmentPage in segmentPageTexts)
                {
                    var start = segment.IndexOf(
                        segmentPage,
                        Math.Clamp(searchOffset, 0, segment.Length),
                        StringComparison.Ordinal);
                    if (start < 0)
                        start = searchOffset;
                    var absoluteStart = segmentStart + Math.Max(0, start);
                    measuredPages.Add((
                        segmentPage,
                        absoluteStart,
                        absoluteStart == 0 || mixedText[absoluteStart - 1] == '\n'));
                    searchOffset = Math.Clamp(start + segmentPage.Length, 0, segment.Length);
                }
            }

            var localReflowAnchor = reflowAnchor is { } anchor
                ? anchor - paginationStreamOffset
                : -1;
            if (localReflowAnchor > 0 && localReflowAnchor < mixedText.Length)
            {
                // Preserve the exact previous first line as the new first
                // line. Reflowing the whole chapter and merely selecting the
                // page that contains the anchor can place it halfway down.
                AddMeasuredPages(mixedText[..localReflowAnchor], 0);
                AddMeasuredPages(mixedText[localReflowAnchor..], localReflowAnchor);
            }
            else
            {
                AddMeasuredPages(mixedText, 0);
            }
            var footnoteIndex = 0;
            foreach (var measuredPage in measuredPages)
            {
                var page = measuredPage.Text;
                var pageStart = measuredPage.Start;

                var notesOnPage = new List<ReaderLinuxTextFallbackFootnote>();
                foreach (var character in page)
                {
                    if (character != ReaderLinuxTextFallbackFootnoteMarker || footnoteIndex >= footnotes.Count)
                        continue;
                    notesOnPage.Add(footnotes[footnoteIndex++]);
                }

                var localTitleStart = -1;
                var localTitleLength = 0;
                if (chapterTitleStart >= 0 && chapterTitleLength > 0)
                {
                    var titleEnd = chapterTitleStart + chapterTitleLength;
                    var overlapStart = Math.Max(chapterTitleStart, pageStart);
                    var overlapEnd = Math.Min(titleEnd, pageStart + page.Length);
                    if (overlapEnd > overlapStart)
                    {
                        localTitleStart = overlapStart - pageStart;
                        localTitleLength = overlapEnd - overlapStart;
                    }
                }

                _readerLinuxTextFallbackPages.Add(page);
                _readerLinuxTextFallbackPageItems.Add(new ReaderLinuxTextFallbackPageItem(
                    page,
                    Math.Max(0, textOffset),
                    null,
                    notesOnPage,
                    localTitleStart,
                    localTitleLength,
                    paginationStreamOffset + Math.Max(0, pageStart),
                    measuredPage.StartsParagraph));
            }

            paginationStreamOffset += mixedText.Length + 1;
            textBuilder.Clear();
            footnotes.Clear();
            chapterTitleStart = -1;
            chapterTitleLength = 0;
            previousTextWasTitle = false;
        }

        foreach (var block in ReaderLinuxTextFallbackBlocks)
        {
            if (block.Image is not null)
            {
                FlushTextPages();
                _readerLinuxTextFallbackPageItems.Add(new ReaderLinuxTextFallbackPageItem(
                    string.Empty,
                    0,
                    block.Image,
                    PaginationOffset: paginationStreamOffset));
                paginationStreamOffset++;
                continue;
            }

            if (block.IsFootnote)
            {
                if (textBuilder.Length == 0)
                    textOffset = 0;
                textBuilder.Append(ReaderLinuxTextFallbackFootnoteMarker);
                footnotes.Add(new ReaderLinuxTextFallbackFootnote(
                    block.FootnoteLabel,
                    block.FootnoteHref));
                continue;
            }

            if (string.IsNullOrWhiteSpace(block.Text)) continue;
            if (textBuilder.Length == 0)
                textOffset = block.TextOffset;
            else if (textBuilder[^1] != '\n')
            {
                // A normal paragraph starts on the next body line, matching
                // the line grid inside the paragraph. Chapter headings get
                // one additional line before the first body paragraph.
                var separatorLines = previousTextWasTitle || block.IsChapterTitle ? 2 : 1;
                textBuilder.Append('\n', separatorLines);
            }
            var blockStart = textBuilder.Length;
            textBuilder.Append(block.Text);
            if (block.IsChapterTitle)
            {
                chapterTitleStart = blockStart;
                chapterTitleLength = block.Text.Length;
            }
            previousTextWasTitle = block.IsChapterTitle;
            if (block.HasInlineFootnotes)
                footnotes.AddRange(block.InlineFootnotes);
        }

        FlushTextPages();

        if (_readerLinuxTextFallbackPageItems.Count == 0)
        {
            var fallbackMeasuredPages = new List<(string Text, int Start)>();
            if (_readerLayout.VerticalWriting)
            {
                fallbackMeasuredPages.AddRange(
                    ReaderLinuxVerticalPagingPolicy.Paginate(
                        _readerLinuxTextFallbackText,
                        verticalGrid.CharsPerColumn,
                        verticalGrid.ColumnsPerPage,
                        _readerLayout.ParagraphIndent));
            }
            else
            {
                var searchOffset = Math.Clamp(
                    paginationStreamOffset,
                    0,
                    _readerLinuxTextFallbackText.Length);
                foreach (var pageText in PaginateReaderPlainTextWithMeasuredLayout(
                    _readerLinuxTextFallbackText,
                    pageWidth,
                    linesPerPage,
                    fontSize,
                    lineHeight,
                    ReaderLinuxTextFallbackPageLeft.FontFamily))
                {
                    var start = _readerLinuxTextFallbackText.IndexOf(
                        pageText,
                        searchOffset,
                        StringComparison.Ordinal);
                    if (start < 0) start = searchOffset;
                    fallbackMeasuredPages.Add((pageText, start));
                    searchOffset = Math.Clamp(
                        start + pageText.Length,
                        0,
                        _readerLinuxTextFallbackText.Length);
                }
            }

            _readerLinuxTextFallbackPages = fallbackMeasuredPages
                .Select(page => page.Text)
                .ToList();
            foreach (var page in fallbackMeasuredPages)
            {
                _readerLinuxTextFallbackPageItems.Add(new ReaderLinuxTextFallbackPageItem(
                    page.Text,
                    0,
                    null,
                    PaginationOffset: Math.Max(0, page.Start),
                    StartsParagraph: page.Start == 0
                        || (page.Start > 0 && _readerLinuxTextFallbackText[page.Start - 1] == '\n')));
                paginationStreamOffset = Math.Clamp(
                    page.Start + page.Text.Length,
                    0,
                    _readerLinuxTextFallbackText.Length);
            }
        }

        var spreadSize = _readerLayout.TwoPageMode ? 2 : 1;
        _readerLinuxTextFallbackPageIndex = reflowAnchor is { } anchor
            ? ReaderLinuxTextFallbackPagingPolicy.ResolveAnchorPageIndex(
                _readerLinuxTextFallbackPageItems
                    .Select(item => item.IsImage ? -1 : item.PaginationOffset)
                    .ToArray(),
                anchor,
                spreadSize)
            : ReaderLinuxTextFallbackPagingPolicy.ResolvePageIndex(
                _readerLinuxTextFallbackPageIndex,
                _readerScrollPosition,
                _readerLinuxTextFallbackMoveToChapterEnd,
                GetLinuxReaderTextFallbackPageCount(),
                spreadSize);
        _readerLinuxTextFallbackMoveToChapterEnd = false;

        // Pagination is calculated from this exact rectangle. Keep the
        // selectable controls at that rectangle instead of letting their
        // content-derived DesiredSize change when a selection starts or ends
        // on a line boundary and triggering a second wrap pass.
        ReaderLinuxTextFallbackPageLeft.Width = pageWidth;
        ReaderLinuxTextFallbackPageLeft.MinWidth = pageWidth;
        ReaderLinuxTextFallbackPageLeft.MaxWidth = pageWidth;
        ReaderLinuxTextFallbackPageLeft.Height = pageHeight;
        ReaderLinuxTextFallbackPageLeft.MinHeight = pageHeight;
        ReaderLinuxTextFallbackPageLeft.MaxHeight = pageHeight;
        ReaderLinuxTextFallbackPageRight.Width = pageWidth;
        ReaderLinuxTextFallbackPageRight.MinWidth = pageWidth;
        ReaderLinuxTextFallbackPageRight.MaxWidth = pageWidth;
        ReaderLinuxTextFallbackPageRight.Height = pageHeight;
        ReaderLinuxTextFallbackPageRight.MinHeight = pageHeight;
        ReaderLinuxTextFallbackPageRight.MaxHeight = pageHeight;
        if (_readerLayout.VerticalWriting)
        {
            // Vertical pages draw on the same rectangle the paginator used.
            // A fixed size keeps the column grid identical across renders.
            ReaderLinuxTextFallbackPageVertical.Width = pageWidth;
            ReaderLinuxTextFallbackPageVertical.MinWidth = pageWidth;
            ReaderLinuxTextFallbackPageVertical.MaxWidth = pageWidth;
            ReaderLinuxTextFallbackPageVertical.Height = pageHeight;
            ReaderLinuxTextFallbackPageVertical.MinHeight = pageHeight;
            ReaderLinuxTextFallbackPageVertical.MaxHeight = pageHeight;
        }
        RenderLinuxReaderTextFallbackPage();
    }

    private int? CaptureLinuxReaderTextFallbackPageAnchor()
    {
        if (_readerLayout.FlowMode != 1
            || _readerLinuxTextFallbackPageItems.Count == 0)
        {
            return null;
        }

        var pageIndex = Math.Clamp(
            _readerLinuxTextFallbackPageIndex,
            0,
            _readerLinuxTextFallbackPageItems.Count - 1);
        for (var index = pageIndex; index < _readerLinuxTextFallbackPageItems.Count; index++)
        {
            var item = _readerLinuxTextFallbackPageItems[index];
            if (!item.IsImage && !string.IsNullOrWhiteSpace(item.Text))
                return item.PaginationOffset;
        }

        return null;
    }

    // Vertical writing replaces the selectable text slots with the custom
    // drawing page. Single-page only — ReaderLayoutDefaults.Normalize forces
    // vertical into FlowMode 1 with two-page spreads disabled.
    private void RenderLinuxReaderTextFallbackVerticalPage()
    {
        var control = ReaderLinuxTextFallbackPageVertical;
        var fontSize = Math.Max(10, 16d * _readerLayout.FontScale);
        control.FontSize = fontSize;
        control.LineHeight = Math.Max(fontSize + 2, fontSize * _readerLayout.LineHeight);

        void HideHorizontalSlots()
        {
            ReaderLinuxTextFallbackPageLeft.Inlines?.Clear();
            ReaderLinuxTextFallbackPageLeft.Text = string.Empty;
            ReaderLinuxTextFallbackPageLeft.IsVisible = false;
            ReaderLinuxTextFallbackPageRight.Inlines?.Clear();
            ReaderLinuxTextFallbackPageRight.Text = string.Empty;
            ReaderLinuxTextFallbackPageRight.IsVisible = false;
            ReaderLinuxTextFallbackPageFootnoteLeft.Content = string.Empty;
            ReaderLinuxTextFallbackPageFootnoteLeft.IsVisible = false;
            ReaderLinuxTextFallbackPageFootnoteRight.Content = string.Empty;
            ReaderLinuxTextFallbackPageFootnoteRight.IsVisible = false;
            ClearLinuxReaderTextFallbackSelectionState();
        }

        var pageCount = GetLinuxReaderTextFallbackPageCount();
        if (pageCount == 0 || _readerLinuxTextFallbackPageItems.Count == 0)
        {
            HideHorizontalSlots();
            control.FootnoteHrefResolver = null;
            control.Text = string.Empty;
            control.IsVisible = false;
            ReaderLinuxTextFallbackPageImageLeft.Source = null;
            ReaderLinuxTextFallbackPageImageLeft.IsVisible = false;
            return;
        }

        _readerLinuxTextFallbackPageIndex = Math.Clamp(
            _readerLinuxTextFallbackPageIndex,
            0,
            Math.Max(0, pageCount - 1));
        var item = _readerLinuxTextFallbackPageItems[_readerLinuxTextFallbackPageIndex];
        if (item.Image is { } image)
        {
            // Image pages keep the shared slot; rotating covers is wrong.
            var (slotMaxWidth, slotMaxHeight) =
                GetLinuxReaderTextFallbackPagedImageBounds(ReaderLinuxTextFallbackPageLeft);
            ReaderLinuxTextFallbackPageImageLeft.Source = image.Source;
            ReaderLinuxTextFallbackPageImageLeft.MaxWidth =
                Math.Min(image.MaxWidth, slotMaxWidth);
            ReaderLinuxTextFallbackPageImageLeft.MaxHeight =
                Math.Min(image.MaxHeight, slotMaxHeight);
            ReaderLinuxTextFallbackPageImageLeft.IsVisible = true;
            control.FootnoteHrefResolver = null;
            control.Text = string.Empty;
            control.IsVisible = false;
            return;
        }

        ReaderLinuxTextFallbackPageImageLeft.Source = null;
        ReaderLinuxTextFallbackPageImageLeft.IsVisible = false;
        ReaderLinuxTextFallbackPageLeft.IsVisible = false;
        ReaderLinuxTextFallbackPageRight.IsVisible = false;

        var titleStart = item.ChapterTitleStart;
        var titleLength = item.ChapterTitleLength;
        var isFirstTextPage = !_readerLinuxTextFallbackPageItems
            .Take(_readerLinuxTextFallbackPageIndex)
            .Any(other => !other.IsImage && !string.IsNullOrWhiteSpace(other.Text));
        if ((titleStart < 0 || titleLength <= 0)
            && isFirstTextPage
            && ReaderLinuxTextFallbackTitleLinePolicy.TryFindLeadingTitleRange(
                item.Text,
                NormalizeReaderPlainTextLine(
                    _readerLinuxTextFallbackTargetTitle
                    ?? GetReaderChapterDisplayName(_readerChapterIndex)),
                out var recoveredStart,
                out var recoveredLength))
        {
            titleStart = recoveredStart;
            titleLength = recoveredLength;
        }

        control.FootnoteHrefResolver = index =>
            index >= 0 && index < item.InlineFootnotes.Count
                ? item.InlineFootnotes[index].Href
                : null;
        control.SelectionStart = 0;
        control.SelectionEnd = 0;
        control.Text = item.Text;
        control.ChapterTitleStart = titleStart;
        control.ChapterTitleLength = titleLength;
        control.ParagraphIndent = _readerLayout.ParagraphIndent;
        // A chapter heading is centered as its own visual unit and must not
        // consume the body paragraph's two-row indent. The first body page
        // after the heading is marked by the newline inside the page text.
        control.StartsParagraph = item.StartsParagraph
            && !(titleStart == 0 && titleLength > 0);
        control.AnnotationRanges = GetLinuxReaderTextFallbackAnnotationRanges(
            item.PaginationOffset,
            item.Text.Length);
        control.IsVisible = true;
    }

    private void RenderLinuxReaderTextFallbackPage()
    {
        if (_readerLayout.VerticalWriting)
        {
            RenderLinuxReaderTextFallbackVerticalPage();
            return;
        }

        ReaderLinuxTextFallbackPageVertical.IsVisible = false;
        var pageCount = GetLinuxReaderTextFallbackPageCount();
        if (pageCount == 0)
        {
            ReaderLinuxTextFallbackPageLeft.Inlines?.Clear();
            ReaderLinuxTextFallbackPageRight.Inlines?.Clear();
            ReaderLinuxTextFallbackPageLeft.Text = string.Empty;
            ReaderLinuxTextFallbackPageRight.Text = string.Empty;
            ReaderLinuxTextFallbackPageLeft.IsVisible = false;
            ReaderLinuxTextFallbackPageRight.IsVisible = false;
            ReaderLinuxTextFallbackPageImageLeft.Source = null;
            ReaderLinuxTextFallbackPageImageRight.Source = null;
            ReaderLinuxTextFallbackPageImageLeft.IsVisible = false;
            ReaderLinuxTextFallbackPageImageRight.IsVisible = false;
            ReaderLinuxTextFallbackPageFootnoteLeft.Content = string.Empty;
            ReaderLinuxTextFallbackPageFootnoteLeft.Tag = null;
            ReaderLinuxTextFallbackPageFootnoteLeft.IsVisible = false;
            ReaderLinuxTextFallbackPageFootnoteRight.Content = string.Empty;
            ReaderLinuxTextFallbackPageFootnoteRight.Tag = null;
            ReaderLinuxTextFallbackPageFootnoteRight.IsVisible = false;
            return;
        }

        _readerLinuxTextFallbackPageIndex = Math.Clamp(
            _readerLinuxTextFallbackPageIndex,
            0,
            Math.Max(0, pageCount - 1));
        RenderLinuxReaderTextFallbackSlot(
            ReaderLinuxTextFallbackPageLeft,
            ReaderLinuxTextFallbackPageImageLeft,
            ReaderLinuxTextFallbackPageFootnoteLeft,
            _readerLinuxTextFallbackPageIndex);

        var rightPageIndex = _readerLinuxTextFallbackPageIndex + 1;
        if (_readerLayout.TwoPageMode && rightPageIndex < pageCount)
        {
            RenderLinuxReaderTextFallbackSlot(
                ReaderLinuxTextFallbackPageRight,
                ReaderLinuxTextFallbackPageImageRight,
                ReaderLinuxTextFallbackPageFootnoteRight,
                rightPageIndex);
        }
        else
        {
            ReaderLinuxTextFallbackPageRight.Inlines?.Clear();
            ReaderLinuxTextFallbackPageRight.Text = string.Empty;
            ReaderLinuxTextFallbackPageRight.IsVisible = false;
            ReaderLinuxTextFallbackPageImageRight.Source = null;
            ReaderLinuxTextFallbackPageImageRight.IsVisible = false;
            ReaderLinuxTextFallbackPageFootnoteRight.Content = string.Empty;
            ReaderLinuxTextFallbackPageFootnoteRight.Tag = null;
            ReaderLinuxTextFallbackPageFootnoteRight.IsVisible = false;
        }
    }

    private void RenderLinuxReaderTextFallbackSlot(
        SelectableTextBlock textBlock,
        Image imageControl,
        Button footnoteButton,
        int pageIndex)
    {
        textBlock.Inlines?.Clear();
        textBlock.Text = string.Empty;
        if (textBlock is ReaderLinuxTextFallbackTextBlock styledTextBlock)
        {
            styledTextBlock.ChapterTitleStart = -1;
            styledTextBlock.ChapterTitleLength = 0;
            styledTextBlock.LayoutText = string.Empty;
            styledTextBlock.AnnotationRanges = [];
        }
        footnoteButton.Content = string.Empty;
        footnoteButton.Tag = null;
        footnoteButton.IsVisible = false;

        if (pageIndex >= 0
            && pageIndex < _readerLinuxTextFallbackPageItems.Count
            && _readerLinuxTextFallbackPageItems[pageIndex] is { Image: { } image })
        {
            var (slotMaxWidth, slotMaxHeight) = GetLinuxReaderTextFallbackPagedImageBounds(textBlock);
            imageControl.Source = image.Source;
            imageControl.MaxWidth = Math.Min(image.MaxWidth, slotMaxWidth);
            imageControl.MaxHeight = Math.Min(image.MaxHeight, slotMaxHeight);
            imageControl.IsVisible = true;
            textBlock.Text = string.Empty;
            textBlock.IsVisible = false;
            return;
        }

        if (pageIndex >= 0
            && pageIndex < _readerLinuxTextFallbackPageItems.Count
            && !_readerLinuxTextFallbackPageItems[pageIndex].IsImage)
        {
            var page = _readerLinuxTextFallbackPageItems[pageIndex];
            if (textBlock is ReaderLinuxTextFallbackTextBlock pageStyledTextBlock)
            {
                var titleStart = page.ChapterTitleStart;
                var titleLength = page.ChapterTitleLength;
                var isFirstTextPage = !_readerLinuxTextFallbackPageItems
                    .Take(pageIndex)
                    .Any(item => !item.IsImage && !string.IsNullOrWhiteSpace(item.Text));
                if ((titleStart < 0 || titleLength <= 0)
                    && isFirstTextPage
                    && ReaderLinuxTextFallbackTitleLinePolicy.TryFindLeadingTitleRange(
                        page.Text,
                        NormalizeReaderPlainTextLine(
                            _readerLinuxTextFallbackTargetTitle
                                ?? GetReaderChapterDisplayName(_readerChapterIndex)),
                        out var recoveredTitleStart,
                        out var recoveredTitleLength))
                {
                    titleStart = recoveredTitleStart;
                    titleLength = recoveredTitleLength;
                }
                pageStyledTextBlock.ChapterTitleStart = titleStart;
                pageStyledTextBlock.ChapterTitleLength = titleLength;
                pageStyledTextBlock.LayoutText = page.Text;
                pageStyledTextBlock.AnnotationRanges =
                    GetLinuxReaderTextFallbackAnnotationRanges(
                        page.PaginationOffset,
                        page.Text.Length);
            }
            if (page.HasInlineFootnotes)
                RenderLinuxReaderTextFallbackInlinePage(textBlock, page);
            else
                textBlock.Text = page.Text;
            textBlock.IsVisible = true;
        }
        else
        {
            textBlock.Text = string.Empty;
            textBlock.IsVisible = false;
        }
        imageControl.Source = null;
        imageControl.IsVisible = false;
    }

    private void RenderLinuxReaderTextFallbackInlinePage(
        SelectableTextBlock textBlock,
        ReaderLinuxTextFallbackPageItem page)
    {
        var footnoteIndex = 0;
        var textStart = 0;
        var text = page.Text;

        void AddRun(string value)
        {
            if (value.Length > 0)
                textBlock.Inlines?.Add(new Run(value));
        }

        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] != ReaderLinuxTextFallbackFootnoteMarker)
                continue;

            AddRun(text[textStart..index]);
            if (footnoteIndex < page.InlineFootnotes.Count)
            {
                var footnote = page.InlineFootnotes[footnoteIndex++];
                textBlock.Inlines?.Add(new InlineUIContainer(
                    CreateLinuxReaderInlineFootnoteButton(footnote)));
            }
            else
            {
                AddRun(UiText.Get("注"));
            }
            textStart = index + 1;
        }

        AddRun(text[textStart..]);
    }

    private Button CreateLinuxReaderInlineFootnoteButton(
        ReaderLinuxTextFallbackFootnote footnote)
    {
        var button = new Button
        {
            Content = footnote.Label,
            Tag = footnote.Href,
            Focusable = true,
            VerticalAlignment = VerticalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(2, 0)
        };
        button.Classes.Add("readerFootnoteMarker");
        button.PointerEntered += ReaderLinuxTextFallbackFootnoteButton_PointerEntered;
        button.PointerExited += ReaderLinuxTextFallbackFootnoteButton_PointerExited;
        return button;
    }

    private int GetLinuxReaderTextFallbackPageCount() =>
        Math.Max(1, _readerLinuxTextFallbackPageItems.Count > 0
            ? _readerLinuxTextFallbackPageItems.Count
            : _readerLinuxTextFallbackPages.Count);

    private int GetLinuxReaderTextFallbackImagePageCount()
    {
        return _readerLinuxTextFallbackPageItems.Count(item => item.IsImage);
    }

    private static List<string> PaginateReaderPlainText(
        string text,
        double lineUnits,
        int linesPerPage)
    {
        var pages = new List<string>();
        var page = new StringBuilder();
        var line = new StringBuilder();
        var lines = 0;

        void CommitLine()
        {
            if (page.Length > 0) page.AppendLine();
            page.Append(line);
            line.Clear();
            lines++;
            if (lines < linesPerPage) return;
            pages.Add(page.ToString().TrimEnd());
            page.Clear();
            lines = 0;
        }

        void CommitBlankLine()
        {
            if (line.Length > 0) CommitLine();
            if (page.Length > 0 && lines + 1 < linesPerPage)
            {
                page.AppendLine();
                lines++;
            }
        }

        var paragraphs = System.Text.RegularExpressions.Regex.Split(
            text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim(),
            @"\n{2,}");
        foreach (var paragraph in paragraphs)
        {
            var linesInParagraph = paragraph.Split('\n');
            if (linesInParagraph.All(lineText => string.IsNullOrWhiteSpace(lineText)))
            {
                CommitBlankLine();
                continue;
            }

            foreach (var paragraphLine in linesInParagraph)
            {
                var remaining = paragraphLine.Trim();
                if (remaining.Length == 0)
                {
                    CommitBlankLine();
                    continue;
                }

                var currentUnits = 0d;
                for (var index = 0; index < remaining.Length; index++)
                {
                    var ch = remaining[index];
                    var unit = GetReaderPlainTextCharUnits(ch);
                    if (line.Length > 0 && currentUnits + unit > lineUnits)
                    {
                        CommitLine();
                        currentUnits = 0;
                    }
                    line.Append(ch);
                    currentUnits += unit;
                }
                CommitLine();
            }
            CommitBlankLine();
        }

        if (line.Length > 0) CommitLine();
        if (page.Length > 0) pages.Add(page.ToString().TrimEnd());
        if (pages.Count == 0) pages.Add(string.Empty);
        return pages;
    }

    private static List<string> PaginateReaderPlainTextWithMeasuredLayout(
        string text,
        double pageWidth,
        int linesPerPage,
        double fontSize,
        double lineHeight,
        FontFamily fontFamily)
    {
        var normalized = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
        if (normalized.Length == 0)
            return [string.Empty];

        try
        {
            var layout = new TextLayout(
                normalized,
                new Typeface(fontFamily, FontStyle.Normal, FontWeight.Normal, FontStretch.Normal),
                Math.Max(1, fontSize),
                Brushes.Transparent,
                TextAlignment.Left,
                TextWrapping.Wrap,
                TextTrimming.None,
                null,
                FlowDirection.LeftToRight,
                Math.Max(1, pageWidth),
                double.PositiveInfinity,
                Math.Max(1, lineHeight),
                0,
                0,
                null,
                null);
            var lines = layout.TextLines
                .Select(line =>
                {
                    var start = Math.Clamp(line.FirstTextSourceIndex, 0, normalized.Length);
                    var end = Math.Clamp(start + line.Length, start, normalized.Length);
                    return (Start: start, End: end);
                })
                .Where(line => line.End > line.Start)
                .ToArray();
            if (lines.Length == 0)
                return [string.Empty];

            var pages = new List<string>();
            var safeLinesPerPage = Math.Max(1, linesPerPage);
            for (var lineIndex = 0; lineIndex < lines.Length; lineIndex += safeLinesPerPage)
            {
                var start = lines[lineIndex].Start;
                var nextLineIndex = Math.Min(lineIndex + safeLinesPerPage, lines.Length);
                var end = nextLineIndex < lines.Length
                    ? lines[nextLineIndex].Start
                    : normalized.Length;
                end = Math.Clamp(end, start, normalized.Length);
                var page = normalized[start..end].TrimEnd();
                if (page.Length > 0)
                    pages.Add(page);
            }

            return pages.Count > 0 ? pages : [string.Empty];
        }
        catch
        {
            // TextLayout is unavailable before an Avalonia text backend is
            // initialized (for example during a headless page estimate). The
            // deterministic unit fallback still keeps every character and
            // is used only for that exceptional path.
            return PaginateReaderPlainText(
                normalized,
                Math.Max(8, pageWidth / Math.Max(1, fontSize)),
                linesPerPage);
        }
    }

    private void UpdateLinuxReaderTextFallbackBlocks(
        IReadOnlyList<ReaderLinuxTextFallbackRawBlock> rawBlocks,
        string fallbackText,
        double fontSize)
    {
        ClearLinuxReaderTextFallbackImages();
        ReaderLinuxTextFallbackBlocks.Clear();
        var (maxWidth, maxHeight) = GetLinuxReaderTextFallbackImageBounds();
        var imageCache = new Dictionary<string, ReaderLinuxTextFallbackImage>(StringComparer.OrdinalIgnoreCase);
        var lineHeight = fontSize * _readerLayout.LineHeight;
        var textMaxWidth = Math.Max(320, _readerLayout.MaxWidth);
        // Mirror the WebView cover rule: a bare "封面" caption next to the
        // cover raster is metadata, not body copy. Drop it only when the
        // chapter actually renders an image so real chapters keep their text.
        var hasImageBlock = rawBlocks.Any(block => !string.IsNullOrWhiteSpace(block.ImagePath));
        var textOffset = 0;
        var pendingText = new StringBuilder();
        var pendingFootnotes = new List<ReaderLinuxTextFallbackFootnote>();
        var pendingOffset = 0;
        var pendingChapterTitle = false;

        void FlushTextBlock()
        {
            var text = NormalizeReaderPlainText(pendingText.ToString());
            pendingText.Clear();
            if (string.IsNullOrWhiteSpace(text))
            {
                pendingFootnotes.Clear();
                pendingChapterTitle = false;
                return;
            }

            ReaderLinuxTextFallbackBlocks.Add(pendingFootnotes.Count == 0
                ? new ReaderLinuxTextFallbackBlock(
                    text,
                    Math.Clamp(pendingOffset, 0, fallbackText.Length),
                    fontSize,
                    lineHeight,
                    textMaxWidth,
                    pendingChapterTitle)
                : new ReaderLinuxTextFallbackBlock(
                    text,
                    Math.Clamp(pendingOffset, 0, fallbackText.Length),
                    fontSize,
                    lineHeight,
                    textMaxWidth,
                    pendingFootnotes.ToArray(),
                    pendingChapterTitle));
            pendingFootnotes.Clear();
            pendingChapterTitle = false;
        }

        foreach (var rawBlock in rawBlocks)
        {
            if (rawBlock.IsParagraphBoundary
                && string.IsNullOrWhiteSpace(rawBlock.Text)
                && string.IsNullOrWhiteSpace(rawBlock.ImagePath)
                && string.IsNullOrWhiteSpace(rawBlock.FootnoteHref))
            {
                FlushTextBlock();
                continue;
            }

            if (!string.IsNullOrWhiteSpace(rawBlock.ImagePath))
            {
                FlushTextBlock();
                if (!imageCache.TryGetValue(rawBlock.ImagePath, out var image))
                {
                    try
                    {
                        image = new ReaderLinuxTextFallbackImage(
                            new Bitmap(rawBlock.ImagePath),
                            maxWidth,
                            maxHeight);
                        imageCache[rawBlock.ImagePath] = image;
                        ReaderLinuxTextFallbackImages.Add(image);
                    }
                    catch
                    {
                        continue;
                    }
                }
                ReaderLinuxTextFallbackBlocks.Add(new ReaderLinuxTextFallbackBlock(image));
                continue;
            }

            if (!string.IsNullOrWhiteSpace(rawBlock.FootnoteHref))
            {
                // Keep chapter headings as a plain, centered text run. A
                // publisher footnote attached to the heading itself must not
                // turn the whole heading into an inline-control layout, which
                // would lose its bold/centered rendering on Linux.
                if (pendingChapterTitle && pendingText.Length > 0)
                    continue;
                if (pendingText.Length == 0)
                    pendingOffset = textOffset;
                pendingText.Append(ReaderLinuxTextFallbackFootnoteMarker);
                pendingFootnotes.Add(new ReaderLinuxTextFallbackFootnote(
                    string.IsNullOrWhiteSpace(rawBlock.FootnoteLabel) ? UiText.Get("注") : rawBlock.FootnoteLabel.Trim(),
                    rawBlock.FootnoteHref));
                continue;
            }

            var normalizedText = NormalizeReaderPlainText(rawBlock.Text ?? string.Empty);
            if (string.IsNullOrWhiteSpace(normalizedText)) continue;
            var textParts = System.Text.RegularExpressions.Regex.Split(
                normalizedText,
                @"(?:\r\n|\n){2,}");
            for (var partIndex = 0; partIndex < textParts.Length; partIndex++)
            {
                var text = textParts[partIndex].Trim();
                if (string.IsNullOrWhiteSpace(text)) continue;
                if (hasImageBlock
                    && string.Equals(text.Replace(" ", string.Empty), "封面", StringComparison.Ordinal))
                    continue;

                var start = IndexOfReaderPlainTextBlock(fallbackText, text, textOffset);
                if (start < 0) start = textOffset;
                if (pendingText.Length == 0)
                    pendingOffset = start;
                pendingText.Append(text);
                pendingChapterTitle |= rawBlock.IsChapterTitle && partIndex == 0;
                textOffset = Math.Clamp(start + text.Length, 0, fallbackText.Length);
                if (partIndex < textParts.Length - 1 || rawBlock.IsParagraphBoundary)
                    FlushTextBlock();
            }
        }

        FlushTextBlock();
        PromoteLinuxReaderFirstChapterTitle();

        if (string.Equals(
                Environment.GetEnvironmentVariable("KKINDLE_TRACE_FALLBACK_TITLE"),
                "1",
                StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                "fallback-blocks "
                + string.Join(
                    " || ",
                    ReaderLinuxTextFallbackBlocks.Take(10).Select(block =>
                        $"title={block.IsChapterTitle},text={block.Text}")));
        }

        if (ReaderLinuxTextFallbackBlocks.Count == 0 && !string.IsNullOrWhiteSpace(fallbackText))
        {
            ReaderLinuxTextFallbackBlocks.Add(new ReaderLinuxTextFallbackBlock(
                fallbackText,
                0,
                fontSize,
                lineHeight,
                textMaxWidth));
        }

        ApplyLinuxReaderTextFallbackAnnotationRanges();
        UpdateLinuxReaderTextFallbackBlockWidths();
    }

    private IReadOnlyList<ReaderLinuxTextFallbackAnnotationRange> GetLinuxReaderTextFallbackAnnotationRanges(
        int textOffset,
        int textLength)
    {
        if (textLength <= 0) return [];
        var chapterPath = GetReaderChapterPath();
        if (string.IsNullOrWhiteSpace(chapterPath)) return [];
        var textEnd = textOffset + textLength;
        return ReaderAnnotations
            .Where(item => string.Equals(item.ChapterPath, chapterPath, StringComparison.OrdinalIgnoreCase))
            .Where(item => item.EndOffset > textOffset && item.StartOffset < textEnd)
            .Select(item =>
            {
                var start = Math.Max(textOffset, item.StartOffset);
                var end = Math.Min(textEnd, Math.Max(item.StartOffset, item.EndOffset));
                return new ReaderLinuxTextFallbackAnnotationRange(
                    start - textOffset,
                    Math.Max(0, end - start),
                    NormalizeReaderAnnotationStyle(item.UnderlineStyle),
                    NormalizeReaderAnnotationColor(item.Color));
            })
            .Where(item => item.Length > 0)
            .ToArray();
    }

    private void ApplyLinuxReaderTextFallbackAnnotationRanges()
    {
        if (!OperatingSystem.IsLinux()) return;
        foreach (var block in ReaderLinuxTextFallbackBlocks.Where(item => item.IsText))
        {
            block.SetAnnotationRanges(GetLinuxReaderTextFallbackAnnotationRanges(
                block.TextOffset,
                block.Text.Length));
        }

        if (IsLinuxReaderTextFallbackActive() && _readerLayout.FlowMode == 1)
            RenderLinuxReaderTextFallbackPage();
    }

    private void PromoteLinuxReaderFirstChapterTitle()
    {
        var expectedTitle = NormalizeReaderPlainTextLine(
            _readerLinuxTextFallbackTargetTitle
                ?? GetReaderChapterDisplayName(_readerChapterIndex));
        if (string.IsNullOrWhiteSpace(expectedTitle)) return;

        var firstTextIndex = -1;
        for (var index = 0; index < ReaderLinuxTextFallbackBlocks.Count; index++)
        {
            if (ReaderLinuxTextFallbackBlocks[index].IsText)
            {
                firstTextIndex = index;
                break;
            }
        }

        if (firstTextIndex < 0) return;
        var block = ReaderLinuxTextFallbackBlocks[firstTextIndex];
        if (block.IsChapterTitle) return;
        var firstLine = NormalizeReaderPlainTextLine(
            NormalizeReaderPlainText(block.Text)
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault());
        if (!string.Equals(firstLine, expectedTitle, StringComparison.OrdinalIgnoreCase))
            return;

        ReaderLinuxTextFallbackBlocks[firstTextIndex] = block.HasInlineFootnotes
            ? new ReaderLinuxTextFallbackBlock(
                block.Text,
                block.TextOffset,
                block.FontSize,
                block.LineHeight,
                block.MaxWidth,
                block.InlineFootnotes,
                isChapterTitle: true)
            : new ReaderLinuxTextFallbackBlock(
                block.Text,
                block.TextOffset,
                block.FontSize,
                block.LineHeight,
                block.MaxWidth,
                isChapterTitle: true);
    }

    private static int IndexOfReaderPlainTextBlock(string fullText, string blockText, int startIndex)
    {
        if (string.IsNullOrWhiteSpace(fullText) || string.IsNullOrWhiteSpace(blockText))
            return -1;

        startIndex = Math.Clamp(startIndex, 0, fullText.Length);
        var index = fullText.IndexOf(blockText, startIndex, StringComparison.Ordinal);
        if (index >= 0) return index;

        var firstLine = blockText
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(firstLine)) return -1;
        return fullText.IndexOf(firstLine.Trim(), startIndex, StringComparison.Ordinal);
    }

    private void UpdateLinuxReaderTextFallbackImages(IReadOnlyList<string> imagePaths)
    {
        ClearLinuxReaderTextFallbackImages();
        ReaderLinuxTextFallbackBlocks.Clear();
        var (maxWidth, maxHeight) = GetLinuxReaderTextFallbackImageBounds();
        foreach (var imagePath in imagePaths.Take(12))
        {
            try
            {
                var image = new ReaderLinuxTextFallbackImage(
                    new Bitmap(imagePath),
                    maxWidth,
                    maxHeight);
                ReaderLinuxTextFallbackImages.Add(image);
                ReaderLinuxTextFallbackBlocks.Add(new ReaderLinuxTextFallbackBlock(image));
            }
            catch
            {
            }
        }
    }

    private void UpdateLinuxReaderTextFallbackImageSizes()
    {
        if (ReaderLinuxTextFallbackImages.Count == 0) return;
        var (maxWidth, maxHeight) = GetLinuxReaderTextFallbackImageBounds();
        foreach (var image in ReaderLinuxTextFallbackImages)
            image.Resize(maxWidth, maxHeight);
        if (_readerLayout.FlowMode == 1)
            RenderLinuxReaderTextFallbackPage();
    }

    /// <summary>
    /// Pins the scrolling fallback paragraphs to a measured width so that
    /// starting or ending a selection on a line boundary cannot re-wrap the
    /// body text (the paged surface pins its rectangle the same way).
    /// </summary>
    private void UpdateLinuxReaderTextFallbackBlockWidths()
    {
        if (ReaderLinuxTextFallbackBlocks.Count == 0) return;
        var width = GetLinuxReaderTextFallbackBlockWidth();
        foreach (var block in ReaderLinuxTextFallbackBlocks)
        {
            if (block.IsText)
                block.ResizeText(width);
        }
    }

    private double GetLinuxReaderTextFallbackBlockWidth()
    {
        var available = ReaderLinuxTextFallbackScroll.Bounds.Width;
        if (!double.IsFinite(available) || available <= 0)
            available = ReaderLinuxTextFallbackOverlay.Bounds.Width;
        if (!double.IsFinite(available) || available <= 0)
            available = ReaderWebViewHost.Bounds.Width;
        if (!double.IsFinite(available) || available <= 0)
            available = Math.Max(320, _readerLayout.MaxWidth);

        // ScrollViewer padding (24 per side) plus room for the vertical scrollbar.
        available -= 48 + 18;
        var maxWidth = Math.Max(320, _readerLayout.MaxWidth);
        return Math.Floor(Math.Max(240, Math.Min(maxWidth, available)));
    }

    private (double MaxWidth, double MaxHeight) GetLinuxReaderTextFallbackImageBounds()    {
        var overlayWidth = ReaderLinuxTextFallbackOverlay.Bounds.Width;
        if (!double.IsFinite(overlayWidth) || overlayWidth <= 0)
            overlayWidth = ReaderWebViewHost.Bounds.Width;
        var overlayHeight = ReaderLinuxTextFallbackOverlay.Bounds.Height;
        if (!double.IsFinite(overlayHeight) || overlayHeight <= 0)
            overlayHeight = ReaderWebViewHost.Bounds.Height;

        var availableWidth = Math.Max(80, overlayWidth - 48);
        var availableHeight = Math.Max(80, overlayHeight - 36);
        return (availableWidth, availableHeight);
    }

    private (double MaxWidth, double MaxHeight) GetLinuxReaderTextFallbackPagedImageBounds(SelectableTextBlock textBlock)
    {
        var slotWidth = double.IsFinite(textBlock.MaxWidth) && textBlock.MaxWidth > 0
            ? textBlock.MaxWidth
            : 0;
        if (slotWidth <= 0)
        {
            var contentWidth = ReaderLinuxTextFallbackPagedContent.Bounds.Width;
            if (!double.IsFinite(contentWidth) || contentWidth <= 0)
                contentWidth = ReaderLinuxTextFallbackPagedRoot.Bounds.Width;
            if (!double.IsFinite(contentWidth) || contentWidth <= 0)
                contentWidth = ReaderLinuxTextFallbackOverlay.Bounds.Width;
            slotWidth = _readerLayout.TwoPageMode
                ? Math.Max(80, (contentWidth - 28) / 2)
                : Math.Max(80, Math.Min(_readerLayout.MaxWidth, contentWidth));
        }

        var slotHeight = ReaderLinuxTextFallbackPagedRoot.Bounds.Height;
        if (!double.IsFinite(slotHeight) || slotHeight <= 0)
            slotHeight = ReaderLinuxTextFallbackOverlay.Bounds.Height;
        slotHeight -= ReaderLinuxTextFallbackPagedRoot.Padding.Top + ReaderLinuxTextFallbackPagedRoot.Padding.Bottom;
        return (Math.Max(80, slotWidth), Math.Max(80, slotHeight));
    }

    private void ClearLinuxReaderTextFallbackImages()
    {
        ReaderLinuxTextFallbackPageImageLeft.Source = null;
        ReaderLinuxTextFallbackPageImageRight.Source = null;
        ReaderLinuxTextFallbackPageImageLeft.IsVisible = false;
        ReaderLinuxTextFallbackPageImageRight.IsVisible = false;
        foreach (var image in ReaderLinuxTextFallbackImages)
            image.Dispose();
        ReaderLinuxTextFallbackImages.Clear();
        ReaderLinuxTextFallbackBlocks.Clear();
    }

    private void ClearLinuxReaderTextFallbackSelectionState()
    {
        ClearLinuxReaderTextFallbackVisualSelection();
        HideReaderSelectionPopup();
        _readerPendingSelection = null;
        _readerPendingSelectionStartOffset = 0;
        _readerPendingSelectionEndOffset = 0;
        _readerPendingSelectionPrefix = string.Empty;
        _readerPendingSelectionSuffix = string.Empty;
        _selectedReaderAnnotation = null;
    }

    private void ClearLinuxReaderTextFallbackVisualSelection()
    {
        var blocks = new[]
            {
                ReaderLinuxTextFallbackText,
                ReaderLinuxTextFallbackPageLeft,
                ReaderLinuxTextFallbackPageRight
            }
            .Concat(ReaderLinuxTextFallbackOverlay
                .GetVisualDescendants()
                .OfType<SelectableTextBlock>())
            .Distinct();

        foreach (var block in blocks)
            block.ClearSelection();
        ReaderLinuxTextFallbackPageVertical.ClearSelection();
    }

    private bool HasLinuxReaderTextFallbackSelection()
    {
        if (!string.IsNullOrWhiteSpace(_readerPendingSelection)
            || ReaderSelectionHostPopup.IsOpen)
        {
            return true;
        }

        return new[]
            {
                ReaderLinuxTextFallbackText,
                ReaderLinuxTextFallbackPageLeft,
                ReaderLinuxTextFallbackPageRight
            }
            .Concat(ReaderLinuxTextFallbackOverlay
                .GetVisualDescendants()
                .OfType<SelectableTextBlock>())
            .Distinct()
            .Any(block => block.SelectionStart != block.SelectionEnd)
            || (ReaderLinuxTextFallbackPageVertical.IsVisible
                && ReaderLinuxTextFallbackPageVertical.SelectionStart
                    != ReaderLinuxTextFallbackPageVertical.SelectionEnd);
    }

    private void SyncLinuxReaderTextFallbackSelectionState(Point? placementPoint = null, double? selectionBottom = null)
    {
        if (!IsLinuxReaderTextFallbackActive())
            return;

        if (_readerLayout.FlowMode == 0)
        {
            ClearLinuxReaderTextFallbackSelectionState();
            return;
        }

        if (ReaderLinuxTextFallbackPageVertical.IsVisible
            && TrySyncLinuxReaderTextFallbackSelectionFromVertical())
        {
            var anchor = GetVerticalReaderSelectionAnchorRect();
            ShowReaderSelectionPopup(
                anchor?.Position ?? placementPoint,
                anchor?.Bottom ?? selectionBottom);
            return;
        }

        if (TrySyncLinuxReaderTextFallbackSelectionFromBlock(
                ReaderLinuxTextFallbackPageLeft,
                GetLinuxReaderTextFallbackPageText(0),
                _readerLinuxTextFallbackPageIndex))
        {
            var anchor = GetReaderSelectionAnchorRect(ReaderLinuxTextFallbackPageLeft);
            ShowReaderSelectionPopup(
                anchor?.Position ?? placementPoint,
                anchor?.Bottom ?? selectionBottom);
            return;
        }

        if (ReaderLinuxTextFallbackPageRight.IsVisible
            && TrySyncLinuxReaderTextFallbackSelectionFromBlock(
                ReaderLinuxTextFallbackPageRight,
                GetLinuxReaderTextFallbackPageText(1),
                _readerLinuxTextFallbackPageIndex + 1))
        {
            var anchor = GetReaderSelectionAnchorRect(ReaderLinuxTextFallbackPageRight);
            ShowReaderSelectionPopup(
                anchor?.Position ?? placementPoint,
                anchor?.Bottom ?? selectionBottom);
            return;
        }

        ClearLinuxReaderTextFallbackSelectionState();
    }

    private void ScheduleLinuxReaderTextFallbackSelectionSync(
        Point placementPoint,
        double? selectionBottom = null)
    {
        var sequence = ++_readerLinuxTextFallbackSelectionSyncSequence;
        Dispatcher.UIThread.Post(
            () =>
            {
                if (sequence != _readerLinuxTextFallbackSelectionSyncSequence
                    || !IsLinuxReaderTextFallbackActive()
                    || ReaderSelectionHostPopup.IsOpen)
                    return;

                // SelectableTextBlock occasionally commits SelectionEnd after
                // its PointerReleased route has completed. Re-read it on the
                // next input frame so a valid drag never loses its toolbar.
                SyncLinuxReaderTextFallbackSelectionState(
                    placementPoint,
                    selectionBottom);
            },
            DispatcherPriority.Input);
    }

    private bool TrySyncLinuxReaderTextFallbackSelectionFromBlock(
        SelectableTextBlock block,
        string pageText,
        int pageIndex)
    {
        var selectedText = block.SelectedText?.Trim();
        if (string.IsNullOrWhiteSpace(selectedText))
            return false;

        var start = Math.Min(block.SelectionStart, block.SelectionEnd);
        var end = Math.Max(block.SelectionStart, block.SelectionEnd);
        start = Math.Clamp(start, 0, pageText.Length);
        end = Math.Clamp(end, 0, pageText.Length);
        var pageOffset = _readerLayout.FlowMode == 0 ? 0 : GetLinuxReaderTextFallbackPageOffset(pageIndex);

        _readerPendingSelection = selectedText;
        _readerPendingSelectionStartOffset = pageOffset + start;
        _readerPendingSelectionEndOffset = pageOffset + end;
        _readerPendingSelectionPrefix = pageText[..start];
        _readerPendingSelectionSuffix = end < pageText.Length ? pageText[end..] : string.Empty;
        _selectedReaderAnnotation = null;
        return true;
    }

    private Rect? GetReaderSelectionAnchorRect(SelectableTextBlock block)
    {
        try
        {
            var start = Math.Max(0, Math.Min(block.SelectionStart, block.SelectionEnd));
            var end = Math.Max(0, Math.Max(block.SelectionStart, block.SelectionEnd));
            if (block.TextLayout is not { } layout)
                return null;

            var left = double.PositiveInfinity;
            var top = double.PositiveInfinity;
            var right = double.NegativeInfinity;
            var boundsBottom = double.NegativeInfinity;
            var lineTop = 0d;
            foreach (var line in layout.TextLines)
            {
                var lineStart = Math.Max(0, line.FirstTextSourceIndex);
                var lineEnd = Math.Max(lineStart, lineStart + line.Length);
                var selectedStart = Math.Max(start, lineStart);
                var selectedEnd = Math.Min(end, lineEnd);
                if (selectedEnd <= selectedStart)
                {
                    lineTop += Math.Max(0, line.Height);
                    continue;
                }

                var segmentLeft = double.PositiveInfinity;
                var segmentRight = double.NegativeInfinity;
                foreach (var textBounds in line.GetTextBounds(
                             selectedStart,
                             selectedEnd - selectedStart))
                {
                    segmentLeft = Math.Min(segmentLeft, textBounds.Rectangle.Left);
                    segmentRight = Math.Max(segmentRight, textBounds.Rectangle.Right);
                }

                if (!double.IsFinite(segmentLeft)
                    || !double.IsFinite(segmentRight))
                {
                    var startHit = layout.HitTestTextPosition(selectedStart);
                    var endHit = layout.HitTestTextPosition(Math.Max(selectedStart, selectedEnd - 1));
                    segmentLeft = selectedStart <= lineStart
                        ? line.Start
                        : startHit.X;
                    segmentRight = selectedEnd >= lineEnd
                        ? line.Start + line.WidthIncludingTrailingWhitespace
                        : endHit.Right;
                }
                if (!double.IsFinite(segmentLeft)
                    || !double.IsFinite(segmentRight)
                    || !double.IsFinite(lineTop)
                    || !double.IsFinite(line.Height))
                {
                    lineTop += Math.Max(0, line.Height);
                    continue;
                }

                left = Math.Min(left, segmentLeft);
                top = Math.Min(top, lineTop);
                right = Math.Max(right, Math.Max(segmentLeft, segmentRight));
                boundsBottom = Math.Max(boundsBottom, lineTop + line.Height);
                lineTop += Math.Max(0, line.Height);
            }

            if (!double.IsFinite(left)
                || !double.IsFinite(top)
                || !double.IsFinite(right)
                || !double.IsFinite(boundsBottom))
                return null;

            var topLeft = block.TranslatePoint(new Point(left, top), ReaderWebViewHost);
            var bottomRight = block.TranslatePoint(new Point(right, boundsBottom), ReaderWebViewHost);
            return topLeft is { } topPoint && bottomRight is { } bottomPoint
                ? new Rect(topPoint, bottomPoint)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private bool TrySyncLinuxReaderTextFallbackSelectionFromBlock(
        SelectableTextBlock block,
        ReaderLinuxTextFallbackBlock textBlock)
    {
        var selectedText = block.SelectedText?.Trim();
        if (string.IsNullOrWhiteSpace(selectedText))
            return false;

        var pageText = textBlock.Text;
        var start = Math.Min(block.SelectionStart, block.SelectionEnd);
        var end = Math.Max(block.SelectionStart, block.SelectionEnd);
        start = Math.Clamp(start, 0, pageText.Length);
        end = Math.Clamp(end, 0, pageText.Length);

        _readerPendingSelection = selectedText;
        _readerPendingSelectionStartOffset = textBlock.TextOffset + start;
        _readerPendingSelectionEndOffset = textBlock.TextOffset + end;
        _readerPendingSelectionPrefix = pageText[..start];
        _readerPendingSelectionSuffix = end < pageText.Length ? pageText[end..] : string.Empty;
        _selectedReaderAnnotation = null;
        return true;
    }

    // Vertical pages keep selection state on the custom drawing control; the
    // offsets map onto the raw page text exactly like the horizontal slots.
    private bool TrySyncLinuxReaderTextFallbackSelectionFromVertical()
    {
        var control = ReaderLinuxTextFallbackPageVertical;
        var selectedText = control.SelectedText?.Trim();
        if (string.IsNullOrWhiteSpace(selectedText))
            return false;

        var pageIndex = _readerLinuxTextFallbackPageIndex;
        if (pageIndex < 0 || pageIndex >= _readerLinuxTextFallbackPageItems.Count)
            return false;
        var item = _readerLinuxTextFallbackPageItems[pageIndex];
        if (item.IsImage)
            return false;

        var pageText = item.Text;
        var start = Math.Min(control.SelectionStart, control.SelectionEnd);
        var end = Math.Max(control.SelectionStart, control.SelectionEnd);
        start = Math.Clamp(start, 0, pageText.Length);
        end = Math.Clamp(end, 0, pageText.Length);

        _readerPendingSelection = selectedText;
        _readerPendingSelectionStartOffset = item.PaginationOffset + start;
        _readerPendingSelectionEndOffset = item.PaginationOffset + end;
        _readerPendingSelectionPrefix = pageText[..start];
        _readerPendingSelectionSuffix = end < pageText.Length ? pageText[end..] : string.Empty;
        _selectedReaderAnnotation = null;
        return true;
    }

    private Rect? GetVerticalReaderSelectionAnchorRect()
    {
        try
        {
            var control = ReaderLinuxTextFallbackPageVertical;
            var start = Math.Max(0, Math.Min(control.SelectionStart, control.SelectionEnd));
            var end = Math.Max(0, Math.Max(control.SelectionStart, control.SelectionEnd));
            if (end <= start
                || control.GetRangeAnchorRect(start, end) is not { } localRect)
            {
                return null;
            }

            var topLeft = control.TranslatePoint(localRect.TopLeft, ReaderWebViewHost);
            var bottomRight = control.TranslatePoint(localRect.BottomRight, ReaderWebViewHost);
            return topLeft is { } topPoint && bottomRight is { } bottomPoint
                ? new Rect(topPoint, bottomPoint)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private bool TrySyncLinuxReaderTextFallbackSelectionFromVisual(
        Visual visual,
        Point placementPoint)
    {
        Visual? current = visual;
        while (current is not null)
        {
            if (current is SelectableTextBlock block
                && block.DataContext is ReaderLinuxTextFallbackBlock textBlock
                && TrySyncLinuxReaderTextFallbackSelectionFromBlock(block, textBlock))
            {
                var anchor = GetReaderSelectionAnchorRect(block);
                ShowReaderSelectionPopup(
                    anchor?.Position ?? placementPoint,
                    anchor?.Bottom);
                return true;
            }

            current = current.GetVisualParent();
        }

        return false;
    }

    private string GetLinuxReaderTextFallbackPageText(int relativePageIndex)
    {
        if (_readerLinuxTextFallbackPages.Count == 0)
            return string.Empty;
        var pageIndex = Math.Clamp(
            _readerLinuxTextFallbackPageIndex + relativePageIndex,
            0,
            Math.Max(0, GetLinuxReaderTextFallbackPageCount() - 1));
        return pageIndex >= 0
            && pageIndex < _readerLinuxTextFallbackPageItems.Count
            && !_readerLinuxTextFallbackPageItems[pageIndex].IsImage
            ? _readerLinuxTextFallbackPageItems[pageIndex].Text
            : string.Empty;
    }

    private int GetLinuxReaderTextFallbackPageOffset(int pageIndex)
    {
        if (_readerLinuxTextFallbackPageItems.Count == 0) return 0;
        pageIndex = Math.Clamp(pageIndex, 0, Math.Max(0, GetLinuxReaderTextFallbackPageCount() - 1));
        return !_readerLinuxTextFallbackPageItems[pageIndex].IsImage
            // Selection and annotation painting must use the same coordinate
            // space. TextOffset is the source-block offset and is shared by
            // every measured page produced from that block group, so using it
            // makes selections on later pages save against the first page.
            ? _readerLinuxTextFallbackPageItems[pageIndex].PaginationOffset
            : 0;
    }

    private static bool IsWithinReaderLinuxTextContent(Visual visual)
    {
        if (visual is SelectableTextBlock or Image)
            return true;
        var current = visual.GetVisualParent();
        while (current is not null)
        {
            if (current is SelectableTextBlock or Image)
                return true;
            current = current.GetVisualParent();
        }
        return false;
    }

    private static bool IsReaderLinuxTextFallbackScrollBarVisual(Visual visual)
    {
        var current = visual;
        while (current is not null)
        {
            if (current is ScrollBar)
                return true;
            current = current.GetVisualParent();
        }

        return false;
    }

    private static double GetReaderPlainTextCharUnits(char ch)
    {
        if (char.IsWhiteSpace(ch)) return 0.35;
        if (ch <= 0x007f) return char.IsLetterOrDigit(ch) ? 0.56 : 0.38;
        return 1.0;
    }

    private void ClampLinuxReaderTextFallbackOffset()
    {
        var extent = Math.Max(0, ReaderLinuxTextFallbackScroll.Extent.Height);
        var viewport = Math.Max(0, ReaderLinuxTextFallbackScroll.Viewport.Height);
        var maximum = Math.Max(0, extent - viewport);
        var current = Math.Max(0, ReaderLinuxTextFallbackScroll.Offset.Y);
        var clamped = Math.Clamp(current, 0, maximum);
        if (Math.Abs(clamped - current) > 0.5)
            ReaderLinuxTextFallbackScroll.Offset = new Vector(0, clamped);
    }

    private void SetLinuxReaderTextFallbackOffset(double offset, bool saveProgress = true)
    {
        var extent = Math.Max(0, ReaderLinuxTextFallbackScroll.Extent.Height);
        var viewport = Math.Max(0, ReaderLinuxTextFallbackScroll.Viewport.Height);
        var maximum = Math.Max(0, extent - viewport);
        var target = Math.Clamp(offset, 0, maximum);
        ReaderLinuxTextFallbackScroll.Offset = new Vector(0, target);
        SyncLinuxReaderTextFallbackScrollState(saveProgress);
    }

    private void ReaderLinuxTextFallback_Tapped(object? sender, TappedEventArgs e)
    {
        if (!IsLinuxReaderTextFallbackActive()) return;
        var tappedPoint = e.GetPosition(ReaderLinuxTextFallbackOverlay);
        var tappedButton = FindReaderFootnoteButton(e.Source as Visual)
            ?? FindReaderFootnoteButtonAt(tappedPoint);
        if (tappedButton is not null)
        {
            e.Handled = true;
            return;
        }
        if (e.Source is Visual visual
            && TrySyncLinuxReaderTextFallbackSelectionFromVisual(
                visual,
                e.GetPosition(ReaderWebViewHost)))
        {
            return;
        }
        if (e.Source is Visual source && !IsWithinReaderLinuxTextContent(source))
            ClearLinuxReaderTextFallbackSelectionState();
    }

    private void ReaderLinuxTextFallback_ContentPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is SelectableTextBlock block
            && block.DataContext is ReaderLinuxTextFallbackBlock textBlock
            && TrySyncLinuxReaderTextFallbackSelectionFromBlock(block, textBlock))
        {
            var point = e.GetPosition(ReaderWebViewHost);
            var bottom = block.TranslatePoint(new Point(0, block.Bounds.Height), ReaderWebViewHost)?.Y;
            var anchor = GetReaderSelectionAnchorRect(block);
            ShowReaderSelectionPopup(
                anchor?.Position ?? point,
                anchor?.Bottom ?? bottom);
            return;
        }

        var fallbackPoint = e.GetPosition(ReaderWebViewHost);
        var fallbackBottom = sender is SelectableTextBlock fallbackBlock
            ? fallbackBlock.TranslatePoint(new Point(0, fallbackBlock.Bounds.Height), ReaderWebViewHost)?.Y
            : null;
        SyncLinuxReaderTextFallbackSelectionState(fallbackPoint, fallbackBottom);
        if (string.IsNullOrWhiteSpace(_readerPendingSelection))
            ScheduleLinuxReaderTextFallbackSelectionSync(fallbackPoint, fallbackBottom);
    }

    private void ReaderLinuxTextFallbackFootnoteButton_PointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is not Button { Tag: string href }) return;
        _ = ObserveReaderTaskAsync(HandleReaderFootnoteHoverAsync(
            href,
            isFootnote: true,
            e.GetPosition(ReaderWebViewHost)));
    }

    private void ReaderLinuxTextFallbackFootnoteButton_PointerExited(object? sender, PointerEventArgs e)
    {
        HideReaderFootnotePopup();
    }

    private static Button? FindReaderFootnoteButton(Visual? visual)
    {
        var current = visual;
        while (current is not null)
        {
            if (current is Button { Tag: string href }
                && !string.IsNullOrWhiteSpace(href))
                return (Button)current;
            current = current.GetVisualParent();
        }
        return null;
    }

    private Button? FindReaderFootnoteButtonAt(Point point)
    {
        foreach (var visual in ReaderLinuxTextFallbackOverlay.GetVisualsAt(point).Reverse())
        {
            if (FindReaderFootnoteButton(visual) is { } button)
                return button;
        }
        return null;
    }

    private bool TrySyncLinuxReaderTextFallbackSelectionAt(
        Point point,
        Point placementPoint)
    {
        foreach (var visual in ReaderLinuxTextFallbackOverlay.GetVisualsAt(point).Reverse())
        {
            if (TrySyncLinuxReaderTextFallbackSelectionFromVisual(visual, placementPoint))
                return true;
        }

        return false;
    }

    private void ReaderLinuxTextFallback_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!IsLinuxReaderTextFallbackActive()) return;
        var button = FindReaderFootnoteButton(e.Source as Visual)
            ?? FindReaderFootnoteButtonAt(e.GetPosition(ReaderLinuxTextFallbackOverlay));
        if (button is not { Tag: string href })
        {
            HideReaderFootnotePopup();
            return;
        }

        if (ReaderFootnoteHostPopup.IsOpen
            && string.Equals(_readerFootnoteHref, href, StringComparison.Ordinal))
            return;
        _ = ObserveReaderTaskAsync(HandleReaderFootnoteHoverAsync(
            href,
            isFootnote: true,
            e.GetPosition(ReaderWebViewHost)));
    }

    private void ReaderLinuxTextFallback_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _readerLinuxTextFallbackSelectionSyncSequence++;
        if (_readerLinuxTextFallbackSelectionDismissPress)
        {
            // Consume the marker on the matching press. Keeping it until a
            // later release lets a footer/chrome dismiss leak across a page
            // turn and swallow the first selection drag on the new page.
            _readerLinuxTextFallbackSelectionDismissPress = false;
            _readerLinuxTextFallbackPointerPressed = false;
            _readerLinuxTextFallbackSelectionAtPointerPress = false;
            _readerLinuxTextFallbackSelectionScrollLocked = false;
            e.Handled = true;
            return;
        }

        if (!IsLinuxReaderTextFallbackActive()
            || (_readerLayout.FlowMode != 0 && _readerLayout.FlowMode != 1))
        {
            _readerLinuxTextFallbackPointerPressed = false;
            _readerLinuxTextFallbackSelectionAtPointerPress = false;
            _readerLinuxTextFallbackSelectionScrollLocked = false;
            return;
        }

        var point = e.GetCurrentPoint(ReaderLinuxTextFallbackOverlay);
        if (!point.Properties.IsLeftButtonPressed || e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            _readerLinuxTextFallbackPointerPressed = false;
            _readerLinuxTextFallbackSelectionAtPointerPress = false;
            _readerLinuxTextFallbackSelectionScrollLocked = false;
            return;
        }

        // A press exactly on the first/last glyph of a line can be hit-tested
        // as ScrollContentPresenter instead of SelectableTextBlock. It is
        // still a text-selection press, and SelectableTextBlock may scroll
        // itself to reveal the selection. Freeze the reader for every press
        // in the continuous viewport; leave the scrollbar free to scroll.
        if (_readerLayout.FlowMode == 0
            && e.Source is Visual source
            && IsReaderLinuxTextFallbackScrollBarVisual(source))
        {
            _readerLinuxTextFallbackPointerPressed = false;
            _readerLinuxTextFallbackSelectionAtPointerPress = false;
            _readerLinuxTextFallbackSelectionScrollLocked = false;
            return;
        }

        _readerLinuxTextFallbackPointerPressed = true;
        _readerLinuxTextFallbackSelectionAtPointerPress =
            HasLinuxReaderTextFallbackSelection();
        _readerLinuxTextFallbackPointerStart = point.Position;
        if (_readerLayout.FlowMode == 0)
        {
            _readerLinuxTextFallbackSelectionScrollOffset =
                ReaderLinuxTextFallbackScroll.Offset;
            _readerLinuxTextFallbackSelectionScrollLocked = true;
        }
    }

    private void ReaderLinuxTextFallback_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_readerLinuxTextFallbackSelectionDismissPress)
        {
            _readerLinuxTextFallbackSelectionDismissPress = false;
            _readerLinuxTextFallbackPointerPressed = false;
            _readerLinuxTextFallbackSelectionAtPointerPress = false;
            ReleaseLinuxTextFallbackSelectionScrollLock();
            e.Handled = true;
            return;
        }

        if (!IsLinuxReaderTextFallbackActive())
        {
            _readerLinuxTextFallbackPointerPressed = false;
            _readerLinuxTextFallbackSelectionAtPointerPress = false;
            _readerLinuxTextFallbackSelectionScrollLocked = false;
            return;
        }

        var point = e.GetPosition(ReaderLinuxTextFallbackOverlay);
        var pointerWasPressed = _readerLinuxTextFallbackPointerPressed;
        var moved = pointerWasPressed
            ? Math.Abs(point.X - _readerLinuxTextFallbackPointerStart.X)
                + Math.Abs(point.Y - _readerLinuxTextFallbackPointerStart.Y)
            : double.PositiveInfinity;
        var dismissSelection = pointerWasPressed
            && _readerLinuxTextFallbackSelectionAtPointerPress
            && moved <= 12;
        _readerLinuxTextFallbackSelectionAtPointerPress = false;
        if (dismissSelection)
        {
            _readerLinuxTextFallbackPointerPressed = false;
            ClearLinuxReaderTextFallbackSelectionState();
            ReleaseLinuxTextFallbackSelectionScrollLock();
            e.Handled = true;
            return;
        }
        if ((FindReaderFootnoteButton(e.Source as Visual)
                ?? FindReaderFootnoteButtonAt(point)) is not null)
        {
            _readerLinuxTextFallbackPointerPressed = false;
            ReleaseLinuxTextFallbackSelectionScrollLock();
            e.Handled = true;
            return;
        }
        if (_readerLayout.VerticalWriting && _readerLinuxVerticalFootnoteHandledRelease)
        {
            // The vertical page already popped its footnote on release; do not
            // let the same tap also register as an edge click-turn.
            _readerLinuxVerticalFootnoteHandledRelease = false;
            _readerLinuxTextFallbackPointerPressed = false;
            ReleaseLinuxTextFallbackSelectionScrollLock();
            e.Handled = true;
            return;
        }
        if (_readerLayout.FlowMode == 0)
        {
            _readerLinuxTextFallbackPointerPressed = false;
            var placementPoint = e.GetPosition(ReaderWebViewHost);
            var synced = e.Source is Visual selectedVisual
                && TrySyncLinuxReaderTextFallbackSelectionFromVisual(
                    selectedVisual,
                    placementPoint);
            if (!synced)
            {
                synced = TrySyncLinuxReaderTextFallbackSelectionAt(point, placementPoint);
            }
            if (synced)
            {
                // Keep the original viewport frozen until selection indices,
                // anchor geometry, and the popup have all been computed. A
                // selectable text block can request a bring-into-view during
                // that work; releasing the lock first reintroduces the jump
                // at a line boundary.
                ReleaseLinuxTextFallbackSelectionScrollLock();
                return;
            }
        }
        SyncLinuxReaderTextFallbackSelectionState(e.GetPosition(ReaderWebViewHost));
        if (_readerLayout.FlowMode == 0)
            ReleaseLinuxTextFallbackSelectionScrollLock();
        if (!string.IsNullOrWhiteSpace(_readerPendingSelection))
        {
            _readerLinuxTextFallbackPointerPressed = false;
            return;
        }
        if (pointerWasPressed && moved > 12)
        {
            _readerLinuxTextFallbackPointerPressed = false;
            ScheduleLinuxReaderTextFallbackSelectionSync(
                e.GetPosition(ReaderWebViewHost));
            return;
        }

        if (!_readerLinuxTextFallbackPointerPressed || _readerLayout.FlowMode != 1)
        {
            _readerLinuxTextFallbackPointerPressed = false;
            return;
        }

        _readerLinuxTextFallbackPointerPressed = false;
        if (moved > 12) return;

        var width = Math.Max(1, ReaderLinuxTextFallbackOverlay.Bounds.Width);
        if (point.X < width / 3 || point.X > width * 2 / 3)
        {
            var onLeft = point.X < width / 3;
            var direction = ReaderPaginationPolicy.GetClickDirection(
                onLeft,
                _readerLayout.VerticalWriting);
            e.Handled = true;
            HideReaderSelectionPopup();
            _ = ObserveReaderTaskAsync(TurnReaderPageAsync(direction));
        }
    }

    private void ReaderLinuxTextFallback_KeyDown(object? sender, KeyEventArgs e)
    {
        TryHandleLinuxReaderTextFallbackKeyDown(e);
    }

    private bool TryHandleLinuxReaderTextFallbackKeyDown(KeyEventArgs e)
    {
        if (!IsLinuxReaderTextFallbackActive() || IsReaderTextInputFocused()) return false;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return false;

        if (e.Key == Key.F11)
        {
            e.Handled = true;
            ToggleReaderZenMode();
            return true;
        }
        if (e.Key == Key.Escape)
        {
            e.Handled = HandleReaderEscapeShortcut();
            return e.Handled;
        }
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.F)
        {
            e.Handled = true;
            OpenReaderSearchShortcut();
            return true;
        }
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.B)
        {
            e.Handled = true;
            _ = ObserveReaderTaskAsync(ToggleReaderBookmarkAsync());
            return true;
        }

        if (_readerLayout.FlowMode == 1)
        {
            var chapterDirection = !_readerIsPdf
                ? e.Key == Key.Up
                    ? -1
                    : e.Key == Key.Down
                        ? 1
                        : 0
                : 0;
            if (chapterDirection != 0)
            {
                e.Handled = true;
                ReaderLinuxTextFallbackOverlay.Focus();
                _ = ObserveReaderTaskAsync(TurnReaderPageAsync(chapterDirection, chapterOnly: true));
                return true;
            }

            // Vertical writing mirrors the horizontal key map: left turns
            // forward and right turns backward; PageUp/PageDown stay put.
            var verticalFlip = !_readerIsPdf && _readerLayout.VerticalWriting;
            var direction = e.Key switch
            {
                Key.Left => verticalFlip ? 1 : -1,
                Key.PageUp => -1,
                Key.Right => verticalFlip ? -1 : 1,
                Key.PageDown => 1,
                _ => 0
            };
            if (direction != 0)
            {
                e.Handled = true;
                ReaderLinuxTextFallbackOverlay.Focus();
                _ = ObserveReaderTaskAsync(TurnReaderPageAsync(direction));
                return true;
            }
        }
        else if (!_readerIsPdf && _readerDocument is not null)
        {
            var chapterDirection = e.Key == Key.Left
                ? -1
                : e.Key == Key.Right
                    ? 1
                    : 0;
            if (chapterDirection != 0)
            {
                e.Handled = true;
                ReaderLinuxTextFallbackOverlay.Focus();
                _ = ObserveReaderTaskAsync(TurnReaderPageAsync(chapterDirection, chapterOnly: true));
                return true;
            }

            var scrollDirection = e.Key == Key.Up
                ? -1
                : e.Key == Key.Down
                    ? 1
                    : 0;
            if (scrollDirection != 0)
            {
                e.Handled = true;
                ReaderLinuxTextFallbackOverlay.Focus();
                _ = ObserveReaderTaskAsync(ScrollReaderWithKeyboardAsync(scrollDirection));
                return true;
            }

            var pageDirection = e.Key == Key.PageUp
                ? -1
                : e.Key == Key.PageDown
                    ? 1
                    : 0;
            if (pageDirection != 0)
            {
                e.Handled = true;
                ReaderLinuxTextFallbackOverlay.Focus();
                _ = ObserveReaderTaskAsync(TurnReaderPageAsync(pageDirection));
                return true;
            }
        }

        return false;
    }

    private void ReaderLinuxTextFallback_PointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (!IsLinuxReaderTextFallbackActive()) return;
        var delta = e.Delta.Y;
        if (Math.Abs(delta) < 0.01) return;
        var direction = delta < 0 ? 1 : -1;
        if (_readerLayout.FlowMode == 1)
        {
            e.Handled = true;
            var wheelDelta = (int)Math.Round(-delta * (Math.Abs(delta) < 12 ? 120 : 1));
            if (_readerLinuxTextFallbackWheelDeltaRemainder != 0
                && Math.Sign(_readerLinuxTextFallbackWheelDeltaRemainder) != Math.Sign(wheelDelta))
            {
                _readerLinuxTextFallbackWheelDeltaRemainder = 0;
            }
            _readerLinuxTextFallbackWheelDeltaRemainder += wheelDelta;
            if (Math.Abs(_readerLinuxTextFallbackWheelDeltaRemainder) >= 120)
            {
                direction = _readerLinuxTextFallbackWheelDeltaRemainder > 0 ? 1 : -1;
                _readerLinuxTextFallbackWheelDeltaRemainder %= 120;
                _ = ObserveReaderTaskAsync(TurnReaderPageAsync(direction));
            }
            return;
        }

        var extent = Math.Max(0, ReaderLinuxTextFallbackScroll.Extent.Height);
        var viewport = Math.Max(0, ReaderLinuxTextFallbackScroll.Viewport.Height);
        var maximum = Math.Max(0, extent - viewport);
        var current = Math.Clamp(ReaderLinuxTextFallbackScroll.Offset.Y, 0, maximum);
        var atEdge = direction < 0
            ? current <= 4
            : current >= maximum - 4;
        if (!atEdge)
        {
            _readerLinuxTextFallbackContinuousWheelDirection = direction;
            _readerLinuxTextFallbackContinuousWheelLastTick = Environment.TickCount64;
            return;
        }

        e.Handled = true;
        var now = Environment.TickCount64;
        var startsNewGesture = _readerLinuxTextFallbackContinuousWheelLastTick > 0
            && (direction != _readerLinuxTextFallbackContinuousWheelDirection
                || now - _readerLinuxTextFallbackContinuousWheelLastTick >= 180);
        _readerLinuxTextFallbackContinuousWheelDirection = direction;
        _readerLinuxTextFallbackContinuousWheelLastTick = now;
        if (startsNewGesture)
            TryMoveReaderChapterFromContinuousEdge(direction);
    }

    private static ReaderLinuxTextFallbackExtractedContent ExtractReaderFallbackContent(
        string path,
        string? startFragment = null,
        string? startTitle = null,
        string? endFragment = null)
    {
        var text = ExtractReaderPlainText(path, startFragment, startTitle, endFragment);
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true
            };
            using var reader = XmlReader.Create(path, settings);
            var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
            var body = document.Descendants().FirstOrDefault(element =>
                element.Name.LocalName.Equals("body", StringComparison.OrdinalIgnoreCase));
            if (body is null)
                return CreateFallbackContentFromTextAndImages(path, text, startTitle);

            var blocks = new List<ReaderLinuxTextFallbackRawBlock>();
            var builder = new StringBuilder();
            var fragment = startFragment?.TrimStart('#');
            var hasFragment = !string.IsNullOrWhiteSpace(fragment);
            var stopFragment = endFragment?.TrimStart('#');
            var hasStopFragment = !string.IsNullOrWhiteSpace(stopFragment)
                && !string.Equals(stopFragment, fragment, StringComparison.Ordinal);
            var capture = !hasFragment;
            var foundFragment = false;
            var stopCapture = false;

            void FlushText(
                bool isChapterTitle = false,
                bool isParagraphBoundary = false)
            {
                var blockText = NormalizeReaderPlainText(builder.ToString());
                builder.Clear();
                if (!string.IsNullOrWhiteSpace(blockText))
                    blocks.Add(new ReaderLinuxTextFallbackRawBlock(
                        blockText,
                        null,
                        null,
                        null,
                        isChapterTitle,
                        isParagraphBoundary));
            }

            void Visit(
                XNode node,
                bool reflowableParagraph = false,
                bool chapterTitle = false)
            {
                if (node is XText textNode)
                {
                    if (capture)
                        AppendReaderPlainText(builder, textNode.Value);
                    return;
                }

                if (node is not XElement element)
                    return;

                var name = element.Name.LocalName.ToLowerInvariant();
                if (name is "script" or "style" or "noscript")
                    return;
                if (IsReaderFootnoteDefinition(element))
                    return;

                var chapterTitleElement = name is "h1" or "h2" or "h3" or "h4" or "h5" or "h6";
                var childChapterTitle = chapterTitle || chapterTitleElement;

                var metadata = string.Join(
                    ' ',
                    element.Attributes()
                        .Where(attribute => attribute.Name.LocalName is "class" or "id")
                        .Select(attribute => attribute.Value));
                var preservesLineBreaks = name is "pre" or "code" or "kbd" or "samp"
                    || System.Text.RegularExpressions.Regex.IsMatch(
                        metadata,
                        @"(?:poem|poetry|verse|诗|詩)",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase
                        | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
                var childReflowableParagraph = !preservesLineBreaks
                    && (reflowableParagraph
                        || name is "p" or "li" or "blockquote"
                        or "div" or "section" or "article" or "main");

                var matchedHere = false;
                if (hasFragment && !capture && ElementMatchesReaderFragment(element, fragment))
                {
                    capture = true;
                    foundFragment = true;
                    matchedHere = true;
                }

                if (capture
                    && hasStopFragment
                    && ElementMatchesReaderFragment(element, stopFragment))
                {
                    FlushText(isChapterTitle: chapterTitle);
                    stopCapture = true;
                    return;
                }

                if (capture && name == "a" && IsReaderFootnoteReference(element))
                {
                    // A heading in this book contains a footnote immediately
                    // after its last character. Preserve the heading semantic
                    // when the text is flushed before the footnote record.
                    FlushText(isChapterTitle: chapterTitle);
                    var href = ResolveReaderElementHref(path, element);
                    if (!string.IsNullOrWhiteSpace(href))
                    {
                        // An EPUB footnote reference often contains an image
                        // whose alt text is the complete footnote explanation.
                        // That alt text belongs in the popup, never in the
                        // inline marker shown in the reading surface.
                        var label = GetReaderFootnoteMarkerLabel(element);
                        blocks.Add(new ReaderLinuxTextFallbackRawBlock(null, null, href, label));
                        return;
                    }
                }

                if (capture && name is "img" or "image" or "picture")
                {
                    FlushText();
                    var imagePath = EpubReaderImageReferenceNormalizer.ResolveFirstLocalImagePath(element, path);
                    if (!string.IsNullOrWhiteSpace(imagePath))
                        blocks.Add(new ReaderLinuxTextFallbackRawBlock(null, imagePath));
                    return;
                }

                foreach (var child in element.Nodes())
                {
                    if (stopCapture) break;
                    Visit(child, childReflowableParagraph, childChapterTitle);
                }
                if (stopCapture) return;

                if (capture && name is "br")
                {
                    if (reflowableParagraph && !preservesLineBreaks)
                        AppendReaderPlainText(builder, " ");
                    else
                        AppendReaderPlainTextLineBreak(builder);
                }
                else if (capture && IsReaderPlainTextBlock(name))
                {
                    AppendReaderPlainTextBreak(builder);
                    var hasText = builder
                        .ToString()
                        .Any(character => !char.IsWhiteSpace(character));
                    FlushText(
                        childChapterTitle,
                        isParagraphBoundary: true);
                    // A footnote reference can be the last node in a
                    // paragraph. Its text was flushed before the reference
                    // was emitted, so retain the paragraph boundary even
                    // though there is no text in the builder now.
                    if (!hasText)
                    {
                        blocks.Add(new ReaderLinuxTextFallbackRawBlock(
                            null,
                            null,
                            IsParagraphBoundary: true));
                    }
                }

                if (matchedHere)
                {
                    capture = true;
                }
            }

            foreach (var node in body.Nodes())
            {
                if (stopCapture) break;
                Visit(node);
            }
            FlushText();

            if (hasFragment && !foundFragment)
                return CreateFallbackContentFromTextAndImages(path, text, startTitle);

            var normalizedStartTitle = NormalizeReaderPlainTextLine(startTitle);
            var firstTextIndex = blocks.FindIndex(block => !string.IsNullOrWhiteSpace(block.Text));
            if (firstTextIndex < 0)
            {
                if (!string.IsNullOrWhiteSpace(normalizedStartTitle))
                    blocks.Insert(0, new ReaderLinuxTextFallbackRawBlock(
                        normalizedStartTitle,
                        null,
                        IsChapterTitle: true,
                        IsParagraphBoundary: true));
            }
            else if (!string.IsNullOrWhiteSpace(normalizedStartTitle))
            {
                var firstBlock = blocks[firstTextIndex];
                var normalizedFirstText = NormalizeReaderPlainText(firstBlock.Text ?? string.Empty);
                var firstLine = NormalizeReaderPlainTextLine(
                    normalizedFirstText
                        .Replace("\r\n", "\n", StringComparison.Ordinal)
                        .Replace('\r', '\n')
                        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        .FirstOrDefault());
                if (string.Equals(firstLine, normalizedStartTitle, StringComparison.OrdinalIgnoreCase))
                {
                    // A few EPUBs put the chapter name in a div/p instead of
                    // h1-h6. Promote the matching first block so both render
                    // paths apply the same centered/bold title treatment.
                    var firstLineBreak = normalizedFirstText.IndexOf('\n');
                    if (firstLineBreak < 0)
                    {
                        blocks[firstTextIndex] = firstBlock with { IsChapterTitle = true };
                    }
                    else
                    {
                        var bodyText = normalizedFirstText[(firstLineBreak + 1)..].Trim();
                        blocks[firstTextIndex] = firstBlock with
                        {
                            Text = normalizedStartTitle,
                            IsChapterTitle = true,
                            IsParagraphBoundary = true
                        };
                        if (!string.IsNullOrWhiteSpace(bodyText))
                            blocks.Insert(
                                firstTextIndex + 1,
                                firstBlock with
                                {
                                    Text = bodyText,
                                    IsChapterTitle = false
                                });
                    }
                }
                else if (firstLine.StartsWith(
                             normalizedStartTitle,
                             StringComparison.OrdinalIgnoreCase))
                {
                    // Keep a title prefix out of the body block when a
                    // publisher placed both on one line in a generic div.
                    var bodyText = normalizedFirstText[normalizedStartTitle.Length..].TrimStart();
                    blocks[firstTextIndex] = firstBlock with { Text = bodyText };
                    blocks.Insert(
                        firstTextIndex,
                        new ReaderLinuxTextFallbackRawBlock(
                            normalizedStartTitle,
                            null,
                            IsChapterTitle: true,
                            IsParagraphBoundary: true));
                }
                else
                {
                    blocks.Insert(0, new ReaderLinuxTextFallbackRawBlock(
                        normalizedStartTitle,
                        null,
                        IsChapterTitle: true,
                        IsParagraphBoundary: true));
                }
            }

            return new ReaderLinuxTextFallbackExtractedContent(text, blocks);
        }
        catch
        {
            return CreateFallbackContentFromTextAndImages(path, text, startTitle);
        }
    }

    private static ReaderLinuxTextFallbackExtractedContent CreateFallbackContentFromTextAndImages(
        string path,
        string text,
        string? startTitle = null)
    {
        var blocks = new List<ReaderLinuxTextFallbackRawBlock>();
        var normalizedText = NormalizeReaderPlainText(text);
        var normalizedTitle = NormalizeReaderPlainTextLine(startTitle);
        if (!string.IsNullOrWhiteSpace(normalizedText))
        {
            var firstLine = NormalizeReaderPlainTextLine(
                normalizedText
                    .Replace("\r\n", "\n", StringComparison.Ordinal)
                    .Replace('\r', '\n')
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault());
            if (!string.IsNullOrWhiteSpace(normalizedTitle)
                && string.Equals(firstLine, normalizedTitle, StringComparison.OrdinalIgnoreCase))
            {
                blocks.Add(new ReaderLinuxTextFallbackRawBlock(
                    normalizedTitle,
                    null,
                    IsChapterTitle: true,
                    IsParagraphBoundary: true));
                var firstLineBreak = normalizedText.IndexOf('\n');
                var bodyText = firstLineBreak >= 0
                    ? normalizedText[(firstLineBreak + 1)..].Trim()
                    : string.Empty;
                if (!string.IsNullOrWhiteSpace(bodyText))
                    blocks.Add(new ReaderLinuxTextFallbackRawBlock(bodyText, null));
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(normalizedTitle))
                    blocks.Add(new ReaderLinuxTextFallbackRawBlock(
                        normalizedTitle,
                        null,
                        IsChapterTitle: true,
                        IsParagraphBoundary: true));
                blocks.Add(new ReaderLinuxTextFallbackRawBlock(normalizedText, null));
            }
        }
        foreach (var imagePath in EpubReaderImageReferenceNormalizer.ExtractLocalImagePaths(path))
            blocks.Add(new ReaderLinuxTextFallbackRawBlock(null, imagePath));
        return new ReaderLinuxTextFallbackExtractedContent(text, blocks);
    }

    private static bool IsReaderFootnoteReference(XElement element)
    {
        if (IsReaderFootnoteBacklink(element))
            return false;

        var metadata = string.Join(
            ' ',
            element.Attributes()
                .Where(attribute => attribute.Name.LocalName is "type" or "role" or "rel" or "class" or "id" or "href")
                .Select(attribute => attribute.Value));
        return System.Text.RegularExpressions.Regex.IsMatch(
            metadata,
            @"\b(noteref|doc-noteref|footnote|endnote|note[-_]?ref|fn[-_]?ref)\b|(?:^|[#\s_-])(?:notes?|fn|ftn|footnotes?|zww?)[-_:]?\d*(?:n|ref)?(?:$|[\s#_-])",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
            | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    }

    private static bool IsReaderFootnoteDefinition(XElement element)
    {
        var type = string.Join(
            ' ',
            element.Attributes()
                .Where(attribute => attribute.Name.LocalName is "type" or "role")
                .Select(attribute => attribute.Value));
        var identity = string.Join(
            ' ',
            element.Attributes()
                .Where(attribute => attribute.Name.LocalName is "class" or "id")
                .Select(attribute => attribute.Value));
        return IsReaderFootnoteDefinition(element.Name.LocalName, type, identity);
    }

    private static bool IsReaderFootnoteDefinition(XmlReader reader, string localName)
    {
        var type = string.Join(
            ' ',
            new[]
            {
                reader.GetAttribute("type"),
                reader.GetAttribute("type", "http://www.idpf.org/2007/ops"),
                reader.GetAttribute("role")
            }.Where(value => !string.IsNullOrWhiteSpace(value)));
        var identity = string.Join(
            ' ',
            new[] { reader.GetAttribute("class"), reader.GetAttribute("id") }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
        return IsReaderFootnoteDefinition(localName, type, identity);
    }

    private static bool IsReaderFootnoteDefinition(
        string localName,
        string type,
        string identity)
    {
        if (System.Text.RegularExpressions.Regex.IsMatch(
                type,
                @"\b(?:doc-)?(?:footnote|endnote)\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
                | System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            return true;

        var isDefinitionContainer = localName.Equals("aside", StringComparison.OrdinalIgnoreCase)
            || localName.Equals("section", StringComparison.OrdinalIgnoreCase)
            || localName.Equals("div", StringComparison.OrdinalIgnoreCase)
            || localName.Equals("ol", StringComparison.OrdinalIgnoreCase)
            || localName.Equals("ul", StringComparison.OrdinalIgnoreCase)
            || localName.Equals("li", StringComparison.OrdinalIgnoreCase)
            || localName.Equals("p", StringComparison.OrdinalIgnoreCase);
        return isDefinitionContainer
            && System.Text.RegularExpressions.Regex.IsMatch(
                identity,
                @"(?:^|[\s_-])(?:duokan-)?(?:footnote|endnote)(?:[\s_-]|$)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
                | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    }

    private static string GetReaderFootnoteMarkerLabel(XElement element)
    {
        var label = NormalizeReaderPlainTextLine(element.Value);
        // Keep genuine short markers such as [1], 注1, or †. Long text is
        // almost always an image alt/title containing the note body itself.
        return !string.IsNullOrWhiteSpace(label) && label.Length <= 8
            ? label
            : UiText.Get("注");
    }

    private static bool IsReaderFootnoteBacklink(XElement element)
    {
        var id = element.Attributes().FirstOrDefault(attribute =>
            attribute.Name.LocalName.Equals("id", StringComparison.OrdinalIgnoreCase))?.Value?.Trim();
        var href = element.Attributes().FirstOrDefault(attribute =>
            attribute.Name.LocalName.Equals("href", StringComparison.OrdinalIgnoreCase))?.Value?.Trim();
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(href))
            return false;

        var hash = href.IndexOf('#');
        if (hash < 0 || hash + 1 >= href.Length)
            return false;

        var fragment = href[(hash + 1)..];
        var query = fragment.IndexOfAny(['?', '#']);
        if (query >= 0) fragment = fragment[..query];
        return id.EndsWith("n", StringComparison.OrdinalIgnoreCase)
            && !fragment.EndsWith("n", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ResolveReaderElementHref(string sourcePath, XElement element)
    {
        var href = element.Attributes().FirstOrDefault(attribute =>
            attribute.Name.LocalName.Equals("href", StringComparison.OrdinalIgnoreCase))?.Value;
        if (string.IsNullOrWhiteSpace(href)) return null;
        try
        {
            var sourceUri = new Uri(Path.GetFullPath(sourcePath), UriKind.Absolute);
            var trimmed = href.Trim();
            var hash = trimmed.IndexOf('#');
            var pathPart = hash >= 0 ? trimmed[..hash] : trimmed;
            if (!Uri.TryCreate(sourceUri, pathPart, out var uri) || !uri.IsFile)
                return null;

            if (hash >= 0)
            {
                var fragment = Uri.UnescapeDataString(trimmed[(hash + 1)..]);
                var builder = new UriBuilder(uri) { Fragment = fragment };
                uri = builder.Uri;
            }
            return uri.AbsoluteUri;
        }
        catch
        {
            return null;
        }
    }

    private static bool ElementMatchesReaderFragment(XElement element, string? fragment)
    {
        if (string.IsNullOrWhiteSpace(fragment)) return false;
        foreach (var attribute in element.Attributes())
        {
            var name = attribute.Name.LocalName;
            if ((name.Equals("id", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("name", StringComparison.OrdinalIgnoreCase))
                && string.Equals(attribute.Value, fragment, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private static string ExtractReaderPlainText(
        string path,
        string? startFragment = null,
        string? startTitle = null,
        string? endFragment = null)
    {
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true
            };
            using var reader = XmlReader.Create(path, settings);
            var builder = new StringBuilder();
            var fallbackBuilder = new StringBuilder();
            var inBody = false;
            var reflowParagraphDepth = 0;
            var preserveBreakDepth = 0;
            var elementStates = new Stack<(bool Reflow, bool Preserve)>();
            var fragment = startFragment?.TrimStart('#');
            var hasFragment = !string.IsNullOrWhiteSpace(fragment);
            var stopFragment = endFragment?.TrimStart('#');
            var hasStopFragment = !string.IsNullOrWhiteSpace(stopFragment)
                && !string.Equals(stopFragment, fragment, StringComparison.Ordinal);
            var capture = !hasFragment;
            var foundFragment = false;
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    var name = reader.LocalName.ToLowerInvariant();
                    if (name == "body") inBody = true;
                    if (inBody && IsReaderFootnoteDefinition(reader, name))
                    {
                        if (!reader.IsEmptyElement)
                            reader.Skip();
                        continue;
                    }
                    var metadata = $"{reader.GetAttribute("class")} {reader.GetAttribute("id")}";
                    var preservesLineBreaks = name is "pre" or "code" or "kbd" or "samp"
                        || System.Text.RegularExpressions.Regex.IsMatch(
                            metadata,
                            @"(?:poem|poetry|verse|诗|詩)",
                            System.Text.RegularExpressions.RegexOptions.IgnoreCase
                            | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
                    var entersReflow = (name is "p" or "li" or "blockquote"
                        or "div" or "section" or "article" or "main")
                        && preserveBreakDepth == 0;
                    if (preservesLineBreaks)
                        preserveBreakDepth++;
                    if (entersReflow)
                        reflowParagraphDepth++;
                    if (inBody
                        && hasFragment
                        && !capture
                        && ElementMatchesReaderFragment(reader, fragment))
                    {
                        capture = true;
                        foundFragment = true;
                    }
                    if (inBody
                        && capture
                        && hasStopFragment
                        && ElementMatchesReaderFragment(reader, stopFragment))
                    {
                        break;
                    }
                    if (inBody && name == "br")
                    {
                        if (reflowParagraphDepth > 0 && preserveBreakDepth == 0)
                            AppendReaderPlainText(fallbackBuilder, " ");
                        else
                            AppendReaderPlainTextLineBreak(fallbackBuilder);
                        if (capture)
                        {
                            if (reflowParagraphDepth > 0 && preserveBreakDepth == 0)
                                AppendReaderPlainText(builder, " ");
                            else
                                AppendReaderPlainTextLineBreak(builder);
                        }
                    }

                    if (reader.IsEmptyElement)
                    {
                        if (entersReflow)
                            reflowParagraphDepth = Math.Max(0, reflowParagraphDepth - 1);
                        if (preservesLineBreaks)
                            preserveBreakDepth = Math.Max(0, preserveBreakDepth - 1);
                    }
                    else
                    {
                        elementStates.Push((entersReflow, preservesLineBreaks));
                    }
                }
                else if (reader.NodeType == XmlNodeType.EndElement)
                {
                    var name = reader.LocalName.ToLowerInvariant();
                    if (inBody && IsReaderPlainTextBlock(name))
                    {
                        AppendReaderPlainTextBreak(fallbackBuilder);
                        if (capture)
                        AppendReaderPlainTextBreak(builder);
                    }
                    if (elementStates.Count > 0)
                    {
                        var state = elementStates.Pop();
                        if (state.Reflow)
                            reflowParagraphDepth = Math.Max(0, reflowParagraphDepth - 1);
                        if (state.Preserve)
                            preserveBreakDepth = Math.Max(0, preserveBreakDepth - 1);
                    }
                    if (name == "body") inBody = false;
                }
                else if (inBody && (reader.NodeType == XmlNodeType.Text || reader.NodeType == XmlNodeType.CDATA))
                {
                    AppendReaderPlainText(fallbackBuilder, reader.Value);
                    if (capture)
                        AppendReaderPlainText(builder, reader.Value);
                }
            }

            var raw = hasFragment && foundFragment
                ? builder.ToString()
                : fallbackBuilder.ToString();
            return EnsureReaderPlainTextStartsWithTitle(
                TrimReaderPlainTextToTitle(NormalizeReaderPlainText(raw), startTitle),
                startTitle);
        }
        catch (XmlException)
        {
            var html = File.ReadAllText(path);
            var withoutTags = System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", " ");
            return EnsureReaderPlainTextStartsWithTitle(
                TrimReaderPlainTextToTitle(
                    NormalizeReaderPlainText(WebUtility.HtmlDecode(withoutTags)),
                    startTitle),
                startTitle);
        }
    }

    private static bool ElementMatchesReaderFragment(XmlReader reader, string? fragment)
    {
        if (string.IsNullOrWhiteSpace(fragment)) return false;
        foreach (var attribute in new[] { "id", "name" })
        {
            var value = reader.GetAttribute(attribute);
            if (string.IsNullOrWhiteSpace(value)) continue;
            if (string.Equals(value, fragment, StringComparison.Ordinal)
                || string.Equals(WebUtility.UrlDecode(value), fragment, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsReaderPlainTextBlock(string name)
        => name is "p" or "div" or "section" or "article" or "main"
            or "h1" or "h2" or "h3" or "h4" or "h5" or "h6"
            or "li" or "blockquote";

    private static void AppendReaderPlainText(StringBuilder builder, string text)
    {
        text = WebUtility.HtmlDecode(text);
        foreach (var ch in text)
        {
            builder.Append(char.IsWhiteSpace(ch) ? ' ' : ch);
        }
    }

    private static void AppendReaderPlainTextBreak(StringBuilder builder)
    {
        if (builder.Length == 0) return;
        if (builder[^1] != '\n') builder.AppendLine();
        if (builder.Length >= 2 && builder[^2] != '\n') builder.AppendLine();
    }

    private static void AppendReaderPlainTextLineBreak(StringBuilder builder)
    {
        if (builder.Length == 0) return;
        if (builder[^1] != '\n') builder.AppendLine();
    }

    private static string NormalizeReaderPlainText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var normalized = string.Join(
            "\n",
            text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Select(line => System.Text.RegularExpressions.Regex.Replace(line, @"[ \t\f\v]+", " ").Trim()));
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"\n{3,}", "\n\n").Trim();
        return normalized.Replace("\n", Environment.NewLine);
    }

    private static string TrimReaderPlainTextToTitle(string text, string? title)
    {
        title = NormalizeReaderPlainTextLine(title);
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(title))
            return text;

        var lines = text.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = NormalizeReaderPlainTextLine(lines[index]);
            if (string.Equals(line, title, StringComparison.OrdinalIgnoreCase)
                || line.Contains(title, StringComparison.OrdinalIgnoreCase))
            {
                return string.Join('\n', lines.Skip(index)).Trim();
            }
        }

        var directIndex = text.IndexOf(title, StringComparison.OrdinalIgnoreCase);
        return directIndex >= 0 ? text[directIndex..].TrimStart() : text;
    }

    private static string EnsureReaderPlainTextStartsWithTitle(string text, string? title)
    {
        title = NormalizeReaderPlainTextLine(title);
        if (string.IsNullOrWhiteSpace(title)) return text;
        if (string.IsNullOrWhiteSpace(text)) return title;

        var firstLine = NormalizeReaderPlainTextLine(
            text.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault());
        if (string.Equals(firstLine, title, StringComparison.OrdinalIgnoreCase)
            || firstLine.Contains(title, StringComparison.OrdinalIgnoreCase))
        {
            return text.TrimStart();
        }

        return title + Environment.NewLine + Environment.NewLine + text.TrimStart();
    }

    private static string NormalizeReaderPlainTextLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        return System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
    }

    private async Task<ReaderScrollState?> CaptureReaderScrollStateAsync(IReaderHost host)
    {
        if (_readerIsPdf) return null;
        if (CaptureLinuxReaderTextFallbackState() is { } linuxState)
            return linuxState;
        if (host is NativeReaderHost nativeReader)
        {
            var nativeState = nativeReader.GetScrollState();
            return new ReaderScrollState(
                nativeState.Position,
                nativeState.Ratio,
                nativeState.ScrollWidth,
                nativeState.ScrollHeight,
                nativeState.ClientWidth,
                nativeState.ClientHeight);
        }
        try
        {
            var result = await host.InvokeScriptAsync(
                "(() => { const el = document.scrollingElement || document.documentElement; if (!el) return null; return JSON.stringify({ left: el.scrollLeft || 0, top: el.scrollTop || 0, scrollWidth: el.scrollWidth || 0, scrollHeight: el.scrollHeight || 0, clientWidth: el.clientWidth || 0, clientHeight: el.clientHeight || 0 }); })();");
            var raw = DecodeReaderScriptString(result);
            if (string.IsNullOrWhiteSpace(raw) || string.Equals(raw, "null", StringComparison.OrdinalIgnoreCase))
                return null;

            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            var horizontal = _readerLayout.FlowMode == 1 || _readerLayout.VerticalWriting;
            var position = horizontal ? ReadDouble(root, "left") : ReadDouble(root, "top");
            if (_readerLayout.VerticalWriting)
                position = Math.Abs(position);
            var scrollWidth = ReadDouble(root, "scrollWidth");
            var scrollHeight = ReadDouble(root, "scrollHeight");
            var clientWidth = ReadDouble(root, "clientWidth");
            var clientHeight = ReadDouble(root, "clientHeight");
            var maximum = horizontal
                ? Math.Max(0, scrollWidth - clientWidth)
                : Math.Max(0, scrollHeight - clientHeight);
            var ratio = maximum > 0 ? Math.Clamp(position / maximum, 0, 1) : 0;
            return new ReaderScrollState(
                Math.Max(0, position),
                ratio,
                scrollWidth,
                scrollHeight,
                clientWidth,
                clientHeight);
        }
        catch
        {
            return null;
        }
    }

    private async Task UpdateReaderScrollStateAsync(IReaderHost host)
    {
        if (TryApplyLinuxReaderTextFallbackState()) return;
        if (host is NativeReaderHost nativeReader)
        {
            var nativeState = nativeReader.GetScrollState();
            ApplyReaderScrollState(new ReaderScrollState(
                nativeState.Position,
                nativeState.Ratio,
                nativeState.ScrollWidth,
                nativeState.ScrollHeight,
                nativeState.ClientWidth,
                nativeState.ClientHeight));
            return;
        }

        var state = await CaptureReaderScrollStateAsync(host);
        if (state is null) return;
        ApplyReaderScrollState(state);
    }

    private async Task RestoreReaderScrollStateAsync(
        IReaderHost host,
        ReaderScrollState state,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (host is NativeReaderHost nativeReader)
        {
            if (nativeReader.Vertical)
                nativeReader.ScrollToOffset((int)Math.Max(0, Math.Round(state.Position)));
            else
                nativeReader.SeekToPixelScroll(state.Position);
            await UpdateReaderScrollStateAsync(host);
        }
    }

    private async Task ApplyReaderLayoutToHostsAsync(CancellationToken cancellationToken)
    {
        await _readerLayoutGate.WaitAsync(cancellationToken);
        try
        {
            ResetReaderContinuousEdgeTracking();
            var currentHost = CurrentReaderHost;
            var scrollState = currentHost is not null
                ? await CaptureReaderScrollStateAsync(currentHost)
                : null;
            var hosts = new[] { _readerActiveHost, _readerPreloadHost }
                .Where(host => host is not null)
                .Cast<IReaderHost>()
                .Distinct()
                .ToArray();
            await Task.WhenAll(hosts.Select(host => ConfigureReaderHostAsync(host, cancellationToken)));
            if (IsLinuxReaderTextFallbackActive())
            {
                // The visible Linux vertical surface is Avalonia-owned, so
                // changing the CSS on the hidden WebView is not enough. Reuse
                // the current pagination anchor and rebuild the custom page
                // with the new indent policy immediately.
                var fallbackAnchor = _readerLayout.FlowMode == 1
                    ? CaptureLinuxReaderTextFallbackPageAnchor()
                    : null;
                if (_readerLayout.FlowMode == 1)
                    RebuildLinuxReaderTextFallbackPages(fallbackAnchor);
                else
                    UpdateLinuxReaderTextFallbackMode();
            }
            // NativeReaderHost preserves a semantic text/page anchor while it
            // recomposes. Its position is a character offset in vertical page
            // mode but a pixel offset in horizontal mode; replaying the old
            // numeric value here would reinterpret one coordinate system as
            // the other and reopen the wrong page after a layout switch.
            if (scrollState is not null
                && currentHost is not null
                && currentHost is not NativeReaderHost
                && ReferenceEquals(CurrentReaderHost, currentHost))
            {
                await RestoreReaderScrollStateAsync(currentHost, scrollState, cancellationToken);
            }
            if (currentHost is not null && ReferenceEquals(CurrentReaderHost, currentHost))
            {
                await UpdateReaderScrollStateAsync(currentHost);
                PrimeReaderContinuousEdgeTracking();
                ScheduleReaderBookPageCountRefresh();
            }
            if (!_readerIsPdf
                && currentHost is not null
                && ReferenceEquals(CurrentReaderHost, currentHost)
                && ReaderInPageSearchBar.IsVisible
                && !string.IsNullOrWhiteSpace(ReaderInPageSearchBox.Text))
            {
                var previousSearchIndex = _readerSearchIndex;
                var searchSequence = ++_readerSearchSequence;
                await ApplyReaderSearchAsync(
                    ReaderInPageSearchBox.Text.Trim(),
                    searchSequence,
                    navigate: false);
                if (_readerSearchCount > 0)
                {
                    _readerSearchIndex = Math.Clamp(previousSearchIndex, 0, _readerSearchCount - 1);
                    await NavigateReaderSearchAsync(_readerSearchIndex, searchSequence);
                }
            }
            UpdateReaderToolbar();
        }
        finally
        {
            _readerLayoutGate.Release();
        }
    }

    private void ReaderWebViewHost_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (IsLinuxReaderTextFallbackActive())
        {
            ScheduleLinuxReaderTextFallbackReflow();
            return;
        }

        ScheduleReaderRelayout();
    }

    private void ScheduleLinuxReaderTextFallbackReflow()
    {
        if (!IsLinuxReaderTextFallbackActive()) return;
        if (_readerLayout.FlowMode == 1
            && _readerLinuxTextFallbackPendingReflowAnchor is null)
        {
            _readerLinuxTextFallbackPendingReflowAnchor =
                CaptureLinuxReaderTextFallbackPageAnchor();
        }

        var sequence = ++_readerLinuxTextFallbackReflowSequence;
        _ = ObserveReaderTaskAsync(RunLinuxReaderTextFallbackReflowAsync(sequence));
    }

    private async Task RunLinuxReaderTextFallbackReflowAsync(int sequence)
    {
        // Column visibility changes can raise SizeChanged before Avalonia has
        // committed the new reader bounds. Waiting through that layout burst
        // prevents a 932px page from being centered inside a 572px slot and
        // painting underneath the TOC.
        await Task.Delay(80);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (sequence != _readerLinuxTextFallbackReflowSequence
                || !IsLinuxReaderTextFallbackActive())
            {
                return;
            }

            UpdateLinuxReaderTextFallbackImageSizes();
            // Selection must remain paint-only; never change wrap points
            // while the pointer is dragging a selection boundary.
            var selectionInProgress = _readerLinuxTextFallbackPointerPressed
                || !string.IsNullOrWhiteSpace(_readerPendingSelection);
            if (selectionInProgress)
            {
                _readerLinuxTextFallbackPendingReflowAnchor = null;
                return;
            }

            UpdateLinuxReaderTextFallbackBlockWidths();
            if (_readerLayout.FlowMode == 1)
            {
                var anchor = _readerLinuxTextFallbackPendingReflowAnchor;
                _readerLinuxTextFallbackPendingReflowAnchor = null;
                RebuildLinuxReaderTextFallbackPages(anchor);
            }
            SyncLinuxReaderTextFallbackState(saveProgress: false);
        });
    }

    // A native webview can trail Avalonia's Grid by several compositor frames
    // while a TOC/assistant column or zen mode changes size. Reflow only after
    // Chromium reports the same viewport as its host, then restore the reading
    // position captured before the resize reports can overwrite it.
    private void ScheduleReaderRelayout()
    {
        if (_readerIsPdf
            || !ReaderRoot.IsVisible
            || _readerNavigationCancellation is not null
            || CurrentReaderHost is not { } currentHost)
            return;

        if (!ReferenceEquals(_readerPendingRelayoutHost, currentHost))
        {
            _readerPendingRelayoutHost = currentHost;
            _readerPendingRelayoutState = CreateTrackedReaderScrollState();
        }
        else if (_readerPendingRelayoutState is null)
        {
            _readerPendingRelayoutState = CreateTrackedReaderScrollState();
        }

        _readerRelayoutCancellation?.Cancel();
        _readerRelayoutCancellation?.Dispose();
        _readerRelayoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _readerSessionCancellation?.Token ?? CancellationToken.None);
        var cancellation = _readerRelayoutCancellation;
        var token = cancellation.Token;
        _ = ObserveReaderTaskAsync(RunScheduledReaderRelayoutAsync(cancellation, token));
    }

    private ReaderScrollState? CreateTrackedReaderScrollState()
    {
        if (_readerClientWidth <= 0 || _readerClientHeight <= 0) return null;
        return new ReaderScrollState(
            Math.Max(0, _readerScrollPosition),
            Math.Clamp(_readerScrollRatio, 0, 1),
            Math.Max(0, _readerScrollWidth),
            Math.Max(0, _readerScrollHeight),
            _readerClientWidth,
            _readerClientHeight);
    }

    private async Task RunScheduledReaderRelayoutAsync(
        CancellationTokenSource cancellation,
        CancellationToken token)
    {
        try
        {
            await Task.Delay(120, token);
            var host = _readerPendingRelayoutHost;
            var state = _readerPendingRelayoutState;
            if (host is null || !ReferenceEquals(CurrentReaderHost, host)) return;

            var converged = await WaitForReaderViewportToMatchHostAsync(host, token);
            token.ThrowIfCancellationRequested();
            await ApplyReaderViewportToHostsAsync(token, host, state);

            // Some WebView2 builds settle after the bounded first wait. A
            // second pass is required only when the native viewport lagged.
            if (!converged)
            {
                await Task.Delay(200, token);
                if (await WaitForReaderViewportToMatchHostAsync(host, token))
                {
                    token.ThrowIfCancellationRequested();
                    await ApplyReaderViewportToHostsAsync(token, host, state);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReferenceEquals(_readerRelayoutCancellation, cancellation))
            {
                _readerPendingRelayoutHost = null;
                _readerPendingRelayoutState = null;
            }
        }
    }

    private async Task ApplyReaderViewportToHostsAsync(
        CancellationToken cancellationToken,
        IReaderHost capturedHost,
        ReaderScrollState? capturedState)
    {
        // The self-drawn surface recomposes on resize by itself; the PDF
        // webview viewer scales its own page.
        await Task.CompletedTask;
    }

    private async Task<bool> WaitForReaderViewportToMatchHostAsync(
        IReaderHost host,
        CancellationToken token)
    {
        if (host is NativeReaderHost)
        {
            // The self-drawn surface sizes from Avalonia bounds directly; the
            // relayout timer handles any late resize.
            return true;
        }

        const int maximumAttempts = 10;
        const double tolerance = 2;
        for (var attempt = 0; attempt < maximumAttempts; attempt++)
        {
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(CurrentReaderHost, host)) return false;
            var expectedWidth = ReaderWebViewHost.Bounds.Width;
            var expectedHeight = ReaderWebViewHost.Bounds.Height;
            if (expectedWidth <= 0 || expectedHeight <= 0) return false;

            try
            {
                var result = await host.InvokeScriptAsync(
                    "(() => JSON.stringify({ width: window.innerWidth || document.documentElement.clientWidth || 0, height: window.innerHeight || document.documentElement.clientHeight || 0 }))();");
                var raw = DecodeReaderScriptString(result);
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    using var document = JsonDocument.Parse(raw);
                    var root = document.RootElement;
                    var viewportWidth = ReadDouble(root, "width");
                    var viewportHeight = ReadDouble(root, "height");
                    if (Math.Abs(viewportWidth - expectedWidth) <= tolerance
                        && Math.Abs(viewportHeight - expectedHeight) <= tolerance)
                    {
                        return true;
                    }
                }
            }
            catch
            {
                // Navigation can briefly make the document unavailable.
            }

            await Task.Delay(40, token);
        }

        return false;
    }

    private async Task WriteReaderLayoutDiagnosticsAsync(string stage, IReaderHost host)
    {
#if DEBUG
        if (_readerIsPdf) return;
        try
        {
            var result = await host.InvokeScriptAsync(
                $$"""
                (() => {
                  const root = document.documentElement;
                  const body = document.body;
                  const el = document.scrollingElement || root;
                  if (!root || !body || !el) return null;
                  const rootStyle = getComputedStyle(root);
                  const bodyStyle = getComputedStyle(body);
                  const textNode = Array.from(body.querySelectorAll('h1,h2,h3,h4,h5,h6,p,li,blockquote,td,th,div,span'))
                    .find(node => (node.textContent || '').trim().length > 0);
                  const textStyle = textNode ? getComputedStyle(textNode) : null;
                  const textRect = textNode ? textNode.getBoundingClientRect() : null;
                  const fontProbe = document.createElement('span');
                  fontProbe.textContent = '祖母的退化论有人挠';
                  fontProbe.style.cssText = 'position: fixed; left: -10000px; top: -10000px; white-space: nowrap; font-size: 48px; font-weight: 400; letter-spacing: 0;';
                  body.appendChild(fontProbe);
                  const measureFont = family => {
                    fontProbe.style.setProperty('font-family', family, 'important');
                    return fontProbe.getBoundingClientRect().width || 0;
                  };
                  const fontProbeWidths = {
                    bundled: measureFont('"KkindleKingHwaOldSong"'),
                    notoSans: measureFont('"Noto Sans CJK SC"'),
                    notoSerif: measureFont('"Noto Serif CJK SC"'),
                    sans: measureFont('sans-serif')
                  };
                  fontProbe.remove();
                  const vertical = bodyStyle.writingMode === 'vertical-rl';
                  let verticalGeometry = null;
                  if (vertical) {
                    const viewport = el.clientWidth || root.clientWidth || window.innerWidth || 0;
                    const verticalPageStep = parseFloat(
                      rootStyle.getPropertyValue('--kkindle-vertical-page-step')) || 0;
                    const originShift = parseFloat(
                      rootStyle.getPropertyValue('--kkindle-vertical-origin-shift')) || 0;
                    const trailingExtent = parseFloat(
                      rootStyle.getPropertyValue('--kkindle-vertical-trailing-extent')) || 0;
                    const leftSide = Math.max(0, (parseFloat(bodyStyle.paddingLeft) || 0) - trailingExtent);
                    const rightSide = parseFloat(bodyStyle.paddingRight) || 0;
                    const baseSide = (leftSide + rightSide) / 2;
                    const safeLeft = viewport - verticalPageStep - baseSide + originShift;
                    const safeRight = viewport - baseSide + originShift;
                    const tolerance = 0.75;
                    const partialGlyphs = [];
                    let glyphCount = 0;
                    let inspectedCharacters = 0;
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
                    verticalGeometry = {
                      viewport,
                      pageStep: verticalPageStep,
                      originShift,
                      safeLeft,
                      safeRight,
                      leftMargin: safeLeft,
                      rightMargin: viewport - safeRight,
                      marginDelta: Math.abs(safeLeft - (viewport - safeRight)),
                      lineHeight: parseFloat(bodyStyle.lineHeight) || 0,
                      glyphCount,
                      inspectedCharacters,
                      partialGlyphCount: partialGlyphs.length,
                      partialGlyphs: partialGlyphs.slice(0, 12)
                    };
                  }
                  return JSON.stringify({
                    innerWidth: window.innerWidth || 0,
                    visualWidth: window.visualViewport?.width || 0,
                    rootFontSize: rootStyle.fontSize,
                    bodyFontSize: bodyStyle.fontSize,
                    blockFontSize: textStyle ? textStyle.fontSize : '',
                    blockLineHeight: textStyle ? textStyle.lineHeight : '',
                    blockTag: textNode ? (textNode.closest('p,div,li,blockquote,td,th')?.tagName || textNode.tagName) : '',
                    paragraphCount: body.querySelectorAll('p').length,
                    firstParagraphFontSize: (() => {
                      const p = body.querySelector('p');
                      return p ? getComputedStyle(p).fontSize + '/' + getComputedStyle(p).lineHeight : '';
                    })(),
                    compatCellCount: body.querySelectorAll(
                      '.kkindle-linux-vertical-single, .kkindle-linux-vertical-tcy, '
                        + '.kkindle-linux-vertical-number, .kkindle-linux-vertical-cjk, '
                        + '.kkindle-linux-vertical-pair-punctuation, .kkindle-linux-vertical-single-punctuation, '
                        + '.kkindle-linux-vertical-footnote').length,
                    firstCellBox: (() => {
                      const cell = body.querySelector(
                        '.kkindle-linux-vertical-single, .kkindle-linux-vertical-pair-punctuation, '
                          + '.kkindle-linux-vertical-cjk');
                      if (!cell) return null;
                      const style = getComputedStyle(cell);
                      const rect = cell.getBoundingClientRect();
                      return style.writingMode + ' ' + style.display + ' w' + rect.width.toFixed(1)
                        + ' h' + rect.height.toFixed(1) + ' lh' + style.lineHeight;
                    })(),
                    rootClientWidth: root.clientWidth || 0,
                    clientWidth: el.clientWidth || 0,
                    clientHeight: el.clientHeight || 0,
                    scrollWidth: el.scrollWidth || 0,
                    scrollHeight: el.scrollHeight || 0,
                    scrollLeft: el.scrollLeft || 0,
                    scrollTop: el.scrollTop || 0,
                    pageStep: el.clientWidth || root.clientWidth || 0,
                    vertical,
                    verticalGeometry,
                    columnCount: bodyStyle.columnCount,
                    columnWidth: bodyStyle.columnWidth,
                    columnGap: bodyStyle.columnGap,
                    paddingLeft: bodyStyle.paddingLeft,
                    paddingRight: bodyStyle.paddingRight,
                    bodyTextLength: (body.innerText || body.textContent || '').trim().length,
                    bodyDisplay: bodyStyle.display,
                    bodyVisibility: bodyStyle.visibility,
                    bodyOpacity: bodyStyle.opacity,
                    bodyColor: bodyStyle.color,
                    bodyFontFamily: bodyStyle.fontFamily,
                    textFontFamily: textStyle?.fontFamily || '',
                    bundledFontLoaded: document.fonts?.check('1em "KkindleKingHwaOldSong"', '祖母的退化论有人挠') === true,
                    fontStatus: document.fonts?.status || '',
                    fontProbeWidths,
                    textTag: textNode?.tagName || '',
                    textClass: textNode?.className || '',
                    textSample: (textNode?.textContent || '').trim().slice(0, 80),
                    textDisplay: textStyle?.display || '',
                    textVisibility: textStyle?.visibility || '',
                    textOpacity: textStyle?.opacity || '',
                    textColor: textStyle?.color || '',
                    textFill: textStyle?.webkitTextFillColor || '',
                    textRect: textRect
                      ? {
                          x: textRect.x,
                          y: textRect.y,
                          width: textRect.width,
                          height: textRect.height
                        }
                      : null
                  });
                })();
                """);
            var raw = DecodeReaderScriptString(result);
            Directory.CreateDirectory(_paths.Logs);
            var entry = JsonSerializer.Serialize(new
            {
                timestamp = DateTimeOffset.Now,
                stage,
                chapter = _readerChapterIndex,
                flowMode = _readerLayout.FlowMode,
                twoPage = _readerLayout.TwoPageMode,
                hostWidth = ReaderWebViewHost.Bounds.Width,
                hostHeight = ReaderWebViewHost.Bounds.Height,
                renderScaling = TopLevel.GetTopLevel(ReaderWebViewHost)?.RenderScaling ?? 1d,
                dom = raw
            });
            await File.AppendAllTextAsync(
                Path.Combine(_paths.Logs, "reader-layout-debug.log"),
                entry + Environment.NewLine);
        }
        catch
        {
            // Diagnostics must never affect reading or navigation.
        }
#else
        await Task.CompletedTask;
#endif
    }

    private async Task<bool> NavigateToReaderItemAsync(
        EpubReaderNavigationItem item,
        CancellationToken cancellationToken,
        ReaderNavigationIntent intent = ReaderNavigationIntent.None,
        int? transitionDirection = null)
    {
        if (_readerDocument is null || CurrentReaderHost is null) return false;
        if (item.ChapterIndex < 0 || item.ChapterIndex >= _readerDocument.Chapters.Count) return false;
        if (!Uri.TryCreate(item.Target, UriKind.Absolute, out var target) || !target.IsFile) return false;
        if (!IsPathInside(_readerDocument.RootPath, target.LocalPath)) return false;
        PruneReaderPendingLocations(intent);
        HideReaderSelectionPopup();
        if (!ReaderNavigationLocationPolicy.UsesRestorePosition(intent))
            _readerRestoredProgress = null;
        ResetReaderContinuousEdgeTracking();
        var linuxFallbackStartsAtTarget = UseLinuxPlainTextRecoveryFallback
            && OperatingSystem.IsLinux()
            && !_readerIsPdf
            && intent is ReaderNavigationIntent.Toc or ReaderNavigationIntent.Progress;
        var linuxFallbackMovesToTargetEnd = linuxFallbackStartsAtTarget
            && _readerLinuxTextFallbackMoveToChapterEnd;
        _readerLinuxTextFallbackTargetTitle = linuxFallbackStartsAtTarget ? item.Title : null;
        if (linuxFallbackStartsAtTarget && !_readerLinuxTextFallbackMoveToChapterEnd)
            _readerLinuxTextFallbackEndFragment = null;

        var current = CurrentReaderHost;
        if (ReaderNavigationLocationPolicy.TargetsSameDocument(current.Source, target))
        {
            var sameDocumentSessionToken = _readerSessionCancellation?.Token ?? cancellationToken;
            _readerNavigationCancellation?.Cancel();
            var sameDocumentNavigationCancellation = CancellationTokenSource.CreateLinkedTokenSource(sameDocumentSessionToken);
            _readerNavigationCancellation = sameDocumentNavigationCancellation;
            var sameDocumentNavigationToken = sameDocumentNavigationCancellation.Token;
            try
            {
                var direction = transitionDirection
                    ?? (item.ChapterIndex < _readerChapterIndex ? -1 : 1);
                await RunReaderContentTransitionAsync(
                    current,
                    current,
                    direction,
                    async () =>
                    {
                        await ApplyReaderLocationAsync(
                            current,
                            target,
                            sameDocumentNavigationToken,
                            intent,
                            _readerRestoredProgress is not null);
                        _readerChapterIndex = item.ChapterIndex;
                        _readerCurrentFragment = GetReaderTargetFragment(target);
                        await UpdateReaderScrollStateAsync(current);
                        if (linuxFallbackStartsAtTarget)
                        {
                            _readerScrollPosition = linuxFallbackMovesToTargetEnd ? -1 : 0;
                            _readerScrollRatio = 0;
                            _readerLinuxTextFallbackPageIndex = linuxFallbackMovesToTargetEnd ? -1 : 0;
                        }
                        await UpdateLinuxReaderTextFallbackAsync(sameDocumentNavigationToken);
                        PositionLinuxReaderTextFallbackAtChapterEnd(
                            linuxFallbackMovesToTargetEnd);
                        return true;
                    },
                    sameDocumentNavigationToken,
                    animate: intent != ReaderNavigationIntent.None);
                PrimeReaderContinuousEdgeTracking();
                SetReaderTocSelection(item);
                ReaderChapterText.Text = GetReaderChapterPositionLabel();
                UpdateReaderToolbar();
                await UpdateReaderBookmarkIndicatorAsync();
                await SaveReaderProgressAsync(sameDocumentSessionToken);
                return true;
            }
            catch (OperationCanceledException) when (sameDocumentNavigationToken.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception exception)
            {
                ReaderStatusText.Text = T("定位失败：{0}", UiText.Localize(exception.Message));
                return false;
            }
            finally
            {
                if (ReferenceEquals(_readerNavigationCancellation, sameDocumentNavigationCancellation))
                    _readerNavigationCancellation = null;
                sameDocumentNavigationCancellation.Dispose();
            }
        }

        await ResetReaderInPageSearchForNavigationAsync();
        var sessionToken = _readerSessionCancellation?.Token ?? cancellationToken;
        _readerNavigationCancellation?.Cancel();
        var navigationCancellation = CancellationTokenSource.CreateLinkedTokenSource(sessionToken);
        _readerNavigationCancellation = navigationCancellation;
        var navigationToken = navigationCancellation.Token;
        var hiddenHost = HiddenReaderHost;
        var host = OperatingSystem.IsLinux()
            ? CurrentReaderHost
            : IsReaderHostReady(hiddenHost) ? hiddenHost! : CurrentReaderHost;
        var previousChapterIndex = _readerChapterIndex;
        var previousFragment = _readerCurrentFragment;
        try
        {
            ReaderStatusText.Text = string.Empty;
            if (OperatingSystem.IsLinux() && !_readerIsPdf)
            {
                _readerChapterIndex = item.ChapterIndex;
                _readerCurrentFragment = GetReaderTargetFragment(target);
                if (linuxFallbackStartsAtTarget)
                {
                    _readerScrollPosition = linuxFallbackMovesToTargetEnd ? -1 : 0;
                    _readerScrollRatio = 0;
                    _readerLinuxTextFallbackPageIndex = linuxFallbackMovesToTargetEnd ? -1 : 0;
                }
            }
            var holdOverlay = await TryShowReaderChapterHoldOverlayAsync(navigationToken);
            var loaded = await NavigateReaderHostAndWaitAsync(host, target, navigationToken);
            if (!loaded) throw new InvalidOperationException(T("章节加载失败。"));

            await ApplySavedAnnotationsAsync(host, navigationToken);
            var direction = transitionDirection
                ?? (item.ChapterIndex < previousChapterIndex ? -1 : 1);
            await RunReaderContentTransitionAsync(
                current,
                host,
                direction,
                async () =>
                {
                    _readerChapterIndex = item.ChapterIndex;
                    _readerScrollPosition = 0;
                    _readerScrollRatio = 0;
                    // host was picked as the hidden host, so the layer must flip
                    // unconditionally; deriving it from CurrentReaderHost would read
                    // the stale pre-swap flag and freeze the visible chapter after the
                    // first jump (TOC / next-chapter worked only once).
                    _readerShowingPreload = ReferenceEquals(host, _readerPreloadHost);
                    SetReaderHostLayer();
                    await ApplyReaderLocationAsync(
                        host,
                        target,
                        navigationToken,
                        intent,
                        _readerRestoredProgress is not null);
                    _readerCurrentFragment = GetReaderTargetFragment(target);
                    await UpdateReaderScrollStateAsync(host);
                    if (linuxFallbackStartsAtTarget)
                    {
                        _readerScrollPosition = linuxFallbackMovesToTargetEnd ? -1 : 0;
                        _readerScrollRatio = 0;
                        _readerLinuxTextFallbackPageIndex = linuxFallbackMovesToTargetEnd ? -1 : 0;
                    }
                    await UpdateLinuxReaderTextFallbackAsync(navigationToken);
                    PositionLinuxReaderTextFallbackAtChapterEnd(
                        linuxFallbackMovesToTargetEnd);
                    return true;
                },
                navigationToken,
                animate: !holdOverlay && intent != ReaderNavigationIntent.None);
            FocusCurrentReaderHost();
            PrimeReaderContinuousEdgeTracking();
            SetReaderTocSelection(item);
            ReaderChapterText.Text = GetReaderChapterPositionLabel();
            UpdateReaderToolbar();
            ReaderStatusText.Text = string.Empty;
            await UpdateReaderBookmarkIndicatorAsync();
            await SaveReaderProgressAsync(sessionToken);
            _ = PreloadNextReaderChapterAsync(sessionToken);
            return true;
        }
        catch (OperationCanceledException) when (navigationToken.IsCancellationRequested)
        {
            if (ReferenceEquals(_readerNavigationCancellation, navigationCancellation))
            {
                _readerChapterIndex = previousChapterIndex;
                _readerCurrentFragment = previousFragment;
            }
            return false;
        }
        catch (Exception exception)
        {
            if (ReferenceEquals(_readerNavigationCancellation, navigationCancellation))
            {
                _readerChapterIndex = previousChapterIndex;
                _readerCurrentFragment = previousFragment;
            }
            ReaderStatusText.Text = T("打开章节失败：{0}", UiText.Localize(exception.Message));
            return false;
        }
        finally
        {
            await HideReaderChapterHoldOverlayAsync();
            if (ReferenceEquals(_readerNavigationCancellation, navigationCancellation))
                _readerNavigationCancellation = null;
            navigationCancellation.Dispose();
        }
    }

    /// <summary>
    /// Freezes the outgoing page above the webview for the duration of a
    /// chapter switch. Same-host chapter navigation clears the document as
    /// soon as it starts, which shows a blank surface until the new chapter
    /// is revealed; the hold overlay keeps the last visible frame on screen
    /// so the reader only ever changes content once, at the fade-in. Returns
    /// false when no snapshot is available and the caller should keep its
    /// regular transition.
    /// </summary>
    private async Task<bool> TryShowReaderChapterHoldOverlayAsync(
        CancellationToken cancellationToken)
    {
        if (_readerIsPdf) return false;
        if (CurrentReaderHost is not IReaderPageSnapshotProvider provider) return false;
        try
        {
            var png = await provider.CaptureVisiblePageAsync(cancellationToken);
            if (png is not { Length: > 0 })
            {
                LogReaderChapterTiming("hold.captureEmpty", Stopwatch.StartNew());
                return false;
            }
            ReaderChapterHoldImage.Source = new Bitmap(new MemoryStream(png));
            ReaderChapterHoldLayer.Opacity = 1;
            ReaderChapterHoldLayer.IsVisible = true;
            LogReaderChapterTiming("hold.shown", Stopwatch.StartNew());
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private async Task HideReaderChapterHoldOverlayAsync()
    {
        try
        {
            if (!ReaderChapterHoldLayer.IsVisible)
            {
                LogReaderChapterTiming("hide.notVisible", Stopwatch.StartNew());
                return;
            }
            ReaderChapterHoldLayer.Opacity = 0;
            await Task.Delay(220).ConfigureAwait(true);
            ReaderChapterHoldLayer.IsVisible = false;
            ReaderChapterHoldImage.Source = null;
            LogReaderChapterTiming("hold.hidden", Stopwatch.StartNew());
        }
        catch
        {
            // A stuck overlay would block the reader, so tolerate any
            // failure here and force the layer down.
            try
            {
                ReaderChapterHoldLayer.IsVisible = false;
            }
            catch
            {
            }
        }
    }

    private async Task ResetReaderInPageSearchForNavigationAsync()
    {
        if (_readerIsPdf
            || (!ReaderInPageSearchBar.IsVisible && _readerSearchCount <= 0
                && string.IsNullOrWhiteSpace(ReaderInPageSearchBox.Text)))
            return;

        await ClearReaderSearchAsync();
        ReaderInPageSearchBar.IsVisible = false;
        ReaderInPageSearchBox.Text = string.Empty;
    }

    // Intent-aware positioning (Kkindle.Core.ReaderNavigationLocationPolicy):
    // TOC entries without an explicit anchor and progress-slider jumps start
    // at the chapter's first line (normalizing the chapter start); anchors are
    // scrolled for TOC/bookmark/annotation targets; search and AI-source
    // targets keep the DOM untouched because their own offset-based scrolling
    // (and annotation offset math) depends on it; plain switches normalize
    // unless a breakpoint restore is pending.
    private void PruneReaderPendingLocations(ReaderNavigationIntent intent)
    {
        if (!ReaderNavigationLocationPolicy.KeepsChunkOffset(intent))
            _readerPendingChunkOffset = null;
        if (intent != ReaderNavigationIntent.Search)
        {
            _readerPendingSearchQuery = null;
            _readerPendingSearchContext = null;
        }
        if (!ReaderNavigationLocationPolicy.KeepsBookmarkQuote(intent))
        {
            _readerPendingBookmarkQuote = null;
            _readerPendingBookmarkPosition = null;
            _readerPendingBookmarkFlowMode = 0;
        }
        if (intent != ReaderNavigationIntent.Annotation)
            _readerPendingAnnotation = null;
    }

    private async Task ApplyReaderLocationAsync(
        IReaderHost host,
        Uri target,
        CancellationToken cancellationToken,
        ReaderNavigationIntent intent,
        bool hasPendingRestorePosition)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (host is not NativeReaderHost nativeReader)
        {
            return;
        }

        var nativeFragment = DecodeReaderFragment(target.Fragment);
        if (intent is ReaderNavigationIntent.Search or ReaderNavigationIntent.AiSource)
        {
            await ScrollToPendingReaderChunkAsync(host, cancellationToken);
        }
        else if (intent == ReaderNavigationIntent.Annotation && _readerPendingAnnotation is { } pending)
        {
            nativeReader.ScrollToOffset(Math.Max(0, pending.StartOffset));
            _readerPendingAnnotation = null;
        }
        else if (intent == ReaderNavigationIntent.Bookmark)
        {
            if (_readerPendingBookmarkPosition is { } bookmarkPosition)
            {
                if (nativeReader.Vertical)
                {
                    nativeReader.ScrollToOffset((int)Math.Max(0, bookmarkPosition));
                }
                else
                {
                    nativeReader.SeekToPixelScroll(bookmarkPosition);
                }
            }
            _readerPendingBookmarkPosition = null;
            _readerPendingBookmarkQuote = null;
        }
        else if (ReaderNavigationLocationPolicy.ShouldNormalizeChapterStart(
                     intent,
                     target,
                     hasPendingRestorePosition))
        {
            // A plain TOC/progress target can point at the same XHTML that is
            // already loaded. Reset the native page explicitly; otherwise the
            // selection changes while the old page remains visible.
            nativeReader.SeekToBoundary(toEnd: false);
        }
        else if (!string.IsNullOrWhiteSpace(nativeFragment))
        {
            nativeReader.ScrollToFragment(nativeFragment);
        }

        await UpdateReaderScrollStateAsync(host);
        UpdateReaderToolbar();
    }

    private static string? GetReaderTargetFragment(Uri target)
    {
        var fragment = target.Fragment.TrimStart('#');
        if (string.IsNullOrWhiteSpace(fragment)) return null;
        return DecodeReaderFragment(fragment);
    }

    private static string? DecodeReaderFragment(string? value)
    {
        var fragment = value?.TrimStart('#');
        if (string.IsNullOrWhiteSpace(fragment)) return null;
        try { return Uri.UnescapeDataString(fragment); }
        catch { return fragment; }
    }

    private void SetReaderTocSelection(EpubReaderNavigationItem? item)
    {
        var index = item is null ? -1 : FindReaderTocIndex(item);
        _suppressReaderTocSelectionNavigation = true;
        try
        {
            ReaderTocList.SelectedIndex = index;
        }
        finally
        {
            _suppressReaderTocSelectionNavigation = false;
        }
        if (item is not null)
            ReaderTocList.ScrollIntoView(item);
        SetReaderCompactSelectedItem(item);
    }

    private void SetReaderTocSelectionForChapter(int chapterIndex)
    {
        SetReaderTocSelectionForLocation(chapterIndex, null);
    }

    private void SetReaderTocSelectionForLocation(int chapterIndex, string? fragment)
    {
        var chapterItems = _readerTocItems
            .Where(item => item.ChapterIndex == chapterIndex)
            .ToArray();
        var decodedFragment = DecodeReaderFragment(fragment);
        var selected = string.IsNullOrWhiteSpace(decodedFragment)
            ? null
            : chapterItems.FirstOrDefault(item =>
                Uri.TryCreate(item.Target, UriKind.Absolute, out var target)
                && string.Equals(
                    GetReaderTargetFragment(target),
                    decodedFragment,
                    StringComparison.Ordinal));
        selected ??= chapterItems.FirstOrDefault();
        selected ??= _readerTocItems
            .Where(item => item.ChapterIndex <= chapterIndex)
            .OrderByDescending(item => item.ChapterIndex)
            .FirstOrDefault();
        SetReaderTocSelection(selected);
    }

    private EpubReaderNavigationItem? FindAdjacentReaderSubchapter(int direction)
    {
        direction = Math.Sign(direction);
        if (direction == 0) return null;

        var chapterItems = _readerTocItems
            .Where(item => item.ChapterIndex == _readerChapterIndex)
            .ToArray();
        if (chapterItems.Length < 2) return null;

        var currentFragment = DecodeReaderFragment(_readerCurrentFragment);
        var currentIndex = -1;
        if (!string.IsNullOrWhiteSpace(currentFragment))
        {
            currentIndex = Array.FindIndex(chapterItems, item =>
                Uri.TryCreate(item.Target, UriKind.Absolute, out var target)
                && string.Equals(
                    GetReaderTargetFragment(target),
                    currentFragment,
                    StringComparison.Ordinal));
        }

        if (currentIndex < 0)
        {
            currentIndex = Array.FindIndex(chapterItems, item =>
                Uri.TryCreate(item.Target, UriKind.Absolute, out var target)
                && string.IsNullOrWhiteSpace(GetReaderTargetFragment(target)));
        }

        // When every entry is anchored, the first one normally represents the
        // containing chapter heading and therefore acts as the current base.
        if (currentIndex < 0) currentIndex = 0;
        var targetIndex = currentIndex + direction;
        return targetIndex >= 0 && targetIndex < chapterItems.Length
            ? chapterItems[targetIndex]
            : null;
    }

    private int GetCurrentReaderTocIndex()
    {
        if (_readerTocItems.Count == 0) return -1;

        var currentFragment = DecodeReaderFragment(_readerCurrentFragment);
        if (!string.IsNullOrWhiteSpace(currentFragment))
        {
            for (var index = 0; index < _readerTocItems.Count; index++)
            {
                var item = _readerTocItems[index];
                if (item.ChapterIndex == _readerChapterIndex
                    && Uri.TryCreate(item.Target, UriKind.Absolute, out var target)
                    && string.Equals(
                        GetReaderTargetFragment(target),
                        currentFragment,
                        StringComparison.Ordinal))
                {
                    return index;
                }
            }
        }

        var sameChapterIndex = -1;
        var precedingIndex = -1;
        for (var index = 0; index < _readerTocItems.Count; index++)
        {
            var item = _readerTocItems[index];
            if (item.ChapterIndex == _readerChapterIndex && sameChapterIndex < 0)
                sameChapterIndex = index;
            if (item.ChapterIndex <= _readerChapterIndex)
                precedingIndex = index;
        }
        return sameChapterIndex >= 0 ? sameChapterIndex : precedingIndex;
    }

    private async Task ScrollToPendingReaderAnnotationAsync(
        IReaderHost host,
        ReaderAnnotation annotation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (host is NativeReaderHost nativeReader)
        {
            nativeReader.ScrollToOffset(Math.Max(0, annotation.StartOffset));
            await UpdateReaderScrollStateAsync(host);
        }
    }

    private async Task ApplySavedAnnotationsAsync(
        IReaderHost host,
        CancellationToken cancellationToken)
    {
        if (_readerBookFile is null || _readerDocument is null) return;
        var chapterPath = GetReaderChapterPath(host);
        if (chapterPath is null) return;

        if (host is NativeReaderHost nativeReader)
        {
            // Annotation offsets are body-textContent coordinates; the native
            // loader reproduces the same stream, so they apply directly.
            var nativeAnnotations = await _readerData.GetAnnotationsAsync(_readerBookFile.Id, cancellationToken);
            nativeReader.SetAnnotations(nativeAnnotations
                .Where(item => string.Equals(item.ChapterPath, chapterPath, StringComparison.OrdinalIgnoreCase))
                .Where(item => !string.IsNullOrWhiteSpace(item.SelectedText))
                .ToList());
            return;
        }

        var annotations = await _readerData.GetAnnotationsAsync(_readerBookFile.Id, cancellationToken);
        var marks = annotations
            .Where(item => string.Equals(item.ChapterPath, chapterPath, StringComparison.OrdinalIgnoreCase))
            .Where(item => !string.IsNullOrWhiteSpace(item.SelectedText))
            .Select(item => new
            {
                Id = item.Id.ToString("N"),
                StartOffset = item.StartOffset,
                EndOffset = item.EndOffset,
                Quote = item.SelectedText.Trim(),
                Prefix = item.Prefix,
                Suffix = item.Suffix,
                Note = item.Note,
                Color = NormalizeReaderAnnotationColor(item.Color),
                Style = NormalizeReaderAnnotationStyle(item.UnderlineStyle)
            })
            .OrderByDescending(item => item.StartOffset)
            .ToArray();

        var serialized = JsonSerializer.Serialize(marks);
        var script = $$"""
            (() => {
              const annotations = {{serialized}};
              const commonSuffixLength = (left, right) => {
                let length = 0;
                const max = Math.min(left.length, right.length);
                while (length < max && left[left.length - 1 - length] === right[right.length - 1 - length]) length++;
                return length;
              };
              const commonPrefixLength = (left, right) => {
                let length = 0;
                const max = Math.min(left.length, right.length);
                while (length < max && left[length] === right[length]) length++;
                return length;
              };
              const unwrap = mark => {
                const parent = mark.parentNode;
                if (!parent) return;
                while (mark.firstChild) parent.insertBefore(mark.firstChild, mark);
                parent.removeChild(mark);
                parent.normalize?.();
              };
              for (const oldMark of Array.from(document.querySelectorAll('.kkindle-saved-annotation'))) {
                unwrap(oldMark);
              }
              const ignored = node => {
                const parent = node?.parentElement;
                return !parent
                  || ['SCRIPT', 'STYLE', 'NOSCRIPT'].includes(parent.tagName)
                  || !!parent.closest?.('#kkindle-selection-bar, .kkindle-wave-sweep');
              };
              const collectNodes = () => {
                const nodes = [];
                const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
                while (walker.nextNode()) {
                  if (!ignored(walker.currentNode)) nodes.push(walker.currentNode);
                }
                return nodes;
              };
              const segmentsFor = (nodes, start, end) => {
                const segments = [];
                let cursor = 0;
                for (const node of nodes) {
                  const length = (node.data || '').length;
                  const nodeStart = cursor;
                  const nodeEnd = cursor + length;
                  const from = Math.max(start, nodeStart);
                  const to = Math.min(end, nodeEnd);
                  if (to > from) {
                    segments.push({
                      node,
                      start: from - nodeStart,
                      end: to - nodeStart
                    });
                  }
                  cursor = nodeEnd;
                  if (cursor >= end) break;
                }
                return segments;
              };
              const styleMark = (mark, annotation) => {
                mark.className = 'kkindle-saved-annotation';
                if (annotation.Id) mark.setAttribute('data-kkindle-annotation', annotation.Id);
                const color = /^#[0-9a-f]{6}$/i.test(annotation.Color || '') ? annotation.Color : '#000000';
                const style = ['solid', 'double', 'dashed', 'dotted', 'wavy', 'marker'].includes(annotation.Style)
                  ? annotation.Style
                  : 'solid';
                const marker = style === 'marker';
                mark.style.setProperty('background-color', marker ? '#000000' : 'transparent', 'important');
                if (marker) {
                  mark.style.setProperty('color', '#FFFFFF', 'important');
                  mark.style.setProperty('text-decoration-line', 'none', 'important');
                } else {
                  mark.style.setProperty('text-decoration-line', 'underline', 'important');
                  mark.style.setProperty('text-decoration-color', color, 'important');
                  mark.style.setProperty('text-decoration-style', style, 'important');
                  mark.style.setProperty('text-decoration-thickness', '2px', 'important');
                  mark.style.setProperty('text-underline-offset', '3px', 'important');
                  mark.style.setProperty('text-decoration-skip-ink', 'none', 'important');
                }
                mark.style.setProperty('display', 'inline', 'important');
                mark.style.cursor = 'pointer';
                if (annotation.Note) mark.title = annotation.Note;
              };
              const wrapSegments = (segments, annotation) => {
                for (let index = segments.length - 1; index >= 0; index--) {
                  const segment = segments[index];
                  if (!segment.node.parentNode) return false;
                  const range = document.createRange();
                  range.setStart(segment.node, Math.min(segment.start, segment.node.data.length));
                  range.setEnd(segment.node, Math.min(segment.end, segment.node.data.length));
                  const mark = document.createElement('span');
                  styleMark(mark, annotation);
                  try {
                    range.surroundContents(mark);
                  } catch (_) {
                    return false;
                  }
                }
                return true;
              };
              for (const annotation of annotations) {
                const quote = annotation.Quote || '';
                if (!quote) continue;
                // Search the logical text stream, not individual text nodes.
                // EPUB markup commonly splits one visible sentence across
                // spans, emphasis tags, and inline links.
                const nodes = collectNodes();
                const text = nodes.map(node => node.data || '').join('');
                const foldedText = text.toLocaleLowerCase();
                const foldedQuote = quote.toLocaleLowerCase();
                const prefix = (annotation.Prefix || '').slice(-72).toLocaleLowerCase();
                const suffix = (annotation.Suffix || '').slice(0, 72).toLocaleLowerCase();
                let bestAt = -1;
                let bestScore = -1;
                const context = prefix + foldedQuote + suffix;
                const contextAt = context.length > foldedQuote.length
                  ? foldedText.indexOf(context)
                  : -1;
                if (contextAt >= 0) bestAt = contextAt + prefix.length;
                let at = bestAt >= 0 ? -1 : foldedText.indexOf(foldedQuote);
                while (at >= 0) {
                  const before = text.slice(Math.max(0, at - 72), at).toLocaleLowerCase();
                  const after = text.slice(at + quote.length, at + quote.length + 72).toLocaleLowerCase();
                  const score = commonSuffixLength(before, prefix) + commonPrefixLength(after, suffix);
                  if (score > bestScore) {
                    bestScore = score;
                    bestAt = at;
                  }
                  at = foldedText.indexOf(foldedQuote, at + Math.max(1, foldedQuote.length));
                }
                if (bestAt < 0) {
                  const storedStart = Math.max(0, Number(annotation.StartOffset) || 0);
                  const storedEnd = Math.max(storedStart, Number(annotation.EndOffset) || storedStart);
                  if (storedEnd > storedStart && storedEnd <= text.length) {
                    bestAt = storedStart;
                  }
                }
                if (bestAt < 0) continue;
                const length = quote.length > 0
                  ? quote.length
                  : Math.max(0, (Number(annotation.EndOffset) || 0) - (Number(annotation.StartOffset) || 0));
                wrapSegments(segmentsFor(nodes, bestAt, bestAt + length), annotation);
              }
              return true;
            })();
            """;
        await host.InvokeScriptAsync(script);
    }

    private async Task ApplyReaderSearchAsync(string query, int sequence, bool navigate = true)
    {
        if (_readerDocument is null || CurrentReaderHost is not { } host) return;
        if (sequence != _readerSearchSequence) return;
        await _readerSearchMutationGate.WaitAsync(ReaderToken);
        try
        {
            if (sequence != _readerSearchSequence) return;
            if (CurrentReaderHost is NativeReaderHost nativeReader)
            {
                // Case-folded search over the same body-text stream the
                // WebKit walker saw; highlights paint per page.
                var nativeQuery = query.Trim();
                var nativeBody = nativeReader.BodyText ?? string.Empty;
                var nativeHits = ReaderSearchTextPolicy
                    .FindMatches(nativeBody, nativeQuery)
                    .Select(match => (match.Start, match.Length))
                    .ToList();

                nativeReader.SetSearchHighlights(nativeHits, null);
                _readerSearchCount = nativeHits.Count;
                _readerSearchIndex = _readerSearchCount > 0
                    ? navigate
                        ? 0
                        : Math.Clamp(_readerSearchIndex, 0, _readerSearchCount - 1)
                    : -1;
                if (navigate && _readerSearchCount > 0)
                    await NavigateReaderSearchAsync(_readerSearchIndex, sequence);
                else
                    UpdateReaderSearchCount();
                return;
            }
        var serializedQuery = JsonSerializer.Serialize(query);
        var script = $$"""
            (() => {
              const oldMarks = Array.from(document.querySelectorAll('mark.kkindle-page-find-hit'));
              const unwrap = mark => {
                const parent = mark.parentNode;
                if (!parent) return;
                while (mark.firstChild) parent.insertBefore(mark.firstChild, mark);
                parent.removeChild(mark);
                parent.normalize?.();
              };
              for (let index = oldMarks.length - 1; index >= 0; index--) {
                unwrap(oldMarks[index]);
              }
              const query = ({{serializedQuery}} || '').trim();
              if (!query || !document.body) return 0;
              const folded = query.toLocaleLowerCase();
              const ignored = node => {
                const parent = node?.parentElement;
                return !parent
                  || ['SCRIPT', 'STYLE', 'NOSCRIPT'].includes(parent.tagName)
                  || !!parent.closest?.('#kkindle-selection-bar, .kkindle-wave-sweep, mark.kkindle-page-find-hit');
              };
              const nodes = [];
              const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
              while (walker.nextNode()) {
                if (!ignored(walker.currentNode)) nodes.push(walker.currentNode);
              }
              const text = nodes.map(node => node.data || '').join('');
              const foldedText = text.toLocaleLowerCase();
              const matches = [];
              const markedMatches = new Set();
              let start = foldedText.indexOf(folded);
              while (start >= 0) {
                matches.push({ start, length: query.length });
                start = foldedText.indexOf(folded, start + Math.max(1, folded.length));
              }
              const segmentsFor = (start, end) => {
                const segments = [];
                let cursor = 0;
                for (const node of nodes) {
                  const length = (node.data || '').length;
                  const nodeStart = cursor;
                  const nodeEnd = cursor + length;
                  const from = Math.max(start, nodeStart);
                  const to = Math.min(end, nodeEnd);
                  if (to > from) {
                    segments.push({
                      node,
                      start: from - nodeStart,
                      end: to - nodeStart
                    });
                  }
                  cursor = nodeEnd;
                  if (cursor >= end) break;
                }
                return segments;
              };
              for (let index = matches.length - 1; index >= 0; index--) {
                const match = matches[index];
                const segments = segmentsFor(match.start, match.start + match.length);
                let didMark = false;
                for (let segmentIndex = segments.length - 1; segmentIndex >= 0; segmentIndex--) {
                  const segment = segments[segmentIndex];
                  if (!segment.node.parentNode) continue;
                  const range = document.createRange();
                  range.setStart(segment.node, Math.min(segment.start, segment.node.data.length));
                  range.setEnd(segment.node, Math.min(segment.end, segment.node.data.length));
                  const mark = document.createElement('mark');
                  mark.className = 'kkindle-page-find-hit';
                  mark.setAttribute('data-kkindle-page-hit', String(index));
                  mark.style.setProperty('background', '#000000', 'important');
                  mark.style.setProperty('color', '#FFFFFF', 'important');
                  mark.style.setProperty('text-decoration', 'none', 'important');
                  try {
                    range.surroundContents(mark);
                    didMark = true;
                  } catch (_) {
                    // A malformed EPUB node should not abort the remaining hits.
                  }
                }
                if (didMark) markedMatches.add(index);
              }
              return markedMatches.size;
            })();
            """;
        string? result;
        try
        {
            result = await host.InvokeScriptAsync(script);
        }
        catch
        {
            if (sequence != _readerSearchSequence) return;
            _readerSearchCount = 0;
            _readerSearchIndex = -1;
            UpdateReaderSearchCount();
            return;
        }
        if (sequence != _readerSearchSequence) return;
        _readerSearchCount = ParseScriptInt(result);
        _readerSearchIndex = _readerSearchCount > 0
            ? navigate
                ? 0
                : Math.Clamp(_readerSearchIndex, 0, _readerSearchCount - 1)
            : -1;
        if (navigate)
            await NavigateReaderSearchAsync(_readerSearchIndex, sequence);
        else
            UpdateReaderSearchCount();
        }
        finally
        {
            _readerSearchMutationGate.Release();
        }
    }

    private async Task NavigateReaderSearchAsync(int index, int? sequence = null)
    {
        if (_readerSearchCount <= 0 || CurrentReaderHost is not { } host)
        {
            UpdateReaderSearchCount();
            return;
        }
        if (sequence is not null && sequence.Value != _readerSearchSequence) return;
        _readerSearchIndex = (index % _readerSearchCount + _readerSearchCount) % _readerSearchCount;
        if (host is NativeReaderHost nativeReader)
        {
            nativeReader.ScrollToSearchHit(_readerSearchIndex);
            UpdateReaderSearchCount();
        }
    }

    private async Task ClearReaderSearchAsync()
    {
        _readerSearchSequence++;
        _readerPdfSearchSequence++;
        _readerSearchCount = 0;
        _readerSearchIndex = -1;
        await _readerSearchMutationGate.WaitAsync(ReaderToken);
        try
        {
        if (CurrentReaderHost is { } host)
        {
            try
            {
                await host.InvokeScriptAsync("""
                    (() => {
                      const unwrap = mark => {
                        const parent = mark.parentNode;
                        if (!parent) return;
                        while (mark.firstChild) parent.insertBefore(mark.firstChild, mark);
                        parent.removeChild(mark);
                        parent.normalize?.();
                      };
                      for (const mark of Array.from(document.querySelectorAll('mark.kkindle-page-find-hit, mark.kkindle-search-hit'))) {
                        unwrap(mark);
                      }
                    })();
                    """);
            }
            catch
            {
                // PDF's built-in viewer and a just-swapped WebView do not
                // expose a scriptable DOM; clearing search is still complete.
            }
        }
        UpdateReaderSearchCount();
        }
        finally
        {
            _readerSearchMutationGate.Release();
        }
    }

    private async Task SaveReaderLayoutAsync(CancellationToken cancellationToken)
    {
        if (_readerBookCard is null || _readerBookFile is null) return;
        var bookId = _readerBookCard.Book.Id;
        var bookFileId = _readerBookFile.Id;
        var layout = NormalizeReaderLayoutForPlatform(_readerLayout);
        try
        {
            await _readerData.SaveLayoutSettingsAsync(
                bookId,
                bookFileId,
                layout,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
        }
    }

    private async Task SaveCurrentReaderGlobalPreferencesAsync(
        CancellationToken cancellationToken)
    {
        _appSettings = AppSettings.Normalize(_appSettings with
        {
            DefaultReaderLayout = _appSettings.DefaultReaderLayout with
            {
                VerticalWriting = _readerLayout.VerticalWriting,
                ParagraphIndent = _readerLayout.ParagraphIndent
            }
        });

        // Keep the basic-settings switch in lockstep without scheduling a
        // second competing settings write. This is also called by the close
        // checkpoint, so a quick exit cannot lose a just-selected direction.
        _suppressAppSettingsAutoSave = true;
        try
        {
            if (DefaultVerticalWritingCheck is not null)
                DefaultVerticalWritingCheck.IsChecked = _readerLayout.VerticalWriting;
        }
        finally
        {
            _suppressAppSettingsAutoSave = false;
        }

        await _appSettingsStore.SaveAsync(_appSettings, cancellationToken);
    }

    // ------------------------------------------------------------------
    // Reading stats: cumulative active reading time plus a progress
    // snapshot. Time only accrues while the window is active and the
    // reader pane is visible, so simply leaving the book open is not
    // counted as reading time. Mirrors the WinUI reference.
    // ------------------------------------------------------------------

    private void StartReaderStatsTimer()
    {
        if (_readerStatsTimer is null)
        {
            _readerStatsTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _readerStatsTimer.Tick += ReaderStatsTimer_Tick;
        }
        _readerStatsTimer.Start();
    }

    private void StopReaderStatsTimer()
    {
        _readerStatsTimer?.Stop();
    }

    private async Task LoadReaderStatsBaseAsync()
    {
        if (_readerBookFile is null) return;
        try
        {
            var stats = await _readerData.GetReadingStatsAsync(
                _readerBookFile.Id,
                _readerSessionCancellation?.Token ?? CancellationToken.None);
            _readerStatsBaseSeconds = stats?.CumulativeSeconds ?? 0;
            UpdateReaderStatsDisplay();
        }
        catch
        {
        }
    }

    private void ReaderStatsTimer_Tick(object? sender, EventArgs e)
    {
        if (!IsActive || !ReaderRoot.IsVisible) return;
        _readerActiveSeconds++;
        _readerSessionSeconds++;
        if (_readerActiveSeconds % 30 == 0)
            _ = FlushReaderActiveSecondsAsync();
        UpdateReaderStatsDisplay();
    }

    private async Task FlushReaderActiveSecondsAsync()
    {
        await _readerStatsFlushGate.WaitAsync();
        try
        {
            if (_readerBookCard is null || _readerBookFile is null || _readerActiveSeconds <= 0) return;
            var activeSeconds = Interlocked.Exchange(ref _readerActiveSeconds, 0);
            if (activeSeconds <= 0) return;
            try
            {
                await _readerData.AddReadingTimeAsync(
                    _readerBookCard.Book.Id,
                    _readerBookFile.Id,
                    activeSeconds,
                    CalculateReaderProgressPercent(),
                    _readerChapterIndex,
                    _readerDocument?.Chapters.Count ?? (_readerIsPdf ? _readerPdfPages.Count : 0),
                    CancellationToken.None);
            }
            catch
            {
                // Keep unsaved seconds pending so the next periodic flush or
                // reader close can retry instead of silently losing time.
                Interlocked.Add(ref _readerActiveSeconds, activeSeconds);
            }
        }
        finally
        {
            _readerStatsFlushGate.Release();
        }
    }

    private void UpdateReaderStatsDisplay()
    {
        if (ReaderStatsText is null) return;
        var cumulative = _readerStatsBaseSeconds + _readerSessionSeconds;
        ReaderStatsText.Text = T("累计阅读 {0} · 本次 {1}", FormatReaderDuration(cumulative), FormatReaderDuration(_readerSessionSeconds));
    }

    private static string FormatReaderDuration(long seconds)
    {
        if (seconds < 60) return UiText.Get("{0} 秒", seconds);
        if (seconds < 3600) return UiText.Get("{0} 分钟", seconds / 60);
        return UiText.Get("{0:0.0} 小时", seconds / 3600.0);
    }

    private void UpdateReaderZoomLabel()
    {
        if (ReaderZoomText is not null)
            ReaderZoomText.Text = $"{_readerLayout.FontScale:P0}";
    }

    // Transient reader-header status: auto-clears after a short moment instead
    // of lingering forever. A sequence guard plus an exact-text check ensure an
    // older timer never wipes a newer or longer-lived message.
    private void ShowReaderTransientStatus(string message)
    {
        ReaderStatusText.Text = message;
        var sequence = ++_readerTransientStatusSequence;
        _ = Task.Delay(2500).ContinueWith(
            _ => Dispatcher.UIThread.Post(() =>
            {
                if (sequence == _readerTransientStatusSequence
                    && string.Equals(ReaderStatusText.Text, message, StringComparison.Ordinal))
                {
                    ReaderStatusText.Text = string.Empty;
                }
            }),
            TaskScheduler.Default);
    }

    private void HandleReaderBridgeShortcut(string key, bool ctrlKey)
    {
        if (string.Equals(key, "escape", StringComparison.OrdinalIgnoreCase))
        {
            HandleReaderEscapeShortcut();
            return;
        }
        if (string.Equals(key, "f11", StringComparison.OrdinalIgnoreCase))
        {
            ToggleReaderZenMode();
            return;
        }
        if (ctrlKey && string.Equals(key, "f", StringComparison.OrdinalIgnoreCase))
        {
            OpenReaderSearchShortcut();
            return;
        }
        if (ctrlKey
            && string.Equals(key, "b", StringComparison.OrdinalIgnoreCase)
            && !IsReaderTextInputFocused())
        {
            _ = ObserveReaderTaskAsync(ToggleReaderBookmarkAsync());
        }
    }

    private async Task<bool> HandleReaderLinkAsync(string href, bool showFootnote = false)
    {
        _readerFootnoteHoverSequence++;
        if (_readerDocument is null || !Uri.TryCreate(href, UriKind.Absolute, out var uri) || !uri.IsFile)
            return false;
        var path = Path.GetFullPath(uri.LocalPath);
        if (!IsPathInside(_readerDocument.RootPath, path))
            return false;

        var match = _readerDocument.Chapters
            .Select((chapter, index) => (chapter, index))
            .FirstOrDefault(item => string.Equals(Path.GetFullPath(item.chapter), path, StringComparison.OrdinalIgnoreCase));
        if (match.chapter is null)
            return false;
        var chapterIndex = match.index;
        var item = new EpubReaderNavigationItem(
            T("第 {0} 章", chapterIndex + 1),
            uri.AbsoluteUri,
            chapterIndex);
        HideReaderFootnotePopup();
        return await NavigateToReaderItemAsync(
            item,
            _readerSessionCancellation?.Token ?? CancellationToken.None,
            showFootnote ? ReaderNavigationIntent.Footnote : ReaderNavigationIntent.Link);
    }

    private async Task HandleReaderFootnoteHoverAsync(
        string href,
        bool isFootnote,
        Point? placementPoint = null)
    {
        if (!isFootnote
            || _readerDocument is null
            || !Uri.TryCreate(href, UriKind.Absolute, out var uri)
            || !uri.IsFile
            || string.IsNullOrWhiteSpace(uri.Fragment))
            return;
        if (OperatingSystem.IsWindows() && GetCursorPos(out var cursor))
            _readerFootnoteAnchorScreenPoint = new PixelPoint(cursor.X, cursor.Y);
        var sequence = ++_readerFootnoteHoverSequence;
        var path = Path.GetFullPath(uri.LocalPath);
        if (!IsPathInside(_readerDocument.RootPath, path)) return;

        var targets = await _footnotes.ResolveAsync(
            _readerDocument.RootPath,
            [uri.AbsoluteUri],
            ReaderToken);
        if (sequence == _readerFootnoteHoverSequence
            && targets.TryGetValue(EpubFootnoteResolver.NormalizeTargetKey(uri.AbsoluteUri), out var footnote))
        {
            _readerFootnoteHref = uri.AbsoluteUri;
            ShowReaderFootnotePopup(footnote, placementPoint);
        }
    }

    private void ShowDirectReaderFootnote(
        string href,
        string text,
        Point? placementPoint)
    {
        // Some reader exports encode the complete note in an image's alt
        // attribute and have no target XHTML fragment to resolve. Keep the
        // synthetic href as the hover identity, but bypass the URI resolver.
        _readerFootnoteHoverSequence++;
        _readerFootnoteHref = href;
        ShowReaderFootnotePopup(text, placementPoint);
    }

    private void StartReaderFootnoteHoverPoll()
    {
        if (!OperatingSystem.IsWindows()) return;
        _readerFootnoteHoverTimer ??= new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(120)
        };
        _readerFootnoteHoverTimer.Stop();
        _readerFootnoteHoverTimer.Tick -= ReaderFootnoteHoverTimer_Tick;
        _readerFootnoteHoverTimer.Tick += ReaderFootnoteHoverTimer_Tick;
        _readerFootnoteHoverTimer.Start();
    }

    private void StopReaderFootnoteHoverPoll()
    {
        _readerFootnoteHoverTimer?.Stop();
        _readerFootnotePollRunning = false;
        _readerFootnoteHoverMissCount = 0;
        _readerFootnoteHref = null;
        _readerFootnotePlacementPoint = null;
        _readerFootnoteAnchorScreenPoint = null;
        HideReaderFootnotePopup();
    }

    private async void ReaderFootnoteHoverTimer_Tick(object? sender, EventArgs e)
    {
        if (_readerFootnotePollRunning) return;
        _readerFootnotePollRunning = true;
        try
        {
            await PollReaderFootnoteHoverAsync();
        }
        finally
        {
            _readerFootnotePollRunning = false;
        }
    }

    private async Task PollReaderFootnoteHoverAsync()
    {
        // NativeReaderHost reports exact layout hot-zones from Avalonia
        // pointer events. Its script probe is intentionally a no-op, so a
        // Windows polling tick must not dismiss a popup opened by that host.
        if (CurrentReaderHost is NativeReaderHost)
            return;

        if (_readerIsPdf
            || !OperatingSystem.IsWindows()
            || !ReaderRoot.IsVisible
            || ReaderLayoutSettingsPopup.IsOpen
            || !GetCursorPos(out var cursor))
        {
            RegisterReaderFootnoteHoverMiss();
            return;
        }

        var windowScaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1d;
        if (ReaderFootnoteHostPopup.IsOpen
            && _readerFootnoteAnchorScreenPoint is { } anchor
            && Math.Abs(cursor.X - anchor.X) <= 24 * windowScaling
            && Math.Abs(cursor.Y - anchor.Y) <= 24 * windowScaling)
        {
            _readerFootnoteHoverMissCount = 0;
            return;
        }

        if (CurrentReaderHost is not { View: Control view } host)
        {
            RegisterReaderFootnoteHoverMiss();
            return;
        }

        var topLeft = view.PointToScreen(new Avalonia.Point(0, 0));
        var scaling = TopLevel.GetTopLevel(view)?.RenderScaling ?? 1d;
        var width = Math.Max(1, view.Bounds.Width * scaling);
        var height = Math.Max(1, view.Bounds.Height * scaling);
        var relativeX = cursor.X - topLeft.X;
        var relativeY = cursor.Y - topLeft.Y;
        if (relativeX < 0 || relativeX >= width || relativeY < 0 || relativeY >= height)
        {
            RegisterReaderFootnoteHoverMiss();
            return;
        }

        string? result;
        try
        {
            result = await host.InvokeScriptAsync(
                CreateReaderFootnoteHoverProbeScript(relativeX, relativeY, width, height));
        }
        catch
        {
            RegisterReaderFootnoteHoverMiss();
            return;
        }

        var raw = DecodeReaderScriptString(result);
        if (string.IsNullOrWhiteSpace(raw)
            || string.Equals(raw, "null", StringComparison.OrdinalIgnoreCase))
        {
            RegisterReaderFootnoteHoverMiss();
            return;
        }

        string href;
        try
        {
            using var document = JsonDocument.Parse(raw);
            href = ReadString(document.RootElement, "href");
        }
        catch (JsonException)
        {
            RegisterReaderFootnoteHoverMiss();
            return;
        }

        if (string.IsNullOrWhiteSpace(href))
        {
            RegisterReaderFootnoteHoverMiss();
            return;
        }
        _readerFootnoteHoverMissCount = 0;
        if (ReaderFootnoteHostPopup.IsOpen
            && string.Equals(_readerFootnoteHref, href, StringComparison.Ordinal))
        {
            return;
        }

        await HandleReaderFootnoteHoverAsync(
            href,
            isFootnote: true,
            placementPoint: new Point(relativeX / scaling, relativeY / scaling));
    }

    private static string CreateReaderFootnoteHoverProbeScript(
        double relativeX,
        double relativeY,
        double hostWidth,
        double hostHeight)
    {
        var x = relativeX.ToString("0.###", CultureInfo.InvariantCulture);
        var y = relativeY.ToString("0.###", CultureInfo.InvariantCulture);
        var width = hostWidth.ToString("0.###", CultureInfo.InvariantCulture);
        var height = hostHeight.ToString("0.###", CultureInfo.InvariantCulture);
        return $$"""
            (() => {
              const root = document.documentElement;
              const vw = root.clientWidth || document.body?.clientWidth || window.innerWidth || 0;
              const vh = root.clientHeight || document.body?.clientHeight || window.innerHeight || 0;
              if (!vw || !vh) return null;
              const x = Math.max(0, Math.min(vw - 1, Math.round({{x}} * vw / {{width}})));
              const y = Math.max(0, Math.min(vh - 1, Math.round({{y}} * vh / {{height}})));
              const element = document.elementFromPoint(x, y);
              const anchor = element?.closest?.('a');
              if (!anchor || !isFootnoteLink(anchor)) return null;
              let url;
              try {
                const href = anchor.getAttribute('href')
                  || anchor.getAttribute('data-kkindle-footnote-href')
                  || '';
                url = new URL(href, location.href);
              }
              catch { return null; }
              if (!url.hash) return null;
              return JSON.stringify({ href: url.href });
            })();
            """;
    }

    private void ShowReaderFootnotePopup(string text, Point? placementPoint = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            HideReaderFootnotePopup();
            return;
        }

        ReaderFootnoteText.Text = text;
        _readerFootnoteHoverMissCount = 0;
        ReaderFootnotePopup.IsVisible = true;
        if (placementPoint is { } point)
            _readerFootnotePlacementPoint = point;
        var placementTarget = IsLinuxReaderTextFallbackActive()
            ? ReaderWebViewHost
            : CurrentReaderHost?.View as Control;
        if (placementTarget is { } view
            && _readerFootnotePlacementPoint is { } anchor)
        {
            ReaderFootnoteHostPopup.PlacementTarget = view;
            ReaderFootnoteHostPopup.Placement = Avalonia.Controls.PlacementMode.AnchorAndGravity;
            ReaderFootnoteHostPopup.PlacementRect = new Rect(anchor.X, anchor.Y, 1, 1);
            ReaderFootnoteHostPopup.PlacementAnchor =
                Avalonia.Controls.Primitives.PopupPositioning.PopupAnchor.TopLeft;
            ReaderFootnoteHostPopup.PlacementGravity =
                Avalonia.Controls.Primitives.PopupPositioning.PopupGravity.BottomRight;
            ReaderFootnoteHostPopup.PlacementConstraintAdjustment =
                Avalonia.Controls.Primitives.PopupPositioning.PopupPositionerConstraintAdjustment.FlipX
                | Avalonia.Controls.Primitives.PopupPositioning.PopupPositionerConstraintAdjustment.FlipY
                | Avalonia.Controls.Primitives.PopupPositioning.PopupPositionerConstraintAdjustment.SlideX
                | Avalonia.Controls.Primitives.PopupPositioning.PopupPositionerConstraintAdjustment.SlideY;
        }
        else
        {
            ReaderFootnoteHostPopup.PlacementTarget = ReaderWebViewHost;
            ReaderFootnoteHostPopup.Placement = Avalonia.Controls.PlacementMode.Pointer;
        }
        ReaderFootnoteHostPopup.HorizontalOffset = 12;
        ReaderFootnoteHostPopup.VerticalOffset = 14;
        ReaderFootnoteHostPopup.IsOpen = true;
    }

    private void RegisterReaderFootnoteHoverMiss()
    {
        if (!ReaderFootnoteHostPopup.IsOpen || ++_readerFootnoteHoverMissCount >= 2)
            HideReaderFootnotePopup();
    }

    private Point? _readerAnnotationHoverPlacementPoint;

    private void ShowReaderAnnotationHoverPopup(string quote, string note, Point? placementPoint)
    {
        ReaderAnnotationHoverQuote.Text = string.IsNullOrWhiteSpace(quote) ? T("批注") : quote;
        ReaderAnnotationHoverNote.Text = note ?? string.Empty;
        ReaderAnnotationHoverNote.IsVisible = !string.IsNullOrWhiteSpace(note);
        if (placementPoint is { } point)
            _readerAnnotationHoverPlacementPoint = point;
        ReaderAnnotationHoverPopup.IsVisible = true;
        if (CurrentReaderHost?.View is Control view
            && _readerAnnotationHoverPlacementPoint is { } anchor)
        {
            ReaderAnnotationHoverHostPopup.PlacementTarget = view;
            ReaderAnnotationHoverHostPopup.Placement = Avalonia.Controls.PlacementMode.AnchorAndGravity;
            ReaderAnnotationHoverHostPopup.PlacementRect = new Rect(anchor.X, anchor.Y, 1, 1);
            ReaderAnnotationHoverHostPopup.PlacementAnchor =
                Avalonia.Controls.Primitives.PopupPositioning.PopupAnchor.TopLeft;
            ReaderAnnotationHoverHostPopup.PlacementGravity =
                Avalonia.Controls.Primitives.PopupPositioning.PopupGravity.BottomRight;
            ReaderAnnotationHoverHostPopup.PlacementConstraintAdjustment =
                Avalonia.Controls.Primitives.PopupPositioning.PopupPositionerConstraintAdjustment.FlipX
                | Avalonia.Controls.Primitives.PopupPositioning.PopupPositionerConstraintAdjustment.FlipY
                | Avalonia.Controls.Primitives.PopupPositioning.PopupPositionerConstraintAdjustment.SlideX
                | Avalonia.Controls.Primitives.PopupPositioning.PopupPositionerConstraintAdjustment.SlideY;
        }
        else
        {
            ReaderAnnotationHoverHostPopup.PlacementTarget = ReaderWebViewHost;
            ReaderAnnotationHoverHostPopup.Placement = Avalonia.Controls.PlacementMode.Pointer;
        }
        ReaderAnnotationHoverHostPopup.HorizontalOffset = 12;
        ReaderAnnotationHoverHostPopup.VerticalOffset = 14;
        ReaderAnnotationHoverHostPopup.IsOpen = true;
    }

    private void HideReaderAnnotationHoverPopup()
    {
        ReaderAnnotationHoverHostPopup.IsOpen = false;
        ReaderAnnotationHoverPopup.IsVisible = false;
        ReaderAnnotationHoverQuote.Text = string.Empty;
        ReaderAnnotationHoverNote.Text = string.Empty;
        _readerAnnotationHoverPlacementPoint = null;
    }

    private void HideReaderFootnotePopup()
    {
        // Invalidate a resolver that is still awaiting the note text. This
        // prevents a popup from reappearing after the pointer has left.
        _readerFootnoteHoverSequence++;
        ReaderFootnoteHostPopup.IsOpen = false;
        ReaderFootnotePopup.IsVisible = false;
        ReaderFootnoteText.Text = string.Empty;
        _readerFootnotePlacementPoint = null;
        _readerFootnoteHref = null;
        _readerFootnoteAnchorScreenPoint = null;
        _readerFootnoteHoverMissCount = 0;
    }

    // Baseline reader status: the bridge and host navigation both restore it.
    private void ResetReaderStatusText()
    {
        ReaderStatusText.Text = _readerIsPdf
            ? T("PDF · {0} 页", _readerPdfPages.Count)
            : string.Empty;
    }

    private void HandleReaderBridgeMessage(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var typeElement)) return;
            switch (typeElement.GetString())
            {
                case "ready":
                    ResetReaderStatusText();
                    break;
                case "pdfPage":
                    if (_readerIsPdf && root.TryGetProperty("page", out var pdfPage)
                        && pdfPage.TryGetInt32(out var page))
                    {
                        _readerPdfPage = Math.Clamp(page, 1, Math.Max(1, _readerPdfPages.Count));
                        _readerChapterIndex = _readerPdfPage - 1;
                        ReaderChapterText.Text = GetReaderChapterPositionLabel();
                        UpdateReaderToolbar();
                    }
                    goto case "scroll";
                case "scroll":
                    if (IsLinuxReaderTextFallbackActive())
                        break;
                    var horizontalScroll = IsReaderPaginated || _readerLayout.VerticalWriting;
                    if (!_readerIsPdf)
                    {
                        var reportedFragment = ReadString(root, "fragment").TrimStart('#');
                        try { reportedFragment = Uri.UnescapeDataString(reportedFragment); } catch { }
                        _readerCurrentFragment = string.IsNullOrWhiteSpace(reportedFragment)
                            ? null
                            : reportedFragment;
                    }
                    _readerScrollPosition = horizontalScroll
                        ? ReadDouble(root, "left")
                        : ReadDouble(root, "top");
                    // Vertical writing reports a negative scrollLeft range;
                    // track the distance from the origin so progress, saved
                    // positions and edge detection stay sign-agnostic.
                    if (!_readerIsPdf && _readerLayout.VerticalWriting)
                        _readerScrollPosition = Math.Abs(_readerScrollPosition);
                    _readerScrollWidth = ReadDouble(root, "scrollWidth");
                    _readerScrollHeight = ReadDouble(root, "scrollHeight");
                    _readerClientWidth = ReadDouble(root, "clientWidth");
                    _readerClientHeight = ReadDouble(root, "clientHeight");
                    var max = horizontalScroll
                        ? Math.Max(0, _readerScrollWidth - _readerClientWidth)
                        : Math.Max(0, _readerScrollHeight - _readerClientHeight);
                    _readerScrollRatio = max > 0 ? Math.Clamp(_readerScrollPosition / max, 0, 1) : 0;
                    ReaderProgressPercentText.Text = $"{CalculateReaderProgressPercent():0}%";
                    _ = SaveReaderProgressAfterScrollAsync(++_readerProgressSaveSequence);
                    // The bridge already supplied the exact scroll position
                    // and fragment. Reusing that snapshot avoids issuing a
                    // second WebView script call for every animation frame.
                    UpdateReaderBookmarkIndicatorFromTrackedLocation();
                    break;
                case "selectionPageTurn":
                    if (IsLinuxReaderTextFallbackActive())
                        break;
                    UpdateReaderToolbar();
                    break;
                case "selection":
                    if (IsLinuxReaderTextFallbackActive())
                        break;
                    _readerPendingSelection = root.TryGetProperty("text", out var selection)
                        ? selection.GetString()
                        : null;
                    _readerPendingSelectionStartOffset = ReadInt(root, "startOffset");
                    _readerPendingSelectionEndOffset = ReadInt(root, "endOffset");
                    _readerPendingSelectionPrefix = ReadString(root, "prefix");
                    _readerPendingSelectionSuffix = ReadString(root, "suffix");
                    if (!string.IsNullOrWhiteSpace(_readerPendingSelection))
                    {
                        _selectedReaderAnnotation = null;
                        if (CurrentReaderHost is NativeReaderHost nativeReader)
                        {
                            Point? placementPoint = null;
                            double? selectionBottom = null;
                            if (root.TryGetProperty("x", out var selectionX)
                                && root.TryGetProperty("y", out var selectionY)
                                && selectionX.TryGetDouble(out var x)
                                && selectionY.TryGetDouble(out var y))
                            {
                                placementPoint = new Point(x, y);
                                if (root.TryGetProperty("bottom", out var bottom)
                                    && bottom.TryGetDouble(out var bottomValue))
                                {
                                    selectionBottom = bottomValue;
                                }

                                // NativeReaderHost is nested in a ContentControl
                                // inside ReaderWebViewHost. Translate its local
                                // selection geometry before placing the popup,
                                // otherwise the bar is offset by the host slot's
                                // margin on resized reader windows.
                                if (nativeReader.View is Control nativeView
                                    && nativeView.TranslatePoint(new Point(0, 0), ReaderWebViewHost)
                                        is { } nativeOrigin)
                                {
                                    placementPoint = new Point(
                                        placementPoint.Value.X + nativeOrigin.X,
                                        placementPoint.Value.Y + nativeOrigin.Y);
                                    if (selectionBottom is { } translatedBottom)
                                    {
                                        selectionBottom = translatedBottom + nativeOrigin.Y;
                                    }
                                }
                            }

                            // The self-drawn EPUB surface has no DOM action bar;
                            // use the Avalonia popup for the same selection
                            // actions used by the fallback reader.
                            ShowReaderSelectionPopup(placementPoint, selectionBottom);
                        }
                    }
                    else
                    {
                        _readerPendingSelection = null;
                        _readerPendingSelectionStartOffset = 0;
                        _readerPendingSelectionEndOffset = 0;
                        _readerPendingSelectionPrefix = string.Empty;
                        _readerPendingSelectionSuffix = string.Empty;
                        HideReaderSelectionPopup();
                    }
                    break;
                case "selectionAction":
                    DispatchReaderSelectionAction(root);
                    break;
                case "link":
                    if (root.TryGetProperty("href", out var href))
                    {
                        var showFootnote = root.TryGetProperty("footnote", out var footnote)
                            && footnote.ValueKind == JsonValueKind.True;
                        var hrefText = href.GetString() ?? string.Empty;
                        if (showFootnote && CurrentReaderHost is NativeReaderHost)
                        {
                            Point? placementPoint = null;
                            if (root.TryGetProperty("x", out var linkX)
                                && root.TryGetProperty("y", out var linkY)
                                && linkX.TryGetDouble(out var x)
                                && linkY.TryGetDouble(out var y))
                            {
                                placementPoint = new Point(Math.Max(0, x), Math.Max(0, y));
                            }

                            var directFootnoteText = ReadString(root, "footnoteText");
                            if (!string.IsNullOrWhiteSpace(directFootnoteText))
                            {
                                ShowDirectReaderFootnote(
                                    hrefText,
                                    directFootnoteText,
                                    placementPoint);
                            }
                            else
                            {
                                // Native footnote definitions are part of the
                                // composed chapter now. A click is therefore
                                // a real fragment navigation; hover remains
                                // the lightweight preview interaction.
                                _ = ObserveReaderTaskAsync(
                                    HandleReaderLinkAsync(hrefText, showFootnote: true));
                            }
                        }
                        else
                        {
                            _ = ObserveReaderTaskAsync(HandleReaderLinkAsync(hrefText, showFootnote));
                        }
                    }
                    break;
                case "page":
                    if (IsReaderPaginated
                        && root.TryGetProperty("direction", out var pageDirection)
                        && pageDirection.TryGetInt32(out var pageTurnDirection))
                    {
                        pageTurnDirection = Math.Sign(pageTurnDirection);
                        if (pageTurnDirection != 0)
                            _ = ObserveReaderTaskAsync(TurnReaderPageAsync(pageTurnDirection));
                    }
                    break;
                case "pageClick":
                    if (!_readerIsPdf && (_readerLayout.FlowMode == 1 || IsNativeReaderPaginated)
                        && root.TryGetProperty("side", out var pageSide))
                    {
                        var side = pageSide.GetString();
                        if (side is "left" or "right")
                        {
                            var clickTurnDirection = ReaderPaginationPolicy.GetClickDirection(
                                side == "left",
                                _readerLayout.VerticalWriting);
                            _ = ObserveReaderTaskAsync(TurnReaderPageAsync(clickTurnDirection));
                        }
                    }
                    break;
                case "bookmarkToggle":
                    _ = ObserveReaderTaskAsync(ToggleReaderBookmarkAsync());
                    break;
                case "footnoteHover":
                    if (root.TryGetProperty("href", out var footnoteHref))
                    {
                        Point? placementPoint = null;
                        if (root.TryGetProperty("x", out var footnoteX)
                            && root.TryGetProperty("y", out var footnoteY)
                            && footnoteX.TryGetDouble(out var x)
                            && footnoteY.TryGetDouble(out var y))
                        {
                                placementPoint = new Point(Math.Max(0, x), Math.Max(0, y));
                        }
                        var hrefText = footnoteHref.GetString() ?? string.Empty;
                        var directFootnoteText = ReadString(root, "footnoteText");
                        if (!string.IsNullOrWhiteSpace(directFootnoteText))
                        {
                            ShowDirectReaderFootnote(hrefText, directFootnoteText, placementPoint);
                        }
                        else
                        {
                            _ = ObserveReaderTaskAsync(
                                HandleReaderFootnoteHoverAsync(
                                    hrefText,
                                    true,
                                    placementPoint));
                        }
                    }
                    break;
                case "footnoteLeave":
                    // Opening the native Popup above WebView2 can itself make
                    // Chromium report pointerout. On Windows the screen-space
                    // hover poll is authoritative and dismisses after the
                    // pointer has genuinely left the marker.
                    if (!OperatingSystem.IsWindows() || IsNativeReaderPaginated)
                        HideReaderFootnotePopup();
                    break;
                case "annotationHover":
                {
                    Point? annotationPlacementPoint = null;
                    if (root.TryGetProperty("x", out var annotationX)
                        && root.TryGetProperty("y", out var annotationY)
                        && annotationX.TryGetDouble(out var annotationPx)
                        && annotationY.TryGetDouble(out var annotationPy))
                    {
                        annotationPlacementPoint = new Point(Math.Max(0, annotationPx), Math.Max(0, annotationPy));
                    }
                    ShowReaderAnnotationHoverPopup(
                        ReadString(root, "quote"),
                        ReadString(root, "note"),
                        annotationPlacementPoint);
                    break;
                }
                case "annotationLeave":
                    // The native host emits this from Avalonia pointer events,
                    // which stay authoritative regardless of platform.
                    HideReaderAnnotationHoverPopup();
                    break;
                case "resize":
                    ScheduleReaderRelayout();
                    break;
                case "wheel":
                    // Paginated EPUB pages and the PDF text view translate the
                    // vertical wheel into page turns, mirroring the WinUI
                    // reference's low-level mouse hook (120 units per page,
                    // direction flips reset the remainder, and the browser is
                    // told to ignore the event so it never double-scrolls).
                    if (IsReaderPaginated
                        && root.TryGetProperty("deltaY", out var wheel))
                    {
                        var delta = (int)Math.Round(wheel.GetDouble());
                        if (delta != 0)
                        {
                            if (_readerWheelDeltaRemainder != 0
                                && Math.Sign(_readerWheelDeltaRemainder) != Math.Sign(delta))
                            {
                                _readerWheelDeltaRemainder = 0;
                            }
                            _readerWheelDeltaRemainder += delta;
                            if (Math.Abs(_readerWheelDeltaRemainder) >= 120)
                            {
                                var direction = _readerWheelDeltaRemainder > 0 ? 1 : -1;
                                _readerWheelDeltaRemainder %= 120;
                                _ = ObserveReaderTaskAsync(TurnReaderPageAsync(direction));
                            }
                        }
                    }
                    break;
                case "continuousEdge":
                    if (!_readerIsPdf
                        && _readerLayout.FlowMode == 0
                        && !IsNativeReaderPaginated
                        && root.TryGetProperty("direction", out var edgeDirection)
                        && edgeDirection.TryGetInt32(out var continuousDirection))
                    {
                        TryMoveReaderChapterFromContinuousEdge(Math.Sign(continuousDirection));
                    }
                    break;
                case "pointermove":
                    // Zen chrome wake-up for pointer movement over the webview
                    // (an HWND island whose events never reach Avalonia). The
                    // page throttles the reports; keep a host-side tick guard
                    // so a burst of messages cannot restart the timer faster
                    // than the 80 ms window.
                    if (_readerZenMode && !OperatingSystem.IsWindows())
                    {
                        var moveNow = Environment.TickCount64;
                        if (moveNow - _readerZenLastMouseMoveTick > 80)
                        {
                            _readerZenLastMouseMoveTick = moveNow;
                            if (root.TryGetProperty("x", out var pointerX)
                                && root.TryGetProperty("y", out var pointerY)
                                && root.TryGetProperty("width", out var surfaceWidth))
                            {
                                UpdateReaderZenChromeForPointer(
                                    pointerX.GetDouble(),
                                    pointerY.GetDouble(),
                                    surfaceWidth.GetDouble());
                            }
                            else if (!_readerZenChromeVisible)
                            {
                                UpdateReaderZenChrome(visible: true);
                            }
                            else
                            {
                                RestartReaderZenChromeHideTimer();
                            }
                        }
                    }
                    break;
                case "key":
                    if (root.TryGetProperty("key", out var key))
                    {
                        var keyName = key.GetString();
                        if (CurrentReaderHost is NativeReaderHost nativeReader
                            && keyName is "Home" or "End")
                        {
                            nativeReader.SeekToBoundary(string.Equals(keyName, "End", StringComparison.Ordinal));
                            _ = ObserveReaderTaskAsync(UpdateReaderScrollStateAsync(nativeReader));
                            break;
                        }
                        if (IsReaderPaginated)
                        {
                            // Single-page and two-column EPUB layouts share the
                            // same key map: up/down change chapters while
                            // left/right turn pages. PDF keeps all arrows as
                            // page turns.
                            var chapterDirection = !_readerIsPdf
                                ? string.Equals(keyName, "ArrowUp", StringComparison.Ordinal)
                                    ? -1
                                    : string.Equals(keyName, "ArrowDown", StringComparison.Ordinal)
                                        ? 1
                                        : 0
                                : 0;
                            if (chapterDirection != 0)
                            {
                                _ = ObserveReaderTaskAsync(
                                    TurnReaderPageAsync(chapterDirection, chapterOnly: true));
                                break;
                            }
                            // Vertical writing mirrors the horizontal page
                            // order like classical Chinese books: left turns
                            // forward and right turns backward. Up/down and
                            // PageUp/PageDown keep their horizontal meaning.
                            var verticalPageOrder = !_readerIsPdf && _readerLayout.VerticalWriting;
                            var direction = string.Equals(keyName, "ArrowLeft", StringComparison.Ordinal)
                                ? (verticalPageOrder ? 1 : -1)
                                : string.Equals(keyName, "ArrowRight", StringComparison.Ordinal)
                                    ? (verticalPageOrder ? -1 : 1)
                                    : string.Equals(keyName, "ArrowUp", StringComparison.Ordinal)
                                        || string.Equals(keyName, "PageUp", StringComparison.Ordinal)
                                        ? -1
                                        : string.Equals(keyName, "ArrowDown", StringComparison.Ordinal)
                                            || string.Equals(keyName, "PageDown", StringComparison.Ordinal)
                                            ? 1
                                            : 0;
                            if (direction != 0)
                                _ = ObserveReaderTaskAsync(TurnReaderPageAsync(direction));
                        }
                        else if (_readerLayout.FlowMode == 0
                            && !IsNativeReaderPaginated
                            && _readerDocument is not null)
                        {
                            // Continuous mode (WinUI reference): left/right own
                            // chapter navigation; up/down scroll smoothly and
                            // stop at chapter edges instead of advancing.
                            var chapterDirection = string.Equals(keyName, "ArrowLeft", StringComparison.Ordinal)
                                ? -1
                                : string.Equals(keyName, "ArrowRight", StringComparison.Ordinal)
                                    ? 1
                                    : 0;
                            if (chapterDirection != 0)
                            {
                                _ = ObserveReaderTaskAsync(
                                    TurnReaderPageAsync(chapterDirection, chapterOnly: true));
                            }
                            else
                            {
                                var scrollDirection = string.Equals(keyName, "ArrowUp", StringComparison.Ordinal)
                                    ? -1
                                    : string.Equals(keyName, "ArrowDown", StringComparison.Ordinal)
                                        ? 1
                                        : 0;
                                if (scrollDirection != 0)
                                    _ = ObserveReaderTaskAsync(ScrollReaderWithKeyboardAsync(scrollDirection));
                            }
                        }
                    }
                    break;
                case "shortcut":
                    HandleReaderBridgeShortcut(
                        ReadString(root, "key"),
                        root.TryGetProperty("ctrlKey", out var ctrlKey)
                            && ctrlKey.ValueKind == JsonValueKind.True);
                    break;
            }
        }
        catch (JsonException)
        {
        }
    }

    // The selection bar now lives inside the reader page (the webview is a
    // native HWND island Avalonia cannot paint over), so its buttons arrive as
    // bridge messages instead of XAML clicks. Each action mirrors the WinUI
    // reference's selection-bar handlers.
    private void ShowReaderSelectionPopup(Point? placementPoint = null, double? selectionBottom = null)
    {
        if (string.IsNullOrWhiteSpace(_readerPendingSelection))
        {
            HideReaderSelectionPopup();
            return;
        }

        // Remember where the selection sits so the annotation input window can
        // open at the same spot after the bar hands off to it.
        _readerLastSelectionPopupAnchor = placementPoint;
        _readerLastSelectionPopupBottom = selectionBottom;
        ShowReaderPopupNearSelection(
            ReaderSelectionHostPopup,
            ReaderSelectionPopupBar,
            fallbackWidth: 360,
            fallbackHeight: 38,
            placementPoint,
            selectionBottom);
    }

    // Shared placement math for popups anchored to the live text selection:
    // center on the selection's left edge, prefer above, clamp inside the
    // reader body, and never let the positioner flip against the desktop.
    private void ShowReaderPopupNearSelection(
        Popup popup,
        Control content,
        double fallbackWidth,
        double fallbackHeight,
        Point? placementPoint,
        double? selectionBottom)
    {
        var hostWidth = Math.Max(1, ReaderWebViewHost.Bounds.Width);
        var hostHeight = Math.Max(1, ReaderWebViewHost.Bounds.Height);
        var anchor = placementPoint ?? new Point(
            Math.Max(24, hostWidth / 2),
            Math.Max(24, hostHeight / 2));
        var barWidth = content.Bounds.Width;
        var barHeight = content.Bounds.Height;
        // On the first open Avalonia may not have arranged the Popup content
        // yet. Use a conservative fallback size; forcing an infinite measure
        // from a selection event can invalidate the reader's layout pass.
        if (!double.IsFinite(barWidth) || barWidth <= 0) barWidth = fallbackWidth;
        if (!double.IsFinite(barHeight) || barHeight <= 0) barHeight = fallbackHeight;

        const double bodyInset = 8;
        const double aboveGap = 10;
        const double belowGap = 12;

        // Top/Bottom gravity centers the bar on the placement point. Shift
        // that point by half the bar width so the bar's left edge aligns with
        // the selection's left edge. If the selection is too close to the
        // reader's right edge, clamp the whole bar back inside the body.
        var minimumLeft = bodyInset;
        var maximumLeft = Math.Max(minimumLeft, hostWidth - bodyInset - barWidth);
        var left = Math.Clamp(anchor.X, minimumLeft, maximumLeft);
        var anchorX = left + barWidth / 2;
        var bottom = selectionBottom is { } value && double.IsFinite(value)
            ? value
            : anchor.Y;
        var roomAbove = anchor.Y - barHeight - aboveGap >= bodyInset;
        var roomBelow = bottom + belowGap + barHeight <= hostHeight - bodyInset;
        var placeAbove = roomAbove || !roomBelow;
        var anchorY = placeAbove
            ? Math.Clamp(
                anchor.Y,
                bodyInset + barHeight + aboveGap,
                Math.Max(bodyInset + barHeight + aboveGap, hostHeight - bodyInset))
            : Math.Clamp(
                bottom,
                bodyInset,
                Math.Max(bodyInset, hostHeight - bodyInset - barHeight - belowGap));
        popup.PlacementTarget = ReaderWebViewHost;
        popup.Placement = Avalonia.Controls.PlacementMode.AnchorAndGravity;
        popup.PlacementRect = new Rect(anchorX, anchorY, 1, 1);
        popup.PlacementAnchor =
            Avalonia.Controls.Primitives.PopupPositioning.PopupAnchor.TopLeft;
        popup.PlacementGravity =
            placeAbove
                ? Avalonia.Controls.Primitives.PopupPositioning.PopupGravity.Top
                : Avalonia.Controls.Primitives.PopupPositioning.PopupGravity.Bottom;
        popup.PlacementConstraintAdjustment =
            Avalonia.Controls.Primitives.PopupPositioning.PopupPositionerConstraintAdjustment.None;
        popup.HorizontalOffset = 0;
        popup.VerticalOffset = placeAbove ? -aboveGap : belowGap;
        popup.IsOpen = true;
    }

    private void HideReaderSelectionPopup()
    {
        StopReaderSelectionHighlightPointerTracking();
        if (ReaderSelectionHighlightMenuButton?.Flyout is PopupFlyoutBase { IsOpen: true } flyout)
            flyout.Hide();
        if (ReaderSelectionHostPopup is not null)
            ReaderSelectionHostPopup.IsOpen = false;
    }

    private void ReaderRoot_SelectionDismissPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!ReaderSelectionHostPopup.IsOpen
            || string.IsNullOrWhiteSpace(_readerPendingSelection)
            || !e.GetCurrentPoint(ReaderRoot).Properties.IsLeftButtonPressed)
            return;

        // The flyout presenter has its own visual root, so its menu items are
        // not visual descendants of ReaderSelectionPopupBar. They are still
        // part of the selection UI: treating one as an outside click clears
        // the pending selection before the style Click handler can save it.
        if (e.Source is Visual source
            && (ReferenceEquals(source, ReaderSelectionPopupBar)
                || source.GetVisualAncestors().Contains(ReaderSelectionPopupBar)
                || source is MenuFlyoutPresenter
                || source.GetVisualAncestors().OfType<MenuFlyoutPresenter>().Any()))
            return;

        e.Handled = true;
        if (IsLinuxReaderTextFallbackActive())
        {
            // ReaderRoot receives the tunnel event before the fallback
            // content's own pointer handlers. Remember this press through its
            // matching release; otherwise clearing the selection here makes
            // the release handler believe it was an ordinary edge click and
            // it turns the page.
            var fallbackPoint = e.GetPosition(ReaderLinuxTextFallbackOverlay);
            _readerLinuxTextFallbackSelectionDismissPress =
                fallbackPoint.X >= 0
                && fallbackPoint.X <= ReaderLinuxTextFallbackOverlay.Bounds.Width
                && fallbackPoint.Y >= 0
                && fallbackPoint.Y <= ReaderLinuxTextFallbackOverlay.Bounds.Height;
            ClearLinuxReaderTextFallbackSelectionState();
            return;
        }

        HideReaderSelectionPopup();
        _readerPendingSelection = null;
        _readerPendingSelectionStartOffset = 0;
        _readerPendingSelectionEndOffset = 0;
        _readerPendingSelectionPrefix = string.Empty;
        _readerPendingSelectionSuffix = string.Empty;
        if (!_readerIsPdf && CurrentReaderHost is { } host)
            _ = ClearCurrentReaderSelectionAsync(host);
    }

    private async void ReaderSelectionCopyButton_Click(object? sender, RoutedEventArgs e)
    {
        HideReaderSelectionPopup();
        await PerformReaderSelectionCopyAsync();
    }

    private async void ReaderSelectionHighlightButton_Click(object? sender, RoutedEventArgs e)
    {
        await ApplyReaderHighlightStyleAsync("solid");
    }

    private void ReaderSelectionHighlightMenuButton_PointerEntered(object? sender, PointerEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_readerPendingSelection)) return;
        if (sender is Button { Flyout: { IsOpen: false } flyout } button)
        {
            flyout.ShowAt(button);
            StartReaderSelectionHighlightPointerTracking();
        }
    }

    private void StartReaderSelectionHighlightPointerTracking()
    {
        StopReaderSelectionHighlightPointerTracking();
        _readerSelectionHighlightOpenedTick = Environment.TickCount64;
        _readerSelectionHighlightOutsideTicks = 0;
        _readerSelectionHighlightPointerTimer ??= new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(40)
        };
        _readerSelectionHighlightPointerTimer.Tick -= ReaderSelectionHighlightPointerTimer_Tick;
        _readerSelectionHighlightPointerTimer.Tick += ReaderSelectionHighlightPointerTimer_Tick;
        _readerSelectionHighlightPointerTimer.Start();
    }

    private void StopReaderSelectionHighlightPointerTracking()
    {
        _readerSelectionHighlightPointerTimer?.Stop();
    }

    private void ReaderSelectionHighlightPointerTimer_Tick(object? sender, EventArgs e)
    {
        if (ReaderSelectionHighlightMenuButton.Flyout is not PopupFlyoutBase { IsOpen: true } flyout)
        {
            StopReaderSelectionHighlightPointerTracking();
            return;
        }
        if (!TryGetReaderCursorScreenPoint(out var screenPoint))
            return;

        var buttonRect = GetReaderScreenRect(ReaderSelectionHighlightMenuButton);
        var presenter = ReaderSelectionHighlightSolidItem
            .GetVisualAncestors()
            .OfType<MenuFlyoutPresenter>()
            .FirstOrDefault();
        if (presenter is null
            && Environment.TickCount64 - _readerSelectionHighlightOpenedTick < 400)
            return;
        PixelRect? presenterRect = presenter is null ? null : GetReaderScreenRect(presenter);
        if (ContainsReaderScreenPoint(buttonRect, screenPoint)
            || presenterRect is { } menuRect && ContainsReaderScreenPoint(menuRect, screenPoint)
            || IsReaderSelectionHighlightMenuBridge(screenPoint, buttonRect, presenterRect))
        {
            _readerSelectionHighlightOutsideTicks = 0;
            return;
        }

        // Require two consecutive outside samples. A single X11 sample can
        // briefly land between the button and a newly arranged flyout while
        // the compositor is moving it into its final tangent position.
        if (++_readerSelectionHighlightOutsideTicks < 2)
            return;

        flyout.Hide();
        StopReaderSelectionHighlightPointerTracking();
    }

    private static PixelRect GetReaderScreenRect(Control control)
    {
        var topLeft = control.PointToScreen(new Point(0, 0));
        var bottomRight = control.PointToScreen(new Point(control.Bounds.Width, control.Bounds.Height));
        return new PixelRect(
            Math.Min(topLeft.X, bottomRight.X),
            Math.Min(topLeft.Y, bottomRight.Y),
            Math.Abs(bottomRight.X - topLeft.X),
            Math.Abs(bottomRight.Y - topLeft.Y));
    }

    private static bool ContainsReaderScreenPoint(PixelRect rect, PixelPoint point) =>
        point.X >= rect.X
        && point.X <= rect.X + rect.Width
        && point.Y >= rect.Y
        && point.Y <= rect.Y + rect.Height;

    private static bool IsReaderSelectionHighlightMenuBridge(
        PixelPoint point,
        PixelRect button,
        PixelRect? presenter)
    {
        if (presenter is not { } menu) return false;
        var left = Math.Max(button.X, menu.X);
        var right = Math.Min(button.X + button.Width, menu.X + menu.Width);
        var top = Math.Min(button.Y + button.Height, menu.Y + menu.Height);
        var bottom = Math.Max(button.Y, menu.Y);
        return left <= right
            && point.X >= left
            && point.X <= right
            && point.Y >= top
            && point.Y <= bottom;
    }

    private async void ReaderSelectionHighlightStyleItem_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string style }) return;
        await ApplyReaderHighlightStyleAsync(style);
    }

    private void ReaderSelectionAnnotateButton_Click(object? sender, RoutedEventArgs e)
    {
        HideReaderSelectionPopup();
        ShowReaderAnnotationInputPopup();
    }

    private void ReaderSelectionAiButton_Click(object? sender, RoutedEventArgs e)
    {
        HideReaderSelectionPopup();
        if (string.IsNullOrWhiteSpace(_readerPendingSelection)) return;
        ShowReaderAiTab();
        _ = ObserveReaderTaskAsync(SendReaderAiQuestionAsync(
            T("请解释下面这段文字的含义、上下文和隐含前提，并给出一个简单例子：\n\n{0}", _readerPendingSelection)));
    }

    private void ReaderSelectionSearchButton_Click(object? sender, RoutedEventArgs e)
    {
        HideReaderSelectionPopup();
        if (string.IsNullOrWhiteSpace(_readerPendingSelection)) return;
        ShowReaderSearchPanel();
        ReaderTocSearchBox.Text = _readerPendingSelection;
        ReaderTocSearchBox.Focus();
    }

    private async void ReaderSelectionDictionaryButton_Click(object? sender, RoutedEventArgs e)
    {
        HideReaderSelectionPopup();
        await PerformReaderSelectionDictionaryAsync();
    }

    private void DispatchReaderSelectionAction(JsonElement root)
    {
        if (!root.TryGetProperty("action", out var actionElement)) return;
        switch (actionElement.GetString())
        {
            case "copy":
                _ = ObserveReaderTaskAsync(PerformReaderSelectionCopyAsync());
                break;
            case "highlight":
                if (root.TryGetProperty("style", out var styleElement)
                    && styleElement.GetString() is { } style)
                {
                    _ = ObserveReaderTaskAsync(ApplyReaderHighlightStyleAsync(style));
                }
                break;
            case "annotate":
                HideReaderSelectionPopup();
                ShowReaderAnnotationInputPopup();
                break;
            case "ai":
                if (!string.IsNullOrWhiteSpace(_readerPendingSelection))
                {
                    ShowReaderAiTab();
                    _ = ObserveReaderTaskAsync(SendReaderAiQuestionAsync(
                        T("请解释下面这段文字的含义、上下文和隐含前提，并给出一个简单例子：\n\n{0}", _readerPendingSelection)));
                }
                break;
            case "search":
                if (string.IsNullOrWhiteSpace(_readerPendingSelection)) break;
                ShowReaderSearchPanel();
                ReaderTocSearchBox.Text = _readerPendingSelection;
                ReaderTocSearchBox.Focus();
                break;
            case "dictionary":
                _ = ObserveReaderTaskAsync(PerformReaderSelectionDictionaryAsync());
                break;
        }
    }

    // Continuous-mode short-chapter skip (WinUI reference's
    // SkipShortChapterIfNeededAsync): shortly after entering a chapter, if it
    // cannot scroll at all (content fits the viewport), advance to the next
    // one so the reader never stops on an empty page. Depth-capped so a chain
    // of empty chapters cannot loop forever.
    private async Task SkipShortReaderChapterIfNeededAsync(
        int enteredIndex,
        CancellationToken cancellationToken)
    {
        if (_readerDocument is null || CurrentReaderHost is not { } host) return;
        try
        {
            await Task.Delay(60, cancellationToken);
            var result = await host.InvokeScriptAsync(
                "(() => { const el = document.scrollingElement || document.documentElement; if (!el) return '{}'; return JSON.stringify({ sh: el.scrollHeight, ch: el.clientHeight, sw: el.scrollWidth, cw: el.clientWidth }); })();");
            if (result is null) return;
            var raw = result.Trim().Trim('"');
            if (raw.Length == 0 || raw == "{}") return;
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            var horizontal = _readerLayout.VerticalWriting;
            var scrollSize = horizontal
                ? ReadDouble(root, "sw")
                : ReadDouble(root, "sh");
            var clientSize = horizontal
                ? ReadDouble(root, "cw")
                : ReadDouble(root, "ch");
            if (scrollSize <= 0 || clientSize <= 0) return;
            if (scrollSize > clientSize + 16) return;
            if (_readerChapterIndex != enteredIndex) return;
            if (_readerChapterIndex + 1 >= _readerDocument.Chapters.Count) return;
            if (_readerContinuousSkipDepth >= 5) return;
            _readerContinuousSkipDepth++;
            await MoveReaderChapterAsync(1);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
        }
    }

    private static async Task ObserveReaderTaskAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // DOM events can race the host being replaced or disposed. A stale
            // event must never become an unobserved UI exception.
        }
    }

    private async Task SaveReaderProgressAfterScrollAsync(int sequence)
    {
        var token = _readerSessionCancellation?.Token ?? CancellationToken.None;
        try
        {
            await Task.Delay(700, token);
            if (sequence != _readerProgressSaveSequence || (_readerDocument is null && !_readerIsPdf)) return;
            await SaveReaderProgressAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    // ------------------------------------------------------------------
    // Scroll-edge chapter advance (滚动接章). In scroll mode the reader
    // continuously advances to the next chapter when the scroll position
    // reaches the bottom edge and steps back at the top edge, with a lock
    // that prevents one chapter from triggering repeated transitions.
    // Mirrors the WinUI reference's PollReaderScrollAsync.
    // ------------------------------------------------------------------

    private void ResetReaderContinuousEdgeTracking()
    {
        _readerContinuousPositionInitialized = false;
        _readerPreviousScrollPosition = 0;
        _readerLastNearTop = false;
        _readerLastNearBottom = false;
        _readerLinuxTextFallbackWheelDeltaRemainder = 0;
        _readerLinuxTextFallbackContinuousWheelDirection = 0;
        _readerLinuxTextFallbackContinuousWheelLastTick = 0;
    }

    private void PrimeReaderContinuousEdgeTracking()
    {
        if (_readerIsPdf || _readerLayout.FlowMode != 0)
        {
            ResetReaderContinuousEdgeTracking();
            return;
        }

        var vertical = _readerLayout.VerticalWriting;
        var scrollSize = vertical ? _readerScrollWidth : _readerScrollHeight;
        var clientSize = vertical ? _readerClientWidth : _readerClientHeight;
        _readerPreviousScrollPosition = _readerScrollPosition;
        _readerContinuousPositionInitialized = true;
        _readerLastNearTop = _readerScrollPosition <= 48;
        _readerLastNearBottom = scrollSize > 0
            && clientSize > 0
            && _readerScrollPosition + clientSize >= scrollSize - 48;
    }

    private void TryAdvanceReaderScrollChapter()
    {
        if (_readerIsPdf || _readerScrollPollRunning) return;
        if (_readerLayout.FlowMode != 0) return;
        if (_readerDocument is null || _readerDocument.Chapters.Count <= 1) return;
        if (_readerChapterIndex < 0 || _readerChapterIndex >= _readerDocument.Chapters.Count) return;
        if (_readerZenMode) return;

        var vertical = _readerLayout.VerticalWriting;
        var scrollSize = vertical ? _readerScrollWidth : _readerScrollHeight;
        var clientSize = vertical ? _readerClientWidth : _readerClientHeight;
        var scrollPosition = _readerScrollPosition;
        if (scrollSize <= 0 || clientSize <= 0) return;

        var nearTop = scrollPosition <= 48;
        var nearBottom = scrollPosition + clientSize >= scrollSize - 48;
        var overflows = scrollSize > clientSize + 16;
        if (!_readerContinuousPositionInitialized)
        {
            _readerContinuousPositionInitialized = true;
            _readerPreviousScrollPosition = scrollPosition;
            _readerLastNearTop = nearTop;
            _readerLastNearBottom = nearBottom;
            return;
        }

        var movement = scrollPosition - _readerPreviousScrollPosition;
        _readerPreviousScrollPosition = scrollPosition;

        if (!nearTop && !nearBottom)
        {
            // Scrolled into the middle: release the continuous lock so the
            // next edge transition is treated as a fresh user action.
            _readerContinuousLocked = false;
            _readerLastNearTop = nearTop;
            _readerLastNearBottom = nearBottom;
            return;
        }

        if (_readerContinuousLocked)
        {
            var forceForward = _readerContinuousDirection > 0
                && overflows
                && nearBottom
                && DateTimeOffset.UtcNow - _readerLastChapterChange > TimeSpan.FromMilliseconds(500);
            var forceBackward = _readerContinuousDirection < 0
                && overflows
                && nearTop
                && DateTimeOffset.UtcNow - _readerLastChapterChange > TimeSpan.FromMilliseconds(500);
            if (forceForward || forceBackward)
                _readerContinuousLocked = false;
            else
                return;
        }

        if (nearBottom && !_readerLastNearBottom && movement > 0.5)
        {
            TryMoveReaderChapterFromContinuousEdge(1);
        }
        else if (nearTop && !_readerLastNearTop && movement < -0.5)
        {
            TryMoveReaderChapterFromContinuousEdge(-1);
        }
        _readerLastNearTop = nearTop;
        _readerLastNearBottom = nearBottom;
    }

    private void TryMoveReaderChapterFromContinuousEdge(int direction)
    {
        if (direction == 0 || _readerIsPdf || _readerLayout.FlowMode != 0) return;
        if (_readerScrollPollRunning || _readerDocument is null) return;
        if (direction < 0 && _readerChapterIndex <= 0) return;
        if (direction > 0 && _readerChapterIndex + 1 >= _readerDocument.Chapters.Count) return;

        var now = DateTimeOffset.UtcNow;
        if (_readerContinuousLocked
            && now - _readerLastChapterChange <= TimeSpan.FromMilliseconds(500))
        {
            return;
        }

        _readerContinuousLocked = true;
        _readerContinuousDirection = direction;
        _readerLastChapterChange = now;
        _readerScrollPollRunning = true;
        _ = ObserveReaderTaskAsync(MoveReaderChapterFromScrollAsync(direction));
    }

    private async Task MoveReaderChapterFromScrollAsync(int direction)
    {
        try
        {
            await MoveReaderChapterAsync(direction);
        }
        finally
        {
            _readerScrollPollRunning = false;
        }
    }

    // Continuous-mode keyboard scroll: up/down move 72 px smoothly and stop at
    // chapter edges (left/right own chapter navigation), exactly like the
    // WinUI reference's ScrollReaderWithKeyboardAsync.
    private async Task ScrollReaderWithKeyboardAsync(int direction)
    {
        if (IsLinuxReaderTextFallbackActive())
        {
            SetLinuxReaderTextFallbackOffset(
                ReaderLinuxTextFallbackScroll.Offset.Y + Math.Sign(direction) * 72);
            TryAdvanceReaderScrollChapter();
            return;
        }

        if (CurrentReaderHost is NativeReaderHost nativeReader)
        {
            // The continuous native surface scrolls itself; keyboard input
            // nudges the same offset the wheel drives.
            nativeReader.ScrollByPixel(Math.Sign(direction) * 72);
            _ = ObserveReaderTaskAsync(UpdateReaderScrollStateAsync(nativeReader));
            return;
        }

        if (CurrentReaderHost is not { } host) return;
        try
        {
            await host.InvokeScriptAsync(
                CreateReaderKeyboardScrollScript(direction, _readerLayout.VerticalWriting));
        }
        catch
        {
            // A stale host must never surface a keyboard scroll failure.
        }
    }

    private static string CreateReaderKeyboardScrollScript(int direction, bool vertical) =>
        $$"""
        (() => {
          const el = document.scrollingElement || document.documentElement;
          if (!el) return false;
          const horizontal = {{(vertical ? "true" : "false")}};
          // Vertical writing anchors scroll at the right edge with a negative
          // scrollLeft range; measure and advance by distance from the origin.
          const position = horizontal ? Math.abs(el.scrollLeft || 0) : el.scrollTop;
          const viewport = horizontal ? el.clientWidth : el.clientHeight;
          const extent = horizontal ? el.scrollWidth : el.scrollHeight;
          const sign = {{(direction < 0 ? -1 : 1)}};
          if (sign < 0 && position <= 4) return false;
          if (sign > 0 && position + viewport >= extent - 4) return false;
          const delta = sign * 72;
          if (horizontal
              && typeof window.__kkindleScrollContinuousVerticalBy === 'function')
            return window.__kkindleScrollContinuousVerticalBy(delta);
          window.scrollBy(horizontal
            ? { left: vertical ? -delta : delta, top: 0, behavior: 'smooth' }
            : { left: 0, top: delta, behavior: 'smooth' });
          return true;
        })();
        """;

    private async Task TurnLinuxReaderTextFallbackPageAsync(int direction)
    {
        if (!IsLinuxReaderTextFallbackActive() || _readerDocument is null) return;
        direction = Math.Sign(direction);
        if (direction == 0) return;

        if (_readerLayout.FlowMode == 1)
        {
            if (GetLinuxReaderTextFallbackPageCount() <= 1 && _readerLinuxTextFallbackPages.Count == 0)
                RebuildLinuxReaderTextFallbackPages();
            var spreadSize = _readerLayout.TwoPageMode ? 2 : 1;
            var maximum = Math.Max(0, GetLinuxReaderTextFallbackPageCount() - spreadSize);
            var targetPage = _readerLinuxTextFallbackPageIndex + direction * spreadSize;
            if (targetPage >= 0 && targetPage <= maximum)
            {
                await RunLinuxReaderFallbackContentTransitionAsync<int>(
                    ReaderPaginationPolicy.GetVisualTurnDirection(
                        direction,
                        _readerLayout.VerticalWriting),
                    _readerPageAnimation,
                    () =>
                    {
                        _readerLinuxTextFallbackPageIndex = targetPage;
                        RenderLinuxReaderTextFallbackPage();
                        SyncLinuxReaderTextFallbackPagedState(saveProgress: true);
                        return Task.FromResult(0);
                    },
                    ReaderToken);
                return;
            }

            if (await TryNavigateAdjacentReaderTocPageBoundaryAsync(direction))
                return;

            if (direction > 0 && _readerChapterIndex + 1 < _readerDocument.Chapters.Count)
                await MoveReaderChapterAsync(1);
            else if (direction < 0 && _readerChapterIndex > 0)
                await MoveReaderChapterAsync(-1);
            else
                ReaderStatusText.Text = direction > 0 ? T("已经是最后一章。") : T("已经是第一章。");
            return;
        }

        var extent = Math.Max(0, ReaderLinuxTextFallbackScroll.Extent.Height);
        var viewport = Math.Max(0, ReaderLinuxTextFallbackScroll.Viewport.Height);
        var scrollMaximum = Math.Max(0, extent - viewport);
        var current = Math.Clamp(ReaderLinuxTextFallbackScroll.Offset.Y, 0, scrollMaximum);
        var pageDelta = Math.Max(120, viewport - 72);
        const double edgeTolerance = 4;

        if (direction > 0)
        {
            if (current < scrollMaximum - edgeTolerance)
            {
                SetLinuxReaderTextFallbackOffset(Math.Min(scrollMaximum, current + pageDelta));
                return;
            }

            if (_readerChapterIndex + 1 < _readerDocument.Chapters.Count)
                await MoveReaderChapterAsync(1);
            else
                ReaderStatusText.Text = T("已经是最后一章。");
            return;
        }

        if (current > edgeTolerance)
        {
            SetLinuxReaderTextFallbackOffset(Math.Max(0, current - pageDelta));
            return;
        }

        if (_readerChapterIndex > 0)
            await MoveReaderChapterAsync(-1);
        else
            ReaderStatusText.Text = T("已经是第一章。");
    }

    private void PositionLinuxReaderTextFallbackAtChapterEnd(bool requested)
    {
        if (!requested
            || !IsLinuxReaderTextFallbackActive()
            || _readerLayout.FlowMode != 1)
        {
            return;
        }

        if (_readerLinuxTextFallbackPageItems.Count == 0)
            RebuildLinuxReaderTextFallbackPages();
        var spreadSize = _readerLayout.TwoPageMode ? 2 : 1;
        _readerLinuxTextFallbackPageIndex =
            ReaderLinuxTextFallbackPagingPolicy.ResolvePageIndex(
                currentPageIndex: -1,
                scrollPosition: -1,
                moveToChapterEnd: true,
                pageCount: GetLinuxReaderTextFallbackPageCount(),
                spreadSize);
        SyncLinuxReaderTextFallbackPagedState(saveProgress: false);
    }

    private async Task<bool> TryNavigateAdjacentReaderTocPageBoundaryAsync(int direction)
    {
        direction = Math.Sign(direction);
        if (direction == 0 || _readerTocItems.Count == 0) return false;

        var currentIndex = GetCurrentReaderTocIndex();
        var targetIndex = currentIndex + direction;
        if (currentIndex < 0 || targetIndex < 0 || targetIndex >= _readerTocItems.Count)
            return false;

        var target = _readerTocItems[targetIndex];
        if (direction < 0)
        {
            var next = targetIndex + 1 < _readerTocItems.Count
                ? _readerTocItems[targetIndex + 1]
                : null;
            _readerLinuxTextFallbackMoveToChapterEnd = true;
            _readerLinuxTextFallbackEndFragment = next is not null
                && next.ChapterIndex == target.ChapterIndex
                && Uri.TryCreate(next.Target, UriKind.Absolute, out var nextTarget)
                    ? GetReaderTargetFragment(nextTarget)
                    : null;
        }
        else
        {
            _readerLinuxTextFallbackMoveToChapterEnd = false;
            _readerLinuxTextFallbackEndFragment = null;
        }

        var navigated = await NavigateToReaderItemAsync(
            target,
            ReaderToken,
            ReaderNavigationIntent.Toc,
            transitionDirection: direction);
        if (!navigated)
        {
            _readerLinuxTextFallbackMoveToChapterEnd = false;
            _readerLinuxTextFallbackEndFragment = null;
        }
        return navigated;
    }

    private async Task TurnReaderPageAsync(int direction, bool chapterOnly = false)
    {
        direction = Math.Sign(direction);
        if (direction == 0) return;

        var navigation = direction * (chapterOnly ? 2 : 1);
        Interlocked.Exchange(ref _readerPendingKeyboardNavigation, navigation);
        while (true)
        {
            // Park until the gate is free instead of dropping the request: a
            // click or wheel notch that arrives while a page-turn animation
            // is still playing must still turn once the animation settles,
            // exactly like the user expects from rapid paging.
            await _readerPageTurnGate.WaitAsync(ReaderToken);
            try
            {
                while ((navigation = Interlocked.Exchange(
                           ref _readerPendingKeyboardNavigation,
                           0)) != 0)
                {
                    await TurnReaderPageCoreAsync(
                        Math.Sign(navigation),
                        chapterOnly: Math.Abs(navigation) == 2);
                }
            }
            finally
            {
                _readerPageTurnGate.Release();
            }

            // A newer input may have parked while this drain ran. Reacquire
            // and honor it; the shared pending slot bounds the backlog to one
            // latest direction.
            if (Volatile.Read(ref _readerPendingKeyboardNavigation) == 0)
                return;
        }
    }

    /// <summary>
    /// Page-compose turn: the compose helper fills the next/previous page in
    /// place (the bank/history containers make this a node shuffle), the hold
    /// overlay covers the compose beat, and the reading position persists as
    /// the page-start character offset.
    /// </summary>
    private async Task TurnReaderPageCoreAsync(int direction, bool chapterOnly)
    {
        if (chapterOnly)
        {
            await MoveReaderChapterAsync(direction, startAtChapterTitle: true);
            return;
        }
        if (_readerIsPdf)
        {
            await NavigatePdfPageAsync(_readerPdfPage + direction, ReaderToken);
            return;
        }
        if (CurrentReaderHost is not { } host) return;
        if (host is not NativeReaderHost nativeTurn)
        {
            // Only the self-drawn surface paginates EPUB now.
            return;
        }
        if (IsLinuxReaderTextFallbackActive())
        {
            await TurnLinuxReaderTextFallbackPageAsync(direction);
            return;
        }

        if (nativeTurn.CanTurn(direction))
        {
            await TurnReaderPageWithAnimationAsync(host, direction);
            return;
        }

        if (direction > 0 && _readerDocument is not null
            && _readerChapterIndex < _readerDocument.Chapters.Count - 1)
        {
            await MoveReaderChapterAsync(1);
        }
        else if (direction < 0 && _readerChapterIndex > 0)
        {
            await MoveReaderChapterAsync(-1);
        }
    }

    private async Task<string?> TurnReaderPageWithAnimationAsync(
        IReaderHost host,
        int direction)
    {
        return await RunReaderContentTransitionAsync(
            host,
            host,
            direction,
            () => host is NativeReaderHost nativeTurn
                ? Task.FromResult<string?>(nativeTurn.TurnPage(direction) ? "true" : "false")
                : Task.FromResult<string?>(null),
            ReaderToken);
    }

    /// <summary>
    /// Runs the one transition pipeline used by both in-chapter page turns and
    /// chapter/TOC navigation. With two hosts, the outgoing document animates
    /// out and the prepared incoming document resumes from the same visual
    /// state after the host swap.
    /// </summary>
    private async Task<T> RunReaderContentTransitionAsync<T>(
        IReaderHost outgoingHost,
        IReaderHost incomingHost,
        int direction,
        Func<Task<T>> changeContentAsync,
        CancellationToken cancellationToken,
        bool animate = true)
    {
        var animation = animate ? _readerPageAnimation : ReaderAnimationNone;
        if (animation == ReaderAnimationNone)
        {
            return await changeContentAsync();
        }

        var visualDirection = ReaderPaginationPolicy.GetVisualTurnDirection(
            direction,
            !_readerIsPdf && _readerLayout.VerticalWriting);

        if (UseLinuxPlainTextRecoveryFallback && OperatingSystem.IsLinux()
            && outgoingHost is not NativeReaderHost)
        {
            return await RunLinuxReaderFallbackContentTransitionAsync(
                visualDirection,
                animation,
                changeContentAsync,
                cancellationToken);
        }

        if (outgoingHost is NativeReaderHost nativeHost)
        {
            // The self-drawn surface renders the incoming page immediately, so
            // the outgoing frame is photographed and animated away on a
            // snapshot overlay while the new page waits underneath.
            var surface = BuildReaderNativeTransitionSurface(nativeHost);
            if (surface is null)
                return await changeContentAsync();
            return await ReaderTransitionPlayer.RunAsync(
                surface,
                animation,
                visualDirection,
                changeContentAsync,
                cancellationToken);
        }

        return await changeContentAsync();
    }

    private ReaderTransitionSurface? BuildReaderNativeTransitionSurface(
        NativeReaderHost host)
    {
        if (host.View is not Control nativeView) return null;
        var bounds = ReaderNativeTransitionLayer.Bounds;
        if (bounds.Width < 32 || bounds.Height < 32) return null;
        return new ReaderTransitionSurface(
            nativeView,
            ReaderNativeTransitionSnapshot,
            ReaderNativeTransitionGhost,
            ReaderNativeTransitionTrail,
            ReaderNativeTransitionFront,
            ReaderNativeTransitionEdge,
            ReaderWebViewHost.Background);
    }

    private ReaderTransitionSurface? BuildLinuxReaderFallbackTransitionSurface()
    {
        if (!IsLinuxReaderTextFallbackActive())
            return null;
        var bounds = ReaderLinuxTextFallbackContent.Bounds;
        if (bounds.Width < 32 || bounds.Height < 32)
            return null;
        return new ReaderTransitionSurface(
            ReaderLinuxTextFallbackContent,
            ReaderLinuxTextFallbackTransitionSnapshot,
            ReaderLinuxTextFallbackTransitionGhost,
            ReaderLinuxTextFallbackTransitionTrail,
            ReaderLinuxTextFallbackTransitionFront,
            ReaderLinuxTextFallbackTransitionEdge,
            ReaderLinuxTextFallbackOverlay.Background);
    }

    /// <summary>
    /// Linux counterpart of the WebView transition pipeline: plays the
    /// selected animation over the native fallback surface while the content
    /// change renders underneath. Falls back to an instant switch whenever
    /// the fallback layer is inactive or a snapshot cannot be captured.
    /// </summary>
    private async Task<T> RunLinuxReaderFallbackContentTransitionAsync<T>(
        int visualDirection,
        int animation,
        Func<Task<T>> changeContentAsync,
        CancellationToken cancellationToken)
    {
        if (animation == ReaderAnimationNone)
            return await changeContentAsync();

        var surface = BuildLinuxReaderFallbackTransitionSurface();
        if (surface is null)
            return await changeContentAsync();

        return await ReaderTransitionPlayer.RunAsync(
            surface,
            animation,
            visualDirection,
            changeContentAsync,
            cancellationToken);
    }

    private const int ReaderTransitionOutDurationMs = 300;
    private const int ReaderTransitionInDurationMs = 360;
    private const int ReaderSlideDurationMs = 430;
    // 墨水屏刷新波前传播时长（普通文本翻页约 200~250ms）；残影在波形结束后
    // 还要保持并消退，因此覆盖层需要多停留 ReaderWaveScripts.GhostTailMs。
    private const int ReaderWaveDurationMs = 230;

    private static async Task<bool> WaitForReaderOverlayReadyAsync(
        IReaderHost host,
        string readyScript,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await TryInvokeReaderBooleanScriptAsync(host, readyScript))
                return true;
            await Task.Delay(10, cancellationToken);
        }
        return false;
    }

    private static async Task TryInvokeReaderTransitionAsync(IReaderHost host, string script)
    {
        try
        {
            await host.InvokeScriptAsync(script);
        }
        catch
        {
            // Animations are decorative and must never block navigation.
        }
    }

    private static async Task<bool> TryInvokeReaderBooleanScriptAsync(
        IReaderHost host,
        string script)
    {
        try
        {
            var result = await host.InvokeScriptAsync(script);
            return string.Equals(
                result?.Trim().Trim('"'),
                "true",
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string CreateReaderFadeTransitionScript(int phase) =>
        $$"""
            (() => {
              const surface = document.body || document.documentElement;
              if (!surface) return false;
              surface.style.willChange = 'opacity';
              const phase = {{phase}};
              if (phase === 0) {
                surface.style.transition = 'opacity {{ReaderTransitionOutDurationMs}}ms cubic-bezier(.4,0,.6,1)';
                surface.style.opacity = '0';
              } else if (phase === 1) {
                surface.style.transition = 'none';
                surface.style.opacity = '0';
              } else {
                surface.style.transition = 'none';
                void surface.offsetWidth;
                surface.style.transition = 'opacity {{ReaderTransitionInDurationMs}}ms cubic-bezier(.4,0,.2,1)';
                surface.style.opacity = '1';
              }
              return true;
            })();
            """;

    private static string CreateReaderFadeTransitionCleanupScript() =>
        """
        (() => {
          const surface = document.body || document.documentElement;
          if (!surface) return false;
          surface.style.transition = 'none';
          surface.style.opacity = '1';
          surface.style.removeProperty('will-change');
          window.requestAnimationFrame?.(() => {
            surface.style.removeProperty('transition');
            surface.style.removeProperty('opacity');
          });
          return true;
        })();
        """;

    private void ReaderTocButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_readerSearchVisible)
        {
            HideReaderSearchPanel(restorePreviousLayout: false);
            _readerTocExpanded = true;
            _readerTocMinimal = false;
            ApplyReaderPanelLayout();
            return;
        }
        if (_readerTocMinimal)
        {
            _readerTocMinimal = false;
            _readerTocExpanded = true;
        }
        else
        {
            _readerTocExpanded = !_readerTocExpanded;
        }
        ApplyReaderPanelLayout();
        if (_readerTocExpanded)
        {
            ShowReaderTocTab();
            SetReaderTocSelectionForLocation(_readerChapterIndex, _readerCurrentFragment);
        }
    }

    private void ReaderTocList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressReaderTocSelectionNavigation) return;
        if (e.AddedItems.Count > 0 && e.AddedItems[0] is EpubReaderNavigationItem item)
            _ = ObserveReaderTaskAsync(
                NavigateToReaderItemAsync(
                    item,
                    _readerSessionCancellation?.Token ?? CancellationToken.None,
                    ReaderNavigationIntent.Toc));
    }

    private async void ReaderSearchButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (ReaderInPageSearchBar.IsVisible)
            {
                await ClearReaderSearchAsync();
                ReaderInPageSearchBar.IsVisible = false;
                ReaderInPageSearchBox.Text = string.Empty;
                return;
            }
            OpenReaderSearchShortcut();
        }
        catch (OperationCanceledException) when (ReaderToken.IsCancellationRequested)
        {
        }
    }

    private async void ReaderFlowModeItem_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag }) return;
        if (_readerIsPdf)
        {
            ReaderStatusText.Text = T("PDF 使用页面模式，可用底部进度条或左右按钮翻页。");
            return;
        }

        var requiredVerticalMode = "single";
        if (_readerLayout.VerticalWriting
            && !string.Equals(tag, requiredVerticalMode, StringComparison.Ordinal))
        {
            SyncReaderFlowMenu();
            ShowReaderTransientStatus(T("竖排模式仅支持单页阅读。关闭竖排后可选择滚动或双栏。"));
            return;
        }
        var flowMode = tag switch
        {
            "scroll" => 0,
            "double" => 1,
            _ => 1
        };
        var twoPage = string.Equals(tag, "double", StringComparison.Ordinal);
        _readerLayout = NormalizeReaderLayoutForPlatform(_readerLayout with
        {
            FlowMode = flowMode,
            TwoPageMode = twoPage
        });
        SyncReaderFlowMenu();
        await ApplyReaderLayoutToHostsAsync(_readerSessionCancellation?.Token ?? CancellationToken.None);
        await SaveReaderLayoutAsync(CancellationToken.None);
        ShowReaderTransientStatus(
            _readerLayout.FlowMode == 0 ? T("已切换为滚动阅读。") : _readerLayout.TwoPageMode ? T("已切换为双栏阅读。") : T("已切换为单页阅读。"));
    }

    private void SyncReaderFlowMenu()
    {
        if (ReaderScrollModeItem is null || ReaderSinglePageModeItem is null || ReaderTwoPageModeItem is null) return;
        var flowMode = _readerLayout.FlowMode;
        var twoPage = _readerLayout.TwoPageMode;
        var vertical = _readerLayout.VerticalWriting;
        if (vertical)
        {
            // Vertical writing always presents as single pages.
            flowMode = 1;
            twoPage = false;
        }

        ReaderScrollModeItem.IsChecked = flowMode == 0;
        ReaderSinglePageModeItem.IsChecked = flowMode == 1 && !twoPage;
        ReaderTwoPageModeItem.IsChecked = flowMode == 1 && twoPage;
        // The native engine now implements continuous scroll and the
        // two-column spread for horizontal writing; only vertical writing is
        // restricted to single pages.
        ReaderScrollModeItem.IsEnabled = !vertical;
        ReaderTwoPageModeItem.IsEnabled = !vertical;
        ReaderSinglePageModeItem.IsEnabled = true;
        if (ReaderFlowButton is not null)
            ReaderFlowButton.Content = flowMode == 0 ? T("滚动") : twoPage ? T("双栏") : T("单页");
    }

    private void ReaderZenMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        ReaderMoreButton.Flyout?.Hide();
        ToggleReaderZenMode();
    }

    private void ReaderLayoutSettingsMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        ReaderMoreButton.Flyout?.Hide();
        ReaderLayoutSettingsButton_Click(sender, e);
    }

    private void ReaderAnimationItem_Click(object? sender, RoutedEventArgs e)
    {
        // MenuFlyout can route Click through a generated MenuItem instance;
        // comparing the sender by object identity therefore loses the user's
        // choice and silently falls back to "无动画". The Tag is the stable
        // value declared in XAML and survives that routing behavior.
        var tag = (sender as MenuItem)?.Tag as string;
        _readerPageAnimation = tag switch
        {
            "fade" => ReaderAnimationFade,
            "slide" => ReaderAnimationSlide,
            "wave" => ReaderAnimationWave,
            "none" => ReaderAnimationNone,
            _ => _readerPageAnimation
        };
        SyncReaderAnimationMenu();
        ReaderMoreButton.Flyout?.Hide();
        ShowReaderTransientStatus(_readerPageAnimation switch
        {
            ReaderAnimationFade => T("翻页动画：淡入淡出"),
            ReaderAnimationSlide => T("翻页动画：左右滑动"),
            ReaderAnimationWave => T("翻页动画：电子墨水刷新"),
            _ => T("翻页动画：无动画")
        });
    }

    private void SyncReaderAnimationMenu()
    {
        if (ReaderAnimationNoneItem is null
            || ReaderAnimationFadeItem is null
            || ReaderAnimationSlideItem is null
            || ReaderAnimationWaveItem is null)
        {
            return;
        }
        ReaderAnimationNoneItem.IsChecked = _readerPageAnimation == ReaderAnimationNone;
        ReaderAnimationFadeItem.IsChecked = _readerPageAnimation == ReaderAnimationFade;
        ReaderAnimationSlideItem.IsChecked = _readerPageAnimation == ReaderAnimationSlide;
        ReaderAnimationWaveItem.IsChecked = _readerPageAnimation == ReaderAnimationWave;
    }

    private async void ReaderDecreaseFontButton_Click(object? sender, RoutedEventArgs e) =>
        await ChangeReaderFontAsync(-0.1);

    private async void ReaderIncreaseFontButton_Click(object? sender, RoutedEventArgs e) =>
        await ChangeReaderFontAsync(0.1);

    private async Task ChangeReaderFontAsync(double delta)
    {
        _readerLayout = NormalizeReaderLayoutForPlatform(_readerLayout with { FontScale = _readerLayout.FontScale + delta });
        UpdateReaderZoomLabel();
        await ApplyReaderLayoutToHostsAsync(_readerSessionCancellation?.Token ?? CancellationToken.None);
        await SaveReaderLayoutAsync(CancellationToken.None);
    }

    private void ReaderBookmarkButton_Click(object? sender, RoutedEventArgs e)
    {
        ShowReaderBookmarkTab();
    }

    private async void ReaderBookmarkCornerButton_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        e.Handled = true;
        await ToggleReaderBookmarkAsync();
    }

    private void UpdateReaderBookmarkCornerSurface()
    {
        // The corner click zone toggles a bookmark at the current position
        // for every reader surface: the native EPUB engine has no injected
        // page to draw the ribbon, so the Avalonia corner stays active here.
        ReaderBookmarkCornerButton.IsVisible = true;
        ReaderWebViewHost.Margin = new Thickness(
            0,
            _readerIsPdf ? 34 : 12,
            2,
            _readerZenMode ? 0 : 10);
    }

    private async void ToggleReaderZenMode()
    {
        if (!_readerZenMode)
        {
            _readerWindowStateBeforeZen = WindowState;
            _readerAssistantVisibleBeforeZen = ReaderAssistantPanel.IsVisible;
            _readerTocExpandedBeforeZen = ReaderTocPanel.IsVisible;
            _readerTocMinimalBeforeZen = ReaderTocCompactPanel.IsVisible;
            WindowState = WindowState.FullScreen;
            _readerZenMode = true;
            ReaderAssistantPanel.IsVisible = false;
            ReaderRoot.ColumnDefinitions[2].Width = new GridLength(0);
            ReaderAssistantToggleButton.IsVisible = false;
            ReaderZenBar.IsVisible = false;
            if (ReaderZenMenuItem is not null) ReaderZenMenuItem.IsChecked = true;
            // Zen mode starts with the minimal TOC rail, matching the WinUI
            // reference: the full TOC panel is collapsed and only the 52-DIP
            // marker rail keeps the chapter map visible. The content header and
            // footer bars collapse so the body fills the whole reading area.
            _readerTocExpanded = false;
            _readerTocMinimal = true;
            ReaderContentPanel.RowDefinitions[0].Height = new GridLength(0);
            ReaderHeaderBar.IsVisible = false;
            ReaderContentPanel.RowDefinitions[2].Height = new GridLength(0);
            ReaderFooterBar.IsVisible = false;
            ReaderTocPanel.Margin = new Thickness(0);
            ReaderTocCompactPanel.Margin = new Thickness(0);
            ReaderContentPanel.Margin = new Thickness(0);
            ReaderAssistantPanel.Margin = new Thickness(0);
            ReaderWebViewBottomCover.Margin = new Thickness(0);
            UpdateReaderBookmarkCornerSurface();
            ApplyReaderPanelLayout();
            UpdateReaderZenTocToggle();
            // The old reader enters distraction-free mode immediately. Mouse
            // movement reveals the title controls again when they are needed.
            UpdateReaderZenChrome(visible: false);
            StartReaderZenPointerWatch();
        }
        else
        {
            await ExitReaderZenModeSmoothlyAsync();
        }
    }

    // Leaving zen restores the side panels, header/footer bars and the window
    // size in one go, which makes the paginated body text reflow and jump.
    // Mask that behind an opaque cover, restore everything, let the relayout
    // settle, then fade the cover away (WinUI reference behavior).
    private async Task ExitReaderZenModeSmoothlyAsync()
    {
        try
        {
            ReaderTransitionCover.Opacity = 1;
            ExitReaderZenModeCore();
            await Task.Delay(320);
            await FadeReaderTransitionCoverAsync(1, 0, 180);
        }
        catch
        {
            ReaderTransitionCover.Opacity = 0;
        }
    }

    private async Task FadeReaderTransitionCoverAsync(double from, double to, int durationMs)
    {
        try
        {
            var animation = new Animation
            {
                Duration = TimeSpan.FromMilliseconds(durationMs),
                FillMode = Avalonia.Animation.FillMode.Forward
            };
            var frame = new KeyFrame { Cue = new Cue(1d) };
            frame.Setters.Add(new Avalonia.Styling.Setter(Border.OpacityProperty, to));
            animation.Children.Add(frame);
            ReaderTransitionCover.Opacity = from;
            await animation.RunAsync(ReaderTransitionCover);
        }
        catch
        {
        }
        ReaderTransitionCover.Opacity = to;
    }

    private void ExitReaderZenMode()
    {
        if (!_readerZenMode) return;
        ReaderTransitionCover.Opacity = 0;
        ExitReaderZenModeCore();
    }

    private void ExitReaderZenModeCore()
    {
        if (!_readerZenMode) return;
        WindowState = _readerWindowStateBeforeZen;
        _readerZenMode = false;
        ReaderTocPanel.IsVisible = false;
        ReaderAssistantPanel.IsVisible = _readerAssistantVisibleBeforeZen;
        ReaderRoot.ColumnDefinitions[2].Width = _readerAssistantVisibleBeforeZen
            ? new GridLength(360)
            : new GridLength(0);
        ReaderAssistantToggleButton.IsVisible = true;
        ReaderZenBar.IsVisible = false;
        if (ReaderZenMenuItem is not null) ReaderZenMenuItem.IsChecked = false;
        _readerTocExpanded = _readerTocExpandedBeforeZen;
        _readerTocMinimal = _readerTocMinimalBeforeZen;
        ReaderContentPanel.RowDefinitions[0].Height = new GridLength(52);
        ReaderHeaderBar.IsVisible = true;
        ReaderContentPanel.RowDefinitions[2].Height = new GridLength(50);
        ReaderFooterBar.IsVisible = true;
        ReaderTocPanel.Margin = new Thickness(0, 38, 0, 0);
        ReaderTocCompactPanel.Margin = new Thickness(0, 38, 0, 0);
        ReaderContentPanel.Margin = new Thickness(0, 38, 0, 0);
        ReaderAssistantPanel.Margin = new Thickness(0, 38, 0, 0);
        ReaderLayoutSettingsOverlay.Margin = new Thickness(0);
        ReaderWebViewBottomCover.Margin = new Thickness(0, 0, 0, 10);
        UpdateReaderBookmarkCornerSurface();
        ApplyReaderPanelLayout();
        UpdateReaderZenTocToggle();
        // Leaving zen restores the chrome unconditionally; the hide timer must
        // not keep running for the bookshelf.
        _readerZenChromeHideTimer?.Stop();
        _readerZenChromeVisible = true;
        ReaderZenTitleTocButton.IsVisible = false;
        ReaderZenTitleExitButton.IsVisible = false;
        ReaderZenActivationRegion.IsVisible = false;
        ReaderZenControlsPopup.IsOpen = false;
        _readerZenPointerWatchTimer?.Stop();
        MinimizeWindowButton.IsVisible = true;
        MaximizeWindowButton.IsVisible = true;
        CloseWindowButton.IsVisible = true;
        WindowBrandText.IsVisible = ReaderRoot.IsVisible;
    }

    // Zen mode auto-hides the top chrome (brand text, zen title buttons and the
    // window caption buttons) so only the body remains; the minimal TOC rail on
    // the left is not part of this chrome and stays visible. Mouse movement
    // reveals it again, and it hides after ~2.5 s of inactivity.
    private bool _readerZenChromeVisible = true;
    private DispatcherTimer? _readerZenChromeHideTimer;
    private DispatcherTimer? _readerZenPointerWatchTimer;
    private long _readerZenLastMouseMoveTick;

    [StructLayout(LayoutKind.Sequential)]
    private struct ReaderNativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ReaderNativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out ReaderNativePoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out ReaderNativeRect rect);

    [DllImport("libX11.so.6")]
    private static extern IntPtr XOpenDisplay(IntPtr displayName);

    [DllImport("libX11.so.6")]
    private static extern IntPtr XDefaultRootWindow(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern int XQueryPointer(
        IntPtr display,
        IntPtr window,
        out IntPtr root,
        out IntPtr child,
        out int rootX,
        out int rootY,
        out int windowX,
        out int windowY,
        out uint mask);

    private static IntPtr _readerX11Display;

    private static bool TryGetReaderCursorScreenPoint(out PixelPoint point)
    {
        point = default;
        if (OperatingSystem.IsWindows())
        {
            if (!GetCursorPos(out var cursor)) return false;
            point = new PixelPoint(cursor.X, cursor.Y);
            return true;
        }

        if (!OperatingSystem.IsLinux()) return false;
        try
        {
            if (_readerX11Display == IntPtr.Zero)
                _readerX11Display = XOpenDisplay(IntPtr.Zero);
            if (_readerX11Display == IntPtr.Zero) return false;
            var rootWindow = XDefaultRootWindow(_readerX11Display);
            if (rootWindow == IntPtr.Zero
                || XQueryPointer(
                    _readerX11Display,
                    rootWindow,
                    out _,
                    out _,
                    out var rootX,
                    out var rootY,
                    out _,
                    out _,
                    out _) == 0)
                return false;
            point = new PixelPoint(rootX, rootY);
            return true;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    private void StartReaderZenPointerWatch()
    {
        if (!OperatingSystem.IsWindows()) return;
        _readerZenPointerWatchTimer ??= new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(80)
        };
        _readerZenPointerWatchTimer.Stop();
        _readerZenPointerWatchTimer.Tick -= ReaderZenPointerWatchTimer_Tick;
        _readerZenPointerWatchTimer.Tick += ReaderZenPointerWatchTimer_Tick;
        _readerZenPointerWatchTimer.Start();
    }

    private void ReaderZenPointerWatchTimer_Tick(object? sender, EventArgs e)
    {
        if (!_readerZenMode || !OperatingSystem.IsWindows())
        {
            _readerZenPointerWatchTimer?.Stop();
            return;
        }

        var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero
            || !GetCursorPos(out var cursor)
            || !GetWindowRect(handle, out var window))
        {
            return;
        }

        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1d;
        var activationWidth = ReaderZenActivationWidth * scaling;
        var activationHeight = ReaderZenActivationHeight * scaling;
        var insideTopRight = cursor.X >= window.Right - activationWidth
            && cursor.X <= window.Right
            && cursor.Y >= window.Top
            && cursor.Y <= window.Top + activationHeight;
        if (insideTopRight != _readerZenChromeVisible)
            UpdateReaderZenChrome(insideTopRight);
    }

    private void ReaderRoot_PointerMoved(object? sender, PointerEventArgs e)
    {
        // Windows uses the native screen-space watcher above for both the
        // Avalonia surface and the child WebView HWND. Mixing its physical
        // pixels with these DIP coordinates makes the two paths fight at
        // non-100% display scaling and repeatedly opens/closes the popup.
        if (!_readerZenMode || OperatingSystem.IsWindows()) return;
        var now = Environment.TickCount64;
        if (now - _readerZenLastMouseMoveTick <= 80) return;
        _readerZenLastMouseMoveTick = now;
        var point = e.GetPosition(ReaderRoot);
        UpdateReaderZenChromeForPointer(point.X, point.Y, ReaderRoot.Bounds.Width);
    }

    private void UpdateReaderZenChromeForPointer(double x, double y, double surfaceWidth)
    {
        if (!_readerZenMode) return;

        var insideTopRight = y >= 0
            && y <= ReaderZenActivationHeight
            && x >= Math.Max(0, surfaceWidth - ReaderZenActivationWidth);
        if (insideTopRight)
        {
            if (!_readerZenChromeVisible)
                UpdateReaderZenChrome(visible: true);
            else
                RestartReaderZenChromeHideTimer();
        }
        else if (_readerZenChromeVisible)
        {
            UpdateReaderZenChrome(visible: false);
        }
    }

    private void ReaderTitleControls_PointerEntered(object? sender, PointerEventArgs e)
    {
        if (!_readerZenMode) return;
        if (!_readerZenChromeVisible)
            UpdateReaderZenChrome(visible: true);
        _readerZenChromeHideTimer?.Stop();
    }

    private void ReaderTitleControls_PointerExited(object? sender, PointerEventArgs e)
    {
        // On Windows the native watcher closes the popup as soon as the
        // pointer leaves the complete activation region. A delayed close from
        // the smaller button row can otherwise race that watcher and flicker.
        if (_readerZenMode && !OperatingSystem.IsWindows())
            RestartReaderZenChromeHideTimer(500);
    }

    private void RestartReaderZenChromeHideTimer(int delayMs = 1200)
    {
        _readerZenChromeHideTimer ??= new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(delayMs)
        };
        _readerZenChromeHideTimer.Stop();
        _readerZenChromeHideTimer.Interval = TimeSpan.FromMilliseconds(delayMs);
        _readerZenChromeHideTimer.Tick -= ReaderZenChromeHideTimer_Tick;
        _readerZenChromeHideTimer.Tick += ReaderZenChromeHideTimer_Tick;
        _readerZenChromeHideTimer.Start();
    }

    private void ReaderZenChromeHideTimer_Tick(object? sender, EventArgs e)
    {
        _readerZenChromeHideTimer?.Stop();
        if (_readerZenMode) UpdateReaderZenChrome(visible: false);
    }

    private void UpdateReaderZenChrome(bool visible)
    {
        _readerZenChromeVisible = visible;
        ReaderZenActivationRegion.IsVisible = _readerZenMode;
        ReaderZenTitleTocButton.IsVisible = false;
        ReaderZenTitleExitButton.IsVisible = false;
        MinimizeWindowButton.IsVisible = visible;
        MaximizeWindowButton.IsVisible = visible;
        CloseWindowButton.IsVisible = visible;
        if (_readerZenMode && visible)
        {
            ReaderZenControlsPopup.PlacementTarget = ReaderZenActivationRegion;
            ReaderZenControlsPopup.Placement = Avalonia.Controls.PlacementMode.AnchorAndGravity;
            ReaderZenControlsPopup.PlacementAnchor = Avalonia.Controls.Primitives.PopupPositioning.PopupAnchor.TopRight;
            ReaderZenControlsPopup.PlacementGravity = Avalonia.Controls.Primitives.PopupPositioning.PopupGravity.BottomLeft;
            ReaderZenControlsPopup.HorizontalOffset = 0;
            ReaderZenControlsPopup.VerticalOffset = 0;
            ReaderZenControlsPopup.IsOpen = true;
        }
        else
        {
            ReaderZenControlsPopup.IsOpen = false;
        }
        // The brand text floats over the minimal TOC rail in zen mode, so it
        // stays hidden there; it is restored when returning to the bookshelf.
        WindowBrandText.IsVisible = !_readerZenMode
            && visible
            && ReaderRoot.IsVisible;

        if (visible && !OperatingSystem.IsWindows())
            RestartReaderZenChromeHideTimer();
        else
            _readerZenChromeHideTimer?.Stop();
    }

    private void UpdateReaderToolbar()
    {
        if (ReaderFlowButton is not null)
        {
            ReaderFlowButton.Content = _readerIsPdf
                ? T("PDF 页")
                : _readerLayout.FlowMode == 0
                ? T("滚动")
                : _readerLayout.TwoPageMode ? T("双栏") : T("单页");
            // The WinUI reference hides the flow selector entirely for PDF.
            ReaderFlowButton.IsVisible = !_readerIsPdf;
        }
        SyncReaderFlowMenu();
        SyncReaderAnimationMenu();
        if (ReaderZoomText is not null)
            ReaderZoomText.Text = $"{_readerLayout.FontScale:P0}";
        if (ReaderProgressPercentText is not null)
            ReaderProgressPercentText.Text = $"{CalculateReaderProgressPercent():0}%";
        if (ReaderReadingProgressText is not null)
            ReaderReadingProgressText.Text = GetReaderReadingProgressLabel();
        if (ReaderProgressSlider is not null)
        {
            _readerProgressSliderUpdating = true;
            if (_readerIsPdf)
            {
                var pageCount = Math.Max(1, _readerPdfPages.Count);
                ReaderProgressSlider.Minimum = 1;
                ReaderProgressSlider.Maximum = pageCount;
                ReaderProgressSlider.Value = Math.Clamp(_readerPdfPage, 1, pageCount);
            }
            else if (_readerTocItems.Count > 0)
            {
                ReaderProgressSlider.Minimum = 1;
                ReaderProgressSlider.Maximum = _readerTocItems.Count;
                ReaderProgressSlider.Value = Math.Clamp(GetCurrentReaderTocIndex() + 1, 1, _readerTocItems.Count);
            }
            else
            {
                ReaderProgressSlider.Minimum = 1;
                ReaderProgressSlider.Maximum = 1;
                ReaderProgressSlider.Value = 1;
            }
            _readerProgressSliderUpdating = false;
        }
        // PDF hides the zoom controls and shows the PDF badge, matching the
        // WinUI reference toolbar states; chapter buttons disable at the edges.
        if (ReaderZoomOutButton is not null && ReaderZoomText is not null && ReaderZoomInButton is not null)
        {
            ReaderZoomOutButton.IsVisible = !_readerIsPdf;
            ReaderZoomText.IsVisible = !_readerIsPdf;
            ReaderZoomInButton.IsVisible = !_readerIsPdf;
        }
        if (ReaderPdfBadge is not null)
            ReaderPdfBadge.IsVisible = _readerIsPdf;
        if (ReaderPreviousButton is not null)
            ReaderPreviousButton.IsEnabled = _readerIsPdf
                ? _readerPdfPage > 1
                : GetCurrentReaderTocIndex() > 0;
        if (ReaderNextButton is not null)
            ReaderNextButton.IsEnabled = _readerIsPdf
                ? _readerPdfPage < Math.Max(1, _readerPdfPages.Count)
                : GetCurrentReaderTocIndex() is var tocIndex
                    && tocIndex >= 0
                    && tocIndex + 1 < _readerTocItems.Count;
        UpdateReaderSearchCount();
    }

    private string GetReaderReadingProgressLabel()
    {
        var (currentPage, totalPages) = GetReaderPagePosition();
        return T("已读 {0} / {1} 页", currentPage, totalPages);
    }

    private (double Width, double Height) GetReaderPageCountMeasure()
    {
        var width = _readerClientWidth > 0
            ? _readerClientWidth
            : ReaderWebViewHost.Bounds.Width;
        var height = _readerClientHeight > 0
            ? _readerClientHeight
            : ReaderWebViewHost.Bounds.Height;
        if (IsLinuxReaderTextFallbackActive())
        {
            if (ReaderLinuxTextFallbackOverlay.Bounds.Width > 0)
                width = ReaderLinuxTextFallbackOverlay.Bounds.Width;
            if (ReaderLinuxTextFallbackOverlay.Bounds.Height > 0)
                height = ReaderLinuxTextFallbackOverlay.Bounds.Height;
        }

        // A native WebView can report zero bounds for a few compositor frames
        // while it is being attached. Do not turn that transient state into a
        // book with thousands of tiny pages; use the normal reader surface's
        // minimum usable geometry until the host reports its real viewport.
        if (width < 640)
        {
            var rootWidth = ReaderRoot.Bounds.Width;
            width = rootWidth >= 640
                ? Math.Min(_readerLayout.MaxWidth, Math.Max(640, rootWidth - 320))
                : 960;
        }
        if (height < 360)
        {
            var rootHeight = ReaderRoot.Bounds.Height;
            height = rootHeight >= 360 ? Math.Max(360, rootHeight - 100) : 600;
        }

        return (
            Math.Max(320, double.IsFinite(width) ? width : 0),
            Math.Max(240, double.IsFinite(height) ? height : 0));
    }

    private void ScheduleReaderBookPageCountRefresh()
    {
        if (_readerIsPdf
            || _readerDocument is not { Chapters.Count: > 0 } document)
            return;

        var (width, height) = GetReaderPageCountMeasure();
        if (width <= 0 || height <= 0) return;

        _readerBookPageCountCancellation?.Cancel();
        _readerBookPageCountCancellation?.Dispose();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _readerSessionCancellation?.Token ?? _lifetimeCancellation.Token);
        _readerBookPageCountCancellation = cancellation;
        var sequence = ++_readerBookPageCountSequence;
        var layout = _readerLayout;
        _ = ObserveReaderTaskAsync(
            RefreshReaderBookPageCountsAsync(
                document,
                layout,
                width,
                height,
                sequence,
                cancellation));
    }

    private async Task RefreshReaderBookPageCountsAsync(
        EpubReaderDocument document,
        ReaderLayoutSettings layout,
        double viewportWidth,
        double viewportHeight,
        int sequence,
        CancellationTokenSource cancellation)
    {
        try
        {
            var useNativeLayout = CurrentReaderHost is NativeReaderHost;
            var counts = await Task.Run(
                () => useNativeLayout
                    ? NativeReaderHost.EstimatePageCounts(
                        document.Chapters,
                        layout,
                        viewportWidth,
                        viewportHeight,
                        cancellation.Token)
                    : document.Chapters
                        .Select(path => EstimateReaderChapterPageCount(
                            path,
                            layout,
                            viewportWidth,
                            viewportHeight))
                        .Select(count => Math.Max(1, count))
                        .ToArray(),
                cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (sequence != _readerBookPageCountSequence
                    || !ReferenceEquals(document, _readerDocument)
                    || cancellation.IsCancellationRequested)
                    return;
                _readerBookPageCounts = counts;
                UpdateReaderToolbar();
            });
        }
        finally
        {
            if (ReferenceEquals(_readerBookPageCountCancellation, cancellation))
            {
                _readerBookPageCountCancellation = null;
                cancellation.Dispose();
            }
        }
    }

    private static int EstimateReaderChapterPageCount(
        string path,
        ReaderLayoutSettings layout,
        double viewportWidth,
        double viewportHeight)
    {
        if (!File.Exists(path)) return 1;

        var fontSize = Math.Max(10, 16d * layout.FontScale);
        var lineHeight = Math.Max(fontSize + 2, fontSize * layout.LineHeight);
        var pageInsets = layout.VerticalWriting
            ? ReaderPlatformLayoutPolicy.GetVerticalPageInsets(
                viewportWidth,
                viewportHeight,
                layout.BodyPadding,
                layout.MaxWidth)
            : (Horizontal: Math.Max(0, layout.BodyPadding), Vertical: Math.Max(0, layout.BodyPadding));
        var availablePageWidth = Math.Max(1, viewportWidth - pageInsets.Horizontal * 2);
        var pageWidth = layout.TwoPageMode
            ? Math.Max(180, (availablePageWidth - 28) / 2)
            : Math.Max(240, Math.Min(layout.MaxWidth, availablePageWidth));
        var pageHeight = Math.Max(180, viewportHeight - pageInsets.Vertical * 2);
        var linesPerPage = Math.Max(4, (int)Math.Floor(pageHeight / lineHeight));
        var lineUnits = Math.Max(8, pageWidth / fontSize);
        var content = ExtractReaderFallbackContent(path);
        var text = new StringBuilder();
        var pages = 0;

        void FlushText()
        {
            if (text.Length == 0) return;
            pages += PaginateReaderPlainText(text.ToString(), lineUnits, linesPerPage).Count;
            text.Clear();
        }

        foreach (var block in content.Blocks)
        {
            if (!string.IsNullOrWhiteSpace(block.ImagePath))
            {
                FlushText();
                pages++;
                continue;
            }

            if (!string.IsNullOrWhiteSpace(block.FootnoteHref))
            {
                text.Append(ReaderLinuxTextFallbackFootnoteMarker);
                continue;
            }

            var blockText = NormalizeReaderPlainText(block.Text ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(blockText))
                text.Append(blockText);
        }

        if (content.Blocks.Count == 0 && !string.IsNullOrWhiteSpace(content.Text))
            text.Append(NormalizeReaderPlainText(content.Text));

        FlushText();
        return Math.Max(1, pages);
    }

    private (int CurrentPage, int TotalPages) GetReaderPagePosition()
    {
        var local = GetReaderChapterPagePosition();
        if (_readerDocument is not { Chapters.Count: > 0 } document
            || _readerBookPageCounts.Length != document.Chapters.Count
            || _readerIsPdf)
            return local;

        if (_readerChapterIndex >= 0 && _readerChapterIndex < _readerBookPageCounts.Length)
            _readerBookPageCounts[_readerChapterIndex] = Math.Max(
                1,
                Math.Max(_readerBookPageCounts[_readerChapterIndex], local.TotalPages));

        var totalPages = _readerBookPageCounts.Sum(count => Math.Max(1, count));
        var prefixPages = _readerBookPageCounts
            .Take(Math.Clamp(_readerChapterIndex, 0, _readerBookPageCounts.Length))
            .Sum(count => Math.Max(1, count));
        return (
            Math.Clamp(prefixPages + local.CurrentPage, 1, Math.Max(1, totalPages)),
            Math.Max(1, totalPages));
    }

    private (int CurrentPage, int TotalPages) GetReaderChapterPagePosition()
    {
        if (_readerIsPdf)
            return (
                Math.Clamp(_readerPdfPage, 1, Math.Max(1, _readerPdfPages.Count)),
                Math.Max(1, _readerPdfPages.Count));

        if (_readerLayout.FlowMode == 1 && IsLinuxReaderTextFallbackActive())
        {
            var pageCount = GetLinuxReaderTextFallbackPageCount();
            var pageIndex = _readerLinuxTextFallbackPageIndex < 0
                ? pageCount - 1
                : _readerLinuxTextFallbackPageIndex;
            return (Math.Clamp(pageIndex + 1, 1, pageCount), pageCount);
        }

        if (CurrentReaderHost is NativeReaderHost nativeReader)
        {
            var pageCount = Math.Max(1, nativeReader.PageCount);
            return (
                Math.Clamp(nativeReader.CurrentPage + 1, 1, pageCount),
                pageCount);
        }

        var horizontal = _readerLayout.FlowMode == 1 || _readerLayout.VerticalWriting;
        var viewport = horizontal ? _readerClientWidth : _readerClientHeight;
        var extent = horizontal ? _readerScrollWidth : _readerScrollHeight;
        if (viewport <= 0 || extent <= 0)
            return (1, 1);

        var position = Math.Max(0, _readerScrollPosition);
        if (_readerLayout.FlowMode == 1)
        {
            // Paginated WebView content is snapped to one viewport per page.
            // Natural vertical flow may end with a partial viewport, whereas
            // horizontal multicol extends to a rounded logical boundary.
            var maximum = Math.Max(0, extent - viewport);
            var viewCount = _readerLayout.VerticalWriting
                ? Math.Max(1, (int)Math.Ceiling(extent / viewport))
                : Math.Max(1, (int)Math.Round(maximum / viewport) + 1);
            var currentView = _readerLayout.VerticalWriting && position >= maximum - 4
                ? viewCount
                : Math.Clamp(
                    (int)Math.Round(position / viewport) + 1,
                    1,
                    viewCount);
            var pagesPerView = _readerLayout.TwoPageMode ? 2 : 1;
            return (
                Math.Clamp((currentView - 1) * pagesPerView + 1, 1, viewCount * pagesPerView),
                viewCount * pagesPerView);
        }

        var totalPages = Math.Max(1, (int)Math.Ceiling(extent / viewport));
        var currentPage = Math.Clamp((int)Math.Floor(position / viewport) + 1, 1, totalPages);
        return (currentPage, totalPages);
    }

    private void UpdateReaderSearchCount()
    {
        var text = _readerSearchCount <= 0
            ? "0/0"
            : $"{_readerSearchIndex + 1}/{_readerSearchCount}";
        ReaderInPageSearchCountText.Text = text;
    }

    private int FindReaderTocIndex(EpubReaderNavigationItem item)
    {
        for (var index = 0; index < _readerTocItems.Count; index++)
            if (ReferenceEquals(_readerTocItems[index], item) || _readerTocItems[index] == item) return index;
        return -1;
    }

    private int FindReaderTocIndexForChapter(int chapterIndex) =>
        _readerTocItems.FirstOrDefault(item => item.ChapterIndex == chapterIndex) is { } item
            ? FindReaderTocIndex(item)
            : -1;

    private string? GetReaderChapterPath(IReaderHost? host = null)
    {
        if (_readerDocument is null) return null;
        if (host?.Source is { IsFile: true } source)
        {
            var sourcePath = Path.GetFullPath(source.LocalPath);
            if (IsPathInside(_readerDocument.RootPath, sourcePath))
            {
                return Path.GetRelativePath(_readerDocument.RootPath, sourcePath)
                    .Replace('\\', '/');
            }
        }
        if (_readerChapterIndex < 0 || _readerChapterIndex >= _readerDocument.Chapters.Count)
            return null;
        return Path.GetRelativePath(
                _readerDocument.RootPath,
                _readerDocument.Chapters[_readerChapterIndex])
            .Replace('\\', '/');
    }

    private double CalculateReaderProgressPercent()
    {
        if (_readerIsPdf)
        {
            if (_readerPdfPages.Count <= 1) return _readerPdfPages.Count == 0 ? 0 : 100;
            return Math.Clamp((_readerPdfPage - 1d) * 100d / (_readerPdfPages.Count - 1d), 0, 100);
        }
        if (_readerDocument is null || _readerDocument.Chapters.Count == 0) return 0;
        return Math.Clamp(
            (_readerChapterIndex + _readerScrollRatio) * 100d / _readerDocument.Chapters.Count,
            0,
            100);
    }

    private static double ReadDouble(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) && element.TryGetDouble(out var value) && double.IsFinite(value)
            ? value
            : 0;

    private static int ReadInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) && element.TryGetInt32(out var value)
            ? Math.Max(0, value)
            : 0;

    private static string ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? string.Empty
            : string.Empty;

    private static string NormalizeReaderAnnotationColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "#000000";
        var normalized = value.Trim();
        if (normalized.Length != 7 || normalized[0] != '#') return "#000000";
        return int.TryParse(
            normalized.AsSpan(1),
            NumberStyles.HexNumber,
            CultureInfo.InvariantCulture,
            out _)
                ? normalized.ToUpperInvariant()
                : "#000000";
    }

    private static int ParseScriptInt(string? result)
    {
        if (string.IsNullOrWhiteSpace(result)) return 0;
        try
        {
            using var json = JsonDocument.Parse(result);
            var element = json.RootElement;
            if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var number)) return number;
            if (element.ValueKind == JsonValueKind.String
                && int.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)) return number;
        }
        catch (JsonException)
        {
        }
        return int.TryParse(result.Trim().Trim('"'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var fallback)
            ? fallback
            : 0;
    }

    private static string EscapeJavaScriptSingleQuoted(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);

    private static string EscapeCssString(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("{", string.Empty, StringComparison.Ordinal)
            .Replace("}", string.Empty, StringComparison.Ordinal)
            .Replace(";", string.Empty, StringComparison.Ordinal);

    private static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
