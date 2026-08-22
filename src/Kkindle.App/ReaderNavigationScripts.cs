namespace Kkindle;

internal static class ReaderNavigationScripts
{
    // Normalize only the leading content path. EPUBs commonly put a large
    // margin on the first paragraph/image or wrap it in several divs; clearing
    // those boxes is enough to start a TOC chapter at the reader's content top
    // without flattening spacing between later paragraphs.
    public const string NormalizeChapterStart =
        """
        (() => {
          const body = document.body;
          if (!body) return;
          const scrollToStart = () => {
            try {
              window.scrollTo({ left: 0, top: 0, behavior: 'instant' });
            } catch (_) {
              window.scrollTo(0, 0);
            }
          };

          // A chapter-start jump is also a location change. Without clearing
          // the old hash, the next scroll report resurrects a stale fragment
          // and progress/bookmarks point at the previous section.
          try {
            if (location.hash) history.replaceState(null, '', location.href.split('#')[0]);
          } catch (_) { }
          window.__kkindleReaderLogicalHash = '';

          document.querySelectorAll('.kkindle-fragment-break, .kkindle-fragment-zeroed').forEach(el => {
            try { el.classList.remove('kkindle-fragment-break'); } catch (_) {}
            try { el.classList.remove('kkindle-fragment-zeroed'); } catch (_) {}
            try { el.style.removeProperty('break-before'); } catch (_) {}
            try { el.style.removeProperty('-webkit-column-break-before'); } catch (_) {}
            try { el.style.removeProperty('margin-top'); } catch (_) {}
          });

          const visualTags = new Set([
            'img', 'svg', 'picture', 'canvas', 'hr', 'table', 'video', 'audio',
            'iframe', 'object', 'embed'
          ]);
          const textTags = new Set([
            'p', 'h1', 'h2', 'h3', 'h4', 'h5', 'h6', 'li', 'blockquote', 'pre',
            'td', 'th', 'dd', 'dt', 'figcaption'
          ]);
          const wrapperTags = new Set([
            'div', 'section', 'article', 'main', 'header', 'aside', 'nav', 'figure'
          ]);

          const isBlank = (node) => {
            if (node.nodeType === Node.TEXT_NODE)
              return (node.textContent || '').trim().length === 0;
            if (node.nodeType !== Node.ELEMENT_NODE) return true;
            const tag = node.tagName.toLowerCase();
            if (visualTags.has(tag)) return false;
            if (node.querySelector('img, svg, picture, canvas, hr, table, video, audio, iframe, object, embed')) return false;
            if (node.querySelector('[id]')) return false;
            return (node.textContent || '').trim().length === 0;
          };

          const stripLeadingBlanks = (container) => {
            while (container.firstChild && isBlank(container.firstChild))
              container.removeChild(container.firstChild);
          };
          const zeroTop = (element) => {
            try { element.style.setProperty('margin-top', '0', 'important'); } catch (_) {}
          };
          const tagOf = (element) => element?.tagName?.toLowerCase() || '';
          const isVisual = (element) => visualTags.has(tagOf(element));
          const isText = (element) => textTags.has(tagOf(element));
          const isWrapper = (element) => wrapperTags.has(tagOf(element));

          stripLeadingBlanks(body);
          const first = body.firstElementChild;
          if (!first) {
            scrollToStart();
            return;
          }

          // Walk down at most the leading wrapper chain. This handles both
          // body > div > p and body > div > img without touching later content.
          let current = first;
          for (let depth = 0; current && current !== body && depth < 12; depth++) {
            stripLeadingBlanks(current);
            zeroTop(current);
            const child = current.firstElementChild;
            if (!child) break;

            zeroTop(child);
            if (isVisual(child)) break;

            // A text block can contain a leading image (for example a figure
            // wrapped in a paragraph). Remove only that image's top margin.
            if (isText(child)) {
              const visual = child.firstElementChild;
              if (visual && isVisual(visual)) zeroTop(visual);
              break;
            }
            if (!isWrapper(child)) break;
            current = child;
          }
          scrollToStart();
        })();
        """;

    // Updates only the browser location hash without moving the DOM. Offset-
    // based bookmark, annotation, search and AI jumps position themselves
    // separately but still need progress reports to carry the new fragment.
    // `needle` is escaped for a JavaScript single-quoted string by the caller.
    public static string CreateLocationHashUpdate(string needle) =>
        $$"""
        (() => {
          let id = '{{needle}}';
          try { id = decodeURIComponent(id); } catch { }
          window.__kkindleReaderLogicalHash = id ? '#' + encodeURIComponent(id) : '';
          try {
            history.replaceState(
              null,
              '',
              id ? '#' + encodeURIComponent(id) : location.href.split('#')[0]);
            return true;
          } catch (_) {
            return false;
          }
        })();
        """;

