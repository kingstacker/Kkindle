using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Kkindle.Infrastructure;

public sealed record EpubReaderNavigationItem(string Title, string Target, int ChapterIndex);

public sealed record EpubReaderDocument(
    string RootPath,
    IReadOnlyList<string> Chapters,
    IReadOnlyList<EpubReaderNavigationItem> Navigation,
    IReadOnlyList<string> ChapterTitles);

public sealed class EpubReaderPreparationService
{
    private const string ExtractionReadyFileName = ".kkindle-extracted";
    // Bump whenever sanitization or the injected bridge changes. Existing
    // reader caches otherwise keep the old JavaScript indefinitely.
    private const string ExtractionFormatVersion = "56";
    private const string ReaderBridgeFileName = ".kkindle-reader-bridge.js";
    private const string ContentSecurityPolicyBase =
        "default-src 'none'; base-uri 'none'; object-src 'none'; frame-src 'none'; " +
        "connect-src 'none'; form-action 'none'; img-src 'self' file:; " +
        "font-src 'self' file: data:; style-src 'self' 'unsafe-inline' file:; " +
        "media-src 'none'; worker-src 'none'; frame-ancestors 'none';";
    private const string ReaderBridgeScript = """
        (() => {
          if (window.__kkindleReaderBridgeInstalled) return;
          window.__kkindleReaderBridgeInstalled = true;

          const send = value => {
            try {
              const body = JSON.stringify(value);
              const webview = window.chrome && window.chrome.webview;
              if (webview && typeof webview.postMessage === "function")
                webview.postMessage(body);
              else {
                const webkit = window.webkit && window.webkit.messageHandlers;
                const handler = webkit && webkit.postAvWebViewMessage;
                if (handler && typeof handler.postMessage === "function")
                  handler.postMessage(body);
                else if (typeof window.invokeCSharpAction === "function")
                  window.invokeCSharpAction(body);
              }
            } catch (_) { }
          };

          // Mark short numeric runs while the XHTML is still entering the
          // document. The reader later supplies the vertical-writing CSS, but
          // WebKitGTK can retain the first painted text run when the DOM is
          // rewritten only after NavigationCompleted. Keeping the markup
          // stable from DOMContentLoaded makes the CSS pass deterministic.
          const prepareVerticalInlineRuns = () => {
            const body = document.body;
            if (!body) return false;

            const numericTokenPattern = /[0-9]+(?:[.,:/+\-–—][0-9]+|%|°[CF])*/g;
            const walker = document.createTreeWalker(body, NodeFilter.SHOW_TEXT);
            const textNodes = [];
            for (let node = walker.nextNode(); node; node = walker.nextNode()) {
              const parent = node.parentElement;
              if (!parent
                  || parent.closest('#kkindle-selection-bar, script, style, noscript, ruby, rt, [data-kkindle-vertical-run="1"]')
                  || !/[0-9]/.test(node.nodeValue || ''))
                continue;
              textNodes.push(node);
            }

            for (const node of textNodes) {
              const value = node.nodeValue || '';
              const fragment = document.createDocumentFragment();
              let cursor = 0;
              let wrapped = false;
              numericTokenPattern.lastIndex = 0;
              for (let match = numericTokenPattern.exec(value);
                   match;
                   match = numericTokenPattern.exec(value)) {
                const token = match[0];
                const start = match.index;
                const before = value[start - 1] || '';
                const after = value[start + token.length] || '';
                const digitCount = (token.match(/[0-9]/g) || []).length;
                const adjacentLatin = /[A-Za-z]/.test(before) || /[A-Za-z]/.test(after);
                if (adjacentLatin || digitCount === 0)
                  continue;

                const pureDigits = /^[0-9]+$/.test(token);
                const className = pureDigits
                    ? (token.length === 1
                        ? 'kkindle-vertical-digit'
                        : token.length <= 4 ? 'kkindle-tcy' : null)
                    : token.length <= 4 ? 'kkindle-tcy-all' : null;
                if (!className) continue;

                if (start > cursor)
                  fragment.appendChild(document.createTextNode(value.slice(cursor, start)));
                const span = document.createElement('span');
                span.className = className;
                span.dataset.kkindleVerticalRun = '1';
                span.textContent = token;
                fragment.appendChild(span);
                cursor = start + token.length;
                wrapped = true;
              }
              if (!wrapped) continue;
              if (cursor < value.length)
                fragment.appendChild(document.createTextNode(value.slice(cursor)));
              node.parentNode?.replaceChild(fragment, node);
            }
            return true;
          };
          window.__kkindlePrepareVerticalInlineRuns = prepareVerticalInlineRuns;

          const reportScroll = () => {
            const element = document.scrollingElement || document.documentElement;
            if (!element) return;
            send({
              type: "scroll",
              top: element.scrollTop || 0,
              left: element.scrollLeft || 0,
              scrollWidth: element.scrollWidth || 0,
              scrollHeight: element.scrollHeight || 0,
              clientWidth: element.clientWidth || 0,
              clientHeight: element.clientHeight || 0,
              fragment: window.__kkindleReaderLogicalHash || location.hash || ''
            });
          };
          let scrollQueued = false;
          const queueScrollReport = () => {
            if (scrollQueued) return;
            scrollQueued = true;
            requestAnimationFrame(() => {
              scrollQueued = false;
              reportScroll();
            });
          };

          // Publisher styles can make Chromium expose a viewport-sized
          // document.scrollingElement even though body content still extends
          // below it. Use the largest DOM extent for edge decisions so a
          // normal wheel at the top of a long chapter is never mistaken for
          // an overscroll at the bottom.
          const getContinuousScrollMetrics = horizontal => {
            const root = document.documentElement;
            const body = document.body;
            const element = document.scrollingElement || root || body;
            const position = horizontal
              ? Math.max(
                  Math.abs(window.scrollX || 0),
                  Math.abs(element?.scrollLeft || 0),
                  Math.abs(root?.scrollLeft || 0),
                  Math.abs(body?.scrollLeft || 0))
              : Math.max(
                  window.scrollY || 0,
                  element?.scrollTop || 0,
                  root?.scrollTop || 0,
                  body?.scrollTop || 0);
            const viewport = horizontal
              ? (window.innerWidth || root?.clientWidth || element?.clientWidth || 0)
              : (window.innerHeight || root?.clientHeight || element?.clientHeight || 0);
            const extent = horizontal
              ? Math.max(
                  element?.scrollWidth || 0,
                  root?.scrollWidth || 0,
                  body?.scrollWidth || 0,
                  root?.offsetWidth || 0,
                  body?.offsetWidth || 0)
              : Math.max(
                  element?.scrollHeight || 0,
                  root?.scrollHeight || 0,
                  body?.scrollHeight || 0,
                  root?.offsetHeight || 0,
                  body?.offsetHeight || 0);
            return { position, viewport, extent };
          };

          let dismissedSelectionText = "";
          const getSelectionAnchorRect = (range, leadingWhitespace) => {
            // getBoundingClientRect() on a multi-line Range returns the union
            // of every line. Its left edge can therefore belong to a later
            // line, which makes the action bar appear above the wrong word.
            // Anchor a collapsed range at the logical start instead; this is
            // the first selected character in document order.
            try {
              const start = range.cloneRange();
              if (leadingWhitespace > 0
                  && range.startContainer?.nodeType === Node.TEXT_NODE) {
                const value = range.startContainer.nodeValue || "";
                const offset = Math.min(
                  value.length,
                  range.startOffset + leadingWhitespace);
                start.setStart(range.startContainer, offset);
              }
              start.collapse(true);
              const caret = start.getBoundingClientRect();
              if (caret && caret.height > 0) return caret;
            } catch (_) { }

            const rects = Array.from(range.getClientRects?.() || [])
              .filter(rect => rect.width > 0 || rect.height > 0);
            return rects[0] || range.getBoundingClientRect();
          };
          const reportSelection = contextEvent => {
            try {
              const selection = window.getSelection();
              if (!selection || selection.rangeCount === 0 || selection.isCollapsed || !document.body) {
                dismissedSelectionText = "";
                send({ type: "selection", text: "" });
                hideSelectionBar();
                return;
              }
              const range = selection.getRangeAt(0);
              if (!document.body.contains(range.commonAncestorContainer)) return;
              const removeNonReaderText = root => {
                root.querySelectorAll?.('script, style, noscript, #kkindle-selection-bar, .kkindle-wave-sweep')
                  .forEach(node => node.remove());
                return root;
              };
              const pointOffset = (container, offset) => {
                const before = document.createRange();
                before.selectNodeContents(document.body);
                before.setEnd(container, offset);
                const fragment = removeNonReaderText(before.cloneContents());
                return (fragment.textContent || "").length;
              };
              const rawText = selection.toString() || "";
              const leading = rawText.length - rawText.trimStart().length;
              const trailing = rawText.length - rawText.trimEnd().length;
              const text = rawText.trim();
              if (dismissedSelectionText && text === dismissedSelectionText) {
                hideSelectionBar();
                return;
              }
              if (text !== dismissedSelectionText) dismissedSelectionText = "";
              const startOffset = pointOffset(range.startContainer, range.startOffset) + leading;
              const endOffset = pointOffset(range.endContainer, range.endOffset) - trailing;
              const textClone = removeNonReaderText(document.body.cloneNode(true));
              const fullText = textClone.textContent || "";
              const rect = getSelectionAnchorRect(range, leading);
              const hasAnchor = rect
                && (rect.width > 0 || rect.height > 0)
                && Number.isFinite(rect.left)
                && Number.isFinite(rect.top);
              // A context-menu event belongs to the pointer, not necessarily
              // to the first selected character. Use it only as a last-resort
              // fallback when the browser cannot expose a selection rect.
              const anchorX = hasAnchor ? rect.left : (contextEvent?.clientX || 0);
              const anchorY = hasAnchor ? rect.top : (contextEvent?.clientY || 0);
              const anchorBottom = hasAnchor ? rect.bottom : (contextEvent?.clientY || 0);
              send({
                type: "selection",
                text: text.slice(0, 12000),
                startOffset,
                endOffset,
                prefix: fullText.slice(Math.max(0, startOffset - 72), startOffset),
                suffix: fullText.slice(endOffset, Math.min(fullText.length, endOffset + 72)),
                x: anchorX,
                y: anchorY,
                bottom: anchorBottom,
                viewportWidth: window.innerWidth || document.documentElement.clientWidth || 0,
                viewportHeight: window.innerHeight || document.documentElement.clientHeight || 0,
                contextMenu: !!contextEvent
              });
              placeSelectionBar(
                anchorX,
                anchorY,
                anchorBottom,
                window.innerWidth || document.documentElement.clientWidth || 0,
                window.innerHeight || document.documentElement.clientHeight || 0);
            } catch (_) { }
          };

          // In-page text-selection action bar. The webview is a native HWND
          // island: Avalonia controls cannot render above it, so the quick
          // actions (复制/划线/批注/AI 解释/搜索/词典) live inside the page,
          // mirroring the WinUI reference's floating selection bar. This
          // bridge script runs in <head>, so the bar is installed once the
          // document is ready (document.body is still null during parse).
          let selectionBar = null;
          const installSelectionBar = () => {
            if (selectionBar) return;
            let styleElement = document.getElementById('kkindle-selection-bar-style');
            if (!styleElement) {
              styleElement = document.createElement('style');
              styleElement.id = 'kkindle-selection-bar-style';
              styleElement.textContent = `
                #kkindle-selection-bar, #kkindle-selection-bar * {
                  box-sizing: border-box; margin: 0; padding: 0;
                  writing-mode: horizontal-tb !important;
                  text-orientation: mixed !important;
                  direction: ltr;
                }
                #kkindle-selection-bar {
                  position: fixed; display: none; z-index: 2147483647;
                  align-items: center;
                  background: #FFFFFF; border: 1px solid #000000;
                  padding: 3px; white-space: nowrap;
                  font-size: 0;
                  font-family: "Microsoft YaHei UI", "Segoe UI", system-ui, sans-serif;
                }
                #kkindle-selection-bar button {
                  background: #FFFFFF; color: #000000; border: 0; outline: 0;
                  min-width: 52px; height: 30px; padding: 3px 8px;
                  display: inline-flex; align-items: center; justify-content: center;
                  font-size: 12px; line-height: 18px; vertical-align: middle;
                  font-family: inherit; cursor: pointer; border-radius: 0;
                }
                #kkindle-selection-bar button:hover { background: #F2F2F2; color: #000000; }
                #kkindle-selection-bar button:active { background: #D9D9D9; color: #000000; }
                #kkindle-selection-bar .kk-sel-sep {
                  display: block; align-self: center; flex: 0 0 1px; width: 1px; height: 18px;
                  background: #D5D5D1; margin: 0 2px;
                }
                #kkindle-selection-bar .kk-sel-highlight-wrap {
                  position: relative; display: inline-flex; align-items: center;
                }
                #kkindle-selection-bar .kk-sel-styles {
                  position: absolute; top: 100%; left: 0; display: none;
                  min-width: 230px; background: #FFFFFF; border: 1px solid #000000;
                  padding: 3px; z-index: 2;
                }
                #kkindle-selection-bar .kk-sel-styles.open { display: block; }
                #kkindle-selection-bar .kk-sel-styles.above {
                  top: auto; bottom: 100%;
                }
                #kkindle-selection-bar .kk-sel-styles button {
                  display: block; width: 100%; min-width: 0; text-align: left;
                  white-space: pre; padding: 3px 10px;
                }
                #kkindle-selection-bar .kk-sel-menu-sep {
                  display: block; height: 1px; margin: 3px 6px;
                  background: #D5D5D1;
                }
                img, svg {
                  visibility: visible !important; opacity: 1 !important;
                }`;
              document.head.appendChild(styleElement);
            }
            selectionBar = document.createElement('div');
            selectionBar.id = 'kkindle-selection-bar';
            selectionBar.innerHTML = `
              <button data-action="copy">复制</button>
              <span class="kk-sel-sep"></span>
              <span class="kk-sel-highlight-wrap">
                <button id="kk-sel-highlight" data-action="highlight-menu">划线 ▾</button>
                <span class="kk-sel-styles" id="kk-sel-styles">
                  <button data-highlight="solid">直线  ───</button>
                  <button data-highlight="double">双线  ═══</button>
                  <button data-highlight="dashed">虚线  ┄┄┄</button>
                  <button data-highlight="dotted">点线  ···</button>
                  <button data-highlight="wavy">波浪线  ﹏﹏</button>
                  <span class="kk-sel-menu-sep"></span>
                  <button data-highlight="marker">荧光标记（黑白反色）  ▰</button>
                </span>
              </span>
              <span class="kk-sel-sep"></span>
              <button data-action="annotate">批注</button>
              <span class="kk-sel-sep"></span>
              <button data-action="ai">AI 解释</button>
              <span class="kk-sel-sep"></span>
              <button data-action="search">搜索</button>
              <span class="kk-sel-sep"></span>
              <button data-action="dictionary">词典</button>`;
            // Keep the live selection intact while interacting with the bar.
            selectionBar.addEventListener('mousedown', event => {
              event.preventDefault();
              event.stopPropagation();
            }, true);
            selectionBar.addEventListener('click', event => {
              const target = event.target instanceof Element ? event.target.closest('button') : null;
              if (!target) return;
              event.preventDefault();
              event.stopPropagation();
              const style = target.dataset.highlight;
              if (style) {
                closeStyles();
                dismissedSelectionText = (window.getSelection?.().toString() || '').trim();
                hideSelectionBar();
                send({ type: 'selectionAction', action: 'highlight', style });
                return;
              }
              const action = target.dataset.action;
              if (!action) return;
              if (action === 'highlight-menu') {
                const panel = document.getElementById('kk-sel-styles');
                if (panel?.classList.contains('open')) closeStyles();
                else openStyles();
                return;
              }
              dismissedSelectionText = (window.getSelection?.().toString() || '').trim();
              hideSelectionBar();
              send({ type: 'selectionAction', action });
            }, true);
            let closeStylesTimer = 0;
            let selectionPointerX = -1;
            let selectionPointerY = -1;
            const clearCloseStylesTimer = () => {
              if (!closeStylesTimer) return;
              window.clearTimeout(closeStylesTimer);
              closeStylesTimer = 0;
            };
            const containsPoint = (element, x, y) => {
              if (!element) return false;
              const rect = element.getBoundingClientRect();
              return x >= rect.left && x <= rect.right
                && y >= rect.top && y <= rect.bottom;
            };
            const pointerIsInHighlightMenu = () =>
              containsPoint(document.getElementById('kk-sel-highlight'), selectionPointerX, selectionPointerY)
              || containsPoint(document.getElementById('kk-sel-styles'), selectionPointerX, selectionPointerY);
            const openStyles = () => {
              clearCloseStylesTimer();
              const panel = document.getElementById('kk-sel-styles');
              if (!panel) return;
              panel.classList.add('open');
              panel.classList.remove('above');
              const barRect = selectionBar.getBoundingClientRect();
              const panelRect = panel.getBoundingClientRect();
              const roomBelow = (window.innerHeight || 0) - barRect.bottom;
              if (roomBelow < panelRect.height + 8 && barRect.top > roomBelow)
                panel.classList.add('above');
            };
            const closeStyles = () => {
              clearCloseStylesTimer();
              document.getElementById('kk-sel-styles')?.classList.remove('open', 'above');
            };
            const scheduleCloseStyles = () => {
              if (closeStylesTimer) return;
              closeStylesTimer = window.setTimeout(() => {
                closeStylesTimer = 0;
                if (!pointerIsInHighlightMenu()) closeStyles();
              }, 160);
            };
            const highlightButton = selectionBar.querySelector('#kk-sel-highlight');
            const highlightPanel = selectionBar.querySelector('#kk-sel-styles');
            highlightButton.addEventListener('mouseenter', openStyles);
            highlightButton.addEventListener('mouseleave', scheduleCloseStyles);
            highlightPanel.addEventListener('mouseenter', clearCloseStylesTimer);
            highlightPanel.addEventListener('mouseleave', scheduleCloseStyles);
            document.addEventListener('mousemove', event => {
              selectionPointerX = event.clientX;
              selectionPointerY = event.clientY;
              const panel = document.getElementById('kk-sel-styles');
              if (!panel?.classList.contains('open')) return;
              if (pointerIsInHighlightMenu()) clearCloseStylesTimer();
              else scheduleCloseStyles();
            }, true);
            document.documentElement.addEventListener('mouseleave', closeStyles);
            document.body.appendChild(selectionBar);
          };
          const placeSelectionBar = (x, y, bottom, viewportWidth, viewportHeight) => {
            if (!selectionBar) return;
            const vw = viewportWidth || window.innerWidth || document.documentElement.clientWidth || 0;
            const vh = viewportHeight || window.innerHeight || document.documentElement.clientHeight || 0;
            selectionBar.style.display = 'flex';
            const barWidth = selectionBar.offsetWidth || 0;
            const barHeight = selectionBar.offsetHeight || 0;
            // x is the first selected glyph, not the pointer or the selection
            // midpoint. Keep the toolbar's leading edge above that glyph;
            // this remains stable when the selection spans several lines.
            const left = Math.min(Math.max(8, x), Math.max(8, vw - barWidth - 8));
            let top = y - barHeight - 10;
            if (top < 8) top = (bottom || y) + 12;
            top = Math.min(Math.max(8, top), Math.max(8, vh - barHeight - 8));
            selectionBar.style.left = left + 'px';
            selectionBar.style.top = top + 'px';
          };
          const hideSelectionBar = () => {
            if (!selectionBar) return;
            selectionBar.style.display = 'none';
            selectionBar.querySelector('#kk-sel-styles')?.classList.remove('open', 'above');
          };

          let bookmarkCorner = null;
          const installBookmarkCorner = () => {
            if (bookmarkCorner || !document.body) return;
            let styleElement = document.getElementById('kkindle-bookmark-corner-style');
            if (!styleElement) {
              styleElement = document.createElement('style');
              styleElement.id = 'kkindle-bookmark-corner-style';
              styleElement.textContent = `
                #kkindle-bookmark-corner {
                  position: fixed; top: 0; right: 0; width: 34px; height: 34px;
                  z-index: 2147483646; margin: 0; padding: 0;
                  border: 0; outline: 0; border-radius: 0;
                  background: transparent; cursor: pointer;
                }
                #kkindle-bookmark-corner::after {
                  content: ""; position: absolute; top: 0; right: 0;
                  width: 0; height: 0; opacity: 0;
                  border-top: 26px solid #000000;
                  border-left: 26px solid transparent;
                }
                #kkindle-bookmark-corner.marked::after { opacity: 1; }`;
              document.head.appendChild(styleElement);
            }
            bookmarkCorner = document.createElement('button');
            bookmarkCorner.id = 'kkindle-bookmark-corner';
            bookmarkCorner.type = 'button';
            bookmarkCorner.title = '添加或取消当前位置书签';
            bookmarkCorner.setAttribute('aria-label', '添加或取消当前位置书签');
            bookmarkCorner.addEventListener('click', event => {
              event.preventDefault();
              event.stopPropagation();
              send({ type: 'bookmarkToggle' });
            });
            document.body.appendChild(bookmarkCorner);
          };
          window.__kkindleSetBookmarkMarked = marked => {
            installBookmarkCorner();
            bookmarkCorner?.classList.toggle('marked', !!marked);
          };

          const isFootnoteLink = element => {
            const backlinkId = (element.getAttribute('id') || '').trim();
            const backlinkHref = (element.getAttribute('href')
              || element.getAttribute('data-kkindle-footnote-href') || '').trim();
            const backlinkHash = backlinkHref.indexOf('#');
            if (backlinkId && backlinkHash >= 0 && backlinkHash + 1 < backlinkHref.length) {
              let backlinkFragment = backlinkHref.slice(backlinkHash + 1);
              const backlinkQuery = backlinkFragment.search(/[?#]/);
              if (backlinkQuery >= 0) backlinkFragment = backlinkFragment.slice(0, backlinkQuery);
              if (backlinkId.toLowerCase().endsWith('n')
                  && backlinkFragment
                  && !backlinkFragment.toLowerCase().endsWith('n'))
                return false;
            }
            const metadata = [
              element.getAttribute('epub:type') || '',
              element.getAttribute('role') || '',
              element.getAttribute('rel') || '',
              element.getAttribute('class') || '',
              element.getAttribute('id') || '',
              element.getAttribute('href') || ''
            ].join(' ');
            const label = (element.textContent || '').trim();
            const detected = /\b(noteref|doc-noteref|footnote|endnote|note[-_]?ref|fn[-_]?ref)\b/i.test(metadata)
              || /(?:^|[#\s_-])(?:notes?|fn|ftn|footnotes?|zww?)[-_:]?\d*(?:n|ref)?(?:$|[\s#_-])/i.test(metadata)
              || (!!(element.closest('sup') || element.querySelector('sup'))
                  && /^(?:\[?\d{1,3}\]?|[＊*†‡])$/.test(label));
            if (detected) element.classList.add('kkindle-footnote-reference');
            return detected;
          };
          const markFootnoteLinks = () => {
            document.querySelectorAll('a').forEach(element => {
              try { isFootnoteLink(element); } catch (_) { }
            });
          };
          const isFootnoteDefinitionElement = element => {
            if (!(element instanceof Element)) return false;
            const metadata = [
              element.getAttribute('epub:type') || '',
              element.getAttribute('role') || '',
              element.getAttribute('class') || '',
              element.getAttribute('id') || ''
            ].join(' ');
            return /\b(?:doc-)?(?:footnote|endnote)\b/i.test(metadata)
              || /(?:^|[\s_-])(?:duokan-)?(?:footnote|endnote)(?:[\s_-]|$)/i.test(metadata);
          };
          const hideFootnoteDefinitions = () => {
            try {
              document.querySelectorAll('aside, section, div, ol, ul, li, p').forEach(element => {
                if (isFootnoteDefinitionElement(element))
                  element.style.setProperty('display', 'none', 'important');
              });
              document.querySelectorAll('a').forEach(anchor => {
                if (!isFootnoteLink(anchor)) return;
                const rawHref = anchor.getAttribute('href')
                  || anchor.getAttribute('data-kkindle-footnote-href') || '';
                let url;
                try { url = new URL(rawHref, location.href); }
                catch (_) { return; }
                if (!url.hash) return;
                let id;
                try { id = decodeURIComponent(url.hash.slice(1)); }
                catch (_) { id = url.hash.slice(1); }
                const target = id ? document.getElementById(id) : null;
                if (!target) return;
                const definition = target.closest('aside, section, div, ol, ul, li, p');
                const visibleTarget = definition && isFootnoteDefinitionElement(definition)
                  ? definition
                  : target;
                visibleTarget.style.setProperty('display', 'none', 'important');
              });
            } catch (_) { }
          };
          if (document.readyState === 'loading')
            document.addEventListener('DOMContentLoaded', () => {
              markFootnoteLinks();
              hideFootnoteDefinitions();
            }, { once: true });
          else {
            markFootnoteLinks();
            hideFootnoteDefinitions();
          }
          const isPageTurnTarget = element =>
            element instanceof Element
            && !element.closest('a, button, input, textarea, select, option, label, #kkindle-selection-bar');
          const isSelectionBarTarget = element =>
            element instanceof Element
            && !!element.closest('#kkindle-selection-bar');
          let pagePointerDown = null;
          document.addEventListener("pointerdown", event => {
            try {
              if (event.button !== 0
                  || event.isPrimary === false
                  || isSelectionBarTarget(event.target)) {
                pagePointerDown = null;
                return;
              }
              const selection = window.getSelection?.();
              const hasLiveSelection = !!(selection
                && !selection.isCollapsed
                && (selection.toString() || '').trim());
              // Light-dismiss is passed through by the native Avalonia popup.
              // On some Linux WebView builds that focus transition collapses
              // window.getSelection() before this handler runs, even though
              // the selection action bar was open when the click began. Treat
              // the visible bar as selection state too so this same click can
              // only dismiss it and can never also turn the page.
              const hadSelection = hasLiveSelection
                || selectionBar?.style.display === 'flex';
              const canTurnPage = window.__kkindleReaderFlowMode === 1
                && isPageTurnTarget(event.target);
              // Track every outside press while the selection popup is open,
              // including scroll mode and links/form controls. Paginated page
              // turns continue to share the same click-vs-drag bookkeeping.
              if (!hadSelection && !canTurnPage) {
                pagePointerDown = null;
                return;
              }
              pagePointerDown = {
                id: event.pointerId,
                x: event.clientX || 0,
                y: event.clientY || 0,
                hadSelection,
                canTurnPage
              };
            } catch (_) { }
          }, true);
          document.addEventListener("pointercancel", () => {
            pagePointerDown = null;
          }, true);
          document.addEventListener("click", event => {
            try {
              const element = event.target instanceof Element
                ? event.target.closest("a")
                : null;
              const storedHref = element?.getAttribute('data-kkindle-footnote-href') || '';
              const href = element?.getAttribute('href') || storedHref;
              if (element && href) {
                event.preventDefault();
                const footnote = isFootnoteLink(element);
                let absoluteHref;
                try { absoluteHref = new URL(href, location.href).href; }
                catch (_) { absoluteHref = href; }
                send({ type: "link", href: absoluteHref, target: element.target || "", footnote });
                return;
              }
            } catch (_) { }
          }, true);
          document.addEventListener("mouseup", () => reportSelection(null), true);
          document.addEventListener("pointerup", event => {
            try {
              const start = pagePointerDown;
              pagePointerDown = null;
              const moved = start
                ? Math.abs((event.clientX || 0) - start.x)
                  + Math.abs((event.clientY || 0) - start.y)
                : Number.POSITIVE_INFINITY;
              // A click that starts while text is selected is a dismiss action.
              // Clear it before the normal page-turn test so one click neither
              // needs a second pass nor falls through to a side-page turn. A
              // drag is still allowed to replace the old selection.
              if (start?.hadSelection
                  && start.id === event.pointerId
                  && event.button === 0
                  && event.isPrimary !== false
                  && moved <= 12) {
                const selection = window.getSelection?.();
                selection?.removeAllRanges?.();
                dismissedSelectionText = "";
                reportSelection(null);
                return;
              }
              reportSelection(null);
              if (!start
                  || start.id !== event.pointerId
                  || event.button !== 0
                  || event.isPrimary === false
                  || !start.canTurnPage
                  || window.__kkindleReaderFlowMode !== 1
                  || !isPageTurnTarget(event.target)) {
                return;
              }
              if (moved > 12) return;
              window.requestAnimationFrame?.(() => {
                try {
                  const selection = window.getSelection ? window.getSelection() : null;
                  if (selection && !selection.isCollapsed && (selection.toString() || '').trim()) return;
                  const width = window.innerWidth || document.documentElement.clientWidth || 0;
                  if (width <= 0) return;
                  const x = event.clientX || 0;
                  if (x < width / 3 || x > width * 2 / 3) {
                    const onLeft = x < width / 3;
                    // Report the physical click side, not a page-local idea of
                    // writing direction. The host owns the global vertical
                    // preference and maps this side to a turn direction.
                    send({ type: "pageClick", side: onLeft ? "left" : "right" });
                  }
                } catch (_) { }
              });
            } catch (_) { }
          }, true);
          let footnoteHoverTimer = 0;
          let footnoteHoverElement = null;
          document.addEventListener("pointerover", event => {
            try {
              const element = event.target instanceof Element
                ? event.target.closest("a")
                : null;
              if (!element || !element.href.includes('#')) return;
              if (!isFootnoteLink(element)) return;
              if (footnoteHoverElement === element) return;
              footnoteHoverElement = element;
              window.clearTimeout(footnoteHoverTimer);
              footnoteHoverTimer = window.setTimeout(() =>
                send({
                  type: "footnoteHover",
                  href: new URL(element.href, location.href).href,
                  x: event.clientX || 0,
                  y: event.clientY || 0
                }), 90);
            } catch (_) { }
          }, true);
          document.addEventListener("pointerout", event => {
            try {
              window.clearTimeout(footnoteHoverTimer);
              const element = event.target instanceof Element
                ? event.target.closest("a")
                : null;
              if (!element) return;
              if (event.relatedTarget instanceof Node && element.contains(event.relatedTarget)) return;
              if (footnoteHoverElement === element) footnoteHoverElement = null;
              if (isFootnoteLink(element)) send({ type: "footnoteLeave" });
            } catch (_) { }
          }, true);
          // Handle navigation on keydown so arrows respond immediately and
          // retain native key-repeat behavior. Horizontal continuous reading
          // leaves up/down to Chromium's native scrolling. Paginated turns
          // always go through the host so the selected transition is applied.
          document.addEventListener("keydown", event => {
            const key = event.key || '';
            const lower = key.toLowerCase();
            if (key === 'F11' || key === 'Escape'
                || (event.ctrlKey && (lower === 'f' || lower === 'b'))) {
              event.preventDefault();
              event.stopPropagation();
              send({ type: "shortcut", key: lower, ctrlKey: !!event.ctrlKey });
              return;
            }
            const paginated = window.__kkindleReaderFlowMode === 1;
            const continuousDirection = !paginated
              ? (key === 'ArrowUp' || key === 'PageUp' ? -1
                : key === 'ArrowDown' || key === 'PageDown' ? 1 : 0)
              : 0;
            if (continuousDirection !== 0) {
              const horizontal = window.__kkindleReaderVertical === true;
              const { position, viewport, extent } = getContinuousScrollMetrics(horizontal);
              const atEdge = continuousDirection < 0
                ? position <= 4
                : extent > 0 && position + viewport >= extent - 4;
              if (atEdge) {
                event.preventDefault();
                send({ type: 'continuousEdge', direction: continuousDirection });
                return;
              }
            }
            const nativeContinuousScroll = !paginated
              && window.__kkindleReaderVertical !== true
              && (key === 'ArrowUp' || key === 'ArrowDown');
            if (nativeContinuousScroll) return;
            const controlled = paginated
              ? ['ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown', 'PageUp', 'PageDown'].includes(key)
              : ['ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown'].includes(key);
            if (controlled) {
              event.preventDefault();
              send({ type: "key", key });
            }
          }, true);
          // Replace Chromium's default context menu with Kreader's native text
          // actions. Right-clicking a live selection reports both its anchor and
          // the click point so the host can place the menu beside the text.
          document.addEventListener("contextmenu", event => {
            event.preventDefault();
            event.stopPropagation();
            reportSelection(event);
          }, true);
          // In paginated mode the vertical wheel advances pages exactly like
          // the WinUI reference's low-level mouse hook; the host accumulates
          // the deltas. Continuous mode is left to native scrolling until a
          // separate wheel gesture starts while the document is already at an
          // edge. This keeps a fast wheel/trackpad gesture from scrolling to
          // the edge and changing chapters in the same gesture.
          let continuousWheelDirection = 0;
          let continuousWheelLastAt = 0;
          const continuousWheelGestureGap = 180;
          let paginatedWheelRemainder = 0;
          document.addEventListener("wheel", event => {
            if (window.__kkindleReaderFlowMode !== 1) {
              const direction = Math.sign(event.deltaY || 0);
              if (direction === 0) return;
              const now = performance.now();
              const startsNewGesture = continuousWheelLastAt > 0
                && (direction !== continuousWheelDirection
                  || now - continuousWheelLastAt >= continuousWheelGestureGap);
              continuousWheelDirection = direction;
              continuousWheelLastAt = now;
              const horizontal = window.__kkindleReaderVertical === true;
              const { position, viewport, extent } = getContinuousScrollMetrics(horizontal);
              const atEdge = direction < 0
                ? position <= 4
                : extent > 0 && position + viewport >= extent - 4;
              if (atEdge) {
                event.preventDefault();
                if (startsNewGesture)
                  send({ type: 'continuousEdge', direction });
              }
              return;
            }
            event.preventDefault();
            const delta = event.deltaY || 0;
            if (delta === 0) return;
            if (paginatedWheelRemainder !== 0
                && Math.sign(paginatedWheelRemainder) !== Math.sign(delta))
              paginatedWheelRemainder = 0;
            paginatedWheelRemainder += delta;
            if (Math.abs(paginatedWheelRemainder) < 120) return;
            const direction = paginatedWheelRemainder > 0 ? 1 : -1;
            paginatedWheelRemainder %= 120;
            send({ type: "page", direction });
          }, { passive: false });
          // Keyboard-driven selections (Shift+arrows) never raise mouseup, so
          // report on selectionchange as well (debounced through rAF), matching
          // the WinUI reference's selection polling.
          let selectionQueued = false;
          document.addEventListener("selectionchange", () => {
            if (selectionQueued) return;
            selectionQueued = true;
            requestAnimationFrame(() => {
              selectionQueued = false;
              reportSelection();
            });
          }, true);
          // Zen mode's auto-hide chrome is woken by pointer movement. The
          // webview is a native HWND island whose events never reach the
          // Avalonia tree, so the page reports movement through the bridge
          // (throttled), replacing the WinUI reference's low-level mouse hook.
          let lastPointerMove = 0;
          document.addEventListener("pointermove", event => {
            const now = Date.now();
            if (now - lastPointerMove < 80) return;
            lastPointerMove = now;
            send({
              type: "pointermove",
              x: event.clientX,
              y: event.clientY,
              width: window.innerWidth
            });
          }, true);
          document.addEventListener("scroll", queueScrollReport, { passive: true });
          window.addEventListener("resize", () => {
            send({ type: "resize" });
            queueScrollReport();
          }, { passive: true });

          const ready = () => {
            prepareVerticalInlineRuns();
            installSelectionBar();
            installBookmarkCorner();
            send({ type: "ready" });
            queueScrollReport();
          };
          if (document.readyState === "loading")
            document.addEventListener("DOMContentLoaded", ready, { once: true });
          else
            ready();
        })();
        """;
    private static readonly Regex CssUrlPattern = new(
        """url\s*\(\s*(?<quote>['"]?)(?<value>[^)'"]+)\k<quote>\s*\)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex CssImportPattern = new(
        "@import\\s+[^;]+;?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex HtmlNamedEntityPattern = new(
        "&[A-Za-z][A-Za-z0-9]+;",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex VerticalNumericTokenPattern = new(
        """[0-9]+(?:[.,:/+\-–—][0-9]+|%|°[CF])*""",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private readonly AppPaths _paths;

    public EpubReaderPreparationService(AppPaths paths)
    {
        _paths = paths;
    }

    public async Task<EpubReaderDocument> PrepareAsync(
        string epubPath,
        string sha256,
        CancellationToken cancellationToken = default)
    {
        var cacheKey = string.Concat(sha256.Where(Uri.IsHexDigit)).ToLowerInvariant();
        if (cacheKey.Length != 64)
            throw new InvalidDataException("书籍校验值无效。");

        var cacheRoot = Path.GetFullPath(Path.Combine(_paths.ReaderCache, cacheKey));
        EnsureContainedPath(_paths.ReaderCache, cacheRoot);
        Directory.CreateDirectory(cacheRoot);

        var extractionReadyPath = Path.Combine(cacheRoot, ExtractionReadyFileName);
        var extractionReady = await IsExtractionReadyAsync(
            extractionReadyPath,
            cacheKey,
            cancellationToken);
        if (!extractionReady)
        {
            // Re-extract on every format-version mismatch. Re-sanitizing an
            // already transformed cache cannot restore content removed by an
            // older sanitizer and would leave bridge changes version-skewed.
            await ExtractSafelyAsync(epubPath, cacheRoot, cancellationToken);

            await SanitizeExtractedResourcesAsync(cacheRoot, cancellationToken);
            await File.WriteAllTextAsync(
                extractionReadyPath,
                $"{cacheKey}\n{ExtractionFormatVersion}",
                Encoding.UTF8,
                cancellationToken);
        }

        var containerPath = Path.Combine(cacheRoot, "META-INF", "container.xml");
        if (!File.Exists(containerPath))
            throw new InvalidDataException("EPUB 缺少 META-INF/container.xml。");

        var container = await LoadXmlAsync(containerPath, cancellationToken);
        var packageRelativePath = container
            .Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "rootfile")?
            .Attribute("full-path")?.Value;
        if (string.IsNullOrWhiteSpace(packageRelativePath))
            throw new InvalidDataException("EPUB 没有声明内容清单。");

        var packagePath = ResolveContainedPath(cacheRoot, packageRelativePath);
        if (!File.Exists(packagePath))
            throw new InvalidDataException("EPUB 内容清单不存在。");

        var package = await LoadXmlAsync(packagePath, cancellationToken);
        var manifest = package.Descendants()
            .Where(element => element.Name.LocalName == "item")
            .Select(element => new ManifestItem(
                element.Attribute("id")?.Value,
                element.Attribute("href")?.Value,
                element.Attribute("media-type")?.Value,
                element.Attribute("properties")?.Value))
            .Where(item => !string.IsNullOrWhiteSpace(item.Id) && !string.IsNullOrWhiteSpace(item.Href))
            .ToDictionary(item => item.Id!, item => item, StringComparer.Ordinal);

        var packageDirectory = Path.GetDirectoryName(packagePath)!;
        var chapters = new List<string>();
        foreach (var itemRef in package.Descendants().Where(element => element.Name.LocalName == "itemref"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var idRef = itemRef.Attribute("idref")?.Value;
            if (idRef is null || !manifest.TryGetValue(idRef, out var item)) continue;
            if (item.MediaType is not ("application/xhtml+xml" or "text/html")) continue;

            var href = Uri.UnescapeDataString(item.Href!.Split('#')[0]);
            var chapterPath = ResolveContainedPath(packageDirectory, href);
            EnsureContainedPath(cacheRoot, chapterPath);
            if (File.Exists(chapterPath)) chapters.Add(chapterPath);
        }

        if (chapters.Count == 0)
            throw new InvalidDataException("EPUB 没有可阅读的章节。");

        var navigation = await ReadNavigationAsync(
            package,
            manifest,
            packageDirectory,
            cacheRoot,
            chapters,
            cancellationToken);
        if (navigation.Count == 0)
        {
            navigation = chapters
                .Select((chapter, index) => new EpubReaderNavigationItem(
                    $"第 {index + 1} 章",
                    new Uri(chapter).AbsoluteUri,
                    index))
                .ToList();
        }

        var chapterTitles = new List<string>(chapters.Count);
        foreach (var chapter in chapters)
        {
            cancellationToken.ThrowIfCancellationRequested();
            chapterTitles.Add(await ReadChapterTitleAsync(chapter, cancellationToken));
        }

        await ReplaceDuplicateChapterTitlesWithBodyPreviewAsync(chapters, chapterTitles, cancellationToken);

        return new EpubReaderDocument(cacheRoot, chapters, navigation, chapterTitles);
    }

    private static async Task<List<EpubReaderNavigationItem>> ReadNavigationAsync(
        XDocument package,
        IReadOnlyDictionary<string, ManifestItem> manifest,
        string packageDirectory,
        string cacheRoot,
        IReadOnlyList<string> chapters,
        CancellationToken cancellationToken)
    {
        var navItem = manifest.Values.FirstOrDefault(item =>
            HasToken(item.Properties, "nav"));
        if (navItem is not null)
        {
            var navPath = ResolveContainedPath(packageDirectory, Uri.UnescapeDataString(navItem.Href!.Split('#')[0]));
            EnsureContainedPath(cacheRoot, navPath);
            if (File.Exists(navPath))
            {
                var navDocument = await LoadXmlAsync(navPath, cancellationToken);
                var navElements = navDocument.Descendants()
                    .Where(element => element.Name.LocalName.Equals("nav", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                var explicitToc = navElements
                    .Where(IsTocNavigationElement)
                    .Select(element => CreateNavigationItems(
                        GetNavigationLinks(element),
                        navPath,
                        cacheRoot,
                        chapters))
                    .OrderByDescending(items => items.Count)
                    .FirstOrDefault(items => items.Count > 0);
                if (explicitToc is not null) return explicitToc;

                var inferredToc = navElements
                    .Where(element => !IsKnownNonTocNavigationElement(element))
                    .Select(element => CreateNavigationItems(
                        GetNavigationLinks(element),
                        navPath,
                        cacheRoot,
                        chapters))
                    .OrderByDescending(items => items.Count)
                    .FirstOrDefault(items => items.Count > 0);
                if (inferredToc is not null)
                {
                    return inferredToc;
                }
            }
        }

        var guideToc = package.Descendants()
            .FirstOrDefault(element =>
                element.Name.LocalName.Equals("reference", StringComparison.OrdinalIgnoreCase)
                && HasToken(GetAttributeValue(element, "type"), "toc"));
        var guideHref = GetAttributeValue(guideToc, "href");
        if (!string.IsNullOrWhiteSpace(guideHref))
        {
            var guidePathPart = guideHref.Split('#', 2)[0].Split('?', 2)[0];
            var guidePath = ResolveContainedPath(
                packageDirectory,
                Uri.UnescapeDataString(guidePathPart));
            EnsureContainedPath(cacheRoot, guidePath);
            if (File.Exists(guidePath))
            {
                var guideDocument = await LoadXmlAsync(guidePath, cancellationToken);
                var guideItems = CreateNavigationItems(
                    GetNavigationLinks(guideDocument.Root),
                    guidePath,
                    cacheRoot,
                    chapters);
                if (guideItems.Count > 0) return guideItems;
            }
        }

        var spineTocId = package.Descendants().FirstOrDefault(element => element.Name.LocalName == "spine")?
            .Attribute("toc")?.Value;
        if (spineTocId is null || !manifest.TryGetValue(spineTocId, out var ncxItem)) return [];

        var ncxPath = ResolveContainedPath(packageDirectory, Uri.UnescapeDataString(ncxItem.Href!.Split('#')[0]));
        EnsureContainedPath(cacheRoot, ncxPath);
        if (!File.Exists(ncxPath)) return [];

        var ncx = await LoadXmlAsync(ncxPath, cancellationToken);
        return CreateNavigationItems(
            ncx.Descendants().Where(element => element.Name.LocalName == "navPoint")
                .Select(element =>
                {
                    var title = element.Descendants().FirstOrDefault(descendant => descendant.Name.LocalName == "navLabel")?
                        .Descendants().FirstOrDefault(descendant => descendant.Name.LocalName == "text")?.Value;
                    var href = element.Elements().FirstOrDefault(child => child.Name.LocalName == "content")?
                        .Attribute("src")?.Value;
                    return (Title: NormalizeTitle(title), Href: href);
                }),
            ncxPath,
            cacheRoot,
            chapters);
    }

    private static List<EpubReaderNavigationItem> CreateNavigationItems(
        IEnumerable<(string Title, string? Href)> source,
        string navigationDocumentPath,
        string cacheRoot,
        IReadOnlyList<string> chapters)
    {
        var result = new List<EpubReaderNavigationItem>();
        var targets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (title, href) in source)
        {
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(href)) continue;
            if (Uri.TryCreate(href, UriKind.Absolute, out var absolute) && !absolute.IsFile) continue;

            var parts = href.Split('#', 2);
            var pathPart = parts[0].Split('?', 2)[0];
            var targetPath = pathPart.Length == 0
                ? navigationDocumentPath
                : ResolveContainedPath(Path.GetDirectoryName(navigationDocumentPath)!, Uri.UnescapeDataString(pathPart));
            EnsureContainedPath(cacheRoot, targetPath);
            var chapterIndex = chapters.ToList().FindIndex(chapter =>
                Path.GetFullPath(chapter).Equals(Path.GetFullPath(targetPath), StringComparison.OrdinalIgnoreCase));
            if (chapterIndex < 0 || !File.Exists(targetPath)) continue;

            var target = new Uri(targetPath).AbsoluteUri;
            if (parts.Length == 2 && parts[1].Length > 0) target += $"#{parts[1]}";
            var fragmentKey = parts.Length == 2 ? DecodeNavigationFragment(parts[1]) : string.Empty;
            if (!targets.Add($"{chapterIndex}\0{fragmentKey}")) continue;
            result.Add(new EpubReaderNavigationItem(title, target, chapterIndex));
        }
        return result;
    }

    private static IEnumerable<(string Title, string? Href)> GetNavigationLinks(XElement? navigation) =>
        navigation?.Descendants()
            .Where(element => element.Name.LocalName.Equals("a", StringComparison.OrdinalIgnoreCase))
            .Select(element => (
                Title: NormalizeTitle(element.Value),
                Href: element.Attributes().FirstOrDefault(attribute =>
                    attribute.Name.LocalName.Equals("href", StringComparison.OrdinalIgnoreCase))?.Value))
        ?? [];

    private static bool IsTocNavigationElement(XElement element) =>
        element.Attributes().Any(attribute =>
            attribute.Name.LocalName.Equals("type", StringComparison.OrdinalIgnoreCase)
            && HasToken(attribute.Value, "toc"))
        || HasToken(element.Attributes().FirstOrDefault(attribute =>
            attribute.Name.LocalName.Equals("role", StringComparison.OrdinalIgnoreCase))?.Value, "doc-toc")
        || HasTocHint(GetAttributeValue(element, "id"))
        || HasTocHint(GetAttributeValue(element, "class"));

    private static bool IsKnownNonTocNavigationElement(XElement element)
    {
        var metadata = element.Attributes()
            .Where(attribute => new[] { "type", "role", "id", "class" }.Any(name =>
                attribute.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase)))
            .Select(attribute => attribute.Value);
        return metadata.Any(value => Regex.IsMatch(
            value,
            @"(?:^|[\s_-])(landmarks?|page[-_]?list|doc[-_]?pagelist)(?:$|[\s_-])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
    }

    private static bool HasToken(string? value, string token) =>
        value?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Any(part => part.Equals(token, StringComparison.OrdinalIgnoreCase)) == true;

    private static string? GetAttributeValue(XElement? element, string name) =>
        element?.Attributes().FirstOrDefault(attribute =>
            attribute.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;

    private static bool HasTocHint(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && Regex.IsMatch(
            value,
            @"(?:^|[\s_-])(toc|table[-_]?of[-_]?contents?)(?:$|[\s_-])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string DecodeNavigationFragment(string fragment)
    {
        try { return Uri.UnescapeDataString(fragment); }
        catch { return fragment; }
    }

    private static async Task<string> ReadChapterTitleAsync(
        string chapterPath,
        CancellationToken cancellationToken)
    {
        try
        {
            var document = await LoadXmlAsync(chapterPath, cancellationToken);
            var heading = document.Descendants().FirstOrDefault(element =>
                element.Name.LocalName.Equals("h1", StringComparison.OrdinalIgnoreCase));
            var title = NormalizeTitle(heading?.Value);
            if (title.Length == 0)
            {
                title = NormalizeTitle(document.Descendants().FirstOrDefault(element =>
                    element.Name.LocalName.Equals("title", StringComparison.OrdinalIgnoreCase))?.Value);
            }

            return title.ToLowerInvariant() switch
            {
                "cover" => "封面",
                "table of contents" => "目录",
                _ => title
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string NormalizeTitle(string? value) =>
        string.Join(' ', (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private const int ChapterTitlePreviewMaxLength = 20;

    private static readonly HashSet<string> BodyPreviewSkippedElements = new(StringComparer.OrdinalIgnoreCase)
    { "head", "title", "script", "style" };

    // Calibre-style conversions stamp the same book-level <title> on every
    // split file, so spine chapters missing from the book's own TOC (front
    // matter such as author bios and dedications) would all show one identical
    // label. Give each member of a duplicate group a title derived from its
    // first body line instead.
    private static async Task ReplaceDuplicateChapterTitlesWithBodyPreviewAsync(
        IReadOnlyList<string> chapters,
        List<string> chapterTitles,
        CancellationToken cancellationToken)
    {
        var duplicates = chapterTitles
            .Where(title => title.Length > 0)
            .GroupBy(title => title, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
        if (duplicates.Count == 0) return;

        for (var index = 0; index < chapterTitles.Count; index++)
        {
            if (!duplicates.Contains(chapterTitles[index])) continue;
            cancellationToken.ThrowIfCancellationRequested();
            var preview = TruncateChapterTitle(
                await ReadChapterBodyPreviewAsync(chapters[index], cancellationToken));
            // A preview that collides again would just move the duplication;
            // keep the original label when no distinct first line exists.
            if (preview.Length > 0 && !duplicates.Contains(preview))
                chapterTitles[index] = preview;
        }
    }

    private static async Task<string> ReadChapterBodyPreviewAsync(
        string chapterPath,
        CancellationToken cancellationToken)
    {
        try
        {
            var document = await LoadXmlAsync(chapterPath, cancellationToken);
            var blockPreview = document
                .Descendants()
                .Where(element =>
                    IsBodyPreviewElement(element)
                    && element.Name.LocalName is
                        "p" or "div" or "section" or "article" or "li"
                        or "blockquote" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6")
                .Select(element => NormalizeTitle(element.Value))
                .FirstOrDefault(value => value.Length > 0);
            if (!string.IsNullOrWhiteSpace(blockPreview))
                return blockPreview;

            return document
                .DescendantNodes()
                .OfType<XText>()
                .Where(text => text.Parent is XElement parent && IsBodyPreviewElement(parent))
                .Select(text => NormalizeTitle(text.Value))
                .FirstOrDefault(value => value.Length > 0) ?? string.Empty;
        }
        catch { return string.Empty; }
    }

    private static bool IsBodyPreviewElement(XElement element) =>
        !BodyPreviewSkippedElements.Contains(element.Name.LocalName)
        && element.Ancestors().All(ancestor => !BodyPreviewSkippedElements.Contains(ancestor.Name.LocalName));

    private static string TruncateChapterTitle(string value)
    {
        value = NormalizeTitle(value);
        return value.Length <= ChapterTitlePreviewMaxLength
            ? value
            : value[..ChapterTitlePreviewMaxLength].TrimEnd() + "…";
    }

    private sealed record ManifestItem(string? Id, string? Href, string? MediaType, string? Properties);

    private static async Task ExtractSafelyAsync(
        string epubPath,
        string destinationRoot,
        CancellationToken cancellationToken)
    {
        await using var input = new FileStream(epubPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(entry.FullName)) continue;

            var destination = ResolveContainedPath(destinationRoot, entry.FullName);
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var source = entry.Open();
            await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
            await source.CopyToAsync(output, cancellationToken);
        }
    }

    private static async Task<XDocument> LoadXmlAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            using var reader = XmlReader.Create(stream, CreateSecureXmlReaderSettings());
            return await XDocument.LoadAsync(reader, LoadOptions.PreserveWhitespace, cancellationToken);
        }
        catch (XmlException)
        {
            // A number of EPUB 2 generators emit HTML entities such as
            // &nbsp; while declaring XHTML. External DTD resolution stays
            // disabled; decode only entities known by the platform and retry.
            var xml = await File.ReadAllTextAsync(path, cancellationToken);
            var normalized = HtmlNamedEntityPattern.Replace(xml, match =>
            {
                var entityName = match.Value.AsSpan(1, match.Value.Length - 2);
                if (entityName.Equals("amp", StringComparison.Ordinal)
                    || entityName.Equals("lt", StringComparison.Ordinal)
                    || entityName.Equals("gt", StringComparison.Ordinal)
                    || entityName.Equals("quot", StringComparison.Ordinal)
                    || entityName.Equals("apos", StringComparison.Ordinal))
                    return match.Value;

                var decoded = WebUtility.HtmlDecode(match.Value);
                return string.Equals(decoded, match.Value, StringComparison.Ordinal)
                    ? match.Value
                    : decoded;
            });
            if (string.Equals(xml, normalized, StringComparison.Ordinal)) throw;

            using var textReader = new StringReader(normalized);
            using var reader = XmlReader.Create(textReader, CreateSecureXmlReaderSettings());
            return await XDocument.LoadAsync(reader, LoadOptions.PreserveWhitespace, cancellationToken);
        }
    }

    private static XmlReaderSettings CreateSecureXmlReaderSettings() => new()
    {
        Async = true,
        // Standard EPUB XHTML commonly carries a DOCTYPE. Ignore it
        // without resolving entities; the null resolver keeps external
        // DTDs and entities out of the reader process.
        DtdProcessing = DtdProcessing.Ignore,
        XmlResolver = null
    };

    private static async Task<bool> IsExtractionReadyAsync(
        string markerPath,
        string cacheKey,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(markerPath)) return false;
        var marker = await File.ReadAllTextAsync(markerPath, cancellationToken);
        return string.Equals(
            marker.Trim(),
            $"{cacheKey}\n{ExtractionFormatVersion}",
            StringComparison.OrdinalIgnoreCase);
    }

    private static async Task SanitizeExtractedResourcesAsync(
        string cacheRoot,
        CancellationToken cancellationToken)
    {
        var htmlFiles = Directory.EnumerateFiles(cacheRoot, "*.*", SearchOption.AllDirectories)
            .Where(path => Path.GetExtension(path).Equals(".xhtml", StringComparison.OrdinalIgnoreCase)
                || Path.GetExtension(path).Equals(".html", StringComparison.OrdinalIgnoreCase)
                || Path.GetExtension(path).Equals(".htm", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        foreach (var path in htmlFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await SanitizeHtmlFileAsync(path, cacheRoot, cancellationToken);
        }

        var cssFiles = Directory.EnumerateFiles(cacheRoot, "*.css", SearchOption.AllDirectories).ToArray();
        foreach (var path in cssFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await SanitizeCssFileAsync(path, cacheRoot, cancellationToken);
        }
    }

    private static async Task SanitizeHtmlFileAsync(
        string path,
        string cacheRoot,
        CancellationToken cancellationToken)
    {
        var document = await LoadXmlAsync(path, cancellationToken);
        var root = document.Root ?? throw new InvalidDataException("EPUB HTML 缺少根元素。");
        var namespaceName = root.Name.Namespace;
        var elements = root.DescendantsAndSelf().ToArray();
        foreach (var element in elements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var localName = element.Name.LocalName;
            if (localName is "script" or "object" or "iframe" or "frame" or "embed" or "applet" or "base")
            {
                element.Remove();
                continue;
            }

            if (localName == "meta"
                && string.Equals(
                    element.Attribute("http-equiv")?.Value,
                    "refresh",
                    StringComparison.OrdinalIgnoreCase))
            {
                element.Remove();
                continue;
            }

            EpubReaderImageReferenceNormalizer.NormalizeHtmlImageReferences(
                element,
                path,
                cacheRoot);

            foreach (var attribute in element.Attributes().ToArray())
            {
                var attributeName = attribute.Name.LocalName;
                if (attributeName.StartsWith("on", StringComparison.OrdinalIgnoreCase)
                    || attributeName is "background")
                {
                    attribute.Remove();
                    continue;
                }

                if (attributeName == "srcset")
                {
                    var sanitizedSrcSet = EpubReaderImageReferenceNormalizer.NormalizeSrcSetAttribute(
                        element,
                        path,
                        cacheRoot);
                    if (string.IsNullOrWhiteSpace(sanitizedSrcSet))
                        attribute.Remove();
                    else
                        attribute.Value = sanitizedSrcSet;
                    continue;
                }

                if (attributeName is "src" or "href" or "action" or "poster" or "data"
                    or "cite" or "formaction" or "xlink:href")
                {
                    if (!IsSafeLocalReference(attribute.Value, path, cacheRoot))
                        attribute.Remove();
                }
                else if (attributeName == "style")
                {
                    var css = SanitizeCss(attribute.Value, path, cacheRoot);
                    if (string.IsNullOrWhiteSpace(css)) attribute.Remove();
                    else attribute.Value = css;
                }
            }

            // Some EPUBs use a remote 24x24 image as the complete label of a
            // local footnote link. Removing the unsafe URL while keeping the
            // now source-less <img> leaves a visible empty square in Chromium.
            // Preserve the note action with a compact text marker; discard
            // other source-less images instead of rendering broken placeholders.
            if (localName == "img" && !element.Attributes().Any(attribute =>
                    attribute.Name.LocalName is "src" or "xlink:href"
                    && !string.IsNullOrWhiteSpace(attribute.Value)))
            {
                var parent = element.Parent;
                if (parent is not null
                    && parent.Name.LocalName == "a"
                    && IsFootnoteReference(parent))
                {
                    element.ReplaceWith(
                        new XElement(
                            namespaceName + "sup",
                            new XAttribute("class", "kkindle-footnote-marker"),
                            "注"));
                }
                else
                {
                    element.Remove();
                }
                continue;
            }

            var styleText = element.Name.LocalName == "style" ? element.Value : null;
            if (styleText is not null)
                element.Value = SanitizeCss(styleText, path, cacheRoot);
        }

        // Mark the short numeric runs in the serialized XHTML itself. The
        // bridge repeats this defensively for dynamically inserted content,
        // but source-level spans are present during the first native WebKit
        // paint and therefore avoid the stale-surface path entirely.
        MarkVerticalNumericRuns(root, namespaceName);

        var head = root.Elements().FirstOrDefault(element => element.Name.LocalName == "head");
        if (head is null)
        {
            head = new XElement(namespaceName + "head");
            root.AddFirst(head);
        }

        head.Elements()
            .Where(element => element.Name.LocalName == "meta"
                && string.Equals(
                    element.Attribute("http-equiv")?.Value,
                    "Content-Security-Policy",
                    StringComparison.OrdinalIgnoreCase))
            .Remove();

        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var policy = $"{ContentSecurityPolicyBase} script-src 'nonce-{nonce}';";
        var bridgePath = Path.Combine(Path.GetDirectoryName(path)!, ReaderBridgeFileName);
        await File.WriteAllTextAsync(
            bridgePath,
            ReaderBridgeScript,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken);
        head.AddFirst(
            new XElement(
                namespaceName + "meta",
                new XAttribute("http-equiv", "Content-Security-Policy"),
                new XAttribute("content", policy)));
        head.Add(
            new XElement(
                namespaceName + "script",
                new XAttribute("nonce", nonce),
                new XAttribute("src", ReaderBridgeFileName),
                " "));

        await WriteXmlAsync(document, path, cancellationToken);
    }

    private static void MarkVerticalNumericRuns(XElement root, XNamespace namespaceName)
    {
        var body = root.Descendants().FirstOrDefault(element =>
            element.Name.LocalName.Equals("body", StringComparison.OrdinalIgnoreCase));
        if (body is null) return;

        var textNodes = body
            .DescendantNodes()
            .OfType<XText>()
            .Where(node => node.Ancestors().All(element =>
                element.Name.LocalName is not ("script" or "style" or "noscript" or "ruby" or "rt")))
            .ToArray();

        foreach (var textNode in textNodes)
        {
            var value = textNode.Value;
            if (!value.Any(char.IsAsciiDigit)) continue;

            var replacements = new List<object>();
            var cursor = 0;
            var wrapped = false;
            foreach (Match match in VerticalNumericTokenPattern.Matches(value))
            {
                var token = match.Value;
                var before = match.Index > 0 ? value[match.Index - 1] : '\0';
                var afterIndex = match.Index + match.Length;
                var after = afterIndex < value.Length ? value[afterIndex] : '\0';
                var digitCount = token.Count(char.IsAsciiDigit);
                var adjacentLatin = (before != '\0' && char.IsAsciiLetter(before))
                    || (after != '\0' && char.IsAsciiLetter(after));
                if (adjacentLatin || digitCount == 0) continue;

                var pureDigits = token.All(char.IsAsciiDigit);
                var className = pureDigits
                    ? token.Length == 1
                        ? "kkindle-vertical-digit"
                        : token.Length <= 4 ? "kkindle-tcy" : null
                    : token.Length <= 4 ? "kkindle-tcy-all" : null;
                if (className is null) continue;

                if (match.Index > cursor)
                    replacements.Add(new XText(value[cursor..match.Index]));
                replacements.Add(
                    new XElement(
                        namespaceName + "span",
                        new XAttribute("class", className),
                        new XAttribute("data-kkindle-vertical-run", "1"),
                        token));
                cursor = afterIndex;
                wrapped = true;
            }

            if (!wrapped) continue;
            if (cursor < value.Length)
                replacements.Add(new XText(value[cursor..]));
            textNode.ReplaceWith(replacements.ToArray());
        }
    }

    private static bool IsFootnoteReference(XElement element)
    {
        if (IsFootnoteBacklink(element))
            return false;

        var metadata = string.Join(
            ' ',
            element.Attributes()
                .Where(attribute => attribute.Name.LocalName is "type" or "role" or "rel" or "class" or "id" or "href")
                .Select(attribute => attribute.Value));
        return Regex.IsMatch(
            metadata,
            @"\b(noteref|doc-noteref|footnote|endnote|note[-_]?ref|fn[-_]?ref)\b|(?:^|[#\s_-])(?:notes?|fn|ftn|footnotes?|zww?)[-_:]?\d*(?:n|ref)?(?:$|[\s#_-])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool IsFootnoteBacklink(XElement element)
    {
        var id = element.Attribute("id")?.Value?.Trim();
        var href = element.Attribute("href")?.Value?.Trim();
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

    private static async Task SanitizeCssFileAsync(
        string path,
        string cacheRoot,
        CancellationToken cancellationToken)
    {
        var css = await File.ReadAllTextAsync(path, cancellationToken);
        var sanitized = SanitizeCss(css, path, cacheRoot);
        if (!string.Equals(css, sanitized, StringComparison.Ordinal))
            await File.WriteAllTextAsync(path, sanitized, Encoding.UTF8, cancellationToken);
    }

    private static string SanitizeCss(string css, string sourcePath, string cacheRoot)
    {
        var sanitized = CssImportPattern.Replace(css, string.Empty);
        return CssUrlPattern.Replace(sanitized, match =>
        {
            var value = match.Groups["value"].Value.Trim();
            return IsSafeLocalReference(value, sourcePath, cacheRoot) ? match.Value : string.Empty;
        });
    }

    private static bool IsSafeLocalReference(string value, string sourcePath, string cacheRoot) =>
        EpubReaderImageReferenceNormalizer.IsSafeLocalReference(value, sourcePath, cacheRoot);

    private static async Task WriteXmlAsync(
        XDocument document,
        string path,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        using (var writer = new Utf8StringWriter(builder))
            document.Save(writer, SaveOptions.DisableFormatting);
        await File.WriteAllTextAsync(path, builder.ToString(), Encoding.UTF8, cancellationToken);
    }

    private sealed class Utf8StringWriter(StringBuilder builder) : StringWriter(builder, System.Globalization.CultureInfo.InvariantCulture)
    {
        public override Encoding Encoding => Encoding.UTF8;
    }

    private static string ResolveContainedPath(string root, string relativePath)
    {
        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(root, normalized));
        EnsureContainedPath(root, fullPath);
        return fullPath;
    }

    private static void EnsureContainedPath(string root, string path)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("EPUB 包含不安全的文件路径。");
    }
}
