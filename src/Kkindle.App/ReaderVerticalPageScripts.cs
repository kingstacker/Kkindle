using System.Globalization;

namespace Kkindle;

/// <summary>
/// Scripts for the application-paginated vertical reader (page-compose mode).
/// The chapter document keeps its content in three containers whose inline
/// extents partition the chapter text: history [0, h), the visible page flow
/// [h, h+f), and the bank [h+f, total). Composing a page moves whole blocks
/// between containers and splits the boundary block with a Range so WebKit
/// remains the line/column breaker — the typography inside a composed page is
/// byte-identical to the flowing layout, while the document only ever lays
/// out one page of boxed content.
/// </summary>
internal static class ReaderVerticalPageScripts
{
    public const string PageFlowId = "kkindle-page-flow";
    public const string PageBankId = "kkindle-page-bank";
    public const string PageHistoryId = "kkindle-page-history";

    /// <summary>
    /// Restructures the loaded chapter into the three-container page model and
    /// installs the compose helpers. <paramref name="startChar"/> is the saved
    /// character offset to open (0 for a fresh chapter entry); content before
    /// it moves into history so the first visible page starts exactly there.
    /// </summary>
    public static string BuildVerticalPageInitScript(int startChar)
    {
        var start = Math.Max(0, startChar).ToString(CultureInfo.InvariantCulture);
        return $$"""
            (() => {
              const body = document.body;
              if (!body || document.getElementById('{{PageFlowId}}')) return true;
              const total = (body.textContent || '').length;

              // Legacy extraction artifacts would re-wrap incorrectly inside
              // page fragments; clear them once before banking the content.
              body.querySelectorAll(
                'span[data-kkindle-vertical-run="1"], .kkindle-native-vertical-digits, '
                  + '.kkindle-native-vertical-footnote').forEach(span => span.replaceWith(
                    document.createTextNode(span.textContent || '')));
              body.normalize();

              const style = document.createElement('style');
              style.id = 'kkindle-page-mode-style';
              style.textContent = `
                html, body {
                  width: 100% !important; height: 100% !important;
                  margin: 0 !important; padding: 0 !important;
                  overflow: hidden !important;
                  writing-mode: vertical-rl !important;
                  text-orientation: mixed !important;
                  direction: ltr !important;
                }
                #{{PageBankId}}, #{{PageHistoryId}} { display: none !important; }
                #{{PageFlowId}} {
                  width: 100% !important; height: 100% !important;
                  margin: 0 !important; overflow: hidden !important;
                  box-sizing: border-box !important;
                }
                #{{PageFlowId}} .kkindle-page-cont { text-indent: 0 !important; }
              `;
              document.head.appendChild(style);

              const history = document.createElement('div');
              history.id = '{{PageHistoryId}}';
              const flow = document.createElement('div');
              flow.id = '{{PageFlowId}}';
              const bank = document.createElement('div');
              bank.id = '{{PageBankId}}';
              bank.setAttribute('aria-hidden', 'true');

              const bankChildren = [];
              while (body.firstChild) {
                const node = body.firstChild;
                body.removeChild(node);
                if (node.nodeType === Node.ELEMENT_NODE || (node.nodeValue || '').trim()) {
                    if (node.nodeType === Node.ELEMENT_NODE)
                        node.dataset.kkindlePageChars = String((node.textContent || '').length);
                    bankChildren.push(node);
                } else if ((node.nodeValue || '').length > 0) {
                    bankChildren.push(node);
                }
              }
              for (const child of bankChildren) bank.appendChild(child);
              body.appendChild(history);
              body.appendChild(flow);
              body.appendChild(bank);

              window.__pg = {
                h: 0,
                f: 0,
                total,
                complete: false
              };

              const containers = () => ({
                bank: document.getElementById('{{PageBankId}}'),
                flow: document.getElementById('{{PageFlowId}}'),
                hist: document.getElementById('{{PageHistoryId}}')
              });
              const overflowPx = flow => Math.max(0, (flow.scrollWidth || 0) - (flow.clientWidth || 0));
              const recomputeChars = () => {
                const { bank, flow, hist } = containers();
                const sum = el => {
                  let totalChars = 0;
                  for (const child of el.children) {
                    const declared = parseInt(child.dataset.kkindlePageChars || '0', 10);
                    totalChars += declared > 0
                      ? declared
                      : (child.textContent || '').length;
                  }
                  return totalChars;
                };
                window.__pg.h = sum(hist);
                window.__pg.f = sum(flow);
              };

              const textWalkerOffset = (block, keepChars) => {
                const walker = document.createTreeWalker(block, NodeFilter.SHOW_TEXT);
                let node, consumed = 0;
                while ((node = walker.nextNode())) {
                  const len = (node.nodeValue || '').length;
                  if (consumed + len >= keepChars)
                    return { node, offset: keepChars - consumed };
                  consumed += len;
                }
                return null;
              };

              // Moves the trailing part of the overflowing block back into the
              // bank, keeping `keepChars` characters of it on the page. The
              // suffix is wrapped in a shallow clone so paragraph classes and
              // the continuation marker (no re-indent) travel with it.
              const splitSuffixToBank = (block, keepChars) => {
                const pos = textWalkerOffset(block, keepChars);
                if (!pos) return false;
                const range = document.createRange();
                range.setStart(pos.node, pos.offset);
                range.setEndAfter(block.lastChild);
                const fragment = range.extractContents();
                if (!fragment.firstChild) return false;
                const wrap = block.cloneNode(false);
                wrap.classList.add('kkindle-page-cont');
                wrap.removeAttribute('id');
                while (wrap.firstChild) wrap.removeChild(wrap.firstChild);
                wrap.appendChild(fragment);
                wrap.dataset.kkindlePageChars = String((wrap.textContent || '').length);
                const { bank } = containers();
                bank.insertBefore(wrap, bank.firstChild);
                block.dataset.kkindlePageChars = String((block.textContent || '').length);
                return true;
              };

              // Shrinks the last flow block until the page fits. Returns
              // 'split' (the page ends at a mid-block boundary), 'whole'
              // (an unsplittable block moved back; the page ends before it)
              // or 'fits' (no overflow — the caller keeps filling).
              const shrinkLastToFit = () => {
                const { flow, bank } = containers();
                for (let tries = 0; tries < 10; tries++) {
                  const over = overflowPx(flow);
                  if (over <= 1) return 'fits';
                  const block = flow.lastChild;
                  if (!block) return 'whole';
                  const inlineExtent = block.getBoundingClientRect().height;
                  const textLen = (block.textContent || '').length;
                  if (textLen < 2 || inlineExtent <= 0) {
                    bank.insertBefore(block, bank.firstChild);
                    return 'whole';
                  }
                  const perChar = inlineExtent / textLen;
                  const remove = Math.min(
                    textLen - 1,
                    Math.max(1, Math.ceil(over / Math.max(2, perChar))));
                  if (!splitSuffixToBank(block, textLen - remove)) {
                    bank.insertBefore(block, bank.firstChild);
                    return 'whole';
                  }
                }
                return overflowPx(flow) <= 2 ? 'split' : 'whole';
              };

              // Decomposes the visible page. `toHistory` keeps its blocks in
              // document order at the history tail; otherwise they return to
              // the bank front as the next uncomposed content.
              const decomposeFlow = toHistory => {
                const { bank, flow, hist } = containers();
                const frag = document.createDocumentFragment();
                while (flow.firstChild) frag.appendChild(flow.firstChild);
                if (toHistory) hist.appendChild(frag);
                else bank.insertBefore(frag, bank.firstChild);
              };

              window.__pgComposeForward = () => {
                const { flow, bank } = containers();
                decomposeFlow(true);
                let guard = 0;
                while (bank.firstChild && guard++ < 4000) {
                  flow.appendChild(bank.firstChild);
                  const outcome = shrinkLastToFit();
                  if (outcome !== 'fits') break;
                }
                recomputeChars();
                window.__pg.complete = !bank.firstChild;
                return JSON.stringify({
                  h: window.__pg.h,
                  f: window.__pg.f,
                  complete: window.__pg.complete
                });
              };

              window.__pgComposeBackward = () => {
                const { hist } = containers();
                if (window.__pg.h <= 0 || !hist.lastChild) return JSON.stringify({ h: window.__pg.h, f: window.__pg.f, complete: true });
                decomposeFlow(false);
                let guard = 0;
                while (hist.lastChild && guard++ < 4000) {
                  const node = hist.lastChild;
                  const { flow } = containers();
                  flow.insertBefore(node, flow.firstChild);
                  const over = overflowPx(flow);
                  if (over <= 1) continue;
                  // The freshly prepended block straddles the page start:
                  // keep its tail on the page and return its head to the
                  // history tail.
                  const inlineExtent = node.getBoundingClientRect().height;
                  const textLen = (node.textContent || '').length;
                  if (textLen < 2 || inlineExtent <= 0) {
                    hist.appendChild(node);
                    break;
                  }
                  const perChar = inlineExtent / textLen;
                  const overChars = Math.min(
                    textLen - 1,
                    Math.max(1, Math.ceil(over / Math.max(2, perChar))));
                  const pos = textWalkerOffset(node, overChars);
                  if (!pos) { hist.appendChild(node); break; }
                  const range = document.createRange();
                  range.setStart(node, 0);
                  range.setEnd(pos.node, pos.offset);
                  const head = range.extractContents();
                  if (head.firstChild) hist.appendChild(head);
                  recomputeChars();
                  window.__pg.h = window.__pg.h - overChars;
                  return JSON.stringify({
                    h: window.__pg.h,
                    f: window.__pg.f,
                    complete: false
                  });
                }
                recomputeChars();
                return JSON.stringify({ h: window.__pg.h, f: window.__pg.f, complete: !hist.lastChild && window.__pg.h <= 0 });
              };

              // Splits the uncomposed region at an absolute character offset
              // (the saved page start or an anchor target) and composes the
              // page that starts there.
              window.__pgComposeAt = startChar => {
                decomposeFlow(false);
                const { bank, hist } = containers();
                let before = window.__pg.h;
                while (startChar < before && hist.lastChild) {
                  const node = hist.lastChild;
                  const len = parseInt(node.dataset?.kkindlePageChars || '0', 10)
                    || (node.textContent || '').length;
                  hist.removeChild(node);
                  bank.insertBefore(node, bank.firstChild);
                  before -= len;
                }
                while (startChar > before && bank.firstChild) {
                  const node = bank.firstChild;
                  const len = parseInt(node.dataset?.kkindlePageChars || '0', 10)
                    || (node.textContent || '').length;
                  bank.removeChild(node);
                  hist.appendChild(node);
                  before += len;
                }
                window.__pg.h = Math.min(before, startChar);
                recomputeChars();
                return window.__pgComposeForward();
              };

              // Composes the page containing a named anchor (TOC fragments and
              // footnote/reference targets). The anchor is located inside the
              // bank; the page opens at the start of its containing block so
              // the anchor is guaranteed to be on the page.
              window.__pgComposeToAnchor = anchorName => {
                const { bank } = containers();
                if (!anchorName) return false;
                let target = bank.querySelector(
                  '#' + (window.CSS && CSS.escape ? CSS.escape(anchorName) : anchorName));
                if (!target) {
                  for (const el of bank.querySelectorAll('[id], a[name], a[id]')) {
                    if (el.id === anchorName || el.getAttribute('name') === anchorName) {
                      target = el; break;
                    }
                  }
                }
                if (!target) return false;
                let child = target;
                while (child.parentElement && child.parentElement !== bank)
                  child = child.parentElement;
                let chars = window.__pg.h;
                for (const sibling of bank.children) {
                  if (sibling === child) break;
                  chars += parseInt(sibling.dataset?.kkindlePageChars || '0', 10)
                    || (sibling.textContent || '').length;
                }
                window.__pgComposeAt(chars);
                return true;
              };

              // Leaves page mode: restores every block to the body in
              // document order and removes the page scaffolding, so a layout
              // switch back to the flowing architecture starts clean.
              window.__pgTeardown = () => {
                window.__pgTeardownTrace = (new Error().stack || '') + '\n=== callers ===\n'
                  + (window.__pgTeardownCallers || []).join('\n---\n');
                window.__pgTeardownCallers = (window.__pgTeardownCallers || []);
                window.__pgTeardownCallers.push(new Error().stack || '');
                const { bank, flow, hist } = containers();
                const body = document.body;
                const frag = document.createDocumentFragment();
                while (hist.firstChild) frag.appendChild(hist.firstChild);
                while (flow.firstChild) frag.appendChild(flow.firstChild);
                while (bank.firstChild) frag.appendChild(bank.firstChild);
                body.appendChild(frag);
                flow.remove();
                bank.remove();
                hist.remove();
                document.getElementById('kkindle-page-mode-style')?.remove();
                delete window.__pg;
              };

              // DEBUG: attribute container removals to their caller.
              const containerObserver = new MutationObserver(mutations => {
                for (const mutation of mutations) {
                  for (const removed of mutation.removedNodes) {
                    if (removed.id === '{{PageBankId}}'
                        || removed.id === '{{PageFlowId}}'
                        || removed.id === '{{PageHistoryId}}') {
                      window.__pgRemovedTrace = new Error().stack;
                    }
                  }
                }
              });
              containerObserver.observe(body, { childList: true });

              const liveTeardown = window.__pgTeardown;
              window.__pgTeardown = (...args) => {
                window.__pgTeardownCaller = new Error().stack || '';
                return liveTeardown(...args);
              };

              window.__pgCanForward = () => {
                const { bank } = containers();
                return !!bank.firstChild;
              };
              window.__pgCanBackward = () => window.__pg.h > 0;
              window.__pgRatio = () => {
                recomputeChars();
                return window.__pg.total > 0
                  ? Math.min(1, (window.__pg.h + window.__pg.f) / window.__pg.total)
                  : 0;
              };
              window.__pgStartChar = () => window.__pg.h;

              recomputeChars();
              {{(startChar > 0 ? "return window.__pgComposeAt(" + start + ");" : "return window.__pgComposeForward();")}}
            })();
            """;
    }

    /// <summary>Reads the page state ({h, f, complete}) after a compose call.</summary>
    public const string ReadVerticalPageStateScript =
        "JSON.stringify({ h: window.__pg.h, f: window.__pg.f, "
        + "complete: window.__pg.complete, "
        + "ratio: window.__pgRatio() })";

    public const string CanTurnForwardScript = "window.__pgCanForward() === true";
    public const string CanTurnBackwardScript = "window.__pgCanBackward() === true";

    /// <summary>Page-start character offset for progress persistence.</summary>
    public const string ReadPageStartCharScript = "window.__pgStartChar() | 0";

    /// <summary>Reading ratio within the chapter for the progress slider.</summary>
    public const string ReadRatioScript = "window.__pgRatio()";
}