    // Builds a fragment-positioning script. `needle` is already escaped for a
    // JavaScript single-quoted string by the caller.
    public static string CreateFragmentScroll(
        string needle,
        int flowMode,
        bool vertical,
        bool twoPage = false,
        bool revealFootnote = false) =>
        $$"""
        (() => {
          const body = document.body;
          if (!body) return { ok: false, reason: 'no-body' };
          const flowMode = {{flowMode}};
          const vertical = {{(vertical ? "true" : "false")}};
          const twoPage = {{(twoPage ? "true" : "false")}};
          const revealFootnote = {{(revealFootnote ? "true" : "false")}};
          let id = '{{needle}}';
          try { id = decodeURIComponent(id); } catch { }

          const visualTags = new Set(['img', 'svg', 'picture', 'canvas', 'table', 'hr', 'figure']);
          const textTags = new Set(['p', 'h1', 'h2', 'h3', 'h4', 'h5', 'h6', 'li', 'blockquote', 'pre']);
          const wrapperTags = new Set(['div', 'section', 'article', 'main', 'header', 'footer', 'aside', 'nav']);
          const isHeading = (n) => n && /^H[1-6]$/i.test(n.tagName);
          const isVisual = (n) => visualTags.has(n?.tagName?.toLowerCase() || '');
          const isText = (n) => textTags.has(n?.tagName?.toLowerCase() || '');
          const isWrapper = (n) => wrapperTags.has(n?.tagName?.toLowerCase() || '');
          const blockSel = 'p,div,section,article,main,header,footer,h1,h2,h3,h4,h5,h6,li,blockquote,figure,pre,table,ul,ol,dl,dd,dt,aside,nav,address';

          const valid = (n) => {
            if (!n || n.nodeType !== Node.ELEMENT_NODE) return false;
            const tag = n.tagName.toLowerCase();
            if (tag === 'script' || tag === 'style' || tag === 'noscript' || tag === 'body') return false;
            const cs = getComputedStyle(n);
            if (cs.display === 'none' || cs.visibility === 'hidden') return false;
            const r = n.getBoundingClientRect();
            if (r.width === 0 && r.height === 0) return false;
            if (isVisual(n)) return true;
            return (n.innerText || n.textContent || '').trim().length > 0;
          };
          const stripLeadingBlanks = (container) => {
            while (container.firstChild
                   && container.firstChild.nodeType === Node.TEXT_NODE
                   && !(container.firstChild.textContent || '').trim())
              container.removeChild(container.firstChild);
          };
          const markZeroTop = (element) => {
            try { element.classList.add('kkindle-fragment-zeroed'); } catch (_) {}
            try { element.style.setProperty('margin-top', '0', 'important'); } catch (_) {}
          };
          const normalizeTargetPath = (root) => {
            let current = root;
            for (let depth = 0; current && current !== body && depth < 8; depth++) {
              markZeroTop(current);
              stripLeadingBlanks(current);
              const child = current.firstElementChild;
              if (!child) break;
              markZeroTop(child);
              if (isVisual(child)) break;
              if (isText(child)) {
                const visual = child.firstElementChild;
                if (visual && isVisual(visual)) markZeroTop(visual);
                break;
              }
              if (!isWrapper(child)) break;
              current = child;
            }
          };

          // Resolve the anchor; hidden/empty/missing anchors forward-search to
          // the first valid heading, paragraph, or image.
          let el = null;
          try { el = document.getElementById(id) || Array.from(document.getElementsByName(id))[0]; } catch { }
          if (revealFootnote && el) {
            let revealTarget = el;
            for (let current = el; current && current !== body; current = current.parentElement) {
              const metadata = [
                current.getAttribute?.('epub:type') || '',
                current.getAttribute?.('role') || '',
                current.getAttribute?.('class') || '',
                current.getAttribute?.('id') || ''
              ].join(' ');
              if (/\b(?:doc-)?(?:footnote|endnote)\b/i.test(metadata)
                  || /(?:^|[\s_-])(?:duokan-)?(?:footnote|endnote)(?:[\s_-]|$)/i.test(metadata)) {
                revealTarget = current;
                break;
              }
            }
            revealTarget.hidden = false;
            revealTarget.removeAttribute?.('hidden');
            revealTarget.style?.setProperty('display', 'block', 'important');
            revealTarget.style?.setProperty('visibility', 'visible', 'important');
          }
          let content = null;
          if (el && valid(el)) {
            if (isHeading(el) || el.matches(blockSel)) content = el;
            else {
              const next = el.nextElementSibling;
              if (next && isHeading(next) && valid(next)) content = next;
              else {
                let parent = el.parentElement;
                while (parent && parent !== body && !parent.matches(blockSel)) parent = parent.parentElement;
                content = (parent && parent !== body && valid(parent)) ? parent : null;
              }
            }
          }
          if (!content) {
            const walker = document.createTreeWalker(body, NodeFilter.SHOW_ELEMENT);
            if (el) while (walker.nextNode()) { if (walker.currentNode === el) break; }
            while (walker.nextNode()) {
              const n = walker.currentNode;
              if (el && el.contains(n)) continue;
              if (valid(n)) { content = n; break; }
            }
          }
          if (!content) {
            const walker = document.createTreeWalker(body, NodeFilter.SHOW_ELEMENT);
            while (walker.nextNode()) {
              const n = walker.currentNode;
              if (n !== body && valid(n)) { content = n; break; }
            }
          }
          if (!content) return { ok: false, reason: 'no-content' };

          // Same-document jumps are performed by script so the native host
          // does not reload the XHTML. Keep the document hash in sync as well;
          // bookmarks and section quotes use it to preserve the exact anchor
          // the reader is currently showing.
          window.__kkindleReaderLogicalHash = id ? '#' + encodeURIComponent(id) : '';
          try {
            if (id) history.replaceState(null, '', '#' + encodeURIComponent(id));
          } catch (_) { }

          let block = content;
          if (!block.matches(blockSel)) {
            let parent = block.parentElement;
            while (parent && parent !== body && !parent.matches(blockSel)) parent = parent.parentElement;
            block = (parent && parent !== body) ? parent : block;
          }

          document.querySelectorAll('.kkindle-fragment-break').forEach(n => {
            try { n.classList.remove('kkindle-fragment-break'); } catch (_) {}
            try { n.style.removeProperty('break-before'); } catch (_) {}
            try { n.style.removeProperty('-webkit-column-break-before'); } catch (_) {}
          });
          document.querySelectorAll('.kkindle-fragment-zeroed').forEach(n => {
            try { n.classList.remove('kkindle-fragment-zeroed'); } catch (_) {}
            try { n.style.removeProperty('margin-top'); } catch (_) {}
          });
          normalizeTargetPath(block);
          if (flowMode === 1) {
            try { block.classList.add('kkindle-fragment-break'); } catch (_) {}
            try { block.style.setProperty('break-before', 'column', 'important'); } catch (_) {}
            try { block.style.setProperty('-webkit-column-break-before', 'always', 'important'); } catch (_) {}
          }
          void block.offsetHeight;

          const scroller = document.scrollingElement || document.documentElement;
          const bodyStyle = getComputedStyle(body);
          const padTop = parseFloat(bodyStyle.paddingTop) || 0;
          const rect = block.getBoundingClientRect();
          const docTop = rect.top + scroller.scrollTop;
          const docLeft = rect.left + scroller.scrollLeft;
          if (flowMode === 1) {
            const padLeft = parseFloat(bodyStyle.paddingLeft) || 0;
            const padRight = parseFloat(bodyStyle.paddingRight) || 0;
            const step = {{ReaderPaginationScripts.PageStepExpression}};
            const rawMax = Math.max(0, scroller.scrollWidth - scroller.clientWidth);
            const max = step > 0
              ? Math.max(0, Math.round(Math.max(0, rawMax - padRight) / step) * step)
              : rawMax;
            // A two-page spread has a left and a right column inside the same
            // viewport. Rounding maps the right column to the next viewport,
            // so floor keeps both columns in their owning spread. Single-page
            // EPUB columns can accumulate fractional-pixel drift, therefore
            // they retain nearest-boundary rounding.
            const pageIndex = step > 0
              ? Math.max(0, twoPage
                  ? Math.floor(Math.max(0, docLeft - padLeft) / step + 1e-6)
                  : Math.round((docLeft - padLeft) / step))
              : 0;
            const pageLeft = pageIndex * step;
            window.scrollTo({ left: Math.max(0, Math.min(max, pageLeft)), top: 0, behavior: 'instant' });
          } else if (vertical) {
            const padRight = parseFloat(bodyStyle.paddingRight) || 0;
            const contentRight = scroller.clientWidth - padRight;
            const docRight = rect.right + scroller.scrollLeft;
            window.scrollTo({
              left: Math.max(0, docRight - contentRight),
              top: Math.max(0, docTop - padTop),
              behavior: 'instant'
            });
          } else {
            window.scrollTo({ top: Math.max(0, docTop - padTop), behavior: 'instant' });
          }

          const after = block.getBoundingClientRect();
          const computed = getComputedStyle(block);
          const padLeft = parseFloat(bodyStyle.paddingLeft) || 0;
          const step = {{ReaderPaginationScripts.PageStepExpression}};
          const column = flowMode === 1 ? {
            step,
            padLeft,
            columnLeft: Math.round(after.left * 100) / 100,
            columnIndex: step > 0
              ? Math.max(0, twoPage
                  ? Math.floor(Math.max(0, docLeft - padLeft) / step + 1e-6)
                  : Math.round((docLeft - padLeft) / step))
              : 0
          } : null;
          return {
            ok: true,
            reason: el ? 'anchor' : 'forward-search',
            targetId: id,
            resolvedTag: content.tagName.toLowerCase(),
            resolvedId: content.id || '',
            resolvedText: (content.innerText || content.textContent || '').trim().slice(0, 40),
            blockTag: block.tagName.toLowerCase(),
            padTop,
            rectTop: Math.round(after.top * 100) / 100,
            rectLeft: Math.round(after.left * 100) / 100,
            marginTop: computed.marginTop,
            scrollTop: scroller.scrollTop,
            scrollLeft: scroller.scrollLeft,
            scrollWidth: scroller.scrollWidth,
            scrollHeight: scroller.scrollHeight,
            clientWidth: scroller.clientWidth,
            clientHeight: scroller.clientHeight,
            column
          };
        })();
        """;
}
